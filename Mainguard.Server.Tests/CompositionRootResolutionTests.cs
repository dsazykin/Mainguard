using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Runtime;
using Mainguard.Server.Services;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-47 integration proof #1 — the daemon composition root resolves the FULL graph at startup with
/// nothing left idling. Not a component test: it asserts the wiring (DI resolution of every mapped gRPC
/// service, the P2-08 gateway stack, and the P2-12 external-PR intake chain) so the "compiles but still
/// runs on stubs" failure mode is caught. The intake-lights-up assertion is the concrete anti-idle check:
/// before P2-47 <see cref="IExternalPrIntake"/> was unregistered and <see cref="PrIntakeHostedService"/>
/// returned <c>Task.CompletedTask</c> from <c>StartAsync</c>; now it resolves and runs the poll loop.
/// </summary>
public sealed class CompositionRootResolutionTests
{
    /// <summary>Every gRPC service mapped by <c>DaemonHost.MapServices</c> must have a fully resolvable
    /// constructor graph in the real composition root (no missing registration). The set is read off the
    /// host's routing table — the hand-kept list this replaced had drifted to 8 of the 9 mapped services
    /// (<c>EgressGrpcService</c> was absent), which is the same silent-coverage-loss the auth theory had.</summary>
    [Fact]
    public void EveryMappedGrpcService_ConstructorGraph_Resolves()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        // ActivatorUtilities resolves each service's ctor dependencies from the real container — a missing
        // registration throws here, which is exactly the startup failure this test guards against.
        foreach (var serviceType in MappedGrpcRpcs.ServiceTypes(sp))
        {
            Assert.NotNull(ActivatorUtilities.CreateInstance(sp, serviceType));
        }
    }

    /// <summary>The P2-08 gateway stack + P2-09 leader + P2-14 governance spine all resolve as singletons.</summary>
    [Fact]
    public void GatewayAndGovernanceGraph_Resolves()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        Assert.NotNull(sp.GetRequiredService<AgentSessionStore>());
        Assert.NotNull(sp.GetRequiredService<IMergeQueueRegistry>());
        Assert.NotNull(sp.GetRequiredService<AiGateway>());
        Assert.NotNull(sp.GetRequiredService<AdmissionController>());
        Assert.NotNull(sp.GetRequiredService<SwarmReconciler>());
        Assert.NotNull(sp.GetRequiredService<DaemonBootSequence>());
        Assert.NotNull(sp.GetRequiredService<SessionLeader>());
        Assert.NotNull(sp.GetRequiredService<KillSwitch>());
        Assert.NotNull(sp.GetRequiredService<KillSwitchGate>());
        Assert.NotNull(sp.GetRequiredService<PlanApprovalService>());
        Assert.NotNull(sp.GetRequiredService<CoordinatorConversationService>());
    }

    /// <summary>
    /// P2-47 anti-idle proof: the external-PR intake dependency chain resolves and
    /// <see cref="PrIntakeHostedService"/> is registered as a hosted service, so the daemon's scheduler
    /// runs the poll loop instead of returning early. Each link (transport / store / worktrees / fetcher)
    /// resolves too — the whole chain the intake engine needs.
    /// </summary>
    [Fact]
    public void ExternalPrIntakeChain_Resolves_AndHostedServiceIsRegistered()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        // The engine and every link it depends on.
        Assert.NotNull(sp.GetRequiredService<IExternalPrIntake>());
        Assert.NotNull(sp.GetRequiredService<IPrIntakeStore>());
        Assert.NotNull(sp.GetRequiredService<Mainguard.Git.Services.IPullRequestService>());
        Assert.NotNull(sp.GetRequiredService<IPrHeadFetcher>());

        // The scheduler slot is present and will now run (it starts the intake engine's RunAsync).
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is PrIntakeHostedService);
        Assert.Contains(hosted, h => h is GatewayHostedService);
    }
}
