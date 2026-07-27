using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Tests.Fakes;

/// <summary>
/// Delegate-backed <see cref="IPullRequestService"/> fake (TI-23 VM tests). Members a test uses are
/// wired via settable delegates; unstubbed members return benign defaults so a VM under test never has
/// to configure operations it doesn't exercise.
/// </summary>
public sealed class FakePullRequestService : IPullRequestService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, PullRequestState, IReadOnlyList<PullRequestItem>>? ListImpl { get; set; }
    public Func<string, CreatePullRequest, PullRequestItem>? CreateImpl { get; set; }
    public Func<string, int, PullRequestMergeMethod, PullRequestItem>? MergeImpl { get; set; }
    public Action<string, int>? CloseImpl { get; set; }

    // Review (T-25)
    public Func<string, int, IReadOnlyList<PullRequestReview>>? ReviewsImpl { get; set; }
    public Func<string, int, IReadOnlyList<ReviewComment>>? ReviewCommentsImpl { get; set; }
    public Func<string, int, SubmitReview, PullRequestReview>? SubmitReviewImpl { get; set; }

    public bool IsSupported(string repoPath) => IsSupportedImpl?.Invoke(repoPath) ?? true;

    public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
        => Task.FromResult(ListImpl?.Invoke(repoPath, filter) ?? Array.Empty<PullRequestItem>());

    public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
        => Task.FromResult(new PullRequestDetail());

    public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct)
        => Task.FromResult(CreateImpl?.Invoke(repoPath, request) ?? new PullRequestItem());

    public Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct)
        => Task.FromResult(MergeImpl?.Invoke(repoPath, number, method) ?? new PullRequestItem { Number = number, State = PullRequestState.Merged });

    public Task CloseAsync(string repoPath, int number, CancellationToken ct)
    {
        CloseImpl?.Invoke(repoPath, number);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct)
        => Task.FromResult(ReviewsImpl?.Invoke(repoPath, number) ?? Array.Empty<PullRequestReview>());

    public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct)
        => Task.FromResult(ReviewCommentsImpl?.Invoke(repoPath, number) ?? Array.Empty<ReviewComment>());

    public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct)
        => Task.FromResult(SubmitReviewImpl?.Invoke(repoPath, number, review) ?? new PullRequestReview { Id = 1, State = ReviewState.Commented });
}
