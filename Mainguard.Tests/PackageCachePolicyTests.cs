using System;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-43 — the environment-INDEPENDENT half of the package cache, asserted as strings and shapes.
///
/// <para><b>Why these are not Docker tests.</b> #269's real guard turned out to be a Dockerfile string
/// assertion, because a Docker leg cannot catch a permissive-engine difference: it passes on the box
/// whose engine hides the bug. The same reasoning applies to every property below. "The cache is not
/// inside the worktree", "the environment names the mount", "the boot script provisions caches/ with the
/// jail group" are all facts about the code, and a container test for them would pass on a box with no
/// userns remap for reasons that have nothing to do with the property. They are checked here, where
/// nothing about the machine can make them true.</para>
/// </summary>
public class PackageCachePolicyTests
{
    // ---- Where the cache is, and where it must NOT be -------------------------------------------

    [Fact]
    public void TheSandboxMount_IsOutsideTheVerifiedWorktree()
    {
        // The rejected non-solution, pinned. A cache under /workspace is untracked gigabytes inside the
        // tree the agent commits from and the merge queue verifies.
        Assert.False(
            PackageCachePolicy.SandboxMount == ContainerSpecBuilder.WorkspaceTarget
            || PackageCachePolicy.SandboxMount.StartsWith(ContainerSpecBuilder.WorkspaceTarget + "/", StringComparison.Ordinal),
            $"the package cache mounts at '{PackageCachePolicy.SandboxMount}', inside '{ContainerSpecBuilder.WorkspaceTarget}'");
    }

    [Fact]
    public void TheSandboxMount_IsOutsideTheTmpfsHome()
    {
        // $HOME is the 256 MiB tmpfs whose exhaustion is the entire reason this feature exists. A mount
        // under it would leave the feature looking wired up while changing nothing.
        Assert.False(
            PackageCachePolicy.SandboxMount == ContainerSpecBuilder.AgentHome
            || PackageCachePolicy.SandboxMount.StartsWith(ContainerSpecBuilder.AgentHome + "/", StringComparison.Ordinal),
            $"the package cache mounts at '{PackageCachePolicy.SandboxMount}', inside the tmpfs '{ContainerSpecBuilder.AgentHome}'");
    }

    [Fact]
    public void TheSandboxMount_IsAnAbsolutePath()
        => Assert.StartsWith("/", PackageCachePolicy.SandboxMount, StringComparison.Ordinal);

    // ---- The layout is per-agent, which is the isolation argument --------------------------------

    [Fact]
    public void TwoAgentsInOneRepo_GetDifferentCacheDirectories()
    {
        var a = PackageCachePolicy.AgentCachePath("/vm", "repohash", "agent-a");
        var b = PackageCachePolicy.AgentCachePath("/vm", "repohash", "agent-b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NeitherAgentsCacheDirectory_ContainsTheOther()
    {
        // The whole cross-tenant argument in one assertion: neither directory is reachable by walking
        // down from the other, so a bind mount of one grants nothing about the other.
        var a = PackageCachePolicy.AgentCachePath("/vm", "repohash", "agent-a");
        var b = PackageCachePolicy.AgentCachePath("/vm", "repohash", "agent-b");
        Assert.DoesNotContain(a, b, StringComparison.Ordinal);
        Assert.DoesNotContain(b, a, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastUsedMarker_IsASiblingOfTheCache_NotAFileInsideIt()
    {
        // The LRU ordering is daemon state. If it lived INSIDE the mounted directory an agent could
        // touch its own marker to dodge eviction — a per-tenant denial-of-service on everyone else's
        // budget share. As a sibling it is in a directory no jail mounts.
        var cache = PackageCachePolicy.AgentCachePath("/vm", "repohash", "agent-a");
        var marker = PackageCachePolicy.LastUsedMarkerPath("/vm", "repohash", "agent-a");
        Assert.False(marker.StartsWith(cache + "/", StringComparison.Ordinal),
            $"the marker '{marker}' is inside the mounted cache '{cache}' — the jail could rewrite it");
        Assert.Equal(System.IO.Path.GetDirectoryName(cache), System.IO.Path.GetDirectoryName(marker));
    }

    [Fact]
    public void AnAgentId_ThatWouldEscapeTheCacheRoot_IsRefused()
        => Assert.Throws<RepoProvisioningException>(
            () => PackageCachePolicy.AgentCachePath("/vm", "repohash", "../../repos"));

    [Fact]
    public void ARepoHandle_ThatWouldEscapeTheCacheRoot_IsRefused()
        => Assert.Throws<RepoProvisioningException>(
            () => PackageCachePolicy.AgentCachePath("/vm", "../..", "agent-a"));

    // ---- IsInsideACacheTree: the MG-3 structural guard's input ------------------------------------

    [Fact]
    public void APathUnderACachesDirectory_IsRecognised()
        => Assert.True(PackageCachePolicy.IsInsideACacheTree("/home/mainguard/mainguard/caches/hash/agent-a"));

    [Fact]
    public void TheMirrorPath_IsNotRecognisedAsACache()
        => Assert.False(PackageCachePolicy.IsInsideACacheTree("/home/mainguard/mainguard/repos/hash.git"));

    [Fact]
    public void TheAgentRepoPath_IsNotRecognisedAsACache()
        => Assert.False(PackageCachePolicy.IsInsideACacheTree("/home/mainguard/mainguard/agents/hash/a1.git"));

    [Fact]
    public void ADirectoryMerelyNamedLikeACache_IsNotRecognised()
        // Whole-SEGMENT, not substring: this is the `NOAAAA`-contains-`AAAA` class of bug, and the
        // consequence here would be accepting a writable mount at a path that is not a cache at all.
        => Assert.False(PackageCachePolicy.IsInsideACacheTree("/home/mainguard/mainguard/repos-caches-backup/x"));

    [Fact]
    public void TheCachesDirectoryItself_IsNotAMountableCache()
        // The root is never mounted — mounting it would hand one jail every other agent's cache, which
        // is precisely the cross-tenant path the per-agent layout exists to remove.
        => Assert.False(PackageCachePolicy.IsInsideACacheTree("/home/mainguard/mainguard/caches"));

    [Fact]
    public void AnEmptySource_IsNotRecognised()
        => Assert.False(PackageCachePolicy.IsInsideACacheTree(""));

    // ---- What package managers are told ----------------------------------------------------------

    [Fact]
    public void TheNuGetGlobalPackagesFolder_IsPointedAtTheCache()
    {
        // NUGET_PACKAGES is the 1.7 GB one — the single variable that decides whether this repository's
        // own .mainguard/verify can run at all.
        var entry = Assert.Single(
            PackageCachePolicy.Environment(), e => e.StartsWith("NUGET_PACKAGES=", StringComparison.Ordinal));
        Assert.StartsWith("NUGET_PACKAGES=" + PackageCachePolicy.SandboxMount + "/", entry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NUGET_PACKAGES")]
    [InlineData("NUGET_HTTP_CACHE_PATH")]
    [InlineData("NUGET_PLUGINS_CACHE_PATH")]
    [InlineData("npm_config_cache")]
    [InlineData("PIP_CACHE_DIR")]
    [InlineData("GOMODCACHE")]
    [InlineData("GOCACHE")]
    [InlineData("CARGO_HOME")]
    public void EveryMappedEcosystem_PointsInsideTheCacheMount(string name)
    {
        // Asserted per VARIABLE rather than over the collection: a single "all of them start with the
        // mount" assertion stops at the first failure and says nothing about the rest, and a variable
        // silently left on the tmpfs $HOME is exactly the half-applied change this suite keeps catching.
        var entry = Assert.Single(
            PackageCachePolicy.Environment(), e => e.StartsWith(name + "=", StringComparison.Ordinal));
        Assert.StartsWith(name + "=" + PackageCachePolicy.SandboxMount + "/", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCacheVariable_LooksLikeASecret()
    {
        // G-13 is re-asserted by ContainerSpecBuilder over the finished env; this catches it at the
        // source, where the fix is one word rather than a spawn failure.
        foreach (var name in PackageCachePolicy.EnvironmentNames())
        {
            var upper = name.ToUpperInvariant();
            Assert.DoesNotContain("KEY", upper, StringComparison.Ordinal);
            Assert.DoesNotContain("TOKEN", upper, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", upper, StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD", upper, StringComparison.Ordinal);
            Assert.DoesNotContain("CREDENTIAL", upper, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnvironmentNames_MatchesEnvironment()
        => Assert.Equal(
            PackageCachePolicy.Environment().Select(e => e.Split('=', 2)[0]).ToArray(),
            PackageCachePolicy.EnvironmentNames().ToArray());

    // ---- Egress: nothing was widened -------------------------------------------------------------

    [Fact]
    public void TheAllowlist_AlreadyCarriesTheNuGetHosts_SoNothingWasAdded()
    {
        // The egress claim, pinned as a fact rather than left in prose: in-jail restore needs
        // api.nuget.org (index + flat container) and www.nuget.org, and BOTH were already default
        // entries before this change. If a future edit removes either, restore breaks and this says so;
        // if a future edit ADDS a host to make restore work, the count assertion below fails and the
        // reviewer is forced to justify it.
        var hosts = EgressAllowlist.DefaultEntries.Select(e => e.HostPattern).ToArray();
        Assert.Contains("api.nuget.org", hosts);
        Assert.Contains("www.nuget.org", hosts);
    }

    [Fact]
    public void TheAllowlist_StillCarriesNoToolchainDownloadHost()
    {
        // #269 fetches the SDK tarball at IMAGE BUILD time on the VM's network, which is why the
        // toolchain is a layer and not an in-jail install. A package cache is not a reason to put that
        // host on the jail's allowlist, and this is the assertion that notices if someone tries.
        var hosts = EgressAllowlist.DefaultEntries.Select(e => e.HostPattern).ToArray();
        Assert.DoesNotContain("builds.dotnet.microsoft.com", hosts);
    }

    [Fact]
    public void EveryPackageRegistryEntry_IsAPackageRegistryKind_NotACustomHole()
        => Assert.All(
            EgressAllowlist.DefaultEntries.Where(e => e.HostPattern.Contains("nuget", StringComparison.Ordinal)),
            e => Assert.Equal(EgressEntryKind.PackageRegistry, e.Kind));

    // ---- The in-jail probe's pure parser ---------------------------------------------------------

    [Fact]
    public void TheProbeScript_NamesTheMountPoint()
    {
        var argv = PackageCachePolicy.WritabilityProbe();
        Assert.Equal("sh", argv[0]);
        Assert.Equal("-c", argv[1]);
        Assert.Contains(PackageCachePolicy.SandboxMount, argv[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeScript_UsesNoToolThatCouldBeMissing()
    {
        // A probe that degrades to "command not found" and reports that as a policy verdict is worse
        // than no probe. The write test is a shell redirect; the only external binary is `rm`.
        var script = PackageCachePolicy.WritabilityProbe()[2];
        Assert.DoesNotContain("mktemp", script, StringComparison.Ordinal);
        Assert.DoesNotContain("touch", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stat ", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOkVerdict_IsNoFailure()
        => Assert.Null(PackageCachePolicy.DescribeProbeFailure("MGCACHE[OK]", 0));

    [Fact]
    public void AMissingMount_IsNamedAsAMissingMount()
    {
        var failure = PackageCachePolicy.DescribeProbeFailure("MGCACHE[MISSING]", 0);
        Assert.NotNull(failure);
        Assert.Contains("no directory", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwritableMount_IsNamedAsAnUnwritableMount()
    {
        var failure = PackageCachePolicy.DescribeProbeFailure("MGCACHE[UNWRITABLE]", 0);
        Assert.NotNull(failure);
        Assert.Contains("cannot create a file", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOutputAtAll_IsADistinctReason_AndNeverAPass()
    {
        // The exact class of bug the framing exists for: a dead container, a missing shell or a dropped
        // transport all produce empty output. It must read as "the probe did not run", never as either
        // verdict — and above all never as a PASS.
        var failure = PackageCachePolicy.DescribeProbeFailure(string.Empty, 0);
        Assert.NotNull(failure);
        Assert.Contains("did not run", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputThatMentionsOk_WithoutTheFrame_IsNotAPass()
    {
        // A naive stdout.Contains("OK") would go green on this. Some CLI banner, some unrelated log
        // line, and the cache check silently stops checking.
        var failure = PackageCachePolicy.DescribeProbeFailure("everything is OK here", 0);
        Assert.NotNull(failure);
        Assert.Contains("did not run", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnterminatedFrame_IsNotAPass()
        => Assert.NotNull(PackageCachePolicy.DescribeProbeFailure("MGCACHE[OK", 0));

    [Fact]
    public void AnEmptyFrame_IsAFailure_AndDistinctFromAnAbsentOne()
    {
        // Present-but-empty proves the probe RAN and said nothing; absent proves it did not run. Both
        // are failures, and conflating them costs an afternoon of looking in the wrong place.
        var empty = PackageCachePolicy.DescribeProbeFailure("MGCACHE[]", 0);
        var absent = PackageCachePolicy.DescribeProbeFailure(string.Empty, 0);
        Assert.NotNull(empty);
        Assert.NotNull(absent);
        Assert.NotEqual(empty, absent);
    }

    [Fact]
    public void AnUnrecognisedVerdict_IsAFailure()
        => Assert.NotNull(PackageCachePolicy.DescribeProbeFailure("MGCACHE[MAYBE]", 0));

    // ---- MG-17: the boot script provisions the cache root exactly like the others -----------------

    [Fact]
    public void TheMountOwnershipScript_CreatesTheCacheRoot()
    {
        // Environment-independent on purpose. This box has no userns remap, so a Docker leg asserting
        // the cache's group would pass for the wrong reason (container uid 1000 IS the host uid here,
        // so it can write anything). The script's TEXT is the fact that survives that.
        var script = UsernsRemapPolicy.MountOwnershipScript("/vm");
        Assert.Contains("\"$root/" + PackageCachePolicy.CachesDirectoryName + "\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCacheRoot_IsInTheGroupShareLoop_NotJustCreated()
    {
        // Creating caches/ without grouping it would leave a directory the daemon owns and the remapped
        // jail cannot write — the feature would fail at the first spawn with an EACCES nobody expected.
        // The loop is the line that hands down gid 101000 and the setgid bit.
        var script = UsernsRemapPolicy.MountOwnershipScript("/vm");
        var loopStart = script.IndexOf("for d in ", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "the ownership script no longer has a group-share loop");
        var loopHeader = script[loopStart..script.IndexOf("; do", loopStart, StringComparison.Ordinal)];
        Assert.Contains(PackageCachePolicy.CachesDirectoryName, loopHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCacheRoot_IsChownedToTheDaemonUser_LikeItsSiblings()
    {
        var script = UsernsRemapPolicy.MountOwnershipScript("/vm");
        var chownLine = script.Split("; ")
            .First(s => s.StartsWith("chown " + UsernsRemapPolicy.RemapUser + " ", StringComparison.Ordinal));
        Assert.Contains(PackageCachePolicy.CachesDirectoryName, chownLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOwnershipScript_StillNeverChownsRecursively()
        // Regression guard on the shape this edit touched: a recursive chown would strip the remapped
        // uid off every byte the jails legitimately wrote, on every boot — and the cache is now the
        // largest such tree by far.
        => Assert.DoesNotContain("chown -R", UsernsRemapPolicy.MountOwnershipScript("/vm"), StringComparison.Ordinal);

    // ---- The budget's floor ----------------------------------------------------------------------

    [Fact]
    public void ABudgetBelowTheFloor_IsRefused()
    {
        // A too-small budget does not make a smaller cache, it makes a permanent cache MISS — every
        // spawn evicting what the last one downloaded. That is a quiet degradation, so it is typed.
        var ex = Assert.Throws<PackageCacheException>(
            () => new PackageCacheManager("/vm", budgetBytes: 1024));
        Assert.Contains("minimum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultBudget_IsAboveTheFloor()
        => Assert.True(PackageCachePolicy.DefaultBudgetBytes >= PackageCachePolicy.MinimumBudgetBytes);

    [Fact]
    public void TheFloor_HoldsAtLeastTwoOfThisRepositorysClosures()
    {
        // The measurement this feature exists for: Mainguard.slnx's NuGet closure is 1.7 GB. A floor
        // below two of those is a thrash, and the number is written down here so a later edit that
        // lowers it has to argue with the measurement.
        const long measuredClosureBytes = 1_700_000_000;
        Assert.True(PackageCachePolicy.MinimumBudgetBytes >= 2 * measuredClosureBytes,
            $"the floor {PackageCachePolicy.MinimumBudgetBytes} cannot hold two 1.7 GB closures");
    }
}
