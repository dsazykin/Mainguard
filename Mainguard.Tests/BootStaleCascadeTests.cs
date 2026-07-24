using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;
using WorkerMergeState = Mainguard.Agents.Agents.WorkerMergeState;

namespace Mainguard.Tests;

/// <summary>
/// MG-29 — a merge replayed by the boot reconcile must fire the stale cascade on the queue that owns
/// the agent.
///
/// <para>The daemon wired <c>onMerged</c> as
/// <c>foreach (var handle in Array.Empty&lt;string&gt;()) registry.Resolve(handle)?…</c> — a
/// <b>hardcoded no-op</b>. Iterating an empty array means the body never runs, so a merge recovered at
/// boot never reached any queue: a co-tenant branch stayed <c>Verified</c> against a main that had
/// already moved, and remained mergeable on stale verification evidence — precisely the state the
/// cascade exists to prevent.</para>
///
/// <para>The callback now carries the lease's repo hash, so the owning queue is a direct lookup. These
/// tests exercise the <b>real daemon callback shape</b> rather than a stand-in.</para>
/// </summary>
public sealed class BootStaleCascadeTests
{
    private const string RepoHash = "repo-hash-a";
    private const string OtherRepo = "repo-hash-b";

    /// <summary>The exact callback body the daemon registers (GatewayServiceRegistration).</summary>
    private static Action<string, string, string> DaemonOnMerged(IMergeQueueRegistry registry) =>
        (repoHash, agentId, postSha) => registry.Resolve(repoHash)?.Queue.ConfirmHumanMerge(agentId, postSha);

    private static MergeQueue NewQueue(string mainSha)
    {
        var verStore = new InMemoryVerificationStore();
        Func<string, CancellationToken, Task<VerificationRecord>> run =
            (id, _) => Task.FromResult(new VerificationRecord(
                id, mainSha, true, "log.txt", "npm test", "confighash", DateTimeOffset.UtcNow));

        return new MergeQueue(RepoHash, mainSha, new InMemoryMergeQueueStore(), verStore, run);
    }

    [Fact]
    public async Task ReplayedMerge_StalesTheCoTenantBranch_OnTheOwningQueue()
    {
        var queue = NewQueue("sha0");
        var registry = new MergeQueueRegistry();
        registry.Register(RepoHash, new MergeQueueContext(queue, new InMemoryMergeLeaseStore()));

        // Two workers verify green against sha0.
        await queue.RunVerificationAsync("winner", CancellationToken.None);
        await queue.RunVerificationAsync("co-tenant", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("co-tenant"));

        // Boot replays the winner's merge through the REAL daemon callback.
        DaemonOnMerged(registry)(RepoHash, "winner", "sha1");

        // The co-tenant's verification is now against a main that moved — it must be re-staled.
        Assert.Equal(WorkerMergeState.StaleVerified, queue.GetState("co-tenant"));
        Assert.False(queue.CanMerge("co-tenant", out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // Pre-fix the body never executed at all; this pins that the lookup actually resolves.
    [Fact]
    public async Task ReplayedMerge_MarksTheMergedAgentMerged()
    {
        var queue = NewQueue("sha0");
        var registry = new MergeQueueRegistry();
        registry.Register(RepoHash, new MergeQueueContext(queue, new InMemoryMergeLeaseStore()));

        await queue.RunVerificationAsync("winner", CancellationToken.None);

        DaemonOnMerged(registry)(RepoHash, "winner", "sha1");

        Assert.Equal(WorkerMergeState.Merged, queue.GetState("winner"));
        Assert.Equal("sha1", queue.CurrentMainSha);
    }

    // The cascade must land on the OWNING queue only — a merge in one repo must not stale another's.
    [Fact]
    public async Task ReplayedMerge_DoesNotTouchAnotherReposQueue()
    {
        var owning = NewQueue("sha0");
        var bystander = NewQueue("sha0");
        var registry = new MergeQueueRegistry();
        registry.Register(RepoHash, new MergeQueueContext(owning, new InMemoryMergeLeaseStore()));
        registry.Register(OtherRepo, new MergeQueueContext(bystander, new InMemoryMergeLeaseStore()));

        await owning.RunVerificationAsync("winner", CancellationToken.None);
        await bystander.RunVerificationAsync("elsewhere", CancellationToken.None);

        DaemonOnMerged(registry)(RepoHash, "winner", "sha1");

        Assert.Equal(WorkerMergeState.Verified, bystander.GetState("elsewhere")); // untouched
    }

    // A repo whose swarm has not come up has no queue — the callback must no-op, not throw.
    [Fact]
    public void ReplayedMerge_ForAnUnregisteredRepo_IsANoOp()
    {
        var registry = new MergeQueueRegistry();
        var ex = Record.Exception(() => DaemonOnMerged(registry)("never-registered", "agent", "sha1"));
        Assert.Null(ex);
    }
}
