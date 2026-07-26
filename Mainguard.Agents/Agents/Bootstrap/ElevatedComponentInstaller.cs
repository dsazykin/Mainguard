using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>What a promotion attempt decided. Mirrors <see cref="DaemonRefreshDecisionKind"/> on
/// purpose — the two "should these bytes replace those bytes?" decisions in this app should not have
/// two different vocabularies.</summary>
public enum ElevatedComponentPromotionKind
{
    /// <summary>The staged build moves the protected copy forward — promote it.</summary>
    Promote,

    /// <summary>The protected copy already IS the staged build. Nothing to do.</summary>
    AlreadyCurrent,

    /// <summary>The stage carries an OLDER build than the protected copy. Refused: promoting it would
    /// silently downgrade the one binary on the machine that runs with administrator rights, undoing
    /// every fix the newer build carried. Downgrade is the cheapest attack on an updater, and it is the
    /// one this path can close without a trust anchor.</summary>
    RefusedDowngrade,

    /// <summary>One of the two versions could not be ordered, so the promote could not be SHOWN to move
    /// forward. Refused rather than guessed — a version string crafted to be unparseable would
    /// otherwise fall through to whatever the fallback was.</summary>
    RefusedUncomparable,

    /// <summary>This build ships no elevated-components stage (a source/dev build). Not an error: the
    /// launcher falls back to the co-located helper and says so.</summary>
    NoStage,
}

/// <summary>One promotion decision plus the sentence explaining it (for oobe.log).</summary>
public sealed record ElevatedComponentPromotion(ElevatedComponentPromotionKind Kind, string Reason)
{
    /// <summary>The single boolean the promote gates on. Both refusals AND "already current" answer
    /// false; a caller that needs to tell them apart reads <see cref="Kind"/>.</summary>
    public bool ShouldPromote => Kind == ElevatedComponentPromotionKind.Promote;
}

/// <summary>
/// What is (or is about to be) installed in the protected root: a version to order by, and a content
/// fingerprint to break ties with.
///
/// <para>The fingerprint exists for the same reason <see cref="DaemonUpdatePolicy"/> keeps its
/// commit-hash rule: the elevated helper's assembly version does not move on every change, so a
/// version-only comparison would report "already current" forever and a rebuilt helper would never
/// reach the protected root. Note what the fingerprint is NOT — it is a content hash of the STAGE,
/// derived from the same bytes we are about to copy, so it proves "different bytes", never "our bytes".
/// Provenance is <see cref="IPayloadSignatureVerifier"/>'s job.</para>
/// </summary>
public sealed record ElevatedComponentIdentity(string Version, string Fingerprint)
{
    /// <summary>
    /// The per-user app directory the components were promoted FROM, recorded at promote time.
    ///
    /// <para><b>Why it is written down rather than passed in.</b> Once the helper runs from the
    /// protected root it no longer sits beside the app, so it can no longer derive the install root it
    /// must validate a <c>--resume-target</c> against — and accepting that root as an argument would
    /// hand the caller the ability to name <c>C:\Windows\System32</c> as "the install" and get
    /// <c>cmd.exe</c> registered as an elevated ONLOGON task. Recording it here instead means the root
    /// comes from a file only an administrator can write, which is the same trust level as the helper
    /// itself. Null on a marker written before this field existed; callers fall back to their own
    /// directory.</para>
    /// </summary>
    public string? AppRoot { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a marker back. Null for absent, empty, malformed, or partially-written JSON —
    /// every one of which means "we do not know what is installed", which the policy treats as
    /// "nothing is installed" and therefore promotes. That is the safe direction: a lost marker
    /// re-installs the current build, it never keeps a stale one.</summary>
    public static ElevatedComponentIdentity? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ElevatedComponentIdentity>(json, Options);
            return string.IsNullOrWhiteSpace(parsed?.Version) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The monotonic promote decision. Pure — no filesystem, no environment.</summary>
public static class ElevatedComponentPolicy
{
    /// <summary>
    /// Decides whether <paramref name="staged"/> should replace <paramref name="installed"/>.
    /// <paramref name="installed"/> is null when the protected root holds nothing (or an unreadable
    /// marker); <paramref name="staged"/> is null when this build ships no stage.
    /// </summary>
    public static ElevatedComponentPromotion Decide(
        ElevatedComponentIdentity? staged, ElevatedComponentIdentity? installed)
    {
        if (staged is null)
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.NoStage,
                "this build ships no elevated-components stage, so there is nothing to install into the "
                + "protected root (a source build; the packaged build stages one at publish)");
        }

        if (installed is null)
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.Promote,
                $"no elevated components are installed yet — installing {staged.Version} "
                + $"({Short(staged.Fingerprint)})");
        }

        var order = UpdateVersion.TryCompare(staged.Version, installed.Version);
        if (order is null)
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.RefusedUncomparable,
                $"refusing to install: staged version '{staged.Version}' and installed version "
                + $"'{installed.Version}' cannot be ordered, so the install could not be shown to move "
                + "forward");
        }

        if (order < 0)
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.RefusedDowngrade,
                $"refusing to install: this build stages elevated components {staged.Version} but "
                + $"{installed.Version} is already installed — promoting it would DOWNGRADE the binary "
                + "that runs with administrator rights. Update Mainguard instead.");
        }

        if (order > 0)
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.Promote,
                $"staged elevated components {staged.Version} replace the installed {installed.Version}");
        }

        if (!string.Equals(staged.Fingerprint, installed.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return new ElevatedComponentPromotion(
                ElevatedComponentPromotionKind.Promote,
                $"same version ({staged.Version}) built from different bytes "
                + $"({Short(installed.Fingerprint)} installed, {Short(staged.Fingerprint)} staged) — reinstalling");
        }

        return new ElevatedComponentPromotion(
            ElevatedComponentPromotionKind.AlreadyCurrent,
            $"elevated components {installed.Version} ({Short(installed.Fingerprint)}) are already installed");
    }

    private static string Short(string fingerprint)
        => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}

/// <summary>
/// The concrete directories one install/uninstall of the elevated components touches. Built from
/// <see cref="ElevatedComponentLayout"/>, never from an argument that crossed the elevation boundary —
/// see <see cref="TryForHost"/>.
///
/// <para><see cref="InstallFootprint"/> and <see cref="RemovalTargets"/> exist as separate lists so the
/// property that matters can be a test rather than a promise: <b>everything the install creates is
/// removed by the uninstall</b>. A relocation that leaks files into Program Files on uninstall is a
/// bug, and the way that bug happens is someone adding a directory to one list and not the other.</para>
/// </summary>
public sealed record ElevatedComponentPlan(
    string ProtectedRoot, string ElevatedDir, string StageDir, string MarkerPath)
{
    /// <summary>Every path the install creates, outermost first.</summary>
    public IReadOnlyList<string> InstallFootprint => new[] { ProtectedRoot, ElevatedDir, MarkerPath };

    /// <summary>Every path the uninstall removes. The product root, recursively — deliberately the
    /// OUTERMOST directory we create, so a file added under it later is covered without anyone
    /// remembering to add it here.</summary>
    public IReadOnlyList<string> RemovalTargets => new[] { ProtectedRoot };

    /// <summary>The plan for an explicit install base — the form tests use (a temp directory), and the
    /// form <see cref="TryForHost"/> delegates to once it has proved the base is protected.</summary>
    public static ElevatedComponentPlan For(string installBase, string appBaseDirectory) => new(
        ElevatedComponentLayout.ProtectedRoot(installBase),
        ElevatedComponentLayout.ElevatedDir(installBase),
        ElevatedComponentLayout.StageDir(appBaseDirectory),
        ElevatedComponentLayout.MarkerPath(installBase));

    /// <summary>
    /// The real machine's plan. Refuses — rather than falling back to somewhere writable — when the
    /// host exposes no administrator-owned root, because "install the elevated binary into a user
    /// directory" is the exact bug this whole file exists to remove.
    /// </summary>
    public static bool TryForHost(
        ProtectedLocationPolicy protection,
        string appBaseDirectory,
        out ElevatedComponentPlan plan,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(protection);
        plan = null!;

        var installBase = protection.PreferredInstallRoot;
        if (installBase is null)
        {
            refusal = "this machine exposes no administrator-owned install root, so Mainguard's elevated "
                + "components cannot be placed out of reach of the user account they defend against";
            return false;
        }

        var candidate = For(installBase, appBaseDirectory);
        if (!protection.TryRequireProtected(candidate.ElevatedDir, "elevated components directory", out refusal))
            return false;

        plan = candidate;
        refusal = string.Empty;
        return true;
    }
}

/// <summary>The outcome of one install attempt — never a bare throw at the caller (this runs inside the
/// elevated helper, whose only channel back is an exit code and a JSON file).</summary>
public sealed record ElevatedComponentInstallResult(
    bool Installed, ElevatedComponentPromotionKind Kind, string Message);

/// <summary>The outcome of one removal. <paramref name="Leftovers"/> is what survived — an uninstall
/// that cannot remove the protected root must SAY so, not return quietly.</summary>
public sealed record ElevatedComponentRemovalResult(
    bool Clean, IReadOnlyList<string> Leftovers, string Message);

/// <summary>
/// <b>Moves the elevated components out of the user's reach, and puts them back.</b>
///
/// <para>Plain <see cref="System.IO"/> throughout — no Windows API, no schtasks, no PowerShell — which
/// is deliberate: the copy/replace/marker sequence is then exercisable on the Linux CI leg against a
/// temp directory, and only "Program Files is really administrator-owned" is left to the manual Windows
/// matrix. The privileged part of this operation is not the file copy; it is the DESTINATION, and the
/// destination is decided by <see cref="ElevatedComponentPlan.TryForHost"/> from the machine's own
/// special folders, never from anything that crossed the elevation boundary.</para>
///
/// <para><b>The source is not a parameter either.</b> The elevated helper promotes its OWN directory.
/// An <c>--install-from &lt;dir&gt;</c> switch would have handed any caller that can reach the helper an
/// arbitrary-write-into-Program-Files primitive as administrator — trading MG-15 for a worse one.</para>
/// </summary>
public static class ElevatedComponentInstaller
{
    /// <summary>
    /// Installs <paramref name="plan"/>'s stage into its protected directory when the promotion policy
    /// says it moves forward. <paramref name="stagedVersion"/> is the staged helper's own informational
    /// version. Never throws: an IO or permission failure becomes a failed result with the reason.
    /// </summary>
    public static ElevatedComponentInstallResult Install(
        ElevatedComponentPlan plan, string stagedVersion, string? appRoot = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedVersion);

        try
        {
            var staged = TryDescribeStage(plan.StageDir, stagedVersion) is { } s
                ? s with { AppRoot = appRoot }
                : null;
            var installed = ElevatedComponentIdentity.TryParse(TryReadMarker(plan.MarkerPath));
            var decision = ElevatedComponentPolicy.Decide(staged, installed);
            log?.Invoke($"elevated components: {decision.Reason}");

            if (!decision.ShouldPromote)
                return new ElevatedComponentInstallResult(false, decision.Kind, decision.Reason);

            // Replace, never merge. A merge would leave a retired file from an older build sitting in
            // the protected directory — still administrator-owned, still launchable, and now unowned by
            // any version we track.
            ReplaceDirectory(plan.StageDir, plan.ElevatedDir);
            File.WriteAllText(plan.MarkerPath, staged!.Serialize());

            var message = $"installed elevated components {staged.Version} into '{plan.ElevatedDir}' — "
                + "they now live where this user account cannot rewrite them";
            log?.Invoke($"elevated components: {message}");
            return new ElevatedComponentInstallResult(true, ElevatedComponentPromotionKind.Promote, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var message = $"could not install the elevated components into '{plan.ElevatedDir}': {ex.Message}";
            log?.Invoke($"elevated components: {message}");
            return new ElevatedComponentInstallResult(false, ElevatedComponentPromotionKind.NoStage, message);
        }
    }

    /// <summary>
    /// Removes everything <see cref="ElevatedComponentPlan.RemovalTargets"/> names. Best-effort by
    /// design — an uninstall must always finish — but an unremoved path is REPORTED, because a
    /// relocation that silently leaks an administrator-owned directory is worse than no relocation:
    /// the user believes Mainguard is gone.
    /// </summary>
    public static ElevatedComponentRemovalResult Remove(
        ElevatedComponentPlan plan, Action<string>? log = null, Action<string>? remove = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var delete = remove ?? DeleteRecursive;
        var leftovers = new List<string>();
        var failures = new List<string>();

        foreach (var target in plan.RemovalTargets)
        {
            try
            {
                delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"'{target}': {ex.Message}");
            }

            if (Directory.Exists(target) || File.Exists(target))
                leftovers.Add(target);
        }

        if (leftovers.Count == 0)
        {
            const string clean = "removed Mainguard's elevated components from the protected install root";
            log?.Invoke($"elevated components: {clean}");
            return new ElevatedComponentRemovalResult(true, Array.Empty<string>(), clean);
        }

        var message = "Mainguard's elevated components could not be removed and are still installed at "
            + $"{string.Join(", ", leftovers)}"
            + (failures.Count > 0 ? $" ({string.Join("; ", failures)})" : string.Empty)
            + ". Removing them needs administrator rights — run the uninstaller as an administrator, or "
            + "delete that folder by hand.";
        log?.Invoke($"elevated components: {message}");
        return new ElevatedComponentRemovalResult(false, leftovers, message);
    }

    /// <summary>Describes the stage, or null when this build ships none (a source build) or it is
    /// empty. An empty stage is treated as no stage: promoting it would replace a working protected
    /// helper with nothing.</summary>
    public static ElevatedComponentIdentity? TryDescribeStage(string stageDir, string stagedVersion)
    {
        if (string.IsNullOrWhiteSpace(stageDir) || !Directory.Exists(stageDir))
            return null;

        var fingerprint = Fingerprint(stageDir);
        return fingerprint is null ? null : new ElevatedComponentIdentity(stagedVersion, fingerprint);
    }

    /// <summary>Reads the protected root's marker, or null when it is absent/unreadable.</summary>
    public static string? TryReadMarker(string markerPath)
    {
        try
        {
            return File.Exists(markerPath) ? File.ReadAllText(markerPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A content fingerprint of a directory: SHA-256 over every regular file's relative path AND
    /// content hash, in a fixed order. Null when the directory has no files.
    ///
    /// <para>Path-and-content, not content alone: two builds that differ only in which file a given
    /// blob lives under are different builds, and a fingerprint that could not tell them apart would
    /// let a rename slip past the "already current" check.</para>
    /// </summary>
    public static string? Fingerprint(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        var entries = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            using var stream = File.OpenRead(file);
            entries.Add(relative + " " + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        if (entries.Count == 0)
            return null;

        entries.Sort(StringComparer.Ordinal);
        var joined = string.Join("\n", entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    /// <summary>The real removal. Injectable in <see cref="Remove"/> only so the "could not remove it"
    /// REPORT — the branch that stops a leak from being silent — is testable without needing an OS that
    /// will actually deny the delete.</summary>
    private static void DeleteRecursive(string target)
    {
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        else if (File.Exists(target))
            File.Delete(target);
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
