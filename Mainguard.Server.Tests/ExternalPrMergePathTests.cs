using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Services;
using Mainguard.Git;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Server.Tests.Agents;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>An external (upstream pull request) queue entry merges on its host, and cannot reach the terminal
/// <c>Merged</c> state any other way</b> — end to end through the real composition: the real in-proc
/// daemon, the shipped <see cref="DaemonClient"/>, the shipped <see cref="DaemonBackedOrchestrator"/> (the
/// exact adapter the Merge button runs on), a real git repository on disk, and a real bare "upstream"
/// repository standing in for the host.
///
/// <para><b>Why the upstream repository is real.</b> The failure this path exists to prevent is not "the
/// merge API was not called" — it is a queue that records a merge nobody can point at a commit for. An
/// external entry's <c>agent/pr-&lt;n&gt;</c> branch really is in the mirror, so a local fast-forward
/// would succeed while the pull request stayed open upstream, and an API that merely <i>says</i> "merged"
/// is no better. Every assertion below is therefore about refs: did <c>refs/heads/main</c> move, does it
/// contain the commit the host named, and — for every refusal — did it stay exactly where it was while
/// the queue stayed exactly as it was. <see cref="HostReportedAMergeThatIsNotOnTheBaseBranch_RecordsNothing"/>
/// is the direct guard: the host reports success, nothing happened upstream, and the entry must not merge.</para>
///
/// <para><b>No live network.</b> The host is a fake (<see cref="FakeHost"/>) whose "merge" performs a real
/// git merge in a real bare repository. The live GitHub round trip stays in the manual matrix, as it does
/// for every other T-23 endpoint.</para>
/// </summary>
public sealed class ExternalPrMergePathTests : IClassFixture<DaemonFixture>, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string SyncRemote = "mainguard-vm";
    private const string AgentId = "pr-7";
    private const int PrNumber = 7;

    private readonly List<string> _dirs = new();
    private readonly DaemonFixture _daemon;
    private readonly string _repoHandle = "repo-extmerge-" + Guid.NewGuid().ToString("N");

    public ExternalPrMergePathTests(DaemonFixture daemon)
    {
        _daemon = daemon;
        _ = _daemon.Token; // force a single synchronous host build
    }

    // ---- the world ------------------------------------------------------------------------------

    /// <summary>
    /// The shape a real external entry has:
    /// <list type="bullet">
    /// <item><c>Upstream</c> — a bare repo standing in for the host, holding <c>main</c> and the PR head.</item>
    /// <item><c>UpstreamWork</c> — a checkout of it; this is where the fake host performs its merges.</item>
    /// <item><c>Path</c> — the user's Windows checkout, with <c>origin</c> → upstream and the sync remote.</item>
    /// <item><c>Mirror</c> — the daemon's bare mirror, carrying <c>agent/pr-7</c> at the VERIFIED head.</item>
    /// </list>
    /// </summary>
    private sealed record World(
        string Path, string Upstream, string UpstreamWork, string Mirror, string MainSha, string PrHead);

    private World BuildWorld()
    {
        // The host's repository.
        var upstream = NewDir("mg-ext-upstream-");
        Git(upstream, "-c", "init.defaultBranch=main", "init", "--bare");

        var work = NewDir("mg-ext-upstream-work-");
        Git(work, "-c", "init.defaultBranch=main", "clone", upstream, ".");
        Identify(work);
        File.WriteAllText(Path.Combine(work, "README.md"), "seed\n");
        Git(work, "add", "-A");
        Git(work, "commit", "-m", "seed");
        Git(work, "branch", "-M", "main");
        Git(work, "push", "-u", "origin", "main");
        var mainSha = Rev(work, "main");

        // The bot's pull request: a branch one commit ahead of main, pushed to the host.
        Git(work, "checkout", "-b", "pr-head");
        File.WriteAllText(Path.Combine(work, "feature.txt"), "bot work\n");
        Git(work, "add", "-A");
        Git(work, "commit", "-m", "bot commit");
        Git(work, "push", "-u", "origin", "pr-head");
        var prHead = Rev(work, "pr-head");
        Git(work, "checkout", "main");

        // The daemon's mirror, holding the materialized PR head as agent/pr-7 — the VERIFIED head, since
        // the intake re-queues the entry whenever this moves.
        var mirror = NewDir("mg-ext-mirror-");
        Git(mirror, "init", "--bare");
        Git(work, "push", mirror, $"pr-head:refs/heads/agent/{AgentId}");

        // The user's own checkout: origin → the host, plus the daemon's sync remote.
        var repo = NewDir("mg-ext-repo-");
        Git(repo, "clone", upstream, ".");
        Identify(repo);
        Git(repo, "remote", "add", SyncRemote, mirror);
        Git(repo, "fetch", SyncRemote);

        Assert.Equal(mainSha, Rev(repo, "main"));
        Assert.Equal(prHead, Rev(repo, $"refs/remotes/{SyncRemote}/agent/{AgentId}"));

        return new World(repo, upstream, work, mirror, mainSha, prHead);
    }

    // ---- the fake host --------------------------------------------------------------------------

    /// <summary>
    /// Stands in for the host's pull-request API. <see cref="Merge"/> defaults to performing a REAL merge
    /// in the upstream repository — which is what makes the happy path's assertions mean something — and
    /// each test overrides the read or the merge to produce one failure mode.
    /// </summary>
    private sealed class FakeHost : IHostPullRequestGateway
    {
        private readonly World _world;

        public FakeHost(World world)
        {
            _world = world;
            Detail = new PullRequestDetail
            {
                Summary = new PullRequestItem
                {
                    Number = PrNumber,
                    State = PullRequestState.Open,
                    HeadSha = world.PrHead,
                },
                Mergeable = true,
                MergeableState = "clean",
            };
        }

        /// <summary>What a read of the pull request returns.</summary>
        public PullRequestDetail Detail { get; set; }

        /// <summary>Thrown from the read, when set.</summary>
        public Func<Exception>? GetThrows { get; set; }

        /// <summary>Replaces the merge behaviour. Returning a sha reports that merge WITHOUT performing
        /// one upstream — the shape a lying (or differently-scoped) host has.</summary>
        public Func<string>? MergeReturnsWithoutMerging { get; set; }

        /// <summary>Thrown from the merge, when set.</summary>
        public Func<Exception>? MergeThrows { get; set; }

        public int GetCalls { get; private set; }
        public int MergeCalls { get; private set; }
        public string? LastExpectedHeadSha { get; private set; }

        public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
        {
            GetCalls++;
            Assert.Equal(PrNumber, number);
            if (GetThrows is not null) throw GetThrows();
            return Task.FromResult(Detail);
        }

        public Task<PullRequestItem> MergeAsync(
            string repoPath, int number, string expectedHeadSha, PullRequestMergeMethod method, CancellationToken ct)
        {
            MergeCalls++;
            LastExpectedHeadSha = expectedHeadSha;
            Assert.Equal(PrNumber, number);
            if (MergeThrows is not null) throw MergeThrows();

            if (MergeReturnsWithoutMerging is not null)
            {
                return Task.FromResult(new PullRequestItem
                {
                    Number = number,
                    State = PullRequestState.Merged,
                    MergeCommitSha = MergeReturnsWithoutMerging(),
                });
            }

            // The real thing: merge the pull request head into main upstream and publish it.
            Git(_world.UpstreamWork, "checkout", "main");
            Git(_world.UpstreamWork, "merge", "--no-ff", "--no-edit", "pr-head");
            Git(_world.UpstreamWork, "push", "origin", "main");
            return Task.FromResult(new PullRequestItem
            {
                Number = number,
                State = PullRequestState.Merged,
                MergeCommitSha = Rev(_world.UpstreamWork, "main"),
            });
        }
    }

    // ---- daemon-side queue ------------------------------------------------------------------------

    /// <summary>Registers a live queue whose authoritative main is <paramref name="mainSha"/>, with the
    /// external entry verified against it (so <c>CanMerge</c> is genuinely true — a gate that blocks the
    /// honest path proves nothing).</summary>
    /// <param name="branchSha">K4/§23.5 — the <c>agent/pr-&lt;n&gt;</c> tip the verification was measured
    /// ON, which the live provisioner resolves from the mirror after the pre-verification publish. The
    /// external merge now READS its verified head from this record rather than re-deriving it from a ref
    /// <c>PrHeadFetcher</c> has already reset forward, so the fixture has to record it too.</param>
    private async Task<MergeQueue> RegisterQueueAsync(
        string mainSha, bool verifyEntry = true, string branchSha = "")
    {
        var registry = (MergeQueueRegistry)_daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        var queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: mainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                agentId, mainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "dotnet test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow, BranchSha: branchSha)));

        queue.EnsureEntry(AgentId, MergeEntryOrigin.External);
        if (verifyEntry)
        {
            await queue.RunVerificationAsync(AgentId, CancellationToken.None);
            Assert.True(queue.CanMerge(AgentId, out var why), $"fixture is wrong — {AgentId} cannot merge: {why}");
        }

        registry.Register(_repoHandle, new MergeQueueContext(queue, Leases));
        return queue;
    }

    private IMergeLeaseStore Leases => _daemon.Services.GetRequiredService<IMergeLeaseStore>();

    private DaemonClient NewClient() => new(_daemon.CreateChannel, () => _daemon.Token);

    private DaemonBackedOrchestrator NewAdapter(DaemonClient client, FakeHost host)
        => new(client, ownsClient: false, journalFactory: NewJournal, hostPullRequests: () => host);

    /// <summary>The adapter, the queue and the fake host, wired to one world and one repo handle.</summary>
    private async Task<(DaemonClient Client, DaemonBackedOrchestrator Adapter, MergeQueue Queue, FakeHost Host)>
        ArrangeAsync(World world, bool verifyEntry = true)
    {
        var client = NewClient();
        var queue = await RegisterQueueAsync(world.MainSha, verifyEntry, branchSha: world.PrHead);
        var host = new FakeHost(world);
        var adapter = NewAdapter(client, host);
        adapter.SetActiveRepo(_repoHandle, world.Path, SyncRemote);

        // The origin travels on the queue stream; the merge routes on it, so wait for the projection.
        Assert.True(
            await WaitUntilAsync(() => adapter.GetQueue().Count > 0),
            "the queue projection never arrived — the origin could not have been read");

        return (client, adapter, queue, host);
    }

    // ---- 1. the happy path: the host merges, the checkout converges ---------------------------------

    /// <summary>
    /// Press Merge on an external entry: the pull request is merged on the host, the user's
    /// <c>refs/heads/main</c> is brought onto the commit that merge produced, and the daemon records THAT
    /// sha. Nothing here asserts "the API was called" — the API being called is the least interesting
    /// thing that could be true; the assertions are the refs.
    /// </summary>
    [Fact]
    public async Task ExternalPr_IsMergedOnTheHost_AndTheCheckoutConvergesOnThatMerge()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        await adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout);

        // The merge happened UPSTREAM: the host's main now contains the pull request's commit.
        var upstreamMain = Rev(world.UpstreamWork, "main");
        Assert.Equal(0, Contains(world.UpstreamWork, world.PrHead, upstreamMain));

        // And the user's checkout converged onto exactly that commit — it moved, and it contains the PR.
        Assert.Equal(upstreamMain, Rev(world.Path, "main"));
        Assert.NotEqual(world.MainSha, Rev(world.Path, "main"));
        Assert.Equal(0, Contains(world.Path, world.PrHead, "main"));

        // The daemon recorded the sha main REALLY moved to, and the entry is terminal.
        Assert.Equal(upstreamMain, queue.CurrentMainSha);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));

        // The host merge was compare-and-swapped against the head the queue verified, not against
        // "whatever the pull request happens to be now".
        Assert.Equal(world.PrHead, host.LastExpectedHeadSha);
    }

    // ---- 2. THE guard: a reported merge that did not land ------------------------------------------

    /// <summary>
    /// <b>The exact bug shape this path exists to prevent.</b> The host's merge call returns success and
    /// names a commit — but nothing was merged upstream, so that commit is not on the base branch. An
    /// entry must NOT reach the terminal <c>Merged</c> state on the strength of an API saying so: the
    /// queue is the record everything downstream trusts, and a confirmed merge is read by the boot
    /// reconcile as proof the merge exists.
    ///
    /// <para>The sha returned is the pull request's own head — a real commit, present in the repository,
    /// and exactly the sort of plausible value that would sail past a check that only tested for
    /// "non-empty". It is not on <c>origin/main</c>, and that is the only thing that matters.</para>
    /// </summary>
    [Fact]
    public async Task HostReportedAMergeThatIsNotOnTheBaseBranch_RecordsNothing()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        host.MergeReturnsWithoutMerging = () => world.PrHead;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains("isn't on", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was recorded", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, host.MergeCalls);                              // it really did go to the host
        Assert.Equal(world.MainSha, Rev(world.Path, "main"));          // and main did not move
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(world.MainSha, queue.CurrentMainSha);             // no cascade
        Assert.Null(Leases.GetOutstanding(_repoHandle));               // released, not stranded
    }

    /// <summary>The same rule when the host names no commit at all: a merge with nothing to verify it
    /// against is not a merge, whatever the response said.</summary>
    [Fact]
    public async Task HostReportedAMergeWithNoCommit_RecordsNothing()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        host.MergeReturnsWithoutMerging = () => string.Empty;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains("named no merge commit", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(world.MainSha, Rev(world.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 3. upstream state: each refusal is its own sentence ---------------------------------------

    /// <summary>Already merged upstream — by a human on the host, or by an earlier attempt. Nothing to do,
    /// and nothing this queue may claim as its own merge.</summary>
    [Fact]
    public async Task PullRequestAlreadyMergedUpstream_RefusesWithoutMergingAnything()
    {
        await AssertUpstreamRefusalAsync(
            host => host.Detail = WithState(host.Detail, PullRequestState.Merged),
            expectedFragment: "already merged upstream");
    }

    /// <summary>Closed without merging — the work is not landing, and the human has to reopen it upstream
    /// if they disagree. Distinct from "already merged": opposite meaning, opposite next step.</summary>
    [Fact]
    public async Task PullRequestClosedWithoutMerging_RefusesWithoutMergingAnything()
    {
        await AssertUpstreamRefusalAsync(
            host => host.Detail = WithState(host.Detail, PullRequestState.Closed),
            expectedFragment: "closed upstream without being merged");
    }

    /// <summary>Conflicts with its base branch on the host. Resolving it is a job on the pull request, not
    /// something a local fast-forward could paper over.</summary>
    [Fact]
    public async Task PullRequestConflictsUpstream_RefusesWithoutMergingAnything()
    {
        await AssertUpstreamRefusalAsync(
            host => host.Detail = WithMergeableState(host.Detail, "dirty"),
            expectedFragment: "conflicts with its base branch");
    }

    /// <summary>Required reviews or status checks are not satisfied. This is branch protection speaking —
    /// a different problem from a conflict, and the flat "mergeable: false" bool cannot tell them apart.</summary>
    [Fact]
    public async Task PullRequestBlockedByRequiredChecks_RefusesWithoutMergingAnything()
    {
        await AssertUpstreamRefusalAsync(
            host => host.Detail = WithMergeableState(host.Detail, "blocked"),
            expectedFragment: "required reviews or status checks");
    }

    /// <summary>
    /// The external analogue of a lost CAS: the pull request gained commits after the queue verified it.
    /// Merging now would land work no verification ever saw — which is precisely what a merge queue is
    /// for preventing — so this refuses and the branch re-verifies.
    /// </summary>
    [Fact]
    public async Task PullRequestHeadMovedSinceVerification_LosesTheCasWithoutMergingAnything()
    {
        await AssertUpstreamRefusalAsync(
            host => host.Detail = WithHeadSha(host.Detail, new string('a', 40)),
            expectedFragment: "new commits since it was verified");
    }

    /// <summary>
    /// <b>K4/§23.5 — the head compare could not fail.</b> The verified head used to be re-derived at merge
    /// time from whatever <c>refs/heads/agent/pr-&lt;n&gt;</c> this checkout held. But
    /// <c>PrHeadFetcher</c> hard-RESETS that ref to the pull request's newest head <i>before</i> the
    /// intake calls <c>NotifyNewCommits</c>, so between a force-push and the next intake poll BOTH sides
    /// of the compare were the new head: the CAS passed, and unverified third-party code merged.
    ///
    /// <para>This walks exactly that window. The queue verified the PR at its old head; the ref in the
    /// checkout is then reset forward to the new one, and upstream reports the new one. Nothing here is
    /// stale from git's point of view — which is the whole problem, and why the verified head has to be
    /// READ from the record rather than computed from the repository.</para>
    /// </summary>
    [Fact]
    public async Task AForcePushedPrHead_CannotPassTheHeadCompareByMovingBothSidesOfIt()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        // The bot force-pushes. The intake's fetcher resets the materialized ref to the new head, and the
        // user's checkout picks it up — both spellings now name the NEW head, not the verified one.
        var newHead = ForcePushPrHead(world);
        Assert.NotEqual(world.PrHead, newHead);
        Assert.Equal(newHead, Rev(world.Path, $"refs/remotes/{SyncRemote}/agent/{AgentId}"));
        host.Detail = WithHeadSha(host.Detail, newHead);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));

        Assert.Contains("not the pull request head the queue verified", refusal.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.Equal(world.MainSha, Rev(world.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle)); // the lease did not strand the repo
    }

    /// <summary>
    /// The one place in the merge-identity lane that refuses on an UNKNOWN rather than declining to
    /// answer. Everywhere else an unmeasured sha means "do not manufacture a refusal"; here declining to
    /// answer means merging code from outside this installation on the strength of a compare that cannot
    /// fail. An unanswerable question in front of an irreversible act on third-party code is a "no".
    /// </summary>
    [Fact]
    public async Task AnExternalEntryWithNoRecordedVerifiedHead_RefusesRatherThanComparingAgainstItself()
    {
        var world = BuildWorld();
        var client = NewClient();
        using var _c = client;
        // A queue that verified the entry WITHOUT recording which head it measured — the pre-K3 record
        // shape, and the shape a seeded row has.
        var queue = await RegisterQueueAsync(world.MainSha, verifyEntry: true, branchSha: "");
        var host = new FakeHost(world);
        using var adapter = NewAdapter(client, host);
        adapter.SetActiveRepo(_repoHandle, world.Path, SyncRemote);
        // The origin travels on the queue stream and the merge routes on it — without this wait the
        // adapter would take the LOCAL path and this test would measure the wrong leg entirely.
        Assert.True(
            await WaitUntilAsync(() => adapter.GetQueue().Count > 0),
            "the queue projection never arrived — the origin could not have been read");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));

        Assert.Contains("no recorded verified head", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-verify", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.MergeCalls);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// A force-push of the pull request head, propagated the way the intake propagates one: the mirror's
    /// <c>agent/pr-&lt;n&gt;</c> is hard-reset forward (<c>PrHeadFetcher</c>'s own behaviour) and the
    /// user's checkout fetches it. Returns the new head.
    /// </summary>
    private string ForcePushPrHead(World world)
    {
        File.WriteAllText(Path.Combine(world.UpstreamWork, "more.txt"), "force-pushed work\n");
        Git(world.UpstreamWork, "checkout", "pr-head");
        Git(world.UpstreamWork, "add", "-A");
        Git(world.UpstreamWork, "commit", "-m", "bot commit 2");
        var newHead = Rev(world.UpstreamWork, "pr-head");
        Git(world.UpstreamWork, "push", "--force", "origin", "pr-head");
        Git(world.UpstreamWork, "push", "--force", world.Mirror, $"pr-head:refs/heads/agent/{AgentId}");
        Git(world.UpstreamWork, "checkout", "main");
        Git(world.Path, "fetch", "--force", SyncRemote);
        Git(world.Path, "fetch", "--force", "origin");
        return newHead;
    }

    /// <summary>Every "the pull request upstream is not in a state we may merge" refusal has the same
    /// shape: the host is read, the merge is never attempted, and no ref and no queue state changes.</summary>
    private async Task AssertUpstreamRefusalAsync(Action<FakeHost> arrangeHost, string expectedFragment)
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        arrangeHost(host);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains(expectedFragment, refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, host.MergeCalls);                              // nothing was merged upstream
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));  // and the host's main is untouched
        Assert.Equal(world.MainSha, Rev(world.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(world.MainSha, queue.CurrentMainSha);
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 4. transport failures ---------------------------------------------------------------------

    /// <summary>A token without write access. The human has to fix the sign-in; retrying will not help,
    /// so the reason has to say which of the two it is.</summary>
    [Fact]
    public async Task HostRefusesForLackOfPermission_SaysSo_AndRecordsNothing()
    {
        await AssertMergeFailureAsync(
            () => new GitOperationException("GitHub request failed (403): Resource not accessible") { HostStatusCode = 403 },
            expectedFragment: "write access");
    }

    /// <summary>The host could not be reached. Nothing was attempted, so the wording must be "try again",
    /// never "the merge was refused" — the two lead the human to opposite conclusions.</summary>
    [Fact]
    public async Task HostUnreachable_SaysSo_AndRecordsNothing()
    {
        await AssertMergeFailureAsync(
            () => new GitOperationException("Could not reach GitHub: No such host is known") { HostUnreachable = true },
            expectedFragment: "couldn't reach the host");
    }

    /// <summary>The host's own head compare-and-swap fired: the pull request changed between the read and
    /// the merge. Reported as staleness so the branch re-verifies, exactly like an <c>--ff-only</c> refusal.</summary>
    [Fact]
    public async Task HostRejectsTheHeadCompareAndSwap_ReportsStaleness_AndRecordsNothing()
    {
        await AssertMergeFailureAsync(
            () => new GitOperationException("GitHub request failed (409): Head branch was modified") { HostStatusCode = 409 },
            expectedFragment: "changed upstream");
    }

    /// <summary>The host declined the merge itself (405 — the pull request is not mergeable). Its words
    /// reach the human rather than being replaced by a guess.</summary>
    [Fact]
    public async Task HostDeclinesTheMerge_SurfacesItsReason_AndRecordsNothing()
    {
        await AssertMergeFailureAsync(
            () => new GitOperationException("GitHub request failed (405): Pull Request is not mergeable") { HostStatusCode = 405 },
            expectedFragment: "not mergeable");
    }

    /// <summary>Every transport failure at the merge call has the same shape: the merge was attempted, it
    /// failed, and nothing anywhere records a merge.</summary>
    private async Task AssertMergeFailureAsync(Func<Exception> throws, string expectedFragment)
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        host.MergeThrows = throws;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains(expectedFragment, refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.Equal(world.MainSha, Rev(world.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(world.MainSha, queue.CurrentMainSha);
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 5. the invariants the local path respects, respected here too -----------------------------

    /// <summary>
    /// MG-11. The daemon's <c>CanMerge</c> gate is enforced for external entries by the same
    /// <c>BeginMerge</c> that guards local ones — so an unverified pull request is refused BEFORE any
    /// token is spent and before the host is touched at all. The external path must go through the gate,
    /// never around it.
    /// </summary>
    [Fact]
    public async Task UnverifiedExternalEntry_IsRefusedByTheDaemonGate_BeforeTheHostIsTouched()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world, verifyEntry: false);
        using var _c = client;
        using var _a = adapter;

        Assert.False(queue.CanMerge(AgentId, out _));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.StartsWith("Can't merge —", refusal.Message, StringComparison.Ordinal);

        Assert.Equal(0, host.GetCalls);
        Assert.Equal(0, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// MG-23. One outstanding merge per repository, across BOTH origins and through the daemon's ONE
    /// lease store. While another merge holds the repo's lease, an external merge waits — in the daemon's
    /// own words — and never reaches the host, because a pull request merged while a local merge is
    /// mid-flight lands on a main the other side already claimed.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_TakesTheSamePerRepoLease_AndNeverReachesTheHostWhileItIsHeld()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        var other = Leases.TryBegin(_repoHandle, "lease-held-by-someone-else", "loom-1", world.MainSha, "main");
        Assert.NotNull(other);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains("already in progress", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));

        // And the other merge's lease is untouched: a refused merge hands back its own, never somebody else's.
        Assert.Equal("lease-held-by-someone-else", Leases.GetOutstanding(_repoHandle)?.LeaseId);
    }

    /// <summary>
    /// A confirmed external merge cannot be merged again. The entry is terminal, so the daemon's gate
    /// refuses the second <c>BeginMerge</c> and the host is never asked to merge an already-merged pull
    /// request — a second merge here would be a second merge commit upstream, from one button press.
    /// </summary>
    [Fact]
    public async Task AConfirmedExternalMerge_CannotBeMergedTwice()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        await adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        var afterFirst = Rev(world.UpstreamWork, "main");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.StartsWith("Can't merge —", refusal.Message, StringComparison.Ordinal);

        Assert.Equal(1, host.MergeCalls);                              // the host saw exactly one merge
        Assert.Equal(afterFirst, Rev(world.UpstreamWork, "main"));     // upstream did not move again
        Assert.Equal(afterFirst, Rev(world.Path, "main"));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// The local half of the freshness CAS still applies: main moving on the user's checkout after the
    /// verification means nothing may be merged — and, critically, the pull request must not be merged
    /// UPSTREAM either. An upstream merge cannot be taken back, so it has to be the last thing that
    /// happens, after every locally-checkable precondition has passed.
    /// </summary>
    [Fact]
    public async Task MainMovedOnTheCheckout_RefusesBeforeTouchingTheHost()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        File.WriteAllText(Path.Combine(world.Path, "other.txt"), "someone else\n");
        Git(world.Path, "add", "-A");
        Git(world.Path, "commit", "-m", "concurrent main move");
        var movedMain = Rev(world.Path, "main");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains("main moved", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.Equal(movedMain, Rev(world.Path, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    /// <summary>A dirty working tree is refused before the host is touched, for the same reason: a pull
    /// request merged upstream that this checkout then cannot converge onto is the one outcome with no
    /// clean recovery.</summary>
    [Fact]
    public async Task DirtyWorkingTree_RefusesBeforeTouchingTheHost()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        File.WriteAllText(Path.Combine(world.Path, "README.md"), "the human is mid-edit\n");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConfirmMergeAsync(AgentId).WaitAsync(Timeout));
        Assert.Contains("uncommitted", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, host.MergeCalls);
        Assert.Equal(world.MainSha, Rev(world.UpstreamWork, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(Leases.GetOutstanding(_repoHandle));
    }

    // ---- 6. it is one visible sentence, not a silent no-op -----------------------------------------

    /// <summary>The surfaces invoke the merge through <see cref="MergeActionRunner"/>, so an external
    /// refusal has to arrive as a warning the human can act on — the same treatment a local refusal gets.</summary>
    [Fact]
    public async Task AnExternalRefusalIsReportedAsAWarning_AndTheMergeAsAConfirmation()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        host.Detail = WithMergeableState(host.Detail, "blocked");

        var reported = new List<(string Message, bool IsWarning)>();
        await MergeActionRunner.RunAsync(adapter, AgentId, (m, w) => reported.Add((m, w))).WaitAsync(Timeout);

        var refusal = Assert.Single(reported);
        Assert.True(refusal.IsWarning, "a refusal must be reported, and reported as a warning");
        Assert.Contains("required reviews or status checks", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(world.MainSha, Rev(world.Path, "main"));

        // The control: unblock it upstream and the same button path reports a real merge.
        reported.Clear();
        host.Detail = WithMergeableState(host.Detail, "clean");
        await MergeActionRunner.RunAsync(adapter, AgentId, (m, w) => reported.Add((m, w))).WaitAsync(Timeout);

        var success = Assert.Single(reported);
        Assert.False(success.IsWarning);
        Assert.Equal(Rev(world.UpstreamWork, "main"), Rev(world.Path, "main"));
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    // ---- 7. the confirmation names the origin that actually merged ---------------------------------

    /// <summary>
    /// <b>An external merge must not be reported in the local fast-forward's words.</b> Every refusal on
    /// this path was already origin-specific; the confirmation was not, so merging an upstream pull request
    /// announced <c>Merged agent/pr-7 into main</c> — the shape of the one thing P2-12 exists to prevent.
    /// It is a plausible sentence, which is what makes it bad: <c>agent/pr-7</c> really is in the mirror and
    /// really could have been fast-forwarded, so the line reads as correct while describing a merge that did
    /// not happen here.
    ///
    /// <para>The assertions below are ordered so the distinguishing one comes first: the local sentence is
    /// ruled out before the external sentence is checked, because a test that only asserted a substring
    /// both origins share would pass in exactly the broken world it exists to catch.</para>
    /// </summary>
    [Fact]
    public async Task TheExternalConfirmation_SaysMergedUpstream_AndCannotWearTheLocalFastForwardSentence()
    {
        var world = BuildWorld();
        var (client, adapter, queue, host) = await ArrangeAsync(world);
        using var _c = client;
        using var _a = adapter;

        var reported = new List<(string Message, bool IsWarning)>();
        await MergeActionRunner.RunAsync(adapter, AgentId, (m, w) => reported.Add((m, w))).WaitAsync(Timeout);

        var success = Assert.Single(reported);
        Assert.False(success.IsWarning, "the merge landed — this is a confirmation, not a refusal");

        // The merge under discussion really is the external one: the host merged the pull request, and
        // this checkout converged onto that merge. Without this the message assertions describe nothing.
        var landedMain = Rev(world.Path, "main");
        Assert.Equal(1, host.MergeCalls);
        Assert.Equal(Rev(world.UpstreamWork, "main"), landedMain);
        Assert.NotEqual(world.MainSha, landedMain);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));

        // THE defect: the local origin's sentence, on an external merge.
        Assert.DoesNotContain($"Merged agent/{AgentId} into", success.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("into main", success.Message, StringComparison.Ordinal);

        // Both halves of what this path guarantees, said separately so each is measured: the pull request
        // was merged UPSTREAM by the host, and local main then fast-forwarded onto the commit that produced.
        Assert.Contains($"pull request #{PrNumber}", success.Message, StringComparison.Ordinal);
        Assert.Contains("upstream", success.Message, StringComparison.Ordinal);
        Assert.Contains("fast-forwarded onto", success.Message, StringComparison.Ordinal);
        Assert.Contains(landedMain[..7], success.Message, StringComparison.Ordinal);

        // And the whole line, exactly — a substring check alone cannot tell the two origins apart.
        Assert.Equal(
            $"Merged pull request #{PrNumber} upstream — main fast-forwarded onto {landedMain[..7]}.",
            success.Message);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static PullRequestDetail WithState(PullRequestDetail detail, PullRequestState state) => new()
    {
        Summary = new PullRequestItem
        {
            Number = detail.Summary.Number,
            State = state,
            HeadSha = detail.Summary.HeadSha,
        },
        Mergeable = detail.Mergeable,
        MergeableState = detail.MergeableState,
    };

    private static PullRequestDetail WithMergeableState(PullRequestDetail detail, string mergeableState) => new()
    {
        Summary = detail.Summary,
        Mergeable = mergeableState == "clean",
        MergeableState = mergeableState,
    };

    private static PullRequestDetail WithHeadSha(PullRequestDetail detail, string headSha) => new()
    {
        Summary = new PullRequestItem
        {
            Number = detail.Summary.Number,
            State = detail.Summary.State,
            HeadSha = headSha,
        },
        Mergeable = detail.Mergeable,
        MergeableState = detail.MergeableState,
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private IOperationJournal NewJournal()
    {
        var dbPath = Path.Combine(NewDir("mg-ext-db-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory())
        {
            db.Database.EnsureCreated();
        }

        return new OperationJournal(factory);
    }

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private static void Identify(string repo)
    {
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@mainguard.local");
        Git(repo, "config", "commit.gpgsign", "false");
    }

    private static void Git(string repo, params string[] args) => AgentTestGit.RunChecked(repo, args);

    private static string Rev(string repo, string reference)
        => AgentTestGit.Run(repo, "rev-parse", "--verify", reference).Out.Trim();

    /// <summary>Exit code of <c>merge-base --is-ancestor</c> — 0 when <paramref name="commit"/> is
    /// reachable from <paramref name="from"/>. Asserted on directly so the check is git's, not ours.</summary>
    private static int Contains(string repo, string commit, string from)
        => AgentTestGit.Run(repo, "merge-base", "--is-ancestor", commit, from).Code;

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}
