using System;
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
    /// MG-4 — the address the gateway binds when nothing was configured: a private, non-loopback IPv4 on
    /// an interface that is up. Null when the host has none.
    ///
    /// <para><b>Loopback is deliberately not a candidate.</b> It is permitted by
    /// <see cref="IsPermitted"/> for an operator who knows what they are doing, but as a DEFAULT it is
    /// the worst possible choice: it binds successfully, passes every in-process check, and is reachable
    /// by nothing that matters — from inside a container <c>127.0.0.1</c> is the container. A gateway
    /// there would look configured and confine nothing.</para>
    ///
    /// <para>Link-local (169.254/16) is excluded for the same reason in reverse: on a host with no real
    /// network it is the address the OS invents, and it is not a route a Docker bridge shares. A
    /// deterministic order (lowest address first) keeps the choice stable across daemon restarts, which
    /// matters because the address is written into every confined jail's base-URL variable.</para>
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
    /// base-URL variable, so it has to stay stable for the life of the process.
    /// </summary>
    private static readonly Lazy<string?> Resolved = new(ResolvePrivateHostAddress, isThreadSafe: true);

    internal static string? TryResolvePrivateHostAddress() => Resolved.Value;

    private static string? ResolvePrivateHostAddress()
    {
        try
        {
            var candidates =
                from nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                where nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                      && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                from unicast in nic.GetIPProperties().UnicastAddresses
                let address = unicast.Address
                where address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                      && !IPAddress.IsLoopback(address)
                      && IsPrivate(address)
                      && address.GetAddressBytes()[0] != 169
                orderby address.ToString(), StringComparer.Ordinal
                select address.ToString();

            return candidates.FirstOrDefault();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // A host whose interfaces cannot be enumerated has no gateway address to offer. Disabled is
            // the correct answer, not a startup failure.
            return null;
        }
    }

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
