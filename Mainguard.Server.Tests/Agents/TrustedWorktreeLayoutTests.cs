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

    /// <summary>
    /// W1-A rework, blocker 2 — the bypass, verbatim, and the ONE assertion that proves it closed: the
    /// shared mirror's <c>main</c> does not move.
    ///
    /// <para>Everything the agent needs is in its own repository, which its jail mounts read-write; only
    /// the worktree's <c>.git</c> pointer was made read-only. So: write
    /// <c>&lt;agentRepo&gt;/worktrees/&lt;n&gt;/commondir</c> = the mirror (mounted at the same path in the
    /// jail), point <c>…/HEAD</c> at <c>refs/heads/&lt;main&gt;</c>, and BLANK <c>…/gitdir</c>. The pointer
    /// is untouched, so the daemon derived the right common directory and the mirror check passed — and
    /// then the round-trip failed, <c>TryResolve</c> returned null, and null meant "run unpinned with the
    /// worktree as cwd". git then walked <c>.git</c> → gitdir → <c>commondir</c> → the mirror and the wip
    /// commit advanced the MIRROR's <c>refs/heads/main</c> with agent content, past the ref mediator and
    /// past the merge queue. Breaking one file was the whole exploit.</para>
    /// </summary>
    [Fact]
    public async Task RewrittenCommondir_IsRefused_AndTheMirrorsMainNeverMoves()
    {
        var env = new Fixture(_root);
        var mirrorMainBefore = Rev(env.Mirror, env.MainBranch);
        Assert.NotEqual(string.Empty, mirrorMainBefore);

        PlantCommondirBypass(env);

        var ex = Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, env.AgentRepo, env.Mirror));
        Assert.Contains("shared mirror", ex.Message, StringComparison.Ordinal);

        // …and the cycle that would have made the commit ends as a skip, before it yields anything.
        var rebaser = new KeepAliveRebaser(
            new ExplodingYieldProtocol(),
            _ => new AgentWorktreeLocation(env.Worktree, env.Mirror, env.MainBranch, env.AgentRepo));
        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Skipped, result.Kind);
        Assert.Equal(mirrorMainBefore, Rev(env.Mirror, env.MainBranch));
    }

    /// <summary>
    /// The measurement that says why blocker 2's fix could not just be "pin <c>GIT_COMMON_DIR</c>", and
    /// the one this suite would have wanted before the branch claimed the Critical closed.
    ///
    /// <para>git's files ref backend builds its common directory with <c>get_common_dir_noenv()</c>, so
    /// <c>GIT_COMMON_DIR</c> does not reach it — the <c>commondir</c> FILE does. With every pin correct
    /// and that one file rewritten, a daemon <c>commit</c> advances the MIRROR's branch. The test drives
    /// the layout by hand (bypassing the refusal above) to show the mechanism is real, so that a future
    /// change that drops <c>AssertCommondirFileAgrees</c> because "GIT_COMMON_DIR covers it" fails here
    /// with the reason.</para>
    /// </summary>
    [Fact]
    public void GitCommonDirEnv_DoesNotGovernRefWrites_WhichIsWhyCommondirIsValidated()
    {
        var env = new Fixture(_root);

        // The honest layout, captured BEFORE the payload — this is what the pin would have produced.
        var layout = TrustedWorktreeLayout.TryResolve(env.Worktree, env.AgentRepo, env.Mirror);
        Assert.NotNull(layout);

        var mirrorMainBefore = Rev(env.Mirror, env.MainBranch);
        PlantCommondirBypass(env);

        File.WriteAllText(Path.Combine(env.Worktree, "agent-work.txt"), "content the agent chose\n");
        AgentGitCommand.RunWithEnv(layout!.WorkTree, layout.Env, "add", "-A");
        AgentGitCommand.RunWithEnv(
            layout.WorkTree, layout.Env,
            "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "wip: sync");

        // The pin did NOT hold the refs: this is the vector, demonstrated.
        Assert.NotEqual(mirrorMainBefore, Rev(env.Mirror, env.MainBranch));
    }

    /// <summary>
    /// The same bypass against the weaker caller shape — no <c>agentRepoPath</c>, so the pointer is what
    /// selects the repository. A broken round-trip used to mean null, i.e. "run unpinned", i.e. follow the
    /// very chain this type exists not to follow. It is a refusal now: once <c>.git</c> is a FILE there is
    /// no null left in this method.
    /// </summary>
    [Fact]
    public void BlankedRegistration_IsRefused_RatherThanRunUnpinned()
    {
        var env = new Fixture(_root);
        PlantCommondirBypass(env);

        var ex = Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, agentRepoPath: null, forbiddenCommonDir: env.Mirror));
        Assert.Contains("does not register", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A `.git` FILE is a linked worktree, and a linked worktree either resolves or throws.
    /// Null on any of these was the fail-open channel the bypass was steered into.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("gitdir:")]
    [InlineData("not a pointer at all")]
    [InlineData("gitdir: /nowhere/in/particular")]
    public void AnyUnusablePointer_IsARefusal_NeverNull(string pointer)
    {
        var env = new Fixture(_root);
        File.WriteAllText(Path.Combine(env.Worktree, ".git"), pointer + "\n");

        Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, agentRepoPath: null, forbiddenCommonDir: env.Mirror));
        Assert.Throws<RepoProvisioningException>(
            () => TrustedWorktreeLayout.TryResolve(env.Worktree, env.AgentRepo, env.Mirror));
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

    /// <summary>
    /// The blocker-2 payload, written entirely inside the per-agent repository — the tree the jail mounts
    /// READ-WRITE — with the worktree's own <c>.git</c> pointer left exactly as git wrote it.
    /// </summary>
    private static void PlantCommondirBypass(Fixture env)
    {
        var registration = Path.Combine(
            env.AgentRepo, "worktrees", Path.GetFileName(env.Worktree));
        Assert.True(Directory.Exists(registration), registration);

        File.WriteAllText(Path.Combine(registration, "commondir"), env.Mirror + "\n");
        File.WriteAllText(Path.Combine(registration, "HEAD"), "ref: refs/heads/" + env.MainBranch + "\n");
        File.WriteAllText(Path.Combine(registration, "gitdir"), string.Empty);
    }

    private static string Rev(string gitDir, string reference)
    {
        var (code, output, _) = AgentTestGit.Run(gitDir, "rev-parse", "--verify", "--quiet", reference);
        return code == 0 ? output.Trim() : string.Empty;
    }

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
