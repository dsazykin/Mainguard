using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// F40 — the non-destructive half of <see cref="IPrHeadFetcher"/>: "what is the PR head right now?",
/// answered without touching the worker's worktree at all.
///
/// <para><b>Why this is a separate capability interface and not a method on
/// <see cref="IPrHeadFetcher"/>.</b> The intake seam is implemented by test doubles and by the
/// production fetcher alike, and the ordering fix must not turn every double into a compile error —
/// a double that does not offer the peek simply falls back to the old behaviour, which is still
/// correct, just less careful. The poll loop asks for the capability and uses it when it is there.</para>
/// </summary>
public interface IPrHeadPeek
{
    /// <summary>
    /// The PR head SHA as the HOST reports it, read straight off the remote (<c>ls-remote</c>) — no
    /// fetch, no worktree, no index, no <c>.git</c> of the worker's touched. Null when the ref is not
    /// advertised (a closed/deleted head), which the caller must treat as "unknown", never as "moved".
    /// </summary>
    Task<string?> PeekRemoteHeadAsync(
        ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct);
}

/// <summary>
/// The production <see cref="IPrHeadFetcher"/> (P2-12 step 2). Fetches <c>pull/&lt;n&gt;/head</c> from the
/// source host directly into the agent worktree and hard-resets the <c>agent/&lt;id&gt;</c> branch to it,
/// then returns the resulting head SHA. This is the daemon provisioning-plane fetch: it targets the real
/// host by explicit URL (not the worktree's quarantine <c>origin</c>, which points only at the bare
/// mirror), and it runs entirely over the ONE shared git primitive (<see cref="AgentGitCommand"/> →
/// <see cref="GitService.RunGit"/>) — there is no HTTP transport here (host API traffic stays in T-23).
///
/// <para><b>W1-A.</b> Both legs run as the daemon user on the host, in a directory the jailed worker can
/// write. So the layout is resolved from daemon-computed roots and pinned
/// (<see cref="TrustedWorktreeLayout"/>) rather than discovered from the worker's own <c>.git</c> pointer
/// file, and <see cref="AgentGitCommand"/>'s config neutralization applies to every call.</para>
///
/// <para><b>F40.</b> <c>reset --hard</c> is destructive and used to run on <i>every</i> poll, before
/// anything had compared the head — so an unchanged PR still had the worker's uncommitted work deleted
/// every interval, and the reset raced the worker's own git on <c>.git/index.lock</c>. Two changes:
/// <see cref="IPrHeadPeek"/> lets the caller decide "changed?" without touching the worktree, and the
/// destructive leg now waits out the worker's <c>index.lock</c> on the shared
/// <see cref="GitMutationGuard"/> backoff and refuses (typed) rather than colliding with it.</para>
///
/// <para><b>NOT closed on this path: the config-snapshot race.</b>
/// <see cref="AgentGitCommand"/>'s neutralization is a snapshot taken microseconds before the spawn. On
/// the keep-alive path that window is shut by construction — the agent is yielded, its jail paused, for
/// the whole cycle. <b>Here it is not.</b> This fetcher is a peer of the worker, not its arbiter: it
/// holds no yield token (see <see cref="WaitForWorktreeLock"/>), so the worker's jail is LIVE while the
/// fetch and the <c>reset --hard</c> run, and <c>reset --hard</c> runs smudge filters. Worse, the
/// trigger is the attacker's: the PR author pushes, the poll fetches, so an external author can drive
/// this leg on demand while flipping <c>filter.*.smudge</c> in a loop from their own jail, and win the
/// race on some iteration. Closing it means giving the intake a yield relationship with the worker
/// (pause the jail for the reset), which is a change to who arbitrates a worker and is deliberately not
/// made here. Until it is, treat the external-PR worker path as HARDENED BUT NOT CLOSED against a
/// repo-local driver: the ordinary case is neutralized, a determined racer is not.</para>
/// </summary>
public sealed class PrHeadFetcher : IPrHeadFetcher, IPrHeadPeek
{
    private readonly Func<string, string, string> _resolveWorktreePath;
    private readonly Func<string, string, string?>? _resolveAgentRepoPath;
    private readonly Func<string, string?>? _resolveMirrorPath;
    private readonly Func<ExternalPrSource, string> _hostUrl;

    /// <param name="resolveWorktreePath">Maps (repoHash, agentId) → the agent's worktree path
    /// (e.g. <see cref="WorktreeManager.WorktreePathFor"/>).</param>
    /// <param name="hostUrl">
    /// Maps a source to the URL the PR head is fetched from; defaults to the host's HTTPS clone URL.
    /// Overridable so the end-to-end suite can point the REAL fetcher at a local fixture host instead of
    /// substituting a fetcher of its own — the fetch/reset/rev-parse mechanics under test stay production
    /// code and only the origin changes.
    /// </param>
    /// <param name="resolveAgentRepoPath">
    /// W1-A — maps (repoHash, agentId) → the daemon-computed per-agent repository
    /// (<see cref="AgentRepoLayout.AgentRepoPath"/>). Optional: supplied, the worker's <c>.git</c> pointer
    /// can no longer decide WHICH repository the daemon's fetch and hard reset land in (a mismatch is a
    /// typed refusal); omitted, the repository is still derived from the pointer's own
    /// <c>…/worktrees/&lt;name&gt;</c> shape and round-trip-checked, which is strictly better than
    /// following it blindly but does not pin identity.
    /// </param>
    /// <param name="resolveMirrorPath">
    /// W1-A rework — maps repoHash → the daemon-owned shared mirror, used as the working directory of the
    /// non-destructive <see cref="PeekRemoteHeadAsync"/>. See that method for why the worker's own
    /// worktree was the wrong place to ask a question about a remote. Optional: without it the peek falls
    /// back to the worktree's PARENT directory, which is daemon-owned and is not a repository.
    /// </param>
    public PrHeadFetcher(
        Func<string, string, string> resolveWorktreePath,
        Func<ExternalPrSource, string>? hostUrl = null,
        Func<string, string, string?>? resolveAgentRepoPath = null,
        Func<string, string?>? resolveMirrorPath = null)
    {
        _resolveWorktreePath = resolveWorktreePath ?? throw new ArgumentNullException(nameof(resolveWorktreePath));
        _hostUrl = hostUrl ?? HttpsCloneUrl;
        _resolveAgentRepoPath = resolveAgentRepoPath;
        _resolveMirrorPath = resolveMirrorPath;
    }

    public Task<string?> PeekRemoteHeadAsync(
        ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
    {
        var fetchUrl = _hostUrl(source);
        var headRef = HeadRefFor(source, prNumber);

        return Task.Run<string?>(() =>
        {
            // ls-remote is a pure remote query: it opens no repository object store, writes no ref, takes
            // no lock. It also needs no repository at all — which is the W1-A rework here.
            //
            // This used to run with the WORKER'S WORKTREE as its working directory, and that quietly made
            // the answer agent-controlled. The worker on an external PR is a third party's code, its
            // repository config is writable from its jail, and `url.<x>.insteadOf` rewrites the URL git
            // actually contacts. So one config key redirected "has the PR head moved?" to a remote of the
            // agent's choosing, which could answer "the same SHA as last time" forever — pinning the
            // intake at unchanged and stopping the daemon from ever noticing a real force-push. A question
            // about a REMOTE has no business being asked from inside the thing it is asked about.
            var worktreePath = _resolveWorktreePath(repoHash, agentId);
            if (AgentGitCommand.TryRun(PeekDirectory(repoHash, worktreePath), out var output, "ls-remote", fetchUrl, headRef) != 0)
            {
                return null;
            }

            // "<sha>\t<ref>" — first line, first field. An empty listing means the host does not advertise
            // the ref (closed, deleted, or never existed): unknown, not moved.
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (line.Length == 0)
            {
                return null;
            }

            var sha = line[0].Split('\t', ' ')[0].Trim();
            return sha.Length == 0 ? null : sha;
        }, ct);
    }

    public Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
    {
        var worktreePath = _resolveWorktreePath(repoHash, agentId);
        var fetchUrl = _hostUrl(source);
        var headRef = HeadRefFor(source, prNumber);
        var agentRepoPath = _resolveAgentRepoPath?.Invoke(repoHash, agentId);

        return Task.Run(() =>
        {
            var layout = TrustedWorktreeLayout.TryResolve(worktreePath, agentRepoPath);
            var dir = layout?.WorkTree ?? worktreePath;
            var env = layout?.Env;

            // TODO(P2-12 human-review): the live-credential slice — a private-repo fetch must inject the
            // stored host token via git's credential env (never argv/URL), mirroring the T-29 checklist.
            // The fetch + reset mechanics are exercised offline over a file:// fixture remote in the tests.
            AgentGitCommand.RunWithEnv(dir, env, "fetch", fetchUrl, $"+{headRef}");

            // F40 — the reset is the destructive leg and the worker's git runs concurrently in the jail.
            // Wait the worker's index.lock out on the SAME backoff every other daemon mutation uses, and
            // refuse rather than race it. Refusing costs one poll interval (the caller audits it and the
            // next cycle retries); forcing it costs the worker a corrupted index.
            WaitForWorktreeLock(worktreePath);
            AgentGitCommand.RunWithEnv(dir, env, "reset", "--hard", "FETCH_HEAD");
            return AgentGitCommand.RunWithEnv(dir, env, "rev-parse", "HEAD").Trim();
        }, ct);
    }

    // The host KIND is always classified from the canonical host name, never from the (possibly
    // overridden) fetch URL: which ref names a pull request head is a property of the host, and a
    // local fixture URL must not silently reclassify it.
    private static string HeadRefFor(ExternalPrSource source, int prNumber)
    {
        var (_, kind) = GitHostDetector.Detect(HttpsCloneUrl(source));
        return GitService.PullRequestHeadRef(kind, prNumber); // throws typed for an unsupported host
    }

    /// <summary>
    /// Blocks until the worker's <c>index.lock</c> is clear, on <see cref="GitMutationGuard"/>'s shared
    /// backoff schedule, or refuses. Deliberately NOT <c>RunGuarded</c>: that requires a yield token, and
    /// the intake has no yield relationship with the worker — it is a peer, not the arbiter. The lock
    /// wait is the part that applies; the token invariant is not this caller's to claim.
    /// </summary>
    private static void WaitForWorktreeLock(string worktreePath)
    {
        var delays = GitMutationGuard.BackoffDelays();
        for (var attempt = 0; attempt < delays.Count; attempt++)
        {
            if (!GitMutationGuard.IsIndexLockHeld(worktreePath))
            {
                return;
            }

            Thread.Sleep(delays[attempt]);
        }

        if (GitMutationGuard.IsIndexLockHeld(worktreePath))
        {
            throw new RepoProvisioningException(
                $"F40: the external-PR worker's index.lock at '{worktreePath}' stayed held across "
                + $"{delays.Count} attempts; refusing to hard-reset its worktree underneath it. The next "
                + "poll retries.");
        }
    }

    /// <summary>
    /// Where the peek's <c>ls-remote</c> runs: never a directory the agent can write.
    ///
    /// <para>First choice is the shared MIRROR — daemon-owned, mounted read-only into every jail since
    /// MG-3, so its config cannot be rewritten from inside one. Failing that, the worktree's PARENT
    /// (<c>&lt;vmRoot&gt;/worktrees/&lt;repoHash&gt;</c>), which is daemon-owned, is not mounted into any
    /// jail, and is not a repository — <c>ls-remote</c> does not need one. A relative fixture URL still
    /// resolves, because every URL this is given is absolute (an <c>https://</c> clone URL, or the E2E
    /// suite's absolute fixture path).</para>
    /// </summary>
    private string PeekDirectory(string repoHash, string worktreePath)
    {
        if (_resolveMirrorPath?.Invoke(repoHash) is { Length: > 0 } mirror && Directory.Exists(mirror))
        {
            return mirror;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(worktreePath));
        return parent is { Length: > 0 } && Directory.Exists(parent) ? parent : worktreePath;
    }

    private static string HttpsCloneUrl(ExternalPrSource source) =>
        $"https://{source.Host}/{source.Owner}/{source.Repo}.git";
}
