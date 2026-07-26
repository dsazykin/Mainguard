using System;
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

    /// <summary>
    /// dnsmasq config: resolve ONLY allowlisted names; everything else NXDOMAIN.
    ///
    /// <para>MG-7 — with the jail's resolver now PINNED here (<c>HostConfig.Dns</c>), this file is the
    /// jail's whole view of DNS: Docker's embedded resolver at <c>127.0.0.11</c> is no longer in the
    /// jail's <c>resolv.conf</c>, so the container-name resolution it used to provide is gone too. The
    /// one name the jail still has to resolve is the proxy itself — <c>HTTP_PROXY</c> is
    /// <c>http://mainguard-egress-proxy:8888</c> — and without an explicit record for it the catch-all
    /// below would answer <c>0.0.0.0</c> and every proxied request would die. Hence
    /// <paramref name="proxyAddress"/>: pinning the resolver and forgetting this record turns
    /// "default-deny egress" into "no egress at all".</para>
    /// </summary>
    /// <param name="proxyAddress">The proxy's own IPv4 on the agent network. Null/empty only on the
    /// pre-MG-7 paths that render policy without a live proxy (pure tests).</param>
    public static string RenderDnsmasqConfig(EgressAllowlist allowlist, string? proxyAddress = null)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard pinned DNS — answer allowlisted names only; all else NXDOMAIN\n");
        sb.Append("no-resolv\n");
        sb.Append("bogus-priv\n");
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            // MUST precede the catch-all: the jail's HTTP_PROXY names this host and nothing else can
            // answer for it once 127.0.0.11 is out of the jail's resolv.conf (see the summary).
            sb.Append("address=/").Append(EgressProxyConfigurator.ProxyContainerName).Append('/')
              .Append(proxyAddress.Trim()).Append('\n');
        }

        // Only the allowlisted names are forwarded to the upstream resolver; the catch-all
        // address=/#/ returns NXDOMAIN-equivalent (0.0.0.0) for everything not explicitly served.
        foreach (var host in HostsOf(allowlist))
            sb.Append("server=/").Append(host).Append("/1.1.1.1\n");
        sb.Append("address=/#/0.0.0.0\n"); // catch-all: unresolvable
        return sb.ToString();
    }

    /// <summary>
    /// The proxy-namespace iptables backstop.
    ///
    /// <para>MG-18 — this script runs inside the <b>proxy container's</b> network namespace, so be
    /// precise about what it can and cannot enforce. It does <b>not</b> stop agent egress: an agent's
    /// packets are routed by the host-side bridge for <c>mainguard-agents</c> and never transit this
    /// namespace, so a <c>FORWARD</c> policy here has nothing to filter. What actually contains the
    /// agents is the network being <c>Internal</c> — which is now asserted on every reuse
    /// (<c>EgressProxyConfigurator.AssertNetworkMatchesPolicy</c>) instead of assumed.</para>
    ///
    /// <para>What this namespace CAN enforce is what reaches the proxy, and that is what the backstop
    /// is now written to do: a default-deny <c>INPUT</c> chain admitting only tinyproxy's CONNECT port
    /// and dnsmasq's 53, <b>and only at the proxy's own address</b>. Previously the ACCEPTs were
    /// <c>--dport</c>-only with no destination, i.e. "to anywhere" — which is not a restriction at all
    /// on a forwarding path and would admit any future listener in this container on those ports.
    /// The <c>FORWARD</c> policy is retained purely as defence in depth for the day someone gives this
    /// container a routing role; it is explicitly not the control keeping agents in.</para>
    /// </summary>
    /// <param name="proxyAddress">The proxy's own IPv4 on the agent network — the only legitimate
    /// destination for agent traffic. Null/empty falls back to port-only rules (pure tests).</param>
    public static string RenderIptablesScript(int proxyPort, string? proxyAddress = null) =>
        RenderIptablesScript(
            proxyPort,
            string.IsNullOrWhiteSpace(proxyAddress) ? Array.Empty<string>() : new[] { proxyAddress });

    /// <summary>
    /// MG-36 — the same backstop for a proxy that holds SEVERAL addresses, one per per-agent segment.
    ///
    /// <para>Segmenting the agents (one internal network each) necessarily gives the proxy a new
    /// interface, and therefore a new address, on every segment it fronts. The MG-18 destination
    /// constraint is what makes the ACCEPTs meaningful — "port 53 to anywhere" is not a restriction —
    /// so it has to be re-rendered as the segment set grows rather than dropped. Every admitted
    /// address gets its own rule; the chain stays default-deny and everything not named is DROPped,
    /// which is exactly the property the single-address form had.</para>
    /// </summary>
    /// <param name="proxyAddresses">Every address the proxy answers on across the agent segments.
    /// Empty falls back to port-only rules (the pure tests and the pre-MG-7 paths).</param>
    public static string RenderIptablesScript(int proxyPort, IReadOnlyCollection<string> proxyAddresses)
    {
        var destinations = (proxyAddresses ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(a => a, System.StringComparer.Ordinal)
            .Select(a => " -d " + a)
            .ToList();
        if (destinations.Count == 0)
        {
            destinations.Add(string.Empty); // port-only fallback
        }

        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# mainguard egress backstop (MG-18) — default-deny INPUT in the PROXY netns.\n");
        sb.Append("# Agent containment is the Internal network, asserted daemon-side; this chain bounds\n");
        sb.Append("# what an agent may reach INSIDE the proxy: tinyproxy + dnsmasq at the proxy's own\n");
        sb.Append("# address, nothing else. FORWARD stays default-deny as defence in depth only.\n");
        sb.Append("# MG-36: one rule per address — the proxy answers on every per-agent segment it fronts.\n");
        sb.Append("set -eu\n");

        // Flush first. Every reload used to APPEND, so the chain grew a fresh copy of every rule each
        // time — and because the copies land AFTER the terminal `-j DROP`, they are dead weight that
        // makes the live policy progressively harder to read (13 rules after two reloads instead of 8).
        // Applying policy has to be idempotent: the chain after N reloads must equal the chain after 1.
        sb.Append("iptables -F INPUT\n");
        sb.Append("iptables -F FORWARD\n");

        sb.Append("iptables -P INPUT DROP\n");
        sb.Append("iptables -A INPUT -i lo -j ACCEPT\n");
        sb.Append("iptables -A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT\n");
        foreach (var to in destinations)
        {
            sb.Append($"iptables -A INPUT -p tcp{to} --dport {proxyPort} -j ACCEPT\n");
            sb.Append($"iptables -A INPUT -p udp{to} --dport 53 -j ACCEPT\n");
            sb.Append($"iptables -A INPUT -p tcp{to} --dport 53 -j ACCEPT\n");
        }

        sb.Append("iptables -A INPUT -j DROP\n");

        sb.Append("iptables -P FORWARD DROP\n");
        sb.Append("iptables -A FORWARD -m state --state ESTABLISHED,RELATED -j ACCEPT\n");
        foreach (var to in destinations)
        {
            sb.Append($"iptables -A FORWARD -p tcp{to} --dport {proxyPort} -j ACCEPT\n");
            sb.Append($"iptables -A FORWARD -p udp{to} --dport 53 -j ACCEPT\n");
            sb.Append($"iptables -A FORWARD -p tcp{to} --dport 53 -j ACCEPT\n");
        }

        sb.Append("iptables -A FORWARD -j DROP\n");
        return sb.ToString();
    }

    private static IEnumerable<string> HostsOf(EgressAllowlist allowlist) =>
        allowlist.Entries.Select(e => e.HostPattern).Distinct();
}
