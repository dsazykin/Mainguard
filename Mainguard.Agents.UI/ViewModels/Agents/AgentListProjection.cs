using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents;

namespace Mainguard.Agents.UI.ViewModels.Agents;

/// <summary>
/// Pure LIFO projection for the section-rail agent list (P2-13 Row 1): newest agent first,
/// ordered by spawn time. Extracted so both the live rail (<c>ControlCenterViewModel</c>) and
/// <c>ActivityBarOrderingTests</c> exercise the exact same ordering — the ordering is not
/// re-implemented in the test.
///
/// <para>Every rail/coordinator ordering in <c>ControlCenterViewModel</c> now calls
/// <see cref="LifoOrder"/>. It previously spelled <c>OrderByDescending(a =&gt; a.SpawnedAt)</c>
/// inline in three places, which made the claim above false AND left the same-timestamp case
/// undefined: <c>OrderByDescending</c> is stable, so equal spawn times fell back to the input
/// order — and the input is <c>IAgentService.ListAgents()</c>, i.e. dictionary enumeration
/// order. Two agents spawned in the same tick could therefore swap places in the rail (and
/// change which coordinator "Stop" targeted) between two refreshes with no state change.</para>
/// </summary>
public static class AgentListProjection
{
    /// <summary>Newest-spawned first (LIFO), ties broken by descending <c>AgentId</c>. A total
    /// ordering — it depends on nothing but the elements themselves, so it does not vary with
    /// input order, and removing any element leaves the relative order of the rest unchanged.</summary>
    public static IReadOnlyList<AgentInfo> LifoOrder(IEnumerable<AgentInfo> agents) =>
        agents
            .OrderByDescending(a => a.SpawnedAt)
            .ThenByDescending(a => a.AgentId, System.StringComparer.Ordinal)
            .ToList();
}
