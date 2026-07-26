using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>The <c>GetDaemonInfo</c> result as the Core skew policy consumes it (proto-free —
/// Mainguard.Agents never references the gRPC stack).</summary>
/// <param name="DaemonVersion">The daemon's assembly informational version.</param>
/// <param name="PayloadVersion">The MainguardOS payload version from <c>/etc/mainguardos-release</c>;
/// empty when the stamp is absent.</param>
public sealed record DaemonVersionInfo(string DaemonVersion, string PayloadVersion);

/// <summary>Why <see cref="DaemonUpdatePolicy.Decide"/> answered the way it did — the typed form, so a
/// REFUSAL can be logged and surfaced instead of vanishing into a <c>false</c> that reads like
/// "up to date".</summary>
public enum DaemonRefreshDecisionKind
{
    /// <summary>The app-shipped daemon is newer (or the same version from a different commit) — refresh.</summary>
    Refresh,

    /// <summary>The deployed daemon already matches — nothing to do.</summary>
    UpToDate,

    /// <summary>The app ships an OLDER daemon than the one deployed. Refusing: promoting it would be a
    /// silent downgrade of a privileged, root-run service.</summary>
    RefusedDowngrade,

    /// <summary>One of the two versions could not be ordered, so "moves forward" could not be
    /// established. Refusing rather than guessing.</summary>
    RefusedUncomparable,
}

/// <summary>One tier-1 skew decision plus the sentence explaining it (for oobe.log / the startup toast).</summary>
public sealed record DaemonRefreshDecision(DaemonRefreshDecisionKind Kind, string Reason)
{
    /// <summary>The single boolean the refresh flow gates on. Only <see cref="DaemonRefreshDecisionKind.Refresh"/>
    /// moves anything; both refusals and "up to date" leave the deployed daemon alone.</summary>
    public bool ShouldRefresh => Kind == DaemonRefreshDecisionKind.Refresh;
}

/// <summary>
/// The pure tier-1 version-skew decision: should the App refresh the in-VM daemon? The field
/// failure this guards against: the daemon deployed inside MainguardEnv is the build baked into the
/// MainguardOS tarball at install time, so every RPC the app grows later answers
/// <c>Unimplemented</c> until the daemon is refreshed.
///
/// <para><b>MG-15 — the decision is now MONOTONIC.</b> It used to be a string inequality: "the app's
/// version is not the daemon's version" meant refresh, in either direction. That made an app rolled
/// back to an older build (a Velopack rollback, a stale second install, a downgraded payload dir)
/// silently overwrite a NEWER daemon at <c>/opt/mainguard</c> with an older binary — running as root,
/// with every fix the newer build carried undone, and no signal that anything went backwards. Version
/// order, not version difference, now gates the promote.</para>
/// </summary>
public static class DaemonUpdatePolicy
{
    /// <summary>
    /// True when the in-VM daemon should be refreshed from the app-shipped payload — the boolean
    /// shorthand for <see cref="Decide"/>. Both REFUSALS also answer false; call
    /// <see cref="Decide"/> when the caller needs to tell "already current" from "refused".
    /// </summary>
    public static bool IsRefreshNeeded(string appVersion, DaemonVersionInfo? daemonInfo)
        => Decide(appVersion, daemonInfo).ShouldRefresh;

    /// <summary>
    /// The full skew decision. <paramref name="daemonInfo"/> is the <c>GetDaemonInfo</c> answer — pass
    /// <c>null</c> when the daemon answered <c>Unimplemented</c> (a pre-<c>GetDaemonInfo</c> daemon IS
    /// the skew signal). A daemon that could not be reached at all is NOT a skew signal — never call
    /// this for daemon-down; skip instead (the reconnect machinery owns liveness).
    ///
    /// <para>The ladder, in order:</para>
    /// <list type="number">
    ///   <item>A daemon that cannot name itself is pre-RPC — refresh (there is no version to order
    ///   against, and every RPC the app grew since is answering <c>Unimplemented</c>).</item>
    ///   <item>Either version unparseable → <see cref="DaemonRefreshDecisionKind.RefusedUncomparable"/>.
    ///   We cannot show the move is forward, so we do not make it.</item>
    ///   <item>App version OLDER than the deployed daemon →
    ///   <see cref="DaemonRefreshDecisionKind.RefusedDowngrade"/>.</item>
    ///   <item>App version NEWER → refresh (the release train).</item>
    ///   <item>Precedence-EQUAL: refresh only when BOTH sides carry a commit hash
    ///   (<c>+&lt;sha&gt;</c>) and the hashes differ — a dev rebuild or a same-version hotfix built
    ///   from a different commit than the deployed daemon. Without this, iterating on the daemon at an
    ///   unchanged version silently never redeploys (the field trap: my changes never reached
    ///   MainguardEnv, 2026-07-22). If either side lacks the hash we cannot tell the builds apart, so
    ///   the matched version stands and nothing is force-refreshed. Note this rule is deliberately
    ///   NOT ordered — build metadata has no SemVer precedence, so "different commit" means "different
    ///   bytes", never "newer"; it is a developer-loop escape hatch, and it is the one path here that
    ///   can still move the daemon sideways onto a same-version build.</item>
    /// </list>
    /// </summary>
    public static DaemonRefreshDecision Decide(string appVersion, DaemonVersionInfo? daemonInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

        if (daemonInfo is null || string.IsNullOrWhiteSpace(daemonInfo.DaemonVersion))
        {
            return new DaemonRefreshDecision(
                DaemonRefreshDecisionKind.Refresh,
                "the deployed daemon predates GetDaemonInfo (or cannot name its version) — refreshing");
        }

        var daemonVersion = daemonInfo.DaemonVersion;
        var order = UpdateVersion.TryCompare(appVersion, daemonVersion);
        if (order is null)
        {
            return new DaemonRefreshDecision(
                DaemonRefreshDecisionKind.RefusedUncomparable,
                $"refusing to refresh: app version '{appVersion}' and daemon version '{daemonVersion}' "
                + "cannot be ordered, so the refresh could not be shown to move forward");
        }

        if (order < 0)
        {
            return new DaemonRefreshDecision(
                DaemonRefreshDecisionKind.RefusedDowngrade,
                $"refusing to refresh: this app ships daemon {StripBuildMetadata(appVersion)} but "
                + $"{StripBuildMetadata(daemonVersion)} is deployed — promoting it would DOWNGRADE the "
                + "root-run daemon. Update Mainguard instead, or clear /opt/mainguard deliberately.");
        }

        if (order > 0)
        {
            return new DaemonRefreshDecision(
                DaemonRefreshDecisionKind.Refresh,
                $"app ships daemon {StripBuildMetadata(appVersion)}, "
                + $"{StripBuildMetadata(daemonVersion)} is deployed — refreshing");
        }

        // Precedence-equal: only a differing commit hash on BOTH sides is a genuinely different binary.
        var appHash = BuildMetadata(appVersion);
        var daemonHash = BuildMetadata(daemonVersion);
        if (appHash.Length > 0 && daemonHash.Length > 0 && !string.Equals(appHash, daemonHash, StringComparison.Ordinal))
        {
            return new DaemonRefreshDecision(
                DaemonRefreshDecisionKind.Refresh,
                $"same version ({StripBuildMetadata(appVersion)}) built from a different commit "
                + $"({daemonHash} deployed, {appHash} shipped) — refreshing");
        }

        return new DaemonRefreshDecision(
            DaemonRefreshDecisionKind.UpToDate,
            $"daemon {daemonVersion} matches app {appVersion} — up to date");
    }

    /// <summary>Drops SemVer build metadata (<c>0.2.0+abc123</c> → <c>0.2.0</c>).</summary>
    public static string StripBuildMetadata(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    /// <summary>The SemVer build metadata after '+' (the commit hash), or empty when there is none.</summary>
    public static string BuildMetadata(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 && plus < trimmed.Length - 1 ? trimmed[(plus + 1)..] : string.Empty;
    }
}

/// <summary>
/// Pure argument-list builders for the in-place daemon refresh — the automated form of the manual
/// field fix (publish → copy over <c>/opt/mainguard</c> → rename apphost → chmod → restart unit).
/// Kept separate from the runner (like <see cref="WslCommands"/>/<see cref="VmUpgradeCommands"/>)
/// so the command shapes — and the G-12 invariant that <b>no builder ever emits the VM-wide
/// shutdown verb</b> — are unit-testable without a process. Everything is scoped in-distro to
/// <c>MainguardEnv</c>; the swap keeps <see cref="RollbackDir"/> so a bad payload is recoverable.
/// </summary>
public static class DaemonUpdateCommands
{
    /// <summary>Where the payload installs the daemon (see build/mainguardos/README.md).</summary>
    public const string InstallDir = "/opt/mainguard";

    /// <summary>The staged copy of the new daemon, assembled before the swap.</summary>
    public const string StagingDir = "/opt/mainguard.new";

    /// <summary>The previous install, kept across the swap as the rollback.</summary>
    public const string RollbackDir = "/opt/mainguard.old";

    /// <summary>The systemd unit (and the apphost's required name — P2-05's <c>pgrep -x mainguardd</c>).</summary>
    public const string UnitName = "mainguardd";

    /// <summary>The apphost name a raw <c>dotnet publish</c> emits (renamed to <see cref="UnitName"/>;
    /// a build.sh-produced payload arrives already renamed, so the rename is probed first).</summary>
    public const string PublishedApphostName = "Mainguard.Server";

    public static IReadOnlyList<string> StopUnit() =>
        WslCommands.InDistroAsRoot("systemctl", "stop", UnitName);

    public static IReadOnlyList<string> StartUnit() =>
        WslCommands.InDistroAsRoot("systemctl", "start", UnitName);

    public static IReadOnlyList<string> RemoveStaging() =>
        WslCommands.InDistroAsRoot("rm", "-rf", StagingDir);

    public static IReadOnlyList<string> CreateStaging() =>
        WslCommands.InDistroAsRoot("mkdir", "-p", StagingDir);

    /// <summary>Copies the payload directory's CONTENTS (<c>&lt;dir&gt;/.</c>) into staging —
    /// <paramref name="vmPayloadDir"/> is the /mnt-translated form of the Windows payload dir.</summary>
    public static IReadOnlyList<string> CopyPayloadIntoStaging(string vmPayloadDir) =>
        WslCommands.InDistroAsRoot("cp", "-r", vmPayloadDir.TrimEnd('/') + "/.", StagingDir + "/");

    /// <summary>Exit 0 iff the staged payload still carries the un-renamed apphost.</summary>
    public static IReadOnlyList<string> ProbePublishedApphost() =>
        WslCommands.InDistroAsRoot("test", "-e", StagingDir + "/" + PublishedApphostName);

    /// <summary>The apphost rename (<c>Mainguard.Server</c> → <c>mainguardd</c>; it loads
    /// <c>Mainguard.Server.dll</c> by its embedded name, so the rename is transparent).</summary>
    public static IReadOnlyList<string> RenameApphost() =>
        WslCommands.InDistroAsRoot("mv", StagingDir + "/" + PublishedApphostName, StagingDir + "/" + UnitName);

    public static IReadOnlyList<string> MakeDaemonExecutable() =>
        WslCommands.InDistroAsRoot("chmod", "0755", StagingDir + "/" + UnitName);

    /// <summary>Drops the PREVIOUS refresh's rollback before this swap creates a new one.</summary>
    public static IReadOnlyList<string> RemoveRollback() =>
        WslCommands.InDistroAsRoot("rm", "-rf", RollbackDir);

    public static IReadOnlyList<string> RetireCurrent() =>
        WslCommands.InDistroAsRoot("mv", InstallDir, RollbackDir);

    public static IReadOnlyList<string> PromoteStaging() =>
        WslCommands.InDistroAsRoot("mv", StagingDir, InstallDir);

    /// <summary>The recovery move when the promote failed AFTER the retire — only ever issued while
    /// <see cref="InstallDir"/> is absent (a blind restore into an existing dir would nest it).</summary>
    public static IReadOnlyList<string> RestoreRollback() =>
        WslCommands.InDistroAsRoot("mv", RollbackDir, InstallDir);

    /// <summary>
    /// SHA-256 of every regular file under <see cref="StagingDir"/>, as <c>&lt;hash&gt;  &lt;path&gt;</c>
    /// lines — the in-VM half of the staged-payload integrity check. An argument list, not a shell
    /// string: <c>find … -exec sha256sum {} +</c> needs no shell, so nothing here can be word-split or
    /// glob-expanded (the payload directory name is fixed and Mainguard-owned anyway).
    /// </summary>
    public static IReadOnlyList<string> HashStagedPayload() =>
        WslCommands.InDistroAsRoot("find", StagingDir, "-type", "f", "-exec", "sha256sum", "{}", "+");

    /// <summary>Every builder — used by the G-12 unit test to prove none emit the VM-wide shutdown
    /// verb and all stay scoped to <c>MainguardEnv</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        StopUnit(),
        StartUnit(),
        RemoveStaging(),
        CreateStaging(),
        CopyPayloadIntoStaging("/mnt/c/Program Files/Mainguard/payload/daemon"),
        ProbePublishedApphost(),
        RenameApphost(),
        MakeDaemonExecutable(),
        RemoveRollback(),
        RetireCurrent(),
        PromoteStaging(),
        RestoreRollback(),
        HashStagedPayload(),
    };
}

/// <summary>The payload on disk could not be turned into a manifest (unreadable, empty, or structurally
/// not a daemon build). Raised before anything is stopped or moved.</summary>
public sealed class DaemonPayloadException : Exception
{
    public DaemonPayloadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A content manifest of the app-shipped daemon payload: every regular file's path and SHA-256, plus
/// the structural check that it actually looks like a daemon build.
///
/// <para><b>Why (MG-9).</b> The refresh used to <c>cp -r</c> the payload into the VM and promote it
/// over <c>/opt/mainguard</c> — a directory whose contents are then executed as root by systemd — with
/// <b>no hash, no manifest, no size check of any kind</b>. Any way the copy could go wrong went
/// undetected: a truncated file from a drvfs hiccup, a partial copy from a full VM disk, a payload dir
/// half-written by an interrupted app update. The daemon then failed to start, or started missing
/// pieces, with the previous good build already retired.</para>
///
/// <para><b>What this establishes, exactly:</b> that the bytes which arrived in the VM are the bytes
/// that were on the host, and that the payload is structurally a daemon build. That is an
/// <b>integrity</b> check against corruption, truncation, and partial copies.</para>
///
/// <para><b>What it does NOT establish:</b> that those bytes are legitimate. Both sides of this
/// comparison are derived from the same source directory, so an attacker who can WRITE that directory
/// simply gets their payload hashed and faithfully delivered. On the current per-user Velopack layout
/// the payload directory (<c>AppContext.BaseDirectory/payload/daemon</c>) is writable by the user we
/// are defending, so same-user malware is entirely unaddressed by this check. Only a signature over
/// the payload (<see cref="IPayloadSignatureVerifier"/> — a documented no-op today, because Mainguard
/// has no signing identity) or a machine-wide install root would change that. Do not describe this as
/// verifying the payload; it verifies the COPY.</para>
/// </summary>
public sealed class DaemonPayloadManifest
{
    /// <summary>The managed assembly the <c>mainguardd</c> apphost loads by embedded name. Its absence
    /// means whatever this directory holds, it is not a daemon build — the cheapest structural check
    /// that a partially-populated payload dir cannot be promoted.</summary>
    public const string RequiredAssembly = "Mainguard.Server.dll";

    private DaemonPayloadManifest(IReadOnlyDictionary<string, string> fileHashes, long totalBytes)
    {
        FileHashes = fileHashes;
        TotalBytes = totalBytes;
    }

    /// <summary>Payload-relative path (forward slashes, as the VM sees it) → lowercase hex SHA-256.</summary>
    public IReadOnlyDictionary<string, string> FileHashes { get; }

    /// <summary>Total payload size — carried for the log, so a suspiciously small promote is legible
    /// in oobe.log after the fact.</summary>
    public long TotalBytes { get; }

    /// <summary>
    /// The canonical text form of this manifest: <c>&lt;sha256&gt;  &lt;relative path&gt;</c> per line,
    /// sorted ordinally by path, LF-terminated. Byte-stable by construction (fixed order, fixed
    /// separator, fixed newline) so the same payload always produces the same bytes on any machine.
    ///
    /// <para><b>This is the artifact CI attests.</b> The daemon payload is a directory and
    /// <c>gh attestation verify</c> takes a file, so the build writes exactly this text to
    /// <see cref="DaemonUpdater.AttestedManifestPathFor"/> and attests THAT. The app then rebuilds the
    /// manifest from the payload on disk and requires it to reproduce these bytes — which is what makes
    /// a build-time attestation say something about the directory a user actually has.</para>
    /// </summary>
    public string ToCanonicalText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (relative, hash) in FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append(hash).Append("  ").Append(relative).Append('\n');
        return builder.ToString();
    }

    /// <summary>SHA-256 of <see cref="ToCanonicalText"/> — the digest an attestation over the manifest
    /// file names as its subject.</summary>
    public string CanonicalDigest() =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ToCanonicalText())))
            .ToLowerInvariant();

    /// <summary>Hashes every regular file under <paramref name="payloadDirectory"/>.</summary>
    /// <exception cref="DaemonPayloadException">The directory is missing, empty, unreadable, or does not
    /// contain <see cref="RequiredAssembly"/>.</exception>
    public static DaemonPayloadManifest Build(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        if (!Directory.Exists(payloadDirectory))
            throw new DaemonPayloadException($"the daemon payload directory '{payloadDirectory}' does not exist");

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(payloadDirectory, file).Replace('\\', '/');
                using var stream = File.OpenRead(file);
                hashes[relative] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                total += new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DaemonPayloadException(
                $"the daemon payload at '{payloadDirectory}' could not be read: {ex.Message}");
        }

        if (hashes.Count == 0)
        {
            // Promoting an empty staging dir would replace /opt/mainguard with nothing.
            throw new DaemonPayloadException($"the daemon payload at '{payloadDirectory}' is empty");
        }

        if (!hashes.Keys.Any(k => k.Equals(RequiredAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DaemonPayloadException(
                $"the daemon payload at '{payloadDirectory}' has no {RequiredAssembly} — it is not a "
                + "complete daemon build, so promoting it would leave the VM with no working daemon");
        }

        return new DaemonPayloadManifest(hashes, total);
    }

    /// <summary>
    /// Compares a staged copy against this manifest. Returns null when the copy is faithful, otherwise
    /// the first discrepancy phrased for the log. Missing, extra AND altered files are all failures:
    /// an extra file in the staged tree means the staging dir was not clean, and a promote would carry
    /// whatever was already sitting there into <c>/opt/mainguard</c>.
    /// </summary>
    public string? FindDiscrepancy(IReadOnlyDictionary<string, string> stagedHashes)
    {
        ArgumentNullException.ThrowIfNull(stagedHashes);

        foreach (var (relative, expected) in FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!stagedHashes.TryGetValue(relative, out var actual))
                return $"'{relative}' is missing from the staged copy";
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return $"'{relative}' hashed {actual} in the VM but {expected} on the host (truncated or corrupted copy)";
        }

        var extra = stagedHashes.Keys
            .Where(k => !FileHashes.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .FirstOrDefault();
        return extra is null ? null : $"'{extra}' is in the staged copy but not in the shipped payload";
    }

    /// <summary>
    /// Parses <c>sha256sum</c> output (<c>&lt;hash&gt;  &lt;absolute path&gt;</c> lines) into
    /// staging-relative path → hash. Lines outside <paramref name="stagingDir"/> or without a 64-hex
    /// hash are ignored, so a warning on stderr or a locale banner cannot be read as a file entry.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSha256Sums(string output, string stagingDir)
    {
        var prefix = stagingDir.TrimEnd('/') + "/";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length < 66)
                continue;

            var hash = line[..64];
            if (!hash.All(char.IsAsciiHexDigit))
                continue;

            // sha256sum separates with two spaces (or " *" in binary mode); take everything after.
            var path = line[64..].TrimStart(' ', '*', '\t');
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            result[path[prefix.Length..]] = hash.ToLowerInvariant();
        }

        return result;
    }
}

/// <summary>The outcome of one refresh attempt — never a bare throw at the caller.</summary>
/// <param name="Message">Human-readable outcome for the oobe.log breadcrumb.</param>
public sealed record DaemonRefreshResult(bool Succeeded, string Message);

/// <summary>The in-place daemon refresh seam (interface-first, per Core convention).</summary>
public interface IDaemonUpdater
{
    /// <summary>Refreshes the in-VM daemon from <paramref name="payloadDirectory"/> (a Windows
    /// host path; translated to its <c>/mnt/&lt;drive&gt;/…</c> form for the in-distro copy).</summary>
    Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct);
}

/// <summary>
/// Performs the tier-1 in-place daemon refresh over the <see cref="IWslRunner"/> seam — argument
/// lists only, never a shell string, never a VM-wide lifecycle verb (G-12). Sequence: stop the
/// <c>mainguardd</c> unit → stage the payload into <see cref="DaemonUpdateCommands.StagingDir"/> →
/// rename the apphost + chmod 0755 → swap dirs keeping <see cref="DaemonUpdateCommands.RollbackDir"/>
/// → start the unit. On a failure after the current install was retired, the rollback is restored;
/// the unit start is always re-attempted so a failed refresh never leaves the daemon stopped. The
/// restarted daemon writes a fresh session token, which the client re-reads per call — self-healing.
/// </summary>
public sealed class DaemonUpdater : IDaemonUpdater
{
    private readonly IWslRunner _wsl;
    private readonly BuildProvenanceGate _provenance;

    /// <param name="provenance">The MG-9 build-provenance gate. Null → the compiled-in policy, which is
    /// a no-op on any build that is not a stamped attested release.</param>
    public DaemonUpdater(IWslRunner wsl, BuildProvenanceGate? provenance = null)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _provenance = provenance ?? new BuildProvenanceGate();
    }

    /// <summary>
    /// Where the attesting release build writes the payload's canonical sha256 manifest — a sibling of
    /// the payload directory (<c>…/payload/daemon</c> → <c>…/payload/daemon.manifest</c>), deliberately
    /// OUTSIDE the directory so it never has to hash itself.
    /// </summary>
    public static string AttestedManifestPathFor(string payloadDirectory) =>
        payloadDirectory.TrimEnd('/', '\\') + ".manifest";

    /// <summary>Where the packaged app ships the daemon payload (the MSBuild
    /// <c>$(MainguardDaemonPayload)</c> copy step in Mainguard.Pro.App.csproj) — mirrors how
    /// <c>payload/MainguardOS.tar.gz</c> is resolved.</summary>
    public static string DefaultPayloadDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "payload", "daemon");

    /// <summary>The in-VM (<c>/mnt/&lt;drive&gt;/…</c>) form of a Windows payload directory — the
    /// path <c>cp</c> reads inside MainguardEnv. Pure (reuses <see cref="HostPathTranslator"/> pinned
    /// to the Linux branch; native Linux paths — tests, CI — pass through unchanged).</summary>
    public static string ToVmPath(string hostPayloadDirectory) =>
        HostPathTranslator.ToDaemonOpenablePath(hostPayloadDirectory, daemonIsWindows: false);

    public async Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        var vmPayloadDir = ToVmPath(payloadDirectory);

        var retired = false;
        try
        {
            // MG-9: build the manifest BEFORE anything is stopped or moved. A payload that is missing
            // its daemon assembly, or that cannot even be enumerated, must fail while the running
            // daemon is still untouched — not halfway through a swap.
            DaemonPayloadManifest manifest;
            try
            {
                manifest = DaemonPayloadManifest.Build(payloadDirectory);
            }
            catch (DaemonPayloadException ex)
            {
                throw new DaemonRefreshStepException($"refusing to refresh: {ex.Message}");
            }

            // The signature seam for the highest-privilege promote in the app: these bytes become a
            // root-run service. It reports NotAvailable today (no signing identity) and we continue
            // with the gap named in the result message rather than implied away.
            var signature = PayloadSignature.VerifyFile(SignedArtifactKind.DaemonPayload, payloadDirectory);
            if (signature.MustRefuse)
            {
                throw new DaemonRefreshStepException(
                    $"refusing to promote the daemon payload: {signature.Reason}");
            }

            // MG-9, our-own-artifact half: the manifest above proves the COPY is faithful, and both its
            // sides come from the same directory, so it says nothing about whether that directory is
            // ours. Build provenance is what says so — the attestation originates from the CI run that
            // produced the payload, not from the same place the payload is being read from. Fail-closed
            // on a stamped release build; an explicit, logged no-op on a developer build.
            var provenance = await VerifyBuildProvenanceAsync(payloadDirectory, manifest, ct)
                .ConfigureAwait(false);
            if (provenance.MustRefuse)
            {
                throw new DaemonRefreshStepException(
                    $"refusing to promote the daemon payload: {provenance.Reason}");
            }

            await RequireAsync(DaemonUpdateCommands.StopUnit(), "stop the mainguardd unit", ct).ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.RemoveStaging(), "clear stale staging", ct).ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.CreateStaging(), "create the staging dir", ct).ConfigureAwait(false);
            await RequireAsync(
                DaemonUpdateCommands.CopyPayloadIntoStaging(vmPayloadDir),
                $"stage the payload from '{vmPayloadDir}'", ct).ConfigureAwait(false);

            // Verify the staged copy against that manifest while it is still only STAGING — the last
            // point at which walking away costs nothing. `cp -r` over a drvfs mount can exit 0 having
            // written a truncated file, and the old code promoted whatever landed with no check of any
            // kind: no hash, no manifest, no size. See VerifyStagedPayloadAsync for exactly what this
            // does and does not establish.
            await VerifyStagedPayloadAsync(manifest, ct).ConfigureAwait(false);

            // A raw publish ships `Mainguard.Server`; a build.sh payload arrives already renamed.
            var apphost = await _wsl.RunAsync(DaemonUpdateCommands.ProbePublishedApphost(), stdin: null, ct)
                .ConfigureAwait(false);
            if (apphost.Succeeded)
            {
                await RequireAsync(DaemonUpdateCommands.RenameApphost(), "rename the apphost to mainguardd", ct)
                    .ConfigureAwait(false);
            }

            await RequireAsync(DaemonUpdateCommands.MakeDaemonExecutable(), "chmod the mainguardd apphost", ct)
                .ConfigureAwait(false);

            await RequireAsync(DaemonUpdateCommands.RemoveRollback(), "drop the previous rollback", ct)
                .ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.RetireCurrent(), "retire the current install", ct)
                .ConfigureAwait(false);
            retired = true;
            await RequireAsync(DaemonUpdateCommands.PromoteStaging(), "promote the staged install", ct)
                .ConfigureAwait(false);
            retired = false; // promoted — /opt/mainguard exists again; never blind-restore over it
            await RequireAsync(DaemonUpdateCommands.StartUnit(), "start the mainguardd unit", ct)
                .ConfigureAwait(false);

            return new DaemonRefreshResult(
                true,
                $"daemon refreshed from '{payloadDirectory}' ({manifest.FileHashes.Count} files, "
                + $"{manifest.TotalBytes} bytes, all sha256-verified in the VM; rollback kept at "
                + $"{DaemonUpdateCommands.RollbackDir}). {signature.Reason} {provenance.Reason}");
        }
        catch (DaemonRefreshStepException ex)
        {
            // Never leave the VM without /opt/mainguard or with the unit stopped. The restore is
            // only issued when the promote failed after the retire (InstallDir is absent then —
            // a blind mv into an existing dir would nest the rollback inside it).
            if (retired)
            {
                await TryRunAsync(DaemonUpdateCommands.RestoreRollback(), ct).ConfigureAwait(false);
            }

            await TryRunAsync(DaemonUpdateCommands.StartUnit(), ct).ConfigureAwait(false);
            return new DaemonRefreshResult(false, ex.Message);
        }
    }

    /// <summary>
    /// The MG-9 build-provenance check for the daemon payload.
    ///
    /// <para>The payload is a DIRECTORY, and <c>gh attestation verify</c> attests files — so the
    /// attesting release build writes the payload's canonical sha256 manifest to
    /// <see cref="AttestedManifestPathFor"/> and attests that file. Here we (1) recompute the canonical
    /// text from the payload directory as it exists on this machine, (2) require the sidecar manifest to
    /// equal it byte for byte, and (3) require an attestation over the sidecar. Tampering with the
    /// payload breaks (2); tampering with both breaks (3); deleting the sidecar breaks (2) as well —
    /// there is no arrangement of deletions that turns the check into a skip, because the REQUIREMENT is
    /// compiled into the app rather than read from beside the artifact.</para>
    /// </summary>
    private async Task<BuildProvenanceVerdict> VerifyBuildProvenanceAsync(
        string payloadDirectory, DaemonPayloadManifest manifest, CancellationToken ct)
    {
        var attestedManifest = AttestedManifestPathFor(payloadDirectory);
        var expected = manifest.ToCanonicalText();

        string? onDisk = null;
        try
        {
            if (File.Exists(attestedManifest))
                onDisk = await File.ReadAllTextAsync(attestedManifest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onDisk = null;
        }

        if (onDisk is null || !string.Equals(onDisk.Replace("\r\n", "\n"), expected, StringComparison.Ordinal))
        {
            // Hand the gate a digest that cannot match anything, so a non-attested build still gets its
            // honest NotAttestedBuild verdict while an attested one refuses with the reason below.
            var verdict = await _provenance.VerifyDigestAsync(
                BuildArtifactKind.DaemonPayload, attestedManifest, string.Empty, ct).ConfigureAwait(false);
            if (verdict.Outcome == BuildProvenanceOutcome.NotAttestedBuild)
                return verdict;

            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                onDisk is null
                    ? $"the attested payload manifest '{attestedManifest}' is missing or unreadable, so "
                      + "the build-time attestation cannot be tied to the payload on disk. Refusing."
                    : $"the payload on disk does not reproduce the attested manifest "
                      + $"'{attestedManifest}' — the daemon build has been altered since it was attested. "
                      + "Refusing.");
        }

        return await _provenance.VerifyDigestAsync(
            BuildArtifactKind.DaemonPayload, attestedManifest, manifest.CanonicalDigest(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-hashes the STAGED copy inside the VM and requires it to match the host manifest file for
    /// file. Runs while the payload is still in <see cref="DaemonUpdateCommands.StagingDir"/>, before
    /// the current install is retired, so a bad copy is abandoned with the running daemon untouched.
    ///
    /// <para>This detects a corrupted, truncated, partial, or polluted copy. It does NOT authenticate
    /// the payload — see <see cref="DaemonPayloadManifest"/> for why the two are not the same thing.</para>
    /// </summary>
    private async Task VerifyStagedPayloadAsync(DaemonPayloadManifest manifest, CancellationToken ct)
    {
        var hashed = await _wsl.RunAsync(DaemonUpdateCommands.HashStagedPayload(), stdin: null, ct)
            .ConfigureAwait(false);
        if (!hashed.Succeeded)
        {
            // Unverifiable is not "fine". Continuing here would silently restore the old no-check
            // behaviour on exactly the machines where something is already wrong.
            var detail = string.IsNullOrWhiteSpace(hashed.StdErr) ? hashed.StdOut : hashed.StdErr;
            throw new DaemonRefreshStepException(
                $"could not hash the staged payload for verification (exit {hashed.ExitCode}): {detail.Trim()}");
        }

        var staged = DaemonPayloadManifest.ParseSha256Sums(hashed.StdOut, DaemonUpdateCommands.StagingDir);
        if (manifest.FindDiscrepancy(staged) is { } discrepancy)
        {
            throw new DaemonRefreshStepException(
                $"the staged daemon payload does not match what the app shipped — {discrepancy}; "
                + "refusing to promote it");
        }
    }

    private async Task RequireAsync(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new DaemonRefreshStepException(
                $"could not {what} (exit {result.ExitCode}): {detail.Trim()}");
        }
    }

    private async Task TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recovery is best-effort; the primary failure is what gets reported.
        }
    }

    /// <summary>Internal control-flow signal for a failed refresh step (never escapes
    /// <see cref="RefreshAsync"/> — the caller sees a typed <see cref="DaemonRefreshResult"/>).</summary>
    private sealed class DaemonRefreshStepException : Exception
    {
        public DaemonRefreshStepException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>How one <see cref="DaemonAutoRefresh.RunAsync"/> attempt ended — the typed form of the
/// oobe.log breadcrumb, for callers (the App's startup toast) that need more than prose.</summary>
public enum DaemonRefreshOutcomeKind
{
    /// <summary>The daemon never answered within the retry budget — skipped, not an error.</summary>
    Unreachable,

    /// <summary>The daemon already matches the app; nothing was touched.</summary>
    UpToDate,

    /// <summary>The app ships an OLDER daemon than the one deployed — the refresh was REFUSED (MG-15).
    /// Distinct from <see cref="UpToDate"/> on purpose: this is a state a user needs told about, since
    /// their app and their VM are out of step in the direction the updater will never fix by itself.</summary>
    RefusedDowngrade,

    /// <summary>The two versions could not be ordered, so the refresh could not be shown to move
    /// forward and was REFUSED rather than guessed at (MG-15).</summary>
    RefusedUncomparable,

    /// <summary>Skew was detected but the app ships no daemon payload — skipped.</summary>
    SkippedNoPayload,

    /// <summary>The in-place refresh ran and succeeded — the daemon now runs the app's version.</summary>
    Refreshed,

    /// <summary>The refresh ran and failed; the daemon was left on (or restored to) the previous build.</summary>
    RefreshFailed,

    /// <summary>An unexpected fault in the flow itself (never thrown at the caller).</summary>
    Faulted,
}

/// <summary>One typed refresh outcome (the callback payload of <see cref="DaemonAutoRefresh.RunAsync"/>).</summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="PreviousDaemonVersion">The daemon version found before any action — <c>null</c> when
/// unknown (unreachable, faulted, or a pre-<c>GetDaemonInfo</c> daemon that cannot name itself).</param>
/// <param name="NewDaemonVersion">The version the daemon runs after a successful refresh (the app's
/// version); <c>null</c> for every other kind.</param>
/// <param name="Detail">The same human-readable text the oobe.log breadcrumb carries.</param>
public sealed record DaemonRefreshOutcome(
    DaemonRefreshOutcomeKind Kind,
    string? PreviousDaemonVersion,
    string? NewDaemonVersion,
    string Detail);

/// <summary>A composed startup-toast payload (proto- and UI-free; the App binds it to its toast host).</summary>
/// <param name="Message">The one-line toast text (Voice Bible pattern T: past tense, names the object).</param>
/// <param name="IsWarning">True for the failed-refresh warning tone; false for the quiet success pill.</param>
public sealed record DaemonRefreshToastContent(string Message, bool IsWarning);

/// <summary>
/// The outcome → toast policy: only an attempt that actually CHANGED something (or tried and
/// failed) earns a toast. Up-to-date, unreachable, no-payload, and internal faults stay silent —
/// they were silent before the toast existed and a startup pill for "nothing happened" is noise.
/// Pure so the trigger rule is unit-testable without Avalonia.
/// </summary>
public static class DaemonRefreshToast
{
    public static DaemonRefreshToastContent? TryCompose(DaemonRefreshOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            DaemonRefreshOutcomeKind.Refreshed => new DaemonRefreshToastContent(
                $"Mainguard OS daemon updated to {outcome.NewDaemonVersion}.", IsWarning: false),
            DaemonRefreshOutcomeKind.RefreshFailed => new DaemonRefreshToastContent(
                "Daemon update didn't complete — still on "
                + $"{outcome.PreviousDaemonVersion ?? "the previous build"}. Details in oobe.log.",
                IsWarning: true),
            // A REFUSED refresh earns a toast for the same reason a failed one does: something the user
            // must act on happened, and it changed nothing. Staying quiet would make a downgrade attempt
            // — the one update attack this unsigned build can actually DETECT (MG-15) — invisible.
            DaemonRefreshOutcomeKind.RefusedDowngrade => new DaemonRefreshToastContent(
                "Daemon update refused — this Mainguard build is older than the daemon already installed"
                + $"{(outcome.PreviousDaemonVersion is { Length: > 0 } v ? $" ({v})" : "")}. "
                + "Nothing was changed. Details in oobe.log.",
                IsWarning: true),
            DaemonRefreshOutcomeKind.RefusedUncomparable => new DaemonRefreshToastContent(
                "Daemon update refused — the app and daemon versions couldn't be compared, so the update "
                + "couldn't be shown to move forward. Nothing was changed. Details in oobe.log.",
                IsWarning: true),
            _ => null,
        };
    }
}

/// <summary>
/// The one call the App makes at control-center startup (fire-and-forget): query the daemon's
/// version, decide skew, refresh if needed, log the outcome — and never throw. Daemon-down is a
/// silent skip (the reconnect machinery owns liveness); a query that answered <c>Unimplemented</c>
/// (mapped to <c>null</c> by the caller) IS the skew signal for pre-<c>GetDaemonInfo</c> daemons.
/// The query is retried briefly because the launch path wakes the VM in the background and systemd
/// needs a few seconds to bring <c>mainguardd</c> up.
/// </summary>
public static class DaemonAutoRefresh
{
    /// <param name="appVersion">The App's own informational version.</param>
    /// <param name="queryDaemonInfo">Calls <c>GetDaemonInfo</c>; returns <c>null</c> for an
    /// <c>Unimplemented</c> answer; THROWS when the daemon is unreachable.</param>
    /// <param name="updater">The refresh performer (fake in tests).</param>
    /// <param name="payloadDirectory">The app-shipped daemon payload dir (Windows host path).</param>
    /// <param name="log">Outcome breadcrumbs (the App passes its oobe.log writer).</param>
    /// <param name="queryAttempts">Bounded unreachable-retry budget (the VM may still be booting).</param>
    /// <param name="queryRetryDelay">Delay between unreachable retries (default 5 s; 0 in tests).</param>
    /// <param name="onOutcome">Optional typed-outcome callback (the App's startup toast). Invoked at
    /// most once, after the outcome is logged, on the caller's thread — never on cancellation, and a
    /// throwing callback is swallowed (this flow must never ripple into the app).</param>
    public static async Task RunAsync(
        string appVersion,
        Func<CancellationToken, Task<DaemonVersionInfo?>> queryDaemonInfo,
        IDaemonUpdater updater,
        string payloadDirectory,
        Action<string> log,
        CancellationToken ct,
        int queryAttempts = 5,
        TimeSpan? queryRetryDelay = null,
        Action<DaemonRefreshOutcome>? onOutcome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentNullException.ThrowIfNull(queryDaemonInfo);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        ArgumentNullException.ThrowIfNull(log);

        var delay = queryRetryDelay ?? TimeSpan.FromSeconds(5);

        // Log first, then report — and never let a throwing callback masquerade as a flow fault.
        void Report(DaemonRefreshOutcomeKind kind, string? previous, string? updatedTo, string detail)
        {
            try
            {
                onOutcome?.Invoke(new DaemonRefreshOutcome(kind, previous, updatedTo, detail));
            }
            catch (Exception)
            {
                // The outcome consumer is cosmetic (a toast); its failure never ripples back.
            }
        }

        try
        {
            DaemonVersionInfo? info = null;
            var reached = false;
            for (var attempt = 0; attempt < queryAttempts && !reached; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                try
                {
                    info = await queryDaemonInfo(ct).ConfigureAwait(false);
                    reached = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Daemon down / VM still booting — retry within the bounded budget.
                }
            }

            if (!reached)
            {
                const string skipped =
                    "daemon auto-update: daemon unreachable — skipped (the reconnect machinery owns liveness)";
                log(skipped);
                Report(DaemonRefreshOutcomeKind.Unreachable, previous: null, updatedTo: null, skipped);
                return;
            }

            var decision = DaemonUpdatePolicy.Decide(appVersion, info);
            if (!decision.ShouldRefresh)
            {
                // A REFUSAL is not "up to date" — it is a real, actionable disagreement between the app
                // and the VM (MG-15), so it gets its own outcome kind and the policy's own sentence.
                // Silently reporting UpToDate here is exactly how a downgrade attempt would stay invisible.
                var kind = decision.Kind switch
                {
                    DaemonRefreshDecisionKind.RefusedDowngrade => DaemonRefreshOutcomeKind.RefusedDowngrade,
                    DaemonRefreshDecisionKind.RefusedUncomparable => DaemonRefreshOutcomeKind.RefusedUncomparable,
                    _ => DaemonRefreshOutcomeKind.UpToDate,
                };
                var detail = $"daemon auto-update: {decision.Reason}"
                    + (info!.PayloadVersion.Length > 0 ? $" (payload {info.PayloadVersion})" : "");
                log(detail);
                Report(kind, info.DaemonVersion, updatedTo: null, detail);
                return;
            }

            // null previous == the daemon could not name itself (pre-GetDaemonInfo).
            var previousVersion = info?.DaemonVersion is { Length: > 0 } v ? v : null;
            var daemonName = previousVersion ?? "pre-GetDaemonInfo";
            if (!Directory.Exists(payloadDirectory)
                || !Directory.EnumerateFileSystemEntries(payloadDirectory).Any())
            {
                var noPayload = $"daemon auto-update: skew detected (daemon {daemonName}, app {appVersion}) but no "
                    + $"daemon payload at '{payloadDirectory}' — skipped";
                log(noPayload);
                Report(DaemonRefreshOutcomeKind.SkippedNoPayload, previousVersion, updatedTo: null, noPayload);
                return;
            }

            log($"daemon auto-update: refreshing skewed daemon ({daemonName} → {appVersion})");
            var result = await updater.RefreshAsync(payloadDirectory, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                log($"daemon auto-update: {result.Message}");
                Report(DaemonRefreshOutcomeKind.Refreshed, previousVersion,
                    DaemonUpdatePolicy.StripBuildMetadata(appVersion), result.Message);
            }
            else
            {
                log($"daemon auto-update FAILED (daemon left on the previous build): {result.Message}");
                Report(DaemonRefreshOutcomeKind.RefreshFailed, previousVersion, updatedTo: null, result.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown mid-refresh-decision — nothing to log (and no outcome: nobody is listening).
        }
        catch (Exception ex)
        {
            // A failed update must never crash (or even ripple into) the app.
            log($"daemon auto-update FAILED: {ex.Message}");
            Report(DaemonRefreshOutcomeKind.Faulted, previous: null, updatedTo: null, ex.Message);
        }
    }
}
