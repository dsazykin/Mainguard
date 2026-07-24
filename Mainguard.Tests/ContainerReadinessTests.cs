using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The readiness classification behind <c>EgressProxyConfigurator</c>'s wait-until-running gate.
///
/// <para><b>Why this exists.</b> <c>EnsureReadyAsync</c> exec'd the proxy config into the container
/// immediately after starting it. Docker's start call is asynchronous, so "started" and "running" are
/// different facts — when the container had not finished starting, the exec failed with an opaque
/// <c>409 "container … is not running"</c> raised from deep inside <c>ExecCreateContainerAsync</c>.
/// That is the intermittent failure that made the RequiresDocker suite red on <c>phase2</c> itself.</para>
///
/// <para>The fix waits for a verified Running state before exec'ing. The <b>decision</b> is extracted
/// here as a pure function precisely because the integration leg needs a real Docker daemon and cannot
/// run on every machine — this is the part that has to be right, so it is tested where it can be.</para>
/// </summary>
public sealed class ContainerReadinessTests
{
    [Fact]
    public void Running_IsReady()
    {
        var state = new ContainerState { Running = true };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Running,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    // "Created but not yet started" — the exact race that produced the 409. It must keep waiting,
    // never be mistaken for ready and never be torn down as a corpse.
    [Fact]
    public void CreatedButNotStarted_IsPending_NotReadyAndNotTerminal()
    {
        var state = new ContainerState { Running = false, FinishedAt = "0001-01-01T00:00:00Z" };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Pending,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    [Fact]
    public void Restarting_IsPending_NotTerminal()
    {
        var state = new ContainerState { Running = false, Restarting = true, FinishedAt = "2026-07-24T10:00:00Z" };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Pending,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    // A container that ran and exited will never come back on its own — the caller must recreate it
    // rather than wait out the full deadline.
    [Fact]
    public void ExitedNonZero_IsTerminal()
    {
        var state = new ContainerState { Running = false, ExitCode = 1, FinishedAt = "2026-07-24T10:00:00Z" };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Terminal,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    // Clean exit is still a corpse: tinyproxy exiting 0 leaves nothing to exec into.
    [Fact]
    public void ExitedZero_IsAlsoTerminal()
    {
        var state = new ContainerState { Running = false, ExitCode = 0, FinishedAt = "2026-07-24T10:00:00Z" };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Terminal,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    [Fact]
    public void Dead_IsTerminal()
    {
        var state = new ContainerState { Running = false, Dead = true };
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Terminal,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }

    [Fact]
    public void NullState_IsPending_NeverReady()
    {
        Assert.Equal(EgressProxyConfigurator.ContainerReadiness.Pending,
            EgressProxyConfigurator.ClassifyReadiness(null));
    }

    // The invariant that actually prevents the 409: nothing except a genuinely Running container may
    // ever classify as ready.
    [Theory]
    [InlineData(false, false, false, "0001-01-01T00:00:00Z")]
    [InlineData(false, true, false, "2026-07-24T10:00:00Z")]
    [InlineData(false, false, true, "2026-07-24T10:00:00Z")]
    [InlineData(false, false, false, "2026-07-24T10:00:00Z")]
    public void NothingButRunning_IsEverClassifiedReady(bool running, bool restarting, bool dead, string finishedAt)
    {
        var state = new ContainerState
        {
            Running = running,
            Restarting = restarting,
            Dead = dead,
            FinishedAt = finishedAt,
        };

        Assert.NotEqual(EgressProxyConfigurator.ContainerReadiness.Running,
            EgressProxyConfigurator.ClassifyReadiness(state));
    }
}
