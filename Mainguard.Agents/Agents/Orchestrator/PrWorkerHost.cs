using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Why an intake'd pull request does — or does not — have a jail to be verified in.</summary>
public enum PrWorkerOutcome
{
    /// <summary>A jail was just spawned for this entry, through the same gated chain a local agent takes.</summary>
    Spawned,

    /// <summary>A jail for this entry was already live: a repeat poll, or a daemon restart that left the
    /// container running (jails are persistent; the session store is not).</summary>
    AlreadyLive,

    /// <summary>A <b>gate</b> said no — kill switch, worker cap, memory admission. This is policy, not a
    /// fault: the pull request is simply left un-materialized for a later poll, when capacity frees.</summary>
    Refused,

    /// <summary>The spawn chain failed (no provisioned mirror, image preflight, Docker unreachable, …).</summary>
    Failed,
}

/// <summary>The answer to "does <c>pr-&lt;n&gt;</c> have a jail?", with the reason when it does not.</summary>
public sealed record PrWorkerResult(PrWorkerOutcome Outcome, string? Reason = null)
{
    /// <summary>True iff this entry now has a live jail — the only condition under which the intake is
    /// allowed to materialize a queue entry at all.</summary>
    public bool HasJail => Outcome is PrWorkerOutcome.Spawned or PrWorkerOutcome.AlreadyLive;

    public static PrWorkerResult Spawned() => new(PrWorkerOutcome.Spawned);
    public static PrWorkerResult AlreadyLive() => new(PrWorkerOutcome.AlreadyLive);
    public static PrWorkerResult Refused(string reason) => new(PrWorkerOutcome.Refused, reason);
    public static PrWorkerResult Failed(string reason) => new(PrWorkerOutcome.Failed, reason);
}

/// <summary>
/// The intake's spawn seam: gives an intake'd upstream pull request a <b>real jail</b>, by the same
/// daemon spawn chain a local agent takes.
///
/// <para><b>Why this exists.</b> <see cref="ExternalPrIntake"/> used to create a worktree and a queue
/// entry and spawn nothing at all. Verification runs in the worker's OWN jail — host execution is an
/// explicit rejection trigger — so an intake'd pull request had nowhere to be verified and could never
/// leave <c>Working</c>. The external <i>merge</i> leg was proven end to end; the external <i>verify</i>
/// leg had no jail to run in. This interface is what the intake asks, and the daemon's implementation
/// answers by running <c>AgentSpawnService.SpawnAsync</c> under an id of <c>pr-&lt;n&gt;</c>.</para>
///
/// <para><b>The PR head is untrusted code from outside</b>, which is the strongest argument there is for
/// the jail being real rather than a shortcut: the implementation takes the full hardened path (per-agent
/// repository, read-only mirror, per-agent egress segment, per-agent package cache, repo toolchain
/// layer), and it takes the full gate path (kill switch, worker cap, memory admission) so a bot filing
/// pull requests cannot fan out jails a coordinator would have been refused.</para>
/// </summary>
public interface IPrWorkerHost
{
    /// <summary>
    /// Ensure a live jail for <paramref name="agentId"/> in <paramref name="repoHash"/>, spawning one if
    /// there is none. Idempotent: an entry that already has a live jail consumes no cap and starts
    /// nothing. Never throws for an ordinary refusal or provisioning failure — those come back typed, so
    /// one bad pull request cannot take the poll loop down with it.
    /// </summary>
    Task<PrWorkerResult> EnsureWorkerAsync(string repoHash, string agentId, int prNumber, CancellationToken ct);

    /// <summary>
    /// Tear this entry's worker down (upstream closed/merged the pull request): jail, MG-36 network
    /// segment, package cache, worktree. Best-effort — a release must never fail a poll.
    /// </summary>
    Task ReleaseWorkerAsync(string repoHash, string agentId, CancellationToken ct);
}
