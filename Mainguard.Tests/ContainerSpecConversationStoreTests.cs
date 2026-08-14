using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The conversation store as it appears on the create request the daemon actually POSTs.
///
/// <para>Every assertion here reads the FINISHED <see cref="CreateContainerParameters"/> rather than the
/// helper that produced it. That is the whole point of the file: a test that asks
/// <c>ConversationStorePolicy</c> where the store should be would pass on a builder that never mounted
/// anything, which is exactly the half-applied shape this repository keeps finding — a declaration wired
/// into a request record and never read.</para>
///
/// <para>Each property gets its own test rather than a run of assertions in one, because a test stops at
/// its first failure: a single "the mount is right" test would go green on a spec whose read-write bit
/// was never set, the moment anything earlier broke.</para>
/// </summary>
public class ContainerSpecConversationStoreTests
{
    private const string Ext4Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";
    private const string StorePath = "/home/mainguard/mainguard/conversations/abc123/agent-1/.claude/projects";
    private const string DeclaredPath = ".claude/projects";
    private const string ProxyDns = "172.30.0.2";

    private static ContainerSpecRequest Request(params ConversationMount[] mounts) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: Ext4Worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: UsernsRemapPolicy.InheritDaemonRemap,
            DnsServerAddress: ProxyDns,
            ConversationMounts: mounts.Length == 0
                ? null
                : mounts);

    private static ConversationMount Store(string host = StorePath, string declared = DeclaredPath)
        => new(host, declared);

    private static Mount? StoreMount(CreateContainerParameters create)
        => (create.HostConfig.Mounts ?? new List<Mount>())
            .FirstOrDefault(m => m.Target == ContainerSpecBuilder.AgentHome + "/" + DeclaredPath);

    // ---- The mount exists at all --------------------------------------------------------------------

    [Fact]
    public void ARequestedStore_ProducesAMountAtTheCLIsOwnPath()
        // Inverted: a builder that ignores ConversationMounts entirely — the "wired into the record,
        // never read" failure. The target must be the CLI's own $HOME path, because unlike a package
        // cache (which is TOLD where it is through the environment) a CLI's transcript directory is not
        // configurable: any other target is a mount that persists nothing.
        => Assert.NotNull(StoreMount(ContainerSpecBuilder.Build(Request(Store()))));

    [Fact]
    public void TheStoreMount_NamesTheDaemonSideDirectoryAsItsSource()
        => Assert.Equal(StorePath, StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.Source);

    [Fact]
    public void TheStoreMount_IsABind_NotAVolume()
        => Assert.Equal("bind", StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.Type);

    [Fact]
    public void TheStoreMount_IsReadWrite()
        // Inverted: ReadOnly = true. The CLI appends to its transcript continuously, so a read-only
        // store fails PARTWAY through a session rather than at the start — and the mount would still be
        // present, so a presence-only test would pass.
        => Assert.False(StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.ReadOnly);

    // ---- Where the STORE ITSELF may live: the two fail-closed facts --------------------------------

    [Fact]
    public void TheStoreSource_IsNotInsideTheTmpfsHome()
    {
        // The load-bearing one. The TARGET is under $HOME by necessity (that is where the CLI reads it),
        // so the only thing that makes this feature real is that the SOURCE is not — a source under the
        // tmpfs would mean the store is the very thing whose destruction it exists to survive, and
        // nothing would look wrong until somebody came back for the history.
        var source = StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.Source!;
        Assert.NotEqual(ContainerSpecBuilder.AgentHome, source);
        Assert.False(source.StartsWith(ContainerSpecBuilder.AgentHome + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStoreSource_IsNotInsideTheWorkspace()
    {
        // Both spellings of "the tree under verification": the container path /workspace, and THIS
        // request's own ext4 worktree, which is what /workspace is a bind mount of. A store in either
        // puts the operator's whole session one `git add -A` away from being merged to main — the same
        // reason the MG-43 package cache lives outside the worktree.
        var source = StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.Source!;
        Assert.False(source.StartsWith(ContainerSpecBuilder.WorkspaceTarget + "/", StringComparison.Ordinal));
        Assert.NotEqual(ContainerSpecBuilder.WorkspaceTarget, source);
        Assert.False(source.StartsWith(Ext4Worktree + "/", StringComparison.Ordinal));
        Assert.NotEqual(Ext4Worktree, source);
    }

    [Fact]
    public void TheStoreTarget_IsUnderTheAgentHome_AndNotInsideTheWorkspace()
    {
        var target = StoreMount(ContainerSpecBuilder.Build(Request(Store())))!.Target!;
        Assert.StartsWith(ContainerSpecBuilder.AgentHome + "/", target, StringComparison.Ordinal);
        Assert.False(target.StartsWith(ContainerSpecBuilder.WorkspaceTarget + "/", StringComparison.Ordinal));
    }

    // ---- The refusals ------------------------------------------------------------------------------

    [Fact]
    public void AStoreSourceInsideTheWorktree_IsRefused()
    {
        // The assertion has to be able to FAIL, so drive it with a source that really is in the worktree
        // rather than trusting the happy path to prove the guard exists. (It is caught by the
        // conversations/-tree guard first, which is the same refusal for the same reason: a read-write
        // mount of daemon-side state may only ever name a conversations/ path.)
        var bad = Ext4Worktree + "/.claude/projects";
        var ex = Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request(Store(bad))));
        Assert.Contains(bad, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreSourceOutsideAnyConversationsTree_IsRefused()
        // MG-3's structural guard, restated for the second read-write mount on the request: a source
        // that is not inside a conversations/ tree could be edited into a writable path at the mirror,
        // the per-agent git dir, or anywhere under the daemon's home.
        => Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(
            Request(Store("/home/mainguard/mainguard/repos/abc123.git"))));

    [Fact]
    public void ADrvfsStoreSource_IsRefused_G11()
        => Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(
            Request(Store("/mnt/c/Users/x/conversations/abc123/agent-1"))));

    [Fact]
    public void NoRequestedStore_ProducesNoMountAtAll()
    {
        // The other direction of the same self-consistency rule the package cache carries: nothing may
        // claim there is a store when none was requested.
        var create = ContainerSpecBuilder.Build(Request());
        Assert.DoesNotContain(
            create.HostConfig.Mounts ?? new List<Mount>(),
            m => ConversationStorePolicy.IsInsideAConversationTree(m.Source));
    }

    [Fact]
    public void TwoDeclaredStores_BothProduceTheirOwnMount()
    {
        // A future adapter may declare more than one path; the loop must not stop at the first.
        var create = ContainerSpecBuilder.Build(Request(
            Store(),
            Store("/home/mainguard/mainguard/conversations/abc123/agent-1/.config/sessions", ".config/sessions")));

        var mounts = create.HostConfig.Mounts!;
        Assert.Contains(mounts, m => m.Target == ContainerSpecBuilder.AgentHome + "/.claude/projects");
        Assert.Contains(mounts, m => m.Target == ContainerSpecBuilder.AgentHome + "/.config/sessions");
    }

    // ---- The rest of the jail is untouched ----------------------------------------------------------

    [Fact]
    public void TheStoreMount_DoesNotDisturbTheOtherHardeningControls()
    {
        // A new mount must not be able to quietly relax anything else on the request — the G2 quartet is
        // re-asserted by Build() itself, so this is really "the store survives every existing assertion".
        var create = ContainerSpecBuilder.Build(Request(Store()));
        Assert.Contains("no-new-privileges", create.HostConfig.SecurityOpt!);
        Assert.Contains("ALL", create.HostConfig.CapDrop!);
        Assert.True(create.HostConfig.ReadonlyRootfs);
        // The tmpfs $HOME is still a tmpfs: the store is a deeper path inside it, not a replacement for
        // it, so everything else the CLI writes to $HOME stays throwaway.
        Assert.True(create.HostConfig.Tmpfs!.ContainsKey(ContainerSpecBuilder.AgentHome));
    }
}
