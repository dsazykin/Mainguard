using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The Resource Monitor's data source, proven against a REAL container engine.
///
/// <para>The tab shipped rendering per-agent CPU/RAM over a sampler that was never written — the client
/// hard-coded <c>CpuPercent: 0, RamGb: 0</c> — so every agent read a convincing 0%. The decisive claim
/// here is therefore not "a formatter formats" but "a container doing real work produces a real,
/// non-zero, plausibly-bounded number". A busybox spin loop pins exactly one core, so the expected
/// reading is knowable rather than merely non-null: <c>docker stats</c> reports percent-of-one-core, so
/// one busy core must land near 100 and cannot exceed the machine's core count × 100.</para>
///
/// <para>Uses trivial <c>busybox</c> containers, so it needs the Docker daemon only and skips cleanly on
/// a box without the CI-built agent images. They carry no <c>mainguard.agent</c> label on purpose: the
/// sampler is handed explicit (agentId, containerId) targets, and labelling them would make them look
/// like live jails to <c>DockerAgentLister</c> — the swarm reconciler's sole liveness truth.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class ResourceSamplingDockerTests
{
    private const string TrivialImage = "busybox:latest";

    [RequiresDockerDaemonFact]
    public async Task Sampler_BusyContainer_ShouldReportRealNonZeroCpuAndMemory()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        if (!await EnsureTrivialImageAsync(docker, ct)) return;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = "agent-" + suffix;
        string? busyId = null;
        string? idleId = null;

        try
        {
            // One container spinning a core, one asleep. Sampling BOTH is what makes the assertion
            // meaningful: a sampler that returned a constant would pass on either container alone.
            busyId = await RunAsync(docker, "mainguard-stats-busy-" + suffix, agentId,
                "while true; do :; done", ct);
            idleId = await RunAsync(docker, "mainguard-stats-idle-" + suffix, agentId + "-idle",
                "sleep 300", ct);

            // Let the spin loop actually accumulate CPU time before the first delta window.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var sampler = new DockerResourceSampler(docker);
            var samples = await sampler.SampleAsync(
                new[] { (agentId, busyId!), (agentId + "-idle", idleId!) }, ct);

            var busy = Assert.Single(samples.Where(s => s.AgentId == agentId));
            var idle = Assert.Single(samples.Where(s => s.AgentId == agentId + "-idle"));

            // 1. Measured at all — not the "unavailable" degrade path.
            Assert.Null(busy.UnavailableReason);
            Assert.NotNull(busy.CpuPercent);
            Assert.NotNull(busy.RamBytes);

            // 2. A REAL number, not a hard-coded zero. This is the assertion the shipped code failed.
            Assert.True(busy.CpuPercent > 20,
                $"a container spinning a full core must report real CPU, got {busy.CpuPercent}");

            // 3. Bounded by physical reality — catches a mis-scaled formula that "passes" by being huge.
            var ceiling = Environment.ProcessorCount * 100.0 + 50;
            Assert.True(busy.CpuPercent < ceiling,
                $"CPU {busy.CpuPercent} exceeds {ceiling} for {Environment.ProcessorCount} cores");

            // 4. Memory is real bytes, not a placeholder.
            Assert.True(busy.RamBytes > 0, $"expected real resident bytes, got {busy.RamBytes}");

            // 5. The sampler DISCRIMINATES: the sleeping container must not read like the spinning one.
            Assert.NotNull(idle.CpuPercent);
            Assert.True(idle.CpuPercent < busy.CpuPercent / 2,
                $"idle ({idle.CpuPercent}) should be far below busy ({busy.CpuPercent})");
        }
        finally
        {
            await RemoveAsync(docker, busyId);
            await RemoveAsync(docker, idleId);
        }
    }

    /// <summary>
    /// A failed sample degrades to an explicit unknown, never to 0. The distinction is the whole point:
    /// <c>0%</c> and "no data" must not render the same, so the sampler must not be able to express the
    /// second as the first.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Sampler_MissingContainer_ShouldReportUnknownNotZero()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var sampler = new DockerResourceSampler(docker);
        var samples = await sampler.SampleAsync(
            new[] { ("ghost", "mainguard-not-a-real-container-" + Guid.NewGuid().ToString("N")[..8]) },
            cts.Token);

        var ghost = Assert.Single(samples);
        Assert.Null(ghost.CpuPercent);   // NOT 0
        Assert.Null(ghost.RamBytes);     // NOT 0
        Assert.False(string.IsNullOrWhiteSpace(ghost.UnavailableReason));
    }

    /// <param name="agentId">Only the sampler's output key — the sampler is given explicit
    /// (agentId, containerId) targets, so these containers deliberately carry NO <c>mainguard.agent</c>
    /// label. Labelling them would make them look like real jails to <c>DockerAgentLister</c>, which the
    /// swarm reconciler treats as its sole liveness truth.</param>
    private static async Task<string> RunAsync(
        IDockerClient docker, string name, string agentId, string script, CancellationToken ct)
    {
        _ = agentId;
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = TrivialImage,
            Name = name,
            Cmd = new List<string> { "sh", "-c", script },
        }, ct);
        await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        return created.ID;
    }

    private static async Task RemoveAsync(IDockerClient docker, string? id)
    {
        if (id is null) return;
        try
        {
            await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerApiException) { /* best-effort cleanup */ }
    }

    private static async Task<bool> EnsureTrivialImageAsync(IDockerClient docker, CancellationToken ct)
    {
        try
        {
            var images = await docker.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
            if (images.Any(i => i.RepoTags is not null && i.RepoTags.Contains(TrivialImage))) return true;

            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
                null, new Progress<JSONMessage>(), ct);
            return true;
        }
        catch (DockerApiException)
        {
            return false; // no registry access — nothing to prove, skip rather than fail
        }
    }
}
