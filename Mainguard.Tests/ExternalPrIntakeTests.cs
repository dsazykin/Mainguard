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

        /// <summary>A non-rate-limit transport failure (an absent/expired host token is the realistic one).</summary>
        public bool ThrowAuthFailure { get; set; }

        public int ListCalls { get; private set; }
        public int MutatingCalls { get; private set; }

        public bool IsSupported(string repoPath) => true;

        public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
        {
            ListCalls++;
            if (ThrowRateLimit)
                throw new GitOperationException("GitHub API rate limit reached: API rate limit exceeded");
            if (ThrowAuthFailure)
                throw new AuthenticationRequiredException("No stored token for github.com.", "github.com");
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

    /// <summary>
    /// The intake's spawn seam, standing in for the daemon's <c>ExternalPrWorkerHost</c>: it creates the
    /// worktree exactly as the real spawn chain's launcher does, records who got a jail, and can be told
    /// to refuse (the MG-2 gates) or fail (no provisioned mirror / Docker down). Release tears the
    /// worktree down — the real one tears down the jail, its network segment and its cache too.
    /// </summary>
    private sealed class FakePrWorkerHost : IPrWorkerHost
    {
        private readonly FakeWorktreeManager _worktrees;
        private readonly HashSet<string> _live = new(StringComparer.Ordinal);

        public FakePrWorkerHost(FakeWorktreeManager worktrees) => _worktrees = worktrees;

        /// <summary>When set, every ensure is refused with this reason (a gate said no).</summary>
        public string? RefuseWith { get; set; }

        /// <summary>When set, every ensure fails with this reason (a provisioning failure).</summary>
        public string? FailWith { get; set; }

        /// <summary>Every (agentId, prNumber) an ensure was asked for, in order — including repeats.</summary>
        public List<(string AgentId, int PrNumber)> Requests { get; } = new();

        /// <summary>The agent ids that actually got a jail.</summary>
        public List<string> Jailed { get; } = new();

        public List<string> Released { get; } = new();

        public Task<PrWorkerResult> EnsureWorkerAsync(string repoHash, string agentId, int prNumber, CancellationToken ct)
        {
            Requests.Add((agentId, prNumber));

            if (_live.Contains(agentId))
                return Task.FromResult(PrWorkerResult.AlreadyLive());
            if (RefuseWith is not null)
                return Task.FromResult(PrWorkerResult.Refused(RefuseWith));
            if (FailWith is not null)
                return Task.FromResult(PrWorkerResult.Failed(FailWith));

            _worktrees.CreateAgentWorktree(repoHash, agentId);
            _live.Add(agentId);
            Jailed.Add(agentId);
            return Task.FromResult(PrWorkerResult.Spawned());
        }

        public Task ReleaseWorkerAsync(string repoHash, string agentId, CancellationToken ct)
        {
            Released.Add(agentId);
            _live.Remove(agentId);
            _worktrees.RemoveAgentWorktree(repoHash, agentId, force: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>Returns whatever head SHA the test currently maps a PR number to; counts fetches.</summary>
    private sealed class FakeHeadFetcher : IPrHeadFetcher
    {
        public Dictionary<int, string> Heads { get; } = new();
        public int Fetches { get; private set; }

        /// <summary>PR numbers whose fetch throws (an unreachable host / a deleted head).</summary>
        public HashSet<int> FailFor { get; } = new();

        public Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct)
        {
            Fetches++;
            if (FailFor.Contains(prNumber))
                throw new GitOperationException($"could not fetch pull/{prNumber}/head");
            return Task.FromResult(Heads.TryGetValue(prNumber, out var sha) ? sha : "unknown");
        }
    }

    private sealed class Harness
    {
        public RecordingPrService Pr = new();
        public FakeWorktreeManager Worktrees = new();
        public FakePrWorkerHost Workers;
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

            Workers = new FakePrWorkerHost(Worktrees);
            Intake = new ExternalPrIntake(
                Pr, Store, Workers, Fetcher,
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

    // ---- The spawn seam: no jail, no entry ------------------------------

    /// <summary>
    /// The materialization now BEGINS with a jail. Before this the intake created a worktree and an entry
    /// and spawned nothing, and since verification runs in the worker's own jail (host execution is a
    /// rejection trigger) the entry could never leave <c>Working</c>.
    /// </summary>
    [Fact]
    public async Task PollOnce_NewMatchingPr_ShouldAskForAJail_ForThatPrsOwnAgentId()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { ("pr-7", 7) }, h.Workers.Requests);
        Assert.Equal(new[] { "pr-7" }, h.Workers.Jailed);
    }

    /// <summary>
    /// MG-2, at the intake. An arriving bot pull request is a spawn request from outside the machine, so
    /// when a gate refuses it (kill switch / worker cap / memory admission) the intake must materialize
    /// <b>nothing</b> — no worktree, no queue entry, and crucially no seen-head, because a recorded head
    /// would make the next poll treat the PR as already materialized and never retry. An entry admitted
    /// without a jail is an entry that can never be verified; an unbounded external queue that spawned
    /// regardless would be a denial of service on the user's own box.
    /// </summary>
    [Fact]
    public async Task PollOnce_WhenAGateRefusesTheSpawn_ShouldMaterializeNothing_AndRetryOnALaterPoll()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        h.Workers.RefuseWith = "Worker cap reached — 6/6 managed workers running.";

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Empty(h.Workers.Jailed);
        Assert.Empty(h.Worktrees.Created);                              // no worktree
        Assert.DoesNotContain("pr-7", h.Queue.Agents);                  // no queue entry
        Assert.Null(h.Store.GetSeenHead(Harness.Source.Key, 7));        // nothing marked as seen
        Assert.Equal(0, h.Fetcher.Fetches);                             // and the head was never fetched

        // The refusal is not terminal: when capacity frees, the same PR materializes normally.
        h.Workers.RefuseWith = null;
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "pr-7" }, h.Workers.Jailed);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
        Assert.Equal(MergeEntryOrigin.External, h.Queue.GetOrigin("pr-7"));
        Assert.Equal("sha-7a", h.Store.GetSeenHead(Harness.Source.Key, 7));
    }

    /// <summary>A provisioning failure (no mirror, image preflight, Docker down) is treated exactly like a
    /// refusal — nothing is materialized — and it does not propagate out of the poll.</summary>
    [Fact]
    public async Task PollOnce_WhenTheSpawnFails_ShouldMaterializeNothing_AndNotThrow()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        h.Workers.FailWith = "no provisioned mirror";

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.DoesNotContain("pr-7", h.Queue.Agents);
        Assert.Null(h.Store.GetSeenHead(Harness.Source.Key, 7));
        Assert.Contains(h.Audit.Read(), e => e.Type == "external_pr_worker_unavailable");
    }

    /// <summary>
    /// The ensure is re-asked on every poll and is a no-op for a live worker: a repeat poll must not spawn
    /// a second jail, and must not consume a second slot of the worker cap.
    /// </summary>
    [Fact]
    public async Task PollOnce_RepeatedPolls_ShouldNotSpawnASecondJail()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";

        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(3, h.Workers.Requests.Count);       // asked every poll…
        Assert.Equal(new[] { "pr-7" }, h.Workers.Jailed); // …and spawned exactly once
    }

    /// <summary>
    /// A human discard has to mean the same thing for an intake'd pull request that it means for a local
    /// agent: the entry is off the queue and nothing is being kept warm for it.
    ///
    /// <para>Materializing is re-asked on EVERY poll and consulted no queue state, so a discarded entry
    /// kept getting its jail re-provisioned — and the release path only fires when the PR CLOSES upstream,
    /// which for an open PR is never. The worker (and its MG-36 network segment, from a bridge pool ~32
    /// deep) would have been held indefinitely for a pull request the operator explicitly dropped.</para>
    ///
    /// <para>The moved-head leg is driven too, because that is the other half of the same defect: it calls
    /// <c>NotifyNewCommits</c>, which threw <c>Discarded → Working</c> before the terminal guard was
    /// asked through <c>IsTerminal</c>. The intake swallows that per PR and never records the head, so it
    /// re-threw on every poll forever.</para>
    /// </summary>
    [Fact]
    public async Task DiscardedEntry_ShouldReleaseTheWorker_AndStopBeingReMaterialized()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "pr-7" }, h.Workers.Jailed);

        Assert.True(h.Queue.TryDiscard("pr-7", "uid:1000", "not wanted", out var refusal), refusal);
        var ensuresBefore = h.Workers.Requests.Count;

        // The PR is still OPEN upstream, and its head then moves (a force-push) — the two things that
        // would otherwise keep the intake re-provisioning and re-throwing.
        h.Fetcher.Heads[7] = "sha-7b";
        await h.Intake.PollOnceAsync(CancellationToken.None);
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "pr-7" }, h.Workers.Released);
        Assert.Equal(ensuresBefore, h.Workers.Requests.Count); // no jail re-provisioned after the discard
        Assert.Equal(WorkerMergeState.Discarded, h.Queue.GetState("pr-7"));
        Assert.DoesNotContain("pr-7", h.Queue.Agents);
    }

    /// <summary>
    /// Closed upstream ⇒ the whole WORKER is released, not just the worktree. In the daemon that is what
    /// reclaims the jail's MG-36 network segment; Docker's local bridge pool is ~32 deep, so a segment
    /// leaked per closed pull request eventually makes every spawn on the box fail.
    /// </summary>
    [Fact]
    public async Task PrClosedUpstream_ShouldReleaseTheWorker_NotJustTheWorktree()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);

        h.Pr.Open.Clear();
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "pr-7" }, h.Workers.Released);
    }

    /// <summary>
    /// The head fetch is a network operation against the host, so it can fail per PULL REQUEST. One
    /// unreachable head must cost that pull request its cycle and nothing more — not the other open PRs
    /// on the same source, and certainly not the daemon's intake loop (<c>RunAsync</c> catches only
    /// cancellation). Nothing is recorded as seen, so the failed one is retried next poll.
    /// </summary>
    [Fact]
    public async Task PollOnce_WhenOnePrsHeadFetchFails_TheOthersStillMaterialize()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Pr.Open.Add(h.Bot(8));
        h.Fetcher.Heads[8] = "sha-8a";
        h.Fetcher.FailFor.Add(7);

        await h.Intake.PollOnceAsync(CancellationToken.None);   // must not throw

        Assert.DoesNotContain("pr-7", h.Queue.Agents);
        Assert.Null(h.Store.GetSeenHead(Harness.Source.Key, 7));
        Assert.Contains(h.Audit.Read(), e => e.Type == "external_pr_materialize_failed");

        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-8"));
        Assert.Equal("sha-8a", h.Store.GetSeenHead(Harness.Source.Key, 8));

        // …and the failed one recovers on the next poll over its already-live jail.
        h.Fetcher.FailFor.Clear();
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
    }

    /// <summary>
    /// Wiring the production target resolver makes the transport's non-rate-limit failures reachable for
    /// the first time (an absent host token is the obvious one). <c>RunAsync</c> catches only cancellation,
    /// so an escaping exception permanently killed the daemon's entire intake loop. One faulting source
    /// must cost that source's cycle and nothing else.
    /// </summary>
    [Fact]
    public async Task PollOnce_WhenTheTransportFaults_ShouldNotThrow_AndShouldKeepPolling()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.ThrowAuthFailure = true;

        await h.Intake.PollOnceAsync(CancellationToken.None);   // must not throw
        Assert.Contains(h.Audit.Read(), e => e.Type == "external_pr_poll_failed");

        // NOT a rate limit, so no backoff window is opened and the next poll really does poll again.
        Assert.Null(h.Intake.BackoffUntil(Harness.Source));
        h.Pr.ThrowAuthFailure = false;
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
    }

    // ---- The settings the poller obeys are the PERSISTED ones ------------

    /// <summary>
    /// The bot-author allow-list the poll actually filters on comes from the STORE, not from a
    /// compiled-in default.
    ///
    /// <para>This is the poller half of the intake settings surface, and it is the half that decides
    /// whether that surface is real. The cadence and the bot list used to be settable properties on this
    /// engine that nothing in production ever assigned — so a settings page could have saved a bot list
    /// anywhere at all and the poll would still have matched only <c>DefaultBotAuthors</c>. Here a saved
    /// list is what selects the pull requests: <c>renovate[bot]</c> is materialized because it is in the
    /// saved list, and <c>codex[bot]</c> is NOT, despite being one of the shipped defaults.</para>
    /// </summary>
    [Fact]
    public async Task PollOnce_ShouldFilterAuthors_ByTheSavedBotList_NotTheCompiledDefault()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Store.SaveSettings(new PrIntakeSettings(true, 60, new[] { "renovate[bot]" }));

        h.Pr.Open.Add(h.Bot(7, "renovate[bot]"));   // in the SAVED list
        h.Pr.Open.Add(h.Bot(8, "codex[bot]"));      // a shipped default, but NOT in the saved list
        h.Fetcher.Heads[7] = "sha-7a";
        h.Fetcher.Heads[8] = "sha-8a";

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
        Assert.DoesNotContain("pr-8", h.Queue.Agents);
    }

    /// <summary>
    /// The saved cadence is the cadence the scheduler delays on, read live — so a change takes effect on
    /// the next cycle rather than on the next daemon restart. The loop is started once by the daemon's
    /// hosted service and runs for the daemon's whole lifetime; a cadence captured at start would leave
    /// the settings page right and the poller wrong for hours.
    /// </summary>
    [Fact]
    public void PollInterval_ShouldFollowTheSavedSetting_Live()
    {
        var h = new Harness();
        Assert.Equal(TimeSpan.FromSeconds(60), h.Intake.PollInterval);   // the shipped default

        h.Store.SaveSettings(new PrIntakeSettings(true, 300, ExternalPrIntake.DefaultBotAuthors));
        Assert.Equal(TimeSpan.FromSeconds(300), h.Intake.PollInterval);

        // Clamped, both ends — a stored row is not a trusted input, and a zero here is a hot loop
        // against a rate-limited host API.
        h.Store.SaveSettings(new PrIntakeSettings(true, 0, ExternalPrIntake.DefaultBotAuthors));
        Assert.Equal(TimeSpan.FromSeconds(PrIntakeSettings.MinPollIntervalSeconds), h.Intake.PollInterval);

        h.Store.SaveSettings(new PrIntakeSettings(true, 99_999, ExternalPrIntake.DefaultBotAuthors));
        Assert.Equal(TimeSpan.FromSeconds(PrIntakeSettings.MaxPollIntervalSeconds), h.Intake.PollInterval);
    }

    /// <summary>
    /// The off switch is obeyed by the poll itself: a disabled intake lists nothing, fetches nothing and
    /// materializes nothing, while the subscriptions stay put so switching it back on needs no re-entry.
    /// Asserted on <c>ListCalls</c> as well as on the queue — "materialized nothing" would also be true
    /// of an intake that listed the host on every cycle and then discarded the result, which is still
    /// upstream traffic the user asked it to stop making.
    /// </summary>
    [Fact]
    public async Task PollOnce_WhenIntakeIsDisabled_ShouldNotEvenListTheHost()
    {
        var h = new Harness();
        h.Intake.Subscribe(Harness.Source);
        h.Pr.Open.Add(h.Bot(7));
        h.Fetcher.Heads[7] = "sha-7a";
        h.Store.SaveSettings(new PrIntakeSettings(false, 60, ExternalPrIntake.DefaultBotAuthors));

        await h.Intake.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, h.Pr.ListCalls);
        Assert.Empty(h.Queue.Agents);
        Assert.Single(h.Store.Subscriptions());   // off, not unsubscribed

        // …and re-enabling needs nothing but the setting.
        h.Store.SaveSettings(new PrIntakeSettings(true, 60, ExternalPrIntake.DefaultBotAuthors));
        await h.Intake.PollOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("pr-7"));
    }

    /// <summary>
    /// The store round-trips the settings through real SQLite, including the two normalizations the
    /// daemon relies on. This is the desktop-suite half of the persistence claim; the daemon suite's
    /// <c>PrIntakeSettingsRpcTests</c> proves the RPC reaches this store and survives the host.
    /// </summary>
    [Fact]
    public void DbPrIntakeStore_ShouldRoundTripSettings_AndNormalizeThem()
    {
        var dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-tests", Guid.NewGuid().ToString("N"), "intake.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
        using (var db = new Mainguard.Git.AppDbContext(dbPath))
        {
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate(db.Database);
        }

        var store = new DbPrIntakeStore(() => new Mainguard.Git.AppDbContext(dbPath));

        // Never written: the daemon's shipped default, not a zeroed row.
        Assert.Equal(PrIntakeSettings.Default, store.GetSettings());

        store.SaveSettings(new PrIntakeSettings(false, 42, new[] { " renovate[bot] ", "" }));

        // A brand-new store object over the same file — the next daemon boot's view.
        var reread = new DbPrIntakeStore(() => new Mainguard.Git.AppDbContext(dbPath)).GetSettings();
        Assert.False(reread.Enabled);
        Assert.Equal(42, reread.PollIntervalSeconds);
        Assert.Equal(new[] { "renovate[bot]" }, reread.BotAuthors);   // trimmed, blanks dropped

        // The upsert stays a single row: a second save replaces rather than accumulates.
        store.SaveSettings(new PrIntakeSettings(true, 77, new[] { "copilot" }));
        var second = new DbPrIntakeStore(() => new Mainguard.Git.AppDbContext(dbPath)).GetSettings();
        Assert.True(second.Enabled);
        Assert.Equal(77, second.PollIntervalSeconds);
        Assert.Equal(new[] { "copilot" }, second.BotAuthors);
    }
}
