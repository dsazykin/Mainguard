using System;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Core.Safety;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

/// <summary>
/// <see cref="StagingPanelViewModel"/>'s commit paths — specifically the "Amend last commit"
/// checkbox, which was bound to a real, enabled control and then read by nobody: both Commit and
/// Commit &amp; Push funnel through DoCommitAsync, which always created a NEW commit. A user who
/// ticked the box got a second commit while believing they had amended.
///
/// Verified against a real fixture repo (a commit either lands on top or replaces HEAD).
/// The pre-commit scanner is switched off so these assert the commit path, not the T-30 gate.
/// </summary>
public class StagingPanelViewModelTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private StagingPanelViewModel NewPanel(out string? lastNotification)
    {
        string? captured = null;
        var vm = new StagingPanelViewModel(
            _git, _fx.RepoPath,
            onCommitAction: () => { },
            showNotification: (m, _) => captured = m,
            scanner: new PreCommitScanner(_git),
            preferences: () => new UserPreferences { PreCommitScanEnabled = false },
            settings: null);
        vm.UpdateStatus(_git.GetRepositoryStatus(_fx.RepoPath));
        foreach (var f in vm.VersionedFiles) f.IsSelected = true;
        foreach (var f in vm.UnversionedFiles) f.IsSelected = true;
        lastNotification = captured;
        return vm;
    }

    private int CommitCount()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip == null ? 0 : repo.Commits.Count();
    }

    private string HeadMessage()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip!.Message.Trim();
    }

    [Fact]
    public async Task Commit_ShouldAmendHead_WhenAmendLastCommitIsTicked()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        _fx.WriteFile("b.txt", "forgotten\n");

        var vm = NewPanel(out _);
        vm.AmendLastCommit = true;
        vm.CommitMessage = "first (amended)";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(1, CommitCount());                 // amended, not appended
        Assert.Equal("first (amended)", HeadMessage());

        using var repo = new Repository(_fx.RepoPath);
        Assert.Contains("b.txt", repo.Head.Tip!.Tree.Select(e => e.Name));
    }

    [Fact]
    public async Task Commit_ShouldClearAmendLastCommit_SoTheNextCommitIsNotAlsoAnAmend()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        _fx.WriteFile("b.txt", "forgotten\n");

        var vm = NewPanel(out _);
        vm.AmendLastCommit = true;
        vm.CommitMessage = "first (amended)";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.AmendLastCommit);

        // ...and a following commit really does append.
        _fx.WriteFile("c.txt", "next\n");
        vm.UpdateStatus(_git.GetRepositoryStatus(_fx.RepoPath));
        foreach (var f in vm.UnversionedFiles) f.IsSelected = true;
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(2, CommitCount());
        Assert.Equal("second", HeadMessage());
    }

    [Fact]
    public async Task Commit_ShouldAppend_WhenAmendLastCommitIsNotTicked()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        _fx.WriteFile("b.txt", "2\n");

        var vm = NewPanel(out _);
        vm.CommitMessage = "second";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(2, CommitCount());
        Assert.Equal("second", HeadMessage());
    }

    [Fact]
    public async Task CommitAndPush_ShouldAlsoHonourAmend()
    {
        // Commit & Push funnels through its own DoCommitAndPushAsync — the amend has to be
        // honoured there too. The push itself is allowed to fail (no remote); it is reported
        // separately and must not undo the amend.
        _fx.CommitFile("a.txt", "1\n", "first");
        _fx.WriteFile("b.txt", "forgotten\n");

        var vm = NewPanel(out _);
        vm.AmendLastCommit = true;
        vm.CommitMessage = "first (amended)";

        await vm.CommitAndPushCommand.ExecuteAsync(null);

        Assert.Equal(1, CommitCount());
        Assert.Equal("first (amended)", HeadMessage());
    }
}
