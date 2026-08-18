using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Repository = LibGit2Sharp.Repository;
namespace Mainguard.Tests;

// TI-19 round-trip cases that require the real git CLI: interactive rebase (driven through
// Mainguard.Client.App's rebase-editor argv shim, same as InteractiveRebaseServiceTests) and a live
// push (flagged non-undoable). Gated so CI can exclude them where git is unavailable.
[Trait("Category", "RequiresGitCli")]
public class OperationJournalCliTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly string _dbPath;
    private readonly OperationJournal _journal;
    private readonly GitService _git;

    public OperationJournalCliTests()
    {
        GitService.SelfInvocationOverride = TestTools.SelfInvocation.ClientAppPrefix();

        _dbPath = Path.Combine(Path.GetTempPath(), "mainguard-journal-cli-" + Guid.NewGuid().ToString("N") + ".db");
        using (var ctx = new AppDbContext(_dbPath)) ctx.Database.Migrate();
        _journal = new OperationJournal(() => new AppDbContext(_dbPath));
        _git = new GitService(null, _journal);
    }

    public void Dispose()
    {
        GitService.SelfInvocationOverride = null;
        _fx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private (Dictionary<string, string> Refs, string Head) CaptureRefState()
    {
        using var repo = new Repository(_fx.RepoPath);
        var refs = repo.Refs.OfType<DirectReference>()
            .ToDictionary(r => r.CanonicalName, r => r.TargetIdentifier, StringComparer.Ordinal);
        var head = repo.Refs.Head is SymbolicReference sym ? sym.TargetIdentifier : repo.Refs.Head.TargetIdentifier;
        return (refs, head);
    }

    private long LatestEntryId()
    {
        using var ctx = new AppDbContext(_dbPath);
        return ctx.JournalEntries.OrderByDescending(e => e.Id).First().Id;
    }

    [Fact]
    public void RoundTrip_InteractiveRebase_UndoRestoresPreState_RedoRestoresPostState()
    {
        var svc = new InteractiveRebaseService(_journal);
        var c0 = _fx.CommitFile("base.txt", "b\n", "base");
        _fx.CommitFile("a.txt", "a\n", "c1");
        _fx.CommitFile("b.txt", "b\n", "c2");
        _fx.CommitFile("c.txt", "c\n", "c3");

        // Drop c2; c3 replays cleanly onto c1 (disjoint files) — the branch tip is rewritten.
        var plan = svc.GetRebasePlan(_fx.RepoPath, c0).ToList();
        var c2 = plan.Single(p => p.Message == "c2");
        c2.Action = RebaseAction.Drop;

        var pre = CaptureRefState();
        svc.StartInteractiveRebase(_fx.RepoPath, c0, plan);
        var post = CaptureRefState();
        Assert.NotEqual(pre.Refs["refs/heads/master"], post.Refs["refs/heads/master"]); // tip really moved

        var entryId = LatestEntryId();

        _journal.Undo(_fx.RepoPath, entryId);
        Assert.Equal(pre.Refs, CaptureRefState().Refs);
        Assert.Equal(pre.Head, CaptureRefState().Head);

        _journal.Redo(_fx.RepoPath, entryId);
        Assert.Equal(post.Refs, CaptureRefState().Refs);
    }

    [Fact]
    public void Push_ShouldBeJournaledFlagged_WithReason_AndRefuseUndo()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.AddBareRemote("origin");
        _fx.WriteFile("a.txt", "2\n");
        _git.StageFile(_fx.RepoPath, "a.txt");
        _git.Commit(_fx.RepoPath, "c2");

        _git.Push(_fx.RepoPath);

        var push = _journal.GetHistory(_fx.RepoPath).First(e => e.Kind == JournalKinds.Push);
        Assert.False(push.IsUndoable);
        Assert.False(string.IsNullOrWhiteSpace(push.UndoBlockedReason));
        Assert.Throws<UndoBlockedException>(() => _journal.Undo(_fx.RepoPath, push.Id));
    }
}
