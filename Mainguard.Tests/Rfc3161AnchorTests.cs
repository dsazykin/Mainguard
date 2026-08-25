using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Tests.Fixtures;
using Mainguard.Tests.TestTools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-15 items 8–9: RFC 3161 anchoring is BEST-EFFORT — an unreachable TSA queues and retries
/// while the chain keeps appending (edge row 4) — and the real-TSA round-trip runs network-gated
/// (nightly), never as a PR gate.
/// </summary>
public sealed class Rfc3161AnchorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private DateTimeOffset _now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Rfc3161AnchorTests()
    {
        _dir = Path.Combine(TempRepoFixture.CanonicalTempRoot, "mainguard-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "anchor-test.db");
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
        new AuditFileMirror(_dbPath + ".audit-mirror"),
        clock: () => _now);

    private AuditAnchorQueue OpenQueue() => new(() => new AppDbContext(_dbPath), () => _now);

    [Fact]
    public async Task TsaUnreachable_ShouldQueueAnchor_AndKeepAppending()
    {
        var log = OpenLog();
        var queue = OpenQueue();
        log.Append("test_event", new { n = 1 }, "tester");
        Assert.True(queue.EnqueueIfDue(log));

        // The TSA is down: the anchor stays PENDING…
        var down = new ScriptedTsaClient { Fail = true };
        var (anchored, pending) = await queue.ProcessPendingAsync(down, CancellationToken.None);
        Assert.Equal(0, anchored);
        Assert.Equal(1, pending);

        // …and the chain never noticed (best-effort is the contract: chaining is not).
        log.Append("test_event", new { n = 2 }, "tester");
        Assert.True(log.VerifyAll().Valid);

        // TSA recovers → the queued anchor is retried and the token stored.
        down.Fail = false;
        (anchored, pending) = await queue.ProcessPendingAsync(down, CancellationToken.None);
        Assert.Equal(1, anchored);
        Assert.Equal(0, pending);

        using var db = new AppDbContext(_dbPath);
        var row = Assert.Single(db.AuditAnchors.Where(a => a.Token != null));
        Assert.NotNull(row.AnchoredAtText);
        Assert.Equal(1, row.HeadSeq);
        Assert.Single(down.RequestedHashes.Distinct()); // retried with the SAME head hash
    }

    [Fact]
    public void EnqueueIfDue_FollowsTheRecordAndTimePolicy()
    {
        var log = OpenLog();
        var queue = OpenQueue();

        Assert.False(queue.EnqueueIfDue(log)); // empty chain — nothing to anchor

        log.Append("test_event", new { n = 1 }, "tester");
        Assert.True(queue.EnqueueIfDue(log));  // first head always anchors
        Assert.False(queue.EnqueueIfDue(log)); // same head — idempotent

        log.Append("test_event", new { n = 2 }, "tester");
        Assert.False(queue.EnqueueIfDue(log)); // 1 record, 0 h — not due

        _now += TimeSpan.FromHours(25);
        Assert.True(queue.EnqueueIfDue(log));  // 24 h passed — due by time
    }

    [Fact]
    public async Task AnchorValidation_RejectsGarbageTokens_AndWrongHashes()
    {
        Assert.False(Rfc3161AnchorValidation.Validate(new byte[] { 1, 2, 3 }, new string('a', 64), out _));
        Assert.False(Rfc3161AnchorValidation.Validate(Array.Empty<byte>(), new string('a', 64), out _));

        // A stored garbage token surfaces through the queue's offline validation (what the verify
        // CLI runs) rather than passing silently.
        var log = OpenLog();
        var queue = OpenQueue();
        log.Append("test_event", new { n = 1 }, "tester");
        queue.EnqueueIfDue(log);
        var scripted = new ScriptedTsaClient(); // returns bytes that are NOT a decodable token
        await queue.ProcessPendingAsync(scripted, CancellationToken.None);
        Assert.Single(queue.ValidateStoredAnchors());
    }

    [RequiresNetworkFact("real RFC 3161 TSA round-trip")]
    public async Task AnchorRoundTrip_ShouldValidateAgainstRealTsa()
    {
        var log = OpenLog();
        var queue = OpenQueue();
        log.Append("test_event", new { n = 1 }, "tester");
        queue.EnqueueIfDue(log);

        // freetsa.org is a public RFC 3161 endpoint; override for an internal TSA.
        var url = Environment.GetEnvironmentVariable("MAINGUARD_TSA_URL") ?? "https://freetsa.org/tsr";
        using var client = new Rfc3161TimestampClient(new Uri(url));
        var (anchored, pending) = await queue.ProcessPendingAsync(client, CancellationToken.None);

        Assert.Equal(1, anchored);
        Assert.Equal(0, pending);
        Assert.Empty(queue.ValidateStoredAnchors()); // the real token validates against the head hash

        using var db = new AppDbContext(_dbPath);
        var row = db.AuditAnchors.Single();
        Assert.True(Rfc3161AnchorValidation.Validate(row.Token!, row.HeadHash, out var at));
        Assert.NotNull(at);
    }

    /// <summary>Fails on demand; on success returns bytes that are deliberately NOT a valid token
    /// (crafting a real one needs a TSA signing cert — that leg is the RequiresNetwork test).</summary>
    private sealed class ScriptedTsaClient : IRfc3161TimestampClient
    {
        public bool Fail { get; set; }
        public List<string> RequestedHashes { get; } = new();

        public Task<byte[]> RequestTimestampTokenAsync(byte[] sha256Hash, CancellationToken ct)
        {
            RequestedHashes.Add(Convert.ToHexStringLower(sha256Hash));
            if (Fail)
            {
                throw new IOException("TSA unreachable (scripted)");
            }

            return Task.FromResult(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        }
    }
}
