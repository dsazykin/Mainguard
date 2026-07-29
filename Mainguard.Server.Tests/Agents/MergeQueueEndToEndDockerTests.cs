using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Services;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// <b>The merge queue, driven as one loop rather than as a pile of parts.</b> Every other merge-queue test
/// in this repository substitutes something load-bearing: the verification is a lambda that returns
/// <c>Passed: true</c>, the <c>main@sha</c> is the string <c>"sha0"</c>, or the queue is hand-registered
/// into the daemon's registry. Each of those is fine for what it tests and none of them can answer the only
/// question the product actually rests on — <i>does an agent's branch get verified in that agent's own jail
/// and then land on the user's real checkout, with <c>main</c> at the commit it should be?</i>
///
/// <para>So nothing here is substituted except the container runtime's tenant. The daemon is the real
/// in-proc composition root; the queue is the one <c>MergeQueueProvisioner</c> builds on
/// <c>ProvisionRepo</c>; the verification command is read out of git from <c>.mainguard/verify</c> and run
/// by a real <c>docker exec</c> in a real hardened jail spawned by the shipped
/// <see cref="SandboxAgentLauncher"/> chain; the merge is driven by the shipped
/// <see cref="DaemonBackedOrchestrator"/> — the exact adapter the Merge button runs on — against a real git
/// repository on disk. <b>Every merge assertion is a ref assertion.</b> "The RPC returned success" is
/// precisely what the pre-#261 code did while running no git at all, so it proves nothing and is never
/// asserted alone.</para>
///
/// <para><b>The fixture repo is Node</b>, not Mainguard: the jail carries <c>nodejs_22</c> in its base
/// image, so the verification is a real toolchain running a real test file, and it can genuinely fail —
/// which the negative controls exercise. Mainguard's own <c>.mainguard/verify</c> (<c>dotnet test</c>)
/// cannot complete in a jail today, and a fixture that cannot fail would make every assertion below
/// vacuous.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class MergeQueueEndToEndDockerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>The bound on waits for a queue-stream projection to arrive. Deliberately short: these
    /// waits fail when a push that should have happened did not, and a three-minute deadline turns that
    /// into a three-minute test.</summary>
    private static readonly TimeSpan ProjectionWait = TimeSpan.FromSeconds(20);

    private readonly List<string> _dirs = new();
    private readonly List<string> _containers = new();
    private readonly List<(string RepoHash, string AgentId)> _segments = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // ================= 1. branch → verify → queue → merge =================

    /// <summary>
    /// <b>Criterion 1.</b> An agent commits in its own worktree; the daemon publishes that branch into the
    /// mirror, runs the repo's <c>.mainguard/verify</c> command inside the agent's own jail, and the human
    /// merge lands it on the user's checkout.
    ///
    /// <para>The assertions that matter are the last three: the checkout's <c>refs/heads/main</c> is at the
    /// <i>exact</i> commit the agent branch pointed at, that commit is not where main started, and the
    /// user's working tree really contains the agent's file. A merge that "succeeded" without moving a ref
    /// fails here.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AgentBranch_VerifiedInItsOwnJail_MergesAndAdvancesMainToTheBranchTip()
    {
        await using var loop = await LoopWorld.StartAsync(this);
        var agent = await loop.SpawnJailedAgentAsync();

        // The agent's work, committed in ITS OWN repository (MG-3) — never in the mirror, never in the
        // user's checkout. The daemon carries it into the mirror immediately before verification.
        var branchTip = agent.Commit(
            "src/calc.js",
            FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n",
            "feat: subtraction");

        var verified = await loop.RunVerificationAsync(agent.Id);

        // The command is the one committed at .mainguard/verify — read out of git, not guessed.
        Assert.Equal(FixtureRepo.VerifyCommand, verified.ResolvedCommand);
        Assert.True(verified.Passed, "the fixture's node test suite should pass in the jail");
        Assert.Equal(WorkerMergeState.Verified.ToString(), verified.State);
        // Pinned to the main the branch will actually merge into, not to an arbitrary string.
        Assert.Equal(loop.MainSha0, verified.MainSha);

        // The branch the queue consumes is refs/heads/agent/<id> IN THE MIRROR, and MG-3's pre-verify
        // publish is what put the agent's commit there.
        Assert.Equal(branchTip, Rev(loop.MirrorPath, $"refs/heads/agent/{agent.Id}"));

        // ---- the human merge, through the shipped adapter ----
        await loop.Adapter.ConfirmMergeAsync(agent.Id).WaitAsync(Timeout);

        // THE assertion. Not "the RPC returned true" — the ref.
        Assert.Equal(branchTip, Rev(loop.Checkout, "main"));
        Assert.NotEqual(loop.MainSha0, Rev(loop.Checkout, "main"));
        Assert.Equal(
            FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n",
            File.ReadAllText(Path.Combine(loop.Checkout, "src", "calc.js")));

        // ...and the daemon recorded the sha main really moved to, terminally, with no lease left behind.
        Assert.Equal(branchTip, loop.Queue.CurrentMainSha);
        Assert.Equal(WorkerMergeState.Merged, loop.Queue.GetState(agent.Id));
        Assert.Null(loop.Leases.GetOutstanding(loop.RepoHandle));
    }

    /// <summary>
    /// The non-vacuity control for criterion 1, and the only thing that makes the test above mean
    /// anything: the identical loop with a branch whose commit BREAKS the fixture's test suite. The jail
    /// reports a non-zero exit, the queue refuses to call it verified, the merge is refused, and
    /// <c>main</c> is exactly where it started.
    /// </summary>
    [RequiresDockerFact]
    public async Task BranchThatFailsItsOwnTests_CannotMerge_AndMainNeverMoves()
    {
        await using var loop = await LoopWorld.StartAsync(this);
        var agent = await loop.SpawnJailedAgentAsync();

        agent.Commit("src/calc.js", "exports.add = (a, b) => a + b + 1;\nexports.mul = (a, b) => a * b;\n",
            "regress: off-by-one in add");

        var verified = await loop.RunVerificationAsync(agent.Id);
        Assert.False(verified.Passed, "the jail must report the fixture suite's real non-zero exit");
        Assert.NotEqual(WorkerMergeState.Verified.ToString(), verified.State);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.Adapter.ConfirmMergeAsync(agent.Id).WaitAsync(Timeout));
        Assert.Contains("not verified", refusal.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(loop.MainSha0, Rev(loop.Checkout, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, loop.Queue.GetState(agent.Id));
        Assert.Null(loop.Leases.GetOutstanding(loop.RepoHandle));
    }

    // ================= 2. the flagged-change review gate (RT-D2) =================

    /// <summary>
    /// <b>Criterion 2.</b> A branch that rewrites its own <c>.mainguard/verify</c> is the RT-D2 case: it
    /// verifies GREEN — the drifted command still passes — and must still be unmergeable until a human
    /// acknowledges the drift. "Green" and "mergeable" are deliberately different questions here.
    ///
    /// <para>Three separate things are asserted, because three separate things were broken or could be:
    /// the daemon <b>blocks</b> the merge; the flagged item <b>reaches the client</b> so a human has
    /// something to acknowledge (it did not — see the projection assertion); and the acknowledgment
    /// <b>clears the gate</b> so the merge then lands on a real ref.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task BranchThatRewritesItsVerifyCommand_IsBlocked_SurfacedForReview_AndMergesOnlyAfterAcknowledgement()
    {
        await using var loop = await LoopWorld.StartAsync(this);
        var agent = await loop.SpawnJailedAgentAsync();

        // The drift. The command still works (verify.js ignores extra argv), so the branch is genuinely
        // Verified — a branch that merely failed would prove nothing about the gate.
        agent.Write(".mainguard/verify", FixtureRepo.VerifyCommand + " --strict\n");
        var branchTip = agent.Commit(
            "src/calc.js", FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: subtraction (+ retuned verify)");

        var verified = await loop.RunVerificationAsync(agent.Id);
        Assert.True(verified.Passed);
        Assert.Equal(WorkerMergeState.Verified.ToString(), verified.State);
        Assert.Equal(FixtureRepo.VerifyCommand + " --strict", verified.ResolvedCommand);

        // (a) The daemon blocks, in its own words, before any ref is touched.
        var can = await loop.Merge.CanMergeAsync(
            new CanMergeRequest { RepoHandle = loop.RepoHandle, AgentId = agent.Id }, loop.Headers);
        Assert.False(can.CanMerge);
        Assert.Contains("test command changed", can.Reason);

        // (b) The item reaches the REVIEW SURFACE. AgentDocumentViewModel renders one acknowledge control
        // per entry.FlaggedItems row and acknowledges it over the daemon RPC; with an empty list there is
        // nothing to render and nothing to click, so a daemon-blocked branch would be permanently
        // unmergeable from the shipped UI. This projection is the cockpit's only source for it.
        await loop.WaitForQueueProjectionAsync(agent.Id);
        var entry = Assert.Single(loop.Adapter.GetQueue(), q => q.AgentId == agent.Id);
        var flagged = Assert.Single(entry.FlaggedItems);
        Assert.Equal(Mainguard.Server.Services.MergeQueueGrpcService.ChangedTestCommandItemId, flagged.Id);
        Assert.False(flagged.Acknowledged);
        Assert.Contains("test command", flagged.Fact, StringComparison.OrdinalIgnoreCase);

        // (c) Pressing Merge without acknowledging is refused and moves nothing.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.Adapter.ConfirmMergeAsync(agent.Id).WaitAsync(Timeout));
        Assert.Contains("test command changed", refusal.Message);
        Assert.Equal(loop.MainSha0, Rev(loop.Checkout, "main"));
        Assert.Null(loop.Leases.GetOutstanding(loop.RepoHandle));

        // (d) Acknowledging a DIFFERENT item does not clear this one — the ack is per item, never global.
        await loop.Adapter.AcknowledgeFlaggedChangeAsync(agent.Id, "some-other-item").WaitAsync(Timeout);
        Assert.False(loop.Queue.CanMerge(agent.Id, out var stillBlocked));
        Assert.Contains("test command changed", stillBlocked);

        // (e) The human acknowledges THE item — through the same client call the cockpit's checkmark makes.
        await loop.Adapter.AcknowledgeFlaggedChangeAsync(
            agent.Id, Mainguard.Server.Services.MergeQueueGrpcService.ChangedTestCommandItemId).WaitAsync(Timeout);
        Assert.True(loop.Queue.CanMerge(agent.Id, out _));

        // …and the acknowledgment REACHES the surface that made it. An ack moves no state, so nothing
        // re-pushed the queue stream — and the stream is where the review surface reads CanMerge, the gate
        // reason and the item's own acknowledged flag from. Without a re-push the human acknowledges and
        // watches the Merge button stay disabled on a branch the daemon would now merge.
        Assert.True(
            await WaitUntilAsync(() =>
                loop.Adapter.GetQueue().FirstOrDefault(q => q.AgentId == agent.Id) is { } e
                && e.FlaggedItems.Count == 1 && e.FlaggedItems[0].Acknowledged
                && loop.Adapter.CanMerge(agent.Id, out _), ProjectionWait),
            "the acknowledgment never reached the client projection — the queue stream did not re-push");

        await loop.Adapter.ConfirmMergeAsync(agent.Id).WaitAsync(Timeout);
        Assert.Equal(branchTip, Rev(loop.Checkout, "main"));
        Assert.Equal(WorkerMergeState.Merged, loop.Queue.GetState(agent.Id));
    }

    // ================= 3. the stale cascade =================

    /// <summary>
    /// <b>Criterion 3.</b> Two co-tenant branches verify green in their own jails against the same
    /// <c>main@sha</c>. One merges; the other's evidence is now about a main that no longer exists, and it
    /// must be <b>blocked from merging</b> — not merely marked — until it has re-verified against the new
    /// main.
    ///
    /// <para>The blocked window is made observable rather than raced for: B's jail is told (by an untracked
    /// worktree file, so the verification COMMAND is byte-identical to main's and the RT-D2 gate stays
    /// silent) to dwell for a few seconds. During that window every merge door is tried and every one is
    /// shut. Afterwards the cascade's re-verification is asserted to be a genuinely new immutable record
    /// pinned to the NEW main — not the old record surviving with a flag flipped.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenOneBranchMerges_TheCoTenantIsInvalidated_BlockedFromMerging_AndReVerifiedAgainstTheNewMain()
    {
        await using var loop = await LoopWorld.StartAsync(this);
        var a = await loop.SpawnJailedAgentAsync();
        var b = await loop.SpawnJailedAgentAsync();

        var aTip = a.Commit("src/calc.js", FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: sub");
        b.Commit("docs/NOTES.md", "b was here\n", "docs: notes");

        Assert.True((await loop.RunVerificationAsync(a.Id)).Passed);
        var bFirst = await loop.RunVerificationAsync(b.Id);
        Assert.True(bFirst.Passed);
        Assert.Equal(loop.MainSha0, bFirst.MainSha);
        Assert.Equal(WorkerMergeState.Verified, loop.Queue.GetState(b.Id));
        Assert.True(loop.Queue.CanMerge(b.Id, out _), "B must be genuinely mergeable before A merges");

        // Make B's re-verification dwell, so the invalidated window is a fact rather than a race. The file
        // is untracked: it is not in either tree, so `.mainguard/verify` is identical on B and on main.
        b.WriteUntracked(FixtureRepo.DelayFile, "6000");

        // A merges for real: main moves on the user's checkout.
        await loop.Adapter.ConfirmMergeAsync(a.Id).WaitAsync(Timeout);
        Assert.Equal(aTip, Rev(loop.Checkout, "main"));

        // ---- the invalidated window: B is BLOCKED, by every door there is ----
        var state = loop.Queue.GetState(b.Id);
        Assert.True(
            state is WorkerMergeState.StaleVerified or WorkerMergeState.Verifying,
            $"B should be invalidated by the cascade, was {state}");

        Assert.False(loop.Queue.CanMerge(b.Id, out var blockedReason));
        Assert.Contains("verif", blockedReason, StringComparison.OrdinalIgnoreCase);

        // The daemon refuses to even grant B a lease (BeginMerge reads the gate under the lease, MG-11).
        var begun = await loop.Merge.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = loop.RepoHandle, AgentId = b.Id }, loop.Headers);
        Assert.False(begun.Granted);
        Assert.Null(loop.Leases.GetOutstanding(loop.RepoHandle));

        // And the shipped Merge button refuses, without moving main off A's commit.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.Adapter.ConfirmMergeAsync(b.Id).WaitAsync(Timeout));
        Assert.Contains("Can't merge", refusal.Message);
        Assert.Equal(aTip, Rev(loop.Checkout, "main"));
        Assert.NotEqual(WorkerMergeState.Merged, loop.Queue.GetState(b.Id));

        // ---- and then the auto re-queue really re-verifies it, in its own jail, against the NEW main ----
        b.DeleteUntracked(FixtureRepo.DelayFile);
        await loop.Queue.LastCascade.WaitAsync(Timeout);
        Assert.True(
            await WaitUntilAsync(() => loop.Queue.GetState(b.Id) == WorkerMergeState.Verified),
            "the cascade's auto re-verification never returned B to Verified");

        // CanMerge is the assertion that a NEW record exists, not the old one re-labelled: the gate
        // compares the record's own MainSha to the queue's current main, and a VerificationRecord is
        // immutable (invariant 2 — re-verification inserts a row, never updates one). B's first record is
        // pinned to MainSha0 forever, so CanMerge could not become true again on it.
        Assert.Equal(aTip, loop.Queue.CurrentMainSha);
        Assert.True(loop.Queue.CanMerge(b.Id, out var afterReVerify), afterReVerify);
    }

    // ================= 4. external (bot / upstream PR) entries =================

    /// <summary>
    /// <b>Criterion 4.</b> An intake'd upstream pull request is a queue entry like any other: it is gated by
    /// the same <c>CanMerge</c>, serialized by the same per-repo lease — and then merged on its HOST, with
    /// the user's checkout reconciled onto the commit the host produced, never by fast-forwarding the
    /// mirrored branch locally behind the pull request's back.
    ///
    /// <para>The host is a fake whose "merge" performs a real git merge in a real upstream repository, so
    /// every assertion is still a ref assertion. The daemon, the queue, the lease and the client adapter
    /// are all the shipped ones.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ExternalPullRequestEntry_MergesUpstream_AndTheCheckoutConvergesOnThatMerge()
    {
        await using var loop = await LoopWorld.StartAsync(this, withUpstreamHost: true);
        var pr = await loop.MaterializeExternalPrAsync(prNumber: 7, withJail: true);

        // The entry is badged External on the queue stream — the merge routes on it, and a Local routing
        // would fast-forward the mirror branch while leaving the pull request open upstream.
        await loop.WaitForQueueProjectionAsync(pr.AgentId);
        Assert.Equal(MergeEntryOrigin.External, loop.Queue.GetOrigin(pr.AgentId));

        // The same verify leg every other entry takes: the daemon reads .mainguard/verify out of git and
        // runs it in THIS entry's own jail. There is nothing external-specific about it, which is the
        // point of criterion 4 — one pipeline, two transports.
        var verified = await loop.RunVerificationAsync(pr.AgentId);
        Assert.True(verified.Passed);
        Assert.Equal(FixtureRepo.VerifyCommand, verified.ResolvedCommand);
        Assert.True(loop.Queue.CanMerge(pr.AgentId, out var why), $"the entry should be mergeable: {why}");

        await loop.Adapter.ConfirmMergeAsync(pr.AgentId).WaitAsync(Timeout);

        // The merge happened UPSTREAM…
        var upstreamMain = Rev(loop.UpstreamWork!, "main");
        Assert.Equal(0, Contains(loop.UpstreamWork!, pr.HeadSha, upstreamMain));

        // …and the user's checkout converged onto exactly that commit.
        Assert.Equal(upstreamMain, Rev(loop.Checkout, "main"));
        Assert.NotEqual(loop.MainSha0, Rev(loop.Checkout, "main"));
        Assert.Equal(0, Contains(loop.Checkout, pr.HeadSha, "main"));

        Assert.Equal(upstreamMain, loop.Queue.CurrentMainSha);
        Assert.Equal(WorkerMergeState.Merged, loop.Queue.GetState(pr.AgentId));
        Assert.Null(loop.Leases.GetOutstanding(loop.RepoHandle));
        Assert.Equal(pr.HeadSha, loop.Host!.LastExpectedHeadSha);   // CAS'd against the VERIFIED head
    }

    /// <summary>
    /// <b>Criterion 4's standing gap, asserted rather than described.</b> The <see cref="ExternalPrIntake"/>
    /// materializes an upstream pull request into a worktree and a queue entry and <b>spawns nothing</b> —
    /// so unless something else gives <c>pr-&lt;n&gt;</c> a jail (the test above does it explicitly), the
    /// entry has nowhere to be verified and can never leave <c>Working</c> under its own steam.
    ///
    /// <para>The refusal itself is correct and must stay: verification runs in the worker's own jail, and
    /// falling back to the host is a rejection trigger. What is missing is upstream of it. The assertion is
    /// written positively against the CURRENT behaviour so that the day the intake is wired to the spawn
    /// chain this test fails and has to be turned into its positive form — a gap that lives only in a
    /// report gets fixed by nobody.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ExternalPullRequestEntry_WithNoJail_CannotVerify_BecauseTheIntakeSpawnsNone()
    {
        await using var loop = await LoopWorld.StartAsync(this, withUpstreamHost: true);
        var pr = await loop.MaterializeExternalPrAsync(prNumber: 9, withJail: false);

        // The worktree exists and the entry is in the queue — everything except a jail to verify in.
        Assert.True(Directory.Exists(loop.WorktreePathFor(pr.AgentId)));
        Assert.Equal(WorkerMergeState.Working, loop.Queue.GetState(pr.AgentId));

        var ex = await Assert.ThrowsAsync<RpcException>(() => loop.Merge.RunVerificationAsync(
            new RunVerificationRequest { RepoHandle = loop.RepoHandle, AgentId = pr.AgentId },
            loop.Headers).ResponseAsync);
        Assert.Contains("no live sandbox", ex.Status.Detail, StringComparison.OrdinalIgnoreCase);

        // …and the consequence: it is stuck at Working, so nothing can merge it.
        Assert.Equal(WorkerMergeState.Working, loop.Queue.GetState(pr.AgentId));
        Assert.False(loop.Queue.CanMerge(pr.AgentId, out var reason));
        Assert.Contains("not verified", reason);
    }

    // ================= restart resume =================

    /// <summary>
    /// <b>The loop has to survive a daemon restart, and it did not.</b> Agent jails are persistent (a
    /// restart re-STARTS a stopped container rather than recreating one) and the merge queue rehydrates
    /// from SQLite in its constructor — but <c>AgentSessionStore</c> is memory-only and is written by
    /// exactly one thing, the spawn path. So a restarted daemon resumed a queue that knew about every
    /// branch, with every jail still running, and answered "no live sandbox" to every single verification:
    /// the resumed queue could never move again, and the stale cascade's auto re-verify failed for every
    /// branch on the box, until each agent was spawned afresh.
    ///
    /// <para>This drives it literally: a second daemon over the same VM root, with no spawn of any kind,
    /// verifies the same agent in the same still-running jail and merges it onto the real checkout. The
    /// jail is located the way P2-08 already says jail liveness is decided — off the container's own
    /// <c>mainguard.repo</c>/<c>mainguard.agent</c> labels.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task AfterADaemonRestart_TheStillRunningJailIsFound_AndTheResumedQueueVerifiesAndMerges()
    {
        await using var loop = await LoopWorld.StartAsync(this);
        var agent = await loop.SpawnJailedAgentAsync();
        var branchTip = agent.Commit(
            "src/calc.js", FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: subtraction");

        // The daemon goes away. The jail does not — nothing stops it, exactly as a daemon crash/upgrade
        // leaves it running.
        await loop.RestartDaemonAsync();
        Assert.Null(loop.SessionFor(agent.Id));  // the new daemon has no session record for this agent…

        // …and verification still runs, in that agent's own still-running jail.
        var verified = await loop.RunVerificationAsync(agent.Id);
        Assert.True(verified.Passed, "the resumed daemon must still find the running jail");
        Assert.Equal(FixtureRepo.VerifyCommand, verified.ResolvedCommand);
        Assert.Equal(WorkerMergeState.Verified.ToString(), verified.State);

        await loop.Adapter.ConfirmMergeAsync(agent.Id).WaitAsync(Timeout);
        Assert.Equal(branchTip, Rev(loop.Checkout, "main"));
        Assert.NotEqual(loop.MainSha0, Rev(loop.Checkout, "main"));
    }

    // ================= the world =================

    /// <summary>
    /// The whole loop, stood up once per test: a real user checkout, the real in-proc daemon pointed at an
    /// isolated VM root, the daemon-provisioned mirror, and the shipped client adapter bound to all three.
    /// </summary>
    private sealed class LoopWorld : IAsyncDisposable
    {
        private readonly MergeQueueEndToEndDockerTests _owner;
        private WebApplicationFactory<Program> _host = null!;
        private DaemonClient _client = null!;

        private LoopWorld(MergeQueueEndToEndDockerTests owner) => _owner = owner;

        public string VmRoot { get; private set; } = "";
        public string Checkout { get; private set; } = "";
        public string MirrorPath { get; private set; } = "";
        public string RepoHandle { get; private set; } = "";
        public string SyncRemoteName { get; private set; } = "";
        public string MainSha0 { get; private set; } = "";
        public string? Upstream { get; private set; }
        public string? UpstreamWork { get; private set; }
        public FakeHost? Host { get; private set; }

        public DaemonBackedOrchestrator Adapter { get; private set; } = null!;
        public MergeQueueService.MergeQueueServiceClient Merge { get; private set; } = null!;
        public AgentService.AgentServiceClient AgentRpc { get; private set; } = null!;
        public RepoSyncService.RepoSyncServiceClient Sync { get; private set; } = null!;
        public Metadata Headers { get; private set; } = null!;

        public MergeQueueContext Context =>
            _host.Services.GetRequiredService<IMergeQueueRegistry>().Resolve(RepoHandle)
            ?? throw new InvalidOperationException($"no live queue for '{RepoHandle}'");

        public MergeQueue Queue => Context.Queue;
        public IMergeLeaseStore Leases => _host.Services.GetRequiredService<IMergeLeaseStore>();

        public static async Task<LoopWorld> StartAsync(
            MergeQueueEndToEndDockerTests owner, bool withUpstreamHost = false)
        {
            var world = new LoopWorld(owner);
            await world.BuildAsync(withUpstreamHost);
            return world;
        }

        private async Task BuildAsync(bool withUpstreamHost)
        {
            VmRoot = _owner.NewDir("mg-e2e-vm-");
            Checkout = _owner.NewDir("mg-e2e-checkout-");

            if (withUpstreamHost)
            {
                // The host's repository, and a checkout of it that the fake host merges in.
                Upstream = _owner.NewDir("mg-e2e-upstream-");
                Git(Upstream, "-c", "init.defaultBranch=main", "init", "--bare");
                UpstreamWork = _owner.NewDir("mg-e2e-upstream-work-");
                Git(UpstreamWork, "clone", Upstream, ".");
                Identify(UpstreamWork);
                FixtureRepo.Seed(UpstreamWork);
                Git(UpstreamWork, "add", "-A");
                Git(UpstreamWork, "commit", "-m", "seed: node fixture project");
                Git(UpstreamWork, "branch", "-M", "main");
                Git(UpstreamWork, "push", "-u", "origin", "main");

                // The user's checkout is a clone of the host repository, so it has an `origin` for the
                // external reconcile to fetch the merged commit from.
                Git(Checkout, "clone", Upstream, ".");
                Identify(Checkout);
            }
            else
            {
                Git(Checkout, "-c", "init.defaultBranch=main", "init");
                Identify(Checkout);
                FixtureRepo.Seed(Checkout);
                Git(Checkout, "add", "-A");
                Git(Checkout, "commit", "-m", "seed: node fixture project");
                Git(Checkout, "branch", "-M", "main");
            }

            MainSha0 = Rev(Checkout, "main");

            // The real composition root, with ONE override: an isolated VM root so mirrors/worktrees never
            // touch the developer's ~/mainguard.
            _host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: VmRoot))));

            var channel = GrpcChannel.ForAddress(_host.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });
            Headers = new Metadata
            {
                { "authorization", $"bearer {_host.Services.GetRequiredService<SessionTokenFile>().Token}" },
            };
            Merge = new MergeQueueService.MergeQueueServiceClient(channel);
            AgentRpc = new AgentService.AgentServiceClient(channel);
            Sync = new RepoSyncService.RepoSyncServiceClient(channel);

            // The shipped provisioning RPC is the only thing that makes the repo active — the queue is
            // built by the daemon's own MergeQueueProvisioner, never hand-registered here.
            var provisioned = await Sync.ProvisionRepoAsync(
                new ProvisionRepoRequest { OriginUrl = Checkout }, Headers);
            RepoHandle = provisioned.RepoHandle;
            SyncRemoteName = provisioned.SyncRemoteName;
            MirrorPath = _host.Services.GetRequiredService<IAgentEnvironment>().Repos.BareRepoPathFor(RepoHandle);

            // What the Windows app does with the ProvisionRepo answer: register the sync remote on the
            // user's own checkout. (The URL the daemon hands back is the Windows-facing UNC form; on this
            // leg the mirror is reachable at its real path.)
            Git(Checkout, "remote", "add", SyncRemoteName, MirrorPath);
            Git(Checkout, "fetch", SyncRemoteName);

            _client = new DaemonClient(
                () => GrpcChannel.ForAddress(_host.Server.BaseAddress,
                    new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() }),
                () => _host.Services.GetRequiredService<SessionTokenFile>().Token);

            Host = withUpstreamHost ? new FakeHost(this) : null;
            Adapter = new DaemonBackedOrchestrator(
                _client, ownsClient: false, journalFactory: NewJournal, hostPullRequests: () => Host!);
            Adapter.SetActiveRepo(RepoHandle, Checkout, SyncRemoteName);
        }

        // ---- agents ----

        /// <summary>
        /// Spawns a real jailed worker through the shipped <c>SpawnAgent</c> RPC — session, worktree,
        /// per-agent repository, hardened container and merge-queue entry, all by the production chain.
        /// </summary>
        public async Task<JailedAgent> SpawnJailedAgentAsync()
        {
            var spawned = await AgentRpc.SpawnAgentAsync(new SpawnAgentRequest
            {
                RepoHandle = RepoHandle,
                AgentKind = "worker",
                ModelApiKey = "sk-test-not-a-real-key",
                TaskPrompt = "end-to-end merge queue fixture",
            }, Headers);

            var session = _host.Services.GetRequiredService<AgentSessionStore>().Find(spawned.AgentId);
            Assert.NotNull(session);
            Assert.False(string.IsNullOrEmpty(session!.ContainerId),
                "the spawn must produce a real jail — verification runs in the worker's own sandbox");
            _owner._containers.Add(session.ContainerId!);
            _owner._segments.Add((RepoHandle, spawned.AgentId));

            return new JailedAgent(spawned.AgentId, WorktreePathFor(spawned.AgentId));
        }

        public string WorktreePathFor(string agentId) => Path.Combine(VmRoot, "worktrees", RepoHandle, agentId);

        // ---- queue drive ----

        public Task<RunVerificationResponse> RunVerificationAsync(string agentId)
            => Merge.RunVerificationAsync(
                new RunVerificationRequest { RepoHandle = RepoHandle, AgentId = agentId }, Headers).ResponseAsync;

        public async Task WaitForQueueProjectionAsync(string agentId)
            => Assert.True(
                await WaitUntilAsync(() => Adapter.GetQueue().Any(q => q.AgentId == agentId), ProjectionWait),
                $"the queue projection never carried '{agentId}'");

        // ---- external PR entries ----

        /// <summary>
        /// The state the P2-12 intake leaves an upstream pull request in: a mirror branch at the PR head, a
        /// worktree, and an <c>External</c> queue entry. Driven through the shipped RPC + the daemon's own
        /// provisioner rather than by hand-registering, so the entry is the one the daemon serves.
        /// </summary>
        /// <param name="withJail">
        /// Whether the entry is given a verification jail. The intake gives it none (see
        /// <see cref="ExternalPullRequestEntry_WithNoJail_CannotVerify_BecauseTheIntakeSpawnsNone"/>), so
        /// <c>true</c> supplies the one piece of wiring that is missing — and supplies it by running the
        /// SAME production spawn chain a local agent takes (<see cref="SandboxAgentLauncher"/>), not by
        /// hand-rolling a container.
        /// </param>
        public async Task<ExternalPr> MaterializeExternalPrAsync(int prNumber, bool withJail)
        {
            var agentId = "pr-" + prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // The bot's pull request on the host.
            Git(UpstreamWork!, "checkout", "-b", "pr-head");
            File.WriteAllText(Path.Combine(UpstreamWork!, "src", "calc.js"),
                FixtureRepo.CalcJs + "exports.div = (a, b) => a / b;\n");
            Git(UpstreamWork!, "add", "-A");
            Git(UpstreamWork!, "commit", "-m", "bot: division");
            Git(UpstreamWork!, "push", "-u", "origin", "pr-head");
            var head = Rev(UpstreamWork!, "pr-head");
            Git(UpstreamWork!, "checkout", "main");

            // The intake's materialize: the agent worktree + branch first…
            if (withJail)
            {
                var launch = await new SandboxAgentLauncher(
                        _host.Services.GetRequiredService<IAgentEnvironment>())
                    .TryLaunchAsync(RepoHandle, agentId, "worker", "sk-test-not-a-real-key",
                        ipcDirPath: null, ct: CancellationToken.None);
                Assert.NotNull(launch);
                _owner._containers.Add(launch!.ContainerId);
                _owner._segments.Add((RepoHandle, agentId));
            }
            else
            {
                await Sync.CreateWorktreeAsync(
                    new CreateWorktreeRequest { RepoHandle = RepoHandle, AgentId = agentId }, Headers);
            }

            // …then the pull request's head fetched onto that branch in the MIRROR (the daemon-side
            // provisioning fetch — the quarantine rule cuts the agent worktree off from the real remote,
            // never this), and the entry stamped External.
            Git(MirrorPath, "fetch", Upstream!, $"+refs/heads/pr-head:refs/heads/agent/{agentId}");
            _host.Services.GetRequiredService<MergeQueueProvisioner>()
                .EnsureEntry(RepoHandle, agentId, MergeEntryOrigin.External);

            Git(Checkout, "fetch", SyncRemoteName);
            Assert.Equal(head, Rev(Checkout, $"refs/remotes/{SyncRemoteName}/agent/{agentId}"));
            return new ExternalPr(agentId, prNumber, head);
        }

        // ---- daemon restart ----

        /// <summary>The daemon's session record for an agent (null once a restart has forgotten it).</summary>
        public AgentSession? SessionFor(string agentId)
            => _host.Services.GetRequiredService<AgentSessionStore>().Find(agentId);

        /// <summary>
        /// Replaces the daemon with a fresh one over the SAME VM root, leaving every jail running — a
        /// daemon restart as the box actually experiences it. The client adapter is rebound to the new
        /// host, and the repo is re-provisioned exactly as the app does when it reopens a repository.
        /// </summary>
        public async Task RestartDaemonAsync()
        {
            Adapter.Dispose();
            _client.Dispose();
            _host.Dispose();

            _host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: VmRoot))));

            var channel = GrpcChannel.ForAddress(_host.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });
            Headers = new Metadata
            {
                { "authorization", $"bearer {_host.Services.GetRequiredService<SessionTokenFile>().Token}" },
            };
            Merge = new MergeQueueService.MergeQueueServiceClient(channel);
            AgentRpc = new AgentService.AgentServiceClient(channel);
            Sync = new RepoSyncService.RepoSyncServiceClient(channel);

            var reprovisioned = await Sync.ProvisionRepoAsync(
                new ProvisionRepoRequest { OriginUrl = Checkout }, Headers);
            Assert.Equal(RepoHandle, reprovisioned.RepoHandle);

            _client = new DaemonClient(
                () => GrpcChannel.ForAddress(_host.Server.BaseAddress,
                    new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() }),
                () => _host.Services.GetRequiredService<SessionTokenFile>().Token);
            Adapter = new DaemonBackedOrchestrator(
                _client, ownsClient: false, journalFactory: NewJournal, hostPullRequests: () => Host!);
            Adapter.SetActiveRepo(RepoHandle, Checkout, SyncRemoteName);
        }

        // ---- teardown ----

        public async ValueTask DisposeAsync()
        {
            try { Adapter?.Dispose(); } catch { /* never fail a test from cleanup */ }
            try { _client?.Dispose(); } catch { /* never fail a test from cleanup */ }
            await Task.Yield();
            try { _host?.Dispose(); } catch { /* never fail a test from cleanup */ }
        }

        private IOperationJournal NewJournal()
        {
            var dbPath = Path.Combine(_owner.NewDir("mg-e2e-journal-"), "journal.db");
            Func<AppDbContext> factory = () => new AppDbContext(dbPath);
            using (var db = factory())
            {
                db.Database.EnsureCreated();
            }

            return new OperationJournal(factory);
        }
    }

    /// <summary>A real jailed agent: its id and its worktree on the daemon's ext4 root.</summary>
    private sealed record JailedAgent(string Id, string WorktreePath)
    {
        /// <summary>Writes a tracked file into the agent's worktree (staged by the next commit).</summary>
        public void Write(string relPath, string content)
        {
            var full = Path.Combine(WorktreePath, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>An UNTRACKED file in the worktree — present to the jail, absent from every git tree.</summary>
        public void WriteUntracked(string relPath, string content) => Write(relPath, content);

        public void DeleteUntracked(string relPath)
        {
            var full = Path.Combine(WorktreePath, relPath);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }

        /// <summary>Commits in the AGENT's own repository (MG-3) and returns the new tip.</summary>
        public string Commit(string relPath, string content, string message)
        {
            Write(relPath, content);
            Git(WorktreePath, "add", "-A");
            Git(WorktreePath, "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local",
                "commit", "-m", message);
            return Rev(WorktreePath, "HEAD");
        }
    }

    private sealed record ExternalPr(string AgentId, int Number, string HeadSha);

    /// <summary>
    /// Stands in for the host's pull-request API. Its merge performs a REAL git merge in the real upstream
    /// repository, which is what lets the external assertions be about refs rather than about a mock.
    /// </summary>
    private sealed class FakeHost : Mainguard.Agents.Services.IHostPullRequestGateway
    {
        private readonly LoopWorld _world;

        public FakeHost(LoopWorld world) => _world = world;

        public string? LastExpectedHeadSha { get; private set; }

        public Task<Mainguard.Git.Models.PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
            => Task.FromResult(new Mainguard.Git.Models.PullRequestDetail
            {
                Summary = new Mainguard.Git.Models.PullRequestItem
                {
                    Number = number,
                    State = Mainguard.Git.Models.PullRequestState.Open,
                    HeadSha = Rev(_world.UpstreamWork!, "pr-head"),
                },
                Mergeable = true,
                MergeableState = "clean",
            });

        public Task<Mainguard.Git.Models.PullRequestItem> MergeAsync(
            string repoPath, int number, string expectedHeadSha,
            Mainguard.Git.Models.PullRequestMergeMethod method, CancellationToken ct)
        {
            LastExpectedHeadSha = expectedHeadSha;
            Git(_world.UpstreamWork!, "checkout", "main");
            Git(_world.UpstreamWork!, "merge", "--no-ff", "--no-edit", "pr-head");
            Git(_world.UpstreamWork!, "push", "origin", "main");
            return Task.FromResult(new Mainguard.Git.Models.PullRequestItem
            {
                Number = number,
                State = Mainguard.Git.Models.PullRequestState.Merged,
                MergeCommitSha = Rev(_world.UpstreamWork!, "main"),
            });
        }
    }

    // ================= the fixture project =================

    /// <summary>
    /// The Node project every world is seeded with. Its verification command is a single argv line (no
    /// shell, no <c>&amp;&amp;</c>) naming a real test file that can genuinely fail — which is what makes
    /// the pass/fail assertions non-vacuous.
    /// </summary>
    private static class FixtureRepo
    {
        public const string VerifyCommand = "node .mainguard/verify.js";
        public const string PassMarker = "fixture verification passed";

        /// <summary>An UNTRACKED, worktree-only dwell knob. Untracked on purpose: anything in the tree
        /// would differ between a branch and main and arm the RT-D2 gate, which is a different test.</summary>
        public const string DelayFile = ".verify-delay-ms";

        public const string CalcJs =
            "exports.add = (a, b) => a + b;\n" +
            "exports.mul = (a, b) => a * b;\n";

        private const string VerifyJs = """
            // The fixture project's test suite. Its EXIT CODE is the only thing the merge queue reads
            // (OPS SA-1: the daemon-observed container-runtime exit), so it must be able to fail.
            const assert = require('node:assert');
            const fs = require('node:fs');
            const path = require('node:path');

            // An untracked, worktree-only dwell so a test can keep the re-verification window observable.
            const delayFile = path.join(__dirname, '..', '.verify-delay-ms');
            if (fs.existsSync(delayFile)) {
              const ms = parseInt(fs.readFileSync(delayFile, 'utf8').trim(), 10) || 0;
              if (ms > 0) {
                Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
              }
            }

            const calc = require('../src/calc.js');
            assert.strictEqual(calc.add(2, 2), 4);
            assert.strictEqual(calc.mul(3, 4), 12);
            console.log('fixture verification passed');

            """;

        public static void Seed(string repoPath)
        {
            WriteFile(repoPath, ".mainguard/verify", VerifyCommand + "\n");
            WriteFile(repoPath, ".mainguard/verify.js", VerifyJs);
            WriteFile(repoPath, "src/calc.js", CalcJs);
            WriteFile(repoPath, "README.md", "A tiny Node project the merge queue can actually verify.\n");
        }

        private static void WriteFile(string root, string relPath, string content)
        {
            var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    // ================= helpers =================

    /// <param name="within">How long to wait. Defaults to the generous whole-loop timeout; the projection
    /// waits pass a short one so a genuine regression reports in seconds instead of minutes.</param>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? Timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
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

    /// <summary>Exit of <c>merge-base --is-ancestor</c> — 0 when <paramref name="commit"/> is reachable
    /// from <paramref name="from"/>. Asserted on directly so the reachability check is git's, not ours.</summary>
    private static int Contains(string repo, string commit, string from)
        => AgentTestGit.Run(repo, "merge-base", "--is-ancestor", commit, from).Code;

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    public async Task DisposeAsync()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        foreach (var id in _containers)
        {
            try { await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); }
            catch { /* never fail a test from cleanup */ }
        }

        // MG-36: every spawn asks for its own default-deny segment, and Docker's default local bridge pool
        // is only ~32 networks deep. A suite that leaks one per agent runs fine on a clean box and then
        // fails EVERY later spawn on the same machine with "all predefined address pools have been fully
        // subnetted" — a failure with nothing to do with the test that hits it. Reclaim them by name; a
        // sweep by prefix also catches the ones an aborted test never got to register.
        var egress = new EgressProxyConfigurator(docker, EgressAllowlist.WithDefaults(new InMemoryAuditLog()));
        foreach (var (repoHash, agentId) in _segments)
        {
            try { await egress.RemoveAgentSegmentAsync(repoHash, agentId); }
            catch { /* never fail a test from cleanup */ }
        }

        try
        {
            foreach (var net in await docker.Networks.ListNetworksAsync())
            {
                if (net.Name is null
                    || !net.Name.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal)
                    || net.Containers is { Count: > 0 })
                {
                    continue;
                }

                try { await docker.Networks.DeleteNetworkAsync(net.ID); }
                catch { /* another suite's live segment — leave it alone */ }
            }
        }
        catch { /* never fail a test from cleanup */ }

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
            catch { /* never fail a test from cleanup */ }
        }
    }
}
