using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>What one <see cref="AgentSessionReconciler"/> pass changed. Every list holds agent ids.</summary>
/// <param name="Adopted">Live jails that had no session record and now have one.</param>
/// <param name="Corrected">Sessions whose tracked state disagreed with Docker and was moved to Docker's.</param>
/// <param name="Lost">Sessions whose jail is no longer running; marked so rather than left looking alive.</param>
/// <param name="Skipped">True when Docker could not be read at all, so the pass changed nothing. This is
/// NOT the same as "nothing to do", and conflating the two is how a reconcile turns into a mass reaping.</param>
public sealed record AgentSessionReconcileReport(
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Corrected,
    IReadOnlyList<string> Lost,
    bool Skipped = false)
{
    /// <summary>The pass that could not run.</summary>
    public static AgentSessionReconcileReport Unavailable { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Skipped: true);

    /// <summary>True when the pass moved something (the log/audit threshold).</summary>
    public bool Changed => Adopted.Count + Corrected.Count + Lost.Count > 0;
}

/// <summary>
/// ISSUES-LOG #18/#20 — makes Docker the truth for the <b>live session store</b>, not only for the
/// P2-08 expected-agents table.
///
/// <para><b>The gap this closes.</b> Two reconcilers already ran at boot:
/// <see cref="SwarmReconciler"/> diffed Docker against the SQLite <c>ExpectedAgents</c> table, and
/// <c>LeaderReattachTask</c> diffed it against the durable PTY leader registry. Neither ever touched
/// <see cref="AgentSessionStore"/> — the purely in-memory registry that <c>ListAgents</c>,
/// <c>StreamAgentEvents</c>, the resource monitor and the kill switch all project from. So a daemon
/// restart left every surviving jail running, billing and holding a worktree while every surface in the
/// app honestly reported zero agents: the containers were adopted into a table nobody renders. "Orphan"
/// was never quite right — they were adopted, into the wrong book.</para>
///
/// <para><b>The second half</b> is drift in the other direction. <see cref="AgentSession.State"/> is
/// push-only: something calls <c>MarkState</c>, or the word never changes. A <c>docker unpause</c> run by
/// hand, an engine restart, a jail killed by the OOM killer — none of these go through an RPC, so nothing
/// tells the daemon, and the stale word survives indefinitely. One agent was reported <c>Paused</c> for
/// 20+ minutes after it had been made to run again. A pass here re-reads Docker and moves the word.</para>
///
/// <para><b>Nothing is destroyed.</b> Adoption gives an orphan back its identity (and therefore a Stop
/// button, a resource row and a kill switch that reaches it); it never stops a container. A jail that is
/// gone is <i>marked</i>, not swept — the daemon's job here is to stop lying about what is running, and a
/// reconcile pass that reaps user work while the user is looking the other way is the failure this whole
/// area already paid for once (see <see cref="SwarmReconcileTask"/>'s remarks). Stopped-but-present jails
/// are also left alone on purpose: <c>DockerSandboxEngine</c> re-starts a persistent jail by name, so
/// removing one would destroy a session that is designed to be resumable.</para>
/// </summary>
public sealed class AgentSessionReconciler
{
    /// <summary>The G-17 audit type for a pass that changed something.</summary>
    public const string ReconciledEvent = "agent_session_reconcile";

    private readonly AgentSessionStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> _listContainers;
    private readonly Mainguard.Git.Audit.IAuditLog? _audit;
    private readonly ILogger _log;

    /// <param name="store">The live session store this pass corrects.</param>
    /// <param name="listContainers">Lists the <c>mainguard.agent</c>-labelled containers. It is allowed —
    /// required, even — to THROW when the engine is unreachable: an empty list from a down Docker would
    /// otherwise read as "every agent's jail vanished" and mark the whole swarm lost.</param>
    /// <param name="audit">G-17 sink; optional so unit tests can drive the reconciler bare.</param>
    /// <param name="log">Milestone sink.</param>
    public AgentSessionReconciler(
        AgentSessionStore store,
        Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> listContainers,
        Mainguard.Git.Audit.IAuditLog? audit = null,
        ILogger? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _listContainers = listContainers ?? throw new ArgumentNullException(nameof(listContainers));
        _audit = audit;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Runs one pass. Never throws: a reconcile that takes the daemon down is worse than a
    /// reconcile that missed a beat, and the next pass is 30 seconds away.</summary>
    public async Task<AgentSessionReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        IReadOnlyList<AgentContainerState> containers;
        try
        {
            containers = await _listContainers(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Docker unreachable / not installed / still starting. Change NOTHING — see the ctor remark.
            _log.LogDebug(ex, "agent-session reconcile skipped: the container engine did not answer");
            return AgentSessionReconcileReport.Unavailable;
        }

        var adopted = new List<string>();
        var corrected = new List<string>();
        var lost = new List<string>();

        // Keyed by (repo, agent) — the same identity AgentSessionStore uses, because an agent id like
        // `pr-7` is unique only inside a repository.
        var byKey = new Dictionary<AgentSessionKey, AgentContainerState>();
        foreach (var container in containers)
        {
            if (string.IsNullOrEmpty(container.AgentId))
            {
                continue; // not one of ours (no agent label) — the filter should already exclude it
            }

            byKey[new AgentSessionKey(container.RepoHash ?? string.Empty, container.AgentId)] = container;
        }

        // ---- 1. Live jails with no session record → adopt -------------------------------------------
        foreach (var (key, container) in byKey)
        {
            if (!container.Live || _store.Find(key) is not null)
            {
                continue;
            }

            try
            {
                // The kind/role come off the container's own labels. A jail created before those labels
                // existed adopts as a role-less session named after its id — visible and stoppable, which
                // is the whole point, and honest about what it does not know.
                _store.Spawn(
                    kind: string.IsNullOrEmpty(container.Kind) ? "unknown" : container.Kind,
                    role: container.Role ?? string.Empty,
                    agentId: container.AgentId,
                    repoHash: key.RepoHash);
                _store.AttachSandbox(key, container.ContainerId);
                if (container.Paused)
                {
                    _store.MarkState(key, PausedState, AdoptedPausedReason);
                }
                else
                {
                    _store.MarkState(key, WorkingState, AdoptedReason);
                }

                adopted.Add(container.AgentId);
            }
            catch (Exception ex)
            {
                // A concurrent spawn won the (repo, id) — that session is the real one; leave it.
                _log.LogDebug(ex, "agent-session reconcile: could not adopt {Agent}", container.AgentId);
            }
        }

        // ---- 2. Sessions whose jail disagrees with the record ---------------------------------------
        foreach (var session in _store.List())
        {
            if (string.IsNullOrEmpty(session.ContainerId))
            {
                continue; // session-only record (unprovisioned repo / headless path) — no jail to compare
            }

            var key = session.Key;
            if (!byKey.TryGetValue(key, out var container) || !container.Live)
            {
                // The jail is gone or stopped. Say so once; a record that keeps claiming "Working" for a
                // container Docker has never heard of is the exact lie this pass exists to end.
                if (!IsAlreadyLost(session.State))
                {
                    _store.MarkState(key, LostState, LostReason);
                    lost.Add(session.Id);
                }

                continue;
            }

            // Only the PAUSE AXIS is corrected. A live session's state word carries orchestration
            // meaning the container cannot know — RateLimited, Yielding, AwaitingReview — and flattening
            // all of it to "Working" because Docker says the process tree is scheduled would destroy
            // more information than the drift did.
            if (container.Paused && !IsPaused(session.State))
            {
                _store.MarkState(key, PausedState, DriftedToPausedReason);
                corrected.Add(session.Id);
            }
            else if (!container.Paused && IsPaused(session.State))
            {
                _store.MarkState(key, WorkingState, DriftedToRunningReason);
                corrected.Add(session.Id);
            }
        }

        var report = new AgentSessionReconcileReport(adopted, corrected, lost);
        if (report.Changed)
        {
            _log.LogInformation(
                "agent-session reconcile: adopted={Adopted} corrected={Corrected} lost={Lost}",
                Describe(adopted), Describe(corrected), Describe(lost));
            _audit?.Append(new Mainguard.Git.Audit.AuditEvent(
                ReconciledEvent, new Dictionary<string, string>
                {
                    ["adopted"] = string.Join(",", adopted),
                    ["corrected"] = string.Join(",", corrected),
                    ["lost"] = string.Join(",", lost),
                    ["when"] = DateTimeOffset.UtcNow.ToString(
                        "O", System.Globalization.CultureInfo.InvariantCulture),
                }));
        }

        return report;
    }

    /// <summary>The state word for a jail Docker reports as scheduled.</summary>
    public const string WorkingState = "Working";

    /// <summary>The state word for a frozen jail.</summary>
    public const string PausedState = "Paused";

    /// <summary>
    /// The state word for a session whose container is gone or stopped. <c>Unresponsive</c> and not
    /// <c>Paused</c>, for the reason <see cref="SandboxKillTarget"/> already learned the hard way: an
    /// agent nothing is containing must never project as merely frozen, because "paused" reads as
    /// recoverable-by-pressing-resume and this one is not.
    /// </summary>
    public const string LostState = "Unresponsive";

    internal const string AdoptedReason =
        "Adopted after a daemon restart — this jail was already running.";

    internal const string AdoptedPausedReason =
        "Adopted after a daemon restart — this jail was already running, and paused.";

    internal const string DriftedToPausedReason =
        "Paused outside Mainguard — the jail is frozen.";

    internal const string DriftedToRunningReason =
        "Resumed outside Mainguard — the jail is running again.";

    internal const string LostReason =
        "The jail is no longer running — Docker has no live container for this agent.";

    private static bool IsPaused(string state) =>
        string.Equals(state, PausedState, StringComparison.Ordinal);

    /// <summary>Terminal-ish words a lost jail may already be sitting on, so a repeating pass does not
    /// keep re-announcing the same death (and does not overwrite a more specific one, like a CLI's own
    /// <c>Dead</c> with its exit tail).</summary>
    private static bool IsAlreadyLost(string state) =>
        state is LostState or "Dead" or "Stopped" or "Starting";

    private static string Describe(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "none" : string.Join(",", ids);
}

/// <summary>
/// Drives <see cref="AgentSessionReconciler"/>: once at startup (adoption — the restart case) and then on
/// an interval (drift — the out-of-band case).
///
/// <para>Deliberately NOT a <c>DaemonBootSequence</c> step. The boot sequence is fail-fast and strictly
/// ordered around merge leases and worktree pruning; this pass is neither ordered against any of that nor
/// allowed to keep the daemon from starting, and it has to keep running afterwards anyway. It reads
/// Docker and writes only the in-memory session store, so it cannot deadlock a boot step it races.</para>
/// </summary>
public sealed class AgentSessionReconcilerService : BackgroundService
{
    /// <summary>How often drift is re-checked. Short enough that a manual <c>docker pause</c>/
    /// <c>unpause</c> corrects itself while the operator is still looking at the screen, long enough that
    /// the engine is not being polled for its own sake — the resource sampler already runs hotter.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly AgentSessionReconciler _reconciler;
    private readonly ILogger<AgentSessionReconcilerService> _log;

    public AgentSessionReconcilerService(
        AgentSessionReconciler reconciler, ILogger<AgentSessionReconcilerService> log)
    {
        _reconciler = reconciler;
        _log = log;
    }

    /// <summary>The most recent pass, for diagnostics and the composition-root test.</summary>
    public AgentSessionReconcileReport? LastReport { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LastReport = await _reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // ReconcileAsync already swallows the expected failures; this is the belt for the braces.
                _log.LogWarning(ex, "agent-session reconcile pass failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
