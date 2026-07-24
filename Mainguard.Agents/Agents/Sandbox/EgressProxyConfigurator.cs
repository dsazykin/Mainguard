using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The Docker implementation of <see cref="IEgressPolicy"/> (P2-07 §3.3): a default-deny egress
/// posture built from an internal Docker network whose only route out is an allowlist-driven proxy
/// container. The proxy runs a tinyproxy HTTP(S) CONNECT allowlist + dnsmasq answering only
/// allowlisted names (NXDOMAIN otherwise) + an iptables backstop that DROPs non-proxy egress — so
/// even an agent that ignores <c>HTTP_PROXY</c> and dials a raw IP is dropped (proxy-env-only
/// enforcement is a rejection trigger). The proxy image is built in CI (<c>images/mainguard-egress-proxy/</c>),
/// never at runtime (G-16).
/// </summary>
public sealed class EgressProxyConfigurator : IEgressPolicy
{
    /// <summary>The internal (default-deny) network agents attach to.</summary>
    public const string AgentNetworkName = "mainguard-agents";

    /// <summary>The egress-capable network the proxy's second leg sits on.</summary>
    public const string EgressNetworkName = "mainguard-egress";

    /// <summary>The proxy container name / in-network hostname.</summary>
    public const string ProxyContainerName = "mainguard-egress-proxy";

    /// <summary>The tinyproxy CONNECT listener port.</summary>
    public const int ProxyPort = 8888;

    /// <summary>The proxy image the shipped daemon runs (overridable per-instance for tests only) —
    /// the spawn preflight checks THIS ref so an absent egress image fails as one actionable typed
    /// error instead of an opaque create failure inside <see cref="EnsureReadyAsync"/>.</summary>
    public const string DefaultImageRef = "mainguard-egress-proxy:latest";

    /// <summary>Backstop timeout for a proxy exec. reload.sh completes in well under a second; a longer
    /// stall means a child is holding the exec's attach pipe (the setup-hang this bounds) rather than
    /// doing work — fail fast instead of blocking forever, even when the caller passed no deadline.</summary>
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for a started proxy container to actually report Running before
    /// treating it as unstartable. Docker's start call is asynchronous, so "started" != "running".</summary>
    private static readonly TimeSpan RunningWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Poll gap while waiting for Running — short enough to add no perceptible latency.</summary>
    private static readonly TimeSpan RunningPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDockerClient _docker;
    private readonly string _proxyImageRef;
    private readonly string? _gatewayUpstream;
    private readonly Func<IReadOnlyList<string>>? _installedAdapterHosts;

    /// <param name="gatewayUpstream">
    /// The P2-08 AI-gateway <c>host:port</c> the proxy routes model-API hosts through (gateway
    /// fronting). Null disables fronting (the model hosts would then reach the provider directly — a
    /// rejection trigger in production; null is for the P2-07-only tests that predate the gateway).
    /// </param>
    /// <param name="installedAdapterHosts">
    /// The hosts the currently-installed agent CLIs declared they need (auto-permit on install —
    /// each adapter's <see cref="Adapters.AdapterSpec.EgressHosts"/>). Read FRESH each
    /// <see cref="EnsureReadyAsync"/> so a CLI installed while the daemon runs is permitted on the
    /// next spawn without a restart; unioned into the rendered proxy config as
    /// <see cref="EgressEntryKind.AgentService"/> (direct route). Null = none (tests / no adapters).
    /// </param>
    public EgressProxyConfigurator(
        IDockerClient docker,
        EgressAllowlist allowlist,
        string proxyImageRef = DefaultImageRef,
        string? gatewayUpstream = null,
        Func<IReadOnlyList<string>>? installedAdapterHosts = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        Allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _proxyImageRef = proxyImageRef;
        _gatewayUpstream = gatewayUpstream;
        _installedAdapterHosts = installedAdapterHosts;
    }

    public EgressAllowlist Allowlist { get; }

    public string NetworkName => AgentNetworkName;

    public string ProxyUrl => $"http://{ProxyContainerName}:{ProxyPort}";

    public EgressVerdict Evaluate(string host) =>
        Allowlist.Allows(host) ? EgressVerdict.Allowed : EgressVerdict.Denied;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        // Internal network: no route out except via a container with a second (egress) leg.
        var internalId = await EnsureNetworkAsync(AgentNetworkName, isInternal: true, ct).ConfigureAwait(false);
        var egressId = await EnsureNetworkAsync(EgressNetworkName, isInternal: false, ct).ConfigureAwait(false);

        var proxy = await FindContainerAsync(ProxyContainerName, ct).ConfigureAwait(false);
        string? proxyId = proxy?.ID;
        var revived = false;

        if (proxy is not null && !string.Equals(proxy.Image, _proxyImageRef, StringComparison.Ordinal))
        {
            // The proxy image was upgraded since this container was created — recreate below so the
            // new bytes run (same policy as the persistent agent jails).
            await _docker.Containers.RemoveContainerAsync(proxy.ID,
                new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
            proxyId = null;
        }
        else if (proxy is not null && !string.Equals(proxy.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            // The VM shutdown (StopVmOnExit) leaves the proxy Exited; exec'ing config into a stopped
            // container 409s ("Container ... is not running") and killed every spawn of the following
            // session. Try to revive it in place — its two network legs persist with the container.
            try
            {
                await _docker.Containers.StartContainerAsync(proxyId!, new ContainerStartParameters(), ct).ConfigureAwait(false);
                // StartContainerAsync returning does NOT mean the container is up — it may still be
                // starting, or have exited again immediately. Confirm before anything execs into it.
                revived = await WaitUntilRunningAsync(proxyId!, ct).ConfigureAwait(false);
                if (!revived)
                {
                    throw new DockerContainerNotRunningException(proxyId!);
                }
            }
            catch (Exception ex) when (ex is DockerApiException or DockerContainerNotRunningException)
            {
                // Unstartable corpse — replace it with a fresh container below.
                await _docker.Containers.RemoveContainerAsync(proxyId!,
                    new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
                proxyId = null;
                revived = false;
            }
        }

        proxyId ??= await CreateAndStartProxyAsync(egressId, ct).ConfigureAwait(false);

        // The config exec 409s ("container … is not running") against anything not fully up. Every path
        // above ends in a start, so verify the postcondition ONCE here rather than assuming it — this is
        // what made the suite flaky: a container that had not finished starting (or had already died)
        // produced an opaque Docker conflict from deep inside the exec instead of a real diagnosis.
        if (!await WaitUntilRunningAsync(proxyId, ct).ConfigureAwait(false))
        {
            throw new DockerContainerNotRunningException(proxyId);
        }

        try
        {
            await PushConfigAsync(proxyId, ct).ConfigureAwait(false);
        }
        catch (DockerApiException) when (revived)
        {
            // The revived corpse died again before the config exec (seen on CI's docker; a fresh
            // container is the known-good path everywhere) — replace it outright and push again.
            // Only the revive path retries: a fresh create that fails must surface, not loop.
            try
            {
                await _docker.Containers.RemoveContainerAsync(proxyId,
                    new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
            }
            catch (DockerApiException)
            {
                // Already gone — the recreate below is the answer either way.
            }

            proxyId = await CreateAndStartProxyAsync(egressId, ct).ConfigureAwait(false);
            await PushConfigAsync(proxyId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the proxy container on the internal network, attaches its second (egress) leg,
    /// and starts it — the known-good creation path shared by first-provision and corpse replacement.</summary>
    private async Task<string> CreateAndStartProxyAsync(string egressNetworkId, CancellationToken ct)
    {
        var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = ProxyContainerName,
            Hostname = ProxyContainerName,
            Image = _proxyImageRef,
            Labels = new Dictionary<string, string> { ["mainguard.role"] = "egress-proxy" },
            HostConfig = new HostConfig
            {
                NetworkMode = AgentNetworkName,
                // The proxy needs NET_ADMIN to install the iptables backstop; nothing else.
                CapDrop = new List<string> { "ALL" },
                CapAdd = new List<string> { "NET_ADMIN", "NET_RAW" },
                SecurityOpt = new List<string> { "no-new-privileges" },
            },
        }, ct).ConfigureAwait(false);

        // Second leg onto the egress-capable network so the proxy — and only the proxy — can reach upstreams.
        await _docker.Networks.ConnectNetworkAsync(egressNetworkId, new NetworkConnectParameters { Container = created.ID }, ct).ConfigureAwait(false);
        await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
        return created.ID;
    }

    private async Task<string> EnsureNetworkAsync(string name, bool isInternal, CancellationToken ct)
    {
        var existing = await _docker.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } },
            }, ct).ConfigureAwait(false);
        var match = existing.FirstOrDefault(n => n.Name == name);
        if (match is not null) return match.ID;

        var created = await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Internal = isInternal,
            Labels = new Dictionary<string, string> { ["mainguard.role"] = isInternal ? "agent-net" : "egress-net" },
        }, ct).ConfigureAwait(false);
        return created.ID;
    }

    /// <summary>
    /// Polls until the container reports <c>State.Running</c>, or the deadline passes. Condition-based
    /// rather than a fixed sleep: Docker's start call is asynchronous, so "started" and "running" are
    /// different facts, and a container that dies on boot never becomes running at all. Returns false
    /// instead of throwing so callers can choose between recreating and surfacing.
    /// </summary>
    private async Task<bool> WaitUntilRunningAsync(string containerId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + RunningWaitTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
                switch (ClassifyReadiness(inspect.State))
                {
                    case ContainerReadiness.Running:
                        return true;
                    case ContainerReadiness.Terminal:
                        return false; // a corpse never becomes running — don't wait out the deadline
                }
            }
            catch (DockerContainerNotFoundException)
            {
                return false; // removed underneath us — the caller recreates.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(RunningPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>What a container's inspected state means for readiness. Pure so the decision is unit
    /// testable without a Docker daemon — the RequiresDocker integration leg cannot run on every
    /// developer machine, and this classification is where the flake actually lived.</summary>
    internal enum ContainerReadiness
    {
        /// <summary>Up and serving — safe to exec into.</summary>
        Running,

        /// <summary>Not up yet, but may still become running (created/starting/restarting).</summary>
        Pending,

        /// <summary>Exited or dead — it will never become running without being recreated.</summary>
        Terminal,
    }

    /// <summary>Classifies an inspected container state (see <see cref="ContainerReadiness"/>).</summary>
    internal static ContainerReadiness ClassifyReadiness(ContainerState? state)
    {
        if (state is null)
        {
            return ContainerReadiness.Pending;
        }

        if (state.Running)
        {
            return ContainerReadiness.Running;
        }

        // Restarting is transient by definition — keep waiting rather than recreating underneath it.
        if (state.Restarting)
        {
            return ContainerReadiness.Pending;
        }

        // Dead, or a finished run: a start was attempted and ended. "Created" has never run and has no
        // FinishedAt, so it stays Pending while the start we just issued takes effect.
        var finished = !string.IsNullOrEmpty(state.FinishedAt)
                       && !state.FinishedAt.StartsWith("0001-01-01", StringComparison.Ordinal);
        return state.Dead || finished ? ContainerReadiness.Terminal : ContainerReadiness.Pending;
    }

    private async Task<ContainerListResponse?> FindContainerAsync(string name, CancellationToken ct)
    {
        var list = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { ["/" + name] = true } },
        }, ct).ConfigureAwait(false);
        return list.FirstOrDefault(c => c.Names.Any(n => n == "/" + name));
    }

    /// <summary>Renders the allowlist to the proxy's config files + backstop script and applies them live.</summary>
    private async Task PushConfigAsync(string proxyId, CancellationToken ct)
    {
        // Auto-permit on install: union the installed agent CLIs' declared hosts (read fresh so a
        // CLI installed since the last spawn is included) into what the proxy renders — as direct-route
        // AgentService entries, never gateway-fronted (they are auth/console hosts, not model APIs).
        var effective = _installedAdapterHosts is null
            ? Allowlist
            : Allowlist.CombinedWith(_installedAdapterHosts(), EgressEntryKind.AgentService, "Agent CLI");

        await WriteFileAsync(proxyId, "/etc/mainguard/tinyproxy-filter", EgressProxyConfig.RenderTinyproxyFilter(effective), ct).ConfigureAwait(false);
        // P2-08: front the model-API hosts through the AI gateway (token bucket + budgets + no-raw-429).
        if (_gatewayUpstream is not null)
        {
            await WriteFileAsync(proxyId, "/etc/mainguard/tinyproxy-upstreams",
                EgressProxyConfig.RenderTinyproxyUpstreams(effective, _gatewayUpstream), ct).ConfigureAwait(false);
        }

        await WriteFileAsync(proxyId, "/etc/mainguard/dnsmasq.conf", EgressProxyConfig.RenderDnsmasqConfig(effective), ct).ConfigureAwait(false);
        await WriteFileAsync(proxyId, "/etc/mainguard/backstop.sh", EgressProxyConfig.RenderIptablesScript(ProxyPort), ct).ConfigureAwait(false);
        // The image's entrypoint reloads tinyproxy/dnsmasq and (re)applies the backstop from these paths.
        await ExecAsync(proxyId, new[] { "sh", "/etc/mainguard/reload.sh" }, ct).ConfigureAwait(false);
    }

    private async Task WriteFileAsync(string containerId, string path, string content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExecTimeout);
        var bounded = timeout.Token;

        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0",
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new List<string> { "sh", "-c", "mkdir -p \"$(dirname \"$1\")\"; cat > \"$1\"", "sh", path },
        }, bounded).ConfigureAwait(false);
        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, bounded).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, 0, bytes.Length, bounded).ConfigureAwait(false);
        stream.CloseWrite();
        await stream.ReadOutputToEndAsync(bounded).ConfigureAwait(false);
    }

    private async Task ExecAsync(string containerId, IReadOnlyList<string> cmd, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExecTimeout);
        var bounded = timeout.Token;

        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0",
            AttachStdout = true,
            AttachStderr = true,
            Cmd = cmd.ToList(),
        }, bounded).ConfigureAwait(false);
        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, bounded).ConfigureAwait(false);
        await stream.ReadOutputToEndAsync(bounded).ConfigureAwait(false);
    }
}

/// <summary>
/// The proxy container was started but never reached a Running state (it died on boot, or was removed
/// underneath us). Raised instead of letting the config exec fail with Docker's opaque
/// <c>409 "container … is not running"</c> from deep inside <c>ExecCreateContainerAsync</c>, which is
/// what made this path hard to diagnose and the CI suite intermittently red.
/// </summary>
public sealed class DockerContainerNotRunningException : Exception
{
    public DockerContainerNotRunningException(string containerId)
        : base($"The egress proxy container '{containerId}' was started but never reached a running state. "
             + "It most likely exited during boot — inspect its logs (docker logs mainguard-egress-proxy).")
    {
        ContainerId = containerId;
    }

    public string ContainerId { get; }
}
