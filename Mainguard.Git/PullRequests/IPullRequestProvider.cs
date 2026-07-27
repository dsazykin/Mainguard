using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug (parsed once by HostConnectionResolver)

namespace Mainguard.Git.PullRequests;

/// <summary>
/// Per-host pull/merge request adapter (T-23). One concrete provider per host family speaks the
/// host's REST dialect and maps its JSON to the host-agnostic models. The token is supplied per
/// call and lives only in the provider's <c>Authorization</c> header — never a URL/argv/log/message.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the
/// dispatch table is complete (adding a real provider is additive) while the service reports those
/// hosts as unsupported until their live flow lands.</para>
/// </summary>
internal interface IPullRequestProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<IReadOnlyList<PullRequestItem>> ListAsync(RepoSlug repo, string token, PullRequestState filter, CancellationToken ct);
    Task<PullRequestDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct);
    Task<PullRequestItem> CreateAsync(RepoSlug repo, string token, CreatePullRequest request, CancellationToken ct);
    /// <param name="expectedHeadSha">The head commit the merge is authorized against — sent as the host's
    /// merge compare-and-swap so a PR that gained commits since verification is refused rather than merged
    /// (P2-12). Null merges whatever the head is now, which is only correct when a human is looking at it.</param>
    Task<PullRequestItem> MergeAsync(RepoSlug repo, string token, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct);
    Task CloseAsync(RepoSlug repo, string token, int number, CancellationToken ct);

    // ---- Review (T-25) ------------------------------------------------------------------------

    Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(RepoSlug repo, string token, int number, CancellationToken ct);
    Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(RepoSlug repo, string token, int number, CancellationToken ct);
    Task<PullRequestReview> SubmitReviewAsync(RepoSlug repo, string token, int number, SubmitReview review, CancellationToken ct);
}
