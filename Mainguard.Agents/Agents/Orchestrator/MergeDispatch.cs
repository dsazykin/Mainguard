using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>A request to merge a queue entry (P2-12). Origin is read from the queue, not passed in.</summary>
/// <param name="RepoPath">The local repo path (T-23 host/token resolution + the foreground merge).</param>
/// <param name="RepoHash">The P2-06 repo hash (resolves the queue + drives the merge lease).</param>
/// <param name="AgentId">The entry's agent id. For an external PR it is <c>pr-&lt;n&gt;</c> and the number
/// IS the pull request — the queue key and the PR it merges cannot be allowed to disagree.</param>
/// <param name="ExpectedMainSha">The <c>main@sha</c> the verification ran against (the A5 CAS old-OID).</param>
/// <param name="MainBranch">The local main branch the merge lands on.</param>
/// <param name="AllowStaleOverride">Loud, separate stale-override path for a local foreground merge.</param>
/// <param name="OverrideReason">Why the override was used (audited).</param>
public sealed record MergeDispatchRequest(
    string RepoPath,
    string RepoHash,
    string AgentId,
    string ExpectedMainSha,
    string MainBranch = "main",
    bool AllowStaleOverride = false,
    string? OverrideReason = null);

/// <summary>The outcome of a dispatched merge (mirrors <see cref="ForegroundMergeResult"/> for both paths).</summary>
public sealed record MergeDispatchOutcome(bool Merged, string? NewMainSha, bool CasLost, string? Reason);

/// <summary>
/// The P2-12 pluggable merge step: routes a merge by the queue entry's <see cref="MergeEntryOrigin"/>.
/// A <see cref="MergeEntryOrigin.Local"/> entry merges via the existing Windows foreground merge
/// (<see cref="IForegroundMergeService"/>, P2-10); a <see cref="MergeEntryOrigin.External"/> entry merges
/// back through the host PR merge API (<see cref="IExternalPrMergeExecutor"/>) and then, once the merge
/// has demonstrably landed locally, fires the queue's <c>NotifyMainMoved</c> stale cascade. Both origins
/// end at the SAME cascade — the human review gate is unchanged (P2-11 cockpit); this only swaps the
/// transport that lands the merge.
///
/// <para><b>This type has no production caller.</b> The shipped merge is driven from the Windows GUI
/// (<c>DaemonBackedOrchestrator.ConfirmMergeAsync</c>), because both transports need host-side things the
/// daemon does not have: the user's checkout for a local merge, and the host token for an external one —
/// which lives only in the host OS keychain and is never copied into the VM. This remains the daemon-side
/// shape from the P2-12 plan for the day a daemon-driven merge exists. If it is ever wired, it must
/// contend for the daemon's SINGLE <see cref="IMergeLeaseStore"/> rather than being handed one of its own,
/// or "one outstanding merge per repository" stops being true the moment both paths are live (MG-23).</para>
///
/// <para><b>It performs no host call of its own.</b> The external transport is
/// <see cref="IExternalPrMergeExecutor"/> — the same implementation the GUI runs. It used to call the
/// merge API here and then take the post-merge main sha from an injected callback, which meant the
/// dispatch could confirm a merge on the strength of a sha nobody had verified against a ref; the
/// executor instead proves the merge is on the base branch before anything is recorded.</para>
///
/// <para><b>MG-23 — external merges obey the same serialization as local ones.</b> The external path used
/// to call the host merge API and then <c>ConfirmHumanMerge</c> with no <c>TryBegin</c> lease, no
/// <c>CanMerge</c> gate, and no freshness CAS, so the one-outstanding-merge-per-repo invariant simply did
/// not cover external entries: a foreground merge and an external-PR merge (or two external merges) on
/// the same repo could land concurrently, each confirming against a main the other had already moved.
/// Both origins now take the SAME per-repo <see cref="IMergeLeaseStore"/> lease, and the external path
/// re-checks <c>CanMerge</c> and the expected <c>main@sha</c> <i>under</i> that lease. The host merge is
/// the external transport's CAS: a lost race (main moved, or the host refuses the merge) is reconciled
/// exactly like an <c>--ff-only</c> refusal — <c>CasLost</c>, nothing confirmed, the branch re-verifies.</para>
/// </summary>
public interface IMergeDispatch
{
    /// <summary>Merges the entry via its origin's transport and returns the outcome.</summary>
    Task<MergeDispatchOutcome> DispatchMergeAsync(MergeDispatchRequest request, CancellationToken ct);
}

/// <inheritdoc cref="IMergeDispatch"/>
public sealed class MergeDispatch : IMergeDispatch
{
    /// <summary>The refusal the foreground path returns when the per-repo lease is already held; the
    /// external path returns the identical wording so the UI renders one vocabulary (§3.4).</summary>
    internal const string LeaseHeldReason = "another merge is already in progress for this repository";

    private readonly IForegroundMergeService _foreground;
    private readonly IExternalPrMergeExecutor _external;
    private readonly IMergeLeaseStore _leases;
    private readonly Func<string, MergeQueue?> _resolveQueue;

    /// <param name="foreground">The P2-10 Windows foreground merge (local entries). Its own <c>onMerged</c>
    /// wiring fires the queue's <c>ConfirmHumanMerge</c> → <c>NotifyMainMoved</c>.</param>
    /// <param name="external">The P2-12 external transport (host PR merge + local reconcile) — the SAME
    /// implementation the GUI drives, so there is one answer to "how does an upstream PR merge".</param>
    /// <param name="leases">The RT-D1 per-repo merge lease store — the SAME instance the foreground merge
    /// uses, which is what makes the one-outstanding-merge-per-repo invariant span both origins (MG-23).</param>
    /// <param name="resolveQueue">Resolves a repo hash → its live <see cref="MergeQueue"/> (origin lookup + cascade).</param>
    public MergeDispatch(
        IForegroundMergeService foreground,
        IExternalPrMergeExecutor external,
        IMergeLeaseStore leases,
        Func<string, MergeQueue?> resolveQueue)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _external = external ?? throw new ArgumentNullException(nameof(external));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _resolveQueue = resolveQueue ?? throw new ArgumentNullException(nameof(resolveQueue));
    }

    public async Task<MergeDispatchOutcome> DispatchMergeAsync(MergeDispatchRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var queue = _resolveQueue(request.RepoHash)
            ?? throw new InvalidOperationException($"No active merge queue for repo '{request.RepoHash}'.");

        var origin = queue.GetOrigin(request.AgentId);
        return origin switch
        {
            MergeEntryOrigin.External => await MergeExternalAsync(queue, request, ct).ConfigureAwait(false),
            _ => MergeLocal(request),
        };
    }

    // Local: the existing foreground merge. NotifyMainMoved is fired by the foreground service's own
    // onMerged callback (daemon-wired to queue.ConfirmHumanMerge) — the dispatch does not double-fire it.
    private MergeDispatchOutcome MergeLocal(MergeDispatchRequest request)
    {
        var result = _foreground.MergeAgentBranch(new ForegroundMergeRequest(
            request.RepoPath,
            request.RepoHash,
            request.AgentId,
            request.ExpectedMainSha,
            request.MainBranch,
            request.AllowStaleOverride,
            request.OverrideReason));

        return new MergeDispatchOutcome(result.Merged, result.NewMainSha, result.CasLost, result.Reason);
    }

    // External: merge through the host PR API, then land the merged sha locally and fire the cascade.
    // Every step below the lease exists because the host API is not git: it gives us no ref-level CAS,
    // so the serialization the local path gets from `--ff-only` has to be reconstructed explicitly.
    private async Task<MergeDispatchOutcome> MergeExternalAsync(MergeQueue queue, MergeDispatchRequest request, CancellationToken ct)
    {
        // MG-23 step 1 — the SAME per-repo lease the foreground merge takes. Whichever origin wins owns
        // this repo's main until it confirms or releases; every other merge, local or external, is told
        // to wait in the identical words. Without this the external transport ran wholly outside the
        // one-outstanding-merge-per-repo invariant.
        var leaseId = Guid.NewGuid().ToString("N");
        var lease = _leases.TryBegin(
            request.RepoHash, leaseId, request.AgentId, request.ExpectedMainSha, request.MainBranch);
        if (lease is null)
        {
            return new MergeDispatchOutcome(false, null, CasLost: false, LeaseHeldReason);
        }

        var confirmed = false;
        try
        {
            // MG-23 step 2 — the merge gates are read UNDER the lease, never before it. A gate read
            // outside the lease is only a snapshot of a repo somebody else may still be merging into.
            if (!queue.CanMerge(request.AgentId, out var gateReason))
            {
                return new MergeDispatchOutcome(false, null, CasLost: false, gateReason);
            }

            // MG-23 step 3 — the freshness compare-and-swap. The local path gets this free from
            // `git merge --ff-only` (git advances refs/heads/main only while main is still an ancestor);
            // the host merge API takes no old-OID, so the queue's authoritative main@sha is compared
            // here instead — inside the lease, so main cannot move between the compare and the merge.
            if (!string.Equals(queue.CurrentMainSha, request.ExpectedMainSha, StringComparison.Ordinal))
            {
                return new MergeDispatchOutcome(false, null, CasLost: true,
                    "verification is stale — main moved; re-verifying");
            }

            // The external transport: merge the pull request on its host, then bring the checkout up to
            // date with the merge the host performed. Every way that can fail — including the host
            // refusing, which is precisely an `--ff-only` refusal on the local path — comes back as a
            // reason rather than an exception, and none of them confirms anything.
            var result = await _external
                .MergeExternalPrAsync(
                    new ForegroundMergeRequest(
                        request.RepoPath, request.RepoHash, request.AgentId,
                        request.ExpectedMainSha, request.MainBranch),
                    lease, ct)
                .ConfigureAwait(false);

            if (!result.Merged || string.IsNullOrEmpty(result.NewMainSha))
            {
                // NOTHING LANDED. Confirming here would move the entry to Merged and fire NotifyMainMoved
                // at every other agent in the repo on the strength of a merge that did not happen.
                return new MergeDispatchOutcome(false, null, result.CasLost, result.Reason);
            }

            // The merge is proven landed, so the queue's stale cascade fires — identical to the local
            // path's terminal handling. Confirm writes the RT-D1 idempotency record and releases the
            // lease, so the boot reconcile can tell a landed merge from an abandoned one.
            _leases.Confirm(request.RepoHash, leaseId, result.NewMainSha!);
            confirmed = true;
            queue.ConfirmHumanMerge(request.AgentId, result.NewMainSha!); // → Merged + NotifyMainMoved

            return new MergeDispatchOutcome(Merged: true, result.NewMainSha, CasLost: false, Reason: null);
        }
        finally
        {
            // Every non-merged exit — gate refusal, lost CAS, cancellation, an exception out of the
            // fetch — hands the lease back. A stranded lease would block every future merge on this
            // repo until the daemon restarts and the reconcile sweeps it.
            if (!confirmed)
            {
                _leases.Release(request.RepoHash, leaseId);
            }
        }
    }
}
