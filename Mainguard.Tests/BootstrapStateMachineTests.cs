using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-05 #4-#7 / plan §6 #3-#8: the state machine (idempotency, resume, typed failure, WSL-not-
// installed gate, G-12 no-shutdown, UTF-16 list parsing, and the G2 ptrace-scope provisioning) with
// the check/act seams mocked. No real wsl.exe is invoked.
public class BootstrapStateMachineTests
{
    // ---- §6 #3 (TI #4): all-satisfied → zero acts (re-run is a no-op) ----------------------------

    [Fact]
    public async Task StateMachine_SkipsSatisfiedSteps()
    {
        var a = new FakeStep("a") { Satisfied = true };
        var b = new FakeStep("b") { Satisfied = true };
        var c = new FakeStep("c") { Satisfied = true };
        var boot = new MainguardOsBootstrapper(new IBootstrapStep[] { a, b, c });

        await boot.RunAsync(progress: null, CancellationToken.None);

        Assert.Equal(0, a.ExecuteCount);
        Assert.Equal(0, b.ExecuteCount);
        Assert.Equal(0, c.ExecuteCount);
    }

    // The same invariant through the REAL default step chain against a fully-satisfied environment:
    // no import, no .wslconfig write — genuinely zero mutations.
    [Fact]
    public async Task StateMachine_HealthyMachine_IsFullNoOp()
    {
        var runner = new RecordingWslRunner
        {
            Responder = args =>
            {
                if (Contains(args, "--list")) return Ok("MainguardEnv\nUbuntu\n");
                if (Contains(args, "/proc/sys/kernel/yama/ptrace_scope")) return Ok("2");
                if (Contains(args, "/proc/sys/fs/inotify/max_user_watches")) return Ok("524288");
                if (IsUsernsProbe(args)) return Ok(GreenUsernsProbe);
                if (Contains(args, "docker") && Contains(args, "info")) return Ok("Server Version: 27");
                if (Contains(args, "cat") && Contains(args, "/etc/docker/daemon.json")) return Ok(FirstBootStep.DockerDaemonJson);
                if (Contains(args, "pgrep")) return Ok("42");   // daemon already running
                return Ok("");
            },
        };
        // .wslconfig already carries our keys → merge is a no-op → step satisfied.
        var existing = "[wsl2]\nmemory=6GB\nautoMemoryReclaim=gradual\n";
        var fs = new FakeFileSystem { WslConfigContent = existing, TarballPresent = true };
        var probe = new FakeHealthProbe { Healthy = true };
        var ctx = new BootstrapContext(runner, fs, probe, SampleOptions());

        await MainguardOsBootstrapper.Create(ctx).RunAsync(null, CancellationToken.None);

        Assert.False(runner.Calls.Any(c => Contains(c, "--import")), "healthy machine must not re-import");
        Assert.Equal(0, fs.WriteCount);
        Assert.Equal(0, fs.BackupCount);
    }

    // ---- §6 #4 (TI #5): resume after a failure ---------------------------------------------------

    [Fact]
    public async Task StateMachine_ResumesAfterFailure()
    {
        var s1 = new FakeStep("s1") { Satisfied = true };                 // already done
        var s2 = new FakeStep("s2") { Satisfied = true };                 // already done
        var s3 = new FakeStep("s3") { Satisfied = false, ThrowOnExecute = true };
        var s4 = new FakeStep("s4") { Satisfied = false };
        var boot = new MainguardOsBootstrapper(new IBootstrapStep[] { s1, s2, s3, s4 });

        // First run: s3 fails.
        await Assert.ThrowsAsync<BootstrapException>(() => boot.RunAsync(null, CancellationToken.None));
        Assert.Equal(0, s1.ExecuteCount);
        Assert.Equal(0, s2.ExecuteCount);
        Assert.Equal(1, s3.ExecuteCount);
        Assert.Equal(0, s4.ExecuteCount);   // never reached

        // Fix s3; rerun resumes at s3 (s1/s2 still skipped), then completes s4.
        s3.ThrowOnExecute = false;
        s3.SatisfiedAfterExecute = true;
        s4.SatisfiedAfterExecute = true;

        await boot.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, s1.ExecuteCount);   // still skipped on resume
        Assert.Equal(0, s2.ExecuteCount);
        Assert.Equal(2, s3.ExecuteCount);   // executed once more on resume
        Assert.Equal(1, s4.ExecuteCount);
    }

    // ---- §6 #5: typed failure names the failing stage --------------------------------------------

    [Fact]
    public async Task StateMachine_FailureCarriesStepName()
    {
        var s1 = new FakeStep("Detect") { Satisfied = true };
        var s2 = new FakeStep("Import MainguardEnv") { Satisfied = false, ThrowOnExecute = true };
        var boot = new MainguardOsBootstrapper(new IBootstrapStep[] { s1, s2 });

        var ex = await Assert.ThrowsAsync<BootstrapException>(() => boot.RunAsync(null, CancellationToken.None));
        Assert.Equal("Import MainguardEnv", ex.StepName);
    }

    [Fact]
    public async Task StateMachine_StepThatDoesNotConverge_FailsTypedWithName()
    {
        // Executes without throwing but never becomes satisfied → the re-verify catches it.
        var s = new FakeStep("Stuck") { Satisfied = false, SatisfiedAfterExecute = false };
        var boot = new MainguardOsBootstrapper(new IBootstrapStep[] { s });

        var ex = await Assert.ThrowsAsync<BootstrapException>(() => boot.RunAsync(null, CancellationToken.None));
        Assert.Equal("Stuck", ex.StepName);
    }

    // ---- §6 #6 (TI #6): WSL not installed → actionable failure BEFORE any act --------------------

    [Fact]
    public async Task StateMachine_WslNotInstalled_ShouldFailActionable_BeforeAnyAct()
    {
        var runner = new RecordingWslRunner { ThrowNotInstalled = true };
        var later = new FakeStep("later") { Satisfied = false };
        var boot = new MainguardOsBootstrapper(new IBootstrapStep[]
        {
            new DetectDistroStep(runner),
            later,
        });

        var ex = await Assert.ThrowsAsync<WslNotInstalledException>(() => boot.RunAsync(null, CancellationToken.None));
        Assert.Contains("WSL2 is not installed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, later.ExecuteCount);   // no act on any step
        Assert.DoesNotContain(runner.Calls, c => Contains(c, "--import"));
    }

    // ---- §6 #7 (TI #7): G-12 — no builder ever emits the VM-wide shutdown verb -------------------

    [Fact]
    public void Lifecycle_ShouldNeverEmitShutdown()
    {
        const string shutdown = "--shutdown";
        foreach (var builder in WslCommands.AllBuilders())
            Assert.DoesNotContain(shutdown, builder);

        // Lifecycle is scoped to our distro only.
        Assert.Equal(new[] { "--terminate", "MainguardEnv" }, WslCommands.Terminate());
        Assert.Equal(new[] { "--unregister", "MainguardEnv" }, WslCommands.Unregister());
    }

    // Source-grep guard: the literal must be absent from Core and Server entirely (mirrors the
    // reviewer grep in the plan §7; an analyzer-free belt-and-braces of Lifecycle_ShouldNeverEmit).
    [Fact]
    public void NoShutdownAnywhere_InCoreOrServer()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "Mainguard.Agents", "Mainguard.Server" })
        {
            var dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                if (File.ReadAllText(file).Contains("--shutdown", StringComparison.Ordinal))
                    offenders.Add(file);
            }
        }

        Assert.True(offenders.Count == 0, "G-12 violation — '--shutdown' found in: " + string.Join(", ", offenders));
    }

    // ---- §6 #6 (TI #6b): UTF-16LE --list --quiet parsing -----------------------------------------

    [Fact]
    public void WslRunner_Utf16ListParsing()
    {
        // wsl.exe emits UTF-16LE with a BOM and trailing NUL padding. Simulate that byte stream and
        // decode it exactly as WslRunner does (Encoding.Unicode), then parse.
        var raw = "﻿MainguardEnv\r\nUbuntu-22.04\r\ndocker-desktop\r\n\0";
        var bytes = Encoding.Unicode.GetBytes(raw);
        var decoded = Encoding.Unicode.GetString(bytes);

        var distros = WslRunner.ParseDistroList(decoded);

        Assert.Equal(new[] { "MainguardEnv", "Ubuntu-22.04", "docker-desktop" }, distros);
    }

    [Fact]
    public void WslRunner_ParseDistroList_EmptyOrWhitespace_YieldsNothing()
    {
        Assert.Empty(WslRunner.ParseDistroList(""));
        Assert.Empty(WslRunner.ParseDistroList("\0﻿\r\n  \r\n"));
    }

    // ---- §6 #8: G2 control (2) — FirstBootStep provisions kernel.yama.ptrace_scope=2 -------------

    [Fact]
    public async Task FirstBootStep_ShouldProvisionPtraceScope2()
    {
        var runner = new RecordingWslRunner
        {
            Responder = args =>
            {
                // MG-17 probe green (checked BEFORE the generic docker-info arm, which would otherwise
                // swallow it — the probe runs `bash -c "… docker info --format …"`).
                if (IsUsernsProbe(args)) return Ok(GreenUsernsProbe);
                // docker info green immediately so the poll ends on the first attempt.
                if (Contains(args, "docker") && Contains(args, "info")) return Ok("Server Version: 27");
                // /proc reads for the final invariant check must report the hardened values.
                if (Contains(args, "/proc/sys/kernel/yama/ptrace_scope")) return Ok("2");
                if (Contains(args, "/proc/sys/fs/inotify/max_user_watches")) return Ok("524288");
                return Ok("");
            },
        };
        var step = new FirstBootStep(runner, dockerPollAttempts: 1, dockerPollDelay: TimeSpan.Zero);

        await step.ExecuteAsync(new Progress<string>(_ => { }), CancellationToken.None);

        // The sysctls are applied by writing /proc/sys directly (the payload has no `sysctl` binary).
        var ptraceIdx = runner.Calls.FindIndex(c => c.Contains("/proc/sys/kernel/yama/ptrace_scope"));
        Assert.True(ptraceIdx >= 0, "expected a write to /proc/sys/kernel/yama/ptrace_scope");
        Assert.Equal("2", runner.Stdins[ptraceIdx]);
        var inotifyIdx = runner.Calls.FindIndex(c => c.Contains("/proc/sys/fs/inotify/max_user_watches"));
        Assert.True(inotifyIdx >= 0, "expected a write to /proc/sys/fs/inotify/max_user_watches");
        Assert.Equal("524288", runner.Stdins[inotifyIdx]);

        // …and BOTH are persisted to /etc/sysctl.d/ (survives VM restart, applied on boot by
        // systemd-sysctl). Find the tee-to-drop-in invocation and confirm its stdin carries the pin.
        var dropInIndex = runner.Calls.FindIndex(c => c.Contains(FirstBootStep.SysctlDropInPath));
        Assert.True(dropInIndex >= 0, "expected a write to " + FirstBootStep.SysctlDropInPath);
        Assert.Contains("kernel.yama.ptrace_scope=2", runner.Stdins[dropInIndex]!, StringComparison.Ordinal);

        // The persistence write runs as root.
        Assert.Contains("-u", runner.Calls[dropInIndex]);
        Assert.Contains("root", runner.Calls[dropInIndex]);

        // ---- MG-17, on the SAME act phase --------------------------------------------------------
        // The subordinate ranges must be pinned BEFORE dockerd is restarted with userns-remap in
        // daemon.json: dockerd refuses to start when the remap user has no range, so the wrong order
        // takes the whole VM down instead of merely leaving the remap off.
        var subuidIdx = runner.Calls.FindIndex(c => c.Contains(UsernsRemapPolicy.SubuidPath));
        var subgidIdx = runner.Calls.FindIndex(c => c.Contains(UsernsRemapPolicy.SubgidPath));
        Assert.True(subuidIdx >= 0 && subgidIdx >= 0, "expected /etc/subuid + /etc/subgid to be written");
        Assert.Equal(UsernsRemapPolicy.SubidFileContent, runner.Stdins[subuidIdx]);
        Assert.Equal(UsernsRemapPolicy.SubidFileContent, runner.Stdins[subgidIdx]);

        var daemonJsonIdx = runner.Calls.FindIndex(c => c.Contains("tee") && c.Contains("/etc/docker/daemon.json"));
        Assert.True(daemonJsonIdx >= 0, "expected /etc/docker/daemon.json to be written");
        Assert.Contains("\"userns-remap\": \"mainguard\"", runner.Stdins[daemonJsonIdx]!, StringComparison.Ordinal);
        Assert.True(subuidIdx < daemonJsonIdx, "/etc/subuid must be written before daemon.json enables the remap");

        // The mount sources the jail bind-mounts are brought to the shared-ownership invariant, and the
        // shared group is provisioned, on the same act.
        Assert.Contains(runner.Calls, c => c.Any(a => a.Contains("groupadd -g 101000 mainguard-jail", StringComparison.Ordinal)));
        Assert.Contains(runner.Calls, c => c.Any(a => a.Contains("worktrees", StringComparison.Ordinal) && a.Contains("g+rwX", StringComparison.Ordinal)));
    }

    /// <summary>The MG-17 boot probe: `bash -c &lt;UsernsRemapPolicy.ProbeScript&gt;`. Matched on the
    /// script itself so it can never be confused with the plain `docker info` readiness poll.</summary>
    private static bool IsUsernsProbe(IReadOnlyList<string> args) =>
        args.Any(a => a.Contains("MGGROUPS[", StringComparison.Ordinal));

    /// <summary>What the probe prints on a correctly provisioned VM.</summary>
    private const string GreenUsernsProbe =
        "MGUSERNS[name=seccomp,profile=builtin;name=cgroupns;name=userns;]"
        + "MGROOT[/var/lib/docker/100000.100000]MGGROUPS[mainguard docker mainguard-jail]";

    [Fact]
    public async Task FirstBootStep_CheckPhase_RequiresPtraceScopeAtLeast2()
    {
        // ptrace_scope regressed to 1 → NOT satisfied (re-provisions).
        Assert.False(await new FirstBootStep(Vm(ptrace: "1", userns: GreenUsernsProbe)).IsSatisfiedAsync(CancellationToken.None));

        // ptrace_scope=2, watches raised, docker green, userns remapped → satisfied.
        Assert.True(await new FirstBootStep(Vm(ptrace: "2", userns: GreenUsernsProbe)).IsSatisfiedAsync(CancellationToken.None));
    }

    // ---- MG-17: the check phase asserts the remap is IN EFFECT, not merely configured -------------

    [Fact]
    public async Task FirstBootStep_CheckPhase_RequiresTheUsernsRemapToBeInEffect()
    {
        // Every other invariant green; only the remap is missing → NOT satisfied, so the step
        // re-provisions. This is the exact state the audit found: a healthy VM with no remap at all.
        var unremapped = Vm(ptrace: "2", userns:
            "MGUSERNS[name=seccomp,profile=builtin;name=cgroupns;]MGROOT[/var/lib/docker]MGGROUPS[mainguard docker]");
        Assert.False(await new FirstBootStep(unremapped).IsSatisfiedAsync(CancellationToken.None));

        // …and the probe answering NOTHING (a failed `docker info`, a distro that did not respond) is
        // also unsatisfied. This is the vacuity guard: the check must never be able to pass because it
        // could not observe anything.
        Assert.False(await new FirstBootStep(Vm(ptrace: "2", userns: "")).IsSatisfiedAsync(CancellationToken.None));
    }

    /// <summary>A VM whose every invariant is green except the ones named.</summary>
    private static RecordingWslRunner Vm(string ptrace, string userns) => new()
    {
        Responder = args =>
        {
            if (IsUsernsProbe(args)) return Ok(userns);
            if (Contains(args, "/proc/sys/kernel/yama/ptrace_scope")) return Ok(ptrace);
            if (Contains(args, "/proc/sys/fs/inotify/max_user_watches")) return Ok("524288");
            if (Contains(args, "docker") && Contains(args, "info")) return Ok("ok");
            return Ok("");
        },
    };

    // ---- Import edge rows (§4): tarball missing / partial-import cleanup --------------------------

    [Fact]
    public async Task ImportDistroStep_MissingTarball_FailsTypedWithPath()
    {
        var runner = new RecordingWslRunner { Responder = _ => Ok("Ubuntu\n") };   // MainguardEnv absent
        var fs = new FakeFileSystem { TarballPresent = false };
        var options = SampleOptions();
        var step = new ImportDistroStep(runner, fs, options);

        Assert.False(await step.IsSatisfiedAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<BootstrapException>(
            () => step.ExecuteAsync(new Progress<string>(_ => { }), CancellationToken.None));
        Assert.Contains(options.TarballPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportDistroStep_FailedImport_UnregistersPartialBeforeThrow()
    {
        var runner = new RecordingWslRunner
        {
            Responder = args => Contains(args, "--import")
                ? new WslRunResult(1, "", "import blew up")
                : Ok("Ubuntu\n"),
        };
        var fs = new FakeFileSystem { TarballPresent = true };
        var step = new ImportDistroStep(runner, fs, SampleOptions());

        await Assert.ThrowsAsync<BootstrapException>(
            () => step.ExecuteAsync(new Progress<string>(_ => { }), CancellationToken.None));

        // Partial import cleaned up (edge row 4).
        Assert.Contains(runner.Calls, c => Contains(c, "--unregister"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static BootstrapOptions SampleOptions() =>
        new(@"C:\Mainguard\vm", @"C:\Mainguard\payload\mainguardos.tar.gz");

    private static bool Contains(IReadOnlyList<string> args, string token) => args.Contains(token);

    private static WslRunResult Ok(string stdout) => new(0, stdout, "");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? AppContext.BaseDirectory;
    }

    private sealed class FakeStep : IBootstrapStep
    {
        public FakeStep(string name) => Name = name;
        public string Name { get; }
        public bool Satisfied { get; set; }
        public bool SatisfiedAfterExecute { get; set; }
        public bool ThrowOnExecute { get; set; }
        public int ExecuteCount { get; private set; }

        public Task<bool> IsSatisfiedAsync(CancellationToken ct) => Task.FromResult(Satisfied);

        public Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
        {
            ExecuteCount++;
            if (ThrowOnExecute)
                throw new BootstrapException(Name, $"{Name} boom");
            Satisfied = SatisfiedAfterExecute;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWslRunner : IWslRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public List<string?> Stdins { get; } = new();
        public Func<IReadOnlyList<string>, WslRunResult>? Responder { get; set; }
        public bool ThrowNotInstalled { get; set; }

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            if (ThrowNotInstalled)
                throw new WslNotInstalledException();
            Calls.Add(args);
            Stdins.Add(stdin);
            return Task.FromResult(Responder?.Invoke(args) ?? new WslRunResult(0, "", ""));
        }
    }

    private sealed class FakeFileSystem : IBootstrapFileSystem
    {
        public string? WslConfigContent { get; set; }
        public bool TarballPresent { get; set; }
        public int WriteCount { get; private set; }
        public int BackupCount { get; private set; }

        public string WslConfigPath => @"C:\Users\test\.wslconfig";
        public long TotalPhysicalMemoryBytes => 16L * 1024 * 1024 * 1024;

        public string? ReadWslConfig() => WslConfigContent;
        public void BackupWslConfig() => BackupCount++;
        public void WriteWslConfig(string content) { WriteCount++; WslConfigContent = content; }
        public bool FileExists(string path) => TarballPresent;
    }

    private sealed class FakeHealthProbe : IDaemonHealthProbe
    {
        public bool Healthy { get; set; }
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(Healthy);
    }
}
