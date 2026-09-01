using System;
using System.Collections.Generic;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Wire-shaped facts must actually render. The daemon has always sent <c>verified_main_sha</c> as
/// its own field and the changed-test-command fact as a flagged item — but the rail read only
/// <c>entry.Verification</c> (never populated by the daemon projection, so the "verified against"
/// stamp could never render in the shipped app) and the cockpit header hid the changed-command
/// warning whenever the run-count delta was absent (which on the wire is always).
/// </summary>
public sealed class QueueProjectionRenderingTests
{
    private static QueueEntry WireEntry(WorkerMergeState state, string? verifiedSha) => new(
        AgentId: "agent-1",
        Name: "Loom-1",
        Branch: "agent/agent-1",
        State: state,
        Detail: "",
        Verification: null, // exactly how the daemon projection ships entries
        FlaggedItems: Array.Empty<FlaggedItem>(),
        VerificationInFlight: false,
        HasLiveSandbox: true,
        VerifiedMainSha: verifiedSha);

    [Fact]
    public void VerifiedEntry_RendersTheVerifiedAgainstStamp_FromTheWireSha()
    {
        var queue = new Mainguard.Agents.Agents.Mock.MockOrchestrator();
        var row = new QueueEntryViewModel("agent-1", _ => { }, queue);
        row.Update(WireEntry(WorkerMergeState.Verified, "d4e1f00929ab3c4d5e6f"), queue);

        Assert.Equal("main@d4e1f00929", row.VerifiedAgainst);
    }

    [Fact]
    public void UnverifiedEntry_DrawsNoStamp()
    {
        var queue = new Mainguard.Agents.Agents.Mock.MockOrchestrator();
        var row = new QueueEntryViewModel("agent-1", _ => { }, queue);
        row.Update(WireEntry(WorkerMergeState.Working, null), queue);

        Assert.Equal("", row.VerifiedAgainst);
    }

    /// <summary>Every reviewable row must reach the cockpit — it is the only home of the Merge button —
    /// while the rail keeps exactly ONE accented Review (the front entry; the One Accent Rule).</summary>
    [Fact]
    public void EveryReviewableRow_ExposesReview_WithExactlyOneAccent()
    {
        var queue = new TwoVerifiedStub();
        var rail = new QueueRailViewModel(queue, _ => { });

        var reviewable = rail.Entries.Where(e => e.IsReviewable).ToList();
        Assert.Equal(2, reviewable.Count);
        Assert.Equal(1, reviewable.Count(e => e.ShowReviewAccent));
        Assert.All(reviewable, e => Assert.True(e.ShowReviewAccent || e.ShowSecondaryReview,
            $"{e.AgentId}: a verified branch with no Review affordance cannot be merged at all"));
        Assert.DoesNotContain(reviewable, e => e.ShowReviewAccent && e.ShowSecondaryReview);

        // Non-reviewable rows get neither.
        Assert.All(rail.Entries.Where(e => !e.IsReviewable),
            e => Assert.False(e.ShowReviewAccent || e.ShowSecondaryReview));
    }

    private sealed class TwoVerifiedStub : IMergeQueueService
    {
        public event Action? Changed;

        public string MainSha => "abc123";

        public IReadOnlyList<QueueEntry> GetQueue() => new[]
        {
            WireEntry(WorkerMergeState.Verified, "sha-1") with { AgentId = "front" },
            WireEntry(WorkerMergeState.Verified, "sha-1") with { AgentId = "second" },
            WireEntry(WorkerMergeState.Working, null) with { AgentId = "still-working" },
        };

        public bool CanMerge(string agentId, out string reason)
        {
            reason = "";
            return true;
        }

        public Task<VerificationOutcome> RunVerificationAsync(string agentId) =>
            throw new NotSupportedException();

        public Task<VerificationLog> GetVerificationLogAsync(string agentId) =>
            throw new NotSupportedException();

        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        // Not exercised by this fixture: nothing here is parked mid-rebase, and a double that pretended
        // otherwise would let a test pass on a conflict the projection never carried.
        public Task ResolveConflictWithAgentAsync(string agentId) =>
            throw new NotSupportedException("this fixture has no parked rebase conflicts");

        public Task AbortRebaseAsync(string agentId) =>
            throw new NotSupportedException("this fixture has no parked rebase conflicts");

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void ChangedTestCommand_RendersInTheCockpitHeader_EvenWithoutARunCountDelta()
    {
        var ctx = new ReviewCockpitContext("agent-1", "Loom-1", "agent/agent-1",
            new List<Mainguard.Git.Models.FilePatch>())
        {
            ChangedTestCommand = true, // the wire fact, no TestDelta available
        };
        var cockpit = new ReviewCockpitViewModel(ctx, onMerge: _ => { });

        Assert.Contains("test command changed", cockpit.TestDeltaSummary, StringComparison.OrdinalIgnoreCase);
    }
}
