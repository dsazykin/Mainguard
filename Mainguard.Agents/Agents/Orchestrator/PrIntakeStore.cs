using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Models;
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The daemon-owned external-PR-intake configuration: the knobs that are not per-source. Whether intake
/// polls at all, how often it polls, and the shared bot-author allow-list an unfiltered source falls back
/// to.
///
/// <para><b>Daemon-owned on purpose.</b> The daemon is what acts on these — it runs the poll loop, it
/// provisions the jail an intake'd PR needs, it is the process that must still know the cadence after
/// the App has been closed and reopened. A copy of this in App-local settings would be a settings screen
/// that lies: the dialog would look saved and the poller would go on using its compiled-in defaults.
/// So there is exactly one home for it, <see cref="IPrIntakeStore"/>, and the App reaches it over gRPC.</para>
/// </summary>
/// <param name="Enabled">False parks the poll loop: it keeps running but materializes nothing, so intake
/// can be switched off without unsubscribing every source.</param>
/// <param name="PollIntervalSeconds">The scheduler cadence. Clamped by <see cref="Normalized"/> — a zero
/// here is a hot loop against a rate-limited host API, and a persisted row is not a trusted input.</param>
/// <param name="BotAuthors">The allow-list a source with no <see cref="ExternalPrSource.AuthorFilter"/>
/// of its own matches against. Empty falls back to <see cref="ExternalPrIntake.DefaultBotAuthors"/>
/// rather than matching nothing: an empty list is far more likely to be a mis-save than a deliberate
/// "subscribe to this repo and ignore every pull request on it".</param>
public sealed record PrIntakeSettings(bool Enabled, int PollIntervalSeconds, IReadOnlyList<string> BotAuthors)
{
    /// <summary>The floor on <see cref="PollIntervalSeconds"/> (matches the settings page's minimum).</summary>
    public const int MinPollIntervalSeconds = 10;

    /// <summary>The ceiling on <see cref="PollIntervalSeconds"/> — one hour.</summary>
    public const int MaxPollIntervalSeconds = 3600;

    /// <summary>What a daemon that has never been configured runs with. <see cref="Enabled"/> is true
    /// because that is the pre-settings behaviour: before this record existed, an intake with a
    /// subscription polled. Making the default false would have switched a working feature off in the
    /// same change that made it configurable.</summary>
    public static PrIntakeSettings Default =>
        new(true, 60, ExternalPrIntake.DefaultBotAuthors);

    /// <summary>This record with out-of-range/empty values pulled back to something the poll loop can
    /// safely run on. Applied on the way IN and on the way OUT, so neither a hand-edited row nor an old
    /// client can hand the scheduler a tight loop.</summary>
    public PrIntakeSettings Normalized() => new(
        Enabled,
        Math.Clamp(PollIntervalSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds),
        BotAuthors is { Count: > 0 }
            ? BotAuthors.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() is { Count: > 0 } cleaned
                ? cleaned
                : ExternalPrIntake.DefaultBotAuthors
            : ExternalPrIntake.DefaultBotAuthors);

    /// <summary><see cref="PollIntervalSeconds"/> as the <see cref="TimeSpan"/> the scheduler delays for.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}

/// <summary>
/// The P2-12 intake persistence seam (daemon SQLite; in-memory in tests). Holds three durable facts:
/// the set of <see cref="ExternalPrSource"/> subscriptions, the last-seen head SHA per intake'd PR, and
/// the daemon's <see cref="PrIntakeSettings"/>. Follows the same daemon-store shape as the P2-10
/// queue/verification stores (in-memory + Db behind one interface, a <c>Func&lt;AppDbContext&gt;</c>
/// factory, a private lock).
/// </summary>
public interface IPrIntakeStore
{
    /// <summary>The daemon's intake configuration, or <see cref="PrIntakeSettings.Default"/> when it has
    /// never been written. Always normalized.</summary>
    PrIntakeSettings GetSettings();

    /// <summary>Persists the daemon's intake configuration (normalized first). The ONE write path — the
    /// settings RPC lands here and the poll loop reads the same row back, so the surface a human edits
    /// and the loop that obeys it cannot drift.</summary>
    void SaveSettings(PrIntakeSettings settings);

    /// <summary>Persists a subscription. Returns false (and stores nothing new) when the exact
    /// <c>(host, owner, repo, filter)</c> is already subscribed — the idempotent path (edge row 3).</summary>
    bool AddSubscription(ExternalPrSource source);

    /// <summary>Every persisted subscription.</summary>
    IReadOnlyList<ExternalPrSource> Subscriptions();

    /// <summary>The last head SHA materialized for a PR, or null if it has never been materialized.</summary>
    string? GetSeenHead(string sourceKey, int prNumber);

    /// <summary>Records (upserts) the head SHA last materialized for a PR.</summary>
    void SetSeenHead(string sourceKey, int prNumber, string headSha);

    /// <summary>The PR numbers currently tracked for a source (drives closed-PR detection).</summary>
    IReadOnlyList<int> TrackedPrNumbers(string sourceKey);

    /// <summary>Stops tracking a PR (its entry closed/merged upstream and was cleaned up).</summary>
    void Untrack(string sourceKey, int prNumber);
}

/// <summary>An in-memory <see cref="IPrIntakeStore"/> for tests and the pre-persistence path.</summary>
public sealed class InMemoryPrIntakeStore : IPrIntakeStore
{
    private readonly object _gate = new();
    private readonly List<ExternalPrSource> _sources = new();
    private readonly Dictionary<(string SourceKey, int PrNumber), string> _heads = new();
    private PrIntakeSettings _settings = PrIntakeSettings.Default;

    public PrIntakeSettings GetSettings()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    public void SaveSettings(PrIntakeSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        lock (_gate)
        {
            _settings = settings.Normalized();
        }
    }

    public bool AddSubscription(ExternalPrSource source)
    {
        lock (_gate)
        {
            if (_sources.Any(s => SameSource(s, source)))
            {
                return false;
            }

            _sources.Add(source);
            return true;
        }
    }

    public IReadOnlyList<ExternalPrSource> Subscriptions()
    {
        lock (_gate)
        {
            return _sources.ToList();
        }
    }

    public string? GetSeenHead(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            return _heads.TryGetValue((sourceKey, prNumber), out var sha) ? sha : null;
        }
    }

    public void SetSeenHead(string sourceKey, int prNumber, string headSha)
    {
        lock (_gate)
        {
            _heads[(sourceKey, prNumber)] = headSha;
        }
    }

    public IReadOnlyList<int> TrackedPrNumbers(string sourceKey)
    {
        lock (_gate)
        {
            return _heads.Keys.Where(k => k.SourceKey == sourceKey).Select(k => k.PrNumber).ToList();
        }
    }

    public void Untrack(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            _heads.Remove((sourceKey, prNumber));
        }
    }

    private static bool SameSource(ExternalPrSource a, ExternalPrSource b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.AuthorFilter ?? "", b.AuthorFilter ?? "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>SQLite-backed <see cref="IPrIntakeStore"/> — durable subscriptions + seen heads (daemon DB).</summary>
public sealed class DbPrIntakeStore : IPrIntakeStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    /// <summary>The primary key of the one configuration row. Intake settings are daemon-wide, not
    /// per-repo, so this table holds exactly one row and an upsert targets it by a constant.</summary>
    private const long ConfigRowId = 1;

    public DbPrIntakeStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public PrIntakeSettings GetSettings()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.PrIntakeConfig.FirstOrDefault(c => c.Id == ConfigRowId);
            if (row is null)
            {
                return PrIntakeSettings.Default;
            }

            return new PrIntakeSettings(
                row.Enabled,
                row.PollIntervalSeconds,
                SplitAuthors(row.BotAuthors)).Normalized();
        }
    }

    public void SaveSettings(PrIntakeSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var normalized = settings.Normalized();

        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.PrIntakeConfig.FirstOrDefault(c => c.Id == ConfigRowId);
            if (row is null)
            {
                row = new PrIntakeConfigRow { Id = ConfigRowId };
                db.PrIntakeConfig.Add(row);
            }

            row.Enabled = normalized.Enabled;
            row.PollIntervalSeconds = normalized.PollIntervalSeconds;
            row.BotAuthors = string.Join(",", normalized.BotAuthors);
            db.SaveChanges();
        }
    }

    /// <summary>The stored comma-separated author list back into a list. Empty yields an empty list,
    /// which <see cref="PrIntakeSettings.Normalized"/> then turns into the default bot list.</summary>
    private static IReadOnlyList<string> SplitAuthors(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? Array.Empty<string>()
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool AddSubscription(ExternalPrSource source)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var filter = source.AuthorFilter ?? "";
            var exists = db.PrIntakeSubscriptions.Any(s =>
                s.Host == source.Host && s.Owner == source.Owner && s.Repo == source.Repo
                && (s.AuthorFilter ?? "") == filter);
            if (exists)
            {
                return false;
            }

            db.PrIntakeSubscriptions.Add(new PrIntakeSubscriptionRow
            {
                Host = source.Host,
                Owner = source.Owner,
                Repo = source.Repo,
                AuthorFilter = source.AuthorFilter,
            });
            db.SaveChanges();
            return true;
        }
    }

    public IReadOnlyList<ExternalPrSource> Subscriptions()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeSubscriptions
                .OrderBy(s => s.Id)
                .ToList()
                .Select(s => new ExternalPrSource(s.Host, s.Owner, s.Repo, s.AuthorFilter))
                .ToList();
        }
    }

    public string? GetSeenHead(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeHeads
                .Where(h => h.SourceKey == sourceKey && h.PrNumber == prNumber)
                .Select(h => h.SeenHeadSha)
                .FirstOrDefault();
        }
    }

    public void SetSeenHead(string sourceKey, int prNumber, string headSha)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.PrIntakeHeads.FirstOrDefault(h => h.SourceKey == sourceKey && h.PrNumber == prNumber);
            if (existing is null)
            {
                db.PrIntakeHeads.Add(new PrIntakeHeadRow
                {
                    SourceKey = sourceKey,
                    PrNumber = prNumber,
                    SeenHeadSha = headSha,
                });
            }
            else
            {
                existing.SeenHeadSha = headSha;
            }

            db.SaveChanges();
        }
    }

    public IReadOnlyList<int> TrackedPrNumbers(string sourceKey)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeHeads.Where(h => h.SourceKey == sourceKey).Select(h => h.PrNumber).ToList();
        }
    }

    public void Untrack(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.PrIntakeHeads.FirstOrDefault(h => h.SourceKey == sourceKey && h.PrNumber == prNumber);
            if (existing is null)
            {
                return;
            }

            db.PrIntakeHeads.Remove(existing);
            db.SaveChanges();
        }
    }
}
