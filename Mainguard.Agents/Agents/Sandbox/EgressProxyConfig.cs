using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Pure renderer for the egress-proxy container's runtime config from an <see cref="EgressAllowlist"/>
/// (P2-07 §3.3). Produces three artefacts the <c>mainguard-egress-proxy</c> image consumes: a tinyproxy
/// allowlist (the HTTP(S) CONNECT allow-filter), a dnsmasq config that answers <b>only</b> allowlisted
/// names (everything else NXDOMAIN — kills DNS exfiltration), and an iptables script that DROPs any
/// non-proxy egress (the backstop — proxy-env-only enforcement is a rejection trigger). Kept pure so
/// the exact rendered policy is unit-assertable without a running container.
/// </summary>
public static class EgressProxyConfig
{
    /// <summary>tinyproxy <c>Filter</c> allowlist: one anchored hostname per line (default-deny + FilterDefaultDeny).</summary>
    public static string RenderTinyproxyFilter(EgressAllowlist allowlist)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard egress allowlist — default-deny (tinyproxy FilterDefaultDeny Yes)\n");
        foreach (var host in HostsOf(allowlist))
            sb.Append(RenderHostPattern(host)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// MG-28 — renders ONE allowlist host as an anchored tinyproxy filter regex.
    ///
    /// <para>Previously every entry was rendered as <c>^{host with dots escaped}$</c>, which produces
    /// an <b>invalid regex</b> for a wildcard entry: <c>*.example.com</c> became
    /// <c>^*\.example\.com$</c>, whose leading <c>*</c> is a quantifier with nothing to repeat. The
    /// policy the UI shows and the filter tinyproxy actually enforces therefore diverged for exactly
    /// the entries a user is most likely to add by hand.</para>
    ///
    /// <para>The rendering now mirrors <c>EgressAllowlist.HostMatches</c>: <c>*.example.com</c> allows
    /// any subdomain AND the apex, so it becomes <c>^([a-z0-9-]+\.)*example\.com$</c>. A non-wildcard
    /// entry stays an exact anchored match.</para>
    /// </summary>
    internal static string RenderHostPattern(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith("*.", System.StringComparison.Ordinal))
        {
            // "*.example.com" -> apex + any depth of subdomain, matching HostMatches exactly.
            return "^([a-z0-9-]+\\.)*" + Escape(h[2..]) + "$";
        }

        return "^" + Escape(h) + "$";
    }

    // Hostnames are [a-z0-9.-]; only the dot is a regex metacharacter among those.
    private static string Escape(string host) => host.Replace(".", "\\.");

    /// <summary>
    /// tinyproxy <c>upstream</c> directives that route every <b>model-API</b> host through the P2-08
    /// AI gateway (<paramref name="gatewayHostPort"/>) instead of straight to the provider. This is the
    /// mechanism that makes the gateway <i>front</i> the model hosts on the egress path: the proxy
    /// forwards a model request to the gateway, which applies the shared-key token bucket + budget +
    /// no-raw-429 handling before the request reaches the real provider. A model-host allowlist entry
    /// without this gateway fronting is a rejection trigger, so this is emitted for every
    /// <see cref="EgressEntryKind.ModelApi"/> entry. Non-model hosts keep their direct route.
    /// </summary>
    public static string RenderTinyproxyUpstreams(EgressAllowlist allowlist, string gatewayHostPort)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard model-API fronting — route model hosts through the P2-08 AI gateway\n");
        foreach (var entry in allowlist.Entries)
        {
            if (entry.Kind == EgressEntryKind.ModelApi)
            {
                sb.Append("upstream http ").Append(gatewayHostPort)
                  .Append(" \"").Append(entry.HostPattern).Append("\"\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>dnsmasq config: resolve ONLY allowlisted names; everything else NXDOMAIN.</summary>
    public static string RenderDnsmasqConfig(EgressAllowlist allowlist)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard pinned DNS — answer allowlisted names only; all else NXDOMAIN\n");
        sb.Append("no-resolv\n");
        sb.Append("bogus-priv\n");
        // Only the allowlisted names are forwarded to the upstream resolver; the catch-all
        // address=/#/ returns NXDOMAIN-equivalent (0.0.0.0) for everything not explicitly served.
        foreach (var host in HostsOf(allowlist))
            sb.Append("server=/").Append(host).Append("/1.1.1.1\n");
        sb.Append("address=/#/0.0.0.0\n"); // catch-all: unresolvable
        return sb.ToString();
    }

    /// <summary>
    /// iptables backstop: DROP all egress from the agent network except to the proxy's listener.
    /// This is the control that makes direct-IP egress (bypassing HTTP_PROXY) fail — the named
    /// rejection trigger is enforcing egress by proxy env alone, without this.
    /// </summary>
    public static string RenderIptablesScript(int proxyPort)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# mainguard egress backstop — DROP non-proxy egress (proxy-env-only is a rejection trigger)\n");
        sb.Append("set -eu\n");
        sb.Append("iptables -P FORWARD DROP\n");
        sb.Append("iptables -A FORWARD -m state --state ESTABLISHED,RELATED -j ACCEPT\n");
        // Allow only traffic destined for the proxy's CONNECT/DNS listeners; drop the rest.
        sb.Append($"iptables -A FORWARD -p tcp --dport {proxyPort} -j ACCEPT\n");
        sb.Append("iptables -A FORWARD -p udp --dport 53 -j ACCEPT\n");
        sb.Append("iptables -A FORWARD -p tcp --dport 53 -j ACCEPT\n");
        sb.Append("iptables -A FORWARD -j DROP\n");
        return sb.ToString();
    }

    private static IEnumerable<string> HostsOf(EgressAllowlist allowlist) =>
        allowlist.Entries.Select(e => e.HostPattern).Distinct();
}
