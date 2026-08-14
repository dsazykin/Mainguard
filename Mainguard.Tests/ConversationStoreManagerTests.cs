using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The IO half of the conversation store: the on-disk layout, the guard that decides whether a resumed
/// jail gets its CLI's resume flag, and the lifecycle rule about when a store is dropped.
///
/// <para>Real directories under a temp VM root — the store is a filesystem feature and a fake would only
/// prove the fake works. No Docker is needed for any of it.</para>
/// </summary>
public sealed class ConversationStoreManagerTests : IDisposable
{
    private const string Declared = ".claude/projects";
    private const string Repo = "abc123";

    private readonly string _vmRoot = Path.Combine(
        Path.GetTempPath(), "mg-conv-" + Guid.NewGuid().ToString("N"));

    private ConversationStoreManager NewManager() => new(_vmRoot);

    private static readonly string[] ClaudePaths = { Declared };
    private static readonly string[] ClaudeCredentials = { ".claude/.credentials.json", ".claude.json" };

    // ---- Layout ------------------------------------------------------------------------------------

    [Fact]
    public void Prepare_CreatesTheStore_OutsideTheWorktreeAndOutsideAnyTmpfsHome()
    {
        var mount = Assert.Single(NewManager().Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));

        Assert.True(Directory.Exists(mount.HostPath));
        // Under conversations/<repo>/<agent>/, mirroring the CLI's own $HOME layout.
        Assert.Equal(
            Path.Combine(_vmRoot, "conversations", Repo, "agent-1", ".claude", "projects"),
            mount.HostPath);
        // The two structural facts, restated where the path is actually produced rather than only where
        // it is asserted on the container spec.
        Assert.DoesNotContain("worktrees", mount.HostPath.Split(Path.DirectorySeparatorChar, '/'));
        Assert.False(mount.HostPath.StartsWith(ContainerSpecBuilder.AgentHome, StringComparison.Ordinal));
    }

    [Fact]
    public void Prepare_TargetsTheCLIsOwnPathUnderHome()
        => Assert.Equal(
            ContainerSpecBuilder.AgentHome + "/" + Declared,
            Assert.Single(NewManager().Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials))
                .SandboxTarget);

    [Fact]
    public void Prepare_IsScopedByTheREPOANDAGENTPAIR_NotByIdAlone()
    {
        // Agent ids are unique per repo and NOT globally: the external-PR intake names its entries
        // pr-<n>, so two subscribed repositories both hold a `pr-7`. This exact collision has been fixed
        // four separate times in this codebase, and here it would cross-mount one pull request author's
        // conversation into another's jail.
        var manager = NewManager();
        var a = Assert.Single(manager.Prepare("repo-a", "pr-7", "claude-code", ClaudePaths, ClaudeCredentials));
        var b = Assert.Single(manager.Prepare("repo-b", "pr-7", "claude-code", ClaudePaths, ClaudeCredentials));

        Assert.NotEqual(a.HostPath, b.HostPath);
    }

    [Fact]
    public void Prepare_IsIdempotent_SoARespawnReusesTheSameStore()
    {
        // The whole feature: the SECOND jail for this (repo, agent) must land on the FIRST one's
        // directory, or a resume would mount an empty store and the transcripts would be unreachable
        // even though they survived.
        var manager = NewManager();
        var first = Assert.Single(manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));
        File.WriteAllText(Path.Combine(first.HostPath, "session.jsonl"), "{}\n");

        var second = Assert.Single(manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));

        Assert.Equal(first.HostPath, second.HostPath);
        Assert.True(File.Exists(Path.Combine(second.HostPath, "session.jsonl")));
    }

    [Fact]
    public void Prepare_RefusesAnAdapterWhoseConversationPathCouldHoldACredential()
    {
        // The spawn-side half of the invariant. The manifest is reviewed product source, but the daemon
        // spawns from an install MARKER in a user-writable VM path, possibly written by an older build —
        // so the gate has to sit where the paths are USED, and it has to leave no store behind.
        var manager = NewManager();
        Assert.Throws<ConversationStoreOverlapException>(
            () => manager.Prepare(Repo, "agent-1", "claude-code", new[] { ".claude" }, ClaudeCredentials));

        Assert.False(Directory.Exists(Path.Combine(_vmRoot, "conversations", Repo, "agent-1")),
            "a refused declaration must not leave a credential-shaped directory on disk");
    }

    [Fact]
    public void NoDeclaredPaths_YieldsNoMounts_AndIsNotAnError()
        // "This CLI gets no persistence yet" is a supported state, not a failure: four of the five
        // bundled adapters are in it deliberately.
        => Assert.Empty(NewManager().Prepare(Repo, "agent-1", "codex", conversationPaths: null,
            credentialPaths: new[] { ".codex/auth.json" }));

    // ---- The resume guard --------------------------------------------------------------------------

    [Fact]
    public void HasTranscripts_IsFalse_OnAFreshStore()
    {
        // THE guard, and the reason it asks about FILES rather than about the directory: Prepare creates
        // the directory on every spawn including the very first, so a directory-existence check would be
        // permanently true — i.e. no guard at all — and every first-ever resume would hand the CLI a
        // resume flag with nothing to resume.
        var manager = NewManager();
        manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials);

        Assert.False(manager.HasTranscripts(Repo, "agent-1", ClaudePaths));
    }

    [Fact]
    public void HasTranscripts_IsTrue_OnceASessionWroteATranscript()
    {
        var manager = NewManager();
        var mount = Assert.Single(manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));
        // The real shape: .claude/projects/<escaped-cwd>/<session-uuid>.jsonl, and /workspace escapes to
        // "-workspace" — identical in every jail for this agent, which is what makes a remount enough.
        var project = Path.Combine(mount.HostPath, "-workspace");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, Guid.NewGuid().ToString("D") + ".jsonl"), "{}\n");

        Assert.True(manager.HasTranscripts(Repo, "agent-1", ClaudePaths));
    }

    [Fact]
    public void HasTranscripts_IsFalse_ForADIFFERENTAgentInTheSameRepo()
    {
        var manager = NewManager();
        var mount = Assert.Single(manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));
        File.WriteAllText(Path.Combine(mount.HostPath, "session.jsonl"), "{}\n");

        Assert.False(manager.HasTranscripts(Repo, "agent-2", ClaudePaths));
    }

    [Fact]
    public void HasTranscripts_IsFalse_WhenNothingWasEverPrepared()
        => Assert.False(NewManager().HasTranscripts(Repo, "never-existed", ClaudePaths));

    // ---- Lifecycle ---------------------------------------------------------------------------------

    [Fact]
    public void Release_DropsTheWholeStoreForThatPair_AndLeavesEveryOtherAlone()
    {
        // Release runs from the FINAL teardown (WorktreeManager.RemoveAgentWorktree — the path that also
        // deletes the branch), so the conversation goes exactly when the work it is about goes. What it
        // must never do is reach past its own (repo, agent).
        var manager = NewManager();
        var mine = Assert.Single(manager.Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));
        var theirs = Assert.Single(manager.Prepare(Repo, "agent-2", "claude-code", ClaudePaths, ClaudeCredentials));
        File.WriteAllText(Path.Combine(mine.HostPath, "session.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(theirs.HostPath, "session.jsonl"), "{}\n");

        manager.Release(Repo, "agent-1");

        Assert.False(Directory.Exists(Path.Combine(_vmRoot, "conversations", Repo, "agent-1")));
        Assert.True(File.Exists(Path.Combine(theirs.HostPath, "session.jsonl")));
    }

    [Fact]
    public void Release_OfOneReposAgent_LeavesTheSameIdInAnotherRepoIntact()
    {
        // The (repo, agent) scoping trap again, on the destructive side where it costs the most.
        var manager = NewManager();
        var a = Assert.Single(manager.Prepare("repo-a", "pr-7", "claude-code", ClaudePaths, ClaudeCredentials));
        var b = Assert.Single(manager.Prepare("repo-b", "pr-7", "claude-code", ClaudePaths, ClaudeCredentials));
        File.WriteAllText(Path.Combine(a.HostPath, "s.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(b.HostPath, "s.jsonl"), "{}\n");

        manager.Release("repo-a", "pr-7");

        Assert.False(File.Exists(Path.Combine(a.HostPath, "s.jsonl")));
        Assert.True(File.Exists(Path.Combine(b.HostPath, "s.jsonl")));
    }

    [Fact]
    public void Release_OfAStoreThatNeverExisted_DoesNotThrow()
        // A teardown must never fail over disk residue.
        => NewManager().Release(Repo, "never-existed");

    // ---- Layout policy is the one source of truth ---------------------------------------------------

    [Fact]
    public void TheStoreRoot_IsASiblingOfTheOtherVmTrees()
        // It has to be, or the MG-17 boot step's group-share does not reach it and the jail cannot write
        // its own transcripts.
        => Assert.Equal(
            Path.Combine(_vmRoot, ConversationStorePolicy.ConversationsDirectoryName),
            NewManager().RootPath);

    [Fact]
    public void APathInsideTheStoreTree_IsRecognisedByTheStructuralGuard()
    {
        // The guard ContainerSpecBuilder uses is whole-segment, and it must recognise the real paths this
        // manager hands it — otherwise every spawn would be refused by its own store.
        var mount = Assert.Single(NewManager().Prepare(Repo, "agent-1", "claude-code", ClaudePaths, ClaudeCredentials));
        Assert.True(ConversationStorePolicy.IsInsideAConversationTree(mount.HostPath.Replace('\\', '/')));
        Assert.False(ConversationStorePolicy.IsInsideAConversationTree(
            "/home/mainguard/mainguard/repos-conversations-backup/x"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_vmRoot))
            {
                Directory.Delete(_vmRoot, recursive: true);
            }
        }
        catch
        {
            // never fail a test from cleanup
        }
    }
}
