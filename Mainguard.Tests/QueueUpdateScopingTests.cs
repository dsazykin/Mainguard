using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// A merge-queue push belongs to the repo handle it was produced for, and to no other.
///
/// <para><b>Why.</b> <c>SetActiveRepo</c> swaps repos by cancelling the old queue pump and starting a new
/// one, but cancelling a stream does not unwind a message the old pump has ALREADY dequeued. That last
/// message lands after the swap and rewrites the whole projection — entries, gate reasons, origins, the
/// main sha — with the PREVIOUS repository's rows. Nothing wrong reaches git, because the daemon holds the
/// merge lease against a handle and refuses a merge for an entry that is not the bound repo's. What breaks
/// is the account the human reads: the rail shows repo A's branches under repo B's name until the next
/// push overwrites them, which on a quiet queue is indefinitely.</para>
/// </summary>
public sealed class QueueUpdateScopingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.QueueUpdate Update(string agentId, string mainSha)
    {
        var update = new Proto.QueueUpdate { MainSha = mainSha };
        update.Entries.Add(new Proto.QueueEntry
        {
            AgentId = agentId,
            State = "Working",
            CanMerge = false,
            GateReason = "not verified",
        });
        return update;
    }

    /// <summary>A stream that holds a message back until released, so the test can reproduce the exact
    /// ordering the race needs: the old pump's update is IN FLIGHT across the repo swap.</summary>
    private static async IAsyncEnumerable<Proto.QueueUpdate> Gated(
        Proto.QueueUpdate update, TaskCompletionSource release, [EnumeratorCancellation] CancellationToken ct)
    {
        await release.Task.ConfigureAwait(false);
        yield return update;

        var forever = new TaskCompletionSource();
        using (ct.Register(() => forever.TrySetResult()))
        {
            await forever.Task.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The old repo's late push must be DROPPED, not applied. Asserted on what the rail renders — the
    /// entries and the main sha — because that is the thing that lied.
    /// </summary>
    [Fact]
    public async Task QueueUpdate_ForAHandleTheAdapterHasLeft_IsDropped()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

        var releaseStaleUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.QueueStreamOverride = (handle, ct) => handle == "handle-a"
            ? Gated(Update("agent-of-repo-a", "aaaaaaa"), releaseStaleUpdate, ct)
            : Gated(Update("agent-of-repo-b", "bbbbbbb"),
                    ReleasedNow(), ct);

        using var cts = new CancellationTokenSource();
        var stalePump = adapter.RunQueuePumpForTestAsync("handle-a", cts.Token);

        // Repo B is now the bound repo. The old pump is still alive here on purpose: this models the
        // window in which its already-dequeued message is on its way to the applier.
        var railShowsB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Changed += () =>
        {
            if (adapter.GetQueue().Any(e => e.AgentId == "agent-of-repo-b")) railShowsB.TrySetResult();
        };
        adapter.SetActiveRepo("handle-b", localRepoPath: "/tmp/b", syncRemoteName: "mainguard-sync");

        Assert.Same(railShowsB.Task, await Task.WhenAny(railShowsB.Task, Task.Delay(Timeout)));

        // NOW let repo A's stale update through, and give the applier a chance to run it.
        releaseStaleUpdate.SetResult();
        await WaitForQuietAsync();

        var rail = adapter.GetQueue();
        Assert.DoesNotContain(rail, e => e.AgentId == "agent-of-repo-a");
        Assert.Contains(rail, e => e.AgentId == "agent-of-repo-b");
        Assert.Equal("bbbbbbb", adapter.MainSha);

        cts.Cancel();
        await stalePump;
    }

    private static TaskCompletionSource ReleasedNow()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>Gives the stale update every chance to be applied. Generous on purpose: a test for
    /// "this must NOT happen" is only as strong as the time it waits for it to happen.</summary>
    private static async Task WaitForQuietAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(10);
        }
    }
}
