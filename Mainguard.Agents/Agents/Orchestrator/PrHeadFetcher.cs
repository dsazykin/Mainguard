using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Security;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The production <see cref="IPrHeadFetcher"/> (P2-12 step 2). Fetches <c>pull/&lt;n&gt;/head</c> from the
/// source host directly into the agent worktree and hard-resets the <c>agent/&lt;id&gt;</c> branch to it,
/// then returns the resulting head SHA. This is the daemon provisioning-plane fetch: it targets the real
/// host by explicit URL (not the worktree's quarantine <c>origin</c>, which points only at the bare
/// mirror), and it runs entirely over the ONE shared git primitive (<see cref="AgentGitCommand"/> →
/// <see cref="GitService.RunGit"/>) — there is no HTTP transport here (host API traffic stays in T-23).
/// </summary>
public sealed class PrHeadFetcher : IPrHeadFetcher
{
    private readonly Func<string, string, string> _resolveWorktreePath;
    private readonly Func<ExternalPrSource, string> _hostUrl;

    /// <param name="resolveWorktreePath">Maps (repoHash, agentId) → the agent's worktree path
    /// (e.g. <see cref="WorktreeManager.WorktreePathFor"/>).</param>
    /// <param name="hostUrl">
    /// Maps a source to the URL the PR head is fetched from; defaults to the host's HTTPS clone URL.
    /// Overridable so the end-to-end suite can point the REAL fetcher at a local fixture host instead of
    /// substituting a fetcher of its own — the fetch/reset/rev-parse mechanics under test stay production
    /// code and only the origin changes.
    /// </param>
    public PrHeadFetcher(
        Func<string, string, string> resolveWorktreePath,
        Func<ExternalPrSource, string>? hostUrl = null)
    {
        _resolveWorktreePath = resolveWorktreePath ?? throw new ArgumentNullException(nameof(resolveWorktreePath));
        _hostUrl = hostUrl ?? HttpsCloneUrl;
    }

    public Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
    {
        var worktreePath = _resolveWorktreePath(repoHash, agentId);
        var fetchUrl = _hostUrl(source);
        // The host KIND is always classified from the canonical host name, never from the (possibly
        // overridden) fetch URL: which ref names a pull request head is a property of the host, and a
        // local fixture URL must not silently reclassify it.
        var (_, kind) = GitHostDetector.Detect(HttpsCloneUrl(source));
        var headRef = GitService.PullRequestHeadRef(kind, prNumber); // throws typed for an unsupported host

        return Task.Run(() =>
        {
            // TODO(P2-12 human-review): the live-credential slice — a private-repo fetch must inject the
            // stored host token via git's credential env (never argv/URL), mirroring the T-29 checklist.
            // The fetch + reset mechanics are exercised offline over a file:// fixture remote in the tests.
            AgentGitCommand.Run(worktreePath, "fetch", fetchUrl, $"+{headRef}");
            AgentGitCommand.Run(worktreePath, "reset", "--hard", "FETCH_HEAD");
            return AgentGitCommand.Run(worktreePath, "rev-parse", "HEAD").Trim();
        }, ct);
    }

    private static string HttpsCloneUrl(ExternalPrSource source) =>
        $"https://{source.Host}/{source.Owner}/{source.Repo}.git";
}
