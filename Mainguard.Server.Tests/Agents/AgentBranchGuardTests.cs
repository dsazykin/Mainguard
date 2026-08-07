using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The stranded-branch defect, over a REAL mirror, a REAL per-agent repository and a REAL linked
/// worktree.
///
/// <para><b>The shape being reproduced was measured on the owner's machine, not imagined:</b> the
/// worktree is created on <c>agent/&lt;id&gt;</c>, the agent runs <c>git checkout -b
/// add-subtract-function</c> and commits there, and <c>refs/heads/agent/&lt;id&gt;</c> stays exactly
/// where the daemon seeded it. Nothing failed, nothing warned, and the work could never reach the merge
/// queue.</para>
///
/// <para><b>Why the agent's git is spawned through <see cref="AgentTestGit"/> rather than the daemon's
/// own helper.</b> Every daemon-side git runs with <c>-c core.hooksPath=/dev/null</c>
/// (<see cref="AgentGitCommand"/>), so driving the "agent" through that helper would disable the very
/// hook these tests exist to exercise — a test that could only ever pass. <see cref="AgentTestGit.Run"/>
/// is a plain <c>git</c> spawn with hooks live, which is what the agent inside the jail actually has.</para>
/// </summary>
public sealed class AgentBranchGuardTests
{
    private const string AgentId = "ef9fe0bd3390433193896eca5e46145e";
    private const string StrandedBranch = "add-subtract-function";

    // ---- layer 3: detection ---------------------------------------------

    [Fact]
    public void AgentThatCommittedOnAnotherBranch_IsDetected_AndTheReportNamesBothBranches()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        // The agent strands its work exactly as it did on the owner's machine. Hooks are bypassed so this
        // test measures DETECTION on its own — a jail created before this change has no hook at all, and
        // that is precisely the population the backstop exists for.
        StrandWorkOffTheAgentBranch(worktree);

        var alignment = env.Worktrees.CheckAgentBranch(hash, AgentId);

        Assert.Equal(AgentBranchAlignmentState.OnAnotherBranch, alignment.State);
        Assert.True(alignment.Drifted);
        Assert.Equal(StrandedBranch, alignment.ActualBranch);
        Assert.Equal("agent/" + AgentId, alignment.ExpectedBranch);

        // The measured shas differ — this is the same fact the owner had to discover by hand.
        Assert.NotNull(alignment.HeadSha);
        Assert.NotNull(alignment.AgentBranchSha);
        Assert.NotEqual(alignment.AgentBranchSha, alignment.HeadSha);

        // The report has to carry a cause, not just a symptom: the branch that was found, the branch that
        // was expected, and a recovery that was actually computed rather than assumed.
        var report = alignment.Describe(AgentId);
        Assert.Contains(StrandedBranch, report, StringComparison.Ordinal);
        Assert.Contains("agent/" + AgentId, report, StringComparison.Ordinal);
        Assert.Contains("merge queue", report, StringComparison.Ordinal);
        Assert.True(alignment.AgentBranchIsAncestorOfHead);
        Assert.Contains("git branch -f", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentOnItsOwnBranch_IsNotReportedAsDrifted()
    {
        // The control. Without it, a probe hard-wired to "drifted" would pass the test above and quietly
        // refuse every honest verification in the product.
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "work\n");
        AgentTestGit.RunChecked(worktree, "add", "feature.txt");
        CommitAsAgent(worktree, "work on the agent's own branch");

        var alignment = env.Worktrees.CheckAgentBranch(hash, AgentId);

        Assert.Equal(AgentBranchAlignmentState.OnAgentBranch, alignment.State);
        Assert.False(alignment.Drifted);
    }

    [Fact]
    public void DetachedHead_IsReportedAsDrift_AndNotMistakenForABranch()
    {
        // `rev-parse --abbrev-ref HEAD` answers the literal string "HEAD" here, which is why the probe
        // asks `symbolic-ref` instead. A probe that used the former would report a branch named "HEAD".
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        AgentTestGit.RunChecked(worktree, "checkout", "--detach", "HEAD");
        File.WriteAllText(Path.Combine(worktree, "detached.txt"), "work\n");
        AgentTestGit.RunChecked(worktree, "add", "detached.txt");
        CommitAsAgent(worktree, "work on a detached HEAD");

        var alignment = env.Worktrees.CheckAgentBranch(hash, AgentId);

        Assert.Equal(AgentBranchAlignmentState.DetachedHead, alignment.State);
        Assert.True(alignment.Drifted);
        Assert.Null(alignment.ActualBranch);
        Assert.Contains("DETACHED HEAD", alignment.Describe(AgentId), StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeWithNoWorktree_ReportsUnknown_NeverAlignment()
    {
        // Unknown must never collapse into "fine". An unreadable answer read as alignment is the same
        // class of defect as the silent no-op this whole change is fixing.
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var alignment = env.Worktrees.CheckAgentBranch(hash, "never-spawned");

        Assert.Equal(AgentBranchAlignmentState.Unknown, alignment.State);
        Assert.False(alignment.Drifted);
    }

    // ---- layer 2: in-jail prevention (ergonomics, NOT security) ----------

    [Fact]
    public void SpawnInstallsTheHook_AndItRefusesABranchTheAgentTriesToCreate()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        Assert.True(File.Exists(HookPath(env, hash)), "the spawn must install the branch guard hook");

        var (code, _, stderr) = AgentTestGit.Run(worktree, "checkout", "-b", StrandedBranch);

        Assert.NotEqual(0, code);
        Assert.Contains("mainguard", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent/" + AgentId, stderr, StringComparison.Ordinal);

        // Refused, and with no residue: HEAD is still the agent's branch and the branch was never created.
        Assert.Equal(
            "refs/heads/agent/" + AgentId,
            AgentTestGit.RunChecked(worktree, "symbolic-ref", "HEAD").Trim());
        Assert.DoesNotContain(
            StrandedBranch,
            AgentTestGit.RunChecked(worktree, "for-each-ref", "--format=%(refname)", "refs/heads"),
            StringComparison.Ordinal);

        // ...and the agent is still able to do its job on the branch that counts.
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "work\n");
        AgentTestGit.RunChecked(worktree, "add", "feature.txt");
        CommitAsAgent(worktree, "work after the refusal");
        Assert.Equal(AgentBranchAlignmentState.OnAgentBranch, env.Worktrees.CheckAgentBranch(hash, AgentId).State);
    }

    [Theory]
    // Each of these was measured to produce refs/heads or pseudo-ref transactions that a naive
    // "reject everything that is not my branch" hook refuses. A guard that breaks ordinary git is a
    // guard the next person deletes, so the exemptions are asserted rather than assumed.
    [InlineData("stash", new[] { "stash" })]
    [InlineData("tag", new[] { "tag", "v1" })]
    [InlineData("pack-refs", new[] { "pack-refs", "--all" })]
    [InlineData("gc", new[] { "gc", "--prune=now", "--quiet" })]
    [InlineData("detach", new[] { "checkout", "--detach", "HEAD" })]
    public void TheHookLeavesOrdinaryGitAlone(string _, string[] args)
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        // `git stash` needs something to stash, and pack-refs/gc need a loose ref worth packing.
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "work\n");
        AgentTestGit.RunChecked(worktree, "add", "feature.txt");
        CommitAsAgent(worktree, "work on the agent's own branch");
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "dirty\n");

        var (code, _, stderr) = AgentTestGit.Run(worktree, args);

        Assert.True(code == 0, $"git {string.Join(' ', args)} must not be blocked by the guard: {stderr}");
    }

    [Fact]
    public void TheHookSurvivesAnUpgradeOverAJailThatIsALREADYStranded()
    {
        // The population this ships to. A pre-existing jail carries a LOOSE foreign branch, and
        // `git pack-refs` re-states every loose ref as a create (0000… -> sha) — indistinguishable from a
        // real branch creation by name and shas alone. Without the hook's no-op exemption, installing the
        // guard would break `git pack-refs` and `git gc` in exactly the repositories that already have the
        // problem.
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, AgentId);

        // Simulate "created before this change": strand the work with the hook removed, then restore it.
        var hookPath = HookPath(env, hash);
        var hook = File.ReadAllText(hookPath);
        File.Delete(hookPath);
        StrandWorkOffTheAgentBranch(worktree);
        File.WriteAllText(hookPath, hook);
        MakeExecutable(hookPath);

        var (code, _, stderr) = AgentTestGit.Run(worktree, "pack-refs", "--all");

        Assert.True(code == 0, $"pack-refs must still work over a pre-existing stranded branch: {stderr}");

        // ...and the drift is still detected afterwards, which is the whole point of the backstop.
        Assert.True(env.Worktrees.CheckAgentBranch(hash, AgentId).Drifted);
    }

    [LinuxOnlyFact]
    // The attribute already skips on Windows; the annotation is what tells the CA1416 analyzer so.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheHookIsReadableByTheJail_ButNotGroupWritable()
    {
        // MG-17: the jail is a different host uid that meets the daemon through the shared group, so the
        // hook must be group-readable and group-executable or it never runs. It must NOT be group-writable:
        // the agent should not be able to edit the guard rail in place. It CAN still delete it — the
        // enclosing repository has to stay group-writable for git to work — which is the concrete reason
        // this layer is documented as ergonomics rather than a boundary. Asserted because the install runs
        // after two GroupShareRecursive passes that set g+rwX on everything they touch.
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, AgentId);

        var mode = File.GetUnixFileMode(HookPath(env, hash));

        Assert.True(mode.HasFlag(UnixFileMode.GroupRead), "the jail's group must be able to read the hook");
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), "the jail's group must be able to execute the hook");
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), "the agent must not be able to edit the hook in place");
    }

    [Fact]
    public void TheHookDoesNotObstructTheDaemonsOwnGit()
    {
        // The daemon pins core.hooksPath=/dev/null on every invocation, so its housekeeping — the branch
        // the worktree is created on, the mediated publish, teardown's `branch -D` — is unaffected with no
        // exemption in the hook. If that ever stopped being true, spawn and teardown would start failing
        // on the guard rail rather than on anything real.
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, AgentId);

        // Creating the worktree already wrote refs/heads/agent/<id>; removal deletes it and prunes, all
        // through the daemon's hardened helper, with the hook present on disk throughout.
        Assert.True(File.Exists(HookPath(env, hash)));
        env.Worktrees.RemoveAgentWorktree(hash, AgentId, force: true);

        Assert.False(Directory.Exists(env.Worktrees.WorktreePathFor(hash, AgentId)));
    }

    // ---- harness ---------------------------------------------------------

    private static string HookPath(WorktreeEnv env, string hash)
        => Path.Combine(env.Worktrees.AgentRepoPathFor(hash, AgentId), "hooks", AgentBranchGuard.HookName);

    /// <summary>
    /// The measured defect: switch to a new branch and commit there. The hook is bypassed with
    /// <c>-c core.hooksPath=/dev/null</c> — the same bypass an agent has, which is exactly why layer 2 is
    /// documented as ergonomics and layer 3 exists.
    /// </summary>
    private static void StrandWorkOffTheAgentBranch(string worktree)
    {
        AgentTestGit.RunChecked(worktree, "-c", "core.hooksPath=/dev/null", "checkout", "-b", StrandedBranch);
        File.WriteAllText(Path.Combine(worktree, "subtract.txt"), "def subtract(a, b): return a - b\n");
        AgentTestGit.RunChecked(worktree, "add", "subtract.txt");
        AgentTestGit.RunChecked(
            worktree, "-c", "core.hooksPath=/dev/null", "-c", "user.name=agent",
            "-c", "user.email=agent@mainguard.local", "commit", "-m", "Add subtract function");
    }

    private static void CommitAsAgent(string worktree, string message)
        => AgentTestGit.RunChecked(
            worktree, "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local",
            "commit", "-m", message);

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed class WorktreeEnv : IDisposable
    {
        private readonly string _vmRoot;

        public WorktreeEnv()
        {
            Fixture = new DualRepoFixture();
            _vmRoot = AgentTestGit.NewVmRoot();
            Repos = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot);
        }

        public DualRepoFixture Fixture { get; }
        public RepoProvisioner Repos { get; }
        public WorktreeManager Worktrees { get; }

        public string Provision() => Repos.Provision(Fixture.WorkRepoPath).RepoHash;

        public void Dispose()
        {
            Fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
