using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Mainguard.Agents.Daemon;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The per-subsystem file sink: category→file routing, size-capped rolling, the single-line format,
/// mask fidelity (a pre-masked line stays secret-free), and the "diagnostics never throw" contract.
/// Also pins the <c>DaemonLogCategories</c> ↔ <c>DaemonLogSubsystems</c> equivalence (the extension
/// point a new subsystem must keep in step).
/// </summary>
public sealed class SubsystemFileLoggerProviderTests : IDisposable
{
    private readonly string _dir;

    public SubsystemFileLoggerProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gl-logtests-" + Guid.NewGuid().ToString("N")[..10]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Categories_And_Subsystems_StayInLockstep()
    {
        // Every category maps to a canonical subsystem, and the two lists are the same set/order — a new
        // daemon subsystem that adds only one of the pair is a bug this test catches.
        Assert.Equal(
            DaemonLogSubsystems.All.ToArray(),
            DaemonLogCategories.All.Select(DaemonLogCategories.Subsystem).ToArray());
    }

    [Fact]
    public void RoutesEachCategory_ToItsOwnFile()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir);
        provider.CreateLogger(DaemonLogCategories.Spawn).LogInformation("spawn happened");
        provider.CreateLogger(DaemonLogCategories.Migration).LogInformation("migrate happened");

        var spawn = ReadAll("spawn.log");
        var migration = ReadAll("migration.log");
        Assert.Contains("spawn happened", spawn);
        Assert.Contains("migrate happened", migration);
        // No cross-contamination.
        Assert.DoesNotContain("migrate happened", spawn);
        Assert.DoesNotContain("spawn happened", migration);
    }

    [Fact]
    public void LineFormat_IsTimestamp_Level_Subsystem_Scope_Message()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir);
        var logger = provider.CreateLogger(DaemonLogCategories.Spawn);
        using (logger.BeginScope("agent-7"))
        {
            logger.LogInformation("worktree ready");
        }

        var line = ReadAll("spawn.log").Split('\n').First(l => l.Contains("worktree ready", StringComparison.Ordinal));
        // {ts:O} [INF] [spawn] (agent-7) worktree ready
        Assert.Matches(@"^\S+ \[INF\] \[spawn\] \(agent-7\) worktree ready$", line.TrimEnd('\r'));
    }

    [Fact]
    public void RollsAtMaxBytes_KeepingTheRolledFile()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir, maxBytes: 256, maxRoll: 2);
        var logger = provider.CreateLogger(DaemonLogCategories.Spawn);
        for (var i = 0; i < 20; i++)
            logger.LogInformation("line {Index} padded to force a roll over the tiny cap", i);

        Assert.True(File.Exists(Path.Combine(_dir, "spawn.log")), "the live file exists");
        Assert.True(File.Exists(Path.Combine(_dir, "spawn.1.log")), "a rolled file was produced at the cap");
        // maxRoll=2 → never more than spawn.log + spawn.1.log + spawn.2.log.
        Assert.False(File.Exists(Path.Combine(_dir, "spawn.3.log")), "rolling is bounded by maxRoll");
    }

    [Fact]
    public void MaskedLine_StaysSecretFree()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir);
        // The provider writes exactly what it is given — an upstream-masked line stays masked; it never
        // re-exposes the raw value.
        provider.CreateLogger(DaemonLogCategories.Rpc)
            .LogInformation("rpc-begin request=model_api_key=*** kind=claude-code");

        var rpc = ReadAll("rpc.log");
        Assert.Contains("model_api_key=***", rpc);
        Assert.DoesNotContain("SUPER-SECRET", rpc);
    }

    [Fact]
    public void BelowMinLevel_IsNotWritten()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir, minLevel: LogLevel.Information);
        var logger = provider.CreateLogger(DaemonLogCategories.Spawn);
        logger.LogDebug("a debug line under the floor");
        logger.LogInformation("an info line at the floor");

        var spawn = ReadAll("spawn.log");
        Assert.DoesNotContain("a debug line", spawn);
        Assert.Contains("an info line", spawn);
    }

    /// <summary>
    /// F56 — <c>rpc.log</c> was created under the process umask (0022 by default), i.e.
    /// world-readable, in a directory that was equally open. Every log file and the directory that
    /// holds them are now owner-only from the moment they exist.
    /// </summary>
    [UnixOnlyFact("log-file permissions are a POSIX mode; Windows uses a single-ACE DACL instead")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void LogFilesAndDirectory_AreCreatedOwnerOnly()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir);
        provider.CreateLogger(DaemonLogCategories.Rpc).LogInformation("rpc-begin method=/x peer=y");

        var dirMode = File.GetUnixFileMode(_dir);
        Assert.False(dirMode.HasFlag(UnixFileMode.GroupRead), "the logs directory must not be group-readable");
        Assert.False(dirMode.HasFlag(UnixFileMode.OtherRead), "the logs directory must not be world-readable");
        Assert.False(dirMode.HasFlag(UnixFileMode.OtherExecute), "the logs directory must not be world-traversable");

        var fileMode = File.GetUnixFileMode(Path.Combine(_dir, "rpc.log"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    /// <summary>The mode has to be right on the FIRST line, not fixed up afterwards: a log file that
    /// spends even one write world-readable has already published that write.</summary>
    [UnixOnlyFact("log-file permissions are a POSIX mode")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void FirstLine_LandsInAnAlreadyRestrictedFile()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir);
        var logger = provider.CreateLogger(DaemonLogCategories.Spawn);
        logger.LogInformation("the very first line");

        var path = Path.Combine(_dir, "spawn.log");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains("the very first line", File.ReadAllText(path));
    }

    /// <summary>A rolled file must not become the readable copy of what the live file protects.</summary>
    [UnixOnlyFact("log-file permissions are a POSIX mode")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void RolledFiles_StayOwnerOnly()
    {
        using var provider = new SubsystemFileLoggerProvider(_dir, maxBytes: 256, maxRoll: 2);
        var logger = provider.CreateLogger(DaemonLogCategories.Spawn);
        for (var i = 0; i < 20; i++)
            logger.LogInformation("line {Index} padded to force a roll over the tiny cap", i);

        var rolled = Path.Combine(_dir, "spawn.1.log");
        Assert.True(File.Exists(rolled));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rolled));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_dir, "spawn.log")));
    }

    [Fact]
    public void DirectoryCreateFailure_IsSwallowed()
    {
        // Point the logs dir at a path UNDER a regular file — CreateDirectory can't succeed there, and a
        // subsequent write can't either; both must be swallowed (diagnostics never break the daemon).
        var blocker = Path.Combine(Path.GetTempPath(), "gl-logblock-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var provider = new SubsystemFileLoggerProvider(Path.Combine(blocker, "logs"));
            var ex = Record.Exception(() =>
            {
                provider.CreateLogger(DaemonLogCategories.Spawn).LogError("should not throw");
                provider.Dispose();
            });
            Assert.Null(ex);
        }
        finally
        {
            try { File.Delete(blocker); } catch { /* best effort */ }
        }
    }

    private string ReadAll(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
