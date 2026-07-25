using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Tests;

/// <summary>TI-P2-22 #8 (the automatable core of the manual matrix): the uninstall orchestration —
/// ordered, failure-tolerant, MainguardEnv-scoped, and G-12 (personal distros untouched).</summary>
public class UninstallerTests
{
    private sealed class FakeWsl : IWslRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        private bool _terminated;

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            if (args.SequenceEqual(WslCommands.Terminate()))
            {
                _terminated = true;
                return Task.FromResult(new WslRunResult(0, "", ""));
            }
            if (args.SequenceEqual(WslCommands.ListRunning()))
            {
                // A personal distro (Ubuntu) is running the whole time; MainguardEnv stops after terminate.
                var running = _terminated ? "Ubuntu\n" : "Ubuntu\nMainguardEnv\n";
                return Task.FromResult(new WslRunResult(0, running, ""));
            }
            return Task.FromResult(new WslRunResult(0, "", ""));
        }
    }

    private sealed class FakeRegistry : IRegistryCommandRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        public Task<bool> RunAsync(IReadOnlyList<string> regArgs, CancellationToken ct)
        {
            Calls.Add(regArgs);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeWslConfigFs : Mainguard.Agents.Agents.Bootstrap.IBootstrapFileSystem
    {
        public string? Content { get; set; }
        public int BackupCount { get; private set; }
        public int WriteCount { get; private set; }

        public string WslConfigPath => @"C:\Users\test\.wslconfig";
        public long TotalPhysicalMemoryBytes => 16L * 1024 * 1024 * 1024;
        public string? ReadWslConfig() => Content;
        public void BackupWslConfig() => BackupCount++;
        public void WriteWslConfig(string content)
        {
            Assert.True(BackupCount > 0, "the backup must be written BEFORE any .wslconfig write");
            WriteCount++;
            Content = content;
        }

        public bool FileExists(string path) => false;
    }

    private static Uninstaller Build(FakeWsl wsl, FakeRegistry reg,
        Func<bool, CancellationToken, Task>? removeAppData = null,
        Mainguard.Agents.Agents.Bootstrap.IBootstrapFileSystem? wslConfigFs = null) =>
        new(wsl, reg,
            stopDaemon: _ => Task.CompletedTask,
            removeScheduledTasks: _ => Task.CompletedTask,
            removeAppData: removeAppData ?? ((_, _) => Task.CompletedTask),
            wslConfigFs: wslConfigFs,
            terminatePollDelay: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task Run_ShouldExecuteOrderedSteps_AndUnregisterOnlyMainguardEnv()
    {
        var wsl = new FakeWsl();
        var reg = new FakeRegistry();
        var report = await Build(wsl, reg).RunAsync(new UninstallOptions());

        Assert.True(report.Clean);
        Assert.True(report.DistroUnregistered);
        Assert.Equal(
            new[] { "stop-daemon", "terminate-distro", "poll-stopped", "unregister-distro", "remove-registry", "remove-scheduled-tasks", "remove-appdata" },
            report.StepsRun);

        // Only MainguardEnv was terminated/unregistered.
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(WslCommands.Terminate()));
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(WslCommands.Unregister()));
        Assert.All(wsl.Calls, c => Assert.All(c, a => Assert.NotEqual("--shutdown", a)));
    }

    // ---- Audit fix #12: uninstall reverts Mainguard's global .wslconfig keys (backed up first) ------

    [Fact]
    public async Task Run_WithWslConfigFs_RevertsOurKeys_BackupFirst()
    {
        var fs = new FakeWslConfigFs { Content = "[wsl2]\nprocessors=4\nmemory=8GB\nautoMemoryReclaim=gradual\n" };
        var report = await Build(new FakeWsl(), new FakeRegistry(), wslConfigFs: fs)
            .RunAsync(new UninstallOptions());

        Assert.True(report.Clean);
        Assert.Contains("revert-wslconfig", report.StepsRun);
        Assert.Equal(1, fs.BackupCount);
        Assert.Equal("[wsl2]\nprocessors=4\n", fs.Content);
    }

    [Fact]
    public async Task Run_WithNothingOfOursInWslConfig_WritesNothing()
    {
        var fs = new FakeWslConfigFs { Content = "[wsl2]\nmemory=12000MB\n" };
        await Build(new FakeWsl(), new FakeRegistry(), wslConfigFs: fs).RunAsync(new UninstallOptions());

        Assert.Equal(0, fs.WriteCount);
        Assert.Equal(0, fs.BackupCount);
    }

    [Fact]
    public async Task Run_ShouldLeavePersonalDistrosRunning_G12()
    {
        var wsl = new FakeWsl();
        var report = await Build(wsl, new FakeRegistry()).RunAsync(new UninstallOptions());

        Assert.Equal(new[] { "Ubuntu" }, report.PersonalDistrosBefore);
        Assert.Contains("Ubuntu", report.RunningDistrosAfter);      // personal distro still running
        Assert.DoesNotContain("MainguardEnv", report.RunningDistrosAfter); // ours stopped
    }

    [Fact]
    public async Task Run_ShouldRemoveIntegrationRegistryKeys()
    {
        var reg = new FakeRegistry();
        await Build(new FakeWsl(), reg).RunAsync(new UninstallOptions());

        var deletedKeys = reg.Calls.Select(c => c[1]).ToArray();
        foreach (var owned in WindowsIntegration.OwnedKeyRoots())
            Assert.Contains(owned, deletedKeys);
    }

    [Fact]
    public async Task Run_ShouldBeFailureTolerant_ContinuingPastAThrowingStep()
    {
        var wsl = new FakeWsl();
        var reg = new FakeRegistry();
        Func<bool, CancellationToken, Task> boom = (_, _) => throw new InvalidOperationException("disk busy");

        var report = await Build(wsl, reg, removeAppData: boom).RunAsync(new UninstallOptions());

        Assert.False(report.Clean);
        Assert.Single(report.Errors);
        // The unregister still happened despite the later appdata failure.
        Assert.True(report.DistroUnregistered);
        Assert.Contains("remove-appdata", report.StepsRun);
    }

    [Fact]
    public async Task Run_WithRemoveSyncRemote_ShouldAppendThatStep()
    {
        var report = await Build(new FakeWsl(), new FakeRegistry())
            .RunAsync(new UninstallOptions(RemoveSyncRemote: true));

        Assert.Contains("remove-sync-remote", report.StepsRun);
    }

    // Q2 default-OFF gating: the sync-remote delegate is NOT invoked unless the user opts in, and the
    // step is only appended when they do.
    [Fact]
    public async Task Run_SyncRemoteDelegate_IsInvokedOnlyWhenOptedIn()
    {
        var invocations = 0;
        Func<CancellationToken, Task> removeSyncRemote = _ => { invocations++; return Task.CompletedTask; };

        Uninstaller BuildWithDelegate() => new(
            new FakeWsl(), new FakeRegistry(),
            stopDaemon: _ => Task.CompletedTask,
            removeScheduledTasks: _ => Task.CompletedTask,
            removeAppData: (_, _) => Task.CompletedTask,
            removeSyncRemote: removeSyncRemote,
            terminatePollDelay: TimeSpan.FromMilliseconds(1));

        // Default off: delegate never runs, step absent.
        var offReport = await BuildWithDelegate().RunAsync(new UninstallOptions());
        Assert.Equal(0, invocations);
        Assert.DoesNotContain("remove-sync-remote", offReport.StepsRun);

        // Opted in: delegate runs exactly once, step present.
        var onReport = await BuildWithDelegate().RunAsync(new UninstallOptions(RemoveSyncRemote: true));
        Assert.Equal(1, invocations);
        Assert.Contains("remove-sync-remote", onReport.StepsRun);
    }

    // MG-34: the daemon-stop step must prefer systemd and, when it falls back to killing the process,
    // match the process NAME exactly. `pkill -f mainguardd` matched any command line merely CONTAINING
    // the string — an editor holding mainguardd.service open, a `tail -f` on its log, a grep — and it
    // runs as root inside the distro, so a fuzzy match killed the user's unrelated processes during an
    // uninstall. Same rule the presence probes already follow (`pgrep -x`, audit fix #10).
    [Fact]
    public void StopDaemonSequence_ShouldPreferSystemd_AndNeverFuzzyMatchProcessCommandLines()
    {
        var sequence = UninstallDaemonCommands.StopSequence();
        Assert.Equal(2, sequence.Count);

        // 1. systemd first — the clean stop that also reaps anything in the unit's cgroup.
        Assert.Equal(DaemonUpdateCommands.StopUnit(), sequence[0]);
        Assert.Contains("systemctl", sequence[0]);
        Assert.Contains("stop", sequence[0]);

        // 2. Only then the last-resort kill, EXACT-name matched.
        var kill = sequence[1];
        Assert.Contains("pkill", kill);
        Assert.Contains("-x", kill);
        Assert.DoesNotContain("-f", kill);
        Assert.Equal(DaemonUpdateCommands.UnitName, kill[^1]);

        // G-12 still holds: every command is scoped to our distro, and the VM-wide shutdown verb is
        // never emitted (it would stop the user's personal distros too).
        Assert.All(sequence, c =>
        {
            Assert.Contains(WslCommands.DistroName, c);
            Assert.All(c, a => Assert.NotEqual("--shutdown", a));
        });
    }
}
