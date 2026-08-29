using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The rung the coordinator loop was missing: a worker's finished work becoming a commit on
/// <c>agent/&lt;id&gt;</c>.
///
/// <para><b>The defect.</b> In the first successful end-to-end run a worker did the approved work and
/// stopped with a 20-line UNCOMMITTED diff. Stopping the agent deleted the worktree, so the diff was
/// lost, the branch carried no commit, and the merge-queue row joined the dead "the agent's sandbox is
/// gone" entries. Nothing had told the worker to commit, and nothing on the daemon side committed for
/// it.</para>
///
/// <para><b>Why the commit has to land here specifically.</b> The daemon's readiness signal is
/// <c>refs/heads/agent/&lt;id&gt;</c> ADVANCING in the agent's own repository and then going quiet
/// (<c>AgentRefWatcher</c> → <c>WorkerReadinessTrigger</c> → <c>MergeQueue.RunVerificationAsync</c>).
/// A commit on any other branch, or no commit at all, is invisible to every one of those. So these
/// tests assert against the AGENT REPOSITORY's ref, not against the worktree's HEAD alone.</para>
///
/// <para>Run against real git through the real <see cref="WorktreeManager"/> — the mechanism is git's
/// index and git's refs, and a fake would assert the shape of a commit while proving nothing about
/// whether one happened.</para>
/// </summary>
public sealed class AgentWorkCommitTests : IDisposable
{
    private const string AgentId = "worker-commit-1";

    private readonly string _vmRoot = NewDir("mainguard-workcommit-vm-");
    private readonly string _source = NewDir("mainguard-workcommit-src-");

    // ---- the happy path, measured on the ref the trigger reads -----------------------------------

    [Fact]
    public void FinishedWork_BecomesACommitOnTheAgentsOwnBranch()
    {
        var (manager, repoHash, worktree) = NewAgent();
        var before = AgentBranchTip(repoHash);
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "public class Feature { }\n");

        var result = manager.CommitAgentWork(repoHash, AgentId, "feat: the approved work");

        Assert.Equal(AgentWorkCommitOutcome.Committed, result.Outcome);
        Assert.True(result.Committed);
        Assert.Equal("agent/" + AgentId, result.Branch);

        // The ref the watcher snapshots really moved, and it moved to the sha we were told about.
        var after = AgentBranchTip(repoHash);
        Assert.NotEqual(before, after);
        Assert.Equal(after, result.Sha);
    }

    [Fact]
    public void TheCommitCarriesTheWorkersMessage_AndAnIdentityNamingTheAgent()
    {
        var (manager, repoHash, worktree) = NewAgent();
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "x\n");

        manager.CommitAgentWork(repoHash, AgentId, "feat: the approved work");

        using var repo = new Repository(AgentRepoPath(repoHash));
        var tip = repo.Branches["agent/" + AgentId].Tip;
        Assert.Equal("feat: the approved work", tip.MessageShort);
        Assert.Contains(AgentId, tip.Author.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Untracked files are the whole point: a worker's new source files are new files. <c>git add -A</c>
    /// is what makes this true, and a test that only edited a tracked file would pass against a
    /// <c>commit -a</c> that silently drops everything the worker created.
    /// </summary>
    [Fact]
    public void NewFilesTheWorkerCreated_AreInTheCommit()
    {
        var (manager, repoHash, worktree) = NewAgent();
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "brand-new.cs"), "public class New { }\n");

        manager.CommitAgentWork(repoHash, AgentId, "feat: add a file");

        Assert.Contains("src/brand-new.cs", CommittedPaths(repoHash));
    }

    /// <summary>
    /// The coupling with the other half of this change, stated as a test rather than as a comment: what
    /// the daemon writes into <c>/workspace</c> is listed in the agent repository's <c>info/exclude</c>,
    /// and this commit is precisely the thing that would otherwise carry it into the user's branch.
    /// </summary>
    [Fact]
    public void FilesTheDaemonExcluded_AreNotInTheCommit()
    {
        var (manager, repoHash, worktree) = NewAgent();
        var exclude = Path.Combine(AgentRepoPath(repoHash), "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        File.AppendAllText(exclude, "\n/CLAUDE.md\n");
        File.WriteAllText(Path.Combine(worktree, "CLAUDE.md"), "# Mainguard's own briefing\n");
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "public class Feature { }\n");

        manager.CommitAgentWork(repoHash, AgentId, "feat: the approved work");

        var paths = CommittedPaths(repoHash);
        Assert.Contains("feature.cs", paths);
        Assert.DoesNotContain("CLAUDE.md", paths);
    }

    /// <summary>
    /// <b>The commit does not publish, and that is load-bearing rather than an omission.</b>
    /// <c>AgentRefWatcher.PollOnce</c> raises <c>Advanced</c> — the event
    /// <c>WorkerReadinessTrigger</c> subscribes to — only for an outcome of <c>Published</c>. A publish
    /// that already happened makes the sweep's own publish <c>Unchanged</c>, which is <c>Current</c> (so
    /// the snapshot is recorded) and NOT <c>Published</c> (so no event fires). Publishing here would
    /// therefore silently disarm the trigger for the very commit it exists to react to.
    ///
    /// <para>Observed as the mirror still lagging the agent's own repository straight after the commit.
    /// The watcher carries it across on its own tick, and the pre-verification re-fetch is the other
    /// half; neither is changed by this.</para>
    /// </summary>
    [Fact]
    public void TheCommitLeavesThePublishToTheWatcher_SoTheTriggerStillSeesTheAdvance()
    {
        var (manager, repoHash, worktree) = NewAgent();
        var mirrorBefore = MirrorBranchTip(repoHash);
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "public class Feature { }\n");

        var result = manager.CommitAgentWork(repoHash, AgentId, "feat: the approved work");

        Assert.Equal(AgentWorkCommitOutcome.Committed, result.Outcome);
        Assert.Equal(mirrorBefore, MirrorBranchTip(repoHash));   // the mirror has NOT been advanced
        Assert.NotEqual(mirrorBefore, AgentBranchTip(repoHash)); // …while the agent's own repo has

        // …and the watcher's own publish is what moves it, exactly as before.
        Assert.True(manager.PublishAgentBranch(repoHash, AgentId));
        Assert.Equal(AgentBranchTip(repoHash), MirrorBranchTip(repoHash));
    }

    // ---- the outcomes that are not a commit ------------------------------------------------------

    /// <summary>
    /// A clean tree answers "nothing to commit", not "committed". The distinction is not cosmetic: the ref
    /// did not move, so nothing downstream will ever observe anything, and telling a worker its work is
    /// recorded when the branch is exactly where it was is how the original defect would come back wearing
    /// a success message.
    /// </summary>
    [Fact]
    public void ACleanWorktree_IsNothingToCommit_AndTheBranchDoesNotMove()
    {
        var (manager, repoHash, _) = NewAgent();
        var before = AgentBranchTip(repoHash);

        var result = manager.CommitAgentWork(repoHash, AgentId, "feat: nothing happened");

        Assert.Equal(AgentWorkCommitOutcome.NothingToCommit, result.Outcome);
        Assert.False(result.Committed);
        Assert.Equal(before, AgentBranchTip(repoHash));
    }

    /// <summary>
    /// A worktree that has wandered off <c>agent/&lt;id&gt;</c> is refused rather than committed onto.
    /// A commit made on some other branch — or on a detached HEAD — is reachable from nothing the
    /// mediator publishes, nothing the queue reads and nothing the trigger watches, so it would be lost
    /// exactly as an uncommitted diff is, while reporting success.
    /// </summary>
    [Fact]
    public void AWorktreeOnAnotherBranch_IsRefused_AndNothingIsCommitted()
    {
        var (manager, repoHash, worktree) = NewAgent();
        var before = AgentBranchTip(repoHash);
        using (var repo = new Repository(worktree))
        {
            Commands.Checkout(repo, repo.CreateBranch("somewhere-else"));
        }

        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "public class Feature { }\n");

        var result = manager.CommitAgentWork(repoHash, AgentId, "feat: on the wrong branch");

        Assert.Equal(AgentWorkCommitOutcome.RefusedBranch, result.Outcome);
        Assert.Equal("agent/" + AgentId, result.Branch);
        Assert.Equal(before, AgentBranchTip(repoHash));
    }

    [Fact]
    public void AnAgentWithNoWorktree_IsReportedAsSuch_RatherThanAsASuccess()
    {
        var manager = new WorktreeManager(_vmRoot);
        var repoHash = SeedAndProvision();

        var result = manager.CommitAgentWork(repoHash, "never-spawned", "feat: nothing");

        Assert.Equal(AgentWorkCommitOutcome.NoWorktree, result.Outcome);
        Assert.False(result.Committed);
    }

    /// <summary>
    /// A worktree manager with no substrate — every test double — answers <c>Unsupported</c>, never
    /// "committed". The default has to be the one that cannot be mistaken for success: a caller relaying
    /// it to a worker would otherwise report work as safe that was never recorded anywhere.
    /// </summary>
    [Fact]
    public void ASubstratelessManager_RefusesRatherThanClaimingToHaveCommitted()
    {
        IAgentWorktreeManager doubleWithNoSubstrate = new NoSubstrateWorktrees();

        var result = doubleWithNoSubstrate.CommitAgentWork("repo", AgentId, "feat: x");

        Assert.Equal(AgentWorkCommitOutcome.Unsupported, result.Outcome);
        Assert.False(result.Committed);
    }

    // ---- the message, which is the one thing a worker supplies ------------------------------------

    /// <summary>
    /// Normalisation is the ONLY thing done to a message that is going to be recorded: line endings
    /// folded, trailing whitespace removed, leading and trailing blank lines dropped. Nothing reflows,
    /// nothing is collapsed, nothing is cut — every row here is a change the worker cannot notice, and
    /// the rows that would matter are refusals instead.
    /// </summary>
    [Theory]
    [InlineData("feat: one line", "feat: one line")]
    [InlineData("  padded  ", "padded")]
    // …but a BODY keeps its indentation: that is the worker's code block, not a slip.
    [InlineData("feat: x\n\n    indented", "feat: x\n\n    indented")]
    [InlineData("feat: x\r\n\r\nbody", "feat: x\n\nbody")]
    [InlineData("\n\nfeat: x\n\nbody\n\n\n", "feat: x\n\nbody")]
    // Blank lines INSIDE the body are the worker's paragraphs and survive untouched — this is what
    // `--cleanup=verbatim` is for, since git's default for `-m` would collapse them.
    [InlineData("feat: x\n\none\n\n\ntwo", "feat: x\n\none\n\n\ntwo")]
    public void AMessageIsNormalised_AndNothingElse(string given, string expected)
    {
        Assert.Equal(expected, AgentCommitMessage.Normalize(given));
    }

    /// <summary>
    /// <b>The G4 rule: a message that cannot be honoured fails loudly.</b> Each of these used to be
    /// silently repaired into something the worker did not write — the over-long one by truncation at
    /// 200 characters mid-word, the missing blank line by git folding the whole body into <c>%s</c>.
    /// </summary>
    [Theory]
    [InlineData("subject too long", 0)]
    [InlineData("missing blank line", 1)]
    [InlineData("far too big", 2)]
    [InlineData("control character", 3)]
    public void AMessageThatCannotBeRecorded_IsRefused_WithTheReason(string _, int which)
    {
        var message = which switch
        {
            0 => new string('x', AgentCommitMessage.MaxSubjectLength + 1),
            1 => "feat: a subject\nstraight into the body",
            2 => "feat: a subject\n\n" + new string('x', AgentCommitMessage.MaxLength),
            _ => "feat: a subject\n\nbody with a \u0007 bell",
        };

        var refusal = AgentCommitMessage.Refuse(AgentCommitMessage.Normalize(message));

        Assert.NotNull(refusal);
        Assert.Contains("Nothing was committed", refusal!, StringComparison.Ordinal);
    }

    /// <summary>The boundary itself is accepted — a limit that refuses its own permitted value is a
    /// different limit, and this one is git's documented convention rather than a number picked here.</summary>
    [Fact]
    public void TheSubjectBoundaryItself_IsNotRefused()
    {
        Assert.Null(AgentCommitMessage.Refuse(new string('x', AgentCommitMessage.MaxSubjectLength)));
        Assert.NotNull(AgentCommitMessage.Refuse(new string('x', AgentCommitMessage.MaxSubjectLength + 1)));
    }

    /// <summary>
    /// A refused message refuses the COMMIT, and the worktree is left exactly as the worker left it: the
    /// branch does not move, nothing is staged behind its back, and a corrected message is a retry.
    /// </summary>
    [Fact]
    public void ARefusedMessage_CommitsNothing_AndLeavesTheWorkToRetryWith()
    {
        var (manager, repoHash, worktree) = NewAgent();
        var before = AgentBranchTip(repoHash);
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "public class Feature { }\n");

        var result = manager.CommitAgentWork(repoHash, AgentId, new string('x', 400));

        Assert.Equal(AgentWorkCommitOutcome.RefusedMessage, result.Outcome);
        Assert.False(result.Committed);
        Assert.Null(result.Sha);
        Assert.Equal(before, AgentBranchTip(repoHash));
        Assert.Contains("72", result.Detail ?? string.Empty, StringComparison.Ordinal);

        // …and the same work commits the moment the message is one git can record.
        var retry = manager.CommitAgentWork(
            repoHash, AgentId, "feat: the approved work\n\nWith the detail in the body, where it goes.");
        Assert.Equal(AgentWorkCommitOutcome.Committed, retry.Outcome);
    }

    /// <summary>An empty message is a default that names the agent, not a refusal: refusing the commit
    /// over a missing subject would lose the work, which is the defect this whole change is about. That
    /// is not the G4 case — nothing structured is being discarded — so it is deliberately unchanged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentMessage_StillCommits_UnderADefaultThatNamesTheAgent(string? given)
    {
        var (manager, repoHash, worktree) = NewAgent();
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "x\n");

        var result = manager.CommitAgentWork(repoHash, AgentId, given);

        Assert.Equal(AgentWorkCommitOutcome.Committed, result.Outcome);
        using var repo = new Repository(AgentRepoPath(repoHash));
        Assert.Contains(
            AgentId, repo.Branches["agent/" + AgentId].Tip.MessageShort, StringComparison.Ordinal);
    }

    // ---- G4: the message is the durable record, and it was being destroyed ------------------------

    /// <summary>
    /// <b>Defect G4.</b> A worker passed a proper git message — subject, blank line, two body paragraphs
    /// — and the commit that resulted had every newline replaced by a space, was cut at 200 characters
    /// mid-word, and had an empty <c>%b</c>. The op reported success. Two of three commits in a stress
    /// run were mangled this way.
    ///
    /// <para>Asserted with git's own <c>%s</c>/<c>%b</c>, because those are what a human reads at review
    /// and what every tool downstream splits on — a test that only compared the whole message would pass
    /// on a body that git considers part of the subject.</para>
    /// </summary>
    [Fact]
    public void AProperMessage_IsRecordedVerbatim_SubjectBlankLineAndBody()
    {
        var (manager, repoHash, worktree) = NewAgent();
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "x\n");
        const string Message =
            "feat(auth): recompute token expiry in UTC\n"
            + "\n"
            + "The clock read the host's local zone, so a token minted at 23:30 CET expired\n"
            + "an hour before the server thought it would.\n"
            + "\n"
            + "Boundary tests cover the DST transition in both directions.";

        var result = manager.CommitAgentWork(repoHash, AgentId, Message);

        Assert.Equal(AgentWorkCommitOutcome.Committed, result.Outcome);
        using var repo = new Repository(AgentRepoPath(repoHash));
        var tip = repo.Branches["agent/" + AgentId].Tip;

        Assert.Equal("feat(auth): recompute token expiry in UTC", tip.MessageShort);
        Assert.Contains("an hour before the server thought it would.", tip.Message, StringComparison.Ordinal);
        Assert.Contains("Boundary tests cover the DST transition", tip.Message, StringComparison.Ordinal);

        // The paragraph break survives — it is the difference between a body and one long line.
        Assert.Contains("would.\n\nBoundary tests", tip.Message.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Git is asked to record the message, not to tidy it.</b> The default cleanup for <c>-m</c> is
    /// <c>whitespace</c>, which COLLAPSES consecutive blank lines — and it is configurable through
    /// <c>commit.cleanup</c>, so leaving it implicit makes the shape of an agent's commit depend on a
    /// config key nobody set on purpose. A body whose spacing is quietly altered is a smaller version of
    /// exactly the defect this change removes, so <c>--cleanup=verbatim</c> is passed explicitly.
    /// </summary>
    [Fact]
    public void TheCommitDoesNotLetGitReflowTheBody()
    {
        var (manager, repoHash, worktree) = NewAgent();
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "x\n");
        const string Message = "feat: spacing the worker chose\n\nfirst\n\n\nsecond";

        var result = manager.CommitAgentWork(repoHash, AgentId, Message);

        Assert.Equal(AgentWorkCommitOutcome.Committed, result.Outcome);
        using var repo = new Repository(AgentRepoPath(repoHash));
        var recorded = repo.Branches["agent/" + AgentId].Tip.Message.Replace("\r\n", "\n");

        Assert.Contains("first\n\n\nsecond", recorded, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private (WorktreeManager Manager, string RepoHash, string Worktree) NewAgent()
    {
        var manager = new WorktreeManager(_vmRoot);
        var repoHash = SeedAndProvision();
        var worktree = manager.CreateAgentWorktree(repoHash, AgentId);
        return (manager, repoHash, worktree);
    }

    private string SeedAndProvision()
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        var readme = Path.Combine(_source, "README.md");
        File.WriteAllText(readme, "seed\n");
        using (var repo = new Repository(_source))
        {
            Commands.Stage(repo, "README.md");
            var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            repo.Commit("seed", sig, sig);
        }

        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    private string AgentRepoPath(string repoHash) =>
        new WorktreeManager(_vmRoot).AgentRepoPathFor(repoHash, AgentId);

    /// <summary>The tip of <c>agent/&lt;id&gt;</c> in the AGENT'S OWN repository — the exact ref
    /// <c>AgentRefWatcher</c> snapshots, so "the branch moved" is measured where the trigger measures
    /// it rather than wherever the commit happened to be made.</summary>
    private string? AgentBranchTip(string repoHash)
    {
        using var repo = new Repository(AgentRepoPath(repoHash));
        return repo.Branches["agent/" + AgentId]?.Tip?.Sha;
    }

    /// <summary>The tip of <c>agent/&lt;id&gt;</c> in the shared MIRROR — where the merge queue reads it,
    /// and the ref a publish moves.</summary>
    private string? MirrorBranchTip(string repoHash)
    {
        using var repo = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        return repo.Branches["agent/" + AgentId]?.Tip?.Sha;
    }

    private string[] CommittedPaths(string repoHash)
    {
        using var repo = new Repository(AgentRepoPath(repoHash));
        return repo.Branches["agent/" + AgentId].Tip.Tree
            .Flatten(repo).Select(e => e.Path).ToArray();
    }

    private sealed class NoSubstrateWorktrees : IAgentWorktreeManager
    {
        public string CreateAgentWorktree(string repoHash, string agentId) => string.Empty;

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) { }

        public void Prune(string repoHash) { }

        public System.Collections.Generic.IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
            Array.Empty<Mainguard.Git.Models.WorktreeItem>();
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        TryDelete(_vmRoot);
        TryDelete(_source);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* never fail a test from cleanup */ }
    }
}

internal static class TreeFlattenExtensions
{
    /// <summary>Every blob path in a tree, recursively — LibGit2Sharp's <c>Tree</c> is one level deep.</summary>
    public static System.Collections.Generic.IEnumerable<TreeEntry> Flatten(this Tree tree, Repository repo)
    {
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                foreach (var nested in ((Tree)entry.Target).Flatten(repo))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }
}
