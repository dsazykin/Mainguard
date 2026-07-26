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
    ///
    /// <para>These lines are <b>appended into the generated <c>tinyproxy.conf</c></b> by
    /// <c>reload.sh</c>, not referenced from it: tinyproxy 1.11 has no <c>Include</c> directive and
    /// rejects one outright ("Syntax error … Unable to parse config file. Not starting."). The file was
    /// previously rendered on every push and read by nothing, so gateway fronting was inert while
    /// looking perfectly applied — see the reload script for the load step.</para>
    ///
    /// <para><paramref name="gatewayHostPort"/> null means fronting is disabled; the artefact is still
    /// rendered (header only, no <c>upstream</c> lines) so that EVERY push replaces the whole loaded
    /// upstream set. Skipping the write instead would leave a PREVIOUS push's upstreams on the proxy's
    /// tmpfs, still being appended into the config on every reload — policy that outlives the
    /// configuration that produced it, which is the same class of defect as never loading it at all.</para>
    /// </summary>
    public static string RenderTinyproxyUpstreams(EgressAllowlist allowlist, string? gatewayHostPort)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard model-API fronting — route model hosts through the P2-08 AI gateway\n");
        if (string.IsNullOrWhiteSpace(gatewayHostPort))
        {
            sb.Append("# (no gateway configured — model hosts keep their direct route)\n");
            return sb.ToString();
        }

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
    /// <param name="upstreamResolvers">
    /// The resolvers an allowlisted name is forwarded to — the proxy container's OWN stub resolver(s),
    /// read from its <c>/etc/resolv.conf</c> by <c>EgressProxyConfigurator.PushConfigAsync</c>. Empty
    /// or null falls back to <see cref="DockerEmbeddedResolver"/>, which is what that file names in
    /// every topology this proxy is actually created in.
    /// </param>
    public static string RenderDnsmasqConfig(
        EgressAllowlist allowlist,
        string? proxyAddress = null,
        IReadOnlyCollection<string>? upstreamResolvers = null)
    {
        var sb = new StringBuilder();
        sb.Append("# mainguard pinned DNS — answer allowlisted names only; all else NXDOMAIN\n");
        // no-resolv is a SECURITY control, not tidiness, and it is why the upstream below is named
        // explicitly instead of deferring to dnsmasq's own resolv.conf handling. Measured against the
        // real image: dropping no-resolv in favour of `server=/<host>/#` ("use the standard servers")
        // does make allowlisted names resolve — and it also gives dnsmasq a DEFAULT upstream, which
        // `address=/#/0.0.0.0` does not fully cover. That catch-all is an IPv4 address record, so it
        // answers the A query and nothing else: the AAAA query for the SAME non-allowlisted name falls
        // through to the default servers and is forwarded off the box. Observed directly in dnsmasq's
        // query log —
        //     query[A]    ipv6.google.com -> config ipv6.google.com is 0.0.0.0
        //     query[AAAA] ipv6.google.com -> forwarded ipv6.google.com to 127.0.0.11
        // — i.e. a live DNS-exfiltration channel for arbitrary names, which is the exact thing this
        // file exists to close (the exfiltrator only needs the QUERY to reach its authoritative server;
        // the answer is irrelevant). With no-resolv there is no default server to fall through to and
        // the same AAAA query is REFUSED locally. So: keep no-resolv, and route the allowlisted names
        // by naming their upstream.
        sb.Append("no-resolv\n");
        sb.Append("bogus-priv\n");
        // The agent fabric is IPv4-only by construction: EgressProxyConfigurator creates both the agent
        // segments and the egress network without EnableIPv6, so a jail has no IPv6 address and no IPv6
        // route — and the agent segments are additionally Internal. Handing a jail a AAAA record
        // therefore hands it an address it can NEVER reach, and which of the two families a tool picks
        // is up to that tool: an agent CLI that resolves a host itself gets a nondeterministic hang or
        // failure depending on nothing it can see or control, and the operator gets no signal at all.
        // dnsmasq is the jail's ONLY resolver (MG-7), so this is the one place that view can be made to
        // match the network the jail actually has. (Available in this image: dnsmasq 2.90, verified with
        // `dnsmasq --test --filter-AAAA` inside the built container — an unsupported option would be
        // fatal at startup rather than ignored.)
        sb.Append("filter-AAAA\n");

        // Defence in depth against a forwarding loop; see the DockerEmbeddedResolver summary for why
        // one cannot form in the topology as it stands. dnsmasq binds the WILDCARD 0.0.0.0:53 (verified
        // in /proc/net/udp inside the running proxy), so its own socket is the only thing bound on port
        // 53 in this netns — including at 127.0.0.11. The single reason a query to 127.0.0.11:53 does
        // not come straight back to dnsmasq is Docker's nat/OUTPUT DNAT, which rewrites it to the
        // embedded resolver's high port before it can be delivered. That rule is not ours, so this line
        // bounds what happens if it is ever absent: dnsmasq stops ANSWERING on loopback, so the query
        // is dropped and the lookup fails cleanly instead of dnsmasq re-forwarding its own query to
        // itself. Nothing legitimately queries dnsmasq over loopback — tinyproxy resolves through
        // /etc/resolv.conf (127.0.0.11, Docker's resolver), and the jails query the proxy's bridge
        // address — and that was confirmed by measurement, not assumed: with this line the proxy's own
        // 127.0.0.1:53 times out, without it the same query is answered.
        sb.Append("except-interface=lo\n");

        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            // MUST precede the catch-all: the jail's HTTP_PROXY names this host and nothing else can
            // answer for it once 127.0.0.11 is out of the jail's resolv.conf (see the summary).
            sb.Append("address=/").Append(EgressProxyConfigurator.ProxyContainerName).Append('/')
              .Append(proxyAddress.Trim()).Append('\n');
        }

        // Only the allowlisted names are forwarded, and only to the resolver(s) this CONTAINER was
        // given; the catch-all address=/#/ returns NXDOMAIN-equivalent (0.0.0.0) for everything else.
        var upstreams = NormalizeUpstreams(upstreamResolvers);
        foreach (var host in HostsOf(allowlist))
        {
            foreach (var resolver in upstreams)
            {
                sb.Append("server=/").Append(host).Append('/').Append(resolver).Append('\n');
            }
        }

        sb.Append("address=/#/0.0.0.0\n"); // catch-all: unresolvable
        return sb.ToString();
    }

    /// <summary>
    /// Docker's embedded resolver — the address Docker puts in <c>/etc/resolv.conf</c> for every
    /// container on a user-defined network, and therefore the proxy's own stub resolver, which forwards
    /// to whatever the HOST is configured to use.
    ///
    /// <para><b>Why the upstream is this and not a public resolver.</b> Every allowlisted domain used to
    /// be pinned to <c>1.1.1.1</c>. That is a public IP on the open internet, so in-jail DNS died
    /// anywhere an external resolver is blocked, intercepted or simply unreachable — corporate networks,
    /// captive portals, split-horizon DNS, an offline runner. It stayed invisible because tinyproxy does
    /// NOT use dnsmasq: it resolves through <c>/etc/resolv.conf</c>, i.e. Docker's embedded resolver, so
    /// egress by name THROUGH the proxy kept working while the agent's own resolution failed. The
    /// population that breaks is exactly the agent CLIs, which are Node and Go binaries carrying their
    /// own resolvers. Reproduced deterministically by blocking 1.1.1.1 in the proxy's netns: a
    /// cold-cache forwarded name then answers REFUSED (EDE: network error) to node while every
    /// locally-served record keeps working.</para>
    ///
    /// <para><b>Why this cannot loop.</b> A query dnsmasq sends to 127.0.0.11:53 is matched by Docker's
    /// own <c>nat</c> OUTPUT rule (<c>-A DOCKER_OUTPUT -d 127.0.0.11/32 -p udp --dport 53 -j DNAT
    /// --to-destination 127.0.0.11:&lt;high port&gt;</c>) and rewritten to the embedded resolver's real
    /// socket before routing, so it is never delivered to dnsmasq's own port-53 socket. Two things this
    /// depends on are already true and must stay true: the backstop restores <c>*filter</c> ONLY, so the
    /// <c>nat</c> table it lives in is untouched, and the backstop admits <c>-i lo</c>, without which
    /// the DNAT'd packet — locally generated to a local address, so it traverses INPUT — would be
    /// dropped by the default-deny chain. Note that dnsmasq's own "ignoring nameserver … local
    /// interface" guard does NOT cover this: it compares against assigned interface addresses, and
    /// 127.0.0.11 is not one (only 127.0.0.1 is). That guard was confirmed to work — pointing the
    /// upstream at the proxy's own bridge address does log it — so its silence for 127.0.0.11 is a
    /// measurement, not an assumption.</para>
    /// </summary>
    public const string DockerEmbeddedResolver = "127.0.0.11";

    /// <summary>The upstream set actually rendered: de-duplicated, order-preserved, and never empty.</summary>
    private static IReadOnlyList<string> NormalizeUpstreams(IReadOnlyCollection<string>? resolvers)
    {
        var normalized = (resolvers ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Falling back rather than rendering nothing is deliberate: a dnsmasq with no server for a
        // domain answers REFUSED, which is a total egress outage for every agent. The fallback is the
        // address Docker gives this container in every topology it is created in (see the constant).
        return normalized.Length == 0 ? new[] { DockerEmbeddedResolver } : normalized;
    }

    /// <summary>
    /// The IPv4 nameservers named by a <c>resolv.conf</c>. Pure so the parse is unit-assertable without
    /// a container.
    ///
    /// <para>IPv4 only, for the same reason <c>filter-AAAA</c> is set: the agent fabric has no IPv6
    /// route, so an IPv6 upstream would be a resolver dnsmasq can never reach — it would answer slowly
    /// and then fail, which is worse than not listing it. Docker never writes a loopback nameserver
    /// other than its own embedded resolver into a container's resolv.conf, so anything loopback that
    /// appears here IS that resolver.</para>
    /// </summary>
    internal static IReadOnlyList<string> ParseResolvConfNameservers(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var found = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            // Docker's generated resolv.conf carries commentary that mentions upstream addresses
            // ("# ExtServers: [host(192.168.65.7)]"), so comments must be dropped BEFORE matching —
            // a substring search for "nameserver" would happily pick a commented-out one up.
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "nameserver", StringComparison.Ordinal))
            {
                continue;
            }

            if (System.Net.IPAddress.TryParse(parts[1], out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !found.Contains(parts[1], StringComparer.Ordinal))
            {
                found.Add(parts[1]);
            }
        }

        return found;
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
    ///
    /// <para><b>The apply is ATOMIC, and that is a correctness requirement, not tidiness.</b> This used
    /// to be <c>iptables -F</c> followed by ~13 separate <c>iptables -A</c> processes. Measured against
    /// a real proxy: immediately after the flush the chain is <c>-P INPUT DROP</c> with <b>zero</b>
    /// rules — a total blackhole that drops even ESTABLISHED traffic — and the full re-apply takes
    /// <b>131 ms</b>. A config push runs on every agent spawn, so spawning agent B silently blackholed
    /// agent A's egress for a tenth of a second.</para>
    ///
    /// <para>Measured effect, with a jail connect-probing the proxy ~20×/s across 5 reloads: <b>20 of
    /// 118 probes failed</b>, in five clusters of exactly one per reload — hard <c>exit 7</c> connect
    /// failures, one-second <c>time_connect</c> values (a dropped SYN recovering on the retransmit),
    /// and an <c>exit 56</c> reset. Those are precisely the three symptoms CI reported
    /// (<c>tconnect=1.002318</c>, <c>exit=7 num_connects=0</c>, <c>Connection reset by peer</c>), and a
    /// dropped ESTABLISHED packet is also a clean explanation for an in-flight HTTP/2 stream dying
    /// mid-response.</para>
    ///
    /// <para>So the ruleset is now handed to <c>iptables-restore</c> as a complete table, which the
    /// kernel applies in a single netlink transaction: the chain moves from the old policy to the new
    /// one with nothing observable in between. Note this is NOT a return to the pre-MG-18 append
    /// behaviour — restoring a table replaces it, so reloads stay idempotent; the difference is that
    /// the replacement is one operation instead of fourteen.</para>
    /// </summary>
    /// <param name="proxyPort">The tinyproxy CONNECT port to admit.</param>
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
        sb.Append("#\n");
        sb.Append("# APPLIED ATOMICALLY. iptables-restore loads the whole table in ONE netlink\n");
        sb.Append("# transaction, so the chain goes straight from the old policy to the new one with no\n");
        sb.Append("# state in between. See the C# summary for the measurement that forced this.\n");
        sb.Append("set -eu\n");

        // The table is REPLACED, not edited: iptables-restore without --noflush swaps the whole filter
        // table in a single transaction. That is what makes reloads idempotent (the chain after N
        // reloads equals the chain after 1 — the original reason this stopped appending) AND
        // uninterrupted (there is never a moment when the policy is DROP with no rules).
        //
        // OUTPUT is declared ACCEPT explicitly. Restoring a table sets every chain in it, so leaving
        // OUTPUT out would not preserve it — it would reset it to this file's idea of the default. The
        // backstop has never filtered outbound traffic (dnsmasq's upstream queries and tinyproxy's
        // upstream connections both leave through it) and must not start now.
        sb.Append("iptables-restore <<'MAINGUARD_BACKSTOP_EOF'\n");
        sb.Append("*filter\n");
        sb.Append(":INPUT DROP [0:0]\n");
        sb.Append(":FORWARD DROP [0:0]\n");
        sb.Append(":OUTPUT ACCEPT [0:0]\n");

        sb.Append("-A INPUT -i lo -j ACCEPT\n");
        sb.Append("-A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT\n");
        foreach (var to in destinations)
        {
            sb.Append($"-A INPUT -p tcp{to} --dport {proxyPort} -j ACCEPT\n");
            sb.Append($"-A INPUT -p udp{to} --dport 53 -j ACCEPT\n");
            sb.Append($"-A INPUT -p tcp{to} --dport 53 -j ACCEPT\n");
        }

        sb.Append("-A INPUT -j DROP\n");

        sb.Append("-A FORWARD -m state --state ESTABLISHED,RELATED -j ACCEPT\n");
        foreach (var to in destinations)
        {
            sb.Append($"-A FORWARD -p tcp{to} --dport {proxyPort} -j ACCEPT\n");
            sb.Append($"-A FORWARD -p udp{to} --dport 53 -j ACCEPT\n");
            sb.Append($"-A FORWARD -p tcp{to} --dport 53 -j ACCEPT\n");
        }

        sb.Append("-A FORWARD -j DROP\n");
        sb.Append("COMMIT\n");
        sb.Append("MAINGUARD_BACKSTOP_EOF\n");
        return sb.ToString();
    }

    private static IEnumerable<string> HostsOf(EgressAllowlist allowlist) =>
        allowlist.Entries.Select(e => e.HostPattern).Distinct();
}
