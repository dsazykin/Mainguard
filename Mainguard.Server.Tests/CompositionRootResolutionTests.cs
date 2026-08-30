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
    /// The optional constructor tail of <see cref="MergeQueueProvisioner"/> is wired EXACTLY as the daemon
    /// intends — every argument, not just the one that happened to get a test.
    ///
    /// <para>Each of those arguments defaults to something that silently substitutes a weaker behaviour, so
    /// deleting its line from <c>GatewayServiceRegistration</c> leaves the product changed and the suite
    /// green. That was measured: removing <c>audit</c>, <c>log</c> and <c>publishAgentRef</c> together left
    /// 504 tests passing. Only <c>checkAgentBranch</c> had a guard, which is why it is the only one that
    /// could not be deleted quietly — and one guard per control is exactly the pattern that produced four
    /// unguarded controls. So this asserts the whole tail as a set, once.</para>
    ///
    /// <para>The comparison is EXACT in both directions, so a new optional argument fails here until
    /// someone states whether the daemon passes it. What each name buys is documented on
    /// <see cref="MergeQueueProvisioner.WiredOptionalControls"/>.</para>
    ///
    /// <para><c>resolveApprovedPlan</c> is in the set as of the coordinator phase-2 merge, and it was pinned
    /// ABSENT before it for a stated reason: the daemon had no agent→approved-plan binding, so any lambda
    /// here would have compared diffs against a GUESSED scope and reported that as enforcement.
    /// Worker-authored plans are keyed by the worker's own agent id — the same id the plan gate holds and
    /// the merge queue tracks the branch under — so the binding is now exact rather than inferred, and the
    /// SA-1/F6 out-of-approved-scope arm is live. It is read through <c>Approved</c> only; an agent with no
    /// approved plan resolves null and skips the scope comparison exactly as before.</para>
    /// </summary>
    [Fact]
    public void MergeQueueProvisioner_OptionalControlTail_IsWiredExactly()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        var provisioner = sp.GetRequiredService<MergeQueueProvisioner>();

        Assert.Equal(
            new[]
            {
                // `syntheticVerifications` is wired UNCONDITIONALLY and empty in production — the
                // dev-only queue-seeding seam (docs/design/queue-seeding.md §3). Its only writer is
                // the flag-gated QueueSeedingService, which a shipped daemon never maps, so the gate
                // lives at the RPC surface rather than in a conditional wiring this exact-set
                // assertion could not tell from an oversight.
                "agentStates", "audit", "checkAgentBranch", "locateAgentWorktree", "log",
                // Without `promptAgent`, the "let the agent resolve" control on a conflicted entry
                // refuses — correctly and loudly, rather than unpausing a jail and telling it nothing —
                // which would leave the card exactly where it started: naming a human action the product
                // cannot perform. Precisely the silent degradation this assertion exists to catch.
                "promptAgent",
                "publishAgentRef", "publishRebasedAgentRef", "resolveApprovedPlan",
                "syntheticVerifications", "yieldProtocolFor",
            },
            provisioner.WiredOptionalControls.OrderBy(n => n, System.StringComparer.Ordinal).ToArray());

        // The four newest names compose ONE capability, and the set above cannot say whether they compose
        // it correctly — three of four present is the re-verify-only cascade with extra arguments. This is
        // the capability, asserted directly: does this daemon's stale cascade REPARENT a branch, or only
        // re-run its tests against a main it no longer descends from? The second is the defect that let
        // exactly one agent per repository ever merge.
        Assert.True(provisioner.ReparentsStaleBranches);

        // ...and `audit` must be the DAEMON's sink. A non-null audit log that is not this one is the null
        // default's behaviour with an extra step: the queue's events go somewhere nothing can reach.
        Assert.Same(sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(), provisioner.AuditLog);
    }

    /// <summary>
    /// The same assertion for the daemon's <see cref="KillSwitch"/>, which had none at all.
    ///
    /// <para>Production passed only <c>gate</c>, <c>target</c> and <c>audit</c>, and deleting even
    /// <c>audit:</c> left the whole Kill-filtered suite green (11 passed) — so two of the remaining
    /// defaults were live in the shipped daemon rather than hypothetical. <c>journal</c> defaulted to an
    /// <c>InMemoryKillJournal</c> nothing held a reference to, which makes step 3's "snapshot written
    /// BEFORE returning" write nowhere that survives the restart an emergency stop is followed by; and
    /// <c>rttBudget</c> defaulted to a bare <c>() =&gt; TimeSpan.Zero</c>, which is indistinguishable from a
    /// measured-healthy channel.</para>
    /// </summary>
    [Fact]
    public void KillSwitch_OptionalControlTail_IsWiredExactly_AndItsJournalIsDurable()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        var kill = sp.GetRequiredService<KillSwitch>();

        Assert.Equal(
            new[] { "audit", "journal", "onRttSpike", "rttBudget" },
            kill.WiredOptionalControls.OrderBy(n => n, System.StringComparer.Ordinal).ToArray());

        // The journal must be the durable, registered one — a snapshot in the killed process's heap is
        // gone exactly when someone comes to read it.
        Assert.IsType<JsonKillJournal>(kill.Journal);
        Assert.Same(sp.GetRequiredService<IKillJournal>(), kill.Journal);

        // ...and the audit events must land in the daemon's sink, not a throwaway.
        Assert.Same(sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(), kill.AuditLog);

        // The A3 feed has somewhere to go the moment a measurement exists.
        Assert.True(kill.ReportsRttSpike);
    }

    /// <summary>
    /// The daemon's RT-D4 posture, pinned as a decision rather than left as a silent default.
    ///
    /// <para>There is no control-channel RTT to measure: P2-09's <c>IAgentControlChannel</c> has no
    /// production transport and <c>SandboxKillTarget.RequestYieldAsync</c> answers <c>false</c> without a
    /// round trip. The old default said that with <c>() =&gt; TimeSpan.Zero</c>, which a
    /// <see cref="KillReport"/> cannot tell apart from a measured-healthy channel; the sentinel says it
    /// out loud and stamps <c>RttMeasured: false</c> on the report and the journal snapshot.</para>
    ///
    /// <para><b>This test is expected to fail the day a real transport lands</b> — at which point the
    /// person wiring the EWMA flips it deliberately, which is the whole point of pinning it.</para>
    /// </summary>
    [Fact]
    public void KillSwitch_ControlChannelRtt_IsExplicitlyUnmeasured_NotSilentlyZero()
    {
        using var host = new DaemonFixture();

        var kill = host.Services.GetRequiredService<KillSwitch>();

        Assert.False(
            kill.MeasuresControlChannelRtt,
            "the daemon has no control-channel RTT source; if one was just wired, update this assertion "
            + "and KillSwitchTiming.UnmeasuredRtt's remarks rather than leaving the posture undeclared");
    }

    /// <summary>
    /// Both boot reconcile steps are built with an audit sink, so a pass that destroys something leaves an
    /// artifact.
    ///
    /// <para>Boot reconcile is the one pass that can declare an agent Dead and force-remove its worktree,
    /// kill its PTY and drop it from the durable leader registry. Both steps discarded their reports
    /// entirely, and the sinks that fixed that are optional constructor arguments — the same silent-delete
    /// exposure as the merge-queue and kill-switch tails, so it is pinned the same way.</para>
    /// </summary>
    [Fact]
    public void BootReconcileSteps_AreWiredToRecordWhatTheyDestroy()
    {
        using var host = new DaemonFixture();

        var boot = host.Services.GetRequiredService<DaemonBootSequence>();

        var swarm = Assert.IsType<SwarmReconcileTask>(
            boot.Tasks.Single(t => t.Name == "swarm-reconcile"));
        Assert.True(swarm.RecordsOutcome, "a boot pass that prunes agents must leave an audit record");

        var reattach = Assert.IsType<LeaderReattachTask>(
            boot.Tasks.Single(t => t.Name == "leader-reattach"));
        Assert.True(reattach.RecordsOutcome, "a boot pass that reaps PTY sessions must leave an audit record");
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

        // The two links that were missing: something that can give an intake'd pull request a jail, and
        // something that can say which repository a subscribed source belongs to.
        Assert.NotNull(sp.GetRequiredService<IPrWorkerHost>());
        Assert.NotNull(sp.GetRequiredService<PrIntakeTargetResolver>());
        Assert.NotNull(sp.GetRequiredService<ActiveRepoIndex>());

        // The scheduler slot is present and will now run (it starts the intake engine's RunAsync).
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is PrIntakeHostedService);
        Assert.Contains(hosted, h => h is GatewayHostedService);
    }

    /// <summary>
    /// The intake's per-source target resolver was the literal <c>_ =&gt; null</c>, which makes every poll
    /// list-and-skip: the poll loop ran, subscriptions persisted, and the intake materialized nothing in
    /// production, ever. A hardwired constant is invisible from the outside — the engine resolves, the
    /// hosted service starts, and every anti-idle assertion above still passes — so it is pinned here at
    /// the only place it is observable: the delegate the registered engine actually holds.
    /// </summary>
    [Fact]
    public void ExternalPrIntake_TargetResolver_IsTheRealResolver_NotAHardwiredNull()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        var intake = Assert.IsType<ExternalPrIntake>(sp.GetRequiredService<IExternalPrIntake>());
        var resolve = (System.Delegate)typeof(ExternalPrIntake)
            .GetField("_resolveTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(intake)!;

        Assert.Same(sp.GetRequiredService<PrIntakeTargetResolver>(), resolve.Target);
        Assert.Equal(nameof(PrIntakeTargetResolver.Resolve), resolve.Method.Name);
    }

    /// <summary>
    /// …and the same for the spawn seam: the engine must hold the DAEMON's worker host, because the whole
    /// defect was an intake that materialized entries and spawned nothing.
    /// </summary>
    [Fact]
    public void ExternalPrIntake_HoldsTheDaemonsWorkerHost()
    {
        using var host = new DaemonFixture();
        var sp = host.Services;

        var intake = sp.GetRequiredService<IExternalPrIntake>();
        var workers = typeof(ExternalPrIntake)
            .GetField("_workers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(intake);

        Assert.IsType<ExternalPrWorkerHost>(workers);
        Assert.Same(sp.GetRequiredService<IPrWorkerHost>(), workers);
    }
}
