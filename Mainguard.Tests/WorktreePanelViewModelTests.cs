using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-21 (worktree VM) — <see cref="WorktreePanelViewModel"/> validation over a canned
/// <see cref="FakeGitService"/>: creating a worktree on a branch already checked out in another
/// worktree is disallowed (<see cref="WorktreePanelViewModel.CanCreate"/> false), while a free branch
/// or a new branch is allowed and drives the right <c>AddWorktree</c> call.
/// </summary>
public class WorktreePanelViewModelTests
{
    private static FakeGitService FakeWith(params (string branch, bool detached, bool main)[] worktrees)
    {
        var wts = worktrees.Select(w => new WorktreeItem
        {
            Path = "/wt/" + (w.branch ?? "detached"),
            Branch = w.detached ? null : w.branch,
            IsDetached = w.detached,
            IsMain = w.main,
            HeadSha = "abcdef1234567890",
        }).ToList();

        return new FakeGitService
        {
            ListWorktreesImpl = _ => wts,
            GetBranchesImpl = _ => new[]
            {
                new GitBranchItem { FriendlyName = "main", IsRemote = false },
                new GitBranchItem { FriendlyName = "feature", IsRemote = false },
                new GitBranchItem { FriendlyName = "origin/main", IsRemote = true },
            },
        };
    }

    [Fact]
    public void Ctor_ShouldLoadWorktrees_AndLocalBranchesOnly()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo");

        Assert.Single(vm.Worktrees);
        Assert.Equal(new[] { "main", "feature" }, vm.Branches);
    }

    [Fact]
    public void CanCreate_WhenSelectedBranchAlreadyCheckedOut_ShouldBeFalse()
    {
        // "main" is checked out in the main worktree; "feature" is free.
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "main",
        };

        Assert.True(vm.SelectedBranchIsCheckedOut);
        Assert.False(vm.CanCreate); // git forbids a second checkout of the same branch
    }

    [Fact]
    public void CanCreate_WithFreeBranchAndPath_ShouldBeTrue()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "feature",
        };

        Assert.False(vm.SelectedBranchIsCheckedOut);
        Assert.True(vm.CanCreate);
    }

    [Fact]
    public void CanCreate_WithoutPath_ShouldBeFalse()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            SelectedBranch = "feature",
        };

        Assert.False(vm.CanCreate);
    }

    [Fact]
    public void CanCreate_NewBranchMode_ShouldValidateNameNotCheckout()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            CreateBranch = true,
        };
        Assert.False(vm.CanCreate); // no name yet

        vm.NewBranchName = "brand-new";
        Assert.True(vm.CanCreate);
        Assert.False(vm.SelectedBranchIsCheckedOut); // checkout rule doesn't apply to a new branch
    }

    [Fact]
    public async Task Create_WithNewBranch_ShouldCallAddWorktreeWithCreateFlag()
    {
        (string repo, string path, string branch, bool create)? call = null;
        var fake = FakeWith(("main", false, true));
        fake.AddWorktreeImpl = (r, p, b, c) => call = (r, p, b, c);

        var vm = new WorktreePanelViewModel(fake, "/repo")
        {
            NewWorktreePath = "../feat-wt",
            CreateBranch = true,
            NewBranchName = "feat",
        };

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(call);
        Assert.Equal("/repo", call!.Value.repo);
        Assert.Equal("../feat-wt", call.Value.path);
        Assert.Equal("feat", call.Value.branch);
        Assert.True(call.Value.create);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Create_FromExistingBranch_ShouldCallAddWorktreeWithoutCreateFlag()
    {
        (string repo, string path, string branch, bool create)? call = null;
        var fake = FakeWith(("main", false, true));
        fake.AddWorktreeImpl = (r, p, b, c) => call = (r, p, b, c);

        var vm = new WorktreePanelViewModel(fake, "/repo")
        {
            NewWorktreePath = "../feature-wt",
            SelectedBranch = "feature",
        };

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(call);
        Assert.Equal("feature", call!.Value.branch);
        Assert.False(call.Value.create);
    }

    [Fact]
    public void DetachedWorktree_ShouldNotBlockAnyBranch()
    {
        // A detached worktree contributes no branch to the checked-out set.
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true), (null!, true, false)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "feature",
        };

        Assert.True(vm.CanCreate);
    }

    // ---- Force remove ---------------------------------------------------------------
    // ForceRemoveCommand shipped implemented but bound nowhere, while the Remove button's own
    // tooltip told the user to "use Force" — so a dirty worktree could not be removed from the app
    // at all. It is now bound to a Force button, and because it discards uncommitted work it is
    // gated on a confirmation that fails closed.

    private sealed class FakeConfirmationService : Mainguard.App.Shell.Services.IConfirmationService
    {
        public bool Result { get; set; }
        public bool Asked { get; private set; }
        public string? LastTitle { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
        {
            Asked = true;
            LastTitle = title;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Remove_WithoutForce_ShouldNotConfirm_AndShouldCallRemoveWorktreeUnforced()
    {
        (string repo, string path, bool force)? call = null;
        var fake = FakeWith(("main", false, true), ("feature", false, false));
        fake.RemoveWorktreeImpl = (r, p, f) => call = (r, p, f);
        var confirm = new FakeConfirmationService { Result = false };

        var vm = new WorktreePanelViewModel(fake, "/repo", null, confirm);
        var row = vm.Worktrees.Single(w => !w.IsMain);

        await row.RemoveCommand.ExecuteAsync(null);

        Assert.False(confirm.Asked); // git itself refuses a dirty unforced remove — nothing to warn about
        Assert.NotNull(call);
        Assert.False(call!.Value.force);
    }

    [Fact]
    public async Task ForceRemove_WhenConfirmed_ShouldCallRemoveWorktreeWithForce()
    {
        (string repo, string path, bool force)? call = null;
        var fake = FakeWith(("main", false, true), ("feature", false, false));
        fake.RemoveWorktreeImpl = (r, p, f) => call = (r, p, f);
        var confirm = new FakeConfirmationService { Result = true };

        var vm = new WorktreePanelViewModel(fake, "/repo", null, confirm);
        var row = vm.Worktrees.Single(w => !w.IsMain);

        await row.ForceRemoveCommand.ExecuteAsync(null);

        Assert.True(confirm.Asked);
        Assert.Equal("Force remove worktree", confirm.LastTitle);
        Assert.NotNull(call);
        Assert.Equal("/repo", call!.Value.repo);
        Assert.Equal("/wt/feature", call.Value.path);
        Assert.True(call.Value.force);
    }

    [Fact]
    public async Task ForceRemove_WhenDeclined_ShouldNotTouchTheWorktree()
    {
        var called = false;
        var fake = FakeWith(("main", false, true), ("feature", false, false));
        fake.RemoveWorktreeImpl = (_, _, _) => called = true;
        var confirm = new FakeConfirmationService { Result = false };

        var vm = new WorktreePanelViewModel(fake, "/repo", null, confirm);
        var row = vm.Worktrees.Single(w => !w.IsMain);

        await row.ForceRemoveCommand.ExecuteAsync(null);

        Assert.True(confirm.Asked);
        Assert.False(called);
    }
}
