using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Models;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// §23 — <b>merge identity</b>. A merge decision is a claim about a triple: the agent branch at the sha
/// the evidence was measured on, the main it was authorized to fast-forward, and the main it produces.
/// Every test here asks the same question from a different side: <i>is the thing about to be recorded, or
/// merged, or reconciled, the thing the decision was made about?</i> — and, where the answer cannot be
/// established, that the product refuses rather than acts.
///
/// <para>The RT-D1 boot reconcile (K1) is first because it is the only one that could fabricate a
/// terminal state out of two facts that were never about the lease it was reconciling.</para>
/// </summary>
public class MergeIdentityTests : IDisposable
{
    private readonly List<string> _dirs = new();

    // ---- fixture -------------------------------------------------------------------------------

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private static void Git(string repo, params string[] args)
    {
        var (code, _, err) = GitService.RunGit(repo, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
        }
    }

    private static string Rev(string repo, string reference)
    {
        var (_, output, _) = GitService.RunGit(repo, "rev-parse", "--verify", reference);
        return output.Trim();
    }

    private static void Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", message);
    }

    /// <summary>
    /// A repo on <c>main</c> at a seed commit, with <c>agent/x</c> and <c>agent/y</c> each one commit
    /// ahead of that same seed — i.e. two branches that BOTH fast-forward onto main, and neither of which
    /// contains the other. That is the shape the whole K1 defect lives in.
    /// </summary>
    private (string Path, string Seed) BuildTwoAgentRepo()
    {
        var repo = NewDir("mainguard-mergeid-");
        Git(repo, "-c", "init.defaultBranch=main", "init");
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@mainguard.local");
        Git(repo, "config", "commit.gpgsign", "false");
        Commit(repo, "README.md", "seed\n", "seed");
        var seed = Rev(repo, "main");

        Git(repo, "checkout", "-b", "agent/x");
        Commit(repo, "x.txt", "x work\n", "x commit");

        Git(repo, "checkout", seed);
        Git(repo, "checkout", "-b", "agent/y");
        Commit(repo, "y.txt", "y work\n", "y commit");

        Git(repo, "checkout", "main");
        return (repo, seed);
    }

    private OperationJournal NewJournal()
    {
        var dbPath = Path.Combine(NewDir("mainguard-mergeid-db-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory()) { db.Database.EnsureCreated(); }
        return new OperationJournal(factory);
    }

    /// <summary>A real, journaled <c>--ff-only</c> merge — exactly what the foreground merge writes.</summary>
    private static void JournaledFfMerge(OperationJournal journal, string repo, string branch)
    {
        using (journal.BeginOperation(repo, JournalKinds.Merge, $"Merge {branch}"))
        {
            Git(repo, "merge", "--ff-only", branch);
        }
    }

    private static MergeLeaseRow Lease(string repoHash, string agentId, string expectedMain) => new()
    {
        RepoHash = repoHash,
        LeaseId = Guid.NewGuid().ToString("N"),
        AgentId = agentId,
        ExpectedMainSha = expectedMain,
        MainBranch = "main",
        Confirmed = false,
        BeginUtc = DateTime.UtcNow.AddSeconds(-1),
    };

    private sealed record Reconciled(List<(string Repo, string Agent, string Sha)> Merged,
        List<(string Repo, string Reason)> Interrupted);

    private static Reconciled RunReconcile(
        string repoPath, IMergeLeaseStore leases, IOperationJournal journal, MergeLeaseRow lease)
    {
        var merged = new List<(string, string, string)>();
        var interrupted = new List<(string, string)>();
        var task = new MergeReconcileTask(
            leases, journal,
            resolveRepoPath: _ => repoPath,
            onMerged: (h, a, s) => merged.Add((h, a, s)),
            onInterrupted: (h, r) => interrupted.Add((h, r)));
        task.Reconcile(lease);
        return new Reconciled(merged, interrupted);
    }

    // ---- K1: the boot reconcile may not synthesize a merge from a coincidence -------------------

    /// <summary>
    /// <b>The headline K1 test.</b> A merge of a DIFFERENT agent's branch satisfies every condition the
    /// old reconcile checked, and every condition a naive tightening would check: main advanced past the
    /// lease's expected sha, and the T-19 journal holds a <c>Merge</c> entry — one written <i>after</i>
    /// this lease was taken, whose own snapshots record main moving from exactly this lease's expected
    /// sha to exactly the sha main holds now. Only the branch identity separates the two merges.
    ///
    /// <para>The old predicate (<c>advanced &amp;&amp; any Merge entry anywhere in the repo</c>) walked
    /// <c>agent/x</c>'s lease to terminal <c>Merged</c> on <c>agent/y</c>'s merge, confirmed the
    /// idempotency record so nothing could ever revisit it, and fired <c>NotifyMainMoved</c> at every
    /// co-tenant for a merge of x that never happened.</para>
    /// </summary>
    [Fact]
    public void ART_D1_Reconcile_DoesNotMarkALeaseMerged_WhenItWasADifferentBranchThatMerged()
    {
        var (repo, seed) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();
        var lease = leases.TryBegin("repohash", "lease-x", "x", seed, "main")!;
        Assert.NotNull(lease);

        // Somebody else's branch merges, journaled exactly as the foreground merge journals it.
        JournaledFfMerge(journal, repo, "agent/y");
        var afterY = Rev(repo, "main");
        Assert.NotEqual(seed, afterY);

        // The journal entry is a real one, and it passes every non-identity filter: right kind, inside the
        // lease's window, and its snapshots name main moving from the lease's expected sha to now.
        var entry = journal.GetHistory(repo).Single(e => e.Kind == JournalKinds.Merge);
        Assert.True(entry.WhenUtc >= lease.BeginUtc);
        Assert.True(OperationJournal.TryReadRef(entry.PreStateJson, "refs/heads/main", out var before));
        Assert.Equal(seed, before);
        Assert.True(OperationJournal.TryReadRef(entry.PostStateJson, "refs/heads/main", out var after));
        Assert.Equal(afterY, after);

        var outcome = RunReconcile(repo, leases, journal, lease);

        // NOTHING is recorded for x: no synthesized confirm, no cascade.
        Assert.Empty(outcome.Merged);
        Assert.Null(leases.GetOutstanding("repohash")?.PostMergeSha);
        // The lease is handed back rather than stranding the repo, and the human is told the truth —
        // that main moved for some other reason and this merge was NOT recorded.
        Assert.Null(leases.GetOutstanding("repohash"));
        var reason = Assert.Single(outcome.Interrupted).Reason;
        Assert.Contains("NOT", reason, StringComparison.Ordinal);
        Assert.Contains("agent/x", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same coincidence: main moved because a human committed on it (a
    /// <c>git pull</c>, a hotfix), while a <c>Merge</c> entry from an earlier, unrelated merge sits in the
    /// journal. Two facts about a repository, neither about this lease.
    /// </summary>
    [Fact]
    public void ART_D1_Reconcile_DoesNotMarkALeaseMerged_WhenAHandCommitMovedMain()
    {
        var (repo, _) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();

        // History: agent/y really did merge, once, before any of this.
        JournaledFfMerge(journal, repo, "agent/y");
        var mainAtLeaseTime = Rev(repo, "main");

        var lease = leases.TryBegin("repohash", "lease-x", "x", mainAtLeaseTime, "main")!;

        // ...then a human commits straight onto main. Nothing merged.
        Commit(repo, "hotfix.txt", "by hand\n", "hotfix");
        Assert.NotEqual(mainAtLeaseTime, Rev(repo, "main"));
        Assert.Contains(journal.GetHistory(repo), e => e.Kind == JournalKinds.Merge);

        var outcome = RunReconcile(repo, leases, journal, lease);

        Assert.Empty(outcome.Merged);
        Assert.Single(outcome.Interrupted);
    }

    /// <summary>
    /// A main that did not move FORWARD from the sha this lease was authorized against. A fast-forward
    /// merge only ever advances main from its old-OID, so a rewound (or sideways) main cannot be the
    /// effect of this lease's merge, whatever else is true of the repository.
    /// </summary>
    [Fact]
    public void ART_D1_Reconcile_RefusesWhenMainDidNotMoveForwardFromTheShaTheLeaseAuthorized()
    {
        var (repo, seed) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();

        // The lease is authorized against a main one commit ahead of the seed...
        Commit(repo, "base.txt", "base\n", "base");
        var authorized = Rev(repo, "main");
        var lease = leases.TryBegin("repohash", "lease-x", "x", authorized, "main")!;

        // ...and main is then rewound behind it, with a real Merge entry in the journal for good measure
        // (the pre-lease history the old predicate was happy to treat as this lease's evidence).
        Git(repo, "reset", "--hard", seed);
        JournaledFfMerge(journal, repo, "agent/y");
        Git(repo, "reset", "--hard", seed);
        Assert.Equal(seed, Rev(repo, "main"));

        var outcome = RunReconcile(repo, leases, journal, lease);

        Assert.Empty(outcome.Merged);
        Assert.Single(outcome.Interrupted);
    }

    /// <summary>The control: this lease's OWN merge is still reconciled, exactly once.</summary>
    [Fact]
    public void ART_D1_Reconcile_StillSynthesizesTheConfirm_ForTheMergeTheLeaseAuthorized()
    {
        var (repo, seed) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();
        var lease = leases.TryBegin("repohash", "lease-x", "x", seed, "main")!;

        JournaledFfMerge(journal, repo, "agent/x");
        var afterX = Rev(repo, "main");

        var outcome = RunReconcile(repo, leases, journal, lease);

        var one = Assert.Single(outcome.Merged);
        Assert.Equal(("repohash", "x", afterX), one);
        Assert.Empty(outcome.Interrupted);
        Assert.Null(leases.GetOutstanding("repohash")); // confirmed, so no longer outstanding
    }

    /// <summary>
    /// The journal fallback, and its identity. With <c>agent/x</c>'s ref deleted after the merge — the
    /// ordinary post-merge cleanup — git has nothing left to be asked, so the reconcile falls back to the
    /// journal. It still has to be THIS lease's entry: right window, right pre/post main shas, and a
    /// description that names this branch.
    /// </summary>
    [Fact]
    public void ART_D1_Reconcile_FallsBackToTheJournal_OnlyForAnEntryThatNamesThisBranch()
    {
        var (repo, seed) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();
        var lease = leases.TryBegin("repohash", "lease-x", "x", seed, "main")!;

        JournaledFfMerge(journal, repo, "agent/x");
        var afterX = Rev(repo, "main");
        Git(repo, "branch", "-D", "agent/x");
        Assert.Equal(string.Empty, Rev(repo, "refs/heads/agent/x"));

        var outcome = RunReconcile(repo, leases, journal, lease);
        Assert.Equal(("repohash", "x", afterX), Assert.Single(outcome.Merged));

        // And the same shape for a DIFFERENT branch is refused: same window, same pre/post main shas,
        // only the description differs — which is the only thing left that names the branch.
        var (repo2, seed2) = BuildTwoAgentRepo();
        var journal2 = NewJournal();
        var leases2 = new InMemoryMergeLeaseStore();
        var lease2 = leases2.TryBegin("repohash", "lease-x", "x", seed2, "main")!;
        JournaledFfMerge(journal2, repo2, "agent/y");
        Git(repo2, "branch", "-D", "agent/x");

        var outcome2 = RunReconcile(repo2, leases2, journal2, lease2);
        Assert.Empty(outcome2.Merged);
        Assert.Single(outcome2.Interrupted);
    }

    /// <summary>
    /// An unreadable main is not an unchanged main. The reconcile used to read "no sha" as "main did not
    /// move" and report an interrupted attempt as fact; it now says it could not tell.
    /// </summary>
    [Fact]
    public void ART_D1_Reconcile_TreatsAnUnreadableMainAsUndecidable_NotAsNeverCommitted()
    {
        var (repo, seed) = BuildTwoAgentRepo();
        var journal = NewJournal();
        var leases = new InMemoryMergeLeaseStore();
        var lease = leases.TryBegin("repohash", "lease-x", "x", seed, "nosuchbranch")!;

        var outcome = RunReconcile(repo, leases, journal, lease);

        Assert.Empty(outcome.Merged);
        var reason = Assert.Single(outcome.Interrupted).Reason;
        Assert.DoesNotContain("no ref moved", reason, StringComparison.Ordinal);
    }

    // ---- K2: the merge consumes the ref it fetched, not a namesake ------------------------------

    /// <summary>
    /// A repo whose sync remote holds the branch the queue verified, and whose LOCAL <c>agent/x</c> is a
    /// stale copy from an old checkout. The fetch at step (2) is fatal-if-failed for the stated reason
    /// that a local copy is "an unknown-age copy, and merging it would land work the queue never
    /// verified" — and the preference order then merged exactly that copy.
    /// </summary>
    [Fact]
    public void ForegroundMerge_PrefersTheRefItJustFetched_OverAStaleLocalAgentBranch()
    {
        var world = BuildDivergedWorld();

        var result = world.Service.MergeAgentBranch(new ForegroundMergeRequest(
            world.RepoPath, "repohash", "x", world.MainSha, "main"));

        Assert.True(result.Merged);
        // Main is at the MIRROR's tip — the commit the queue verified — not the stale local one.
        Assert.Equal(world.MirrorTip, Rev(world.RepoPath, "main"));
        Assert.NotEqual(world.StaleLocalTip, Rev(world.RepoPath, "main"));
    }

    /// <summary>
    /// With the verified sha recorded on the lease, the source is that sha or nothing. Here the mirror has
    /// moved on since the verification and the stale local copy is not it either: neither ref is the
    /// commit a human was shown a green rail about, so no ref moves.
    /// </summary>
    [Fact]
    public void ForegroundMerge_RefusesWhenNeitherRefIsTheCommitTheQueueVerified()
    {
        var world = BuildDivergedWorld();

        var result = world.Service.MergeAgentBranch(new ForegroundMergeRequest(
            world.RepoPath, "repohash", "x", world.MainSha, "main",
            ExpectedBranchSha: "0123456789012345678901234567890123456789"));

        Assert.False(result.Merged);
        Assert.True(result.CasLost); // the branch moved out from under the evidence → re-verify
        Assert.Contains("not the commit the queue verified", result.Reason ?? "", StringComparison.Ordinal);
        Assert.Equal(world.MainSha, Rev(world.RepoPath, "main")); // nothing landed
    }

    /// <summary>The control: naming the verified sha merges exactly that commit.</summary>
    [Fact]
    public void ForegroundMerge_WithTheVerifiedSha_MergesExactlyThatCommit()
    {
        var world = BuildDivergedWorld();

        var result = world.Service.MergeAgentBranch(new ForegroundMergeRequest(
            world.RepoPath, "repohash", "x", world.MainSha, "main",
            ExpectedBranchSha: world.MirrorTip));

        Assert.True(result.Merged);
        Assert.Equal(world.MirrorTip, Rev(world.RepoPath, "main"));
    }

    private sealed record DivergedWorld(
        string RepoPath, string MainSha, string MirrorTip, string StaleLocalTip,
        ForegroundMergeService Service);

    /// <summary>
    /// The shape the K2 defect needs and nothing else: the user's checkout holds a LOCAL
    /// <c>agent/x</c> at an old commit, and the sync remote's mirror holds <c>agent/x</c> one commit
    /// further on. Both fast-forward main, so <c>--ff-only</c> is happy with either — which is exactly why
    /// it cannot be the thing that decides.
    /// </summary>
    private DivergedWorld BuildDivergedWorld()
    {
        var repo = NewDir("mainguard-mergeid-fg-");
        Git(repo, "-c", "init.defaultBranch=main", "init");
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@mainguard.local");
        Git(repo, "config", "commit.gpgsign", "false");
        Commit(repo, "README.md", "seed\n", "seed");
        var mainSha = Rev(repo, "main");

        Git(repo, "checkout", "-b", "agent/x");
        Commit(repo, "a.txt", "first\n", "agent commit 1");
        var staleLocalTip = Rev(repo, "agent/x");
        Commit(repo, "b.txt", "second\n", "agent commit 2");
        var mirrorTip = Rev(repo, "agent/x");

        // The mirror gets the NEWER tip; the checkout's local branch is rewound to the older one.
        var bare = NewDir("mainguard-mergeid-bare-");
        Git(bare, "init", "--bare");
        Git(repo, "remote", "add", "mainguard-vm", bare);
        Git(repo, "push", "mainguard-vm", "agent/x");
        Git(repo, "checkout", "main");
        Git(repo, "branch", "-f", "agent/x", staleLocalTip);
        Assert.Equal(staleLocalTip, Rev(repo, "refs/heads/agent/x"));

        var dbPath = Path.Combine(NewDir("mainguard-mergeid-fgdb-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory()) { db.Database.EnsureCreated(); }
        var service = new ForegroundMergeService(
            resolveSyncRemote: _ => new Mainguard.Agents.Agents.SyncRemote("mainguard-vm", bare),
            journal: new OperationJournal(factory),
            leases: new InMemoryMergeLeaseStore());

        return new DivergedWorld(repo, mainSha, mirrorTip, staleLocalTip, service);
    }

    // ---- K6: the guard re-reads the state it decided from ---------------------------------------

    private sealed class ActiveToken : IYieldToken
    {
        public string AgentId => "a1";
        public bool IsActive => true;
        public YieldOutcome Outcome => YieldOutcome.ByReady;
        public void Resume() { }
        public void Dispose() { }
    }

    /// <summary>
    /// The guard's verdict is a SNAPSHOT, and <c>RunGuarded</c> then waits out an <c>index.lock</c>
    /// backoff before acting. The three preconditions the verdict was made of — mid-rebase, detached HEAD,
    /// an in-progress merge — are exactly the states a worktree enters while a lock is held, and they were
    /// never looked at again. The action must not run against a worktree the guard no longer allows.
    /// </summary>
    [Fact]
    public void RunGuarded_RefusesWhenTheStateItDecidedFrom_ChangedDuringTheLockBackoff()
    {
        var probes = 0;
        var actionRuns = 0;

        // Held for two probes, then clear — the ordinary transient-lock shape.
        bool IsLockHeld() => ++probes <= 2;

        var ex = Assert.Throws<GitMutationStateChangedException>(() => GitMutationGuard.RunGuarded(
            new ActiveToken(),
            IsLockHeld,
            action: () => { actionRuns++; return 0; },
            sleep: _ => { },
            // The agent started its own rebase while we backed off.
            recheck: () => GitMutationGuard.CanMutate(
                new GitDirState(RebaseInProgress: true, DetachedHead: false, MergeInProgress: false))));

        Assert.Equal(0, actionRuns);
        Assert.Contains("no longer the worktree the guard allowed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mid-rebase", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the thing that makes the test above measure the re-check rather than a guard that
    /// refuses everything: an unchanged worktree still runs the action, once.
    /// </summary>
    [Fact]
    public void RunGuarded_StillRunsTheAction_WhenTheStateIsUnchanged()
    {
        var actionRuns = 0;
        var rechecks = 0;

        var result = GitMutationGuard.RunGuarded(
            new ActiveToken(),
            isLockHeld: () => false,
            action: () => { actionRuns++; return 7; },
            sleep: _ => { },
            recheck: () => { rechecks++; return MutationVerdict.Allowed; });

        Assert.Equal(7, result);
        Assert.Equal(1, actionRuns);
        Assert.Equal(1, rechecks); // read once, at the moment of action
    }

    /// <summary>
    /// A lock that never clears must not reach the re-check at all: nothing was attempted, so there is no
    /// "state it decided from" question to answer, and the failure must stay the lock failure a caller
    /// already knows how to read.
    /// </summary>
    [Fact]
    public void RunGuarded_APersistentLock_StillThrowsTheLockException_NotTheStateOne()
    {
        var rechecks = 0;

        Assert.Throws<GitMutationLockException>(() => GitMutationGuard.RunGuarded(
            new ActiveToken(),
            isLockHeld: () => true,
            action: () => 0,
            sleep: _ => { },
            recheck: () => { rechecks++; return MutationVerdict.Allowed; }));

        Assert.Equal(0, rechecks);
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
