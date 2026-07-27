using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug

namespace Mainguard.Git.PullRequests;

/// <summary>
/// Base for not-yet-implemented PR providers (T-23). The dispatch table stays complete so wiring a
/// real provider is additive, but every operation throws a typed "not yet supported for &lt;host&gt;"
/// and <see cref="IsImplemented"/> is false so <c>PullRequestService.IsSupported</c> reports the host
/// as unsupported (the UI then shows the graceful unsupported affordance rather than erroring).
/// </summary>
internal abstract class UnsupportedPullRequestProvider : IPullRequestProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    private Exception NotSupported() =>
        new GitOperationException($"Pull request integration is not yet supported for {HostLabel}.");

    public Task<IReadOnlyList<PullRequestItem>> ListAsync(RepoSlug repo, string token, PullRequestState filter, CancellationToken ct) => throw NotSupported();
    public Task<PullRequestDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct) => throw NotSupported();
    public Task<PullRequestItem> CreateAsync(RepoSlug repo, string token, CreatePullRequest request, CancellationToken ct) => throw NotSupported();
    public Task<PullRequestItem> MergeAsync(RepoSlug repo, string token, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct) => throw NotSupported();
    public Task CloseAsync(RepoSlug repo, string token, int number, CancellationToken ct) => throw NotSupported();

    // Review (T-25): same typed "not yet supported for <host>" until the host's live flow lands.
    public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(RepoSlug repo, string token, int number, CancellationToken ct) => throw NotSupported();
    public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(RepoSlug repo, string token, int number, CancellationToken ct) => throw NotSupported();
    public Task<PullRequestReview> SubmitReviewAsync(RepoSlug repo, string token, int number, SubmitReview review, CancellationToken ct) => throw NotSupported();
}

/// <summary>GitLab merge-request provider stub (T-23): <c>/projects/:id/merge_requests</c> lands with the live matrix.</summary>
internal sealed class GitLabPullRequestProvider : UnsupportedPullRequestProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket pull-request provider stub (T-23).</summary>
internal sealed class BitbucketPullRequestProvider : UnsupportedPullRequestProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps pull-request provider stub (T-23).</summary>
internal sealed class AzureDevOpsPullRequestProvider : UnsupportedPullRequestProvider
{
    protected override string HostLabel => "Azure DevOps";
}
