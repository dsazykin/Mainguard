using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Cli;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-15: the audit chain's first production READERS — the <c>AuditService</c> RPCs over the real
/// in-proc daemon, the coordinator-role denial (the chain carries other agents' prompts and the
/// human's decisions), and the offline <c>mainguardd audit verify</c> CLI's exit-code contract
/// (0 intact / 2 tampered).
/// </summary>
public sealed class AuditRpcTests
{
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    // Unique per test instance: the in-proc hosts share one run-scoped daemon DB, so assertions
    // must be scoped to what THIS test appended (same discipline as the queue-lifecycle suite).
    private readonly string _marker = "audit-rpc-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task VerifyAudit_OverTheRealDaemon_ReportsAValidPersistedChain()
    {
        using var host = new DaemonFixture();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        audit.Append(new AuditEvent("test_probe", new System.Collections.Generic.Dictionary<string, string>
        {
            ["marker"] = _marker,
        }));

        var client = new AuditService.AuditServiceClient(host.CreateChannel());
        var response = await client.VerifyAuditAsync(new VerifyAuditRequest(), host.AuthHeaders());

        Assert.True(response.Valid, $"chain invalid at seq {response.FirstBadSeq}");
        Assert.False(response.HasFirstBadSeq);
        Assert.True(response.Persistent, "the in-proc daemon DB opened, so the chained log must be active");
        Assert.True(response.HeadSeq >= 1);
        Assert.Equal(64, response.HeadHash.Length);
    }

    [Fact]
    public async Task ReadAudit_ReturnsTheAppendedRecord_DecryptedAndChained()
    {
        using var host = new DaemonFixture();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        audit.Append(new AuditEvent("test_probe", new System.Collections.Generic.Dictionary<string, string>
        {
            ["marker"] = _marker,
        }));

        var client = new AuditService.AuditServiceClient(host.CreateChannel());

        // Read a window that ENDS at the head rather than one that starts at seq 1. The in-proc hosts
        // share one run-scoped chained log (see _marker above), and `FromSeq = 1, Take = 500` asserts
        // against the OLDEST 500 records — so once the assembly has written more than that, this test's
        // own append is off the end of the window and Assert.Single fails having found nothing. That is
        // a property of how many other tests ran first, which is why it failed in a full run and passed
        // in isolation. Anchoring to the head keeps the assertion about this test's record.
        var head = (await client.VerifyAuditAsync(new VerifyAuditRequest(), host.AuthHeaders())).HeadSeq;
        var response = await client.ReadAuditAsync(
            new ReadAuditRequest { FromSeq = Math.Max(1, head - 499), Take = 500 },
            host.AuthHeaders());

        Assert.True(response.Persistent);
        var record = Assert.Single(response.Records, r => r.PayloadJson.Contains(_marker, StringComparison.Ordinal));
        Assert.Equal("test_probe", record.Type);
        Assert.Equal(64, record.Hash.Length);
        Assert.Equal(64, record.PrevHash.Length);
        Assert.False(string.IsNullOrEmpty(record.Timestamp));
    }

    [Fact]
    public async Task Coordinator_IsDeniedBothAuditRpcs_ByTheRoleLayer()
    {
        using var host = new DaemonFixture();
        host.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var client = new AuditService.AuditServiceClient(host.CreateChannel());
        var coordinator = host.AuthHeaders(CoordinatorToken);

        var verify = await Assert.ThrowsAsync<RpcException>(() =>
            client.VerifyAuditAsync(new VerifyAuditRequest(), coordinator).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, verify.StatusCode);
        Assert.Contains("coordinator role", verify.Status.Detail);

        var read = await Assert.ThrowsAsync<RpcException>(() =>
            client.ReadAuditAsync(new ReadAuditRequest { FromSeq = 1 }, coordinator).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, read.StatusCode);
        Assert.Contains("coordinator role", read.Status.Detail);
    }

    // ---- the offline CLI verb -----------------------------------------------------------------

    [Fact]
    public void AuditVerifyCli_ExitZeroOnIntactChain_ExitTwoOnTamper()
    {
        var dir = Path.Combine(TempDirRoot(), "audit-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "mainguard-daemon.db");
        try
        {
            using (var db = new AppDbContext(dbPath))
            {
                db.Database.Migrate();
            }

            // Written exactly as the daemon writes it (same keyring/mirror conventions the CLI reads).
            var log = new ChainedAuditLog(
                () => new AppDbContext(dbPath),
                new AuditCrypto(new SecureKeyring(Path.Combine(dir, "audit-keyring"))),
                new AuditFileMirror(dbPath + ".audit-mirror"));
            log.Append("test_probe", new { marker = _marker }, "tester");
            log.Append("test_probe", new { marker = _marker + "-2" }, "tester");
            SqliteConnection.ClearAllPools();

            Assert.Equal(0, AuditCommands.Run(new[] { "audit", "verify", "--data", dbPath }));

            // The attacker's move: drop the guard trigger, forge a column.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var drop = conn.CreateCommand();
                drop.CommandText = "DROP TRIGGER AuditRecords_no_update";
                drop.ExecuteNonQuery();
                using var forge = conn.CreateCommand();
                forge.CommandText = "UPDATE AuditRecords SET Type = 'forged' WHERE Seq = 1";
                Assert.Equal(1, forge.ExecuteNonQuery());
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal(2, AuditCommands.Run(new[] { "audit", "verify", "--data", dbPath }));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AuditVerifyCli_MissingStore_IsIntactByDefinition()
    {
        var missing = Path.Combine(TempDirRoot(), "audit-cli-missing-" + Guid.NewGuid().ToString("N"), "no.db");
        Assert.Equal(0, AuditCommands.Run(new[] { "audit", "verify", "--data", missing }));
    }

    [Fact]
    public void AuditVerifyCli_UnknownArguments_AreUsageErrors()
    {
        Assert.Equal(64, AuditCommands.Run(new[] { "audit" }));
        Assert.Equal(64, AuditCommands.Run(new[] { "audit", "replay" }));
        Assert.Equal(64, AuditCommands.Run(new[] { "audit", "verify", "--data" }));
        Assert.Equal(64, AuditCommands.Run(new[] { "audit", "verify", "--bogus" }));
    }

    private static string TempDirRoot()
        => Path.Combine(Path.GetTempPath(), "mainguard-server-tests-cli");
}
