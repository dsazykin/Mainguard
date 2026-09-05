using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Services;

/// <summary>
/// The narrow host seam the external merge is allowed to use (P2-12 invariant 1: intake writes nothing
/// upstream without an explicit user action). Deliberately two methods — read the pull request, and merge
/// it — so this path structurally cannot close, comment on, or review a PR, and so a test fake is a fake
/// of the merge, not a re-implementation of the whole nine-method <see cref="IPullRequestService"/>.
///
/// <para>The production implementation is <see cref="HostPullRequestGateway"/>, a thin adapter over the
/// ONE audited T-23 transport. It exists so tests drive the merge against a fake host and <b>never</b> the
/// live GitHub API.</para>
/// </summary>
public interface IHostPullRequestGateway
{
    /// <summary>The pull request's current upstream state: open/closed/merged, head sha, mergeability.</summary>
    Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct);

    /// <summary>
    /// Merges the pull request on the host, refusing if its head is no longer
    /// <paramref name="expectedHeadSha"/>. Returns the item carrying the resulting
    /// <see cref="PullRequestItem.MergeCommitSha"/>.
    /// </summary>
    Task<PullRequestItem> MergeAsync(
        string repoPath, int number, string expectedHeadSha, PullRequestMergeMethod method, CancellationToken ct);
}

/// <summary>The production <see cref="IHostPullRequestGateway"/>: the audited T-23 transport, narrowed.</summary>
public sealed class HostPullRequestGateway : IHostPullRequestGateway
{
    private readonly IPullRequestService _pr;

    public HostPullRequestGateway(IPullRequestService pullRequests)
        => _pr = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));

    public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
        => _pr.GetAsync(repoPath, number, ct);

    public Task<PullRequestItem> MergeAsync(
        string repoPath, int number, string expectedHeadSha, PullRequestMergeMethod method, CancellationToken ct)
        => _pr.MergeAsync(repoPath, number, method, expectedHeadSha, ct);
}

/// <summary>
/// <b>The middle leg of the RT-D1 conversation for an <see cref="MergeEntryOrigin.External"/> entry</b>
/// (P2-12 §3.3), shaped exactly like <see cref="IJournaledMergeExecutor"/> is for a local one: the caller
/// already holds the repo's single merge lease (taken over the daemon's <c>BeginMerge</c>), this performs
/// the merge, and the caller records the outcome with <c>ConfirmMerge</c> or hands the lease back with
/// <c>AbandonMerge</c>. It takes, confirms and releases <b>nothing</b> — there is exactly one
/// <see cref="IMergeLeaseStore"/> in the system and it is the daemon's (MG-23).
/// </summary>
public interface IExternalPrMergeExecutor
{
    /// <summary>
    /// Merges the entry's upstream pull request through the host API, then converges the user's checkout
    /// onto the merge the host performed. Returns the same <see cref="ForegroundMergeResult"/> vocabulary
    /// the local leg returns, so the caller's begin → merge → confirm/abandon logic is origin-agnostic.
    /// </summary>
    Task<ForegroundMergeResult> MergeExternalPrAsync(
        ForegroundMergeRequest request, MergeLeaseRow lease, CancellationToken ct);
}

/// <summary>
/// The P2-12 external merge: an intake'd bot PR is merged <b>on its host</b>, never by fast-forwarding
/// its mirrored branch locally.
///
/// <para><b>Why a local fast-forward is the wrong answer, and a dangerous one.</b> An external entry's
/// <c>agent/pr-&lt;n&gt;</c> branch really does exist in the mirror, so <c>git merge --ff-only</c> would
/// happily succeed: main advances, the queue records a merge, the work looks shipped — and the pull
/// request is still open upstream. That is not a missing feature, it is divergent state that reads as
/// correct from inside the app, and it is the external twin of the defect the local path was just fixed
/// for. The upstream merge is therefore <b>authoritative</b>: nothing is recorded until the host has
/// actually merged the PR and that merge commit is demonstrably on the base branch this checkout fetches.</para>
///
/// <para><b>What "merged" means here.</b> Three things must all hold, in order, or nothing is recorded:
/// (1) the host merged the pull request and named the commit it produced; (2) that commit is reachable
/// from the host remote's main after a fetch — proof from git, not from the API's own say-so; and (3) the
/// user's <c>refs/heads/main</c> fast-forwards onto it. Only then does the caller confirm, with the sha
/// main <i>actually</i> moved to. "The merge API returned success" is explicitly not sufficient: an API
/// that reports a merge it did not perform, or performed somewhere this checkout cannot see, must not be
/// able to walk an entry to the terminal <c>Merged</c> state.</para>
///
/// <para><b>The compare-and-swap.</b> The local leg gets its CAS free from <c>--ff-only</c>. Here there
/// are two, because there are two refs that can move under us: the PR head (checked against the verified
/// <c>agent/pr-&lt;n&gt;</c> tip AND re-checked by the host's own <c>sha</c> merge parameter, which
/// refuses with 409 if the PR gained a commit in the window), and main (pre-checked against the lease's
/// expected sha, then enforced by the <c>--ff-only</c> reconcile).</para>
///
/// <para><b>K4/§23.5 — the head CAS could not fail, and that is why it is READ and not derived.</b> The
/// "verified <c>agent/pr-&lt;n&gt;</c> tip" above used to be computed here, at merge time, from whatever
/// this checkout held under that name. <c>PrHeadFetcher</c> hard-resets that ref to the pull request's
/// newest head BEFORE the intake re-queues the entry, so between a force-push and the next intake poll
/// both sides of the compare were the new head: it passed, and unverified third-party code merged. The
/// verified head now comes from <c>ForegroundMergeRequest.ExpectedBranchSha</c> — the sha the daemon put
/// on the lease from its OWN verification record — and this checkout is only asked whether that commit is
/// present. An entry with no recorded verified head refuses rather than falling back, the one place in
/// this lane where an unknown is a "no": here declining to answer means merging code from outside this
/// installation on the strength of a compare that cannot fail.</para>
///
/// <para><b>Double-merge.</b> A confirmed entry is terminal <c>Merged</c>, so <c>CanMerge</c> is false and
/// the daemon's <c>BeginMerge</c> never grants a second lease for it. Independently, a second attempt
/// reads the PR as <c>Merged</c> upstream and refuses before touching anything, and the host's head CAS
/// would refuse a replay anyway. Three layers, none of which depends on the other two.</para>
/// </summary>
public sealed class ExternalPrMergeService : IExternalPrMergeExecutor
{
    /// <summary>The <c>pr-&lt;n&gt;</c> agent-id prefix the intake materializes external entries under.</summary>
    private const string ExternalAgentIdPrefix = "pr-";

    private readonly Func<string, SyncRemote> _resolveSyncRemote;
    private readonly IHostPullRequestGateway _host;
    private readonly IOperationJournal _journal;
    private readonly PullRequestMergeMethod _method;

    /// <param name="resolveSyncRemote">repoHash → the ONE SC-2 sync remote (never a literal). The verified
    /// PR head is read from the mirror through it, exactly as the local leg reads the agent branch.</param>
    /// <param name="host">The narrow host seam (production: the audited T-23 transport).</param>
    /// <param name="journal">The T-19 operation journal — the local fast-forward is one undoable op, the
    /// same as the local leg's merge, so the RT-D1 boot reconcile can replay it.</param>
    /// <param name="method">The host merge method. Defaults to a merge commit; squash/rebase work too
    /// because the proof is the commit the host names, not the PR head's identity.</param>
    public ExternalPrMergeService(
        Func<string, SyncRemote> resolveSyncRemote,
        IHostPullRequestGateway host,
        IOperationJournal journal,
        PullRequestMergeMethod method = PullRequestMergeMethod.Merge)
    {
        _resolveSyncRemote = resolveSyncRemote ?? throw new ArgumentNullException(nameof(resolveSyncRemote));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _method = method;
    }

    /// <summary>The upstream PR number an external entry's agent id names (<c>pr-7</c> → 7), or null.</summary>
    public static int? PrNumberFor(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || !agentId.StartsWith(ExternalAgentIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(
            agentId.AsSpan(ExternalAgentIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n > 0
            ? n
            : null;
    }

    public async Task<ForegroundMergeResult> MergeExternalPrAsync(
        ForegroundMergeRequest request, MergeLeaseRow lease, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (lease is null) throw new ArgumentNullException(nameof(lease));

        if (PrNumberFor(request.AgentId) is not int prNumber)
        {
            return Refuse(
                $"'{request.AgentId}' is an upstream pull request entry but its id doesn't name a PR number");
        }

        // ---- (1) The local checkout must be able to receive the merge BEFORE anything is merged
        // upstream. An upstream merge we cannot land locally is not undoable, so every precondition that
        // would refuse the reconcile is checked while refusing is still free.

        var preflight = PrepareCheckout(request);
        if (preflight.Refusal is { } refusal)
        {
            return refusal;
        }

        var hostRemote = preflight.HostRemote!;
        var verifiedHead = preflight.VerifiedHead!;

        // ---- (2) What the host says the pull request is right now.

        PullRequestDetail detail;
        try
        {
            detail = await _host.GetAsync(request.RepoPath, prNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(DescribeHostFailure(ex, prNumber, "read"));
        }

        if (ClassifyUpstreamState(detail, prNumber, verifiedHead) is { } upstreamRefusal)
        {
            return upstreamRefusal;
        }

        // ---- (3) The merge itself, on the host, under its own head compare-and-swap.

        PullRequestItem merged;
        try
        {
            merged = await _host.MergeAsync(request.RepoPath, prNumber, verifiedHead, _method, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(DescribeHostFailure(ex, prNumber, "merge"), CasLost: IsLostRace(ex));
        }

        if (string.IsNullOrWhiteSpace(merged.MergeCommitSha))
        {
            // The host claims a merge but names no commit, so there is nothing to verify it against and
            // nothing for main to fast-forward onto. Recording a merge on this evidence is exactly the
            // "the API returned success" failure this whole path exists to prevent.
            return Refuse(
                $"the host reported pull request #{prNumber} as merged but named no merge commit — "
                + "nothing was recorded; check the pull request on its host");
        }

        // ---- (4) The pull request is merged upstream. Converge the user's checkout onto it, and prove
        // from git — never from the API response — that the merge really is on the base branch.

        return ReconcileLocalMain(request, hostRemote, merged.MergeCommitSha, prNumber);
    }

    // ---- (1) local preconditions ------------------------------------------------------------------

    private readonly record struct Preflight(
        ForegroundMergeResult? Refusal, string? HostRemote, string? VerifiedHead);

    private Preflight PrepareCheckout(ForegroundMergeRequest request)
    {
        // A dirty tree cannot be merged into. Refusing here, before the host call, is the difference
        // between "nothing happened" and "the PR is merged upstream but your repo is stuck".
        var (statusCode, dirt, _) = GitService.RunGit(
            request.RepoPath, "status", "--porcelain", "--untracked-files=no");
        if (statusCode != 0)
        {
            return new Preflight(
                Refuse("couldn't read the repository status — is this still a git repository?"), null, null);
        }

        if (dirt.Trim().Length > 0)
        {
            return new Preflight(
                Refuse("the working tree has uncommitted changes — commit or stash them, then merge"), null, null);
        }

        // The host remote is where the merged commit will arrive. It is NOT the sync remote: that is the
        // daemon's staging mirror, which has no idea the pull request exists.
        var syncRemote = _resolveSyncRemote(request.RepoHash);
        var hostRemote = ResolveHostRemote(request.RepoPath, syncRemote.Name);
        if (hostRemote is null)
        {
            return new Preflight(
                Refuse(
                    "this checkout has no host remote to pull the merged pull request from — add the "
                    + "repository's origin, then merge"),
                null, null);
        }

        // HEAD must already be on main, or the fast-forward would advance some other ref.
        var currentBranch = RevParse(request.RepoPath, "--abbrev-ref", "HEAD");
        if (!string.Equals(currentBranch, request.MainBranch, StringComparison.Ordinal))
        {
            var (checkoutCode, _, checkoutErr) = GitService.RunGit(request.RepoPath, "checkout", request.MainBranch);
            if (checkoutCode != 0
                || !string.Equals(
                    RevParse(request.RepoPath, "--abbrev-ref", "HEAD"), request.MainBranch, StringComparison.Ordinal))
            {
                return new Preflight(
                    Refuse($"couldn't switch to '{request.MainBranch}' to merge into ({FirstLine(checkoutErr)})"),
                    null, null);
            }
        }

        // Freshness pre-check against the lease's old-OID, the same compare the local leg makes.
        var mainSha = RevParse(request.RepoPath, "--verify", request.MainBranch);
        if (!string.Equals(mainSha, request.ExpectedMainSha, StringComparison.Ordinal))
        {
            return new Preflight(
                Refuse("verification is stale — main moved; re-verifying", CasLost: true), null, null);
        }

        // The verified PR head — K4, and the whole reason this method exists in this shape.
        //
        // It used to be RE-DERIVED here, at merge time, by reading whatever refs/heads/agent/pr-<n> this
        // checkout currently held. PrHeadFetcher hard-RESETS that ref to the PR's newest head BEFORE the
        // intake calls NotifyNewCommits, so between a force-push and the next intake poll the ref names the
        // NEW head — and the head compare below then compared the new head against the new head and passed.
        // "The verified head" was, by construction, whatever was current: the external-origin twin of H6,
        // and the fix for H6 does not reach it precisely because the reset happens first.
        //
        // So it is now READ, from the identity the daemon recorded when it granted the lease — the
        // agent/pr-<n> tip the queue's verification was measured ON. That is the one sha that cannot be
        // re-derived into agreement with itself.
        var (fetchCode, _, fetchErr) = UncRemoteTrust.RunGitTrustingRemote(
            request.RepoPath, syncRemote.Name, "fetch", syncRemote.Name);
        if (fetchCode != 0)
        {
            return new Preflight(
                Refuse(
                    $"couldn't fetch '{syncRemote.Name}' — the verified pull request head can't be "
                    + $"confirmed as current ({FirstLine(fetchErr)})"),
                null, null);
        }

        var branch = $"agent/{request.AgentId}";

        // The one place in this file that REFUSES on an unknown rather than falling back. Everywhere else a
        // missing measurement means "decline to answer"; here declining to answer means merging code from
        // outside this installation on the strength of a compare that cannot fail. An unanswerable question
        // gating an irreversible act on third-party code is a "no".
        if (string.IsNullOrEmpty(request.ExpectedBranchSha))
        {
            return new Preflight(
                Refuse(
                    "the queue has no recorded verified head for this pull request, so there is nothing "
                    + "to compare its current head against — re-verify the entry, then merge"),
                null, null);
        }

        // The verified head still has to BE here, or there is nothing for the reconcile to land. Which
        // spelling holds it does not matter; that it is the recorded sha does.
        if (!IsPresent(request.RepoPath, branch, syncRemote.Name, request.ExpectedBranchSha))
        {
            return new Preflight(
                Refuse(
                    $"'{branch}' in this repository is not the pull request head the queue verified "
                    + $"({Short(request.ExpectedBranchSha)}) — re-verifying", CasLost: true),
                null, null);
        }

        return new Preflight(null, hostRemote, request.ExpectedBranchSha);
    }

    // ---- (2) upstream state classification ---------------------------------------------------------

    /// <summary>
    /// Each upstream condition that must stop the merge, with its own sentence. They are separated because
    /// they need opposite responses from the human: a conflict is theirs to resolve, a blocked check is
    /// someone else's to satisfy, an already-merged PR needs nothing at all.
    /// </summary>
    private static ForegroundMergeResult? ClassifyUpstreamState(
        PullRequestDetail detail, int prNumber, string verifiedHead)
    {
        var summary = detail.Summary;

        switch (summary.State)
        {
            case PullRequestState.Merged:
                // Already merged upstream — by someone else, or by an earlier attempt whose local
                // reconcile failed. NOT recorded as this queue's merge: we have no merge commit we
                // verified and no main we moved. The intake cancels the entry on its next poll (a PR that
                // is no longer open is pruned), and main moving fires the cascade the normal way.
                return Refuse(
                    $"pull request #{prNumber} is already merged upstream — nothing to merge; "
                    + "the queue entry clears once the intake sees it closed");

            case PullRequestState.Closed:
                return Refuse(
                    $"pull request #{prNumber} was closed upstream without being merged — reopen it on its "
                    + "host if this work should still land");

            case PullRequestState.Draft:
                return Refuse($"pull request #{prNumber} is still a draft upstream — mark it ready, then merge");
        }

        if (summary.IsDraft)
        {
            return Refuse($"pull request #{prNumber} is still a draft upstream — mark it ready, then merge");
        }

        // The head CAS. The queue verified `verifiedHead`; if the PR has moved since, merging it would
        // land commits no verification ever saw. This is the external analogue of the local leg's lost
        // ref CAS, and it is reported the same way so the queue re-verifies rather than despairs.
        if (summary.HeadSha.Length > 0
            && !string.Equals(summary.HeadSha, verifiedHead, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                $"verification is stale — pull request #{prNumber} has new commits since it was verified; "
                + "re-verifying", CasLost: true);
        }

        // The host's own mergeability verdict. `mergeable_state` is consulted rather than the flat
        // `mergeable` bool because that bool reads false for conflicts and for branch protection alike.
        return detail.MergeableState switch
        {
            "dirty" => Refuse(
                $"pull request #{prNumber} conflicts with its base branch upstream — resolve the conflict "
                + "on the pull request, then merge"),
            "blocked" => Refuse(
                $"pull request #{prNumber} is blocked upstream — its required reviews or status checks "
                + "aren't satisfied yet"),
            "behind" => Refuse(
                $"pull request #{prNumber} is behind its base branch upstream — update the pull request, "
                + "then merge", CasLost: true),
            "draft" => Refuse($"pull request #{prNumber} is still a draft upstream — mark it ready, then merge"),
            // "clean"/"unstable"/"unknown"/absent: let the merge call itself be the authority. `unknown`
            // just means the host hasn't finished computing mergeability, and refusing on it would make
            // the merge button randomly unavailable.
            _ => null,
        };
    }

    // ---- (4) local reconcile ------------------------------------------------------------------------

    /// <summary>
    /// Converges <c>refs/heads/main</c> onto the merge the host performed, and proves it. The proof is
    /// deliberately made of two independent git facts — the merge commit is reachable from the host
    /// remote's base branch, and the local main that results contains it — because the one thing this
    /// method must never do is take the API's word for a merge and record it.
    /// </summary>
    private ForegroundMergeResult ReconcileLocalMain(
        ForegroundMergeRequest request, string hostRemote, string mergeCommitSha, int prNumber)
    {
        var (fetchCode, _, fetchErr) = GitService.RunGit(request.RepoPath, "fetch", hostRemote);
        if (fetchCode != 0)
        {
            return MergedUpstreamButNotLocally(
                prNumber, $"couldn't fetch '{hostRemote}' ({FirstLine(fetchErr)})");
        }

        var upstreamMain = $"refs/remotes/{hostRemote}/{request.MainBranch}";
        if (RevParse(request.RepoPath, "--verify", "--quiet", upstreamMain).Length == 0)
        {
            return MergedUpstreamButNotLocally(
                prNumber, $"'{hostRemote}/{request.MainBranch}' isn't in this repository");
        }

        // THE gate. If the commit the host named is not on the branch it claims to have merged into, then
        // whatever happened upstream did not happen here — and an entry must not reach Merged on it.
        var (ancestorCode, _, _) = GitService.RunGit(
            request.RepoPath, "merge-base", "--is-ancestor", mergeCommitSha, upstreamMain);
        if (ancestorCode != 0)
        {
            return Refuse(
                $"the host reported pull request #{prNumber} as merged, but that commit isn't on "
                + $"'{hostRemote}/{request.MainBranch}' — nothing was recorded; check the pull request "
                + "on its host");
        }

        // The A5 ref-level CAS, same as the local leg: git advances refs/heads/main only while main is
        // still an ancestor. Journaled (T-19) so it is undoable and the boot reconcile can replay it.
        var mergeExit = -1;
        var mergeError = string.Empty;
        using (_journal.BeginOperation(
            request.RepoPath, JournalKinds.Merge, $"Merge pull request #{prNumber}"))
        {
            // stderr CAPTURED, not discarded — see the classification below.
            var (code, _, err) = GitService.RunGit(
                request.RepoPath, "merge", "--ff-only", $"{hostRemote}/{request.MainBranch}");
            mergeExit = code;
            mergeError = err;
        }

        if (mergeExit != 0)
        {
            // Same defect as ForegroundMergeService's: a hardcoded "doesn't fast-forward" on ANY non-zero
            // exit, when index.lock contention, a refusing pre-merge hook, a full disk or unrelated
            // histories all land here too. Ask git the fast-forward question directly instead of assuming
            // the answer, and otherwise report what git actually said.
            var upstreamRef = $"{hostRemote}/{request.MainBranch}";
            var (probeCode, _, _) = GitService.RunGit(
                request.RepoPath, "merge-base", "--is-ancestor", request.MainBranch, upstreamRef);
            var detail = FirstLine(mergeError);

            return MergedUpstreamButNotLocally(prNumber, probeCode switch
            {
                0 => $"the local merge failed even though '{request.MainBranch}' DOES fast-forward onto "
                     + $"'{upstreamRef}' — so this is not a divergence. git said: {detail}",
                1 => $"'{request.MainBranch}' doesn't fast-forward onto '{upstreamRef}' ({detail})",
                _ => $"the local merge failed and the cause could not be established (the fast-forward "
                     + $"probe itself exited {probeCode}). git said: {detail}",
            });
        }

        var newMainSha = RevParse(request.RepoPath, "--verify", request.MainBranch);
        if (newMainSha.Length == 0)
        {
            return MergedUpstreamButNotLocally(prNumber, $"couldn't read '{request.MainBranch}' after the merge");
        }

        // Belt and braces: main must now contain the merge commit and must have actually moved. A
        // fast-forward that lands on the sha we started from is a no-op, and a no-op is not a merge.
        var (containsCode, _, _) = GitService.RunGit(
            request.RepoPath, "merge-base", "--is-ancestor", mergeCommitSha, newMainSha);
        if (containsCode != 0 || string.Equals(newMainSha, request.ExpectedMainSha, StringComparison.Ordinal))
        {
            return MergedUpstreamButNotLocally(
                prNumber, $"'{request.MainBranch}' did not advance onto the merge commit");
        }

        return new ForegroundMergeResult(true, newMainSha, CasLost: false, Reason: null);
    }

    /// <summary>
    /// The one genuinely awkward outcome: the pull request IS merged on the host, but this checkout could
    /// not converge onto it. Nothing is recorded — the queue may only claim a merge it can point at a
    /// commit for — so the wording has to make clear that the work is not lost, only unreflected here. The
    /// entry clears on the intake's next poll (the PR is no longer open), and main moving fires the stale
    /// cascade normally, so the system converges without the queue ever asserting something false.
    /// </summary>
    private static ForegroundMergeResult MergedUpstreamButNotLocally(int prNumber, string detail) =>
        Refuse(
            $"pull request #{prNumber} was merged upstream, but this checkout couldn't be brought up to "
            + $"date with it ({detail}) — nothing was recorded; fetch and update your repository");

    // ---- host failure vocabulary --------------------------------------------------------------------

    /// <summary>
    /// Turns a host failure into one sentence naming what the human has to do about it. Branches on the
    /// HTTP status the transport preserved rather than on the host's prose, because the same English can
    /// come back for causes needing opposite responses.
    /// </summary>
    private static string DescribeHostFailure(Exception ex, int prNumber, string what) => ex switch
    {
        AuthenticationRequiredException =>
            $"the host rejected the stored sign-in while trying to {what} pull request #{prNumber} — "
            + "sign in again, then merge",

        GitOperationException { HostUnreachable: true } =>
            $"couldn't reach the host to {what} pull request #{prNumber} — check your connection and "
            + "try again; nothing was merged",

        GitOperationException { HostStatusCode: 403 } h
            when h.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) =>
            $"the host's API rate limit was reached while trying to {what} pull request #{prNumber} — "
            + "wait a few minutes and try again; nothing was merged",

        GitOperationException { HostStatusCode: 401 or 403 } =>
            $"your host sign-in isn't permitted to {what} pull request #{prNumber} — a token with write "
            + "access to this repository is required",

        GitOperationException { HostStatusCode: 404 } =>
            $"the host has no pull request #{prNumber} for this repository, or your sign-in can't see it",

        GitOperationException { HostStatusCode: 409 } =>
            $"verification is stale — pull request #{prNumber} changed upstream while it was being "
            + "merged; re-verifying",

        GitOperationException { HostStatusCode: 405 } h =>
            $"the host refused to merge pull request #{prNumber}: {FirstLine(h.Message)}",

        GitOperationException { HostStatusCode: >= 500 } =>
            $"the host had a server error while trying to {what} pull request #{prNumber} — try again "
            + "shortly; nothing was merged",

        _ => $"the host refused to {what} pull request #{prNumber}: {FirstLine(ex.Message)}",
    };

    /// <summary>
    /// True for host failures that are a lost race rather than a standing condition — 409 (the head moved
    /// under the merge) and 405 (it stopped being mergeable). Reported as <c>CasLost</c> so the branch
    /// re-verifies against what upstream actually is now, exactly as an <c>--ff-only</c> refusal does.
    /// </summary>
    private static bool IsLostRace(Exception ex) =>
        ex is GitOperationException { HostStatusCode: 405 or 409 };

    // ---- git helpers (mirrors of the local leg's, deliberately) -------------------------------------

    /// <summary>
    /// The remote the pull request lives on: <c>origin</c> when present, else the sole remaining remote
    /// that is not the daemon's sync mirror. Same "origin, else the obvious one" rule
    /// <see cref="Mainguard.Git.Services.IPullRequestService"/> resolves the host and token with, so the
    /// merge fetches from the same place the API call talked to.
    /// </summary>
    private static string? ResolveHostRemote(string repoPath, string syncRemoteName)
    {
        var (code, output, _) = GitService.RunGit(repoPath, "remote");
        if (code != 0)
        {
            return null;
        }

        string? sole = null;
        var many = false;
        foreach (var line in output.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length == 0 || string.Equals(name, syncRemoteName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name, "origin", StringComparison.Ordinal))
            {
                return name;
            }

            if (sole is null) { sole = name; } else { many = true; }
        }

        // Ambiguous (two non-origin remotes) is refused rather than guessed: fetching the wrong one would
        // reconcile main against a repository the pull request was never merged into.
        return many ? null : sole;
    }

    /// <summary>
    /// Whether the recorded verified head is actually in this checkout, under either spelling of
    /// <c>agent/pr-&lt;n&gt;</c>. This asks "is the commit the queue verified HERE" — it deliberately does
    /// not compute a verified head, which is the K4 defect.
    /// </summary>
    private static bool IsPresent(string repoPath, string branch, string syncRemoteName, string expectedHead)
    {
        var local = RevParse(repoPath, "--verify", "--quiet", $"refs/heads/{branch}");
        if (string.Equals(local, expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tracking = RevParse(repoPath, "--verify", "--quiet", $"refs/remotes/{syncRemoteName}/{branch}");
        return string.Equals(tracking, expectedHead, StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static ForegroundMergeResult Refuse(string reason, bool CasLost = false) =>
        new(false, null, CasLost, reason);

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no detail reported";
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
        return newline < 0 ? trimmed : trimmed[..newline];
    }

    private static string RevParse(string repoPath, params string[] args)
    {
        var full = new string[args.Length + 1];
        full[0] = "rev-parse";
        Array.Copy(args, 0, full, 1, args.Length);
        var (code, output, _) = GitService.RunGit(repoPath, full);
        return code == 0 ? output.Trim() : string.Empty;
    }
}
