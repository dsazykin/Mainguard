using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
namespace Mainguard.Agents.Agents;

/// <summary>
/// P2-06 daemon service (no UI dependency). Manages per-agent worktrees: create
/// <c>agent/&lt;id&gt;</c> off the mirror's default branch, remove, and prune.
/// Every worktree is <b>quarantined</b> — its sole configured remote is a daemon-owned repository
/// (§3.4), so an agent's <c>git push</c> can only land there, never in the user's real remote and
/// never with credentials it does not have.
///
/// <para><b>MG-3.</b> That repository is no longer the shared mirror: each agent gets its OWN bare
/// repo (<see cref="AgentRepoManager"/>) borrowing the mirror's objects through
/// <c>objects/info/alternates</c>, and the worktree is linked off <i>that</i>. The mirror stops being
/// a surface the jail writes at all; the daemon carries the agent's branch across
/// (<see cref="PublishAgentBranch"/>), naming the source and destination refs itself.</para>
/// </summary>
public interface IAgentWorktreeManager
{
    /// <summary>Create the worktree for an agent (branch <c>agent/&lt;id&gt;</c> off the mirror's default branch). Returns its path.</summary>
    string CreateAgentWorktree(string repoHash, string agentId);

    /// <summary>
    /// <b>Resume</b> — give an agent id that ALREADY has an <c>agent/&lt;id&gt;</c> branch a fresh worktree
    /// standing on that branch, with its commits intact. Returns the worktree path.
    ///
    /// <para>This is the mirror image of <see cref="CreateAgentWorktree"/>, which refuses outright when the
    /// branch exists (a duplicate id) and creates the branch with <c>worktree add -b</c>. Adoption requires
    /// the branch to exist and checks it out <i>without</i> <c>-b</c>, so the work committed by the previous
    /// jail is what the new one starts from. An absent branch is
    /// <see cref="Mainguard.Git.Exceptions.AgentBranchMissingException"/> — never a fresh branch off the
    /// default, which would report success for an operation that recovered nothing.</para>
    ///
    /// <para><b>The default throws, deliberately.</b> A worktree manager with no substrate cannot adopt
    /// anything, and the one alternative — quietly delegating to <see cref="CreateAgentWorktree"/> — would
    /// answer "resumed" for a jail started on an empty branch. This repository's recurring defect is a
    /// control that looks applied and is not; a default that cannot do the job must say so.</para>
    /// </summary>
    string AdoptAgentWorktree(string repoHash, string agentId)
        => throw new Mainguard.Git.Exceptions.RepoProvisioningException(
            $"This worktree manager cannot adopt an existing branch, so agent '{agentId}' in repo "
            + $"'{repoHash}' cannot be resumed here.");

    /// <summary>
    /// Remove an agent's worktree; <paramref name="force"/> discards a dirty tree, otherwise a dirty tree
    /// is refused (typed).
    ///
    /// <para><b>The mirror's <c>agent/&lt;id&gt;</c> goes with it only when deleting it destroys
    /// nothing</b> — i.e. when the branch tip is already contained in the mirror's integration branch. A
    /// branch that carries a commit of its own survives the teardown, because this is the documented end
    /// of a worker's life (commit, report, stop) and the ref is the only name its commits have. The
    /// difference from <see cref="RemoveAgentWorktreeKeepingBranch"/> is therefore no longer "does it
    /// delete the branch" but "is it allowed to ask": a teardown may reap what is spent, a resume's
    /// rollback may not reap at all.</para>
    /// </summary>
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);

    /// <summary>
    /// Deletes <c>refs/heads/agent/&lt;id&gt;</c> from the mirror <b>because the thing it represented was
    /// withdrawn</b> — the external-PR intake's pull request closed upstream or was discarded by a human.
    /// Returns true when the mirror no longer carries the branch.
    ///
    /// <para><b>This is the one deletion taken on a caller's word.</b> <see cref="RemoveAgentWorktree"/>
    /// proves first that a delete costs nothing; this one is called where the commits provably live
    /// somewhere else (the pull request they were fetched from) and the entry has already left the queue,
    /// so keeping the ref would only make the next intake of that same <c>pr-&lt;n&gt;</c> collide with a
    /// branch nobody is coming back for. It is audited with the sha for that reason.</para>
    ///
    /// <para>The default returns false — a manager with no mirror deleted nothing and says so. Not a
    /// throw: a failure to tidy must never take an intake poll down, and residue here is residue, never
    /// lost work.</para>
    /// </summary>
    bool DiscardAgentBranch(string repoHash, string agentId) => false;

    /// <summary>
    /// Clears an agent's worktree + per-agent repository while <b>leaving
    /// <c>refs/heads/agent/&lt;id&gt;</c> in the mirror exactly where it is</b> — the rollback path for a
    /// resume whose jail failed to start.
    ///
    /// <para><see cref="RemoveAgentWorktree"/> ends with <c>branch -D</c>, which is correct for a teardown
    /// (the agent is finished; no residue) and catastrophic here: the branch is the only surviving copy of
    /// the work the resume was invoked to recover, so a failed resume that ran the ordinary cleanup would
    /// destroy exactly what it was asked to save.</para>
    ///
    /// <para><b>The default throws</b> rather than falling back to <see cref="RemoveAgentWorktree"/>: a
    /// manager that cannot preserve the branch must not silently delete it, and every caller of this method
    /// is on a best-effort cleanup path where leaving residue is strictly better than losing commits.</para>
    /// </summary>
    void RemoveAgentWorktreeKeepingBranch(string repoHash, string agentId)
        => throw new System.NotSupportedException(
            $"This worktree manager cannot remove agent '{agentId}''s worktree while preserving its "
            + "branch, and a resume's cleanup must never delete the branch it was resuming.");

    /// <summary>
    /// The teardown's last publish, with its <b>outcome</b> rather than a bool — so a refusal can be told
    /// apart from "nothing to publish". The default derives it from <see cref="PublishAgentBranch"/>: true
    /// is current, false is "the mirror lacks nothing", never a refusal, so a substrate-less manager can
    /// never keep a repository on a guess.
    /// </summary>
    AgentRefPublishOutcome PublishAgentBranchOutcome(string repoHash, string agentId)
        => PublishAgentBranch(repoHash, agentId)
            ? AgentRefPublishOutcome.Published
            : AgentRefPublishOutcome.NothingToPublish;

    /// <summary>
    /// Clears an agent's worktree while keeping <b>both</b> the mirror's <c>refs/heads/agent/&lt;id&gt;</c>
    /// and the agent's own repository on disk — the teardown path for an agent whose last publish the
    /// mediator <b>refused</b>. A refused non-fast-forward publish means the mirror holds the pre-rewrite
    /// tip and the agent's repository holds the only copy of the rewritten commits; the ordinary teardown
    /// deleted that repository on the comment's belief that every publish had copied its objects across,
    /// which is false for exactly the publish that was refused.
    ///
    /// <para><b>The default throws</b>, for the reason <see cref="RemoveAgentWorktreeKeepingBranch"/>'s
    /// does: a manager that cannot preserve the work must say so rather than fall back to the deleting
    /// removal.</para>
    /// </summary>
    void RemoveAgentWorktreeKeepingRepository(string repoHash, string agentId, string reason)
        => throw new System.NotSupportedException(
            $"This worktree manager cannot remove agent '{agentId}''s worktree while preserving its "
            + "repository, and a teardown after a refused publish must never delete the only copy of the work.");

    /// <summary>Prune stale worktree metadata.</summary>
    void Prune(string repoHash);

    /// <summary>List the repo's agent worktrees via the porcelain parser (drives the ListWorktrees RPC).</summary>
    IReadOnlyList<WorktreeItem> List(string repoHash);

    /// <summary>
    /// MG-3 — the per-agent repository whose git dir backs this agent's worktree, bind-mounted
    /// READ-WRITE into exactly one jail at its identical VM path. Empty when this implementation has
    /// no per-agent repo (the test doubles), which simply means the jail carries no such mount.
    /// </summary>
    string AgentRepoPathFor(string repoHash, string agentId) => string.Empty;

    /// <summary>
    /// MG-3 — carry <c>refs/heads/agent/&lt;id&gt;</c> from the agent's own repository into the shared
    /// mirror, which is where the merge queue reads it from. The daemon names both the source ref and
    /// the destination; the agent never proposes a ref update at all. Returns true when the mirror's
    /// ref is at the agent's tip afterwards.
    /// </summary>
    bool PublishAgentBranch(string repoHash, string agentId) => false;

    /// <summary>
    /// MG-3 / P2-09 — the same carry-across for a branch the <b>daemon itself</b> just rebased onto main.
    ///
    /// <para>Separate from <see cref="PublishAgentBranch"/> because a rebase is never a fast-forward, so
    /// the ordinary publish refuses it as rewritten history and the keep-alive's whole effect stops at
    /// the agent's own repository — invisible to the merge queue, the cockpit and the host's sync fetch.
    /// The rewrite is instead checked for LOST work by patch-id (see <c>AgentRefMediator.PublishRebase</c>);
    /// rules 1, 3 and 4 are unchanged.</para>
    /// </summary>
    bool PublishRebasedAgentBranch(string repoHash, string agentId) => false;

    /// <summary>
    /// MG-3 — start watching this agent's own <c>refs/heads/agent/&lt;id&gt;</c> and publish it into the
    /// mirror whenever it moves (design §7: the daemon watches AND re-fetches before verification).
    /// Called at spawn. Default no-op for the substrate-less test doubles.
    /// </summary>
    void WatchAgentRef(string repoHash, string agentId) { }

    /// <summary>MG-3 — stop watching (teardown). Default no-op.</summary>
    void UnwatchAgentRef(string repoHash, string agentId) { }

    /// <summary>
    /// Establishes which branch this agent's worktree is actually committing on, so that work committed
    /// somewhere other than <c>agent/&lt;id&gt;</c> is REPORTED rather than silently ignored.
    ///
    /// <para>The default is <see cref="AgentBranchAlignmentState.Unknown"/>, not "aligned": the test
    /// doubles that take this default have no worktree to read, and an implementation that cannot measure
    /// alignment must never be able to assert it. A caller that treats Unknown as a pass is choosing to;
    /// it cannot do so by accident.</para>
    /// </summary>
    AgentBranchAlignment CheckAgentBranch(string repoHash, string agentId)
        => new(AgentBranchAlignmentState.Unknown, AgentRepoLayout.BranchPrefix + agentId,
            Detail: "this worktree manager has no worktree to inspect");

    /// <summary>
    /// Records everything in an agent's worktree as one commit on <c>agent/&lt;id&gt;</c> — the step that
    /// makes a worker's work outlive its jail, and the only thing the verification trigger can observe.
    ///
    /// <para><b>Why the daemon does this rather than the agent's CLI.</b> Not for want of permission on
    /// the repository: <see cref="CreateAgentWorktree"/> exists precisely so <c>git commit</c> stays
    /// available to an agent. It is that the agent supplies a MESSAGE and nothing else — repository,
    /// worktree and branch are computed here, from the id the endpoint already proves. An agent cannot
    /// commit onto another branch, into another agent's tree, or with a pathspec of its choosing: the
    /// same structural argument that makes <see cref="AgentRefMediator"/> safe.</para>
    ///
    /// <para><b>The default is a refusal.</b> A manager with no substrate has no worktree to commit, and
    /// answering "committed" for a commit that did not happen is the failure this codebase keeps paying
    /// for — the caller would report success to the worker while the branch stayed empty.</para>
    /// </summary>
    /// <param name="message">The commit message — subject, blank line, body, exactly as git means it.
    /// It travels as one argv element through the audited arg-list git primitive, never through a shell,
    /// so newlines in it are ordinary characters. Judged by <see cref="AgentCommitMessage"/>: a message
    /// that cannot be recorded is REFUSED, never repaired into something the worker did not write.</param>
    AgentWorkCommitResult CommitAgentWork(string repoHash, string agentId, string? message)
        => new(AgentWorkCommitOutcome.Unsupported, AgentRepoLayout.BranchPrefix + agentId,
            Detail: "this worktree manager has no worktree to commit");
}

/// <summary>What one <see cref="IAgentWorktreeManager.CommitAgentWork"/> did, or why it refused.</summary>
public enum AgentWorkCommitOutcome
{
    /// <summary>A new commit exists on <c>agent/&lt;id&gt;</c>. The ref moved.</summary>
    Committed,

    /// <summary>The worktree was clean — nothing to record. Not an error, and deliberately distinct from
    /// <see cref="Committed"/>: the ref did NOT move, so nothing downstream observes anything, and a
    /// caller that reported this as a commit would be telling a worker its work is safe while the branch
    /// sits exactly where it was.</summary>
    NothingToCommit,

    /// <summary>HEAD is not <c>agent/&lt;id&gt;</c> (another branch, or detached). Refused: a commit made
    /// there is stranded where nothing — the mediator, the queue, the trigger — ever looks.</summary>
    RefusedBranch,

    /// <summary>There is no worktree for this agent (never created, or already torn down).</summary>
    NoWorktree,

    /// <summary>The message cannot be recorded as a commit message (G4). Refused rather than repaired:
    /// the alternative shipped for weeks and it flattened newlines to spaces, cut the result at 200
    /// characters mid-word, left <c>%b</c> empty, and reported success. See
    /// <see cref="AgentCommitMessage"/>.</summary>
    RefusedMessage,

    /// <summary>Git itself failed. Nothing is claimed about what did or did not land.</summary>
    Failed,

    /// <summary>This worktree manager cannot commit at all (the substrate-less test doubles).</summary>
    Unsupported,
}

/// <summary>The outcome of one agent-work commit, with the sha when there is one.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Branch">The branch the commit was aimed at — always <c>agent/&lt;id&gt;</c>, computed by
/// the daemon, echoed here so a refusal can name it.</param>
/// <param name="Sha">The new tip on success; null otherwise. Never a guess.</param>
/// <param name="Detail">Human-readable reason, for a refusal or a failure.</param>
public sealed record AgentWorkCommitResult(
    AgentWorkCommitOutcome Outcome, string Branch, string? Sha = null, string? Detail = null)
{
    /// <summary>True only when the branch actually moved — i.e. when there is something for the ref
    /// watcher to see.</summary>
    public bool Committed => Outcome == AgentWorkCommitOutcome.Committed;
}

/// <summary>
/// MG-3 — the one best-effort wrapper around <see cref="IAgentWorktreeManager.WatchAgentRef"/>, for the
/// paths that <b>take responsibility for a jail they did not spawn</b>.
///
/// <para><b>Why a shared helper rather than a call per site.</b> The spawn path
/// (<c>SandboxAgentLauncher.LaunchAsync</c>) is the only place a watch starts as part of creating the
/// agent; every OTHER way the daemon comes to own a live jail is an <i>adoption</i> — the boot
/// <c>SwarmReconciler</c> keeping a survivor, the external-PR intake finding its <c>pr-&lt;n&gt;</c>
/// container still up — and an adoption that skips this leaves the agent unwatched for the rest of its
/// life. Nothing errors when it does: the pre-verification publish (design §7's other half) still carries
/// the tip into the mirror whenever a verification happens to run, so the symptom is a review cockpit,
/// merge-queue projection and stale cascade sitting on a tip the agent already moved past. Each site
/// rolling its own <c>try</c> is how the second one got forgotten.</para>
///
/// <para><b>Best-effort by contract.</b> Both callers sit on paths where a throw is worse than a missed
/// watch — the boot sequence is fail-fast (an exception here is a daemon that will not start) and the
/// intake poll must survive one bad pull request. <c>Watch</c> is idempotent and keyed on
/// <c>(repoHash, agentId)</c>, so calling it again on a later pass costs nothing and one repository's
/// <c>pr-7</c> never stands in for another's.</para>
/// </summary>
public static class AgentRefWatchRegistration
{
    /// <summary>Registers <paramref name="agentId"/> in <paramref name="repoHash"/> with the MG-3 ref
    /// sweep, swallowing any failure. Returns true when the watch was accepted — for callers that want to
    /// log or assert it, never as something to branch the caller's own outcome on.</summary>
    public static bool TryWatch(IAgentWorktreeManager worktrees, string repoHash, string agentId)
    {
        try
        {
            worktrees.WatchAgentRef(repoHash, agentId);
            return true;
        }
        catch (Exception)
        {
            // The mirror still catches up at verification time; no caller may fail over the sweep.
            return false;
        }
    }
}

/// <inheritdoc cref="IAgentWorktreeManager"/>
public sealed class WorktreeManager : IAgentWorktreeManager
{
    private readonly string _vmRoot;
    private readonly Func<string, (int ExitCode, string Output)> _pnpmRunner;
    private readonly Action<string>? _warningSink;
    private readonly IAuditLog? _audit;
    private readonly Sandbox.PackageCacheManager? _packageCaches;
    private readonly AgentRepoManager _agentRepos;
    private readonly AgentRefMediator _refs;
    private readonly Lazy<AgentRefWatcher> _watcher;

    /// <param name="vmRoot">The VM base directory (shared with the provisioner). Injected for tests.</param>
    /// <param name="pnpmRunner">
    /// Runs <c>pnpm install</c> in a worktree. Injected so tests can assert the command was
    /// <i>issued</i> (or simulate a failure) without running real pnpm; defaults to a real spawn.
    /// This is the ONE process launch in <c>Mainguard.Agents/Agents</c> — all git goes through the
    /// shared <see cref="GitServices.RunGit"/> primitive.
    /// </param>
    /// <param name="warningSink">Receives non-fatal warnings (e.g. a failed pnpm install).</param>
    /// <param name="audit">
    /// MG-3 — G-17 sink for the one security-relevant event this type produces: a REFUSED publish
    /// (<see cref="AgentRefRefusedEvent"/>). An agent that rewrote history the mirror had already
    /// published, or an id that failed the layout gate, must leave a durable record rather than only a
    /// log line — the whole finding was a control that looked applied and was not.
    /// </param>
    /// <param name="packageCaches">
    /// MG-43 — the daemon-owned package cache, so a retired agent's cache is reclaimed on the ONE
    /// teardown path every caller already goes through (<see cref="RemoveAgentWorktree"/> — the swarm
    /// reconciler, the external-PR intake, the sync service and the launcher's own cleanup all land
    /// here). Without it a cache would only ever be reclaimed by budget eviction, which is the residue
    /// path, not the ordinary one. Null (every existing test double) simply keeps no cache.
    /// </param>
    public WorktreeManager(
        string? vmRoot = null,
        Func<string, (int ExitCode, string Output)>? pnpmRunner = null,
        Action<string>? warningSink = null,
        IAuditLog? audit = null,
        Sandbox.PackageCacheManager? packageCaches = null)
    {
        _vmRoot = vmRoot ?? DefaultVmRoot();
        _pnpmRunner = pnpmRunner ?? RealPnpmInstall;
        _warningSink = warningSink;
        _audit = audit;
        _packageCaches = packageCaches;
        _agentRepos = new AgentRepoManager(_vmRoot);
        _refs = new AgentRefMediator(_agentRepos, BareRepoPathFor, OnPublishOutcome);
        // The warning sink is what keeps an eviction from the sweep visible: an agent that silently stops
        // being watched still reaches the mirror at verification time, so nothing fails — the watcher
        // half of design §7 just quietly stops, which is exactly the shape of failure MG-3 exists to
        // avoid producing more of.
        _watcher = new Lazy<AgentRefWatcher>(
            () => new AgentRefWatcher(_refs, _agentRepos, interval: null, warningSink: _warningSink));
    }

    /// <summary>The G-17 audit type for a refused publish (MG-3).</summary>
    public const string AgentRefRefusedEvent = "agent_ref_refused";

    /// <summary>The G-17 audit type for a resume whose rescue publish found nothing to carry, recorded
    /// because the very next step deletes the repository it looked in.</summary>
    public const string AgentRescueEmptyEvent = "agent_rescue_empty";

    /// <summary>The G-17 audit type for a teardown that left <c>agent/&lt;id&gt;</c> standing because the
    /// branch carried commits the mirror's integration branch does not.</summary>
    public const string AgentBranchKeptEvent = "agent_branch_kept";

    /// <summary>G-17 sibling: the agent's own repository was kept through teardown because its last publish
    /// was refused, so the mirror does not hold its tip.</summary>
    public const string AgentRepoKeptEvent = "agent_repo_kept";

    /// <summary>The G-17 audit type for the one deletion that is taken on a caller's word rather than on a
    /// proof that it costs nothing — <see cref="IAgentWorktreeManager.DiscardAgentBranch"/>.</summary>
    public const string AgentBranchDiscardedEvent = "agent_branch_discarded";

    // A refusal is the interesting half: it means an agent rewrote history the mirror had already
    // published (or aimed at something that is not its own branch), and it must not pass silently just
    // because the caller wanted a bool.
    private void OnPublishOutcome(AgentRefPublishResult result)
    {
        if (!result.Refused)
        {
            return;
        }

        // The agent id is echoed RAW, never through AgentRepoLayout.RefFor: a RefusedTarget outcome is
        // precisely the case where the id failed that gate, and re-running it here would throw out of a
        // mediator whose entire contract is that it never does.
        _warningSink?.Invoke(
            $"MG-3: refused to publish agent '{result.AgentId}' into repo "
            + $"'{result.RepoHash}' — {result.Outcome}: {result.Reason}");

        _audit?.Append(new AuditEvent(AgentRefRefusedEvent, new Dictionary<string, string>
        {
            ["repo"] = result.RepoHash,
            ["agent"] = result.AgentId,
            ["outcome"] = result.Outcome.ToString(),
            ["old"] = result.OldSha ?? string.Empty,
            ["new"] = result.NewSha ?? string.Empty,
            ["reason"] = result.Reason ?? string.Empty,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));
    }

    /// <summary>MG-3 — the mediator this manager publishes through (the watcher shares it).</summary>
    public AgentRefMediator RefMediator => _refs;

    /// <summary>MG-3 — the per-agent repository store (the watcher needs it to snapshot refs).</summary>
    public AgentRepoManager AgentRepos => _agentRepos;

    /// <summary>
    /// MG-3 — the ref watcher, created on first use so a manager that never spawns an agent (every unit
    /// test, the merge-diff bridge) never starts a background loop at all. Its lifetime is the daemon's:
    /// <see cref="UnwatchAgentRef"/> removes an agent from the sweep, and there is nothing per-agent to
    /// dispose.
    /// </summary>
    public AgentRefWatcher RefWatcher => _watcher.Value;

    /// <summary>
    /// Installs the conflict hand-back's one exception to the mediator's fast-forward rule — see
    /// <see cref="AgentRefMediator.RewritePermitted"/>. The composition root wires it from the
    /// provisioner's parking store; nothing else may.
    /// </summary>
    public void PermitHandedBackRewrite(Func<string, string, bool> permitted, Action<string, string> consumed)
    {
        _refs.RewritePermitted = permitted ?? throw new ArgumentNullException(nameof(permitted));
        _refs.RewriteConsumed = consumed ?? throw new ArgumentNullException(nameof(consumed));
    }

    /// <summary>True once the hand-back exception is wired — the composition-root test's observable.</summary>
    public bool HasHandedBackRewritePolicy => _refs.RewritePermitted is not null;

    /// <inheritdoc />
    public void WatchAgentRef(string repoHash, string agentId) => RefWatcher.Watch(repoHash, agentId);

    /// <inheritdoc />
    public void UnwatchAgentRef(string repoHash, string agentId) => RefWatcher.Unwatch(repoHash, agentId);

    public string CreateAgentWorktree(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        if (!Directory.Exists(barePath))
        {
            throw new RepoProvisioningException($"No provisioned mirror for repo '{repoHash}'; provision it first.");
        }

        var branch = BranchFor(agentId);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var agentRepoPath = _agentRepos.PathFor(repoHash, agentId);

        // Refuse (typed) BEFORE any mutation if the branch or either path already exists (edge row 3):
        // leave no residue. The mirror still carries agent/<id> — publishing it there is what the merge
        // queue consumes — so it remains the authoritative "is this id taken?" question.
        if (BranchExists(barePath, branch))
        {
            throw new AgentWorktreeConflictException($"Branch '{branch}' already exists for repo '{repoHash}'.");
        }

        if (Directory.Exists(worktreePath))
        {
            throw new AgentWorktreeConflictException($"Worktree path already exists for agent '{agentId}'.");
        }

        if (_agentRepos.Exists(repoHash, agentId))
        {
            throw new AgentWorktreeConflictException($"A per-agent repository already exists for agent '{agentId}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // MG-3: the agent's own repository, borrowing the mirror's objects through alternates. The
        // worktree is linked off THIS, not off the mirror — which is what lets the mirror's mount become
        // read-only without taking `git commit` away from the agent.
        _agentRepos.Create(repoHash, agentId, barePath);
        try
        {
            var baseBranch = DefaultBranch(agentRepoPath);
            AgentGitCommand.Run(agentRepoPath, "worktree", "add", "-b", branch, worktreePath, baseBranch);
            FinishWorktreeLocked(repoHash, agentId, worktreePath, agentRepoPath);
        }
        catch
        {
            // Leave no residue: a half-made agent repo would make the next spawn of this id conflict.
            _agentRepos.Remove(repoHash, agentId);
            throw;
        }

        return worktreePath;
    }

    /// <inheritdoc />
    public string AdoptAgentWorktree(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        if (!Directory.Exists(barePath))
        {
            throw new RepoProvisioningException($"No provisioned mirror for repo '{repoHash}'; provision it first.");
        }

        var branch = BranchFor(agentId);
        var worktreePath = WorktreePathFor(repoHash, agentId);

        // (1) Rescue first. The previous jail's per-agent repository may still hold commits the mirror
        //     never saw — the ref watcher publishes on its own clock and the last-publish-on-teardown only
        //     runs for a clean stop, so a daemon crash or a VM halt leaves exactly this gap. That repo is
        //     deleted a few lines below and the mirror is what the adopted branch is cloned FROM, so this
        //     is the last moment those commits can be saved. Mediated (fast-forward only, never a delete),
        //     and best effort: an unreadable residue must not stop the resume, it must not silently lose
        //     work either — which is why it is attempted rather than skipped.
        //
        //     ...and "best effort" has to mean the OUTCOME IS READ. This call's result used to be dropped
        //     on the floor three lines before ClearWorktreeResidue deletes the only copy of those commits.
        //     OnPublishOutcome returns early unless result.Refused, so `Failed` and `NothingToPublish`
        //     reached nothing at all — and `Failed` is defined as "git itself failed (unreadable repo,
        //     races, disk)", which is exactly the transient case the paragraph above is about. A momentary
        //     error therefore discarded the agent's last commits with no log line, no audit event and no
        //     failed call.
        if (_agentRepos.Exists(repoHash, agentId))
        {
            var rescue = _refs.Publish(repoHash, agentId);
            switch (rescue.Outcome)
            {
                case AgentRefPublishOutcome.Failed:
                    // REFUSE the adoption. The residue below is the only copy, the failure is transient by
                    // definition, and a retried resume once the disk/race clears loses nothing — whereas
                    // proceeding is unrecoverable. Deliberately NOT extended to the Refused outcomes: a
                    // non-fast-forward is a permanent property of those commits, so refusing on it would
                    // strand the agent forever instead of once. Those are logged + audited by
                    // OnPublishOutcome, which the mediator has already invoked.
                    throw new AgentBranchRescueFailedException(repoHash, agentId, rescue.Reason);

                case AgentRefPublishOutcome.NothingToPublish:
                    // Not fatal — step (2) immediately asks the mirror whether the branch exists at all and
                    // refuses with AgentBranchMissingException when it does not. But it must not be
                    // SILENT: this is the daemon about to delete a repository it could not read a branch
                    // out of, and if that repository held work on some other branch, this line is the only
                    // trace that it existed.
                    _warningSink?.Invoke(
                        $"MG-3: nothing to rescue from agent '{agentId}' in repo '{repoHash}' before its "
                        + $"residue is cleared — {rescue.Reason}");
                    _audit?.Append(new AuditEvent(AgentRescueEmptyEvent, new Dictionary<string, string>
                    {
                        ["repo"] = repoHash,
                        ["agent"] = agentId,
                        ["reason"] = rescue.Reason ?? string.Empty,
                        ["when"] = DateTimeOffset.UtcNow.ToString(
                            "O", System.Globalization.CultureInfo.InvariantCulture),
                    }));
                    break;
            }
        }

        // (2) The branch IS the thing being resumed. Checked AFTER the rescue publish, so an agent whose
        //     branch reached the mirror only just now is still resumable — and refused outright when it is
        //     genuinely absent. Never a fresh branch off the default: see AgentBranchMissingException.
        if (!BranchExists(barePath, branch))
        {
            throw new AgentBranchMissingException(repoHash, agentId, branch);
        }

        // (3) Clear the dead jail's residue — the stale worktree and the per-agent repo — WITHOUT touching
        //     the mirror's branch. The package cache is deliberately kept: it belongs to this same agent,
        //     and re-downloading it is the cost RemoveAgentWorktree pays because there its agent is retired.
        ClearWorktreeResidue(repoHash, agentId, barePath);

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // (4) A fresh per-agent repository, cloned from the mirror — which carries refs/heads/agent/<id>
        //     at the tip we just confirmed, so the adopted branch arrives with its history.
        var agentRepoPath = _agentRepos.Create(repoHash, agentId, barePath);
        try
        {
            // No `-b`: check out the EXISTING branch. `-b` is what makes CreateAgentWorktree a creation,
            // and it is the one character between resuming the work and starting an empty branch under
            // the same name.
            AgentGitCommand.Run(agentRepoPath, "worktree", "add", worktreePath, branch);
            FinishWorktreeLocked(repoHash, agentId, worktreePath, agentRepoPath);
        }
        catch
        {
            // Same "no residue" rule as a create — and pointedly NOT RemoveAgentWorktree, whose last act
            // is `branch -D` on the mirror. The branch outlives every failure on this path.
            _agentRepos.Remove(repoHash, agentId);
            throw;
        }

        return worktreePath;
    }

    // The tail both entry points share: quarantine remote, dependency hook, jail-writable modes, the
    // in-jail branch guard, and the mirror publish. Factored out so a create and an adoption cannot drift
    // into producing differently-configured worktrees — the difference between them is meant to be
    // exactly which branch `worktree add` lands on.
    private void FinishWorktreeLocked(
        string repoHash, string agentId, string worktreePath, string agentRepoPath)
    {
        // Quarantine remote (§3.4 + MG-3): the worktree's remotes MUST be exactly
        // {origin -> the agent's OWN repo}. A linked worktree shares its main repository's config,
        // so this is also the agent repo's only remote. `git push origin` therefore succeeds
        // entirely inside the agent's writable space — LLM CLIs push reflexively and that has to
        // keep working — while the mirror is not a remote it can name at all.
        AgentGitCommand.TryRun(worktreePath, out _, "remote", "remove", "origin");
        AgentGitCommand.Run(worktreePath, "remote", "add", "origin", agentRepoPath);

        // pnpm hook (§3.3): only when a lockfile is present, and non-fatal — a failure surfaces
        // a warning but the worktree is still returned.
        MaybeRunPnpm(worktreePath, agentId);

        // MG-17: the jail that mounts this worktree read-write is host uid/gid 101000 (the userns
        // remap), not this process's uid 1000 — so a checkout laid down under the daemon's 022 umask
        // (0644 files, 0755 dirs) is one the agent can READ and never EDIT. Group-share it. This runs
        // LAST so it also covers whatever `pnpm install` just wrote.
        GroupShareRecursive(worktreePath);
        // …and again over the agent repo, whose `worktrees/<id>` metadata `worktree add` just created.
        GroupShareRecursive(agentRepoPath);

        // The in-jail guard rail (layer 2), installed AFTER both GroupShareRecursive passes so the
        // hook keeps its own narrower mode instead of being widened back to group-writable. It is
        // ergonomics, not a boundary — see AgentBranchGuard — and it is best effort: a spawn must
        // never fail because a guard rail could not be written.
        //
        // The return value is ARMED, not written, and it is deliberately not thrown on: a spawn whose
        // guard rail could not be armed still has to happen (layer 3 reports the drift either way), but
        // it must not happen QUIETLY. AgentBranchGuard warns through this same sink with the reason,
        // which is the whole difference between a control that is inert and a control that is inert and
        // says so — the guard was measured to be silently inert on a `noexec` agent repository.
        AgentBranchGuard.InstallHook(agentRepoPath, agentId, _warningSink);

        // Seed the merge queue's input contract: refs/heads/agent/<id> exists in the MIRROR from the
        // moment the agent does, pointing at the base commit. Everything downstream (the queue, the
        // diff bridge, the host repo's sync fetch) reads it there and is unaffected by where the
        // agent actually commits. On an adoption the mirror is already at the tip, so this is an
        // `Unchanged` no-op — asserted rather than assumed by the resume tests.
        PublishAgentBranch(repoHash, agentId);
    }

    /// <inheritdoc />
    public string AgentRepoPathFor(string repoHash, string agentId) => _agentRepos.PathFor(repoHash, agentId);

    /// <inheritdoc />
    public AgentBranchAlignment CheckAgentBranch(string repoHash, string agentId)
        => AgentBranchGuard.Probe(
            WorktreePathFor(repoHash, agentId), _agentRepos.PathFor(repoHash, agentId), agentId);

    /// <summary>
    /// Daemon-side identity for an agent's work commit. The agent id is IN the name, so a reader of the
    /// user's history can tell which agent produced a commit without consulting anything else, and the
    /// address is under <c>.invalid</c> (RFC 2606) so it can never be mistaken for a mailbox. Passed with
    /// <c>-c</c> rather than configured, so the commit never depends on the worktree having an identity —
    /// the same choice <see cref="Orchestrator.KeepAliveRebaser"/> made for the same reason.
    /// </summary>
    private static string[] IdentityFor(string agentId) => new[]
    {
        "-c", "user.name=Mainguard agent " + agentId,
        "-c", "user.email=" + agentId + "@agents.mainguard.invalid",
    };

    /// <inheritdoc />
    public AgentWorkCommitResult CommitAgentWork(string repoHash, string agentId, string? rawMessage)
    {
        var branch = BranchFor(agentId);

        // The message is judged FIRST, before any git runs. It is the one pure check here, it costs
        // nothing, and a refusal that arrives before `add -A` leaves the worktree exactly as the worker
        // left it — so a rewritten message is a retry rather than a recovery.
        var message = AgentCommitMessage.Normalize(rawMessage);
        if (AgentCommitMessage.Refuse(message) is { } messageRefusal)
        {
            return new AgentWorkCommitResult(
                AgentWorkCommitOutcome.RefusedMessage, branch, Detail: messageRefusal);
        }

        if (message.Length == 0)
        {
            message = AgentCommitMessage.DefaultFor(agentId);
        }

        var worktreePath = WorktreePathFor(repoHash, agentId);
        if (!Directory.Exists(worktreePath))
        {
            return new AgentWorkCommitResult(
                AgentWorkCommitOutcome.NoWorktree, branch,
                Detail: $"agent '{agentId}' has no worktree in repo '{repoHash}' — nothing to commit.");
        }

        // The alignment authority is the EXISTING one. A second opinion about which branch an agent is on
        // is how one of the two becomes decorative (MG-12), and this one already knows how to answer
        // "detached", "some other branch" and "could not tell" as three different things. `Unknown` is
        // refused with the rest: an unread state is not evidence of alignment, which is the whole reason
        // AgentBranchGuard has that member at all.
        var alignment = CheckAgentBranch(repoHash, agentId);
        if (alignment.State != AgentBranchAlignmentState.OnAgentBranch)
        {
            return new AgentWorkCommitResult(
                AgentWorkCommitOutcome.RefusedBranch, branch,
                Detail: $"this worktree's HEAD is not {branch} ({alignment.State}"
                    + (alignment.ActualBranch is { Length: > 0 } actual ? $": {actual}" : string.Empty)
                    + "). A commit made there would be reachable from no branch the merge queue reads.");
        }

        try
        {
            // `add -A` honours the agent repository's local info/exclude, which is where the daemon's own
            // droppings in /workspace are listed (the CLI's settings file and the operating-instructions
            // file). That is not incidental: this method is what would otherwise commit them, and the
            // exclusion and this call ship together for exactly that reason.
            AgentGitCommand.Run(worktreePath, "add", "-A");

            // Ask git whether there is anything staged BEFORE committing, so "nothing to commit" is an
            // outcome rather than a swallowed non-zero exit that also hides real failures.
            if (AgentGitCommand.TryRun(worktreePath, out _, "diff", "--cached", "--quiet") == 0)
            {
                return new AgentWorkCommitResult(
                    AgentWorkCommitOutcome.NothingToCommit, branch,
                    Sha: HeadShaOrNull(worktreePath),
                    Detail: "the worktree is clean — there is no change to record.");
            }

            var args = new List<string>(IdentityFor(agentId));

            // `--cleanup=verbatim` is explicit for two reasons. The default for `-m` is `whitespace`,
            // which COLLAPSES consecutive blank lines — so a two-paragraph body would come back with its
            // spacing quietly altered, which is a smaller version of the defect this change removes. And
            // the default is `commit.cleanup`-configurable, so leaving it implicit means the shape of an
            // agent's commit depends on a config key nobody set deliberately. The text was normalised
            // and judged before this point; git is asked to record it and nothing else.
            args.AddRange(new[] { "commit", "--cleanup=verbatim", "-m", message });
            AgentGitCommand.Run(worktreePath, args.ToArray());

            var sha = HeadShaOrNull(worktreePath);

            // NOT published here, and that is load-bearing rather than an omission. AgentRefWatcher's
            // sweep raises `Advanced` only for an outcome of `Published` — a publish that already happened
            // makes the sweep's own publish `Unchanged`, which is `Current` (so the snapshot is recorded)
            // and NOT `Published` (so no event is raised). Publishing eagerly here would therefore
            // silently disarm WorkerReadinessTrigger for the very commit it exists to react to. The
            // watcher carries it across within its tick, and the pre-verification re-fetch is the other
            // half; both were already there.
            return new AgentWorkCommitResult(AgentWorkCommitOutcome.Committed, branch, sha);
        }
        catch (RepoProvisioningException ex)
        {
            return new AgentWorkCommitResult(AgentWorkCommitOutcome.Failed, branch, Detail: ex.Message);
        }
    }

    private static string? HeadShaOrNull(string worktreePath) =>
        AgentGitCommand.TryRun(worktreePath, out var sha, "rev-parse", "HEAD") == 0
            ? sha.Trim() is { Length: > 0 } trimmed ? trimmed : null
            : null;

    /// <inheritdoc />
    public bool PublishAgentBranch(string repoHash, string agentId) => Publish(repoHash, agentId).Current;

    /// <inheritdoc />
    public bool PublishRebasedAgentBranch(string repoHash, string agentId)
        => _refs.PublishRebase(repoHash, agentId).Current;

    /// <summary>
    /// MG-3 stage 2 — the mediated publish, with the outcome rather than a bool. The four rules
    /// (destination is this agent's branch, fast-forward only, no deletes, never the integration
    /// branch) live in <see cref="AgentRefMediator"/>; this is the manager's entry point to them.
    /// </summary>
    public AgentRefPublishResult Publish(string repoHash, string agentId)
        => _refs.Publish(repoHash, agentId);

    /// <summary>
    /// MG-17 — grants the group <c>rwX</c> throughout a tree the daemon owns and a remapped jail must be
    /// able to write, and sets the setgid bit on its directories so anything created underneath inherits
    /// that group.
    ///
    /// <para>The GROUP itself is not set here and does not need to be: <c>~/mainguard/worktrees</c> is
    /// provisioned <c>2775 mainguard:mainguard-jail</c> at boot, so every directory created inside it
    /// already carries gid 101000 by inheritance. What inheritance cannot supply is the mode — umask is
    /// a property of the writing process, not of the parent directory — which is exactly what this fixes.
    /// <b>Anything MG-3 adds that a jail must write needs this same call (or
    /// <c>core.sharedRepository=group</c> for a git dir); anything it mounts read-only needs neither,
    /// because read+traverse already come from the group.</b></para>
    ///
    /// <para>Best effort and Unix-only: on Windows (the unit-test and dev-box path) there is no jail and
    /// no remap, and a failure to relax a mode must never fail a spawn — the failure it would cause is
    /// strictly worse than the one it is preventing.</para>
    /// </summary>
    internal static void GroupShareRecursive(string root)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            ShareOne(root, isDirectory: true);
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                ShareOne(dir, isDirectory: true);
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ShareOne(file, isDirectory: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A symlink loop, a racing removal, a filesystem with no Unix modes — never fail the spawn.
        }
    }

    private static void ShareOne(string path, bool isDirectory)
    {
        // Repeated (the caller already returned on Windows) so the platform analyzer can see the guard
        // on the call site itself rather than one frame up.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            // `g+rwX`: read/write always; execute only where execute already exists (or it is a
            // directory), so a data file never becomes executable.
            mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            if (isDirectory
                || (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                mode |= UnixFileMode.GroupExecute;
            }

            if (isDirectory)
            {
                // setgid: children inherit the shared group instead of the creator's primary group.
                mode |= UnixFileMode.SetGroup;
            }

            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Per-entry best effort (a dangling symlink is the common one).
        }
    }

    public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
    {
        var barePath = BareRepoPathFor(repoHash);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var branch = BranchFor(agentId);
        // The owner of the worktree metadata. A worktree made before MG-3 is still linked off the
        // mirror, so an upgraded daemon must be able to tear that one down too.
        var owner = _agentRepos.Exists(repoHash, agentId) ? _agentRepos.PathFor(repoHash, agentId) : barePath;

        if (Directory.Exists(worktreePath))
        {
            if (!force && IsDirty(worktreePath))
            {
                throw new AgentWorktreeConflictException(
                    $"Worktree for agent '{agentId}' has uncommitted changes; pass force to discard them.");
            }

            if (force)
            {
                AgentGitCommand.Run(owner, "worktree", "remove", "--force", worktreePath);
            }
            else
            {
                AgentGitCommand.Run(owner, "worktree", "remove", worktreePath);
            }
        }

        // Prune any dangling metadata. Always safe: metadata names no objects.
        AgentGitCommand.TryRun(owner, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");

        // ...and then delete agent/<id> ONLY when deleting it destroys nothing. See ReapBranch: this line
        // used to be unconditional, which made the documented end of a worker's life — commit, report,
        // stop — delete the commit it had just published and verified.
        ReapBranch(repoHash, agentId, branch, barePath);

        // MG-3: the agent's own repository goes with it. Its objects were COPIED into the mirror by every
        // publish that SUCCEEDED (a fetch across a local transport transfers objects; the mirror borrows
        // from nobody), so deleting it strands nothing the mirror's refs name. That sentence used to end
        // "can never strand a commit" and was false for the one publish the mediator refuses — a
        // non-fast-forward tip after an amend or a rebase — where this delete removed the only copy of the
        // rewritten commits. The teardown now reads the publish outcome and takes
        // RemoveAgentWorktreeKeepingRepository instead on a refusal; this line is reached only when the
        // mirror is current.
        _agentRepos.Remove(repoHash, agentId);

        // MG-43: and so does its package cache. The cache is derived, disposable content that only this
        // agent's jail ever mounted, so a retired agent has no claim on the gigabytes it holds — and
        // reclaiming here is what keeps budget eviction a residue path (a crashed daemon, an unclean
        // removal) rather than the routine one.
        _packageCaches?.Release(repoHash, agentId);

        // §4 gc policy: this is the natural idle point — if that was the last borrower, unreachable
        // objects in the mirror may finally be pruned.
        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
    }

    /// <summary>
    /// The teardown's last act: delete <c>agent/&lt;id&gt;</c> from the mirror <b>iff</b> the mediator says
    /// deleting it destroys nothing, and leave a durable record when it does not.
    ///
    /// <para><b>The boundary.</b> "No residue" exists because a mirror that accumulates a ref per agent
    /// forever is a mirror nothing can ever prune (MG-3 §4: unreachable objects are only deleted while no
    /// borrower is attached, and a live ref makes them reachable, not unreachable). That reason applies in
    /// full to a branch that never left the base commit — every coordinator, every failed spawn, every
    /// worker that did nothing — and it is those that the rule still reaps. It does not apply to a branch
    /// that carries a commit: there the ref is not residue, it is the only name for work, and
    /// <see cref="MirrorMaintenance.AfterAgentDetached"/> runs a prune two lines later.</para>
    ///
    /// <para><b>A kept branch is not silent.</b> An operator who stops an agent and finds a branch left
    /// behind must be able to see why, and a queue row that offers Review for a branch has to be able to
    /// trust the branch is there. The warning and the G-17 audit event are that record.</para>
    /// </summary>
    private void ReapBranch(string repoHash, string agentId, string branch, string barePath)
    {
        var verdict = _refs.MayReap(repoHash, agentId);
        if (verdict.MayDelete)
        {
            AgentGitCommand.TryRun(barePath, out _, "branch", "-D", branch);
            return;
        }

        _warningSink?.Invoke(
            $"MG-3: kept '{branch}' in repo '{repoHash}' through teardown — {verdict.Reason}. "
            + "The agent is gone; the branch stays so its commits do, and a human can still review, merge "
            + "or discard it.");

        _audit?.Append(new AuditEvent(AgentBranchKeptEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHash,
            ["agent"] = agentId,
            ["branch"] = branch,
            ["outcome"] = verdict.Outcome.ToString(),
            ["sha"] = verdict.Sha ?? string.Empty,
            ["reason"] = verdict.Reason ?? string.Empty,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));
    }

    /// <inheritdoc />
    public bool DiscardAgentBranch(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        var branch = BranchFor(agentId);
        if (!Directory.Exists(barePath))
        {
            return false;
        }

        var sha = AgentGitCommand.TryRun(barePath, out var head, "rev-parse", "--verify", "--quiet",
            "refs/heads/" + branch) == 0 ? head.Trim() : string.Empty;

        // Nothing there is success: the caller asked for the branch to be gone and it is.
        if (sha.Length == 0)
        {
            return true;
        }

        if (AgentGitCommand.TryRun(barePath, out _, "branch", "-D", branch) != 0)
        {
            return false;
        }

        // Audited, and this one is not optional. Every OTHER way a branch is deleted now proves first that
        // the delete costs nothing; this is the single path that deletes work on a caller's say-so, so the
        // say-so has to be on the record with the sha it destroyed.
        _audit?.Append(new AuditEvent(AgentBranchDiscardedEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHash,
            ["agent"] = agentId,
            ["branch"] = branch,
            ["sha"] = sha,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
        return true;
    }

    /// <inheritdoc />
    public AgentRefPublishOutcome PublishAgentBranchOutcome(string repoHash, string agentId)
        => Publish(repoHash, agentId).Outcome;

    /// <inheritdoc />
    public void RemoveAgentWorktreeKeepingRepository(string repoHash, string agentId, string reason)
    {
        var barePath = BareRepoPathFor(repoHash);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var owner = _agentRepos.Exists(repoHash, agentId) ? _agentRepos.PathFor(repoHash, agentId) : barePath;

        if (Directory.Exists(worktreePath))
        {
            // Forced, as every teardown removal is: the tree belongs to a jail that no longer exists.
            AgentGitCommand.TryRun(owner, out _, "worktree", "remove", "--force", worktreePath);
            if (Directory.Exists(worktreePath))
            {
                try { Directory.Delete(worktreePath, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        AgentGitCommand.TryRun(owner, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");

        // No reap, no repository delete, no cache release: the repository IS the work now. The mirror's
        // agent/<id> is left where it stands (the pre-refusal tip), so a queue row that names it still
        // names a real ref.
        var tip = AgentGitCommand.TryRun(owner, out var head, "rev-parse", "--verify", "--quiet",
            "refs/heads/" + BranchFor(agentId)) == 0 ? head.Trim() : string.Empty;
        _warningSink?.Invoke(
            $"MG-3: kept agent '{agentId}''s own repository in repo '{repoHash}' through teardown — {reason}. "
            + $"Its {BranchFor(agentId)} is at {tip} and the mirror does not hold it; a human can publish, "
            + "review or discard it from " + owner + ".");
        _audit?.Append(new AuditEvent(AgentRepoKeptEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHash,
            ["agent"] = agentId,
            ["branch"] = BranchFor(agentId),
            ["sha"] = tip,
            ["repository"] = owner,
            ["reason"] = reason,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
    }

    /// <inheritdoc />
    public void RemoveAgentWorktreeKeepingBranch(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        ClearWorktreeResidue(repoHash, agentId, barePath);

        // The package cache is deliberately NOT released. It belongs to this same agent, which is either
        // about to be resumed again or has just failed to be — and the cache is the one part of a jail
        // that is expensive to rebuild.
        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
    }

    /// <summary>
    /// Removes the dead jail's worktree and per-agent repository and <b>leaves the mirror's
    /// <c>agent/&lt;id&gt;</c> alone</b>. The single distinction from <see cref="RemoveAgentWorktree"/>'s
    /// tail, and the reason it is a separate method rather than a flag: the <c>branch -D</c> there is not
    /// an incidental line, it is what makes a teardown final.
    /// </summary>
    private void ClearWorktreeResidue(string repoHash, string agentId, string barePath)
    {
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var owner = _agentRepos.Exists(repoHash, agentId) ? _agentRepos.PathFor(repoHash, agentId) : barePath;

        if (Directory.Exists(worktreePath))
        {
            // Always forced: the tree belongs to a jail that no longer exists, and refusing on a dirty
            // checkout would make a resume impossible for exactly the agents that were interrupted
            // mid-edit. Uncommitted bytes are not recoverable through the branch and were never the
            // thing being resumed — the commits are (see AdoptAgentWorktree step 1).
            AgentGitCommand.TryRun(owner, out _, "worktree", "remove", "--force", worktreePath);
            if (Directory.Exists(worktreePath))
            {
                // git declined (metadata already pruned, a stale lock, an unmounted path). The DIRECTORY
                // is what `worktree add` collides with, so remove it directly. Best effort: one that will
                // not go away surfaces as git's own error on the next add, which names the real cause
                // better than anything this line could.
                try { Directory.Delete(worktreePath, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        AgentGitCommand.TryRun(owner, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");

        // No `branch -D`. This is the whole point of the method.
        _agentRepos.Remove(repoHash, agentId);
    }

    public void Prune(string repoHash)
    {
        var barePath = BareRepoPathFor(repoHash);
        AgentGitCommand.Run(barePath, "worktree", "prune");
        foreach (var agentId in _agentRepos.ListAgentIds(repoHash))
        {
            AgentGitCommand.TryRun(_agentRepos.PathFor(repoHash, agentId), out _, "worktree", "prune");
        }
    }

    public IReadOnlyList<WorktreeItem> List(string repoHash)
    {
        // MG-3: worktrees now hang off the per-agent repositories, so the listing is their union. The
        // mirror is still asked because a pre-MG-3 daemon left its worktrees there, and an upgrade must
        // not make them vanish from the RPC while they are still on disk.
        var items = new List<WorktreeItem>();
        foreach (var gitDir in EnumerateWorktreeOwners(repoHash))
        {
            if (AgentGitCommand.TryRun(gitDir, out var porcelain, "worktree", "list", "--porcelain") != 0)
            {
                continue;
            }

            foreach (var item in WorktreePorcelainParser.Parse(porcelain))
            {
                // Skip each repository's own bare stanza: it is a git dir, not an agent worktree, and
                // before MG-3 the mirror's stanza was the (single) "main" entry that got filtered the
                // same way by everything downstream.
                if (item.Branch is { Length: > 0 } branch
                    && branch.StartsWith(AgentRepoLayout.BranchPrefix, StringComparison.Ordinal))
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private IEnumerable<string> EnumerateWorktreeOwners(string repoHash)
    {
        yield return BareRepoPathFor(repoHash);
        foreach (var agentId in _agentRepos.ListAgentIds(repoHash))
        {
            yield return _agentRepos.PathFor(repoHash, agentId);
        }
    }

    /// <summary>The bare-mirror path for a hash (shared layout with the provisioner).</summary>
    public string BareRepoPathFor(string repoHash) => Path.Combine(_vmRoot, "repos", repoHash + ".git");

    /// <summary>The worktree path for an agent: <c>&lt;vmRoot&gt;/worktrees/&lt;hash&gt;/&lt;agentId&gt;</c>.</summary>
    public string WorktreePathFor(string repoHash, string agentId)
        => Path.Combine(_vmRoot, "worktrees", repoHash, agentId);

    private static string BranchFor(string agentId) => "agent/" + agentId;

    private void MaybeRunPnpm(string worktreePath, string agentId)
    {
        if (!File.Exists(Path.Combine(worktreePath, "pnpm-lock.yaml")))
        {
            return;
        }

        try
        {
            var (exitCode, output) = _pnpmRunner(worktreePath);
            if (exitCode != 0)
            {
                _warningSink?.Invoke(
                    $"pnpm install failed for agent '{agentId}' (exit {exitCode}); the worktree was still created. {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: agents can still run without dependencies installed.
            _warningSink?.Invoke($"pnpm install could not run for agent '{agentId}': {ex.Message}");
        }
    }

    private static bool BranchExists(string barePath, string branch)
        => AgentGitCommand.TryRun(barePath, out _, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch) == 0;

    private static bool IsDirty(string worktreePath)
        => AgentGitCommand.Run(worktreePath, "status", "--porcelain").Trim().Length > 0;

    private static string DefaultBranch(string barePath)
    {
        // The mirror's HEAD points at the source's default branch (main/master); base the worktree
        // off whatever that is rather than assuming a literal name.
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    private static (int ExitCode, string Output) RealPnpmInstall(string worktreePath)
    {
        // The single, injectable, real process launch in Mainguard.Agents/Agents. Git never spawns here.
        var psi = new ProcessStartInfo
        {
            FileName = "pnpm",
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("install");

        using var process = Process.Start(psi)
            ?? throw new RepoProvisioningException("Failed to launch pnpm.");
        // Drain BOTH pipes concurrently: pnpm writes progress to stderr, and reading stdout to end
        // first deadlocks once stderr fills its ~64KB pipe buffer (the audit-flagged wsl-runner bug
        // class) — which would hang worktree creation inside the daemon.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (process.ExitCode, combined);
    }

    // MainguardPaths.HomeDirectory(), not the old `?? "."` fallback: a relative VM root silently
    // resolving against the daemon's CWD is exactly the class of bug that crash-looped mainguardd.
    // An unresolvable home now fails loudly with the systemd remedy named.
    private static string DefaultVmRoot()
        => Path.Combine(MainguardPaths.HomeDirectory(), "mainguard");
}
