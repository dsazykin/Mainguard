using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
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
        Assert.Equal(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MainBranch, "HEAD").Code);
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
        Assert.NotEqual(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MainBranch, "HEAD").Code);
        Assert.True(yield.LastToken!.Resumed); // resumed so the agent finishes its own rebase

        // The agent finishes its rebase; the next cycle succeeds.
        Directory.Delete(rebaseMergeDir, recursive: true);
        var second = await rebaser.RunCycleAsync("a1");
        Assert.Equal(RebaseCycleKind.Rebased, second.Kind);
        Assert.Equal(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MainBranch, "HEAD").Code);
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
        Assert.NotEqual(0, AgentTestGit.Run(env.Worktree, "merge-base", "--is-ancestor", env.MainBranch, "HEAD").Code);

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

        public bool IsActive => !Resumed;

        public YieldOutcome Outcome => YieldOutcome.ByReady;

        public void Resume() => Resumed = true;

        public void Dispose() => Resume();
    }
}
