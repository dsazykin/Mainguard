using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-06 worktree + quarantine-remote tests on the <see cref="DualRepoFixture"/>: add/remove/
/// prune round-trip, duplicate-id + dirty-remove typed failures, quarantine-only remotes, the
/// agent-push-lands-in-bare invariant, the byte-identical Windows↔VM round-trip, the pnpm hook,
/// and the SC-2 resolved-name path.
/// </summary>
public sealed class AgentWorktreeManagerTests
{
    // MG-17: the worktree is bind-mounted READ-WRITE into a jail whose host uid/gid is 101000 (the
    // userns remap), not the daemon's 1000. A checkout laid down under the daemon's 022 umask is 0644
    // files / 0755 dirs — readable by the agent and NOT editable, which is the product silently broken.
    [LinuxOnlyFact]
    // The attribute already skips on Windows; the annotation is what tells the CA1416 analyzer so.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Worktree_Checkout_IsGroupWritableForTheRemappedJail()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        var root = File.GetUnixFileMode(path);
        Assert.True(root.HasFlag(UnixFileMode.GroupWrite), "the worktree root must be group-writable");
        // setgid so files the agent creates keep the shared jail group (the daemon has to read them).
        Assert.True(root.HasFlag(UnixFileMode.SetGroup), "the worktree root must be setgid");

        // Every checked-out file, not just the root — this is the one the agent actually edits.
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git", System.StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.True(File.GetUnixFileMode(file).HasFlag(UnixFileMode.GroupWrite), file + " must be group-writable");
        }
    }

    [Fact]
    public void Worktree_AddRemovePrune_RoundTrip()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        Assert.True(Directory.Exists(path));

        // Listed via the porcelain parser; the agent branch exists.
        var listed = env.Worktrees.List(hash);
        Assert.Contains(listed, w => w.Branch == "agent/a1");
        Assert.Equal("commit",
            AgentTestGit.RunChecked(env.BarePath(hash), "cat-file", "-t", "refs/heads/agent/a1").Trim());

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false);
        env.Worktrees.Prune(hash);

        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(env.Worktrees.List(hash), w => w.Branch == "agent/a1");
        // The agent branch is gone (no residue).
        Assert.NotEqual(0, AgentTestGit.Run(env.BarePath(hash), "rev-parse", "--verify", "--quiet", "refs/heads/agent/a1").Code);
    }

    [Fact]
    public void Worktree_DuplicateAgentId_ThrowsTyped_NoResidue()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var before = env.Worktrees.List(hash).Count;

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.CreateAgentWorktree(hash, "a1"));

        // No new worktree/branch left behind by the refused call.
        Assert.Equal(before, env.Worktrees.List(hash).Count);
    }

    [Fact]
    public void Worktree_DirtyRemove_ForceSemantics()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        // Make the worktree dirty.
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted\n");

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false));
        Assert.True(Directory.Exists(path)); // refused, still there

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(Directory.Exists(path)); // force cleans
    }

    // MG-3: the quarantine remote is now the agent's OWN repository, not the shared mirror. That is the
    // whole point — `git push origin` has to keep working (LLM CLIs push reflexively) while the mirror
    // stops being something the agent talks to at all.
    // ================= resume: adopting an existing agent/<id> =================

    /// <summary>
    /// <b>The point of the whole resume path.</b> A jail dies (daemon restart, VM stop, crash) leaving its
    /// worktree and per-agent repository on disk and its commits on <c>agent/&lt;id&gt;</c> in the mirror.
    /// Adopting must put a NEW worktree on that same branch with those commits present — not a fresh
    /// branch off main under the same name, which is what <c>worktree add -b</c> would produce and what a
    /// human would have no way to notice.
    /// </summary>
    [Fact]
    public void Adopt_StartsOnTheExistingBranch_WithItsCommitsIntact()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var first = env.Worktrees.CreateAgentWorktree(hash, "a1");
        // The mirror's own default branch, whatever it is called — the base the worktree was created off.
        var mainSha = AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "HEAD").Trim();
        var tip = CommitInWorktree(first, "work.txt", "agent work", "feat: the work being resumed");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/a1").Trim());

        // The dead jail's residue: worktree + per-agent repo still on disk, container gone. This is the
        // state a crash leaves; a clean stop reaches the same branch by a different route (it may reap,
        // and declines to because this branch carries a commit).
        env.Worktrees.RemoveAgentWorktreeKeepingBranch(hash, "a1");
        Assert.False(Directory.Exists(first));
        Assert.Equal("commit", AgentTestGit.RunChecked(bare, "cat-file", "-t", "refs/heads/agent/a1").Trim());

        var adopted = env.Worktrees.AdoptAgentWorktree(hash, "a1");

        // Same branch, same tip, and the file the previous jail committed is on disk in the new worktree.
        Assert.Equal("agent/a1", AgentTestGit.RunChecked(adopted, "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(tip, AgentTestGit.RunChecked(adopted, "rev-parse", "HEAD").Trim());
        Assert.NotEqual(mainSha, tip); // the fixture must really carry work, or this measures nothing
        Assert.Equal("agent work", File.ReadAllText(Path.Combine(adopted, "work.txt")));

        // …and it is a normal, fully-configured agent worktree: quarantined to its own repo, and the
        // mirror still at the same tip (the adoption publishes, and that publish is a no-op).
        Assert.Equal(env.Worktrees.AgentRepoPathFor(hash, "a1"),
            AgentTestGit.RunChecked(adopted, "remote", "get-url", "origin").Trim());
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// The honest refusal. With no <c>agent/&lt;id&gt;</c> there is nothing to resume, and the one thing
    /// adoption must never do is silently create the branch — that would report success for an operation
    /// that recovered nothing at all.
    /// </summary>
    [Fact]
    public void Adopt_WithNoSuchBranch_RefusesTyped_AndCreatesNothing()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var ex = Assert.Throws<AgentBranchMissingException>(
            () => env.Worktrees.AdoptAgentWorktree(hash, "ghost"));
        Assert.Equal("agent/ghost", ex.Branch);
        Assert.Equal(hash, ex.RepoHash);

        // No branch invented, no worktree left behind, no per-agent repository.
        Assert.NotEqual(0, AgentTestGit.Run(bare, "rev-parse", "--verify", "--quiet", "refs/heads/agent/ghost").Code);
        Assert.False(Directory.Exists(env.Worktrees.WorktreePathFor(hash, "ghost")));
        Assert.DoesNotContain(env.Worktrees.List(hash), w => w.Branch == "agent/ghost");
    }

    /// <summary>
    /// The adoption's first act is a rescue. The MG-3 watcher publishes on its own clock and the
    /// last-publish-on-teardown only runs for a clean stop, so a crashed jail can leave commits in its own
    /// repository that the mirror never saw. The adoption clones the branch FROM the mirror and deletes
    /// that repository, so anything not carried across first would be destroyed by the very operation
    /// invoked to save it.
    /// </summary>
    [Fact]
    public void Adopt_CarriesAcrossCommitsTheMirrorNeverSaw()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var first = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var published = CommitInWorktree(first, "one.txt", "1", "feat: published");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));

        // …and then a commit the daemon never got to publish, exactly as a crash would leave it.
        var unpublished = CommitInWorktree(first, "two.txt", "2", "feat: never published");
        Assert.Equal(published, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        Assert.NotEqual(published, unpublished);

        var adopted = env.Worktrees.AdoptAgentWorktree(hash, "a1");

        Assert.Equal(unpublished, AgentTestGit.RunChecked(adopted, "rev-parse", "HEAD").Trim());
        Assert.Equal(unpublished, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        Assert.Equal("2", File.ReadAllText(Path.Combine(adopted, "two.txt")));
    }

    /// <summary>
    /// ...and when that rescue FAILS, the adoption must refuse rather than proceed.
    ///
    /// <para>The rescue publish's outcome used to be discarded, three lines before
    /// <c>ClearWorktreeResidue</c> deletes the only copy of the unpublished commits.
    /// <c>OnPublishOutcome</c> returns early unless <c>result.Refused</c>, so a <c>Failed</c> outcome —
    /// defined as "git itself failed (unreadable repo, races, disk)", i.e. exactly the transient case the
    /// rescue exists for — reached nothing at all: no log line, no audit event, no failed call, and the
    /// agent's last commits deleted a moment later.</para>
    ///
    /// <para>Modelled here with a stale <c>.lock</c> on the mirror's own ref, which is what a crashed or
    /// concurrent git leaves behind and is the exact failure class this application exists to prevent. The
    /// unpublished work must still be on disk afterwards, so a retry once the lock clears recovers it.</para>
    /// </summary>
    [Fact]
    public void Adopt_WhenTheRescuePublishFails_RefusesTyped_AndKeepsTheUnpublishedWork()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var first = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var published = CommitInWorktree(first, "one.txt", "1", "feat: published");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));

        // The commits the mirror never saw — the asset the rescue exists to save.
        var unpublished = CommitInWorktree(first, "two.txt", "2", "feat: never published");
        Assert.NotEqual(published, unpublished);

        // The crash shape, exactly as Adopt_CarriesAcrossCommitsTheMirrorNeverSaw sets it up: the previous
        // jail's worktree and per-agent repository are still on disk, holding the only copy of `two.txt`.
        var agentRepo = env.Worktrees.AgentRepoPathFor(hash, "a1");

        // A stale lock on refs/heads/agent/a1 in the mirror: the fetch into quarantine still succeeds, so
        // the rescue gets as far as the compare-and-swap and then cannot take the ref.
        var refLock = Path.Combine(bare, "refs", "heads", "agent", "a1.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(refLock)!);
        File.WriteAllText(refLock, string.Empty);

        try
        {
            var ex = Assert.Throws<AgentBranchRescueFailedException>(
                () => env.Worktrees.AdoptAgentWorktree(hash, "a1"));
            Assert.Equal(hash, ex.RepoHash);
            Assert.Equal("a1", ex.AgentId);

            // THE point: the only copy of the unpublished commit is still there, so a retry recovers it.
            Assert.True(Directory.Exists(agentRepo), "the rescue failed, so its source must NOT be deleted");
            Assert.Equal(
                unpublished,
                AgentTestGit.RunChecked(agentRepo, "rev-parse", "--verify", "refs/heads/agent/a1").Trim());
        }
        finally
        {
            File.Delete(refLock);
        }

        // ...and once the lock clears, the ordinary adoption carries the work across as it always did.
        var adopted = env.Worktrees.AdoptAgentWorktree(hash, "a1");
        Assert.Equal(unpublished, AgentTestGit.RunChecked(adopted, "rev-parse", "HEAD").Trim());
        Assert.Equal("2", File.ReadAllText(Path.Combine(adopted, "two.txt")));
    }

    /// <summary>
    /// The two removals differ in exactly one thing, and it is no longer "does it delete the branch": a
    /// teardown may reap a SPENT branch, while a resume's rollback may not reap at all. Both leave a
    /// branch that carries a commit standing — the rollback because it is recovering that work, the
    /// teardown because it would otherwise be destroying it.
    /// </summary>
    [Fact]
    public void NeitherRemoval_TouchesABranchThatCarriesACommit()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var keptPath = env.Worktrees.CreateAgentWorktree(hash, "kept");
        var keptTip = CommitInWorktree(keptPath, "k.txt", "k", "feat: kept");
        env.Worktrees.PublishAgentBranch(hash, "kept");

        var tornDownPath = env.Worktrees.CreateAgentWorktree(hash, "torndown");
        var tornDownTip = CommitInWorktree(tornDownPath, "g.txt", "g", "feat: also work");
        env.Worktrees.PublishAgentBranch(hash, "torndown");

        env.Worktrees.RemoveAgentWorktreeKeepingBranch(hash, "kept");
        env.Worktrees.RemoveAgentWorktree(hash, "torndown", force: true);

        // Both worktrees and both per-agent repositories are gone…
        Assert.False(Directory.Exists(keptPath));
        Assert.False(Directory.Exists(tornDownPath));
        Assert.False(Directory.Exists(env.Worktrees.AgentRepoPathFor(hash, "kept")));
        Assert.False(Directory.Exists(env.Worktrees.AgentRepoPathFor(hash, "torndown")));

        // …and neither took a commit with it.
        Assert.Equal(keptTip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/kept").Trim());
        Assert.Equal(tornDownTip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/torndown").Trim());

        // Which makes both adoptable — i.e. the merge queue's "Resume the entry" affordance can actually
        // give either one a jail again. Under the unconditional `branch -D` the torn-down one could not be
        // resumed, reviewed or merged, and its row went on offering all three.
        env.Worktrees.AdoptAgentWorktree(hash, "kept");
        env.Worktrees.AdoptAgentWorktree(hash, "torndown");
    }

    /// <summary>
    /// <b>F1, the data loss.</b> A worker commits, the commit publishes, verification passes, the row goes
    /// to <c>Verified</c> — and then the worker is stopped, which is the documented end of its lifecycle
    /// (<c>AgentOperatingInstructions.Worker</c> tells it to commit, report and stop). The teardown must
    /// not be what destroys the output of the lifecycle it ends.
    ///
    /// <para>Measured before the fix: <c>rev-parse refs/heads/agent/&lt;id&gt;</c> answered the tip before
    /// the stop and "unknown revision" after it, leaving the commit dangling and gc-eligible while the
    /// queue row still said Verified and still offered Review on a branch that no longer existed.</para>
    /// </summary>
    [Fact]
    public void Teardown_KeepsTheCommitAWorkerJustPublished_BecauseNothingElseNamesIt()
    {
        var audit = new Mainguard.Git.Audit.InMemoryAuditLog();
        var warnings = new List<string>();
        using var env = new WorktreeEnv(audit: audit, warningSink: warnings.Add);
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "w1");
        var tip = CommitInWorktree(path, "work.txt", "the approved work", "feat: the work");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "w1"));
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/w1").Trim());

        env.Worktrees.RemoveAgentWorktree(hash, "w1", force: true);

        // The ref still names the tip…
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/w1").Trim());
        // …and the commit is genuinely still there, not merely a ref pointing at a pruned object.
        Assert.Equal("commit", AgentTestGit.RunChecked(bare, "cat-file", "-t", tip).Trim());
        Assert.Contains("work.txt", AgentTestGit.RunChecked(bare, "show", "--name-only", "--format=", tip));

        // The jail leaves nothing else behind: this is a teardown, not a rollback.
        Assert.False(Directory.Exists(path));
        Assert.False(Directory.Exists(env.Worktrees.AgentRepoPathFor(hash, "w1")));

        // …and a kept branch is never silent. An operator who stops an agent and finds a branch left
        // standing has to be able to find out why, and the row that still offers Review needs the fact on
        // the record rather than only in a log line that dies with the process.
        var kept = Assert.Single(audit.Read(), e => e.Type == WorktreeManager.AgentBranchKeptEvent);
        Assert.Equal(tip, kept.Fields["sha"]);
        Assert.Equal("agent/w1", kept.Fields["branch"]);
        Assert.Equal(nameof(AgentBranchReapOutcome.CarriesWork), kept.Fields["outcome"]);
        Assert.Contains(warnings, w => w.Contains("kept 'agent/w1'"));
    }

    /// <summary>
    /// The other half of the boundary, and the reason "no residue" is not simply deleted: an agent that
    /// never committed leaves a branch that names nothing main does not already have, and THAT is reaped.
    /// This is every coordinator, every failed spawn, and the ~20 dead rows the first end-to-end run left.
    /// </summary>
    [Fact]
    public void Teardown_StillReapsABranchThatNeverLeftTheBase()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "idle");
        // Dirty, but never committed — exactly the state the run report describes, and exactly the state
        // in which there is nothing on the branch to protect.
        File.WriteAllText(Path.Combine(path, "draft.txt"), "uncommitted\n");

        env.Worktrees.RemoveAgentWorktree(hash, "idle", force: true);

        Assert.NotEqual(0, AgentTestGit.Run(bare, "rev-parse", "--verify", "--quiet", "refs/heads/agent/idle").Code);
        Assert.DoesNotContain(env.Worktrees.List(hash), w => w.Branch == "agent/idle");
    }

    /// <summary>
    /// …and the branch becomes reapable again the moment its work reaches main, which is what keeps the
    /// mirror from accumulating a ref per agent forever: a merged branch is spent, and a teardown after
    /// the merge cleans it up with nothing lost.
    /// </summary>
    [Fact]
    public void Teardown_ReapsABranchOnceItsWorkIsContainedInMain()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "merged");
        var tip = CommitInWorktree(path, "m.txt", "m", "feat: merged work");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "merged"));

        // The mirror-side result of a human's `git merge --ff-only agent/merged`.
        var mainRef = "refs/heads/" + AgentTestGit.RunChecked(bare, "symbolic-ref", "--short", "HEAD").Trim();
        AgentTestGit.RunChecked(bare, "update-ref", mainRef, tip);

        env.Worktrees.RemoveAgentWorktree(hash, "merged", force: true);

        Assert.NotEqual(0, AgentTestGit.Run(bare, "rev-parse", "--verify", "--quiet", "refs/heads/agent/merged").Code);
        // The work itself is untouched — it is main's now.
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", mainRef).Trim());
    }

    /// <summary>
    /// A publish the mediator REFUSES (the tip was rewritten, so it is not a fast-forward of the mirror's)
    /// leaves the agent's own repository holding the only copy of the rewritten commits. The teardown used
    /// to delete that repository on the comment's belief that every publish had copied its objects across
    /// — true of every publish but the refused one. The keeping removal clears the worktree and nothing
    /// else, and says so.
    /// </summary>
    [Fact]
    public void ARefusedPublish_KeepsTheAgentsRepository_AndSaysSoInTheAudit()
    {
        var audit = new Mainguard.Git.Audit.InMemoryAuditLog();
        using var env = new WorktreeEnv(audit: audit);
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "amender");
        var first = CommitInWorktree(path, "a.txt", "a", "feat: first cut");
        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.Published,
            env.Worktrees.PublishAgentBranchOutcome(hash, "amender"));

        // The rewrite: same change, new sha. The mirror now holds a tip the agent's branch no longer contains.
        AgentTestGit.RunChecked(path,
            "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local", "-c", "commit.gpgsign=false",
            "commit", "--amend", "-m", "feat: first cut, reworded");
        var amended = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(first, amended);
        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedNonFastForward,
            env.Worktrees.PublishAgentBranchOutcome(hash, "amender"));

        var repo = env.Worktrees.AgentRepoPathFor(hash, "amender");
        env.Worktrees.RemoveAgentWorktreeKeepingRepository(hash, "amender", "the last publish was refused");

        // The worktree is gone; the repository, and the rewritten commit on its branch, are not.
        Assert.False(Directory.Exists(path));
        Assert.True(Directory.Exists(repo));
        Assert.Equal(amended, AgentTestGit.RunChecked(repo, "rev-parse", "--verify", "refs/heads/agent/amender").Trim());
        // The mirror is untouched — still the tip it was allowed to hold.
        Assert.Equal(first, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/amender").Trim());

        var kept = Assert.Single(audit.Read(), e => e.Type == WorktreeManager.AgentRepoKeptEvent);
        Assert.Equal(amended, kept.Fields["sha"]);
        Assert.Equal(repo, kept.Fields["repository"]);

        // The control: the ordinary teardown, on a branch whose publish was current, still removes the repo.
        var other = env.Worktrees.CreateAgentWorktree(hash, "tidy");
        CommitInWorktree(other, "b.txt", "b", "feat: tidy");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "tidy"));
        var otherRepo = env.Worktrees.AgentRepoPathFor(hash, "tidy");
        env.Worktrees.RemoveAgentWorktree(hash, "tidy", force: true);
        Assert.False(Directory.Exists(otherRepo));
    }

    /// <summary>
    /// The conflict hand-back's exception to rule 2: a human handed a parked rebase back to the agent, the
    /// agent finished it, and the rewritten branch must reach the mirror exactly once. Without the mark the
    /// refusal is permanent; with it the rewrite lands, the mark is consumed, and the next rewrite is
    /// refused again.
    /// </summary>
    [Fact]
    public void AHandedBackRewrite_IsPublishedOnce_AndRuleTwoIsBackAfterwards()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "resolver");
        var first = CommitInWorktree(path, "r.txt", "r", "feat: first");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "resolver"));

        static string Rewrite(string worktree, string message)
        {
            AgentTestGit.RunChecked(worktree,
                "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local", "-c", "commit.gpgsign=false",
                "commit", "--amend", "-m", message);
            return AgentTestGit.RunChecked(worktree, "rev-parse", "HEAD").Trim();
        }

        // Rule 2, absolute while nobody has handed anything back.
        var rewritten = Rewrite(path, "feat: first, resolved onto main");
        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedNonFastForward,
            env.Worktrees.PublishAgentBranchOutcome(hash, "resolver"));
        Assert.Equal(first, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/resolver").Trim());

        // The human's grant: one rewrite, consumed by the publish it lets through.
        var granted = new HashSet<string> { "resolver" };
        var consumed = new List<string>();
        env.Worktrees.PermitHandedBackRewrite(
            (_, agent) => granted.Contains(agent),
            (_, agent) => { granted.Remove(agent); consumed.Add(agent); });
        Assert.True(env.Worktrees.HasHandedBackRewritePolicy);

        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.Published,
            env.Worktrees.PublishAgentBranchOutcome(hash, "resolver"));
        Assert.Equal(rewritten, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/resolver").Trim());
        Assert.Equal(new[] { "resolver" }, consumed);

        // Spent: a second rewrite is refused exactly as before the grant.
        Rewrite(path, "feat: first, rewritten again");
        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedNonFastForward,
            env.Worktrees.PublishAgentBranchOutcome(hash, "resolver"));
        Assert.Equal(rewritten, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/resolver").Trim());

        // And a grant for a DIFFERENT agent does not leak: an ungranted rewrite stays refused.
        var other = env.Worktrees.CreateAgentWorktree(hash, "bystander");
        CommitInWorktree(other, "b.txt", "b", "feat: b");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "bystander"));
        Rewrite(other, "feat: b, rewritten");
        Assert.Equal(Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedNonFastForward,
            env.Worktrees.PublishAgentBranchOutcome(hash, "bystander"));
    }

    /// <summary>
    /// The one deletion taken on a caller's word: an intake'd pull request that closed upstream. Deletes a
    /// branch that carries commits — which every other path now refuses — because those commits live in
    /// the pull request they were fetched from and <c>pr-&lt;n&gt;</c> is a reused id.
    /// </summary>
    [Fact]
    public void DiscardAgentBranch_DeletesEvenABranchCarryingWork_AndSaysSoInTheAudit()
    {
        var audit = new Mainguard.Git.Audit.InMemoryAuditLog();
        using var env = new WorktreeEnv(audit: audit);
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "pr-7");
        var tip = CommitInWorktree(path, "p.txt", "p", "feat: the pull request");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "pr-7"));

        env.Worktrees.RemoveAgentWorktree(hash, "pr-7", force: true);
        // The ordinary teardown kept it — this is the state the discard has to be able to clear.
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/pr-7").Trim());

        Assert.True(env.Worktrees.DiscardAgentBranch(hash, "pr-7"));
        Assert.NotEqual(0, AgentTestGit.Run(bare, "rev-parse", "--verify", "--quiet", "refs/heads/agent/pr-7").Code);

        var record = Assert.Single(audit.Read(), e => e.Type == WorktreeManager.AgentBranchDiscardedEvent);
        Assert.Equal(tip, record.Fields["sha"]);
        Assert.Equal("agent/pr-7", record.Fields["branch"]);

        // Idempotent: asking again for a branch that is already gone is success, not a failure to retry.
        Assert.True(env.Worktrees.DiscardAgentBranch(hash, "pr-7"));
    }

    /// <summary>
    /// A teardown that could NOT establish the ancestry keeps the branch. An unanswerable safety question
    /// is not a yes — and the failure mode being prevented is a probe that errors and reads as "already
    /// contained in main", which deletes on exactly the repositories git is unhappy with.
    /// </summary>
    [Fact]
    public void Teardown_KeepsTheBranch_WhenTheAncestryProbeCannotAnswer()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var path = env.Worktrees.CreateAgentWorktree(hash, "u1");
        var tip = CommitInWorktree(path, "u.txt", "u", "feat: unanswerable");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "u1"));

        // The mirror's HEAD now names a branch that does not exist, so "what does main contain" has no
        // answer at all. The branch's own commit is untouched and still worth protecting.
        AgentTestGit.RunChecked(bare, "symbolic-ref", "HEAD", "refs/heads/no-such-branch");

        var verdict = env.Worktrees.RefMediator.MayReap(hash, "u1");
        Assert.Equal(AgentBranchReapOutcome.Undecidable, verdict.Outcome);
        Assert.False(verdict.MayDelete);

        env.Worktrees.RemoveAgentWorktree(hash, "u1", force: true);
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "--verify", "refs/heads/agent/u1").Trim());
    }

    /// <summary>Commits a file in an agent worktree the way the agent's CLI would, and returns the tip.</summary>
    private static string CommitInWorktree(string worktreePath, string relPath, string content, string message)
    {
        var full = Path.Combine(worktreePath, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        AgentTestGit.RunChecked(worktreePath, "add", "-A");
        AgentTestGit.RunChecked(worktreePath,
            "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local", "-c", "commit.gpgsign=false",
            "commit", "-m", message);
        return AgentTestGit.RunChecked(worktreePath, "rev-parse", "HEAD").Trim();
    }

    [Fact]
    public void QuarantineRemote_IsExactlyTheAgentsOwnRepo_NeverTheSharedMirror()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        var agentRepo = env.Worktrees.AgentRepoPathFor(hash, "a1");

        // Exactly one configured remote, named origin, pointing at the agent's own repository.
        var remotes = AgentTestGit.RunChecked(path, "remote").Trim()
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "origin" }, remotes);

        var originUrl = AgentTestGit.RunChecked(path, "remote", "get-url", "origin").Trim();
        Assert.Equal(agentRepo, originUrl);

        // NOT the shared mirror (MG-3), not the user's real remote, not the fixture's work repo.
        Assert.NotEqual(bare, originUrl);
        Assert.NotEqual(env.Fixture.WorkRepoPath, originUrl);
        Assert.NotEqual(env.Fixture.BareMirrorPath, originUrl);

        // The mirror itself still denies rewrites/deletes over receive-pack (defence in depth; after
        // stage 3 it is no longer the load-bearing control).
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyNonFastForwards").Trim());
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyDeletes").Trim());
    }

    /// <summary>
    /// MG-3 — the per-agent repository borrows the mirror's objects instead of copying them. A
    /// three-commit seed repo produces an agent repo whose OWN object store is empty (one
    /// <c>objects/info/alternates</c> file and nothing else), and the whole directory is a few tens of
    /// kilobytes — the sample hooks and config, not history.
    /// </summary>
    [Fact]
    public void AgentRepo_BorrowsObjectsThroughAlternates_NeverCopiesHistory()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var agentRepo = env.Worktrees.AgentRepoPathFor(hash, "a1");
        var bare = env.BarePath(hash);

        var alternates = Path.Combine(agentRepo, "objects", "info", "alternates");
        Assert.True(File.Exists(alternates), "the per-agent repo must carry objects/info/alternates");
        Assert.Equal(Path.Combine(bare, "objects"), File.ReadAllText(alternates).Trim());

        // No pack and no loose object of its own — every object of the history resolves through the
        // alternate. (The alternates file itself is the only thing under objects/.)
        var ownObjects = Directory
            .EnumerateFiles(Path.Combine(agentRepo, "objects"), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "alternates")
            .ToArray();
        Assert.Empty(ownObjects);

        // …and history is nonetheless fully readable from it.
        Assert.Equal("commit", AgentTestGit.RunChecked(agentRepo, "cat-file", "-t", "HEAD").Trim());
    }

    // MG-3: an agent's push lands in its OWN repo. Reaching the mirror is a separate, daemon-driven
    // step that names both refs itself — so this test is also the proof that the agent cannot move the
    // mirror's ref by pushing.
    [Fact]
    public void AgentPush_LandsInItsOwnRepo_AndOnlyTheDaemonPublishesToTheMirror()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        // The fixture's separate mirror stands in for the user's real remote.
        var upstreamBefore = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);
        var mirrorBefore = AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim();

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "from-agent\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent work");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");
        var agentSha = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();

        // The push moved the AGENT's own ref...
        var agentRepo = env.Worktrees.AgentRepoPathFor(hash, "a1");
        Assert.Equal(agentSha, AgentTestGit.RunChecked(agentRepo, "rev-parse", "refs/heads/agent/a1").Trim());

        // ...and the mirror has NOT moved: nothing the agent did reached it.
        Assert.Equal(mirrorBefore, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());

        // Only the daemon's publish carries it across — the merge queue's input contract, unchanged.
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));
        Assert.Equal(agentSha, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        // The mirror holds the objects itself (it borrows from nobody), so deleting the agent repo
        // later cannot strand the commit its ref names.
        Assert.Equal("commit", AgentTestGit.RunChecked(bare, "cat-file", "-t", agentSha).Trim());

        // The "real remote" is completely untouched throughout.
        var upstreamAfter = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);
        Assert.Equal(upstreamBefore, upstreamAfter);
    }

    [Fact]
    public void WindowsVm_CommitRoundTrip_ByteIdentical()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var content = "round-trip payload\n";

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), content);
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent round trip");
        var agentSha = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");
        // MG-3: the daemon carries agent/<id> from the agent's own repo into the mirror the Windows
        // side syncs from. Without it the agent's work never leaves its own writable space.
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));

        // Windows side: register the SC-2-resolved sync remote and fetch + merge the agent branch.
        var remote = env.Env.ResolveSyncRemote(hash);
        AgentTestGit.Run(env.Fixture.WorkRepoPath, "remote", "remove", remote.Name); // idempotent
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        // The merged commit is byte-identical (same SHA), and the blob matches.
        var windowsSha = AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(agentSha, windowsSha);
        Assert.Equal(content, File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    [Fact]
    public void SyncRemote_NameIsResolvedNotHardcoded_RoundTripUsesCloudName()
    {
        using var env = new WorktreeEnv(syncRemoteName: "mainguard-cloud");
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        var remote = env.Env.ResolveSyncRemote(hash);
        Assert.Equal("mainguard-cloud", remote.Name); // the resolved name, not a hardcoded mainguard-vm

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "cloud\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "cloud round trip");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));

        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        Assert.Equal("cloud\n", File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    // MG-1: the bare mirror is bind-mounted into the jail read-write, so a hostile agent can plant
    // hooks in <bare>/hooks or redirect core.hooksPath in <bare>/config. The daemon then runs git
    // against that directory OUTSIDE the jail (worktree add/remove/prune, branch -D, status). Every
    // daemon-side git must neutralize hooks/fsmonitor so none of those planted hooks ever execute.
    [Fact]
    public void DaemonGit_NeverRunsHooks_PlantedByAgentInBareMirror()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var sentinel = Path.Combine(Path.GetTempPath(), "mg1-hook-fired-" + System.Guid.NewGuid().ToString("N"));

        // (1) Hooks in the default <bare>/hooks dir, covering ref-update and checkout events.
        PlantHook(Path.Combine(bare, "hooks"), "reference-transaction", sentinel);
        PlantHook(Path.Combine(bare, "hooks"), "post-checkout", sentinel);
        PlantHook(Path.Combine(bare, "hooks"), "post-index-change", sentinel);

        // (2) A core.hooksPath redirect written into the agent-writable config, pointing at a hostile
        //     dir — proves the command-line override wins over repo-local config.
        var evilHooks = Path.Combine(bare, "evil-hooks");
        PlantHook(evilHooks, "reference-transaction", sentinel);
        PlantHook(evilHooks, "post-checkout", sentinel);
        AgentTestGit.RunChecked(bare, "config", "core.hooksPath", evilHooks);

        // Exercise every daemon-side operation that updates refs or checks out a tree.
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");   // ref create + checkout
        File.WriteAllText(Path.Combine(path, "x.txt"), "x\n");
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true); // ref delete + worktree remove
        env.Worktrees.Prune(hash);                                  // prune

        Assert.False(
            File.Exists(sentinel),
            "a hook planted in the agent-writable bare mirror was executed by daemon-side git (MG-1)");
    }

    // Control: with hooks NEUTRALIZED, the same operations still succeed end-to-end.
    [Fact]
    public void DaemonGit_WithNeutralizedHooks_StillCompletesWorktreeLifecycle()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        // A reference-transaction hook that ABORTS (exit 1) would break every ref update if it ran.
        var hooksDir = Path.Combine(bare, "hooks");
        var abortHook = Path.Combine(hooksDir, "reference-transaction");
        Directory.CreateDirectory(hooksDir);
        File.WriteAllText(abortHook, "#!/bin/sh\nexit 1\n");
        MakeExecutable(abortHook);

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1"); // would fail if the hook fired
        Assert.True(Directory.Exists(path));
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(Directory.Exists(path));
    }

    private static void PlantHook(string hooksDir, string hookName, string sentinelPath)
    {
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, hookName);
        // Touch a sentinel the instant the hook runs. Cross-platform: git (incl. Git for Windows)
        // runs hooks through /bin/sh.
        File.WriteAllText(hookPath, "#!/bin/sh\ntouch \"" + sentinelPath.Replace("\\", "/") + "\"\nexit 0\n");
        MakeExecutable(hookPath);
    }

    private static void MakeExecutable(string filePath)
    {
        if (!System.OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    [Fact]
    public void Pnpm_InstallFailure_NonFatal_WorktreeStillCreated()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            // Seed a lockfile so the pnpm hook fires.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");

            var warnings = new List<string>();
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(
                vmRoot,
                pnpmRunner: _ => (1, "boom"),      // simulate failure
                warningSink: warnings.Add);

            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            var path = worktrees.CreateAgentWorktree(hash, "a1");

            Assert.True(Directory.Exists(path));                    // still created
            Assert.Contains(warnings, w => w.Contains("pnpm"));     // warning surfaced
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Pnpm_Install_RunsOnlyWhenLockfilePresent()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            string? ranIn = null;
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(vmRoot, pnpmRunner: dir => { ranIn = dir; return (0, string.Empty); });

            // No lockfile in the seed repo → the hook does not fire.
            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            worktrees.CreateAgentWorktree(hash, "a1");
            Assert.Null(ranIn);

            // Commit a lockfile, re-provision (incremental fetch), then a new worktree fires the hook.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");
            provisioner.Provision(fixture.WorkRepoPath);
            var path = worktrees.CreateAgentWorktree(hash, "a2");

            Assert.Equal(path, ranIn); // issued, in the a2 worktree
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    /// <summary>A test substrate facade: real provisioner + worktree manager, resolvable remote name.</summary>
    private sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly string _syncRemoteName;
        private readonly RepoProvisioner _provisioner;

        public FakeAgentEnvironment(string syncRemoteName, RepoProvisioner provisioner, IAgentWorktreeManager worktrees)
        {
            _syncRemoteName = syncRemoteName;
            _provisioner = provisioner;
            Repos = provisioner;
            Worktrees = worktrees;
        }

        public string SubstrateId => "test";
        public SubstrateCapabilities Capabilities { get; } = new(false, false, "ext4-native", "test");
        public IRepoProvisioner Repos { get; }
        public IAgentWorktreeManager Worktrees { get; }

        // P2-07 seam members: this worktree-only double never touches sandboxes/egress.
        public Mainguard.Agents.Agents.Sandbox.ISandboxEngine Sandboxes =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");
        public Mainguard.Agents.Agents.Sandbox.IEgressPolicy Egress =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");

        // Resolves to the LOCAL bare path (the test's "windows-facing" handle) under the given name.
        public SyncRemote ResolveSyncRemote(string repoHash)
            => new(_syncRemoteName, _provisioner.BareRepoPathFor(repoHash));
    }

    /// <summary>Bundles a fixture + temp VM root + wired services, cleaned up on dispose.</summary>
    private sealed class WorktreeEnv : System.IDisposable
    {
        private readonly string _vmRoot;

        public WorktreeEnv(
            string syncRemoteName = "mainguard-vm",
            Mainguard.Git.Audit.IAuditLog? audit = null,
            System.Action<string>? warningSink = null)
        {
            Fixture = new DualRepoFixture();
            _vmRoot = AgentTestGit.NewVmRoot();
            var provisioner = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot, audit: audit, warningSink: warningSink);
            Env = new FakeAgentEnvironment(syncRemoteName, provisioner, Worktrees);
        }

        public DualRepoFixture Fixture { get; }
        public IAgentEnvironment Env { get; }
        public WorktreeManager Worktrees { get; }

        public string Provision() => Env.Repos.Provision(Fixture.WorkRepoPath).RepoHash;
        public string BarePath(string hash) => Path.Combine(_vmRoot, "repos", hash + ".git");

        public void Dispose()
        {
            Fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
