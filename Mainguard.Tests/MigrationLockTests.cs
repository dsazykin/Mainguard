using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The startup migration path, over the failure that actually happens: an orphaned
/// <c>__EFMigrationsLock</c> row left by a head killed mid-migration, which EF then waits on forever. The
/// shell used to neither clear it nor name it — it timed out after 20s and blamed "another Mainguard
/// instance", which on the machine where this reproduced was not running at all.
///
/// <para>The clearing test drives the real EF <c>Migrate()</c> against a poisoned database and would HANG
/// rather than fail if the clear stopped working, so migration runs on a worker with a hard bound and the
/// test fails on the timeout instead.</para>
/// </summary>
public class MigrationLockTests : IDisposable
{
    /// <summary>Shaped exactly as EF's own timestamp; also the value the real reproduction carried.</summary>
    private const string OrphanedAt = "2026-07-29 16:24:15.4969859+00:00";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mainguard-migrationlock-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "probe.db");

    public MigrationLockTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Clearing_an_orphaned_lock_lets_a_migration_that_would_otherwise_hang_complete()
    {
        CreateMigratedDatabase();
        PoisonWithOrphanedLock();

        Assert.True(MigrationLock.TryClearOrphanedLock(DbPath), "no lock row was cleared");

        var migrated = MigrateInBackground();
        Assert.True(
            await FinishesWithin(migrated, TimeSpan.FromSeconds(30)),
            "Migrate() still blocked after the orphaned lock was cleared — EF polls that row forever, so "
            + "this is the hang the clear exists to prevent.");
        await migrated;
    }

    /// <summary>The negative half of the pair: without the clear, the same migration does NOT finish. This
    /// is what makes the test above mean something — and it is measured here, not assumed.</summary>
    [Fact]
    public async Task Without_the_clear_the_same_migration_does_not_finish()
    {
        CreateMigratedDatabase();
        PoisonWithOrphanedLock();

        var blocked = MigrateInBackground();

        Assert.False(
            await FinishesWithin(blocked, TimeSpan.FromSeconds(5)),
            "Migrate() completed against a poisoned __EFMigrationsLock — EF no longer blocks on that row, "
            + "so the clear (and the whole hazard it guards) needs re-checking against the new EF.");

        // Never leave that poll running into the next test (this assembly shares one process and one
        // dispatcher — see HarnessHygiene). Clearing the row releases the waiter, which also shows the
        // clear unblocks a migration ALREADY in flight, not only one that has yet to start.
        MigrationLock.TryClearOrphanedLock(DbPath);
        Assert.True(
            await FinishesWithin(blocked, TimeSpan.FromSeconds(30)),
            "the blocked migration never resumed after its lock row was cleared");
        await blocked;
    }

    [Fact]
    public void Clearing_a_database_with_no_lock_row_reports_nothing_cleared_and_does_not_throw()
    {
        CreateMigratedDatabase();
        Assert.False(MigrationLock.TryClearOrphanedLock(DbPath));
    }

    [Fact]
    public void Clearing_a_database_that_does_not_exist_is_a_no_op_and_creates_nothing()
    {
        Assert.False(MigrationLock.TryClearOrphanedLock(DbPath));
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void A_held_lock_is_named_in_the_stall_message_with_the_time_it_was_taken()
    {
        CreateMigratedDatabase();
        PoisonWithOrphanedLock();

        var message = MigrationLock.DescribeStall(DbPath, TimeSpan.FromSeconds(20));

        Assert.Contains(DbPath, message, StringComparison.Ordinal);
        Assert.Contains("20s", message, StringComparison.Ordinal);
        Assert.Contains(MigrationLock.TableName, message, StringComparison.Ordinal);
        Assert.Contains(OrphanedAt, message, StringComparison.Ordinal);
        // The correction that matters: a stale row does not imply a live process, so the message must not
        // send the reader to the process list the way the old one did.
        Assert.Contains("does NOT require a running process", message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_lock_row_the_stall_message_says_so_instead_of_inventing_a_cause()
    {
        CreateMigratedDatabase();

        var message = MigrationLock.DescribeStall(DbPath, TimeSpan.FromSeconds(20));

        Assert.Contains(DbPath, message, StringComparison.Ordinal);
        Assert.Contains("No stale row", message, StringComparison.Ordinal);
        Assert.DoesNotContain(OrphanedAt, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_database_is_reported_as_missing_rather_than_as_a_lock()
    {
        var message = MigrationLock.DescribeStall(DbPath, TimeSpan.FromSeconds(20));

        Assert.Contains("does not exist", message, StringComparison.Ordinal);
        Assert.DoesNotContain(MigrationLock.TableName, message, StringComparison.Ordinal);
    }

    private void CreateMigratedDatabase()
    {
        using var db = new AppDbContext(DbPath);
        db.Database.Migrate();
    }

    /// <summary>Runs <c>Migrate()</c> off the test thread — the ONLY safe way to observe a call whose
    /// failure mode is "never returns".</summary>
    private Task MigrateInBackground() => Task.Run(() =>
    {
        using var db = new AppDbContext(DbPath);
        db.Database.Migrate();
    });

    /// <summary>Bounded wait that never blocks the test thread (xUnit1031), and cancels its own timer so a
    /// finished task leaves nothing pending behind it.</summary>
    private static async Task<bool> FinishesWithin(Task task, TimeSpan budget)
    {
        using var timer = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(budget, timer.Token)) == task;
        timer.Cancel();
        return finished;
    }

    /// <summary>Reproduces a head killed mid-migration: the schema is current, but the lock row it claimed
    /// is still sitting there. Shaped exactly as EF creates it.</summary>
    private void PoisonWithOrphanedLock()
    {
        Execute($"CREATE TABLE IF NOT EXISTS \"{MigrationLock.TableName}\" "
                + "(\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, "
                + "\"Timestamp\" TEXT NOT NULL);");
        Execute($"INSERT OR REPLACE INTO \"{MigrationLock.TableName}\" (\"Id\", \"Timestamp\") "
                + $"VALUES (1, '{OrphanedAt}');");
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
