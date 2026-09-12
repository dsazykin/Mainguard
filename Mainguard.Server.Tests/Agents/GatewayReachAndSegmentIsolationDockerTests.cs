using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

/// <summary>
/// F25 and F30 — the two claims in this batch that only a real engine can settle.
///
/// <para><b>F25.</b> The model gateway's default bind moved off "the lowest private IPv4 on any up NIC"
/// (on a Mac, the Wi-Fi address — a plaintext HTTP gateway on the operator's LAN) to loopback on Docker
/// Desktop. That is only a legitimate choice if the egress proxy can still dial it, which is a property
/// of Docker's <c>host-gateway</c> mapping and of nothing in this repo. <c>CanProxyReachAsync</c> is the
/// production probe that gates confinement per spawn, so it is the thing asserted here.</para>
///
/// <para><b>F30 — and the measurement changed the design.</b> The claim was that Docker's internal
/// isolation drops FORWARDED traffic but not traffic addressed to the bridge itself. Measured here it
/// is TRUE and the working assumption ("an internal container reaches nothing") was half right: an
/// internal segment has no default route, so everything off its own subnet is "Network is unreachable"
/// — but its own bridge's gateway address is on-link and answers in 0.05 ms. That is why
/// <c>GatewayBindPolicy</c> binds <c>docker0</c> and never a <c>br-*</c> segment bridge: binding a
/// segment's own address would let a jail dial the gateway directly, past tinyproxy and past the F26
/// port bound. The measurement is pinned below in both directions, with a non-internal control so a
/// passing result cannot be an inert probe.</para>
/// </summary>
namespace Mainguard.Server.Tests.Agents;

[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class GatewayReachAndSegmentIsolationDockerTests
{
    private const string TrivialImage = "busybox:latest";

    /// <summary>
    /// F25 — the egress proxy can reach a LOOPBACK-bound gateway through the
    /// <c>host.docker.internal</c> alias, and cannot reach it by its literal loopback address.
    ///
    /// <para>Both halves matter. The first is what makes the narrowed bind usable at all; the second is
    /// why the daemon must translate the bind address before telling anything about it — inside a
    /// container <c>127.0.0.1</c> is the container, and a jail's <c>NO_PROXY</c> covers it, so a jail
    /// pointed at loopback dials itself.</para>
    ///
    /// <para><b>Docker Desktop only, and that is the claim, not a convenience.</b> The loopback bind is
    /// chosen on macOS/Windows precisely because <c>host-gateway</c> routes to the host's loopback stack
    /// there. On Linux Docker Engine it maps to the docker0 address, where a <c>127.0.0.1</c> listener is
    /// genuinely unreachable from a container — so run unconditionally this asserted something false and
    /// failed the Linux CI leg by design. The Linux half of the contract is
    /// <see cref="Proxy_CanReachTheResolvedBridgeBoundGateway"/>, which asserts the address the resolver
    /// actually picks there.</para>
    /// </summary>
    [RequiresDockerDesktopFact]
    public async Task Proxy_CanReachALoopbackBoundGateway_ViaTheHostAlias()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        // A listener bound to the daemon host's loopback ONLY — exactly what the gateway now binds by
        // default on Docker Desktop.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = AcceptQuietlyAsync(listener);

        try
        {
            Assert.True(
                await fx.Egress.CanProxyReachAsync($"{EgressProxyConfigurator.GatewayHostAlias}:{port}"),
                $"the egress proxy could not reach a loopback-bound gateway via "
                + $"{EgressProxyConfigurator.GatewayHostAlias}:{port}. Confinement would be skipped on "
                + "every BYOK spawn, i.e. the raw provider key would go into the jail.");

            Assert.False(
                await fx.Egress.CanProxyReachAsync($"127.0.0.1:{port}"),
                "127.0.0.1 inside the proxy is the proxy — if this ever succeeds the probe is measuring "
                + "something other than the daemon host.");
        }
        finally
        {
            listener.Stop();
            await accepting;
        }
    }

    /// <summary>
    /// Audit B1/B3 — the Linux half, and the one the PR-blocking CI leg runs: on native Docker Engine the
    /// daemon binds the address <c>GatewayBindPolicy</c> resolves (docker0), and a listener there must be
    /// reachable from the egress proxy through the EXACT string <c>ProxyReachableHostFor</c> hands the
    /// jail and the proxy allowlist.
    ///
    /// <para>It also pins the resolver itself against the real host: a null here would mean the daemon
    /// disables the gateway on a machine that plainly has a docker bridge, which is how "confinement
    /// skipped, raw provider key into every BYOK jail" shipped. The bridge being idle — carrier-down
    /// because Mainguard puts every container on a user-defined network — is the normal state, not an
    /// edge case, so this is the condition that matters.</para>
    /// </summary>
    [RequiresLinuxDockerEngineFact]
    public async Task Proxy_CanReachTheResolvedBridgeBoundGateway()
    {
        var bind = Mainguard.Server.Gateway.GatewayBindPolicy.TryResolvePrivateHostAddress();
        Assert.True(
            bind is not null,
            "the gateway bind resolved to NOTHING on a Linux host running the Docker test tier. The "
            + "daemon would disable the gateway and every BYOK spawn would put the raw provider key in "
            + "its jail.");

        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Parse(bind!), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = AcceptQuietlyAsync(listener);

        try
        {
            // The same translation the daemon applies before writing the base URL into a jail and the
            // allowlist entry into the proxy — asserted through it, not around it.
            var reachable = Mainguard.Server.Gateway.GatewayBindPolicy.ProxyReachableHostFor(bind);
            Assert.Equal(bind, reachable); // a bridge address is dialled by its literal value

            Assert.True(
                await fx.Egress.CanProxyReachAsync($"{reachable}:{port}"),
                $"the egress proxy could not reach a gateway bound at {reachable}:{port}. Confinement "
                + "would be skipped on every BYOK spawn, i.e. the raw provider key would go into the jail.");
        }
        finally
        {
            listener.Stop();
            await accepting;
        }
    }

    /// <summary>
    /// F25 — the proxy really does carry the alias, and a proxy created before it did is replaced rather
    /// than left in place quietly failing every reachability probe.
    /// </summary>
    [RequiresDockerFact]
    public async Task Proxy_CarriesTheGatewayHostAlias()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        var proxy = await FindProxyAsync(fx.Docker);
        Assert.NotNull(proxy);

        var inspect = await fx.Docker.Containers.InspectContainerAsync(proxy!.ID);
        Assert.Contains(
            EgressProxyConfigurator.GatewayHostAlias + ":host-gateway",
            inspect.HostConfig.ExtraHosts ?? new List<string>());

        // And the name resolves inside the container, which is the property the alias exists for.
        var resolved = await fx.ExecAsync(
            proxy.ID, "getent", "ahostsv4", EgressProxyConfigurator.GatewayHostAlias);
        Assert.Equal(0, resolved.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(resolved.Stdout), "the alias resolved to nothing");
    }

    /// <summary>
    /// F26 — the rendered backstop really does land in the live proxy, and the resulting chain admits
    /// the gateway port and drops everything else to that host.
    ///
    /// <para>Asserted against <c>iptables -S OUTPUT</c> in the running container rather than against the
    /// rendered text, because the whole finding is about what the PROXY enforces. The address in the
    /// rules is resolved inside the container from the <c>host.docker.internal</c> alias, so this also
    /// proves the two halves of F25 and F26 compose.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Backstop_BoundsTheGatewayRouteToItsPort_InTheLiveProxy()
    {
        const int gatewayPort = 5251;
        await using var fx = new SandboxFixture();

        // A configurator that knows where the gateway is — the production shape once the daemon's
        // bind is loopback and the proxy reaches it through the alias.
        var egress = new EgressProxyConfigurator(
            fx.Docker,
            EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()),
            gatewayReachableAt: $"{EgressProxyConfigurator.GatewayHostAlias}:{gatewayPort}");

        await egress.EnsureReadyAsync(CancellationToken.None);

        var proxy = await FindProxyAsync(fx.Docker);
        Assert.NotNull(proxy);

        var rules = await fx.ExecAsync(proxy!.ID, "iptables", "-S", "OUTPUT");
        Assert.Equal(0, rules.ExitCode);

        Assert.Contains($"--dport {gatewayPort} -j ACCEPT", rules.Stdout, StringComparison.Ordinal);
        Assert.Contains("-j DROP", rules.Stdout, StringComparison.Ordinal);

        // Every ACCEPT to the gateway host names a port; there is no blanket allow.
        foreach (var line in rules.Stdout.Split('\n'))
        {
            if (line.Contains("-A OUTPUT", StringComparison.Ordinal)
                && line.Contains("-j ACCEPT", StringComparison.Ordinal))
            {
                Assert.Contains("--dport", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// F30 — an internal segment reaches NOTHING off its own subnet (no default route), but its own
    /// bridge's gateway address IS on-link and reachable.
    ///
    /// <para>This is the constraint that decides where the model gateway may bind: <c>docker0</c>, which
    /// is off every segment's subnet, and never a segment's own <c>br-*</c> address, which every jail on
    /// that segment could dial directly — bypassing tinyproxy, the allowlist, and the F26 port bound.
    /// Both halves are asserted, because the safety of the choice depends on the first and the danger of
    /// the alternative on the second.</para>
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task InternalSegment_ReachesOnlyItsOwnSubnet()
    {
        var docker = DockerEndpointResolver.CreateClient();
        if (!await EnsureTrivialImageAsync(docker, CancellationToken.None))
        {
            return; // no registry access — nothing to prove.
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        string? internalNet = null, openNet = null, internalBox = null, openBox = null;
        try
        {
            (internalNet, var internalGateway) = await CreateNetworkAsync(docker, "mg-f30-int-" + suffix, isInternal: true);
            (openNet, var openGateway) = await CreateNetworkAsync(docker, "mg-f30-open-" + suffix, isInternal: false);

            internalBox = await StartBoxAsync(docker, "mg-f30-int-box-" + suffix, internalNet);
            openBox = await StartBoxAsync(docker, "mg-f30-open-box-" + suffix, openNet);

            // The CONTROL first: on a normal bridge the container has a default route and its gateway
            // answers. If this ever fails, the assertions below prove nothing.
            var openRoutes = await ExecAsync(docker, openBox, "ip", "route");
            Assert.Contains("default", openRoutes.Stdout, StringComparison.Ordinal);
            var openPing = await ExecAsync(docker, openBox, "ping", "-c", "1", "-W", "2", openGateway);
            Assert.True(openPing.ExitCode == 0, $"the control probe failed: {openPing.Stdout}{openPing.Stderr}");

            // Half one: no default route, so everything off this segment's own subnet is unreachable —
            // including the OTHER bridge's gateway, and including docker0 (which is why the model
            // gateway may bind there).
            var internalRoutes = await ExecAsync(docker, internalBox, "ip", "route");
            Assert.DoesNotContain("default", internalRoutes.Stdout, StringComparison.Ordinal);

            var offSubnet = await ExecAsync(docker, internalBox, "ping", "-c", "1", "-W", "2", openGateway);
            Assert.False(
                offSubnet.ExitCode == 0,
                $"an INTERNAL segment reached {openGateway}, which is off its own subnet. Containment "
                + "rests on that being impossible.");

            // Half two, and the reason the bind is restricted to docker0: the segment's OWN bridge
            // address is on-link and answers. A gateway bound there would be dialable by every jail on
            // the segment WITHOUT going through tinyproxy — no allowlist, no F26 port bound.
            var ownBridge = await ExecAsync(docker, internalBox, "ping", "-c", "1", "-W", "2", internalGateway);
            Assert.True(
                ownBridge.ExitCode == 0,
                $"expected the segment's own bridge ({internalGateway}) to be on-link; if this engine "
                + "blocks it too, the docker0-only bind restriction is merely redundant, not wrong.");
        }
        finally
        {
            await RemoveContainerAsync(docker, internalBox);
            await RemoveContainerAsync(docker, openBox);
            await RemoveNetworkAsync(docker, internalNet);
            await RemoveNetworkAsync(docker, openNet);
            docker.Dispose();
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task AcceptQuietlyAsync(System.Net.Sockets.TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
            }
        }
        catch (Exception)
        {
            // The listener was stopped — that is how this loop ends.
        }
    }

    private static async Task<ContainerListResponse?> FindProxyAsync(IDockerClient docker)
    {
        var name = "/" + EgressProxyConfigurator.ProxyContainerName;
        var all = await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        return all.FirstOrDefault(c => c.Names.Any(n => n == name));
    }

    private static async Task<(string Id, string Gateway)> CreateNetworkAsync(
        IDockerClient docker, string name, bool isInternal)
    {
        var created = await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Internal = isInternal,
        });

        var inspect = await docker.Networks.InspectNetworkAsync(created.ID);
        var gateway = inspect.IPAM?.Config?.Select(c => c.Gateway).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        Assert.False(string.IsNullOrWhiteSpace(gateway), $"network {name} reported no gateway address");
        return (created.ID, gateway!);
    }

    private static async Task<string> StartBoxAsync(IDockerClient docker, string name, string networkId)
    {
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = TrivialImage,
            Cmd = new List<string> { "sleep", "300" },
            Labels = new Dictionary<string, string> { ["mainguard.role"] = "f30-probe" },
            HostConfig = new HostConfig { NetworkMode = networkId },
        });

        await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters());
        return created.ID;
    }

    private static async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(
        IDockerClient docker, string containerId, params string[] command)
    {
        var exec = await docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = command.ToList(),
        });

        using var stream = await docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        var inspect = await docker.Exec.InspectContainerExecAsync(exec.ID);
        return (inspect.ExitCode, stdout, stderr);
    }

    private static async Task<bool> EnsureTrivialImageAsync(IDockerClient docker, CancellationToken ct)
    {
        try
        {
            var images = await docker.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
            if (images.Any(i => i.RepoTags is not null && i.RepoTags.Contains(TrivialImage)))
            {
                return true;
            }

            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
                null, new Progress<JSONMessage>(), ct);
            return true;
        }
        catch (DockerApiException)
        {
            return false; // no registry access — nothing to prove, skip rather than fail.
        }
    }

    private static async Task RemoveContainerAsync(IDockerClient docker, string? id)
    {
        if (id is null) return;
        try
        {
            await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerApiException) { /* best-effort cleanup */ }
    }

    private static async Task RemoveNetworkAsync(IDockerClient docker, string? id)
    {
        if (id is null) return;
        try
        {
            await docker.Networks.DeleteNetworkAsync(id);
        }
        catch (DockerApiException) { /* best-effort cleanup */ }
    }
}
