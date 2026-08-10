using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The destructive-action confirmation gate in <see cref="BranchBrowserViewModel"/>.
///
/// <para>These paths used to put the confirmation dialog <i>inside</i>
/// <c>if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &amp;&amp; desktop.MainWindow != null)</c>
/// while the destructive git call sat <i>outside</i> it. That inverts the gate: no desktop lifetime
/// means no prompt <b>and the action still runs</b>. It also made the confirmation impossible to
/// test without a live window, which is why none of these had a single test. They now go through
/// <see cref="IConfirmationService"/> — the same seam the commit graph's hard reset uses — so
/// declining is verifiable and a missing window fails closed.</para>
/// </summary>
public class BranchBrowserConfirmationTests
{
    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool Result { get; set; }
        public int AskCount { get; private set; }
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
        {
            AskCount++;
            LastTitle = title;
            LastMessage = message;
            return Task.FromResult(Result);
        }
    }

    private static readonly GitTagItem Tag = new() { Name = "v1.2.3" };

    private static FakeGitService FakeWithBranches()
        => new()
        {
            GetBranchesImpl = _ => new[]
            {
                new GitBranchItem { Name = "refs/heads/main", FriendlyName = "main", IsCurrentRepositoryHead = true },
                new GitBranchItem { Name = "refs/heads/feature", FriendlyName = "feature" },
            },
        };

    private static BranchBrowserViewModel NewVm(FakeGitService git, IConfirmationService confirm, List<string>? notes = null)
        => new(git, "/repo", null, notes is null ? null : notes.Add, null, null, null, confirm);

    // ---- DeleteTag -------------------------------------------------------------------

    [Fact]
    public async Task DeleteTag_WhenDeclined_ShouldNotDeleteTheTag()
    {
        var deleted = false;
        var git = new FakeGitService { DeleteTagImpl = (_, _) => deleted = true };
        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, confirm);

        await vm.DeleteTagCommand.ExecuteAsync(Tag);

        Assert.Equal(1, confirm.AskCount);
        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteTag_WhenConfirmed_ShouldDeleteTheTag()
    {
        (string repo, string name)? call = null;
        var git = new FakeGitService
        {
            DeleteTagImpl = (r, n) => call = (r, n),
            // LoadBranches runs on success.
            GetBranchesImpl = _ => Array.Empty<GitBranchItem>(),
            GetTagsImpl = _ => Array.Empty<GitTagItem>(),
            GetReflogImpl = (_, _, _) => Array.Empty<ReflogItem>(),
        };
        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, confirm);

        await vm.DeleteTagCommand.ExecuteAsync(Tag);

        Assert.Equal(1, confirm.AskCount);
        Assert.Equal("Delete Tag", confirm.LastTitle);
        Assert.NotNull(call);
        Assert.Equal("v1.2.3", call!.Value.name);
    }

    /// <summary>
    /// The inverted-gate regression itself, stated as behaviour: whatever the confirmation says
    /// "no" to must not happen. The production service says "no" when there is no window, so this
    /// also pins the headless case shut.
    /// </summary>
    [Fact]
    public async Task DeleteTag_WhenConfirmationIsUnavailable_ShouldFailClosed()
    {
        var deleted = false;
        var git = new FakeGitService { DeleteTagImpl = (_, _) => deleted = true };
        // DialogConfirmationService with no desktop lifetime — exactly what the headless/no-window
        // case gives you in production.
        var vm = NewVm(git, new DialogConfirmationService());

        await vm.DeleteTagCommand.ExecuteAsync(Tag);

        Assert.False(deleted);
    }

    // ---- DeleteRemoteTag -------------------------------------------------------------

    [Fact]
    public async Task DeleteRemoteTag_WhenDeclined_ShouldNotDeleteFromOrigin()
    {
        var deleted = false;
        var git = new FakeGitService { DeleteRemoteTagImpl = (_, _, _) => deleted = true };
        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, confirm);

        await vm.DeleteRemoteTagCommand.ExecuteAsync(Tag);

        Assert.Equal(1, confirm.AskCount);
        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteRemoteTag_WhenConfirmed_ShouldDeleteFromOrigin()
    {
        (string repo, string remote, string name)? call = null;
        var git = new FakeGitService { DeleteRemoteTagImpl = (r, rm, n) => call = (r, rm, n) };
        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, confirm);

        await vm.DeleteRemoteTagCommand.ExecuteAsync(Tag);

        Assert.Equal("Delete Remote Tag", confirm.LastTitle);
        Assert.NotNull(call);
        Assert.Equal("origin", call!.Value.remote);
        Assert.Equal("v1.2.3", call.Value.name);
    }

    [Fact]
    public async Task DeleteRemoteTag_WhenConfirmationIsUnavailable_ShouldFailClosed()
    {
        var deleted = false;
        var git = new FakeGitService { DeleteRemoteTagImpl = (_, _, _) => deleted = true };
        var vm = NewVm(git, new DialogConfirmationService());

        await vm.DeleteRemoteTagCommand.ExecuteAsync(Tag);

        Assert.False(deleted);
    }

    // ---- RebaseCurrentOnto -----------------------------------------------------------

    [Fact]
    public async Task RebaseCurrentOnto_WhenDeclined_ShouldNotRebase()
    {
        var rebased = false;
        var git = FakeWithBranches();
        git.RebaseImpl = (_, _) => rebased = true;
        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, confirm);

        await vm.RebaseCurrentOntoCommand.ExecuteAsync(new GitBranchItem { Name = "refs/heads/feature", FriendlyName = "feature" });

        Assert.Equal(1, confirm.AskCount);
        Assert.False(rebased);
    }

    [Fact]
    public async Task RebaseCurrentOnto_WhenConfirmed_ShouldRebaseOntoTheTarget()
    {
        (string repo, string target)? call = null;
        var git = FakeWithBranches();
        git.RebaseImpl = (r, t) => call = (r, t);
        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, confirm);

        await vm.RebaseCurrentOntoCommand.ExecuteAsync(new GitBranchItem { Name = "refs/heads/feature", FriendlyName = "feature" });

        Assert.Equal("Rebase", confirm.LastTitle);
        // The prompt has to name the branch whose history is about to be rewritten.
        Assert.Contains("main", confirm.LastMessage);
        Assert.NotNull(call);
        Assert.Equal("feature", call!.Value.target);
    }

    [Fact]
    public async Task RebaseCurrentOnto_WhenConfirmationIsUnavailable_ShouldFailClosed()
    {
        var rebased = false;
        var git = FakeWithBranches();
        git.RebaseImpl = (_, _) => rebased = true;
        var vm = NewVm(git, new DialogConfirmationService());

        await vm.RebaseCurrentOntoCommand.ExecuteAsync(new GitBranchItem { Name = "refs/heads/feature", FriendlyName = "feature" });

        Assert.False(rebased);
    }
}
