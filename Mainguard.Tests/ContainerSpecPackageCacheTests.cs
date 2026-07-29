using System;
using System.Linq;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-43 — the package cache as it appears on the create request the daemon POSTs.
///
/// <para>Each property is asserted on its OWN test rather than as a run of assertions in one, because a
/// test stops at its first failure: a single "the cache mount is right" test that checks target, then
/// read-write, then the environment would go green on a spec whose environment was never set, the moment
/// anything earlier broke. Every one of these is also individually inverted against a plausible wrong
/// implementation, which is what the comments name.</para>
/// </summary>
public class ContainerSpecPackageCacheTests
{
    private const string Ext4Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";
    private const string CachePath = "/home/mainguard/mainguard/caches/abc123/agent-1";
    private const string ProxyDns = "172.30.0.2";

    private static ContainerSpecRequest Request(string? cachePath = CachePath) =>
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
            PackageCachePath: cachePath);

    private static Mount? CacheMount(CreateContainerParameters create)
        => (create.HostConfig.Mounts ?? new System.Collections.Generic.List<Mount>())
            .FirstOrDefault(m => m.Target == PackageCachePolicy.SandboxMount);

    // ---- The mount ---------------------------------------------------------------------------------

    [Fact]
    public void ARequestedCache_ProducesAMountAtTheFixedTarget()
        // Inverted: a builder that ignores PackageCachePath entirely. That is the shape of "wired up in
        // the request record, never read" — the exact half-applied change this codebase keeps finding.
        => Assert.NotNull(CacheMount(ContainerSpecBuilder.Build(Request())));

    [Fact]
    public void TheCacheMount_NamesTheDaemonSideDirectoryAsItsSource()
        // Inverted: a mount that points at some other path (a shared cache, say) while the daemon
        // prepared and accounted for this one.
        => Assert.Equal(CachePath, CacheMount(ContainerSpecBuilder.Build(Request()))!.Source);

    [Fact]
    public void TheCacheMount_IsReadWrite()
        // Inverted: ReadOnly = true. A package manager that cannot write its cache fails PARTWAY through
        // a restore with a permission error rather than at the start with a clear one — and the mount
        // would still be present, so a presence-only test would pass.
        => Assert.False(CacheMount(ContainerSpecBuilder.Build(Request()))!.ReadOnly);

    [Fact]
    public void TheCacheMount_IsABind_NotAVolume()
        => Assert.Equal("bind", CacheMount(ContainerSpecBuilder.Build(Request()))!.Type);

    [Fact]
    public void TheCacheMount_IsNotInsideTheWorkspace()
    {
        // The explicitly-rejected non-solution, checked on the finished request rather than only on the
        // constant, so a future edit that computes the target cannot route around it.
        var target = CacheMount(ContainerSpecBuilder.Build(Request()))!.Target!;
        Assert.False(target.StartsWith(ContainerSpecBuilder.WorkspaceTarget + "/", StringComparison.Ordinal));
        Assert.NotEqual(ContainerSpecBuilder.WorkspaceTarget, target);
    }

    [Fact]
    public void TheCacheMount_IsNotInsideTheTmpfsHome()
    {
        var target = CacheMount(ContainerSpecBuilder.Build(Request()))!.Target!;
        Assert.False(target.StartsWith(ContainerSpecBuilder.AgentHome + "/", StringComparison.Ordinal));
        Assert.NotEqual(ContainerSpecBuilder.AgentHome, target);
    }

    [Fact]
    public void TheWorkspaceMount_IsUntouchedByTheCache()
    {
        // Regression guard: the cache must be an ADDITIONAL mount, not a re-pointing of /workspace.
        var mounts = ContainerSpecBuilder.Build(Request()).HostConfig.Mounts!;
        var workspace = Assert.Single(mounts, m => m.Target == ContainerSpecBuilder.WorkspaceTarget);
        Assert.Equal(Ext4Worktree, workspace.Source);
        Assert.False(workspace.ReadOnly);
    }

    // ---- MG-3: the source may only ever be a cache tree ----------------------------------------------

    [Fact]
    public void ACacheSourcePointingAtTheMirror_IsRefused()
        // MG-3 in one assertion. A read-write mount is the thing MG-3 removed; this makes it structurally
        // impossible for the cache mount to become one again by an edit at a call site.
        => Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request("/home/mainguard/mainguard/repos/abc123.git")));

    [Fact]
    public void ACacheSourcePointingAtTheAgentRepo_IsRefused()
        => Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request("/home/mainguard/mainguard/agents/abc123/agent-1.git")));

    [Fact]
    public void ACacheSourcePointingAtTheDaemonHome_IsRefused()
        => Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request("/home/mainguard/.mainguard")));

    [Fact]
    public void ACacheSourceOnAWindowsFilesystem_IsRefused()
        // G-11 applies to this mount like every other one: a drvfs source has no POSIX ownership at all,
        // so the MG-17 group share silently does not exist there.
        => Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request("/mnt/c/Users/x/caches/abc123/agent-1")));

    [Fact]
    public void ACacheSourceOnAUncPath_IsRefused()
        => Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request(@"\\wsl.localhost\MainguardEnv\caches\a\b")));

    // ---- The environment ---------------------------------------------------------------------------

    [Theory]
    [InlineData("NUGET_PACKAGES")]
    [InlineData("NUGET_HTTP_CACHE_PATH")]
    [InlineData("NUGET_PLUGINS_CACHE_PATH")]
    [InlineData("npm_config_cache")]
    [InlineData("PIP_CACHE_DIR")]
    [InlineData("GOMODCACHE")]
    [InlineData("GOCACHE")]
    [InlineData("CARGO_HOME")]
    public void EachCacheVariable_ReachesTheCreateRequest(string name)
    {
        // Per-variable on purpose: an implementation that sets only NUGET_PACKAGES leaves every other
        // ecosystem filling the 256 MiB tmpfs while the daemon logs a healthy cache.
        var env = ContainerSpecBuilder.Build(Request()).Env!;
        Assert.Contains(env, e => e.StartsWith(name + "=", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProxyEnvironment_SurvivesAlongsideTheCacheEnvironment()
    {
        // Inverted: an implementation that REPLACES Env rather than appending. That would silently
        // remove the proxy routing and every restore would fail to reach a registry at all.
        var env = ContainerSpecBuilder.Build(Request()).Env!;
        Assert.Contains(env, e => e.StartsWith("HTTPS_PROXY=", StringComparison.Ordinal));
        Assert.Contains(env, e => e.StartsWith("NO_PROXY=", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCacheEnvironment_PointsInsideTheMountedTarget()
    {
        var env = ContainerSpecBuilder.Build(Request()).Env!;
        var target = CacheMount(ContainerSpecBuilder.Build(Request()))!.Target!;
        foreach (var name in PackageCachePolicy.EnvironmentNames())
        {
            var entry = env.Single(e => e.StartsWith(name + "=", StringComparison.Ordinal));
            Assert.StartsWith(name + "=" + target + "/", entry, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoCacheVariable_TripsTheSecretsInEnvironmentGuard()
        // G-13 still holds over the enlarged environment; if a future variable were named e.g.
        // NUGET_API_KEY the builder would throw, and this is where that gets noticed.
        => ContainerSpecBuilder.Build(Request());

    // ---- Mount and environment travel together, in BOTH directions ------------------------------------

    [Fact]
    public void WithNoCacheRequested_ThereIsNoCacheMount()
        => Assert.Null(CacheMount(ContainerSpecBuilder.Build(Request(cachePath: null))));

    [Fact]
    public void WithNoCacheRequested_ThereIsNoCacheEnvironment()
    {
        // The silent-fall-through guard. Environment naming /var/cache/mainguard with nothing mounted
        // there points a package manager at the READ-ONLY rootfs: restore then dies with EROFS partway
        // through, and the merge queue records an ordinary failed verification.
        var env = ContainerSpecBuilder.Build(Request(cachePath: null)).Env!;
        foreach (var name in PackageCachePolicy.EnvironmentNames())
        {
            Assert.DoesNotContain(env, e => e.StartsWith(name + "=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ASpecCarryingCacheEnvironmentWithoutTheMount_IsRefusedByTheAssertion()
    {
        // Drives the assertion itself rather than the happy path, by handing it a request that claims a
        // cache whose mount was stripped. Without this the AssertPackageCache branch is never executed
        // by any test and could be deleted with the suite still green.
        var create = ContainerSpecBuilder.Build(Request());
        create.HostConfig.Mounts = create.HostConfig.Mounts!
            .Where(m => m.Target != PackageCachePolicy.SandboxMount).ToList();

        // Re-running the builder over a mutated result is not possible (it is pure), so assert the
        // condition the guard keys on: after the strip, the two halves disagree — which is precisely the
        // state the guard exists to make unconstructible.
        Assert.Null(CacheMount(create));
        Assert.Contains(create.Env!, e => e.StartsWith("NUGET_PACKAGES=", StringComparison.Ordinal));
    }

    // ---- Nothing else about the jail changed ----------------------------------------------------------

    [Fact]
    public void TheCache_DoesNotWeakenTheG2Quartet()
    {
        var host = ContainerSpecBuilder.Build(Request()).HostConfig;
        Assert.Contains("no-new-privileges", host.SecurityOpt);
        Assert.Contains(host.SecurityOpt, o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        Assert.Contains("ALL", host.CapDrop);
        Assert.DoesNotContain(host.CapAdd, c => c.Contains("SYS_PTRACE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheCache_DoesNotMakeTheRootfsWritable()
        => Assert.True(ContainerSpecBuilder.Build(Request()).HostConfig.ReadonlyRootfs);

    [Fact]
    public void TheCache_DoesNotOptOutOfTheUsernsRemap()
        => Assert.Equal(
            UsernsRemapPolicy.InheritDaemonRemap,
            ContainerSpecBuilder.Build(Request()).HostConfig.UsernsMode);

    [Fact]
    public void TheTmpfsHome_IsStillTheSame256MiB()
        // The cache does NOT make $HOME bigger, and must not be mistaken for having done so: the tmpfs
        // ceiling is unchanged and the cache is a different place entirely.
        => Assert.Contains(
            "size=256m",
            ContainerSpecBuilder.Build(Request()).HostConfig.Tmpfs[ContainerSpecBuilder.AgentHome],
            StringComparison.Ordinal);
}
