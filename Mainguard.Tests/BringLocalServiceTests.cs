using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The cockpit's Bring local, against real temp repositories. The button was bound to a null
/// delegate for its whole life — these pin the real behavior: create the local branch, fast-forward
/// an existing one, and REFUSE (never rewrite) a diverged one; every ref move journaled (T-19).
/// </summary>
public sealed class BringLocalServiceTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private static void Git(string repo, params string[] args)
    {
        var (code, _, err) = GitService.RunGit(repo, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
        }
    }

    private static string Rev(string repo, string reference)
    {
        var (_, output, _) = GitService.RunGit(repo, "rev-parse", "--verify", reference);
        return output.Trim();
    }

    /// <summary>A checkout with a sync remote whose bare mirror carries agent/x one commit ahead.</summary>
    private (string Checkout, string Mirror) BuildPair(string agentId = "x")
    {
        var checkout = NewDir("mainguard-bringlocal-");
        Git(checkout, "-c", "init.defaultBranch=main", "init");
        Git(checkout, "config", "user.name", "T");
        Git(checkout, "config", "user.email", "t@mainguard.local");
        Git(checkout, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(checkout, "README.md"), "seed\n");
        Git(checkout, "add", "-A");
        Git(checkout, "commit", "-m", "seed");

        var mirror = NewDir("mainguard-bringlocal-mirror-");
        Git(mirror, "-c", "init.defaultBranch=main", "init", "--bare");
        Git(checkout, "remote", "add", "mainguard-local", mirror);
        Git(checkout, "push", "mainguard-local", "main");

        // The agent's branch exists only in the mirror (published by the daemon's ref mediator).
        var agentClone = NewDir("mainguard-bringlocal-agent-");
        Git(agentClone, "clone", mirror, ".");
        Git(agentClone, "config", "user.name", "A");
        Git(agentClone, "config", "user.email", "a@mainguard.local");
        Git(agentClone, "config", "commit.gpgsign", "false");
        Git(agentClone, "checkout", "-b", $"agent/{agentId}");
        File.WriteAllText(Path.Combine(agentClone, "feature.txt"), "agent work\n");
        Git(agentClone, "add", "-A");
        Git(agentClone, "commit", "-m", "agent commit");
        Git(agentClone, "push", "origin", $"agent/{agentId}");

        return (checkout, mirror);
    }

    private OperationJournal NewJournal()
    {
        var dbPath = Path.Combine(NewDir("mainguard-bringlocal-db-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory()) { db.Database.EnsureCreated(); }
        return new OperationJournal(factory);
    }

    [Fact]
    public void BringLocal_CreatesTheLocalBranch_AtTheRemoteTip_AndJournals()
    {
        var (checkout, mirror) = BuildPair();
        var journal = NewJournal();

        var result = new BringLocalService(journal).BringLocal(checkout, "mainguard-local", "x");

        Assert.True(result.Done, result.Reason);
        Assert.Equal("agent/x", result.LocalBranch);
        Assert.Equal(Rev(checkout, "refs/remotes/mainguard-local/agent/x"), Rev(checkout, "refs/heads/agent/x"));

        // Journaled: the T-19 log carries the ref move (undoable like any branch op).
        var entries = journal.GetHistory(checkout);
        Assert.Contains(entries, e => e.Kind == JournalKinds.CreateBranch && e.Description.Contains("agent/x"));

        // And HEAD did not move — bringing a branch local is not checking it out.
        var (_, head, _) = GitService.RunGit(checkout, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("main", head.Trim());
        _ = mirror;
    }

    [Fact]
    public void BringLocal_FastForwardsAnExistingLocalBranch()
    {
        var (checkout, mirror) = BuildPair();
        var svc = new BringLocalService(NewJournal());
        Assert.True(svc.BringLocal(checkout, "mainguard-local", "x").Done);
        var before = Rev(checkout, "refs/heads/agent/x");

        // The agent pushes another commit.
        var agentClone = NewDir("mainguard-bringlocal-agent2-");
        Git(agentClone, "clone", "--branch", "agent/x", mirror, ".");
        Git(agentClone, "config", "user.name", "A");
        Git(agentClone, "config", "user.email", "a@mainguard.local");
        Git(agentClone, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(agentClone, "more.txt"), "more\n");
        Git(agentClone, "add", "-A");
        Git(agentClone, "commit", "-m", "more work");
        Git(agentClone, "push", "origin", "agent/x");

        var result = svc.BringLocal(checkout, "mainguard-local", "x");

        Assert.True(result.Done, result.Reason);
        var after = Rev(checkout, "refs/heads/agent/x");
        Assert.NotEqual(before, after);
        Assert.Equal(Rev(checkout, "refs/remotes/mainguard-local/agent/x"), after);
    }

    [Fact]
    public void BringLocal_RefusesADivergedLocalBranch_RatherThanRewritingIt()
    {
        var (checkout, _) = BuildPair();
        var svc = new BringLocalService(NewJournal());
        Assert.True(svc.BringLocal(checkout, "mainguard-local", "x").Done);

        // The human commits their own work on the local agent/x — it is now AHEAD of the mirror, so
        // updating it to the mirror tip would be a rewind, and a non-forced fetch src:dst refuses.
        // Local work must never be rewritten by a bring-local.
        Git(checkout, "checkout", "agent/x");
        File.WriteAllText(Path.Combine(checkout, "local-edit.txt"), "human work\n");
        Git(checkout, "add", "-A");
        Git(checkout, "commit", "-m", "human edit");
        Git(checkout, "checkout", "main");

        var result = svc.BringLocal(checkout, "mainguard-local", "x");

        Assert.False(result.Done);
        Assert.Contains("agent/x", result.Reason);
        // The human's commit survives.
        var (_, log, _) = GitService.RunGit(checkout, "log", "--oneline", "refs/heads/agent/x");
        Assert.Contains("human edit", log);
    }

    [Fact]
    public void BringLocal_RefusesWhenTheAgentNeverPublished()
    {
        var (checkout, _) = BuildPair();

        var result = new BringLocalService(NewJournal()).BringLocal(checkout, "mainguard-local", "ghost");

        Assert.False(result.Done);
        Assert.Contains("hasn't published", result.Reason);
    }
}
