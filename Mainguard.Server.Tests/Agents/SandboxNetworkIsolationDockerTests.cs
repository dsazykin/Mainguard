using System;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-36 — east-west isolation, read off real containers on a real Docker daemon.
///
/// <para><b>The finding.</b> Every jail attached to one flat <c>mainguard-agents</c> segment, so agent
/// A could dial agent B's container IP and any port B had open. Egress-to-the-internet was contained
/// and the daemon was unreachable, but there was no control at all between tenants — a prompt-injected
/// agent could reach straight into another agent's process, and nothing in the system would notice.</para>
///
/// <para><b>Why this test exists in this shape.</b> The isolation claim is a NEGATIVE one ("A cannot
/// reach B"), and a negative is trivially satisfiable by accident: if B's listener never started, or
/// B's container died, or the probe was malformed, every assertion here would pass while proving
/// nothing. So each blocked probe is paired with a positive control that fails if the setup is broken
/// — B reaches its own listener, and A reaches its own proxy. Both directions are asserted in the same
/// test so they cannot drift apart.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public class SandboxNetworkIsolationDockerTests
{
    /// <summary>An arbitrary port B listens on. Above 1024 so the jail's unprivileged uid can bind it.</summary>
    private const int VictimPort = 9099;

    [RequiresDockerFact]
    public async Task AgentA_CannotReachAgentB_ButStillReachesItsOwnProxy()
    {
        await using var fx = new SandboxFixture();

        var repo = "mg36" + Guid.NewGuid().ToString("N")[..8];
        var (attacker, attackerSegment) = await fx.CreateJailOnSegmentAsync(repo, "agent-a");
        var (victim, victimSegment) = await fx.CreateJailOnSegmentAsync(repo, "agent-b");

        // Two agents, two segments — the structural half of the fix.
        Assert.NotEqual(attackerSegment.NetworkName, victimSegment.NetworkName);
        Assert.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, attackerSegment.NetworkName, StringComparison.Ordinal);

        var victimAddress = await fx.AddressOnAsync(victim, victimSegment.NetworkName);
        Assert.False(string.IsNullOrEmpty(victimAddress), "agent-b has no address on its own segment");

        // --- CONTROL 1: B's listener is genuinely up and serving. -------------------------------
        // Without this, "A got nothing from B" would be indistinguishable from "B was never there".
        await StartVictimListenerAsync(fx, victim);
        var loopback = await ProbeAsync(fx, victim, $"127.0.0.1:{VictimPort}");
        Assert.True(loopback.Reached,
            $"the victim's own listener is not answering, so this test cannot prove anything about "
            + $"reachability. {loopback.Detail}");

        // --- CONTROL 2: A's egress path is intact. ----------------------------------------------
        // Segmentation must not have severed the one hop a jail legitimately needs. A reply of any
        // kind from the proxy's CONNECT port proves the TCP hop across A's own segment works.
        var ownProxy = await ProbeAsync(fx, attacker, $"{attackerSegment.ProxyAddress}:{EgressProxyConfigurator.ProxyPort}");
        Assert.True(ownProxy.Reached,
            $"agent-a can no longer reach the egress proxy on its own segment — segmentation broke the "
            + $"one hop the jail needs. {ownProxy.Detail}");

        // --- THE FINDING: A -> B is blocked. ----------------------------------------------------
        var eastWest = await ProbeAsync(fx, attacker, $"{victimAddress}:{VictimPort}");
        Assert.False(eastWest.Reached,
            $"agent-a REACHED agent-b at {victimAddress}:{VictimPort} — the jails are still on a shared "
            + $"segment and MG-36 is not fixed. {eastWest.Detail}");

        // --- And A cannot hop to another segment via the proxy's address there. -----------------
        // The proxy is a member of every segment, so its OTHER addresses are the obvious pivot. They
        // are unreachable for the same reason B is: internal networks are isolated from one another.
        var crossSegment = await ProbeAsync(
            fx, attacker, $"{victimSegment.ProxyAddress}:{EgressProxyConfigurator.ProxyPort}");
        Assert.False(crossSegment.Reached,
            $"agent-a reached the proxy's address on agent-b's segment ({victimSegment.ProxyAddress}) — "
            + $"the segments are not isolated. {crossSegment.Detail}");
    }

    /// <summary>
    /// The leg-1 reachability probe must be able to report REACHABLE.
    ///
    /// <para>This exists because the previous probe could not. It ran
    /// <c>sh -c 'echo &gt; /dev/tcp/host/port'</c>; <c>/dev/tcp</c> is a <b>bash</b> builtin and
    /// <c>sh</c> is dash on Debian, so it failed with <c>cannot create /dev/tcp/…: Directory
    /// nonexistent</c> and printed UNREACHABLE <b>on every run, unconditionally</b> — including against
    /// a hop that was demonstrably healthy. It shipped a permanent false negative into the one
    /// diagnostic a CI failure is read through, and the diagnostic's own guide turned that into
    /// "the jail cannot reach the proxy", pointing the next investigation at the wrong leg.</para>
    ///
    /// <para>A probe that only ever returns one answer is not measuring anything, and the only way to
    /// catch that is to assert the answer it is supposed to give when the thing works. Both directions
    /// are pinned here: REACHABLE for the live proxy, UNREACHABLE for a closed port on the same host —
    /// so a probe that degenerated to "always REACHABLE" would fail too.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ReachabilityProbe_ReportsReachable_ForAHealthyHop()
    {
        await using var fx = new SandboxFixture();

        var repo = "mg36" + Guid.NewGuid().ToString("N")[..8];
        var (jail, segment) = await fx.CreateJailOnSegmentAsync(repo, "agent-a");

        var live = await fx.TcpProbeAsync(jail, $"{segment.ProxyAddress}:{EgressProxyConfigurator.ProxyPort}");
        Assert.True(live.Reached,
            $"the reachability probe cannot report success against a healthy jail->proxy hop, so it "
            + $"cannot diagnose anything. {live.Detail}");

        // The other direction, on the same reachable host: a port nothing listens on must read
        // UNREACHABLE, or the probe is just saying yes to everything.
        var closed = await fx.TcpProbeAsync(jail, $"{segment.ProxyAddress}:9");
        Assert.False(closed.Reached, $"the probe reported a closed port as reachable. {closed.Detail}");
    }

    // The other half of the containment claim, restated on the segmented topology: a jail must still
    // have no path to the daemon. The daemon listens on the VM's loopback, and an internal network has
    // no route off the bridge at all — but "still true after the topology changed" is the only version
    // of that statement worth anything, so it is asserted rather than assumed.
    [RequiresDockerFact]
    public async Task AJailOnItsOwnSegment_StillHasNoRouteOffTheBridge()
    {
        await using var fx = new SandboxFixture();

        var repo = "mg36" + Guid.NewGuid().ToString("N")[..8];
        var (jail, segment) = await fx.CreateJailOnSegmentAsync(repo, "agent-a");

        // The segment is Internal — the property that keeps the jail off the outside world (MG-18).
        var inspect = await fx.Docker.Networks.InspectNetworkAsync(segment.NetworkName);
        Assert.True(inspect.Internal, $"the per-agent segment '{segment.NetworkName}' is not Internal");

        // The daemon's port on the VM loopback, dialled by address rather than by name so no resolver
        // is involved: an internal bridge has no route to the host, so this cannot connect.
        var daemon = await ProbeAsync(fx, jail, "127.0.0.1:5250");
        Assert.False(daemon.Reached, $"a jail reached the daemon's port. {daemon.Detail}");
    }

    /// <summary>
    /// The jail's view of DNS matches the fabric it actually has: IPv4 only.
    ///
    /// <para>Both the agent segments and the egress network are created without IPv6, so a jail has no
    /// IPv6 address and no IPv6 route. An AAAA record handed to a jail is therefore an address it can
    /// never reach, and whether a given tool picks it is up to that tool — a nondeterministic failure
    /// with no signal, which is the worst kind to meet in the field. dnsmasq is the jail's only
    /// resolver (MG-7), so its rendered config carries <c>filter-AAAA</c>.</para>
    ///
    /// <para>Asserted against a live jail rather than only against the renderer, because the risk here
    /// is not "did we write the line" but "does dnsmasq accept it" — an option this build did not
    /// support would be fatal at startup and take pinned DNS down with it. The dnsmasq health check is
    /// part of the assertion for exactly that reason.</para>
    ///
    /// <para><b>The probe is deliberately node, not <c>getent</c>.</b> glibc short-circuits: on a host
    /// with no non-loopback IPv6 address it never sends the AAAA query at all, so <c>getent ahosts</c>
    /// reports IPv4-only whether or not <c>filter-AAAA</c> is set — it cannot tell the two apart, and a
    /// test built on it passes vacuously (measured). The agent CLIs are Node and Go binaries that carry
    /// their OWN resolvers and do issue the AAAA query regardless, which is exactly the population this
    /// control exists for, so the assertion is made through the resolver they actually use.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task JailResolution_IsIPv4Only_MatchingTheIPv4OnlyFabric()
    {
        await using var fx = new SandboxFixture();

        var repo = "mg36" + Guid.NewGuid().ToString("N")[..8];
        var (jail, _) = await fx.CreateJailOnSegmentAsync(repo, "agent-a");

        // dnsmasq accepted the config and is serving — without this the assertions below could pass
        // simply because nothing resolves at all.
        var status = await fx.ExecAsync(
            EgressProxyConfigurator.ProxyContainerName, "cat", "/run/mainguard/dnsmasq.status");
        Assert.Equal("ok", status.Stdout.Trim());

        // CONTROL: an allowlisted name still resolves over A. filter-AAAA must narrow the answer, not
        // break resolution — a resolver that answers nothing would satisfy the AAAA assertion below
        // while making the jail useless.
        var v4 = await fx.ExecAsync(jail, "node", "-e",
            "require('dns').resolve4('api.anthropic.com',(e,a)=>console.log(e?'ERR '+e.code:a.join(',')))");
        Assert.Equal(0, v4.ExitCode);
        Assert.DoesNotContain("ERR", v4.Stdout, StringComparison.Ordinal);

        // THE CONTROL UNDER TEST: the same name must yield no AAAA, so a CLI that asks for one is told
        // there is none instead of being handed an address the jail can never route to.
        // The two sentinels must not be substrings of one another — "NOAAAA" contains "AAAA", and an
        // assertion that cannot tell its own two outcomes apart is the bug this file keeps finding.
        var v6 = await fx.ExecAsync(jail, "node", "-e",
            "require('dns').resolve6('api.anthropic.com',(e,a)=>console.log(e?'RESULT-none '+e.code:'RESULT-got '+a.join(',')))");
        Assert.Equal(0, v6.ExitCode);
        Assert.DoesNotContain("RESULT-got", v6.Stdout, StringComparison.Ordinal);
        Assert.Contains("RESULT-none", v6.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Starts a trivial TCP listener inside the victim jail (python3 is pre-baked into the
    /// agent image's toolchain), and waits for it to actually bind — "the process was launched" and
    /// "the socket accepts" are different facts, and the gap between them is a real window.</summary>
    private static async Task StartVictimListenerAsync(SandboxFixture fx, string containerId)
    {
        await fx.ExecAsync(containerId, "sh", "-c",
            $"(python3 -m http.server {VictimPort} --bind 0.0.0.0 </dev/null >/dev/null 2>&1 &) ; exit 0");

        for (var i = 0; i < 50; i++)
        {
            // /proc/net/tcp, hex port, state 0A = LISTEN. Parsed directly so this needs no ss/netstat.
            var listening = await fx.ExecAsync(containerId, "sh", "-c",
                $"awk 'NR>1{{split($2,a,\":\"); if (a[2]==\"{VictimPort:X4}\" && $4==\"0A\") f=1}} END{{exit(f?0:1)}}' /proc/net/tcp");
            if (listening.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>The ONE reachability probe — <see cref="SandboxFixture.TcpProbeAsync"/>, shared with the
    /// egress-failure diagnostic so a fix to one is a fix to both.</summary>
    private static Task<(bool Reached, string Detail)> ProbeAsync(
        SandboxFixture fx, string containerId, string hostPort) => fx.TcpProbeAsync(containerId, hostPort);
}
