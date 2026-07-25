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
public sealed class ForegroundMergeService : IForegroundMergeService
{
    private readonly IAgentEnvironment _environment;
    private readonly IOperationJournal _journal;
    private readonly IMergeLeaseStore _leases;
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
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _canMerge = canMerge;
        _onMerged = onMerged;
        _onStaleOverride = onStaleOverride;
        _depsRefreshRunner = depsRefreshRunner ?? DefaultDepsRefreshRunner;
    }

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
                _leases.Release(request.RepoHash, lease.LeaseId);
                return new ForegroundMergeResult(false, null, CasLost: false, gateReason);
            }

            var result = PerformJournaledMerge(request, lease);
            if (result.Merged && result.NewMainSha is not null)
            {
                ConfirmMerge(request.RepoHash, lease, result.NewMainSha);
            }
            else
            {
                _leases.Release(request.RepoHash, lease.LeaseId);
            }

            return result;
        }
        catch
        {
            _leases.Release(request.RepoHash, lease.LeaseId);
            throw;
        }
    }

    /// <summary>RT-D1 step 1: take the per-repo merge lease (null if one is already outstanding).</summary>
    public MergeLeaseRow? BeginMerge(ForegroundMergeRequest request)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        return _leases.TryBegin(request.RepoHash, leaseId, request.AgentId, request.ExpectedMainSha, request.MainBranch);
    }

    /// <summary>
    /// RT-D1 step 2: fetch the SC-2 sync remote, then merge under the A5 ref CAS (journaled via T-19).
    /// Does NOT confirm the lease — the RT-D1 test can "crash" here and let the boot reconcile finish.
    /// </summary>
    public ForegroundMergeResult PerformJournaledMerge(ForegroundMergeRequest request, MergeLeaseRow lease)
    {
        // SC-2: the sync remote name is always resolved, never a hardcoded "mainguard-vm" literal.
        var syncRemote = _environment.ResolveSyncRemote(request.RepoHash);
        GitService.RunGit(request.RepoPath, "fetch", syncRemote.Name);

        // Ensure HEAD is on the main branch so the ff-only merge advances refs/heads/main.
        var currentBranch = RevParse(request.RepoPath, "--abbrev-ref", "HEAD");
        if (!string.Equals(currentBranch, request.MainBranch, StringComparison.Ordinal))
        {
            GitService.RunGit(request.RepoPath, "checkout", request.MainBranch);
        }

        // Freshness pre-check (fast path). The ff-only merge below is the atomic CAS regardless.
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
        using (_journal.BeginOperation(request.RepoPath, JournalKinds.Merge, $"Merge agent/{request.AgentId}"))
        {
            var (code, _, _) = GitService.RunGit(request.RepoPath, "merge", "--ff-only", $"agent/{request.AgentId}");
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
        _leases.Confirm(repoHash, lease.LeaseId, newMainSha);
        _onMerged?.Invoke(lease.AgentId, newMainSha);
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
