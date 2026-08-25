using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// ISSUES-LOG #19: the Coordinator panel read "No coordinator running" while the daemon's own
/// <c>ListAgents</c> answered <c>role=coordinator</c> for that repo, once a minute, for 20+ minutes — to
/// this same client, on a call whose answer was thrown away except for the agent ids.
///
/// <para><b>The mechanism.</b> The panel is derived from the client's agent projection, which is fed
/// ONLY by <c>StreamAgentEvents</c>: one destructive snapshot at subscribe time, then deltas. A delta
/// carries neither kind nor role, so a delta for an agent the projection has not seen fabricates a
/// <b>role-less</b> placeholder — and the single <c>ListAgents</c> call meant to repair it gave up
/// permanently on one failure. The Coordinator panel filters on <c>Role == "coordinator"</c>, so a live
/// coordinator stranded as a role-less record is invisible to it forever, while the worker rail happily
/// shows the same agent and the daemon keeps answering correctly. Nothing retried, ever.</para>
///
/// <para>These tests assert the user-visible consequence — does the projection the panel filters
/// eventually carry a coordinator — rather than a private field.</para>
/// </summary>
public sealed class CoordinatorProjectionRepairTests
{
    private const string CoordinatorId = "f1574a0ba9b443ffa2a5b2f9345df622";

    // Never contacted: the overrides replace both the stream and the listing, and DaemonClient does no
    // I/O at construction (its channel factory is lazy).
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.AgentInfo Listed(string id, string kind, string state, string role) =>
        new() { AgentId = id, AgentKind = kind, State = state, Role = role };

    /// <summary>The daemon's real answer throughout the #19 outage, verbatim from <c>rpc.log</c>.</summary>
    private static IReadOnlyList<Proto.AgentInfo> TheLiveCoordinator() =>
        new[] { Listed(CoordinatorId, "claude-code", "Paused", AgentRoles.Coordinator) };

    /// <summary>A state delta — all the daemon sends after the snapshot, and it carries no role.</summary>
    private static Proto.AgentEvent StateDelta(string agentId, string state) =>
        new() { AgentId = agentId, State = new Proto.StateChange { State = state, Reason = "" } };

    private static Proto.AgentEvent EmptySnapshot() =>
        new() { AgentId = string.Empty, Snapshot = new Proto.AgentSnapshot() };

    private static async Task<bool> WaitForCoordinatorAsync(DaemonBackedOrchestrator adapter)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.ListAgents().Any(a => a.Role == AgentRoles.Coordinator))
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// The defect itself. A state delta arrives for a coordinator the projection has never seen — the
    /// exact shape of a client that connected before the coordinator spawned, or that missed the spawn
    /// across a reconnect. The fabricated placeholder must not be the last word: the authoritative listing
    /// puts the role back, and the Coordinator panel's filter finds it again.
    /// </summary>
    [Fact]
    public async Task ARoleLessPlaceholder_IsRepairedFromTheAuthoritativeListing()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.AgentListOverride = _ => Task.FromResult(TheLiveCoordinator());

        // The delta path: an agent the projection has never seen. Before the fix this left a record with
        // Role == "" and nothing ever revisited it.
        adapter.ApplyAgentEvent(StateDelta(CoordinatorId, "Paused"));

        Assert.True(
            await WaitForCoordinatorAsync(adapter),
            "the projection never recovered the coordinator's role, so the Coordinator panel reads "
            + "'No coordinator running' for the rest of the session while the daemon keeps reporting "
            + "role=coordinator for that agent (ISSUES-LOG #19)");

        var coordinator = adapter.ListAgents().Single(a => a.Role == AgentRoles.Coordinator);
        Assert.Equal(CoordinatorId, coordinator.AgentId);
        Assert.Equal("claude-code", coordinator.Name);
        Assert.Equal(CoordinatorId, adapter.CoordinatorAgentId);
    }

    /// <summary>
    /// A snapshot is not a second opinion — it is a stringly-typed <c>id:kind:state:role</c> payload the
    /// daemon splits on <c>,</c> and <c>:</c>, and it is the only thing that ever replaces this projection
    /// wholesale. When it disagrees with <c>ListAgents</c> the RPC wins (and is newer besides), so an
    /// empty or lossy snapshot can no longer strand the panel.
    /// </summary>
    [Fact]
    public async Task AnEmptySnapshot_IsConfirmedAgainstTheListingRatherThanTrustedBlindly()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.AgentListOverride = _ => Task.FromResult(TheLiveCoordinator());

        adapter.ApplyAgentEvent(EmptySnapshot());

        Assert.True(
            await WaitForCoordinatorAsync(adapter),
            "an empty snapshot was taken as the truth, so a client that reconnected while the daemon "
            + "had not yet re-registered its running jails never learned about the live coordinator");
    }

    /// <summary>
    /// The listing is authoritative for identity and <b>only</b> for identity. State and the live detail
    /// flow on deltas, which are newer than any poll, so a listing taken a moment ago must not walk a
    /// just-Dead agent back to alive and wipe the exit tail the surface is showing.
    /// </summary>
    [Fact]
    public async Task TheListingRepairsRoleAndKind_ButNeverRewindsStateOrDetail()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.AgentListOverride = _ => Task.FromResult(TheLiveCoordinator());

        // The daemon told us the coordinator died, with why. The listing below still says "Paused".
        adapter.ApplyAgentEvent(new Proto.AgentEvent
        {
            AgentId = CoordinatorId,
            State = new Proto.StateChange { State = "Dead", Reason = "claude exited: code 1" },
        });

        Assert.True(await WaitForCoordinatorAsync(adapter));

        var coordinator = adapter.ListAgents().Single(a => a.Role == AgentRoles.Coordinator);
        Assert.Equal(AgentLifecycleState.Dead, coordinator.State);
        Assert.Equal("claude exited: code 1", coordinator.Detail);
    }

    /// <summary>
    /// The repair is a standing pass, not a one-shot. The login-harvest sweep already calls
    /// <c>ListAgents</c> every minute and used to keep only the ids; feeding the projection from it means
    /// a stranded coordinator is a coordinator again within the sweep interval, with no extra RPC — even
    /// when no further agent event ever arrives to trigger anything.
    /// </summary>
    [Fact]
    public async Task ThePeriodicLoginSweep_RepairsTheProjection_WithNoAgentEventAtAll()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.AgentListOverride = _ => Task.FromResult(TheLiveCoordinator());

        Assert.Empty(adapter.ListAgents());

        // The sweep's harvest half will fail against the uncontacted client; that is deliberate and must
        // not stop the repair, which happens before any per-agent call.
        await adapter.PersistLiveAgentLoginsAsync(CancellationToken.None);

        Assert.Equal(
            CoordinatorId,
            adapter.ListAgents().Single(a => a.Role == AgentRoles.Coordinator).AgentId);
    }

    /// <summary>A merge never deletes: a listing that raced a spawn cannot make a live agent vanish from
    /// the rail. Removal stays the snapshot's (and a stop delta's) job.</summary>
    [Fact]
    public async Task TheListingNeverRemovesAnAgentItDoesNotMention()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.AgentListOverride = _ => Task.FromResult(TheLiveCoordinator());

        adapter.ApplyAgentEvent(StateDelta("just-spawned-worker", "Working"));
        Assert.True(await WaitForCoordinatorAsync(adapter));

        Assert.Contains(adapter.ListAgents(), a => a.AgentId == "just-spawned-worker");
    }
}
