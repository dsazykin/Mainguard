using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Mainguard.Git;

/// <summary>
/// The single source of truth for Mainguard's per-user data root — every component that persists
/// per-user state (keyring, SQLite, settings, daemon token, adapter cache, OOBE state) resolves
/// its directory through here instead of calling <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// directly.
///
/// <para><b>Why this exists (the daemon crash-loop class of bug):</b> on Unix,
/// <c>GetFolderPath(..., SpecialFolderOption.None)</c> — the default — VERIFIES the directory exists
/// (an <c>access(2)</c> check) and returns <c>""</c> when it doesn't. A fresh service account's home
/// has no <c>~/.local/share</c>, so every <c>Path.Combine(GetFolderPath(LocalApplicationData), "Mainguard")</c>
/// silently produced the RELATIVE path <c>Mainguard/…</c>, which resolved against the process CWD
/// (<c>/</c> or a root-owned dir) → <c>EACCES</c> → unhandled exception → systemd
/// restart loop. Setting <c>HOME</c> did not help, because the subdirectory still did not exist.
/// Resolution here uses <c>DoNotVerify</c>, falls back to <c>$HOME</c>, and FAILS LOUDLY with a
/// named cause rather than ever returning an empty or relative path.</para>
///
/// <para><b>Locations:</b> <c>%LocalAppData%\Mainguard</c> on Windows; <c>~/.mainguard</c> elsewhere
/// (the same directory that holds <c>daemon.token</c> — one place to look in the VM). The in-VM daemon
/// identity is <c>/home/mainguard/.mainguard</c> (the systemd unit's <c>Environment=HOME</c>).</para>
///
/// <para><b>Relocation seam:</b> <c>MAINGUARD_DATA_ROOT</c> (see <see cref="DataRootOverrideVariable"/>)
/// replaces the resolved root wholesale, for contexts that must not read or write the real user's data —
/// principally the test suites, which set it once per run to a throwaway directory
/// (<c>Mainguard.Tests/TestDataRootIsolation.cs</c>). Unset in normal use, so shipping behaviour is
/// unchanged. It is deliberately an environment variable rather than a static property: the value must
/// reach spawned child processes (a head, the daemon, the scripted-agent harness) too, and a static could
/// not. The value must be an ABSOLUTE path — a relative one is rejected loudly, because "relative data
/// root" is precisely the bug class this type exists to prevent.</para>
/// </summary>
public static class MainguardPaths
{
    /// <summary>The current Windows data-root folder name under <c>%LocalAppData%</c>.</summary>
    private const string WindowsFolder = "Mainguard";

    /// <summary>
    /// Environment variable that relocates <see cref="DataRoot"/> wholesale. Set by the test suites to a
    /// per-run temp directory so no test reads or writes <c>~/.mainguard</c>; unset in shipping use.
    /// </summary>
    public const string DataRootOverrideVariable = "MAINGUARD_DATA_ROOT";

    /// <summary>
    /// Mainguard's per-user data root. Always absolute; never verified-to-exist (callers create what
    /// they need). Honours the <see cref="DataRootOverrideVariable"/> relocation seam when set. Throws
    /// <see cref="InvalidOperationException"/> with an actionable message when the base cannot be
    /// resolved — a service context must set <c>HOME</c> (or run as a user with a passwd entry) rather
    /// than let a relative path escape.
    /// </summary>
    public static string DataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            overridden = overridden.Trim();
            if (!Path.IsPathRooted(overridden))
            {
                throw new InvalidOperationException(
                    $"{DataRootOverrideVariable} must be an absolute path — a relative data root resolves "
                    + "against the process working directory, which is the crash-loop bug class this type "
                    + $"exists to prevent. (Value: '{overridden}'.)");
            }

            return Path.GetFullPath(overridden);
        }

        return RealUserDataRoot();
    }

    /// <summary>
    /// The data root Mainguard would use with no relocation in effect — i.e. the REAL per-user root. Only
    /// interesting to the isolation guard test, which asserts the running suite is not pointed at it; no
    /// shipping caller should prefer this over <see cref="DataRoot"/>.
    /// </summary>
    public static string RealUserDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(WindowsLocalAppData(), WindowsFolder);
        }

        return Path.Combine(HomeDirectory(), ".mainguard");
    }

    /// <summary>Resolves <c>%LocalAppData%</c> (DoNotVerify), or throws the actionable error.</summary>
    private static string WindowsLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData) || !Path.IsPathRooted(localAppData))
        {
            throw new InvalidOperationException(
                "Cannot resolve %LocalAppData% — Mainguard's data root is unknown. " +
                $"(Resolved value: '{localAppData}'.)");
        }

        return localAppData;
    }

    /// <summary>
    /// The machine's ADMINISTRATOR-OWNED install roots, in preference order, for
    /// <c>Mainguard.Agents</c>'s <c>ProtectedLocationPolicy</c> (audit MG-15). Entries that cannot be
    /// resolved come back as empty strings and the caller drops them — a machine that exposes none is
    /// a machine where relocation is refused, never one where it silently lands somewhere writable.
    ///
    /// <para>Lives here for the same reason <see cref="DataRoot"/> does: <c>DoNotVerify</c> is the only
    /// safe option. The default option verifies existence and returns <c>""</c>, which on this path
    /// would not produce a relative data root but something subtler and worse — a Program Files entry
    /// that vanishes from the allow-list on the one machine where it was not yet materialised, silently
    /// turning "protected" into "nothing is protected".</para>
    /// </summary>
    public static IReadOnlyList<string> ProtectedInstallRoots()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Nothing elevated runs off these today; they exist so the policy answers coherently on a
            // dev box rather than degrading to "this machine protects nothing".
            return new[] { "/opt", "/usr/local", "/usr" };
        }

        return new[]
        {
            // The NATIVE Program Files, which a 32-bit process would otherwise miss entirely.
            Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty,
            SpecialFolder(Environment.SpecialFolder.ProgramFiles),
            SpecialFolder(Environment.SpecialFolder.ProgramFilesX86),
            SpecialFolder(Environment.SpecialFolder.Windows),
        };
    }

    /// <summary>
    /// The roots an unprivileged user can write, for the same policy's deny pass. <c>%ProgramData%</c>
    /// is deliberately absent from <see cref="ProtectedInstallRoots"/> AND from here: its default ACL
    /// lets any user create files in it, so it is not protected — and it is not a per-user root either.
    /// </summary>
    public static IReadOnlyList<string> UserWritableRoots()
    {
        var roots = new List<string>
        {
            SpecialFolder(Environment.SpecialFolder.UserProfile),
            SpecialFolder(Environment.SpecialFolder.LocalApplicationData),
            SpecialFolder(Environment.SpecialFolder.ApplicationData),
            SafeTempPath(),
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            roots.Add("/tmp");
            roots.Add("/var/tmp");
        }

        return roots;
    }

    /// <summary>DoNotVerify resolution that never throws — an unresolvable folder is an empty string the
    /// caller drops, which is the correct behaviour for an allow/deny LIST (unlike the data root, where
    /// an unresolvable value must fail loudly because there is no alternative).</summary>
    private static string SpecialFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeTempPath()
    {
        try { return Path.GetTempPath(); }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>
    /// The user's home directory, resolved robustly for BOTH interactive and service contexts:
    /// <c>GetFolderPath(UserProfile, DoNotVerify)</c> (env <c>HOME</c>, then the passwd entry —
    /// without the exists-check that turns a never-materialized home into <c>""</c>), then
    /// <c>$HOME</c> directly. Never returns an empty or relative path — throws with the systemd
    /// remedy instead.
    /// </summary>
    public static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
        {
            throw new InvalidOperationException(
                "Cannot resolve the user home directory: HOME is not set and no passwd entry resolved. " +
                "A service running mainguardd must set it explicitly (systemd: Environment=HOME=/home/mainguard). " +
                $"(Resolved value: '{home}'.)");
        }

        return home;
    }
}
