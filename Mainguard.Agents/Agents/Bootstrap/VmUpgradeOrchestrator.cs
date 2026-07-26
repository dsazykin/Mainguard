using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>What the tier-2 upgrade needs to know about this install: the app-shipped payload
/// tarball and where the two distro installs live on the host.</summary>
/// <param name="TarballPath">The bundled <c>payload/MainguardOS.tar.gz</c> (Windows host path).</param>
/// <param name="StagingInstallDir">Where <c>MainguardEnv-staging</c> is imported (host path;
/// sibling of the canonical dir, e.g. <c>…\Mainguard\vm-staging</c>).</param>
/// <param name="CanonicalInstallDir">The canonical <c>MainguardEnv</c> install dir (host path,
/// e.g. <c>…\Mainguard\vm</c>) — where the promoted VHDX ends up.</param>
public sealed record VmUpgradeOptions(string TarballPath, string StagingInstallDir, string CanonicalInstallDir);

/// <summary>Which recovery posture a failed upgrade left the machine in.</summary>
public enum VmUpgradeFailureKind
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The failure happened BEFORE the old distro was retired: <c>MainguardEnv</c> is still
    /// registered with all user data, its daemon was re-started, and staging was cleaned up
    /// best-effort. Nothing was lost; the upgrade can simply be retried.</summary>
    OldDistroIntact,

    /// <summary>The failure happened AFTER the old distro was unregistered but before the staging
    /// VHDX was promoted: no <c>MainguardEnv</c> is registered right now, but the migrated (and
    /// validated) data lives in the VHDX named by <see cref="VmUpgradeResult.StagingVhdxPath"/>.
    /// The message carries the exact recovery command; the VHDX is never deleted.</summary>
    StrandedAfterRetire,
}

/// <summary>The outcome of one upgrade attempt — typed, never a bare throw at the caller
/// (the tier-2 twin of <see cref="DaemonRefreshResult"/>).</summary>
/// <param name="Succeeded">True when the new payload is the canonical <c>MainguardEnv</c>.</param>
/// <param name="FailureKind">The recovery posture on failure; <see cref="VmUpgradeFailureKind.None"/> on success.</param>
/// <param name="Message">Human-readable outcome (actionable on failure).</param>
/// <param name="StagingVhdxPath">Only for <see cref="VmUpgradeFailureKind.StrandedAfterRetire"/>:
/// the host path of the VHDX holding the migrated user data — whichever location holds the newest
/// verified copy (the canonical dir once the fallback copy verified, staging otherwise).</param>
/// <param name="PromoteStrategy">Which promote strategy placed the VHDX in the canonical dir:
/// <c>"move"</c> or <c>"copy-then-cleanup"</c>; null when the promote never got that far (both
/// strategies failed, or an earlier step failed). Diagnostic — the App logs it to oobe.log.</param>
public sealed record VmUpgradeResult(
    bool Succeeded, VmUpgradeFailureKind FailureKind, string Message, string? StagingVhdxPath = null,
    string? PromoteStrategy = null);

/// <summary>The host-filesystem side effects the upgrade needs (temp tar transport + the VHDX
/// move), behind a seam so the orchestrator is unit-testable without touching a disk.</summary>
public interface IVmUpgradeHostFileSystem
{
    /// <summary>Creates and returns a fresh host temp directory for the migration tar files.</summary>
    string CreateTempDirectory();

    /// <summary>Moves a file (the staging VHDX → its canonical home). Must throw on failure.</summary>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>Copies a file (the promote's fallback when the move is blocked — WSL's shared
    /// utility VM can hold a registered distro's VHDX against moves while reads stay permitted).
    /// Must throw on failure; overwrites a stale destination.</summary>
    void CopyFile(string sourcePath, string destinationPath);

    /// <summary>Length in bytes of an existing file (copy verification). Must throw when the file
    /// does not exist.</summary>
    long GetFileLength(string path);

    /// <summary>Ensures a directory exists (the canonical install dir before the VHDX move).</summary>
    void CreateDirectory(string path);

    /// <summary>Best-effort recursive delete (temp dir, leftover staging dir); never throws.</summary>
    void DeleteDirectoryBestEffort(string path);
}

/// <summary>The default host filesystem (System.IO).</summary>
public sealed class VmUpgradeHostFileSystem : IVmUpgradeHostFileSystem
{
    public string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-vm-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) =>
        File.Copy(sourcePath, destinationPath, overwrite: true);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort hygiene only — never the primary failure.
        }
    }
}

/// <summary>The tier-2 in-place VM upgrade seam (interface-first, per Core convention).</summary>
public interface IVmUpgradeOrchestrator
{
    /// <summary>Runs the full <see cref="VmUpgradePlan"/>; per-step progress via
    /// <paramref name="progress"/> (marshalling is the caller's concern).</summary>
    Task<VmUpgradeResult> UpgradeAsync(VmUpgradeOptions options, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Executes the P2-21 §3.6 in-place MainguardOS upgrade by driving <see cref="VmUpgradePlan.Steps"/>
/// in order over the <see cref="IWslRunner"/> seam — argument lists only, never a shell string,
/// never a VM-wide lifecycle verb (G-12; every verb is scoped to <c>MainguardEnv</c> /
/// <c>MainguardEnv-staging</c>).
///
/// <para><b>Migration transport.</b> The user trees (<see cref="VmUpgradeCommands.UserDataPath"/>
/// and <see cref="VmUpgradeCommands.DaemonStatePath"/>, minus the rotating
/// <see cref="VmUpgradeCommands.DaemonTokenFileName"/>) cross distros as tar archives written to a
/// host temp file: the old distro packs with <c>tar -cpf /mnt/&lt;drive&gt;/…</c> and staging
/// unpacks with <c>tar -xpf</c> — two plain argv invocations. A host-side stdout→stdin pipe
/// through <see cref="IWslRunner.RunAsync"/> was rejected because the seam is string-typed
/// (UTF-8 decode/encode round-trips corrupt binary tar bytes) and fully buffered in memory
/// (multi-GB repo trees); a shell pipe string is banned outright. The host file is binary-safe,
/// disk-buffered, and deleted after the run.</para>
///
/// <para><b>Invariant 3 (launch blocker).</b> The old distro is terminated/unregistered ONLY after
/// <c>migrate-user-data</c> AND <c>validate-migration</c> both succeed. Validation enumerates the
/// provisioned repos/worktrees inside the OLD distro (<c>find</c>, depth-bounded), enumerates the
/// same trees inside STAGING, and requires the pure set difference
/// (<see cref="VmUpgradeMigrator.FindMissingFromListings"/>) to be empty — real in-distro state,
/// not host-side assumptions.</para>
///
/// <para><b>Failure atomicity.</b> Any failure before the old distro is unregistered leaves it
/// registered (its daemon re-started) and cleans staging up best-effort —
/// <see cref="VmUpgradeFailureKind.OldDistroIntact"/>. A failure after the retire but before the
/// promote surfaces <see cref="VmUpgradeFailureKind.StrandedAfterRetire"/> naming exactly where
/// the migrated VHDX lives and the recovery command; that VHDX is never deleted.</para>
///
/// <para><b>Promotion mechanics.</b> <c>wsl --unregister</c> deletes the distro's install dir, so
/// staging's VHDX is first unlocked (<c>--terminate MainguardEnv-staging</c>) and MOVED to the
/// canonical install dir on the host, then the stale staging registration is dropped, then the
/// moved VHDX is registered as <c>MainguardEnv</c> via <c>--import-in-place</c>. The daemon unit is
/// then started on the promoted distro (best-effort — the unit is shipped enabled, so any later
/// boot starts it too). Tier-1 (<see cref="DaemonAutoRefresh"/>) runs at next app startup and
/// re-syncs the daemon build if the new payload's daemon trails the app.</para>
///
/// <para><b>Promote resilience (field incident, 2026-07).</b> WSL's shared utility VM holds a
/// registered-but-stopped distro's VHDX for as long as ANY distro keeps the utility VM alive
/// (docker-desktop or a personal distro is enough), so the move can fail with a sharing violation
/// indefinitely — not just transiently. The move is therefore retried a short, bounded number of
/// times (covers machines where the utility VM does idle out), and when still blocked the promote
/// falls back to <b>copy-then-cleanup</b>: COPY the VHDX to the canonical path (reads are permitted
/// under the hold), verify the copy (exists + length matches), and only then
/// <c>--unregister MainguardEnv-staging</c> (which deletes the original VHDX + the registration) and
/// <c>--import-in-place</c> the canonical copy. Note the deliberate REORDER: the move path
/// unregisters staging AFTER the VHDX left its install dir; the copy path unregisters AFTER the
/// verified copy (the unregister is the cleanup that deletes the original). The copy briefly
/// doubles the VHDX on disk — accepted; a disk-headroom preflight is a tracked separate follow-up.
/// <see cref="VmUpgradeFailureKind.StrandedAfterRetire"/> is terminal only when BOTH strategies
/// fail, and its message names both failures; the data-bearing VHDX (whichever location holds the
/// newest verified copy) is never deleted on any failure path.</para>
/// </summary>
public sealed class VmUpgradeOrchestrator : IVmUpgradeOrchestrator
{
    private const string UserDataTarName = "user-data.tar";
    private const string DaemonStateTarName = "daemon-state.tar";

    /// <summary>Bounded attempts for the promote's VHDX move — covers the transient-release case
    /// (the WSL utility VM idling out between attempts) before the copy fallback takes over.</summary>
    internal const int MoveAttempts = 3;

    private static readonly TimeSpan DefaultMoveRetryDelay = TimeSpan.FromSeconds(3);

    private readonly IWslRunner _wsl;
    private readonly IVmUpgradeHostFileSystem _fs;
    private readonly TimeSpan _moveRetryDelay;
    private readonly BuildProvenanceGate _provenance;

    public VmUpgradeOrchestrator(
        IWslRunner wsl,
        IVmUpgradeHostFileSystem? hostFileSystem = null,
        TimeSpan? moveRetryDelay = null,
        BuildProvenanceGate? provenance = null)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _fs = hostFileSystem ?? new VmUpgradeHostFileSystem();
        _moveRetryDelay = moveRetryDelay ?? DefaultMoveRetryDelay;
        _provenance = provenance ?? new BuildProvenanceGate();
    }

    public async Task<VmUpgradeResult> UpgradeAsync(
        VmUpgradeOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stagingVhdx = Path.Combine(options.StagingInstallDir, "ext4.vhdx");
        var promotedVhdx = Path.Combine(options.CanonicalInstallDir, "ext4.vhdx");

        string? tempDir = null;
        var oldRetired = false;      // true only once `--unregister MainguardEnv` succeeded
        var vhdxMovedTo = (string?)null;   // canonical path once the newest verified copy lives there
        var promoteStrategy = (string?)null; // "move" | "copy-then-cleanup" once the VHDX landed
        var hasUserData = false;
        var hasDaemonState = false;

        try
        {
            foreach (var step in VmUpgradePlan.Steps())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(step.Description);

                switch (step.Id)
                {
                    case "import-staging":
                        // MG-9: verify the NEW rootfs before anything about the running VM is touched.
                        // The tarball becomes a root-executing VM; a build-time attestation is the only
                        // evidence about it that a file swap cannot regenerate. Refusing here is the
                        // cheapest possible failure — the old distro has not been touched yet, so the
                        // posture stays OldDistroIntact.
                        var tarballProvenance = await _provenance
                            .VerifyFileAsync(BuildArtifactKind.MainguardOsTarball, options.TarballPath, ct)
                            .ConfigureAwait(false);
                        if (tarballProvenance.MustRefuse)
                            throw new VmUpgradeStepException(tarballProvenance.Reason);
                        progress?.Report(tarballProvenance.Reason);

                        // A stale staging from a previously failed run would make the import fail —
                        // clear it best-effort first (idempotent re-run posture).
                        await TryRunAsync(VmUpgradeCommands.TerminateStaging(), ct).ConfigureAwait(false);
                        await TryRunAsync(VmUpgradeCommands.UnregisterStaging(), ct).ConfigureAwait(false);
                        await RequireAsync(
                            VmUpgradeCommands.ImportStaging(options.StagingInstallDir, options.TarballPath),
                            "import the new payload as MainguardEnv-staging", ct).ConfigureAwait(false);
                        break;

                    case "migrate-user-data":
                        // Quiesce BOTH daemons so state is copied at rest: the old one so its
                        // SQLite DB / ledgers aren't mid-write (reversible — the failure path
                        // re-starts it, so this is not a "retire"), and staging's, which systemd
                        // auto-starts the moment any in-staging command boots the distro.
                        await RequireAsync(DaemonUpdateCommands.StopUnit(), "stop the old distro's mainguardd unit", ct)
                            .ConfigureAwait(false);
                        await RequireAsync(
                            VmUpgradeCommands.StopUnitInStaging(), "stop staging's auto-started mainguardd unit", ct)
                            .ConfigureAwait(false);

                        tempDir = _fs.CreateTempDirectory();
                        hasUserData = await MigrateTreeAsync(
                            VmUpgradeCommands.UserDataPath, Path.Combine(tempDir, UserDataTarName),
                            excludeDaemonToken: false, progress, ct).ConfigureAwait(false);
                        hasDaemonState = await MigrateTreeAsync(
                            VmUpgradeCommands.DaemonStatePath, Path.Combine(tempDir, DaemonStateTarName),
                            excludeDaemonToken: true, progress, ct).ConfigureAwait(false);
                        if (!hasUserData)
                        {
                            progress?.Report("No provisioned repositories found — nothing to migrate.");
                        }

                        break;

                    case "validate-migration":
                        if (hasUserData)
                        {
                            await ValidateTreeAsync(VmUpgradeCommands.UserDataPath, "repos/worktrees", ct)
                                .ConfigureAwait(false);
                        }

                        if (hasDaemonState)
                        {
                            await ValidateTreeAsync(VmUpgradeCommands.DaemonStatePath, "daemon state", ct)
                                .ConfigureAwait(false);
                        }

                        break;

                    case "terminate-old":
                        await RequireAsync(VmUpgradeCommands.TerminateOld(), "terminate the old MainguardEnv", ct)
                            .ConfigureAwait(false);
                        break;

                    case "unregister-old":
                        await RequireAsync(VmUpgradeCommands.UnregisterOld(), "unregister the old MainguardEnv", ct)
                            .ConfigureAwait(false);
                        oldRetired = true;
                        break;

                    case "promote-staging":
                        // Unlock the VHDX, get it to the canonical home BEFORE unregistering
                        // staging (unregister deletes the install dir's contents), then register
                        // the canonical VHDX under the canonical name. Preferred: a bounded-retry
                        // MOVE; fallback when WSL's utility VM holds the file: COPY, verify, and
                        // let the staging unregister delete the original (see the class doc).
                        await RequireAsync(
                            VmUpgradeCommands.TerminateStaging(), "terminate MainguardEnv-staging before the promote", ct)
                            .ConfigureAwait(false);
                        _fs.CreateDirectory(options.CanonicalInstallDir);
                        var moveFailure = await TryMoveWithRetryAsync(stagingVhdx, promotedVhdx, progress, ct)
                            .ConfigureAwait(false);
                        if (moveFailure is null)
                        {
                            // Move order: VHDX left staging's install dir → unregister staging
                            // (best-effort hygiene; nothing data-bearing remains there) → import.
                            promoteStrategy = "move";
                            vhdxMovedTo = promotedVhdx;
                            await TryRunAsync(VmUpgradeCommands.UnregisterStaging(), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            progress?.Report(
                                "The VHDX move is blocked (WSL still holds the staging disk) — falling back to copy-then-cleanup.");
                            try
                            {
                                _fs.CopyFile(stagingVhdx, promotedVhdx);
                                var sourceLength = _fs.GetFileLength(stagingVhdx);
                                var copiedLength = _fs.GetFileLength(promotedVhdx);
                                if (copiedLength != sourceLength)
                                {
                                    throw new IOException(
                                        $"copied VHDX is {copiedLength} bytes but the source is {sourceLength} bytes");
                                }
                            }
                            catch (Exception copyEx)
                            {
                                // BOTH strategies failed — the terminal stranded state names both.
                                throw new VmUpgradeStepException(
                                    $"could not promote the staging VHDX '{stagingVhdx}' to '{promotedVhdx}': "
                                    + $"the move failed after {MoveAttempts} attempts ({moveFailure.Message}), "
                                    + $"and the copy fallback also failed ({copyEx.Message})");
                            }

                            // Copy order (REORDERED vs the move path): the canonical copy is
                            // verified FIRST, and only then is staging unregistered — that
                            // unregister deletes the original VHDX, so it is REQUIRED here (a
                            // still-registered staging would leave the disk doubled and let the
                            // final staging-dir cleanup race a live registration).
                            promoteStrategy = "copy-then-cleanup";
                            vhdxMovedTo = promotedVhdx;
                            await RequireAsync(
                                VmUpgradeCommands.UnregisterStaging(),
                                "unregister MainguardEnv-staging after the verified fallback copy", ct)
                                .ConfigureAwait(false);
                        }

                        await RequireAsync(
                            VmUpgradeCommands.PromoteStagingInPlace(promotedVhdx),
                            "register the upgraded VHDX as MainguardEnv (--import-in-place)", ct).ConfigureAwait(false);
                        break;

                    default:
                        throw new VmUpgradeStepException($"unknown upgrade plan step '{step.Id}'");
                }
            }

            // The unit is shipped enabled, so this is belt-and-braces (any boot starts it); a start
            // hiccup here must not fail an upgrade that already promoted successfully.
            progress?.Report("Starting the Mainguard daemon on the upgraded environment.");
            await TryRunAsync(DaemonUpdateCommands.StartUnit(), ct).ConfigureAwait(false);
            _fs.DeleteDirectoryBestEffort(options.StagingInstallDir);

            return new VmUpgradeResult(
                true, VmUpgradeFailureKind.None,
                "Mainguard OS was upgraded in place; provisioned repositories, worktrees, and daemon state were migrated and validated.",
                PromoteStrategy: promoteStrategy);
        }
        catch (VmUpgradeStepException ex) when (!oldRetired)
        {
            // The old distro is still registered: clean staging up, bring the old daemon back, and
            // report a retryable failure. User data was never at risk.
            await TryRunAsync(VmUpgradeCommands.TerminateStaging(), ct).ConfigureAwait(false);
            await TryRunAsync(VmUpgradeCommands.UnregisterStaging(), ct).ConfigureAwait(false);
            _fs.DeleteDirectoryBestEffort(options.StagingInstallDir);
            await TryRunAsync(DaemonUpdateCommands.StartUnit(), ct).ConfigureAwait(false);
            return new VmUpgradeResult(
                false, VmUpgradeFailureKind.OldDistroIntact,
                $"The upgrade stopped before touching your current environment ({ex.Message}). "
                + "Everything is still where it was — you can retry the upgrade anytime.");
        }
        catch (VmUpgradeStepException ex)
        {
            // After the retire: the migrated data is safe in the VHDX — name exactly where it is
            // and how to finish; NEVER delete it, never pretend a distro exists.
            var vhdxPath = vhdxMovedTo ?? stagingVhdx;
            return new VmUpgradeResult(
                false, VmUpgradeFailureKind.StrandedAfterRetire,
                $"The old environment was retired but the new one could not be registered ({ex.Message}). "
                + $"Your migrated data is intact in the upgrade disk at '{vhdxPath}'. "
                + $"To finish manually, run: wsl --import-in-place {WslCommands.DistroName} \"{vhdxPath}\" "
                + "— or contact support with this message. Do not delete that file.",
                StagingVhdxPath: vhdxPath,
                PromoteStrategy: promoteStrategy);
        }
        finally
        {
            if (tempDir is not null)
            {
                _fs.DeleteDirectoryBestEffort(tempDir);
            }
        }
    }

    /// <summary>The promote's preferred strategy: a short bounded-retry MOVE of the staging VHDX
    /// to its canonical home. Returns null on success, or the LAST failure once the attempts are
    /// exhausted (the caller falls back to copy-then-cleanup — the hold that blocks the move on
    /// machines whose WSL utility VM never idles out is not transient; see the class doc).</summary>
    private async Task<Exception?> TryMoveWithRetryAsync(
        string stagingVhdx, string promotedVhdx, IProgress<string>? progress, CancellationToken ct)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= MoveAttempts; attempt++)
        {
            try
            {
                _fs.MoveFile(stagingVhdx, promotedVhdx);
                return null;
            }
            catch (Exception ex)
            {
                failure = ex;
                if (attempt < MoveAttempts)
                {
                    progress?.Report(
                        $"VHDX move attempt {attempt}/{MoveAttempts} failed ({ex.Message}) — retrying shortly…");
                    await Task.Delay(_moveRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }

        return failure;
    }

    /// <summary>Migrates one tree old→staging via the host-file tar transport. Returns false (and
    /// does nothing) when the tree does not exist in the old distro.</summary>
    private async Task<bool> MigrateTreeAsync(
        string treePath, string hostTarPath, bool excludeDaemonToken, IProgress<string>? progress, CancellationToken ct)
    {
        var probe = await _wsl.RunAsync(VmUpgradeCommands.ProbeOldDirectory(treePath), stdin: null, ct)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return false;
        }

        progress?.Report($"Migrating {treePath} into the new environment…");
        var vmTarPath = ToVmPath(hostTarPath);
        await RequireAsync(
            VmUpgradeCommands.ExportTreeToTar(treePath, vmTarPath, excludeDaemonToken),
            $"pack '{treePath}' from the old distro", ct).ConfigureAwait(false);
        await RequireAsync(
            VmUpgradeCommands.MakeStagingDirectory(treePath),
            $"create '{treePath}' in staging", ct).ConfigureAwait(false);
        await RequireAsync(
            VmUpgradeCommands.ExtractTreeFromTar(treePath, vmTarPath),
            $"unpack '{treePath}' into staging", ct).ConfigureAwait(false);
        await RequireAsync(
            VmUpgradeCommands.ChownTreeInStaging(treePath),
            $"re-own '{treePath}' in staging", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The invariant-3 gate for one tree: enumerate old, enumerate staging, require the
    /// pure diff to be empty.</summary>
    private async Task ValidateTreeAsync(string treePath, string what, CancellationToken ct)
    {
        var oldListing = await RequireAsync(
            VmUpgradeCommands.EnumerateOldTree(treePath),
            $"enumerate the old distro's {what}", ct).ConfigureAwait(false);
        var stagingListing = await RequireAsync(
            VmUpgradeCommands.EnumerateStagingTree(treePath),
            $"enumerate staging's {what}", ct).ConfigureAwait(false);

        // The daemon token (rotates per start) and the per-subsystem logs dir (diagnostic, started
        // fresh in the new distro) are deliberately NOT migrated, so their absence in staging is
        // correct — filter them out of the expected set before the diff. The logs dir only exists
        // under the daemon-state tree; the filter is a no-op for the user-data tree.
        var logsRoot = treePath.TrimEnd('/') + "/" + VmUpgradeCommands.DaemonLogsDirName;
        var expected = VmUpgradeMigrator.ParseFindListing(oldListing.StdOut)
            .Where(p => !p.EndsWith("/" + VmUpgradeCommands.DaemonTokenFileName, StringComparison.Ordinal)
                     && !string.Equals(p, logsRoot, StringComparison.Ordinal)
                     && !p.StartsWith(logsRoot + "/", StringComparison.Ordinal));
        var missing = VmUpgradeMigrator.FindMissingFromListings(
            expected, VmUpgradeMigrator.ParseFindListing(stagingListing.StdOut));
        if (missing.Count > 0)
        {
            var sample = string.Join(", ", missing.Take(5));
            throw new VmUpgradeStepException(
                $"migration validation failed for {what}: {missing.Count} path(s) missing in staging (e.g. {sample})");
        }
    }

    /// <summary>The <c>/mnt/&lt;drive&gt;/…</c> form of a host temp file (native Linux paths —
    /// tests, CI — pass through unchanged; same shape as <see cref="DaemonUpdater.ToVmPath"/>).</summary>
    public static string ToVmPath(string hostPath) =>
        HostPathTranslator.ToDaemonOpenablePath(hostPath, daemonIsWindows: false);

    private async Task<WslRunResult> RequireAsync(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new VmUpgradeStepException($"could not {what} (exit {result.ExitCode}): {detail.Trim()}");
        }

        return result;
    }

    private async Task TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recovery/hygiene is best-effort; the primary outcome is what gets reported.
        }
    }

    /// <summary>Internal control-flow signal for a failed step (never escapes
    /// <see cref="UpgradeAsync"/> — callers see a typed <see cref="VmUpgradeResult"/>).</summary>
    private sealed class VmUpgradeStepException : Exception
    {
        public VmUpgradeStepException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>The tier-2 availability decision the App consults after the tier-1 check.</summary>
/// <param name="OfferUpgrade">True when the installed payload is provably older than the bundled one.</param>
/// <param name="InstalledVersion">The VM's payload version ('' when unknown).</param>
/// <param name="ExpectedVersion">The app-bundled payload version ('' when unknown).</param>
public sealed record VmUpgradeAvailability(bool OfferUpgrade, string InstalledVersion, string ExpectedVersion);

/// <summary>
/// The one tier-2 detection call the App makes after the tier-1 daemon check (never throws; a
/// detection hiccup means "no offer", logged). Expected version: the app-bundled
/// <c>payload/mainguardos-release</c> stamp (packaged next to the tarball). Installed version: the
/// tier-1 <c>GetDaemonInfo</c> answer's payload version, falling back to reading
/// <c>/etc/mainguardos-release</c> in-distro over <see cref="IWslRunner"/> when the daemon is down
/// or predates the RPC. The decision itself is <see cref="VmUpgradePolicy.IsUpgradeAvailable"/>.
/// </summary>
public static class VmUpgradeCheck
{
    /// <summary>Where the packaged app ships the payload release stamp (the MSBuild copy step in
    /// Mainguard.Pro.App.csproj, next to <c>payload/MainguardOS.tar.gz</c>).</summary>
    public static string DefaultPayloadStampPath() =>
        Path.Combine(AppContext.BaseDirectory, "payload", "mainguardos-release");

    /// <param name="payloadStampPath">Path of the app-bundled <c>mainguardos-release</c> stamp.</param>
    /// <param name="queryDaemonInfo">Calls <c>GetDaemonInfo</c>; null for an <c>Unimplemented</c>
    /// answer; may throw when the daemon is unreachable (triggers the wsl fallback).</param>
    /// <param name="wsl">Fallback reader of the in-VM release stamp.</param>
    /// <param name="log">Outcome breadcrumbs (the App passes its oobe.log writer).</param>
    public static async Task<VmUpgradeAvailability> RunAsync(
        string payloadStampPath,
        Func<CancellationToken, Task<DaemonVersionInfo?>> queryDaemonInfo,
        IWslRunner wsl,
        Action<string> log,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadStampPath);
        ArgumentNullException.ThrowIfNull(queryDaemonInfo);
        ArgumentNullException.ThrowIfNull(wsl);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            string expected;
            try
            {
                expected = File.Exists(payloadStampPath)
                    ? MainguardOsReleaseStamp.ParseVersion(await File.ReadAllTextAsync(payloadStampPath, ct).ConfigureAwait(false))
                    : string.Empty;
            }
            catch (IOException)
            {
                expected = string.Empty;
            }

            if (VmUpgradePolicy.TryParseVersion(expected) is null)
            {
                log($"vm upgrade check: no readable payload stamp at '{payloadStampPath}' — no offer");
                return new VmUpgradeAvailability(false, "", expected);
            }

            var installed = string.Empty;
            try
            {
                var info = await queryDaemonInfo(ct).ConfigureAwait(false);
                installed = info?.PayloadVersion ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Daemon down / pre-RPC — fall through to the in-distro stamp read below.
            }

            if (VmUpgradePolicy.TryParseVersion(installed) is null)
            {
                var read = await wsl.RunAsync(VmUpgradeCommands.ReadInstalledReleaseStamp(), stdin: null, ct)
                    .ConfigureAwait(false);
                if (read.Succeeded)
                {
                    installed = MainguardOsReleaseStamp.ParseVersion(read.StdOut);
                }
            }

            if (VmUpgradePolicy.TryParseVersion(installed) is null)
            {
                log("vm upgrade check: installed payload version unknown (daemon and stamp both unreadable) — no offer");
                return new VmUpgradeAvailability(false, installed, expected);
            }

            var offer = VmUpgradePolicy.IsUpgradeAvailable(installed, expected);
            log(offer
                ? $"vm upgrade check: installed payload {installed} < bundled {expected} — offering the in-place upgrade"
                : $"vm upgrade check: installed payload {installed}, bundled {expected} — no upgrade needed");
            return new VmUpgradeAvailability(offer, installed, expected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new VmUpgradeAvailability(false, "", "");
        }
        catch (Exception ex)
        {
            // Detection must never ripple into the app.
            log($"vm upgrade check failed (no offer): {ex.Message}");
            return new VmUpgradeAvailability(false, "", "");
        }
    }
}
