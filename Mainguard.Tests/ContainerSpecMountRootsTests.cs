using System;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// ESC-I1 made structural (macos-host substrate): when a spec names AllowedMountRoots, every
/// bind-mount SOURCE must sit under one of them — a user repo or any other host path is a typed
/// refusal at construction, whoever the caller was. When no roots are named (WSL2 today), the
/// per-source rejections stand alone and nothing changes.
/// </summary>
public class ContainerSpecMountRootsTests
{
    private const string Root = "/Users/dev/mainguard";
    private const string DataRoot = "/Users/dev/.mainguard";

    private static ContainerSpecRequest Request(
        string worktree = Root + "/worktrees/abc123/agent-1",
        string? bareRepo = Root + "/repos/abc123.git",
        string? ipcDir = DataRoot + "/ipc/abc123",
        string[]? roots = null) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            BareRepoPath: bareRepo,
            IpcDirPath: ipcDir,
            DnsServerAddress: "172.30.0.2",
            AllowedMountRoots: roots ?? new[] { Root, DataRoot });

    [Fact]
    public void SourcesUnderTheRoots_ShouldBuild()
        // Worktree + read-only mirror + IPC dir, all under the two substrate roots — the normal
        // macos-host spawn shape.
        => Assert.NotNull(ContainerSpecBuilder.Build(Request()));

    [Fact]
    public void AWorktreeOutsideEveryRoot_ShouldBeRefusedTyped()
    {
        // The decisive case: a USER repo path as the workspace. The per-source shape guards cannot
        // catch it (it is a perfectly normal absolute Unix path); the roots guard must.
        var ex = Assert.Throws<SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(Request(worktree: "/Users/dev/Code/my-real-repo")));
        Assert.Contains("ESC-I1", ex.Message);
        Assert.Contains("/Users/dev/Code/my-real-repo", ex.Message);
    }

    [Fact]
    public void ABareRepoOutsideEveryRoot_ShouldBeRefusedTyped()
        => Assert.Throws<SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(Request(bareRepo: "/Users/dev/somewhere-else/abc123.git")));

    [Fact]
    public void APrefixSibling_ShouldNotSlipThrough()
        // "/Users/dev/mainguard-evil" starts with the ROOT STRING but is not under the root
        // directory — the guard must compare path segments, not raw prefixes.
        => Assert.Throws<SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(Request(worktree: "/Users/dev/mainguard-evil/wt")));

    [Fact]
    public void NoRootsDeclared_ShouldKeepTheOldBehavior()
        // WSL2 today: null roots = the per-source rejections stand alone; the same out-of-root
        // path builds fine. This pins that adding the guard changed nothing for substrates that
        // have not opted in.
        => Assert.NotNull(ContainerSpecBuilder.Build(
            Request(worktree: "/home/other/wt", bareRepo: null, ipcDir: null, roots: Array.Empty<string>())
                with
            { AllowedMountRoots = null }));
}
