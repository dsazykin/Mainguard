using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-3 stage 2 — the daemon-side mediated ref update: the only path by which anything an agent
/// produced reaches the shared mirror. Everything here runs against real git.
///
/// <para>The design's four rules (§4) are asserted as behaviour, not as configuration: the destination
/// is this agent's branch, updates are fast-forward only, an absent source is never a delete, and the
/// integration branch is never a valid target.</para>
/// </summary>
public sealed class AgentRefMediationTests
{
    /// <summary>
    /// How long <see cref="Watcher_TheBackgroundLoopReallySweeps_WithNobodyDrivingIt"/> waits for the
    /// loop's publish before declaring that it never ran. Deliberately far larger than the sweep it is
    /// waiting on: this is a failure deadline, not a sleep, so nothing is spent when the loop works and
    /// contention only makes a passing run slower, never red. If it ever fires, the loop is dead.
    /// </summary>
    private static readonly TimeSpan LoopDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public void Publish_FastForward_MovesTheMirrorsRef_AndIsIdempotent()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        var baseSha = AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim();
        var tip = env.CommitInWorktree(worktree, "one.txt", "one\n");

        var first = env.Worktrees.Publish(hash, "a1");
        Assert.Equal(AgentRefPublishOutcome.Published, first.Outcome);
        Assert.Equal(baseSha, first.OldSha);
        Assert.Equal(tip, first.NewSha);
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());

        // A second publish with nothing new is Unchanged — not a no-op that quietly reports success,
        // and not a second ref move.
        var second = env.Worktrees.Publish(hash, "a1");
        Assert.Equal(AgentRefPublishOutcome.Unchanged, second.Outcome);
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// Rule 2. An agent that rewrites history it already published (amend, hard reset, force-push into
    /// its own repo — all of which succeed inside its own writable space) cannot make the mirror follow.
    /// </summary>
    [Fact]
    public void Publish_NonFastForward_IsRefused_AndTheMirrorsRefDoesNotMove()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        var published = env.CommitInWorktree(worktree, "one.txt", "one\n");
        Assert.Equal(AgentRefPublishOutcome.Published, env.Worktrees.Publish(hash, "a1").Outcome);

        // The agent rewrites the commit it already published, and pushes it to its own repo.
        AgentTestGit.RunChecked(worktree, "reset", "--hard", "HEAD~1");
        var rewritten = env.CommitInWorktree(worktree, "one.txt", "rewritten\n");
        Assert.NotEqual(published, rewritten);
        Assert.Equal(rewritten,
            AgentTestGit.RunChecked(env.AgentRepoPath(hash, "a1"), "rev-parse", "refs/heads/agent/a1").Trim());

        var result = env.Worktrees.Publish(hash, "a1");

        Assert.Equal(AgentRefPublishOutcome.RefusedNonFastForward, result.Outcome);
        Assert.Contains("rewrote published history", result.Reason);
        // The mirror — the merge queue's input — still holds exactly what it held before.
        Assert.Equal(published, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        // …and the refusal reached the warning sink rather than passing silently.
        Assert.Contains(env.Warnings, w => w.Contains("MG-3", StringComparison.Ordinal) && w.Contains("refused"));
        // …and left a durable G-17 record. A log line is not enough for an event that means an agent
        // tried to rewrite history the mirror had already published; the whole finding was a control
        // that looked applied and was not.
        var audited = Assert.Single(
            env.Audit.Read(), e => e.Type == WorktreeManager.AgentRefRefusedEvent);
        Assert.Equal("a1", audited.Fields["agent"]);
        Assert.Equal(nameof(AgentRefPublishOutcome.RefusedNonFastForward), audited.Fields["outcome"]);
        Assert.Equal(published, audited.Fields["old"]);
        Assert.Equal(rewritten, audited.Fields["new"]);
    }

    /// <summary>
    /// Rule 3. Deleting the branch inside its own repository — or destroying the repository outright —
    /// must not delete the mirror's copy. The mirror's <c>agent/&lt;id&gt;</c> is what the merge queue,
    /// the review cockpit and the host repo's sync fetch all read.
    /// </summary>
    [Fact]
    public void Publish_WhenTheAgentDeletesItsOwnBranch_IsNeverReadAsADelete()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        var published = env.CommitInWorktree(worktree, "one.txt", "one\n");
        Assert.Equal(AgentRefPublishOutcome.Published, env.Worktrees.Publish(hash, "a1").Outcome);

        // Delete the ref in the agent's own repository (it owns it; this is allowed there).
        var agentRepo = env.AgentRepoPath(hash, "a1");
        AgentTestGit.RunChecked(worktree, "checkout", "--detach");
        AgentTestGit.RunChecked(agentRepo, "update-ref", "-d", "refs/heads/agent/a1");

        var afterDelete = env.Worktrees.Publish(hash, "a1");
        Assert.Equal(AgentRefPublishOutcome.NothingToPublish, afterDelete.Outcome);
        Assert.Equal(published, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());

        // The stronger case: the whole per-agent repository is gone.
        env.AgentRepos.Remove(hash, "a1");
        var afterWipe = env.Worktrees.Publish(hash, "a1");
        Assert.Equal(AgentRefPublishOutcome.NothingToPublish, afterWipe.Outcome);
        Assert.Equal(published, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// Rules 1 and 4, at the level where they can actually be violated: the destination is computed from
    /// the agent id, so the only way to aim it somewhere else is to hand it an id that is not an id. The
    /// integration branch is refused against the mirror's OWN HEAD, so a repo whose default is
    /// <c>master</c> (or anything else) is covered without naming a literal.
    /// </summary>
    [Fact]
    public void Publish_CannotBeAimedAtAnythingButThatAgentsOwnBranch()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        var mainBranch = AgentTestGit.RunChecked(bare, "symbolic-ref", "--short", "HEAD").Trim();
        var mainBefore = AgentTestGit.RunChecked(bare, "rev-parse", mainBranch).Trim();

        // Ids that would escape the agent/ namespace or climb out of it are refused outright, and the
        // mediator never reaches git at all.
        foreach (var hostile in new[] { "../main", "a1/../../main", "..", "a1 b" })
        {
            var result = env.Worktrees.Publish(hash, hostile);
            Assert.True(
                result.Outcome == AgentRefPublishOutcome.RefusedTarget,
                $"id '{hostile}' produced {result.Outcome} ({result.Reason})");
        }

        // The mirror's integration branch is untouched by every one of them.
        Assert.Equal(mainBefore, AgentTestGit.RunChecked(bare, "rev-parse", mainBranch).Trim());

        // And an id that IS the default branch's name still only ever produces refs/heads/agent/<id>,
        // which the rule-4 check refuses as a target only when it collides with the integration branch.
        Assert.StartsWith(AgentRepoLayout.RefPrefix, AgentRepoLayout.RefFor(mainBranch), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 4 on its own. Reaching it needs a mirror whose HEAD really is the branch in question, which
    /// is the only way <c>refs/heads/agent/&lt;id&gt;</c> can also be the integration branch — and it is
    /// exactly why the check is written against the mirror's own HEAD rather than the literal "main".
    /// A repository that has adopted an agent branch as its default must not have it advanced by the
    /// agent, because that is the branch the merge queue merges INTO.
    /// </summary>
    [Fact]
    public void Publish_IsRefused_WhenTheTargetWouldBeTheReposOwnIntegrationBranch()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        var before = AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim();

        // The mirror now treats agent/a1 as its default branch.
        AgentTestGit.RunChecked(bare, "symbolic-ref", "HEAD", "refs/heads/agent/a1");
        env.CommitInWorktree(worktree, "one.txt", "one\n");

        var result = env.Worktrees.Publish(hash, "a1");

        Assert.Equal(AgentRefPublishOutcome.RefusedTarget, result.Outcome);
        Assert.Contains("integration branch", result.Reason);
        Assert.Equal(before, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// One agent cannot publish onto another's branch: the destination is a function of the id the
    /// DAEMON passes, and each agent's repository is fetched only for its own ref.
    /// </summary>
    [Fact]
    public void Publish_ForOneAgent_NeverMovesAnotherAgentsBranch()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var wtA = env.Worktrees.CreateAgentWorktree(hash, "a1");
        env.Worktrees.CreateAgentWorktree(hash, "a2");
        var bare = env.BarePath(hash);
        var b2Before = AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a2").Trim();

        // a1 forges a ref named after a2 inside its OWN repository and commits onto it.
        var forged = env.CommitInWorktree(wtA, "forged.txt", "not a2's work\n");
        AgentTestGit.RunChecked(env.AgentRepoPath(hash, "a1"), "update-ref", "refs/heads/agent/a2", forged);

        // Publishing a1 carries a1's branch and nothing else.
        Assert.Equal(AgentRefPublishOutcome.Published, env.Worktrees.Publish(hash, "a1").Outcome);
        Assert.Equal(forged, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        Assert.Equal(b2Before, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a2").Trim());
    }

    /// <summary>The quarantine ref the fetch lands in is daemon-private and is never left behind — a
    /// stray <c>refs/mainguard/incoming/*</c> would keep objects reachable forever and would show up in
    /// anything that enumerates the mirror's refs.</summary>
    [Fact]
    public void Publish_LeavesNoQuarantineRefBehind_OnSuccessOrOnRefusal()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        env.CommitInWorktree(worktree, "one.txt", "one\n");
        Assert.Equal(AgentRefPublishOutcome.Published, env.Worktrees.Publish(hash, "a1").Outcome);
        Assert.Empty(RefsUnder(bare, AgentRefMediator.QuarantineRefPrefix));

        AgentTestGit.RunChecked(worktree, "reset", "--hard", "HEAD~1");
        env.CommitInWorktree(worktree, "one.txt", "rewritten\n");
        Assert.Equal(AgentRefPublishOutcome.RefusedNonFastForward, env.Worktrees.Publish(hash, "a1").Outcome);
        Assert.Empty(RefsUnder(bare, AgentRefMediator.QuarantineRefPrefix));
    }

    // ---- The watcher (design §7: the daemon watches AND re-fetches before verification) -------------

    /// <summary>
    /// A watched agent's commit reaches the mirror on the next sweep, with no verification asked for and
    /// nobody calling publish — which is the whole point of the watcher half: an agent's own
    /// <c>git push</c> stays meaningful.
    ///
    /// <para>Driven through <c>PollOnce</c> rather than by sleeping on the background loop, because a
    /// test that waits "long enough" keeps passing after the loop stops. That premise only became TRUE
    /// with <see cref="AgentRefWatcher.DriveManually"/>: <c>Watch</c> starts the 1 Hz loop, so this test
    /// used to hand-crank a sweep while the watcher swept underneath it, and whichever got there first
    /// consumed the snapshot delta. On an idle box the manual sweep almost always won; under contention
    /// the loop won often enough to redden a run at random, which is worse than a test that never
    /// existed. The loop itself is not left unproven — see
    /// <see cref="Watcher_TheBackgroundLoopReallySweeps_WithNobodyDrivingIt"/>.</para>
    /// </summary>
    [Fact]
    public void Watcher_PublishesWhenTheAgentsRefMoves_AndDoesNothingWhileItIsStill()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        using var watcher = new AgentRefWatcher(
            env.Worktrees.RefMediator, env.AgentRepos, AgentRefWatcher.DriveManually);
        watcher.Watch(hash, "a1");

        // The first sweep publishes the ref as it stands (the snapshot starts empty, so an agent that
        // committed before the watch began is never missed). Asserted as EXACTLY one outcome: `Assert.All`
        // over the empty list is how "the sweep produced nothing at all" used to read as a pass.
        Assert.True(Assert.Single(watcher.PollOnce()).Current);
        // A still agent costs nothing: no outcome at all, because the snapshot did not change.
        Assert.Empty(watcher.PollOnce());

        var tip = env.CommitInWorktree(worktree, "one.txt", "one\n");
        var moved = watcher.PollOnce();
        Assert.Single(moved);
        Assert.Equal(AgentRefPublishOutcome.Published, moved[0].Outcome);
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());

        Assert.Empty(watcher.PollOnce());
        watcher.Unwatch(hash, "a1");
        env.CommitInWorktree(worktree, "two.txt", "two\n");
        Assert.Empty(watcher.PollOnce()); // unwatched: the sweep no longer considers it
    }

    /// <summary>
    /// The background loop really sweeps — the half <see cref="AgentRefWatcher.DriveManually"/> takes out
    /// of every other watcher test. Nothing here calls <c>PollOnce</c>, publishes, or asks for a
    /// verification: a commit is made, <c>Watch</c> is called, and the mirror is expected to catch up on
    /// its own. If <c>Watch</c> stopped starting the loop, or the loop stopped sweeping, this is the test
    /// that goes red.
    ///
    /// <para>The window closes on the EVENT (the mediator's publish observation), not on a clock: the
    /// deadline is only the point at which "the loop never ran" is declared, so a machine under load
    /// takes longer to get there and still passes.</para>
    /// </summary>
    [Fact]
    public void Watcher_TheBackgroundLoopReallySweeps_WithNobodyDrivingIt()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        // Commit before anything is watching, and publish nothing by hand. The mirror is demonstrably
        // NOT already where the assertion wants it, so the assertion cannot pass without the loop.
        var tip = env.CommitInWorktree(worktree, "one.txt", "one\n");
        Assert.NotEqual(tip, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());

        var observed = new BlockingCollection<AgentRefPublishResult>();
        var mediator = new AgentRefMediator(env.AgentRepos, env.BarePath, observed.Add);
        using var watcher = new AgentRefWatcher(mediator, env.AgentRepos, TimeSpan.FromMilliseconds(50));

        watcher.Watch(hash, "a1");

        Assert.True(
            observed.TryTake(out var result, (int)LoopDeadline.TotalMilliseconds),
            "the background sweep loop never published — Watch() did not start it, or it stopped sweeping");
        Assert.Equal(AgentRefPublishOutcome.Published, result.Outcome);
        Assert.Equal(tip, result.NewSha);
        Assert.Equal(tip, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// A watch whose agent repository has gone self-evicts. `SwarmReconciler` disposes an orphan by
    /// calling `RemoveAgentWorktree` directly — it never goes through the launcher, so nothing else
    /// unwatches it. A vanished repo publishes `NothingToPublish`, which is not `Current`, so the
    /// snapshot would never be recorded and the entry would spawn a git process every tick for the life
    /// of the daemon.
    ///
    /// <para>Eviction now takes TWO consecutive absences, so this also pins the half that makes the
    /// guard real: after the first sweep the agent is still watched. A fix that simply stopped evicting
    /// would pass the second half of this test and fail nothing — hence the assertion that it does go,
    /// and that it says so on the way out.</para>
    /// </summary>
    [Fact]
    public void Watcher_DropsAnAgentWhoseRepositoryIsGone_ButOnlyOnACorroboratedAbsence()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var warnings = new List<string>();
        using var watcher = new AgentRefWatcher(
            env.Worktrees.RefMediator, env.AgentRepos, AgentRefWatcher.DriveManually, warnings.Add);
        watcher.Watch(hash, "a1");
        Assert.True(Assert.Single(watcher.PollOnce()).Current);
        Assert.Contains((hash, "a1"), watcher.Watched);

        // Teardown by a path that does not unwatch (the reconciler's).
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);

        // One absence is not enough: a single filesystem answer is exactly what used to be able to
        // unwatch a live agent for good.
        Assert.Empty(watcher.PollOnce());
        Assert.Contains((hash, "a1"), watcher.Watched);
        Assert.Empty(warnings);

        // Corroborated — now it goes, and the eviction is on the record rather than silent.
        Assert.Empty(watcher.PollOnce());
        Assert.DoesNotContain((hash, "a1"), watcher.Watched);
        Assert.Contains(warnings, w => w.Contains("stopped watching agent 'a1'", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The defect this class was carrying.</b> <c>Directory.Exists</c> returns false on ANY error —
    /// permission denied, a transient I/O error, a path the OS rejects — so one bad moment under load
    /// could unwatch a LIVE agent permanently. Every other non-<c>Current</c> outcome is self-correcting
    /// (the snapshot goes unrecorded and the next tick retries); eviction is the one that has no next
    /// tick, which is why it is the only path where an agent's work can silently stop reaching the
    /// mirror between verifications.
    ///
    /// <para>The failure is injected through the presence probe rather than by breaking a real directory,
    /// because it must be an I/O ERROR and not a deletion — the two are the same <c>bool</c> in the code
    /// being fixed, and a test that deletes the directory would prove nothing about the difference. That
    /// the real probe tells them apart is asserted separately, against a real denied directory, in
    /// <see cref="ProbeRepo_TellsAnUnreadableRepositoryApartFromAnAbsentOne"/>.</para>
    /// </summary>
    [Fact]
    public void Watcher_WhenTheRepositoryCannotBeRead_KeepsTheWatch_AndCatchesUpOnceItCan()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var warnings = new List<string>();
        var failing = true;
        using var watcher = new AgentRefWatcher(
            env.Worktrees.RefMediator, env.AgentRepos, AgentRefWatcher.DriveManually, warnings.Add,
            path => failing ? AgentRepoPresence.Unreadable : AgentRefWatcher.ProbeRepo(path));
        watcher.Watch(hash, "a1");

        // The agent is alive and working throughout: it commits while the daemon cannot read its repo.
        var tip = env.CommitInWorktree(worktree, "one.txt", "one\n");

        // Sweeps under the failure publish nothing (there is nothing readable to publish) …
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(watcher.PollOnce());
        }

        // … but the agent is STILL WATCHED. This is the assertion the old code failed on the first tick.
        Assert.Contains((hash, "a1"), watcher.Watched);

        // The condition is reported — once per streak, not once per tick, or a 1 Hz loop would bury the
        // log it is meant to raise. Asserted as the ONE warning and by its content, which together also
        // say that nothing reported an eviction: a "stopped watching" line here would be a second
        // warning, and the only warning is this one.
        Assert.Single(warnings);
        Assert.Contains("could not read agent repository", warnings[0], StringComparison.Ordinal);

        // The watch is not merely present in a dictionary: once the filesystem answers again, the very
        // next sweep carries the commit made during the outage into the mirror.
        failing = false;
        var recovered = Assert.Single(watcher.PollOnce());
        Assert.Equal(AgentRefPublishOutcome.Published, recovered.Outcome);
        Assert.Equal(tip, AgentTestGit.RunChecked(env.BarePath(hash), "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// The probe itself, on a real filesystem: an absent path is <c>Absent</c>, a present one is
    /// <c>Present</c>. Absence must still be established, or the fix would just be "never evict".
    /// </summary>
    [Fact]
    public void ProbeRepo_ReportsAbsence_WhenTheFilesystemReallySaysSo()
    {
        var root = AgentTestGit.NewVmRoot();
        try
        {
            Assert.Equal(AgentRepoPresence.Present, AgentRefWatcher.ProbeRepo(root));
            Assert.Equal(AgentRepoPresence.Absent, AgentRefWatcher.ProbeRepo(Path.Combine(root, "gone.git")));
        }
        finally
        {
            AgentTestGit.DeleteTree(root);
        }
    }

    /// <summary>
    /// The other half, against a directory the OS genuinely refuses to answer about: <c>Unreadable</c>,
    /// NOT <c>Absent</c>. The last assertion is the defect in one line — <c>Directory.Exists</c> answers
    /// the identical situation with the same <c>false</c> it uses for "deleted", which is what made an
    /// I/O error able to evict a live agent.
    /// </summary>
    [RequiresAccessDeniedFact]
    public void ProbeRepo_TellsAnUnreadableRepositoryApartFromAnAbsentOne()
    {
        var root = AgentTestGit.NewVmRoot();
        var agentRepo = Path.Combine(root, "denied", "a1.git");
        try
        {
            Directory.CreateDirectory(agentRepo);
            using (AccessDenialSupport.Deny(Path.Combine(root, "denied")))
            {
                Assert.Equal(AgentRepoPresence.Unreadable, AgentRefWatcher.ProbeRepo(agentRepo));
                Assert.False(
                    Directory.Exists(agentRepo),
                    "Directory.Exists answered TRUE for a denied path, so this run never exercised the "
                    + "collapse the probe exists to avoid.");
            }
        }
        finally
        {
            AgentTestGit.DeleteTree(root);
        }
    }

    /// <summary>
    /// A refusal must stay PENDING. If the watcher recorded the snapshot regardless of the outcome, one
    /// refused tick would make it stop trying — and "it silently gave up" is indistinguishable from
    /// "nothing has changed" in every log the operator can see.
    /// </summary>
    [Fact]
    public void Watcher_DoesNotSwallowARefusal_ItKeepsRetryingUntilTheMirrorCatchesUp()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        using var watcher = new AgentRefWatcher(
            env.Worktrees.RefMediator, env.AgentRepos, AgentRefWatcher.DriveManually);
        watcher.Watch(hash, "a1");
        Assert.True(Assert.Single(watcher.PollOnce()).Current);

        var published = env.CommitInWorktree(worktree, "one.txt", "one\n");
        Assert.Equal(AgentRefPublishOutcome.Published, Assert.Single(watcher.PollOnce()).Outcome);

        // The agent rewrites published history: every sweep from here must keep refusing rather than
        // recording the snapshot and going quiet.
        AgentTestGit.RunChecked(worktree, "reset", "--hard", "HEAD~1");
        env.CommitInWorktree(worktree, "one.txt", "rewritten\n");
        Assert.Equal(AgentRefPublishOutcome.RefusedNonFastForward, Assert.Single(watcher.PollOnce()).Outcome);
        Assert.Equal(AgentRefPublishOutcome.RefusedNonFastForward, Assert.Single(watcher.PollOnce()).Outcome);

        // Once the agent puts the published commit back in its ancestry, the very next sweep succeeds.
        AgentTestGit.RunChecked(worktree, "reset", "--hard", published);
        var recovered = env.CommitInWorktree(worktree, "three.txt", "three\n");
        Assert.Equal(AgentRefPublishOutcome.Published, Assert.Single(watcher.PollOnce()).Outcome);
        Assert.Equal(recovered,
            AgentTestGit.RunChecked(env.BarePath(hash), "rev-parse", "refs/heads/agent/a1").Trim());
    }

    /// <summary>
    /// Design §7's trigger is "both", so two publishes of the SAME agent overlapping is the normal shape,
    /// not an edge case: the watcher sweeps on its own clock while the merge queue and the review cockpit
    /// publish immediately before reading the mirror. They share one quarantine ref and each deletes it in
    /// a <c>finally</c>, so before serialization the loser reported <c>NothingToPublish</c> ("the fetched
    /// ref resolved to nothing") or <c>Failed</c> ("the compare-and-swap … lost") for a mirror that was
    /// carrying exactly the tip it asked for — and <c>Current</c>, which is what
    /// <c>PublishAgentBranch</c> returns, was false for a publish that had in fact succeeded.
    /// </summary>
    [Fact]
    public void Publish_ConcurrentlyForTheSameAgent_NeverReportsFailureForAMirrorThatIsCurrent()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var tip = env.CommitInWorktree(worktree, "one.txt", "one\n");

        const int Racers = 8;
        var start = new Barrier(Racers);
        var results = new AgentRefPublishResult[Racers];
        var threads = new Thread[Racers];
        for (var i = 0; i < Racers; i++)
        {
            var slot = i;
            threads[slot] = new Thread(() =>
            {
                start.SignalAndWait();
                results[slot] = env.Worktrees.Publish(hash, "a1");
            });
            threads[slot].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "a concurrent publish never finished");
        }

        // Exactly one of them moved the ref; every other one must say the mirror is already current, and
        // none may report a refusal (no rule was broken) or a failure (nothing failed).
        Assert.Equal(1, results.Count(r => r.Outcome == AgentRefPublishOutcome.Published));
        Assert.All(results, r => Assert.True(
            r.Current, $"a concurrent publish reported {r.Outcome} ({r.Reason}) for a current mirror"));
        Assert.Equal(tip, AgentTestGit.RunChecked(env.BarePath(hash), "rev-parse", "refs/heads/agent/a1").Trim());
        Assert.Empty(RefsUnder(env.BarePath(hash), AgentRefMediator.QuarantineRefPrefix));
    }

    private static IReadOnlyList<string> RefsUnder(string gitDir, string prefix)
        => AgentTestGit.RunChecked(gitDir, "for-each-ref", "--format=%(refname)", prefix)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

    /// <summary>A provisioned mirror + a real worktree manager over a temp VM root, capturing warnings.</summary>
    private sealed class MediationEnv : IDisposable
    {
        private readonly string _vmRoot = AgentTestGit.NewVmRoot();
        private readonly DualRepoFixture _fixture = new();
        private readonly RepoProvisioner _provisioner;

        public MediationEnv()
        {
            _provisioner = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot, warningSink: Warnings.Add, audit: Audit);
            AgentRepos = new AgentRepoManager(_vmRoot);
        }

        public WorktreeManager Worktrees { get; }

        public AgentRepoManager AgentRepos { get; }

        public List<string> Warnings { get; } = new();

        public Mainguard.Git.Audit.InMemoryAuditLog Audit { get; } = new();

        public string Provision() => _provisioner.Provision(_fixture.WorkRepoPath).RepoHash;

        public string BarePath(string hash) => _provisioner.BareRepoPathFor(hash);

        public string AgentRepoPath(string hash, string agentId) => AgentRepos.PathFor(hash, agentId);

        /// <summary>Commits in the agent's worktree (which moves its OWN repo's ref) and returns the sha.</summary>
        public string CommitInWorktree(string worktree, string relPath, string content)
        {
            AgentTestGit.SetIdentity(worktree);
            File.WriteAllText(Path.Combine(worktree, relPath), content);
            AgentTestGit.RunChecked(worktree, "add", relPath);
            AgentTestGit.RunChecked(worktree, "commit", "-m", "agent work: " + relPath);
            return AgentTestGit.RunChecked(worktree, "rev-parse", "HEAD").Trim();
        }

        public void Dispose()
        {
            _fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
