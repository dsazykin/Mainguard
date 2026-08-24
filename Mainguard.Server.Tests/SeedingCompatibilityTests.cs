using System;
using System.Linq;
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

    // ---- (b) No automatic verification fires for seeded ids --------------------------------------

    /// <summary>
    /// TODAY no automatic caller of <c>MergeQueue.RunVerificationAsync</c> exists at all, and this
    /// tripwire is how the seeder finds out the moment one lands. The coordinator phase-2/3 branches
    /// (`feat/coordinator-phase-2-worker-authored-plans` / `feat/coordinator-phase-3-role-lock`) add
    /// exactly the two types probed here.
    ///
    /// <para><b>To whoever merges those branches — this failure is addressed to you, and it is not a
    /// nuisance pin.</b> Before deleting or re-pinning it: (1) assert `WorkerPlanGate.Allows` stays
    /// permissive for ids it never held AND `MayAutoVerify` refuses them — seeded ids must pass the
    /// new merge gate and stay invisible to `WorkerReadinessTrigger`; (2) extend `QueueSeeder` with
    /// real plan seeding (`WorkerPlanGate.Hold` → `PlanApprovalService.Present` → approve, for a
    /// synthetic id), filling `SeedEntrySpec`'s reserved proto fields 8/9 (`with_plan`, `scope`) —
    /// without that, plan-gated verification and the scope arm of the flagged review exist in the
    /// product and cannot be seeded, which is precisely the silent-coverage rot this contract
    /// exists to prevent; (3) replace this tripwire with direct pins on both properties.</para>
    /// </summary>
    [Fact]
    public void NoAutomaticVerificationCallerExistsYet_TripwireForTheCoordinatorBranches()
    {
        var agentsAssembly = typeof(MergeQueue).Assembly;
        var arrivals = agentsAssembly.GetTypes()
            .Where(t => t.Name.Contains("ReadinessTrigger", StringComparison.Ordinal)
                || t.Name.Contains("WorkerPlanGate", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(arrivals.Count == 0,
            "The coordinator plan-gate/auto-verify machinery has arrived: " + string.Join(", ", arrivals)
            + ". Read this test's doc comment — the queue seeder must be extended (plan seeding via the"
            + " reserved SeedEntrySpec fields 8/9) and these properties re-pinned directly before this"
            + " tripwire is removed. Deleting it without doing that turns the seeder into a tool that"
            + " silently no longer covers what the queue does.");
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
    public void TheSeedingBelt_RefusesThePrefix_WhenDisabled()
    {
        var interceptor = new SeedingGateInterceptor(new QueueSeedingOptions(Enabled: false));
        var context = new MethodOnlyServerCallContext(SeedingGateInterceptor.MethodPrefix + "SeedQueueEntries");

        var ex = Assert.Throws<RpcException>(() => interceptor.UnaryServerHandler<object, object>(
            new object(), context, (_, _) => Task.FromResult(new object())).GetAwaiter().GetResult());
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        // ...and it is a PREFIX gate: every other service flows through untouched on the same daemon.
        var other = new MethodOnlyServerCallContext("/mainguard.v1.MergeQueueService/CanMerge");
        var result = interceptor.UnaryServerHandler<object, object>(
            new object(), other, (_, _) => Task.FromResult(new object())).GetAwaiter().GetResult();
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
