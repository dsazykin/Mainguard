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
/// <param name="AgentId">The entry's agent id (<c>pr-&lt;n&gt;</c> for an external PR).</param>
/// <param name="ExpectedMainSha">The <c>main@sha</c> the verification ran against (the A5 CAS old-OID).</param>
/// <param name="MainBranch">The local main branch the merge lands on.</param>
/// <param name="PrNumber">The upstream PR number — REQUIRED for an external entry, ignored for a local one.</param>
/// <param name="Method">The host merge method for an external entry.</param>
/// <param name="AllowStaleOverride">Loud, separate stale-override path for a local foreground merge.</param>
/// <param name="OverrideReason">Why the override was used (audited).</param>
public sealed record MergeDispatchRequest(
    string RepoPath,
    string RepoHash,
    string AgentId,
    string ExpectedMainSha,
    string MainBranch = "main",
    int? PrNumber = null,
    PullRequestMergeMethod Method = PullRequestMergeMethod.Merge,
    bool AllowStaleOverride = false,
    string? OverrideReason = null);

/// <summary>The outcome of a dispatched merge (mirrors <see cref="ForegroundMergeResult"/> for both paths).</summary>
public sealed record MergeDispatchOutcome(bool Merged, string? NewMainSha, bool CasLost, string? Reason);

/// <summary>
/// The P2-12 pluggable merge step: routes a merge by the queue entry's <see cref="MergeEntryOrigin"/>.
/// A <see cref="MergeEntryOrigin.Local"/> entry merges via the existing Windows foreground merge
/// (<see cref="IForegroundMergeService"/>, P2-10); a <see cref="MergeEntryOrigin.External"/> entry merges
/// back through the host PR merge API (T-23) and then, once the merged SHA lands locally, fires the
/// queue's <c>NotifyMainMoved</c> stale cascade. Both origins end at the SAME cascade — the human review
/// gate is unchanged (P2-11 cockpit); this only swaps the transport that lands the merge.
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
    private readonly IPullRequestService _prService;
    private readonly IMergeLeaseStore _leases;
    private readonly Func<string, MergeQueue?> _resolveQueue;
    private readonly Func<MergeDispatchRequest, CancellationToken, Task<string>> _fetchMergedMainSha;

    /// <param name="foreground">The P2-10 Windows foreground merge (local entries). Its own <c>onMerged</c>
    /// wiring fires the queue's <c>ConfirmHumanMerge</c> → <c>NotifyMainMoved</c>.</param>
    /// <param name="prService">The audited T-23 transport used for the host PR merge (external entries).</param>
    /// <param name="leases">The RT-D1 per-repo merge lease store — the SAME instance the foreground merge
    /// uses, which is what makes the one-outstanding-merge-per-repo invariant span both origins (MG-23).</param>
    /// <param name="resolveQueue">Resolves a repo hash → its live <see cref="MergeQueue"/> (origin lookup + cascade).</param>
    /// <param name="fetchMergedMainSha">After a host merge, fetches main and returns its new SHA (the sha that "lands locally").</param>
    public MergeDispatch(
        IForegroundMergeService foreground,
        IPullRequestService prService,
        IMergeLeaseStore leases,
        Func<string, MergeQueue?> resolveQueue,
        Func<MergeDispatchRequest, CancellationToken, Task<string>> fetchMergedMainSha)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _resolveQueue = resolveQueue ?? throw new ArgumentNullException(nameof(resolveQueue));
        _fetchMergedMainSha = fetchMergedMainSha ?? throw new ArgumentNullException(nameof(fetchMergedMainSha));
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
        if (request.PrNumber is not int prNumber)
        {
            throw new InvalidOperationException(
                $"External entry '{request.AgentId}' has no PR number to merge through the host API.");
        }

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

            // The ONE audited transport performs the merge (an explicit user action — invariant 1 permits it).
            try
            {
                await _prService.MergeAsync(request.RepoPath, prNumber, request.Method, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The host refused: the PR stopped being mergeable because its base moved, or it was
                // already merged/closed upstream. That is precisely an `--ff-only` refusal on the local
                // path — the CAS lost, no merge of ours landed, nothing is confirmed, the branch
                // re-verifies against the new main. Reported as CasLost, not as a crash.
                return new MergeDispatchOutcome(false, null, CasLost: true,
                    $"verification is stale — the host refused the merge; re-verifying ({ex.Message})");
            }

            // The merged sha lands locally (fetch main), then the queue's stale cascade fires — identical
            // to the local path's terminal handling. Confirm writes the RT-D1 idempotency record and
            // releases the lease, so the boot reconcile can tell a landed merge from an abandoned one.
            var newMainSha = await _fetchMergedMainSha(request, ct).ConfigureAwait(false);
            _leases.Confirm(request.RepoHash, leaseId, newMainSha);
            confirmed = true;
            queue.ConfirmHumanMerge(request.AgentId, newMainSha); // → Merged + NotifyMainMoved

            return new MergeDispatchOutcome(Merged: true, newMainSha, CasLost: false, Reason: null);
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
