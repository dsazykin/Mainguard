using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Services;

/// <summary>
/// The merge-gate probe the foreground merge consults before it touches a ref — shaped exactly like
/// <see cref="IMergeQueue.CanMerge"/> so the daemon can hand it the live queue's method and nothing
/// re-implements the gate. MG-11: without this the Windows-side merge ran with no gate at all, so
/// staleness and every unacknowledged flagged item were enforced only by whichever UI happened to call it.
/// </summary>
public delegate bool MergeGateCheck(string agentId, out string reason);

/// <summary>
/// The Windows-side human-gated foreground merge (P2-10 §3.5). See <see cref="IForegroundMergeService"/>.
///
/// <para><b>A5 freshness is a ref-level compare-and-swap on <c>refs/heads/main</c>.</b> Because P2-09
/// keep-alive-rebases every agent branch onto the exact main it was verified against, a verified branch
/// is a fast-forward of main — so <c>git merge --ff-only agent/&lt;id&gt;</c> is BOTH the merge and the
/// atomic CAS: git advances <c>refs/heads/main</c> only if main is still an ancestor. If anything moved
/// main between the freshness read and the merge, <c>--ff-only</c> refuses (the CAS loses), no merge
/// happens, and the branch is re-verified. This is deliberately NOT an <c>index.lock</c>-scoped
/// read-then-merge: <c>index.lock</c> guards the index, not ref updates (<c>update-ref</c>/push/fetch can
/// move main without it), so only a ref-level CAS closes the TOCTOU (OPS §6.5).</para>
///
/// <para><b>T-19 journal reuse.</b> The merge runs inside one <see cref="IOperationJournal"/> operation
/// (the single undo journal — not a second implementation), so a bad merge is undoable and the RT-D1
/// boot reconcile can replay it.</para>
/// </summary>
public sealed class ForegroundMergeService : IForegroundMergeService, IJournaledMergeExecutor
{
    private readonly Func<string, SyncRemote> _resolveSyncRemote;
    private readonly IOperationJournal _journal;
    private readonly IMergeLeaseStore? _leases;
    private readonly MergeGateCheck? _canMerge;
    private readonly Action<string, string>? _onMerged;
    private readonly Action<string, string>? _onStaleOverride;
    private readonly Func<string, IReadOnlyList<string>, int> _depsRefreshRunner;

    /// <param name="environment">Substrate facade — resolves the SC-2 sync remote name (never a literal).</param>
    /// <param name="journal">The T-19 operation journal (the merge is one undoable op).</param>
    /// <param name="leases">The RT-D1 merge-lease store.</param>
    /// <param name="canMerge">The live queue's merge gate, evaluated UNDER the lease. Null leaves the merge
    /// ungated — which is only ever correct for a caller that has no queue at all.</param>
    /// <param name="onMerged">Fired after a confirmed merge: (agentId, newMainSha) → daemon <c>ConfirmHumanMerge</c>/<c>NotifyMainMoved</c>.</param>
    /// <param name="onStaleOverride">Fired when the loud override path is used: (agentId, reason) → <c>stale_override_used</c> audit.</param>
    /// <param name="depsRefreshRunner">Runs the post-merge dependency refresh (workingDir, args) → exit; default uses the package manager.</param>
    public ForegroundMergeService(
        IAgentEnvironment environment,
        IOperationJournal journal,
        IMergeLeaseStore leases,
        MergeGateCheck? canMerge = null,
        Action<string, string>? onMerged = null,
        Action<string, string>? onStaleOverride = null,
        Func<string, IReadOnlyList<string>, int>? depsRefreshRunner = null)
        : this(
            (environment ?? throw new ArgumentNullException(nameof(environment))).ResolveSyncRemote,
            journal,
            leases ?? throw new ArgumentNullException(nameof(leases)),
            canMerge, onMerged, onStaleOverride, depsRefreshRunner)
    {
    }

    /// <summary>
    /// The substrate-free constructor: the caller supplies the SC-2 sync-remote resolution directly
    /// (repoHash → <see cref="SyncRemote"/>) instead of the whole daemon-side <see cref="IAgentEnvironment"/>.
    /// This is what lets the <b>Windows GUI</b> run the human merge leg — it holds the sync-remote binding
    /// verbatim from <c>ProvisionRepo</c> and has no substrate facade (ESC-I2), and the merge needs nothing
    /// else from it.
    /// </summary>
    /// <param name="resolveSyncRemote">repoHash → the ONE host-side sync remote (SC-2; never a literal).</param>
    /// <param name="journal">The T-19 operation journal (the merge is one undoable op).</param>
    /// <param name="leases">
    /// The RT-D1 merge-lease store — required only by <see cref="MergeAgentBranch"/>, which drives the whole
    /// begin → merge → confirm conversation itself.
    /// <para><b>Pass null when the caller already holds the repo's lease</b> and only drives
    /// <see cref="PerformJournaledMerge"/> — the Windows-side leg of the RT-D1 conversation, whose lease was
    /// taken over the daemon's <c>BeginMerge</c> RPC. <b>Never hand this parameter a second store just to
    /// satisfy it:</b> one-outstanding-merge-per-repo is only an invariant while every origin contends for
    /// the SAME store, and that store is the daemon's singleton (MG-23).</para>
    /// </param>
    /// <param name="canMerge">The live queue's merge gate, evaluated UNDER the lease. Null leaves this leg
    /// ungated — correct only for a caller that has no local queue, i.e. one whose gate was already
    /// enforced daemon-side (<c>BeginMerge</c> refuses an ungated branch before it ever grants a lease).</param>
    /// <param name="onMerged">Fired after a confirmed merge: (agentId, newMainSha) → daemon <c>ConfirmHumanMerge</c>/<c>NotifyMainMoved</c>.</param>
    /// <param name="onStaleOverride">Fired when the loud override path is used: (agentId, reason) → <c>stale_override_used</c> audit.</param>
    /// <param name="depsRefreshRunner">Runs the post-merge dependency refresh (workingDir, args) → exit; default uses the package manager.</param>
    public ForegroundMergeService(
        Func<string, SyncRemote> resolveSyncRemote,
        IOperationJournal journal,
        IMergeLeaseStore? leases,
        MergeGateCheck? canMerge = null,
        Action<string, string>? onMerged = null,
        Action<string, string>? onStaleOverride = null,
        Func<string, IReadOnlyList<string>, int>? depsRefreshRunner = null)
    {
        _resolveSyncRemote = resolveSyncRemote ?? throw new ArgumentNullException(nameof(resolveSyncRemote));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _leases = leases;
        _canMerge = canMerge;
        _onMerged = onMerged;
        _onStaleOverride = onStaleOverride;
        _depsRefreshRunner = depsRefreshRunner ?? DefaultDepsRefreshRunner;
    }

    // The lease store is optional ONLY for the PerformJournaledMerge-only caller (see the ctor doc). Any
    // lease-owning entry point that reaches this on a null store is a composition error, not a merge refusal.
    private IMergeLeaseStore Leases => _leases ?? throw new InvalidOperationException(
        "This ForegroundMergeService was constructed without a merge-lease store: it can only run "
        + "PerformJournaledMerge under a lease its caller already holds (the RT-D1 two-step daemon "
        + "conversation). Use the daemon-side instance for the full begin → merge → confirm flow.");

    public ForegroundMergeResult MergeAgentBranch(ForegroundMergeRequest request)
    {
        var lease = BeginMerge(request);
        if (lease is null)
        {
            return new ForegroundMergeResult(false, null, CasLost: false,
                "another merge is already in progress for this repository");
        }

        try
        {
            // MG-11: the gate is read UNDER the lease, never before it — a gate read outside the lease is
            // only a snapshot of a repo somebody else may still be merging into (the MG-23 ordering the
            // external dispatch already uses). The loud stale-override path deliberately bypasses it: the
            // override IS the documented, audited "CanMerge is false and the human accepted it" route, and
            // routing it through the gate would make it unreachable.
            if (!request.AllowStaleOverride && _canMerge is not null
                && !_canMerge(request.AgentId, out var gateReason))
            {
                Leases.Release(request.RepoHash, lease.LeaseId);
                return new ForegroundMergeResult(false, null, CasLost: false, gateReason);
            }

            var result = PerformJournaledMerge(request, lease);
            if (result.Merged && result.NewMainSha is not null)
            {
                ConfirmMerge(request.RepoHash, lease, result.NewMainSha);
            }
            else
            {
                Leases.Release(request.RepoHash, lease.LeaseId);
            }

            return result;
        }
        catch
        {
            Leases.Release(request.RepoHash, lease.LeaseId);
            throw;
        }
    }

    /// <summary>RT-D1 step 1: take the per-repo merge lease (null if one is already outstanding).</summary>
    public MergeLeaseRow? BeginMerge(ForegroundMergeRequest request)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        return Leases.TryBegin(request.RepoHash, leaseId, request.AgentId, request.ExpectedMainSha, request.MainBranch);
    }

    /// <summary>
    /// RT-D1 step 2: fetch the SC-2 sync remote, then merge under the A5 ref CAS (journaled via T-19).
    /// Does NOT confirm the lease — the RT-D1 test can "crash" here and let the boot reconcile finish.
    ///
    /// <para><b>Every precondition failure returns a reason; none of them is a silent no-op.</b> Before
    /// this was reachable from the GUI the preconditions were merely <i>assumed</i>: the fetch and the
    /// <c>checkout</c> exit codes were discarded, so a failed fetch merged a stale copy of the branch and a
    /// checkout refused by a dirty tree left HEAD on some OTHER branch which <c>--ff-only</c> would then
    /// happily advance. A merge that lands on the wrong ref is worse than a merge that refuses.</para>
    /// </summary>
    public ForegroundMergeResult PerformJournaledMerge(ForegroundMergeRequest request, MergeLeaseRow lease)
    {
        // (1) A dirty working tree cannot be merged into: `checkout`/`merge` would refuse (or, worse, be
        // refused AFTER we thought we had switched branches). Untracked files are deliberately tolerated —
        // they never block a checkout or a fast-forward, and refusing on them would block honest merges.
        var (statusCode, dirt, _) = GitService.RunGit(
            request.RepoPath, "status", "--porcelain", "--untracked-files=no");
        if (statusCode != 0)
        {
            return new ForegroundMergeResult(false, null, CasLost: false,
                "couldn't read the repository status — is this still a git repository?");
        }

        if (dirt.Trim().Length > 0)
        {
            return new ForegroundMergeResult(false, null, CasLost: false,
                "the working tree has uncommitted changes — commit or stash them, then merge");
        }

        // (2) SC-2: the sync remote name is always resolved, never a hardcoded "mainguard-vm" literal.
        // A failed fetch is fatal, not cosmetic: whatever agent/<id> happens to be in this repo is then
        // an unknown-age copy, and merging it would land work the queue never verified.
        var syncRemote = _resolveSyncRemote(request.RepoHash);
        var (fetchCode, _, fetchErr) = GitService.RunGit(request.RepoPath, "fetch", syncRemote.Name);
        if (fetchCode != 0)
        {
            return new ForegroundMergeResult(false, null, CasLost: false,
                $"couldn't fetch '{syncRemote.Name}' — the agent branch can't be verified as current ({FirstLine(fetchErr)})");
        }

        // (3) Ensure HEAD is on the main branch so the ff-only merge advances refs/heads/main.
        var currentBranch = RevParse(request.RepoPath, "--abbrev-ref", "HEAD");
        if (!string.Equals(currentBranch, request.MainBranch, StringComparison.Ordinal))
        {
            var (checkoutCode, _, checkoutErr) = GitService.RunGit(request.RepoPath, "checkout", request.MainBranch);
            if (checkoutCode != 0
                || !string.Equals(
                    RevParse(request.RepoPath, "--abbrev-ref", "HEAD"), request.MainBranch, StringComparison.Ordinal))
            {
                return new ForegroundMergeResult(false, null, CasLost: false,
                    $"couldn't switch to '{request.MainBranch}' to merge into ({FirstLine(checkoutErr)})");
            }
        }

        // (4) Resolve what the merge will consume. The queue's input is refs/heads/agent/<id> in the mirror;
        // in THIS repo that commit is reachable either as a local branch or — with the sync remote's default
        // refspec — only as refs/remotes/<sync>/agent/<id>. `git merge agent/<id>` does NOT fall back to the
        // remote-tracking form, so naming it unconditionally fails with "not something we can merge" on a
        // repo that has never checked the branch out. Same commit either way; only the spelling differs.
        var branch = $"agent/{request.AgentId}";
        var mergeSource = ResolveMergeSource(request.RepoPath, branch, syncRemote.Name);
        if (mergeSource is null)
        {
            return new ForegroundMergeResult(false, null, CasLost: false,
                $"'{branch}' isn't in this repository yet — the agent hasn't pushed it to '{syncRemote.Name}'");
        }

        // (5) Freshness pre-check (fast path). The ff-only merge below is the atomic CAS regardless.
        var mainSha = RevParse(request.RepoPath, "--verify", request.MainBranch);
        var stale = !string.Equals(mainSha, request.ExpectedMainSha, StringComparison.Ordinal);
        if (stale && !request.AllowStaleOverride)
        {
            return new ForegroundMergeResult(false, null, CasLost: true,
                "verification is stale — main moved; re-verifying");
        }

        if (stale && request.AllowStaleOverride)
        {
            // The loud, separate override path (P2-10 step 4): journaled by the merge below + audited here.
            _onStaleOverride?.Invoke(request.AgentId, request.OverrideReason ?? "stale override");
        }

        var mergeExit = -1;
        // One journaled operation (T-19) — the merge is undoable and replayable by the RT-D1 reconcile.
        using (_journal.BeginOperation(request.RepoPath, JournalKinds.Merge, $"Merge {branch}"))
        {
            var (code, _, _) = GitService.RunGit(request.RepoPath, "merge", "--ff-only", mergeSource);
            mergeExit = code;
        }

        if (mergeExit != 0)
        {
            // The CAS lost: agent/<id> is no longer a fast-forward of main (main moved or the branch
            // was not rebased onto this main). No merge landed.
            return new ForegroundMergeResult(false, null, CasLost: true,
                "verification is stale — the branch no longer fast-forwards onto main; re-verifying");
        }

        var newMainSha = RevParse(request.RepoPath, "--verify", request.MainBranch);

        // Post-merge dependency refresh: always script-free, wrapped in NTFS EPERM/EBUSY retry.
        RunPostMergeDependencyRefresh(request.RepoPath);

        return new ForegroundMergeResult(true, newMainSha, CasLost: false, null);
    }

    /// <summary>RT-D1 step 3: record the idempotency outcome, release the lease, fire the stale cascade.</summary>
    public void ConfirmMerge(string repoHash, MergeLeaseRow lease, string newMainSha)
    {
        Leases.Confirm(repoHash, lease.LeaseId, newMainSha);
        _onMerged?.Invoke(lease.AgentId, newMainSha);
    }

    /// <summary>
    /// The ref this merge consumes: the local <c>refs/heads/agent/&lt;id&gt;</c> when the user's repo has
    /// one, else the sync remote's tracking form of the same mirror branch. Null when neither exists.
    /// </summary>
    private static string? ResolveMergeSource(string repoPath, string branch, string syncRemoteName)
    {
        if (RevParse(repoPath, "--verify", "--quiet", $"refs/heads/{branch}").Length > 0)
        {
            return branch;
        }

        var tracking = $"{syncRemoteName}/{branch}";
        return RevParse(repoPath, "--verify", "--quiet", $"refs/remotes/{tracking}").Length > 0 ? tracking : null;
    }

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

    // ---- Post-merge dependency refresh (script-free, always) --------------

    private void RunPostMergeDependencyRefresh(string repoPath)
    {
        var (manager, present) = DetectPackageManager(repoPath);
        if (!present)
        {
            return; // no lockfile — nothing to refresh.
        }

        // EVERY package-manager invocation is script-free: "--ignore-scripts" is always present, so a
        // poisoned dependency lifecycle hook in an agent branch never executes on the Windows host (the canary).
        var args = new List<string> { "install", "--ignore-scripts" };
        _ = manager; // the manager selects the binary in the runner; the args are identical + script-free.

        WithNtfsRetry(() => _depsRefreshRunner(repoPath, args));
    }

    private static (string Manager, bool Present) DetectPackageManager(string repoPath)
    {
        if (File.Exists(Path.Combine(repoPath, "pnpm-lock.yaml"))) return ("pnpm", true);
        if (File.Exists(Path.Combine(repoPath, "yarn.lock"))) return ("yarn", true);
        if (File.Exists(Path.Combine(repoPath, "package-lock.json")) ||
            File.Exists(Path.Combine(repoPath, "npm-shrinkwrap.json"))) return ("npm", true);
        return (string.Empty, false);
    }

    // Retries the NTFS-flaky file operation on EPERM/EBUSY (surfaced as IOException /
    // UnauthorizedAccessException on Windows) with a short backoff, then gives up (best-effort refresh).
    private static void WithNtfsRetry(Func<int> action)
    {
        var delays = new[] { 25, 50, 100 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < delays.Length)
            {
                Thread.Sleep(delays[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < delays.Length)
            {
                Thread.Sleep(delays[attempt]);
            }
            catch (Exception)
            {
                // The dependency refresh is best-effort and must never fail the merge.
                return;
            }
        }
    }

    private static int DefaultDepsRefreshRunner(string workingDir, IReadOnlyList<string> args)
    {
        // The daemon runs the package manager on the Windows host. Best-effort: a missing manager must
        // not fail the merge. The first arg selects the binary family; here we default to npm.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "npm",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0; // manager not on PATH — treat as a no-op refresh.
        }
    }
}
