using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Git;
using Mainguard.Git.Services;
using Mainguard.Server.Tests.Agents;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>The GUI Merge button actually merges</b> — end to end, through the real composition: the real in-proc
/// daemon, the shipped <see cref="DaemonClient"/>, the shipped <see cref="DaemonBackedOrchestrator"/> (the
/// exact adapter the Merge button runs on), and a real git repository on disk.
///
/// <para><b>These tests assert repository state, never RPC success.</b> "ConfirmMerge returned Confirmed"
/// is precisely what the broken code already produced: it took the repo's merge lease, walked the branch to
/// the terminal <c>Merged</c> state and wrote the RT-D1 idempotency record — with no git merge anywhere
/// between the two RPCs, passing the cached PRE-merge <c>main@sha</c> as the post-merge one. Every
/// assertion below is therefore about <c>refs/heads/main</c>: did it move, to what commit, and — for every
/// refusal — did it stay exactly where it was while the queue stayed exactly as it was.</para>
/// </summary>
public sealed class MergeExecutionPathTests : IClassFixture<DaemonFixture>, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string SyncRemote = "mainguard-vm";

    private readonly List<string> _dirs = new();
    private readonly DaemonFixture _daemon;

    // One in-proc daemon for the whole class (building it is by far the slowest thing here), with a
    // FRESH repo handle per test — the registry, the queues and the merge leases are all keyed by handle,
    // so tests still cannot see each other's state.
    private readonly string _repoHandle = "repo-merge-" + Guid.NewGuid().ToString("N");

    public MergeExecutionPathTests(DaemonFixture daemon)
    {
        _daemon = daemon;
        _ = _daemon.Token; // force a single synchronous host build
    }

    // ---- the repository fixture -----------------------------------------

    /// <summary>The user's real Windows-side checkout: main, an <c>agent/x</c> branch one commit ahead of
    /// it (the verified-and-rebased shape), and the daemon-owned sync remote registered on it.</summary>
    private sealed record RepoFixture(string Path, string MainSha, string AgentTip);

    private RepoFixture BuildRepo(string agentId = "x")
    {
        var repo = NewDir("mainguard-mergepath-");
        Git(repo, "-c", "init.defaultBranch=main", "init");
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@mainguard.local");
        Git(repo, "config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "seed");
        var mainSha = Rev(repo, "main");

        Git(repo, "checkout", "-b", $"agent/{agentId}");
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "agent work\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "agent commit");
        var agentTip = Rev(repo, $"agent/{agentId}");
        Git(repo, "checkout", "main");

        // The sync remote the merge fetches (an empty bare mirror: the branch is already local here, which
        // is the shape a repo has once the agent's work has been synced down).
        var bare = NewDir("mainguard-mergepath-bare-");
        Git(bare, "init", "--bare");
        Git(repo, "remote", "add", SyncRemote, bare);

        return new RepoFixture(repo, mainSha, agentTip);
    }

    // ---- the daemon-side queue ------------------------------------------

    /// <summary>Registers a live queue for this test's repo handle whose authoritative main is
    /// <paramref name="mainSha"/>, with a passing verification recorded for each named agent (so
    /// <c>CanMerge</c> is genuinely true — a gate that blocks the honest path proves nothing).</summary>
    private async Task<MergeQueue> RegisterQueueAsync(string mainSha, params string[] verifiedAgents)
    {
        var registry = (MergeQueueRegistry)_daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        var leases = Leases;
        var queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: mainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                agentId, mainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "dotnet test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow)));

        foreach (var agentId in verifiedAgents)
        {
            await queue.RunVerificationAsync(agentId, CancellationToken.None);
            Assert.True(queue.CanMerge(agentId, out var why), $"fixture is wrong — {agentId} cannot merge: {why}");
        }

        registry.Register(_repoHandle, new MergeQueueContext(queue, leases));
        return queue;
    }

    private IMergeLeaseStore Leases => _daemon.Services.GetRequiredService<IMergeLeaseStore>();

    private DaemonClient NewClient() => new(_daemon.CreateChannel, () => _daemon.Token);

    private DaemonBackedOrchestrator NewAdapter(DaemonClient client)
        => new(client, ownsClient: false, journalFactory: NewJournal);

    // ---- 1. the merge actually merges ------------------------------------

    /// <summary>
    /// The defect itself. Press Merge → <c>refs/heads/main</c> must be the agent branch's commit. Against
    /// the pre-fix code this fails on the very first assertion: the RPC pair completed happily, the branch
    /// went to <c>Merged</c>, and main had not moved by a single commit.
    /// </summary>
    [Fact]
    public async Task Merge_MovesMainToTheAgentBranch_AndRecordsTheShaMainActuallyMovedTo()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "x");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        await adapter.ConfirmMergeAsync("x").WaitAsync(Timeout);

        // REPOSITORY STATE — the only assertion that can tell a merge from a recorded non-merge.
        Assert.Equal(repo.AgentTip, Rev(repo.Path, "main"));
        Assert.NotEqual(repo.MainSha, Rev(repo.Path, "main"));

        // And the daemon recorded the sha main REALLY moved to, not the cached pre-merge one.
        Assert.Equal(repo.AgentTip, queue.CurrentMainSha);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 2. main moved underneath: nothing lands, nothing cascades -------

    /// <summary>
    /// The gate passes and the lease is granted, but main moved on the user's repo since the verification —
    /// the A5 CAS loses. Nothing may land and, above all, nothing may be RECORDED as having landed: a
    /// <c>ConfirmMerge</c> here would mark the branch <c>Merged</c> and fire <c>NotifyMainMoved</c> at every
    /// other agent in the repo on the strength of a merge that did not happen.
    /// </summary>
    [Fact]
    public async Task MainMovedUnderneath_NothingMerges_NothingIsRecorded_AndTheCoTenantIsUntouched()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "x", "y");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        // Someone else advanced main on the working repo after the verification was pinned to repo.MainSha.
        File.WriteAllText(Path.Combine(repo.Path, "other.txt"), "someone else\n");
        Git(repo.Path, "add", "-A");
        Git(repo.Path, "commit", "-m", "concurrent main move");
        var movedMain = Rev(repo.Path, "main");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.Contains("main moved", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(movedMain, Rev(repo.Path, "main"));          // the agent's work did NOT land
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Equal(repo.MainSha, queue.CurrentMainSha);          // no cascade: main never "advanced"
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("y")); // the co-tenant's work is untouched
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 3. not a fast-forward -------------------------------------------

    /// <summary>
    /// The freshness pre-check passes (the queue's main IS the repo's main) but the branch diverged, so
    /// <c>--ff-only</c> refuses — the CAS lost at the ref level. Same rule: no merge, nothing recorded.
    /// </summary>
    [Fact]
    public async Task BranchIsNotAFastForward_RefusesLoudly_AndLeavesMainAndTheQueueAlone()
    {
        var repo = BuildRepo();

        // Diverge: main gains a commit the agent branch does not contain, so agent/x is no longer a
        // fast-forward of main. The queue is then pinned to THIS main, so freshness passes and the refusal
        // has to come from the merge itself.
        File.WriteAllText(Path.Combine(repo.Path, "other.txt"), "divergence\n");
        Git(repo.Path, "add", "-A");
        Git(repo.Path, "commit", "-m", "divergent main commit");
        var divergedMain = Rev(repo.Path, "main");

        using var client = NewClient();
        var queue = await RegisterQueueAsync(divergedMain, "x");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.Contains("fast-forward", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(divergedMain, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 4. dirty working tree -------------------------------------------

    /// <summary>The human has uncommitted work in the repo the merge would land on. Refuse before touching
    /// anything, and say what to do about it — never merge into (or half-checkout) a dirty tree.</summary>
    [Fact]
    public async Task DirtyWorkingTree_RefusesWithSomethingActionable_AndMergesNothing()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "x");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        File.WriteAllText(Path.Combine(repo.Path, "README.md"), "the human is mid-edit\n");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.Contains("uncommitted", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 5. the lease is held elsewhere ----------------------------------

    /// <summary>One outstanding merge per repo. A second merge is refused in the daemon's own words, and —
    /// the part that matters for the new abandon path — the FIRST merge's lease is still held afterwards:
    /// a refused merge hands back its own lease, never somebody else's.</summary>
    [Fact]
    public async Task LeaseHeldElsewhere_RefusesAndLeavesTheOtherMergesLeaseIntact()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "x");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        var leases = Leases;
        var other = leases.TryBegin(_repoHandle, "lease-held-by-someone-else", "y", repo.MainSha, "main");
        Assert.NotNull(other);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.Contains("already in progress", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Equal("lease-held-by-someone-else", leases.GetOutstanding(_repoHandle)?.LeaseId);
    }

    // ---- 6. the gate refuses ---------------------------------------------

    /// <summary>The branch is not verified, so <c>CanMerge</c> is false and <c>BeginMerge</c> refuses before
    /// granting anything. The reason reaches the human and the repo is untouched.</summary>
    [Fact]
    public async Task GateRefuses_NoLeaseIsTaken_NoRefMoves_AndTheReasonIsSurfaced()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha); // nothing verified
        queue.EnsureEntry("x", MergeEntryOrigin.Local);
        Assert.False(queue.CanMerge("x", out _));

        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.StartsWith("Can't merge —", refusal.Message, StringComparison.Ordinal);

        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 7. no local checkout bound --------------------------------------

    /// <summary>An adapter that only knows the opaque repo handle cannot merge anything — and must refuse
    /// BEFORE taking the lease rather than consume the repo's one outstanding merge on a merge it has no
    /// way to perform.</summary>
    [Fact]
    public async Task WithoutALocalCheckout_RefusesBeforeTakingTheLease()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "x");
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle); // handle only — no path, no sync remote

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("x").WaitAsync(Timeout));
        Assert.Contains("local checkout", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("x"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 8. an external PR is not merged behind the host's back ----------

    /// <summary>
    /// P2-12: an <c>External</c> entry's <c>agent/pr-&lt;n&gt;</c> branch really does exist in the mirror, so
    /// the local fast-forward would happily "succeed" — landing the pull request's commits on the user's
    /// main while the PR stayed open upstream. The origin travels on the queue stream precisely so the
    /// local merge path can decline it.
    /// </summary>
    [Fact]
    public async Task ExternalPullRequestEntry_IsNotMergedLocally()
    {
        var repo = BuildRepo("pr-7");
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha, "pr-7");
        queue.EnsureEntry("pr-7", MergeEntryOrigin.External);
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        // The origin reaches the client on the queue stream, so wait for the projection to carry it.
        Assert.True(
            await WaitUntilAsync(() => adapter.GetQueue().Count > 0),
            "the queue projection never arrived — the origin could not have been read");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync("pr-7").WaitAsync(Timeout));
        Assert.Contains("pull request", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-7"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
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

    // ---- 9. every outcome is a sentence the human sees --------------------

    /// <summary>The surfaces invoke the merge fire-and-forget, so the runner is what turns each outcome
    /// into one visible line. A refusal must report the daemon's reason as a warning; a real merge must
    /// report the merge.</summary>
    [Fact]
    public async Task EveryOutcomeIsReported_RefusalAsAWarning_MergeAsAConfirmation()
    {
        var repo = BuildRepo();
        using var client = NewClient();
        var queue = await RegisterQueueAsync(repo.MainSha); // nothing verified → the gate refuses
        queue.EnsureEntry("x", MergeEntryOrigin.Local);
        using var adapter = NewAdapter(client);
        adapter.SetActiveRepo(_repoHandle, repo.Path, SyncRemote);

        var reported = new List<(string Message, bool IsWarning)>();
        await MergeActionRunner.RunAsync(adapter, "x", (m, w) => reported.Add((m, w)))
            .WaitAsync(Timeout);

        var refusal = Assert.Single(reported);
        Assert.True(refusal.IsWarning, "a refusal must be reported, and reported as a warning");
        Assert.Contains("Can't merge", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(repo.MainSha, Rev(repo.Path, "main"));

        // The control: once the branch verifies, the same button path reports a real merge.
        reported.Clear();
        await queue.RunVerificationAsync("x", CancellationToken.None);
        await MergeActionRunner.RunAsync(adapter, "x", (m, w) => reported.Add((m, w)))
            .WaitAsync(Timeout);

        var success = Assert.Single(reported);
        Assert.False(success.IsWarning);
        Assert.Equal(repo.AgentTip, Rev(repo.Path, "main"));

        // A LOCAL merge is a fast-forward of the user's own checkout, so it says so — and it must not
        // borrow the external origin's "merged upstream" wording, which would claim a host round trip
        // that never happened. The two origins are told apart in both directions, not just one.
        Assert.DoesNotContain("upstream", success.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull request", success.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Merged agent/x into main", success.Message, StringComparison.Ordinal);
        Assert.Equal("Merged agent/x into main.", success.Message);
    }

    // ---- helpers ---------------------------------------------------------

    private IOperationJournal NewJournal()
    {
        var dbPath = Path.Combine(NewDir("mainguard-mergepath-db-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory())
        {
            db.Database.EnsureCreated();
        }

        return new OperationJournal(factory);
    }

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private static void Git(string repo, params string[] args) => AgentTestGit.RunChecked(repo, args);

    private static string Rev(string repo, string reference)
        => AgentTestGit.Run(repo, "rev-parse", "--verify", reference).Out.Trim();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}
