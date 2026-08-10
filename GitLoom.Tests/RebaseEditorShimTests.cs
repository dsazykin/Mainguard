using System;
using System.IO;
using GitLoom.App;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The GIT_SEQUENCE_EDITOR / GIT_EDITOR shim's exit-code contract. Git reads exit 0 as "the editor
/// wrote the file you asked for"; if we cannot place the todo and exit 0 anyway, git proceeds with
/// its own default todo — a plain <c>pick</c> of every commit — silently discarding every reorder,
/// squash, drop and fixup, and then reports the rebase as a success. So the load-bearing assertion
/// here is that a failed copy exits NON-ZERO (git aborts, branch untouched) and says why.
///
/// Every test writes under the test's own temp directory and cleans up after itself.
/// </summary>
public class RebaseEditorShimTests : IDisposable
{
    private readonly string _dir;

    public RebaseEditorShimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gitloom-rebase-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            {
                try { new FileInfo(f) { IsReadOnly = false }.Refresh(); } catch { }
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- WriteTodo -------------------------------------------------------------------

    [Fact]
    public void WriteTodo_WhenCopySucceeds_ShouldReturnZero_AndPlaceTheTodo()
    {
        var generated = Write("generated-todo", "pick aaaaaaa one\nsquash bbbbbbb two\n");
        var gitTodo = Write("git-rebase-todo", "pick aaaaaaa one\npick bbbbbbb two\n");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteTodo(generated, gitTodo, stderr);

        Assert.Equal(RebaseEditorShim.Success, exit);
        Assert.Equal("pick aaaaaaa one\nsquash bbbbbbb two\n", File.ReadAllText(gitTodo));
        Assert.Null(RebaseEditorShim.LastError);
        Assert.Equal("", stderr.ToString());
    }

    /// <summary>
    /// The regression this whole class exists for: the destination git handed us cannot be written.
    /// Before the fix the copy threw into a bare <c>catch { }</c> and the shim exited 0, so git
    /// rebased with its default plain-pick todo and called it a success.
    /// </summary>
    [Fact]
    public void WriteTodo_WhenDestinationIsReadOnly_ShouldReturnNonZero()
    {
        var generated = Write("generated-todo", "drop aaaaaaa one\n");
        var gitTodo = Write("git-rebase-todo", "pick aaaaaaa one\n");
        File.SetAttributes(gitTodo, FileAttributes.ReadOnly);

        // A test run as root can write a read-only file, which would make this vacuous.
        Assert.True(IsUnwritable(gitTodo), "destination should be read-only for this test to mean anything");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteTodo(generated, gitTodo, stderr);

        Assert.NotEqual(RebaseEditorShim.Success, exit);
        // git must see the plan was NOT applied, and the user must be able to find out why.
        Assert.Equal("pick aaaaaaa one\n", File.ReadAllText(gitTodo));
        Assert.NotNull(RebaseEditorShim.LastError);
        Assert.Contains("git-rebase-todo", stderr.ToString());
    }

    /// <summary>
    /// Same contract, but through a failure mode no privilege level can bypass (the destination
    /// path is a directory), so this half of the guarantee holds even when the run is elevated.
    /// </summary>
    [Fact]
    public void WriteTodo_WhenDestinationCannotBeWrittenAtAll_ShouldReturnNonZero()
    {
        var generated = Write("generated-todo", "reword aaaaaaa one\n");
        var gitTodo = Path.Combine(_dir, "git-rebase-todo-dir");
        Directory.CreateDirectory(gitTodo);

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteTodo(generated, gitTodo, stderr);

        Assert.NotEqual(RebaseEditorShim.Success, exit);
        Assert.NotNull(RebaseEditorShim.LastError);
    }

    [Fact]
    public void WriteTodo_WhenGeneratedTodoIsMissing_ShouldReturnNonZero()
    {
        var gitTodo = Write("git-rebase-todo", "pick aaaaaaa one\n");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteTodo(Path.Combine(_dir, "does-not-exist"), gitTodo, stderr);

        Assert.NotEqual(RebaseEditorShim.Success, exit);
        Assert.Equal("pick aaaaaaa one\n", File.ReadAllText(gitTodo));
        Assert.NotNull(RebaseEditorShim.LastError);
    }

    // ---- WriteRebaseMessage ----------------------------------------------------------

    /// <summary>
    /// Nothing staged for this step is a normal outcome (git calls GIT_EDITOR for steps we have no
    /// replacement message for), so it must stay exit 0 and leave git's own text alone.
    /// </summary>
    [Fact]
    public void WriteRebaseMessage_WhenNothingStagedForThisStep_ShouldReturnZero()
    {
        var msgDir = Path.Combine(_dir, "messages");
        Directory.CreateDirectory(msgDir);
        var gitMsg = SetUpRebaseMessageFile("aaaaaaa1111111111111111111111111111111", "git's default message\n");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteRebaseMessage(msgDir, gitMsg, stderr);

        Assert.Equal(RebaseEditorShim.Success, exit);
        Assert.Equal("git's default message\n", File.ReadAllText(gitMsg));
    }

    [Fact]
    public void WriteRebaseMessage_WhenStagedMessageExists_ShouldReturnZero_AndPlaceIt()
    {
        const string sha = "aaaaaaa1111111111111111111111111111111";
        var msgDir = Path.Combine(_dir, "messages");
        Directory.CreateDirectory(msgDir);
        File.WriteAllText(Path.Combine(msgDir, sha + ".msg"), "the reworded subject\n");
        var gitMsg = SetUpRebaseMessageFile(sha, "git's default message\n");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteRebaseMessage(msgDir, gitMsg, stderr);

        Assert.Equal(RebaseEditorShim.Success, exit);
        Assert.Equal("the reworded subject\n", File.ReadAllText(gitMsg));
    }

    /// <summary>
    /// A message WAS staged and we could not place it: exiting 0 here would commit git's default
    /// message and silently drop the user's reword, so this must be non-zero too.
    /// </summary>
    [Fact]
    public void WriteRebaseMessage_WhenStagedMessageCannotBeWritten_ShouldReturnNonZero()
    {
        const string sha = "aaaaaaa1111111111111111111111111111111";
        var msgDir = Path.Combine(_dir, "messages");
        Directory.CreateDirectory(msgDir);
        File.WriteAllText(Path.Combine(msgDir, sha + ".msg"), "the reworded subject\n");
        var gitMsg = SetUpRebaseMessageFile(sha, "git's default message\n");
        File.SetAttributes(gitMsg, FileAttributes.ReadOnly);

        Assert.True(IsUnwritable(gitMsg), "destination should be read-only for this test to mean anything");

        var stderr = new StringWriter();
        var exit = RebaseEditorShim.WriteRebaseMessage(msgDir, gitMsg, stderr);

        Assert.NotEqual(RebaseEditorShim.Success, exit);
        Assert.Equal("git's default message\n", File.ReadAllText(gitMsg));
        Assert.NotNull(RebaseEditorShim.LastError);
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>
    /// Lays out the ".git/rebase-merge/done + COMMIT_EDITMSG" shape the shim reads the current step
    /// from, and returns the message-file path git would hand the editor.
    /// </summary>
    private string SetUpRebaseMessageFile(string sha, string defaultMessage)
    {
        var rebaseMerge = Path.Combine(_dir, "rebase-merge");
        Directory.CreateDirectory(rebaseMerge);
        File.WriteAllText(Path.Combine(rebaseMerge, "done"), $"pick {sha} the original subject\n");

        var gitMsg = Path.Combine(rebaseMerge, "COMMIT_EDITMSG");
        File.WriteAllText(gitMsg, defaultMessage);
        return gitMsg;
    }

    private static bool IsUnwritable(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write);
            return false;
        }
        catch
        {
            return true;
        }
    }
}
