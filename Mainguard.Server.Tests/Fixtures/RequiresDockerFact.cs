using System;
using System.Threading;
using Docker.DotNet;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless a Docker daemon is reachable AND the CI-built P2-07
/// images are present (TI-P2-07 §A.5: the RequiresDocker leg is PR-blocking in Linux CI, where the
/// images are built first — <c>images/mainguard-agent-base</c> / <c>images/mainguard-egress-proxy</c>, never
/// at runtime, G-16 — but a developer machine without them skips rather than fails on an image pull).
/// The probe runs once and is cached. Apply the CI category with a class-level
/// <c>[Trait("Category","RequiresDocker")]</c> alongside this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsReady)
            Skip = DockerAvailability.SkipReason;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless a Docker daemon is reachable — <b>daemon presence
/// only, no CI-built image required</b>. For RequiresDocker tests that stand up their own trivial
/// container (e.g. the P2-08 swarm-reconciler convergence test uses <c>busybox</c>, not the P2-07
/// agent-base image). Apply the CI category with a class-level <c>[Trait("Category","RequiresDocker")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerDaemonFactAttribute : FactAttribute
{
    public RequiresDockerDaemonFactAttribute()
    {
        if (!DockerAvailability.IsDaemonReady)
            Skip = DockerAvailability.DaemonSkipReason;
    }
}

/// <summary>
/// A <see cref="RequiresDockerFactAttribute"/> for a claim that is only TRUE on Docker Desktop — i.e.
/// macOS and Windows, where the engine runs in its own VM and <c>host-gateway</c> routes to the host's
/// loopback stack.
///
/// <para>It exists because the alternative is a test that fails on the Linux runner by design. On Linux
/// Docker Engine <c>host-gateway</c> is the docker0 address and a host listener bound to
/// <c>127.0.0.1</c> is simply not reachable from a container, so asserting the Docker Desktop property
/// unconditionally is asserting something false. The Linux half of the same contract is asserted by its
/// own test through <see cref="RequiresLinuxDockerEngineFactAttribute"/> — the point is that both
/// platforms are covered, not that one of them is skipped.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerDesktopFactAttribute : FactAttribute
{
    public RequiresDockerDesktopFactAttribute()
    {
        if (!DockerAvailability.IsReady)
        {
            Skip = DockerAvailability.SkipReason;
        }
        else if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Skip = "Docker Desktop only: on Linux Docker Engine host-gateway is the docker0 address, so a "
                 + "loopback-bound host listener is genuinely unreachable from a container.";
        }
    }
}

/// <summary>
/// The mirror of <see cref="RequiresDockerDesktopFactAttribute"/>: a claim that only holds on native
/// Linux Docker Engine — where the gateway binds the <c>docker0</c> address rather than loopback. This
/// is the production Linux/WSL2 path (the Pro daemon on Windows runs inside the WSL2 VM), so it is the
/// one the PR-blocking Linux CI leg actually exercises.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresLinuxDockerEngineFactAttribute : FactAttribute
{
    public RequiresLinuxDockerEngineFactAttribute()
    {
        if (!DockerAvailability.IsReady)
        {
            Skip = DockerAvailability.SkipReason;
        }
        else if (!OperatingSystem.IsLinux())
        {
            Skip = "Native Linux Docker Engine only: elsewhere the engine is in a VM and the daemon does "
                 + "not bind a bridge address at all.";
        }
    }
}

/// <summary>
/// MG-43 — a <see cref="RequiresDockerFactAttribute"/> that ALSO requires <c>MAINGUARD_VERIFY_E2E=1</c>.
///
/// <para>It gates the one test that runs a repository's real <c>.mainguard/verify</c> end to end inside
/// a jail: a full Release restore + build + test of <c>Mainguard.slnx</c>, which is a ~1.7 GB download
/// and tens of minutes. That is the right thing to measure and the wrong thing to put in every PR run,
/// so it is opt-in and its result is reported in the PR that changes the cache.</para>
///
/// <para>Skipping is expressed by setting <see cref="FactAttribute.Skip"/> from the constructor, NOT by
/// throwing: this repo is on xunit 2.9.3 (v2 core), where <c>Assert.Skip</c>/<c>SkipException.ForSkip</c>
/// does not exist as a skip at all and reports as a FAILURE.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerAndOptInFactAttribute : FactAttribute
{
    /// <summary>The environment variable that opts a run in.</summary>
    public const string OptInVariable = "MAINGUARD_VERIFY_E2E";

    public RequiresDockerAndOptInFactAttribute()
    {
        if (!DockerAvailability.IsReady)
        {
            Skip = DockerAvailability.SkipReason;
        }
        else if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Set {OptInVariable}=1 to run the full in-jail verification (minutes, ~1.7 GB of packages).";
        }
    }
}

internal static class DockerAvailability
{
    /// <summary>The agent base image the RequiresDocker leg needs (matches <c>SandboxFixture.ImageRef</c>).</summary>
    private static readonly string AgentImage =
        Environment.GetEnvironmentVariable("MAINGUARD_AGENT_IMAGE") ?? "mainguard-agent-base:latest";

    private static readonly Lazy<(bool Ready, string Reason)> _probe = new(Probe);
    private static readonly Lazy<(bool Ready, string Reason)> _daemonProbe = new(ProbeDaemon);

    public static bool IsReady => _probe.Value.Ready;
    public static string SkipReason => _probe.Value.Reason;

    /// <summary>Docker daemon reachable — no image requirement (for tests that stand up their own).</summary>
    public static bool IsDaemonReady => _daemonProbe.Value.Ready;
    public static string DaemonSkipReason => _daemonProbe.Value.Reason;

    private static (bool, string) Probe()
    {
        var (daemonReady, daemonReason) = _daemonProbe.Value;
        if (!daemonReady)
            return (false, daemonReason);

        try
        {
            using var client = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.Images.InspectImageAsync(AgentImage, cts.Token).GetAwaiter().GetResult();
            return (true, string.Empty);
        }
        catch
        {
            return (false, $"Docker is up but the '{AgentImage}' image is not built (CI builds images/ first).");
        }
    }

    private static (bool, string) ProbeDaemon()
    {
        try
        {
            using var client = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.System.PingAsync(cts.Token).GetAwaiter().GetResult();
            return (true, string.Empty);
        }
        catch
        {
            return (false, "Docker daemon not reachable (RequiresDocker leg runs in Linux CI).");
        }
    }
}
