using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>The shipped <see cref="IJailLimitsGateway"/>: <c>AgentService.GetJailLimits</c> / <c>SetJailLimits</c>
/// over the daemon client. Holds no state; every load is a fresh read.</summary>
public sealed class DaemonJailLimitsGateway : IJailLimitsGateway
{
    private const double GiB = 1024d * 1024 * 1024;
    private readonly DaemonClient _client;

    public DaemonJailLimitsGateway(DaemonClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<JailLimitsView> LoadAsync(CancellationToken ct = default)
        => ToView(await _client.GetJailLimitsAsync(ct).ConfigureAwait(false));

    public async Task<JailLimitsView> SaveAsync(double memoryGiB, double cpus, CancellationToken ct = default)
        => ToView(await _client.SetJailLimitsAsync(
            (long)Math.Round(memoryGiB * GiB, MidpointRounding.AwayFromZero), cpus, ct).ConfigureAwait(false));

    private static JailLimitsView ToView(JailLimits wire) => new(
        wire.MemoryBytes / GiB, wire.Cpus, wire.Pids, wire.IsDefault,
        wire.MinMemoryBytes / GiB, wire.MaxMemoryBytes / GiB, wire.MinCpus, wire.MaxCpus);
}
