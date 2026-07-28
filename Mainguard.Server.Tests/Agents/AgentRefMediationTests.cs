using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <c>git push</c> stays meaningful. Driven through <c>PollOnce</c> rather than by sleeping on the
    /// background loop, because a test that waits "long enough" keeps passing after the loop stops.
    /// </summary>
    [Fact]
    public void Watcher_PublishesWhenTheAgentsRefMoves_AndDoesNothingWhileItIsStill()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);
        var watcher = new AgentRefWatcher(env.Worktrees.RefMediator, env.AgentRepos);
        watcher.Watch(hash, "a1");

        // The first sweep publishes the ref as it stands (the snapshot starts empty, so an agent that
        // committed before the watch began is never missed).
        Assert.All(watcher.PollOnce(), r => Assert.True(r.Current));
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
        watcher.Dispose();
    }

    /// <summary>
    /// A watch whose agent repository has gone self-evicts. `SwarmReconciler` disposes an orphan by
    /// calling `RemoveAgentWorktree` directly — it never goes through the launcher, so nothing else
    /// unwatches it. A vanished repo publishes `NothingToPublish`, which is not `Current`, so the
    /// snapshot would never be recorded and the entry would spawn a git process every tick for the life
    /// of the daemon.
    /// </summary>
    [Fact]
    public void Watcher_DropsAnAgentWhoseRepositoryIsGone_RatherThanRetryingForever()
    {
        using var env = new MediationEnv();
        var hash = env.Provision();
        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var watcher = new AgentRefWatcher(env.Worktrees.RefMediator, env.AgentRepos);
        watcher.Watch(hash, "a1");
        watcher.PollOnce();
        Assert.Contains((hash, "a1"), watcher.Watched);

        // Teardown by a path that does not unwatch (the reconciler's).
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);

        Assert.Empty(watcher.PollOnce());
        Assert.DoesNotContain((hash, "a1"), watcher.Watched);
        watcher.Dispose();
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
        var watcher = new AgentRefWatcher(env.Worktrees.RefMediator, env.AgentRepos);
        watcher.Watch(hash, "a1");
        watcher.PollOnce();

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
        watcher.Dispose();
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
