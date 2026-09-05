using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>The jail ceiling as the page renders it: GiB and CPUs, plus the band the daemon clamps to.</summary>
public sealed record JailLimitsView(
    double MemoryGiB, double Cpus, long Pids, bool IsDefault,
    double MinMemoryGiB, double MaxMemoryGiB, double MinCpus, double MaxCpus);

/// <summary>
/// The Settings → Agent Jails page's seam onto the DAEMON's per-jail ceiling (owner decision 2026-09-04).
/// Daemon state, like the intake settings: the daemon is what spawns, so it owns the number and the page
/// is its client. A save answers with the ceiling AS PERSISTED (clamped), and the page renders that.
/// </summary>
public interface IJailLimitsGateway
{
    Task<JailLimitsView> LoadAsync(CancellationToken ct = default);

    Task<JailLimitsView> SaveAsync(double memoryGiB, double cpus, CancellationToken ct = default);
}
