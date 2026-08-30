using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Where an agent's worktree lives and which branch it rebases onto.</summary>
/// <param name="WorktreePath">The agent's worktree (the mutation target).</param>
/// <param name="BarePath">The daemon-owned bare mirror the worktree is linked to.</param>
/// <param name="MainBranch">The mirror branch to rebase onto (the current, already-fetched main).</param>
public sealed record AgentWorktreeLocation(string WorktreePath, string BarePath, string MainBranch);

/// <summary>The T-04 handoff payload: the conflicted worktree the resolver runs against.</summary>
public sealed record ConflictHandoff(string AgentId, string WorktreePath, string MainBranch);

/// <summary>What one keep-alive cycle did.</summary>
public enum RebaseCycleKind
{
    /// <summary>Nothing to do: clean worktree, already on top of main.</summary>
    CleanNoop,

    /// <summary>Committed a wip snapshot and/or reparented the branch onto fresh main; agent resumed.</summary>
    Rebased,

    /// <summary>
    /// The cycle did NOT reparent the branch and did not mutate anything it could not finish: the guard
    /// refused (agent mid-rebase / detached / mid-merge), the mirror's main could not be carried across, or
    /// git refused the rebase without leaving one in progress. The agent is resumed and the next cycle
    /// retries.
    ///
    /// <para>The load-bearing part is the negative: after a <c>Skipped</c> the branch is <b>not</b> known to
    /// sit on top of main, so a caller must not treat it as re-verifiable. <see cref="CleanNoop"/> and
    /// <see cref="Rebased"/> are the only two kinds that carry that guarantee.</para>
    /// </summary>
    Skipped,

    /// <summary>The rebase conflicted: status <see cref="AgentRunState.Conflict"/>, worktree parked for T-04, PTY stays paused.</summary>
    Conflict,
}

/// <summary>The outcome of one keep-alive cycle.</summary>
public sealed record RebaseCycleResult(RebaseCycleKind Kind, string? Detail, bool WipCommitCreated)
{
    /// <summary>
    /// True only when this cycle ESTABLISHED that the branch now sits on top of the mirror's main — i.e.
    /// <c>git merge --ff-only agent/&lt;id&gt;</c> would be available to the human merge.
    ///
    /// <para>This predicate exists so no caller has to re-derive the rule from the enum, and so the rule is
    /// stated once, next to the kinds it reads. A <see cref="RebaseCycleKind.Skipped"/> or
    /// <see cref="RebaseCycleKind.Conflict"/> cycle left the parent exactly where it was; re-verifying on
    /// the strength of one produces a record pinned to the NEW main for a branch that does not descend from
    /// it — which is precisely the state whose merge is then refused, forever.</para>
    /// </summary>
    public bool BranchIsOnTopOfMain => Kind is RebaseCycleKind.Rebased or RebaseCycleKind.CleanNoop;
}

/// <summary>The keep-alive rebase driver seam (also P2-10's <c>NotifyMainMoved</c> entry point).</summary>
public interface IKeepAliveRebaser
{
    /// <summary>Runs one yield → guard → wip-commit → rebase-onto-main → resume cycle for an agent.</summary>
    Task<RebaseCycleResult> RunCycleAsync(string agentId, CancellationToken ct = default);

    /// <summary>P2-10 hook: main moved after a human merge — run a keep-alive cycle to reparent the agent.</summary>
    Task<RebaseCycleResult> NotifyMainMoved(string agentId, CancellationToken ct = default);
}

/// <summary>
/// P2-09 keep-alive rebase (contract §2.2). The single path by which a human's live edits reach an
/// agent worktree — <b>only via Git</b>, never file sync (invariant 1). One cycle:
/// <list type="number">
///   <item>Cooperatively yield the agent (the returned token is the sole mutation gateway).</item>
///   <item><see cref="GitMutationGuard.CanMutate"/> — skip the cycle if the agent is mid its own rebase / detached / mid-merge.</item>
///   <item>If the worktree is dirty: <c>git add -A</c> + <c>git commit -m "wip: sync"</c> (guarded against a transient lock).</item>
///   <item><c>git rebase &lt;main&gt;</c> onto the already-fetched mirror main.</item>
///   <item><b>K6/§23.6</b> — both mutations hand <see cref="GitMutationGuard.RunGuarded{T}"/> a re-read of
///     step 2's verdict, evaluated once the <c>index.lock</c> backoff clears and immediately before the
///     mutation. Step 2 is a snapshot, the backoff is up to ~1.5 s, and the three states it checks are
///     exactly the ones an agent enters while a lock is held; a refusal there ends the cycle as
///     <see cref="RebaseCycleKind.Skipped"/>, the same terminus step 2's own refusal has.</item>
///   <item>Conflict → status <see cref="AgentRunState.Conflict"/>, hand the worktree to the T-04 resolver, keep the PTY paused
///     (resume-after-resolve is a later hook). <b>No automatic <c>rebase --abort</c></b> (rejection trigger).</item>
///   <item>Success → resume the agent.</item>
/// </list>
/// This is not a second git runner: every git call routes through the shared audited
/// <see cref="AgentGitCommand"/> primitive.
///
/// <para><b>Kill-switch aware</b> (MG-39(b)): the cycle consults the shared <see cref="KillSwitchGate"/>
/// both before starting and before resuming, so a background rebase tick can never <c>docker unpause</c>
/// a jail the kill switch just froze.</para>
/// </summary>
public sealed class KeepAliveRebaser : IKeepAliveRebaser
{
    // Daemon-side identity for the wip snapshot / rebase replay, so the cycle never depends on the
    // worktree having a user identity configured.
    private static readonly string[] Identity =
    {
        "-c", "user.name=Mainguard Keep-Alive",
        "-c", "user.email=keepalive@mainguard.local",
    };

    /// <summary>The reason word a cycle refused because the kill switch holds everything frozen.</summary>
    internal const string KillSwitchSkipReason =
        "Kill switch engaged — the keep-alive cycle is refused while the queue is frozen.";

    private readonly IYieldProtocol _yield;
    private readonly Func<string, AgentWorktreeLocation> _locate;
    private readonly Action<string, AgentRunState> _setState;
    private readonly Action<ConflictHandoff> _onConflict;
    private readonly TimeSpan? _yieldTimeout;
    private readonly KillSwitchGate _killGate;

    /// <param name="yield">The cooperative-yield protocol (the mutation gateway).</param>
    /// <param name="locate">Resolves an agent id → its worktree/bare/main.</param>
    /// <param name="setState">Reflects the agent run state (Yielding/Rebasing/Conflict/Working).</param>
    /// <param name="onConflict">Routes a conflicted worktree to the T-04 resolver.</param>
    /// <param name="yieldTimeout">Overrides the yield window (tests pass a short one).</param>
    /// <param name="killGate">MG-39(b): the shared kill-switch freeze gate. A cycle refuses to start —
    /// and, if the kill fires mid-cycle, refuses to resume — while it is frozen. Defaults to a private,
    /// never-frozen gate so an un-wired caller behaves exactly as before.</param>
    public KeepAliveRebaser(
        IYieldProtocol yield,
        Func<string, AgentWorktreeLocation> locate,
        Action<string, AgentRunState>? setState = null,
        Action<ConflictHandoff>? onConflict = null,
        TimeSpan? yieldTimeout = null,
        KillSwitchGate? killGate = null)
    {
        _yield = yield ?? throw new ArgumentNullException(nameof(yield));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _setState = setState ?? ((_, _) => { });
        _onConflict = onConflict ?? (_ => { });
        _yieldTimeout = yieldTimeout;
        _killGate = killGate ?? new KillSwitchGate();
    }

    public Task<RebaseCycleResult> NotifyMainMoved(string agentId, CancellationToken ct = default) =>
        RunCycleAsync(agentId, ct);

    public async Task<RebaseCycleResult> RunCycleAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        // MG-39(b): the keep-alive cycle is a BACKGROUND mutator whose every non-conflict path ends in
        // token.Resume() → ISandboxEngine.UnpauseAsync. Left unaware of the freeze gate it would happily
        // start (or finish) during/after a kill and `docker unpause` the very jail the operator just
        // stopped — a timer silently undoing the emergency stop. Refuse to start while frozen: a Skipped
        // cycle is retried by the next tick once the operator resumes, and skipping only costs the agent
        // a staler main, whereas running costs containment.
        if (_killGate.IsFrozen)
        {
            return new RebaseCycleResult(RebaseCycleKind.Skipped, KillSwitchSkipReason, WipCommitCreated: false);
        }

        var loc = _locate(agentId);
        _setState(agentId, AgentRunState.Yielding);

        var token = await _yield.RequestYieldAsync(agentId, _yieldTimeout, ct).ConfigureAwait(false);
        var conflicted = false;
        try
        {
            var verdict = GitMutationGuard.CanMutate(GitMutationGuard.Inspect(loc.WorktreePath));
            if (!verdict.CanMutate)
            {
                // Guard skip: do not mutate. Resume the agent; the next cycle retries (edge row 2).
                _setState(agentId, AgentRunState.Working);
                return new RebaseCycleResult(RebaseCycleKind.Skipped, verdict.Reason, WipCommitCreated: false);
            }

            // MG-3: the worktree is linked off the agent's OWN repository now, which holds its own copy
            // of main taken when the agent was created. Before this change the worktree shared the
            // mirror's refs, so "the already-fetched mirror main" was current by construction; it no
            // longer is, and rebasing onto a stale main would silently make the keep-alive cycle a no-op
            // — the human's committed work would stop reaching the agent while every state transition
            // still looked healthy. So carry the mirror's main across first.
            //
            // ...and that is exactly why the fetch's exit code is now READ. It used to be discarded into
            // `out _`, which made this method's own warning come true: a failed fetch left the agent's
            // stale copy of main in place, `git rebase <main>` then found the branch already on top of it
            // and exited 0 without moving HEAD, and the cycle returned CleanNoop — "Clean; already on top
            // of main" — about a main from before the human's merge. Every state transition looked
            // healthy, the queue re-verified on the strength of it, and the merge refused. A cycle that
            // cannot establish what main IS must not claim the branch is on top of it.
            if (!TryRefreshMainFromMirror(loc, out var refreshFailure))
            {
                _setState(agentId, AgentRunState.Working);
                return new RebaseCycleResult(RebaseCycleKind.Skipped, refreshFailure, WipCommitCreated: false);
            }

            _setState(agentId, AgentRunState.Rebasing);

            // K6 — the verdict above is a SNAPSHOT, and both mutations below wait out an index.lock
            // backoff before they run. Handing the guard a re-read of the same three preconditions is what
            // makes the decision hold at the moment of action rather than at the moment of decision; the
            // refusal comes back typed and this cycle skips, exactly as the start-of-cycle refusal does.
            Func<MutationVerdict> recheck =
                () => GitMutationGuard.CanMutate(GitMutationGuard.Inspect(loc.WorktreePath));

            var wip = false;
            int rebaseExit;
            string headBefore;
            try
            {
                if (IsDirty(loc.WorktreePath))
                {
                    GitMutationGuard.RunGuarded(
                        token,
                        () => GitMutationGuard.IsIndexLockHeld(loc.WorktreePath),
                        () =>
                        {
                            AgentGitCommand.Run(loc.WorktreePath, "add", "-A");
                            AgentGitCommand.Run(loc.WorktreePath, Args("commit", "-m", "wip: sync"));
                            return 0;
                        },
                        recheck: recheck);
                    wip = true;
                }

                headBefore = HeadSha(loc.WorktreePath);
                rebaseExit = GitMutationGuard.RunGuarded(
                    token,
                    () => GitMutationGuard.IsIndexLockHeld(loc.WorktreePath),
                    () => AgentGitCommand.TryRun(loc.WorktreePath, out _, Args("rebase", loc.MainBranch)),
                    recheck: recheck);
            }
            catch (GitMutationStateChangedException ex)
            {
                // The agent started its own rebase (or detached, or opened a merge) while we waited for
                // its index.lock. Nothing was mutated. Same terminus as the start-of-cycle guard skip:
                // resume the agent and let the next cycle retry, with the measured reason rather than a
                // restatement of it. WipCommitCreated is reported honestly — the wip commit may already
                // have landed before the rebase leg refused.
                _setState(agentId, AgentRunState.Working);
                return new RebaseCycleResult(RebaseCycleKind.Skipped, ex.Message, wip);
            }

            if (rebaseExit != 0)
            {
                var state = GitMutationGuard.Inspect(loc.WorktreePath);
                if (state.RebaseInProgress)
                {
                    // A real conflict: park the worktree for T-04. Do NOT abort, do NOT resume (PTY stays paused).
                    conflicted = true;
                    _setState(agentId, AgentRunState.Conflict);
                    _onConflict(new ConflictHandoff(agentId, loc.WorktreePath, loc.MainBranch));
                    return new RebaseCycleResult(RebaseCycleKind.Conflict,
                        "Rebase onto main conflicted; routed to the T-04 resolver, agent paused until resolved.",
                        wip);
                }

                // A non-conflict rebase failure (nothing left mid-rebase): surface it and resume so the
                // agent isn't stuck. Reported as Skipped, not Rebased — nothing was reparented, and the
                // old kind said the opposite of what happened to the one caller that has to decide
                // whether the branch may now be re-verified.
                _setState(agentId, AgentRunState.Working);
                return new RebaseCycleResult(RebaseCycleKind.Skipped,
                    $"Rebase returned {rebaseExit} without leaving a rebase in progress; the branch was not reparented.",
                    wip);
            }

            _setState(agentId, AgentRunState.Working);
            var moved = wip || !string.Equals(headBefore, HeadSha(loc.WorktreePath), StringComparison.Ordinal);
            return new RebaseCycleResult(
                moved ? RebaseCycleKind.Rebased : RebaseCycleKind.CleanNoop,
                moved ? "Committed/reparented onto main." : "Clean; already on top of main.",
                wip);
        }
        finally
        {
            // Resume on every path except a live conflict (where the PTY must stay paused for the resolver)
            // — and except a kill that fired WHILE this cycle ran (MG-39(b)). The start-of-cycle gate check
            // cannot cover that race: the kill's docker pause and this cycle's docker unpause would then be
            // concurrent, and last-writer-wins could leave a killed jail running. Re-reading the gate here
            // makes the kill win by construction; the token is deliberately left un-resumed (the jail stays
            // paused, the state stays Paused) until the operator resumes the kill switch.
            if (!conflicted && !_killGate.IsFrozen)
            {
                token.Resume();
            }
        }
    }

    private static bool IsDirty(string worktreePath) =>
        AgentGitCommand.Run(worktreePath, "status", "--porcelain").Trim().Length > 0;

    /// <summary>
    /// MG-3 — fast-forwards the agent repository's copy of the main branch from the shared mirror, so
    /// <c>git rebase &lt;main&gt;</c> below really does rebase onto the main the human advanced.
    ///
    /// <para>The refspec is daemon-written and names exactly one branch. It is forced (<c>+</c>) because
    /// the agent owns this ref inside its own repo and may have moved it; the mirror is authoritative for
    /// what "main" means, and the agent's own work lives on <c>agent/&lt;id&gt;</c>, which this never
    /// touches. Fetching main can never be refused as "checked out": the worktree is on the agent
    /// branch.</para>
    ///
    /// <para><b>The exit code decides the cycle.</b> False means "we could not establish what main is",
    /// and the only honest response to that is to skip: the alternative — rebasing onto whatever copy of
    /// main the agent repo happens to hold — exits 0, moves nothing, and reports the branch as already on
    /// top of a main that has since moved. A skipped cycle costs the agent one round of staleness; a
    /// silently-stale one costs the human a branch that reports itself mergeable and is not.</para>
    ///
    /// <para>A location with no mirror or no main branch (the substrate-less test doubles) is not a
    /// failure: there is nothing to carry across, which is the pre-MG-3 shape, and the rebase below is
    /// then against whatever ref the caller named.</para>
    /// </summary>
    private static bool TryRefreshMainFromMirror(AgentWorktreeLocation loc, out string? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(loc.BarePath) || string.IsNullOrEmpty(loc.MainBranch))
        {
            return true;
        }

        var exit = AgentGitCommand.TryRun(
            loc.WorktreePath, out _, "fetch", "--no-tags", loc.BarePath,
            $"+refs/heads/{loc.MainBranch}:refs/heads/{loc.MainBranch}");
        if (exit == 0)
        {
            return true;
        }

        failure =
            $"Could not carry '{loc.MainBranch}' across from the mirror at '{loc.BarePath}' (git fetch exited "
            + $"{exit}); refusing to rebase onto a main we cannot establish is current.";
        return false;
    }

    private static string HeadSha(string worktreePath)
    {
        AgentGitCommand.TryRun(worktreePath, out var output, "rev-parse", "HEAD");
        return output.Trim();
    }

    private static string[] Args(params string[] gitArgs)
    {
        var all = new string[Identity.Length + gitArgs.Length];
        Array.Copy(Identity, all, Identity.Length);
        Array.Copy(gitArgs, 0, all, Identity.Length, gitArgs.Length);
        return all;
    }
}
