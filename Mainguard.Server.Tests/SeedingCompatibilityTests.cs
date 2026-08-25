using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The queue-seeding COMPATIBILITY CONTRACT (docs/design/queue-seeding.md §9): the properties the
/// dev-only seeder relies on, pinned so that a change which invalidates one — above all, merging the
/// coordinator phase-2/3 branches — fails HERE, loudly, instead of letting the seeder rot into a tool
/// that silently stops covering the flows the queue grows. A seeding tool that fakes its coverage is
/// worse than no tool; these tests are what keeps that from happening quietly.
/// </summary>
public sealed class SeedingCompatibilityTests
{
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    // ---- (a) The gate defaults seeding depends on, pinned in BOTH directions ---------------------

    /// <summary>
    /// `ChangedTestCommandGate` passes an id it never saw — the RT-D2 gate arms per-branch at
    /// verification time, and an unarmed id is unflagged, not blocked.
    /// </summary>
    [Fact]
    public void ChangedTestCommandGate_PassesAnIdItNeverSaw()
    {
        var gate = new ChangedTestCommandGate();
        Assert.True(gate.Allows("seed-unknown", out var reason), reason);
    }

    /// <summary>
    /// `FlaggedChangeGate` DENIES an id it never saw (MG-40 default-DENY). This is the direction that
    /// makes the pair load-bearing for seeding: a seeded entry becomes mergeable ONLY because the
    /// provisioner's seeded arm still runs the real flagged-change review (an empty classified set is
    /// AllAcknowledged) — i.e. seeding DEPENDS on the mirror-read half executing, and this pin is what
    /// turns removing that half into a loud failure instead of a quiet fake.
    /// </summary>
    [Fact]
    public void FlaggedChangeGate_DeniesAnIdItNeverSaw()
    {
        var gate = new FlaggedChangeGate(new Mainguard.Git.Audit.InMemoryAuditLog());
        Assert.False(gate.Allows("seed-unknown", out var reason));
        Assert.NotEqual("", reason);
    }

    // ---- (b) The coordinator plan gate, in both of its two directions ----------------------------
    //
    // These four replace the tripwire this file carried until the phase-2/3 merge
    // (NoAutomaticVerificationCallerExistsYet_TripwireForTheCoordinatorBranches), which asserted that
    // no automatic caller of MergeQueue.RunVerificationAsync existed yet and named — for whoever did the
    // merge — the three things to do before removing it. All three are done: the seeder gained real plan
    // seeding (SeedSpec.WithPlan/Scope over the proto's formerly-reserved fields 8/9, exercised by
    // QueueSeederTests), and the two properties the tripwire stood in for are pinned DIRECTLY below.

    /// <summary>
    /// <b>Direction 1 — the merge gate lets an unheld id through.</b> <see cref="WorkerPlanGate"/> is a
    /// third <see cref="IMergeGate"/> ANDed into every queue, and a seeded entry (like a manual-mode agent
    /// or an external-PR head) is not a coordinator-delegated worker. If this gate answered "no" for ids it
    /// never held, every seeded entry — and every non-coordinated branch in the product — would become
    /// unmergeable, silently, at merge time.
    /// </summary>
    [Fact]
    public void WorkerPlanGate_StaysPermissive_ForAnIdItNeverHeld()
    {
        var gate = new WorkerPlanGate(new Mainguard.Agents.Agents.Orchestrator.PlanApprovalService());

        Assert.True(gate.Allows("seed-unknown", out var reason), reason);
        Assert.Equal("", reason);
    }

    /// <summary>
    /// <b>Direction 2 — the automatic trigger refuses the same id.</b> <c>MayAutoVerify</c> is deliberately
    /// NOT <c>Allows</c>: an automatic trigger reading the permissive merge default would start spending
    /// test-suite runs on every agent in the daemon. A seeded entry has no jail and no agent, so an
    /// automatic verification fired at one would be a refusal-shaped failure nobody asked for.
    /// </summary>
    [Fact]
    public void TheAutoVerifyPredicate_RefusesAnIdThePlanGateNeverHeld()
    {
        var gate = new WorkerPlanGate(new Mainguard.Agents.Agents.Orchestrator.PlanApprovalService());

        Assert.False(gate.MayAutoVerify("seed-unknown", out var reason));
        Assert.Contains("not a plan-gated worker", reason);
    }

    /// <summary>
    /// The same two facts as the queue and the trigger themselves see them, rather than as the gate reports
    /// them — because "the predicate says no" and "the trigger therefore does nothing" are different
    /// claims, and only the second one is the guarantee. A seeded id armed on a real
    /// <see cref="WorkerReadinessTrigger"/> is dropped <see cref="ReadinessOutcome.Ineligible"/>, and the
    /// queue's verification runner (which throws here) is never entered.
    /// </summary>
    [Fact]
    public void ASeededId_ArmedOnTheRealTrigger_IsDroppedIneligible_AndNothingRuns()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mainguard-seedcompat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var registry = new MergeQueueRegistry();
            registry.Register("repo", new MergeQueueContext(
                new MergeQueue(
                    repoHash: "repo", currentMainSha: "abc",
                    store: new InMemoryMergeQueueStore(),
                    verifications: new InMemoryVerificationStore(),
                    runVerification: (id, _) => throw new InvalidOperationException(
                        $"the automatic trigger started a verification for '{id}' — a seeded entry has no "
                        + "agent and no jail, so this can only be a fabricated automatic caller")),
                new InMemoryMergeLeaseStore()));

            using var watcher = new AgentRefWatcher(
                new AgentRefMediator(new AgentRepoManager(temp), _ => temp),
                new AgentRepoManager(temp),
                AgentRefWatcher.DriveManually);
            // A virtual clock, so the quiet period is elapsed by advancing it rather than by sleeping.
            var now = DateTimeOffset.UnixEpoch;
            using var trigger = new WorkerReadinessTrigger(
                source: watcher,
                queues: registry,
                planGate: new WorkerPlanGate(new Mainguard.Agents.Agents.Orchestrator.PlanApprovalService()),
                sweepInterval: WorkerReadinessTrigger.DriveManually,
                clock: () => now);

            trigger.NotifyAdvanced("repo", "seed-abcd1234", "sha1");
            now = now.AddHours(1);
            var decision = Assert.Single(trigger.PollOnce());

            Assert.Equal(ReadinessOutcome.Ineligible, decision.Outcome);
            Assert.Contains("not a plan-gated worker", decision.Reason);
            Assert.Empty(trigger.Armed);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// ...and the structural half, which is what makes the pin above hold for the <c>with_plan</c> seeds
    /// too. A plan-seeded id IS a plan-gated worker by construction, so <c>MayAutoVerify</c> answers TRUE
    /// for it — the guarantee there cannot come from the predicate, and comes instead from the trigger
    /// never being armed: it arms only from <see cref="AgentRefWatcher.Advanced"/>, which reads registered
    /// agents' OWN repositories, never the bare mirror the seeder writes its refs into. This asserts the
    /// seeder cannot reach around that: it holds no handle on the trigger or on the ref machinery, so no
    /// seeding call can start an automatic verification whatever the predicate says.
    /// </summary>
    [Fact]
    public void TheSeeder_HoldsNoHandleOnTheAutomaticVerificationMachinery()
    {
        var forbidden = new[] { typeof(WorkerReadinessTrigger), typeof(AgentRefWatcher), typeof(AgentRefMediator) };

        var dependencies = typeof(QueueSeeder).GetConstructors()
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
            .Concat(typeof(QueueSeeder)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType))
            .ToList();

        Assert.DoesNotContain(dependencies, d => forbidden.Contains(d));
    }

    // ---- (c) Entry creation stays where the seeder assumes it is ---------------------------------

    /// <summary>
    /// The lifecycle verbs refuse ids the queue does not track — so nothing the seeder does can act on
    /// an entry it did not first create through `EnsureEntry`, and a typo'd clear cannot manufacture a
    /// terminal row for a branch that never existed.
    /// </summary>
    [Fact]
    public void LifecycleVerbs_RefuseAnUnknownId()
    {
        var queue = new MergeQueue(
            repoHash: "repo", currentMainSha: "abc",
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (_, _) => throw new InvalidOperationException("no verification in this test"));

        Assert.False(queue.TryDiscard("seed-ghost", "test", "", out var discardRefusal));
        Assert.Contains("not in the merge queue", discardRefusal);
        Assert.False(queue.TryReject("seed-ghost", "test", "", out var rejectRefusal));
        Assert.Contains("not in the merge queue", rejectRefusal);
    }

    // ---- (d) The gate itself, end to end over the wire -------------------------------------------

    /// <summary>The fixture seam itself: the configuration key must reach the boot capture.</summary>
    [Fact]
    public void TheBootFlagFixture_RegistersEnabledOptions()
    {
        using var fixture = new DaemonFixture { EnableQueueSeeding = true };
        Assert.True(fixture.Services.GetRequiredService<QueueSeedingOptions>().Enabled);
    }

    /// <summary>
    /// A daemon WITHOUT the boot flag does not have the service at all — UNIMPLEMENTED, not a refusal.
    /// This is the primary gate and the dev panel's hide probe, and it is what a shipped build always
    /// answers.
    /// </summary>
    [Fact]
    public async Task WithoutTheBootFlag_TheSeedingServiceIsUnimplemented()
    {
        using var fixture = new DaemonFixture();
        var client = new QueueSeedingService.QueueSeedingServiceClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetSeedingStatusAsync(new GetSeedingStatusRequest(), fixture.AuthHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    /// <summary>
    /// With the flag: the status probe answers (the panel's show signal) — and a COORDINATOR token is
    /// still denied every seeding method at the role layer. The boot flag decides whether the operator
    /// gets the surface, never whether an agent does.
    /// </summary>
    [Fact]
    public async Task WithTheBootFlag_StatusAnswers_AndACoordinatorTokenIsStillDenied()
    {
        using var fixture = new DaemonFixture { EnableQueueSeeding = true };
        var client = new QueueSeedingService.QueueSeedingServiceClient(fixture.CreateChannel());

        var status = await client.GetSeedingStatusAsync(
            new GetSeedingStatusRequest(), fixture.AuthHeaders()).ResponseAsync;
        Assert.True(status.Enabled);

        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);
        var denied = await Assert.ThrowsAsync<RpcException>(() =>
            client.SeedQueueEntriesAsync(
                new SeedQueueEntriesRequest
                {
                    RepoHandle = "any",
                    Entries = { new SeedEntrySpec { TargetState = "Working" } },
                },
                fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var statusDenied = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetSeedingStatusAsync(
                new GetSeedingStatusRequest(), fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, statusDenied.StatusCode);
    }

    /// <summary>
    /// The belt behind the primary: even if a refactor mapped the service unconditionally,
    /// `SeedingGateInterceptor` refuses the prefix on a flagless daemon. Driven directly (the mapped
    /// daemon cannot demonstrate the belt — the primary answers first with UNIMPLEMENTED).
    /// </summary>
    [Fact]
    public async Task TheSeedingBelt_RefusesThePrefix_WhenDisabled()
    {
        var interceptor = new SeedingGateInterceptor(new QueueSeedingOptions(Enabled: false));
        var context = new MethodOnlyServerCallContext(SeedingGateInterceptor.MethodPrefix + "SeedQueueEntries");

        var ex = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<object, object>(
            new object(), context, (_, _) => Task.FromResult(new object())));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        // ...and it is a PREFIX gate: every other service flows through untouched on the same daemon.
        var other = new MethodOnlyServerCallContext("/mainguard.v1.MergeQueueService/CanMerge");
        var result = await interceptor.UnaryServerHandler<object, object>(
            new object(), other, (_, _) => Task.FromResult(new object()));
        Assert.NotNull(result);
    }

    /// <summary>The one fact the belt reads is <see cref="ServerCallContext.Method"/>; everything else
    /// is inert scaffolding this subclass supplies without a gRPC server.</summary>
    private sealed class MethodOnlyServerCallContext : ServerCallContext
    {
        private readonly string _method;

        public MethodOnlyServerCallContext(string method) => _method = method;

        protected override string MethodCore => _method;
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = new();
        protected override System.Threading.CancellationToken CancellationTokenCore => default;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } =
            new("", new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }
}
