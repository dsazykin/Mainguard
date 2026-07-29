using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-43 RequiresDocker legs: the daemon-owned package cache against a <b>real</b> hardened jail.
///
/// <para>The fast unit tests own everything that is a property of the code (the mount is outside the
/// worktree, the environment names the mount, the boot script provisions <c>caches/</c> with the jail
/// group) — deliberately, because a Docker leg for any of those would pass on a permissive engine or an
/// unmapped runner for reasons that have nothing to do with the property. What is left here is the
/// handful of facts a dictionary cannot tell you: how big the tmpfs <c>$HOME</c> really is, that a
/// write bigger than it really does fail there and really does succeed in the cache, that one jail
/// really cannot see another's cache, and that the in-container probe really distinguishes its three
/// verdicts.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public class PackageCacheDockerTests
{
    private readonly ITestOutputHelper _out;

    public PackageCacheDockerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Comfortably over the 256 MiB tmpfs, comfortably under anything that would strain a
    /// runner's disk. 320 MiB written with <c>dd</c> from <c>/dev/zero</c>.</summary>
    private const int OverTmpfsMiB = 320;

    // ---- The premise, measured rather than assumed ---------------------------------------------------

    /// <summary>
    /// The measurement this whole feature exists for: <c>$HOME</c> is a tmpfs and it is 256 MiB. If this
    /// ever stops being true the rest of this file is measuring nothing — and a green suite would be
    /// hiding that the cache had become unnecessary, or (worse) that the contrast test below was passing
    /// because the home had quietly grown.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheJailsHome_IsATmpfsOf256MiB()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-premise");

        var df = await fx.Engine.ExecAsync(
            handle.ContainerId,
            new[] { "sh", "-c", $"df -m {ContainerSpecBuilder.AgentHome} | tail -1" },
            CancellationToken.None);
        _out.WriteLine($"df {ContainerSpecBuilder.AgentHome} => {df.Stdout.Trim()}");

        var totalMiB = ParseDfTotalMiB(df.Stdout);
        Assert.NotNull(totalMiB);
        Assert.InRange(totalMiB!.Value, 200, 300);
    }

    /// <summary>
    /// And the consequence, measured: a write the size of a real dependency closure's first few hundred
    /// megabytes fails in <c>$HOME</c>. This is the "<c>dd</c> of 400 MiB stops at 256" observation, run
    /// by the suite instead of quoted in a comment.
    /// </summary>
    [RequiresDockerFact]
    public async Task AWriteLargerThanTheTmpfs_FailsInTheJailsHome()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-home-full");

        var write = await Dd(fx, handle.ContainerId, ContainerSpecBuilder.AgentHome + "/blob", OverTmpfsMiB);
        _out.WriteLine($"dd into $HOME => exit {write.ExitCode}: {write.Stderr.Trim()}");

        Assert.NotEqual(0, write.ExitCode);
    }

    // ---- The cache mount, in a real container ---------------------------------------------------------

    [RequiresDockerFact]
    public async Task AJailWithACache_HasTheMountPresent()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-present", packageCachePath: fx.NewTempCache("pc-present"));

        var probe = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"test -d {PackageCachePolicy.SandboxMount} && echo PRESENT" },
            CancellationToken.None);

        Assert.Equal("PRESENT", probe.Stdout.Trim());
    }

    [RequiresDockerFact]
    public async Task AJailWithACache_CanWriteToIt()
    {
        // Separate from presence: a bind mount that exists and refuses writes is the failure mode that
        // breaks a restore halfway, and a presence-only assertion is green for it.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-write", packageCachePath: fx.NewTempCache("pc-write"));

        var write = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"echo hello > {PackageCachePolicy.SandboxMount}/probe && echo WROTE" },
            CancellationToken.None);

        Assert.Equal("WROTE", write.Stdout.Trim());
    }

    /// <summary>
    /// The whole point, as one measurement: the write that fails in <c>$HOME</c> succeeds in the cache.
    /// Nothing about the jail changed except where the bytes go.
    /// </summary>
    [RequiresDockerFact]
    public async Task AWriteLargerThanTheTmpfs_SucceedsInTheCache()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-big", packageCachePath: fx.NewTempCache("pc-big"));

        var write = await Dd(fx, handle.ContainerId, PackageCachePolicy.SandboxMount + "/blob", OverTmpfsMiB);
        _out.WriteLine($"dd into the cache => exit {write.ExitCode}: {write.Stderr.Trim()}");

        Assert.Equal(0, write.ExitCode);
    }

    [RequiresDockerFact]
    public async Task TheCache_IsNotTheSameFilesystemAsTheTmpfsHome()
    {
        // Belt to the braces of the size test: if a future edit pointed the mount back at a tmpfs, the
        // dd contrast would still pass on a runner whose tmpfs happened to be large.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-fs", packageCachePath: fx.NewTempCache("pc-fs"));

        var home = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"df -m {ContainerSpecBuilder.AgentHome} | tail -1" }, default);
        var cache = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"df -m {PackageCachePolicy.SandboxMount} | tail -1" }, default);
        _out.WriteLine($"home : {home.Stdout.Trim()}");
        _out.WriteLine($"cache: {cache.Stdout.Trim()}");

        var cacheMiB = ParseDfTotalMiB(cache.Stdout);
        Assert.NotNull(cacheMiB);
        Assert.True(cacheMiB!.Value > 1024,
            $"the cache reports only {cacheMiB} MiB — it is not on the daemon's ext4 filesystem");
    }

    [RequiresDockerFact]
    public async Task TheCache_IsNotInsideTheWorkspace()
    {
        // The rejected non-solution, checked from inside the container: nothing the cache holds can be
        // reached from /workspace, so `git add -A` in the agent's tree cannot pick any of it up.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-ws", packageCachePath: fx.NewTempCache("pc-ws"));

        await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"echo x > {PackageCachePolicy.SandboxMount}/marker" }, default);
        var find = await fx.Engine.ExecAsync(
            handle.ContainerId,
            new[] { "sh", "-c", $"find {ContainerSpecBuilder.WorkspaceTarget} -name marker -print | head -1" },
            default);

        Assert.Equal(string.Empty, find.Stdout.Trim());
    }

    // ---- The ownership grant, measured where the runner's uid cannot flatter it -------------------------

    /// <summary>
    /// The regression this exists for, and the reason it runs the jail as <b>uid 1002</b>.
    ///
    /// <para>Every other cache test here uses <c>SandboxFixture.NewTempCache</c>, which chmods 0777 to
    /// take the runner's uid mapping out of the question — deliberately, because those tests are about
    /// the MOUNT. This one is about the GRANT, so it uses the shipped <see cref="PackageCacheManager"/>
    /// against an ordinary VM root (default umask, no chmod, no <c>mainguard-jail</c> group), exactly as
    /// <c>MergeQueueEndToEndDockerTests</c> builds one per test under <c>/tmp</c>.</para>
    ///
    /// <para><b>Why uid 1002.</b> Ask of any assertion: could this pass in an environment where the thing
    /// under test does not exist? At the image's default uid 1000 it could, and did — on this developer
    /// box the container's uid 1000 IS the daemon's uid, so the jail is the directory's OWNER and writes
    /// it whatever the group says. That is precisely why the original grant bug was invisible here and
    /// failed on CI, whose runner uid is not 1000; it is the same shape as MG-3's attack test going green
    /// for the wrong reason. Running the jail as an identity that owns nothing and is in no shared group
    /// reproduces the CI position on EVERY machine, so this probe fails before the fix and passes after
    /// it in both environments rather than only in one.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ACachePreparedByTheShippedManager_IsWritableByAJailWhoseUidOwnsNothing()
    {
        await using var fx = new SandboxFixture();
        var manager = new PackageCacheManager(fx.NewTempVmRoot());
        var usage = manager.Prepare("repo", "grant-probe");
        _out.WriteLine(usage.Describe());

        var handle = await fx.SpawnAsync(
            agentId: "grant-probe", agentUid: 1002, supervisorUid: 1003,
            packageCachePath: manager.PathFor("repo", "grant-probe"));

        var write = await fx.Engine.ExecAsync(
            handle.ContainerId,
            new[] { "sh", "-c", $"echo hello > {PackageCachePolicy.SandboxMount}/probe && echo WROTE" },
            CancellationToken.None);
        _out.WriteLine($"write as uid 1002 => exit {write.ExitCode}: {write.Stdout.Trim()} {write.Stderr.Trim()}");

        Assert.Equal("WROTE", write.Stdout.Trim());
    }

    /// <summary>
    /// The same grant, one level deeper: a package manager does not write a file at the mount root, it
    /// creates a tree. Inverted separately because a leaf that is writable but hands its children the
    /// wrong group (no setgid) breaks on the SECOND level, after restore has already started — which is
    /// the mid-restore failure this feature exists to remove.
    /// </summary>
    [RequiresDockerFact]
    public async Task AJailWhoseUidOwnsNothing_CanCreateTheNestedTreeARestoreNeeds()
    {
        await using var fx = new SandboxFixture();
        var manager = new PackageCacheManager(fx.NewTempVmRoot());
        manager.Prepare("repo", "grant-nested");

        var handle = await fx.SpawnAsync(
            agentId: "grant-nested", agentUid: 1002, supervisorUid: 1003,
            packageCachePath: manager.PathFor("repo", "grant-nested"));

        var write = await fx.Engine.ExecAsync(
            handle.ContainerId,
            new[]
            {
                "sh", "-c",
                $"mkdir -p {PackageCachePolicy.SandboxMount}/nuget/packages/pkg/1.0.0 "
                + $"&& echo x > {PackageCachePolicy.SandboxMount}/nuget/packages/pkg/1.0.0/.nupkg.metadata "
                + "&& echo WROTE",
            },
            CancellationToken.None);
        _out.WriteLine($"nested write as uid 1002 => exit {write.ExitCode}: {write.Stdout.Trim()} {write.Stderr.Trim()}");

        Assert.Equal("WROTE", write.Stdout.Trim());
    }

    // ---- Cross-tenant isolation, in real containers -----------------------------------------------------

    [RequiresDockerFact]
    public async Task OneAgentsJail_CannotReadAnotherAgentsCache()
    {
        // The supply-chain argument, measured. Agent A poisons its own cache; agent B's jail must not be
        // able to see the file at all — not at the mount point (B has its own), and not at A's VM path
        // (B mounts nothing there).
        await using var fx = new SandboxFixture();
        var cacheA = fx.NewTempCache("pc-tenant-a");
        var cacheB = fx.NewTempCache("pc-tenant-b");

        var a = await fx.SpawnAsync(agentId: "pc-tenant-a", packageCachePath: cacheA);
        var b = await fx.SpawnAsync(agentId: "pc-tenant-b", packageCachePath: cacheB);

        await fx.Engine.ExecAsync(
            a.ContainerId, new[] { "sh", "-c", $"echo poison > {PackageCachePolicy.SandboxMount}/newtonsoft.dll" }, default);

        var atMount = await fx.Engine.ExecAsync(
            b.ContainerId, new[] { "cat", PackageCachePolicy.SandboxMount + "/newtonsoft.dll" }, default);
        Assert.NotEqual(0, atMount.ExitCode);
    }

    [RequiresDockerFact]
    public async Task OneAgentsJail_CannotReachAnotherAgentsCacheByItsVmPath()
    {
        // Inverted separately: "B's mount point has no such file" is also true of an implementation that
        // shares one cache under a per-agent SUBDIRECTORY, where B could simply walk up. Here B is asked
        // for A's absolute VM path, which is the only other way in.
        await using var fx = new SandboxFixture();
        var cacheA = fx.NewTempCache("pc-path-a");

        var a = await fx.SpawnAsync(agentId: "pc-path-a", packageCachePath: cacheA);
        var b = await fx.SpawnAsync(agentId: "pc-path-b", packageCachePath: fx.NewTempCache("pc-path-b"));

        await fx.Engine.ExecAsync(
            a.ContainerId, new[] { "sh", "-c", $"echo poison > {PackageCachePolicy.SandboxMount}/newtonsoft.dll" }, default);

        var atVmPath = await fx.Engine.ExecAsync(b.ContainerId, new[] { "cat", cacheA + "/newtonsoft.dll" }, default);
        _out.WriteLine($"B reading A's VM path {cacheA} => exit {atVmPath.ExitCode}: {atVmPath.Stderr.Trim()}");
        Assert.NotEqual(0, atVmPath.ExitCode);
    }

    [RequiresDockerFact]
    public async Task OneAgentsJail_CannotEscapeUpwardsFromItsOwnCacheMount()
    {
        // Inside a container a bind-mount root's `..` is the CONTAINER's parent, not the host's — which
        // is the reason the leaf is what gets mounted. Measured rather than reasoned: the sibling
        // directory names that exist on the VM must not be listable from inside.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-updir", packageCachePath: fx.NewTempCache("pc-updir"));

        var up = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", $"ls -a {PackageCachePolicy.SandboxMount}/.." }, default);
        _out.WriteLine($"ls ..  => {up.Stdout.Trim()}");

        // /var/cache/.. is /var — a container directory. It must not contain the daemon's cache layout.
        Assert.DoesNotContain("repo", up.Stdout.Split('\n').Select(s => s.Trim()));
    }

    // ---- The in-container probe really distinguishes its verdicts ----------------------------------------

    [RequiresDockerFact]
    public async Task TheProbe_ReportsOk_InAJailThatHasAWritableCache()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-probe-ok", packageCachePath: fx.NewTempCache("pc-probe-ok"));

        var probe = await fx.Engine.ExecAsync(handle.ContainerId, PackageCachePolicy.WritabilityProbe(), default);
        _out.WriteLine($"probe(ok) => exit {probe.ExitCode} stdout='{probe.Stdout.Trim()}'");

        Assert.Null(PackageCachePolicy.DescribeProbeFailure(probe.Stdout, probe.ExitCode));
    }

    [RequiresDockerFact]
    public async Task TheProbe_ReportsMissing_InAJailWithNoCacheMount()
    {
        // "Our records say mounted, the container says otherwise" — the MG-42 class, for this feature.
        // A jail with no cache mount must not read as OK.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "pc-probe-missing");

        var probe = await fx.Engine.ExecAsync(handle.ContainerId, PackageCachePolicy.WritabilityProbe(), default);
        _out.WriteLine($"probe(missing) => exit {probe.ExitCode} stdout='{probe.Stdout.Trim()}'");

        var failure = PackageCachePolicy.DescribeProbeFailure(probe.Stdout, probe.ExitCode);
        Assert.NotNull(failure);
        Assert.Contains("no directory", failure, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task TheProbe_ReportsUnwritable_WhenTheMountIsThereButRefusesWrites()
    {
        // The third verdict, which neither of the other two can produce. Built by hand because
        // ContainerSpecBuilder (correctly) refuses to construct a read-only cache mount at all — so this
        // exercises the branch that exists for a mount the daemon did not build, e.g. one whose backing
        // directory lost its group share.
        await using var fx = new SandboxFixture();
        var cache = fx.NewTempCache("pc-probe-ro");

        var (id, _) = await fx.CreateJailOnSegmentAsync(
            "pcprobe" + Guid.NewGuid().ToString("N")[..8], "pc-probe-ro",
            packageCachePath: cache,
            // Flipped read-only AFTER the builder has approved the spec: the container is what is under
            // test here, not the builder (which refuses to construct this mount read-only at all).
            mutateForTest: create => create.HostConfig.Mounts!
                .Single(m => m.Target == PackageCachePolicy.SandboxMount).ReadOnly = true);

        var probe = await fx.Engine.ExecAsync(id, PackageCachePolicy.WritabilityProbe(), default);
        _out.WriteLine($"probe(read-only) => exit {probe.ExitCode} stdout='{probe.Stdout.Trim()}'");

        var failure = PackageCachePolicy.DescribeProbeFailure(probe.Stdout, probe.ExitCode);
        Assert.NotNull(failure);
        Assert.Contains("cannot create a file", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the consequence, through the shipped engine: a jail whose cache is unusable is never handed
    /// back. This is the loud-failure requirement, measured — the cache directory is made unwritable to
    /// everyone, so no uid mapping on any runner can make the jail able to write it.
    /// </summary>
    [RequiresDockerFact]
    public async Task SpawningWithAnUnusableCache_IsATypedRefusal_NotAJail()
    {
        await using var fx = new SandboxFixture();
        var cache = fx.NewUnwritableTempCache("pc-refuse");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.SpawnAsync(agentId: "pc-refuse", packageCachePath: cache));

        // The fixture wraps spawn failures with diagnostics; the typed cause is what matters.
        Assert.IsType<PackageCacheUnavailableException>(ex.InnerException);
    }

    [RequiresDockerFact]
    public async Task ARefusedSpawn_LeavesNoRunningContainerBehind()
    {
        // Inverted separately: throwing while leaving a started jail around would hand the next caller a
        // container that looks alive and cannot restore anything.
        await using var fx = new SandboxFixture();
        var cache = fx.NewUnwritableTempCache("pc-refuse2");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.SpawnAsync(agentId: "pc-refuse2", packageCachePath: cache));

        var all = await fx.Docker.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        Assert.DoesNotContain(all, c => c.Names.Any(n => n.Contains("pc-refuse2", StringComparison.Ordinal)));
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static Task<SandboxExecResult> Dd(SandboxFixture fx, string containerId, string path, int mib)
        => fx.Engine.ExecAsync(
            containerId,
            new[] { "sh", "-c", $"dd if=/dev/zero of={path} bs=1M count={mib} 2>&1" },
            CancellationToken.None);

    /// <summary>Total size in MiB from a <c>df -m</c> tail line, or null when the line is not readable —
    /// a null is a FAILED assertion at every call site, never a quietly-skipped one.</summary>
    private static int? ParseDfTotalMiB(string dfLine)
    {
        var fields = dfLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], out var mib) ? mib : null;
    }
}
