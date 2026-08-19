using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-15 items 3–6: the persisted chain — append/reopen resume, schema-level append-only,
/// column and ciphertext tamper detection at the exact seq, encryption leaving no plaintext on
/// disk, crash-mid-append recovery, and the mirror-as-witness posture (a content mismatch is
/// evidence, never auto-repaired).
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _mirrorPath;
    private readonly InMemoryKeyStore _keys = new();

    public AuditLogTests()
    {
        _dir = Path.Combine(TempRepoFixture.CanonicalTempRoot, "mainguard-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "audit-test.db");
        _mirrorPath = Path.Combine(_dir, "audit-mirror.bin");
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
        new AuditCrypto(_keys),
        new AuditFileMirror(_mirrorPath));

    private static void AppendSample(IChainedAuditLog log, int count, string type = "test_event")
    {
        for (var i = 0; i < count; i++)
        {
            log.Append(type, new Dictionary<string, string> { ["index"] = i.ToString() }, "tester");
        }
    }

    [Fact]
    public void Append_ShouldChainFromPreviousHead()
    {
        var log = OpenLog();
        Assert.Equal(1, log.Append("a", new { v = 1 }, "tester"));
        Assert.Equal(2, log.Append("b", new { v = 2 }, "tester"));

        var records = log.Read(1, 10);
        Assert.Equal(2, records.Count);
        Assert.Equal(HashChain.GenesisHash, records[0].PrevHash);
        Assert.Equal(records[0].Hash, records[1].PrevHash);
        Assert.True(log.VerifyAll().Valid);
    }

    [Fact]
    public void Append_ShouldResumeChain_AcrossReopen()
    {
        var first = OpenLog();
        AppendSample(first, 3);

        var reopened = OpenLog();
        reopened.Append("after_restart", new { ok = true }, "tester");

        var (valid, firstBad) = reopened.VerifyAll();
        Assert.True(valid);
        Assert.Null(firstBad);
        Assert.Equal(4, reopened.Read(1, 100).Count);
    }

    [Fact]
    public void LegacyAppend_ShouldRoundTripFields_ThroughRead()
    {
        var log = OpenLog();
        IAuditLog seam = log;
        seam.Append(new AuditEvent("plan_approved", new Dictionary<string, string>
        {
            ["plan_id"] = "p-1",
            ["os_identity"] = "daniel",
        }));

        var events = seam.Read();
        var evt = Assert.Single(events);
        Assert.Equal("plan_approved", evt.Type);
        Assert.Equal("p-1", evt.Fields["plan_id"]);
        Assert.True(log.VerifyAll().Valid);
    }

    [Fact]
    public void NoRewritePathExists_SchemaRejectsUpdateAndDelete()
    {
        var log = OpenLog();
        AppendSample(log, 2);
        SqliteConnection.ClearAllPools();

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var delete = conn.CreateCommand())
        {
            delete.CommandText = "DELETE FROM AuditRecords WHERE Seq = 1";
            var ex = Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
            Assert.Contains("append-only", ex.Message);
        }

        using (var update = conn.CreateCommand())
        {
            update.CommandText = "UPDATE AuditRecords SET Type = 'forged' WHERE Seq = 1";
            var ex = Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
            Assert.Contains("append-only", ex.Message);
        }
    }

    [Theory]
    [InlineData("TimestampText", "'2020-01-01T00:00:00.0000000+00:00'")]
    [InlineData("Type", "'forged_type'")]
    [InlineData("Hash", "'0000000000000000000000000000000000000000000000000000000000000000'")]
    [InlineData("PrevHash", "'0000000000000000000000000000000000000000000000000000000000000000'")]
    [InlineData("PayloadCiphertext", "x'00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff'")]
    public void VerifyAll_ShouldFailAtExactSeq_WhenColumnTampered(string column, string forgedValue)
    {
        var log = OpenLog();
        AppendSample(log, 5);
        SqliteConnection.ClearAllPools();

        // A real attacker with file access drops the guard trigger first — exactly what the chain
        // exists to catch after the fact.
        TamperRow(3, column, forgedValue);

        var reopened = OpenLog();
        var (valid, firstBad) = reopened.VerifyAll();
        Assert.False(valid);
        Assert.Equal(3, firstBad);
    }

    [Fact]
    public void EncryptionAtRest_ShouldLeaveNoPlaintextPromptOnDisk()
    {
        const string sentinel = "SENTINEL-PROMPT-9f8e7d6c";
        var log = OpenLog();
        log.Append("inference", new Dictionary<string, string>
        {
            ["model"] = "test-model",
            ["prompt"] = sentinel,
        }, "tester");
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _dbPath, _mirrorPath })
        {
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(sentinel, System.Text.Encoding.UTF8.GetString(bytes));
        }

        // ...while the authorized reader still decrypts it.
        var record = OpenLog().Read(1, 1).Single();
        Assert.Contains(sentinel, record.PayloadJson);
    }

    [Fact]
    public void CrashMidAppend_ShouldLeaveNoTornRecord()
    {
        var log = OpenLog();
        AppendSample(log, 2);

        // Crash between the DB commit and the mirror append — the widest divergence a crash can make.
        log.FaultBetweenDbAndMirror = () => throw new IOException("simulated crash");
        Assert.Throws<IOException>(() => log.Append("crashing", new { n = 3 }, "tester"));

        var reopened = OpenLog();
        var (valid, firstBad) = reopened.VerifyAll();
        Assert.True(valid);
        Assert.Null(firstBad);
        Assert.Equal(3, reopened.Read(1, 100).Count); // the DB record survived; the mirror was backfilled

        reopened.Append("resumed", new { n = 4 }, "tester");
        Assert.True(reopened.VerifyAll().Valid);
    }

    [Fact]
    public void CrashMidAppend_TornMirrorTailBytes_ShouldRecoverClean()
    {
        var log = OpenLog();
        AppendSample(log, 3);

        // A torn tail: the length prefix says more bytes than were ever written.
        using (var stream = new FileStream(_mirrorPath, FileMode.Append))
        {
            stream.Write(new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x41, 0x42 });
        }

        var reopened = OpenLog();
        Assert.True(reopened.VerifyAll().Valid);
        reopened.Append("after_torn_tail", new { ok = true }, "tester");
        Assert.True(reopened.VerifyAll().Valid);
    }

    [Fact]
    public void MirrorContentMismatch_ShouldFailVerify_NeverAutoRepair()
    {
        var log = OpenLog();
        AppendSample(log, 3);

        // Rewrite the mirror wholesale with record 2's type forged. Content disagreement in the
        // intact prefix = evidence; recovery must NOT quietly rewrite it back from the DB.
        var mirror = new AuditFileMirror(_mirrorPath);
        var records = mirror.ReadAll();
        File.Delete(_mirrorPath);
        var forged = new AuditFileMirror(_mirrorPath);
        foreach (var record in records)
        {
            forged.Append(record.Seq == 2 ? record with { Type = "forged" } : record);
        }

        var reopened = OpenLog();
        var (valid, firstBad) = reopened.VerifyAll();
        Assert.False(valid);
        Assert.Equal(2, firstBad);
    }

    private void TamperRow(long seq, string column, string forgedValue)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var drop = conn.CreateCommand();
        drop.CommandText = "DROP TRIGGER AuditRecords_no_update";
        drop.ExecuteNonQuery();
        using var update = conn.CreateCommand();
        update.CommandText = $"UPDATE AuditRecords SET {column} = {forgedValue} WHERE Seq = {seq}";
        Assert.Equal(1, update.ExecuteNonQuery());
        SqliteConnection.ClearAllPools();
    }

    private sealed class InMemoryKeyStore : ISecureKeyStore
    {
        private readonly Dictionary<string, string> _values = new();

        public void Set(string key, string secret) => _values[key] = secret;
        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key) => _values.Remove(key);
        public IReadOnlyList<string> List(string prefix)
            => _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }
}
