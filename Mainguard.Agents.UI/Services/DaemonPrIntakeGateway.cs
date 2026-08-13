using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IPrIntakeGateway"/>: the LIVE, daemon-owned external-PR-intake configuration
/// over <c>PrIntakeService</c>'s <c>GetPrIntakeSettings</c> / <c>UpdatePrIntakeSettings</c> /
/// <c>SubscribePrIntakeSource</c>.
///
/// <para><b>Why this had to exist.</b> The intake settings dialog shipped complete and unreachable, and
/// the reason nobody wired it is that both available wirings were wrong: there was no intake RPC, and
/// the daemon's store lives in a different database from the App's. Pointing the dialog at an App-side
/// store would have made it save successfully and change nothing, because the process that polls, that
/// fetches PR heads, and that provisions a jail per intake'd pull request is the daemon. This class is
/// the missing third option — the dialog writes to the daemon that acts on it.</para>
///
/// <para>Holds no state: every load is a fresh <c>GetPrIntakeSettings</c>, and every save returns the
/// daemon's PERSISTED values (clamped cadence, defaulted bot list) rather than an echo of the request.</para>
/// </summary>
public sealed class DaemonPrIntakeGateway : IPrIntakeGateway
{
    private readonly DaemonClient _client;

    public DaemonPrIntakeGateway(DaemonClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<PrIntakeConfiguration> LoadAsync(CancellationToken ct = default)
    {
        var response = await _client.GetPrIntakeSettingsAsync(ct).ConfigureAwait(false);
        return ToConfiguration(response.Settings, response.Sources);
    }

    public async Task<PrIntakeConfiguration> SaveAsync(
        bool enabled, int pollIntervalSeconds, IReadOnlyList<string> botAuthors, CancellationToken ct = default)
    {
        var request = new Mainguard.Protos.V1.PrIntakeSettings
        {
            Enabled = enabled,
            PollIntervalSeconds = pollIntervalSeconds,
        };
        request.BotAuthors.AddRange(botAuthors ?? Array.Empty<string>());

        var persisted = await _client.UpdatePrIntakeSettingsAsync(request, ct).ConfigureAwait(false);

        // The subscription list is not part of the settings write, so it is re-read rather than guessed:
        // returning a stale list here is how a page starts rendering a repository that is no longer
        // subscribed (or misses one another surface added).
        var reloaded = await _client.GetPrIntakeSettingsAsync(ct).ConfigureAwait(false);
        return ToConfiguration(persisted, reloaded.Sources);
    }

    public async Task<(bool Added, PrIntakeConfiguration Configuration)> SubscribeAsync(
        string host, string owner, string repo, string? authorFilter, CancellationToken ct = default)
    {
        var response = await _client.SubscribePrIntakeSourceAsync(
            new PrIntakeSource
            {
                Host = host ?? string.Empty,
                Owner = owner ?? string.Empty,
                Repo = repo ?? string.Empty,
                AuthorFilter = authorFilter ?? string.Empty,
            },
            ct).ConfigureAwait(false);

        // Subscribe does not change the settings, so they come from a read rather than from the reply —
        // the page's cadence/bot-list fields must not be repopulated from a message that never carried them.
        var settings = await _client.GetPrIntakeSettingsAsync(ct).ConfigureAwait(false);
        return (response.Added, ToConfiguration(settings.Settings, response.Sources));
    }

    private static PrIntakeConfiguration ToConfiguration(
        Mainguard.Protos.V1.PrIntakeSettings? settings, IEnumerable<PrIntakeSource> sources)
    {
        var wire = settings ?? new Mainguard.Protos.V1.PrIntakeSettings();
        return new PrIntakeConfiguration(
            wire.Enabled,
            wire.PollIntervalSeconds,
            wire.BotAuthors.ToList(),
            sources.Select(s => new PrIntakeSourceItem(s.Host, s.Owner, s.Repo, s.AuthorFilter)).ToList());
    }
}
