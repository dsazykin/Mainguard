using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Server.Gateway;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The Resource Monitor's whole wire, end to end through the REAL composition root: a container-stats
/// reading enters daemon-side, crosses the real <c>StreamAgentResources</c> RPC, and lands in the shipped
/// <see cref="DaemonBackedOrchestrator"/>'s <c>GetAgentUsage()</c> — the exact projection the Resources
/// tab binds to.
///
/// <para>This is the test the feature never had. The tab shipped complete over a data source nobody wrote:
/// the client hard-coded <c>CpuPercent: 0, RamGb: 0</c>, so every agent displayed a confident 0% forever
/// and every existing test still passed, because they only ever asserted that a formatter formats. So the
/// assertions here are on VALUES that could only have come from the sampler — 37.5% and 1 GiB are
/// arbitrary and unmistakable — rather than on the shape of the projection.</para>
/// </summary>
public sealed class AgentResourceProjectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>A sampler that answers with whatever the test decided, for whichever agents are asked.</summary>
    private sealed class ScriptedSampler : IContainerResourceSampler
    {
        private readonly Func<string, ContainerResourceSample> _answer;

        public ScriptedSampler(Func<string, ContainerResourceSample> answer) => _answer = answer;

        public Task<IReadOnlyList<ContainerResourceSample>> SampleAsync(
            IEnumerable<(string AgentId, string ContainerId)> targets, CancellationToken ct = default)
        {
            IReadOnlyList<ContainerResourceSample> result =
                targets.Select(t => _answer(t.AgentId)).ToArray();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task SampledCpuAndRam_ReachTheClientProjection_AsRealValues()
    {
        const double cpu = 37.5;
        const double oneGib = 1024.0 * 1024.0 * 1024.0;

        using var daemon = new DaemonFixture
        {
            ResourceSampler = new ScriptedSampler(id => new ContainerResourceSample(id, cpu, oneGib, null)),
        };
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it

        // A live session with a sandbox attached — what the daemon samples against.
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        var session = store.Spawn(kind: "claude-code", agentId: "res-agent-1", repoHash: "repo-1");
        store.AttachSandbox(session.Key, "container-abc");

        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var arrived = await WaitUntilAsync(() =>
            adapter.GetAgentUsage().Any(u => u.AgentId == "res-agent-1" && u.CpuPercent is not null));
        Assert.True(arrived, "no CPU reading ever reached the client projection");

        var row = adapter.GetAgentUsage().Single(u => u.AgentId == "res-agent-1");

        // The sampled values themselves — not merely "non-null", and emphatically not 0.
        Assert.Equal(cpu, row.CpuPercent!.Value, 3);
        Assert.Equal(1.0, row.RamGb!.Value, 3); // 1 GiB of bytes, projected as GB

        // The totals line decomposes from the same readings rather than being computed separately.
        Assert.Equal(cpu, adapter.Current.CpuPercent!.Value, 3);
        Assert.Equal(1.0, adapter.Current.RamGb!.Value, 3);
    }

    /// <summary>
    /// A failed sample must arrive as UNKNOWN, not as zero. <c>0%</c> means "this agent is idle";
    /// <c>null</c> means "we could not measure it". The two must remain distinguishable all the way to
    /// the ViewModel, because collapsing them is the false-reassurance bug this feature exists to fix.
    /// </summary>
    [Fact]
    public async Task FailedSample_ReachesTheClientAsUnknown_NotZero()
    {
        using var daemon = new DaemonFixture
        {
            ResourceSampler = new ScriptedSampler(id => ContainerResourceSample.Unavailable(id, "timed out")),
        };
        _ = daemon.Token;

        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        var session = store.Spawn(kind: "claude-code", agentId: "res-agent-2", repoHash: "repo-1");
        store.AttachSandbox(session.Key, "container-def");

        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var seen = await WaitUntilAsync(() =>
            adapter.GetAgentUsage().Any(u => u.AgentId == "res-agent-2"));
        Assert.True(seen, "the agent never appeared in the projection at all");

        var row = adapter.GetAgentUsage().Single(u => u.AgentId == "res-agent-2");
        Assert.Null(row.CpuPercent);
        Assert.Null(row.RamGb);
    }

    /// <summary>
    /// The metering predicate, proven on the real daemon: an agent the gateway issued a confinement token
    /// to reports measurable spend; an agent with no token (an OAuth session, a CLI that declares no
    /// base-URL/model-host pair, or a daemon with the gateway off) reports that its spend is NOT
    /// measurable — which is what makes the client hide the cost UI instead of drawing "$0.00".
    /// </summary>
    [Fact]
    public async Task MeteredFlag_TracksTheGatewayConfinementToken()
    {
        const double oneGib = 1024.0 * 1024.0 * 1024.0;

        using var daemon = new DaemonFixture
        {
            ResourceSampler = new ScriptedSampler(id => new ContainerResourceSample(id, 5.0, oneGib, null)),
        };
        _ = daemon.Token;

        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        var byok = store.Spawn(kind: "claude-code", agentId: "byok-agent", repoHash: "repo-1");
        store.AttachSandbox(byok.Key, "container-byok");
        var oauth = store.Spawn(kind: "claude-code", agentId: "oauth-agent", repoHash: "repo-1");
        store.AttachSandbox(oauth.Key, "container-oauth");

        // Exactly what SandboxAgentLauncher does when it confines a spawn — and deliberately NOT done
        // for the OAuth agent, which has no key to withhold.
        daemon.Services.GetRequiredService<AgentGatewayCredentials>()
            .Issue("byok-agent", "sk-test-provider-key", "api.anthropic.com");

        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var arrived = await WaitUntilAsync(() =>
            adapter.GetAgentUsage().Any(u => u.AgentId == "byok-agent" && u.CpuPercent is not null)
            && adapter.GetAgentUsage().Any(u => u.AgentId == "oauth-agent" && u.CpuPercent is not null));
        Assert.True(arrived, "readings never reached the client projection");

        var usage = adapter.GetAgentUsage();
        Assert.True(usage.Single(u => u.AgentId == "byok-agent").IsMetered,
            "a gateway-confined agent must report measurable spend");
        Assert.False(usage.Single(u => u.AgentId == "oauth-agent").IsMetered,
            "an agent with no confinement token must NOT report measurable spend");

        // And the unmeasurable one carries no spend figure at all — not a zero.
        Assert.Null(usage.Single(u => u.AgentId == "oauth-agent").SpendUsd);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }
}
