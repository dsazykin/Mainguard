namespace Mainguard.Git.Models;

/// <summary>
/// One persisted external-PR-intake subscription (P2-12): a <c>(host, owner, repo, author-filter)</c>
/// tuple the daemon polls for bot-authored pull requests. Uniqueness is on all four fields so a
/// duplicate subscribe is idempotent (edge row 3). One row per subscription.
/// </summary>
public class PrIntakeSubscriptionRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The host (e.g. <c>github.com</c>).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The repository owner / org.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The repository name.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>An optional per-source author filter overriding the daemon's default bot list (null = use the default list).</summary>
    public string? AuthorFilter { get; set; }
}

/// <summary>
/// The last-seen head SHA for an intake'd PR (P2-12). Keyed by <c>SourceKey</c> (<c>host/owner/repo</c>)
/// and PR number, it is the "seen PR head SHAs" store the poll compares against: a new number or a moved
/// SHA drives (re-)materialization; the set of rows for a source is also the set of tracked PRs (a row
/// that no longer appears open upstream is a closed PR to clean up). One row per (source, PR number).
/// </summary>
public class PrIntakeHeadRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The source key (<c>host/owner/repo</c>) this PR belongs to.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The upstream PR number.</summary>
    public int PrNumber { get; set; }

    /// <summary>The last head SHA materialized as <c>agent/pr-&lt;n&gt;</c>.</summary>
    public string SeenHeadSha { get; set; } = string.Empty;
}

/// <summary>
/// The daemon's external-PR-intake configuration — the knobs that are not per-source: whether intake
/// polls at all, how often, and the shared bot-author allow-list. <b>Exactly one row</b> (Id 1): this is
/// daemon-wide state, not per-repo, and the store upserts it by that constant.
///
/// <para>It lives in the DAEMON database next to the subscriptions and seen heads because the daemon is
/// the process that acts on it — it runs the poll loop and provisions the jail each intake'd PR needs.
/// Keeping a second copy in App-local settings would give the settings page somewhere to write that the
/// poller never reads, which is a settings screen that lies.</para>
/// </summary>
public class PrIntakeConfigRow
{
    /// <summary>Primary key. Always <c>1</c> — a singleton row.</summary>
    public long Id { get; set; }

    /// <summary>False parks the poll loop (it keeps running and materializes nothing), so intake can be
    /// switched off without unsubscribing every source.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The scheduler cadence in seconds. Clamped on read AND write by
    /// <c>PrIntakeSettings.Normalized</c> — a persisted row is not a trusted input, and a zero here would
    /// be a hot loop against a rate-limited host API.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>The shared bot-author allow-list, comma-separated (one column rather than a child table:
    /// it is a short, order-insignificant list of literals that is always read and written whole).
    /// Empty means "use the intake's compiled-in default bot list".</summary>
    public string BotAuthors { get; set; } = string.Empty;
}
