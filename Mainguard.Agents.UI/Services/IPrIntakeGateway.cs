using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>One subscribed intake source as the App sees it: the upstream repository, and the author
/// filter in effect for it (empty = the shared bot list).</summary>
public sealed record PrIntakeSourceItem(string Host, string Owner, string Repo, string AuthorFilter)
{
    /// <summary>The stable key the daemon groups PRs under, rendered as the row's title.</summary>
    public string Key => $"{Host}/{Owner}/{Repo}";
}

/// <summary>The intake configuration + the persisted subscription list, as one load result.</summary>
/// <param name="Enabled">Whether the daemon's poll loop materializes anything.</param>
/// <param name="PollIntervalSeconds">The cadence, already clamped by the daemon.</param>
/// <param name="BotAuthors">The shared bot-author allow-list.</param>
/// <param name="Sources">Every persisted subscription — the daemon's list, not a client-side echo.</param>
public sealed record PrIntakeConfiguration(
    bool Enabled,
    int PollIntervalSeconds,
    IReadOnlyList<string> BotAuthors,
    IReadOnlyList<PrIntakeSourceItem> Sources);

/// <summary>
/// The App's seam to the <b>daemon-owned</b> external-PR-intake configuration (P2-12). The App reaches
/// the agent platform only through the daemon (ESC-I2/G-18), and this is the intake's slice of that:
/// implemented over <see cref="DaemonClient"/> in production, so the settings page never references the
/// daemon's store types or its database.
///
/// <para><b>Why the seam is this shape.</b> The settings page previously took an
/// <c>IPrIntakeStore</c> — the daemon's own store interface — and defaulted to an in-process
/// implementation. That is a store the App can construct and the daemon can never see, so the page could
/// only ever have been a settings screen that lies. The daemon is the process that polls, that provisions
/// jails, and that must still hold the configuration when the App is closed; so the App gets a client
/// seam, not a store.</para>
///
/// <para>Async because every implementation that matters is a gRPC round trip. Holds no state: each
/// <see cref="LoadAsync"/> is a fresh read, so the page reflects the daemon's authoritative
/// configuration including anything another surface changed. Errors propagate — the page renders the
/// daemon's own refusal rather than a success it did not get.</para>
/// </summary>
public interface IPrIntakeGateway
{
    /// <summary>Reads the daemon's intake configuration and its persisted subscriptions.</summary>
    Task<PrIntakeConfiguration> LoadAsync(CancellationToken ct = default);

    /// <summary>Writes the intake configuration. Returns it AS PERSISTED — the daemon clamps the cadence
    /// and substitutes its default bot list for an empty one, and the caller renders that, not what the
    /// user typed.</summary>
    Task<PrIntakeConfiguration> SaveAsync(
        bool enabled, int pollIntervalSeconds, IReadOnlyList<string> botAuthors, CancellationToken ct = default);

    /// <summary>Subscribes one source. <c>Added</c> is false when it was already subscribed — idempotent,
    /// not an error. The returned configuration carries the full persisted list after the add.</summary>
    Task<(bool Added, PrIntakeConfiguration Configuration)> SubscribeAsync(
        string host, string owner, string repo, string? authorFilter, CancellationToken ct = default);
}

/// <summary>
/// A standalone in-memory gateway for the render harness / design preview, seeded with the same defaults
/// the daemon ships. It is deliberately NOT the production default anywhere: the whole defect this seam
/// exists to fix was a settings surface silently defaulting to storage the daemon never reads, so a
/// caller has to name this one on purpose.
/// </summary>
public sealed class InMemoryPrIntakeGateway : IPrIntakeGateway
{
    private readonly object _gate = new();
    private readonly List<PrIntakeSourceItem> _sources;
    private bool _enabled;
    private int _pollIntervalSeconds;
    private IReadOnlyList<string> _botAuthors;

    public InMemoryPrIntakeGateway(
        bool enabled = true,
        int pollIntervalSeconds = 60,
        IEnumerable<string>? botAuthors = null,
        IEnumerable<PrIntakeSourceItem>? sources = null)
    {
        _enabled = enabled;
        _pollIntervalSeconds = pollIntervalSeconds;
        // The daemon's own default list, called rather than re-typed — a hand-copied mirror is exactly
        // how the two halves of this feature drifted apart the first time.
        _botAuthors = (botAuthors ?? Mainguard.Agents.Agents.Orchestrator.ExternalPrIntake.DefaultBotAuthors).ToList();
        _sources = (sources ?? Array.Empty<PrIntakeSourceItem>()).ToList();
    }

    public Task<PrIntakeConfiguration> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Snapshot());

    public Task<PrIntakeConfiguration> SaveAsync(
        bool enabled, int pollIntervalSeconds, IReadOnlyList<string> botAuthors, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // The daemon's clamp, applied here too, so the harness cannot show a cadence production
            // would refuse.
            var normalized = new Mainguard.Agents.Agents.Orchestrator.PrIntakeSettings(
                enabled, pollIntervalSeconds, botAuthors).Normalized();
            _enabled = normalized.Enabled;
            _pollIntervalSeconds = normalized.PollIntervalSeconds;
            _botAuthors = normalized.BotAuthors;
        }

        return Task.FromResult(Snapshot());
    }

    public Task<(bool Added, PrIntakeConfiguration Configuration)> SubscribeAsync(
        string host, string owner, string repo, string? authorFilter, CancellationToken ct = default)
    {
        var item = new PrIntakeSourceItem(host, owner, repo, authorFilter ?? string.Empty);
        bool added;
        lock (_gate)
        {
            added = !_sources.Any(s => Same(s, item));
            if (added)
            {
                _sources.Add(item);
            }
        }

        return Task.FromResult((added, Snapshot()));
    }

    private PrIntakeConfiguration Snapshot()
    {
        lock (_gate)
        {
            return new PrIntakeConfiguration(
                _enabled, _pollIntervalSeconds, _botAuthors.ToList(), _sources.ToList());
        }
    }

    private static bool Same(PrIntakeSourceItem a, PrIntakeSourceItem b)
        => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.AuthorFilter, b.AuthorFilter, StringComparison.OrdinalIgnoreCase);
}
