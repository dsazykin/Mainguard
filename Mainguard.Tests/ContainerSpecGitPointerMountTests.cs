using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// W1-A — the worktree's <c>.git</c> pointer file must not be agent-writable.
///
/// <para>It holds one line (<c>gitdir: &lt;abs VM path&gt;</c>), <c>git worktree add</c> writes it once,
/// and nothing ever writes it again. But it lives inside the read-write <c>/workspace</c> mount, and it
/// is the first thing any git run with the worktree as its working directory reads — including the
/// daemon's, which runs outside the jail as the daemon user. Pointing it at the shared mirror was the
/// audit's "plausible second vector": the mirror is read-only to every jail precisely so no agent can
/// reach it, and a daemon that follows the pointer reaches it on the agent's behalf.</para>
///
/// <para>The daemon-side half of that is closed by <c>TrustedWorktreeLayout</c> (which never reads the
/// pointer when the caller supplies the repository, and refuses a mirror redirect when it does). This is
/// the other half: the file simply stops being writable.</para>
/// </summary>
public class ContainerSpecGitPointerMountTests
{
    private const string Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";
    private const string AgentRepo = "/home/mainguard/mainguard/agents/abc123/agent-1.git";
    private const string Mirror = "/home/mainguard/mainguard/repos/abc123.git";

    [Fact]
    public void AgentRepoJail_MountsTheGitPointerFileReadOnly_InsideTheWritableWorkspace()
    {
        var mounts = ContainerSpecBuilder.Build(Request()).HostConfig.Mounts;

        var pointer = Assert.Single(mounts, m => m.Target == "/workspace/.git");
        Assert.Equal("bind", pointer.Type);
        Assert.Equal(Worktree + "/.git", pointer.Source);
        Assert.True(pointer.ReadOnly, "the worktree's .git pointer file is writable from inside the jail");

        // …and it is genuinely nested: the workspace itself stays read-write, so this removes nothing the
        // agent does. A read-only /workspace would be a different (and wrong) change.
        var workspace = Assert.Single(mounts, m => m.Target == "/workspace");
        Assert.False(workspace.ReadOnly);
    }

    /// <summary>The pointer file only EXISTS in the MG-3 layout, where the worktree is linked off a
    /// per-agent repository. Without one, <c>.git</c> is an ordinary directory and mounting a file over it
    /// would fail the container create outright — so the mount travels with `AgentRepoPath`, never alone.</summary>
    [Fact]
    public void JailWithoutAPerAgentRepo_GetsNoPointerMount()
    {
        var mounts = ContainerSpecBuilder.Build(Request(agentRepo: null)).HostConfig.Mounts;

        Assert.DoesNotContain(mounts, m => m.Target == "/workspace/.git");
        Assert.Single(mounts, m => m.Target == "/workspace");
    }

    /// <summary>The mirror stays read-only and the per-agent repo stays read-write: W1-A tightens one
    /// file and changes neither of MG-3's two decisions.</summary>
    [Fact]
    public void Mg3MountPosture_IsUnchanged()
    {
        var mounts = ContainerSpecBuilder.Build(Request()).HostConfig.Mounts;

        Assert.True(Assert.Single(mounts, m => m.Target == Mirror).ReadOnly);
        Assert.False(Assert.Single(mounts, m => m.Target == AgentRepo).ReadOnly);
    }

    private static ContainerSpecRequest Request(string? agentRepo = AgentRepo) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: Worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: UsernsRemapPolicy.InheritDaemonRemap,
            BareRepoPath: Mirror,
            DnsServerAddress: "172.30.0.2",
            AgentRepoPath: agentRepo);
}
