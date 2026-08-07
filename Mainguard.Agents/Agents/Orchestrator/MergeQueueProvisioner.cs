using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Review;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// MG-10 — the swarm-up lifecycle hook that actually BUILDS a repo's <see cref="MergeQueue"/> and puts it
/// in the <see cref="MergeQueueRegistry"/>.
///
/// <para><b>Why this type exists.</b> Every ingredient of the P2-10 merge contract shipped — the state
/// machine, the immutable verification store, the RT-D1 lease, the stale cascade, the RT-D2 provenance
/// resolver — and not one of them was ever instantiated outside the test projects. The daemon registered an
/// <i>empty</i> <see cref="IMergeQueueRegistry"/> and nothing called <c>Register</c>, so every
/// <c>MergeQueueService</c> RPC resolved to <c>NOT_FOUND</c> for the daemon's whole lifetime and the client's
/// queue pump swallowed it and retried against an empty projection. The merge guarantees were therefore
/// neither enforced nor bypassable — they simply were not running. This is the missing constructor call,
/// placed on the events that make a repo "active": the repo is provisioned, or an agent joins it.</para>
///
/// <para><b>Restart resume comes free:</b> <see cref="MergeQueue"/> hydrates from its persisted rows in its
/// constructor, so a queue rebuilt here after a daemon restart resumes the repo's real state rather than
/// starting empty — which is exactly why the queue must be rebuilt from the SAME persisted stores the
/// previous daemon instance wrote to, not from fresh in-memory ones.</para>
/// </summary>
public sealed class MergeQueueProvisioner
{
    /// <summary>
    /// The tracked, in-tree file whose content is the repo's verification command line (P2-10 §3.2 "the
    /// project's configured verification command"). It is read out of git — <c>git show &lt;ref&gt;:</c> —
    /// for BOTH the branch and the main baseline, which is what makes RT-D2 drift detectable at all: the
    /// comparison is between two committed trees, so a branch that rewrites its own test command to
    /// <c>exit 0</c> differs from main's baseline and is flagged before a merge is possible.
    /// </summary>
    public const string VerificationConfigPath = ".mainguard/verify";

    /// <summary>
    /// The tracked, in-tree file naming the toolchains the verification command needs inside the jail
    /// (<see cref="RepoToolchainConfig.Path"/>). Read out of git for BOTH trees, exactly like
    /// <see cref="VerificationConfigPath"/> — but with one asymmetry the verify command does not have:
    /// only main's copy is ever <i>provisioned</i>. See <see cref="ToolchainDeclarationResolver"/>.
    /// </summary>
    public const string ToolchainConfigPath = RepoToolchainConfig.Path;

    private readonly object _gate = new();
    private readonly MergeQueueRegistry _registry;
    private readonly IRepoProvisioner _repos;
    private readonly IMergeLeaseStore _leases;
    private readonly Func<string, string, string?> _resolveContainerId;
    private readonly Func<string, IMergeQueueStore> _queueStore;
    private readonly Func<string, IVerificationStore> _verificationStore;
    private readonly ISandboxEngine _sandboxes;
    private readonly VerificationRunner _runner;
    private readonly IAuditLog _audit;
    private readonly Action<string>? _log;
    private readonly Func<string, string, bool>? _publishAgentRef;
    private readonly Func<string, string, AgentBranchAlignment>? _checkAgentBranch;
    private readonly IMergeBranchDiffService _mergeDiff;
    private readonly Func<string, TaskPlan?>? _resolveApprovedPlan;

    /// <param name="registry">The registry the gRPC layer resolves repo handles through.</param>
    /// <param name="repos">Locates the daemon-side bare mirror for a repo hash (main sha + config trees).</param>
    /// <param name="leases">The RT-D1 merge-lease store. <b>Must be the same singleton</b> the foreground
    /// merge, <c>BeginMerge</c> and <see cref="MergeDispatch"/> use — the one-outstanding-merge-per-repo
    /// invariant is only an invariant while every origin contends for the same store (MG-23).</param>
    /// <param name="resolveContainerId">(repoHash, agentId) → the agent's live jail, or null when it has
    /// none. Verification runs in the worker's own sandbox; no jail means no verification (never the host).</param>
    /// <param name="queueStore">repoHash → the persisted queue-state store (SQLite in the daemon).</param>
    /// <param name="verificationStore">repoHash → the immutable verification-record store.</param>
    /// <param name="sandboxes">The engine that runs the test command and reports the container-runtime exit.</param>
    /// <param name="artifactDirectory">Daemon-owned directory the verification log artifacts land in.</param>
    /// <param name="mergeDiff">
    /// P2-11 — the branch-vs-main diff the flagged-change review classifies. <b>Required, deliberately.</b>
    /// Made optional it would have to be null-checked at the point the gate is composed, and a gate that is
    /// only wired when some collaborator happens to be present is the failure mode this whole change exists
    /// to remove (phase 2 found a gate check made unreachable by an <c>if</c>). With no way to construct a
    /// provisioner that cannot run the review, there is no way to construct one whose queue silently lacks
    /// the gate.
    /// </param>
    /// <param name="audit">Audit sink threaded into every queue (the loud <c>stale_override_used</c> path).</param>
    /// <param name="log">Optional milestone sink (daemon Merge log category).</param>
    /// <param name="resolveApprovedPlan">
    /// SA-1/F6 — agentId → the managed worker's human-approved <see cref="TaskPlan"/>, or null when the
    /// agent has none. Supplying it turns on the <c>out-of-approved-scope</c> arm of the detector: every
    /// file the branch touches outside <c>TaskPlan.Scope</c> becomes its own must-acknowledge item.
    ///
    /// <para><b>Null in the daemon today, and that is a statement of fact rather than a default.</b> There is
    /// no agent→approved-plan binding anywhere in the running daemon: <c>PlanApprovalService.PlanApproved</c>
    /// has no production subscriber, no spawn path accepts or records a plan id, and <c>AgentSession</c>
    /// carries none — so no honest implementation of this callback exists yet to pass. The scope comparison
    /// is therefore wired, tested and inert, waiting on the plan-authorship pipeline; it is NOT reported as
    /// enforced. See the PR body and <c>docs/design/coordinator-phase-2-decisions.md</c> §3a.</para>
    /// </param>
    /// <param name="publishAgentRef">
    /// MG-3 — (repoHash, agentId) → carry the agent's branch from its own repository into the mirror.
    /// Called immediately BEFORE every verification, which is the second half of the resolved
    /// fetch-trigger question (design §7): the watcher keeps the mirror responsive, and this makes the
    /// bytes that get verified definitely current rather than whatever the watcher last saw. Null (the
    /// pre-MG-3 tests) simply verifies whatever the mirror already holds.
    /// </param>
    /// <param name="checkAgentBranch">
    /// (repoHash, agentId) → which branch the agent's worktree is ACTUALLY on. Consulted immediately after
    /// the publish above, because that publish is the point at which "the agent has produced nothing" and
    /// "the agent produced work somewhere the mirror will never see" become indistinguishable: the mediator
    /// carries only <c>refs/heads/agent/&lt;id&gt;</c>, so an agent that committed on another branch leaves
    /// that ref untouched and every downstream consumer reads a ref that is present, readable and stale.
    /// Drift is raised here rather than logged, because a verification that runs against the wrong bytes
    /// and passes is worse than one that refuses with the reason.
    ///
    /// <para>Null (every pre-existing test) restores the old behaviour exactly: nothing is checked. The
    /// daemon passes it.</para>
    /// </param>
    public MergeQueueProvisioner(
        MergeQueueRegistry registry,
        IRepoProvisioner repos,
        IMergeLeaseStore leases,
        Func<string, string, string?> resolveContainerId,
        Func<string, IMergeQueueStore> queueStore,
        Func<string, IVerificationStore> verificationStore,
        ISandboxEngine sandboxes,
        string artifactDirectory,
        IMergeBranchDiffService mergeDiff,
        IAuditLog? audit = null,
        Action<string>? log = null,
        Func<string, string, bool>? publishAgentRef = null,
        Func<string, TaskPlan?>? resolveApprovedPlan = null,
        Func<string, string, AgentBranchAlignment>? checkAgentBranch = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _resolveContainerId = resolveContainerId ?? throw new ArgumentNullException(nameof(resolveContainerId));
        _queueStore = queueStore ?? throw new ArgumentNullException(nameof(queueStore));
        _verificationStore = verificationStore ?? throw new ArgumentNullException(nameof(verificationStore));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _runner = new VerificationRunner(
            _sandboxes,
            artifactDirectory ?? throw new ArgumentNullException(nameof(artifactDirectory)));
        _mergeDiff = mergeDiff ?? throw new ArgumentNullException(nameof(mergeDiff));
        _audit = audit ?? new InMemoryAuditLog();
        _log = log;
        _publishAgentRef = publishAgentRef;
        _resolveApprovedPlan = resolveApprovedPlan;
        _checkAgentBranch = checkAgentBranch;
    }

    /// <summary>
    /// Whether this provisioner will actually establish which branch an agent is on before verifying it.
    ///
    /// <para>Exposed for one reason: the check is an optional constructor argument, so the daemon failing
    /// to pass it would restore the silent behaviour exactly, with every other test in this repository
    /// still green. That is the shape of defect this codebase keeps producing — a control that is
    /// implemented, tested, and wired nowhere — so the composition root asserts on this rather than
    /// trusting a line in a registration file.</para>
    /// </summary>
    public bool ChecksAgentBranchAlignment => _checkAgentBranch is not null;

    /// <summary>
    /// Ensures a live, registered queue for <paramref name="repoHandle"/> and returns it; null when the repo
    /// has no provisioned bare mirror yet (nothing to govern — the same "empty until a repo is active"
    /// posture the registry has always documented, now with an actual path out of "empty").
    ///
    /// <para>Idempotent, and safe to call on every provision/spawn. On a repeat call it also RECONCILES the
    /// queue's authoritative <c>main@sha</c> with the mirror's: a re-provision fetches main forward, and a
    /// queue still comparing verifications against the pre-fetch sha would call stale evidence fresh. Moving
    /// it fires the ordinary stale cascade, which is the correct response to main having moved.</para>
    /// </summary>
    public MergeQueueContext? EnsureQueue(string repoHandle)
    {
        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return null;
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        var mainSha = RevParse(barePath, mainBranch);
        if (string.IsNullOrEmpty(mainSha))
        {
            // No mirror (or an empty one): a queue keyed to an unknown main would verify against "" and
            // call every record fresh. Better to stay absent — the handle keeps resolving to NOT_FOUND,
            // which is honest, until the repo is really provisioned.
            return null;
        }

        MergeQueueContext context;
        var created = false;
        lock (_gate)
        {
            var existing = _registry.Resolve(repoHandle);
            if (existing is not null)
            {
                context = existing;
            }
            else
            {
                context = Build(repoHandle, mainSha);
                _registry.Register(repoHandle, context);
                created = true;
            }
        }

        if (!created && !string.Equals(context.Queue.CurrentMainSha, mainSha, StringComparison.Ordinal))
        {
            context.Queue.NotifyMainMoved(mainSha);
            _log?.Invoke($"merge queue repo={repoHandle} main advanced to {mainSha} — stale cascade fired");
        }

        if (created)
        {
            _log?.Invoke($"merge queue registered repo={repoHandle} main={mainSha} branch={mainBranch}");
        }

        return context;
    }

    /// <summary>
    /// Ensures the repo's queue exists AND that <paramref name="agentId"/> has an entry in it (the agent
    /// joined the swarm). Without this an agent's branch is invisible to <c>StreamQueue</c>: the queue only
    /// reports agents it tracks, and nothing else in the daemon ever adds one.
    /// </summary>
    public void EnsureEntry(string repoHandle, string agentId, MergeEntryOrigin origin = MergeEntryOrigin.Local)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        EnsureQueue(repoHandle)?.Queue.EnsureEntry(agentId, origin);
    }

    /// <summary>Drops a repo's queue on teardown (the handle resolves to NOT_FOUND again).</summary>
    public void Remove(string repoHandle) => _registry.Remove(repoHandle);

    // ---- Internals -------------------------------------------------------

    private MergeQueueContext Build(string repoHandle, string mainSha)
    {
        // The RT-D2 gate is per-repo-queue because its flag state is per-branch and its acknowledgment is
        // the human's; sharing one across repos would let one repo's ack clear another's flag.
        var changedTestCommand = new ChangedTestCommandGate();

        // P2-11, and the point of this change: the flagged-change gate is per-repo-queue for the same
        // reason the RT-D2 one is — its acknowledgments are per-branch and belong to the human who read
        // that branch's diff. It shipped complete and was constructed nowhere outside the test projects and
        // one dead ViewModel branch, so the daemon ANDed a single gate into CanMerge and the entire
        // human-diff-review boundary (executable config, CI workflows, git hooks, security-sensitive paths,
        // and the plan's approved scope) was evaluated by nothing. Adding it here is what makes the review
        // a merge precondition rather than a rendering.
        var flaggedChanges = new FlaggedChangeGate(_audit);

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: repoHandle,
            currentMainSha: mainSha,
            store: _queueStore(repoHandle),
            verifications: _verificationStore(repoHandle),
            runVerification: (agentId, ct) =>
                RunVerificationAsync(repoHandle, agentId, queue, changedTestCommand, flaggedChanges, ct),
            // Null requeue = re-verify, which is the production stale-cascade behaviour (§3.3): a staled
            // branch re-runs its own verification against the new main rather than sitting stale forever.
            requeue: null,
            gates: new IMergeGate[] { changedTestCommand, flaggedChanges },
            audit: _audit);

        return new MergeQueueContext(queue, _leases)
        {
            ChangedTestCommand = changedTestCommand,
            FlaggedChanges = flaggedChanges,
        };
    }

    /// <summary>
    /// One verification run, daemon-side: resolve the RT-D2 command provenance from git, record whether it
    /// drifted from the main baseline, then run it in the agent's OWN jail and let the container-runtime
    /// exit decide (OPS SA-1 — never a supervisor-reported frame).
    ///
    /// <para>MG-11 (2): <see cref="VerificationCommandResolver"/> had no production caller at all, so
    /// <c>ChangedVsMain</c> was never computed in the running app and the <c>changed-test-command</c> flag
    /// could not fire — a branch really could rewrite its own test command and merge unflagged. Resolving it
    /// HERE, at verification time, is also what pins the provenance into the immutable
    /// <see cref="VerificationRecord"/> the queue persists.</para>
    /// </summary>
    private async Task<VerificationRecord> RunVerificationAsync(
        string repoHandle, string agentId, MergeQueue queue, ChangedTestCommandGate changedGate,
        FlaggedChangeGate flaggedGate, CancellationToken ct)
    {
        var containerId = _resolveContainerId(repoHandle, agentId);
        if (string.IsNullOrEmpty(containerId))
        {
            // Host execution is a rejection trigger (§3.2). No jail ⇒ no verification, loudly.
            throw new InvalidOperationException(
                $"Agent '{agentId}' has no live sandbox — verification runs in the worker's own jail, never on the host.");
        }

        // MG-3 (design §7, "fetch trigger: both"): re-publish the agent's branch from its OWN repository
        // into the mirror right now, so what is about to be verified is the agent's current tip rather
        // than whatever the ref watcher last happened to see. The daemon names the source ref and the
        // destination; the agent cannot name a ref at all.
        _publishAgentRef?.Invoke(repoHandle, agentId);

        // ...and now say so if that publish carried nothing because the agent's work is not on the branch
        // the daemon carries. The restriction to refs/heads/agent/<id> is deliberate and stays (see
        // AgentRefMediator): what was wrong is that a branch outside it was ignored SILENTLY, which is
        // byte-for-byte the same observation as an agent that has done nothing at all. Verification is the
        // point the work is proposed as ready, so it is the point the difference has to be stated.
        //
        // This deliberately does NOT fast-forward agent/<id> onto whatever HEAD happens to be. See
        // docs/design/agent-branch-confinement.md §4: the trust argument for auto-recovery is sound, but
        // the daemon has no signal that the branch the agent is standing on is the branch it means to
        // submit, and replacing a silent no-op with a silent yes-op keeps the property that made this
        // defect invisible.
        var alignment = _checkAgentBranch?.Invoke(repoHandle, agentId);
        if (alignment is { Drifted: true })
        {
            var report = alignment.Describe(agentId);
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} verification REFUSED — {report}");
            _audit.Append(new AuditEvent(AgentBranchGuard.DriftEvent, new Dictionary<string, string>
            {
                ["repo"] = repoHandle,
                ["agent"] = agentId,
                ["expected"] = alignment.ExpectedBranch,
                ["actual"] = alignment.ActualBranch ?? "(detached HEAD)",
                ["head"] = alignment.HeadSha ?? string.Empty,
                ["agent_branch"] = alignment.AgentBranchSha ?? string.Empty,
                ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }));

            // InvalidOperationException, like the no-jail refusal above: MergeQueueGrpcService maps it to
            // FAILED_PRECONDITION carrying this text, so the operator reads the measurement rather than
            // "Exception was thrown by handler".
            throw new InvalidOperationException(report);
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        var resolution = VerificationCommandResolver.Resolve(
            branchConfigContent: ShowFile(barePath, "agent/" + agentId, VerificationConfigPath),
            mainConfigContent: ShowFile(barePath, mainBranch, VerificationConfigPath));

        // The same RT-D2 question asked of the TOOLCHAIN declaration. Note the argument order is
        // identical to the line above — branch vs main — but the resolver's answer is not symmetric:
        // what it hands back to provision is always main's, and the branch's copy only decides the flag.
        var toolchain = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: ShowFile(barePath, "agent/" + agentId, ToolchainConfigPath),
            mainConfigContent: ShowFile(barePath, mainBranch, ToolchainConfigPath),
            repoHandle: repoHandle);

        // Arm (or clear) the RT-D2 gate BEFORE the run: a branch whose command drifted is unmergeable from
        // the moment we know, not from whenever a UI happens to look.
        changedGate.SetFlagged(agentId, ChangedTestCommandGate.TestCommandItem, resolution.ChangedVsMain);
        changedGate.SetFlagged(agentId, ChangedTestCommandGate.ToolchainItem, toolchain.ChangedVsMain);

        // ...and arm the P2-11 flagged-change gate from the same committed trees, at the same moment, for
        // the same reason: a branch that edits a CI workflow, a git hook, an executable config or a
        // security-sensitive path — or that reaches outside its approved scope — is unmergeable from the
        // instant the daemon can know it, not from whenever a UI happens to look at it. Verification time is
        // also the correct cadence: the acknowledgment binds to the flagged set's content hash, so a branch
        // that pushes new work re-verifies, re-classifies, and drops every ack that covered the old bytes.
        ArmFlaggedChangeReview(repoHandle, agentId, flaggedGate);

        // ...and before running anything, confirm the jail REALLY carries what main declared. This is a
        // daemon-observed exec in the worker's own container, not a lookup in the daemon's own
        // bookkeeping: the failure being defended against is precisely the one where our records say the
        // layer was provisioned and the container is running something else. Without it, a jail missing
        // its toolchain produces exit 127 and an ordinary "verification failed" — indistinguishable from
        // the agent's code being broken, on the one screen where that distinction decides a merge.
        await EnsureToolchainPresentAsync(repoHandle, containerId!, toolchain.Provisioned, ct).ConfigureAwait(false);

        // Pin the record to the queue's authoritative main — the same value CanMerge compares against, so a
        // pass here is a pass against the main this branch will actually merge into.
        return await _runner.RunAsync(
            new VerificationRequest(
                agentId, containerId!, queue.CurrentMainSha,
                resolution.Command, resolution.ResolvedCommand, resolution.ConfigHash),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Classifies the branch's merge diff and installs the resulting must-acknowledge set on the agent's
    /// <see cref="AcknowledgmentStore"/>, which is what <see cref="FlaggedChangeGate"/> reads in
    /// <c>CanMerge</c>.
    ///
    /// <para><b>Every exit from here is fail-closed.</b> If the diff cannot be computed — no mirror, a
    /// branch the mirror does not carry, a git invocation that fell over — the store is deliberately left
    /// untouched rather than set to an empty set. An empty set is
    /// <see cref="AcknowledgmentStore.AllAcknowledged"/>, i.e. indistinguishable from "reviewed and clean",
    /// and writing one here would hand a merge to precisely the branch whose review just failed. An agent
    /// with no store is denied by <see cref="FlaggedChangeGate.Allows"/>'s MG-40 default-DENY, and the
    /// operator gets a named reason instead of a silent pass.</para>
    ///
    /// <para>The failure is also kept out of the verification result on purpose: "the review could not run"
    /// and "this branch's tests failed" are the one distinction the merge decision rests on, and collapsing
    /// them into a single thrown verification is how that distinction gets erased.</para>
    /// </summary>
    private void ArmFlaggedChangeReview(string repoHandle, string agentId, FlaggedChangeGate flaggedGate)
    {
        IReadOnlyList<Mainguard.Git.Models.FilePatch> files;
        try
        {
            files = _mergeDiff.Compute(repoHandle, agentId).Files;
        }
        catch (Exception ex)
        {
            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} flagged-change review FAILED "
                + $"({ex.Message}) — the branch stays unmergeable until it can be classified");
            return;
        }

        // SA-1/F6. `managed` is derived from the plan's presence rather than taken as a separate flag: the
        // scope comparison is meaningful exactly when there is an approved scope to compare against, and a
        // "managed but plan-less" combination could only ever mean "compare against nothing", which is the
        // state this change exists to end.
        var approvedPlan = _resolveApprovedPlan?.Invoke(agentId);
        var items = FlaggedChangeDetector.DetectFlagged(files, approvedPlan, managed: approvedPlan is not null);

        flaggedGate.StoreFor(agentId).SetFlagged(items);

        if (items.Count > 0)
        {
            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} flagged {items.Count} change(s) "
                + "requiring human acknowledgment before merge");
        }
    }

    /// <summary>
    /// Runs each declared toolchain's catalogued probe inside the worker's jail. Every probe must exit
    /// zero; the first that does not raises <see cref="ToolchainProvisioningException"/> and the
    /// verification never runs.
    /// </summary>
    private async Task EnsureToolchainPresentAsync(
        string repoHandle, string containerId, ToolchainDeclaration declaration, CancellationToken ct)
    {
        if (declaration.IsEmpty)
        {
            return;
        }

        foreach (var id in declaration.Ids)
        {
            var recipe = ToolchainCatalog.TryGet(id)
                ?? throw new UnknownToolchainException(repoHandle, id, ToolchainCatalog.KnownIds);

            SandboxExecResult probe;
            try
            {
                probe = await _sandboxes.ExecAsync(containerId, recipe.Probe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"could not probe '{id}' in the worker's jail: {ex.Message}");
            }

            if (probe.ExitCode != 0)
            {
                throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                    $"'{id}' is declared but absent from the worker's jail — the probe "
                    + $"`{string.Join(' ', recipe.Probe)}` exited {probe.ExitCode}. "
                    + "Verification was NOT run; this is a provisioning failure, not a failing test. "
                    + $"stderr: {Trim(probe.Stderr)}");
            }
        }
    }

    private static string Trim(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        return s.Length <= 400 ? s : s[^400..];
    }

    // `git show <ref>:<path>` — null when the ref or the path is absent (an absent branch-side config is
    // the typed "no verification command configured" edge; an absent main-side baseline counts as drift).
    private static string? ShowFile(string barePath, string reference, string path)
        => TryGit(barePath, out var output, "show", $"{reference}:{path}") ? output : null;

    private static string RevParse(string barePath, string reference)
        => TryGit(barePath, out var output, "rev-parse", "--verify", reference) ? output.Trim() : string.Empty;

    private static string ResolveDefaultBranch(string barePath)
    {
        if (TryGit(barePath, out var output, "symbolic-ref", "--short", "HEAD"))
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    // The mirror directory is checked before every spawn: git's working directory must exist or
    // Process.Start throws, and "this repo was never provisioned" is a routine state here (the provisioner
    // is called on spawn paths that legitimately degrade to session-only), not an error.
    private static bool TryGit(string barePath, out string output, params string[] args)
    {
        output = string.Empty;
        if (!System.IO.Directory.Exists(barePath))
        {
            return false;
        }

        return AgentGitCommand.TryRun(barePath, out output, args) == 0;
    }
}
