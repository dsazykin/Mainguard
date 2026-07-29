using System;
using System.Collections.Generic;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Security;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Resolves a subscribed <see cref="ExternalPrSource"/> (<c>host/owner/repo</c>) to the daemon objects a
/// poll needs: the repo path the T-23 transport reads host, slug and token from, the repo hash keying the
/// mirror/worktrees, and that repo's live <see cref="MergeQueue"/>.
///
/// <para><b>This is the "hardwired null".</b> <c>RegisterPrIntake</c> passed
/// <c>resolveTarget: _ =&gt; null</c>, which makes every poll list-and-skip — so even with the poll loop
/// running and subscriptions persisted, the intake materialized nothing in production, ever. The
/// comment said a resolver was "deferred to the swarm-lifecycle wiring"; this is that wiring.</para>
///
/// <para><b>Matching is on the repository's own origin remote</b>, not on anything the subscription
/// asserts about itself: a source matches an active repo only when that repo's origin really is
/// <c>host/owner/repo</c>. So a subscription cannot point the intake at a repository it does not
/// describe — the daemon decides which repo a source belongs to by reading git, and an unmatched source
/// resolves to null and is skipped exactly as before.</para>
/// </summary>
public sealed class PrIntakeTargetResolver
{
    private readonly ActiveRepoIndex _repos;
    private readonly MergeQueueProvisioner _queues;
    private readonly Func<string, IReadOnlyList<GitRemoteItem>> _remotes;

    /// <param name="remotes">repoPath → its remotes. Injected so the matching rule is testable without a
    /// git repository on disk; production passes the shared <c>IGitService</c>.</param>
    public PrIntakeTargetResolver(
        ActiveRepoIndex repos,
        MergeQueueProvisioner queues,
        Func<string, IReadOnlyList<GitRemoteItem>> remotes)
    {
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _remotes = remotes ?? throw new ArgumentNullException(nameof(remotes));
    }

    /// <summary>The target for a source, or null when no active repo matches it (or the matched repo has
    /// no queue yet — an unprovisioned mirror). Null makes the poll list-and-skip, never crash.</summary>
    public PrIntakeTarget? Resolve(ExternalPrSource source)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var repo in _repos.Snapshot())
        {
            if (!Matches(source, repo.RepoPath))
            {
                continue;
            }

            // EnsureQueue rather than a registry read: a repo can be active in the index while its queue
            // has not been built yet, and building it is idempotent. Null means no provisioned mirror,
            // which is a "not yet", not an error.
            var queue = _queues.EnsureQueue(repo.Handle)?.Queue;
            if (queue is null)
            {
                continue;
            }

            return new PrIntakeTarget(repo.RepoPath, repo.Handle, queue);
        }

        return null;
    }

    /// <summary>
    /// True iff <paramref name="repoPath"/>'s origin remote really names this source's host + owner/repo.
    /// Case-insensitive (hosts and GitHub slugs are), and never throws — an unreadable repository is
    /// simply not a match.
    /// </summary>
    private bool Matches(ExternalPrSource source, string repoPath)
    {
        try
        {
            var remotes = _remotes(repoPath);
            if (remotes is null)
            {
                return false;
            }

            GitRemoteItem? origin = null;
            foreach (var remote in remotes)
            {
                if (string.Equals(remote.Name, "origin", StringComparison.Ordinal))
                {
                    origin = remote;
                    break;
                }

                origin ??= remote;
            }

            if (origin is null || string.IsNullOrWhiteSpace(origin.FetchUrl))
            {
                return false;
            }

            var (host, _) = GitHostDetector.Detect(origin.FetchUrl);
            if (!string.Equals(host, source.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var slug = GitHostDetector.ParseOwnerRepo(origin.FetchUrl);
            return slug is not null
                && string.Equals(slug.Value.Owner, source.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slug.Value.Repo, source.Repo, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A repository that has gone away / cannot be read is not a match. The poll must not fault
            // over one stale entry in the index.
            return false;
        }
    }
}
