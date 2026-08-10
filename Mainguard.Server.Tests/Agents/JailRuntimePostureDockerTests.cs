using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// Tickets #59/#60 — the jail's posture read <b>from inside a running container</b>, rather than from
/// the create request that asked for it.
///
/// <para><b>The measurement that produced this file.</b> Seven jail controls were removed from a real
/// running jail with the entire 98-test <c>RequiresDocker</c> suite green. Every one of them was
/// asserted only where the container is CONSTRUCTED — <c>ContainerSpecBuilder</c>'s own guards, or a
/// test reading back <c>HostConfig</c> — and nowhere that the container RUNS. A spec is a request; the
/// kernel is the authority. Where those two can disagree, only the kernel's answer is evidence.</para>
///
/// <para><b>Attribution is the design constraint.</b> Each probe below is paired with something that
/// makes its verdict attributable to the control it names: a positive control that must succeed (so
/// "everything failed" is not the explanation), or a variant directed at the process's OWN resources
/// where every permission check trivially passes (so only a syscall filter can produce the refusal).
/// The assertion this file replaces had neither, and was satisfied by kernel read semantics rather than
/// by any hardening at all.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class JailRuntimePostureDockerTests
{
    private readonly ITestOutputHelper _out;

    public JailRuntimePostureDockerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Linux capability numbers. Bit <c>n</c> of the hex mask in <c>/proc/self/status</c>.</summary>
    private const int CapSysPtrace = 19;

    private const int CapSysAdmin = 21;
    private const int CapNetRaw = 13;

    // ---- The premise, measured rather than assumed --------------------------------------------------

    /// <summary>
    /// Every syscall probe below runs through the jail's own Python. If the interpreter ever leaves the
    /// image, those tests must fail as "the probe could not run" — a distinct, named fact — rather than
    /// quietly reporting a refusal they never attempted. That is the exact failure mode this whole
    /// ticket exists to remove, so the premise is asserted rather than assumed.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheJailHasThePythonInterpreterEverySyscallProbeNeeds()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "rt-premise");

        var probe = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "command -v python3");
        _out.WriteLine($"command -v python3 => exit {probe.ExitCode}: {probe.Stdout.Trim()}");

        Assert.Equal(0, probe.ExitCode);
    }

    // ---- G-15 / G2 control 4: the kernel's view of the jail's privileges ----------------------------

    /// <summary>
    /// The capability set, read from <c>/proc/self/status</c> in the running jail.
    ///
    /// <para><b>Why the bounding set and not the effective one.</b> The jail runs as uid 1000, and a
    /// non-root process has an empty effective set whatever the container was created with — including
    /// under <c>--privileged</c>. An assertion on <c>CapEff</c> is therefore green on a jail with no
    /// capability hardening at all. <c>CapBnd</c> is the ceiling the container was created with and is
    /// the field <c>CapDrop</c>/<c>CapAdd</c> actually move, so it is the one that can distinguish them.</para>
    ///
    /// <para><c>CAP_SYS_ADMIN</c> and <c>CAP_NET_RAW</c> are named specifically because both are in
    /// Docker's DEFAULT bounding set and neither is in this jail's: their absence is what separates
    /// "capabilities were dropped" from "the daemon's defaults were accepted".</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRunningJail_HasNoSysPtraceAndAMinimalCapabilityBoundingSet()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "rt-caps");

        var status = await ReadProcStatusAsync(fx, handle.ContainerId);
        var bounding = ParseCapMask(status, "CapBnd");
        _out.WriteLine($"CapBnd=0x{bounding:x} ({BitOperations.PopCount((ulong)bounding)} capabilities)");

        Assert.False(HasCapability(bounding, CapSysPtrace),
            $"CAP_SYS_PTRACE is in the jail's bounding set (CapBnd=0x{bounding:x}) — G2 control 4 is not in effect.");
        Assert.False(HasCapability(bounding, CapSysAdmin),
            $"CAP_SYS_ADMIN is in the jail's bounding set (CapBnd=0x{bounding:x}); the daemon's default "
            + "capability set was accepted rather than dropped.");
        Assert.False(HasCapability(bounding, CapNetRaw),
            $"CAP_NET_RAW is in the jail's bounding set (CapBnd=0x{bounding:x}); the daemon's default "
            + "capability set was accepted rather than dropped.");

        // The spec adds back seven. A ceiling materially larger than that is the default set, whatever
        // individual names happen to be missing from it.
        var count = BitOperations.PopCount((ulong)bounding);
        Assert.True(count <= 8,
            $"the jail's capability ceiling holds {count} capabilities (CapBnd=0x{bounding:x}); the hardened "
            + "spec drops ALL and adds back seven.");
    }

    /// <summary>
    /// <c>no-new-privileges</c> and a loaded seccomp filter, as the kernel reports them.
    ///
    /// <para><b>What <c>Seccomp: 2</c> does and does not prove.</b> It proves a filter is loaded, so it
    /// catches <c>seccomp=unconfined</c>. It does NOT catch the profile being dropped from
    /// <c>SecurityOpt</c> entirely, because Docker then applies its own default profile and the field
    /// still reads 2. Which profile is loaded is what the syscall probes above measure; this asserts the
    /// weaker, separate fact that filtering is on at all.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRunningJail_RunsUnderNoNewPrivilegesAndASeccompFilter()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "rt-nnp");

        var status = await ReadProcStatusAsync(fx, handle.ContainerId);

        Assert.Equal("1", Field(status, "NoNewPrivs"));
        Assert.Equal("2", Field(status, "Seccomp")); // 2 == SECCOMP_MODE_FILTER
    }

    // ---- MG-17: the userns remap, as far as a runtime probe can reach it ----------------------------

    /// <summary>
    /// The jail must not opt OUT of the daemon's user-namespace remap, and its live <c>uid_map</c> must
    /// agree with what the daemon is actually configured to do.
    ///
    /// <para><b>The honest limit of this test, stated rather than hidden.</b> Docker's per-container
    /// <c>UsernsMode</c> has no value meaning "definitely remap": <c>""</c> inherits the daemon's setting
    /// and <c>"host"</c> is an explicit opt-out. On a daemon with no remap configured — which is the CI
    /// runner's — the two produce an IDENTICAL runtime posture, so no in-container observation can
    /// separate them there. The daemon-level fact is asserted at boot by <c>UsernsRemapPolicy</c>, and
    /// the spec-level refusal by <c>ContainerSpecBuilder.AssertUsernsRemapped</c>.</para>
    ///
    /// <para>What this adds is the two facts that ARE checkable at runtime: the live container is not
    /// carrying the opt-out (true on every daemon), and its <c>uid_map</c> agrees with the daemon's
    /// advertised remap (which goes red for the opt-out on any daemon that has one configured). Both
    /// arms are recorded to the job summary so a reader can see which one this run exercised.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRunningJail_DidNotOptOutOfTheDaemonsUsernsRemap()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "rt-userns");

        var info = await fx.Docker.System.GetSystemInfoAsync();
        var daemonRemapped = info.SecurityOptions?.Any(
            o => o.Contains("userns", StringComparison.OrdinalIgnoreCase)) == true;

        var uidMap = await fx.ExecAsync(handle.ContainerId, "cat", "/proc/self/uid_map");
        var inspect = await fx.InspectAsync(handle.ContainerId);
        var mode = inspect.HostConfig.UsernsMode ?? string.Empty;

        var lines = new[]
        {
            $"daemon securityOptions : {string.Join(", ", info.SecurityOptions ?? new List<string>())}",
            $"daemon userns-remapped : {daemonRemapped}",
            $"container UsernsMode   : '{mode}'",
            $"in-jail /proc/self/uid_map : {uidMap.Stdout.Trim()}",
        };
        foreach (var line in lines)
        {
            _out.WriteLine(line);
        }

        JobSummary.Write("MG-17 userns posture (ticket #59)", lines);

        // True on every daemon: the running container is not carrying the opt-out.
        Assert.NotEqual(UsernsRemapPolicy.OptOutUsernsMode, mode);

        // And the kernel's own mapping agrees with what the daemon says it does. On a remapped daemon
        // the opt-out shows up here as an identity map; on an unremapped one both arms expect identity.
        var mappedToHostUid = ParseUidMapHostStart(uidMap.Stdout);
        Assert.NotNull(mappedToHostUid);
        Assert.Equal(daemonRemapped, mappedToHostUid!.Value != 0);
    }

    // ---- The shared toolchain tree: read-only where it runs, not where it is requested ---------------

    /// <summary>
    /// The toolchain mount is what lets ONE tree be shared by every jail on the machine, and it is safe
    /// to share for exactly one reason: nothing in a jail can write it. A writable share would let agent
    /// A replace the interpreter agent B's verification runs under — the merge gate decided by another
    /// tenant.
    ///
    /// <para>Flipping <c>ReadOnly = false</c> on that bind left the whole suite green: the only assertion
    /// was on the mount list of the create request. This attempts the write and requires the kernel to
    /// answer <c>EROFS</c> specifically — not merely a non-zero exit, which a missing mount, a bad path
    /// or a permission bit would also produce.</para>
    ///
    /// <para>Three controls make the refusal attributable: the tree is world-writable on the host, so no
    /// file mode can be the cause; the mount is READ back successfully in the same exec, so it is
    /// genuinely there; and a write to <c>/tmp</c> succeeds in the same exec, so "this jail cannot write
    /// anything" is not the explanation.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheSharedToolchainMount_RefusesWritesFromInsideTheJail_WithEROFS()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(
            agentId: "rt-toolchain", toolchainsRootPath: fx.NewTempToolchainRoot());

        var probe = await fx.ExecAsync(handle.ContainerId, "sh", "-c", ToolchainProbeScript);
        _out.WriteLine($"toolchain probe => exit {probe.ExitCode}: {probe.Stdout.Trim()}");
        var frames = probe.Stdout;

        // Controls first: the mount is present and readable, and this jail can write somewhere.
        Assert.Equal("READ", JailSyscallProbe.ReadFrame(frames, "TCREAD"));
        Assert.Equal("WROTE", JailSyscallProbe.ReadFrame(frames, "TCTMP"));

        // The claim: the write is refused, and refused by the mount rather than by anything else.
        Assert.Equal("REFUSED", JailSyscallProbe.ReadFrame(frames, "TCWRITE"));
        Assert.Contains("Read-only file system", JailSyscallProbe.ReadFrame(frames, "TCERR"), StringComparison.Ordinal);
    }

    // ---- MG-43: the cache is per-agent because the SHIPPED path layout makes it so -------------------

    /// <summary>
    /// Two agents in the SAME repository get caches that cannot see each other — proven by poisoning one
    /// and asking the other, in real containers.
    ///
    /// <para><b>Why this is not covered by the cross-tenant tests that already exist.</b> Those call
    /// <c>SandboxFixture.NewTempCache</c>, which hands each jail a path the TEST invented; they would
    /// stay green against an implementation that gives every agent in a repo one shared cache, because
    /// the sharing would happen in <c>PackageCacheManager.PathFor</c> and that method is never called.
    /// Measured: collapsing the layout to one cache per repo left the entire Docker suite green, caught
    /// only by a string comparison of two computed paths in a merge-queue test that never opens a
    /// container. This test derives both paths from the shipped manager, so the mount each jail receives
    /// is the one production would give it.</para>
    ///
    /// <para>The path comparison is asserted too — first, as the cheap premise — because if the manager
    /// returned one path the two jails would share a mount and the read below would SUCCEED, which is a
    /// far more confusing failure than "these are the same path".</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TwoAgentsInOneRepo_GetCachesNeitherCanReadFromTheOther()
    {
        await using var fx = new SandboxFixture();
        var manager = new PackageCacheManager(fx.NewTempVmRoot());
        const string repo = "shared-repo";

        manager.Prepare(repo, "rt-cache-a");
        manager.Prepare(repo, "rt-cache-b");
        var pathA = manager.PathFor(repo, "rt-cache-a");
        var pathB = manager.PathFor(repo, "rt-cache-b");
        _out.WriteLine($"A: {pathA}");
        _out.WriteLine($"B: {pathB}");

        Assert.NotEqual(pathA, pathB);

        // uid 1002 owns nothing on this box and is in no shared group, which reproduces the CI position
        // on every machine — see PackageCacheDockerTests for why 1000 would flatter the result.
        var a = await fx.SpawnAsync(agentId: "rt-cache-a", agentUid: 1002, supervisorUid: 1003, packageCachePath: pathA);
        var b = await fx.SpawnAsync(agentId: "rt-cache-b", agentUid: 1002, supervisorUid: 1003, packageCachePath: pathB);

        var poison = await fx.ExecAsync(
            a.ContainerId, "sh", "-c", $"echo poison > {PackageCachePolicy.SandboxMount}/newtonsoft.dll && echo WROTE");
        // The control: A really did write. Without it, B's failure to read could simply mean the file
        // was never created — which is what a broken poisoning step reports too.
        Assert.Equal("WROTE", poison.Stdout.Trim());

        var atMount = await fx.ExecAsync(b.ContainerId, "cat", PackageCachePolicy.SandboxMount + "/newtonsoft.dll");
        Assert.NotEqual(0, atMount.ExitCode);
        Assert.DoesNotContain("poison", atMount.Stdout, StringComparison.Ordinal);

        // …and not by A's absolute VM path either, which is the other way in when one tree is shared
        // under per-agent subdirectories.
        var atVmPath = await fx.ExecAsync(b.ContainerId, "cat", pathA + "/newtonsoft.dll");
        _out.WriteLine($"B reading A's VM path => exit {atVmPath.ExitCode}: {atVmPath.Stderr.Trim()}");
        Assert.NotEqual(0, atVmPath.ExitCode);
        Assert.DoesNotContain("poison", atVmPath.Stdout, StringComparison.Ordinal);
    }

    // ---- MG-3 stage 3: a jail that outlived the read-only mirror change is recreated -----------------

    /// <summary>
    /// A persistent jail whose mirror is mounted READ-WRITE must be recreated on the next spawn, not
    /// reused. Mount options are fixed at container create, so recreating is the only way to change them
    /// — and without this the MG-3 write path survives the upgrade in every container already running.
    ///
    /// <para><b>"Identical except" is the whole design</b>, borrowed from the stale-secrets test above it.
    /// A hand-built stand-in would be recreated by the image/network/DNS/secret-layout checks that
    /// already exist, so it would pass whether or not the writable-mirror check is there at all. Reusing
    /// the real container's own <c>HostConfig</c> and flipping ONE mount leaves the mirror's read-write
    /// bit as the only difference, so a pass is attributable to that check and nothing else.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AJailWhoseMirrorIsMountedReadWrite_IsRecreatedRatherThanReused()
    {
        await using var fx = new SandboxFixture();
        var mirror = fx.NewTempBareMirror();

        // The control: a healthy, current jail IS reused. Without it, "recreated" below could equally
        // mean "this spawn never reuses anything", which would make the assertion meaningless.
        var current = await fx.SpawnAsync(agentId: "rt-mirror", bareRepoPath: mirror);
        var reused = await fx.SpawnAsync(agentId: "rt-mirror", bareRepoPath: mirror);
        Assert.Equal(current.ContainerId, reused.ContainerId);
        Assert.True(reused.Reused);

        var inspect = await fx.Docker.Containers.InspectContainerAsync(current.ContainerId);
        var name = inspect.Name.TrimStart('/');
        var legacyHostConfig = inspect.HostConfig;
        var mirrorMount = Assert.Single(
            legacyHostConfig.Mounts, m => string.Equals(m.Target, mirror, StringComparison.Ordinal));
        mirrorMount.ReadOnly = false; // the pre-MG-3-stage-3 posture, and the ONLY difference

        await fx.Docker.Containers.RemoveContainerAsync(
            current.ContainerId, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true });

        var legacy = await fx.Docker.Containers.CreateContainerAsync(new Docker.DotNet.Models.CreateContainerParameters
        {
            Name = name,
            // Config.Image (the ref the container was CREATED with), not inspect.Image (the resolved id)
            // — otherwise the image-identity check fires and recreates for that reason instead.
            Image = inspect.Config.Image,
            User = inspect.Config.User,
            Cmd = inspect.Config.Cmd,
            Env = inspect.Config.Env,
            WorkingDir = inspect.Config.WorkingDir,
            Labels = inspect.Config.Labels,
            HostConfig = legacyHostConfig,
        });
        await fx.Docker.Containers.StartContainerAsync(
            legacy.ID, new Docker.DotNet.Models.ContainerStartParameters());

        // Proof the planted container really is the pre-change posture, so a recreate below is a
        // response to something that was actually there.
        var plantedInspect = await fx.Docker.Containers.InspectContainerAsync(legacy.ID);
        Assert.True(
            plantedInspect.Mounts.Single(m => m.Destination == mirror).RW,
            "the planted jail was supposed to carry a READ-WRITE mirror; it does not, so nothing was proven.");

        var afterUpgrade = await fx.SpawnAsync(agentId: "rt-mirror", bareRepoPath: mirror);

        Assert.NotEqual(legacy.ID, afterUpgrade.ContainerId);
        Assert.False(afterUpgrade.Reused);

        // …and the replacement fixed the thing it was triggered by, rather than merely churning.
        var fresh = await fx.Docker.Containers.InspectContainerAsync(afterUpgrade.ContainerId);
        Assert.False(
            fresh.Mounts.Single(m => m.Destination == mirror).RW,
            "the recreated jail still mounts the shared mirror read-write.");
    }

    // ---- probes ------------------------------------------------------------------------------------

    /// <summary>The toolchain-mount probe. Reads before it writes, and takes its own positive controls,
    /// so every frame means the same thing whether the mount is read-only or not.</summary>
    private static string ToolchainProbeScript =>
        $"tc={ToolchainPaths.SandboxMount}; err=/tmp/rt-tc-stderr; : > \"$err\"; "
        // Control: the mount is there and readable. "Refused" from a path that does not exist is not a
        // measurement of anything.
        + "printf 'TCREAD['; if [ -r \"$tc/interpreter\" ]; then printf 'READ'; else printf 'ABSENT'; fi; printf ']'; "
        // The attack.
        + "printf 'TCWRITE['; if ( printf 'tampered\\n' > \"$tc/interpreter\" ) 2>>\"$err\"; "
        + "  then printf 'WROTE'; else printf 'REFUSED'; fi; printf ']'; "
        // Control: this jail can write SOMEWHERE, so a refusal above is about the mount and not about
        // the container being unable to write at all.
        + "printf 'TCTMP['; if ( printf 'x' > /tmp/rt-tc-canary ) 2>/dev/null; then printf 'WROTE'; else printf 'REFUSED'; fi; printf ']'; "
        // The kernel's own words, so EROFS is distinguishable from EACCES/ENOENT.
        + "printf 'TCERR['; head -c 300 \"$err\" | tr -d ']\\n' | tr -s ' '; printf ']'";

    // ---- readers -----------------------------------------------------------------------------------

    private static async Task<string> ReadProcStatusAsync(SandboxFixture fx, string containerId)
    {
        var status = await fx.ExecAsync(containerId, "cat", "/proc/self/status");
        Assert.True(status.ExitCode == 0 && status.Stdout.Contains("CapBnd", StringComparison.Ordinal),
            $"could not read /proc/self/status in the jail: exit={status.ExitCode} <<{status.Stdout}>>");
        return status.Stdout;
    }

    private static string Field(string procStatus, string name)
    {
        var line = procStatus
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal));
        Assert.True(line is not null,
            $"/proc/self/status carries no '{name}' field, so the fact it reports was never observed. "
            + $"Raw: <<{procStatus}>>");
        return line![(name.Length + 1)..].Trim();
    }

    private static long ParseCapMask(string procStatus, string name)
    {
        var raw = Field(procStatus, name);
        Assert.True(long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask),
            $"'{name}: {raw}' is not a readable hex capability mask.");
        return mask;
    }

    private static bool HasCapability(long mask, int capability) => (mask & (1L << capability)) != 0;

    /// <summary>The host uid that container uid 0 maps to, from a <c>/proc/&lt;pid&gt;/uid_map</c> line
    /// (<c>&lt;container-start&gt; &lt;host-start&gt; &lt;length&gt;</c>). Null when unreadable, which is
    /// a FAILED assertion at the call site rather than a quietly-skipped one.</summary>
    internal static long? ParseUidMapHostStart(string uidMap)
    {
        var line = uidMap.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        var fields = line?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 2 }
               && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostStart)
            ? hostStart
            : null;
    }
}
