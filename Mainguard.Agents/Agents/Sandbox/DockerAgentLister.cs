using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Lists the live agent jails straight from Docker by the <c>mainguard.agent</c> label P2-07 sets — the
/// <b>sole source of truth</b> the P2-08 <see cref="SwarmReconciler"/> consumes (no PID/lock files).
/// Kept separate from <see cref="DockerSandboxEngine"/> so the reconciler depends only on a listing
/// function, not the whole engine.
/// </summary>
public static class DockerAgentLister
{
    /// <summary>Reads every container carrying the <c>mainguard.agent</c> label into reconciler state.</summary>
    public static async Task<IReadOnlyList<AgentContainerState>> ListAsync(IDockerClient docker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docker);
        var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["mainguard.agent"] = true },
            },
        }, ct).ConfigureAwait(false);

        return containers.Select(c => new AgentContainerState(
            AgentId: Label(c, AgentIdLabel),
            RepoHash: Label(c, RepoHashLabel),
            ContainerId: c.ID,
            Running: string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase),
            // Docker reports a frozen container as "paused", NOT as "running" — so a jail the kill switch
            // or a human paused is present but not Running, and every caller that wanted "still here" has
            // to read AgentContainerState.Live. Carried separately so the daemon can also correct its own
            // tracked state toward Docker's (ISSUES-LOG #20).
            Paused: string.Equals(c.State, "paused", StringComparison.OrdinalIgnoreCase),
            Kind: Label(c, KindLabel),
            Role: Label(c, AgentRoleLabel))).ToList();
    }

    /// <summary>The agent id label P2-07 stamps on every jail — also the list filter.</summary>
    public const string AgentIdLabel = "mainguard.agent";

    /// <summary>The owning repository's handle hash.</summary>
    public const string RepoHashLabel = "mainguard.repo";

    /// <summary>The agent CLI running in the jail (<c>claude-code</c>, …) — what the daemon's session
    /// record calls <c>Kind</c>. Empty on a jail created before this label existed.</summary>
    public const string KindLabel = "mainguard.kind";

    /// <summary>
    /// The ORCHESTRATION role (<c>""</c> / <c>coordinator</c> / <c>managed</c>).
    ///
    /// <para>Deliberately not <c>mainguard.role</c>: that label already means something else — which kind
    /// of container this is (<c>agent</c> vs the egress proxy's <c>egress-proxy</c>) — and the two answer
    /// different questions. Without this label a jail adopted after a daemon restart comes back as a
    /// role-less worker, so the Coordinator surface would find no coordinator for a repo that has one.</para>
    /// </summary>
    public const string AgentRoleLabel = "mainguard.agent.role";

    private static string Label(ContainerListResponse container, string key) =>
        container.Labels is not null && container.Labels.TryGetValue(key, out var value) ? value : string.Empty;
}
