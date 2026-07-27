using System;
using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-3 — the per-agent repository layout, the agent-id gate, and the §4 gc policy that makes an
/// alternates-borrowing layout safe to live with.
/// </summary>
public sealed class AgentRepoTests
{
    // ---- The id gate ------------------------------------------------------

    /// <summary>
    /// The agent id names BOTH a directory under <c>agents/</c> and a component of
    /// <c>refs/heads/agent/&lt;id&gt;</c>. A traversal-shaped id would put the "per-agent" repository on
    /// top of the shared mirror — the one directory the whole design depends on the jail not owning.
    /// </summary>
    [Theory]
    [InlineData("../../repos/deadbeef")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("-dashed")]
    [InlineData("work.lock")]
    [InlineData("")]
    public void AgentId_ThatIsNotASafePathAndRefComponent_IsRefusedTyped(string agentId)
    {
        Assert.Throws<RepoProvisioningException>(
            () => AgentRepoLayout.AgentRepoPath("/vm", "hash", agentId));
        Assert.Throws<RepoProvisioningException>(() => AgentRepoLayout.RefFor(agentId));
    }

    [Theory]
    [InlineData("a1")]
    [InlineData("pr-42")]
    [InlineData("0f7c1b2a3d4e5f60718293a4b5c6d7e8")]
    public void AgentId_TheShapesTheDaemonActuallyMints_AreAccepted(string agentId)
    {
        Assert.Equal(
            Path.Combine("/vm", "agents", "hash", agentId + ".git"),
            AgentRepoLayout.AgentRepoPath("/vm", "hash", agentId));
        Assert.Equal("refs/heads/agent/" + agentId, AgentRepoLayout.RefFor(agentId));
    }

    // ---- §4: gc policy ----------------------------------------------------

    /// <summary>
    /// <c>gc.auto=0</c> on the mirror. Git fires <c>gc --auto</c> implicitly after many ordinary
    /// commands — including the incremental fetch the provisioner itself runs — and an implicit prune
    /// deletes objects live agent repositories are borrowing through their alternate. Git tracks no
    /// borrowers, so nothing else would notice until an agent's repo simply stopped working.
    /// </summary>
    [Fact]
    public void Mirror_DisablesAutomaticGc_SoAnImplicitPruneCannotBreakBorrowers()
    {
        using var env = new AgentRepoEnv();
        var hash = env.Provision();

        Assert.Equal("0", AgentTestGit.RunChecked(env.BarePath(hash), "config", "gc.auto").Trim());

        // Re-provisioning an EXISTING mirror re-asserts it, so a mirror cloned before this policy is
        // repaired by a daemon update alone rather than needing a re-clone.
        AgentTestGit.RunChecked(env.BarePath(hash), "config", "gc.auto", "6700");
        env.Provision();
        Assert.Equal("0", AgentTestGit.RunChecked(env.BarePath(hash), "config", "gc.auto").Trim());
    }

    /// <summary>
    /// The safe half of gc: repacking consolidates loose objects and re-deltas them but <b>deletes
    /// nothing</b>, so it may run with agents attached. The discriminator is the flag: git documents
    /// <c>repack -a -d</c> as also cleaning up what <c>git prune</c> would leave behind, which is
    /// exactly the deletion an alternates borrower cannot survive; <c>-A</c> loosens unreachable objects
    /// instead of dropping them. Measured against git 2.53: <c>-a -d</c> removes the object below,
    /// <c>-A -d</c> keeps it.
    /// </summary>
    [Fact]
    public void RepackWithoutPrune_KeepsEveryObjectResolvable_EvenUnreachableOnesAgentsMayBorrow()
    {
        using var env = new AgentRepoEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        // An object no mirror ref reaches — the shape of thing a borrower can still depend on.
        var orphan = env.WriteOrphanBlob(bare);
        // Push it inside a pack (setup uses `gc`, never the method under test) — the discrimination
        // between -a and -A only exists for unreachable objects that live in a pack.
        AgentTestGit.RunChecked(bare, "gc", "--prune=never", "--quiet");
        Assert.Equal(0, AgentTestGit.Run(bare, "cat-file", "-e", orphan).Code);

        Assert.True(MirrorMaintenance.RepackWithoutPrune(bare));

        Assert.Equal(0, AgentTestGit.Run(bare, "cat-file", "-e", orphan).Code);
        // …and the mirror is still a working repository afterwards.
        Assert.Equal("commit", AgentTestGit.RunChecked(bare, "cat-file", "-t", "HEAD").Trim());
    }

    /// <summary>
    /// The unsafe half: a full prune runs ONLY when the mirror has no alternates borrower left. While an
    /// agent is attached the call does nothing and says so; once the last agent detaches the same call
    /// reclaims the tail.
    /// </summary>
    [Fact]
    public void Prune_IsRefusedWhileAnAgentIsAttached_AndReclaimsOnceTheLastOneDetaches()
    {
        using var env = new AgentRepoEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);
        env.Worktrees.CreateAgentWorktree(hash, "a1");

        var orphan = env.WriteOrphanBlob(bare);
        AgentTestGit.RunChecked(bare, "gc", "--prune=never", "--quiet");

        // Attached: refused, and the object is untouched.
        Assert.False(MirrorMaintenance.PruneWhenIdle(bare, env.AgentRepos, hash));
        Assert.Equal(0, AgentTestGit.Run(bare, "cat-file", "-e", orphan).Code);

        // The last agent detaches (teardown deletes its repository) → the prune is allowed and reclaims.
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(env.AgentRepos.AnyAttached(hash));
        Assert.True(MirrorMaintenance.PruneWhenIdle(bare, env.AgentRepos, hash));
        Assert.NotEqual(0, AgentTestGit.Run(bare, "cat-file", "-e", orphan).Code);
    }

    // ---- Teardown ---------------------------------------------------------

    /// <summary>
    /// A publish is a real object transfer, not a second alternate: the mirror borrows from nobody, so
    /// destroying the agent's repository cannot strand the commit the mirror's <c>agent/&lt;id&gt;</c>
    /// still names. That is the property that makes teardown (which deletes the whole per-agent repo)
    /// safe, and the property a "just add another alternate" shortcut would quietly not have.
    /// </summary>
    [Fact]
    public void PublishedWork_IsCopiedIntoTheMirror_NotBorrowedFromTheAgentRepository()
    {
        using var env = new AgentRepoEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");

        AgentTestGit.SetIdentity(worktree);
        File.WriteAllText(Path.Combine(worktree, "agent.txt"), "work\n");
        AgentTestGit.RunChecked(worktree, "add", "agent.txt");
        AgentTestGit.RunChecked(worktree, "commit", "-m", "agent work");
        var sha = AgentTestGit.RunChecked(worktree, "rev-parse", "HEAD").Trim();
        Assert.True(env.Worktrees.PublishAgentBranch(hash, "a1"));

        // Destroy the agent's repository under the mirror's feet.
        env.AgentRepos.Remove(hash, "a1");
        Assert.False(Directory.Exists(env.Worktrees.AgentRepoPathFor(hash, "a1")));

        Assert.Equal(sha, AgentTestGit.RunChecked(bare, "rev-parse", "refs/heads/agent/a1").Trim());
        Assert.Equal("commit", AgentTestGit.RunChecked(bare, "cat-file", "-t", sha).Trim());
        Assert.Contains("agent.txt", AgentTestGit.RunChecked(bare, "show", "--stat", sha));
    }

    /// <summary>Teardown leaves no residue anywhere: the worktree, the per-agent repository and the
    /// mirror's copy of the branch all go.</summary>
    [Fact]
    public void Teardown_RemovesTheWorktree_ThePerAgentRepository_AndTheMirrorsBranch()
    {
        using var env = new AgentRepoEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);
        var worktree = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var agentRepo = env.Worktrees.AgentRepoPathFor(hash, "a1");
        Assert.True(Directory.Exists(agentRepo));

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);

        Assert.False(Directory.Exists(agentRepo));
        Assert.False(Directory.Exists(worktree));
        Assert.NotEqual(0, AgentTestGit.Run(bare, "rev-parse", "--verify", "--quiet", "refs/heads/agent/a1").Code);
        Assert.False(env.AgentRepos.AnyAttached(hash));
    }

    /// <summary>A provisioned mirror plus a wired worktree manager over a temp VM root.</summary>
    private sealed class AgentRepoEnv : IDisposable
    {
        private readonly string _vmRoot = AgentTestGit.NewVmRoot();
        private readonly DualRepoFixture _fixture = new();
        private readonly RepoProvisioner _provisioner;

        public AgentRepoEnv()
        {
            _provisioner = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot);
            AgentRepos = new AgentRepoManager(_vmRoot);
        }

        public WorktreeManager Worktrees { get; }

        public AgentRepoManager AgentRepos { get; }

        public string Provision() => _provisioner.Provision(_fixture.WorkRepoPath).RepoHash;

        public string BarePath(string hash) => _provisioner.BareRepoPathFor(hash);

        /// <summary>Writes a blob no ref reaches, and returns its sha.</summary>
        public string WriteOrphanBlob(string barePath)
        {
            var scratch = Path.Combine(_vmRoot, "orphan-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(scratch, "an object a borrower may still need\n");
            return AgentTestGit.RunChecked(barePath, "hash-object", "-w", scratch).Trim();
        }

        public void Dispose()
        {
            _fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
