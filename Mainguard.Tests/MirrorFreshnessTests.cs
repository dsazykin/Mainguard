using System;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// The mirror's freshness on the rail (owner decision 2026-09-04): the daemon states two facts — when it
/// last pulled the mirror's main forward from the checkout, and whether that failed — and the client
/// renders the age. Pinned at the projection (the wire fields reach the seam, absent fields stay absent)
/// and at the wording (a failure is a warning that carries the daemon's reason, never a footnote).
/// </summary>
public sealed class MirrorFreshnessTests
{
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    [Fact]
    public void TheStampAndTheError_ReachTheSeam_AndAbsentFieldsStayAbsent()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

        adapter.ApplyQueueUpdate(new Proto.QueueUpdate { MainSha = "abc123" });
        Assert.Null(adapter.MirrorMainRefreshedAt);
        Assert.Null(adapter.MirrorMainRefreshError);

        var at = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
        adapter.ApplyQueueUpdate(new Proto.QueueUpdate
        {
            MainSha = "abc123",
            MirrorMainRefreshedAt = at.ToString("O"),
            MirrorMainRefreshError = "",
        });
        Assert.Equal(at, adapter.MirrorMainRefreshedAt);
        Assert.Null(adapter.MirrorMainRefreshError);

        adapter.ApplyQueueUpdate(new Proto.QueueUpdate
        {
            MainSha = "abc123",
            MirrorMainRefreshedAt = at.ToString("O"),
            MirrorMainRefreshError = "git fetch origin main failed: could not read from remote",
        });
        Assert.Equal("git fetch origin main failed: could not read from remote", adapter.MirrorMainRefreshError);
    }

    [Fact]
    public void TheLine_SaysTheAge_AndAFailureIsAWarningCarryingTheReason()
    {
        var now = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

        Assert.Equal(("", false), QueueRailViewModel.MirrorFreshness(null, null, now));
        Assert.Equal(
            ("mirror refreshed from your checkout just now", false),
            QueueRailViewModel.MirrorFreshness(now.AddSeconds(-20), null, now));
        Assert.Equal(
            ("mirror refreshed from your checkout 5 min ago", false),
            QueueRailViewModel.MirrorFreshness(now.AddMinutes(-5), null, now));
        Assert.Equal(
            ("mirror refreshed from your checkout 3 h ago", false),
            QueueRailViewModel.MirrorFreshness(now.AddHours(-3), null, now));

        var (text, failed) = QueueRailViewModel.MirrorFreshness(now.AddMinutes(-2), "git fetch origin main failed: boom", now);
        Assert.True(failed);
        Assert.Contains("could not be refreshed", text);
        Assert.Contains("2 min ago", text);
        Assert.Contains("git fetch origin main failed: boom", text);
    }
}
