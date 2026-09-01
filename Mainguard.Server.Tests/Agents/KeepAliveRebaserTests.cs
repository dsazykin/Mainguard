using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-09 tests 3, 4, 5, 9 on a real worktree over the <see cref="DualRepoFixture"/> (real git, no
/// Docker): clean rebase onto advanced main + wip commit + resume; agent mid-its-own-rebase → guard
/// skip then next-cycle success; induced conflict → status <see cref="AgentRunState.Conflict"/> routed
/// to the T-04 resolver with the rebase left in progress; and the invariant-1 proof that a human's
/// edits reach the worktree only via Git.
/// </summary>
public sealed class KeepAliveRebaserTests
{
    [Fact]
    public async Task KeepAlive_CleanRebase_CommitsWip_ReparentsOntoMain_AndResumes()
    {
        using var env = new RebaseEnv();

        // Agent makes a dirty (uncommitted) change in its worktree.
        File.WriteAllText(Path.Combine(env.Worktree, "agent.txt"), "agent work\n");

        // Human advances main with a different file.
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        var yield = new FakeYieldProtocol();
        var states = new List<AgentRunState>();
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location, (_, s) => states.Add(s));

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Rebased, result.Kind);
        Assert.True(result.WipCommitCreated);
        // Both the agent's now-committed file and the human's rebased-in file are present.
        Assert.True(File.Exists(Path.Combine(env.Worktree, "agent.txt")));
        Assert.True(File.Exists(Path.Combine(env.Worktree, "human.txt")));
        // The wip commit exists on the branch and main is now an ancestor (reparented).
        Assert.Contains("wip: sync", AgentTestGit.RunChecked(env.Worktree, "log", "--oneline"));
        Assert.Equal(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MirrorMainSha, "HEAD").Code);
        // Agent was resumed (token released), never left in Conflict.
        Assert.True(yield.LastToken!.Resumed);
        Assert.Contains(AgentRunState.Working, states);
        Assert.DoesNotContain(AgentRunState.Conflict, states);
    }

    [Fact]
    public async Task KeepAlive_AgentMidOwnRebase_Skips_ThenNextCycleSucceeds()
    {
        using var env = new RebaseEnv();
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        // Simulate the agent being mid its own rebase: a rebase-merge dir in the worktree's gitdir.
        var rebaseMergeDir = Path.Combine(env.WorktreeGitDir, "rebase-merge");
        Directory.CreateDirectory(rebaseMergeDir);

        var yield = new FakeYieldProtocol();
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location);

        var skipped = await rebaser.RunCycleAsync("a1");
        Assert.Equal(RebaseCycleKind.Skipped, skipped.Kind);
        // No mutation: main is not yet an ancestor of the untouched agent branch.
        Assert.NotEqual(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MirrorMainSha, "HEAD").Code);
        Assert.True(yield.LastToken!.Resumed); // resumed so the agent finishes its own rebase

        // The agent finishes its rebase; the next cycle succeeds.
        Directory.Delete(rebaseMergeDir, recursive: true);
        var second = await rebaser.RunCycleAsync("a1");
        Assert.Equal(RebaseCycleKind.Rebased, second.Kind);
        Assert.Equal(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MirrorMainSha, "HEAD").Code);
    }

    /// <summary>
    /// <b>K6/§23.6 — the guard's verdict is a snapshot, and this is the window it was blind to.</b>
    ///
    /// <para><c>CanMutate</c> reads the worktree once, at the top of the cycle; the mutations then run
    /// after an <c>index.lock</c> backoff that re-checked only the lock — which was never one of the three
    /// preconditions the verdict was made of. So an agent that started its OWN rebase after the guard
    /// looked, and before the daemon acted, got <c>git add -A; git commit</c> and <c>git rebase main</c>
    /// run against a worktree the guard would have refused.</para>
    ///
    /// <para>The window is opened here exactly where it is in production: the <c>Rebasing</c> state is set
    /// after the guard and before the first mutation, so the state callback is the honest place to make
    /// the worktree change underneath. Nothing about the cycle is stubbed.</para>
    /// </summary>
    [Fact]
    public async Task KeepAlive_AgentStartsItsOwnRebase_AfterTheGuardLooked_MutatesNothing()
    {
        using var env = new RebaseEnv();

        // The agent has uncommitted work, so the wip-commit leg runs first — the earliest mutation there
        // is, and therefore the one that proves the re-check happens before ANY of them.
        File.WriteAllText(Path.Combine(env.Worktree, "agent.txt"), "agent work\n");
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        var rebaseMergeDir = Path.Combine(env.WorktreeGitDir, "rebase-merge");
        var yield = new FakeYieldProtocol();
        var states = new List<AgentRunState>();
        var opened = false; // once — the second cycle is the control and must find a quiescent worktree.
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location, (_, s) =>
        {
            states.Add(s);
            // The agent starts its own rebase in the window between the guard's read and the mutation.
            if (s == AgentRunState.Rebasing && !opened)
            {
                opened = true;
                Directory.CreateDirectory(rebaseMergeDir);
            }
        });

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Skipped, result.Kind);
        Assert.Contains("mid-rebase", result.Detail ?? "", StringComparison.Ordinal);

        // NOTHING was mutated: no wip commit, and the branch was not reparented.
        Assert.False(result.WipCommitCreated);
        Assert.DoesNotContain("wip: sync", AgentTestGit.RunChecked(env.Worktree, "log", "--oneline"));
        Assert.NotEqual(0,
            AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MirrorMainSha, "HEAD").Code);
        // The agent is resumed so it can finish its own rebase; the next cycle retries.
        Assert.True(yield.LastToken!.Resumed);
        Assert.DoesNotContain(AgentRunState.Conflict, states);

        // The control: once the agent's rebase is done, the same cycle does the work.
        Directory.Delete(rebaseMergeDir, recursive: true);
        var second = await rebaser.RunCycleAsync("a1");
        Assert.Equal(RebaseCycleKind.Rebased, second.Kind);
        Assert.True(second.WipCommitCreated);
    }

    [Fact]
    public async Task KeepAlive_Conflict_SetsStatusConflict_RoutesToResolver_LeavesRebaseInProgress()
    {
        using var env = new RebaseEnv();

        // Agent commits a change to a shared file on its branch.
        AgentTestGit.SetIdentity(env.Worktree);
        File.WriteAllText(Path.Combine(env.Worktree, "shared.txt"), "agent version\n");
        AgentTestGit.RunChecked(env.Worktree, "add", "shared.txt");
        AgentTestGit.RunChecked(env.Worktree, "commit", "-m", "agent edits shared");

        // Human commits a CONFLICTING change to the same file on main.
        env.AdvanceMain("shared.txt", "human version\n", "human edits shared");

        var yield = new FakeYieldProtocol();
        var states = new List<AgentRunState>();
        ConflictHandoff? handoff = null;
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location, (_, s) => states.Add(s), h => handoff = h);

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Conflict, result.Kind);
        Assert.Contains(AgentRunState.Conflict, states);
        // Routed to the T-04 resolver against the worktree path.
        Assert.NotNull(handoff);
        Assert.Equal(env.Worktree, handoff!.WorktreePath);
        // The rebase is LEFT in progress (no automatic abort) for the resolver.
        Assert.True(Directory.Exists(Path.Combine(env.WorktreeGitDir, "rebase-merge")));
        // PTY stays paused: the token was NOT resumed.
        Assert.False(yield.LastToken!.Resumed);
        // ...but it WAS settled. See the ledger test below for what that buys.
        Assert.True(yield.LastToken.ReleasedWithoutResuming);
    }

    /// <summary>
    /// <b>The human's escape hatch has to keep working on a conflicted agent.</b>
    ///
    /// <para>The conflict arm deliberately leaves the jail frozen, and it used to do that by simply never
    /// settling the yield token — which leaked the <c>HumanPauseLedger</c> machine hold that
    /// <c>YieldProtocol</c> takes on the pause path, because the hold was released inside the resume.
    /// <c>AgentPauseService.UnpauseAsync</c> refuses while a hold is outstanding, and it refuses with "the
    /// daemon is briefly holding this agent for a queue update — try again in a moment": a sentence whose
    /// whole promise is that it self-clears. On a conflicted agent it never did. So the one control a
    /// human had left on a parked jail was permanently refused, by a message telling them to wait.</para>
    ///
    /// <para>This is the real <see cref="YieldProtocol"/> over the real <see cref="HumanPauseLedger"/>, and
    /// it asserts the exact predicate the unpause RPC gates on. It also asserts the jail is still frozen —
    /// releasing the hold must hand back the CLAIM, never the pause.</para>
    /// </summary>
    [Fact]
    public async Task KeepAlive_Conflict_ReleasesTheMachineHold_SoAHumansUnpauseIsNotRefusedForever()
    {
        using var env = new RebaseEnv();

        AgentTestGit.SetIdentity(env.Worktree);
        File.WriteAllText(Path.Combine(env.Worktree, "shared.txt"), "agent version\n");
        AgentTestGit.RunChecked(env.Worktree, "add", "shared.txt");
        AgentTestGit.RunChecked(env.Worktree, "commit", "-m", "agent edits shared");
        env.AdvanceMain("shared.txt", "human version\n", "human edits shared");

        var ledger = new HumanPauseLedger();
        var sandbox = new PauseCountingSandbox();
        // The daemon's own composition: no cooperative transport exists, so every yield takes the
        // docker-pause path — which is the path that takes the hold.
        var yield = new YieldProtocol(
            channelFor: _ => UnboundAgentControlChannel.Instance,
            sandbox: sandbox,
            containerIdFor: _ => "container-1",
            arbiter: ledger);
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location);

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Conflict, result.Kind);
        // The jail IS still frozen — that part is deliberate and must not change.
        Assert.Equal(1, sandbox.PauseCount);
        Assert.Equal(0, sandbox.UnpauseCount);
        // ...and the machine has let go of its claim on that pause, so the human's unpause reaches its
        // own logic instead of being turned away by a refusal that never clears.
        Assert.False(ledger.HasMachineHold("a1"),
            "the conflict arm leaked the machine hold — a human unpause is refused forever");
    }

    /// <summary>The same leak on the other never-resumed path: a kill switch that fires mid-cycle. The
    /// jail stays frozen until the operator resumes the queue, and the ledger must not be left claiming
    /// the daemon is mid-update for the rest of the process's life.</summary>
    [Fact]
    public async Task KeepAlive_KillSwitchMidCycle_AlsoReleasesTheMachineHold()
    {
        using var env = new RebaseEnv();
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        var ledger = new HumanPauseLedger();
        var sandbox = new PauseCountingSandbox();
        var killGate = new KillSwitchGate();
        var yield = new YieldProtocol(
            channelFor: _ => UnboundAgentControlChannel.Instance,
            sandbox: sandbox,
            containerIdFor: _ => "container-1",
            arbiter: ledger);
        // Frozen only AFTER the cycle starts — the start-of-cycle gate check cannot cover this race, which
        // is the whole reason the finally re-reads the gate.
        var rebaser = new KeepAliveRebaser(
            yield, _ => { killGate.Freeze(); return env.Location; }, killGate: killGate);

        await rebaser.RunCycleAsync("a1");

        Assert.Equal(0, sandbox.UnpauseCount); // the kill wins: the jail stays frozen
        Assert.False(ledger.HasMachineHold("a1"));
    }

    /// <summary>Counts freezes. The engine is otherwise unused — no jail is really spawned here.</summary>
    private sealed class PauseCountingSandbox : Mainguard.Agents.Agents.Sandbox.ISandboxEngine
    {
        public int PauseCount { get; private set; }

        public int UnpauseCount { get; private set; }

        public Task<Mainguard.Agents.Agents.Sandbox.SandboxHandle> SpawnAsync(
            Mainguard.Agents.Agents.Sandbox.SandboxSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Mainguard.Agents.Agents.Sandbox.SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            UnpauseCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task HumanEdits_ReachWorktreeOnlyViaGit()
    {
        using var env = new RebaseEnv();

        // An uncommitted change on the Windows side must NOT reach the worktree (no file sync).
        File.WriteAllText(Path.Combine(env.WorkRepo, "uncommitted.txt"), "not committed\n");

        // A committed change advances main and DOES reach the worktree via the rebase.
        env.AdvanceMain("committed.txt", "committed\n", "human commit");

        var rebaser = new KeepAliveRebaser(new FakeYieldProtocol(), _ => env.Location);
        await rebaser.RunCycleAsync("a1");

        Assert.False(File.Exists(Path.Combine(env.Worktree, "uncommitted.txt")));
        Assert.True(File.Exists(Path.Combine(env.Worktree, "committed.txt")));
    }

    // ---- MG-39(b) — the keep-alive cycle must never undo a kill ----

    /// <summary>
    /// Every non-conflict path of a cycle ends in <c>token.Resume()</c> → <c>ISandboxEngine.UnpauseAsync</c>.
    /// A rebaser blind to the kill-switch gate would therefore let a background tick <c>docker unpause</c> the
    /// very jail the operator just froze. Frozen ⇒ the cycle refuses to start at all: no yield is requested,
    /// so nothing can be resumed, and the worktree is left untouched.
    /// </summary>
    [Fact]
    public async Task KeepAlive_WhileKillSwitchFrozen_RefusesToRun_AndNeverTouchesTheAgent()
    {
        using var env = new RebaseEnv();
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        var gate = new KillSwitchGate();
        gate.Freeze();

        var yield = new FakeYieldProtocol();
        var states = new List<AgentRunState>();
        var rebaser = new KeepAliveRebaser(yield, _ => env.Location, (_, s) => states.Add(s), killGate: gate);

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Skipped, result.Kind);
        Assert.Null(yield.LastToken); // no yield token was ever taken → no unpause path exists
        Assert.Empty(states);         // the agent's state was not touched either
        // No mutation: main is still not an ancestor of the untouched agent branch.
        Assert.NotEqual(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MirrorMainSha, "HEAD").Code);

        // Once the operator resumes the kill switch, the very next cycle works normally.
        gate.Resume();
        var after = await rebaser.RunCycleAsync("a1");
        Assert.Equal(RebaseCycleKind.Rebased, after.Kind);
    }

    /// <summary>
    /// The start-of-cycle gate check cannot cover a kill that fires <i>while</i> the cycle runs — the kill's
    /// pause and the cycle's resume would race, and last-writer-wins could leave a killed jail running. The
    /// gate is therefore re-read before the resume, so the kill wins by construction.
    /// </summary>
    [Fact]
    public async Task KeepAlive_KillFiresMidCycle_LeavesTheAgentPaused()
    {
        using var env = new RebaseEnv();
        env.AdvanceMain("human.txt", "human work\n", "human commit on main");

        var gate = new KillSwitchGate();
        var yield = new FakeYieldProtocol();
        // Engage the kill switch exactly while the cycle is mid-rebase (the widest window).
        var rebaser = new KeepAliveRebaser(
            yield, _ => env.Location,
            (_, s) => { if (s == AgentRunState.Rebasing) gate.Freeze(); },
            killGate: gate);

        var result = await rebaser.RunCycleAsync("a1");

        Assert.Equal(RebaseCycleKind.Rebased, result.Kind); // the cycle itself completed
        Assert.False(yield.LastToken!.Resumed, "a kill fired mid-cycle but the rebaser still resumed the agent");
    }

    /// <summary>A real provisioned mirror + agent worktree over the DualRepoFixture; advances main via re-provision.</summary>
    private sealed class RebaseEnv : IDisposable
    {
        private readonly DualRepoFixture _fixture = new();
        private readonly string _vmRoot = AgentTestGit.NewVmRoot();
        private readonly RepoProvisioner _provisioner;
        private readonly string _hash;

        public RebaseEnv()
        {
            _provisioner = new RepoProvisioner(_vmRoot);
            _hash = _provisioner.Provision(_fixture.WorkRepoPath).RepoHash;
            var worktrees = new WorktreeManager(_vmRoot);
            Worktree = worktrees.CreateAgentWorktree(_hash, "a1");
            var bare = Path.Combine(_vmRoot, "repos", _hash + ".git");
            // The mirror's default branch (libgit2 seeds "master"); rebase onto whatever it actually is.
            var mainBranch = AgentTestGit.RunChecked(bare, "symbolic-ref", "--short", "HEAD").Trim();
            Location = new AgentWorktreeLocation(Worktree, bare, mainBranch);
            WorktreeGitDir = ResolveWorktreeGitDir(Worktree);
        }

        public string WorkRepo => _fixture.WorkRepoPath;

        public string Worktree { get; }

        public string WorktreeGitDir { get; }

        public AgentWorktreeLocation Location { get; }

        public string MainBranch => Location.MainBranch;

        /// <summary>
        /// The MIRROR's current main commit, by sha.
        ///
        /// <para>These assertions used to name the branch (<c>merge-base --is-ancestor main HEAD</c>),
        /// which resolved <c>main</c> in whatever repository the worktree belonged to. Under MG-3 the
        /// worktree hangs off the agent's OWN repository, which holds its own copy of main frozen at
        /// spawn — so a branch-name assertion silently starts measuring the stale ref and reads
        /// "unmutated" as "already up to date". Naming the sha makes the question unambiguous: is the
        /// commit the human actually pushed reachable from the agent's HEAD? The worktree can always
        /// resolve it — that is exactly what the alternate to the mirror is for.</para>
        /// </summary>
        public string MirrorMainSha =>
            AgentTestGit.RunChecked(Location.BarePath, "rev-parse", Location.MainBranch).Trim();

        /// <summary>Commits a file on the Windows-side work repo and re-provisions so the mirror's main advances.</summary>
        public void AdvanceMain(string relPath, string content, string message)
        {
            _fixture.Commit(relPath, content, message);
            _provisioner.Provision(_fixture.WorkRepoPath); // incremental fetch advances refs/heads/main in the mirror
        }

        private static string ResolveWorktreeGitDir(string worktreePath)
        {
            var dotGit = Path.Combine(worktreePath, ".git");
            if (Directory.Exists(dotGit))
            {
                return dotGit;
            }

            foreach (var line in File.ReadAllLines(dotGit))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = trimmed["gitdir:".Length..].Trim();
                    return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(worktreePath, target));
                }
            }

            return dotGit;
        }

        public void Dispose()
        {
            _fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }

    /// <summary>A no-op cooperative-yield protocol: always yields ready with a resumable in-memory token.</summary>
    private sealed class FakeYieldProtocol : IYieldProtocol
    {
        public FakeToken? LastToken { get; private set; }

        public Task<IYieldToken> RequestYieldAsync(string agentId, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            LastToken = new FakeToken(agentId);
            return Task.FromResult<IYieldToken>(LastToken);
        }
    }

    private sealed class FakeToken : IYieldToken
    {
        public FakeToken(string agentId) => AgentId = agentId;

        public string AgentId { get; }

        public bool Resumed { get; private set; }

        /// <summary>True once the cycle handed the critical section back WITHOUT waking the jail — the
        /// conflict path's terminus. Recorded separately from <see cref="Resumed"/> because the whole
        /// point is that they are different outcomes: one wakes the agent, one does not.</summary>
        public bool ReleasedWithoutResuming { get; private set; }

        public bool IsActive => !Resumed && !ReleasedWithoutResuming;

        public YieldOutcome Outcome => YieldOutcome.ByReady;

        public void Resume() => Resumed = true;

        public void ReleaseWithoutResuming() => ReleasedWithoutResuming = true;

        public void Dispose() => Resume();
    }
}
