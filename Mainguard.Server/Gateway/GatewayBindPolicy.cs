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
