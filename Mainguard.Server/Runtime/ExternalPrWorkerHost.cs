using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The daemon's <see cref="IPrWorkerHost"/>: gives an intake'd upstream pull request a <b>real jail</b>
/// by running the ONE spawn chain (<see cref="AgentSpawnService.SpawnAsync"/>) under the id
/// <c>pr-&lt;n&gt;</c>.
///
/// <para><b>What the intake was missing.</b> <see cref="ExternalPrIntake"/> materialized a worktree and a
/// queue entry and spawned nothing, and <see cref="AgentSessionStore"/> could only mint GUID ids — so no
/// <c>pr-&lt;n&gt;</c> session, and therefore no <c>pr-&lt;n&gt;</c> jail, could exist. Verification runs
/// in the worker's own jail (host execution is a rejection trigger), so an intake'd pull request was
/// permanently stuck at <c>Working</c>. Everything below is the wiring that closes that, and none of it
/// is a new spawn path: it is the existing one, entered with an id the intake chose.</para>
///
/// <para><b>Gates (MG-2) — an external pull request bypasses none of them.</b> A bot filing pull requests
/// is an untrusted, unbounded source of spawn requests, so an intake spawn is admitted through exactly
/// the evaluator the wired coordinator shim uses, <see cref="CoordinatorSpawnGate"/>:
/// <list type="number">
///   <item>the <b>kill switch</b>, inside <see cref="AgentSpawnService.SpawnAsync"/> — a frozen daemon
///   refuses an intake spawn exactly as it refuses a coordinator's;</item>
///   <item>the <b>worker cap</b>: a <c>pr-&lt;n&gt;</c> worker spawns with
///   <see cref="AgentRoles.Managed"/>, so it is counted in and capped by the SAME
///   <c>MaxActiveWorkers</c> pool as coordinator-spawned workers rather than a private allowance of its
///   own — ten open bot pull requests cannot put ten jails on the box past a cap of six;</item>
///   <item><b>memory admission</b>, the <see cref="AdmissionController"/> headroom sampler.</item>
/// </list>
/// The budget gate needs no separate treatment here: budget is metered per model call at the AI gateway,
/// and this jail is deliberately spawned with no model key, no gateway session and no agent CLI — it
/// spends nothing because there is nothing in it to spend.</para>
///
/// <para><b>Refusal is not failure.</b> A refused spawn returns typed, the intake materializes nothing,
/// and the pull request is retried on a later poll. Nothing is weakened to make room for it.</para>
/// </summary>
public sealed class ExternalPrWorkerHost : IPrWorkerHost
{
    private readonly AgentSpawnService _spawns;
    private readonly AgentSessionStore _sessions;
    private readonly SandboxAgentLauncher _launcher;
    private readonly AdmissionController _admission;
    private readonly CoordinatorLimits _limits;
    private readonly Func<string, string, string?> _resolveRunningJail;
    private readonly IAuditLog _audit;
    private readonly ILogger _log;

    /// <param name="resolveRunningJail">
    /// (repoHash, agentId) → the container id of a RUNNING jail for that pair, read off the
    /// <c>mainguard.repo</c>/<c>mainguard.agent</c> labels — the same lookup the merge queue uses to
    /// decide jail liveness. It is what makes <see cref="EnsureWorkerAsync"/> idempotent across a daemon
    /// restart: sessions are memory-only while jails are persistent, so after a restart the session for
    /// <c>pr-&lt;n&gt;</c> is gone and its container is still up. Without this the intake would try to
    /// spawn a second jail over a worktree that already exists and fail every poll forever.
    /// </param>
    public ExternalPrWorkerHost(
        AgentSpawnService spawns,
        AgentSessionStore sessions,
        SandboxAgentLauncher launcher,
        AdmissionController admission,
        CoordinatorLimits limits,
        Func<string, string, string?> resolveRunningJail,
        IAuditLog audit,
        ILoggerFactory loggerFactory)
    {
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _resolveRunningJail = resolveRunningJail ?? throw new ArgumentNullException(nameof(resolveRunningJail));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _log = loggerFactory.CreateLogger(DaemonLogCategories.Intake);
    }

    public async Task<PrWorkerResult> EnsureWorkerAsync(
        string repoHash, string agentId, int prNumber, CancellationToken ct)
    {
        // Already ours, in this daemon's memory. Looked up by the FULL session identity (repo, id): a
        // `pr-<n>` id is unique only inside a repo, and the session store is now keyed to match, so a
        // pull request #n in another subscribed repository is a different session entirely rather than a
        // collision this had to refuse. That refusal is what this method used to do — the second repo's
        // pull request was never intake'd at all — and it is the defect this scoping removes.
        var key = new AgentSessionKey(repoHash, agentId);
        if (_sessions.Find(key) is { } live)
        {
            if (!string.IsNullOrEmpty(live.ContainerId))
            {
                return PrWorkerResult.AlreadyLive();
            }

            // Ours, this repo, but no jail ever attached — a session-only record left by a degraded
            // spawn. It still holds the (repo, id) key, so a respawn would (correctly) hit the store's
            // duplicate guard; say so instead of looping into it every poll.
            return PrWorkerResult.Failed(
                $"agent '{agentId}' in repo '{repoHash}' has a session but no jail — it is retried once "
                + "that session is released.");
        }

        // Not in memory, but the container runtime — which P2-08 designates the sole source of truth for
        // jail liveness — may still be running it (daemon restart). Adopting it costs no cap and starts
        // nothing; the merge queue already resolves the same jail by the same labels, so this entry can
        // verify without a respawn.
        if (_resolveRunningJail(repoHash, agentId) is { Length: > 0 })
        {
            _log.LogInformation(
                "external PR worker: adopting still-running jail agent={Agent} repo={Repo}", agentId, repoHash);
            return PrWorkerResult.AlreadyLive();
        }

        // MG-2. The same evaluator the coordinator's in-jail spawn shim is admitted through, over the same
        // Managed-worker population — an external pull request gets no allowance a coordinator would not.
        var activeManaged = _sessions.List().Count(s => s.Role == AgentRoles.Managed);
        var refusal = CoordinatorSpawnGate.Evaluate(activeManaged, _limits.MaxActiveWorkers, _admission);
        if (refusal is not null)
        {
            _log.LogWarning(
                "external PR worker refused (pr={Pr} agent={Agent}): {Reason}", prNumber, agentId, refusal);
            _audit.Append(new AuditEvent("external_pr_spawn_refused", new Dictionary<string, string>
            {
                ["agent"] = agentId,
                ["pr"] = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["repo"] = repoHash,
                ["active_managed"] = activeManaged.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max_active_workers"] = _limits.MaxActiveWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = refusal,
            }));
            return PrWorkerResult.Refused(refusal);
        }

        try
        {
            var spawned = await _spawns.SpawnAsync(
                repoHandle: repoHash,
                // No installed adapter answers to this kind, so the jail starts with no CLI: it exists to
                // be the sandbox its own verification runs in, not to run an agent.
                agentKind: ExternalPrIntake.WorkerAgentKind,
                // No model key: this worker never talks to a provider, so it never needs one and the
                // untrusted head never gets near one.
                modelApiKey: null,
                // Managed: counted by the cap above, and its terminal is daemon-locked read-only (P2-14) —
                // the correct posture for a jail holding somebody else's code.
                role: AgentRoles.Managed,
                ct: ct,
                agentId: agentId,
                // The badge that routes the merge back through the host's pull-request API. SpawnAsync's
                // EnsureEntry runs after the jail attaches and overwrites the origin, so passing it here
                // is what stops a Local stamp from undoing the intake's External one.
                queueOrigin: MergeEntryOrigin.External,
                withoutHostCredentials: true).ConfigureAwait(false);

            // A repo with no provisioned mirror yields a session-only record and no jail. That is not a
            // worker: leave nothing behind and report it, rather than letting the intake materialize an
            // entry that would answer "no live sandbox" to every verification.
            if (_sessions.Find(new AgentSessionKey(repoHash, spawned))?.ContainerId is not { Length: > 0 })
            {
                await _spawns.StopAsync(new AgentSessionKey(repoHash, spawned), CancellationToken.None)
                    .ConfigureAwait(false);
                return PrWorkerResult.Failed(
                    $"repo '{repoHash}' has no provisioned mirror, so no jail could be started for '{agentId}'.");
            }

            _log.LogInformation(
                "external PR worker jailed: pr={Pr} agent={Agent} repo={Repo}", prNumber, agentId, repoHash);
            _audit.Append(new AuditEvent("external_pr_spawned", new Dictionary<string, string>
            {
                ["agent"] = agentId,
                ["pr"] = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["repo"] = repoHash,
            }));
            return PrWorkerResult.Spawned();
        }
        catch (AgentSpawnRefusedException ex)
        {
            // The kill switch. Policy, not a fault — same treatment as the cap.
            _log.LogWarning("external PR worker refused (pr={Pr}): {Reason}", prNumber, ex.Message);
            return PrWorkerResult.Refused(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Image preflight, toolchain build, package cache, Docker unreachable. One bad pull request
            // must not take the daemon's poll loop down with it.
            _log.LogError(ex, "external PR worker spawn failed: pr={Pr} agent={Agent}", prNumber, agentId);
            return PrWorkerResult.Failed(ex.Message);
        }
    }

    public async Task ReleaseWorkerAsync(string repoHash, string agentId, CancellationToken ct)
    {
        try
        {
            // The ordinary stop: session record, PTY, input lock, jail, MG-36 segment, package cache,
            // worktree. Everything a local agent's stop reclaims — scoped to THIS repo's session, so
            // closing repo A's pull request #7 cannot tear down repo B's still-open pull request #7.
            var result = await _spawns.StopAsync(new AgentSessionKey(repoHash, agentId), ct)
                .ConfigureAwait(false);
            if (result.Stopped)
            {
                return;
            }

            // No session (a restart forgot it) but the jail may still be up — tear it down by the same
            // teardown, or the container and its network segment leak for the life of the VM. The bridge
            // pool is ~32 deep; a segment leaked per closed pull request exhausts it.
            if (_resolveRunningJail(repoHash, agentId) is { Length: > 0 } containerId)
            {
                await _launcher.TeardownAsync(repoHash, agentId, containerId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "external PR worker release failed: agent={Agent}", agentId);
        }
    }
}
