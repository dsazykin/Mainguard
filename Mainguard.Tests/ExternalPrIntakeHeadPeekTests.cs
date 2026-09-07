using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// F40 — the external-PR intake used to fetch and <c>reset --hard</c> the worker's worktree BEFORE
/// anything compared the head, so a PR that had not moved still had the worker's uncommitted work
/// deleted on every poll interval, and the reset raced the worker's own git on <c>.git/index.lock</c>.
///
/// <para>These tests pin the ordering at the seam where it is decidable: the destructive
/// <see cref="IPrHeadFetcher.FetchHeadAsync"/> must not be called at all for an unchanged head, and must
/// still be called whenever the head moved or the non-destructive peek could not answer. The peek itself
/// (<c>git ls-remote</c> — no fetch, no worktree, no lock) is production code in
/// <see cref="PrHeadFetcher"/>; what is measured here is that the intake asks it first and believes it.</para>
/// </summary>
public class ExternalPrIntakeHeadPeekTests
{
    private const string RepoPath = "/repo";
    private const string RepoHash = "hash0";

    [Fact]
    public async Task UnchangedHead_IsDetectedByThePeek_AndTheWorktreeIsNeverTouched()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Prs.Add(Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        // First poll materializes: a new PR has no seen head, so the authoritative fetch is correct.
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, h.Fetcher.Fetches);
        Assert.Equal("sha-7a", h.Store.GetSeenHead(Harness.Source.Key, 7));

        // Ten more polls with the head standing still. Before F40 every one of them ran
        // fetch + `reset --hard` in the worker's worktree.
        for (var i = 0; i < 10; i++)
        {
            await h.Intake.PollOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, h.Fetcher.Fetches);   // still the materializing one, and only it
        Assert.Equal(10, h.Fetcher.Peeks);    // asked every time — just never destructively
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
    }

    [Fact]
    public async Task MovedHead_StillFetches_AndStillInvalidatesTheVerification()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Prs.Add(Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);

        h.Fetcher.Heads[7] = "sha-7b"; // force-push
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, h.Fetcher.Fetches);
        Assert.Equal("sha-7b", h.Store.GetSeenHead(Harness.Source.Key, 7));
    }

    /// <summary>
    /// The peek is an optimisation with a fail-open contract: "the host does not advertise this ref" is
    /// UNKNOWN, not UNCHANGED. Answering null must fall through to the authoritative fetch, or a PR whose
    /// ref listing hiccups would silently stop being refreshed.
    /// </summary>
    [Fact]
    public async Task PeekThatCannotAnswer_FallsThroughToTheAuthoritativeFetch()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Prs.Add(Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);

        h.Fetcher.PeekReturnsNull = true;
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, h.Fetcher.Fetches);
    }

    /// <summary>
    /// A fetcher with no peek capability (every pre-F40 double, and any future implementation that does
    /// not offer one) must behave exactly as it did before — the ordering fix is an improvement where it
    /// applies, never a behaviour change where it does not.
    /// </summary>
    [Fact]
    public async Task FetcherWithoutPeekCapability_KeepsTheOldBehaviour()
    {
        var plain = new PeeklessFetcher();
        var h = new Harness(plain);
        h.Intake.Subscribe(Harness.Source);
        h.Prs.Add(Bot(7));
        plain.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, plain.Fetches);
        Assert.Equal("sha-7a", h.Store.GetSeenHead(Harness.Source.Key, 7));
    }

    // ---- fakes ---------------------------------------------------------------

    private static PullRequestItem Bot(int n) =>
        new() { Number = n, Author = "codex[bot]", State = PullRequestState.Open };

    private class PeeklessFetcher : IPrHeadFetcher
    {
        public Dictionary<int, string> Heads { get; } = new();
        public int Fetches { get; private set; }

        public Task<string> FetchHeadAsync(
            ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(Heads.TryGetValue(prNumber, out var sha) ? sha : "unknown");
        }
    }

    /// <summary>The same fetcher plus the F40 peek, so "did the intake compare before destroying?" is a
    /// pair of counters rather than an inference.</summary>
    private sealed class PeekingFetcher : PeeklessFetcher, IPrHeadPeek
    {
        public int Peeks { get; private set; }

        public bool PeekReturnsNull { get; set; }

        public Task<string?> PeekRemoteHeadAsync(
            ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
        {
            Peeks++;
            if (PeekReturnsNull)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult(Heads.TryGetValue(prNumber, out var sha) ? sha : null);
        }
    }

    private sealed class StubPrService : IPullRequestService
    {
        public List<PullRequestItem> Open { get; } = new();

        public bool IsSupported(string repoPath) => true;

        public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PullRequestItem>>(Open.ToArray());

        public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct)
            => throw new NotSupportedException();

        public Task CloseAsync(string repoPath, int number, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Always hands back a live jail: this suite is about the fetch ordering, not provisioning.</summary>
    private sealed class AlwaysLiveWorkerHost : IPrWorkerHost
    {
        public Task<PrWorkerResult> EnsureWorkerAsync(string repoHash, string agentId, int prNumber, CancellationToken ct)
            => Task.FromResult(PrWorkerResult.AlreadyLive());

        public Task ReleaseWorkerAsync(string repoHash, string agentId, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public StubPrService Pr = new();
        public PeekingFetcher Fetcher = new();
        public InMemoryPrIntakeStore Store = new();
        public InMemoryAuditLog Audit = new();
        public MergeQueue Queue;
        public ExternalPrIntake Intake;

        private long _tick;

        public Harness(IPrHeadFetcher? fetcher = null)
        {
            Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            {
                var when = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick));
                return Task.FromResult(new VerificationRecord(
                    id, Queue!.CurrentMainSha, true, "log.txt", "npm test", "cfg", when));
            };
            Queue = new MergeQueue(
                RepoHash, "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(), run,
                requeue: (id, ct) => Task.CompletedTask);

            Intake = new ExternalPrIntake(
                Pr, Store, new AlwaysLiveWorkerHost(), fetcher ?? Fetcher,
                resolveTarget: _ => new PrIntakeTarget(RepoPath, RepoHash, Queue),
                audit: Audit,
                clock: () => DateTimeOffset.UnixEpoch);
        }

        public List<PullRequestItem> Prs => Pr.Open;

        public static ExternalPrSource Source => new("github.com", "acme", "app", null);
    }
}
