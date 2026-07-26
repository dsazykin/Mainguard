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
}
