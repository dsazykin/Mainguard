using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// One jail's sampled resource usage. <b>A null reading means NOT MEASURED, never zero.</b> The two are
/// different facts and the UI renders them differently: <c>0</c> is "this agent is idle", null is "we do
/// not know". Collapsing the second into the first is the exact false-reassurance shape this repo keeps
/// producing, so it is unrepresentable here rather than merely discouraged.
/// </summary>
/// <param name="AgentId">The <c>mainguard.agent</c> label value the jail carries.</param>
/// <param name="CpuPercent">Percent of ONE core, the <c>docker stats</c> convention: a container pinning
/// two cores reads 200. Null when the engine returned no usable delta.</param>
/// <param name="RamBytes">Resident bytes, cache-excluded the same way <c>docker stats</c> does it.</param>
/// <param name="UnavailableReason">Why a reading is null — carried for the daemon log, never rendered as
/// a number. Null exactly when both readings are present.</param>
public sealed record ContainerResourceSample(
    string AgentId,
    double? CpuPercent,
    double? RamBytes,
    string? UnavailableReason)
{
    /// <summary>An honest "we tried and could not measure it" for one agent.</summary>
    // PROBE M9 (DO NOT MERGE): "not measured" collapses into "measured zero" — the distinction the
    // sampler exists to preserve is removed.
    public static ContainerResourceSample Unavailable(string agentId, string reason) =>
        new(agentId, 0, 0, reason);
}

/// <summary>
/// Where per-jail CPU/RAM readings come from. Abstracted so the daemon-side join, the gRPC stream and the
/// client projection can all be driven with KNOWN values end to end — without that seam the only way to
/// test the wire would be to assert that a formatter formats, which is exactly the class of test that let
/// this feature ship with no data source behind it.
/// </summary>
public interface IContainerResourceSampler
{
    /// <summary>Samples the given jails. One agent's failure is that agent's row, not the whole call's.</summary>
    Task<IReadOnlyList<ContainerResourceSample>> SampleAsync(
        IEnumerable<(string AgentId, string ContainerId)> targets, CancellationToken ct = default);
}

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its handler INLINE, on the thread that calls
/// <see cref="Report"/>, instead of posting it elsewhere like <see cref="Progress{T}"/> does.
///
/// <para>Needed because Docker.DotNet delivers a one-shot stats reading through this callback and then
/// returns: with <see cref="Progress{T}"/> the value is captured on a different thread at an unspecified
/// later moment, so the awaiting code can observe "nothing arrived" for a call that succeeded. Inline
/// delivery makes the reading visible by the time the await completes, by construction rather than by
/// timing.</para>
/// </summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}

/// <summary>
/// The sampler for a host with no reachable container engine: every agent is explicitly UNKNOWN. Used
/// when the Docker client cannot even be constructed, so that case degrades to "not measured" like any
/// other sampling failure instead of taking the daemon down — or, worse, reporting a fleet of zeros.
/// </summary>
public sealed class UnavailableContainerResourceSampler : IContainerResourceSampler
{
    private readonly string _reason;

    public UnavailableContainerResourceSampler(string reason = "no container engine") => _reason = reason;

    public Task<IReadOnlyList<ContainerResourceSample>> SampleAsync(
        IEnumerable<(string AgentId, string ContainerId)> targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        IReadOnlyList<ContainerResourceSample> result = targets
            .Select(t => ContainerResourceSample.Unavailable(t.AgentId, _reason))
            .ToArray();
        return Task.FromResult(result);
    }
}

/// <summary>
/// Samples per-jail CPU/RAM from the Docker engine — the data source the Resource Monitor was built over
/// but which never existed (<c>GetAgentUsage</c> hard-coded <c>CpuPercent: 0, RamGb: 0</c>, so the tab
/// rendered a convincing zero for every agent forever).
///
/// <para><b>One-shot, not streaming.</b> <c>GET /containers/{id}/stats?stream=false</c> returns a single
/// reading and closes. The streaming form would hold one long-lived connection per agent for the life of
/// the app to produce a number nobody is looking at most of the time; the one-shot form costs one request
/// per agent per tick and nothing in between. Note this is a request to the daemon we already talk to —
/// NOT a process per agent per tick (the <c>wsl.exe</c>-per-second bootstrap bug this repo already had to
/// fix).</para>
///
/// <para><b>Never <c>one-shot=true</c>.</b> That query parameter looks like exactly what this wants and is
/// a trap: it makes the engine skip the priming read, so <c>precpu_stats</c> comes back all zeros and the
/// CPU delta is uncomputable. Measured on both engines below — with <c>one-shot=true</c>,
/// <c>precpu_stats.cpu_usage.total_usage = 0</c> and <c>system_cpu_usage</c> absent. A naive percentage
/// off that reads 0%, which is a fabricated number, not a missing one. <c>stream=false</c> alone makes the
/// engine take two readings ~1s apart and hand back both, which is what a real delta needs.</para>
///
/// <para><b>Verified on BOTH engines</b> (2026-08-06), because a check that only ever runs where the
/// problem is absent is how this repo has shipped invisible bugs before: Docker <b>20.10.24</b> (the
/// end-of-life engine in today's <c>MainguardEnv</c>, max API v1.41) and Docker <b>29.4.3</b> (CI and
/// Docker Desktop). Docker.DotNet 3.125.15's <c>GetContainerStatsAsync(id, {Stream=false}, IProgress, ct)</c>
/// delivers exactly one message and completes on both — 1.7s and 2.3s respectively — so the
/// <c>AttachStdin</c> hang that forced <see cref="DockerSocketExecStdinTransport"/> onto a raw socket does
/// NOT affect this endpoint, and the ordinary client is used. A missing container throws
/// <c>DockerContainerNotFoundException</c> in tens of milliseconds on both.</para>
///
/// <para><b>Both cgroup generations.</b> Memory accounting differs between cgroup v1 (<c>cache</c> /
/// <c>total_inactive_file</c>) and v2 (<c>inactive_file</c>), and the generation follows the HOST KERNEL,
/// not the engine version — so pinning the engine does not pin this. Both are handled, matching what the
/// <c>docker stats</c> CLI itself subtracts; the raw usage is the fallback rather than a failure.</para>
/// </summary>
public sealed class DockerResourceSampler : IContainerResourceSampler
{
    /// <summary>
    /// How long one tick may spend before its readings are abandoned. Finite on purpose: a wedged
    /// endpoint must cost one skipped tick, never a stuck sampler.
    ///
    /// <para>Matched to <see cref="DockerSandboxEngine.ControlPlaneTimeout"/> rather than tuned
    /// independently. The engine's own floor here is ~1s (it must collect two readings to produce a
    /// delta), so the interesting question is how much QUEUEING behind other work to tolerate before
    /// calling a sample lost — and a daemon serving a whole Docker test suite, or a laptop mid-build,
    /// can hold a control-plane call well past a few seconds. Being impatient here does not fail safe:
    /// it converts "busy" into "unknown", which is a worse answer than waiting.</para>
    /// </summary>
    public static readonly TimeSpan DefaultSampleTimeout = DockerSandboxEngine.ControlPlaneTimeout;

    /// <summary>
    /// Ceiling on jails sampled concurrently. The calls are sampled in parallel because each one blocks
    /// ~1s inside the engine — sequentially, N agents would take N seconds and the tick would fall behind
    /// its own interval. Bounded rather than unbounded so a large swarm cannot open an unbounded number of
    /// simultaneous connections to the daemon socket.
    /// </summary>
    public const int MaxConcurrentSamples = 8;

    private readonly IDockerClient _docker;
    private readonly TimeSpan _timeout;

    public DockerResourceSampler(IDockerClient docker, TimeSpan? sampleTimeout = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _timeout = sampleTimeout ?? DefaultSampleTimeout;
    }

    /// <summary>
    /// Samples the given jails, in parallel and bounded. One agent's failure yields an
    /// <see cref="ContainerResourceSample.Unavailable"/> row <b>for that agent only</b> — a jail that died
    /// between the caller's listing and the stats call must not blank out the whole tab.
    /// </summary>
    /// <param name="targets">Agent id paired with its container id. The caller supplies these from the
    /// daemon's own session registry rather than having this class re-derive them, so an agent that has no
    /// sandbox yet is still the caller's to report as unknown instead of silently vanishing from the list.</param>
    public async Task<IReadOnlyList<ContainerResourceSample>> SampleAsync(
        IEnumerable<(string AgentId, string ContainerId)> targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var list = targets
            .Where(t => !string.IsNullOrEmpty(t.AgentId) && !string.IsNullOrEmpty(t.ContainerId))
            .ToList();
        if (list.Count == 0) return Array.Empty<ContainerResourceSample>();

        using var gate = new SemaphoreSlim(MaxConcurrentSamples);
        var tasks = list.Select(async target =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await SampleOneAsync(target.AgentId, target.ContainerId, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ContainerResourceSample> SampleOneAsync(string agentId, string containerId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        ContainerStatsResponse? reading = null;
        try
        {
            await _docker.Containers.GetContainerStatsAsync(
                containerId,
                // Stream = false, and deliberately NOT OneShot — see the class remarks.
                new ContainerStatsParameters { Stream = false },
                // NOT Progress<T>: that type is specified to raise its callback ASYNCHRONOUSLY, posting
                // to the captured SynchronizationContext or, when there is none, to the thread pool. The
                // await below can therefore complete while the callback is still queued, leaving the
                // reading null — reported as "no reading returned" for a container that answered
                // perfectly. It is a race, so it wins on an idle laptop and loses on a loaded CI runner:
                // exactly the shape of bug that reads as "works on my machine".
                new SynchronousProgress<ContainerStatsResponse>(r => reading ??= r),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller is shutting down; not a sampling failure
        }
        catch (OperationCanceledException)
        {
            return ContainerResourceSample.Unavailable(agentId, "timed out");
        }
        catch (DockerContainerNotFoundException)
        {
            // Raced a teardown between the listing and this call — normal, not an error.
            return ContainerResourceSample.Unavailable(agentId, "container gone");
        }
        catch (Exception ex)
        {
            return ContainerResourceSample.Unavailable(agentId, ex.GetType().Name);
        }

        if (reading is null)
            return ContainerResourceSample.Unavailable(agentId, "no reading returned");

        var cpu = TryComputeCpuPercent(reading);
        var ram = TryComputeMemoryBytes(reading);
        var reason = (cpu, ram) switch
        {
            (null, null) => "no usable cpu or memory counters",
            (null, _) => "no usable cpu delta",
            (_, null) => "no usable memory counters",
            _ => null,
        };
        return new ContainerResourceSample(agentId, cpu, ram, reason);
    }

    /// <summary>
    /// The <c>docker stats</c> CPU percentage: the container's CPU-time delta as a share of the system
    /// CPU-time delta, scaled by the number of online CPUs so 100 means "one full core".
    ///
    /// <para>Returns null — never 0 — whenever the inputs cannot support a percentage: a zero system delta
    /// (two readings inside one clock tick, or the priming read skipped), a negative delta (counter reset
    /// across a restart), or an unknown CPU count. Each of those is genuinely unmeasured, and the whole
    /// point of this method is that it refuses to invent a number for them.</para>
    /// </summary>
    public static double? TryComputeCpuPercent(ContainerStatsResponse s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.CPUStats?.CPUUsage is null || s.PreCPUStats?.CPUUsage is null) return null;

        var cpuDelta = (double)s.CPUStats.CPUUsage.TotalUsage - s.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = (double)s.CPUStats.SystemUsage - s.PreCPUStats.SystemUsage;

        // A skipped priming read (one-shot=true) lands here: system delta 0 -> unknown, not 0%.
        if (systemDelta <= 0 || cpuDelta < 0) return null;

        // online_cpus is absent on some engines; percpu_usage length is the documented fallback.
        var cpus = s.CPUStats.OnlineCPUs > 0
            ? (int)s.CPUStats.OnlineCPUs
            : s.CPUStats.CPUUsage.PercpuUsage?.Count ?? 0;
        if (cpus <= 0) return null;

        return cpuDelta / systemDelta * cpus * 100.0;
    }

    /// <summary>
    /// The <c>docker stats</c> memory figure: total usage minus the reclaimable page cache, which is what
    /// makes the number track the workload instead of drifting up with every file the agent reads.
    ///
    /// <para>cgroup v2 reports <c>inactive_file</c>, cgroup v1 <c>total_inactive_file</c> (older engines
    /// only <c>cache</c>); the generation is a property of the host kernel, so both are handled. The
    /// subtraction is skipped if it would go negative — a bogus counter should degrade to the raw usage,
    /// not to a negative megabyte count. Null only when the engine reported no usage at all.</para>
    /// </summary>
    public static double? TryComputeMemoryBytes(ContainerStatsResponse s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.MemoryStats is null || s.MemoryStats.Usage == 0) return null;

        double usage = s.MemoryStats.Usage;
        var stats = s.MemoryStats.Stats;
        if (stats is not null)
        {
            foreach (var key in new[] { "inactive_file", "total_inactive_file", "cache" })
            {
                if (stats.TryGetValue(key, out var cache) && cache > 0 && cache < s.MemoryStats.Usage)
                {
                    usage -= cache;
                    break;
                }
            }
        }

        return usage;
    }
}
