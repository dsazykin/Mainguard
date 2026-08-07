using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The outcome of a resume. A refusal is an ordinary result carrying its reason, not an exception: every
/// way a resume can decline is a state of the world the human has to read (the branch is gone, the entry
/// already has an agent, a merge is in flight), and each deserves its own sentence rather than a status
/// code.
/// </summary>
/// <param name="Resumed">True when the entry now has a live jail standing on its own branch.</param>
/// <param name="Reason">Render-verbatim refusal; empty on success.</param>
/// <param name="AgentId">The entry that was resumed — the SAME id, never a new one.</param>
/// <param name="Branch">The branch the new jail's worktree stands on (<c>agent/&lt;id&gt;</c>).</param>
/// <param name="State">The entry's merge state after the resume.</param>
/// <param name="ClearedStalledVerification">Whether the resume also had to clear a <c>Verifying</c> state
/// with no run behind it — a fact the surface states rather than hides, because the entry's state changed
/// as a side effect of an action the human asked for a different reason.</param>
public sealed record AgentResumeResult(
    bool Resumed, string Reason, string AgentId, string Branch, string State,
    bool ClearedStalledVerification = false);

/// <summary>
/// <b>Resume a stranded merge-queue entry</b> — give an entry whose jail is gone a live one again,
/// standing on its own <c>agent/&lt;id&gt;</c> branch with its commits intact, so it can be verified and
/// merged instead of only discarded.
///
/// <para><b>Adoption, not re-creation.</b> The entry keeps its identity: same agent id, same branch, same
/// persisted row, same origin badge, same verification history. A resume that minted a fresh id would
/// leave the human with two entries for one piece of work and no way to say which is which — and the
/// stranded one would still be stranded. Nothing here creates a queue entry; <c>EnsureEntry</c> on the
/// spawn path finds the existing one and only re-stamps its origin, which is why the origin is read off
/// the queue and handed back rather than defaulted (a default <c>Local</c> would silently un-badge an
/// intake'd external pull request).</para>
///
/// <para><b>Why it is a daemon service and not a ViewModel command.</b> Every question a resume has to
/// answer is only answerable here: whether the entry exists in THIS repo's queue, whether a verification
/// is genuinely running, whether a merge lease is open, whether the branch still exists in the mirror, and
/// whether the id already has a session. A client that "just spawned with the same id" would be asserting
/// all five. This repository's signature defect is a control that lives only in the UI layer.</para>
///
/// <para><b>Why it is human-only.</b> Adoption is the ability to attach a jail to an agent id someone else
/// owns. An agent that could invoke it could take over another agent's branch — strictly worse than the
/// merge power contract §4 already denies it, since it subsumes the ability to write that branch and then
/// have the daemon verify the result. The RPC in front of this service is on <c>RoleInterceptor</c>'s
/// coordinator denial list, and the in-jail IPC shim's op set does not (and must not) grow a resume:
/// <see cref="AgentSpawnService.HandleShimRequestAsync"/> never passes an agent id at all.</para>
/// </summary>
public sealed class AgentResumeService
{
    /// <summary>The audit event a resume appends: who resumed what, when, and out of which state.</summary>
    public const string ResumedEvent = "queue_entry_resumed";

    private readonly AgentSessionStore _store;
    private readonly AgentSpawnService _spawns;
    private readonly IMergeQueueRegistry _queues;
    private readonly IAuditLog _audit;
    private readonly ILogger _log;

    public AgentResumeService(
        AgentSessionStore store,
        AgentSpawnService spawns,
        IMergeQueueRegistry queues,
        IAuditLog audit,
        ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _log = loggerFactory.CreateLogger(DaemonLogCategories.Spawn);
    }

    /// <summary>
    /// Resumes <paramref name="agentId"/> in <paramref name="repoHandle"/>.
    ///
    /// <para>Everything is keyed on the <b>pair</b>. Agent ids are unique per repo and not globally — the
    /// external-PR intake names its entries <c>pr-&lt;n&gt;</c>, so two subscribed repositories both hold a
    /// <c>pr-7</c>, and the same collision has been fixed four separate times in this codebase. The queue
    /// is resolved by handle, the session is looked up by <see cref="AgentSessionKey"/>, and the mirror the
    /// branch is read from is that repo's. Nothing here takes an id on its own.</para>
    /// </summary>
    /// <param name="resumedBy">Daemon-derived actor (never client-supplied — see
    /// <see cref="Auth.IApproverIdentityResolver"/> and its documented limits).</param>
    /// <param name="agentKind">The CLI to run in the new jail. The queue entry does not record which CLI
    /// produced the branch (<c>MergeQueueRow</c> holds no kind), so this is the human's choice at resume
    /// time, exactly as it is at spawn time — stated rather than guessed.</param>
    public async Task<AgentResumeResult> ResumeAsync(
        string repoHandle, string agentId, string resumedBy, string agentKind, CancellationToken ct,
        string? modelApiKey = null,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? cliCredentials = null)
    {
        if (string.IsNullOrWhiteSpace(repoHandle) || string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("repo_handle and agent_id are required.");
        }

        var branch = AgentRepoLayout.BranchPrefix + agentId;

        // (1) The entry must be one THIS repo's queue actually tracks. Resolve → the repo has a live queue
        //     at all; the membership check → the id is one of its entries. GetState defaults an unknown
        //     agent to Working, so asking it first would invent an entry to resume.
        var ctx = _queues.Resolve(repoHandle);
        if (ctx is null)
        {
            return Refuse(agentId, branch, "", "there is no active merge queue for this repository");
        }

        var queue = ctx.Queue;
        if (!Contains(queue.Agents, agentId))
        {
            // A discarded entry is deliberately absent from Agents (it left the live queue) but is still
            // tracked, so it gets its own sentence rather than "not in the queue" — the human dropped it
            // on purpose and undoing that is a different decision from resuming.
            return Contains(queue.DiscardedAgents, agentId)
                ? Refuse(agentId, branch, WorkerMergeState.Discarded.ToString(),
                    "this entry was discarded — a discarded entry is not resumed, it is finished")
                : Refuse(agentId, branch, "", "this entry is not in this repository's merge queue");
        }

        var state = queue.GetState(agentId);
        if (state is WorkerMergeState.Merged or WorkerMergeState.Rejected)
        {
            return Refuse(agentId, branch, state.ToString(),
                $"this entry is already {state} — a terminal entry has nothing left to resume");
        }

        // (2) A live session for this (repo, id) means there is nothing stranded. Repo-scoped Find, not the
        //     daemon-global one: another repository's `pr-7` running is not this one's.
        if (_store.Find(new AgentSessionKey(repoHandle, agentId)) is not null)
        {
            return Refuse(agentId, branch, state.ToString(),
                "this entry already has a live agent — stop it before resuming");
        }

        // (3) Never inside the BeginMerge → ConfirmMerge window. The same reason a discard is refused
        //     there: a merge is already executing against this branch on the user's checkout, and putting
        //     a writable jail on it mid-merge is how the queue ends up disagreeing with git.
        var lease = ctx.Leases.GetOutstanding(repoHandle);
        if (lease is not null && string.Equals(lease.AgentId, agentId, StringComparison.Ordinal))
        {
            return Refuse(agentId, branch, state.ToString(),
                "a merge is in progress for this entry — finish or abandon it before resuming");
        }

        // (4) A verification that is REALLY running has a jail, so this entry is not stranded. Refused
        //     rather than raced: the honest answer is "wait".
        //
        //     The wording is deliberately NOT the clear's ("wait for it to finish"). An in-flight run
        //     implies the state is Verifying, so step 5's clear would refuse this case too — with a
        //     sentence about the wrong operation, and with a test asserting on it unable to tell the two
        //     guards apart. Saying "nothing to resume" is what makes this refusal this refusal.
        if (queue.IsVerificationInFlight(agentId))
        {
            return Refuse(agentId, branch, state.ToString(),
                "a verification is running for this entry right now — it has a live sandbox, so there is "
                + "nothing to resume; wait for the run to finish");
        }

        // (5) A `Verifying` row with no run behind it is a claim about a jail that no longer exists. Walk it
        //     back to Working through the SAME method the standalone clear uses, so there is exactly one
        //     implementation of that transition and it cannot drift. Evidence is NOT discarded: the
        //     immutable VerificationRecords stay in the verification store, and a passing record that is
        //     still fresh against main keeps its meaning — this only retracts the assertion that something
        //     is currently running.
        var clearedStalled = false;
        if (state == WorkerMergeState.Verifying)
        {
            clearedStalled = queue.TryClearStalledVerification(agentId, resumedBy, out var clearRefusal);
            if (!clearedStalled)
            {
                return Refuse(agentId, branch, queue.GetState(agentId).ToString(), clearRefusal);
            }
        }

        // (6) The origin is read off the entry and handed back to the spawn. EnsureEntry overwrites the
        //     origin on every call, so letting it default would silently re-badge an intake'd external pull
        //     request as Local — and the origin is what routes its merge back through the host's
        //     pull-request API instead of fast-forwarding the mirrored branch behind the PR's back.
        var origin = queue.GetOrigin(agentId);

        _log.LogInformation(
            "resume: repo={Repo} agent={Agent} from={State} origin={Origin} kind={Kind} by={By}",
            repoHandle, agentId, state, origin, agentKind, resumedBy);

        string spawnedId;
        try
        {
            spawnedId = await _spawns.SpawnAsync(
                repoHandle, agentKind, modelApiKey, role: string.Empty, ct,
                extraEnv: extraEnv,
                cliCredentials: cliCredentials,
                parentAgentId: null,
                agentId: agentId,
                queueOrigin: origin,
                adoptExistingBranch: true).ConfigureAwait(false);
        }
        catch (AgentBranchMissingException ex)
        {
            // The third honest answer: the branch is gone too, so there is no work to resume. A resume
            // that half-works — a fresh empty branch under the old name, reported as success — is worse
            // than this refusal, because the human would have no way to notice.
            _log.LogWarning("resume refused repo={Repo} agent={Agent}: {Reason}", repoHandle, agentId, ex.Message);
            // BREAK 2 (throwaway branch, never to be merged): report SUCCESS for a resume that recovered
            // nothing, instead of refusing and naming the missing branch.
            return new AgentResumeResult(
                true, "", agentId, branch, queue.GetState(agentId).ToString(), clearedStalled);
        }

        // (7) A resume whose whole point is a live jail must not report success for a session-only record.
        //     The spawn degrades to session-only for an unprovisioned repo; that cannot be reached from
        //     here (step 1 needs a live queue, which needs a mirror) but "cannot be reached" is exactly the
        //     assumption this codebase keeps finding to be false, so it is measured instead.
        var session = _store.Find(new AgentSessionKey(repoHandle, spawnedId));
        if (session?.ContainerId is not { Length: > 0 })
        {
            _log.LogWarning(
                "resume produced no sandbox repo={Repo} agent={Agent} — rolling the session back", repoHandle, agentId);
            await _spawns.StopAsync(new AgentSessionKey(repoHandle, spawnedId), ct).ConfigureAwait(false);
            // No "nothing was changed" here: a retracted stale verification IS a change, and the refusal
            // helper appends it. A sentence that claimed otherwise would be wrong exactly when it matters.
            return Refuse(agentId, branch, queue.GetState(agentId).ToString(),
                "the resume produced no sandbox, so this entry still cannot be verified",
                clearedStalled);
        }

        var finalState = queue.GetState(agentId);
        _audit.Append(new AuditEvent(ResumedEvent, new Dictionary<string, string>
        {
            ["repo"] = repoHandle,
            ["agent"] = agentId,
            ["by"] = string.IsNullOrWhiteSpace(resumedBy) ? "unknown" : resumedBy,
            ["from_state"] = state.ToString(),
            ["to_state"] = finalState.ToString(),
            ["branch"] = branch,
            ["agent_kind"] = agentKind ?? string.Empty,
            ["cleared_stalled_verification"] = clearedStalled ? "true" : "false",
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        _log.LogInformation(
            "resume complete: repo={Repo} agent={Agent} branch={Branch} container={Container} state={State}",
            repoHandle, agentId, branch, session.ContainerId, finalState);

        return new AgentResumeResult(true, "", agentId, branch, finalState.ToString(), clearedStalled);
    }

    /// <summary>
    /// A refusal, with one rule: if the resume already retracted a stale <c>Verifying</c> claim before
    /// failing, the refusal <b>says so</b>.
    ///
    /// <para>The retraction is correct on its own — the entry was reporting a run that did not exist — and
    /// re-asserting it would put the lie back. But a refusal reads as "nothing happened", so a state change
    /// left unmentioned inside one is exactly the silent side effect this codebase keeps finding. The
    /// client turns a refusal into a warning carrying this text verbatim, so the sentence is where the
    /// human learns it.</para>
    /// </summary>
    private AgentResumeResult Refuse(
        string agentId, string branch, string state, string reason, bool clearedStalled = false)
        => new(
            false,
            clearedStalled
                ? reason + " (its stalled verification was cleared even so, so it can be verified again "
                    + "once it has a sandbox)"
                : reason,
            agentId, branch, state, clearedStalled);

    private static bool Contains(IReadOnlyList<string> ids, string agentId)
    {
        foreach (var id in ids)
        {
            if (string.Equals(id, agentId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
