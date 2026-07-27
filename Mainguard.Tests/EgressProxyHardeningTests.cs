using System.Linq;
using System.Text.Json;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-25 + MG-7 — the egress proxy's own posture.
///
/// <para>The proxy is the single chokepoint every agent's egress crosses, and it was the least hardened
/// container in the system: <c>NET_ADMIN</c> <b>and</b> <c>NET_RAW</c>, only <c>no-new-privileges</c>, no
/// seccomp profile, a writable rootfs, and no resource ceiling of any kind. A compromise there sees (and
/// can rewrite) the policy for every jail at once, so it now carries the same class of controls as the
/// jails it fronts.</para>
///
/// <para>The MG-7 half is here too: pinning the jail's resolver at this container means the jail loses
/// Docker's embedded resolver, and with it the container-name resolution that made
/// <c>HTTP_PROXY=http://mainguard-egress-proxy:8888</c> work. The rendered dnsmasq config has to answer
/// for the proxy's own name or "default-deny egress" becomes "no egress at all".</para>
/// </summary>
public sealed class EgressProxyHardeningTests
{
    private static EgressAllowlist Defaults() =>
        new(EgressAllowlist.DefaultEntries, new InMemoryAuditLog());

    // ---- MG-25: capabilities ----

    [Fact]
    public void ProxyHostConfig_DropsNetRaw_KeepingOnlyWhatTheProxyNeeds()
    {
        var host = EgressProxyConfigurator.ProxyHostConfig();

        Assert.Contains("ALL", host.CapDrop);

        // NET_ADMIN stays: the iptables backstop cannot be installed without it.
        Assert.Contains("NET_ADMIN", host.CapAdd);

        // NET_RAW goes: it gates raw/packet sockets, and the only consumer that would have needed it is
        // iptables-LEGACY (libiptc drives the classic tables over an AF_INET/SOCK_RAW socket). Debian
        // bookworm — this image's base — ships iptables-nft, which speaks netlink and is gated on
        // NET_ADMIN alone. Nothing else in the image opens a raw socket.
        Assert.DoesNotContain("NET_RAW", host.CapAdd);

        // The additions are each load-bearing, and their absence is why dnsmasq had NEVER started in
        // this container (verified against a real daemon). NET_BIND_SERVICE: under CapDrop ALL even
        // root cannot bind below 1024, so port 53 was unreachable. SETGID/SETUID: dnsmasq
        // unconditionally calls setgroups()/setgid()/setuid() to drop to its own unprivileged user and
        // exits 5 without them (--user=root does not help — setgroups() needs CAP_SETGID even for the
        // gid already held). KILL: once dnsmasq is no longer root, restarting it on a policy reload
        // needs CAP_KILL, and without it the reload leaves the OLD policy serving.
        // Net effect is LESS privilege, not more: dnsmasq ends up unprivileged instead of root.
        Assert.Contains("NET_BIND_SERVICE", host.CapAdd);
        Assert.Contains("SETGID", host.CapAdd);
        Assert.Contains("SETUID", host.CapAdd);
        Assert.Contains("KILL", host.CapAdd);

        // The set is still closed — no capability beyond the five that are individually justified.
        Assert.Equal(5, host.CapAdd.Count);
    }

    [Fact]
    public void ProxyHostConfig_RunsARealInit_SoOrphanedDaemonsAreReaped()
    {
        // Both daemons are backgrounded from a docker-exec shell that exits immediately, orphaning them
        // onto pid 1 — which is the image's `sleep infinity` and never calls wait(). Without a reaper
        // every policy reload leaks a zombie: they accumulate against PidsLimit, and a dead daemon
        // keeps its name in /proc, so "is it still running?" answers yes forever (which is how a
        // successful stop reads as a failed one, and a CRASHED dnsmasq reads as healthy).
        Assert.True(EgressProxyConfigurator.ProxyHostConfig().Init);
    }

    [Fact]
    public void ProxyHostConfig_CarriesTheSameDefaultDenySeccompProfileAsTheJails()
    {
        var host = EgressProxyConfigurator.ProxyHostConfig();

        Assert.Contains("no-new-privileges", host.SecurityOpt);

        var seccomp = Assert.Single(host.SecurityOpt, o => o.StartsWith("seccomp="));
        Assert.DoesNotContain("unconfined", seccomp);

        // A custom seccomp= REPLACES Docker's default rather than overlaying it, so assert the profile
        // really is deny-by-default — an ALLOW-all overlay here would be worse than having none.
        using var doc = JsonDocument.Parse(seccomp["seccomp=".Length..]);
        Assert.Equal("SCMP_ACT_ERRNO", doc.RootElement.GetProperty("defaultAction").GetString());
    }

    [Fact]
    public void ProxyHostConfig_IsReadOnly_WithTmpfsForEveryRuntimeWrite()
    {
        var host = EgressProxyConfigurator.ProxyHostConfig();

        Assert.True(host.ReadonlyRootfs);
        Assert.False(host.Privileged);

        // The rendered policy and both daemons' pid files live under /run (/var/run is a symlink to it
        // on debian) — which is exactly why the image's CONF_DIR moved off /etc.
        Assert.Contains("/run", host.Tmpfs.Keys);
        Assert.Contains("/tmp", host.Tmpfs.Keys);
    }

    [Fact]
    public void ProxyHostConfig_BoundsEveryResourceAxis()
    {
        var host = EgressProxyConfigurator.ProxyHostConfig();

        // The proxy sits on the one path every agent's egress takes; it must not be able to starve the
        // VM either. Two small daemons plus an iptables invocation need very little.
        Assert.True(host.Memory > 0);
        Assert.True(host.PidsLimit is > 0);
        Assert.True(host.NanoCPUs > 0);
        Assert.Contains(host.Ulimits, u => u.Name == "nofile" && u.Hard > 0);
    }

    // ---- MG-7: the pinned resolver must still answer for the proxy itself ----

    [Fact]
    public void Dnsmasq_AnswersForTheProxysOwnName_BeforeTheCatchAll()
    {
        const string proxyAddress = "172.30.0.2";
        var conf = EgressProxyConfig.RenderDnsmasqConfig(Defaults(), proxyAddress);

        var selfRecord = $"address=/{EgressProxyConfigurator.ProxyContainerName}/{proxyAddress}";
        Assert.Contains(selfRecord, conf);

        // Order is load-bearing: the catch-all answers 0.0.0.0 for everything it reaches, so a self
        // record after it would leave the jail unable to resolve its own HTTP_PROXY host.
        Assert.True(
            conf.IndexOf(selfRecord, System.StringComparison.Ordinal)
                < conf.IndexOf("address=/#/0.0.0.0", System.StringComparison.Ordinal),
            "the proxy's self record must precede the NXDOMAIN catch-all");
    }

    [Fact]
    public void Dnsmasq_StillNxdomainsEverythingNotAllowlisted()
    {
        // Non-vacuity guard on the record above: adding it must not have opened anything else up.
        var conf = EgressProxyConfig.RenderDnsmasqConfig(Defaults(), "172.30.0.2");

        Assert.Contains("no-resolv", conf);
        Assert.Contains("address=/#/0.0.0.0", conf);
        Assert.DoesNotContain("server=/example.com/", conf);
    }

    // ---- in-jail DNS must not depend on a PUBLIC resolver ----

    /// <summary>
    /// No allowlisted name may be pinned to a resolver out on the internet.
    ///
    /// <para>Every one of them used to be pinned to <c>1.1.1.1</c>, so in-jail DNS died anywhere an
    /// external resolver is blocked, intercepted or unreachable — corporate networks, captive portals,
    /// split-horizon DNS, an offline runner. It stayed invisible because tinyproxy does NOT resolve
    /// through dnsmasq (it uses <c>/etc/resolv.conf</c>, i.e. Docker's embedded resolver), so egress by
    /// name THROUGH the proxy kept working while the agent's own resolution failed — and the agent CLIs
    /// are Node and Go binaries carrying their own resolvers, which is exactly the population that
    /// breaks. Reproduced against the real image by blocking 1.1.1.1 in the proxy's netns: a cold-cache
    /// forwarded name then answers REFUSED while every locally-served record keeps working.</para>
    ///
    /// <para>Asserted as "no public literal" rather than "not 1.1.1.1", because swapping one hardcoded
    /// public resolver for another would satisfy the narrower assertion while leaving the defect
    /// exactly where it was.</para>
    /// </summary>
    [Fact]
    public void Dnsmasq_ForwardsToTheContainersOwnResolver_NotAPublicOne()
    {
        var conf = EgressProxyConfig.RenderDnsmasqConfig(Defaults(), "172.30.0.2");

        var forwards = conf.Split('\n')
            .Where(l => l.StartsWith("server=/", System.StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(forwards); // the allowlist must still be forwarded somewhere

        foreach (var line in forwards)
        {
            var upstream = line[(line.LastIndexOf('/') + 1)..];
            Assert.True(
                System.Net.IPAddress.TryParse(upstream, out var address)
                    && System.Net.IPAddress.IsLoopback(address),
                $"'{line}' forwards an allowlisted name to '{upstream}', which is not this container's "
                + "own stub resolver. A public resolver here means in-jail DNS breaks wherever external "
                + "resolvers are blocked, intercepted or unreachable.");
        }
    }

    /// <summary>
    /// The upstream is whatever the proxy container's <c>/etc/resolv.conf</c> names, and the fallback is
    /// used only when that read yields nothing.
    /// </summary>
    [Fact]
    public void Dnsmasq_UsesTheDiscoveredResolvers_AndFallsBackWhenThereAreNone()
    {
        var discovered = EgressProxyConfig.RenderDnsmasqConfig(
            Defaults(), "172.30.0.2", new[] { "10.0.0.53", "10.0.0.54" });
        Assert.Contains("server=/api.anthropic.com/10.0.0.53\n", discovered, System.StringComparison.Ordinal);
        Assert.Contains("server=/api.anthropic.com/10.0.0.54\n", discovered, System.StringComparison.Ordinal);

        // Rendering NO server for a domain would answer REFUSED — a total egress outage — so an empty
        // discovery falls back to the address Docker gives this container in every topology it is
        // created in, rather than leaving the domain unrouted.
        var fallback = EgressProxyConfig.RenderDnsmasqConfig(Defaults(), "172.30.0.2", System.Array.Empty<string>());
        Assert.Contains(
            $"server=/api.anthropic.com/{EgressProxyConfig.DockerEmbeddedResolver}\n",
            fallback, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>no-resolv</c> is a SECURITY control and must survive the upstream change.
    ///
    /// <para>The obvious way to defer to the host resolver is to drop <c>no-resolv</c> and write
    /// <c>server=/&lt;host&gt;/#</c> ("use the standard servers"). Measured against the real image, that
    /// resolves allowlisted names correctly AND opens a DNS-exfiltration channel: giving dnsmasq a
    /// default upstream means the catch-all no longer covers everything, because
    /// <c>address=/#/0.0.0.0</c> is an IPv4 record and answers only the A query. The AAAA query for the
    /// same non-allowlisted name falls through and is forwarded off the box —
    /// <c>query[AAAA] ipv6.google.com → forwarded ipv6.google.com to 127.0.0.11</c> in dnsmasq's own
    /// query log — and an exfiltrator needs only the QUERY to reach its authoritative server. With
    /// <c>no-resolv</c> there is no default server to fall through to and the same query is REFUSED
    /// locally.</para>
    /// </summary>
    [Fact]
    public void Dnsmasq_KeepsNoResolv_SoANonAllowlistedNameHasNoDefaultUpstreamToLeakTo()
    {
        var conf = EgressProxyConfig.RenderDnsmasqConfig(
            Defaults(), "172.30.0.2", new[] { "127.0.0.11" });

        Assert.Contains("\nno-resolv\n", conf, System.StringComparison.Ordinal);

        // The "use the standard servers" spec is the shape that requires dropping no-resolv; its
        // presence would mean the leak above is back regardless of what the no-resolv line says.
        Assert.DoesNotContain("/#\n", conf, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// dnsmasq must not answer on loopback — the guard that keeps a forwarding loop impossible.
    ///
    /// <para>dnsmasq binds the wildcard <c>0.0.0.0:53</c>, so its own socket is the only thing bound on
    /// port 53 in the proxy's netns, 127.0.0.11 included (verified in <c>/proc/net/udp</c> inside the
    /// running container). What actually stops a query to 127.0.0.11:53 coming straight back is Docker's
    /// <c>nat</c> OUTPUT DNAT to the embedded resolver's high port — a rule that is not ours. This line
    /// bounds the blast radius if it is ever absent: the query is dropped and the lookup fails cleanly
    /// instead of dnsmasq re-forwarding its own query to itself. Verified non-vacuous against the real
    /// image: with the line the proxy's own 127.0.0.1:53 times out, without it the same query is
    /// answered.</para>
    /// </summary>
    [Fact]
    public void Dnsmasq_DoesNotServeOnLoopback_SoAForwardingLoopCannotForm()
    {
        var conf = EgressProxyConfig.RenderDnsmasqConfig(Defaults(), "172.30.0.2");
        Assert.Contains("\nexcept-interface=lo\n", conf, System.StringComparison.Ordinal);
    }

    [Theory]
    // Docker's generated resolv.conf: the embedded resolver, plus commentary that NAMES upstream
    // addresses ("# ExtServers: [host(192.168.65.7)]") — so comments must be dropped before matching.
    [InlineData("# Generated by Docker Engine.\nnameserver 127.0.0.11\noptions ndots:0\n"
              + "# Based on host file: '/etc/resolv.conf'\n# ExtServers: [host(192.168.65.7)]\n", "127.0.0.11")]
    [InlineData("nameserver 10.0.0.53\nnameserver 10.0.0.54\n", "10.0.0.53,10.0.0.54")]
    [InlineData("nameserver 10.0.0.53\nnameserver 10.0.0.53\n", "10.0.0.53")]            // de-duplicated
    [InlineData("#nameserver 8.8.8.8\n;nameserver 8.8.4.4\n", "")]                        // commented out
    // IPv6 upstreams are dropped for the same reason filter-AAAA is set: the fabric has no IPv6 route,
    // so listing one yields a resolver dnsmasq can never reach.
    [InlineData("nameserver fe80::1\nnameserver 10.0.0.53\n", "10.0.0.53")]
    [InlineData("search example.com\noptions ndots:0\n", "")]
    [InlineData("", "")]
    public void ResolvConf_ParsesOnlyRealIPv4Nameservers(string content, string expected)
    {
        var parsed = EgressProxyConfig.ParseResolvConfNameservers(content);
        Assert.Equal(expected, string.Join(',', parsed));
    }
}
