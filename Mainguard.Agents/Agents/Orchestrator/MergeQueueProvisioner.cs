using System;
using System.Collections.Generic;
using System.Linq;
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
///
/// <para><b>...and rehydrating the state is only half of it, which is the second missing call this type now
/// carries.</b> A queue that comes back holding a <c>Verifying</c> row holds a row about a run that died
/// with the previous process: the state is persisted per transition, the in-flight set is memory, so the
/// entry reports "verifying" forever about something that does not exist.
/// <see cref="MergeQueue.ResumeAfterRestartAsync"/> is the answer to that and had no production caller
/// either; <see cref="EnsureQueue"/> starts it here, on the created branch, the moment the queue exists.</para>
///
/// <para><b>Why here and not in <c>DaemonBootSequence</c>.</b> That is the obvious home and it is the wrong
/// one — a boot task would have nothing to iterate. The <see cref="MergeQueueRegistry"/> is empty at boot by
/// design, <c>ActiveRepoIndex</c> is memory-only and equally empty, and the RT-D1 merge-reconcile slot next
/// door says so out loud ("repos map in as their swarms come up; none at boot"). A resume step wired into
/// the ordered boot sequence would run against zero queues on every start and pass its own tests
/// forever — the same "complete mechanism, no production caller" shape, moved up one level. The moment a
/// repo's persisted queue state re-enters the process is <see cref="EnsureQueue"/>, so that is the moment
/// the resume can act; it is reached from <c>ProvisionRepo</c>, from a jailed spawn, and from the PR-intake
/// target resolver.</para>
///
/// <para><b>Order relative to the rest of boot.</b> Every one of those entry points is an RPC handler, so a
/// resume necessarily runs after the boot sequence's merge-reconcile step — which matters, because that
/// step can synthesize a missing <c>ConfirmMerge</c> and move main, and re-verifying against a main that is
/// about to move produces evidence the stale cascade immediately invalidates. It also lands after the swarm
/// reconciler, which is what settles Docker as the truth for jail liveness and prunes the worktrees of
/// agents whose containers are gone — the exact question <c>hasLiveJail</c> asks. Neither ordering is
/// enforced by a lock, and neither needs to be: the probe reads the container runtime directly at the
/// instant it runs, so a resume that somehow overtook the reconciler would still get the true answer, and
/// a main that moves underneath a re-run is the ordinary stale cascade rather than a corruption.</para>
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
    private readonly Func<string, ApprovedWork?>? _resolveApprovedWork;
    private readonly Func<string, IYieldProtocol>? _yieldFor;
    private readonly Func<string, string, string?>? _locateAgentWorktree;
    private readonly IAgentSupervisor _agentStates;
    private readonly Func<string, string, bool>? _publishRebasedAgentRef;
    private readonly OsvSnapshot _osv;
    private readonly string _artifactDir;
    private readonly SyntheticVerificationRegistry? _synthetic;
    private readonly Func<string, string, string, CancellationToken, Task<bool>>? _promptAgent;

    /// <summary>
    /// The worktrees this provisioner's cascade has parked mid-rebase, and what it measured about each.
    ///
    /// <para>Owned here rather than injected because the parking and the un-parking are this type's own
    /// acts: <see cref="OnRebaseConflict"/> writes it, <see cref="LetAgentResolveConflictAsync"/> and
    /// <see cref="AbortParkedRebaseAsync"/> clear it, and no fourth writer exists. Exposed so the gRPC
    /// projection can put the facts on the card — a provisioner is already what that layer resolves for
    /// the post-merge mirror refresh.</para>
    /// </summary>
    public RebaseConflictParkingStore ParkedConflicts { get; } = new();

    /// <summary>
    /// The phase-2 plan gate, ANDed into every repo's queue: a worker whose own plan was never approved
    /// cannot merge, whatever it verified. This is the <i>backstop</i> half of "a worker does not start
    /// work before its plan is approved" — the primary half is that the daemon never handed it the task.
    /// Shared across repos on purpose: it is keyed by agent id and every agent id is daemon-global.
    /// </summary>
    private readonly IMergeGate? _planGate;

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
    /// <param name="resolveApprovedWork">
    /// SA-1/F6 — agentId → what a human approved for this worker and what the worker has since said about
    /// following it (<see cref="ApprovedWork"/>), or null when the agent has none. Supplying it turns on
    /// two arms of the review, one per half of an approval:
    /// <list type="bullet">
    /// <item><b>The scope half.</b> Every file the branch touches outside <c>TaskPlan.Scope</c> becomes its
    /// own must-acknowledge item (the detector's out-of-approved-scope arm).</item>
    /// <item><b>The approach half</b> (<see cref="DeviationReview"/>). A scope is machine-comparable and is
    /// compared; an <c>Approach</c> is prose and is not — which is how a worker shipped the opposite of its
    /// approved approach with the scope honoured and every gate green. The worker's own commit-time
    /// declaration becomes a must-ack row, and so does the ABSENCE of one.</item>
    /// </list>
    ///
    /// <para><b>One callback for both, deliberately.</b> They have to describe the SAME approved plan: two
    /// resolvers would be free to name two different ones the instant a re-scope lands, and the reviewer
    /// would then be shown an approach the diff was never measured against.</para>
    ///
    /// <para><b>Wired by the daemon since phase 2, and the history is the point.</b> It used to be null for
    /// a stated reason: there was no agent→approved-plan binding anywhere in the running daemon, so any
    /// callback passed here would have compared diffs against a GUESSED scope and reported that as
    /// enforcement — worse than the honest gap. Worker-authored plans supply the exact binding, because a
    /// plan is keyed by the WORKER's own agent id, which is the same id the plan gate holds and the same
    /// id the merge queue tracks the branch under. The composition root reads it through
    /// <c>PlanStatus.Approved</c> only (a pending or rejected plan's scope has authorised nothing), and an
    /// agent with no approved plan still resolves null — unmanaged, scope comparison skipped, exactly as
    /// before. See <c>docs/design/coordinator-phase-2-decisions.md</c> §3a and
    /// <c>docs/design/queue-seeding.md</c> §9 (the seeder's <c>with_plan</c>/<c>scope</c> specs are how
    /// this arm is exercised without spawning an agent).</para>
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
    /// <param name="yieldProtocol">
    /// P2-09's cooperative yield — the <b>only</b> gateway to a mutable worktree (invariant 2), and
    /// therefore a hard precondition of the keep-alive rebase rather than a companion feature of it. The
    /// worktree this cascade rewrites is bind-mounted read-write into a RUNNING jail; rebasing it while the
    /// agent's own CLI is mid <c>git commit</c> is the <c>.git/index.lock</c> collision this application
    /// exists to prevent, and a worse version of it, because the daemon would be the second writer.
    /// <see cref="GitMutationGuard.RunGuarded{T}"/> refuses to run at all without an active token, so this
    /// is not a policy the rebase could be talked out of.
    /// <para>Null leaves the cascade at re-verify-only. See <see cref="ReparentsStaleBranches"/>.</para>
    /// </param>
    /// <param name="locateAgentWorktree">
    /// (repoHash, agentId) → the directory this agent's worktree lives in, or <b>null/empty when it has
    /// none</b>. That nullability is the contract, not a convenience: a stale entry whose agent was
    /// stopped has nothing to rebase, and the cascade has to be able to tell that apart from a rebase
    /// that failed.
    /// <para>Only the worktree comes from here. The mirror it rebases FROM and the branch it rebases ONTO
    /// are the ones this provisioner already resolved for the queue's own <c>main@sha</c>, so the rebase
    /// target and the verification baseline cannot drift apart — which they silently would if two callers
    /// each answered "what is main" for the same repo.</para>
    /// </param>
    /// <param name="publishRebasedAgentRef">
    /// (repoHash, agentId) → carry the branch the keep-alive rebase just REPARENTED into the mirror.
    /// Distinct from <paramref name="publishAgentRef"/> and not optional in spirit: a rebase is never a
    /// fast-forward, so the ordinary mediated publish refuses it as rewritten history, and a cycle whose
    /// result never reaches the mirror is a cycle with no observable effect at all. False from this means
    /// the mirror still holds the un-rebased branch, which the cascade reports rather than re-verifies.
    /// </param>
    /// <param name="agentStates">
    /// Where the run-state of an agent the cascade touched is reflected (<c>Rebasing</c>, and — the one
    /// that matters — <c>Conflict</c>). The daemon passes the real <c>PtyAgentSupervisor</c>, so the state
    /// reaches clients on the agent-event stream; the default no-op supervisor keeps the pure-unit paths
    /// working.
    /// </param>
    /// <param name="osvSnapshot">
    /// The offline advisory snapshot the P2-11 §3.6 lockfile review consults. Defaults to the shipped
    /// <see cref="OsvSnapshot.Default"/>.
    ///
    /// <para><b>Absent at the composition root on purpose</b>, unlike <paramref name="resolveApprovedWork"/>,
    /// and for a stated reason rather than by oversight. The snapshot is <b>bundled</b>: a review-time
    /// network call is a P2-11 rejection trigger, so there is no fetch-and-cache the daemon could pass a
    /// handle to, and the embedded copy IS the production answer. Refresh happens by shipping a build —
    /// which is exactly why the snapshot's age is carried to the reviewer instead of assumed
    /// (<see cref="OsvSnapshot.MaxAge"/>): a bundled database is guaranteed to age, and a reviewer told
    /// nothing would read an empty CVE column as a clean bill of health. Passing a snapshot here is for
    /// tests that need the missing/stale states without a hand-edited assembly.</para>
    /// </param>
    /// <param name="syntheticVerifications">
    /// The dev-only queue-seeding seam (docs/design/queue-seeding.md §3): a registry of seeded ids
    /// whose verification OUTCOME is supplied instead of executed. An unregistered id — every id, in
    /// a shipped daemon — takes the real path untouched. <b>Always wired by the daemon and empty in
    /// production</b>: the registry's only writer is the flag-gated <c>QueueSeedingService</c>, so
    /// the gate lives at the RPC surface (one place, loudly) rather than in a conditional wiring
    /// here that the composition root's exact-set assertion could not distinguish from an oversight.
    /// For a registered id the mirror-read half of the verification (RT-D2 provenance, gate arming,
    /// flagged-change review) still runs for real; only the jail half is replaced, and the record it
    /// produces is REQUIRED to be visibly synthetic
    /// (<see cref="SyntheticVerificationPlan.SeededProvenanceMarker"/>).
    /// </param>
    /// <param name="promptAgent">
    /// (repoHash, agentId, prompt) → whether the text was actually submitted to that agent's live CLI.
    /// The daemon passes its existing prompt-delivery path — the SAME
    /// <c>AgentCliBinder.TrySendPromptAsync</c> a coordinator's <c>send_worker_prompt</c> uses, with its
    /// measured CR-as-a-separate-frame submission — because a second way to type at a worker is a second
    /// place for the "the prompt accumulated unsubmitted in its input box" defect to live.
    /// <para>Only <see cref="LetAgentResolveConflictAsync"/> uses it, and null makes exactly that one
    /// action refuse with a reason rather than silently doing half of itself: unpausing a jail and NOT
    /// telling the agent why it woke up is how an agent goes back to whatever it was doing on top of a
    /// half-finished rebase.</para>
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
        Func<string, ApprovedWork?>? resolveApprovedWork = null,
        IMergeGate? planGate = null,
        Func<string, string, AgentBranchAlignment>? checkAgentBranch = null,
        Func<string, IYieldProtocol>? yieldProtocolFor = null,
        Func<string, string, string?>? locateAgentWorktree = null,
        IAgentSupervisor? agentStates = null,
        Func<string, string, bool>? publishRebasedAgentRef = null,
        OsvSnapshot? osvSnapshot = null,
        SyntheticVerificationRegistry? syntheticVerifications = null,
        Func<string, string, string, CancellationToken, Task<bool>>? promptAgent = null)
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
        _resolveApprovedWork = resolveApprovedWork;
        _planGate = planGate;
        _checkAgentBranch = checkAgentBranch;
        _yieldFor = yieldProtocolFor;
        _locateAgentWorktree = locateAgentWorktree;
        _agentStates = agentStates ?? NullAgentSupervisor.Instance;
        _publishRebasedAgentRef = publishRebasedAgentRef;
        _osv = osvSnapshot ?? OsvSnapshot.Default;
        _artifactDir = artifactDirectory;
        _synthetic = syntheticVerifications;
        _promptAgent = promptAgent;

        // The whole optional tail, recorded as data. See WiredOptionalControls for why every one of these
        // is here rather than only the one that happened to get a test.
        var wired = new SortedSet<string>(StringComparer.Ordinal);
        if (audit is not null) { wired.Add(nameof(audit)); }
        if (log is not null) { wired.Add(nameof(log)); }
        if (publishAgentRef is not null) { wired.Add(nameof(publishAgentRef)); }
        if (resolveApprovedWork is not null) { wired.Add(nameof(resolveApprovedWork)); }
        if (checkAgentBranch is not null) { wired.Add(nameof(checkAgentBranch)); }
        if (yieldProtocolFor is not null) { wired.Add(nameof(yieldProtocolFor)); }
        if (locateAgentWorktree is not null) { wired.Add(nameof(locateAgentWorktree)); }
        if (agentStates is not null) { wired.Add(nameof(agentStates)); }
        if (publishRebasedAgentRef is not null) { wired.Add(nameof(publishRebasedAgentRef)); }
        if (osvSnapshot is not null) { wired.Add(nameof(osvSnapshot)); }
        if (syntheticVerifications is not null) { wired.Add(nameof(syntheticVerifications)); }
        if (promptAgent is not null) { wired.Add(nameof(promptAgent)); }
        WiredOptionalControls = wired;
    }

    /// <summary>
    /// Whether this provisioner's queues actually REPARENT a staled branch, rather than merely re-running
    /// its tests against a main it does not descend from.
    ///
    /// <para>Exposed for the same reason <see cref="WiredOptionalControls"/> is: the keep-alive rebase is
    /// composed from two optional arguments, and a provisioner missing either one silently falls back to
    /// the re-verify-only cascade — which is not a degraded fix, it is the original defect. One name for
    /// the composed capability means the composition root can assert the capability instead of the parts.</para>
    /// </summary>
    public bool ReparentsStaleBranches =>
        _yieldFor is not null && _locateAgentWorktree is not null && _publishRebasedAgentRef is not null;

    /// <summary>
    /// Every optional constructor argument this provisioner was <b>actually given</b>, by parameter name.
    ///
    /// <para><b>Why this exists as a set rather than as one bool per control.</b> Each argument in the
    /// optional tail defaults to something that silently substitutes a weaker behaviour, and deleting the
    /// corresponding line from the daemon's registration was MEASURED to leave the whole suite green:
    /// dropping <c>audit</c> falls back to a throwaway <see cref="InMemoryAuditLog"/>, so
    /// <c>queue_entry_discarded</c>, <c>stale_override_used</c>, <c>verification_restart_resume</c>, the
    /// branch-drift <c>DriftEvent</c> and the flagged-change gate's events detach from the daemon's sink;
    /// dropping <c>publishAgentRef</c> verifies whatever the ref watcher last saw rather than the agent's
    /// tip, i.e. verified bytes ≠ submitted bytes; dropping <c>log</c> removes the merge category's only
    /// production writer; dropping <c>checkAgentBranch</c> restores the silent stranded-branch behaviour.
    /// A bool per control invites a test per control, and the four that had no test are precisely the four
    /// that could be deleted unnoticed — so the composition root asserts the whole tail ONCE, against an
    /// exact expected set. Adding a new optional argument, or dropping an existing one, both fail that
    /// assertion until the daemon's intent is restated.</para>
    ///
    /// <para>The set is deliberately EXACT rather than a minimum. <c>osvSnapshot</c> is absent on purpose:
    /// the advisory snapshot is bundled, so the embedded default IS the production answer and a passed one
    /// could only be a test double. An absence that is a decision has to be as pinned as a presence, or the
    /// decision quietly becomes an oversight the first time someone passes a guess.
    /// (<c>resolveApprovedWork</c> was pinned absent for a similar stated reason until phase 2 gave the
    /// daemon a real agent→approved-plan binding; it is in the set now, and the note is kept because the
    /// shape of that argument — a real binding or none, never a guess — is the standing rule.)</para>
    /// </summary>
    public IReadOnlySet<string> WiredOptionalControls { get; }

    /// <summary>
    /// The audit sink every queue, gate and drift report this provisioner builds appends to.
    ///
    /// <para>Exposed alongside <see cref="WiredOptionalControls"/> because "an audit log was passed" and
    /// "the daemon's audit log was passed" are different facts, and only the second one means the audit
    /// trail is attached to anything a reader could ever reach. The composition root asserts reference
    /// identity with the host's registered <see cref="IAuditLog"/>.</para>
    /// </summary>
    public IAuditLog AuditLog => _audit;

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
            // The mirror can be BEHIND the queue: ConfirmMerge installs the client's post-merge sha as
            // the queue's main and only then pulls the mirror forward, and that pull can fail (or not have
            // run yet). Trusting the mirror here walked the queue's main BACKWARDS to the pre-merge sha and
            // fired the stale cascade at every Verified entry for a move that never happened. A mirror
            // whose main is an ancestor of the queue's — or that does not even hold the queue's sha yet —
            // is behind, so the queue is left where it is and the mirror is pulled forward instead. Only a
            // mirror the queue is NOT ahead of (main really moved on origin) still moves the queue.
            var queueMain = context.Queue.CurrentMainSha;
            var mirrorBehind = !TryGit(barePath, out _, "cat-file", "-e", queueMain + "^{commit}")
                || TryGit(barePath, out _, "merge-base", "--is-ancestor", mainSha, queueMain);
            if (mirrorBehind)
            {
                _log?.Invoke($"merge queue repo={repoHandle} mirror main {mainSha} is behind the queue's {queueMain} — refreshing the mirror, not the queue");
                TryRefreshMirrorMainAfterMerge(repoHandle, out _);
                var refreshed = RevParse(barePath, mainBranch);
                if (!string.IsNullOrEmpty(refreshed)
                    && !string.Equals(refreshed, queueMain, StringComparison.Ordinal)
                    && TryGit(barePath, out _, "cat-file", "-e", queueMain + "^{commit}")
                    && !TryGit(barePath, out _, "merge-base", "--is-ancestor", refreshed, queueMain))
                {
                    context.Queue.NotifyMainMoved(refreshed);
                    _log?.Invoke($"merge queue repo={repoHandle} main advanced to {refreshed} — stale cascade fired");
                }
            }
            else
            {
                context.Queue.NotifyMainMoved(mainSha);
                _log?.Invoke($"merge queue repo={repoHandle} main advanced to {mainSha} — stale cascade fired");
            }
        }

        if (created)
        {
            _log?.Invoke($"merge queue registered repo={repoHandle} main={mainSha} branch={mainBranch}");

            // The freshness half of the restart repair, and it runs INLINE and FIRST — before the resume,
            // before the caller gets the context, before anything can touch this queue. It is the only
            // part of the boot repair that must not race: it decides which rehydrated rows are still
            // evidence about the tree in the mirror, and every later step (the resume's cascade, the
            // re-arm below, a human's merge) reads that decision. Backgrounding it announced tips into
            // live verifications as mid-run moves. Cheap by construction — one rev-parse per non-terminal
            // entry, no diff, no container.
            PrimeBranchTipsAfterRestart(repoHandle, context.Queue);

            // RT-D1: a merge lease that outlived the daemon (crash between BeginMerge and ConfirmMerge) is
            // classified HERE, against the mirror, the moment the repo's queue exists. The boot-sequence
            // slot cannot do it — it runs before any repo is mapped in and has no path to ask git on —
            // so until this call existed an outstanding lease was never reconciled at all and blocked
            // every later merge on the repo for good. Inline and before the resume: nothing can be
            // mid-merge on a queue that did not exist a moment ago, so this is the one instant a lease
            // that is merely outstanding is known to be a leftover rather than a live conversation.
            ReconcileOutstandingLease(repoHandle, barePath, context);

            // ...and THIS is the restart resume's production trigger. See the class remarks for why it is
            // here and not in DaemonBootSequence. Started only on the created branch: a repeat EnsureQueue
            // is the same live queue, whose Verifying rows are real runs this process started.
            context.Queue.BeginResumeAfterRestart(
                hasLiveJail: agentId => !string.IsNullOrEmpty(_resolveContainerId(repoHandle, agentId)),
                log: _log);

            // ...and the OTHER thing a restart destroys (L1). Background, because unlike the prime it
            // costs a merge diff per entry, and unlike the prime it cannot race: every step re-reads the
            // state and refuses to manufacture a store.
            BeginRearmAfterRestart(repoHandle, context);
        }

        return context;
    }

    // ---- Restart lease reconcile (RT-D1) ----------------------------------

    /// <summary>
    /// Classifies and settles the repo's outstanding merge lease at queue creation. Every verdict acts:
    /// a merge git proves landed is confirmed (and the cascade fires), a merge that never moved main is
    /// released and surfaced, and an undecidable one is released with the ambiguity stated. Safe to act on
    /// all three only because the queue was created this instant, so no merge can be in flight.
    /// </summary>
    private void ReconcileOutstandingLease(string repoHandle, string barePath, MergeQueueContext context)
    {
        var lease = _leases.GetOutstanding(repoHandle);
        if (lease is null)
        {
            return;
        }

        try
        {
            // The crash this exists for is "the human's merge landed on their checkout and the daemon
            // died before ConfirmMerge refreshed the mirror" — so an unrefreshed mirror would read the
            // merge that DID land as NeverCommitted and release the lease over it. Refresh first.
            TryRefreshMirrorMainAfterMerge(repoHandle, out _);
            AlignLeaseMainBranch(barePath, lease);
            BuildLeaseReconcile(barePath, context).Reconcile(lease);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge lease reconcile failed repo={repoHandle} lease={lease.LeaseId}: {ex.Message}");
            _audit.Append(new AuditEvent("merge_lease_reconcile_failed", new Dictionary<string, string>
            {
                ["repo"] = repoHandle,
                ["agent"] = lease.AgentId,
                ["lease"] = lease.LeaseId,
                ["reason"] = ex.Message,
            }));
        }
    }

    /// <summary>
    /// The on-demand half, for <c>BeginMerge</c>: when the repo's lease is held, ask git whether the merge
    /// it authorized has already <b>landed</b>, and if so record it so the next merge can proceed.
    ///
    /// <para>Acts on the <c>Merged</c> verdict ONLY. A lease that is merely outstanding may be a live
    /// human merge between <c>BeginMerge</c> and <c>ConfirmMerge</c>; releasing it on a
    /// <c>NeverCommitted</c> reading would grant a second, concurrent merge on the same repo — the one
    /// thing the lease exists to prevent. Those verdicts are left to the queue-creation reconcile, where
    /// nothing can be in flight.</para>
    /// </summary>
    /// <returns>True when a landed merge was recorded and the lease is no longer outstanding.</returns>
    public bool TryReconcileLandedLease(string repoHandle, out string reason)
    {
        reason = string.Empty;
        var context = _registry.Resolve(repoHandle);
        var lease = _leases.GetOutstanding(repoHandle);
        if (context is null || lease is null)
        {
            return false;
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        try
        {
            TryRefreshMirrorMainAfterMerge(repoHandle, out _);
            AlignLeaseMainBranch(barePath, lease);
            var task = BuildLeaseReconcile(barePath, context);
            var currentMain = RevParse(barePath, lease.MainBranch);
            if (task.Classify(barePath, lease, currentMain) != MergeReconcileTask.ReconcileVerdict.Merged)
            {
                reason = $"a merge of agent/{lease.AgentId} is still in progress (lease taken "
                       + $"{(DateTime.UtcNow - lease.BeginUtc).TotalMinutes:0} min ago)";
                return false;
            }

            _leases.Confirm(repoHandle, lease.LeaseId, currentMain);
            context.Queue.ConfirmHumanMerge(
                lease.AgentId, currentMain, MergeAuthorization.BootReconcile(lease.LeaseId));
            _log?.Invoke($"merge lease reconciled on demand repo={repoHandle} agent={lease.AgentId} main={currentMain}");
            return true;
        }
        catch (Exception ex)
        {
            reason = $"the outstanding merge lease could not be reconciled: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// <c>BeginMerge</c> writes the literal <c>"main"</c> onto every lease, and a mirror whose integration
    /// branch is called something else would read as "main unreadable" — an Undecidable that releases a
    /// lease over a merge that landed. The mirror's own default branch is the ref the merge moved, so the
    /// in-memory row is pointed at it when the recorded name resolves to nothing there.
    /// </summary>
    private static void AlignLeaseMainBranch(string barePath, Mainguard.Git.Models.MergeLeaseRow lease)
    {
        if (RevParse(barePath, lease.MainBranch).Length == 0)
        {
            lease.MainBranch = ResolveDefaultBranch(barePath);
        }
    }

    private MergeReconcileTask BuildLeaseReconcile(string barePath, MergeQueueContext context) =>
        new(
            _leases,
            // The daemon has no path to the user's checkout, so the T-19 journal fallback is unavailable
            // by construction — a branch ref that is gone reads Undecidable, which is the honest answer.
            new Mainguard.Git.Services.NullOperationJournal(),
            resolveRepoPath: _ => barePath,
            onMerged: (_, agentId, postSha) =>
                context.Queue.ConfirmHumanMerge(agentId, postSha, MergeAuthorization.BootReconcile()),
            onInterrupted: (repoHash, why) =>
            {
                _log?.Invoke($"merge lease released repo={repoHash}: {why}");
                _audit.Append(new AuditEvent("merge_lease_released", new Dictionary<string, string>
                {
                    ["repo"] = repoHash,
                    ["reason"] = why,
                }));
            });

    // ---- Restart re-arm (L1) ---------------------------------------------

    /// <summary>
    /// The most recent <see cref="RearmAfterRestart"/> pass. Same posture as
    /// <c>MergeQueue.LastCascade</c> and <c>MergeQueue.LastResume</c>: tests await it, production fires
    /// and forgets, and it is a completed no-op until something starts one.
    /// </summary>
    public Task LastRearm { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The states <see cref="RearmAfterRestart"/> re-arms the flagged-change review for: exactly the two
    /// <c>MergeQueue.CanMerge</c> admits.
    ///
    /// <para>Public, and asserted against the whole enum by
    /// <c>EveryStateThatCanMerge_IsAStateTheRestartRearmCovers</c>, because the two sets must not be
    /// allowed to drift: a state that can merge and is missing here is the L1 dead end reopened, and a
    /// state here that cannot merge is a git diff per entry per boot for nothing. Every other non-terminal
    /// state is already in the readiness trigger's eligible set (or is a run in flight), so its next
    /// verification arms the gate exactly as it always did.</para>
    /// </summary>
    public static readonly IReadOnlySet<WorkerMergeState> RearmableStates =
        new HashSet<WorkerMergeState> { WorkerMergeState.Verified, WorkerMergeState.AwaitingReview };

    /// <summary>Starts a <see cref="RearmAfterRestart"/> pass in the background and publishes it on
    /// <see cref="LastRearm"/>. Background for the reason <c>BeginResumeAfterRestart</c> is: the caller is
    /// inside a gRPC handler and this pass runs a git diff per entry.</summary>
    private Task BeginRearmAfterRestart(string repoHandle, MergeQueueContext context) =>
        LastRearm = Task.Run(() => RearmAfterRestart(repoHandle, context));

    /// <summary>
    /// Tells the freshly-rehydrated queue what the mirror says each entry's branch tip is <b>now</b>, and
    /// lets <see cref="MergeQueue.NotifyBranchAdvanced"/> decide what that means.
    ///
    /// <para><b>This is the durable half of the branch-side freshness compare.</b> The observed half —
    /// <c>BranchTipInvalidator</c> riding <c>AgentRefWatcher.Advanced</c> — cannot answer here, twice
    /// over: <c>_branchTip</c> is deliberately not persisted (§19.7), and the watcher only ever sweeps
    /// agents something called <c>Watch</c> for, i.e. agents with a live sandbox. So a branch that moved
    /// while the daemon was down, whose agent has since been stopped, is announced by nothing at all. What
    /// IS durable is the record's own <see cref="VerificationRecord.BranchSha"/> (J1, §19.4), and
    /// <see cref="MergeQueue.NotifyBranchAdvanced"/> already compares against exactly that — so this
    /// method only has to ask git the question.</para>
    ///
    /// <para><b>It runs inline, and that is load-bearing.</b> A tip announced into a queue that has since
    /// started a verification is recorded as a mid-run move and demotes the run's own green (§19.4's M8
    /// window). Running before the context is handed back means there is no such queue to race.</para>
    ///
    /// <para>A record that predates <c>BranchSha</c> reads as empty, does not match any real tip, and is
    /// therefore invalidated — a one-time demotion to <c>Working</c> that the readiness trigger re-verifies
    /// out of. That is the honest answer and not the belt's "both sides must be KNOWN to refuse": the belt
    /// guards a permanent refusal with no way out, and this walks the row to the one state from which the
    /// product re-measures it.</para>
    /// </summary>
    private void PrimeBranchTipsAfterRestart(string repoHandle, MergeQueue queue)
    {
        string barePath;
        try
        {
            barePath = _repos.BareRepoPathFor(repoHandle);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge queue repo={repoHandle} branch-tip prime SKIPPED ({ex.Message})");
            return;
        }

        foreach (var agentId in queue.Agents)
        {
            // Nothing to protect and nothing to learn: a terminal row is refused by NotifyBranchAdvanced
            // anyway, and asking git about it is a subprocess for an answer nobody reads.
            if (!RearmableStates.Contains(queue.GetState(agentId)))
            {
                continue;
            }

            try
            {
                // An empty answer (a mirror git could not resolve `agent/<id>` in) is passed through as
                // empty and ignored: an unknown tip must never be written over a known one.
                if (queue.NotifyBranchAdvanced(agentId, RevParse(barePath, "agent/" + agentId)))
                {
                    _log?.Invoke(
                        $"merge queue repo={repoHandle} agent={agentId} branch moved while the daemon was "
                        + "down — the entry is back on Working and is no longer mergeable");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke(
                    $"merge queue repo={repoHandle} agent={agentId} branch-tip prime FAILED ({ex.Message}) "
                    + "— this entry's merge gate falls back to the states it can refuse from");
            }
        }
    }

    /// <summary>
    /// Re-derives, from the mirror, the flagged-change classification a daemon restart destroys — for the
    /// entries where its absence is a dead end, and only after
    /// <see cref="PrimeBranchTipsAfterRestart"/> has already thrown out the ones whose evidence is about a
    /// different tree.
    ///
    /// <h3>The defect (L1): <c>Verified</c> plus a restart was a permanent dead end</h3>
    ///
    /// <para>Observed live: three <c>Verified</c> rows were unmergeable <b>forever</b> after a bounce.
    /// <see cref="FlaggedChangeGate"/> holds its per-agent <see cref="AcknowledgmentStore"/>s in memory, so
    /// a restart wipes every one of them; <see cref="ArmFlaggedChangeReview"/> — the only thing that ever
    /// creates one — runs solely inside a verification; §19.7 deliberately withholds Verify from a
    /// <c>Verified</c> row; and <c>WorkerReadinessTrigger</c>'s eligible set excludes <c>Verified</c>. So
    /// the gate answered its MG-40 default-DENY ("flagged-change review has not run for this branch (no
    /// acknowledgment record)") and <b>nothing in the product could ever re-arm it</b>. The default is
    /// right; what was missing was any path back.
    ///
    /// <h3>Why re-derive and never restore</h3>
    ///
    /// <para>The alternative — persisting the store — is the trap. A flagged set is a CLASSIFICATION OF A
    /// DIFF, i.e. a measurement, and §19.7 already settled what happens to a measurement written to
    /// SQLite: it outlives its own truth. Persisted acknowledgments are worse still, because
    /// <see cref="FlaggedChange.Id"/> is content-bound (<c>kind|path|contentHash</c>) precisely so an ack
    /// cannot survive the push that changes the bytes it was granted for, and a durable ack is a standing
    /// invitation to reconstruct the id it was granted under. Re-deriving needs no jail, no container and
    /// no agent — only the mirror — so the honest fix is to run the review again.</para>
    ///
    /// <h3>Why this cannot become a way to merge unreviewed work</h3>
    ///
    /// <list type="number">
    ///   <item><b>Nothing is restored, so nothing is trusted.</b> The pass writes no acknowledgment and
    ///   reads none from anywhere. A re-armed store comes back with zero acks, so every flagged item must
    ///   be acknowledged again, by a human, in the cockpit. A restart can only ever INCREASE the review
    ///   owed — it can never discharge any of it.</item>
    ///   <item><b>The set describes the bytes that will merge.</b> It is computed from the mirror's
    ///   current <c>agent/&lt;id&gt;</c> against main, at this instant, exactly as the verification path
    ///   computes it — so the items and their content hashes bind to today's diff, not to a remembered
    ///   one.</item>
    ///   <item><b>The evidence was checked against the tree first.</b>
    ///   <see cref="PrimeBranchTipsAfterRestart"/> runs inline, before this pass and before the queue is
    ///   handed to anyone, and walks any entry whose rehydrated
    ///   <see cref="VerificationRecord.BranchSha"/> does not name the mirror's current tip back to
    ///   <c>Working</c>. Only what survives that is still in <see cref="RearmableStates"/>, so re-arming
    ///   can never hand the gate to a row whose green is about bytes nobody will merge.</item>
    ///   <item><b>Fail-closed is untouched.</b> <see cref="ArmFlaggedChangeReview"/> leaves the store
    ///   ABSENT on any failure to compute the diff, and an absent store is still the default-DENY. This
    ///   pass adds a second chance to classify; it never adds a way to skip classification.</item>
    /// </list>
    ///
    /// <h3>Scope</h3>
    ///
    /// <para>Re-armed for exactly the states <c>CanMerge</c> admits — <c>Verified</c> and
    /// <c>AwaitingReview</c>. They are the only states in which a missing store is a dead end: every other
    /// non-terminal state is already in the readiness trigger's eligible set (or is a run in flight), so
    /// its next verification arms the gate as it always did. That equality is asserted, not assumed, by
    /// <c>EveryStateThatCanMerge_IsAStateTheRestartRearmCovers</c>.</para>
    ///
    /// <para>Nothing here throws: it runs on a background task nobody awaits in production, and a
    /// re-arm pass must never be the thing that takes the daemon down.</para>
    /// </summary>
    /// <param name="repoHandle">The repo whose queue was just rebuilt.</param>
    /// <param name="context">That queue and its gates.</param>
    /// <returns>The agent ids whose flagged-change review this pass re-armed.</returns>
    public IReadOnlyList<string> RearmAfterRestart(string repoHandle, MergeQueueContext context)
    {
        if (context.FlaggedChanges is not { } flaggedGate)
        {
            return Array.Empty<string>();
        }

        var queue = context.Queue;
        var rearmed = new List<string>();

        foreach (var agentId in queue.Agents)
        {
            try
            {
                // Only the states that could merge — PrimeBranchTipsAfterRestart has already run, inline,
                // so anything whose evidence is about a different tree is no longer one of them.
                if (!RearmableStates.Contains(queue.GetState(agentId)))
                {
                    continue;
                }

                // PeekStore, never StoreFor: this must not manufacture the empty (and therefore
                // trivially AllAcknowledged) store that FlaggedChangeGate.PeekStore exists to refuse.
                if (flaggedGate.PeekStore(agentId) is not null)
                {
                    continue;
                }

                ArmFlaggedChangeReview(repoHandle, agentId, flaggedGate);
                if (flaggedGate.PeekStore(agentId) is null)
                {
                    // The diff could not be computed. ArmFlaggedChangeReview already said so; the entry
                    // stays denied, which is the fail-closed default and not a regression.
                    continue;
                }

                rearmed.Add(agentId);
            }
            catch (Exception ex)
            {
                _log?.Invoke(
                    $"merge queue repo={repoHandle} agent={agentId} restart re-arm FAILED ({ex.Message}) "
                    + "— the entry stays unmergeable until it can be classified");
            }
        }

        if (rearmed.Count > 0)
        {
            _log?.Invoke(
                $"merge queue repo={repoHandle} restart re-armed the flagged-change review for "
                + $"{rearmed.Count} mergeable entr{(rearmed.Count == 1 ? "y" : "ies")} "
                + "(acknowledgments are NOT restored — every flagged item must be acknowledged again)");

            // The gate's ANSWER changed without any state moving, and that reaches a client only on the
            // queue stream, which re-pushes only on Changed.
            queue.NotifyGateChanged();
        }

        return rearmed;
    }

    /// <summary>Audit event appended when a queue row is withheld because its worker has no approved
    /// plan, carrying the gate's own <c>reason</c>. The paired admission is
    /// <see cref="QueueEntryAdmittedEvent"/>.</summary>
    public const string QueueEntryDeferredEvent = "queue_entry_deferred_no_plan";

    /// <summary>Audit event appended when a deferred row is created after its plan was approved.</summary>
    public const string QueueEntryAdmittedEvent = "queue_entry_admitted_on_plan";

    /// <summary>
    /// Rows withheld by the plan gate, keyed by (repo, agent) — the same composite key the gate itself
    /// uses, and for the same reason: an agent id is unique only within a repo.
    ///
    /// <para>Bounded the way <c>AgentRefMediator</c>'s per-agent publish gates are: one small entry per
    /// worker spawned but never approved, in one daemon lifetime, cleared on admission and on
    /// <see cref="Remove"/>. It holds no task, no prompt and no plan — only the fact that a row is owed.</para>
    /// </summary>
    private readonly Dictionary<(string RepoHandle, string AgentId), MergeEntryOrigin> _deferredEntries = new();

    /// <summary>
    /// Ensures the repo's queue exists AND that <paramref name="agentId"/> has an entry in it (the agent
    /// joined the swarm). Without this an agent's branch is invisible to <c>StreamQueue</c>: the queue only
    /// reports agents it tracks, and nothing else in the daemon ever adds one.
    ///
    /// <para><b>G1 — a queue row now requires an approved plan.</b> Three <c>scripted</c> probes that made
    /// ZERO plan calls and received ZERO approvals each got a merge-queue row, at the same time as
    /// <c>get_worker_status</c> correctly answered "no work is authorised". The row was created here, and
    /// this method consulted nothing.</para>
    ///
    /// <para><b>Why the ROW and not the publish.</b> A branch existing is not the harm — F1 established
    /// that an agent's branch must survive its teardown, so refusing to publish would destroy work to fix a
    /// display problem. A queue row is something else: it is a claim on human attention, an entry in the
    /// list a person is asked to work through, and it comes with Verify — i.e. with the daemon offering to
    /// spend a test-suite run on work nobody authorised. That is what should require an approved plan, and
    /// that is what this gates.</para>
    ///
    /// <para><b>The gate is the same object, asked the same question.</b> <see cref="_planGate"/> is the
    /// <see cref="IMergeGate"/> already ANDed into every queue, so an id it never held (a manual-mode
    /// agent, an external-PR head, an unseeded id) is permitted here exactly as it is permitted to merge —
    /// a second opinion about what "approved" means is how one of the two copies goes decorative (MG-12).
    /// Only a worker this gate is HOLDING, and whose plan is not approved, is withheld.</para>
    ///
    /// <para><b>Nothing is lost by waiting.</b> A withheld row is remembered and created by
    /// <see cref="AdmitDeferredEntries"/> the moment the plan is approved, so the normal path — spawn,
    /// present, approve, work — puts the row in front of the human at the point it starts meaning
    /// something, rather than at the point the jail happened to attach.</para>
    /// </summary>
    public void EnsureEntry(string repoHandle, string agentId, MergeEntryOrigin origin = MergeEntryOrigin.Local)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        if (_planGate is not null && !_planGate.Allows(agentId, out var reason))
        {
            lock (_gate)
            {
                _deferredEntries[(repoHandle ?? string.Empty, agentId)] = origin;
            }

            // The ROW is withheld and nothing else is. EnsureQueue is called anyway because this method
            // has always been what BUILDS a coordinator-spawned worker's repo queue — that spawn path
            // creates its worktree inside the launcher rather than through the RepoSync RPC, so returning
            // early here would also skip registering the queue, the main-sha reconcile and the restart
            // resume for the whole repository. Gating a row is not a reason to stop governing a repo.
            EnsureQueue(repoHandle);

            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} — no queue row yet: {reason}");
            _audit.Append(new AuditEvent(QueueEntryDeferredEvent, new Dictionary<string, string>
            {
                ["repo"] = repoHandle ?? string.Empty,
                ["agent"] = agentId,
                ["reason"] = reason ?? string.Empty,
            }));
            return;
        }

        EnsureQueue(repoHandle)?.Queue.EnsureEntry(agentId, origin);
    }

    /// <summary>
    /// Creates the rows that <see cref="EnsureEntry"/> withheld and whose workers the plan gate now
    /// permits. Returns the agent ids admitted (empty on a pass that moved nothing).
    ///
    /// <para>This is the other half of G1, and without it the fix would be a regression: a worker held at
    /// the gate is the NORMAL case — every coordinator-spawned worker is spawned before it has presented
    /// anything — so gating the row without a way back would mean a legitimately approved worker never got
    /// one. The daemon calls this on <c>PlanApprovalService.PlanApproved</c>, which is the exact moment the
    /// gate's answer changes.</para>
    ///
    /// <para><b>It re-asks the gate; it does not trust the caller.</b> The event says "a plan was
    /// approved", not "this agent may now have a row" — the two differ for an id whose plan was approved
    /// and then superseded, and for every other id in the deferred set. So each candidate is put back
    /// through the same predicate <see cref="EnsureEntry"/> used, and one that still fails simply stays
    /// deferred. Passing the approved worker's id in as a hint would make this a second authority.</para>
    /// </summary>
    public IReadOnlyList<string> AdmitDeferredEntries()
    {
        List<KeyValuePair<(string RepoHandle, string AgentId), MergeEntryOrigin>> candidates;
        lock (_gate)
        {
            if (_deferredEntries.Count == 0)
            {
                return Array.Empty<string>();
            }

            candidates = _deferredEntries.ToList();
        }

        var admitted = new List<string>();
        foreach (var (key, origin) in candidates)
        {
            if (_planGate is not null && !_planGate.Allows(key.AgentId, out _))
            {
                continue;
            }

            // Dropped from the deferred set BEFORE the row is created, and unconditionally: EnsureQueue can
            // still answer null (a repo whose mirror went away), and a candidate that is now AUTHORISED but
            // unprovisionable must not be retried on every future approval for the rest of the daemon's
            // life. Its own next EnsureEntry — a resume, a re-provision — creates the row, with the gate
            // now saying yes.
            lock (_gate)
            {
                _deferredEntries.Remove(key);
            }

            EnsureQueue(key.RepoHandle)?.Queue.EnsureEntry(key.AgentId, origin);
            admitted.Add(key.AgentId);
            _log?.Invoke(
                $"merge queue repo={key.RepoHandle} agent={key.AgentId} — plan approved; the queue row is now live");
            _audit.Append(new AuditEvent(QueueEntryAdmittedEvent, new Dictionary<string, string>
            {
                ["repo"] = key.RepoHandle,
                ["agent"] = key.AgentId,
            }));
        }

        return admitted;
    }

    /// <summary>The (repo, agent) pairs currently owed a queue row — exposed so the withholding is
    /// observable rather than only inferable from a row's absence.</summary>
    public IReadOnlyList<(string RepoHandle, string AgentId)> DeferredEntries()
    {
        lock (_gate)
        {
            return _deferredEntries.Keys.ToList();
        }
    }

    /// <summary>Drops a repo's queue on teardown (the handle resolves to NOT_FOUND again).</summary>
    public void Remove(string repoHandle)
    {
        lock (_gate)
        {
            // The owed rows go with the queue they were owed against — a repo that is no longer governed
            // has nothing to admit them into.
            foreach (var key in _deferredEntries.Keys
                .Where(k => string.Equals(k.RepoHandle, repoHandle, StringComparison.Ordinal)).ToList())
            {
                _deferredEntries.Remove(key);
            }
        }

        _registry.Remove(repoHandle);
    }

    // ---- Internals -------------------------------------------------------

    private MergeQueueContext Build(string repoHandle, string mainSha)
    {
        // The RT-D2 gate is per-repo-queue because its flag state is per-branch and its acknowledgment is
        // the human's; sharing one across repos would let one repo's ack clear another's flag. It gets the
        // daemon's audit log for the same reason the flagged-change gate below does: waiving "this branch
        // changed the command that verifies it" is the single most security-relevant acknowledgment in the
        // product, and it wrote nothing anywhere until it was handed a sink.
        var changedTestCommand = new ChangedTestCommandGate(_audit);

        // P2-11, and the point of this change: the flagged-change gate is per-repo-queue for the same
        // reason the RT-D2 one is — its acknowledgments are per-branch and belong to the human who read
        // that branch's diff. It shipped complete and was constructed nowhere outside the test projects and
        // one dead ViewModel branch, so the daemon ANDed a single gate into CanMerge and the entire
        // human-diff-review boundary (executable config, CI workflows, git hooks, security-sensitive paths,
        // and the plan's approved scope) was evaluated by nothing. Adding it here is what makes the review
        // a merge precondition rather than a rendering.
        var flaggedChanges = new FlaggedChangeGate(_audit);

        // Every gate here is independent and they AND: the RT-D2 changed-test-command gate, the P2-11
        // flagged-change review, and (when the daemon supplied one) the phase-2 plan gate. Adding a gate is
        // always additive — dropping one to make a set "simpler" silently deletes a merge precondition.
        var gates = _planGate is null
            ? new IMergeGate[] { changedTestCommand, flaggedChanges }
            : new IMergeGate[] { changedTestCommand, flaggedChanges, _planGate };

        // P2-09's keep-alive rebaser, per repo because its `locate` closes over the repo handle. Built
        // here and not once per provisioner so a repo whose mirror the daemon cannot resolve simply has
        // no rebaser, rather than one that throws on every cascade.
        var rebaser = BuildRebaser(repoHandle);

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: repoHandle,
            currentMainSha: mainSha,
            store: _queueStore(repoHandle),
            verifications: _verificationStore(repoHandle),
            runVerification: (agentId, ct) =>
                RunVerificationAsync(repoHandle, agentId, queue, changedTestCommand, flaggedChanges, ct),
            // P2-10 §3.3 step 2, and the whole point of that change: "yield → keep-alive rebase onto new
            // main → RunVerificationAsync". This argument was `null` — i.e. re-verify only — which reads
            // like a lighter cascade and is not one. `git merge --ff-only` is the merge, so the instant
            // any agent merges, every co-tenant branch stops descending from main; re-verifying them
            // re-establishes a PASS against work that was never rebased, the entry returns to Verified,
            // and the merge is then refused as stale. Nothing in the daemon could break that loop.
            requeue: (agentId, ct) => RequeueStaleAsync(repoHandle, agentId, queue, rebaser, ct),
            // `gates`, NOT a fresh literal: the phase-2 plan gate is only present when the daemon supplied
            // one, and rebuilding the array inline here would drop it — the exact silent-deletion this
            // forward merge had to resolve, since both sides edited this argument list.
            gates: gates,
            audit: _audit,
            // The branch's merge state, reported back onto the AGENT — the seam a coordinator's
            // `get_worker_status` and the client's agent stream both read. Without it a worker whose
            // branch verified green stayed at the liveness word its sandbox attach wrote ("Working")
            // for the rest of its life, so the coordinator could never report that its own fan-out had
            // finished. Same sink and same posture as MarkRunState, one line above the same supervisor.
            onStateChanged: (agentId, state) => MarkMergeState(repoHandle, agentId, state));

        return new MergeQueueContext(queue, _leases)
        {
            ChangedTestCommand = changedTestCommand,
            FlaggedChanges = flaggedChanges,
            // The SAME callback ArmFlaggedChangeReview measures the diff against, handed to the projection
            // that renders the approval to the human. Resolving it twice from two seams is how the
            // reviewer ends up reading one plan's approach beside another plan's verdict.
            ResolveApprovedWork = _resolveApprovedWork,
        };
    }

    /// <summary>Audit event carrying the T-04 <see cref="ConflictHandoff"/> payload for a conflicted
    /// keep-alive rebase — the durable record of a worktree parked for a human.</summary>
    public const string KeepAliveConflictEvent = "keepalive_rebase_conflict";

    /// <summary>
    /// The per-repo keep-alive rebaser, or null when this provisioner was not given the two things a
    /// rebase cannot be performed without (the yield gateway and a way to find the worktree).
    /// </summary>
    private IKeepAliveRebaser? BuildRebaser(string repoHandle)
    {
        if (_yieldFor is null || _locateAgentWorktree is null)
        {
            return null;
        }

        return new KeepAliveRebaser(
            yield: _yieldFor(repoHandle),
            // RequeueStaleAsync establishes the worktree BEFORE it calls a cycle, so this only ever runs
            // for an agent already known to have one. The throw is the contract restated, not a path.
            locate: agentId => LocateAgentWorktree(repoHandle, agentId)
                ?? throw new InvalidOperationException(
                    $"Agent '{agentId}' in repo '{repoHandle}' has no worktree to rebase."),
            // The run state reaches clients on the agent-event stream, which is what makes a Conflict
            // visible at all: AgentRunState.Conflict had no production writer, so the one arm of the cycle
            // that requires a human was also the one arm nothing rendered.
            setState: (agentId, state) => MarkRunState(repoHandle, agentId, state),
            onConflict: handoff => OnRebaseConflict(repoHandle, handoff));
    }

    /// <summary>
    /// One stale-cascade re-entry for one branch: <b>reparent, then re-verify</b> — and re-verify
    /// <i>only</i> if the reparent actually happened.
    ///
    /// <para><b>The ordering is the fix.</b> Verification pins its record to the queue's current main and
    /// asks nothing about ancestry, so a branch that was not rebased passes and looks fresh; only
    /// <c>--ff-only</c> ever finds out, at merge time, and its refusal sends the entry straight back round
    /// the same loop. So the rebase is a precondition of the re-verification, not a step beside it, and a
    /// cycle that did not put the branch on top of main ends the re-entry at <c>Working</c> with the
    /// measured reason instead.</para>
    ///
    /// <para><b>Every arm here ends somewhere a human can act from.</b> That is the second requirement,
    /// and it is why there are four of them:</para>
    /// <list type="bullet">
    ///   <item><b>No jail</b> — the agent was stopped, so there is nothing to yield and (§3.2) nothing to
    ///   verify in either. The entry returns to <c>Working</c> naming the missing sandbox, which is what
    ///   the <c>ResumeAgent</c> path exists to answer. It is deliberately NOT handed to
    ///   <c>RunVerificationAsync</c> to fail: that route reaches the same state through a
    ///   "verification failed"-shaped event for a verification that never ran.</item>
    ///   <item><b>No worktree</b> — the jail is up but its worktree is gone (a pruned or half-torn-down
    ///   agent). Same terminus, different reason; a rebase would throw and the cascade would swallow it.</item>
    ///   <item><b>Conflict</b> — the rebase hit the human's changes. The worktree stays parked mid-rebase
    ///   for T-04, the jail stays paused, and <see cref="OnRebaseConflict"/> makes that state legible.</item>
    ///   <item><b>Skipped</b> — the guard refused (the agent is mid its own rebase), or main could not be
    ///   carried across. Nothing was mutated; the branch is simply not reparentable this instant.</item>
    /// </list>
    ///
    /// <para>Nothing here throws for a reason other than cancellation: <see cref="MergeQueue"/> runs the
    /// cascade on a background task whose <c>catch</c> is silent, so an exception escaping this method is
    /// a branch that vanishes from the cascade with no record anywhere. Failures are converted into the
    /// same <c>Working</c>-plus-reason terminus as everything else.</para>
    /// </summary>
    private async Task RequeueStaleAsync(
        string repoHandle, string agentId, MergeQueue queue, IKeepAliveRebaser? rebaser, CancellationToken ct)
    {
        // Still stale? The FIFO was captured when main moved; by the time this entry's turn comes it may
        // have been discarded, rejected, or re-verified by someone else. Yielding, pausing and rebasing a
        // worktree on behalf of an entry that is no longer waiting for it would be side effects nobody
        // asked for, with the re-verify at the end throwing from a state it cannot leave.
        if (queue.GetState(agentId) != WorkerMergeState.StaleVerified)
        {
            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} stale re-queue skipped — the entry is "
                + $"{queue.GetState(agentId)}, not StaleVerified");
            return;
        }

        // Dev-only seeding (docs/design/queue-seeding.md §5): the cascade's re-queue for a SEEDED
        // entry ends at one of the two termini production itself exhibits, chosen per plan. Hold
        // leaves the entry resting at StaleVerified — indistinguishable from awaiting its FIFO turn,
        // which is the state a human seeds in order to look at. Cascade takes the real no-jail
        // terminus directly: a seeded entry has no sandbox by construction, so it must NOT fall into
        // the null-rebaser convenience below, whose re-verify would mint a fresh Verified for a
        // branch that no longer descends from main — the exact loop-forever defect the reparenting
        // cascade exists to end.
        if (_synthetic?.TryGet(repoHandle, agentId) is { } seeded)
        {
            if (seeded.StaleBehavior == SyntheticStaleBehavior.Hold)
            {
                _log?.Invoke(
                    $"merge queue repo={repoHandle} agent={agentId} seeded entry HELD at StaleVerified "
                    + "(stale-behavior Hold)");
                return;
            }

            Block(repoHandle, agentId, queue,
                NoLiveSandboxReason, "seeded-no-jail", sandboxIsGone: true);
            return;
        }

        if (rebaser is null)
        {
            // No rebase capability was wired (the pure-unit paths). Preserve the historical behaviour
            // exactly rather than inventing a third one.
            await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
            return;
        }

        // The same jail question the verification itself asks, asked first — because if the answer is no,
        // neither the yield nor the verification can happen, and there is nothing to be gained from
        // finding that out twice.
        if (string.IsNullOrEmpty(_resolveContainerId(repoHandle, agentId)))
        {
            Block(repoHandle, agentId, queue, NoLiveSandboxReason, "no-live-jail", sandboxIsGone: true);
            return;
        }

        if (LocateAgentWorktree(repoHandle, agentId) is null)
        {
            Block(repoHandle, agentId, queue,
                "this branch needs rebasing onto the new main and its worktree is gone — resume the agent",
                "no-worktree");
            return;
        }

        // The main this branch is reparented ONTO must be the main its re-verification will be pinned
        // AGAINST. LocateAgentWorktree resolves both from the same mirror so the two cannot be different
        // REFS — and for a window they were the same ref at two different SHAS, which is the same defect
        // wearing a clock. `ConfirmMerge` fires this cascade from inside `TryConfirmHumanMerge` and pulls
        // the mirror's main forward AFTERWARDS, so a cascade that got here first carried the PRE-merge
        // main into the agent's repository; `git rebase main` then found the branch already on top of it,
        // exited 0 without moving anything, and the cycle reported `CleanNoop` — which
        // `BranchIsOnTopOfMain` reads as "this may be re-verified". The entry went green against the new
        // main while its branch did not descend from it, and the human's `--ff-only` merge refused it
        // forever, with the rail reading "ready to merge".
        //
        // This is the half of the defect `KeepAliveRebaser.TryRefreshMainFromMirror` did not close.
        // Reading the fetch's exit code closed the case where the fetch FAILS; a fetch that succeeds
        // against a mirror which is merely BEHIND establishes a main just as confidently, and the wrong
        // one. So the mirror is caught up here — the same one-refspec pull the merge-confirm makes, made
        // idempotent so the cascade never has to win a race with it — and a mirror that still disagrees
        // blocks rather than mints a green.
        if (!TryAlignMirrorMain(repoHandle, queue.CurrentMainSha, out var mirrorMain))
        {
            Block(repoHandle, agentId, queue,
                "this branch is not on top of the new main yet — the daemon's mirror is still on an older "
                + "main and could not be caught up, so a rebase now would reparent it onto the wrong commit",
                $"mirror-main-behind:{mirrorMain}");
            return;
        }

        RebaseCycleResult cycle;
        try
        {
            cycle = await rebaser.NotifyMainMoved(agentId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Block(repoHandle, agentId, queue,
                $"this branch could not be rebased onto the new main ({ex.Message})", "rebase-threw");
            return;
        }

        if (cycle.Kind == RebaseCycleKind.Conflict)
        {
            Block(repoHandle, agentId, queue, RebaseConflictReason, "conflict");

            // ...and then say CONFLICT again, because the line above just erased it.
            //
            // The run-state axis and the merge-state axis are two vocabularies sharing ONE field on the
            // session (MarkRunState and MarkMergeState call the same IAgentSupervisor.MarkState). The
            // cycle wrote `Conflict` moments ago; `Block` moves the queue entry to Working, whose
            // transition notice reflects the MERGE word onto the same field — so a jail that is
            // `docker pause`d and parked mid-rebase ended up reporting `Working`, which is what an agent
            // busily making progress reports.
            //
            // That is not cosmetic. The daemon's coordinator-facing prompt and verify guards, and this
            // repo's own RunVerification guard, key on that state word (FrozenJailPolicy: Paused or
            // Conflict) precisely because it is the fact every surface already projects. Reporting
            // `Working` for a frozen jail is the one answer that makes all of them wave a delivery through
            // into a SIGSTOPped process — until AgentSessionReconciler's interval-driven pause pass
            // happens to correct the word, which is a window, not a design.
            //
            // Re-asserted here rather than by teaching MarkMergeState to skip this case: the ordering is
            // local, it is the last thing the cascade does to this agent, and a suppression rule inside
            // the merge-state reflection would have to know about a run state it otherwise never reads.
            MarkRunState(repoHandle, agentId, AgentRunState.Conflict);
            // …and the pause AXIS, which no later merge transition overwrites. The re-assert above closes
            // the ordering within this cycle; this closes the window after it, when the queue's next
            // transition rewrites the word while the jail is still SIGSTOPped.
            MarkFrozen(repoHandle, agentId, ConflictFrozenReason);
            return;
        }

        if (!cycle.BranchIsOnTopOfMain)
        {
            Block(repoHandle, agentId, queue,
                $"this branch is not on top of the new main yet — {cycle.Detail ?? "the keep-alive rebase was skipped"}",
                "skipped");
            return;
        }

        // The reparented branch has to REACH the mirror, and the ordinary publish cannot carry it: a
        // rebase is not a fast-forward, so MG-3's mediator refuses it as rewritten history. Left there,
        // the whole cycle succeeds and changes nothing observable — the queue, the cockpit and the human's
        // merge all read the mirror, and the mirror would still hold the un-rebased branch whose --ff-only
        // merge refuses. This is the daemon-rebase publish, checked for lost work by patch-id instead.
        if (_publishRebasedAgentRef is not null && !_publishRebasedAgentRef(repoHandle, agentId))
        {
            Block(repoHandle, agentId, queue,
                "this branch was rebased onto the new main but the rebased branch could not be published "
                + "to the mirror — the merge would still see the old, unmergeable branch",
                "publish-refused");
            return;
        }

        // The belt on the whole re-entry, asked of git rather than inferred from a cycle kind: does the
        // branch the queue is about to verify actually DESCEND from the main that verification will be
        // pinned to? That is the single predicate `BranchIsOnTopOfMain` stands for, and every way the
        // cascade has ever got it wrong — a stale rebase target, a rebase that exited 0 having moved
        // nothing, a publish that reported success — ends with this answer being no. It costs one
        // `merge-base`, it depends on no event having fired, and it is the only one of the three checks
        // above it that cannot be fooled by a component reporting its own success.
        //
        // It refuses only on a POSITIVE mismatch: both shas must be readable. A substrate with no mirror
        // (the pure-unit doubles) answers nothing, and inventing a refusal from ignorance would strand
        // every entry on it.
        if (!BranchDescendsFromMain(repoHandle, agentId, queue.CurrentMainSha, out var branchTip))
        {
            Block(repoHandle, agentId, queue,
                "this branch is not on top of the new main yet — the keep-alive cycle reported success but "
                + "the published branch still does not descend from main, so the merge would be refused",
                $"not-descended:{branchTip}");
            return;
        }

        _log?.Invoke(
            $"merge queue repo={repoHandle} agent={agentId} reparented onto main "
            + $"(wip={cycle.WipCommitCreated}) — re-verifying");

        await queue.RunVerificationAsync(agentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes that the mirror's main is the main the QUEUE moved to, catching the mirror up if it is
    /// not. See the call site for the defect this closes.
    /// </summary>
    /// <param name="expectedMainSha">The queue's own <c>main@sha</c>. Empty means the queue has never been
    /// told one (the substrate-less doubles), and there is nothing to establish — aligned by definition.</param>
    /// <param name="mirrorMain">The mirror's main after the attempt, for the audit detail.</param>
    private bool TryAlignMirrorMain(string repoHandle, string expectedMainSha, out string mirrorMain)
    {
        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        mirrorMain = RevParse(barePath, mainBranch);

        if (string.IsNullOrEmpty(expectedMainSha)
            || string.Equals(mirrorMain, expectedMainSha, StringComparison.Ordinal))
        {
            return true;
        }

        // Only a mirror we can READ can be known to be behind. An unreadable one is the substrate-less
        // shape, and refusing from ignorance would block every cascade on it.
        if (string.IsNullOrEmpty(mirrorMain))
        {
            return true;
        }

        // A mirror that already CONTAINS the queue's main is ahead of it, not behind it, and the refresh
        // below is a FORCED single-refspec fetch — running it here would drag the mirror's main backwards
        // to whatever origin happens to hold. Being ahead is not this method's business (it is the
        // pre-existing shape, and the reconcile owns it); being behind is.
        if (TryGit(barePath, out _, "merge-base", "--is-ancestor", expectedMainSha, mainBranch))
        {
            return true;
        }

        TryRefreshMirrorMainAfterMerge(repoHandle, out _);
        mirrorMain = RevParse(barePath, mainBranch);
        return string.Equals(mirrorMain, expectedMainSha, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the mirror holds <paramref name="sha"/> as a commit at all. The belt in
    /// <see cref="RunVerificationAsync"/> asks this before asking about descent: a queue main the mirror
    /// has not yet fetched (the window between a confirm and the mirror refresh) is an UNKNOWN, and
    /// <c>merge-base --is-ancestor</c> answers "no" for an unknown sha exactly as it does for a real
    /// non-descent — so without this the belt would refuse from ignorance.
    /// </summary>
    private bool MirrorKnowsCommit(string repoHandle, string sha) =>
        !string.IsNullOrEmpty(sha)
        && TryGit(_repos.BareRepoPathFor(repoHandle), out _, "rev-parse", "--verify", "--quiet", sha + "^{commit}");

    /// <summary>
    /// Whether the mirror's <c>agent/&lt;id&gt;</c> really contains the queue's main — the predicate the
    /// whole re-entry exists to establish, asked of git. False only when BOTH shas are known and the
    /// answer is no.
    /// </summary>
    private bool BranchDescendsFromMain(
        string repoHandle, string agentId, string expectedMainSha, out string branchTip)
    {
        var barePath = _repos.BareRepoPathFor(repoHandle);
        var branchRef = $"refs/heads/agent/{agentId}";
        branchTip = RevParse(barePath, branchRef);

        if (string.IsNullOrEmpty(expectedMainSha) || string.IsNullOrEmpty(branchTip))
        {
            return true;
        }

        return TryGit(barePath, out _, "merge-base", "--is-ancestor", expectedMainSha, branchRef);
    }

    /// <summary>
    /// Where this agent's keep-alive rebase runs, or null when it has no worktree on disk.
    ///
    /// <para>The mirror and the main branch are resolved HERE rather than taken from the caller, and from
    /// the same two calls <see cref="EnsureQueue"/> uses to key the queue's <c>main@sha</c>. That is what
    /// makes "the main this branch is rebased onto" and "the main this branch is verified against"
    /// provably the same ref: two independent answers to that question would disagree exactly once, at
    /// which point a branch would be rebased onto one main and declared fresh against another.</para>
    /// </summary>
    private AgentWorktreeLocation? LocateAgentWorktree(string repoHandle, string agentId)
    {
        string? worktree;
        try
        {
            worktree = _locateAgentWorktree?.Invoke(repoHandle, agentId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} worktree lookup FAILED ({ex.Message})");
            return null;
        }

        if (string.IsNullOrEmpty(worktree) || !System.IO.Directory.Exists(worktree))
        {
            return null;
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        return new AgentWorktreeLocation(worktree, barePath, ResolveDefaultBranch(barePath));
    }

    /// <summary>
    /// The cascade's terminus for a branch whose agent has been stopped — the one measured reason that
    /// names BOTH facts a human needs (the branch must be rebased, and there is no sandbox to do it in),
    /// and therefore the one that must outrank <see cref="MergeQueue.StrandedReason"/>.
    ///
    /// <para>A constant because the two writers (the seeded path and the real no-jail probe) say the same
    /// thing about the same situation, and because a test must be able to name the exact sentence the
    /// daemon logs rather than a paraphrase of it.</para>
    /// </summary>
    public const string NoLiveSandboxReason =
        "this branch needs rebasing onto the new main and its agent has no live sandbox — resume the agent";

    /// <summary>
    /// The cascade's terminus for a branch whose keep-alive rebase hit the human's changes: the worktree
    /// is parked mid-rebase, the jail is paused, and neither is undone automatically (no
    /// <c>rebase --abort</c> — a rejection trigger).
    ///
    /// <para>A constant for the same two reasons <see cref="NoLiveSandboxReason"/> is, plus a third: it
    /// is the sentence the card renders verbatim, it names a required human action, and the two controls
    /// that make that action possible — <see cref="LetAgentResolveConflictAsync"/> and
    /// <see cref="AbortParkedRebaseAsync"/> — have to be offered for exactly the entries wearing it.</para>
    /// </summary>
    public const string RebaseConflictReason =
        "rebasing this branch onto the new main hit a conflict — the agent is paused with the "
        + "rebase in progress and needs a human to resolve it";

    /// <summary>
    /// The verification refusal for a branch that does not descend from the queue's main. A constant
    /// prefix, because the queue rail renders it verbatim as the <c>CanMerge</c> reason and a test has to
    /// be able to name the sentence rather than a paraphrase of it.
    /// </summary>
    public const string NotOnTopOfMainReasonPrefix =
        "this branch is not on top of the queue's main — rebase needed";

    private static string NotOnTopOfMainReason(string agentId, string branchTip, string mainSha) =>
        $"{NotOnTopOfMainReasonPrefix}: agent/{agentId} at {Short(branchTip)} does not contain "
        + $"main@{Short(mainSha)}, so a passing run would mint a Verified that `--ff-only` refuses";

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    // The one terminus for every way a re-entry can fail to reparent: Working, with the reason a human
    // reads on the queue rail (MergeQueue renders it verbatim as the CanMerge reason) and an audit event.
    //
    // `sandboxIsGone` is what makes that parenthesis true again for the no-jail arm: CanMerge used to
    // order its generic StrandedReason ahead of every measured reason, so the one branch of this method
    // that had actually ESTABLISHED the missing sandbox had its sentence replaced by a vaguer one. See
    // MergeQueue.WorkingReason for why it is a per-reason flag rather than a blanket precedence.
    private void Block(
        string repoHandle, string agentId, MergeQueue queue, string reason, string detail,
        bool sandboxIsGone = false)
    {
        _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} stale re-queue BLOCKED ({detail}) — {reason}");
        queue.TryReturnToWorking(agentId, reason, detail, sandboxIsGone);
    }

    /// <summary>
    /// Reflects a keep-alive run state on the agent, so the states the cycle transitions through are
    /// observable rather than internal to a background task.
    /// </summary>
    /// <summary>What the pause axis says while a keep-alive rebase is parked on a conflict.</summary>
    public const string ConflictFrozenReason =
        "its keep-alive rebase onto the new main conflicted, so the daemon froze the jail with the rebase "
        + "still in progress and a human has to resolve it";

    /// <summary>Sets or clears the session's pause axis; reporting must never be able to fail a rebase.</summary>
    private void MarkFrozen(string repoHandle, string agentId, string? reason)
    {
        try
        {
            _agentStates.MarkFrozen(agentId, reason);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} frozen mark FAILED ({ex.Message})");
        }
    }

    private void MarkRunState(string repoHandle, string agentId, AgentRunState state)
    {
        var detail = state switch
        {
            AgentRunState.Yielding => "Yielding for a keep-alive rebase onto the new main.",
            AgentRunState.Rebasing => "Rebasing onto the new main after a merge.",
            AgentRunState.Conflict => "The keep-alive rebase conflicted; the jail is paused pending resolution.",
            _ => null,
        };

        try
        {
            _agentStates.MarkState(agentId, state.ToString(), detail);
        }
        catch (Exception ex)
        {
            // Reporting a state must never be able to fail a rebase.
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} state mark FAILED ({ex.Message})");
        }
    }

    /// <summary>
    /// Reflects a branch's MERGE state on the agent that produced it, so "is this worker done?" has an
    /// answer anywhere outside the queue's own projection.
    ///
    /// <para><b>The states are the queue's, verbatim.</b> <see cref="WorkerMergeState"/>'s names are the
    /// vocabulary already carried on the wire, rendered on the rail and persisted on the row; re-spelling
    /// them here would be a second vocabulary for one fact, which is how the two drift. The session store
    /// treats them as first-class already — <c>AgentSessionReconciler</c>'s drift pass says so out loud,
    /// refusing to flatten "orchestration meaning the container cannot know — RateLimited, Yielding,
    /// AwaitingReview" back to <c>Working</c>, and correcting only the pause axis.</para>
    ///
    /// <para><b>What it is NOT.</b> Not a lifecycle claim: the jail may be running, paused or long gone,
    /// and the reconciler still owns that axis. It is the branch's standing, on the agent, which is the
    /// one thing a coordinator can act on and the one thing it could not see.</para>
    /// </summary>
    private void MarkMergeState(string repoHandle, string agentId, WorkerMergeState state)
    {
        var detail = state switch
        {
            WorkerMergeState.Working =>
                "Back at work — this branch is not verified against the current main.",
            WorkerMergeState.Verifying =>
                "Verifying this branch against main, in the agent's own jail.",
            WorkerMergeState.Verified =>
                "Verified against the current main — waiting for a human to review and merge it.",
            WorkerMergeState.StaleVerified =>
                "Main moved, so this branch's verification no longer counts; it is being re-queued.",
            WorkerMergeState.AwaitingReview => "Waiting for your review.",
            // H2 — the coordinator's window onto its own fan-out is this word plus this sentence
            // (get_worker_status, contract §3). A red verification used to report as `Working` / "back at
            // work", which is what an unverified branch reports: a coordinator was structurally unable to
            // learn that its worker's tests had failed, and so could neither steer it nor say so. This is
            // the third surface the missing state was lying to, and it is the one whose reader is an agent
            // that will act on the answer.
            WorkerMergeState.VerificationFailed =>
                "The verification FAILED on this branch — the tests ran and did not pass. Read the run "
                + "output, then fix the branch and push; it verifies again on the new commit.",
            WorkerMergeState.Merged => "Merged into main.",
            WorkerMergeState.Rejected => "Rejected in review.",
            WorkerMergeState.Discarded => "Dropped from the merge queue.",
            _ => null,
        };

        try
        {
            _agentStates.MarkState(agentId, state.ToString(), detail);
        }
        catch (Exception ex)
        {
            // Reporting a state must never be able to fail a queue transition — the row is already
            // written by the time this runs.
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} merge-state mark FAILED ({ex.Message})");
        }
    }

    /// <summary>
    /// The T-04 handoff route, given a destination.
    ///
    /// <para><b>There is no T-04 resolver yet, and this does not pretend otherwise.</b> What it does is
    /// make the conflict a thing that exists outside the background task that produced it: the worktree
    /// path and branch land in the audit trail (so the resolver, when it lands, has the handoff it was
    /// always meant to receive), the merge log names it, and the agent is marked
    /// <see cref="AgentRunState.Conflict"/> — which the daemon's supervisor streams to clients as a state
    /// change. The queue entry is separately returned to <c>Working</c> carrying the same explanation, so
    /// the branch reads as needing a human rather than as quietly unverified.</para>
    ///
    /// <para>Before this, all of that was dead: <see cref="AgentRunState.Conflict"/> had no production
    /// writer, <see cref="ConflictHandoff"/> was constructed nowhere outside tests, and a conflicted
    /// worktree was left parked with nothing anywhere naming it — indistinguishable from an agent that
    /// simply stopped making progress.</para>
    /// </summary>
    private void OnRebaseConflict(string repoHandle, ConflictHandoff handoff)
    {
        // Measured HERE and nowhere later. `git diff --diff-filter=U` only answers while the rebase is in
        // progress, and the whole point of the parking is that it stays that way until a human acts — but
        // an agent with a shell in that worktree can `git add` a path out of the unmerged set at any time,
        // so an answer taken at render time would drift from the answer the daemon blocked the entry on.
        var conflicted = MeasureConflictedPaths(handoff.WorktreePath);
        var parked = new ParkedRebaseConflict(
            handoff.AgentId, handoff.WorktreePath, handoff.MainBranch, conflicted, DateTimeOffset.UtcNow);
        ParkedConflicts.Park(repoHandle, parked);

        _log?.Invoke(
            $"merge queue repo={repoHandle} agent={handoff.AgentId} keep-alive rebase CONFLICTED against "
            + $"'{handoff.MainBranch}' — worktree {handoff.WorktreePath} is parked mid-rebase and the jail "
            + $"stays paused until it is resolved (conflicting: {DescribePaths(conflicted)})");

        _audit.Append(new AuditEvent(KeepAliveConflictEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHandle,
            ["agent"] = handoff.AgentId,
            ["worktree"] = handoff.WorktreePath,
            ["main_branch"] = handoff.MainBranch,
            // The half of the handoff the event never carried. A resolver — human or T-04 — that is told
            // WHERE without being told WHAT has to re-derive it from a worktree that may since have moved.
            ["conflicted_paths"] = string.Join(" ", conflicted),
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));
    }

    /// <summary>
    /// The unmerged paths in a worktree, repo-relative, or an empty list when git could not be asked.
    ///
    /// <para>Empty means <b>not measured</b>, never "nothing conflicts": the caller is on the path where
    /// git has just refused a rebase, so a rendered "no files conflict" would be a fabricated reassurance
    /// over a real conflict. Every surface treats the empty list as unknown.</para>
    /// </summary>
    private static IReadOnlyList<string> MeasureConflictedPaths(string worktreePath)
    {
        if (string.IsNullOrEmpty(worktreePath) || !System.IO.Directory.Exists(worktreePath))
        {
            return Array.Empty<string>();
        }

        if (AgentGitCommand.TryRun(
                worktreePath, out var output, "diff", "--name-only", "--diff-filter=U") != 0)
        {
            return Array.Empty<string>();
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string DescribePaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "not measured" : string.Join(", ", paths);

    // ---- The two things a human can do about a parked conflict ---------------------------------------
    //
    // The cascade's conflict arm blocks the entry with a sentence naming a required human action
    // (RebaseConflictReason) and, until these existed, the surface offered no operation that could perform
    // it: the jail was paused, so `docker exec` answered "Container … is paused, unpause the container
    // before exec", and the card's only controls were Verify (which cannot run in a paused jail), Discard
    // (which throws the work away) and the verification log. A card that names an action the product does
    // not have is worse than one that names none — it reads as the recovery the human is looking for.
    //
    // Neither of these is the T-04 resolver, and neither pretends to be: T-04 is the staging/diff surface
    // where a HUMAN resolves hunks. These are the two operations that can be built from machinery that
    // already exists — hand it back to the agent that made it, or undo it — and they are deliberately the
    // only two.

    /// <summary>The audit event a hand-back appends: the jail was unpaused and the worker was told to
    /// finish its own rebase.</summary>
    public const string ConflictHandedBackEvent = "keepalive_conflict_handed_back";

    /// <summary>The audit event an abort appends: the parked rebase was undone and the branch restored.</summary>
    public const string ConflictRebaseAbortedEvent = "keepalive_conflict_rebase_aborted";

    /// <summary>What the entry says after its conflict has been handed back to the worker.</summary>
    public const string ConflictHandedBackReason =
        "the agent has been unpaused and asked to finish resolving this rebase itself — it verifies again "
        + "once the rebase completes";

    /// <summary>What the entry says after its parked rebase was aborted.</summary>
    public const string ConflictAbortedReason =
        "the conflicted rebase was aborted — this branch is back where it was, still behind the new main, "
        + "and needs verifying again";

    /// <summary>
    /// <b>"Let the agent resolve"</b> — unpause the parked jail and tell the worker to finish its own
    /// rebase.
    ///
    /// <para><b>Why this is a real answer and not a shrug.</b> The worker is a coding agent and the
    /// conflict is between its own commits and the human's; resolving it is inside its competence and
    /// nobody else has more context on the half it wrote. What it could not do is notice: it was frozen
    /// mid-rebase by the daemon, with no message explaining why, and unfreezing it without saying anything
    /// would have it carry on with whatever it was doing on top of a half-finished rebase. So the unpause
    /// and the instruction are one operation, and the instruction goes through the SAME prompt-delivery
    /// path a coordinator's steer uses rather than a second way to type at a worker.</para>
    ///
    /// <para><b>Order matters.</b> The jail is unpaused FIRST: a paused container's pty is frozen, so a
    /// prompt written to it would sit unread in a buffer — the exact "the prompt accumulated unsubmitted"
    /// shape this codebase has already paid for once.</para>
    ///
    /// <para>Nothing here decides the conflict, and nothing here touches the index: the branch is exactly
    /// as it was, mid-rebase, and the queue entry stays at <c>Working</c> with a reason that says what is
    /// now true instead of what was true a moment ago.</para>
    /// </summary>
    public async Task<ConflictActionResult> LetAgentResolveConflictAsync(
        string repoHandle, string agentId, CancellationToken ct = default)
    {
        if (!TryOpenParkedConflict(repoHandle, agentId, out var parked, out var queue, out var refusal))
        {
            return ConflictActionResult.Refused(refusal);
        }

        var containerId = _resolveContainerId(repoHandle, agentId);
        if (string.IsNullOrEmpty(containerId))
        {
            return ConflictActionResult.Refused(
                "this entry's sandbox is gone, so there is no agent to hand the conflict back to — resume "
                + "the entry to give it one, or abort the rebase");
        }

        if (_promptAgent is null)
        {
            // Deliberately refused rather than half-performed. Unpausing without telling the agent why it
            // woke up is how an agent resumes whatever it was doing on top of a half-finished rebase.
            return ConflictActionResult.Refused(
                "this daemon has no way to send the agent an instruction, so handing the conflict back "
                + "would wake it with no idea why — abort the rebase instead");
        }

        try
        {
            await _sandboxes.UnpauseAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Classified BY STATE, never by error-message substring (ISandboxEngine.IsPausedAsync' own
            // note): an engine that refuses because the jail is already running has given us the state we
            // wanted, and only an engine that still reports it paused has actually failed.
            if (await StillPausedAsync(containerId, ct).ConfigureAwait(false))
            {
                return ConflictActionResult.Refused($"the agent's jail could not be unpaused ({ex.Message})");
            }
        }

        // The jail is running again: clear the pause axis before anything is typed into it.
        MarkFrozen(repoHandle, agentId, null);
        MarkRunState(repoHandle, agentId, AgentRunState.Rebasing);
        try
        {
            _agentStates.ResumeInput(agentId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} resume-input FAILED ({ex.Message})");
        }

        var prompt = ResolveConflictPrompt(parked);
        bool delivered;
        try
        {
            delivered = await _promptAgent(repoHandle, agentId, prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            delivered = false;
            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} conflict hand-back prompt THREW ({ex.Message})");
        }

        if (!delivered)
        {
            // Half of the operation happened and the entry must say so: the sentence it is wearing — "the
            // agent is paused with the rebase in progress" — is no longer true, and leaving it would send
            // the next reader looking for a paused jail that is running.
            const string partial =
                "the agent's jail was unpaused but the instruction could not be delivered to its CLI — it "
                + "is awake with the rebase still in progress, so tell it yourself in its terminal or "
                + "abort the rebase";
            queue.TryReturnToWorking(agentId, partial, "conflict-handback-no-prompt");
            return ConflictActionResult.Refused(partial);
        }

        ParkedConflicts.Clear(repoHandle, agentId);
        queue.TryReturnToWorking(agentId, ConflictHandedBackReason, "conflict-handed-back");

        _log?.Invoke(
            $"merge queue repo={repoHandle} agent={agentId} conflict HANDED BACK — jail unpaused and the "
            + $"worker told to finish its rebase onto '{parked.MainBranch}'");
        _audit.Append(new AuditEvent(ConflictHandedBackEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHandle,
            ["agent"] = agentId,
            ["worktree"] = parked.WorktreePath,
            ["main_branch"] = parked.MainBranch,
            ["conflicted_paths"] = string.Join(" ", parked.ConflictedPaths),
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        return ConflictActionResult.Ok();
    }

    /// <summary>
    /// <b>"Abort rebase"</b> — <c>git rebase --abort</c> in the parked worktree, then let the jail run
    /// again. The branch returns to exactly where it was before the cascade touched it, and the entry
    /// returns to the queue needing verification against the new main.
    ///
    /// <para><b>Deterministic, and it never makes things worse.</b> An abort loses no committed work — the
    /// branch tip is restored to its pre-rebase value, including any <c>wip: sync</c> snapshot the cycle
    /// made — and it costs only the replay progress, which is why it is behind the same confirmation the
    /// queue's other irreversible action is. It is the honest answer for a conflict nobody wants to spend
    /// an agent's context on: the branch is unmergeable either way until it is rebased, and an aborted
    /// rebase is a state a person can reason about.</para>
    ///
    /// <para><b>The mutation goes through the P2-09 yield, not around it.</b> The jail is currently frozen
    /// by the parking, which is the state a yield token exists to produce — but a token is the only API
    /// that may gate a worktree mutation (invariant 2), and re-requesting a yield over an already-paused
    /// container would <c>docker pause</c> a paused jail and be refused by the engine. So the jail is
    /// unpaused first, an ordinary yield is taken over it, and the token's own resume is what leaves the
    /// jail running at the end. The extra round trip buys the invariant instead of an exception to it.</para>
    /// </summary>
    public async Task<ConflictActionResult> AbortParkedRebaseAsync(
        string repoHandle, string agentId, CancellationToken ct = default)
    {
        if (!TryOpenParkedConflict(repoHandle, agentId, out var parked, out var queue, out var refusal))
        {
            return ConflictActionResult.Refused(refusal);
        }

        if (_yieldFor is null)
        {
            return ConflictActionResult.Refused(
                "this daemon has no cooperative-yield gateway wired, and a worktree may not be mutated "
                + "without one");
        }

        var containerId = _resolveContainerId(repoHandle, agentId);
        if (string.IsNullOrEmpty(containerId))
        {
            return ConflictActionResult.Refused(
                "this entry's sandbox is gone, so the yield that gates every worktree mutation cannot be "
                + "taken — resume the entry first");
        }

        try
        {
            await _sandboxes.UnpauseAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (await StillPausedAsync(containerId, ct).ConfigureAwait(false))
            {
                return ConflictActionResult.Refused($"the agent's jail could not be unpaused ({ex.Message})");
            }
        }

        IYieldToken token;
        try
        {
            token = await _yieldFor(repoHandle).RequestYieldAsync(agentId, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConflictActionResult.Refused(
                $"the agent could not be quiesced for the abort ({ex.Message}) — nothing was changed");
        }

        try
        {
            // No `recheck` here, and that is not an oversight: GitMutationGuard's verdict refuses a
            // worktree that is mid-rebase, which is the precondition of this operation rather than a
            // hazard to it. What still applies is the index.lock backoff, which is the half that matters —
            // the jail was running for the few milliseconds above and its CLI may hold the lock.
            var exit = GitMutationGuard.RunGuarded(
                token,
                () => GitMutationGuard.IsIndexLockHeld(parked.WorktreePath),
                () => AgentGitCommand.TryRun(parked.WorktreePath, out _, "rebase", "--abort"));

            if (exit != 0)
            {
                return ConflictActionResult.Refused(
                    $"`git rebase --abort` failed in the parked worktree (exit {exit}) — the rebase is "
                    + "still in progress and nothing was changed");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConflictActionResult.Refused(
                $"the parked rebase could not be aborted ({ex.Message}) — nothing was changed");
        }
        finally
        {
            // Leaves the jail RUNNING, which is the point: the branch is no longer mid-anything, so the
            // reason to keep the agent frozen is gone with it.
            token.Resume();
        }

        ParkedConflicts.Clear(repoHandle, agentId);
        MarkFrozen(repoHandle, agentId, null);
        MarkRunState(repoHandle, agentId, AgentRunState.Working);
        queue.TryReturnToWorking(agentId, ConflictAbortedReason, "conflict-aborted");

        _log?.Invoke(
            $"merge queue repo={repoHandle} agent={agentId} parked rebase ABORTED — worktree "
            + $"{parked.WorktreePath} restored and the jail resumed");
        _audit.Append(new AuditEvent(ConflictRebaseAbortedEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHandle,
            ["agent"] = agentId,
            ["worktree"] = parked.WorktreePath,
            ["main_branch"] = parked.MainBranch,
            ["conflicted_paths"] = string.Join(" ", parked.ConflictedPaths),
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        return ConflictActionResult.Ok();
    }

    /// <summary>
    /// The preconditions both conflict actions share: this repo has a live queue, this entry is parked,
    /// and the rebase the parking is about is still in progress.
    ///
    /// <para>The last check is the one that keeps the parking honest. The record is memory and the
    /// worktree is on disk with an agent that has a shell in it: a worker (or a human, or a later cycle)
    /// can finish or abort the rebase without telling anyone, and acting on a stale record would run
    /// <c>rebase --abort</c> over whatever the worktree became. A parking whose rebase is gone is forgotten
    /// here rather than acted on.</para>
    /// </summary>
    private bool TryOpenParkedConflict(
        string repoHandle, string agentId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ParkedRebaseConflict? parked,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MergeQueue? queue,
        out string refusal)
    {
        queue = null;
        parked = ParkedConflicts.Find(repoHandle, agentId);
        if (parked is null)
        {
            refusal = "this entry has no rebase parked for a human — there is no conflict to act on";
            return false;
        }

        var ctx = _registry.Resolve(repoHandle);
        if (ctx is null)
        {
            refusal = $"no active merge queue for repo handle '{repoHandle}'";
            return false;
        }

        queue = ctx.Queue;

        if (!System.IO.Directory.Exists(parked.WorktreePath)
            || !GitMutationGuard.Inspect(parked.WorktreePath).RebaseInProgress)
        {
            ParkedConflicts.Clear(repoHandle, agentId);
            refusal =
                "the rebase this entry was parked on is no longer in progress — the worktree has already "
                + "moved on, so there is nothing here to resolve or abort";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>Whether the engine still reports the jail frozen. An engine that cannot answer says false,
    /// which reads as "not paused" — the direction that lets the operation proceed rather than refusing on
    /// ignorance.</summary>
    private async Task<bool> StillPausedAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _sandboxes.IsPausedAsync(containerId, ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// What the worker is told when its conflict is handed back. It states the four things it cannot see
    /// from inside a jail that was frozen without explanation: that it was paused and why, that it is
    /// awake now, which files git could not replay, and the two commands that end the rebase.
    ///
    /// <para>It asks the worker NOT to abort. An abort is the human's other button and it is recorded as
    /// such; a worker that quietly aborted would leave the queue's reason describing a hand-back that no
    /// longer describes anything.</para>
    /// </summary>
    internal static string ResolveConflictPrompt(ParkedRebaseConflict parked)
    {
        var files = parked.ConflictedPaths.Count == 0
            ? "Run `git status` to see which files are unmerged."
            : "The unmerged files are: " + string.Join(", ", parked.ConflictedPaths) + ".";

        return
            $"Mainguard paused you because rebasing your branch onto '{parked.MainBranch}' hit a conflict, "
            + "and a human has now asked you to resolve it yourself. You are unpaused. Your worktree is "
            + $"still mid-rebase. {files} Resolve each conflict, `git add` the files you fixed, then run "
            + "`git rebase --continue` until the rebase finishes. Do NOT run `git rebase --abort` — say so "
            + "instead if you cannot resolve it. Your branch is verified again automatically once the "
            + "rebase completes.";
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
        // Dev-only seeding (docs/design/queue-seeding.md §3): a registered seeded id skips the JAIL
        // half of this path — there is no agent repository to publish from and no sandbox to run in —
        // while the mirror-read half below (RT-D2 provenance, gate arming, flagged-change review)
        // runs for real against the seeded branch's committed tree. Every id a shipped daemon ever
        // sees resolves to null here and takes the path unchanged.
        var syntheticPlan = _synthetic?.TryGet(repoHandle, agentId);

        var containerId = syntheticPlan is null
            ? ResolveJailAndPublishForVerification(repoHandle, agentId)
            : null;

        // The descent belt, asked of git AFTER the publish above so it measures the tip about to be
        // verified. A run pins its record to the queue's main and asks nothing about ancestry, so a branch
        // that does not descend from that main passes and looks fresh — and only `--ff-only` finds out, at
        // merge time, forever. The cascade's re-entry already asks this before ITS re-verify; every other
        // caller (the human's Verify, the readiness trigger, a branch a human just un-parked) reached this
        // method without anyone asking. LOCAL entries only: an external pull-request head legitimately
        // sits behind main and lands by the host's merge commit, never by a fast-forward. Unreadable shas
        // answer true (the substrate-less doubles), so nothing is refused from ignorance.
        if (syntheticPlan is null
            && queue.GetOrigin(agentId) == MergeEntryOrigin.Local
            && MirrorKnowsCommit(repoHandle, queue.CurrentMainSha)
            && !BranchDescendsFromMain(repoHandle, agentId, queue.CurrentMainSha, out var tipBehindMain))
        {
            var report = NotOnTopOfMainReason(agentId, tipBehindMain, queue.CurrentMainSha);
            _log?.Invoke($"merge queue repo={repoHandle} agent={agentId} verification REFUSED — {report}");
            // InvalidOperationException, like the other pre-run refusals: the queue settles the entry to
            // Working with this sentence as its reason, and the RPC maps it to FAILED_PRECONDITION.
            throw new InvalidOperationException(report);
        }

        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);

        // The branch-side half of what this run is evidence FOR, resolved AFTER the publish above so it
        // names the tree the container is about to be measured on rather than whatever the mirror held
        // before. Every read below — the RT-D2 configs, the toolchain declaration, the flagged-change
        // diff — is taken from `agent/<id>` at this same instant, so one sha describes all of them.
        //
        // Empty is a possible answer (a mirror git could not answer for, and always the seeded path) and
        // it is passed through as empty rather than substituted: see VerificationRecord.BranchSha.
        var branchSha = RevParse(barePath, "agent/" + agentId);

        // Both sides of both RT-D2 comparisons, held as locals rather than passed inline: the gate's
        // acknowledgment record has to be able to say what changed FROM WHAT TO WHAT, and the only place
        // those two contents exist together is right here, read from the two trees at one instant.
        var branchVerifyConfig = ShowFile(barePath, "agent/" + agentId, VerificationConfigPath);
        var mainVerifyConfig = ShowFile(barePath, mainBranch, VerificationConfigPath);
        var branchToolchainConfig = ShowFile(barePath, "agent/" + agentId, ToolchainConfigPath);
        var mainToolchainConfig = ShowFile(barePath, mainBranch, ToolchainConfigPath);

        var resolution = VerificationCommandResolver.Resolve(
            branchConfigContent: branchVerifyConfig,
            mainConfigContent: mainVerifyConfig);

        // The same RT-D2 question asked of the TOOLCHAIN declaration. Note the argument order is
        // identical to the line above — branch vs main — but the resolver's answer is not symmetric:
        // what it hands back to provision is always main's, and the branch's copy only decides the flag.
        var toolchain = ToolchainDeclarationResolver.Resolve(
            branchConfigContent: branchToolchainConfig,
            mainConfigContent: mainToolchainConfig,
            repoHandle: repoHandle);

        // Arm (or clear) the RT-D2 gate BEFORE the run: a branch whose command drifted is unmergeable from
        // the moment we know, not from whenever a UI happens to look. The drift detail rides along so a
        // later acknowledgment can be recorded as the specific waiver it is.
        changedGate.SetFlagged(
            agentId, ChangedTestCommandGate.TestCommandItem, resolution.ChangedVsMain,
            new ChangedTestCommandGate.CommandDrift(
                VerificationConfigPath, mainVerifyConfig, branchVerifyConfig));
        changedGate.SetFlagged(
            agentId, ChangedTestCommandGate.ToolchainItem, toolchain.ChangedVsMain,
            new ChangedTestCommandGate.CommandDrift(
                ToolchainConfigPath, mainToolchainConfig, branchToolchainConfig));

        // ...and arm the P2-11 flagged-change gate from the same committed trees, at the same moment, for
        // the same reason: a branch that edits a CI workflow, a git hook, an executable config or a
        // security-sensitive path — or that reaches outside its approved scope — is unmergeable from the
        // instant the daemon can know it, not from whenever a UI happens to look at it.
        //
        // Verification time is the cadence, and what makes that honest is that a branch which pushes new
        // work RE-VERIFIES. That sentence used to stand here as a description of behaviour the daemon did
        // not have: nothing walked a locally-spawned agent's entry out of Verified for its own commits, so
        // a green branch that pushed again was never re-verified and this gate stayed armed against the
        // diff of two commits ago — the F6 out-of-scope classification, and every acknowledgment bound to
        // the old flagged-set hash, silently outliving the bytes they were computed from. That is now a
        // real edge (MergeQueue.NotifyBranchAdvanced walks Verified/AwaitingReview back to Working on a
        // tip move, and the readiness trigger re-fires from there), which is what re-arms this call
        // against the NEW diff. If that invalidation is ever removed, this comment becomes a lie again and
        // ArmFlaggedChangeReview_IsReArmedAgainstTheNewDiff_AfterAPush is the test that says so.
        ArmFlaggedChangeReview(repoHandle, agentId, flaggedGate);

        // The seeded arm ends here, AFTER every gate above was armed for real: the outcome is
        // supplied instead of executed, and the record it produces says so about itself
        // (SeededProvenanceMarker) — a value-supplied pass that claimed to be a container exit is
        // the forgery P2-10 exists to prevent, so the one synthetic fact is the one labeled fact.
        if (syntheticPlan is not null)
        {
            return await RunSyntheticVerificationAsync(
                repoHandle, agentId, queue, syntheticPlan, resolution, branchSha, ct).ConfigureAwait(false);
        }

        // ...and before running anything, confirm the jail REALLY carries what main declared. This is a
        // daemon-observed exec in the worker's own container, not a lookup in the daemon's own
        // bookkeeping: the failure being defended against is precisely the one where our records say the
        // layer was provisioned and the container is running something else. Without it, a jail missing
        // its toolchain produces exit 127 and an ordinary "verification failed" — indistinguishable from
        // the agent's code being broken, on the one screen where that distinction decides a merge.
        await EnsureToolchainPresentAsync(repoHandle, containerId!, toolchain.Provisioned, ct).ConfigureAwait(false);

        // Pin the record to BOTH shas it is only true between: the queue's authoritative main — the same
        // value CanMerge compares against, so a pass here is a pass against the main this branch will
        // actually merge into — and the branch tip resolved above, so a pass here is also a pass on the
        // tree that will actually merge.
        return await _runner.RunAsync(
            new VerificationRequest(
                agentId, containerId!, queue.CurrentMainSha,
                resolution.Command, resolution.ResolvedCommand, resolution.ConfigHash, branchSha),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The jail half's preflight for a REAL verification: resolve the agent's live sandbox (no jail ⇒
    /// refuse loudly — host execution is a rejection trigger, §3.2), re-publish the agent's branch
    /// from its own repository into the mirror (MG-3, design §7 "fetch trigger: both" — what is about
    /// to be verified must be the agent's current tip, not whatever the ref watcher last saw), and
    /// refuse with the measurement when the agent's worktree is not on the branch the daemon carries
    /// (a branch outside refs/heads/agent/&lt;id&gt; was previously ignored SILENTLY, byte-for-byte the
    /// same observation as an agent that has done nothing; deliberately NOT auto-recovered — see
    /// docs/design/agent-branch-confinement.md §4). Skipped entirely for a seeded id, which has no
    /// agent repository and no sandbox by construction.
    /// </summary>
    private string ResolveJailAndPublishForVerification(string repoHandle, string agentId)
    {
        var containerId = _resolveContainerId(repoHandle, agentId);
        if (string.IsNullOrEmpty(containerId))
        {
            throw new InvalidOperationException(
                $"Agent '{agentId}' has no live sandbox — verification runs in the worker's own jail, never on the host.");
        }

        _publishAgentRef?.Invoke(repoHandle, agentId);

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

        return containerId!;
    }

    /// <summary>
    /// The seeded arm of a verification (docs/design/queue-seeding.md §3–4): the plan's outcome in
    /// place of a sandboxed run, everything else real. The hold keeps the run GENUINELY in flight —
    /// the queue's in-flight set, the wire's <c>verification_in_flight</c> and
    /// <c>ClearStalledVerification</c>'s "wait" refusal all measure this await — and a cancellation
    /// (the clear path's drain, or the RPC's own token) surfaces out of the delegate for the queue's
    /// real failure path to settle; nothing here ever fabricates an outcome for a run that was
    /// interrupted. The artifact and the provenance marker are the record's own statement that no
    /// run happened.
    /// </summary>
    private async Task<VerificationRecord> RunSyntheticVerificationAsync(
        string repoHandle, string agentId, MergeQueue queue, SyntheticVerificationPlan plan,
        VerificationCommandResolver.Resolution resolution, string branchSha, CancellationToken ct)
    {
        if (plan.HoldSeconds > 0)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, plan.HoldCancellation.Token);
            await Task.Delay(TimeSpan.FromSeconds(plan.HoldSeconds), linked.Token).ConfigureAwait(false);
        }

        var when = DateTimeOffset.UtcNow;
        var artifactPath = WriteSyntheticArtifact(agentId, queue.CurrentMainSha, plan, resolution, when);
        _log?.Invoke(
            $"merge queue repo={repoHandle} agent={agentId} SEEDED verification settled "
            + $"(requested {(plan.Passed ? "PASS" : "FAIL")}; no run was executed)");

        // Pinned to the queue's authoritative main exactly as the real runner pins it, with the REAL
        // resolved provenance — plus the marker that keeps this record from ever claiming a run.
        return new VerificationRecord(
            agentId,
            queue.CurrentMainSha,
            plan.Passed,
            artifactPath,
            resolution.ResolvedCommand + SyntheticVerificationPlan.SeededProvenanceMarker,
            resolution.ConfigHash,
            when,
            // The seeded branch IS a real ref with a real tree — every gate above was armed from it — so
            // its verdict is pinned to a real tip like any other. Only the outcome is supplied, and only
            // the outcome is marked as supplied.
            branchSha);
    }

    // Mirrors VerificationRunner.WriteArtifact's shape so the log view renders familiarly; the body
    // is the honest statement of what did (not) happen. Best-effort like the real one.
    private string WriteSyntheticArtifact(
        string agentId, string mainSha, SyntheticVerificationPlan plan,
        VerificationCommandResolver.Resolution resolution, DateTimeOffset when)
    {
        var name = $"verify_{new string(agentId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())}"
            + $"_{when.UtcDateTime:yyyyMMddTHHmmssfff}_{mainSha}.log";
        var path = System.IO.Path.Combine(_artifactDir, name);
        var body =
            $"agent: {agentId}\n"
            + $"main@sha: {mainSha}\n"
            + $"resolved-command: {resolution.ResolvedCommand}{SyntheticVerificationPlan.SeededProvenanceMarker}\n"
            + $"config-hash: {resolution.ConfigHash}\n"
            + $"seeded: true — NO RUN WAS EXECUTED; requested outcome: {(plan.Passed ? "PASS" : "FAIL")}\n"
            + $"when-utc: {when.UtcDateTime:O}\n"
            + "---- stdout ----\n"
            + "(seeded verification — produced by the dev-only queue seeder; the daemon executed nothing. "
            + "See docs/design/queue-seeding.md.)\n";

        try
        {
            System.IO.Directory.CreateDirectory(_artifactDir);
            System.IO.File.WriteAllText(path, body);
        }
        catch (System.IO.IOException)
        {
            // Best-effort, same posture as VerificationRunner: losing the artifact must not lose the record.
        }

        return path;
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
        var approved = _resolveApprovedWork?.Invoke(agentId);
        var items = new List<FlaggedChange>(
            FlaggedChangeDetector.DetectFlagged(files, approved?.Plan, managed: approved is not null));

        // The APPROACH half of the same approval. The scope comparison above answers "did it touch files
        // nobody agreed to"; nothing answered "did it do what it said it would do", and a worker that owns
        // its own tests can turn any divergence green. These rows are the worker's own commit-time
        // declaration — and, when there is none, the fact that nobody answered. Only for a worker that
        // HOLDS an approval: with no approved approach there is nothing to deviate from, and a row saying
        // otherwise would be a ritual rather than a control.
        if (approved is not null)
        {
            items.AddRange(DeviationReview.ItemsFor(
                approved.Declaration, approved.Deviations, FlaggedChangeDetector.HashDiff(files)));
        }

        // P2-11 §3.6, and the second half of this method's job. The detector above classifies by PATH, so a
        // lockfile change reaches the human as "package-lock.json changed" and nothing more — which cannot
        // distinguish a patch bump from an added transitive carrying a postinstall script, the supply-chain
        // case the whole semantic diff exists for. Adding the rows HERE and not in the cockpit is decided by
        // two facts: the gate that blocks the merge is this store, and the semantic diff needs the WHOLE
        // manifest on both sides, which only the mirror has (a unified-diff hunk is truncated context — a
        // package-lock.json cannot be JSON-parsed out of one). See ReviewLockfiles.
        items.AddRange(ReviewLockfiles(repoHandle, agentId, files));

        flaggedGate.StoreFor(agentId).SetFlagged(items);

        if (items.Count > 0)
        {
            _log?.Invoke(
                $"merge queue repo={repoHandle} agent={agentId} flagged {items.Count} change(s) "
                + "requiring human acknowledgment before merge");
        }
    }

    /// <summary>
    /// The P2-11 §3.6 semantic lockfile review for every dependency manifest the branch's diff touches:
    /// which packages were added, removed or version-bumped, which of those carry a known advisory in the
    /// offline <see cref="OsvSnapshot"/>, and which declare install scripts.
    ///
    /// <para><b>Why it runs daemon-side, here, rather than in the cockpit.</b> Three reasons, and the first
    /// is decisive on its own:</para>
    /// <list type="number">
    ///   <item><b>This store is the gate.</b> <see cref="FlaggedChangeGate"/> reads
    ///   <see cref="AcknowledgmentStore"/>, and <c>ReviewCockpitContext.LockfileFlags</c> is consulted only
    ///   on the cockpit's LOCAL composition branch — the branch the shipped app never takes, because
    ///   production always supplies <c>live:</c>. Rows added there would render and block nothing; rows
    ///   added here block the merge and reach the cockpit through the projection the daemon already
    ///   streams.</item>
    ///   <item><b>Only the mirror has the inputs.</b> The semantic diff needs both manifests in full;
    ///   the client's projection is a <see cref="Mainguard.Git.Models.FilePatch"/> list, i.e. hunks with
    ///   truncated context. A <c>package-lock.json</c> is not parseable from a hunk — a client-side
    ///   implementation would silently classify a fragment and report the result as the whole file.</item>
    ///   <item><b>Acks bind to the set's content hash.</b> Items composed client-side would not be in the
    ///   hash the daemon's gate computed, so a human's acknowledgment would address an id the gate has
    ///   never heard of — the same "the checkmark cleared a store no merge consults" defect the live
    ///   flagged-item seam was built to end.</item>
    /// </list>
    ///
    /// <para><b>Cost.</b> Bounded before any parsing: only paths <see cref="LockfileReview.KindFor"/>
    /// recognises are read at all (two <c>git show</c>s each), and a manifest above
    /// <see cref="LockfileReview.MaxManifestBytes"/> is refused rather than parsed. <b>Measured</b>
    /// (<c>LockfileReviewCostTests</c>): a 5,000-package <c>package-lock.json</c>, 556 KB per side, parses
    /// both sides and diffs them in ~75 ms — against a verification that runs a full test suite in a
    /// container, which is what this method is already sitting inside. A branch touching no manifest pays
    /// nothing beyond the path check.</para>
    ///
    /// <para><b>Fail-closed, like the rest of this review.</b> Every way of not knowing — an unreadable
    /// blob, an oversize manifest, a missing or stale advisory snapshot — produces a
    /// <see cref="FlaggedKind.LockfileAdvisoryUnknown"/> must-acknowledge item, never an omission. An
    /// omitted item is an acknowledged item, so silence here would report "we could not check this for
    /// CVEs" as "this has no CVEs".</para>
    /// </summary>
    private IReadOnlyList<FlaggedChange> ReviewLockfiles(
        string repoHandle, string agentId, IReadOnlyList<Mainguard.Git.Models.FilePatch> files)
    {
        List<FlaggedChange>? items = null;
        string? barePath = null;
        string? mainBranch = null;

        foreach (var patch in files)
        {
            var path = FilePatchPath.NewPath(patch);
            if (LockfileReview.KindFor(path) is not { } kind)
            {
                continue;
            }

            // Resolved lazily: the overwhelmingly common branch touches no manifest at all, and this is on
            // the verification path of every agent in every repo.
            barePath ??= _repos.BareRepoPathFor(repoHandle);
            mainBranch ??= ResolveDefaultBranch(barePath);

            // The SAME two committed trees the RT-D2 provenance is read from, so the lockfile verdict and
            // the verification baseline cannot describe different bytes.
            var baseText = ShowFile(barePath, mainBranch, path);
            var branchText = ShowFile(barePath, "agent/" + agentId, path);

            // Neither side readable while the diff insists the file changed means git could not answer, not
            // that the manifest is empty. The distinction is the caller's to make and it is made here.
            var unreadable = baseText is null && branchText is null;
            if (unreadable)
            {
                _log?.Invoke(
                    $"merge queue repo={repoHandle} agent={agentId} lockfile '{path}' changed but neither "
                    + "tree yielded its contents — flagged as unreviewed");
            }

            items ??= new List<FlaggedChange>();
            items.AddRange(LockfileReview.Review(
                path, kind, baseText, branchText, _osv, DateTimeOffset.UtcNow, unreadable));
        }

        return (IReadOnlyList<FlaggedChange>?)items ?? Array.Empty<FlaggedChange>();
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

    /// <summary>
    /// Pulls the origin checkout's main forward into the bare mirror, called on a CONFIRMED human
    /// merge. Without it the mirror's main stays wherever the last provision-fetch left it, and the
    /// window between a merge and the next repo-open is a trap the E2E suite never walks (it verifies
    /// every agent before merging): a spawn in that window bases its worktree on the stale mirror
    /// main AND — worse — <see cref="EnsureQueue"/>'s reconcile trusts the mirror, so it walked the
    /// queue's authoritative main BACKWARDS to the pre-merge sha. Verifications then ran coherently
    /// against the old main, CanMerge said yes, and the client-side <c>--ff-only</c> refused every
    /// one of them with "main moved" — a permanently unmergeable Verified entry, observed live.
    ///
    /// <para>Forced single-refspec fetch (origin's main is authoritative the moment a human merge is
    /// confirmed — that is what "confirmed" means), narrow on purpose: agent refs stay mediated by
    /// MG-3, and tags/other branches are not this path's business. Failure is reported, not thrown —
    /// the merge has already landed; the mirror catches up at the next provision either way.</para>
    /// </summary>
    public bool TryRefreshMirrorMainAfterMerge(string repoHandle, out string reason)
    {
        var barePath = _repos.BareRepoPathFor(repoHandle);
        var mainBranch = ResolveDefaultBranch(barePath);
        if (string.IsNullOrEmpty(RevParse(barePath, mainBranch)))
        {
            reason = $"no mirror main at '{barePath}'";
            return false;
        }

        if (!TryGit(barePath, out var output,
                "fetch", "--no-tags", "origin", $"+refs/heads/{mainBranch}:refs/heads/{mainBranch}"))
        {
            reason = $"git fetch origin {mainBranch} failed: {output.Trim()}";
            return false;
        }

        reason = "";
        return true;
    }

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
