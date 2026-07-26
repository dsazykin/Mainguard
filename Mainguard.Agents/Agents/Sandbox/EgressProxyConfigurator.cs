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

    /// <summary>The docker label both mainguard networks carry, keyed to their role.</summary>
    public const string NetworkRoleLabel = "mainguard.role";

    /// <summary>MG-25 — resource ceilings for the proxy container. It runs two small daemons (tinyproxy
    /// + dnsmasq) and an iptables invocation; anything beyond this is a runaway, and the proxy sits on
    /// the ONE path every agent's egress takes, so it must not be able to starve the VM either.</summary>
    private const long ProxyMemoryBytes = 256L * 1024 * 1024;
    private const long ProxyPidsLimit = 128;
    private const long ProxyNanoCpus = 1_000_000_000; // 1 core
    private const long ProxyNoFile = 4096;

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

    /// <summary>
    /// Something outside this call disturbed the proxy while we were adopting it — it was removed, or
    /// stopped/killed by a signal. Internal control flow, never surfaced: <see cref="EnsureReadyAsync"/>
    /// answers it by starting the whole sequence over.
    /// </summary>
    private sealed class ProxyDisturbedException : Exception
    {
        public ProxyDisturbedException(string containerId, string what)
            : base($"The egress proxy '{containerId}' was {what} while EnsureReadyAsync was adopting it.") { }
    }

    /// <summary>Exit codes that mean "a signal ended this", i.e. 128+N. A container that exits this way
    /// did not fail — something stopped it, and for a SHARED singleton that is a routine race, not an
    /// error. 143 = SIGTERM (docker stop), 137 = SIGKILL (docker kill / force-remove), 130 = SIGINT.</summary>
    internal static bool IsExternalStopExit(long exitCode) => exitCode is 143 or 137 or 130;

    /// <summary>How many times the adopt-or-create sequence is attempted before a failure is real.</summary>
    private const int MaxAdoptionAttempts = 3;

    /// <summary>Breathing room before re-attempting, so a retry does not re-enter the same race Docker
    /// is still resolving (a stop that is mid-flight completes in well under this).</summary>
    private static readonly TimeSpan AdoptionRetryDelay = TimeSpan.FromMilliseconds(400);

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        // Internal network: no route out except via a container with a second (egress) leg.
        var internalId = await EnsureNetworkAsync(AgentNetworkName, isInternal: true, ct).ConfigureAwait(false);
        var egressId = await EnsureNetworkAsync(EgressNetworkName, isInternal: false, ct).ConfigureAwait(false);

        // The proxy is a SHARED, singleton container. Every SandboxFixture teardown removes it, the VM
        // shutdown path stops it, an operator may do either, and Docker itself can still be finishing a
        // stop when the next start arrives. So between finding it and finishing with it, it can be
        // removed OR signalled out from under us at ANY point — and neither is a failure. The right
        // answer to both is the same: start the sequence over.
        //
        // Retrying at the SEQUENCE level is deliberate. The window exists before every individual call,
        // so hardening call sites one at a time only moves it. Two distinct instances of this bug have
        // now been fixed by widening this loop rather than by patching a call:
        //   * the container REMOVED mid-adoption — the recovery path's own RemoveContainerAsync raised
        //     a 404 that escaped uncaught (reproduced locally: 3 failures in 10 runs);
        //   * the container SIGTERM'd mid-adoption — CI caught a proxy that lived 340ms and exited 143
        //     with empty logs, because a rapid stop→start let our start land inside a stop Docker was
        //     still completing, which then signalled the process we had just started.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureProxyReadyOnceAsync(egressId, ct).ConfigureAwait(false);
                return;
            }
            catch (ProxyDisturbedException) when (attempt < MaxAdoptionAttempts)
            {
                // Removed or signalled underneath us. Let Docker settle, then go around again.
                await Task.Delay(AdoptionRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureProxyReadyOnceAsync(string egressId, CancellationToken ct)
    {
        var proxy = await FindContainerAsync(ProxyContainerName, ct).ConfigureAwait(false);
        string? proxyId = proxy?.ID;
        var revived = false;

        if (proxy is not null && !string.Equals(proxy.Image, _proxyImageRef, StringComparison.Ordinal))
        {
            // The proxy image was upgraded since this container was created — recreate below so the
            // new bytes run (same policy as the persistent agent jails).
            await TryRemoveContainerAsync(proxy.ID, ct).ConfigureAwait(false);
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
                // Unstartable corpse — replace it with a fresh container below. The removal MUST
                // tolerate the container already being gone: this is the recovery path, and having it
                // throw its own 404 turned a recoverable state into an unhandled failure.
                await TryRemoveContainerAsync(proxyId!, ct).ConfigureAwait(false);
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
            // Three different states hide behind "not running", with three different answers, and
            // conflating them is what sent repeated CI investigations after a boot bug that was never
            // there. Only the third is a real failure.
            var state = await TryInspectStateAsync(proxyId, ct).ConfigureAwait(false);

            // 1. Removed underneath us — a concurrent teardown. Start over and create a fresh one.
            if (state is null)
            {
                throw new ProxyDisturbedException(proxyId, "removed");
            }

            // 2. Signalled underneath us — `docker stop`/`kill`, or a stop Docker was still completing
            //    when our start landed. The container did not fail; something ended it. Start over.
            if (!state.Running && IsExternalStopExit(state.ExitCode))
            {
                throw new ProxyDisturbedException(
                    proxyId, $"stopped externally (exit {state.ExitCode} = 128+{state.ExitCode - 128})");
            }

            // 3. It genuinely came up wrong. THIS is a boot failure, and it reports as one.
            throw new DockerContainerNotRunningException(
                proxyId, await DescribeContainerFailureAsync(proxyId, ct).ConfigureAwait(false));
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
            HostConfig = ProxyHostConfig(),
        }, ct).ConfigureAwait(false);

        // Second leg onto the egress-capable network so the proxy — and only the proxy — can reach upstreams.

        await _docker.Networks.ConnectNetworkAsync(egressNetworkId, new NetworkConnectParameters { Container = created.ID }, ct).ConfigureAwait(false);
        await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
        return created.ID;
    }

    /// <summary>
    /// MG-25 — the proxy's hardened <see cref="HostConfig"/>. Built here (and asserted by the pure
    /// tests) rather than inline so the posture is one auditable surface.
    ///
    /// <para>The proxy is the single chokepoint every agent's egress crosses, and it was the least
    /// hardened container in the system: two extra capabilities, no seccomp, a writable rootfs and no
    /// resource ceilings. It now carries the same class of controls as the jails it fronts, minus the
    /// one capability it genuinely needs.</para>
    ///
    /// <para><b>Capabilities.</b> The list got LONGER while the privilege got SMALLER; the old
    /// two-item set was not lean, it was broken. <c>NET_ADMIN</c> stays — the iptables backstop cannot
    /// be installed without it. <c>NET_RAW</c> is dropped: it exists for raw/packet sockets, and the
    /// only consumer that would have needed it is <c>iptables-legacy</c> (libiptc drives the classic
    /// tables over an <c>AF_INET/SOCK_RAW</c> socket). Debian bookworm — this image's base — ships
    /// <c>iptables-nft</c> as the <c>iptables</c> alternative, which speaks netlink to nftables and is
    /// gated on <c>NET_ADMIN</c> alone. Nothing else in the image opens a raw socket.</para>
    ///
    /// <para>The three additions are each something dnsmasq cannot start without, and their absence is
    /// why <b>dnsmasq had never once run in this container</b> — verified against a real daemon, not
    /// reasoned about. <c>NET_BIND_SERVICE</c>: under <c>CapDrop ALL</c> even root cannot bind below
    /// 1024, so port 53 was unreachable. <c>SETGID</c>/<c>SETUID</c>: dnsmasq unconditionally calls
    /// <c>setgroups()</c>/<c>setgid()</c>/<c>setuid()</c> at startup to drop to its own unprivileged
    /// user, and without those capabilities the call fails and dnsmasq exits 5 (<c>failed to change
    /// group-id to dip: Operation not permitted</c>). <c>--user=root</c> does not avoid this —
    /// <c>setgroups()</c> needs <c>CAP_SETGID</c> even when the target gid is the one already held, and
    /// it was tried and observed to fail identically. Granting them is therefore the LOWER-privilege
    /// outcome: dnsmasq ends up running as an unprivileged uid rather than as root. The whole thing
    /// stayed invisible because the old exfil probe queried a name that NXDOMAINs everywhere, so a dead
    /// dnsmasq passed it exactly as well as a live one.</para>
    ///
    /// <para><c>KILL</c> is the consequence of that privilege drop: signalling a process owned by a
    /// DIFFERENT uid needs <c>CAP_KILL</c>, so without it the reload's <c>pkill</c> fails with
    /// <c>Operation not permitted</c> and the old daemon keeps serving the OLD policy — an allowlist
    /// edit, including REMOVING a host, would never reach a running proxy. Its scope is signalling
    /// inside this container's pid namespace, which holds two daemons and nothing else.</para>
    ///
    /// <para><b>Read-only rootfs.</b> Every writable surface the two daemons need is a tmpfs: the
    /// daemon-rendered policy and the generated tinyproxy config live under <c>/run/mainguard</c>
    /// (which is why the image's <c>CONF_DIR</c> moved off <c>/etc</c>), pid files under <c>/run</c>,
    /// scratch under <c>/tmp</c>. The image's own scripts stay on the read-only layer where an agent
    /// that somehow reached this container cannot rewrite the policy that contains it.</para>
    /// </summary>
    internal static HostConfig ProxyHostConfig() => new()
    {
        NetworkMode = AgentNetworkName,

        // NET_ADMIN: the iptables backstop. NET_BIND_SERVICE: dnsmasq's port 53. SETGID/SETUID:
        // dnsmasq's own privilege drop. KILL: restarting those daemons on a policy reload, once they
        // are no longer running as root. Nothing else. (See the summary for why each is load-bearing —
        // this set is smaller in privilege than the old one despite being longer.)
        CapDrop = new List<string> { "ALL" },
        CapAdd = new List<string> { "NET_ADMIN", "NET_BIND_SERVICE", "SETGID", "SETUID", "KILL" },

        // The same default-deny seccomp profile the jails run (a custom seccomp= REPLACES Docker's
        // default, so this is the moby default plus the ptrace/process_vm_* denials — not a loosening).
        SecurityOpt = new List<string> { "no-new-privileges", SeccompProfile.SecurityOptValue },

        // A real init as pid 1 (Docker injects tini). The image's entrypoint ends in `sleep infinity`,
        // which never calls wait() — and both daemons are started in the background from a docker-exec
        // shell that immediately exits, so they are orphaned onto pid 1. Without a reaper every policy
        // reload leaves a zombie behind: they accumulate against PidsLimit, and a dead daemon keeps its
        // name in /proc, which makes "is it still running?" answer yes forever.
        Init = true,

        ReadonlyRootfs = true,
        Tmpfs = new Dictionary<string, string>
        {
            // /var/run is a symlink to /run on debian, so this covers both daemons' pid files as well
            // as /run/mainguard, where the daemon writes the rendered policy.
            ["/run"] = "size=16m,mode=0755",
            ["/tmp"] = "size=16m,mode=1777",
        },

        Memory = ProxyMemoryBytes,
        PidsLimit = ProxyPidsLimit,
        NanoCPUs = ProxyNanoCpus,
        Ulimits = new List<Ulimit>
        {
            new() { Name = "nofile", Soft = ProxyNoFile, Hard = ProxyNoFile },
        },

        Privileged = false,
    };

    /// <summary>
    /// The proxy's IPv4 on the internal agent network — the address every jail pins as its resolver
    /// (MG-7) and the only destination the backstop admits (MG-18). Null when the proxy is absent or
    /// not yet attached; callers decide whether that is fatal (spawning is).
    /// </summary>
    public async Task<string?> ResolveProxyAddressAsync(CancellationToken ct = default)
    {
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(ProxyContainerName, ct).ConfigureAwait(false);
            return ProxyAddressOf(inspect);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Pure extraction of the proxy's agent-network IPv4 from an inspect response.</summary>
    internal static string? ProxyAddressOf(ContainerInspectResponse? inspect)
    {
        var networks = inspect?.NetworkSettings?.Networks;
        if (networks is null || !networks.TryGetValue(AgentNetworkName, out var endpoint))
            return null;
        return string.IsNullOrWhiteSpace(endpoint?.IPAddress) ? null : endpoint.IPAddress;
    }

    private async Task<string> EnsureNetworkAsync(string name, bool isInternal, CancellationToken ct)
    {
        var existing = await _docker.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } },
            }, ct).ConfigureAwait(false);
        var match = existing.FirstOrDefault(n => n.Name == name);
        if (match is not null)
        {
            // MG-18: reuse by NAME only was the whole hole. `mainguard-agents` being Internal is the
            // control that keeps agents off the outside world — not the proxy, not the env vars — and
            // a network by that name that is NOT internal silently gives every jail unrestricted
            // egress with no error anywhere. Verify the property on every reuse.
            AssertNetworkMatchesPolicy(match, name, isInternal);
            return match.ID;
        }

        var created = await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Internal = isInternal,
            Labels = new Dictionary<string, string> { [NetworkRoleLabel] = RoleFor(isInternal) },
        }, ct).ConfigureAwait(false);
        return created.ID;
    }

    /// <summary>The <see cref="NetworkRoleLabel"/> value a mainguard network of this kind carries — the
    /// stamp that says mainguard created it, and half of what the MG-18 reuse gate checks.</summary>
    public static string RoleFor(bool isInternal) => isInternal ? "agent-net" : "egress-net";

    /// <summary>
    /// MG-18 — asserts a pre-existing network we are about to reuse still encodes the egress posture we
    /// created it with. Pure (no Docker client) so the drift cases are unit-assertable.
    ///
    /// <para>Fails <b>closed</b>: it throws rather than deleting and recreating. A drifted network can
    /// have live containers attached, and removing it out from under them is a destructive act taken on
    /// the basis of an ambiguous signal; refusing to spawn is the conservative answer, and the message
    /// names the exact remediation. The label is checked alongside <c>Internal</c> because a foreign
    /// network that merely happens to share the name is the same class of surprise — we only ever want
    /// to reuse a network mainguard itself stamped.</para>
    /// </summary>
    internal static void AssertNetworkMatchesPolicy(NetworkResponse match, string name, bool isInternal)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (match.Internal != isInternal)
            throw new EgressNetworkDriftException(name,
                $"expected Internal={isInternal} but the existing network reports Internal={match.Internal}. "
                + (isInternal
                    ? "A non-internal 'agent' network gives every jail unrestricted egress — the default-deny posture is off."
                    : "An internal 'egress' network cuts the proxy off from every upstream — nothing can leave."));

        var actualRole = match.Labels is not null && match.Labels.TryGetValue(NetworkRoleLabel, out var found) ? found : null;
        var expectedRole = RoleFor(isInternal);
        if (!string.Equals(actualRole, expectedRole, StringComparison.Ordinal))
            throw new EgressNetworkDriftException(name,
                $"expected the label {NetworkRoleLabel}={expectedRole} but found {(actualRole is null ? "no such label" : $"'{actualRole}'")}. "
                + "Mainguard only reuses networks it stamped; an unstamped network of the same name was created by something else.");
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

        if (state.Dead)
        {
            return ContainerReadiness.Terminal;
        }

        // A finished run means a start was attempted and ended — but ONLY if that exit is newer than
        // the most recent start. `docker start` on a stopped container does NOT clear FinishedAt: for
        // a moment the inspect reports Running=false alongside the PREVIOUS run's FinishedAt, which is
        // indistinguishable from a corpse unless StartedAt is consulted. Treating that window as
        // terminal made the revive path destroy and recreate a container that was merely still
        // starting — invisible on a fast daemon, reliably hit on a loaded CI runner. Verified: a
        // restarted proxy reports running=true with FinishedAt still set to the earlier stop.
        var finished = ParseDockerTime(state.FinishedAt);
        if (finished is null)
        {
            return ContainerReadiness.Pending; // never ran (Created), or no timestamp yet
        }

        var started = ParseDockerTime(state.StartedAt);
        return started is not null && started > finished
            ? ContainerReadiness.Pending  // the latest start postdates the last exit — it is coming up
            : ContainerReadiness.Terminal;
    }

    /// <summary>Docker's RFC3339 state timestamps, with its "never happened" sentinel treated as null.</summary>
    private static DateTimeOffset? ParseDockerTime(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith("0001-01-01", StringComparison.Ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Everything a human would have run <c>docker logs</c> / <c>docker inspect</c> for, gathered at the
    /// moment of failure and folded into the exception.
    ///
    /// <para>The old message ended with "inspect its logs (docker logs mainguard-egress-proxy)". On CI
    /// there is nobody to do that, and by the time anyone looks the container has been torn down with
    /// the job — so a boot failure there was a dead end: no exit code, no OOM flag, not one line of the
    /// container's own output. Whatever the container said about why it died is the single most useful
    /// fact about this failure, so it belongs IN the failure. Best-effort throughout: a diagnostic that
    /// throws while diagnosing would replace a real error with a useless one.</para>
    /// </summary>
    private async Task<string> DescribeContainerFailureAsync(string containerId, CancellationToken ct)
    {
        var report = new StringBuilder();

        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            var state = inspect.State;
            report.Append(" state=").Append(state?.Status ?? "<none>")
                  .Append(" exit=").Append(state?.ExitCode)
                  .Append(" oomKilled=").Append(state?.OOMKilled)
                  .Append(" startedAt=").Append(state?.StartedAt)
                  .Append(" finishedAt=").Append(state?.FinishedAt);

            var lifetime = Lifetime(state);
            if (lifetime is not null)
            {
                report.Append(" lifetime=").Append((long)lifetime.Value.TotalMilliseconds).Append("ms");
            }

            if (!string.IsNullOrWhiteSpace(state?.Error))
            {
                report.Append(" dockerError='").Append(state!.Error).Append('\'');
            }

            // Name the verdict rather than leaving the reader to decode 128+N. An exit of 143 with an
            // empty log and a sub-second lifetime is NOT a boot failure, and saying "it exited during
            // boot" about it actively misdirects — that wording cost several investigations.
            report.Append("\nverdict: ").Append(Verdict(state, lifetime));
        }
        catch (Exception ex)
        {
            report.Append(" (inspect failed: ").Append(ex.Message).Append(')');
        }

        try
        {
            using var logs = await _docker.Containers.GetContainerLogsAsync(
                containerId,
                tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "50" },
                ct).ConfigureAwait(false);
            var (stdout, stderr) = await logs.ReadOutputToEndAsync(ct).ConfigureAwait(false);
            var combined = (stdout + stderr).Trim();
            report.Append("\n--- container logs (last 50 lines) ---\n")
                  .Append(combined.Length == 0 ? "(the container produced no output)" : combined);
        }
        catch (Exception ex)
        {
            report.Append("\n(could not read container logs: ").Append(ex.Message).Append(')');
        }

        return report.ToString();
    }

    /// <summary>How long the container's last run lasted, when both timestamps are known.</summary>
    internal static TimeSpan? Lifetime(ContainerState? state)
    {
        var started = ParseDockerTime(state?.StartedAt);
        var finished = ParseDockerTime(state?.FinishedAt);
        return started is not null && finished is not null && finished > started
            ? finished - started
            : null;
    }

    /// <summary>
    /// A plain-language reading of why the container is not running. The exit code alone is not
    /// self-explanatory to whoever reads a CI log at 3am, and the difference that matters most —
    /// "something stopped this" versus "this crashed" — is invisible unless you know 128+N by heart.
    /// </summary>
    internal static string Verdict(ContainerState? state, TimeSpan? lifetime)
    {
        if (state is null)
        {
            return "the container could not be inspected.";
        }

        if (state.Running)
        {
            return "the container reports Running (it came up after the wait expired).";
        }

        if (state.OOMKilled)
        {
            return "the kernel OOM-killed it — raise the memory ceiling or find the leak.";
        }

        if (IsExternalStopExit(state.ExitCode))
        {
            var signal = state.ExitCode - 128;
            var name = signal switch { 15 => "SIGTERM", 9 => "SIGKILL", 2 => "SIGINT", _ => "a signal" };
            var brief = lifetime is not null && lifetime.Value < TimeSpan.FromSeconds(2)
                ? $" It lived only {(long)lifetime.Value.TotalMilliseconds}ms, so it was ended almost immediately after starting."
                : string.Empty;
            return $"STOPPED EXTERNALLY — exit {state.ExitCode} is 128+{signal} ({name}), i.e. something "
                 + $"signalled this container; it did NOT fail to boot.{brief} Look for a concurrent "
                 + "`docker stop`/`kill`, a teardown racing this call, or a stop Docker was still completing.";
        }

        return $"it exited on its own with code {state.ExitCode} — a genuine boot failure; the logs below say why.";
    }

    /// <summary>Force-removes a container, treating "already gone" as success. Used on every teardown
    /// path here: the proxy is shared, so something else removing it first is a normal outcome, not an
    /// error — and a cleanup that throws its own 404 masks whatever we were actually recovering from.</summary>
    private async Task TryRemoveContainerAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await _docker.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone — which is exactly the state we wanted.
        }
    }

    /// <summary>The container's state, or null when it no longer exists — the distinction the caller
    /// needs to tell "removed" apart from "still here but not running".</summary>
    private async Task<ContainerState?> TryInspectStateAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            return inspect.State;
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
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

    /// <summary>The tmpfs directory the daemon renders policy into. It moved off <c>/etc/mainguard</c>
    /// when the proxy gained a read-only rootfs (MG-25): the image's scripts stay on the immutable
    /// layer, only the rendered policy is writable.</summary>
    private const string ConfDir = "/run/mainguard";

    /// <summary>The reload entrypoint, still on the read-only image layer.</summary>
    private const string ReloadScript = "/etc/mainguard/reload.sh";

    /// <summary>Renders the allowlist to the proxy's config files + backstop script and applies them live.</summary>
    private async Task PushConfigAsync(string proxyId, CancellationToken ct)
    {
        // Auto-permit on install: union the installed agent CLIs' declared hosts (read fresh so a
        // CLI installed since the last spawn is included) into what the proxy renders — as direct-route
        // AgentService entries, never gateway-fronted (they are auth/console hosts, not model APIs).
        var effective = _installedAdapterHosts is null
            ? Allowlist
            : Allowlist.CombinedWith(_installedAdapterHosts(), EgressEntryKind.AgentService, "Agent CLI");

        // MG-7/MG-18: both the pinned-DNS self-record and the backstop's destination constraint need
        // the proxy's own address on the agent network. Resolved here, after the container is up and
        // attached, because Docker assigns it at attach time.
        var proxyAddress = await ResolveProxyAddressAsync(ct).ConfigureAwait(false);

        await WriteFileAsync(proxyId, ConfDir + "/tinyproxy-filter", EgressProxyConfig.RenderTinyproxyFilter(effective), ct).ConfigureAwait(false);
        // P2-08: front the model-API hosts through the AI gateway (token bucket + budgets + no-raw-429).
        if (_gatewayUpstream is not null)
        {
            await WriteFileAsync(proxyId, ConfDir + "/tinyproxy-upstreams",
                EgressProxyConfig.RenderTinyproxyUpstreams(effective, _gatewayUpstream), ct).ConfigureAwait(false);
        }

        await WriteFileAsync(proxyId, ConfDir + "/dnsmasq.conf", EgressProxyConfig.RenderDnsmasqConfig(effective, proxyAddress), ct).ConfigureAwait(false);
        await WriteFileAsync(proxyId, ConfDir + "/backstop.sh", EgressProxyConfig.RenderIptablesScript(ProxyPort, proxyAddress), ct).ConfigureAwait(false);
        // The image's entrypoint reloads tinyproxy/dnsmasq and (re)applies the backstop from these paths.
        await ExecAsync(proxyId, new[] { "sh", ReloadScript }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The largest rendered artefact delivered as an exec argument. Linux caps ONE argv entry at
    /// <c>MAX_ARG_STRLEN</c> = 32 pages = 128 KiB; this leaves generous headroom. Rendered policy is
    /// ~1 KiB (ten allowlist entries), so the fallback below is effectively unreachable — it exists so
    /// that an absurdly long allowlist degrades to the old transport instead of failing.
    /// </summary>
    private const int MaxArgvContentBytes = 64 * 1024;

    /// <summary>
    /// Writes one rendered POLICY artefact into the proxy container.
    ///
    /// <para>The content travels as an exec <b>argument</b>, not over the exec's stdin. Stdin here means
    /// a hijacked HTTP stream, and it has two failure modes that both end in a silently WRONG policy
    /// rather than an error: the reader (<c>cat</c>) only terminates on a socket half-close, which not
    /// every Docker endpoint propagates, and the upstream bytes themselves are not delivered at all by
    /// some socket proxies (Docker Desktop's WSL2 proxy delivers zero, verified). Either way the push
    /// stalls until <see cref="ExecTimeout"/> and the file is left holding whatever arrived first — a
    /// tinyproxy filter truncated to its header comment is a default-deny proxy that permits NOTHING,
    /// and a half-written backstop is worse. An argument is delivered by the same mechanism as the
    /// command itself, so there is no partial-write state to land in.</para>
    ///
    /// <para>This is for policy ONLY and must never carry a secret: an exec argument is visible in the
    /// target container's process table for the life of the call. That is fine here — the allowlist is
    /// user-visible policy, and this container is the proxy, which no agent can reach. Agent
    /// credentials go through a different path for exactly this reason (<c>DockerSandboxEngine</c>
    /// writes them over stdin, never argv/env — G-13).</para>
    ///
    /// <para><c>printf '%s'</c> with the content as a positional parameter, so the bytes are never
    /// interpolated into the script text: no quoting, no expansion, and a <c>%</c> in the content is
    /// data rather than a format directive. The result is byte-exact.</para>
    /// </summary>
    private async Task WriteFileAsync(string containerId, string path, string content, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(content) <= MaxArgvContentBytes)
        {
            await ExecAsync(
                containerId,
                new[] { "sh", "-c", "mkdir -p \"$(dirname \"$1\")\"; printf '%s' \"$2\" > \"$1\"", "sh", path, content },
                ct).ConfigureAwait(false);
            return;
        }

        await WriteFileOverStdinAsync(containerId, path, content, ct).ConfigureAwait(false);
    }

    /// <summary>The pre-existing stdin transport, kept only for content too large for an argv entry.</summary>
    private async Task WriteFileOverStdinAsync(string containerId, string path, string content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExecTimeout);
        var bounded = timeout.Token;

        var bytes = Encoding.UTF8.GetBytes(content);

        // `head -c <n>` rather than `cat`: reading an exact byte count is self-terminating, so at least
        // this path does not additionally depend on the half-close arriving.
        var length = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0",
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new List<string> { "sh", "-c", "mkdir -p \"$(dirname \"$1\")\"; head -c \"$2\" > \"$1\"", "sh", path, length },
        }, bounded).ConfigureAwait(false);
        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, bounded).ConfigureAwait(false);
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
/// MG-18 — a docker network with a mainguard name exists but no longer encodes the egress posture we
/// created it with (most importantly: <c>mainguard-agents</c> is not <c>Internal</c>). Raised INSTEAD of
/// reusing it, because reuse-by-name silently disables default-deny egress for every jail on it and
/// produces no error anywhere: the proxy still starts, the allowlist still renders, and the agents are
/// simply on the open internet. Fails closed and names the remediation.
/// </summary>
public sealed class EgressNetworkDriftException : Exception
{
    public EgressNetworkDriftException(string networkName, string detail)
        : base($"Refusing to reuse the docker network '{networkName}': {detail} "
             + $"Remove it and let mainguard recreate it (docker network rm {networkName}) — "
             + "note that any container still attached must be stopped first.")
    {
        NetworkName = networkName;
    }

    public string NetworkName { get; }
}

/// <summary>
/// The proxy container was started but never reached a Running state (it died on boot, or was removed
/// underneath us). Raised instead of letting the config exec fail with Docker's opaque
/// <c>409 "container … is not running"</c> from deep inside <c>ExecCreateContainerAsync</c>, which is
/// what made this path hard to diagnose and the CI suite intermittently red.
/// </summary>
public sealed class DockerContainerNotRunningException : Exception
{
    /// <param name="diagnostics">The container's state and its own log output, captured at the moment
    /// of failure. This used to say "inspect its logs (docker logs …)", which is no help on CI: the
    /// container is gone with the job before anyone reads it, so the one fact that explains the failure
    /// — what the container printed on its way out — was never recoverable. Empty only if the
    /// diagnostic itself could not run.</param>
    public DockerContainerNotRunningException(string containerId, string diagnostics = "")
        // NOTE: no guess about the cause in this sentence. It used to assert "it most likely exited
        // during boot", which was wrong for every externally-stopped container and sent readers hunting
        // a boot bug that did not exist. The verdict is derived from the container's actual state and
        // carried in the diagnostics below.
        : base($"The egress proxy container '{containerId}' was started but never reached a running state."
             + (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : diagnostics))
    {
        ContainerId = containerId;
        Diagnostics = diagnostics;
    }

    public string ContainerId { get; }

    /// <summary>The captured state + container logs (see the constructor).</summary>
    public string Diagnostics { get; } = string.Empty;
}
