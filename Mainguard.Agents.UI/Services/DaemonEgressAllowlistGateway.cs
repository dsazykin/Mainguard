using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IEgressAllowlistGateway"/>: the LIVE, daemon-owned allowlist over
/// <c>EgressService</c>'s <c>ListAllowlist</c> / <c>AddAllowlistHost</c> / <c>RemoveAllowlistHost</c>.
///
/// <para><b>Why this had to exist.</b> The sandbox egress policy was enforced but not inspectable: the
/// only implementation of this seam was <see cref="InMemoryEgressAllowlistGateway"/>, a hardcoded seed
/// used by the render harness. The editor view was therefore unreachable in the app, and an agent whose
/// CLI was killed by the default-deny proxy had exactly one affordance — the block prompt's
/// "Unblock and retry", which can only ever add the ONE host the detector managed to parse out of a
/// death message. There was no way to see what was already allowed, add a host ahead of time, or undo
/// an unblock. The whole transport was already present on <see cref="DaemonClient"/>; nothing wrapped
/// it.</para>
///
/// <para>Holds no state of its own — every read is a fresh <c>ListAllowlist</c>, so the view reflects
/// the daemon's authoritative list including entries some other surface (or the unblock prompt) added.
/// Errors propagate: the ViewModel renders the daemon's reason, which is the point of a default-deny
/// control the user is allowed to widen.</para>
/// </summary>
public sealed class DaemonEgressAllowlistGateway : IEgressAllowlistGateway
{
    private readonly DaemonClient _client;

    public DaemonEgressAllowlistGateway(DaemonClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<IReadOnlyList<EgressAllowlistItem>> ListAsync(CancellationToken ct = default)
    {
        var entries = await _client.ListAllowlistAsync(ct).ConfigureAwait(false);
        return entries
            .Select(e => new EgressAllowlistItem(e.Name, e.HostPattern, e.Kind, e.DefeatsA6))
            .ToArray();
    }

    public async Task AddAsync(string name, string hostPattern, string kind, CancellationToken ct = default)
        => await _client.AddAllowlistHostAsync(name, hostPattern, kind, ct).ConfigureAwait(false);

    public async Task RemoveAsync(string hostPattern, CancellationToken ct = default)
        => await _client.RemoveAllowlistHostAsync(hostPattern, ct).ConfigureAwait(false);
}
