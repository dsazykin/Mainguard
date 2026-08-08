using System.Collections.Generic;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The sampler's arithmetic, exercised without a daemon. These are the two functions that decide whether
/// a reading exists at all, so the cases that must return <c>null</c> matter more than the ones that
/// return a number: every one of them is a case where the obvious implementation quietly yields <c>0</c>,
/// which the UI would render as a confident, wrong "this agent is idle".
/// </summary>
public class DockerResourceSamplerMathTests
{
    [Fact]
    public void Cpu_MatchesDockerStatsConvention_OneFullCoreIs100()
    {
        // 1s of container CPU time against 16s of system CPU time across 16 cores = one full core.
        var stats = Stats(cpu: 2_000_000_000, preCpu: 1_000_000_000,
            system: 32_000_000_000, preSystem: 16_000_000_000, cpus: 16);

        Assert.Equal(100.0, DockerResourceSampler.TryComputeCpuPercent(stats)!.Value, 3);
    }

    [Fact]
    public void Cpu_TwoCoresPinned_Reads200()
    {
        var stats = Stats(cpu: 3_000_000_000, preCpu: 1_000_000_000,
            system: 32_000_000_000, preSystem: 16_000_000_000, cpus: 16);

        Assert.Equal(200.0, DockerResourceSampler.TryComputeCpuPercent(stats)!.Value, 3);
    }

    /// <summary>
    /// The <c>one-shot=true</c> trap, as data: that query parameter makes the engine skip the priming
    /// read, so <c>precpu</c> comes back all zeros. A naive percentage off that is 0% — a fabricated
    /// number, not a missing one. It must be null.
    /// </summary>
    [Fact]
    public void Cpu_ZeroSystemDelta_IsUnknown_NotZero()
    {
        var stats = Stats(cpu: 2_000_000_000, preCpu: 0, system: 0, preSystem: 0, cpus: 16);

        Assert.Null(DockerResourceSampler.TryComputeCpuPercent(stats));
    }

    /// <summary>A counter reset across a container restart yields a negative delta — unknown, not 0.</summary>
    [Fact]
    public void Cpu_NegativeDelta_IsUnknown_NotZero()
    {
        var stats = Stats(cpu: 1_000_000_000, preCpu: 5_000_000_000,
            system: 32_000_000_000, preSystem: 16_000_000_000, cpus: 16);

        Assert.Null(DockerResourceSampler.TryComputeCpuPercent(stats));
    }

    [Fact]
    public void Cpu_UnknownCpuCount_IsUnknown_NotZero()
    {
        var stats = Stats(cpu: 2_000_000_000, preCpu: 1_000_000_000,
            system: 32_000_000_000, preSystem: 16_000_000_000, cpus: 0);

        Assert.Null(DockerResourceSampler.TryComputeCpuPercent(stats));
    }

    /// <summary>Absent <c>online_cpus</c> falls back to the per-CPU array length rather than giving up.</summary>
    [Fact]
    public void Cpu_FallsBackToPercpuLength_WhenOnlineCpusAbsent()
    {
        var stats = Stats(cpu: 2_000_000_000, preCpu: 1_000_000_000,
            system: 32_000_000_000, preSystem: 16_000_000_000, cpus: 0);
        stats.CPUStats.CPUUsage.PercpuUsage = new List<ulong> { 1, 2, 3, 4 };

        Assert.Equal(25.0, DockerResourceSampler.TryComputeCpuPercent(stats)!.Value, 3);
    }

    /// <summary>
    /// cgroup v2 reports <c>inactive_file</c>; the page cache is subtracted so the figure tracks the
    /// workload instead of drifting up with every file the agent reads.
    /// </summary>
    [Fact]
    public void Memory_CgroupV2_SubtractsInactiveFile()
    {
        var stats = Stats(cpu: 0, preCpu: 0, system: 0, preSystem: 0, cpus: 1);
        stats.MemoryStats.Usage = 500_000_000;
        stats.MemoryStats.Stats = new Dictionary<string, ulong> { ["inactive_file"] = 200_000_000 };

        Assert.Equal(300_000_000.0, DockerResourceSampler.TryComputeMemoryBytes(stats)!.Value, 3);
    }

    /// <summary>cgroup v1 names it differently — and the generation follows the host kernel, not the
    /// engine version, so pinning the engine does not pin this.</summary>
    [Fact]
    public void Memory_CgroupV1_SubtractsTotalInactiveFile()
    {
        var stats = Stats(cpu: 0, preCpu: 0, system: 0, preSystem: 0, cpus: 1);
        stats.MemoryStats.Usage = 500_000_000;
        stats.MemoryStats.Stats = new Dictionary<string, ulong> { ["total_inactive_file"] = 100_000_000 };

        Assert.Equal(400_000_000.0, DockerResourceSampler.TryComputeMemoryBytes(stats)!.Value, 3);
    }

    /// <summary>A cache figure larger than usage is bogus; degrade to raw usage, never to a negative.</summary>
    [Fact]
    public void Memory_ImplausibleCache_DegradesToRawUsage()
    {
        var stats = Stats(cpu: 0, preCpu: 0, system: 0, preSystem: 0, cpus: 1);
        stats.MemoryStats.Usage = 100_000_000;
        stats.MemoryStats.Stats = new Dictionary<string, ulong> { ["inactive_file"] = 900_000_000 };

        Assert.Equal(100_000_000.0, DockerResourceSampler.TryComputeMemoryBytes(stats)!.Value, 3);
    }

    [Fact]
    public void Memory_NoUsageReported_IsUnknown_NotZero()
    {
        var stats = Stats(cpu: 0, preCpu: 0, system: 0, preSystem: 0, cpus: 1);
        stats.MemoryStats.Usage = 0;

        Assert.Null(DockerResourceSampler.TryComputeMemoryBytes(stats));
    }

    private static ContainerStatsResponse Stats(
        ulong cpu, ulong preCpu, ulong system, ulong preSystem, uint cpus) => new()
        {
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = cpu },
                SystemUsage = system,
                OnlineCPUs = cpus,
            },
            PreCPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = preCpu },
                SystemUsage = preSystem,
            },
            MemoryStats = new MemoryStats(),
        };
}
