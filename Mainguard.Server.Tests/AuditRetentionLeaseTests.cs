using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Server.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F64b — retention used to redact purely by age. An RT-D1 merge lease that is still outstanding
/// (a merge in flight, or one stranded by a crash and waiting for the boot reconcile) could have the
/// audit records describing it tombstoned out from under it, and because redaction is irreversible
/// by design that evidence was simply gone. The sweep now asks which leases are open first.
/// </summary>
public sealed class AuditRetentionLeaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _mirrorPath;
    private readonly InMemoryKeyStore _keys = new();

    /// <summary>Mutable so records can be written "90+ days ago" without waiting.</summary>
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public AuditRetentionLeaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mg-retention-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "retention-test.db");
        _mirrorPath = Path.Combine(_dir, "retention-mirror.bin");
        using var db = new AppDbContext(_dbPath);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ChainedAuditLog OpenLog() => new(
        () => new AppDbContext(_dbPath),
        new AuditCrypto(_keys),
        new AuditFileMirror(_mirrorPath),
        () => _now);

    private static AuditRetentionService NewService() =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);

    private void AppendAged(IChainedAuditLog log, string type, IDictionary<string, string> payload, TimeSpan age)
    {
        var restore = _now;
        _now = DateTimeOffset.UtcNow - age;
        log.Append(type, payload, "tester");
        _now = restore;
    }

    private static bool IsRedacted(IChainedAuditLog log, long seq)
        => log.Read(seq, 1).Single().PayloadJson == ChainedAuditLog.RedactedTombstone;

    // ---- baseline: nothing in flight, retention behaves exactly as it did ----

    [Fact]
    public void WithNoOpenLease_ExpiredRecordsAreRedacted()
    {
        var log = OpenLog();
        AppendAged(log, "merge_started", new Dictionary<string, string> { ["agent_id"] = "agent-1" },
            TimeSpan.FromDays(200));
        AppendAged(log, "merge_started", new Dictionary<string, string> { ["agent_id"] = "agent-2" },
            TimeSpan.FromDays(1));

        NewService().Sweep(log, leases: null);

        Assert.True(IsRedacted(log, 1), "the 200-day-old record expires");
        Assert.False(IsRedacted(log, 2), "the 1-day-old record is inside the window");
    }

    // ---- the fix: an open lease holds its own evidence back ----

    [Fact]
    public void AnOpenLease_HoldsBackTheRecordsThatNameIt()
    {
        var log = OpenLog();
        AppendAged(log, "merge_started",
            new Dictionary<string, string> { ["agent_id"] = "agent-held", ["repo"] = "r1" },
            TimeSpan.FromDays(200));
        AppendAged(log, "merge_started",
            new Dictionary<string, string> { ["agent_id"] = "agent-unrelated" },
            TimeSpan.FromDays(200));

        var leases = new InMemoryMergeLeaseStore();
        Assert.NotNull(leases.TryBegin("repo-hash", "lease-1", "agent-held", "mainsha", "main"));

        NewService().Sweep(log, leases);

        Assert.False(IsRedacted(log, 1), "a record naming an agent with an open merge lease must survive");
        Assert.True(IsRedacted(log, 2), "an unrelated expired record still expires");
    }

    [Fact]
    public void ALeaseIdInThePayload_AlsoHoldsTheRecord()
    {
        var log = OpenLog();
        AppendAged(log, "merge_confirm_pending",
            new Dictionary<string, string> { ["lease_id"] = "lease-77" },
            TimeSpan.FromDays(200));

        var leases = new InMemoryMergeLeaseStore();
        leases.TryBegin("repo-hash", "lease-77", "some-agent", "mainsha", "main");

        NewService().Sweep(log, leases);

        Assert.False(IsRedacted(log, 1));
    }

    /// <summary>
    /// Held, not exempt. Once the lease closes, the next sweep expires the record normally — the
    /// guard defers retention, it does not create an audit record that never expires.
    /// </summary>
    [Fact]
    public void OnceTheLeaseCloses_TheHeldRecordExpiresOnTheNextSweep()
    {
        var log = OpenLog();
        AppendAged(log, "merge_started", new Dictionary<string, string> { ["agent_id"] = "agent-held" },
            TimeSpan.FromDays(200));

        var leases = new InMemoryMergeLeaseStore();
        leases.TryBegin("repo-hash", "lease-1", "agent-held", "mainsha", "main");
        var service = NewService();

        service.Sweep(log, leases);
        Assert.False(IsRedacted(log, 1));

        leases.Confirm("repo-hash", "lease-1", "post-merge-sha");
        service.Sweep(log, leases);
        Assert.True(IsRedacted(log, 1));
    }

    [Fact]
    public void RedactionEvents_AreNeverThemselvesRedacted_EvenUnderTheLeaseAwareWalk()
    {
        var log = OpenLog();
        AppendAged(log, "merge_started", new Dictionary<string, string> { ["agent_id"] = "agent-old" },
            TimeSpan.FromDays(200));
        AppendAged(log, "merge_started", new Dictionary<string, string> { ["agent_id"] = "agent-held" },
            TimeSpan.FromDays(200));

        var leases = new InMemoryMergeLeaseStore();
        leases.TryBegin("repo-hash", "lease-1", "agent-held", "mainsha", "main");
        var service = NewService();

        service.Sweep(log, leases); // redacts seq 1, appends a redaction event at seq 3
        service.Sweep(log, leases); // must not try to redact seq 3, and must not fail verification

        var (valid, firstBad) = log.VerifyAll();
        Assert.True(valid, $"chain must still verify (first bad seq: {firstBad})");
        Assert.Equal(ChainedAuditLog.RedactionEventType, log.Read(3, 1).Single().Type);
    }

    // ---- the pure predicates ----

    [Fact]
    public void LeaseReferences_TakesLeaseIdAndAgentId_ButNotRepoHash()
    {
        var references = AuditRetentionService.LeaseReferences(new[]
        {
            new Mainguard.Git.Models.MergeLeaseRow
            {
                RepoHash = "repo-hash-do-not-hold-on-this",
                LeaseId = "lease-1",
                AgentId = "agent-1",
            },
        });

        Assert.Contains("lease-1", references);
        Assert.Contains("agent-1", references);
        // Holding on the repo hash would stop retention for a whole repository the moment one merge
        // began — a retention outage wearing a safety check's clothes.
        Assert.DoesNotContain("repo-hash-do-not-hold-on-this", references);
    }

    [Fact]
    public void LeaseReferences_IsEmpty_WhenThereIsNoLeaseStore()
        => Assert.Empty(AuditRetentionService.LeaseReferences(null));

    [Fact]
    public void IsHeldByOpenLease_MatchesOnTheEnvelopeText()
    {
        var references = new HashSet<string>(StringComparer.Ordinal) { "agent-7" };

        Assert.True(AuditRetentionService.IsHeldByOpenLease("{\"agent_id\":\"agent-7\"}", references));
        Assert.False(AuditRetentionService.IsHeldByOpenLease("{\"agent_id\":\"agent-8\"}", references));
        Assert.False(AuditRetentionService.IsHeldByOpenLease("", references));
    }

    private sealed class InMemoryKeyStore : ISecureKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void Set(string key, string secret) => _values[key] = secret;

        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public void Delete(string key) => _values.Remove(key);

        public IReadOnlyList<string> List(string prefix)
            => _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }
}
