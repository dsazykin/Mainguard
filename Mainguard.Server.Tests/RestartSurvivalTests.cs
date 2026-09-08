using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>Audit F3 — "the loop survives a daemon restart" was true of the state files and false of the
/// loop.</b>
///
/// <para>Five ledgers were memory-only while the jails they described were not: the human pause ledger,
/// the kill switch's causation ledger, the pause axis, the mid-rebase conflict parking, and the conflict
/// hand-back permit. So a restart with a <c>docker pause</c>d jail left the reconciler adopting it as
/// Paused and frozen, and then every exit refused — Unpause said "this agent isn't human-paused", the
/// kill switch's Resume released nothing, Resolve/Abort said "no rebase parked" — while the frozen axis
/// refused prompts and verification and the reaper stopped the jail half an hour later. The only ways out
/// were Stop, which throws the work away, and a raw <c>docker unpause</c> from a terminal, which is not
/// inside the app at all.
///
/// <para><c>AgentSessionReconcileTests.Reconcile_ShouldAdoptAPausedJail_AsPaused</c> pins the entry to
/// that state and nothing tested the exit. <b>These are the exit.</b></para>
///
/// <para>Every "restart" here is what a restart actually is: a SECOND set of daemon objects built over
/// the same store file, with the first set discarded. Nothing is handed across in memory.</para>
/// </summary>
public sealed class RestartSurvivalTests : IDisposable
{
    private const string Repo = "restartrepohash";

    private readonly string _ledgerPath = Path.Combine(
        Path.GetTempPath(), $"mg-restart-{Guid.NewGuid():N}", AgentRestartLedger.FileName);

    private IAgentRestartLedger NewLedger() => new JsonAgentRestartLedger(_ledgerPath);

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_ledgerPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Cleanup is a courtesy, not a contract.
        }
    }

    // ================= THE ACCEPTANCE TEST =========================================================

    /// <summary>
    /// <b>The one that matters.</b> A human pauses an agent; the daemon dies; the jail keeps running,
    /// frozen. A new daemon adopts it — and the human presses Resume in the app and the jail thaws.
    ///
    /// <para>Everything is driven through the shipped types: the same <see cref="AgentPauseService"/> the
    /// <c>UnpauseAgent</c> RPC calls, the same <see cref="AgentSessionReconciler"/> adoption pass the
    /// hosted service runs, the same <see cref="JsonAgentRestartLedger"/> the daemon writes. The engine is
    /// a fake only so the test can run without Docker; the Docker-witnessed twin is
    /// <see cref="RestartSurvivalDockerTests"/>.</para>
    /// </summary>
    [Fact]
    public async Task APausedJail_IsResumedFromInsideTheApp_AfterADaemonRestart()
    {
        const string agentId = "pr-7";
        var engine = new FakePausableEngine();

        // ---- daemon #1: a live agent, paused by a human ----
        {
            var d1 = NewDaemon(engine);
            d1.Store.Spawn("claude-code", agentId: agentId, repoHash: Repo);
            d1.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-1");

            var paused = await d1.Pause.PauseAsync(agentId, CancellationToken.None);
            Assert.True(paused.Done, paused.Reason);
            Assert.Contains("jail-1", engine.Paused);
        }

        // ---- the daemon dies. The jail does not: `docker pause` outlives the process that asked. ----

        // ---- daemon #2: nothing in memory, the same ledger file, the jail still there and frozen ----
        var d2 = NewDaemon(engine);
        Assert.Empty(d2.Store.List());

        var reconciler = new AgentSessionReconciler(
            d2.Store,
            listContainers: _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(new[]
            {
                new AgentContainerState(agentId, Repo, "jail-1", Running: false, Paused: true,
                    Kind: "claude-code"),
            }));

        var report = await reconciler.ReconcileAsync();
        Assert.Contains(agentId, report.Adopted);

        var adopted = d2.Store.Find(Repo, agentId);
        Assert.NotNull(adopted);
        Assert.Equal(AgentSessionReconciler.PausedState, adopted!.State);

        // This is the fact the restart used to erase, and every refusal keyed on it.
        Assert.True(
            d2.Ledger.IsHumanPaused(agentId),
            "the daemon forgot that a HUMAN paused this jail, so nothing in the app is entitled to wake it");

        // ---- the human presses Resume. In the app. With no `docker unpause` anywhere. ----
        var resumed = await d2.Pause.UnpauseAsync(agentId, CancellationToken.None);

        Assert.True(resumed.Done, resumed.Reason);
        Assert.DoesNotContain("jail-1", engine.Paused);
        Assert.Equal("Working", d2.Store.Find(Repo, agentId)!.State);
        Assert.Null(d2.Store.FrozenReason(new AgentSessionKey(Repo, agentId)));
        Assert.False(d2.Ledger.IsHumanPaused(agentId));
    }

    /// <summary>
    /// The other half of F3's population: a jail somebody froze with a raw <c>docker pause</c>, or one
    /// paused by a daemon that predates the ledger. No ledger inside the app claims it, so the old
    /// Unpause answered "this agent isn't human-paused" — about a jail whose only other exit was Stop.
    /// The pause axis records WHO, and an engine-read freeze nobody claims is one a human may thaw.
    /// </summary>
    [Fact]
    public async Task AJailFrozenOutsideTheApp_IsResumableByAHuman_OnceAdopted()
    {
        const string agentId = "orphan-1";
        var engine = new FakePausableEngine();
        engine.Paused.Add("jail-x"); // somebody ran `docker pause` in a terminal

        var d = NewDaemon(engine);
        var reconciler = new AgentSessionReconciler(
            d.Store,
            listContainers: _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(new[]
            {
                new AgentContainerState(agentId, Repo, "jail-x", Running: false, Paused: true),
            }));
        await reconciler.ReconcileAsync();

        Assert.False(d.Ledger.IsHumanPaused(agentId), "nothing in the app paused this one");

        var resumed = await d.Pause.UnpauseAsync(agentId, CancellationToken.None);

        Assert.True(resumed.Done, resumed.Reason);
        Assert.DoesNotContain("jail-x", engine.Paused);
    }

    /// <summary>
    /// The refusal that must SURVIVE: a freeze somebody inside the app owns is not a human's to undo from
    /// the pause RPC, because its owner has a release of its own that does more than call unpause (the
    /// conflict card resolves a rebase; the kill switch releases a terminal lock). Widening the exit for
    /// unclaimed freezes must not widen it for claimed ones.
    /// </summary>
    [Fact]
    public async Task AJailFrozenByTheDaemonItself_IsStillNotThawedByTheHumanUnpause()
    {
        const string agentId = "worker-1";
        var engine = new FakePausableEngine();
        engine.Paused.Add("jail-c");

        var d = NewDaemon(engine);
        d.Store.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        d.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-c");
        d.Store.MarkState(new AgentSessionKey(Repo, agentId), "Paused", "conflict");
        d.Store.MarkFrozen(new AgentSessionKey(Repo, agentId), YieldProtocol.YieldPausedReason);

        var resumed = await d.Pause.UnpauseAsync(agentId, CancellationToken.None);

        Assert.False(resumed.Done);
        Assert.Contains("isn't human-paused", resumed.Reason);
        Assert.Contains("jail-c", engine.Paused);
    }

    // ================= F17: a failed unpause must leave a retryable state ===========================

    /// <summary>
    /// Audit F17. The ledger flag is cleared BEFORE the engine call, which is right while the call is in
    /// flight and wrong once it has failed: the jail is still frozen and the next Unpause used to answer
    /// "this agent isn't human-paused" about a jail this operation had just failed to thaw. The
    /// documented workaround was "pause it again, then unpause" — re-asserting by hand the fact the
    /// daemon dropped.
    /// </summary>
    [Fact]
    public async Task AFailedUnpause_LeavesTheAgentHumanPaused_SoResumeCanBePressedAgain()
    {
        const string agentId = "flaky-1";
        var engine = new FakePausableEngine { FailUnpause = true };
        var d = NewDaemon(engine);
        d.Store.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        d.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-f");
        Assert.True((await d.Pause.PauseAsync(agentId, CancellationToken.None)).Done);

        var failed = await d.Pause.UnpauseAsync(agentId, CancellationToken.None);
        Assert.False(failed.Done);
        Assert.Contains("press Resume again", failed.Reason);
        Assert.True(d.Ledger.IsHumanPaused(agentId), "the failed unpause dropped the fact a retry needs");

        engine.FailUnpause = false;
        var retried = await d.Pause.UnpauseAsync(agentId, CancellationToken.None);

        Assert.True(retried.Done, retried.Reason);
        Assert.DoesNotContain("jail-f", engine.Paused);
    }

    // ================= The kill switch's containment ================================================

    /// <summary>
    /// An emergency stop is what people press before restarting things, so "engage, restart, resume" is
    /// the ordinary sequence rather than an edge case. Before this the restart lost the causation ledger,
    /// Resume "released exactly the containment this target applied" — which was now nothing — and every
    /// killed jail stayed frozen for good.
    /// </summary>
    [Fact]
    public async Task TheKillSwitchsContainment_SurvivesARestart_AndResumeReleasesIt()
    {
        var engine = new FakePausableEngine();
        string agentId;

        // ---- daemon #1: engage ----
        {
            var d1 = NewKillDaemon(engine);
            agentId = d1.Store.Spawn("claude-code", repoHash: Repo).Id;
            d1.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-k");

            await d1.KillSwitch.EngageAsync();
            Assert.Contains("jail-k", engine.Paused);
        }

        // ---- daemon #2: the jail is still frozen and this daemon has never heard of the stop ----
        var d2 = NewKillDaemon(engine);
        d2.Store.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        d2.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-k");

        var resume = await d2.KillSwitch.ResumeAsync();

        Assert.Contains(resume.Agents, a => a.AgentId == agentId && a.Outcome == KillResumeOutcome.Resumed);
        Assert.DoesNotContain("jail-k", engine.Paused);
        Assert.False(d2.Locks.IsLocked(agentId), "the terminal sever this stop took was never reversed");
    }

    /// <summary>
    /// The kill journal's <c>ReadAll</c> had no production caller at all — its own doc comment calls a
    /// write-only journal "the defect, not a design choice". It has one now: it is where a restarted kill
    /// switch learns WHICH epoch the containment it inherited belongs to, so the resume report names the
    /// stop rather than reporting an anonymous release.
    /// </summary>
    [Fact]
    public async Task TheKillEpoch_IsRecoveredFromTheDurableJournal_WhenTheLedgerLostIt()
    {
        var engine = new FakePausableEngine();
        var journalPath = Path.Combine(Path.GetDirectoryName(_ledgerPath)!, "kills.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        string agentId;
        string epoch;

        {
            var d1 = NewKillDaemon(engine, journalPath);
            agentId = d1.Store.Spawn("claude-code", repoHash: Repo).Id;
            d1.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-e");
            epoch = (await d1.KillSwitch.EngageAsync()).KillEpochId;
        }

        // The epoch scalar is dropped from the ledger — a torn write, or a file written by a daemon that
        // predates it. The per-agent containment is intact, so the release is still owed and the journal
        // is the record that says which stop owes it.
        NewLedger().SetKillEpochId(null);

        var d2 = NewKillDaemon(engine, journalPath);
        d2.Store.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        d2.Store.AttachSandbox(new AgentSessionKey(Repo, agentId), "jail-e");

        var resume = await d2.KillSwitch.ResumeAsync();

        Assert.Equal(epoch, resume.KillEpochId);
        Assert.DoesNotContain("jail-e", engine.Paused);
    }

    // ================= The pause axis, the parking and the hand-back permit =========================

    /// <summary>
    /// The pause axis is the fact every frozen-jail guard reads (the state word is rewritten by the merge
    /// queue on every transition, which is why the axis exists at all). It has to come back WITH the jail
    /// and with the reason that froze it — and it is applied at adoption, because a freeze mark for a
    /// session that does not exist yet has nowhere to live.
    /// </summary>
    [Fact]
    public async Task ThePauseAxis_ComesBackWithTheJail_AtAdoption()
    {
        const string agentId = "axis-1";
        var key = new AgentSessionKey(Repo, agentId);

        var s1 = new AgentSessionStore(new InMemoryAuditLog(), NewLedger());
        s1.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        s1.AttachSandbox(key, "jail-a");
        s1.MarkFrozen(key, "a human paused it");

        var s2 = new AgentSessionStore(new InMemoryAuditLog(), NewLedger());
        Assert.Null(s2.FrozenReason(key));

        await new AgentSessionReconciler(
            s2,
            listContainers: _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(new[]
            {
                new AgentContainerState(agentId, Repo, "jail-a", Running: false, Paused: true),
            })).ReconcileAsync();

        Assert.Equal("a human paused it", s2.FrozenReason(key));
    }

    /// <summary>A stop drops the mark. <c>pr-&lt;n&gt;</c> ids are reused by design, so a mark left in the
    /// ledger would freeze the next session that ever took the same (repo, id).</summary>
    [Fact]
    public void ThePauseAxis_IsForgottenWhenTheSessionIsStopped()
    {
        const string agentId = "axis-2";
        var key = new AgentSessionKey(Repo, agentId);

        var s1 = new AgentSessionStore(new InMemoryAuditLog(), NewLedger());
        s1.Spawn("claude-code", agentId: agentId, repoHash: Repo);
        s1.AttachSandbox(key, "jail-b");
        s1.MarkFrozen(key, "frozen");
        s1.Stop(key);

        var s2 = new AgentSessionStore(new InMemoryAuditLog(), NewLedger());
        s2.Spawn("claude-code", agentId: agentId, repoHash: Repo);

        Assert.Null(s2.FrozenReason(key));
    }

    /// <summary>
    /// The parked worktree is still on disk and the jail is still frozen; only the daemon's knowledge of
    /// it went away. With it gone, Resolve and Abort both answered "this entry is not parked mid-rebase"
    /// and a Stop force-removed a worktree with a rebase in progress.
    /// </summary>
    [Fact]
    public void AParkedRebaseConflict_AndItsHandBackPermit_SurviveARestart()
    {
        var p1 = new RebaseConflictParkingStore(NewLedger());
        p1.Park(Repo, new ParkedRebaseConflict(
            "pr-9", "/work/agent/pr-9", "main", new[] { "src/A.cs", "src/B.cs" },
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)));
        p1.MarkHandedBack(Repo, "pr-9");

        var p2 = new RebaseConflictParkingStore(NewLedger());

        var parked = p2.Find(Repo, "pr-9");
        Assert.NotNull(parked);
        Assert.Equal("/work/agent/pr-9", parked!.WorktreePath);
        Assert.Equal("main", parked.MainBranch);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, parked.ConflictedPaths);
        Assert.True(
            p2.IsHandedBack(Repo, "pr-9"),
            "the one-rewrite permit is gone, so the handed-back branch is refused on every sweep forever");

        // And the same repo-scoping the in-memory store has: another repo's pr-9 is a different conflict.
        Assert.Null(p2.Find("other-repo", "pr-9"));

        // Clearing is durable too — a resolved conflict must not come back as one.
        p2.Clear(Repo, "pr-9");
        p2.ClearHandedBack(Repo, "pr-9");
        var p3 = new RebaseConflictParkingStore(NewLedger());
        Assert.Null(p3.Find(Repo, "pr-9"));
        Assert.False(p3.IsHandedBack(Repo, "pr-9"));
    }

    // ================= The store itself =============================================================

    /// <summary>
    /// An unreadable ledger rehydrates as NOTHING REMEMBERED, which is the pre-existing restart behaviour.
    /// The failure direction matters: asserting a freeze or a human pause the daemon cannot substantiate
    /// would refuse operations on jails nobody froze.
    /// </summary>
    [Fact]
    public void AnUnreadableLedger_RehydratesAsNothingRemembered()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        File.WriteAllText(_ledgerPath, "{ not json");

        var ledger = NewLedger();

        Assert.Empty(ledger.LoadAll());
        Assert.Null(ledger.KillEpochId);
        Assert.False(new HumanPauseLedger(ledger).IsHumanPaused("anyone"));
    }

    /// <summary>Five writers share one file, so a row is read-modify-written under the store's own lock;
    /// a facts-in-one-row-per-agent shape must not let one writer's update erase another's.</summary>
    [Fact]
    public void FiveFactsAboutOneAgent_CoexistInOneRow()
    {
        var w = NewLedger();
        new HumanPauseLedger(w).MarkHumanPaused("multi");

        var store = new AgentSessionStore(new InMemoryAuditLog(), w);
        store.Spawn("claude-code", agentId: "multi", repoHash: Repo);
        store.MarkFrozen(new AgentSessionKey(Repo, "multi"), "frozen because");

        var parking = new RebaseConflictParkingStore(w);
        parking.Park(Repo, new ParkedRebaseConflict(
            "multi", "/wt", "main", Array.Empty<string>(), DateTimeOffset.UnixEpoch));
        parking.MarkHandedBack(Repo, "multi");

        var row = Assert.Single(NewLedger().LoadAll());
        Assert.Equal("multi", row.AgentId);
        Assert.True(row.HumanPaused);
        Assert.Equal("frozen because", Assert.Single(row.Frozen).Reason);
        Assert.Equal("/wt", Assert.Single(row.Parked).WorktreePath);
        Assert.Equal(Repo, Assert.Single(row.HandedBackRepos));
    }

    /// <summary>A row with nothing left in it is dropped, so the file tracks live state rather than every
    /// agent the daemon has ever paused.</summary>
    [Fact]
    public void AnEmptiedRow_LeavesTheFile()
    {
        var ledger = new HumanPauseLedger(NewLedger());
        ledger.MarkHumanPaused("transient");
        Assert.Single(NewLedger().LoadAll());

        ledger.ClearHumanPaused("transient");

        Assert.Empty(NewLedger().LoadAll());
    }

    // ================= Harness ======================================================================

    private sealed record PauseDaemon(
        AgentSessionStore Store, HumanPauseLedger Ledger, AgentPauseService Pause);

    private PauseDaemon NewDaemon(ISandboxEngine engine)
    {
        var ledger = NewLedger();
        var store = new AgentSessionStore(new InMemoryAuditLog(), ledger);
        var pauseLedger = new HumanPauseLedger(ledger);
        return new PauseDaemon(
            store,
            pauseLedger,
            new AgentPauseService(
                store, new FakeEnvironment(engine), pauseLedger, new KillSwitchGate(),
                NullLogger<AgentPauseService>.Instance));
    }

    private sealed record KillDaemon(
        AgentSessionStore Store, TerminalLockRegistry Locks, KillSwitch KillSwitch);

    private KillDaemon NewKillDaemon(ISandboxEngine engine, string? journalPath = null)
    {
        var ledger = NewLedger();
        var store = new AgentSessionStore(new InMemoryAuditLog(), ledger);
        var locks = new TerminalLockRegistry();
        var leaderDir = Path.Combine(Path.GetDirectoryName(_ledgerPath)!, "leader.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        var target = new SandboxKillTarget(
            store, engine, new SessionLeader(new LeaderRegistry(leaderDir)), locks,
            new HumanPauseLedger(ledger), NullLoggerFactory.Instance, ledger);
        var kill = new KillSwitch(
            new KillSwitchGate(), target,
            journalPath is null ? null : new JsonKillJournal(journalPath),
            new InMemoryAuditLog(), null, null, null, ledger);
        return new KillDaemon(store, locks, kill);
    }

    private sealed class FakePausableEngine : ISandboxEngine
    {
        public HashSet<string> Paused { get; } = new(StringComparer.Ordinal);

        /// <summary>Makes <c>docker unpause</c> fail, for the F17 retry test.</summary>
        public bool FailUnpause { get; set; }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            Paused.Add(containerId);
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            if (FailUnpause)
            {
                throw new InvalidOperationException("the container engine is not answering");
            }

            Paused.Remove(containerId);
            return Task.CompletedTask;
        }

        public Task<bool> IsPausedAsync(string containerId, CancellationToken ct = default)
            => Task.FromResult(Paused.Contains(containerId));

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : IAgentEnvironment
    {
        public FakeEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;

        public string SubstrateId => "test";

        public SubstrateCapabilities Capabilities => throw new NotSupportedException();

        public IRepoProvisioner Repos => throw new NotSupportedException();

        public IAgentWorktreeManager Worktrees => throw new NotSupportedException();

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress => throw new NotSupportedException();

        public SyncRemote ResolveSyncRemote(string repoHash) => throw new NotSupportedException();
    }
}
