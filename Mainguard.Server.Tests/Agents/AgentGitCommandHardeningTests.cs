using System;
using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// W1-A layer 1 — the belt. Every daemon-side git runs, as the daemon user on the host, against a
/// repository the jailed agent can write. These tests plant the audit's exact payload (a repo-local
/// <c>filter.*</c> driver plus a <c>.gitattributes</c> line that assigns it) and measure whether the
/// driver's command is executed.
///
/// <para><b>Each execution test proves itself non-vacuous first.</b> An unhardened <c>git</c> — the
/// test harness's own runner, which is what the daemon effectively was — runs the payload and the
/// marker appears; then the same repository is driven through <see cref="AgentGitCommand"/> and the
/// marker must not appear again. A test that only asserted the negative would pass just as happily if
/// git had stopped supporting filters, or if the attribute never matched.</para>
///
/// <para>The payload command is <c>git config</c> rather than a shell utility: git spawns a filter
/// through a shell on every platform, and <c>git</c> is by definition on the PATH of a machine running
/// these tests, so the proof does not depend on <c>touch</c>/<c>sh</c> existing.</para>
/// </summary>
public sealed class AgentGitCommandHardeningTests : IDisposable
{
    private readonly string _root = AgentTestGit.NewVmRoot();

    public void Dispose() => AgentTestGit.DeleteTree(_root);

    [Fact]
    public void HostileFilterDriver_IsNotExecuted_WhenTheDaemonStagesTheWorktree()
    {
        var repo = NewRepo("filter");
        PlantAttributes(repo, "* filter=pwn");
        // A clean filter runs on `git add`/`git status`/`git diff` — i.e. on every leg of the
        // keep-alive cycle the audit measured.
        AgentTestGit.RunChecked(repo, "config", "filter.pwn.clean", MarkerCommand("clean"));
        AgentTestGit.RunChecked(repo, "config", "filter.pwn.smudge", MarkerCommand("smudge"));

        // Non-vacuous: unhardened git executes it.
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v1\n");
        AgentTestGit.RunChecked(repo, "add", "-A");
        Assert.Equal("yes", Marker(repo, "clean"));

        ClearMarkers(repo);

        // The daemon's own path must not.
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v2\n");
        AgentGitCommand.Run(repo, "add", "-A");
        Assert.Null(Marker(repo, "clean"));

        // …nor on the status probe the rebaser opens every cycle with, nor on a checkout's smudge leg.
        AgentGitCommand.Run(repo, "status", "--porcelain");
        AgentGitCommand.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "wip: sync");
        AgentGitCommand.Run(repo, "checkout", "--", "payload.txt");
        Assert.Null(Marker(repo, "clean"));
        Assert.Null(Marker(repo, "smudge"));
    }

    [Fact]
    public void HostileTextconvDriver_IsNotExecuted_WhenTheDaemonDiffs()
    {
        var repo = NewRepo("textconv");
        PlantAttributes(repo, "* diff=pwn");
        AgentTestGit.RunChecked(repo, "config", "diff.pwn.textconv", MarkerCommand("textconv"));

        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v1\n");
        AgentTestGit.RunChecked(repo, "add", "-A");
        AgentTestGit.RunChecked(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "payload.txt"), "v2\n");

        // Non-vacuous: `git diff` applies textconv by default.
        AgentTestGit.RunChecked(repo, "diff");
        Assert.Equal("yes", Marker(repo, "textconv"));

        ClearMarkers(repo);

        AgentGitCommand.TryRun(repo, out _, "diff");
        Assert.Null(Marker(repo, "textconv"));
    }

    /// <summary>
    /// The families that cannot be reached from a two-line fixture (<c>merge.&lt;d&gt;.driver</c> needs
    /// a real conflicting rebase, <c>uploadpack.packObjectsHook</c> needs a serving process) are pinned
    /// on the pure classifier instead, so the list is a tested artifact rather than a comment.
    /// </summary>
    [Theory]
    [InlineData("filter.lfs.clean")]
    [InlineData("filter.lfs.smudge")]
    [InlineData("filter.a.process")]
    [InlineData("merge.ours.driver")]
    [InlineData("diff.external")]
    [InlineData("diff.jpg.command")]
    [InlineData("diff.jpg.textconv")]
    [InlineData("difftool.x.cmd")]
    [InlineData("mergetool.x.cmd")]
    [InlineData("core.hooksPath")]
    [InlineData("core.fsmonitor")]
    [InlineData("core.sshCommand")]
    [InlineData("core.pager")]
    [InlineData("core.editor")]
    [InlineData("core.gitProxy")]
    [InlineData("core.askPass")]
    [InlineData("core.alternateRefsCommand")]
    [InlineData("sequence.editor")]
    [InlineData("credential.helper")]
    [InlineData("credential.https://evil.example.com.helper")]
    [InlineData("remote.origin.uploadpack")]
    [InlineData("remote.origin.receivepack")]
    [InlineData("uploadpack.packObjectsHook")]
    [InlineData("init.templateDir")]
    [InlineData("gpg.program")]
    [InlineData("gpg.ssh.program")]
    [InlineData("trailer.sign.command")]
    [InlineData("submodule.sub.update")]
    [InlineData("pager.log")]
    public void CommandExecutingKeys_AreClassifiedAsSuch(string key)
        => Assert.True(GitConfigExecutionSurface.IsCommandExecuting(key), key);

    /// <summary>
    /// The other half of the contract: the keys the product itself writes must NOT be neutralized, or
    /// the belt would quietly break provisioning. These are exactly the repo-local keys
    /// <c>RepoProvisioner</c>/<c>AgentRepo</c>/<c>MirrorMaintenance</c> set.
    /// </summary>
    [Theory]
    [InlineData("core.sharedRepository")]
    [InlineData("core.untrackedCache")]
    [InlineData("core.bare")]
    [InlineData("core.repositoryformatversion")]
    [InlineData("gc.auto")]
    [InlineData("maintenance.auto")]
    [InlineData("receive.denyNonFastForwards")]
    [InlineData("receive.denyDeletes")]
    [InlineData("remote.origin.url")]
    [InlineData("remote.origin.fetch")]
    [InlineData("user.name")]
    [InlineData("user.email")]
    [InlineData("filter.lfs.required")]
    [InlineData("alias.st")]
    [InlineData("extensions.worktreeConfig")]
    public void ProductAndInertKeys_AreLeftAlone(string key)
        => Assert.False(GitConfigExecutionSurface.IsCommandExecuting(key), key);

    /// <summary>
    /// A subsection name may legally contain <c>=</c>, and <c>git -c</c> splits at the first one — so
    /// the override would silently land on a different key while the real driver stayed live. That is
    /// the one outcome worse than no override, so it is a typed refusal and git never runs.
    /// </summary>
    [Fact]
    public void UnexpressibleKeyName_IsRefused_RatherThanSilentlyMisapplied()
    {
        Assert.False(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.a=b.clean"));
        Assert.False(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.a b.clean"));
        Assert.True(GitConfigExecutionSurface.IsExpressibleOnCommandLine("filter.ab.clean"));

        var repo = NewRepo("unexpressible");
        // Written straight into the config file: `git config` would reject the name too.
        File.AppendAllText(
            Path.Combine(repo, ".git", "config"),
            "\n[filter \"a=b\"]\n\tclean = " + MarkerCommand("clean") + "\n");

        var ex = Assert.Throws<RepoProvisioningException>(() => AgentGitCommand.Run(repo, "status", "--porcelain"));
        Assert.Contains("cannot be overridden", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The belt must not have made ordinary daemon git any less usable: a clean repository
    /// still stages, commits and reports through the same path.</summary>
    [Fact]
    public void OrdinaryDaemonGit_StillWorks()
    {
        var repo = NewRepo("ordinary");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "hello\n");
        AgentGitCommand.Run(repo, "add", "-A");
        AgentGitCommand.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "seed");
        Assert.Equal(string.Empty, AgentGitCommand.Run(repo, "status", "--porcelain").Trim());
        Assert.Equal(0, AgentGitCommand.TryRun(repo, out var sha, "rev-parse", "HEAD"));
        Assert.Equal(40, sha.Trim().Length);
    }

    // ---- fixture -------------------------------------------------------------

    private string NewRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        AgentTestGit.RunChecked(path, "init", "-q");
        AgentTestGit.SetIdentity(path);
        return path;
    }

    private static void PlantAttributes(string repo, string line)
        => File.WriteAllText(Path.Combine(repo, ".gitattributes"), line + "\n");

    /// <summary>The payload: a command git will spawn through a shell, whose only effect is a
    /// config marker this test can read back. Never a shell utility — <c>git</c> is the one binary
    /// guaranteed present.</summary>
    private static string MarkerCommand(string marker) => $"git config --local pwned.{marker} yes";

    private static string? Marker(string repo, string marker)
    {
        var (code, output, _) = AgentTestGit.Run(repo, "config", "--local", "--get", "pwned." + marker);
        return code == 0 ? output.Trim() : null;
    }

    private static void ClearMarkers(string repo)
        => AgentTestGit.Run(repo, "config", "--local", "--remove-section", "pwned");
}
