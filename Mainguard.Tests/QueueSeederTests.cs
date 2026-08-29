using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The dev-only queue seeder (docs/design/queue-seeding.md), over a REAL bare mirror provisioned from
/// a REAL origin checkout — the same fixture posture as <see cref="MergeQueueProvisionerTests"/>,
/// because the property under test is precisely that seeded entries travel the production wiring.
///
/// <para>The sandbox engine in every test THROWS on use and <c>resolveContainerId</c> answers null:
/// the suite passing is itself the proof that seeding needs no jail and executes nothing — the
/// forgery rule's structural half. The record half (the visible provenance marker) is asserted
/// directly.</para>
/// </summary>
public sealed class QueueSeederTests : IDisposable
{
    private const string Actor = "test-operator";

    private readonly string _vmRoot = NewDir("mainguard-seed-vm-");
    private readonly string _source = NewDir("mainguard-seed-src-");
    private readonly Mainguard.Git.Audit.InMemoryAuditLog _audit = new();
    private readonly SyntheticVerificationRegistry _synthetic = new();
    private readonly MergeQueueRegistry _registry = new();

    // The phase-2 plan pipeline, wired exactly as the daemon wires it (design §9): one PlanApprovalService,
    // one WorkerPlanGate over it, that gate ANDed into every queue as an IMergeGate, and the provisioner's
    // SA-1/F6 scope lookup reading APPROVED plans out of that same service.
    private readonly PlanApprovalService _plans;
    private readonly WorkerPlanGate _planGate;

    public QueueSeederTests()
    {
        _plans = new PlanApprovalService(audit: _audit);
        _planGate = new WorkerPlanGate(_plans, _audit);
    }

    // ---- Static targets --------------------------------------------------

    [Fact]
    public async Task SeedWorking_CreatesARealEntryOverARealBranch()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Working) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("", outcome.Refusal);
        Assert.StartsWith(QueueSeeder.IdPrefix, outcome.AgentId, StringComparison.Ordinal);
        Assert.Equal("Working", outcome.ReachedState);

        // The branch is REAL: the mirror carries agent/<id> one commit ahead of main.
        var ctx = _registry.Resolve(repoHash)!;
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(outcome.AgentId));
        Assert.NotNull(RevParse(BarePath(repoHash), "refs/heads/agent/" + outcome.AgentId));

        // ...and the audit chain records the entry as synthetic input.
        Assert.Contains(_audit.Read(), e => e.Type == QueueSeeder.SeededEvent
            && e.Fields["agent"] == outcome.AgentId && e.Fields["by"] == Actor);
    }

    [Fact]
    public async Task SeedVerified_IsMergeable_AndItsRecordIsVisiblySynthetic()
    {
        var (seeder, repoHash, verifications) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("Verified", outcome.ReachedState);

        var ctx = _registry.Resolve(repoHash)!;
        Assert.True(ctx.Queue.CanMerge(outcome.AgentId, out var reason), reason);

        // The forgery rule's record half: the immutable record says, itself, that no run happened.
        var record = verifications.Latest(repoHash, outcome.AgentId)!;
        Assert.True(record.Passed);
        Assert.EndsWith(SyntheticVerificationPlan.SeededProvenanceMarker, record.ResolvedCommand, StringComparison.Ordinal);
        Assert.Equal(ctx.Queue.CurrentMainSha, record.MainSha);
        Assert.Contains("NO RUN WAS EXECUTED", File.ReadAllText(record.LogArtifactPath));
    }

    [Fact]
    public async Task SeedVerifyFail_SettlesToVerificationFailed_ThroughTheRealFailurePath()
    {
        var (seeder, repoHash, verifications) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Working, VerificationFails: true) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        // H2 — the seeded failure lands where a real one now does. The SPEC still says `Working` (that is
        // the spelling the seeding RPC and its UI send), but the state it reaches is the honest one; the
        // failure is no longer indistinguishable from an entry nobody ran.
        Assert.Equal("VerificationFailed", outcome.ReachedState);

        var ctx = _registry.Resolve(repoHash)!;
        Assert.False(ctx.Queue.CanMerge(outcome.AgentId, out var reason));
        Assert.Contains("FAILED", reason);
        Assert.False(verifications.Latest(repoHash, outcome.AgentId)!.Passed);
    }

    [Fact]
    public async Task SeedFlagged_IsBlockedByTheRealGate_UntilAcknowledged()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, Flavor: SeedFlavor.Flagged) },
            Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("Verified", outcome.ReachedState);

        // The REAL classifier flagged the workflow file the seeded commit really touches; nothing
        // was injected. Acknowledging through the real store clears the real gate.
        var ctx = _registry.Resolve(repoHash)!;
        Assert.False(ctx.Queue.CanMerge(outcome.AgentId, out var reason));
        var store = ctx.FlaggedChanges!.PeekStore(outcome.AgentId)!;
        var item = Assert.Single(store.Items);
        Assert.Contains("workflows", item.Path);

        store.Acknowledge(item.Id);
        Assert.True(ctx.Queue.CanMerge(outcome.AgentId, out reason), reason);
    }

    [Fact]
    public async Task SeedChangedTestCommand_ArmsTheRealRtD2Gate()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, Flavor: SeedFlavor.ChangedTestCommand) },
            Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        var ctx = _registry.Resolve(repoHash)!;
        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(outcome.AgentId));
        Assert.False(ctx.Queue.CanMerge(outcome.AgentId, out var reason));
        Assert.Contains("test command changed", reason);
    }

    // ---- The plan dimension (design §9) ----------------------------------

    /// <summary>
    /// A seed WITHOUT <c>WithPlan</c> is outside the plan gate entirely, and both halves of that matter:
    /// the gate's unheld-id default lets it merge (a seeded entry is no more coordinator-delegated than a
    /// manual-mode agent), and the stricter auto-verify predicate refuses it, so no automatic caller ever
    /// fires a verification at an entry that has no agent and no jail behind it.
    ///
    /// <para>These are the same two properties <c>SeedingCompatibilityTests</c> pins against the bare gate;
    /// asserted here against an id the seeder REALLY produced, because "the gate is permissive for unheld
    /// ids" only protects seeding if seeded ids are actually unheld.</para>
    /// </summary>
    [Fact]
    public async Task ASeedWithoutAPlan_IsOutsideThePlanGate_Permitted_AndAutoVerifyIneligible()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified) }, Actor, CancellationToken.None);
        var agentId = Assert.Single(report.Results).AgentId;

        Assert.True(_planGate.Allows(agentId, out var allowReason), allowReason);
        Assert.False(_planGate.MayAutoVerify(agentId, out var autoReason));
        Assert.Contains("not a plan-gated worker", autoReason);
        Assert.Null(_plans.LatestForWorker(agentId));

        // ...and it really is mergeable — the permissive default is load-bearing, not decorative.
        Assert.True(_registry.Resolve(repoHash)!.Queue.CanMerge(agentId, out var mergeReason), mergeReason);
    }

    /// <summary>
    /// <c>WithPlan</c> drives the REAL pipeline — <see cref="WorkerPlanGate.Hold"/> →
    /// <see cref="PlanApprovalService.Present"/> → <see cref="PlanApprovalService.Approve"/> — so the id
    /// becomes a genuinely plan-gated worker: the gate holds it, the plan record exists with a real
    /// approver and a real approval event, and the merge gate now passes it because that plan was
    /// APPROVED rather than because the gate never heard of it.
    ///
    /// <para>The one synthetic fact is authorship (no worker inspected anything), and the record says so
    /// about itself — the plan twin of the verification record's "[seeded — not executed]".</para>
    /// </summary>
    [Fact]
    public async Task SeedWithPlan_DrivesTheRealPlanPipeline_AndTheRecordSaysItIsSeeded()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, WithPlan: true) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("", outcome.Refusal);
        Assert.Equal("Verified", outcome.ReachedState);

        // Held for real: the id is now inside the gate, so the auto-verify predicate answers as it does
        // for any approved worker (nothing ARMS the trigger for it — the mirror is not watched).
        Assert.True(_planGate.MayAutoVerify(outcome.AgentId, out var autoReason), autoReason);
        Assert.True(_plans.HasApprovedPlan(outcome.AgentId));
        Assert.True(_planGate.Allows(outcome.AgentId, out var allowReason), allowReason);

        var plan = _plans.LatestForWorker(outcome.AgentId)!;
        Assert.Equal(PlanStatus.Approved, plan.Status);
        Assert.Equal(Actor, plan.ApproverIdentity);
        Assert.Equal(QueueSeeder.SeededCoordinatorId, plan.CoordinatorId);
        Assert.Contains(QueueSeeder.SeededPlanMarker, plan.Plan.Approach);
        Assert.Contains(QueueSeeder.SeededPlanMarker, plan.Plan.TestStrategy);

        // The real pipeline's own audit events, not the seeder's paraphrase of them.
        Assert.Contains(_audit.Read(), e => e.Type == "worker_task_withheld" && e.Fields["worker_agent_id"] == outcome.AgentId);
        Assert.Contains(_audit.Read(), e => e.Type == "plan_presented" && e.Fields["worker_agent_id"] == outcome.AgentId);
        Assert.Contains(_audit.Read(), e => e.Type == "plan_approved" && e.Fields["worker_agent_id"] == outcome.AgentId);
        Assert.Contains(_audit.Read(), e => e.Type == QueueSeeder.SeededEvent
            && e.Fields["agent"] == outcome.AgentId && e.Fields["with_plan"] == "true"
            && e.Fields["plan_id"] == plan.PlanId);

        // Default scope is the seed's own path, so with_plan alone changes nothing about mergeability.
        Assert.True(_registry.Resolve(repoHash)!.Queue.CanMerge(outcome.AgentId, out var mergeReason), mergeReason);
    }

    /// <summary>
    /// The arm this parameter exists for (SA-1/F6): a plan-gated seed whose approved scope does NOT cover
    /// what its commit touches gets a real <see cref="FlaggedKind.OutOfApprovedScope"/> must-acknowledge
    /// item and cannot merge until a human acknowledges it. Nothing is injected — the item is produced by
    /// the same <c>ArmFlaggedChangeReview</c> pass a real branch's verification runs, comparing the real
    /// merge diff against the real approved plan's scope.
    /// </summary>
    [Fact]
    public async Task SeedWithPlan_OutsideItsScope_ArmsTheRealOutOfApprovedScopeItem()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, WithPlan: true, Scope: new[] { "docs/" }) },
            Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("Verified", outcome.ReachedState);
        Assert.Equal(new[] { "docs/" }, _plans.LatestForWorker(outcome.AgentId)!.Plan.Scope);

        var ctx = _registry.Resolve(repoHash)!;
        Assert.False(ctx.Queue.CanMerge(outcome.AgentId, out _));

        var store = ctx.FlaggedChanges!.PeekStore(outcome.AgentId)!;
        var item = Assert.Single(store.Items, i => i.Kind == Mainguard.Git.Review.FlaggedKind.OutOfApprovedScope);
        Assert.Equal($"seed/{outcome.AgentId}.txt", item.Path);

        store.Acknowledge(item.Id);
        Assert.True(ctx.Queue.CanMerge(outcome.AgentId, out var reason), reason);
    }

    /// <summary>
    /// Clearing releases the plan gate's hold, so the id returns to the permitted/ineligible pair every
    /// unheld id has. The decided plan RECORD survives — a decided plan is history, exactly as it is for a
    /// real worker whose spawn service dropped its hold at teardown — and a seed id is a fresh guid, so
    /// nothing can inherit it.
    /// </summary>
    [Fact]
    public async Task Clearing_ReleasesThePlanGatesHold_ButKeepsTheDecidedPlanAsHistory()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, WithPlan: true) }, Actor, CancellationToken.None);
        var agentId = Assert.Single(report.Results).AgentId;
        Assert.True(_planGate.MayAutoVerify(agentId, out _));

        await seeder.ClearAsync(repoHash, Actor);

        Assert.False(_planGate.MayAutoVerify(agentId, out var reason));
        Assert.Contains("not a plan-gated worker", reason);
        Assert.True(_planGate.Allows(agentId, out _));
        Assert.Equal(PlanStatus.Approved, _plans.LatestForWorker(agentId)!.Status);
    }

    /// <summary>
    /// A substrate without the plan pipeline refuses a <c>WithPlan</c> spec verbatim and leaves NOTHING
    /// behind — no branch, no registry row, no entry. A half-seeded entry that looked plan-gated and was
    /// not is precisely the fabricated state this tool exists to never produce.
    /// </summary>
    [Fact]
    public async Task WithPlan_OnASubstrateWithoutThePlanPipeline_IsRefused_AndSeedsNothing()
    {
        var (_, repoHash, _) = Provision();
        var repos = new RepoProvisioner(_vmRoot);
        var planless = new QueueSeeder(
            new MergeQueueProvisioner(
                registry: _registry, repos: repos, leases: new InMemoryMergeLeaseStore(),
                resolveContainerId: (_, _) => null,
                queueStore: _ => new InMemoryMergeQueueStore(),
                verificationStore: _ => new InMemoryVerificationStore(),
                sandboxes: new ThrowingSandboxEngine(),
                artifactDirectory: NewDir("mainguard-seed-artifacts-"),
                mergeDiff: new MergeBranchDiffService(repos, (_, _) => true),
                syntheticVerifications: _synthetic),
            _registry, _synthetic, repos);

        Assert.False(planless.CanSeedPlans);
        var report = await planless.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified, WithPlan: true) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Contains("no coordinator plan pipeline", outcome.Refusal);
        Assert.Equal("", outcome.ReachedState);
        Assert.Null(RevParse(BarePath(repoHash), "refs/heads/agent/" + outcome.AgentId));
        Assert.DoesNotContain(_registry.Resolve(repoHash)!.Queue.Agents, id => id == outcome.AgentId);
    }

    [Fact]
    public async Task SeedReviewFamily_ReachesAwaitingReviewRejectedAndDiscarded()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash, new[]
        {
            new SeedSpec(WorkerMergeState.AwaitingReview),
            new SeedSpec(WorkerMergeState.Rejected, Reason: "seeded review no"),
            new SeedSpec(WorkerMergeState.Discarded, Reason: "seeded tidy-up"),
        }, Actor, CancellationToken.None);

        Assert.Equal(new[] { "AwaitingReview", "Rejected", "Discarded" },
            report.Results.Select(r => r.ReachedState).ToArray());
        Assert.All(report.Results, r => Assert.Equal("", r.Refusal));

        // The discard record carries the daemon-derived actor, like any human discard.
        var ctx = _registry.Resolve(repoHash)!;
        var discarded = report.Results[2].AgentId;
        Assert.Equal(Actor, ctx.Queue.GetDiscard(discarded)!.By);
    }

    // ---- Holds -----------------------------------------------------------

    [Fact]
    public async Task SeedVerifying_IsGenuinelyInFlight_AndClearDrainsItWithoutResurrection()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verifying, HoldSeconds: 60) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("Verifying", outcome.ReachedState);

        var ctx = _registry.Resolve(repoHash)!;
        // Genuinely in flight: the in-flight set says so, and the human's escape hatch refuses with
        // the honest "wait" — the same measurements the wire serves.
        Assert.True(ctx.Queue.IsVerificationInFlight(outcome.AgentId));
        Assert.False(ctx.Queue.TryClearStalledVerification(outcome.AgentId, Actor, out var refusal));
        Assert.Contains("running", refusal);

        // Clear drains the hold BEFORE deleting the row (the resurrection-ordering rule), and the id
        // is gone from both live and discarded views afterwards — not re-minted at Working.
        var clear = await seeder.ClearAsync(repoHash, Actor);
        Assert.Contains(outcome.AgentId, clear.Cleared);
        Assert.Empty(clear.Failures);
        Assert.DoesNotContain(outcome.AgentId, ctx.Queue.Agents);
        Assert.DoesNotContain(outcome.AgentId, ctx.Queue.DiscardedAgents);
        Assert.Null(RevParse(BarePath(repoHash), "refs/heads/agent/" + outcome.AgentId));
        Assert.Null(_synthetic.TryGet(repoHash, outcome.AgentId));
    }

    // ---- Dynamics --------------------------------------------------------

    [Fact]
    public async Task SeedStalePair_MergedSpecReallyStalesTheEarlierVerifiedSeed()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash, new[]
        {
            new SeedSpec(WorkerMergeState.Verified, StaleBehavior: SyntheticStaleBehavior.Hold),
            new SeedSpec(WorkerMergeState.Merged),
        }, Actor, CancellationToken.None);

        Assert.All(report.Results, r => Assert.Equal("", r.Refusal));
        var ctx = _registry.Resolve(repoHash)!;
        var (stale, merged) = (report.Results[0].AgentId, report.Results[1].AgentId);

        // The merge is REAL: origin main advanced to the seeded branch's commit, the queue main
        // followed, and the co-seeded Verified entry was staled by the real cascade and HELD there.
        Assert.Equal(WorkerMergeState.Merged, ctx.Queue.GetState(merged));
        Assert.Equal(WorkerMergeState.StaleVerified, ctx.Queue.GetState(stale));
        Assert.Equal(ctx.Queue.CurrentMainSha, RevParse(_source, "HEAD"));
    }

    [Fact]
    public async Task SeedStaleVerified_AdvancesMainOutOfBand_AndHoldsTheEntryStale()
    {
        var (seeder, repoHash, _) = Provision();

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.StaleVerified) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Equal("", outcome.Refusal);
        Assert.Equal("StaleVerified", outcome.ReachedState);
    }

    [Fact]
    public async Task SeedMerged_IsRefusedVerbatim_WhenTheOriginCheckoutIsNotOnMain()
    {
        var (seeder, repoHash, _) = Provision();
        using (var repo = new Repository(_source))
        {
            Commands.Checkout(repo, repo.CreateBranch("elsewhere"));
        }

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Merged) }, Actor, CancellationToken.None);

        var outcome = Assert.Single(report.Results);
        Assert.Contains("not on", outcome.Refusal);
        // The walk stopped where it was refused: verified, never merged, lease handed back.
        Assert.Equal("Verified", outcome.ReachedState);
        Assert.Null(_registry.Resolve(repoHash)!.Leases.GetOutstanding(repoHash));
    }

    [Fact]
    public async Task PushCommits_ReallyInvalidatesAVerifiedSeed()
    {
        var (seeder, repoHash, _) = Provision();
        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified) }, Actor, CancellationToken.None);
        var agentId = report.Results[0].AgentId;
        var oldTip = RevParse(BarePath(repoHash), "refs/heads/agent/" + agentId);

        var push = seeder.PushCommits(repoHash, agentId, count: 2);

        Assert.True(push.Pushed, push.Refusal);
        Assert.NotEqual(oldTip, push.NewTipSha);
        Assert.Equal(push.NewTipSha, RevParse(BarePath(repoHash), "refs/heads/agent/" + agentId));
        // The real NotifyNewCommits: evidence cleared, entry back to Working.
        Assert.Equal("Working", push.State);
        Assert.False(_registry.Resolve(repoHash)!.Queue.CanMerge(agentId, out _));
    }

    [Fact]
    public void PushCommits_RefusesANonSeededId()
    {
        var (seeder, repoHash, _) = Provision();
        var push = seeder.PushCommits(repoHash, "real-agent", 1);
        Assert.False(push.Pushed);
        Assert.Contains("not a seeded entry", push.Refusal);
    }

    // ---- The verify-config auto-provision --------------------------------

    [Fact]
    public async Task SeedVerified_ProvisionsAMissingVerifyConfig_Loudly()
    {
        var (seeder, repoHash, _) = Provision(withVerifyConfig: false);

        var report = await seeder.SeedAsync(repoHash,
            new[] { new SeedSpec(WorkerMergeState.Verified) }, Actor, CancellationToken.None);

        Assert.True(report.ProvisionedVerifyConfig);
        Assert.Equal("", Assert.Single(report.Results).Refusal);
        // The config landed as a REAL commit on origin main.
        Assert.Equal("true\n",
            File.ReadAllText(Path.Combine(_source, ".mainguard", "verify")));
    }

    // ---- The registry's own boundary -------------------------------------

    [Fact]
    public void Registry_RefusesAPlanForANonSeedId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _synthetic.Register("repo", "real-agent", new SyntheticVerificationPlan(passed: true)));
        Assert.Contains("seed-", ex.Message);
    }

    // ---- Fixture ---------------------------------------------------------

    private (QueueSeeder Seeder, string RepoHash, InMemoryVerificationStore Verifications) Provision(
        bool withVerifyConfig = true)
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, "README.md", "seed fixture\n", "init");
        if (withVerifyConfig)
        {
            WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, "npm test\n", "seed verify config");
        }

        var repos = new RepoProvisioner(_vmRoot);
        var repoHash = repos.Provision(_source).RepoHash;

        var verifications = new InMemoryVerificationStore();
        var provisioner = new MergeQueueProvisioner(
            registry: _registry,
            repos: repos,
            leases: new InMemoryMergeLeaseStore(),
            // No jail exists and none may be asked for: seeded verifications must never resolve one.
            resolveContainerId: (_, _) => null,
            queueStore: _ => new InMemoryMergeQueueStore(),
            verificationStore: _ => verifications,
            sandboxes: new ThrowingSandboxEngine(),
            artifactDirectory: NewDir("mainguard-seed-artifacts-"),
            mergeDiff: new MergeBranchDiffService(repos, (_, _) => true),
            audit: _audit,
            planGate: _planGate,
            // SA-1/F6, read the way the composition root reads it: APPROVED plans only, keyed by the
            // worker's own agent id — which for a seeded entry is the seed id itself.
            resolveApprovedPlan: agentId =>
                _plans.LatestForWorker(agentId) is { Status: PlanStatus.Approved } approved
                    ? approved.Plan
                    : null,
            syntheticVerifications: _synthetic);

        return (
            new QueueSeeder(provisioner, _registry, _synthetic, repos, log: null, plans: _plans, planGate: _planGate),
            repoHash,
            verifications);
    }

    private string BarePath(string repoHash) => new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash);

    private static void WriteAndCommit(string repoPath, string relativePath, string content, string message)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.UtcNow);
        repo.Commit(message, sig, sig);
    }

    private static string? RevParse(string repoPath, string reference)
    {
        using var repo = new Repository(repoPath);
        return repo.Lookup<Commit>(reference)?.Sha;
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        TryDelete(_vmRoot);
        TryDelete(_source);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // Loose object files are read-only; clear attributes so the delete succeeds.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>Seeding executes nothing — an engine call IS a test failure.</summary>
    private sealed class ThrowingSandboxEngine : ISandboxEngine
    {
        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never touch the sandbox engine");

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never exec in a jail");

        public Task PauseAsync(string containerId, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never pause a jail");

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never unpause a jail");

        public Task StopAsync(string containerId, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never stop a jail");

        public Task RemoveAsync(string containerId, CancellationToken ct = default)
            => throw new InvalidOperationException("the queue seeder must never remove a jail");
    }
}
