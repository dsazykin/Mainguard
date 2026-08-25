using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Tests;

// TI-08: interactive rebase (CLI-driven). Every case drives real `git rebase -i`
// through InteractiveRebaseService and reads real repo state back.
//
// The service starts the rebase with GIT_SEQUENCE_EDITOR / GIT_EDITOR pointing at
// GitService.GetSelfInvocationPrefix() — normally the running process. Under
// `dotnet test` the running process is the test host, which knows nothing of the
// --rebase-editor/--rebase-msg argv shims. So we override the prefix to the built
// Mainguard.Client.App (copied next to the test assembly via its ProjectReference), whose
// argv modes perform the shim copies and exit before Avalonia init.
[Trait("Category", "RequiresGitCli")]
public class InteractiveRebaseServiceTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly InteractiveRebaseService _svc = new();
    private readonly GitService _git = new();

    public InteractiveRebaseServiceTests()
    {
        GitService.SelfInvocationOverride = TestTools.SelfInvocation.ClientAppPrefix();
    }

    public void Dispose()
    {
        GitService.SelfInvocationOverride = null;
        _fx.Dispose();
    }

    // ---- helpers ---------------------------------------------------------

    private string HeadSha() { using var r = new Repository(_fx.RepoPath); return r.Head.Tip.Sha; }
    private string HeadTreeSha() { using var r = new Repository(_fx.RepoPath); return r.Head.Tip.Tree.Sha; }
    private string HeadMsg() { using var r = new Repository(_fx.RepoPath); return r.Head.Tip.MessageShort; }
    private string FileText(string rel) => File.ReadAllText(Path.Combine(_fx.RepoPath, rel));
    private bool FileThere(string rel) => File.Exists(Path.Combine(_fx.RepoPath, rel));

    private int CountSince(string baseSha)
    {
        using var r = new Repository(_fx.RepoPath);
        return r.Commits.QueryBy(new CommitFilter
        {
            IncludeReachableFrom = r.Head.Tip,
            ExcludeReachableFrom = r.Lookup<Commit>(baseSha)
        }).Count();
    }

    // Seeds base + c1(edit middle) + c2(edit middle again) + c3(add file). Dropping c1
    // makes c2 conflict when 3-way-applied onto base; c3 then applies cleanly. This is the
    // single-conflict shape shared by the conflict / abort / progress tests. Returns the
    // plan already marked to drop c1 (the rest Pick), plus the base SHA.
    private (string baseSha, List<RebaseTodoItem> plan) SeedDropConflict()
    {
        var baseSha = _fx.CommitFile("file.txt", "L1\nL2\nL3\n", "base");
        _fx.CommitFile("file.txt", "L1\nX\nL3\n", "c1");
        _fx.CommitFile("file.txt", "L1\nY\nL3\n", "c2");
        _fx.CommitFile("other.txt", "o\n", "c3");
        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        plan[0].Action = RebaseAction.Drop; // drop c1 -> c2 conflicts on base
        return (baseSha, plan);
    }

    // ---- 1: plan ---------------------------------------------------------

    [Fact]
    public void GetRebasePlan_ShouldListRangeOldestFirst_AllPick()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        var c1 = _fx.CommitFile("a.txt", "a\n", "c1");
        var c2 = _fx.CommitFile("b.txt", "b\n", "c2");
        var c3 = _fx.CommitFile("c.txt", "c\n", "c3");

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha);

        Assert.Equal(new[] { c1, c2, c3 }, plan.Select(p => p.Sha));
        Assert.All(plan, p => Assert.Equal(RebaseAction.Pick, p.Action));
    }

    // ---- 2: reorder ------------------------------------------------------

    [Fact]
    public void Reorder_ShouldSwapHistoryOrder_AndPreserveFinalTree()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "add a");
        _fx.CommitFile("b.txt", "b\n", "add b");

        var treeBefore = HeadTreeSha();
        Assert.Equal("add b", HeadMsg());

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        (plan[0], plan[1]) = (plan[1], plan[0]); // swap add a / add b

        _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal(treeBefore, HeadTreeSha());   // identical final tree
        Assert.Equal("add a", HeadMsg());          // order swapped: "add a" now on top
        Assert.True(FileThere("a.txt") && FileThere("b.txt"));
    }

    // ---- 3: reword -------------------------------------------------------

    [Fact]
    public void Reword_ShouldChangeMessage_KeepTree()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "c1");
        _fx.CommitFile("b.txt", "b\n", "original subject");

        var treeBefore = HeadTreeSha();

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        plan[1].Action = RebaseAction.Reword;
        plan[1].NewMessage = "reworded subject";

        _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal("reworded subject", HeadMsg());
        Assert.Equal(treeBefore, HeadTreeSha());
    }

    // ---- 4: squash -------------------------------------------------------

    [Fact]
    public void Squash_ShouldCombineTwoCommits_WithNewMessage()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "c1");
        _fx.CommitFile("b.txt", "b\n", "c2");

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        plan[1].Action = RebaseAction.Squash;
        plan[1].NewMessage = "combined";

        _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal(1, CountSince(baseSha));       // 2 -> 1
        Assert.Equal("combined", HeadMsg());        // NewMessage used
        Assert.True(FileThere("a.txt") && FileThere("b.txt")); // combined diff
    }

    // ---- 5: fixup --------------------------------------------------------

    [Fact]
    public void Fixup_ShouldKeepFirstMessage()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "first");
        _fx.CommitFile("b.txt", "b\n", "second");

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        plan[1].Action = RebaseAction.Fixup;

        _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal(1, CountSince(baseSha));
        Assert.Equal("first", HeadMsg());           // fixup drops the second message
        Assert.True(FileThere("a.txt") && FileThere("b.txt"));
    }

    // ---- 6: drop ---------------------------------------------------------

    [Fact]
    public void Drop_ShouldRemoveCommitChanges()
    {
        var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "c1");
        _fx.CommitFile("b.txt", "b\n", "c2");   // to be dropped
        _fx.CommitFile("c.txt", "c\n", "c3");

        var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
        plan[1].Action = RebaseAction.Drop;     // drop c2 (b.txt)

        _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal(2, CountSince(baseSha));
        Assert.True(FileThere("a.txt") && FileThere("c.txt"));
        Assert.False(FileThere("b.txt"));       // dropped commit's changes absent
    }

    // ---- 7: conflict mid-rebase, resolve, continue -----------------------

    [Fact]
    public void ConflictMidRebase_ShouldThrowMergeConflict_AndContinueAfterResolveCompletesPlan()
    {
        var (baseSha, plan) = SeedDropConflict();

        Assert.Throws<MergeConflictException>(
            () => _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan));
        Assert.True(_git.IsRebasing(_fx.RepoPath));

        // Resolve via the T-03 whole-file path, then continue the rest of the plan.
        _git.ResolveConflict(_fx.RepoPath, "file.txt", "L1\nY\nL3\n");
        _git.ContinueRebase(_fx.RepoPath);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal("L1\nY\nL3\n", FileText("file.txt")); // c2's change survived
        Assert.True(FileThere("other.txt"));               // c3 applied after the resolve
        Assert.Equal(2, CountSince(baseSha));              // c1 dropped -> c2', c3'
    }

    // ---- 8: abort --------------------------------------------------------

    [Fact]
    public void Abort_ShouldRestoreExactPreRebaseHead()
    {
        var (baseSha, plan) = SeedDropConflict();
        var preSha = HeadSha();

        Assert.Throws<MergeConflictException>(
            () => _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan));
        Assert.True(_git.IsRebasing(_fx.RepoPath));

        _git.AbortRebase(_fx.RepoPath);

        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.Equal(preSha, HeadSha());        // byte-identical pre-rebase HEAD
    }

    // ---- 9: pre-flight guards (four typed refusals; repo untouched) ------

    [Fact]
    public void Start_ShouldThrowTyped_OnDirtyTree_MergeCommitInRange_FirstItemSquash_AlreadyRebasing()
    {
        // (a) dirty working tree
        {
            var baseSha = _fx.CommitFile("base.txt", "b\n", "base");
            _fx.CommitFile("a.txt", "a\n", "c1");
            var plan = _svc.GetRebasePlan(_fx.RepoPath, baseSha).ToList();
            var pre = HeadSha();
            _fx.WriteFile("a.txt", "dirty\n"); // uncommitted change
            Assert.Throws<GitOperationException>(
                () => _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan));
            Assert.Equal(pre, HeadSha());
            Assert.False(_git.IsRebasing(_fx.RepoPath));
            _fx.WriteFile("a.txt", "a\n"); // restore clean tree for the next guards
        }

        // (b) merge commit in range — refused at plan time (GetRebasePlan)
        using (var fx2 = new TempRepoFixture())
        {
            var baseSha = fx2.CommitFile("base.txt", "b\n", "base");
            CreateMergeCommitOnHead(fx2);
            var pre = TipSha(fx2);
            Assert.Throws<GitOperationException>(() => _svc.GetRebasePlan(fx2.RepoPath, baseSha));
            Assert.Equal(pre, TipSha(fx2));
        }

        // (c) first kept item is Squash
        using (var fx3 = new TempRepoFixture())
        {
            var baseSha = fx3.CommitFile("base.txt", "b\n", "base");
            fx3.CommitFile("a.txt", "a\n", "c1");
            fx3.CommitFile("b.txt", "b\n", "c2");
            var plan = _svc.GetRebasePlan(fx3.RepoPath, baseSha).ToList();
            plan[0].Action = RebaseAction.Squash; // first item cannot be squash
            var pre = TipSha(fx3);
            Assert.Throws<GitOperationException>(
                () => _svc.StartInteractiveRebase(fx3.RepoPath, baseSha, plan));
            Assert.Equal(pre, TipSha(fx3));
            Assert.False(_git.IsRebasing(fx3.RepoPath));
        }

        // (d) a rebase is already in progress
        using (var fx4 = new TempRepoFixture())
        {
            var baseSha = fx4.CommitFile("base.txt", "b\n", "base");
            fx4.CommitFile("a.txt", "a\n", "c1");
            var plan = _svc.GetRebasePlan(fx4.RepoPath, baseSha).ToList();
            var pre = TipSha(fx4);
            var marker = Path.Combine(fx4.RepoPath, ".git", "rebase-merge");
            Directory.CreateDirectory(marker);
            try
            {
                Assert.Throws<GitOperationException>(
                    () => _svc.StartInteractiveRebase(fx4.RepoPath, baseSha, plan));
                Assert.Equal(pre, TipSha(fx4));
            }
            finally
            {
                Directory.Delete(marker, true);
            }
        }
    }

    // ---- 10: progress ----------------------------------------------------

    [Fact]
    public void GetRebaseProgress_ShouldReportStepAndTotal_MidConflict()
    {
        Assert.Null(_svc.GetRebaseProgress(_fx.RepoPath)); // not rebasing -> null

        var (baseSha, plan) = SeedDropConflict();
        Assert.Throws<MergeConflictException>(
            () => _svc.StartInteractiveRebase(_fx.RepoPath, baseSha, plan));

        var progress = _svc.GetRebaseProgress(_fx.RepoPath);
        Assert.NotNull(progress);
        Assert.Equal(2, progress!.Value.Total);           // two picks after the drop
        Assert.InRange(progress.Value.Step, 1, progress.Value.Total);

        _git.AbortRebase(_fx.RepoPath);
        Assert.Null(_svc.GetRebaseProgress(_fx.RepoPath)); // null again once finished
    }

    // ---- shared helpers for guard (b) ------------------------------------

    private static string TipSha(TempRepoFixture fx)
    {
        using var r = new Repository(fx.RepoPath);
        return r.Head.Tip.Sha;
    }

    // Builds main+feature commits on top of HEAD and a no-ff merge commit, so the
    // baseSha..HEAD range contains a >1-parent commit.
    private static void CreateMergeCommitOnHead(TempRepoFixture fx)
    {
        using var repo = new Repository(fx.RepoPath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        var main = repo.Head.FriendlyName;

        repo.CreateBranch("feature");
        Commands.Checkout(repo, repo.Branches["feature"]);
        File.WriteAllText(Path.Combine(fx.RepoPath, "feat.txt"), "f\n");
        Commands.Stage(repo, "feat.txt");
        repo.Commit("feature commit", sig, sig);

        Commands.Checkout(repo, repo.Branches[main]);
        File.WriteAllText(Path.Combine(fx.RepoPath, "mainf.txt"), "m\n");
        Commands.Stage(repo, "mainf.txt");
        repo.Commit("main commit", sig, sig);

        repo.Merge(repo.Branches["feature"], sig,
            new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
    }
}
