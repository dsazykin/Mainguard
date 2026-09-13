using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Tests.TestTools;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F33 — the defence-in-depth controls that had drifted into decoration.</b>
///
/// <para>None of these was a live exploit and none is asserted as one. What they had in common is the
/// failure mode this codebase names repeatedly: a control that reads as applied and measures nothing.
/// A containment check that compares spellings while the kernel binds inodes; a secret guard that
/// inspects variable names when nobody names a variable <c>MY_SECRET</c>; a freshness heuristic whose
/// input the filesystem may not have; a mode that hands the world delete rights it never needed.</para>
/// </summary>
public class SandboxDefenceInDepthTests
{
    private const string RepoHash = "abc123def456abc123";

    private static ContainerSpecRequest Request(
        string worktree,
        string[]? roots = null,
        IReadOnlyList<string>? extraEnvNames = null) =>
        new(
            RepoHash: RepoHash,
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: SandboxLimits.Default,
            NetworkName: EgressProxyConfigurator.AgentNetworkName,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: UsernsRemapPolicy.InheritDaemonRemap,
            DnsServerAddress: "172.30.0.2",
            AllowedMountRoots: roots);

    // ------------------------------------------------- containment is about real paths, not spellings

    /// <summary>
    /// The escape the textual check could not see: a symlink under a daemon-owned root pointing out of
    /// it. Docker binds what the path RESOLVES to, so a check that compares the spelling authorises a
    /// mount of somewhere else entirely.
    /// </summary>
    [UnixOnlyFact("the escape shape is a POSIX symlink out of a daemon-owned root")]
    public void ABindSourceThatSymlinksOutOfEveryRoot_IsRefused()
    {
        var scratch = NewScratch();
        var root = Directory.CreateDirectory(Path.Combine(scratch, "mainguard")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(scratch, "somebody-elses-repo")).FullName;

        // Spelled inside the root; resolves outside it.
        var escape = Path.Combine(root, "worktrees");
        Directory.CreateSymbolicLink(escape, outside);

        var ex = Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request(escape, roots: new[] { root })));
        Assert.Contains("ESC-I1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The other direction, and the reason both sides are resolved: a ROOT reached through a
    /// symlink must still contain the sources under it. Resolving only the source would start refusing
    /// every legitimate mount on a machine whose data root sits under macOS's own <c>/var</c>.</summary>
    [UnixOnlyFact("a root reached through a symlink is the macOS /var → /private/var shape")]
    public void ABindSourceUnderASymlinkedRoot_IsStillContained()
    {
        var scratch = NewScratch();
        var real = Directory.CreateDirectory(Path.Combine(scratch, "real")).FullName;
        var worktree = Directory.CreateDirectory(Path.Combine(real, "worktrees", "abc", "agent-1")).FullName;

        var alias = Path.Combine(scratch, "alias");
        Directory.CreateSymbolicLink(alias, real);

        // The root is spelled through the alias; the source is spelled through the real path.
        var create = ContainerSpecBuilder.Build(Request(worktree, roots: new[] { alias }));
        Assert.Contains(create.HostConfig.Mounts, m => m.Source == worktree);
    }

    /// <summary>A leaf that does not exist yet is a legitimate mount source (a cache directory the
    /// manager is about to create), so resolution walks past absent components rather than refusing —
    /// and still resolves the links ABOVE them, which is where the escape would be.</summary>
    [UnixOnlyFact("resolution through a symlinked parent to an absent leaf")]
    public void RealPath_ResolvesThroughASymlinkedParent_ToALeafThatDoesNotExistYet()
    {
        var scratch = NewScratch();
        var real = Directory.CreateDirectory(Path.Combine(scratch, "real")).FullName;
        var alias = Path.Combine(scratch, "alias");
        Directory.CreateSymbolicLink(alias, real);

        var absentUnderAlias = Path.Combine(alias, "caches", "not-created-yet");

        Assert.Equal(
            Path.Combine(real, "caches", "not-created-yet"),
            ContainerSpecBuilder.RealPath(absentUnderAlias));
    }

    [Fact]
    public void RealPath_OfAPathWithNoLinksOnIt_IsItsNormalizedSelf()
    {
        var plain = Path.Combine(NewScratch(), "plain", "child");
        Assert.Equal(Path.GetFullPath(plain), ContainerSpecBuilder.RealPath(plain));
    }

    // ------------------------------------------------------- the secret guard reads values, not names

    /// <summary>
    /// A credential in an innocuously-named variable. The name rule cannot see it — and nobody writes
    /// <c>MY_SECRET_TOKEN=</c> by accident, so the name rule was never the one that would fire.
    /// </summary>
    ///
    /// <para>The values below are what the shared catalog covers today. It has NO rule for a model API
    /// key (<c>sk-ant-…</c>, <c>sk-…</c>), so an anonymously-named variable holding one still passes
    /// both halves — the likeliest secret to reach a jail's Env is the one least covered. Adding that
    /// rule belongs in <c>Mainguard.Git/Safety/SecretPatterns.cs</c>, where every consumer (the
    /// pre-commit scanner included) picks it up at once, rather than as a second private list here.
    [Theory]
    [InlineData("GH", "ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AWS_ID", "AKIAIOSFODNN7EXAMPLE")]
    public void AnInnocuouslyNamedVariableCarryingACredential_IsRefused(string name, string value)
    {
        var create = ContainerSpecBuilder.Build(Request("/home/mainguard/mainguard/worktrees/abc/agent-1"));
        create.Env.Add($"{name}={value}");

        var ex = Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.AssertNoSecretsInEnvForTests(create));

        Assert.Contains("G-13", ex.Message, StringComparison.Ordinal);
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        // INVARIANT: the refusal names the variable and the rule, and NEVER the value it matched.
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The shipped environment is proxy routing, toolchain PATH and flags. If this ever fires,
    /// the value rule has started refusing the jail's own configuration.</summary>
    [Fact]
    public void TheEnvironmentTheBuilderItselfProduces_PassesBothHalvesOfTheGuard()
    {
        // Build() runs the guard on the way out, so reaching this line at all is the assertion.
        var create = ContainerSpecBuilder.Build(Request("/home/mainguard/mainguard/worktrees/abc/agent-1"));
        Assert.NotEmpty(create.Env);
    }

    /// <summary>Audit F33: the NO_PROXY list named a host nothing resolves. A standing exemption from
    /// the only route out of a default-deny jail is not something to keep warm for later.</summary>
    [Fact]
    public void NoProxy_ExemptsLoopbackOnly_AndNamesNoUnroutableInternalHost()
    {
        var create = ContainerSpecBuilder.Build(Request("/home/mainguard/mainguard/worktrees/abc/agent-1"));

        foreach (var spelling in new[] { "NO_PROXY=", "no_proxy=" })
        {
            var entry = Assert.Single(create.Env, e => e.StartsWith(spelling, StringComparison.Ordinal));
            Assert.DoesNotContain("mainguard.internal", entry, StringComparison.Ordinal);
            Assert.Equal(spelling + "localhost,127.0.0.1,::1", entry);
        }
    }

    // --------------------------------------------------------- the birth-time heuristic's own premise

    [Fact]
    public void ABirthTimeTheFilesystemCouldNotSupply_IsNotTreatedAsAnAnswer()
    {
        // The two sentinels a filesystem without statx produces, and the skewed-clock shape.
        Assert.False(DockerSandboxEngine.BirthTimeIsUsable(DateTime.UnixEpoch));
        Assert.False(DockerSandboxEngine.BirthTimeIsUsable(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(DockerSandboxEngine.BirthTimeIsUsable(DateTime.MinValue));
        Assert.False(DockerSandboxEngine.BirthTimeIsUsable(DateTime.UtcNow.AddDays(30)));
    }

    [Fact]
    public void ARealBirthTime_IsTreatedAsAnAnswer()
    {
        Assert.True(DockerSandboxEngine.BirthTimeIsUsable(DateTime.UtcNow.AddMinutes(-5)));
        Assert.True(DockerSandboxEngine.BirthTimeIsUsable(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ------------------------------------------------------- the world-writable cache leaf's blast radius

    /// <summary>
    /// The fallback rung has to be world-WRITABLE or the remapped jail uid cannot write its cache. It
    /// never had to be world-DESTRUCTIBLE: without the sticky bit any local account could delete or
    /// rename another agent's cached packages.
    /// </summary>
    [Fact]
    public void TheWorldWritableCacheLeaf_CarriesTheStickyBit()
    {
        var mode = PackageCachePolicy.LeafMode(PackageCacheGrant.ModeOnly);

        Assert.True(mode.HasFlag(UnixFileMode.OtherWrite), "the jail must still be able to write it");
        Assert.True(mode.HasFlag(UnixFileMode.StickyBit), "…but only the owner may unlink or rename");
        Assert.True(mode.HasFlag(UnixFileMode.SetGroup), "setgid still propagates the group downward");
    }

    /// <summary>The group-shared rung is not world-writable at all, so it needs no sticky bit and must
    /// not silently acquire one — the two rungs are different grants, not one with a flag.</summary>
    [Fact]
    public void TheGroupSharedCacheLeaf_IsNotWorldWritable()
    {
        var mode = PackageCachePolicy.LeafMode(PackageCacheGrant.SharedJailGroup);

        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.False(mode.HasFlag(UnixFileMode.StickyBit));
        Assert.True(mode.HasFlag(UnixFileMode.SetGroup));
    }

    /// <summary>A scratch directory whose own path carries no symlinks, so each test's fixture is
    /// measuring the link IT created rather than the platform's (on macOS <c>/var</c> and <c>/tmp</c>
    /// are themselves links, which is exactly the shape under test).</summary>
    private static string NewScratch()
    {
        var dir = Path.Combine(
            ContainerSpecBuilder.RealPath(Path.GetTempPath()), "mg-f33-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
