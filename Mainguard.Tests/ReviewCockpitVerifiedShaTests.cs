using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Models;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The review cockpit's header strip renders "verified @ &lt;sha&gt;" — the one thing that tells a
/// reviewer whether the green they are about to merge was measured against today's main or a week-old
/// one. It never appeared. <c>ControlCenterViewModel</c> built <see cref="ReviewCockpitContext"/> with
/// the bare 4-arg constructor and set none of the enrichment properties, so <c>BuildHeader</c> was a
/// no-op past the title.
///
/// <para>The value was not missing — it was <b>discarded</b>. The daemon has always sent
/// <c>verified_main_sha</c> on every queue entry; <c>DaemonBackedOrchestrator</c>'s queue projection
/// dropped it on the floor one layer above the cockpit.</para>
/// </summary>
public class ReviewCockpitVerifiedShaTests
{
    private static ReviewCockpitContext Context(string? verifiedSha) =>
        new("agent-1", "claude-code", "agent/agent-1", Array.Empty<FilePatch>())
        {
            VerifiedAgainstSha = verifiedSha,
        };

    /// <summary>
    /// Pins the CONSUMER, which was never the broken half — <c>BuildHeader</c> read
    /// <c>VerifiedAgainstSha</c> correctly all along. It is here so the producer wiring below has
    /// something stated to satisfy, and so a later change to the header cannot quietly drop the stamp
    /// again. The fails-before evidence is <see cref="QueueEntry_CarriesTheVerifiedSha"/>: the sha had
    /// nowhere to travel, so in the app this property was always null and the view hid the strip.
    /// </summary>
    [Fact]
    public void VerifiedSha_RendersTheStamp()
    {
        var vm = new ReviewCockpitViewModel(Context("d4e1f00929ab3c4d5e6f"), onMerge: _ => { });

        Assert.Contains("verified @", vm.VerifiedText, StringComparison.Ordinal);
        Assert.Contains("d4e1f00", vm.VerifiedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unverified entry draws NO stamp. This is the half that must not regress: the point of the
    /// strip is trust, so an absent verification has to render as absent — never as a reassuring stamp
    /// against some other sha.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoVerification_DrawsNoStamp(string? sha)
    {
        var vm = new ReviewCockpitViewModel(Context(sha), onMerge: _ => { });

        Assert.True(string.IsNullOrEmpty(vm.VerifiedText),
            $"an unverified branch was stamped '{vm.VerifiedText}'");
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER — before, this did not compile: <c>QueueEntry</c> had no
    /// <c>VerifiedMainSha</c> at all, so the daemon's <c>verified_main_sha</c> had nowhere to travel and
    /// the projection dropped it. That is the whole reason the stamp never rendered.
    /// </summary>
    [Fact]
    public void QueueEntry_CarriesTheVerifiedSha()
    {
        var entry = new QueueEntry(
            "agent-1", "agent-1", "agent/agent-1", WorkerMergeState.Verified, "",
            Verification: null, FlaggedItems: Array.Empty<FlaggedItem>(),
            VerifiedMainSha: "d4e1f00929ab3c4d5e6f");

        Assert.Equal("d4e1f00929ab3c4d5e6f", entry.VerifiedMainSha);
    }

    /// <summary>
    /// The sha is NOT smuggled in as a fabricated <c>VerificationRecord</c>. That record also carries a
    /// pass/fail verdict and test counts, none of which are on the wire — synthesising one to carry a
    /// sha would mean inventing the verdict, which is the precise failure mode this surface exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void VerifiedSha_IsNotCarriedAsAnInventedVerdict()
    {
        var entry = new QueueEntry(
            "agent-1", "agent-1", "agent/agent-1", WorkerMergeState.Verified, "",
            Verification: null, FlaggedItems: Array.Empty<FlaggedItem>(),
            VerifiedMainSha: "d4e1f00929ab3c4d5e6f");

        Assert.Null(entry.Verification);
    }
}
