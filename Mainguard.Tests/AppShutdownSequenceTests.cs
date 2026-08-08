using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The Core shutdown orchestrator over a fake environment: order, the StopVmOnExit on/off legs, and
/// the reentrancy guard (a second exit request must not double-run the teardown). Tray-hide never
/// reaching here is a property of the App's interception, asserted in the ViewModel/App layer, not
/// this pure sequence.
/// </summary>
public class AppShutdownSequenceTests
{
    private sealed class Recorder : IProgress<string>
    {
        public readonly List<string> Lines = new();

        public void Report(string value) => Lines.Add(value);
    }

    private sealed class FakeEnv : IAppShutdownEnvironment
    {
        public readonly List<string> Calls = new();
        public bool StopVmOnExitValue;
        public bool ProvisioningInFlightValue;
        public int ReleaseCount;
        public int StopCount;

        public bool StopVmOnExit => StopVmOnExitValue;

        public bool SandboxProvisioningInFlight => ProvisioningInFlightValue;

        public void ReleaseKeepAlive()
        {
            Calls.Add("Release");
            ReleaseCount++;
        }

        public Task StopVmAsync(CancellationToken ct)
        {
            Calls.Add("StopVm");
            StopCount++;
            return Task.CompletedTask;
        }

        public void Log(string message) => Calls.Add($"log:{message}");
    }

    [Fact]
    public async Task StopVmOnExit_on_releases_then_stops_in_order()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(new[]
        {
            ShutdownStatus.ReleasingKeepAlive,
            ShutdownStatus.StoppingVm,
            ShutdownStatus.Done,
        }, rec.Lines);
        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
        Assert.True(env.Calls.IndexOf("Release") < env.Calls.IndexOf("StopVm"),
            "keep-alive must be released before the VM stop");
    }

    [Fact]
    public async Task StopVmOnExit_off_releases_but_never_stops_the_vm()
    {
        var env = new FakeEnv { StopVmOnExitValue = false };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(new[]
        {
            ShutdownStatus.ReleasingKeepAlive,
            ShutdownStatus.Done,
        }, rec.Lines);
        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(0, env.StopCount);
    }

    [Fact]
    public async Task Second_exit_request_is_reentrancy_guarded_and_does_not_double_run()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var seq = new AppShutdownSequence(env);

        await seq.RunAsync(null, CancellationToken.None);
        Assert.True(seq.HasRun);

        await seq.RunAsync(null, CancellationToken.None); // second request

        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
    }

    /// <summary>
    /// The 2026-08-05 field failure, as a regression: StopVmOnExit is on AND a sandbox-image build is
    /// running. Terminating MainguardEnv here kills the in-flight docker build (the owner's oobe.log
    /// shows BuildFailed logged 10ms after "terminated MainguardEnv on exit", three runs in a row), so
    /// the images can never become current and the coordinator refuses to start on the next launch too.
    /// The terminate must be skipped — the keep-alive release still happens.
    /// </summary>
    [Fact]
    public async Task Image_build_in_flight_vetoes_the_StopVmOnExit_terminate()
    {
        var env = new FakeEnv { StopVmOnExitValue = true, ProvisioningInFlightValue = true };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(0, env.StopCount);
        Assert.DoesNotContain("StopVm", env.Calls);
        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(new[]
        {
            ShutdownStatus.ReleasingKeepAlive,
            ShutdownStatus.LeavingVmForImageBuild,
            ShutdownStatus.Done,
        }, rec.Lines);
    }

    /// <summary>The veto is scoped to the build: with nothing in flight the terminate still runs, so
    /// the fix cannot silently disable StopVmOnExit for everyone.</summary>
    [Fact]
    public async Task No_build_in_flight_still_stops_the_vm()
    {
        var env = new FakeEnv { StopVmOnExitValue = true, ProvisioningInFlightValue = false };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(1, env.StopCount);
        Assert.Contains(ShutdownStatus.StoppingVm, rec.Lines);
        Assert.DoesNotContain(ShutdownStatus.LeavingVmForImageBuild, rec.Lines);
    }

    [Fact]
    public async Task Concurrent_exit_requests_run_the_teardown_once()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var seq = new AppShutdownSequence(env);

        await Task.WhenAll(
            Task.Run(() => seq.RunAsync(null, CancellationToken.None)),
            Task.Run(() => seq.RunAsync(null, CancellationToken.None)));

        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
    }
}
