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

    /// <summary>
    /// Can this container open a TCP connection to <paramref name="hostPort"/>? Uses curl with the
    /// proxy explicitly disabled (<c>--noproxy '*'</c>): the jail's <c>HTTP_PROXY</c> would otherwise
    /// route the probe through the egress proxy and answer a question nobody asked. A three-digit HTTP
    /// code means the peer answered; anything else (curl's <c>000</c>, a non-zero exit) means it did
    /// not — which for a DROPped destination is a timeout, and for an isolated segment an immediate
    /// "network unreachable".
    /// </summary>
    private static async Task<(bool Reached, string Detail)> ProbeAsync(
        SandboxFixture fx, string containerId, string hostPort)
    {
        var result = await fx.ExecAsync(containerId,
            "curl", "-sS", "--noproxy", "*", "-m", "5", "-o", "/dev/null", "-w", "%{http_code}",
            "http://" + hostPort);

        var code = result.Stdout.Trim();
        var reached = result.ExitCode == 0
            && code.Length == 3
            && code != "000"
            && int.TryParse(code, out _);

        return (reached, $"probe {hostPort}: exit={result.ExitCode} code='{code}' stderr='{result.Stderr.Trim()}'");
    }
}
