using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mainguard.Git.Services;

/// <summary>
/// Lets one git invocation read a repository that lives behind a Windows <b>UNC</b> path
/// (<c>\\server\share\…</c>) without the user having to relax their own global git trust settings.
///
/// <para><b>Why this exists.</b> The agent platform's sync remote is a bare mirror inside the WSL2
/// VM, addressed from Windows as
/// <c>\\wsl.localhost\MainguardEnv\home\mainguard\mainguard\repos\&lt;hash&gt;.git</c>. Those files are
/// owned by the VM's Linux user, so Windows reports a different owner SID and every
/// <c>git fetch</c> against the mirror dies with
/// <c>fatal: detected dubious ownership in repository at '\\wsl.localhost\…'</c>. This blocked the
/// product's core guarantee — a verified agent branch reaching <c>main</c> — on the very first merge
/// for every Windows + WSL2 user.</para>
///
/// <para><b>Two measured git facts drive the implementation</b> (git 2.45.1.windows.1, verified live
/// against a real MainguardEnv mirror; see <c>docs/review/walkthrough-windows-2026-08-24/</c> W4):</para>
/// <list type="number">
/// <item><description><b><c>safe.directory</c> is ignored in command scope.</b> Git reads it through
/// <c>read_very_early_config()</c>, which sets <c>ignore_cmdline = 1</c>. So
/// <c>git -c safe.directory=&lt;path&gt;</c> — and its env twin
/// <c>GIT_CONFIG_COUNT/KEY_0/VALUE_0</c> — are silently discarded, <b>including the <c>*</c>
/// wildcard</b>. Both were measured failing against the live mirror. Only the file-based
/// <i>system</i> and <i>global</i> scopes are honoured. That is why this class shims a config
/// <i>file</i> rather than passing <c>-c</c>: the obvious one-line fix does nothing at all.</description></item>
/// <item><description><b>The value must be the literal Windows path, backslashes and all.</b>
/// <c>\\wsl.localhost\…</c> matches; the forward-slash spelling <c>//wsl.localhost/…</c> does not.
/// And because a git config <i>file</i> treats <c>\</c> as an escape character, every backslash has
/// to be written doubled — see <see cref="EscapeConfigValue"/>. Getting this wrong is exactly how
/// the original manual workaround failed: the value landed in <c>.gitconfig</c> with one leading
/// backslash instead of two, so it read back as <c>\wsl.localhost\…</c> and never matched, while
/// looking character-identical to git's error text on visual inspection.</description></item>
/// </list>
///
/// <para><b>Trust scope.</b> The exception names the one exact mirror path, never <c>*</c>, and is
/// injected via <c>GIT_CONFIG_SYSTEM</c> for the duration of a single child process — nothing is
/// written to the user's own configuration at any scope. The shim <c>include.path</c>s the real
/// system config, so the user keeps every setting they had; the only difference for that one
/// invocation is the added trust entry. Non-Windows hosts and non-UNC remotes take an early return
/// and behave exactly as before.</para>
/// </summary>
internal static class UncRemoteTrust
{
    /// <summary>
    /// Runs git with <paramref name="remoteName"/>'s repository trusted, when — and only when — that
    /// remote is a Windows UNC path. Any other remote (an https host, a plain local mirror, anything
    /// on macOS/Linux) falls through to an unmodified <see cref="GitService.RunGit(string, string[])"/>.
    /// </summary>
    internal static (int Code, string Out, string Err) RunGitTrustingRemote(
        string repoPath, string remoteName, params string[] args)
    {
        var trusted = ResolveUncRemoteUrl(repoPath, remoteName);
        if (trusted is null)
        {
            return GitService.RunGit(repoPath, args);
        }

        string? shim = null;
        try
        {
            shim = WriteShim(ResolveSystemConfigPath(repoPath), trusted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory we cannot write to must not turn a fetch into a crash: run git
            // unmodified and let its own dubious-ownership message reach the caller's phrased reason.
            return GitService.RunGit(repoPath, args);
        }

        try
        {
            return GitService.RunGit(
                repoPath,
                new Dictionary<string, string> { ["GIT_CONFIG_SYSTEM"] = shim },
                default,
                args);
        }
        finally
        {
            try { File.Delete(shim); } catch { /* best-effort; the temp dir is ours */ }
        }
    }

    /// <summary>
    /// The remote's URL when it is a Windows UNC path worth trusting, else null. The URL is read from
    /// git's own config rather than taken from a caller, because the string that has to match is the
    /// one git itself will resolve — and the daemon's <c>SyncRemote.Url</c> is not always populated
    /// on the client side.
    /// </summary>
    internal static string? ResolveUncRemoteUrl(string repoPath, string remoteName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(remoteName))
        {
            return null;
        }

        var (code, output, _) = GitService.RunGit(repoPath, "config", "--get", $"remote.{remoteName}.url");
        return code != 0 ? null : NormalizeUncPath(output);
    }

    /// <summary>
    /// A UNC path is <c>\\server\share\…</c> — two leading backslashes. Trailing separators are
    /// trimmed because git resolves the repository directory without one, and the comparison against
    /// a configured <c>safe.directory</c> is a plain string compare.
    /// </summary>
    internal static string? NormalizeUncPath(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        trimmed = trimmed.TrimEnd('\\', '/');

        // A bare "\\" or "\\server" is not a repository path; refuse rather than emit a trust
        // entry broad enough to cover a whole host.
        return trimmed.Length > 2 && trimmed.LastIndexOf('\\') > 1 ? trimmed : null;
    }

    /// <summary>
    /// Escapes a value for a git config <b>file</b>. Git's own writer escapes backslash and
    /// double-quote this way; skipping it is what silently turned <c>\\wsl.localhost\…</c> into the
    /// unmatchable <c>\wsl.localhost\…</c> in the original hand-written workaround.
    /// </summary>
    internal static string EscapeConfigValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// The shim handed to git as <c>GIT_CONFIG_SYSTEM</c>: the user's real system config, included
    /// verbatim so nothing they configured is lost, plus the single trusted directory.
    /// </summary>
    internal static string BuildShimContent(string? includeSystemConfigPath, string trustedPath)
    {
        var sb = new StringBuilder();
        sb.Append("# Generated by Mainguard for a single git invocation. Safe to delete.\n");
        if (!string.IsNullOrWhiteSpace(includeSystemConfigPath))
        {
            sb.Append("[include]\n\tpath = ").Append(EscapeConfigValue(includeSystemConfigPath!)).Append('\n');
        }
        sb.Append("[safe]\n\tdirectory = ").Append(EscapeConfigValue(trustedPath)).Append('\n');
        return sb.ToString();
    }

    private static string WriteShim(string? systemConfigPath, string trustedPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-git-trust");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".gitconfig");
        File.WriteAllText(file, BuildShimContent(systemConfigPath, trustedPath));
        return file;
    }

    /// <summary>The path of the real system config, so the shim can include rather than replace it.</summary>
    private static string? ResolveSystemConfigPath(string repoPath)
    {
        // --show-origin prefixes every line with "file:<path>\t"; an absent or empty system config
        // exits non-zero, in which case there is simply nothing to preserve.
        var (code, output, _) = GitService.RunGit(
            repoPath, "config", "--list", "--show-origin", "--name-only", "--system");
        if (code != 0)
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("file:", StringComparison.Ordinal))
            {
                continue;
            }

            var tab = trimmed.IndexOf('\t');
            if (tab > "file:".Length)
            {
                return trimmed["file:".Length..tab];
            }
        }

        return null;
    }
}
