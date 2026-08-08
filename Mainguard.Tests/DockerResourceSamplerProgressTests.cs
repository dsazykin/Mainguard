using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The sampler must capture a reading that the Docker client hands it through <see cref="IProgress{T}"/>
/// and then returns — with no dependence on timing.
///
/// <para><b>Why this exists.</b> The first implementation passed <c>new Progress&lt;T&gt;(...)</c>, which
/// is specified to raise its callback <i>asynchronously</i> (posted to the captured
/// <c>SynchronizationContext</c>, or to the thread pool when there is none). The awaited call could
/// therefore complete while the callback was still queued, and the sampler would report
/// "no reading returned" for a container that had answered perfectly. Measured in isolation, the value
/// was missing in <b>1721 of 2000</b> trials — yet it passed on a developer box against real Docker and
/// failed only on a loaded CI runner, because extra async work inside the client usually let the queued
/// callback win the race. That is the "works on my machine" failure in its purest form.</para>
///
/// <para>The fake below reports and returns <b>immediately</b>, with nothing after the callback to hide
/// the race behind. Under <c>Progress&lt;T&gt;</c> this test fails essentially always; under inline
/// delivery it cannot fail, because the value is visible by construction rather than by timing.</para>
/// </summary>
public class DockerResourceSamplerProgressTests
{
    [Fact]
    public async Task Sampler_CapturesAReadingDeliveredThroughIProgress_WithoutRacingTheReturn()
    {
        var docker = new StatsOnlyDockerClient(BusyStats());
        var sampler = new DockerResourceSampler(docker);

        var samples = await sampler.SampleAsync(new[] { ("agent-1", "container-1") }, CancellationToken.None);

        var sample = Assert.Single(samples);
        Assert.True(sample.UnavailableReason is null,
            $"the reading was delivered but not captured: reason='{sample.UnavailableReason}'");
        Assert.Equal(100.0, sample.CpuPercent!.Value, 3);
        Assert.Equal(500_000_000.0 - 200_000_000.0, sample.RamBytes!.Value, 3);
    }

    /// <summary>Repeats it enough that a timing-dependent implementation cannot pass by luck.</summary>
    [Fact]
    public async Task Sampler_CapturesTheReading_OnEveryOneOfManyAttempts()
    {
        var sampler = new DockerResourceSampler(new StatsOnlyDockerClient(BusyStats()));

        var missed = 0;
        for (int i = 0; i < 200; i++)
        {
            var samples = await sampler.SampleAsync(new[] { ("agent-1", "container-1") }, CancellationToken.None);
            if (samples[0].CpuPercent is null) missed++;
        }

        Assert.True(missed == 0, $"the reading was lost on {missed}/200 attempts — the delivery is racy");
    }

    private static ContainerStatsResponse BusyStats() => new()
    {
        CPUStats = new CPUStats
        {
            CPUUsage = new CPUUsage { TotalUsage = 2_000_000_000 },
            SystemUsage = 32_000_000_000,
            OnlineCPUs = 16,
        },
        PreCPUStats = new CPUStats
        {
            CPUUsage = new CPUUsage { TotalUsage = 1_000_000_000 },
            SystemUsage = 16_000_000_000,
        },
        MemoryStats = new MemoryStats
        {
            Usage = 500_000_000,
            Stats = new Dictionary<string, ulong> { ["inactive_file"] = 200_000_000 },
        },
    };

    /// <summary>
    /// A Docker client whose stats call reports through the progress callback and returns straight away.
    /// Everything else throws: if the sampler ever starts calling something else, that should be a loud
    /// failure rather than a silently-satisfied stub.
    /// </summary>
    private sealed class StatsOnlyDockerClient : IDockerClient, IContainerOperations
    {
        private readonly ContainerStatsResponse _stats;

        public StatsOnlyDockerClient(ContainerStatsResponse stats) => _stats = stats;

        public Task GetContainerStatsAsync(
            string id, ContainerStatsParameters parameters,
            IProgress<ContainerStatsResponse> progress, CancellationToken cancellationToken)
        {
            progress.Report(_stats);
            return Task.CompletedTask;
        }

        IContainerOperations IDockerClient.Containers => this;

        public DockerClientConfiguration Configuration => throw new NotSupportedException();
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(100);
        public IImageOperations Images => throw new NotSupportedException();
        public INetworkOperations Networks => throw new NotSupportedException();
        public IVolumeOperations Volumes => throw new NotSupportedException();
        public ISecretsOperations Secrets => throw new NotSupportedException();
        public IConfigOperations Configs => throw new NotSupportedException();
        public ISwarmOperations Swarm => throw new NotSupportedException();
        public ITasksOperations Tasks => throw new NotSupportedException();
        public ISystemOperations System => throw new NotSupportedException();
        public IPluginOperations Plugin => throw new NotSupportedException();
        public IExecOperations Exec => throw new NotSupportedException();
        public void Dispose() { }

        // ---- the rest of IContainerOperations: never reached on this path ----
        public Task<MultiplexedStream> AttachContainerAsync(string id, bool tty, ContainerAttachParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Stream> ExportContainerAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ExtractArchiveToContainerAsync(string id, ContainerPathStatParameters parameters, Stream stream, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<GetArchiveFromContainerResponse> GetArchiveFromContainerAsync(string id, GetArchiveFromContainerParameters parameters, bool statOnly, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Stream> GetContainerLogsAsync(string id, ContainerLogsParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task GetContainerLogsAsync(string id, ContainerLogsParameters parameters, CancellationToken cancellationToken, IProgress<string> progress) => throw new NotImplementedException();
        public Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Stream> GetContainerStatsAsync(string id, ContainerStatsParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IList<ContainerFileSystemChangeResponse>> InspectChangesAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ContainerProcessesResponse> ListProcessesAsync(string id, ContainerListProcessesParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task PauseContainerAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ContainersPruneResponse> PruneContainersAsync(ContainersPruneParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RenameContainerAsync(string id, ContainerRenameParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ResizeContainerTtyAsync(string id, ContainerResizeParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RestartContainerAsync(string id, ContainerRestartParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> StartContainerAsync(string id, ContainerStartParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task UnpauseContainerAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ContainerUpdateResponse> UpdateContainerAsync(string id, ContainerUpdateParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ContainerWaitResponse> WaitContainerAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
