using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>One seed request row, mirroring the wire's <c>SeedEntrySpec</c> vocabulary verbatim
/// (target state and flavor names travel as strings so the panel and the daemon share one
/// vocabulary and a mismatch is a typed daemon refusal, never a silent client remap).</summary>
/// <param name="WithPlan">Drive the real phase-2 plan pipeline for this entry (held → presented →
/// approved), so plan-gated merge and the out-of-approved-scope arm are reachable without an agent.</param>
/// <param name="Scope"><paramref name="WithPlan"/> only: the approved plan's scope patterns. Empty means
/// "the path this seed's own commit touches" — in scope, merges; anything else arms the real
/// out-of-approved-scope must-acknowledge item.</param>
public sealed record SeedEntryRequestItem(
    string TargetState,
    int Count = 1,
    string Flavor = "PLAIN",
    bool VerificationFails = false,
    int HoldSeconds = 0,
    string StaleBehavior = "HOLD",
    string Reason = "",
    bool WithPlan = false,
    IReadOnlyList<string>? Scope = null);

/// <summary>One seeded entry's outcome — <paramref name="Refusal"/> empty on success, verbatim otherwise.</summary>
public sealed record SeedResultItem(string AgentId, string ReachedState, string Refusal);

/// <summary>One batch's outcome as the panel renders it.</summary>
public sealed record SeedBatchResult(
    IReadOnlyList<SeedResultItem> Results, string MainSha, bool ProvisionedVerifyConfig);

/// <summary>
/// The App's seam to the DEV-ONLY daemon queue seeder (docs/design/queue-seeding.md). Same shape as
/// the sibling gateways (<see cref="IPrIntakeGateway"/> etc.): implemented over
/// <see cref="DaemonClient"/> in production, holding no state, errors propagating as the daemon's own
/// refusals. The one seam-specific fact: on a daemon started without the seeding boot flag the
/// service is UNMAPPED, so <see cref="IsAvailableAsync"/> answers false off UNIMPLEMENTED — that is
/// the panel's entire visibility contract, and no capability flag travels anywhere else.
/// </summary>
public interface IQueueSeedingGateway
{
    /// <summary>True iff this daemon was started as a seeding daemon (the panel's show/hide probe).
    /// False — never a throw — for UNIMPLEMENTED (no flag) and PermissionDenied (the belt).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Seeds one ordered batch against the currently-open repo.</summary>
    Task<SeedBatchResult> SeedAsync(IReadOnlyList<SeedEntryRequestItem> entries, CancellationToken ct = default);

    /// <summary>Appends real commits to a seeded entry's branch (the real invalidation follows).
    /// Returns the entry's resulting state via <see cref="SeedResultItem.ReachedState"/>.</summary>
    Task<SeedResultItem> PushCommitsAsync(string agentId, int count = 1, CancellationToken ct = default);

    /// <summary>Removes every seeded entry of the currently-open repo.</summary>
    Task<(IReadOnlyList<string> Cleared, IReadOnlyList<SeedResultItem> Failures)> ClearAsync(
        CancellationToken ct = default);
}
