using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// A subscribed source of bot-authored pull requests (P2-12): a repo on a host, optionally narrowed to a
/// specific author. When <see cref="AuthorFilter"/> is null the intake's configurable default bot list
/// applies. Vendor-neutral: Codex/Jules/Copilot only ever surface PRs, and this subscribes those PRs into
/// the same verify → review → merge pipeline.
/// </summary>
public sealed record ExternalPrSource(string Host, string Owner, string Repo, string? AuthorFilter)
{
    /// <summary>The stable key (<c>host/owner/repo</c>) the seen-head store groups PRs under.</summary>
    public string Key => $"{Host}/{Owner}/{Repo}";
}

/// <summary>
/// External agent PR intake (P2-12, daemon). Polls subscribed sources for open bot-authored PRs through
/// the ONE audited T-23 transport (<see cref="IPullRequestService"/>), materializes each new/updated PR
/// head as an <c>agent/pr-&lt;n&gt;</c> merge-queue entry (<b>jail</b> → fetch → <c>Working</c>), and lets
/// the P2-10 queue verify it exactly as a local agent. Merge is routed back through the host PR merge API
/// by <see cref="MergeDispatch"/>, never a local foreground merge.
///
/// <para><b>The jail comes first, and nothing is materialized without one.</b> This used to create a
/// worktree and an entry and spawn nothing, so an external entry could never leave <c>Working</c>:
/// verification runs in the worker's own jail and host execution is a rejection trigger. Materialization
/// now begins by asking <see cref="IPrWorkerHost"/> for a real <c>pr-&lt;n&gt;</c> worker; a gate refusal
/// or a provisioning failure materializes <b>nothing at all</b> — no worktree, no entry, no seen-head —
/// so the pull request is simply picked up again on a later poll. That ordering is what keeps an
/// unbounded external queue from becoming an unbounded number of jails on the user's own machine.</para>
///
/// <para>INVARIANTS: the intake writes NOTHING upstream without an explicit user action — it only ever
/// calls the read (list) surface of the transport (invariant 1); all host traffic stays inside the T-23
/// transport (invariant 2); external entries obey the same <c>CanMerge</c> gates as local branches
/// (invariant 3, inherited by entering the same queue).</para>
/// </summary>
public interface IExternalPrIntake
{
    /// <summary>Persists a source to poll. Duplicate <c>(host, owner, repo, filter)</c> subscribe is idempotent.</summary>
    void Subscribe(ExternalPrSource source);

    /// <summary>Poll: new/updated open PRs matching the filter → materialize each as a queue entry
    /// (fetch PR head into the VM bare repo as agent/pr-&lt;n&gt;, worktree, enter MergeQueue at Working);
    /// PRs closed upstream → cancel + prune. Rate limits back off through the host client's typed error.</summary>
    Task PollOnceAsync(CancellationToken ct);

    /// <summary>The daemon scheduler loop: poll on the configured interval until cancelled (P2-12).
    /// A poll never throws a rate limit (caught + backed off), so the loop never crashes.</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Materializes a PR head into an agent worktree (P2-12 step 2). The production implementation fetches
/// <c>pull/&lt;n&gt;/head</c> from the host into the agent worktree and hard-resets it (the daemon
/// provisioning-plane fetch — the quarantine rule cuts the <i>agent</i> worktree off from the real remote,
/// not this daemon-side provisioning fetch). Returns the resulting head SHA; a moved SHA drives a
/// re-materialize. This is a git-CLI seam — <b>no HTTP transport</b> (host API traffic stays in T-23).
/// </summary>
public interface IPrHeadFetcher
{
    /// <summary>Fetch/refresh the PR head into <c>agent/&lt;agentId&gt;</c> and return its current head SHA.</summary>
    Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct);
}

/// <summary>Resolves an <see cref="ExternalPrSource"/> to the daemon objects a poll needs.</summary>
/// <param name="RepoPath">The local repo path the T-23 <see cref="IPullRequestService"/> resolves host + token from.</param>
/// <param name="RepoHash">The P2-06 repo hash keying the bare mirror, worktrees, and queue.</param>
/// <param name="Queue">The repo's live <see cref="MergeQueue"/> the PR enters.</param>
public sealed record PrIntakeTarget(string RepoPath, string RepoHash, MergeQueue Queue);

/// <inheritdoc cref="IExternalPrIntake"/>
public sealed class ExternalPrIntake : IExternalPrIntake
{
    /// <summary>The default bot authors an unfiltered source polls for (configurable via <see cref="AuthorFilters"/>).</summary>
    public static readonly IReadOnlyList<string> DefaultBotAuthors =
        new[] { "codex[bot]", "google-jules[bot]", "copilot" };

    /// <summary>The agent kind an external-PR verification worker spawns under. Deliberately NOT a CLI
    /// kind: no installed adapter answers to it, so the jail starts with no agent CLI, no model API key
    /// and no launch command — it exists only to be the sandbox its own verification runs in.</summary>
    public const string WorkerAgentKind = "external-pr";

    private readonly IPullRequestService _prService;
    private readonly IPrIntakeStore _store;
    private readonly IPrWorkerHost _workers;
    private readonly IPrHeadFetcher _fetcher;
    private readonly Func<ExternalPrSource, PrIntakeTarget?> _resolveTarget;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, int Attempt)> _backoff =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The daemon's live intake configuration, read from the store on every use.
    ///
    /// <para><b>Read, never cached.</b> This used to be two settable properties (<c>AuthorFilters</c> and
    /// <c>PollInterval</c>) holding compiled-in defaults that nothing in production ever assigned — so
    /// the feature's cadence and bot list were unconfigurable, and a settings surface could only have
    /// changed them for the lifetime of one object. Reading the store here is what makes the settings a
    /// human saves and the loop that obeys them the SAME fact: there is no second copy to drift, and a
    /// change takes effect on the next poll rather than on the next daemon restart.</para>
    /// </summary>
    public PrIntakeSettings Settings => _store.GetSettings();

    /// <summary>The configurable bot-author allow-list for sources without their own <c>AuthorFilter</c>
    /// (the persisted <see cref="Settings"/> value).</summary>
    public IReadOnlyList<string> AuthorFilters => Settings.BotAuthors;

    /// <summary>The poll cadence for the daemon scheduler loop (<see cref="RunAsync"/>), from the
    /// persisted <see cref="Settings"/>. Clamped by the store, so it can never be a tight loop.</summary>
    public TimeSpan PollInterval => Settings.PollInterval;

    /// <summary>The first rate-limit backoff delay; each consecutive rate-limit doubles it up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The backoff ceiling — a persistent rate limit never spins tighter than this.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    public ExternalPrIntake(
        IPullRequestService prService,
        IPrIntakeStore store,
        IPrWorkerHost workers,
        IPrHeadFetcher fetcher,
        Func<ExternalPrSource, PrIntakeTarget?> resolveTarget,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null)
    {
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _resolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Subscribe(ExternalPrSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        // Idempotent: the store dedupes on (host, owner, repo, filter) — a repeat subscribe adds no row.
        _store.AddSubscription(source);
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        // ONE read per poll, then passed down: the settings are a database round trip and a poll can walk
        // many pull requests, but more importantly every source in a single cycle must be judged against
        // the same configuration — re-reading per PR would let a save land mid-poll and filter half the
        // list by the old bot list and half by the new one.
        var settings = _store.GetSettings();

        // The off switch. The loop keeps running (so re-enabling needs no daemon restart) but nothing is
        // listed, fetched or materialized — intake goes quiet without the user having to unsubscribe
        // every source and re-enter them later.
        if (!settings.Enabled)
        {
            return;
        }

        foreach (var source in _store.Subscriptions())
        {
            ct.ThrowIfCancellationRequested();
            await PollSourceAsync(source, settings, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The daemon scheduler loop: poll on <see cref="PollInterval"/> until cancelled. One poll at a
    /// time; a poll never throws a rate limit (it is caught and backed off), so the loop never crashes.
    ///
    /// <para>The cadence is re-read from the store on every iteration, not captured once at start: this
    /// loop is started by the daemon's hosted service and lives for the daemon's whole lifetime, so a
    /// cadence read once would mean a saved interval could not take effect until the user restarted the
    /// daemon — the settings page would be right and the poller would be wrong for hours.</para></summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollSourceAsync(ExternalPrSource source, PrIntakeSettings settings, CancellationToken ct)
    {
        // Honour any active rate-limit backoff for this source (edge row 4 — never a tight retry loop).
        lock (_gate)
        {
            if (_backoff.TryGetValue(source.Key, out var b) && _clock() < b.Until)
            {
                return;
            }
        }

        var target = _resolveTarget(source);
        if (target is null)
        {
            return; // the repo isn't mounted this poll — leave the subscription for a later cycle.
        }

        IReadOnlyList<PullRequestItem> prs;
        try
        {
            prs = await _prService.ListAsync(target.RepoPath, PullRequestState.Open, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRateLimit(ex))
        {
            RecordBackoff(source);
            return; // poller stays alive; the next allowed poll is delayed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else the transport can raise — an expired/absent host token, DNS, a 5xx — used to
            // escape PollOnceAsync, and RunAsync catches only cancellation, so ONE unauthenticated source
            // permanently killed the daemon's whole intake loop (silently: nothing else logs it). Now the
            // source is skipped for this cycle and audited, and every other subscription still polls.
            _audit.Append(new AuditEvent("external_pr_poll_failed", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["reason"] = ex.Message,
            }));
            return;
        }

        ClearBackoff(source);

        var openNumbers = prs.Select(p => p.Number).ToHashSet();

        // Materialize/refresh each matching open PR.
        foreach (var pr in prs.Where(p => MatchesAuthor(p, source, settings)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MaterializeAsync(source, target, pr, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per PULL REQUEST, not per source: the head fetch is a network operation against the
                // host, and one unreachable/deleted head must not stop the other open PRs on the same
                // source from materializing — let alone escape into RunAsync, which catches only
                // cancellation and would end the daemon's intake loop for good. Nothing is recorded as
                // seen, so this PR is simply retried next cycle.
                _audit.Append(new AuditEvent("external_pr_materialize_failed", new Dictionary<string, string>
                {
                    ["source"] = source.Key,
                    ["pr"] = pr.Number.ToString(),
                    ["agent"] = AgentIdFor(pr.Number),
                    ["reason"] = ex.Message,
                }));
            }
        }

        // Clean up any tracked PR that is no longer open upstream (closed/merged mid-queue — edge row 2).
        foreach (var tracked in _store.TrackedPrNumbers(source.Key))
        {
            if (openNumbers.Contains(tracked))
            {
                continue;
            }

            var agentId = AgentIdFor(tracked);
            target.Queue.Cancel(agentId);
            // Releases the whole worker, not just the worktree: the jail, its MG-36 network segment (the
            // bridge pool is ~32 deep — a segment leaked per intake'd PR exhausts it) and its package
            // cache all go with it. Best-effort by contract; a release never fails a poll.
            await _workers.ReleaseWorkerAsync(target.RepoHash, agentId, ct).ConfigureAwait(false);
            _store.Untrack(source.Key, tracked);
            _audit.Append(new AuditEvent("external_pr_closed", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["pr"] = tracked.ToString(),
                ["agent"] = agentId,
            }));
        }
    }

    private async Task MaterializeAsync(ExternalPrSource source, PrIntakeTarget target, PullRequestItem pr, CancellationToken ct)
    {
        var agentId = AgentIdFor(pr.Number);
        var seen = _store.GetSeenHead(source.Key, pr.Number);

        // A human dropped this entry from the queue. Materializing is re-asked on EVERY poll, so without
        // this the intake would go on re-provisioning the jail and its MG-36 network segment (the bridge
        // pool is ~32 deep) for a pull request the operator explicitly discarded — and would keep the
        // worker alive indefinitely, since the release path below only fires when the PR CLOSES upstream.
        // Releasing here instead makes the discard mean the same thing for an intake'd PR that it means
        // for a local agent: the entry is off the queue and nothing is being kept warm for it.
        //
        // Checked before EnsureWorkerAsync, because that call is the provisioning this must prevent.
        //
        // The release happens ONCE, gated on the PR still being tracked: this runs on every poll for as
        // long as the PR stays open upstream, and tearing the same worker down again on each one would
        // trade a leak for a stream of pointless teardowns. Untracking is what makes it once — it also
        // takes the PR out of the closed-upstream sweep, which would otherwise release it a second time
        // whenever it finally closes.
        if (target.Queue.GetState(agentId) == WorkerMergeState.Discarded)
        {
            if (seen is not null)
            {
                await _workers.ReleaseWorkerAsync(target.RepoHash, agentId, ct).ConfigureAwait(false);
                _store.Untrack(source.Key, pr.Number);
                _audit.Append(new AuditEvent("external_pr_discarded", new Dictionary<string, string>
                {
                    ["source"] = source.Key,
                    ["pr"] = pr.Number.ToString(),
                    ["agent"] = agentId,
                }));
            }

            return;
        }

        // THE JAIL FIRST. This is the whole point: an external entry with no sandbox can never be
        // verified, because verification runs in the worker's own jail and never on the host. The host
        // creates the worktree as part of the ordinary spawn chain, so there is no separate
        // CreateAgentWorktree here any more — a worktree without a jail is precisely the state that made
        // criterion 4's verify leg impossible to demonstrate.
        //
        // Re-asked on EVERY poll, not just the first: it is idempotent for a live worker, and it is what
        // gives a daemon that restarted (sessions are memory-only, jails are not) a path back to a
        // verifiable entry instead of a permanently stuck one.
        var worker = await _workers.EnsureWorkerAsync(target.RepoHash, agentId, pr.Number, ct).ConfigureAwait(false);
        if (!worker.HasJail)
        {
            // Gate refusal or provisioning failure: materialize NOTHING. No seen-head is written, so the
            // next poll retries from scratch — the PR waits for capacity rather than entering a queue it
            // could never leave.
            _audit.Append(new AuditEvent("external_pr_worker_unavailable", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["pr"] = pr.Number.ToString(),
                ["agent"] = agentId,
                ["outcome"] = worker.Outcome.ToString(),
                ["reason"] = worker.Reason ?? string.Empty,
            }));
            return;
        }

        if (seen is null)
        {
            // New PR: fetch the head into the jailed worker's own repository, then enter the queue at
            // Working as an External entry.
            var head = await _fetcher.FetchHeadAsync(source, target.RepoHash, agentId, pr.Number, ct).ConfigureAwait(false);
            target.Queue.EnsureEntry(agentId, MergeEntryOrigin.External);
            _store.SetSeenHead(source.Key, pr.Number, head);
            _audit.Append(new AuditEvent("external_pr_materialized", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["pr"] = pr.Number.ToString(),
                ["agent"] = agentId,
                ["head"] = head,
                ["worker"] = worker.Outcome.ToString(),
            }));
            return;
        }

        // Existing PR: refresh the worktree head and detect a moved head (a force-push is just a head move
        // whose old SHA disappears — edge row 1).
        var newHead = await _fetcher.FetchHeadAsync(source, target.RepoHash, agentId, pr.Number, ct).ConfigureAwait(false);
        if (string.Equals(newHead, seen, StringComparison.Ordinal))
        {
            return; // unchanged — idempotent; no re-queue, no duplicate worktree.
        }

        // Head moved: invalidate the stale verification and re-enter Working (identical to local agents).
        target.Queue.NotifyNewCommits(agentId);
        _store.SetSeenHead(source.Key, pr.Number, newHead);
        _audit.Append(new AuditEvent("external_pr_head_moved", new Dictionary<string, string>
        {
            ["source"] = source.Key,
            ["pr"] = pr.Number.ToString(),
            ["agent"] = agentId,
            ["head"] = newHead,
        }));
    }

    // ---- Author filter (configurable; per-source override wins over the default bot list) ----

    /// <summary>True iff the PR author matches this source's filter (its own, else the persisted shared
    /// bot list). Case-insensitive.</summary>
    public bool MatchesAuthor(PullRequestItem pr, ExternalPrSource source)
        => MatchesAuthor(pr, source, Settings);

    /// <summary>The same match against an already-read settings snapshot — so one poll judges every
    /// source and every pull request in it against one configuration.</summary>
    private static bool MatchesAuthor(PullRequestItem pr, ExternalPrSource source, PrIntakeSettings settings)
    {
        var filters = string.IsNullOrWhiteSpace(source.AuthorFilter)
            ? settings.BotAuthors
            : new[] { source.AuthorFilter! };

        return filters.Any(f => string.Equals(f, pr.Author, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Rate-limit backoff --------------------------------------------------

    // The host client maps a rate limit to a typed GitOperationException naming "rate limit" (G-4 scrubbed).
    private static bool IsRateLimit(Exception ex) =>
        ex is GitOperationException && ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    private void RecordBackoff(ExternalPrSource source)
    {
        lock (_gate)
        {
            var attempt = _backoff.TryGetValue(source.Key, out var b) ? b.Attempt + 1 : 1;
            var delayTicks = Math.Min(
                BaseBackoff.Ticks * (long)Math.Pow(2, attempt - 1),
                MaxBackoff.Ticks);
            _backoff[source.Key] = (_clock() + TimeSpan.FromTicks(delayTicks), attempt);
        }

        _audit.Append(new AuditEvent("external_pr_rate_limited", new Dictionary<string, string>
        {
            ["source"] = source.Key,
        }));
    }

    private void ClearBackoff(ExternalPrSource source)
    {
        lock (_gate)
        {
            _backoff.Remove(source.Key);
        }
    }

    /// <summary>The time (per source) before which the next poll is skipped due to rate-limit backoff, if any.</summary>
    public DateTimeOffset? BackoffUntil(ExternalPrSource source)
    {
        lock (_gate)
        {
            return _backoff.TryGetValue(source.Key, out var b) ? b.Until : null;
        }
    }

    /// <summary>The agent id an intake'd pull request takes: <c>pr-&lt;n&gt;</c>. It is the worktree name,
    /// the <c>agent/pr-&lt;n&gt;</c> branch, the jail's <c>mainguard.agent</c> label, the package-cache
    /// directory and the merge-queue key — one id all the way down, which is what lets an external entry
    /// use the identical verify path as a local agent rather than a parallel one.
    ///
    /// <para><b>It is unique only WITHIN a repo</b>, which is why every one of those is paired with the
    /// repo handle (the container name, its <c>mainguard.repo</c> label, the cache directory, the queue)
    /// and why <see cref="IPrWorkerHost"/> takes <c>repoHash</c> on both of its methods. Two subscribed
    /// repositories that each have a pull request #7 are two different workers, not a collision.</para>
    public static string AgentIdFor(int prNumber) => $"pr-{prNumber}";
}
