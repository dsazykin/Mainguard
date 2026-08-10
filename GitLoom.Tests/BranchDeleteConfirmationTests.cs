using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.Core;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The Delete-key branch delete in <see cref="CommitTimelineViewModel"/>: what the confirmation
/// says, and what happens when the branch is not merged. Previously it promised "This action
/// cannot be undone" (false — a local delete is journalled and undoable) and then deleted with
/// <c>git branch -D</c> semantics regardless (also false — unmerged work was orphaned silently).
/// Runs headless because the ViewModel wires the sidebar branch browser.
/// </summary>
public class BranchDeleteConfirmationTests : IDisposable
{
    private readonly List<SqliteConnection> _connections = new();

    /// <summary>Answers each confirmation in order and records what it was asked.</summary>
    private sealed class ScriptedConfirmationService : IConfirmationService
    {
        private readonly Queue<bool> _answers;
        public ScriptedConfirmationService(params bool[] answers) => _answers = new Queue<bool>(answers);

        public List<(string Title, string Message)> Asked { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
        {
            Asked.Add((title, message));
            return Task.FromResult(_answers.Count > 0 && _answers.Dequeue());
        }
    }

    private IPinnedRefService InMemoryPins()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        _connections.Add(conn);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var ctx = new AppDbContext(options)) ctx.Database.EnsureCreated();
        return new PinnedRefService(() => new AppDbContext(options));
    }

    private CommitTimelineViewModel NewVm(GitService git, string repoPath, ScriptedConfirmationService confirm)
        => new(git, repoPath, null, confirm, InMemoryPins());

    public void Dispose()
    {
        foreach (var c in _connections) c.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>master keeps c1; "feature" carries a commit master never sees.</summary>
    private static void BuildUnmergedFeature(TempRepoFixture fx)
    {
        fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");
        fx.Checkout("feature");
        fx.CommitFile("b.txt", "feature work\n", "cF");
        fx.Checkout("master");
    }

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldNotClaimTheDeleteIsIrreversible()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");     // merged — deletes without a second prompt

        var confirm = new ScriptedConfirmationService(true);
        var vm = NewVm(new GitService(), fx.RepoPath, confirm);
        vm.SelectedRefName = "feature";

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        var message = Assert.Single(confirm.Asked).Message;
        Assert.DoesNotContain("cannot be undone", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("History", message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldKeepUnmergedBranch_WhenDiscardDeclined()
    {
        using var fx = new TempRepoFixture();
        BuildUnmergedFeature(fx);
        var git = new GitService();

        // Confirms the delete, then declines the "not merged — delete anyway?" follow-up.
        var confirm = new ScriptedConfirmationService(true, false);
        var vm = NewVm(git, fx.RepoPath, confirm);
        vm.SelectedRefName = "feature";

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        Assert.Equal(2, confirm.Asked.Count);
        Assert.Contains("not fully merged", confirm.Asked[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(git.GetBranches(fx.RepoPath), b => b.FriendlyName == "feature");
    }

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldDiscardUnmergedBranch_OnlyAfterSecondConfirmation()
    {
        using var fx = new TempRepoFixture();
        BuildUnmergedFeature(fx);
        var git = new GitService();

        var confirm = new ScriptedConfirmationService(true, true);
        var vm = NewVm(git, fx.RepoPath, confirm);
        vm.SelectedRefName = "feature";

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        Assert.Equal(2, confirm.Asked.Count);
        Assert.DoesNotContain(git.GetBranches(fx.RepoPath), b => b.FriendlyName == "feature");
    }
}
