using System;
using System.Collections.Generic;
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

        // NOT `File.Exists`. That assertion passed for the whole time this guard was inert inside the
        // jail — the file was there, mode 0755, and git skipped it — while the refusal below failed. A
        // present file is not an armed guard, and asserting the former while believing the latter is the
        // entire defect this test now has to be able to see.
        AssertGuardIsArmed(env, hash);

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

        // This test asserts that git is NOT blocked — which an inert hook satisfies perfectly. Inside the
        // jail it therefore went green while measuring nothing at all, three times over. The arming
        // precondition is what makes a pass mean "the exemptions are right" rather than "the guard never
        // ran".
        AssertGuardIsArmed(env, hash);

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

        // Same vacuity trap as above: pack-refs returning 0 proves the no-op exemption only if the hook
        // it is exempted by can actually run.
        AssertGuardIsArmed(env, hash);

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

    // ---- the arming measurement (#68) ------------------------------------
    //
    // The in-jail failure: git 2.39.5 (well past the 2.28 that added `reference-transaction`), the hook
    // present at mode 0755, and `checkout -b` returning 0. git decides a hook exists with
    // `access(path, X_OK)`, which answers EACCES on a mount flagged `noexec` whatever the bits say; git
    // then prints "the hook was ignored because it's not set as executable" as a HINT and carries on.
    // The jail's /tmp is a Docker tmpfs, and Docker's default tmpfs flags are nosuid,nodev,noexec.

    [LinuxOnlyFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheArmingMeasurement_AnswersNo_ForAHookGitWouldSkip()
    {
        // Real files on a real filesystem — no seam. Every case is a shape a written-and-chmodded hook
        // actually takes in production, and none of them is detectable by looking at mode bits.
        using var env = new WorktreeEnv();
        var dir = Directory.CreateDirectory(Path.Combine(env.VmRoot, "arming")).FullName;

        // The control first: an ordinary hook on an ordinary VM root arms. If this ever fails, the
        // measurement is refusing everything and the three refusals below would prove nothing.
        var executable = Path.Combine(dir, "executable");
        File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
        MakeExecutable(executable);
        Assert.Null(AgentBranchGuard.MeasureHookCanRun(executable));

        // `bad interpreter` — the CRLF trap this file has warned about since it was written. A mode
        // check cannot see it; running the thing can.
        var crlf = Path.Combine(dir, "crlf");
        File.WriteAllText(crlf, "#!/bin/sh\r\nexit 0\r\n");
        MakeExecutable(crlf);
        Assert.NotNull(AgentBranchGuard.MeasureHookCanRun(crlf));

        // Executable, well-formed, and still unrunnable — the interpreter is gone.
        var noInterpreter = Path.Combine(dir, "no-interpreter");
        File.WriteAllText(noInterpreter, "#!/nonexistent/mainguard-probe-sh\nexit 0\n");
        MakeExecutable(noInterpreter);
        Assert.NotNull(AgentBranchGuard.MeasureHookCanRun(noInterpreter));

        Assert.NotNull(AgentBranchGuard.MeasureHookCanRun(Path.Combine(dir, "absent")));

        // The closest unprivileged analogue of the `noexec` mount that caused #68: git's predicate is
        // `access(X_OK)`, and clearing the bit is the other way to make it answer EACCES. Conditional
        // ONLY because some filesystems a developer may put the VM root on (WSL's DrvFs, exFAT) ignore
        // chmod entirely and report 0777 back — there the case is not weakly true, it is unstateable.
        // The four unconditional assertions above are what carry this test everywhere.
        var noExecBit = Path.Combine(dir, "no-exec-bit");
        File.WriteAllText(noExecBit, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(noExecBit, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (!File.GetUnixFileMode(noExecBit).HasFlag(UnixFileMode.UserExecute))
        {
            Assert.NotNull(AgentBranchGuard.MeasureHookCanRun(noExecBit));
        }
    }

    [LinuxOnlyFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheHarnessItself_PutsAgentRepositoriesWhereHooksCanRun()
    {
        // #68 was not a bug in the guard — it was a bug in WHERE this suite built its repositories. The
        // VM root came from Path.GetTempPath(), and inside the jail that is a Docker tmpfs mounted
        // `noexec`, so git skipped every hook the suite installed. One test failed and three passed
        // while measuring nothing. This asserts the harness's own precondition directly, so the next
        // time it breaks the failure names the harness instead of appearing as a mysterious guard
        // regression.
        using var env = new WorktreeEnv();
        var probe = Path.Combine(env.VmRoot, "harness-probe");
        File.WriteAllText(probe, "#!/bin/sh\nexit 0\n");
        MakeExecutable(probe);

        var reason = AgentBranchGuard.MeasureHookCanRun(probe);
        Assert.True(
            reason is null,
            $"the test VM root '{env.VmRoot}' cannot execute scripts ({reason}), so every hook this "
            + "suite installs would be silently skipped by git. Set MAINGUARD_TEST_VM_ROOT to a "
            + "directory on a filesystem that permits execution.");
    }

    [Fact]
    public void AGuardThatCannotFire_IsReported_AndDoesNotClaimToBeInstalled()
    {
        // The house rule: a control that knows it is inert must not look armed. The probe is overridden
        // because an unprivileged test cannot mount `noexec`; what is being proved here is the REPORTING,
        // and the detection it depends on is pinned against real files by the test above.
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var repo = env.Worktrees.AgentRepoPathFor(hash, AgentId);
        Directory.CreateDirectory(repo);

        var warnings = new List<string>();
        var armed = AgentBranchGuard.InstallHook(
            repo, AgentId, warnings.Add, armingProbe: _ => "Permission denied");

        Assert.False(armed, "a hook that cannot fire must not be reported as installed");

        // The file IS written — that is the point. Nothing about the observable file says it is inert,
        // which is exactly why the warning has to.
        Assert.True(File.Exists(Path.Combine(repo, "hooks", AgentBranchGuard.HookName)));

        var warning = Assert.Single(warnings);
        Assert.Contains("NOT ARMED", warning, StringComparison.Ordinal);
        Assert.Contains(AgentId, warning, StringComparison.Ordinal);
        Assert.Contains("Permission denied", warning, StringComparison.Ordinal);
        Assert.Contains("noexec", warning, StringComparison.Ordinal);
        // It must not leave the reader thinking nothing is watching: layer 3 still is.
        Assert.Contains("verification", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnArmedGuard_IsReportedAsInstalled_AndWarnsAboutNothing()
    {
        // The control. Without it, an InstallHook hard-wired to "not armed" would pass the test above and
        // fill every ordinary spawn with a false alarm — the same shape of mistake in the other direction.
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var warnings = new List<string>();
        var armed = AgentBranchGuard.InstallHook(
            env.Worktrees.AgentRepoPathFor(hash, AgentId), AgentId, warnings.Add);

        Assert.True(armed, $"the guard must arm on an ordinary VM root: {string.Join(" | ", warnings)}");
        Assert.Empty(warnings);
    }

    // ---- harness ---------------------------------------------------------

    /// <summary>
    /// The precondition every layer-2 test needs and none of them had: git can actually RUN the hook that
    /// was installed. Distinct from <c>File.Exists</c> on purpose — see #68.
    /// </summary>
    private static void AssertGuardIsArmed(WorktreeEnv env, string hash)
    {
        var hookPath = HookPath(env, hash);
        Assert.True(File.Exists(hookPath), "the spawn must install the branch guard hook");

        var reason = AgentBranchGuard.MeasureHookCanRun(hookPath);
        Assert.True(
            reason is null,
            $"the branch guard hook at '{hookPath}' exists but git cannot run it ({reason}), so this test "
            + "would measure nothing. On a `noexec` filesystem git skips the hook with a hint and "
            + "`checkout -b` succeeds — put the VM root somewhere execution is permitted.");
    }

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

        /// <summary>The VM root itself — needed by the arming tests, which write probe files beside the
        /// repositories precisely so they land on the same filesystem the guard will.</summary>
        public string VmRoot => _vmRoot;

        public string Provision() => Repos.Provision(Fixture.WorkRepoPath).RepoHash;

        public void Dispose()
        {
            Fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
