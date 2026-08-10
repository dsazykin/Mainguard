using System;
using System.Collections.Generic;
using System.Linq;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The branch context menu's affordances. Several implemented <see cref="BranchBrowserViewModel"/>
/// commands were reachable from no menu and no XAML at all — most importantly there was no way to
/// update a branch you were not standing on. These pin the surfaced set so a command cannot quietly
/// become unreachable again, and pin that every menu entry points at a real command.
/// </summary>
public class BranchBrowserMenuTests
{
    private static FakeGitService Fake() => new()
    {
        GetBranchesImpl = _ => new[]
        {
            new GitBranchItem { Name = "refs/heads/main", FriendlyName = "main", IsCurrentRepositoryHead = true },
            new GitBranchItem { Name = "refs/heads/feature", FriendlyName = "feature" },
            new GitBranchItem { Name = "refs/remotes/origin/feature", FriendlyName = "origin/feature", IsRemote = true },
        },
        GetTagsImpl = _ => new[] { new GitTagItem { Name = "v1.0.0" } },
    };

    private static BranchBrowserViewModel Vm() => new(Fake(), "/repo");

    private static IReadOnlyList<MenuItemViewModel> Items(MenuItemViewModel? menu)
    {
        Assert.NotNull(menu);
        return menu!.SubItems.Where(i => i is not SeparatorViewModel).ToList();
    }

    private static IReadOnlyList<string> Headers(MenuItemViewModel? menu)
        => Items(menu).Select(i => i.Header).ToList();

    [Fact]
    public void LocalBranchMenu_ShouldOfferUpdateAndPull_ForABranchYouAreNotOn()
    {
        var headers = Headers(Vm().BuildRefMenu("feature"));

        // The headline gap: no menu path existed to bring another branch up to date.
        Assert.Contains("Update feature from upstream", headers);
        Assert.Contains("Pull feature (rebase)", headers);
    }

    [Fact]
    public void LocalBranchMenu_ShouldOfferBothRebaseDirections()
    {
        var menu = Vm().BuildRefMenu("feature");
        var headers = Headers(menu);

        Assert.Contains("Rebase main onto feature", headers);
        Assert.Contains("Check out feature and rebase it onto main", headers);
    }

    /// <summary>
    /// "Rebase current onto X" is the confirmed command now; the identical unconfirmed one it
    /// replaced is gone, so history rewriting can no longer happen from the menu without a prompt.
    /// </summary>
    [Fact]
    public void LocalBranchMenu_RebaseCurrentOnto_ShouldUseTheConfirmedCommand()
    {
        var vm = Vm();
        var item = Items(vm.BuildRefMenu("feature")).Single(i => i.Header == "Rebase main onto feature");

        Assert.Same(vm.RebaseCurrentOntoCommand, item.Command);
    }

    [Fact]
    public void RemoteBranchMenu_ShouldOfferPullRebase_AndTheConfirmedRebase()
    {
        var vm = Vm();
        var headers = Headers(vm.BuildRefMenu("origin/feature"));

        Assert.Contains("Pull origin/feature into main (rebase)", headers);
        Assert.Contains("Rebase main onto origin/feature", headers);
    }

    /// <summary>The two genuine stubs ("Action coming soon!" and a diff command that only printed
    /// "Connect DiffViewer UI next") were deleted rather than surfaced — nothing should be able to
    /// route to them again.</summary>
    [Fact]
    public void BranchMenus_ShouldNotContainStubPlaceholders()
    {
        var vm = Vm();
        var all = Headers(vm.BuildRefMenu("feature"))
            .Concat(Headers(vm.BuildRefMenu("origin/feature")))
            .Concat(Headers(vm.BuildRefMenu("v1.0.0")))
            .ToList();

        Assert.DoesNotContain(all, h => h.Contains("coming soon", StringComparison.OrdinalIgnoreCase));
        Assert.All(all, h => Assert.False(string.IsNullOrWhiteSpace(h)));
    }

    /// <summary>Every non-separator entry must actually be wired — a menu item with a null command
    /// is the same defect class as a command with no menu item.</summary>
    [Fact]
    public void EveryMenuItem_ShouldBeBoundToACommand()
    {
        var vm = Vm();
        foreach (var refName in new[] { "feature", "main", "origin/feature", "v1.0.0" })
        {
            foreach (var item in Items(vm.BuildRefMenu(refName)))
            {
                Assert.True(item.Command is not null, $"'{item.Header}' on '{refName}' has no command");
            }
        }
    }

    [Fact]
    public void CurrentBranchMenu_ShouldStillOfferUpdate_ButNotSelfIntegration()
    {
        var menu = Vm().BuildRefMenu("main");
        var items = Items(menu);

        // Update/pull of the branch you are on is a plain pull — always available.
        Assert.True(items.Single(i => i.Header == "Update main from upstream").IsEnabled);
        // Merging or rebasing a branch into itself is meaningless.
        Assert.False(items.Single(i => i.Header == "Merge main into main").IsEnabled);
        Assert.False(items.Single(i => i.Header == "Rebase main onto main").IsEnabled);
        Assert.False(items.Single(i => i.Header == "Check out main and rebase it onto main").IsEnabled);
    }
}
