using System;
using System.IO;

namespace GitLoom.App;

/// <summary>
/// The editor shim git launches during an interactive rebase. <c>InteractiveRebaseService</c> points
/// <c>GIT_SEQUENCE_EDITOR</c> at <c>GitLoom --rebase-editor &lt;todo&gt;</c> and <c>GIT_EDITOR</c> at
/// <c>GitLoom --rebase-msg &lt;msgDir&gt;</c>; git then execs us with the file it wants edited.
///
/// <para><b>The exit code is the entire contract.</b> Git reads exit 0 as "the editor wrote the file
/// I asked for". If placing the todo fails (AV lock, EPERM, long path, full disk) and we still exit
/// 0, git falls back to its <i>default</i> todo — a plain <c>pick</c> of every commit — silently
/// discarding every reorder, squash, drop and fixup the user built, and then reports the rebase as a
/// success. So every failure to place a file git is waiting on returns non-zero: git aborts the
/// rebase and leaves the branch exactly where it was. The reason is written to stderr (which git
/// relays to its own output) and kept in <see cref="LastError"/> for callers and tests.</para>
/// </summary>
public static class RebaseEditorShim
{
    /// <summary>Exit code meaning "the file git asked for is in place".</summary>
    public const int Success = 0;

    /// <summary>Exit code meaning "we did NOT write the file — abort, do not fall back to a default".</summary>
    public const int Failure = 1;

    /// <summary>Reason for the most recent failure, or <c>null</c> after a success. Also written to stderr.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Copies the todo GitLoom generated over the sequence file git handed us. Returns
    /// <see cref="Success"/> only when the todo is genuinely in place.
    /// </summary>
    public static int WriteTodo(string generatedTodoPath, string gitTodoPath, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        LastError = null;

        if (string.IsNullOrWhiteSpace(generatedTodoPath) || string.IsNullOrWhiteSpace(gitTodoPath))
        {
            return Fail(stderr, "no todo path was supplied to the rebase editor.");
        }

        string todo;
        try
        {
            todo = File.ReadAllText(generatedTodoPath);
        }
        catch (Exception ex)
        {
            return Fail(stderr, $"could not read the generated todo '{generatedTodoPath}': {ex.Message}");
        }

        try
        {
            File.Copy(generatedTodoPath, gitTodoPath, true);
        }
        catch (Exception ex)
        {
            return Fail(stderr, $"could not write git's sequence file '{gitTodoPath}': {ex.Message}");
        }

        // Invariant 5: log the todo actually applied to git's sequence file. Only reached once the
        // copy has succeeded, so this can never report a todo git did not receive.
        System.Diagnostics.Debug.WriteLine("[GitLoom] Interactive rebase applied todo:\n" + todo);
        return Success;
    }

    /// <summary>
    /// Copies the message GitLoom staged for the commit git is currently rewording/squashing over
    /// the message file git handed us.
    ///
    /// <para>Staging nothing is a legitimate outcome — git invokes <c>GIT_EDITOR</c> for steps we
    /// have no replacement message for — so "no staged message for this step" returns
    /// <see cref="Success"/> and leaves git's default text alone. Failing to place a message we
    /// <i>did</i> stage returns <see cref="Failure"/>: exiting 0 there would commit git's default
    /// message and silently drop the user's reword.</para>
    /// </summary>
    public static int WriteRebaseMessage(string msgDir, string gitMsgPath, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        LastError = null;

        if (string.IsNullOrWhiteSpace(msgDir) || string.IsNullOrWhiteSpace(gitMsgPath) || !Directory.Exists(msgDir))
        {
            return Success;
        }

        string? msgFile;
        try
        {
            var sha = ReadCurrentRebaseSha(gitMsgPath);
            if (sha == null) return Success;
            msgFile = ResolveStagedMessageFile(msgDir, sha);
        }
        catch (Exception ex)
        {
            // We could not even work out which step git is on, so we cannot tell "nothing staged"
            // apart from "staged but unreachable". Fail closed rather than commit git's default.
            return Fail(stderr, $"could not resolve the rebase step being edited: {ex.Message}");
        }

        if (msgFile == null) return Success;

        try
        {
            File.Copy(msgFile, gitMsgPath, true);
        }
        catch (Exception ex)
        {
            return Fail(stderr, $"could not write git's message file '{gitMsgPath}': {ex.Message}");
        }

        return Success;
    }

    private static int Fail(TextWriter stderr, string reason)
    {
        LastError = reason;
        // git relays the editor's stderr, so this reaches the user even in Release, where the old
        // Debug.WriteLine diagnostic did not exist at all.
        try { stderr.WriteLine("GitLoom rebase editor: " + reason); } catch { /* stderr closed */ }
        return Failure;
    }

    /// <summary>
    /// The staged-message file for <paramref name="sha"/>, or <c>null</c> when nothing was staged
    /// for this step. Tolerates abbreviated SHAs on either side.
    /// </summary>
    private static string? ResolveStagedMessageFile(string msgDir, string sha)
    {
        var exact = Path.Combine(msgDir, sha + ".msg");
        if (File.Exists(exact)) return exact;

        foreach (var f in Directory.GetFiles(msgDir, "*.msg"))
        {
            var key = Path.GetFileNameWithoutExtension(f);
            if (key.StartsWith(sha, StringComparison.OrdinalIgnoreCase)
                || sha.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the original SHA of the rebase step git is currently editing from the last executed
    /// line of <c>.git/rebase-merge/done</c>. The git directory is derived from the message-file
    /// path git handed us, so this is correct even for linked worktrees.
    /// </summary>
    internal static string? ReadCurrentRebaseSha(string gitMsgPath)
    {
        var donePath = FindRebaseDone(gitMsgPath);
        if (donePath == null) return null;

        string[] lines;
        try { lines = File.ReadAllLines(donePath); }
        catch { return null; }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // done lines look like: "<command> <sha> <subject...>"
            return parts.Length >= 2 ? parts[1] : null;
        }
        return null;
    }

    private static string? FindRebaseDone(string gitMsgPath)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(gitMsgPath))!);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "rebase-merge", StringComparison.Ordinal))
                {
                    var here = Path.Combine(dir.FullName, "done");
                    if (File.Exists(here)) return here;
                }
                var nested = Path.Combine(dir.FullName, "rebase-merge", "done");
                if (File.Exists(nested)) return nested;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }
}
