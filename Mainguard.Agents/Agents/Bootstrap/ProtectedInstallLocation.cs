using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// <b>Which directories an unprivileged user cannot write — the rule MG-15 turns on.</b>
///
/// <para><b>The finding.</b> Mainguard installs per-user (Velopack, <c>%LocalAppData%\Mainguard</c>), so
/// <see cref="AppContext.BaseDirectory"/> — where the elevated helper and the daemon payload are
/// resolved from — is writable by <b>the same unprivileged user</b> the elevated resume Scheduled Task
/// runs as, and that task is registered <c>/RL HIGHEST /SC ONLOGON</c>, which fires <b>with no UAC
/// prompt</b>. A same-user process could therefore overwrite the exact binary Windows was about to
/// auto-launch with full privileges. That is a complete, silent local privilege escalation from an
/// ordinary file write.</para>
///
/// <para><b>Why the previous two mitigations cannot close it.</b>
/// <see cref="TrustedExecutablePath"/> (MG-9) bounds WHERE an elevated target comes from — it stops an
/// <i>arbitrary</i> exe at an <i>unexpected</i> path, and cannot stop a <i>replaced</i> exe at the
/// <i>legitimate</i> path. ACL hardening on the install directory does nothing either: same-user
/// malware carries the same token, so any ACL that lets the app update itself lets the attacker rewrite
/// it. The only fix is to <b>remove the write primitive</b> — put anything that will run elevated where
/// a non-admin cannot write it, and never point a no-prompt elevated facility at anywhere else.</para>
///
/// <para><b>Deliberately syntactic.</b> This asks "is this path under a location the OS reserves for
/// administrators?", never "can I write here right now?". A probe (try to create a file) answers the
/// wrong question — it answers it for the process that happens to be running, which during setup is the
/// ELEVATED helper, for whom everything is writable. It is also platform-independent, so the Windows
/// rules are exercised on the Linux CI leg (the same discipline
/// <see cref="TrustedExecutablePath"/> adopted, and the only way these rules get tested at all).</para>
///
/// <para><b>Allow-list first, deny-list wins.</b> A path is protected only when it sits under a root
/// this policy was told is administrator-owned (Program Files, the Windows directory) — and it is
/// rejected regardless if it also sits under a user-owned root (the profile, LocalAppData, temp).
/// A deny-list alone would be a hole (an unlisted user-writable location would read as protected); the
/// deny pass on top catches a redirected or nested allow-root.</para>
/// </summary>
public sealed class ProtectedLocationPolicy
{
    private static readonly Lazy<ProtectedLocationPolicy> HostPolicy = new(BuildForHost);

    private ProtectedLocationPolicy(IReadOnlyList<string> protectedRoots, IReadOnlyList<string> userWritableRoots)
    {
        ProtectedRoots = protectedRoots;
        UserWritableRoots = userWritableRoots;
    }

    /// <summary>Administrator-owned roots, canonicalised. Empty means "nothing on this machine is known
    /// to be protected", and every <see cref="IsProtected"/> answer is then false — fail-closed.</summary>
    public IReadOnlyList<string> ProtectedRoots { get; }

    /// <summary>User-owned roots. Anything at or under one of these is never protected, even when it is
    /// also under a <see cref="ProtectedRoots"/> entry.</summary>
    public IReadOnlyList<string> UserWritableRoots { get; }

    /// <summary>The root the elevated components are installed under — the first protected root that
    /// survives the deny pass. Null when this machine exposes none, which callers must treat as
    /// "relocation is not possible here", never as "install into the user directory anyway".</summary>
    public string? PreferredInstallRoot =>
        ProtectedRoots.FirstOrDefault(r => !IsUnderAny(r, UserWritableRoots));

    /// <summary>Builds a policy from explicit roots. This is the constructor tests use, and it is why
    /// the Windows rules are checkable on Linux: the roots are data, not an environment lookup.</summary>
    public static ProtectedLocationPolicy Create(
        IEnumerable<string>? protectedRoots, IEnumerable<string>? userWritableRoots)
        => new(Canonicalize(protectedRoots), Canonicalize(userWritableRoots));

    /// <summary>The real machine's policy (cached — the roots do not change while the process runs).</summary>
    public static ProtectedLocationPolicy ForHost() => HostPolicy.Value;

    /// <summary>
    /// True when <paramref name="path"/> is at or under a protected root and under no user-writable
    /// root. A path that cannot be canonicalised is NOT protected: an answer we could not compute must
    /// never be the permissive one.
    /// </summary>
    public bool IsProtected(string? path)
    {
        if (!TrustedExecutablePath.TryCanonicalizeDirectory(path, out var canonical, out _))
            return false;

        // Deny wins, and it is checked FIRST so a reader cannot mistake the order.
        if (IsUnderAny(canonical, UserWritableRoots))
            return false;

        return IsUnderAny(canonical, ProtectedRoots);
    }

    /// <summary>The refusing form: <c>false</c> plus a sentence naming what was wrong, for the log and
    /// for the user-facing message. <paramref name="what"/> names the artifact ("elevated helper").</summary>
    public bool TryRequireProtected(string? path, string what, out string refusal)
    {
        if (!TrustedExecutablePath.TryCanonicalizeDirectory(path, out var canonical, out var pathRefusal))
        {
            refusal = $"the {what} path '{path}' is not a usable absolute path: {pathRefusal}";
            return false;
        }

        if (IsUnderAny(canonical, UserWritableRoots))
        {
            refusal = $"'{canonical}' is inside a directory this user can write, so anything placed "
                + $"there can be replaced by any process running as this user — a {what} must live "
                + "somewhere only an administrator can write";
            return false;
        }

        if (!IsUnderAny(canonical, ProtectedRoots))
        {
            refusal = ProtectedRoots.Count == 0
                ? $"this machine exposes no administrator-owned install root, so a {what} cannot be "
                    + "placed out of reach of the user it defends against"
                : $"'{canonical}' is not under any administrator-owned root "
                    + $"({string.Join(", ", ProtectedRoots)})";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    private static bool IsUnderAny(string canonical, IReadOnlyList<string> roots)
        => roots.Any(r => TrustedExecutablePath.IsWithinOrEqual(canonical, r));

    private static IReadOnlyList<string> Canonicalize(IEnumerable<string>? roots)
    {
        var result = new List<string>();
        foreach (var raw in roots ?? Enumerable.Empty<string>())
        {
            if (TrustedExecutablePath.TryCanonicalizeDirectory(raw, out var canonical, out _)
                && !result.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(canonical);
            }
        }

        return result;
    }

    /// <summary>
    /// The host's roots. Resolved through <see cref="Mainguard.Git.MainguardPaths"/>, which is this
    /// codebase's ONE sanctioned caller of <c>Environment.GetFolderPath</c> — the default option
    /// verifies the directory exists and returns <c>""</c> when it does not, which here would silently
    /// drop a Program Files entry off the allow-list and turn "protected" into "nothing is protected"
    /// on exactly the machine where that matters. (A structural test enforces the rule.)
    /// </summary>
    private static ProtectedLocationPolicy BuildForHost()
        => Create(Mainguard.Git.MainguardPaths.ProtectedInstallRoots(),
            Mainguard.Git.MainguardPaths.UserWritableRoots());
}

/// <summary>
/// <b>Where the relocated elevated components live, and where they are staged from.</b>
///
/// <para>Scope is the plan of record's §1 decision (<c>docs/design/code-signing-plan.md</c>): relocate
/// <b>only the elevated pieces</b>, not the whole app. A full Program Files migration is defence in
/// depth with a real UX cost — every self-update would need elevation or a privileged updater service —
/// while relocating what actually runs elevated closes the escalation and leaves per-user self-update
/// intact.</para>
///
/// <para>Two directories, and the distinction is the whole design:</para>
/// <list type="bullet">
///   <item><b>The stage</b> (<c>&lt;app&gt;\elevated-stage\</c>) — shipped inside the per-user Velopack
///   install, so it updates with the app and needs no elevation. It is USER-WRITABLE and is therefore
///   never the thing that gets launched by a no-prompt elevated facility.</item>
///   <item><b>The protected root</b> (<c>%ProgramFiles%\Mainguard\elevated\</c>) — written once, by the
///   already-elevated helper, at the single UAC prompt the OOBE already raises. This is what the
///   launcher prefers and what the elevated Scheduled Task is allowed to point at.</item>
/// </list>
///
/// <para>Every path here is built with <see cref="Join"/> rather than <see cref="Path.Combine"/>,
/// because <c>Path</c> is host-relative: on the Linux CI leg it does not treat <c>\</c> as a separator
/// and would happily produce <c>C:\Program Files\Mainguard/elevated</c>, which then fails every
/// containment check the Windows rules run. Style is taken from the root.</para>
/// </summary>
public static class ElevatedComponentLayout
{
    /// <summary>The product directory created under the protected root.</summary>
    public const string ProductFolderName = "Mainguard";

    /// <summary>The subdirectory holding everything that runs with administrator rights.</summary>
    public const string ElevatedFolderName = "elevated";

    /// <summary>The per-user staging directory the packaged app ships (see the Pro head's
    /// <c>StageElevatedComponentsToPublish</c> target).</summary>
    public const string StageFolderName = "elevated-stage";

    /// <summary>The elevated helper's file name — the one binary that crosses the UAC boundary.</summary>
    public const string HelperFileName = "Mainguard.Installer.Elevated.exe";

    /// <summary>Records what was promoted (version + content fingerprint) so a later promote can be
    /// ordered instead of guessed. Lives INSIDE the protected root: an attacker who could rewrite the
    /// marker could otherwise pin the protected copy at an old build forever.</summary>
    public const string MarkerFileName = "elevated-components.json";

    /// <summary><c>&lt;installBase&gt;\Mainguard</c> — the whole footprint the uninstall removes.</summary>
    public static string ProtectedRoot(string installBase) => Join(installBase, ProductFolderName);

    /// <summary><c>&lt;installBase&gt;\Mainguard\elevated</c>.</summary>
    public static string ElevatedDir(string installBase) => Join(installBase, ProductFolderName, ElevatedFolderName);

    /// <summary>The protected copy of the elevated helper.</summary>
    public static string HelperPath(string installBase)
        => Join(installBase, ProductFolderName, ElevatedFolderName, HelperFileName);

    /// <summary>The promotion marker inside the protected root.</summary>
    public static string MarkerPath(string installBase)
        => Join(installBase, ProductFolderName, ElevatedFolderName, MarkerFileName);

    /// <summary>The per-user staging directory inside the running install.</summary>
    public static string StageDir(string appBaseDirectory) => Join(appBaseDirectory, StageFolderName);

    /// <summary>The staged helper — what runs at the FIRST elevation, before a protected copy exists.</summary>
    public static string StagedHelperPath(string appBaseDirectory)
        => Join(appBaseDirectory, StageFolderName, HelperFileName);

    /// <summary>Joins path segments using the separator the ROOT is written in, never the host's.
    /// See the type remarks for why <see cref="Path.Combine"/> is wrong here.</summary>
    public static string Join(string root, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var separator = SeparatorOf(root);
        var trimmed = root.TrimEnd('\\', '/');

        // A bare drive ("C:") trims to nothing meaningful — put its separator back so the result is
        // rooted rather than relative.
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            trimmed += separator;

        var text = trimmed;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;
            if (!text.EndsWith(separator))
                text += separator;
            text += segment.Trim('\\', '/');
        }

        return text;
    }

    private static char SeparatorOf(string root)
    {
        if (root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':')
            return '\\';
        return root.Contains('\\', StringComparison.Ordinal) && !root.Contains('/', StringComparison.Ordinal)
            ? '\\'
            : '/';
    }
}

/// <summary>The Scheduled Task privilege levels <c>schtasks /RL</c> accepts. Two values, because the
/// choice between them is the entire MG-15 fix.</summary>
public enum ScheduledTaskRunLevel
{
    /// <summary>Runs with the logon token, unelevated. A replaced binary at this path gains nothing —
    /// it already runs as the user who replaced it.</summary>
    Limited,

    /// <summary>Runs elevated with <b>no UAC prompt</b>, at every logon, for the lifetime of the
    /// registration. Only ever permitted for a target the user cannot write.</summary>
    Highest,
}

/// <summary>
/// <b>The MG-15 rule, in one function.</b>
///
/// <para>An ONLOGON <c>/RL HIGHEST</c> task is a standing, silent auto-elevation: Windows launches its
/// target with administrator rights at every logon and never asks the user anything. Registering one
/// against a binary in a user-writable directory hands every same-user process a one-write path to
/// SYSTEM-adjacent privilege. So the run level is not a caller's choice — it is <b>derived from where
/// the target lives</b>:</para>
///
/// <list type="bullet">
///   <item>Target under an administrator-owned root → <see cref="ScheduledTaskRunLevel.Highest"/> is
///   safe, because the bytes cannot be swapped without already having administrator rights.</item>
///   <item>Target anywhere else → <see cref="ScheduledTaskRunLevel.Limited"/>, always.</item>
/// </list>
///
/// <para><b>What Limited costs, honestly.</b> Mainguard's app head stays per-user (plan §1), so the
/// shipped registration is Limited: after the reboot the wizard resumes <i>unelevated</i>. That is
/// sufficient, because the post-reboot work is <c>wsl --import</c> and friends, none of which need
/// administrator rights — the features were already enabled before the reboot, and DISM completes them
/// during it. If a resumed run ever does need elevation again it takes the same single UAC prompt the
/// first pass took. The old <c>HIGHEST</c> bought exactly one thing: the escalation.</para>
///
/// <para>The rule is written to survive a later decision to move the app itself into Program Files —
/// the moment the target becomes protected, this returns Highest with no code change.</para>
/// </summary>
public static class ResumeTaskPolicy
{
    /// <summary>The <c>schtasks /RL</c> token for an elevated, no-prompt task.</summary>
    public const string SchtasksHighest = "HIGHEST";

    /// <summary>The <c>schtasks /RL</c> token for an unelevated task.</summary>
    public const string SchtasksLimited = "LIMITED";

    /// <summary>Derives the run level from the target's location. Never call this with a policy you
    /// built from the target itself — the roots must come from the machine (or, in tests, from data).</summary>
    public static ScheduledTaskRunLevel RunLevelFor(string? targetExePath, ProtectedLocationPolicy protection)
    {
        ArgumentNullException.ThrowIfNull(protection);

        // The DIRECTORY is what matters: a file is only as replaceable as the directory holding it.
        var directory = TrustedExecutablePath.DirectoryOf(targetExePath);
        return protection.IsProtected(directory) ? ScheduledTaskRunLevel.Highest : ScheduledTaskRunLevel.Limited;
    }

    /// <summary>The literal <c>schtasks /RL</c> argument for a run level.</summary>
    public static string SchtasksValue(ScheduledTaskRunLevel level)
        => level == ScheduledTaskRunLevel.Highest ? SchtasksHighest : SchtasksLimited;

    /// <summary>The sentence the OOBE log carries, so a machine that resumed unelevated says WHY.</summary>
    public static string Explain(string? targetExePath, ScheduledTaskRunLevel level)
        => level == ScheduledTaskRunLevel.Highest
            ? $"resume task will run elevated: '{targetExePath}' is in an administrator-owned directory, "
                + "so its bytes cannot be replaced without administrator rights"
            : $"resume task will run UNELEVATED: '{targetExePath}' is in a directory this user can write, "
                + "and a no-prompt elevated task must never point at a binary the user can replace (MG-15)";
}

/// <summary>Which helper the elevation launcher picked, and whether that copy is out of the user's
/// reach. <paramref name="InstallRoot"/> is the directory <see cref="TrustedExecutablePath"/> must
/// contain it in.</summary>
public sealed record ElevatedHelperChoice(
    string HelperPath, string InstallRoot, bool RunsFromProtectedLocation, string Reason);

/// <summary>
/// Picks which copy of the elevated helper to launch. Pure, so the preference order is a unit test
/// rather than a comment.
///
/// <para><b>Order, and why.</b> The protected copy wins whenever it exists and is current, because it
/// is the only one same-user malware cannot rewrite. It loses to the stage only when the stage is
/// NEWER — which is exactly the update case, and is safe because that launch is the UAC-prompted one
/// the user explicitly consents to, and its first act is to promote itself so the next launch is
/// protected again. A source/dev build has no stage and no protected copy at all; it falls back to the
/// helper sitting beside the app, exactly as before this change, with the degradation logged rather
/// than implied.</para>
/// </summary>
public static class ElevatedHelperResolution
{
    public static ElevatedHelperChoice Choose(
        string? protectedHelperPath,
        bool protectedHelperExists,
        bool protectedHelperIsCurrent,
        string? protectedElevatedDir,
        bool stagedHelperExists,
        string? stagedHelperPath,
        string? stageDir,
        string fallbackHelperPath,
        string fallbackInstallRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackHelperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackInstallRoot);

        if (protectedHelperExists
            && protectedHelperIsCurrent
            && !string.IsNullOrWhiteSpace(protectedHelperPath)
            && !string.IsNullOrWhiteSpace(protectedElevatedDir))
        {
            return new ElevatedHelperChoice(
                protectedHelperPath!, protectedElevatedDir!, RunsFromProtectedLocation: true,
                $"using the protected helper at '{protectedHelperPath}' (administrator-owned, so it "
                + "cannot be replaced by a process running as this user)");
        }

        if (stagedHelperExists && !string.IsNullOrWhiteSpace(stagedHelperPath) && !string.IsNullOrWhiteSpace(stageDir))
        {
            return new ElevatedHelperChoice(
                stagedHelperPath!, stageDir!, RunsFromProtectedLocation: false,
                protectedHelperExists
                    ? $"using the staged helper at '{stagedHelperPath}': it is newer than the protected "
                        + "copy, and this UAC-prompted launch promotes it into the protected root"
                    : $"using the staged helper at '{stagedHelperPath}': no protected copy exists yet, "
                        + "and this UAC-prompted launch installs one");
        }

        return new ElevatedHelperChoice(
            fallbackHelperPath, fallbackInstallRoot, RunsFromProtectedLocation: false,
            $"using the co-located helper at '{fallbackHelperPath}': this build ships no elevated-components "
            + "stage (a source build), so the helper runs from a directory this user can write — the "
            + "single UAC prompt is the only thing standing behind it");
    }
}
