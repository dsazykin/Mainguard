using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// W1-A layer 2 — the daemon must not learn its git layout from files the agent can write.
///
/// <para>A linked worktree's <c>.git</c> is a one-line pointer inside the agent's workspace, and the
/// per-worktree gitdir it names carries a <c>commondir</c> that decides where <c>refs/</c>,
/// <c>objects/</c> and <c>config</c> come from. Both are agent-writable, and every daemon-side git that
/// runs with the worktree as its working directory follows both. These tests drive real git worktrees
/// and then rewrite that pointer the way an agent would.</para>
/// </summary>
public sealed class TrustedWorktreeLayoutTests : IDisposable
{
    private readonly string _root = AgentTestGit.NewVmRoot();

    public void Dispose() => AgentTestGit.DeleteTree(_root);

    [Fact]
    public void RealLinkedWorktree_ResolvesToTheDaemonComputedRepository()
    {
        var env = new Fixture(_root);

        var layout = TrustedWorktreeLayout.TryResolve(env.Worktree, env.AgentRepo, env.Mirror);

        Assert.NotNull(layout);
        // Paths come back symlink-resolved (macOS puts the daemon on a host where /var is a link into
        // /private), so the assertions name the SHAPE rather than a spelling: the common dir is the agent
        // repository, the gitdir is its `worktrees/<name>` child, and the work tree is the one asked for.
        Assert.EndsWith("a1.git", layout!.CommonDir, StringComparison.Ordinal);
        Assert.EndsWith("wt-a1", layout.WorkTree, StringComparison.Ordinal);
        Assert.Equal(layout.CommonDir, Path.GetDirectoryName(Path.GetDirectoryName(layout.GitDir)));
        Assert.Equal("worktrees", Path.GetFileName(Path.GetDirectoryName(layout.GitDir)));
        Assert.Equal(layout.GitDir, layout.Env["GIT_DIR"]);
        Assert.Equal(layout.CommonDir, layout.Env["GIT_COMMON_DIR"]);
        Assert.Equal(layout.WorkTree, layout.Env["GIT_WORK_TREE"]);
    }

    /// <summary>The audit's "plausible second vector", exactly: aim the pointer at the shared mirror —
    /// which is read-only to every jail precisely so no agent can reach it — and let the daemon reach it
    /// on the agent's behalf, with the daemon's own credentials, outside the jail.</summary>
    [Fact]
    public void PointerAimedAtTheSharedMirror_IsRefused()
    {
        var env = new Fixture(_root);
        Redirect(env.Worktree, Path.Combine(env.Mirror, "worktrees", "stolen"));

        var ex = Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, agentRepoPath: null, forbiddenCommonDir: env.Mirror));
        Assert.Contains("shared mirror", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>With the daemon-computed repository supplied, the pointer can no longer decide WHOSE
    /// repository the daemon operates on — not the mirror, not a co-tenant's, not one the agent made.</summary>
    [Fact]
    public void PointerAimedAtSomeOtherRepository_IsRefused_WhenTheDaemonKnowsTheRealOne()
    {
        var env = new Fixture(_root);
        var elsewhere = Path.Combine(_root, "elsewhere.git");
        Directory.CreateDirectory(Path.Combine(elsewhere, "worktrees", "w"));
        Redirect(env.Worktree, Path.Combine(elsewhere, "worktrees", "w"));

        var ex = Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, env.AgentRepo, env.Mirror));
        Assert.Contains("Refusing to run daemon-side git against a repository the agent chose", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A pointer that is not <c>&lt;repo&gt;/worktrees/&lt;name&gt;</c> is not a linked worktree of
    /// anything. Refused rather than guessed at.</summary>
    [Fact]
    public void PointerWithoutALinkedWorktreeShape_IsRefused()
    {
        var env = new Fixture(_root);
        Redirect(env.Worktree, Path.Combine(_root, "not-a-worktree-dir"));

        var ex = Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, agentRepoPath: null, forbiddenCommonDir: env.Mirror));
        Assert.Contains("not a linked worktree directory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A main working tree has a real <c>.git</c> DIRECTORY — no indirection to subvert, so
    /// nothing to pin and nothing to refuse. Null means "run exactly as before", which is what keeps the
    /// substrate-less test doubles and the pre-MG-3 shapes working.</summary>
    [Fact]
    public void MainWorkingTree_ResolvesToNull_AndIsRunUnpinned()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        AgentTestGit.RunChecked(plain, "init", "-q");

        Assert.Null(TrustedWorktreeLayout.TryResolve(plain));
        Assert.Null(TrustedWorktreeLayout.TryResolve(Path.Combine(_root, "does-not-exist")));
    }

    /// <summary>
    /// End to end through the keep-alive cycle: a redirected pointer ends the cycle as
    /// <see cref="RebaseCycleKind.Skipped"/> — and does so BEFORE the yield, so nothing was paused,
    /// nothing was mutated and no token was taken. The yield protocol here throws if it is touched,
    /// which is the assertion.
    /// </summary>
    [Fact]
    public async Task KeepAliveCycle_RefusesARedirectedWorktree_BeforeItYieldsAnything()
    {
        var env = new Fixture(_root);
        Redirect(env.Worktree, Path.Combine(env.Mirror, "worktrees", "stolen"));

        var rebaser = new KeepAliveRebaser(
            new ExplodingYieldProtocol(),
            _ => new AgentWorktreeLocation(env.Worktree, env.Mirror, env.MainBranch, env.AgentRepo));

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Skipped, result.Kind);
        Assert.False(result.BranchIsOnTopOfMain);
        Assert.Contains("shared mirror", result.Detail!, StringComparison.Ordinal);
    }

    // ---- fixture -------------------------------------------------------------

    /// <summary>Overwrites the worktree's <c>.git</c> pointer the way a jailed agent with a writable
    /// workspace can.</summary>
    private static void Redirect(string worktree, string target)
        => File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + target + "\n");

    /// <summary>A source repo → a bare shared MIRROR → a per-agent repository cloned from it → a real
    /// linked worktree off the agent repository. The production MG-3 shape.</summary>
    private sealed class Fixture
    {
        public Fixture(string root)
        {
            var source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            AgentTestGit.RunChecked(source, "init", "-q");
            AgentTestGit.SetIdentity(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "hello\n");
            AgentTestGit.RunChecked(source, "add", "-A");
            AgentTestGit.RunChecked(source, "commit", "-q", "-m", "seed");
            MainBranch = AgentTestGit.RunChecked(source, "symbolic-ref", "--short", "HEAD").Trim();

            Mirror = Path.Combine(root, "mirror.git");
            AgentTestGit.RunChecked(root, "clone", "--bare", "-q", source, Mirror);

            AgentRepo = Path.Combine(root, "a1.git");
            AgentTestGit.RunChecked(root, "clone", "--bare", "--shared", "-q", Mirror, AgentRepo);

            Worktree = Path.Combine(root, "wt-a1");
            AgentTestGit.RunChecked(AgentRepo, "worktree", "add", "-q", "-b", "agent/a1", Worktree, MainBranch);
        }

        public string Mirror { get; }

        public string AgentRepo { get; }

        public string Worktree { get; }

        public string MainBranch { get; }
    }

    /// <summary>Fails the test if the cycle reaches the yield at all.</summary>
    private sealed class ExplodingYieldProtocol : IYieldProtocol
    {
        public Task<IYieldToken> RequestYieldAsync(string agentId, TimeSpan? timeout = null, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "The keep-alive cycle yielded (and would have paused) an agent whose worktree layout it had "
                + "already been given cause to refuse.");
    }
}
