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
