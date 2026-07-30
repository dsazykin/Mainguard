using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Mainguard.Git;

/// <summary>
/// EF Core's advisory migration lock, and what to do about it at startup.
///
/// <para><b>The hazard:</b> <c>Database.Migrate()</c> claims a single row in <c>__EFMigrationsLock</c>
/// BEFORE it even looks at whether any migration is pending, and polls for that row in an UNBOUNDED
/// <c>Thread.Sleep</c> loop until it disappears. A process killed mid-migration leaves the row behind, so
/// from then on every launch hangs forever — with nothing running and nothing holding an OS-level lock.
/// Zero pending migrations does not save you: the claim happens first.</para>
///
/// <para><b>Prior art, deliberately mirrored:</b> the daemon already hit exactly this (a WSL idle-stop of
/// the distro killing it mid-migration) and fixed it in
/// <c>Mainguard.Server/Gateway/GatewayServiceRegistration.TryPrepareDatabase</c> — clear the orphaned row
/// before migrating, and keep a watchdog behind it. The desktop shell shares the hazard but never got the
/// remedy; <c>App.Initialize</c> now calls in here. The single-writer premise is the same on both sides:
/// the daemon is one systemd instance per VM, and the shell holds the <c>Mainguard.App.SingleInstance</c>
/// mutex (<c>ShellEntryPoint.RunDesktop</c>) before <c>Initialize</c> runs, so a row present at startup was
/// orphaned by a dead process, not claimed by a live one.</para>
///
/// <para><b>And when the watchdog still fires:</b> <see cref="DescribeStall"/> LOOKS at the database and
/// reports what is there. The message it replaces asserted a cause nobody had checked — "Another Mainguard
/// instance may be holding the database lock — close it and relaunch" — which sends the reader to a process
/// list that is empty and never mentions the row actually responsible.</para>
/// </summary>
public static class MigrationLock
{
    /// <summary>EF Core's advisory migration-lock table. A row means "a migration is in progress", whether
    /// or not the process that claimed it still exists.</summary>
    public const string TableName = "__EFMigrationsLock";

    /// <summary>
    /// Drops any orphaned lock row at <paramref name="dbPath"/> so the migration that follows is not made
    /// to wait for a holder that is gone. Returns true when a row was actually cleared. Best effort by
    /// design: a fresh database or a pre-lock EF schema has no such table, and that is not an error —
    /// <c>Migrate()</c> and the caller's watchdog remain the backstop either way.
    /// </summary>
    public static bool TryClearOrphanedLock(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath))
                return false;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{TableName}\";";
            return command.ExecuteNonQuery() > 0;
        }
        catch (Exception)
        {
            // Absent table / unreadable file — nothing to clear, and a repair must never become the
            // failure it was repairing.
            return false;
        }
    }

    /// <summary>
    /// A sentence describing what is blocking migrations against <paramref name="dbPath"/>, suitable for a
    /// timeout message. Names the lock row and when it was taken when that is what is there; otherwise says
    /// plainly that no stale lock was found, rather than inventing a cause.
    /// </summary>
    /// <param name="timeout">The watchdog budget that elapsed, quoted back so the message stands alone.</param>
    public static string DescribeStall(string dbPath, TimeSpan timeout)
    {
        var headline =
            $"Database migrations did not complete within {timeout.TotalSeconds:0}s against '{dbPath}'.";

        if (!File.Exists(dbPath))
        {
            return headline + " The database file does not exist, so this is not a lock — creating it "
                + "failed or the path is not writable.";
        }

        string? heldSince;
        try
        {
            heldSince = ReadLockRowTimestamp(dbPath);
        }
        catch (Exception ex)
        {
            return headline + $" (Could not inspect {TableName} to say why: {ex.Message})";
        }

        if (heldSince is not null)
        {
            return headline
                + $" EF Core's migration lock is held: {TableName} holds the row taken at {heldSince}, "
                + "and EF waits for it indefinitely. That row does NOT require a running process — it is "
                + "left behind by one killed mid-migration. Startup clears orphaned rows before migrating, "
                + "so one surviving that means a second writer re-took it: look for another process on "
                + $"this database, then clear it with DELETE FROM {TableName};";
        }

        return headline
            + $" No stale row in {TableName}, so this is not the migration lock. Another process holding "
            + "the SQLite write lock, or a slow/unavailable filesystem under the data root, is what "
            + "remains to check.";
    }

    /// <summary>The <c>Timestamp</c> of the migration-lock row, or <c>null</c> when the table is absent or
    /// empty (the healthy state — EF drops the row when a migration completes).</summary>
    private static string? ReadLockRowTimestamp(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Existence must be checked separately: SQLite fails to PREPARE a statement naming a missing
        // table, so a guard inside the same query would never run.
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
            exists.Parameters.AddWithValue("$name", TableName);
            if (exists.ExecuteScalar() is null)
                return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Timestamp FROM \"{TableName}\" LIMIT 1";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}
