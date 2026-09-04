using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Editions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The exit leg of "Stop agents and Mainguard OS on exit" (owner decision 2026-09-04): the window's agent
/// surface ends every live agent through the ordinary Stop, and the seam the shutdown sequence calls is
/// the surface's own method — not a second stop path.
/// </summary>
public sealed class StopAllAgentsOnExitTests
{
    [Fact]
    public async Task StopAllAgents_EndsEveryLiveAgent_AndLeavesTerminalOnesAlone()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        var before = mock.ListAgents();
        Assert.True(vm.LiveAgentCount > 1, "the mock seeds several live agents");

        await ((IAgentPlatformSurface)vm).StopAllAgentsAsync(CancellationToken.None);

        Assert.Equal(0, vm.LiveAgentCount);
        Assert.All(mock.ListAgents(), a => Assert.True(
            a.State is AgentLifecycleState.Rejected or AgentLifecycleState.Merged
                or AgentLifecycleState.Dead or AgentLifecycleState.TornDown,
            $"{a.AgentId} is still {a.State}"));
        Assert.Equal(before.Count, mock.ListAgents().Count); // ended, not erased: branches stay until teardown
    }

    /// <summary>The wiring, not the mechanism: the surface the manifest hands the window is the one the
    /// production exit teardown reaches — a correct <c>StopAllAgentsAsync</c> nobody routes to is the
    /// MG-12 shape.</summary>
    [Fact]
    public async Task TheManifestsSurface_IsWhatTheProductionExitTeardownStops()
    {
        var originalFactory = ProComposition.OrchestratorServicesFactory;
        var originalSurface = ProComposition.LiveAgentSurface;
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(mock);
            using var surface = new ProManifest().CreateControlCenter();
            Assert.Same(surface, ProComposition.LiveAgentSurface);
            Assert.True(surface!.LiveAgentCount > 0);

            var env = new ProductionShutdownEnvironment(() => { }, _ => Task.CompletedTask, _ => { });
            await env.StopAgentsAsync(CancellationToken.None);

            Assert.Equal(0, surface.LiveAgentCount);
        }
        finally
        {
            ProComposition.OrchestratorServicesFactory = originalFactory;
            ProComposition.LiveAgentSurface = originalSurface;
        }
    }

    [Fact]
    public async Task StopAllAgents_HonoursCancellation_BetweenAgents()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IAgentPlatformSurface)vm).StopAllAgentsAsync(cts.Token));
        Assert.True(vm.LiveAgentCount > 0, "an already-cancelled budget stops nothing");
    }
}
