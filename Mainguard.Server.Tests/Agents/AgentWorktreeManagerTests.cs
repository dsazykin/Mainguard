using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-06 worktree + quarantine-remote tests on the <see cref="DualRepoFixture"/>: add/remove/
/// prune round-trip, duplicate-id + dirty-remove typed failures, quarantine-only remotes, the
/// agent-push-lands-in-bare invariant, the byte-identical Windows↔VM round-trip, the pnpm hook,
/// and the SC-2 resolved-name path.
/// </summary>
public sealed class AgentWorktreeManagerTests
{
    // MG-17: the worktree is bind-mounted READ-WRITE into a jail whose host uid/gid is 101000 (the
    // userns remap), not the daemon's 1000. A checkout laid down under the daemon's 022 umask is 0644
    // files / 0755 dirs — readable by the agent and NOT editable, which is the product silently broken.
    [LinuxOnlyFact]
    // The attribute already skips on Windows; the annotation is what tells the CA1416 analyzer so.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Worktree_Checkout_IsGroupWritableForTheRemappedJail()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        var root = File.GetUnixFileMode(path);
        Assert.True(root.HasFlag(UnixFileMode.GroupWrite), "the worktree root must be group-writable");
        // setgid so files the agent creates keep the shared jail group (the daemon has to read them).
        Assert.True(root.HasFlag(UnixFileMode.SetGroup), "the worktree root must be setgid");

        // Every checked-out file, not just the root — this is the one the agent actually edits.
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git", System.StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.True(File.GetUnixFileMode(file).HasFlag(UnixFileMode.GroupWrite), file + " must be group-writable");
        }
    }

    [Fact]
    public void Worktree_AddRemovePrune_RoundTrip()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        Assert.True(Directory.Exists(path));

        // Listed via the porcelain parser; the agent branch exists.
        var listed = env.Worktrees.List(hash);
        Assert.Contains(listed, w => w.Branch == "agent/a1");
        Assert.Equal("commit",
            AgentTestGit.RunChecked(env.BarePath(hash), "cat-file", "-t", "refs/heads/agent/a1").Trim());

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false);
        env.Worktrees.Prune(hash);

        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(env.Worktrees.List(hash), w => w.Branch == "agent/a1");
        // The agent branch is gone (no residue).
        Assert.NotEqual(0, AgentTestGit.Run(env.BarePath(hash), "rev-parse", "--verify", "--quiet", "refs/heads/agent/a1").Code);
    }

    [Fact]
    public void Worktree_DuplicateAgentId_ThrowsTyped_NoResidue()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var before = env.Worktrees.List(hash).Count;

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.CreateAgentWorktree(hash, "a1"));

        // No new worktree/branch left behind by the refused call.
        Assert.Equal(before, env.Worktrees.List(hash).Count);
    }

    [Fact]
    public void Worktree_DirtyRemove_ForceSemantics()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        // Make the worktree dirty.
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted\n");

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false));
        Assert.True(Directory.Exists(path)); // refused, still there

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(Directory.Exists(path)); // force cleans
    }

    [Fact]
    public void QuarantineRemote_IsExactlyTheDaemonBareRepo()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        // Exactly one configured remote, named origin, pointing at the bare mirror.
        var remotes = AgentTestGit.RunChecked(path, "remote").Trim()
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "origin" }, remotes);

        var originUrl = AgentTestGit.RunChecked(path, "remote", "get-url", "origin").Trim();
        Assert.Equal(bare, originUrl);

        // Not the user's real remote (the fixture's work repo or its separate mirror).
        Assert.NotEqual(env.Fixture.WorkRepoPath, originUrl);
        Assert.NotEqual(env.Fixture.BareMirrorPath, originUrl);

        // The mirror itself denies rewrites/deletes.
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyNonFastForwards").Trim());
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyDeletes").Trim());
    }

    [Fact]
    public void AgentPush_LandsInBareRepo_NeverUpstream()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        // The fixture's separate mirror stands in for the user's real remote.
        var upstreamBefore = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "from-agent\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent work");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        // The push moved the quarantine bare's ref...
        Assert.Equal("commit",
            AgentTestGit.RunChecked(env.BarePath(hash), "cat-file", "-t", "refs/heads/agent/a1").Trim());
        // ...and left the "real remote" completely untouched.
        var upstreamAfter = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);
        Assert.Equal(upstreamBefore, upstreamAfter);
    }

    [Fact]
    public void WindowsVm_CommitRoundTrip_ByteIdentical()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var content = "round-trip payload\n";

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), content);
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent round trip");
        var agentSha = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        // Windows side: register the SC-2-resolved sync remote and fetch + merge the agent branch.
        var remote = env.Env.ResolveSyncRemote(hash);
        AgentTestGit.Run(env.Fixture.WorkRepoPath, "remote", "remove", remote.Name); // idempotent
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        // The merged commit is byte-identical (same SHA), and the blob matches.
        var windowsSha = AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(agentSha, windowsSha);
        Assert.Equal(content, File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    [Fact]
    public void SyncRemote_NameIsResolvedNotHardcoded_RoundTripUsesCloudName()
    {
        using var env = new WorktreeEnv(syncRemoteName: "mainguard-cloud");
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        var remote = env.Env.ResolveSyncRemote(hash);
        Assert.Equal("mainguard-cloud", remote.Name); // the resolved name, not a hardcoded mainguard-vm

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "cloud\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "cloud round trip");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        Assert.Equal("cloud\n", File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    // MG-1: the bare mirror is bind-mounted into the jail read-write, so a hostile agent can plant
    // hooks in <bare>/hooks or redirect core.hooksPath in <bare>/config. The daemon then runs git
    // against that directory OUTSIDE the jail (worktree add/remove/prune, branch -D, status). Every
    // daemon-side git must neutralize hooks/fsmonitor so none of those planted hooks ever execute.
    [Fact]
    public void DaemonGit_NeverRunsHooks_PlantedByAgentInBareMirror()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        var sentinel = Path.Combine(Path.GetTempPath(), "mg1-hook-fired-" + System.Guid.NewGuid().ToString("N"));

        // (1) Hooks in the default <bare>/hooks dir, covering ref-update and checkout events.
        PlantHook(Path.Combine(bare, "hooks"), "reference-transaction", sentinel);
        PlantHook(Path.Combine(bare, "hooks"), "post-checkout", sentinel);
        PlantHook(Path.Combine(bare, "hooks"), "post-index-change", sentinel);

        // (2) A core.hooksPath redirect written into the agent-writable config, pointing at a hostile
        //     dir — proves the command-line override wins over repo-local config.
        var evilHooks = Path.Combine(bare, "evil-hooks");
        PlantHook(evilHooks, "reference-transaction", sentinel);
        PlantHook(evilHooks, "post-checkout", sentinel);
        AgentTestGit.RunChecked(bare, "config", "core.hooksPath", evilHooks);

        // Exercise every daemon-side operation that updates refs or checks out a tree.
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");   // ref create + checkout
        File.WriteAllText(Path.Combine(path, "x.txt"), "x\n");
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true); // ref delete + worktree remove
        env.Worktrees.Prune(hash);                                  // prune

        Assert.False(
            File.Exists(sentinel),
            "a hook planted in the agent-writable bare mirror was executed by daemon-side git (MG-1)");
    }

    // Control: with hooks NEUTRALIZED, the same operations still succeed end-to-end.
    [Fact]
    public void DaemonGit_WithNeutralizedHooks_StillCompletesWorktreeLifecycle()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var bare = env.BarePath(hash);

        // A reference-transaction hook that ABORTS (exit 1) would break every ref update if it ran.
        var hooksDir = Path.Combine(bare, "hooks");
        var abortHook = Path.Combine(hooksDir, "reference-transaction");
        Directory.CreateDirectory(hooksDir);
        File.WriteAllText(abortHook, "#!/bin/sh\nexit 1\n");
        MakeExecutable(abortHook);

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1"); // would fail if the hook fired
        Assert.True(Directory.Exists(path));
        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(Directory.Exists(path));
    }

    private static void PlantHook(string hooksDir, string hookName, string sentinelPath)
    {
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, hookName);
        // Touch a sentinel the instant the hook runs. Cross-platform: git (incl. Git for Windows)
        // runs hooks through /bin/sh.
        File.WriteAllText(hookPath, "#!/bin/sh\ntouch \"" + sentinelPath.Replace("\\", "/") + "\"\nexit 0\n");
        MakeExecutable(hookPath);
    }

    private static void MakeExecutable(string filePath)
    {
        if (!System.OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    [Fact]
    public void Pnpm_InstallFailure_NonFatal_WorktreeStillCreated()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            // Seed a lockfile so the pnpm hook fires.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");

            var warnings = new List<string>();
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(
                vmRoot,
                pnpmRunner: _ => (1, "boom"),      // simulate failure
                warningSink: warnings.Add);

            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            var path = worktrees.CreateAgentWorktree(hash, "a1");

            Assert.True(Directory.Exists(path));                    // still created
            Assert.Contains(warnings, w => w.Contains("pnpm"));     // warning surfaced
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Pnpm_Install_RunsOnlyWhenLockfilePresent()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            string? ranIn = null;
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(vmRoot, pnpmRunner: dir => { ranIn = dir; return (0, string.Empty); });

            // No lockfile in the seed repo → the hook does not fire.
            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            worktrees.CreateAgentWorktree(hash, "a1");
            Assert.Null(ranIn);

            // Commit a lockfile, re-provision (incremental fetch), then a new worktree fires the hook.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");
            provisioner.Provision(fixture.WorkRepoPath);
            var path = worktrees.CreateAgentWorktree(hash, "a2");

            Assert.Equal(path, ranIn); // issued, in the a2 worktree
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    /// <summary>A test substrate facade: real provisioner + worktree manager, resolvable remote name.</summary>
    private sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly string _syncRemoteName;
        private readonly RepoProvisioner _provisioner;

        public FakeAgentEnvironment(string syncRemoteName, RepoProvisioner provisioner, IAgentWorktreeManager worktrees)
        {
            _syncRemoteName = syncRemoteName;
            _provisioner = provisioner;
            Repos = provisioner;
            Worktrees = worktrees;
        }

        public string SubstrateId => "test";
        public SubstrateCapabilities Capabilities { get; } = new(false, false, "ext4-native", "test");
        public IRepoProvisioner Repos { get; }
        public IAgentWorktreeManager Worktrees { get; }

        // P2-07 seam members: this worktree-only double never touches sandboxes/egress.
        public Mainguard.Agents.Agents.Sandbox.ISandboxEngine Sandboxes =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");
        public Mainguard.Agents.Agents.Sandbox.IEgressPolicy Egress =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");

        // Resolves to the LOCAL bare path (the test's "windows-facing" handle) under the given name.
        public SyncRemote ResolveSyncRemote(string repoHash)
            => new(_syncRemoteName, _provisioner.BareRepoPathFor(repoHash));
    }

    /// <summary>Bundles a fixture + temp VM root + wired services, cleaned up on dispose.</summary>
    private sealed class WorktreeEnv : System.IDisposable
    {
        private readonly string _vmRoot;

        public WorktreeEnv(string syncRemoteName = "mainguard-vm")
        {
            Fixture = new DualRepoFixture();
            _vmRoot = AgentTestGit.NewVmRoot();
            var provisioner = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot);
            Env = new FakeAgentEnvironment(syncRemoteName, provisioner, Worktrees);
        }

        public DualRepoFixture Fixture { get; }
        public IAgentEnvironment Env { get; }
        public WorktreeManager Worktrees { get; }

        public string Provision() => Env.Repos.Provision(Fixture.WorkRepoPath).RepoHash;
        public string BarePath(string hash) => Path.Combine(_vmRoot, "repos", hash + ".git");

        public void Dispose()
        {
            Fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
