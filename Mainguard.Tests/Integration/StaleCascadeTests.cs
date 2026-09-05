using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests.Integration;

/// <summary>
/// P2-10 stale cascade (plan §6 tests 2,7 + TI-P2-10 2,7,8) — the master doc's canonical two/three-worker
/// scenario: one branch merges, every other <c>Verified</c> branch flips <c>StaleVerified</c>, auto
/// rebases + re-verifies against the new main, and the merge button stays blocked until fresh.
/// </summary>
public class StaleCascadeTests
{
    private sealed class Builder
    {
        public InMemoryVerificationStore VerStore = new();
        public MergeQueue Queue = null!;
        private long _tick;
        private readonly HashSet<string> _fail = new(StringComparer.Ordinal);

        // A gate the re-queue awaits before re-verifying, so a test can deterministically observe the
        // intermediate StaleVerified state (in production the re-verify follows immediately).
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FailFor(string id) => _fail.Add(id);

        public void ReleaseRequeue() => _gate.TrySetResult();

        public MergeQueue Build()
        {
            MergeQueue queue = null!;
            Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            {
                var when = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick));
                return Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, !_fail.Contains(id), "log", "npm test", "hash", when));
            };
            Func<string, CancellationToken, Task> requeue = async (id, ct) =>
            {
                await _gate.Task.ConfigureAwait(false);
                await queue.RunVerificationAsync(id, ct).ConfigureAwait(false);
            };
            queue = new MergeQueue("repo", "sha0", new InMemoryMergeQueueStore(), VerStore, run, requeue);
            Queue = queue;
            return queue;
        }
    }

    [Fact]
    public async Task TwoWorkers_AMergesFirst_BReverifiesBeforeMergeEnabled()
    {
        var b = new Builder();
        var q = b.Build();
        await q.RunVerificationAsync("A", CancellationToken.None);
        await q.RunVerificationAsync("B", CancellationToken.None);
        Assert.True(q.CanMerge("A", out _));
        Assert.True(q.CanMerge("B", out _));

        // A is the human merge (the only path to Merged): fires the cascade for the new main.
        q.RequestReview("A");
        q.ConfirmHumanMerge("A", "sha1");
        Assert.Equal(WorkerMergeState.Merged, q.GetState("A"));

        // Immediately, B is stale (re-verify gated) and its merge button is blocked.
        Assert.Equal(WorkerMergeState.StaleVerified, q.GetState("B"));
        Assert.False(q.CanMerge("B", out var reason));
        Assert.Contains("stale", reason);

        // Once the auto re-queue runs, B is fresh against the new main and mergeable again.
        b.ReleaseRequeue();
        await q.LastCascade;
        Assert.Equal(WorkerMergeState.Verified, q.GetState("B"));
        Assert.Equal("sha1", b.VerStore.Latest("repo", "B")!.MainSha);
        Assert.True(q.CanMerge("B", out _));
    }

    [Fact]
    public async Task ThreeWorkers_AllStaleAndRequeued_FIFO()
    {
        var b = new Builder();
        var q = b.Build();
        await q.RunVerificationAsync("A", CancellationToken.None);
        await q.RunVerificationAsync("B", CancellationToken.None);
        await q.RunVerificationAsync("C", CancellationToken.None);

        q.RequestReview("A");
        q.ConfirmHumanMerge("A", "sha1");

        Assert.Equal(WorkerMergeState.StaleVerified, q.GetState("B"));
        Assert.Equal(WorkerMergeState.StaleVerified, q.GetState("C"));

        b.ReleaseRequeue();
        await q.LastCascade;
        Assert.Equal(WorkerMergeState.Verified, q.GetState("B"));
        Assert.Equal(WorkerMergeState.Verified, q.GetState("C"));
        Assert.Equal("sha1", b.VerStore.Latest("repo", "B")!.MainSha);
        Assert.Equal("sha1", b.VerStore.Latest("repo", "C")!.MainSha);
    }

    [Fact]
    public async Task VerificationFailsAfterRebase_SurfacesTheFailure_NotSilentlyRetried()
    {
        var b = new Builder();
        var q = b.Build();
        await q.RunVerificationAsync("A", CancellationToken.None);
        await q.RunVerificationAsync("B", CancellationToken.None);

        b.FailFor("B"); // B's re-verification against the new main will fail.
        q.RequestReview("A");
        q.ConfirmHumanMerge("A", "sha1");
        b.ReleaseRequeue();
        await q.LastCascade;

        // H2: the cascade's re-verification failed, and a failure is now its own state rather than the
        // Working an unverified branch sits in. Still surfaced, never silently retried.
        Assert.Equal(WorkerMergeState.VerificationFailed, q.GetState("B"));
        Assert.False(q.CanMerge("B", out var reason));
        Assert.Contains("FAILED", reason);
    }
}
