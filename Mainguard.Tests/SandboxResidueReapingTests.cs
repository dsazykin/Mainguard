using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F27 + F32 + F28 — the three things the sandbox left behind, and the refusals that make
/// collecting them safe.</b>
///
/// <para>Every test here is about a REFUSAL, and that is deliberate. A reaper that deletes too little
/// wastes disk and address-pool slots; a reaper that deletes too much destroys a live agent's network
/// or the image its jail is running on, and does it in the background where nothing can explain the
/// symptom afterwards. So the decision halves of both collectors are pure, and every condition that
/// saves a resource is pinned here — with no Docker daemon involved, because a Docker-gated suite is
/// exactly the one that does not run on the box where someone changes this logic.</para>
/// </summary>
public class SandboxResidueReapingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- F27: leaked network segments

    private const string RepoHash = "abc123def456abc123";
    private const string AgentId = "agent-1";

    private static string Segment => EgressProxyConfigurator.AgentSegmentName(RepoHash, AgentId);

    private static string Jail => ContainerSpecBuilder.ContainerName(RepoHash, AgentId);

    private static string AgentNetRole => EgressProxyConfigurator.RoleFor(isInternal: true);

    private static SegmentReapVerdict Decide(
        string? roleLabel = null,
        TimeSpan? age = null,
        IReadOnlyCollection<string>? jails = null,
        IReadOnlyCollection<string>? attached = null,
        string? segment = null)
        => SandboxSegmentReapPolicy.Decide(
            segment ?? Segment,
            roleLabel ?? EgressProxyConfigurator.RoleFor(isInternal: true),
            Now - (age ?? TimeSpan.FromHours(3)),
            Now,
            SandboxSegmentReapPolicy.DefaultGrace,
            jails ?? Array.Empty<string>(),
            attached ?? Array.Empty<string>());

    /// <summary>The correlation the whole reaper rests on: a segment names exactly one jail, derivably.</summary>
    [Fact]
    public void ASegmentName_NamesTheJailItWasCreatedFor()
    {
        Assert.Equal(Jail, SandboxSegmentReapPolicy.JailNameFor(Segment));
    }

    [Theory]
    [InlineData("mainguard-agents")]        // the SHARED agent network — one character from the prefix
    [InlineData("mainguard-egress")]
    [InlineData("bridge")]
    [InlineData("")]
    [InlineData(null)]
    public void ANetworkThatIsNotAPerAgentSegment_IsNotEvenACandidate(string? name)
    {
        Assert.Null(SandboxSegmentReapPolicy.JailNameFor(name));
    }

    [Fact]
    public void AnEmptyStampedSegmentWhoseJailIsGone_IsReaped()
    {
        var verdict = Decide();
        Assert.True(verdict.Reap);
        Assert.Contains(Jail, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary><b>The one that matters.</b> A stopped jail is a REUSABLE jail — the engine's whole
    /// reuse path exists for it — so its segment is not garbage, and the container list the reaper
    /// consults is deliberately the all-states one.</summary>
    [Fact]
    public void ASegmentWhoseJailStillExists_IsKept_EvenThoughNothingIsAttachedToIt()
    {
        var verdict = Decide(jails: new[] { Jail });
        Assert.Equal(SegmentReapDecision.KeptJailExists, verdict.Decision);
    }

    [Fact]
    public void ASegmentWithSomethingOtherThanTheProxyOnIt_IsKept()
    {
        var verdict = Decide(attached: new[] { "some-other-container" });
        Assert.Equal(SegmentReapDecision.KeptOccupied, verdict.Decision);
        Assert.Contains("some-other-container", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A spawn creates the segment BEFORE the container, so a young empty segment is a spawn in
    /// flight. Reaping it would break the very spawn that made it.</summary>
    [Fact]
    public void AYoungEmptySegment_IsKept_BecauseASpawnCreatesTheSegmentFirst()
    {
        var verdict = Decide(age: TimeSpan.FromMinutes(1));
        Assert.Equal(SegmentReapDecision.KeptTooYoung, verdict.Decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("egress-net")]
    [InlineData("something-else")]
    public void ASegmentMainguardDidNotStamp_IsKept(string? role)
    {
        // Same rule the reuse gate applies: we only ever touch a network we created. A network that
        // merely shares our prefix belongs to something else.
        var verdict = SandboxSegmentReapPolicy.Decide(
            Segment, role, Now - TimeSpan.FromDays(2), Now,
            SandboxSegmentReapPolicy.DefaultGrace, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(SegmentReapDecision.KeptNotOurs, verdict.Decision);
    }

    [Fact]
    public void TheSharedAgentNetwork_IsNeverReapable_EvenWhenEmptyAndStamped()
    {
        var verdict = SandboxSegmentReapPolicy.Decide(
            EgressProxyConfigurator.AgentNetworkName, AgentNetRole, Now - TimeSpan.FromDays(30), Now,
            SandboxSegmentReapPolicy.DefaultGrace, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(SegmentReapDecision.KeptNotOurs, verdict.Decision);
    }

    // ------------------------------------------------------------ F32: stale toolchain image layers

    private static ToolchainImageCandidate Layer(
        string id, TimeSpan age, int containers = 0, params string[] tags)
        => new(id, tags.Length == 0 ? new[] { ToolchainProvisioner.ImageName + ":" + id[^4..] } : tags,
            Now - age, containers);

    private static IReadOnlyList<ToolchainImageGcVerdict> Judge(
        IReadOnlyList<ToolchainImageCandidate> layers, params string[] keep)
        => ToolchainImageGcPolicy.Judge(
            layers, keep, Now, ToolchainImageGcPolicy.DefaultMinimumAge, retain: 0);

    /// <summary><b>The invariant the whole feature is judged on: an image a container references is
    /// never removed.</b> Asserted over every other condition simultaneously — old, unwanted, and well
    /// past the retention window — because "in use" has to win on its own, not only when nothing else
    /// applies.</summary>
    [Fact]
    public void AnImageAnyContainerReferences_IsNeverRemoved()
    {
        var layers = new[]
        {
            Layer("sha256:aaaa", TimeSpan.FromDays(90), containers: 1),
            Layer("sha256:bbbb", TimeSpan.FromDays(90), containers: 7),
        };

        Assert.All(Judge(layers), v =>
        {
            Assert.False(v.Remove);
            Assert.Equal(ToolchainImageGcDecision.KeptInUse, v.Decision);
        });
    }

    [Fact]
    public void TheLayerTheCallerStillWants_IsKept_ByTagOrById()
    {
        var byTag = Layer("sha256:cccc", TimeSpan.FromDays(30), 0, ToolchainProvisioner.ImageName + ":wanted");
        var byId = Layer("sha256:dddd", TimeSpan.FromDays(30));

        var verdicts = Judge(new[] { byTag, byId },
            ToolchainProvisioner.ImageName + ":wanted", "sha256:dddd");

        Assert.All(verdicts, v => Assert.Equal(ToolchainImageGcDecision.KeptWanted, v.Decision));
    }

    /// <summary>A layer is built BEFORE the jail that will use it — during a spawn that can run for
    /// minutes — so a young unreferenced layer is a build in flight, not garbage.</summary>
    [Fact]
    public void AFreshlyBuiltUnreferencedLayer_IsKept()
    {
        var verdict = Assert.Single(Judge(new[] { Layer("sha256:eeee", TimeSpan.FromMinutes(20)) }));
        Assert.Equal(ToolchainImageGcDecision.KeptTooYoung, verdict.Decision);
    }

    [Fact]
    public void AnImageCarryingATagOutsideTheToolchainRepository_IsNeverRemoved()
    {
        var shared = Layer("sha256:ffff", TimeSpan.FromDays(90), 0,
            ToolchainProvisioner.ImageName + ":abcd", "mainguard-agent-base:latest");

        var verdict = Assert.Single(Judge(new[] { shared }));
        Assert.Equal(ToolchainImageGcDecision.KeptForeign, verdict.Decision);
    }

    [Fact]
    public void TheNewestRemovableLayers_AreRetained_SoARevertDoesNotRebuild()
    {
        var layers = new[]
        {
            Layer("sha256:0001", TimeSpan.FromDays(10)),
            Layer("sha256:0002", TimeSpan.FromDays(20)),
            Layer("sha256:0003", TimeSpan.FromDays(30)),
            Layer("sha256:0004", TimeSpan.FromDays(40)),
        };

        var verdicts = ToolchainImageGcPolicy.Judge(
            layers, Array.Empty<string>(), Now, ToolchainImageGcPolicy.DefaultMinimumAge, retain: 2);

        Assert.Equal(ToolchainImageGcDecision.KeptRetained, verdicts[0].Decision); // 10 days — newest
        Assert.Equal(ToolchainImageGcDecision.KeptRetained, verdicts[1].Decision); // 20 days
        Assert.True(verdicts[2].Remove);
        Assert.True(verdicts[3].Remove);
    }

    /// <summary>Retention counts only what would otherwise be REMOVED, so an in-use layer never spends
    /// one of the retained slots and shields a genuinely stale one behind it.</summary>
    [Fact]
    public void RetentionDoesNotSpendItsSlotsOnLayersThatWereKeptAnyway()
    {
        var layers = new[]
        {
            Layer("sha256:1111", TimeSpan.FromDays(1), containers: 1),  // newest, but in use
            Layer("sha256:2222", TimeSpan.FromDays(20)),
            Layer("sha256:3333", TimeSpan.FromDays(30)),
        };

        var verdicts = ToolchainImageGcPolicy.Judge(
            layers, Array.Empty<string>(), Now, ToolchainImageGcPolicy.DefaultMinimumAge, retain: 1);

        Assert.Equal(ToolchainImageGcDecision.KeptInUse, verdicts[0].Decision);
        Assert.Equal(ToolchainImageGcDecision.KeptRetained, verdicts[1].Decision);
        Assert.True(verdicts[2].Remove);
    }

    [Fact]
    public void JudgingNothing_RemovesNothing()
    {
        Assert.Empty(Judge(Array.Empty<ToolchainImageCandidate>()));
    }

    // ------------------------------------------------ F28: the jail posture a reuse never re-checked

    private const string Ext4Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";

    private static HostConfig BuiltHostConfig(SandboxLimits limits) =>
        ContainerSpecBuilder.Build(new ContainerSpecRequest(
            RepoHash: RepoHash,
            AgentId: AgentId,
            WorktreePath: Ext4Worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: limits,
            NetworkName: EgressProxyConfigurator.AgentNetworkName,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: UsernsRemapPolicy.InheritDaemonRemap,
            DnsServerAddress: "172.30.0.2")).HostConfig;

    /// <summary>The floor: a jail this builder just created has drifted from itself in no respect. If
    /// this ever fails, every reuse recreates and the persistent jail stops being persistent.</summary>
    [Fact]
    public void AJailTheBuilderJustCreated_ShowsNoDrift()
    {
        var limits = SandboxLimits.Default;
        var verdict = ContainerSpecBuilder.InspectPosture(BuiltHostConfig(limits), limits);

        Assert.False(verdict.MustRecreate);
        Assert.False(verdict.MustRetighten);
    }

    /// <summary>The operator-lowers-the-ceiling case. It must be a RETIGHTEN and not a recreate: moving
    /// a slider is not a reason to destroy every running agent's session.</summary>
    [Fact]
    public void AJailCreatedWithAHigherCeiling_IsRetightenedInPlace_NotRecreated()
    {
        var wasCreatedWith = SandboxLimits.Default with { MemoryBytes = 8L * 1024 * 1024 * 1024, Cpus = 8 };
        var operatorLoweredTo = SandboxLimits.Default with { MemoryBytes = 1L * 1024 * 1024 * 1024, Cpus = 1 };

        var verdict = ContainerSpecBuilder.InspectPosture(BuiltHostConfig(wasCreatedWith), operatorLoweredTo);

        Assert.False(verdict.MustRecreate);
        Assert.True(verdict.MustRetighten);
        Assert.Contains("memory", verdict.Describe(), StringComparison.Ordinal);
        Assert.Contains("CPU ceiling", verdict.Describe(), StringComparison.Ordinal);
    }

    /// <summary>The audit's own example: a jail created before MG-26 has no CPU cap at all, and was
    /// reused forever without one.</summary>
    [Fact]
    public void APreMg26JailWithNoCpuCeiling_IsCaught()
    {
        var actual = BuiltHostConfig(SandboxLimits.Default);
        actual.NanoCPUs = 0;

        var verdict = ContainerSpecBuilder.InspectPosture(actual, SandboxLimits.Default);

        Assert.True(verdict.MustRetighten);
        Assert.Contains("no CPU ceiling at all", verdict.Describe(), StringComparison.Ordinal);
    }

    public static TheoryData<string, Action<HostConfig>> HardeningRegressions => new()
    {
        { "writable rootfs", h => h.ReadonlyRootfs = false },
        { "privileged", h => h.Privileged = true },
        { "no cap drop", h => h.CapDrop = new List<string>() },
        { "ptrace back", h => h.CapAdd = new List<string> { "SYS_PTRACE" } },
        { "no seccomp", h => h.SecurityOpt = new List<string> { "no-new-privileges" } },
        { "seccomp unconfined", h => h.SecurityOpt = new List<string> { "no-new-privileges", "seccomp=unconfined" } },
        { "no no-new-privileges", h => h.SecurityOpt = new List<string> { SeccompProfile.SecurityOptValue } },
        { "userns opt-out", h => h.UsernsMode = UsernsRemapPolicy.OptOutUsernsMode },
        { "no rlimits", h => h.Ulimits = new List<Ulimit>() },
    };

    /// <summary>Every hardening control is fixed at create, so drift in any one of them is a RECREATE —
    /// there is no update endpoint that could put it back.</summary>
    [Theory]
    [MemberData(nameof(HardeningRegressions))]
    public void AJailMissingAnyHardeningControl_IsRecreated(string _, Action<HostConfig> regress)
    {
        var actual = BuiltHostConfig(SandboxLimits.Default);
        regress(actual);

        var verdict = ContainerSpecBuilder.InspectPosture(actual, SandboxLimits.Default);

        Assert.True(verdict.MustRecreate);
    }

    /// <summary>An unanswerable question is not a reason to destroy a jail — the convention every
    /// sibling probe on the reuse path already follows.</summary>
    [Fact]
    public void AJailWhoseHostConfigCannotBeRead_ReportsNoDrift()
    {
        var verdict = ContainerSpecBuilder.InspectPosture(null, SandboxLimits.Default);

        Assert.False(verdict.MustRecreate);
        Assert.False(verdict.MustRetighten);
    }
}
