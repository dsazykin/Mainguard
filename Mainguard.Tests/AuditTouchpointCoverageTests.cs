using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// The P2-10 merge-queue record (7-field), disambiguated from the UI prototype VerificationRecord.
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-15 item 7 — touchpoint coverage over the REAL chained store: a scripted governance session
/// (plan draft → approve → reject → verify → stale override → egress change → merge reject → kill
/// switch) drives the actual G-17 services against a <see cref="ChainedAuditLog"/> on a real
/// migrated SQLite file, and the trail is asserted as an EXACT ordered event-type sequence with
/// exactly one event per operation. Then the part InMemoryAuditLog could never do: the store is
/// REOPENED and the same story is still there, chain-verified.
///
/// <para>Plus the RT-D3 leg end-to-end: a kill during an audit-store outage lands its chained
/// <c>killswitch_audit_gap</c> in the real store on recovery, and the chain still verifies after a
/// reopen — the freeze-then-audit carve-out made durably tamper-evident, not just in-memory.</para>
/// </summary>
public sealed class AuditTouchpointCoverageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public AuditTouchpointCoverageTests()
    {
        _dir = Path.Combine(TempRepoFixture.CanonicalTempRoot, "mainguard-audit-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "coverage.db");
        using var db = new AppDbContext(_dbPath);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private ChainedAuditLog OpenLog() => new(
        () => new AppDbContext(_dbPath),
        new AuditCrypto(new SecureKeyring(Path.Combine(_dir, "audit-keyring"))),
        new AuditFileMirror(_dbPath + ".audit-mirror"));

    [Fact]
    public async Task TouchpointCoverage_ScriptedSwarmSession_ShouldEmitExpectedEventSequence()
    {
        var audit = OpenLog();

        // ---- Plans: draft two, approve one, reject one (P2-14 → plan_approved / plan_rejected) ----
        var plans = new PlanApprovalService(audit: audit);
        var fields = new TaskPlanFields(new[] { "src/one/**" }, "do the thing", "tests green");
        var draft1 = plans.Draft("coord-1", "task-1", fields, "implement task-1", 1.5m);
        var draft2 = plans.Draft("coord-1", "task-2", fields, "implement task-2", 1.5m);
        Assert.True(draft1.IsDrafted);
        Assert.True(draft2.IsDrafted);
        plans.Approve(draft1.PlanId!, "uid:501");
        plans.Reject(draft2.PlanId!, "out of scope");

        // ---- Merge queue: verify green, stale override, review verdict "no" (P2-10) ----
        MergeQueue queue = null!;
        queue = new MergeQueue("repo-cov", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            runVerification: (id, _) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, true, "log", "npm test", "cfg", DateTimeOffset.UtcNow)),
            requeue: (_, _) => Task.CompletedTask,
            audit: audit);
        queue.EnsureEntry("w-1", MergeEntryOrigin.Local);
        await queue.RunVerificationAsync("w-1", CancellationToken.None);
        queue.RecordStaleOverrideUse("w-1", "release freeze exception");
        queue.RequestReview("w-1");
        Assert.True(queue.TryReject("w-1", rejectedBy: "uid:501", reason: "wrong approach", out _));

        // ---- Egress: one allowlist change (P2-07 transparency) ----
        var allowlist = new EgressAllowlist(Array.Empty<EgressAllowlistEntry>(), audit);
        allowlist.Add(new EgressAllowlistEntry("Example", "example.test", EgressEntryKind.Custom), "uid:501");

        // ---- Kill switch (P2-14): the emergency stop, audited ----
        var kill = new KillSwitch(new KillSwitchGate(), new NoAgentsKillTarget(), audit: audit,
            rttBudget: () => TimeSpan.Zero);
        await kill.EngageAsync();

        // ---- The exact story, in order, exactly one event per operation (G-17 idempotence) ----
        var types = audit.Read().Select(e => e.Type).ToArray();
        Assert.Equal(new[]
        {
            "plan_approved",
            "plan_rejected",
            "stale_override_used",
            MergeQueue.RejectedEvent,
            EgressAllowlist.ChangeEventType,
            "killswitch",
        }, types);

        var (valid, firstBad) = audit.VerifyAll();
        Assert.True(valid);
        Assert.Null(firstBad);

        // ---- What the in-memory journal could never do: the story OUTLIVES the writer ----
        SqliteConnection.ClearAllPools();
        var reopened = OpenLog();
        Assert.Equal(types, reopened.Read().Select(e => e.Type).ToArray());
        Assert.True(reopened.VerifyAll().Valid);
    }

    [Fact]
    public async Task KillSwitchDuringAuditOutage_GapLandsInThePersistedChain_OnRecovery()
    {
        var chained = OpenLog();
        var faultable = new OutageWrapper(chained);
        var kill = new KillSwitch(new KillSwitchGate(), new NoAgentsKillTarget(), audit: faultable,
            rttBudget: () => TimeSpan.Zero);

        // The kill fires while the store is down: never blocked, nothing appended.
        faultable.Down = true;
        var report = await kill.EngageAsync();
        Assert.True(report.QueueFrozen);
        Assert.Empty(chained.Read());

        // Recovery → the chained gap marker lands in the REAL store, and survives a reopen.
        faultable.Down = false;
        kill.NotifyAuditStoreRecovered();

        SqliteConnection.ClearAllPools();
        var reopened = OpenLog();
        var gap = Assert.Single(reopened.Read(), e => e.Type == "killswitch_audit_gap");
        Assert.Equal(report.KillEpochId, gap.Fields["kill_epoch_id"]);
        Assert.True(reopened.VerifyAll().Valid);
    }

    /// <summary>An empty fleet — the kill's audit path is what is under test, not containment.</summary>
    private sealed class NoAgentsKillTarget : IKillTarget
    {
        public IReadOnlyList<string> ActiveAgentIds => Array.Empty<string>();
        public Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public Task PauseAsync(string agentId, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyDictionary<string, string> CaptureStates() => new Dictionary<string, string>();
    }

    /// <summary>Simulates the store being unavailable (RT-D3): Down throws exactly like the real
    /// chained log does on a store failure; recovered appends flow through to the real chain.</summary>
    private sealed class OutageWrapper : IAuditLog
    {
        private readonly IAuditLog _inner;
        public bool Down { get; set; }

        public OutageWrapper(IAuditLog inner) => _inner = inner;

        public void Append(AuditEvent auditEvent)
        {
            if (Down)
            {
                throw new IOException("audit store unavailable (simulated outage)");
            }

            _inner.Append(auditEvent);
        }

        public IReadOnlyList<AuditEvent> Read() => _inner.Read();
    }
}
