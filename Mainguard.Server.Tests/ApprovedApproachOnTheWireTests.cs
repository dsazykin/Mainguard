using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>What a reviewer can learn about what was APPROVED</b>, at the daemon's edge.
///
/// <para>The review surface rendered a diff and nothing to compare it against. The approved plan's
/// <c>approach</c> — the paragraph the human actually said yes to — existed on the daemon, was read once
/// at approval, and then never left it. Measured on a real run: an approved approach said the module had
/// no error-handling or validation idiom, so the plan would keep plain <c>a / b</c>; the branch shipped
/// <c>RangeError</c> on zero plus a validation layer that changed the behaviour of three pre-existing
/// helpers. The scope was honoured so <c>flagged_items</c> was empty, <c>can_merge</c> was true, and the
/// verification was green because the worker had written the tests. Every gate held. The reviewer simply
/// never had the other half of the comparison on screen.</para>
///
/// <para>These drive the shipped <c>StreamQueue</c> RPC against the real composition root, so a daemon
/// that stops carrying the approval fails here rather than in a hand-built projection.</para>
/// </summary>
public sealed class ApprovedApproachOnTheWireTests
{
    private const string RepoHandle = "repo-approach";
    private const string MainSha = "main-0000aa";
    private const string AgentId = "loom-approach";

    private const string Approach =
        "the module has no error-handling or validation idiom anywhere in it, so I will keep plain a / b "
        + "and let the language semantics stand";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The approved plan's identity and approach reach the client on the queue entry, verbatim. Verbatim
    /// because a summarised approach is the daemon choosing which sentence the reviewer gets to compare
    /// against — and the sentence that would get dropped is the one the diff disagrees with.
    /// </summary>
    [Fact]
    public async Task TheApprovedApproachReachesTheClientOnTheQueueEntry()
    {
        using var host = new DaemonFixture();
        var (merge, headers) = NewClients(host);
        RegisterQueue(host, ApprovedWork(DeviationDeclaration.None));

        var entry = await FirstEntryAsync(merge, headers);

        Assert.Equal(Approach, entry.ApprovedPlanApproach);
        Assert.Equal("Add divide() to the calculator", entry.ApprovedPlanTitle);
        Assert.Equal("plan-1", entry.ApprovedPlanId);
    }

    /// <summary>
    /// <b>The three declarations stay three on the wire.</b> A bool would have collapsed "the worker
    /// asserted it followed the approach" into "nobody ever asked" — the exact conflation the whole
    /// mechanism exists to prevent, reintroduced one layer down where no surface could recover it.
    /// </summary>
    [Theory]
    [InlineData(nameof(DeviationDeclaration.None))]
    [InlineData(nameof(DeviationDeclaration.NotDeclared))]
    [InlineData(nameof(DeviationDeclaration.Declared))]
    public async Task TheWorkersDeclarationReachesTheClientAsOneOfThreeAnswers(string declaration)
    {
        using var host = new DaemonFixture();
        var (merge, headers) = NewClients(host);
        RegisterQueue(host, ApprovedWork(Enum.Parse<DeviationDeclaration>(declaration)));

        var entry = await FirstEntryAsync(merge, headers);

        Assert.Equal(declaration, entry.DeviationDeclaration);
    }

    /// <summary>
    /// An entry with no approved plan carries nothing — a manual agent, an external-PR head and a worker
    /// spawned with plan mode off were never approved against anything, and a surface that received an
    /// empty approach would have to decide whether that meant "approved, with nothing written" or "never
    /// approved". Empty means the second, and the surface draws no panel.
    /// </summary>
    [Fact]
    public async Task AnEntryWithNoApprovedPlan_CarriesNoApprovalFieldsAtAll()
    {
        using var host = new DaemonFixture();
        var (merge, headers) = NewClients(host);
        RegisterQueue(host, approved: null);

        var entry = await FirstEntryAsync(merge, headers);

        Assert.Equal("", entry.ApprovedPlanApproach);
        Assert.Equal("", entry.ApprovedPlanTitle);
        Assert.Equal("", entry.ApprovedPlanId);
        Assert.Equal("", entry.DeviationDeclaration);
    }

    // ---- helpers ------------------------------------------------------------

    private static ApprovedWork ApprovedWork(DeviationDeclaration declaration) => new(
        new TaskPlan(
            "plan-1", "Add divide() to the calculator",
            new[] { "src/calc.js" }, Approach, "node test.js", 1m, DateTimeOffset.UtcNow),
        declaration,
        declaration == DeviationDeclaration.Declared
            ? new[] { "added RangeError on zero" }
            : Array.Empty<string>());

    private static async Task<Mainguard.Protos.V1.QueueEntry> FirstEntryAsync(
        MergeQueueService.MergeQueueServiceClient merge, Metadata headers)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = merge.StreamQueue(
            new StreamQueueRequest { RepoHandle = RepoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        return Assert.Single(stream.ResponseStream.Current.Entries);
    }

    private static void RegisterQueue(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, ApprovedWork? approved)
    {
        var queue = new MergeQueue(
            repoHash: "approach-h1",
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(
                new VerificationRecord(agentId, MainSha, true, "", "node test.js", "cfg", DateTimeOffset.UtcNow)));
        queue.EnsureEntry(AgentId, MergeEntryOrigin.Local);

        var registry = (MergeQueueRegistry)host.Services.GetRequiredService<IMergeQueueRegistry>();
        registry.Register(RepoHandle, new MergeQueueContext(
            queue, host.Services.GetRequiredService<IMergeLeaseStore>())
        {
            ResolveApprovedWork = approved is null ? null : _ => approved,
        });
    }

    private static (MergeQueueService.MergeQueueServiceClient Merge, Metadata Headers) NewClients(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };
        return (new MergeQueueService.MergeQueueServiceClient(channel), headers);
    }
}
