using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Models;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// The cancel arm of the human merge — the one leg of <see cref="DaemonBackedOrchestrator.ConfirmMergeAsync"/>
/// that nothing covered, and the one the cancellable-merge change made reachable and wrong.
///
/// <para><b>The defect.</b> <c>ControlCenterViewModel.MergeToken()</c> cancelled the previous merge's token
/// every time any merge started, and the cockpit's Merge command is sync fire-and-forget whose
/// <c>CanExecute</c> is the daemon's gate flag rather than an in-flight guard. So a double-click — or Merge
/// on entry B while A was still fetching — cancelled A. That token also reached RT-D1 <b>step 3</b>, the
/// recording, which has no abandon arm and no report arm: on the local path A's <c>--ff-only</c> had
/// already landed on the user's own checkout, so what the cancel stopped was not the merge but the record
/// of it. The human was then shown either a raw <c>Status(StatusCode="Cancelled")</c> or, had it surfaced
/// as an <see cref="OperationCanceledException"/>, the sentence "nothing was merged and the queue is
/// unchanged" — about a merge sitting in their git history. There is no Cancel control in the shipped app,
/// so the second click bought no user benefit and carried that hazard alone.</para>
///
/// <para><b>What is asserted here</b> is the three-part fix, each part against the shipped code path:
/// a second merge does not cancel the first; step 3 does not observe the surface's token; and a merge that
/// LANDED is never abandoned nor described as not having happened.</para>
/// </summary>
public sealed class MergeCancelArmTests
{
    private const string Handle = "repo-handle";
    private const string LandedSha = "1111111222222233333334444444555555566666";

    // ---- (1) the second click ---------------------------------------------------------------------

    /// <summary>
    /// The defect at its source: two merges from one surface, and the FIRST one's token must still be
    /// live. Asserted on the token the shipped <c>onMerge</c> closure passes to
    /// <see cref="MergeActionRunner.RunAsync"/>, which is the thing that was cancelled.
    /// </summary>
    [AvaloniaFact]
    public void ASecondMerge_LeavesTheFirstMergesTokenRunning()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(mock, mock, mock, mock, mock, mock, Owner: null));

        var first = vm.MergeToken();
        var second = vm.MergeToken();

        Assert.False(
            first.IsCancellationRequested,
            "pressing Merge a second time cancelled the merge already in flight — on the local path its "
            + "fast-forward has already landed on the user's checkout, so only the RECORDING is stopped");

        // One source for the surface's whole life is what makes that true, so it is stated directly: a
        // per-merge source would satisfy the assertion above only until someone re-introduced the rotate.
        Assert.Equal(first, second);
    }

    /// <summary>After teardown a merge must not start on a fresh live token — the surface that would have
    /// bounded it is gone. The runner checks the token before the call, so this reads as "cancelled",
    /// which is honest: nothing was begun.</summary>
    [AvaloniaFact]
    public void AMergeStartedAfterTeardown_GetsAnAlreadyCancelledToken()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var vm = new ControlCenterViewModel(
            new OrchestratorServices(mock, mock, mock, mock, mock, mock, Owner: null));

        vm.Dispose();

        Assert.True(vm.MergeToken().IsCancellationRequested);
    }

    // ---- (2)+(3) the recording leg ------------------------------------------------------------------

    /// <summary>
    /// <b>The heart of it.</b> The surface's token is cancelled while step 2 is running on the LOCAL path —
    /// exactly what a second Merge click used to do. <c>PerformJournaledMerge</c> is synchronous git work
    /// that ignores the token once started, so the fast-forward lands anyway; the only question is what
    /// happens next. Step 3 must still run, the lease must not be abandoned, and the human must be told
    /// the merge happened.
    /// </summary>
    [Fact]
    public async Task ACancelWhileTheLocalMergeIsLanding_StillRecordsIt_AndNeverAbandons()
    {
        using var client = UncontactedClient();
        using var adapter = NewAdapter(client);
        using var surface = new CancellationTokenSource();

        var abandons = new List<string>();
        var recorded = new List<string>();
        adapter.BeginMergeOverride = (_, _, _) => Task.FromResult(Granted());
        adapter.AbandonMergeOverride = (_, _, _, reason, _) => { abandons.Add(reason); return Task.FromResult(true); };
        adapter.RecordMergeOverride = (_, _, _, sha, ct) =>
        {
            // The token reaching step 3 is the assertion. Observing it here rather than trusting the call
            // site means a future re-link fails this test rather than silently restoring the defect.
            ct.ThrowIfCancellationRequested();
            recorded.Add(sha);
            return Task.FromResult(true);
        };
        adapter.MergeExecutorOverride = _ => new LandingExecutor(surface);

        var outcome = await adapter.ConfirmMergeAsync("agent-7", surface.Token);

        Assert.True(surface.IsCancellationRequested); // the merge really was cancelled mid-flight
        Assert.Equal(new[] { LandedSha }, recorded); // ...and recorded anyway
        Assert.Empty(abandons);                      // ...and the lease was never handed back
        Assert.Equal(LandedSha, outcome.NewMainSha);
        Assert.Equal(MergeEntryOrigin.Local, outcome.Origin);

        // And the sentence the human reads describes the merge that happened.
        Assert.Equal("Merged agent/agent-7 into main.", MergeActionRunner.Confirmation(outcome));
    }

    /// <summary>
    /// Step 3 failing on a merge that LANDED — a daemon that went away between the two legs. The queue is
    /// now behind git, which the human has to be told; what must not be said is that the merge was refused,
    /// and what must not be done is an abandon, because the lease names a merge that really occurred.
    /// </summary>
    [Fact]
    public async Task AFailedRecordingOfALandedMerge_SaysTheMergeIsInGit_NotThatItWasRefused()
    {
        using var client = UncontactedClient();
        using var adapter = NewAdapter(client);

        var abandons = new List<string>();
        adapter.BeginMergeOverride = (_, _, _) => Task.FromResult(Granted());
        adapter.AbandonMergeOverride = (_, _, _, reason, _) => { abandons.Add(reason); return Task.FromResult(true); };
        adapter.RecordMergeOverride = (_, _, _, _, _) =>
            Task.FromException<bool>(new InvalidOperationException("the daemon is unreachable"));
        adapter.MergeExecutorOverride = _ => new LandingExecutor(cancelWhileMerging: null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("agent-7", CancellationToken.None));

        Assert.Empty(abandons);
        Assert.Contains("IS merged into main", failure.Message, StringComparison.Ordinal);
        Assert.Contains("couldn't record it", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Can't merge", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The EXTERNAL path's cancel, which is ambiguous by construction: the only awaits it cancels at are
    /// the host's own calls, and an HTTP request whose client stopped waiting may still have been served.
    /// Abandoning and reporting "nothing was merged" would be a confident false statement about a pull
    /// request that may be merged upstream right now, so neither happens.
    /// </summary>
    [Fact]
    public async Task ACancelDuringTheHostMerge_NeitherAbandons_NorClaimsNothingWasMerged()
    {
        using var client = UncontactedClient();
        using var adapter = NewAdapter(client);
        using var surface = new CancellationTokenSource();

        adapter.ApplyQueueUpdate(ExternalEntry("pr-7"));

        var abandons = new List<string>();
        adapter.BeginMergeOverride = (_, _, _) => Task.FromResult(Granted());
        adapter.AbandonMergeOverride = (_, _, _, reason, _) => { abandons.Add(reason); return Task.FromResult(true); };
        adapter.RecordMergeOverride = (_, _, _, _, _) => Task.FromResult(true);
        adapter.ExternalMergeExecutorOverride = _ => new CancellingHostExecutor(surface);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("pr-7", surface.Token));

        Assert.Empty(abandons);
        Assert.Contains("pull request #7", failure.Message, StringComparison.Ordinal);
        Assert.Contains("can't tell whether the host merged it", failure.Message, StringComparison.Ordinal);

        // Not an OperationCanceledException — MergeActionRunner would then say "nothing was merged and the
        // queue is unchanged", which is precisely the claim this arm cannot support.
        Assert.IsNotType<OperationCanceledException>(failure);
    }

    /// <summary>
    /// The arm that was always right, asserted so the fix above cannot quietly swallow it: a cancel that
    /// lands BEFORE the local merge starts means nothing was merged, and the lease must go back or the
    /// repository stays unmergeable.
    /// </summary>
    [Fact]
    public async Task ACancelBeforeTheLocalMergeStarts_HandsTheLeaseBack()
    {
        using var client = UncontactedClient();
        using var adapter = NewAdapter(client);
        using var surface = new CancellationTokenSource();

        var abandons = new List<string>();
        adapter.BeginMergeOverride = (_, _, _) => { surface.Cancel(); return Task.FromResult(Granted()); };
        adapter.AbandonMergeOverride = (_, _, _, reason, _) => { abandons.Add(reason); return Task.FromResult(true); };
        adapter.RecordMergeOverride = (_, _, _, _, _) =>
            throw new Xunit.Sdk.XunitException("step 3 must not run for a merge that never started");
        adapter.MergeExecutorOverride = _ =>
            throw new Xunit.Sdk.XunitException("the executor must not run once the token is already cancelled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ConfirmMergeAsync("agent-7", surface.Token));

        Assert.Single(abandons);
    }

    // ---- rig ----------------------------------------------------------------------------------------

    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    /// <summary>An adapter bound the way <c>ProvisionRepo</c> binds one — handle, local checkout and sync
    /// remote — which is what makes <c>ConfirmMergeAsync</c> reach its merge legs at all.</summary>
    private static DaemonBackedOrchestrator NewAdapter(DaemonClient client)
    {
        var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.SetActiveRepo(Handle, localRepoPath: "/not/read/by/these/tests", syncRemoteName: "mainguard-sync");
        return adapter;
    }

    private static Proto.BeginMergeResponse Granted() => new()
    {
        Granted = true,
        LeaseId = "lease-1",
        ExpectedMainSha = "0000000aaaaaaabbbbbbbccccccc111111122222",
        ExpectedBranchSha = "9999999888888877777776666666555555544444",
    };

    /// <summary>A queue push that marks one entry as an upstream pull request, so the adapter routes it
    /// down the external transport exactly as a real push would.</summary>
    private static Proto.QueueUpdate ExternalEntry(string agentId)
    {
        var update = new Proto.QueueUpdate { MainSha = "0000000aaaaaaabbbbbbbccccccc111111122222" };
        update.Entries.Add(new Proto.QueueEntry
        {
            AgentId = agentId,
            State = "Verified",
            CanMerge = true,
            Origin = nameof(MergeEntryOrigin.External),
        });
        return update;
    }

    /// <summary>
    /// The local leg as it really behaves: synchronous git work that does not observe the token once it is
    /// running, so a cancel arriving mid-merge does not stop the fast-forward landing. Optionally fires
    /// that cancel itself, which is what a second Merge click did.
    /// </summary>
    private sealed class LandingExecutor : IJournaledMergeExecutor
    {
        private readonly CancellationTokenSource? _cancelWhileMerging;

        public LandingExecutor(CancellationTokenSource? cancelWhileMerging) => _cancelWhileMerging = cancelWhileMerging;

        public ForegroundMergeResult PerformJournaledMerge(ForegroundMergeRequest request, MergeLeaseRow lease)
        {
            _cancelWhileMerging?.Cancel();
            return new ForegroundMergeResult(Merged: true, NewMainSha: LandedSha, CasLost: false, Reason: null);
        }
    }

    /// <summary>The host merge cancelled mid-call — the shape <c>ExternalPrMergeService</c> produces, whose
    /// <c>when (ex is not OperationCanceledException)</c> filters deliberately let a cancel through rather
    /// than turning it into a refusal.</summary>
    private sealed class CancellingHostExecutor : IExternalPrMergeExecutor
    {
        private readonly CancellationTokenSource _cancel;

        public CancellingHostExecutor(CancellationTokenSource cancel) => _cancel = cancel;

        public Task<ForegroundMergeResult> MergeExternalPrAsync(
            ForegroundMergeRequest request, MergeLeaseRow lease, CancellationToken ct)
        {
            _cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new Xunit.Sdk.XunitException("the linked token must observe the surface's cancel");
        }
    }
}
