using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.PullRequests;
using Mainguard.Git.Security;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic pull-request service (T-23). Resolves the repo's origin host + stored token,
/// parses <c>owner/repo</c> from the remote once (via the shared <see cref="HostConnectionResolver"/>,
/// the same path the T-24 issue service uses), and dispatches to the matching internal
/// <see cref="IPullRequestProvider"/> (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs).
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call
/// <c>new</c> — socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed
/// to the provider, which places it only in the <c>Authorization</c> header; it never enters a URL,
/// argv, log, or exception message here.</para>
/// </summary>
public sealed class PullRequestService : IPullRequestService
{
    private readonly HostConnectionResolver _resolver;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public PullRequestService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
    {
        if (git is null) throw new ArgumentNullException(nameof(git));
        _resolver = new HostConnectionResolver(git, keyring ?? new SecureKeyring());
        _http = httpClient ?? new HttpClient();
    }

    public bool IsSupported(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out _))
            return false;
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            return false;
        return !string.IsNullOrEmpty(_resolver.TokenFor(host));
    }

    public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.ListAsync(slug, token, filter, ct);
    }

    public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetAsync(slug, token, number, ct);
    }

    public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CreateAsync(slug, token, request, ct);
    }

    public Task<PullRequestItem> MergeAsync(
        string repoPath, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.MergeAsync(slug, token, number, method, expectedHeadSha, ct);
    }

    public Task CloseAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CloseAsync(slug, token, number, ct);
    }

    public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetReviewsAsync(slug, token, number, ct);
    }

    public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetReviewCommentsAsync(slug, token, number, ct);
    }

    public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.SubmitReviewAsync(slug, token, number, review, ct);
    }

    // Resolves (provider, owner/repo, token) or throws a typed error. Central so no operation
    // reaches a provider without a validated host, parsed slug, and a token. Host/token/slug plumbing
    // is the shared HostConnectionResolver (same path as the T-24 issue service); only the provider
    // dispatch is PR-specific.
    private (IPullRequestProvider Provider, RepoSlug Slug, string Token) Resolve(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out var remoteUrl))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Pull request integration is not available for '{host}'.");

        var slug = HostConnectionResolver.ParseSlug(remoteUrl)
            ?? throw new GitOperationException($"Could not parse an owner/repository from the origin URL for '{host}'.");

        var token = _resolver.TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, slug, token);
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private IPullRequestProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubPullRequestProvider(_http),
        HostKind.GitLab => new GitLabPullRequestProvider(),
        HostKind.Bitbucket => new BitbucketPullRequestProvider(),
        HostKind.AzureDevOps => new AzureDevOpsPullRequestProvider(),
        _ => null,
    };
}
