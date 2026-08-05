using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Phase 3, contract §8 step 1 — "remove every capability outside §3 from the coordinator's runtime".</b>
///
/// <para>Contract §2 states the coordinator "has no worktree, no git credentials and no view of repository
/// contents". Before this change that was true only of the <i>prompt</i>: the shipped coordinator's jail was
/// byte-for-byte a worker's — a read-write <c>/workspace</c> worktree with the repository checked out, a
/// read-write per-agent git directory, the shared mirror, and a read-write package cache. And the prompt
/// that claimed otherwise (<c>CoordinatorAgent.SystemPrompt</c>) is never delivered to the in-jail CLI at
/// all, because the shipped coordinator is a vendor CLI, not the in-process loop that constant belongs to.
/// That is §5 exactly: a system prompt is not a security boundary, and this one was not even a prompt.</para>
///
/// <para>These are pure builder tests — no Docker — so they run on every CI leg. They assert the create
/// request the daemon hands to <c>CreateContainerAsync</c>, which is the artifact that decides what the
/// jail can actually reach.</para>
/// </summary>
public class CoordinatorJailSpecTests
{
    private const string Ext4Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";
    private const string Ext4Mirror = "/home/mainguard/mainguard/repos/abc123.git";
    private const string Ext4AgentRepo = "/home/mainguard/mainguard/agents/abc123/agent-1.git";
    private const string Ext4Cache = "/home/mainguard/mainguard/caches/abc123/agent-1";
    private const string Ext4Adapters = "/home/mainguard/mainguard/adapters";
    private const string Ext4Ipc = "/home/mainguard/mainguard/ipc/agent-1";

    private static ContainerSpecRequest Request(
        bool withoutRepositoryAccess,
        string worktree = Ext4Worktree,
        string? mirror = Ext4Mirror,
        string? agentRepo = Ext4AgentRepo,
        string? cache = Ext4Cache) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: withoutRepositoryAccess ? string.Empty : worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: UsernsRemapPolicy.InheritDaemonRemap,
            AdaptersRootPath: Ext4Adapters,
            IpcDirPath: Ext4Ipc,
            BareRepoPath: withoutRepositoryAccess ? null : mirror,
            DnsServerAddress: "172.30.0.2",
            AgentRepoPath: withoutRepositoryAccess ? null : agentRepo,
            PackageCachePath: withoutRepositoryAccess ? null : cache,
            WithoutRepositoryAccess: withoutRepositoryAccess);

    /// <summary>
    /// The role lock itself: a coordinator's jail carries no path into the repository. Not the worktree,
    /// not the mirror (read-only is still "a view of repository contents"), not its own git dir, not the
    /// package cache.
    /// </summary>
    [Fact]
    public void ACoordinatorJail_MountsNoWorktree_NoMirror_NoGitDir_AndNoCache()
    {
        var create = ContainerSpecBuilder.Build(Request(withoutRepositoryAccess: true));

        var targets = create.HostConfig.Mounts.Select(m => m.Target).ToArray();

        Assert.DoesNotContain("/workspace", targets);
        Assert.DoesNotContain(Ext4Mirror, targets);
        Assert.DoesNotContain(Ext4AgentRepo, targets);
        Assert.DoesNotContain(PackageCachePolicy.SandboxMount, targets);

        // What it DOES keep: the two read-only capability mounts — the CLI it runs, and its four tools.
        Assert.Equal(
            new[] { AdapterPaths.SandboxMount, AgentIpcPaths.SandboxMount }.OrderBy(t => t).ToArray(),
            targets.OrderBy(t => t).ToArray());
    }

    /// <summary>
    /// A coordinator jail has <b>no writable bind mount at all</b>. Combined with the read-only rootfs,
    /// its only writable storage is tmpfs, which dies with the container — so nothing it writes can
    /// outlive it or reach another agent.
    /// </summary>
    [Fact]
    public void ACoordinatorJail_HasNoWritableBindMount()
    {
        var create = ContainerSpecBuilder.Build(Request(withoutRepositoryAccess: true));

        Assert.All(create.HostConfig.Mounts, m => Assert.True(m.ReadOnly, $"{m.Target} is writable"));
        Assert.True(create.HostConfig.ReadonlyRootfs);
    }

    /// <summary>
    /// <c>WorkingDir</c> is still <c>/workspace</c> and the CLI is exec'd with <c>-w /workspace</c>, so the
    /// path has to exist — an absent working directory fails the exec outright. It is an empty, agent-owned
    /// tmpfs: a cwd with no repository content that does not survive the container.
    /// </summary>
    [Fact]
    public void ACoordinatorJail_GetsAnEmptyTmpfsWorkspace_SoTheExecStillLands()
    {
        var create = ContainerSpecBuilder.Build(Request(withoutRepositoryAccess: true));

        Assert.Equal("/workspace", create.WorkingDir);
        Assert.Contains("/workspace", create.HostConfig.Tmpfs.Keys);
        Assert.Contains("uid=1000", create.HostConfig.Tmpfs["/workspace"]);
    }

    /// <summary>
    /// <b>Fail-closed.</b> The flag does not merely stop the launcher passing repository paths — the pure
    /// builder refuses them. A future caller that starts supplying a worktree for a coordinator gets a
    /// typed spawn failure instead of silently handing back the capability the role lock removed. Two
    /// copies of a policy is how one of them becomes decorative (MG-12), so the builder is the authority.
    /// </summary>
    [Theory]
    [InlineData(Ext4Worktree, null, null, null)]
    [InlineData("", Ext4Mirror, null, null)]
    [InlineData("", null, Ext4AgentRepo, null)]
    [InlineData("", null, null, Ext4Cache)]
    public void ARepositoryLessJail_RefusesAnyRepositoryPath(
        string worktree, string? mirror, string? agentRepo, string? cache)
    {
        var request = Request(withoutRepositoryAccess: true) with
        {
            WorktreePath = worktree,
            BareRepoPath = mirror,
            AgentRepoPath = agentRepo,
            PackageCachePath = cache,
        };

        var ex = Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(request));
        Assert.Contains("coordinator", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The paired negative control. A WORKER's jail is unchanged by this work — it still gets its
    /// read-write worktree, its own git dir, the mirror and the cache. Without this, every assertion above
    /// would pass on a build that had simply broken all mounting for everyone.
    /// </summary>
    [Fact]
    public void AWorkerJail_IsUnchanged_AndStillGetsItsWorktreeAndGitDir()
    {
        var create = ContainerSpecBuilder.Build(Request(withoutRepositoryAccess: false));

        var mounts = create.HostConfig.Mounts.ToDictionary(m => m.Target, m => m);

        Assert.True(mounts.ContainsKey("/workspace"));
        Assert.False(mounts["/workspace"].ReadOnly, "a worker's worktree must stay writable");
        Assert.True(mounts.ContainsKey(Ext4Mirror));
        Assert.True(mounts[Ext4Mirror].ReadOnly, "MG-3: the shared mirror is read-only");
        Assert.True(mounts.ContainsKey(Ext4AgentRepo));
        Assert.False(mounts[Ext4AgentRepo].ReadOnly, "MG-3: the per-agent git dir is the one writable git dir");
        Assert.True(mounts.ContainsKey(PackageCachePolicy.SandboxMount));

        // And a worker does NOT get the coordinator's empty /workspace tmpfs — that would shadow its
        // worktree with an empty directory.
        Assert.DoesNotContain("/workspace", create.HostConfig.Tmpfs.Keys);
    }
}
