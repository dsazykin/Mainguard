using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-42 RequiresDocker legs: the per-repo toolchain layer built by the <b>shipped</b>
/// <see cref="ToolchainProvisioner"/> against a <b>real</b> container runtime, and then run inside a
/// <b>real hardened jail</b>.
///
/// <para>The measured fact this feature exists for: the curated agent base image has no .NET, so
/// Mainguard could not verify its own repository — <c>command -v dotnet</c> in
/// <c>mainguard-agent-base:latest</c> answers nothing. These tests assert the same thing the fast unit
/// tests assert, but through Docker, because the unit tests model the image store with a dictionary and
/// a dictionary cannot tell you whether a Dockerfile this code generated actually builds.</para>
///
/// <para><b>Cost note.</b> The first run downloads and unpacks a ~1 GB SDK, so it is minutes; every run
/// after that is a cache hit against the content-addressed tag, which is the whole point and is itself
/// asserted below.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public class ToolchainProvisioningDockerTests
{
    private const string RepoHandle = "toolchain-docker-test";
    private static readonly TimeSpan BuildBudget = TimeSpan.FromMinutes(20);

    private readonly ITestOutputHelper _out;

    public ToolchainProvisioningDockerTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The premise, asserted rather than assumed: the base image really does lack .NET. If this ever
    /// stops being true the rest of this file is measuring nothing, and a green suite would be hiding
    /// that the feature had become unnecessary — or, worse, that the probe below was passing because
    /// the base image had grown a dotnet all along.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheBaseImage_HasNoDotnet_WhichIsWhyALayerIsNeeded()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "tc-premise");

        var probe = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", "command -v dotnet" }, CancellationToken.None);

        Assert.NotEqual(0, probe.ExitCode);
    }

    /// <summary>
    /// End to end: resolve the base image's digest exactly as the spawn preflight does, build the
    /// declared layer through the shipped provisioner, start a REAL hardened jail from it, and watch
    /// <c>dotnet --version</c> answer the pinned SDK.
    /// </summary>
    [RequiresDockerFact]
    public async Task ADeclaredToolchain_IsBuilt_AndRunsInAHardenedJail()
    {
        using var cts = new CancellationTokenSource(BuildBudget);
        await using var fx = new SandboxFixture();

        var layer = await BuildLayerAsync(fx, "dotnet-10\n", cts.Token);

        // A hardened jail from the LAYER, through the same engine the daemon spawns with.
        var handle = await fx.SpawnFromImageAsync(layer.ImageRef, agentId: "tc-dotnet", ct: cts.Token);

        var version = await fx.Engine.ExecAsync(handle.ContainerId, new[] { "dotnet", "--version" }, cts.Token);
        _out.WriteLine($"dotnet --version => exit {version.ExitCode}: {version.Stdout.Trim()} {version.Stderr.Trim()}");

        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("10.0.3", version.Stdout.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The layer must not quietly change the jail's posture. Installs run as root inside the build; the
    /// running container must still be the non-root agent uid, on the same read-only rootfs. A layer
    /// that ended on <c>USER root</c> would undo a control <see cref="ContainerSpecBuilder"/> cannot
    /// re-check, because it asserts the create SPEC and the user comes from the IMAGE.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheLayeredJail_StillRunsAsTheNonRootAgentUid()
    {
        using var cts = new CancellationTokenSource(BuildBudget);
        await using var fx = new SandboxFixture();

        var layer = await BuildLayerAsync(fx, "dotnet-10\n", cts.Token);
        var handle = await fx.SpawnFromImageAsync(layer.ImageRef, agentId: "tc-uid", ct: cts.Token);

        var uid = await fx.Engine.ExecAsync(handle.ContainerId, new[] { "id", "-u" }, cts.Token);
        Assert.Equal("1000", uid.Stdout.Trim());

        // ...and the rootfs is still read-only (the layer added an image layer, not a writable surface).
        var write = await fx.Engine.ExecAsync(
            handle.ContainerId, new[] { "sh", "-c", "touch /opt/should-not-be-writable" }, cts.Token);
        Assert.NotEqual(0, write.ExitCode);
    }

    /// <summary>
    /// MG-27 continuity: the layer records the base digest it was built on, and that digest is the one
    /// the base ref actually resolves to right now. Without this the layer would be a place the pin
    /// silently stopped: everything downstream would be verifying a derived image whose parentage
    /// nobody checked.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheLayer_RecordsTheBaseDigestItWasBuiltOn()
    {
        using var cts = new CancellationTokenSource(BuildBudget);
        await using var fx = new SandboxFixture();

        var baseDigest = await fx.Engine.ImageDigestAsync(fx.ImageRef, cts.Token);
        Assert.True(SandboxImageDigest.IsDigest(baseDigest), $"base image did not resolve to a digest: '{baseDigest}'");

        var layer = await BuildLayerAsync(fx, "dotnet-10\n", cts.Token);
        var builder = new DockerToolchainImageBuilder(fx.Docker);
        var labels = await builder.InspectLabelsAsync(layer.ImageRef, cts.Token);

        Assert.NotNull(labels);
        Assert.Equal(baseDigest, labels![ToolchainProvisioner.BaseDigestLabel]);
        Assert.Equal("dotnet-10", labels[ToolchainProvisioner.SpecLabel]);

        // The ref handed to the spawn path is the LAYER's own content digest, not its tag.
        Assert.True(SandboxImageDigest.IsDigest(layer.ImageRef), $"expected a digest, got '{layer.ImageRef}'");
    }

    /// <summary>
    /// The cost claim, measured rather than asserted in prose: a second <c>EnsureAsync</c> for the same
    /// declaration does no build at all. If this regressed, every agent spawn on a .NET repo would pay
    /// a multi-minute SDK install.
    /// </summary>
    [RequiresDockerFact]
    public async Task ASecondEnsure_IsACacheHit_AndCostsAlmostNothing()
    {
        using var cts = new CancellationTokenSource(BuildBudget);
        await using var fx = new SandboxFixture();

        var first = await BuildLayerAsync(fx, "dotnet-10\n", cts.Token); // may or may not build; warms the cache

        var sw = Stopwatch.StartNew();
        var second = await BuildLayerAsync(fx, "dotnet-10\n", cts.Token);
        sw.Stop();

        _out.WriteLine($"second EnsureAsync took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(first.ImageRef, second.ImageRef);
        // Generous by an order of magnitude: a real SDK build is minutes. This asserts "no build ran",
        // not a latency SLO, so a loaded runner cannot make it flaky.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"a cache hit took {sw.Elapsed} — the layer was almost certainly rebuilt.");
    }

    /// <summary>
    /// A catalogued-but-uninstalled toolchain must be caught. This drives the real
    /// <see cref="ISandboxEngine.ExecAsync"/> probe against a jail from the BASE image while declaring
    /// <c>dotnet-10</c> — the exact shape of "our records say provisioned, the container says
    /// otherwise" — and requires the probe to see it.
    /// </summary>
    [RequiresDockerFact]
    public async Task AJailWithoutTheDeclaredToolchain_FailsItsProbe()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "tc-missing");

        var recipe = ToolchainCatalog.TryGet("dotnet-10")!;
        var probe = await fx.Engine.ExecAsync(handle.ContainerId, recipe.Probe, CancellationToken.None);

        Assert.NotEqual(0, probe.ExitCode);
    }

    /// <summary>
    /// Records WHICH engine and image store produced this run's results, in the test output, on every
    /// run — pass or fail.
    ///
    /// <para>This exists because of a concrete gap: four of these tests failed on CI and passed locally,
    /// the deciding difference was the image store's <c>RepoDigests</c> behaviour, and <b>the runner's
    /// Docker version appears nowhere in the workflow logs</b>. Answering "what was different about that
    /// environment?" required reasoning from an error string instead of reading a fact. It is an
    /// observation, not a gate — asserting a particular engine version would fail the suite on every
    /// runner-image bump for no safety benefit — but the empty/non-empty <c>RepoDigests</c> line is the
    /// one that explains this whole class of failure, so it is printed unconditionally.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task RecordTheEngineAndItsImageStoreBehaviour()
    {
        await using var fx = new SandboxFixture();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var version = await fx.Docker.System.GetVersionAsync(cts.Token);
        var info = await fx.Docker.System.GetSystemInfoAsync(cts.Token);
        var inspect = await fx.Docker.Images.InspectImageAsync(fx.ImageRef, cts.Token);

        var repoDigests = inspect.RepoDigests is { Count: > 0 }
            ? string.Join(",", inspect.RepoDigests)
            : "[] — a `<name>@sha256:` FROM CANNOT resolve against this store and would attempt a PULL";

        _out.WriteLine($"engine    : {version.Version} (api {version.APIVersion}, {version.Os}/{version.Arch})");
        _out.WriteLine($"driver    : {info.Driver}");
        _out.WriteLine($"base ref  : {fx.ImageRef}");
        _out.WriteLine($"base id   : {inspect.ID}");
        _out.WriteLine($"repoTags  : {string.Join(",", inspect.RepoTags ?? new List<string>())}");
        _out.WriteLine($"repoDigests: {repoDigests}");

        // The only thing worth asserting here: the base resolves to a content digest at all, which is
        // the input the whole pin is built on. Everything else above is evidence for the next reader.
        Assert.True(SandboxImageDigest.IsDigest(SandboxImageDigest.Normalize(inspect.ID)),
            $"the base image's id '{inspect.ID}' is not a usable content digest");
    }

    // ---- harness ---------------------------------------------------------

    private async Task<ProvisionedToolchain> BuildLayerAsync(SandboxFixture fx, string declarationText, CancellationToken ct)
    {
        var baseDigest = await fx.Engine.ImageDigestAsync(fx.ImageRef, ct);
        var declaration = ToolchainDeclarationResolver.Parse(declarationText, RepoHandle);

        var provisioner = new ToolchainProvisioner(new DockerToolchainImageBuilder(fx.Docker), _out.WriteLine);
        var layer = await provisioner.EnsureAsync(RepoHandle, declaration, baseDigest, ct);

        Assert.NotNull(layer);
        return layer!;
    }
}
