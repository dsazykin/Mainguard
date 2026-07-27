using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Services;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// P2-12 external agent PR intake (plan §6 tests 1–5,7 + TI-P2-12 1–8; the §6/TI union). Every test is
/// driven through the T-23 provider seam and the worktree/fetch seams — no live network, no Docker. The
/// merge-path dispatch (test 6) lives in <see cref="MergeDispatchTests"/>.
/// </summary>
public class ExternalPrIntakeTests
{
    private const string RepoPath = "/repo";
    private const string RepoHash = "hash0";

    // ---- Fakes ------------------------------------------------------------

    /// <summary>A recording <see cref="IPullRequestService"/>: returns a scripted open-PR list (or throws a
    /// typed rate-limit), and counts every mutating call so "zero upstream writes" is assertable.</summary>
    private sealed class RecordingPrService : IPullRequestService
    {
        public List<PullRequestItem> Open { get; } = new();
        public bool ThrowRateLimit { get; set; }
        public int ListCalls { get; private set; }
        public int MutatingCalls { get; private set; }

        public bool IsSupported(string repoPath) => true;

        public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
        {
            ListCalls++;
            if (ThrowRateLimit)
                throw new GitOperationException("GitHub API rate limit reached: API rate limit exceeded");
            return Task.FromResult<IReadOnlyList<PullRequestItem>>(Open.ToList());
        }

        public Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, string? expectedHeadSha, CancellationToken ct)
        {
            MutatingCalls++;
            return Task.FromResult(new PullRequestItem { Number = number, State = PullRequestState.Merged });
        }

        public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct)
        {
            MutatingCalls++;
            return Task.FromResult(new PullRequestItem());
        }

        public Task CloseAsync(string repoPath, int number, CancellationToken ct)
        {
            MutatingCalls++;
            return Task.CompletedTask;
        }

        public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct)
        {
            MutatingCalls++;
            return Task.FromResult(new PullRequestReview());
        }

        public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct) =>
            Task.FromResult(new PullRequestDetail());

        public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PullRequestReview>>(Array.Empty<PullRequestReview>());

        public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReviewComment>>(Array.Empty<ReviewComment>());
    }

    /// <summary>Records worktree create/remove without touching git.</summary>
    private sealed class FakeWorktreeManager : IAgentWorktreeManager
    {
        public List<string> Created { get; } = new();
        public List<string> Removed { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId)
        {
            Created.Add(agentId);
            return $"/wt/{repoHash}/{agentId}";
        }

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) => Removed.Add(agentId);
        public void Prune(string repoHash) { }
        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();
    }

    /// <summary>Returns whatever head SHA the test currently maps a PR number to; counts fetches.</summary>
    private sealed class FakeHeadFetcher : IPrHeadFetcher
    {
        public Dictionary<int, string> Heads { get; } = new();
        public int Fetches { get; private set; }

        public Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(Heads.TryGetValue(prNumber, out var sha) ? sha : "unknown");
        }
    }

    private sealed class Harness
    {
        public RecordingPrService Pr = new();
        public FakeWorktreeManager Worktrees = new();
        public FakeHeadFetcher Fetcher = new();
        public InMemoryPrIntakeStore Store = new();
        public InMemoryAuditLog Audit = new();
        public MergeQueue Queue = null!;
        public ExternalPrIntake Intake = null!;
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;

        private long _tick;

        public Harness()
        {
            var stateStore = new InMemoryMergeQueueStore();
            var verStore = new InMemoryVerificationStore();
            Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            {
                var when = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick));
                return Task.FromResult(new VerificationRecord(
                    id, Queue.CurrentMainSha, true, "log.txt", "npm test", "cfg", when));
            };
            Queue = new MergeQueue(RepoHash, "sha0", stateStore, verStore, run,
                requeue: (id, ct) => Task.CompletedTask);

            Intake = new ExternalPrIntake(
                Pr, Store, Worktrees, Fetcher,
                resolveTarget: _ => new PrIntakeTarget(RepoPath, RepoHash, Queue),
                audit: Audit,
                clock: () => Now);
        }

        public static ExternalPrSource Source => new("github.com", "acme", "app", null);

        public PullRequestItem Bot(int n, string author = "codex[bot]") =>
            new() { Number = n, Author = author, State = PullRequestState.Open };
    }

    // ---- Test 1: materialize only matching PRs ----------------------------

    [Fact]
    public async Task PollOnce_NewMatchingPr_ShouldMaterializeQueueEntry()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7, "codex[bot]"));
        h.Pr.Open.Add(h.Bot(8, "alice"));       // human author — must be ignored
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
        Assert.Equal(MergeEntryOrigin.External, h.Queue.GetOrigin("pr-7"));
        Assert.Equal(new[] { "pr-7" }, h.Worktrees.Created);
        Assert.DoesNotContain("pr-8", h.Queue.Agents);
        Assert.Equal("sha-7a", h.Store.GetSeenHead(Harness.Source.Key, 7));
    }

    // ---- Test 2: idempotent (same PR twice + double subscribe) ------------

    [Fact]
    public async Task PollOnce_SamePrTwice_ShouldBeIdempotent()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Intake.Subscribe(Harness.Source);      // double subscribe — one source row (edge row 3)
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Single(h.Store.Subscriptions());
        Assert.Equal(new[] { "pr-7" }, h.Worktrees.Created); // created exactly once, no duplicate
        Assert.Single(h.Queue.Agents);
    }

    // ---- Test 3: force-push invalidates verification + re-queues ----------

    [Fact]
    public async Task PrForcePushed_ShouldInvalidateVerification_AndRequeue()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Queue.RunVerificationAsync("pr-7", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState("pr-7"));
        Assert.True(h.Queue.CanMerge("pr-7", out _));

        // Force-push: the head moves to a new sha whose old sha disappears (edge row 1).
        h.Fetcher.Heads[7] = "sha-7b";
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
        Assert.False(h.Queue.CanMerge("pr-7", out _)); // old verification no longer satisfies CanMerge
        Assert.Equal("sha-7b", h.Store.GetSeenHead(Harness.Source.Key, 7));
        Assert.Equal(2, h.Fetcher.Fetches);            // worktree refreshed on the second poll
    }

    // ---- Test 4: closed upstream → cancel + prune ------------------------

    [Fact]
    public async Task PrClosedUpstream_ShouldCancelEntry_AndPruneWorktree()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Contains("pr-7", h.Queue.Agents);

        // PR 7 closed/merged upstream → no longer in the open list.
        h.Pr.Open.Clear();
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.DoesNotContain("pr-7", h.Queue.Agents);              // entry gone
        Assert.Equal(new[] { "pr-7" }, h.Worktrees.Removed);        // worktree + branch pruned
        Assert.Empty(h.Store.TrackedPrNumbers(Harness.Source.Key)); // untracked
    }

    // ---- Test 5: rate limit → backoff, never a crash loop ----------------

    [Fact]
    public async Task PollRateLimited_ShouldBackoff_ThroughTypedHostError_NeverCrashLoop()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.ThrowRateLimit = true;

        await h.Intake.PollOnceAsync(CancellationToken.None); // typed rate-limit is caught, not thrown
        Assert.NotNull(h.Intake.BackoffUntil(Harness.Source));

        // A second immediate poll must be skipped (bounded backoff) — no tight retry against the host.
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, h.Pr.ListCalls);

        // Once the backoff window elapses, polling resumes.
        h.Now = h.Now.AddHours(1);
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Pr.ListCalls);
    }

    // ---- Test 7: zero upstream writes during a full poll+verify cycle ----

    [Fact]
    public async Task Intake_ShouldWriteNothingUpstream_WithoutExplicitUserAction()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Queue.RunVerificationAsync("pr-7", CancellationToken.None); // the "verify" half of the cycle

        Assert.True(h.Pr.ListCalls > 0);        // it did poll (read-only)
        Assert.Equal(0, h.Pr.MutatingCalls);    // merge/create/close/submit-review never called
    }

    // ---- Test 8: author filter configurable + matches bot accounts -------

    [Theory]
    [InlineData("codex[bot]", null, true)]
    [InlineData("Codex[bot]", null, true)]        // case-insensitive
    [InlineData("google-jules[bot]", null, true)]
    [InlineData("copilot", null, true)]
    [InlineData("alice", null, false)]            // human author excluded
    [InlineData("my-bot", "my-bot", true)]        // per-source filter override matches
    [InlineData("codex[bot]", "my-bot", false)]   // per-source filter override excludes the default bots
    public void AuthorFilter_ShouldBeConfigurable_AndMatchBotAccounts(string author, string? sourceFilter, bool expected)
    {
        var h = new Harness();
        var source = new ExternalPrSource("github.com", "acme", "app", sourceFilter);
        var pr = new PullRequestItem { Number = 1, Author = author, State = PullRequestState.Open };

        Assert.Equal(expected, h.Intake.MatchesAuthor(pr, source));
    }
}
