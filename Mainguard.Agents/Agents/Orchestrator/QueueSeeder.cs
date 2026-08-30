using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>What a seeded branch's real commit changes (docs/design/queue-seeding.md §2). The flavor
/// is what makes gate classification REAL rather than injected: the branch actually touches (or does
/// not touch) the paths the real classifiers look at.</summary>
public enum SeedFlavor
{
    /// <summary>An inert <c>seed/&lt;id&gt;.txt</c> — nothing flagged; <c>CanMerge</c> can genuinely pass.</summary>
    Plain,

    /// <summary>Adds a CI workflow file, which the real <c>RiskClassifier</c>/<c>FlaggedChangeDetector</c>
    /// must flag — real must-acknowledge items reach the wire.</summary>
    Flagged,

    /// <summary>Edits <c>.mainguard/verify</c> so it drifts from main's baseline — the real RT-D2
    /// <c>ChangedTestCommandGate</c> arms.</summary>
    ChangedTestCommand,
}

/// <summary>One seed request: reach <paramref name="TargetState"/> for <paramref name="Count"/> new
/// entries. Specs are processed in order, and order is semantic — a <c>Merged</c> spec really moves
/// main and thereby really stales every earlier <c>Verified</c> seed.</summary>
/// <param name="WithPlan">
/// Drive the coordinator phase-2 plan pipeline for this entry FOR REAL (design §9): the daemon holds a
/// task for the synthetic id, a plan is presented against the one-live-plan-per-worker invariant, and a
/// human approval is recorded. Without it the seeded id is outside the plan gate entirely — which is the
/// property the seeder has always relied on and which is now pinned directly.
/// </param>
/// <param name="Scope">
/// <see cref="WithPlan"/> only: the approved plan's scope patterns. Null/empty means "the path this
/// seed's own commit touches", so a plan-gated seed is IN scope and merges; any other pattern set puts
/// the seeded commit outside its approved scope and arms the real out-of-approved-scope must-ack item.
/// </param>
public sealed record SeedSpec(
    WorkerMergeState TargetState,
    int Count = 1,
    SeedFlavor Flavor = SeedFlavor.Plain,
    bool VerificationFails = false,
    int HoldSeconds = 0,
    SyntheticStaleBehavior StaleBehavior = SyntheticStaleBehavior.Hold,
    string Reason = "",
    bool WithPlan = false,
    IReadOnlyList<string>? Scope = null);

/// <summary>One seeded entry's outcome. <paramref name="Refusal"/> is empty on success and verbatim
/// otherwise — refusal-as-response, per the wire convention of the queue's own RPCs.</summary>
public sealed record SeedOutcome(string AgentId, string ReachedState, string Refusal);

/// <summary>One batch's outcome.</summary>
public sealed record SeedBatchReport(
    IReadOnlyList<SeedOutcome> Results, string MainSha, bool ProvisionedVerifyConfig);

/// <summary>One <see cref="QueueSeeder.PushCommits"/> outcome.</summary>
public sealed record PushCommitsReport(bool Pushed, string Refusal, string NewTipSha, string State);

/// <summary>One <see cref="QueueSeeder.ClearAsync"/> outcome.</summary>
public sealed record ClearSeedReport(IReadOnlyList<string> Cleared, IReadOnlyList<SeedOutcome> Failures);

/// <summary>
/// The dev-only merge-queue seeder (docs/design/queue-seeding.md). Produces legitimate queue entries
/// in any target <see cref="WorkerMergeState"/> by driving the REAL <see cref="MergeQueue"/> public
/// transitions with synthetic input — a real branch/commit fabricated in the bare mirror, and a
/// verification outcome supplied through <see cref="SyntheticVerificationRegistry"/> whose record is
/// visibly synthetic. Nothing here writes a row, asserts a state, or reaches around a gate; every
/// walk is the legal path, and every refusal along it is returned verbatim.
///
/// <para><b>Reachable only through the flag-gated <c>QueueSeedingService</c></b> — this type has no
/// other production caller, and the RPC surface it serves is unmapped unless
/// <c>MAINGUARD_ENABLE_QUEUE_SEEDING=1</c> was set at daemon startup.</para>
///
/// <para><b>Scratch-repo posture.</b> The <c>Merged</c> and <c>StaleVerified</c> walks move main FOR
/// REAL through the origin checkout (the mirror's main is force-fetched from origin, so any other
/// main move would later be rolled back — the documented walked-backwards defect), and the real
/// stale cascade they fire touches every entry in the repo's queue, including real agents'. This
/// tool is pointed at a scratch repository.</para>
/// </summary>
public sealed class QueueSeeder
{
    /// <summary>Audit event appended for every entry this seeder creates — the durable record, next
    /// to the entry's real transitions, that the entry was synthetic input.</summary>
    public const string SeededEvent = "queue_entry_seeded";

    /// <summary>The seeded-id prefix. Shared with <see cref="SyntheticVerificationRegistry.RequiredIdPrefix"/>;
    /// the clear path's scope is this prefix, structurally.</summary>
    public const string IdPrefix = SyntheticVerificationRegistry.RequiredIdPrefix;

    /// <summary>
    /// The plan-record twin of <see cref="SyntheticVerificationPlan.SeededProvenanceMarker"/>. A seeded
    /// plan is a REAL record with a REAL approval — the one thing it must never be mistaken for is a plan
    /// a worker inspected a repository and wrote, so it says so about itself, in the field a human reads.
    /// </summary>
    public const string SeededPlanMarker = " [seeded — not authored by a worker]";

    /// <summary>The coordinator id recorded on a seeded plan. Carries the <see cref="IdPrefix"/> so a
    /// seeded plan is identifiable from the plan record alone, exactly as a seeded entry is from its id.</summary>
    public const string SeededCoordinatorId = IdPrefix + "coordinator";

    private const string VerifyConfigContent = "true\n";

    private readonly MergeQueueProvisioner _provisioner;
    private readonly IMergeQueueRegistry _registry;
    private readonly SyntheticVerificationRegistry _synthetic;
    private readonly IRepoProvisioner _repos;
    private readonly PlanApprovalService? _plans;
    private readonly WorkerPlanGate? _planGate;
    private readonly Action<string>? _log;

    /// <param name="plans">The phase-2 plan service. Optional, and its absence is a stated fact rather
    /// than a default: a substrate without the coordinator plan pipeline can still seed everything else,
    /// and a <c>WithPlan</c> spec there is REFUSED verbatim instead of quietly producing an entry that
    /// looks plan-gated and is not.</param>
    /// <param name="planGate">The daemon-side plan gate — the same instance the merge queue ANDs in as an
    /// <see cref="IMergeGate"/>, or the hold this seeder arms would not be the hold anything reads.</param>
    public QueueSeeder(
        MergeQueueProvisioner provisioner,
        IMergeQueueRegistry registry,
        SyntheticVerificationRegistry synthetic,
        IRepoProvisioner repos,
        Action<string>? log = null,
        PlanApprovalService? plans = null,
        WorkerPlanGate? planGate = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _synthetic = synthetic ?? throw new ArgumentNullException(nameof(synthetic));
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _plans = plans;
        _planGate = planGate;
        _log = log;
    }

    /// <summary>True when this seeder can drive the phase-2 plan pipeline (both halves wired).</summary>
    public bool CanSeedPlans => _plans is not null && _planGate is not null;

    // ---- Seeding ---------------------------------------------------------

    /// <summary>
    /// Seeds one ordered batch. Throws <see cref="RepoProvisioningException"/> only when the repo has
    /// no provisioned mirror at all (the gRPC layer maps it to NOT_FOUND); everything after that is a
    /// per-entry verbatim refusal, so one entry failing to reach its target does not fault the batch.
    /// </summary>
    public async Task<SeedBatchReport> SeedAsync(
        string repoHandle, IReadOnlyList<SeedSpec> specs, string actor, CancellationToken ct)
    {
        var context = _provisioner.EnsureQueue(repoHandle)
            ?? throw new RepoProvisioningException(
                $"No provisioned mirror for repo handle '{repoHandle}' — provision the repo first.");

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = DefaultBranch(barePath);

        // The verify-family walks need a committed .mainguard/verify on main; a repo without one gets
        // one committed to ORIGIN main (user decision — convenience over seeding "no-config repos
        // as-is"), loudly reported. This is the one write the seeder makes outside any state walk.
        var provisionedVerifyConfig = false;
        if (specs.Any(NeedsVerification)
            && string.IsNullOrWhiteSpace(ShowFile(barePath, mainBranch, MergeQueueProvisioner.VerificationConfigPath)))
        {
            var refusal = ProvisionVerifyConfig(repoHandle, barePath, mainBranch);
            if (refusal is null)
            {
                provisionedVerifyConfig = true;
                context = _provisioner.EnsureQueue(repoHandle) ?? context; // reconcile onto the moved main
            }
            else
            {
                _log?.Invoke($"queue seeder repo={repoHandle}: could not provision verify config — {refusal}");
            }
        }

        var results = new List<SeedOutcome>();
        foreach (var spec in specs)
        {
            var count = Math.Max(1, spec.Count);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await SeedOneAsync(repoHandle, context, barePath, mainBranch, spec, actor, ct)
                    .ConfigureAwait(false));
            }
        }

        // Reported states are FINAL states: a later spec legitimately moves an earlier seed (a Merged
        // spec's cascade stales a Verified one), and reporting where each entry ended is what makes
        // the response the truth about the batch rather than a per-step log of it.
        var queue = context.Queue;
        var final = results
            .Select(r => queue.Agents.Contains(r.AgentId) || queue.DiscardedAgents.Contains(r.AgentId)
                ? r with { ReachedState = queue.GetState(r.AgentId).ToString() }
                : r)
            .ToList();

        return new SeedBatchReport(final, queue.CurrentMainSha, provisionedVerifyConfig);
    }

    private async Task<SeedOutcome> SeedOneAsync(
        string repoHandle, MergeQueueContext context, string barePath, string mainBranch,
        SeedSpec spec, string actor, CancellationToken ct)
    {
        var queue = context.Queue;
        var agentId = IdPrefix + Guid.NewGuid().ToString("N")[..8];

        // Asked BEFORE anything is created: a plan-gated seed this substrate cannot drive must leave no
        // branch, no registry row and no entry behind, because a half-seeded entry that LOOKS plan-gated
        // and is not is exactly the fabricated state this tool refuses to produce.
        if (spec.WithPlan && !CanSeedPlans)
        {
            return new SeedOutcome(agentId, "",
                "this daemon has no coordinator plan pipeline wired — with_plan needs WorkerPlanGate "
                + "and PlanApprovalService, so seed without it (the entry is then outside the plan gate, "
                + "which is what an unheld id has always been)");
        }

        try
        {
            // 1. The real branch — a real commit on top of the mirror's main, before anything else,
            //    so every downstream consumer (diff, review, merge) has real git data from the start.
            var seededPath = CreateSeedBranch(barePath, mainBranch, agentId, spec.Flavor);

            // 2. The synthetic-verification plan, registered BEFORE the entry can verify. For targets
            //    that never verify it is still registered: it marks the id as seeded to the requeue
            //    path, and a later human Verify click on the entry takes the seeded arm rather than
            //    refusing on the missing jail.
            var plan = new SyntheticVerificationPlan(
                // The target and the verdict cannot disagree: asking for a VerificationFailed entry IS
                // asking for a failing run, whether or not the caller also set verification_fails.
                passed: !spec.VerificationFails && spec.TargetState != WorkerMergeState.VerificationFailed,
                holdSeconds: spec.TargetState == WorkerMergeState.Verifying ? Math.Max(1, spec.HoldSeconds) : spec.HoldSeconds,
                staleBehavior: spec.StaleBehavior);
            _synthetic.Register(repoHandle, agentId, plan);

            // 3. The plan dimension, BEFORE the entry can verify or merge: the real
            //    Hold → Present → Approve walk, so the plan gate holds this id for real and the
            //    approved scope is in place by the time the verification arms the flagged-change review.
            var planId = "";
            if (spec.WithPlan)
            {
                var planRefusal = SeedPlan(agentId, spec, seededPath, actor, out planId);
                if (planRefusal.Length > 0)
                {
                    return new SeedOutcome(agentId, "", planRefusal);
                }
            }

            // 4. The entry itself — the ONE creation path everything real uses.
            _provisioner.EnsureEntry(repoHandle, agentId, MergeEntryOrigin.Local);

            // 5. The audit record that this entry is synthetic input, before any further transition.
            _provisioner.AuditLog.Append(new AuditEvent(SeededEvent, new Dictionary<string, string>
            {
                ["repo"] = repoHandle,
                ["agent"] = agentId,
                ["by"] = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
                ["target_state"] = spec.TargetState.ToString(),
                ["flavor"] = spec.Flavor.ToString(),
                ["with_plan"] = spec.WithPlan ? "true" : "false",
                ["plan_id"] = planId,
                ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }));

            // 6. The walk to the target — existing public transitions only.
            var refusal = await WalkToTargetAsync(repoHandle, context, barePath, mainBranch, agentId, spec, actor, ct)
                .ConfigureAwait(false);

            var reached = queue.Agents.Contains(agentId) || queue.DiscardedAgents.Contains(agentId)
                ? queue.GetState(agentId).ToString()
                : "";
            _log?.Invoke(
                $"queue seeder repo={repoHandle} agent={agentId} target={spec.TargetState} "
                + $"reached={reached}{(refusal.Length > 0 ? $" refusal={refusal}" : "")}");
            return new SeedOutcome(agentId, reached, refusal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A refusal, not a fault: the batch continues and the entry reports what stopped it.
            var reached = queue.Agents.Contains(agentId) ? queue.GetState(agentId).ToString() : "";
            return new SeedOutcome(agentId, reached, ex.Message);
        }
    }

    private static bool NeedsVerification(SeedSpec spec) => spec.TargetState is not
        (WorkerMergeState.Working or WorkerMergeState.Discarded)
        // A Working seed that is asked to FAIL still runs a verification — and since H2 lands at
        // VerificationFailed. Reading `Working` as "no verification needed" would skip the very setup the
        // spec exists to arrange.
        || (spec.TargetState == WorkerMergeState.Working && spec.VerificationFails);

    /// <summary>
    /// The phase-2 plan pipeline, driven for real for a synthetic id (design §9): the daemon
    /// <see cref="WorkerPlanGate.Hold"/>s a task for this id exactly as the spawn path does, a plan is
    /// <see cref="PlanApprovalService.Present"/>ed against the one-live-plan-per-worker invariant, and
    /// the operator's own daemon-derived identity approves it. Every refusal along the way is returned
    /// verbatim; nothing writes a plan record by hand.
    ///
    /// <para><b>What is synthetic here is authorship, and only authorship</b> — there is no worker, so no
    /// worker inspected the repository and wrote this. The record says that of itself
    /// (<see cref="SeededPlanMarker"/> in the two free-text fields a human reads, plus the
    /// <see cref="SeededCoordinatorId"/>), for the same reason the seeded verification record labels its
    /// own outcome: a plan that could pass for worker-authored is a forgery, not a fixture.</para>
    ///
    /// <para><b>The approval is NOT synthetic.</b> <paramref name="actor"/> is the daemon-derived operator
    /// identity of the connection that called the seeding RPC — the same identity a real approval records
    /// — and that person really did ask for this. Approving as "unknown" or as a fabricated human would be
    /// an unattributable approval, which <see cref="PlanApprovalService.Approve"/> refuses outright.</para>
    ///
    /// <para><b>Default scope is the seed's own path</b> so <c>with_plan</c> alone changes nothing about
    /// mergeability — it only moves the id inside the plan gate. Naming any other scope is what arms the
    /// real out-of-approved-scope item, which is the arm this parameter exists to make seedable.</para>
    /// </summary>
    private string SeedPlan(string agentId, SeedSpec spec, string seededPath, string actor, out string planId)
    {
        planId = "";
        var title = $"seeded plan-gated entry {agentId}";
        var taskPrompt = $"the task withheld from {agentId}{SeededPlanMarker}";
        var scope = spec.Scope is { Count: > 0 }
            ? spec.Scope.Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : new List<string> { seededPath };

        // (1) The daemon withholds the task — the same call AgentSpawnService makes on the spawn path.
        _planGate!.Hold(agentId, SeededCoordinatorId, title, taskPrompt, budgetUsd: 0m);

        // (2) The plan is presented, through the real invariant (a second Present for this id refuses).
        var presented = _plans!.Present(
            agentId,
            SeededCoordinatorId,
            title,
            new TaskPlanFields(scope, "supplied by the queue seeder" + SeededPlanMarker,
                "none — no work was executed" + SeededPlanMarker),
            taskPrompt,
            budgetUsd: 0m);
        if (!presented.IsPresented || presented.PlanId is null)
        {
            _planGate.Forget(agentId);
            return presented.Message;
        }

        // (3) ...and the operator who called the RPC approves it, attributably.
        _plans.Approve(presented.PlanId, string.IsNullOrWhiteSpace(actor) ? "queue-seeder" : actor);

        // (4) The worker's own commit-time deviation declaration, supplied here for the same reason its
        // authorship is: there is no worker, and this record already says so of itself in the two
        // free-text fields a human reads.
        //
        // Left at DeviationDeclaration.NotDeclared it would arm a must-acknowledge row on EVERY seeded
        // plan-gated entry, forever — a row whose real cause is "a dev tool made this entry" rather than
        // anything about the branch. A blocker that is always present for a whole class of entries and
        // never means anything is exactly how a gate teaches people to click through it, which is what
        // the declaration mechanism must not become. The fail-closed default stays where it matters: on
        // a real worker, which is the only thing that can actually answer.
        _plans.DeclareDeviations(agentId, deviations: null);
        planId = presented.PlanId;
        _log?.Invoke(
            $"queue seeder agent={agentId} plan={planId} — the REAL plan pipeline ran "
            + $"(held → presented → approved) over {scope.Count} scope pattern(s)");
        return "";
    }

    private async Task<string> WalkToTargetAsync(
        string repoHandle, MergeQueueContext context, string barePath, string mainBranch,
        string agentId, SeedSpec spec, string actor, CancellationToken ct)
    {
        var queue = context.Queue;
        switch (spec.TargetState)
        {
            case WorkerMergeState.Working:
                if (spec.VerificationFails)
                {
                    // The verify-FAIL entry: run the (synthetic) verification and let the REAL settle path
                    // decide where it lands. Since H2 that destination is VerificationFailed, not Working —
                    // a red run is its own state now — so this spec reaches the same entry it always meant
                    // to seed and the reported `reached` state says so. Seeding the failure by its own name
                    // is the `VerificationFailed` target below; this arm is kept because it is the spelling
                    // the seeding UI and the RPC already send.
                    await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
                }

                return "";

            case WorkerMergeState.VerificationFailed:
                // The red verdict, seeded by name. The synthetic plan is forced to fail for this target
                // (see the `passed:` argument at the call site) so the target and the outcome cannot
                // disagree — a "seed me a failed entry" that produced a green one would be exactly the
                // fabricated state this tool refuses to make.
                await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
                return "";

            case WorkerMergeState.Verifying:
                {
                    // Started, deliberately NOT awaited: the hold keeps the run genuinely in flight. The
                    // task is retained on the plan so the clear path can drain it before Cancel (the
                    // row-resurrection ordering rule), and observed so a cancelled hold faults nothing.
                    var plan = _synthetic.TryGet(repoHandle, agentId)!;
                    var run = queue.RunVerificationAsync(agentId, CancellationToken.None);
                    plan.InFlight = run;
                    Observe(run);
                    return "";
                }

            case WorkerMergeState.Verified:
                await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
                return "";

            case WorkerMergeState.AwaitingReview:
                await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
                queue.RequestReview(agentId);
                return "";

            case WorkerMergeState.Rejected:
                await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
                return queue.TryReject(agentId, actor, spec.Reason, out var rejectRefusal) ? "" : rejectRefusal;

            case WorkerMergeState.Discarded:
                return queue.TryDiscard(agentId, actor, spec.Reason, out var discardRefusal) ? "" : discardRefusal;

            case WorkerMergeState.StaleVerified:
                {
                    await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);

                    // A REAL out-of-band main move (a scenario the queue's reconcile explicitly
                    // supports): an empty commit on origin main, fetched into the mirror, reconciled by
                    // EnsureQueue — which fires the real NotifyMainMoved cascade. The seeded plan's
                    // StaleBehavior decides where the cascade leaves this entry; Hold is the resting
                    // StaleVerified this target exists for.
                    var refusal = AdvanceOriginMain(repoHandle, barePath, mainBranch,
                        $"seed: advance main to stale {agentId}");
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    _provisioner.EnsureQueue(repoHandle);
                    await queue.LastCascade.ConfigureAwait(false);
                    return "";
                }

            case WorkerMergeState.Merged:
                return await MergeSeededAsync(repoHandle, context, barePath, mainBranch, agentId, ct)
                    .ConfigureAwait(false);

            default:
                return $"'{spec.TargetState}' is not a seedable target state";
        }
    }

    /// <summary>
    /// The real thing, end to end: the RT-D1 lease walk around a real <c>--ff-only</c> merge in the
    /// origin checkout — the same operation the GUI's foreground merge performs (minus its T-19
    /// client journal entry, a documented gap). Every gate genuinely evaluates inside
    /// <see cref="MergeQueue.TryConfirmHumanMerge"/>: a Flagged seed cannot reach Merged without its
    /// items acknowledged, exactly like a real branch. Every non-merged exit hands the lease back.
    /// </summary>
    private async Task<string> MergeSeededAsync(
        string repoHandle, MergeQueueContext context, string barePath, string mainBranch,
        string agentId, CancellationToken ct)
    {
        var queue = context.Queue;
        await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);

        if (ResolveOriginCheckout(barePath) is not { } origin)
        {
            return "the mirror has no origin checkout to merge in — seeded merges need a local scratch repo";
        }

        if (TryGit(origin, out var headBranch, "symbolic-ref", "--short", "HEAD") != 0
            || !string.Equals(headBranch.Trim(), mainBranch, StringComparison.Ordinal))
        {
            return $"the origin checkout is not on '{mainBranch}' — check it out (or seed without a Merged entry)";
        }

        var expectedMainSha = queue.CurrentMainSha;
        var leaseId = Guid.NewGuid().ToString("N");
        if (context.Leases.TryBegin(repoHandle, leaseId, agentId, expectedMainSha, mainBranch) is null)
        {
            return "another merge is already in progress for this repository";
        }

        var confirmed = false;
        try
        {
            var tip = ShowRef(barePath, "refs/heads/agent/" + agentId);
            if (tip is null)
            {
                return "the seeded branch is missing from the mirror";
            }

            // The branch exists only in the mirror until fetched; then the same ff-only merge the
            // human's foreground merge runs.
            if (TryGit(origin, out var fetchOut, "fetch", "--no-tags", barePath,
                    $"refs/heads/agent/{agentId}") != 0)
            {
                return $"fetching the seeded branch into the origin checkout failed: {fetchOut.Trim()}";
            }

            if (TryGit(origin, out var mergeOut, "merge", "--ff-only", tip) != 0)
            {
                return $"the ff-only merge was refused in the origin checkout: {mergeOut.Trim()}";
            }

            var newMainSha = Run(origin, "rev-parse", "HEAD").Trim();

            // Gate + CAS + the Merged transition, atomically, daemon-side — the MG-11 enforcement
            // point, evaluated for a seeded branch exactly as for a real one.
            // Labelled `seeded` in the merge audit for the same reason a seeded verification carries its
            // SeededProvenanceMarker: a synthetic merge that recorded itself as a human one would be a
            // forged entry in the one chain that has to be trustworthy.
            if (!queue.TryConfirmHumanMerge(
                    agentId, newMainSha, expectedMainSha, out var reason, MergeAuthorization.Seeded()))
            {
                // The merge has landed on origin main but the queue refused to record it — surface
                // the reason verbatim; the mirror refresh below still reconciles main so the queue
                // cannot disagree with git.
                _provisioner.TryRefreshMirrorMainAfterMerge(repoHandle, out _);
                _provisioner.EnsureQueue(repoHandle);
                return reason;
            }

            context.Leases.Confirm(repoHandle, leaseId, newMainSha);
            confirmed = true;

            if (!_provisioner.TryRefreshMirrorMainAfterMerge(repoHandle, out var refreshReason))
            {
                _log?.Invoke($"queue seeder repo={repoHandle}: mirror main refresh failed — {refreshReason}");
            }

            // Let the real cascade the confirm fired settle before reporting states.
            await queue.LastCascade.ConfigureAwait(false);
            return "";
        }
        finally
        {
            if (!confirmed)
            {
                context.Leases.Release(repoHandle, leaseId);
            }
        }
    }

    // ---- PushCommits (the orchestrator-dynamics primitive) ---------------

    /// <summary>
    /// Appends <paramref name="count"/> real commits to an existing seeded branch and drives the real
    /// new-commits invalidation — <see cref="MergeQueue.NotifyNewCommits"/>, the same public
    /// transition the ref-watcher path drives, with the same guards (terminal entries ignore it).
    /// </summary>
    public PushCommitsReport PushCommits(string repoHandle, string agentId, int count)
    {
        if (_synthetic.TryGet(repoHandle, agentId) is null)
        {
            return new PushCommitsReport(false,
                $"'{agentId}' is not a seeded entry of this repo — PushCommits drives seeded branches only",
                "", "");
        }

        var context = _registry.Resolve(repoHandle);
        if (context is null)
        {
            return new PushCommitsReport(false, $"no active merge queue for repo handle '{repoHandle}'", "", "");
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var reference = "refs/heads/agent/" + agentId;
        var tip = ShowRef(barePath, reference);
        if (tip is null)
        {
            return new PushCommitsReport(false, "the seeded branch is missing from the mirror", "", "");
        }

        var pushes = Math.Max(1, count);
        for (var i = 0; i < pushes; i++)
        {
            var newTip = CommitOnTop(barePath, tip!,
                path: $"seed/{agentId}.txt",
                content: $"seeded entry {agentId} — push {Guid.NewGuid():N}\n",
                message: $"seed: new commits on {agentId}");
            Run(barePath, "update-ref", reference, newTip, tip!);
            tip = newTip;
        }

        // The real invalidation. Verified evidence clears, requeue blocks retire, terminal entries
        // ignore it — all inside the state machine, none of it here.
        context.Queue.NotifyNewCommits(agentId);

        return new PushCommitsReport(true, "", tip!, context.Queue.GetState(agentId).ToString());
    }

    // ---- Clearing --------------------------------------------------------

    /// <summary>
    /// Removes every seeded entry of a repo. The per-id ordering is load-bearing (design §8): the
    /// entry is made terminal (or its hold drained) BEFORE <see cref="MergeQueue.Cancel"/> deletes
    /// the row, because a verification completing after the delete would re-mint the row.
    /// Structurally scoped to the <see cref="IdPrefix"/> — real entries are unreachable from here.
    /// </summary>
    public async Task<ClearSeedReport> ClearAsync(string repoHandle, string actor)
    {
        var context = _registry.Resolve(repoHandle);
        if (context is null)
        {
            return new ClearSeedReport(Array.Empty<string>(), Array.Empty<SeedOutcome>());
        }

        var queue = context.Queue;
        var barePath = _repos.BareRepoPathFor(repoHandle);

        // The registry's ids UNION the queue's seed- entries: after a daemon restart the persisted
        // rows survive while the in-memory plans do not, and clear must still reach them.
        var ids = _synthetic.IdsFor(repoHandle)
            .Concat(queue.Agents.Where(id => id.StartsWith(IdPrefix, StringComparison.Ordinal)))
            .Concat(queue.DiscardedAgents.Where(id => id.StartsWith(IdPrefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var cleared = new List<string>();
        var failures = new List<SeedOutcome>();

        foreach (var agentId in ids)
        {
            // A merge in flight for this entry decides — same refusal the discard RPC gives.
            var lease = context.Leases.GetOutstanding(repoHandle);
            if (lease is not null && string.Equals(lease.AgentId, agentId, StringComparison.Ordinal))
            {
                failures.Add(new SeedOutcome(agentId, queue.GetState(agentId).ToString(),
                    "a merge is in progress for this entry — finish or abandon it before clearing"));
                continue;
            }

            // (1) Terminal-or-drained first. TryDiscard's refusals (already terminal) are fine; what
            // matters is that no in-flight run can settle after the row is gone.
            queue.TryDiscard(agentId, actor, "cleared by the queue seeder", out _);
            if (_synthetic.TryGet(repoHandle, agentId) is { } plan)
            {
                plan.HoldCancellation.Cancel();
                if (plan.InFlight is { } run)
                {
                    try
                    {
                        await run.ConfigureAwait(false);
                    }
                    catch
                    {
                        // The drained run's cancellation/refusal already settled against the
                        // now-terminal entry; there is nothing to report here.
                    }
                }
            }

            // (2) The row, (3) the ref, (4) the plan.
            queue.Cancel(agentId);
            TryGit(barePath, out _, "update-ref", "-d", "refs/heads/agent/" + agentId);
            _synthetic.Remove(repoHandle, agentId);

            // ...and (5) the plan gate's hold, exactly as AgentSpawnService.Forget drops a stopped
            // worker's. The plan RECORD deliberately survives — a decided plan is history, and
            // PlanApprovalService has no delete for the same reason the audit chain has none — but the
            // id stops being a plan-gated worker, so it returns to the permissive/ineligible pair every
            // other unheld id has. A seed id is a fresh guid, so nothing can inherit the leftover record.
            _planGate?.Forget(agentId);
            cleared.Add(agentId);
        }

        if (cleared.Count > 0)
        {
            _log?.Invoke($"queue seeder repo={repoHandle}: cleared {cleared.Count} seeded entr"
                + (cleared.Count == 1 ? "y" : "ies"));
        }

        return new ClearSeedReport(cleared, failures);
    }

    /// <summary>Every (repoHandle, agentId) currently registered — the status RPC's enumeration.</summary>
    public IReadOnlyList<(string RepoHash, string AgentId)> SeededEntries() => _synthetic.All();

    // ---- Git plumbing ----------------------------------------------------

    /// <summary>
    /// A real commit on top of <paramref name="mainBranch"/>, reachable as
    /// <c>refs/heads/agent/&lt;agentId&gt;</c>. Pure plumbing against the bare mirror — no worktree,
    /// no hooks (the mirror has none, and the hardened runner pins them off regardless).
    /// </summary>
    /// <returns>The repository path the commit touches — the flavor's own path, which is also the
    /// default approved scope of a <c>WithPlan</c> seed (see <see cref="SeedPlan"/>).</returns>
    private string CreateSeedBranch(string barePath, string mainBranch, string agentId, SeedFlavor flavor)
    {
        var mainSha = Run(barePath, "rev-parse", "--verify", mainBranch).Trim();
        var (path, content) = flavor switch
        {
            SeedFlavor.Flagged => (".github/workflows/seed-ci.yml",
                "# seeded CI workflow — exists so the real flagged-change classifier flags this branch\n"
                + "name: seed-ci\non: [push]\njobs: {}\n"),
            SeedFlavor.ChangedTestCommand => (MergeQueueProvisioner.VerificationConfigPath,
                $"true seeded-drift-{agentId}\n"),
            _ => ($"seed/{agentId}.txt", $"seeded entry {agentId}\n"),
        };

        var commit = CommitOnTop(barePath, mainSha, path, content, $"seed: {agentId} ({flavor})");
        Run(barePath, "update-ref", "refs/heads/agent/" + agentId, commit,
            new string('0', 40)); // CAS from zero — the ref must not already exist
        return path;
    }

    /// <summary>One plumbing commit: <paramref name="parentSha"/>'s tree with one blob added/replaced.</summary>
    private string CommitOnTop(string barePath, string parentSha, string path, string content, string message)
    {
        var scratch = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-seed-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(scratch);
        try
        {
            var blobFile = System.IO.Path.Combine(scratch, "blob");
            System.IO.File.WriteAllText(blobFile, content);
            var blobSha = Run(barePath, "hash-object", "-w", blobFile).Trim();

            var index = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = System.IO.Path.Combine(scratch, "index") };
            AgentGitCommand.RunWithEnv(barePath, index, "read-tree", parentSha);
            AgentGitCommand.RunWithEnv(barePath, index,
                "update-index", "--add", "--cacheinfo", $"100644,{blobSha},{path}");
            var treeSha = AgentGitCommand.RunWithEnv(barePath, index, "write-tree").Trim();

            // The hardened env blanks all git identity config, so the identity is pinned inline.
            return Run(barePath,
                "-c", "user.name=Mainguard Queue Seeder",
                "-c", "user.email=seeder@mainguard.invalid",
                "commit-tree", treeSha, "-p", parentSha, "-m", message).Trim();
        }
        finally
        {
            try { System.IO.Directory.Delete(scratch, recursive: true); } catch { /* scratch only */ }
        }
    }

    /// <summary>Commits <c>.mainguard/verify</c> (content <c>true</c>) to origin main via the origin
    /// checkout — porcelain, because the file must exist in the working tree the human looks at.
    /// Returns a verbatim refusal, or null on success (mirror refreshed).</summary>
    private string? ProvisionVerifyConfig(string repoHandle, string barePath, string mainBranch)
    {
        if (ResolveOriginCheckout(barePath) is not { } origin)
        {
            return "the mirror has no origin checkout to commit a verify config in";
        }

        if (TryGit(origin, out var headBranch, "symbolic-ref", "--short", "HEAD") != 0
            || !string.Equals(headBranch.Trim(), mainBranch, StringComparison.Ordinal))
        {
            return $"the origin checkout is not on '{mainBranch}' — commit a {MergeQueueProvisioner.VerificationConfigPath} yourself";
        }

        var target = System.IO.Path.Combine(origin,
            MergeQueueProvisioner.VerificationConfigPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        System.IO.File.WriteAllText(target, VerifyConfigContent);
        Run(origin, "add", MergeQueueProvisioner.VerificationConfigPath);
        Run(origin,
            "-c", "user.name=Mainguard Queue Seeder",
            "-c", "user.email=seeder@mainguard.invalid",
            "commit", "-m", "seed: provision .mainguard/verify for queue seeding");

        if (!_provisioner.TryRefreshMirrorMainAfterMerge(repoHandle, out var reason))
        {
            return $"verify config committed but the mirror refresh failed: {reason}";
        }

        _log?.Invoke($"queue seeder repo={repoHandle}: committed {MergeQueueProvisioner.VerificationConfigPath} "
            + "to origin main (the repo had none)");
        return null;
    }

    /// <summary>A real empty commit on origin main + mirror refresh — the out-of-band main move.</summary>
    private string? AdvanceOriginMain(string repoHandle, string barePath, string mainBranch, string message)
    {
        if (ResolveOriginCheckout(barePath) is not { } origin)
        {
            return "the mirror has no origin checkout to advance main in — seed a Merged sibling instead";
        }

        if (TryGit(origin, out var headBranch, "symbolic-ref", "--short", "HEAD") != 0
            || !string.Equals(headBranch.Trim(), mainBranch, StringComparison.Ordinal))
        {
            return $"the origin checkout is not on '{mainBranch}' — cannot advance main to stale this entry";
        }

        Run(origin,
            "-c", "user.name=Mainguard Queue Seeder",
            "-c", "user.email=seeder@mainguard.invalid",
            "commit", "--allow-empty", "-m", message);

        return _provisioner.TryRefreshMirrorMainAfterMerge(repoHandle, out var reason)
            ? null
            : $"main advanced on origin but the mirror refresh failed: {reason}";
    }

    /// <summary>The origin checkout the mirror was cloned from, or null when it is not a local
    /// directory this daemon can run git in.</summary>
    private static string? ResolveOriginCheckout(string barePath)
    {
        if (TryGit(barePath, out var url, "remote", "get-url", "origin") != 0)
        {
            return null;
        }

        var path = url.Trim();
        return path.Length > 0 && System.IO.Directory.Exists(path) ? path : null;
    }

    private static string DefaultBranch(string barePath)
    {
        if (TryGit(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    private static string? ShowRef(string barePath, string reference)
        => TryGit(barePath, out var output, "rev-parse", "--verify", reference) == 0 ? output.Trim() : null;

    private static string? ShowFile(string barePath, string reference, string path)
        => TryGit(barePath, out var output, "show", $"{reference}:{path}") == 0 ? output : null;

    private static string Run(string workingDir, params string[] args)
        => AgentGitCommand.Run(workingDir, args);

    private static int TryGit(string workingDir, out string output, params string[] args)
        => AgentGitCommand.TryRun(workingDir, out output, args);

    // A background run whose exception nobody awaits must still be observed.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
