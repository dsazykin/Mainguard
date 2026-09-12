using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Mainguard.Server.Gateway;

/// <summary>
/// MG-13 — what the daemon is allowed to bind for the model gateway.
///
/// <para><b>Why this exists.</b> The daemon binds loopback only, and says so as a stated rule
/// ("never binds a wildcard / non-loopback address", invariant 2). That is load-bearing: MG-19's whole
/// finding is that loopback + a bearer token IS the trust boundary. But the agent jail sits on an
/// <c>Internal=true</c> Docker network and cannot reach loopback — from inside a container
/// <c>127.0.0.1</c> is the container itself — so a gateway on loopback is unreachable by the very
/// agents it exists to front, and MG-4's key confinement cannot work.</para>
///
/// <para>So the gateway listener is the ONE deliberate relaxation, and it is narrowed rather than
/// opened: it may bind loopback or a <b>private</b> (RFC 1918 / link-local) address — the Docker bridge
/// the egress proxy can reach — and <b>never</b> a wildcard or a routable public address. Wildcard is
/// the specific mistake this guards: <c>0.0.0.0</c> would expose the gateway to every network the host
/// is on, turning a jail-facing port into an internet-facing one.</para>
///
/// <para>The control plane (gRPC) is untouched and stays loopback-only.</para>
/// </summary>
internal static class GatewayBindPolicy
{
    /// <summary>Why a bind address was refused (empty when permitted).</summary>
    internal static bool IsPermitted(IPAddress? address, out string reason)
    {
        if (address is null)
        {
            reason = "No gateway bind address was supplied.";
            return false;
        }

        // A wildcard bind listens on EVERY interface — including whatever public network the host
        // happens to be on. This is the failure this policy exists to make impossible.
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            reason = $"'{address}' is a wildcard bind — the model gateway must never listen on every "
                   + "interface. Bind the Docker bridge address the agent network reaches.";
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            reason = string.Empty;
            return true;
        }

        if (IsPrivate(address))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"'{address}' is a routable public address — the model gateway may only bind loopback "
               + "or a private (RFC 1918) address such as the Docker bridge.";
        return false;
    }

    /// <summary>
    /// The name a container uses for "the machine the daemon runs on". Docker maps it to the host side
    /// of the container's bridge when the container is created with
    /// <c>--add-host=host.docker.internal:host-gateway</c> — which
    /// <c>EgressProxyConfigurator.ProxyHostConfig</c> now sets on the egress proxy. On Docker Desktop
    /// (macOS/Windows) that route reaches the host's <b>loopback</b> listeners, which is what makes a
    /// loopback bind usable as a gateway there.
    /// </summary>
    internal const string ProxyReachableHostAlias =
        Mainguard.Agents.Agents.Sandbox.EgressProxyConfigurator.GatewayHostAlias;

    /// <summary>The narrow default on a Docker Desktop host: loopback, reached via
    /// <see cref="ProxyReachableHostAlias"/>.</summary>
    private const string LoopbackBind = "127.0.0.1";

    /// <summary>
    /// F25 — the address the gateway binds when nothing was configured. This is deliberately the
    /// NARROWEST address that the egress proxy can still dial, and it is chosen per platform rather
    /// than by scanning every interface.
    ///
    /// <para><b>What it used to be, and why that was the finding.</b> It picked the lexicographically
    /// lowest private IPv4 on any up, non-loopback NIC. On a Mac with no 10.x/172.x that is the Wi-Fi
    /// address, so the daemon published a plaintext HTTP gateway — authenticated only by a token the
    /// agent itself can read and commit — on the operator's LAN. Anyone on that LAN who obtained the
    /// token could spend the operator's real provider key. Narrowing the bind removes the LAN from the
    /// exposure surface entirely; it does not remove the token-in-repo problem, which is what the
    /// scheduled rotation in <c>AgentGatewayCredentials</c> bounds.</para>
    ///
    /// <para><b>macOS / Windows → loopback.</b> Docker Desktop routes
    /// <c>host.docker.internal</c> (mapped to <c>host-gateway</c>) to the host's loopback stack, so a
    /// loopback-bound gateway IS reachable from a container on a non-internal network — verified on the
    /// target machine, not assumed. Nothing off the box can reach it at all.</para>
    ///
    /// <para><b>Linux (including the WSL2 VM) → the Docker bridge.</b> There <c>host-gateway</c> is the
    /// host side of <c>docker0</c>, and a host loopback listener is genuinely unreachable from a
    /// container, so loopback would be the "looks configured, confines nothing" failure the old comment
    /// warned about. The bridge address is reachable by containers and by this host, and by nothing
    /// else — it is not a LAN address. <c>docker0</c> is preferred over the per-network <c>br-*</c>
    /// bridges because its address is stable across network create/remove cycles.</para>
    ///
    /// <para>Null when no such address exists — the gateway is then simply disabled and every spawn
    /// behaves as it did before it existed. Falling back to an arbitrary up NIC is exactly the
    /// behaviour this finding removed, so there is no such fallback.</para>
    ///
    /// <para>This picks an address; it does not prove a jail can reach it. That is measured, per spawn,
    /// by <c>IEgressPolicy.CanProxyReachAsync</c> before any agent is confined — so a wrong guess here
    /// costs a skipped confinement, never a broken agent.</para>
    /// </summary>
    /// <summary>
    /// Resolved once per process. <see cref="DaemonOptions"/> is a record whose default runs this in a
    /// property initialiser, so every construction — and the test suites build hundreds — would otherwise
    /// enumerate every network interface. The host's addresses do not change under a daemon in any way
    /// that would make a re-resolve safe anyway: the chosen address is written into every confined jail's
    /// base-URL variable, so it has to stay stable for the life of the process. The memoization contract
    /// is unchanged by F25: on Docker Desktop the answer is now a compile-time constant (strictly more
    /// stable), and on Linux <c>docker0</c>'s address does not move under a running daemon.
    /// </summary>
    private static readonly Lazy<string?> Resolved = new(ResolveDefaultBindAddress, isThreadSafe: true);

    internal static string? TryResolvePrivateHostAddress() => Resolved.Value;

    /// <summary>
    /// The host a CONTAINER must dial to reach a gateway bound at <paramref name="bindAddress"/>.
    ///
    /// <para>These are the same string everywhere except loopback, and loopback is the case that
    /// matters: a jail's <c>NO_PROXY</c> covers <c>127.0.0.1</c>, so pointing a jail's base URL at
    /// loopback makes it dial ITSELF rather than the daemon. The alias below is the only name that
    /// crosses that boundary, and the egress proxy is created with the <c>host-gateway</c> mapping that
    /// resolves it.</para>
    ///
    /// <para>Callers: the gateway base URL written into a confined jail, and the <c>host:port</c> the
    /// egress proxy is told to permit and to probe. Both must agree, which is why the translation lives
    /// in one place beside the bind policy that created the need for it.</para>
    /// </summary>
    internal static string? ProxyReachableHostFor(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return null;
        }

        var value = bindAddress.Trim();
        return IPAddress.TryParse(value, out var parsed) && IPAddress.IsLoopback(parsed)
            ? ProxyReachableHostAlias
            : value;
    }

    private static string? ResolveDefaultBindAddress()
    {
        // Docker Desktop: loopback is both the narrowest bind and a reachable one (via the alias).
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            return LoopbackBind;
        }

        return TryResolveDockerBridgeAddress();
    }

    /// <summary>
    /// The IPv4 <c>docker0</c> holds on this host. Null when Docker has created no default bridge here,
    /// in which case there are no jails either and a gateway would have nothing to front.
    ///
    /// <para><b>Only <c>docker0</c>, and that is a containment decision, not laziness.</b> The
    /// user-defined <c>br-*</c> bridges include the per-agent segments themselves, and a container on an
    /// internal segment CAN reach its own bridge's address — measured on this engine:
    /// <c>ping 192.168.97.1</c> from inside an internal-network container answers in 0.05 ms, while
    /// <c>ping 172.17.0.1</c> from the same container is "Network is unreachable" (an internal segment
    /// has no default route, so only its own subnet is on-link). Binding the gateway to a segment's own
    /// bridge would therefore let a jail dial the gateway DIRECTLY, bypassing tinyproxy — and with it
    /// the F26 port bound and every other egress control. <c>docker0</c> is off every jail's subnet, so
    /// the only route to it is the one through the proxy. It is also the stable choice: its address
    /// survives network create/remove cycles, which the memoization contract needs.</para>
    /// </summary>
    private static string? TryResolveDockerBridgeAddress()
    {
        try
        {
            return SelectDockerBridgeAddress(
                System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Select(n => new HostInterface(n.Name, Addresses(n))));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // A host whose interfaces cannot be enumerated has no gateway address to offer. Disabled is
            // the correct answer, not a startup failure.
            return null;
        }
    }

    /// <summary>
    /// One host interface reduced to the only two things the bridge choice may depend on: its NAME and
    /// the addresses it holds. The operational status is deliberately absent — see
    /// <see cref="SelectDockerBridgeAddress"/>.
    /// </summary>
    internal readonly record struct HostInterface(string Name, IReadOnlyList<IPAddress> Addresses);

    private static IReadOnlyList<IPAddress> Addresses(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // One unreadable interface (a tunnel device that vanished mid-enumeration) must not cost the
            // host its gateway — every other interface still answers.
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// Picks <c>docker0</c>'s private IPv4 out of the host's interfaces — the whole of the Linux/WSL2
    /// bind decision, as a pure function so it can be asserted without a host to stand it up on.
    ///
    /// <para><b>The operational status is not an input, and that is the fix for a shipped regression.</b>
    /// This used to filter the interface list to <c>OperationalStatus == Up</c> before matching the name.
    /// A Linux bridge reports CARRIER rather than admin state: with no port attached it is
    /// <c>&lt;NO-CARRIER,BROADCAST,MULTICAST,UP&gt; state DOWN</c>,
    /// <c>/sys/class/net/&lt;name&gt;/operstate</c> reads <c>down</c>, and .NET maps that to
    /// <c>OperationalStatus.Down</c> — measured on this engine against a freshly created bridge with
    /// nothing attached. Mainguard puts every container on a user-defined network
    /// (<c>mainguard-agent</c>, the per-agent <c>br-*</c> segments) and never on the default bridge, so
    /// <b>docker0 is idle on precisely the hosts this resolver exists for</b>. The filter excluded it,
    /// the resolver returned null, <c>DaemonOptions.GatewayBindAddress</c> was therefore "disabled", and
    /// every BYOK spawn fell through to "confinement skipped" — the raw provider key into the jail,
    /// strictly worse than the LAN bind the narrowing was written to fix. On Windows the Pro daemon runs
    /// inside the WSL2 VM, so that was the production Windows path too.</para>
    ///
    /// <para><b>Why the interface and not Docker.</b> <c>docker network inspect bridge</c> (or
    /// <c>bip</c> in <c>daemon.json</c>) would also yield the address, but it makes resolving a bind
    /// address depend on an engine round-trip — inside a property initialiser that runs before anything
    /// has connected to Docker, on a machine where the engine may not be up yet — or on parsing a file
    /// that is usually absent. It also answers a subtly different question (what Docker INTENDS) where
    /// binding needs what the host actually holds. The interface is the same source Kestrel binds from.
    /// Carrier is irrelevant to bindability: an address configured on an admin-up bridge is bindable with
    /// or without a port attached, which is why the status is absent from <see cref="HostInterface"/>
    /// entirely rather than filtered on more leniently — a shape that cannot express the bug.</para>
    /// </summary>
    internal static string? SelectDockerBridgeAddress(IEnumerable<HostInterface> interfaces) =>
        (from nic in interfaces
         where string.Equals(nic.Name, "docker0", StringComparison.Ordinal)
         from address in nic.Addresses
         where address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
               && !IPAddress.IsLoopback(address)
               && IsPrivate(address)
               && address.GetAddressBytes()[0] != 169
         // Deterministic across restarts: the address is written into every confined jail's base URL.
         orderby address.ToString(), StringComparer.Ordinal
         select address.ToString()).FirstOrDefault();

    /// <summary>RFC 1918 / RFC 3927 / unique-local — i.e. not routable on the public internet.</summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (unique local) and fe80::/10 (link local).
            var v6 = address.GetAddressBytes();
            return (v6[0] & 0xFE) == 0xFC || (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80);
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            10 => true,                              // 10.0.0.0/8
            172 => b[1] >= 16 && b[1] <= 31,         // 172.16.0.0/12 (Docker's default bridge range)
            192 => b[1] == 168,                      // 192.168.0.0/16
            169 => b[1] == 254,                      // 169.254.0.0/16 link-local
            _ => false,
        };
    }
}
