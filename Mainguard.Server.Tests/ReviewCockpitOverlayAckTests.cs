using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The review cockpit <b>overlay</b> — the second review surface, opened from the queue rail — against a
/// real in-proc daemon, driven through the shipped <see cref="DaemonClient"/> +
/// <see cref="DaemonBackedOrchestrator"/> the app runs on.
///
/// <para><b>What was wrong.</b> The overlay was composed with <c>changedGate: null</c>, <c>queue: null</c>
/// and a private in-process <c>AcknowledgmentStore</c>, so it surfaced no daemon-flagged item at all — and
/// had it surfaced one, the checkmark would have written to a store no merge consults. A control that
/// reports a merge unblocked while the gate is still shut is worse than an absent one, which is why the
/// wiring was deliberately left out until the acknowledgment had somewhere real to go.</para>
///
/// <para>These tests assert on the <b>daemon-side gate</b>, never on "the RPC was invoked": the branch is
/// blocked, the overlay's own acknowledge control is pressed, and the merge is then genuinely permitted
/// where it was refused before — plus the negative, that acknowledging a different item id clears nothing.</para>
/// </summary>
public sealed class ReviewCockpitOverlayAckTests
{
    private const string AgentId = "loom-9";
    private const string RepoHandle = "repo-overlay-1";
    private const string MainSha = "main-000001";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Overlay_SurfacesTheDaemonsFlaggedItem_AndItsAcknowledgementUnblocksTheDaemonGate()
    {
        using var world = await OverlayWorld.StartAsync();

        // ---- (a) the daemon's gate is genuinely shut, in its own words ----
        Assert.False(world.Queue.CanMerge(AgentId, out var blockedReason));
        Assert.Contains("test command", blockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(world.ChangedTestCommand.IsUnacknowledged(AgentId));

        // ---- (b) the OVERLAY surfaces that item. It surfaced nothing at all before. ----
        var cockpit = world.OpenOverlay();
        Assert.True(cockpit.FlaggedPanel.HasItems, "the overlay surfaced no flagged item for a daemon-blocked branch");
        var row = Assert.Single(cockpit.FlaggedPanel.Items);
        Assert.Equal("changed-test-command", row.ItemId);
        Assert.Contains("test command", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.IsAcknowledged);

        // ---- (c) the overlay renders the daemon's refusal, not a local guess ----
        Assert.False(cockpit.CanMerge);
        Assert.Contains("test command", cockpit.MergeReason, StringComparison.OrdinalIgnoreCase);

        // ---- (d) the control is live: offered, and nothing is standing in its way ----
        Assert.True(row.CanAck, "the overlay's acknowledge control was not usable on a live, blocked item");
        Assert.False(cockpit.FlaggedPanel.HasUnavailableNotice);
        Assert.True(row.AcknowledgeCommand.CanExecute(null));

        // ---- (e) THE NEGATIVE: acknowledging a DIFFERENT id clears nothing. The ack is per item. ----
        var wrong = await world.Acks.AcknowledgeAsync(AgentId, "some-other-item", CancellationToken.None);
        Assert.False(wrong.Acknowledged);
        Assert.True(world.ChangedTestCommand.IsUnacknowledged(AgentId), "a wrong item id cleared the gate");
        Assert.False(world.Queue.CanMerge(AgentId, out var stillBlocked));
        Assert.Contains("test command", stillBlocked, StringComparison.OrdinalIgnoreCase);
        cockpit.RefreshFromQueue();
        Assert.False(cockpit.FlaggedPanel.Items[0].IsAcknowledged);
        Assert.False(cockpit.CanMerge);

        // ---- (f) the human presses THE row's acknowledge control on the overlay ----
        await row.AcknowledgeCommand.ExecuteAsync(null);

        // The DAEMON-SIDE gate is what moved: its own state, and the merge it was refusing.
        Assert.False(world.ChangedTestCommand.IsUnacknowledged(AgentId));
        Assert.True(
            world.Queue.CanMerge(AgentId, out var afterReason),
            $"the overlay's acknowledgment never reached the daemon gate — still: {afterReason}");

        // The row tells the truth about it: acknowledged, with nothing to warn about.
        Assert.True(row.IsAcknowledged);
        Assert.False(row.HasAckNotice);
        Assert.Equal("Acknowledged", row.AckLabel);

        // ---- (g) and the overlay itself converges on the permitted merge off the re-pushed stream ----
        Assert.True(
            await WaitUntilAsync(() => { cockpit.RefreshFromQueue(); return cockpit.CanMerge; }),
            "the overlay never saw the merge become permitted (the queue stream did not re-push the gate change)");
        Assert.Equal("ready to merge", cockpit.MergeReason);
    }

    /// <summary>
    /// The absent-daemon half of the same promise: with no repo active there is no handle to address the
    /// acknowledgment RPC with, so nothing can reach the gate. The adapter must SAY so rather than return
    /// a completed task the surface reads as success — the silent no-op is the exact defect being fixed,
    /// and re-introducing it one layer down would be indistinguishable from never fixing it.
    /// </summary>
    [Fact]
    public async Task WithNoActiveRepo_TheAcknowledgementIsRefusedOutLoud_NotSilentlyDropped()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();
        // Deliberately no SetActiveRepo.

        Assert.False(adapter.CanAcknowledgeFlaggedChange(out var why));
        Assert.Contains("no repository", why, StringComparison.OrdinalIgnoreCase);

        var outcome = await adapter.AcknowledgeFlaggedChangeReportedAsync(
            AgentId, "changed-test-command", CancellationToken.None);
        Assert.False(outcome.Acknowledged);
        Assert.False(outcome.CanMerge);
        Assert.NotEqual("", outcome.Reason);

        // The seam the overlay's panel consults before offering the control agrees.
        var source = new DaemonFlaggedChangeSource(adapter);
        Assert.False(source.CanAcknowledge(out var sourceWhy));
        Assert.NotEqual("", sourceWhy);
        Assert.Empty(source.FlaggedFor(AgentId));
    }

    // ---- the world ------------------------------------------------------

    private sealed class OverlayWorld : IDisposable
    {
        private OverlayWorld(
            DaemonFixture daemon, DaemonClient client, DaemonBackedOrchestrator adapter,
            MergeQueue queue, ChangedTestCommandGate changedTestCommand)
        {
            _daemon = daemon;
            _client = client;
            Adapter = adapter;
            Queue = queue;
            ChangedTestCommand = changedTestCommand;
            Acks = new DaemonFlaggedChangeSource(adapter);
        }

        private readonly DaemonFixture _daemon;
        private readonly DaemonClient _client;

        public DaemonBackedOrchestrator Adapter { get; }
        public MergeQueue Queue { get; }
        public ChangedTestCommandGate ChangedTestCommand { get; }
        public DaemonFlaggedChangeSource Acks { get; }

        public static async Task<OverlayWorld> StartAsync()
        {
            var daemon = new DaemonFixture();
            _ = daemon.Token; // force a single synchronous host build before the pumps race on it
            var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
            var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
            adapter.Start();

            var registry = (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>();
            var leases = daemon.Services.GetRequiredService<IMergeLeaseStore>();

            // The RT-D2 gate, ANDed into CanMerge exactly as the daemon wires it — this is the gate the
            // acknowledgment has to reach, and the reason the branch is blocked below.
            var changed = new ChangedTestCommandGate();
            var queue = new MergeQueue(
                repoHash: "h-overlay",
                currentMainSha: MainSha,
                store: new InMemoryMergeQueueStore(),
                verifications: new InMemoryVerificationStore(),
                runVerification: (agentId, ct) => Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                    agentId, MainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test --strict",
                    ConfigHash: "cfg", When: DateTimeOffset.UtcNow)),
                gates: new IMergeGate[] { changed });

            // Green, and therefore mergeable but for the drift — a branch that merely failed would prove
            // nothing about the gate.
            await queue.RunVerificationAsync(AgentId, CancellationToken.None);
            changed.SetFlagged(AgentId, changed: true);

            registry.Register(RepoHandle, new MergeQueueContext(queue, leases) { ChangedTestCommand = changed });
            adapter.SetActiveRepo(RepoHandle);

            var projected = await WaitUntilAsync(() =>
                adapter.GetQueue().FirstOrDefault(e => e.AgentId == AgentId) is { } e && e.FlaggedItems.Count == 1);
            Assert.True(projected, "the daemon's flagged item never reached the client queue projection");

            return new OverlayWorld(daemon, client, adapter, queue, changed);
        }

        /// <summary>Builds the overlay exactly as <c>ControlCenterViewModel.OpenReviewAsync</c> does.</summary>
        public ReviewCockpitViewModel OpenOverlay()
        {
            var ctx = new ReviewCockpitContext(AgentId, "Loom-9", $"agent/{AgentId}", Array.Empty<FilePatch>());
            return new ReviewCockpitViewModel(
                ctx,
                onMerge: _ => { },
                live: new DaemonFlaggedChangeSource(Adapter));
        }

        public void Dispose()
        {
            Adapter.Dispose();
            _client.Dispose();
            _daemon.Dispose();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
