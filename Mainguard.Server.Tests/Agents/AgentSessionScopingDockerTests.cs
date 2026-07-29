using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Gateway;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// #270's jail-liveness fallback, against two REAL jails that share an agent id.
///
/// <para>The session store is memory-only and the jails are persistent, so after a daemon restart the
/// daemon asks the container runtime — P2-08's sole source of truth for jail liveness — which container
/// belongs to an agent. That lookup reads the <c>mainguard.repo</c> and <c>mainguard.agent</c> labels
/// P2-07 stamps on every jail. Now that an agent id is unique only WITHIN a repo (the external-PR intake
/// names its workers <c>pr-&lt;n&gt;</c> after the pull request number), matching on
/// <c>mainguard.agent</c> alone would hand one repo's queue the other repo's container — a jail holding
/// somebody else's untrusted pull-request head.</para>
///
/// <para>This is asserted against Docker rather than a fake because the labels are the product of the
/// shipped <see cref="ContainerSpecBuilder"/> and the answer is read by the shipped
/// <see cref="GatewayServiceRegistration.ResolveRunningJail"/>: a fake lister would only prove that the
/// test's own dictionary has two keys.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class AgentSessionScopingDockerTests
{
    private const string SharedAgentId = "pr-7";

    [RequiresDockerFact]
    public async Task TwoReposJailsUnderOneAgentId_AreDistinguishedByBothLabels()
    {
        await using var sandbox = new SandboxFixture();
        var repoA = "scope-a-" + Guid.NewGuid().ToString("N")[..8];
        var repoB = "scope-b-" + Guid.NewGuid().ToString("N")[..8];

        var (containerA, _) = await sandbox.CreateJailOnSegmentAsync(repoA, SharedAgentId);
        var (containerB, _) = await sandbox.CreateJailOnSegmentAsync(repoB, SharedAgentId);

        // Two containers really exist for the one agent id — the premise the rest of the test rests on.
        Assert.NotEqual(containerA, containerB);

        // Each carries BOTH labels, and the agent label is identical across them: the id alone genuinely
        // cannot tell these apart, so what follows is not disambiguating on an accidental difference.
        var inspectA = await sandbox.InspectAsync(containerA);
        var inspectB = await sandbox.InspectAsync(containerB);
        Assert.Equal(SharedAgentId, inspectA.Config.Labels["mainguard.agent"]);
        Assert.Equal(SharedAgentId, inspectB.Config.Labels["mainguard.agent"]);
        Assert.Equal(repoA, inspectA.Config.Labels["mainguard.repo"]);
        Assert.Equal(repoB, inspectB.Config.Labels["mainguard.repo"]);

        // The daemon's OWN resolver, on both labels: each repo gets its own jail…
        Assert.Equal(containerA, GatewayServiceRegistration.ResolveRunningJail(repoA, SharedAgentId));
        Assert.Equal(containerB, GatewayServiceRegistration.ResolveRunningJail(repoB, SharedAgentId));

        // …and a repo with no jail of its own gets NOTHING, rather than the first container that happens
        // to answer to `pr-7`. This is the assertion an agent-label-only match would fail.
        Assert.Null(GatewayServiceRegistration.ResolveRunningJail("scope-c-nobody", SharedAgentId));

        // Stopping repo A's jail must not change repo B's answer — the release path tears down one
        // (repo, agent) and the other is still running and still resolvable.
        await sandbox.Engine.StopAsync(containerA);
        await WaitUntilStoppedAsync(sandbox.Docker, containerA);

        Assert.Null(GatewayServiceRegistration.ResolveRunningJail(repoA, SharedAgentId));
        Assert.Equal(containerB, GatewayServiceRegistration.ResolveRunningJail(repoB, SharedAgentId));
    }

    /// <summary>The container names the two jails run under are themselves per-(repo, agent), which is why
    /// two jails could be created for one agent id at all — Docker refuses a duplicate name.</summary>
    [RequiresDockerFact]
    public async Task TheTwoJails_RunUnderDistinctPerRepoContainerNames()
    {
        await using var sandbox = new SandboxFixture();
        var repoA = "scope-a-" + Guid.NewGuid().ToString("N")[..8];
        var repoB = "scope-b-" + Guid.NewGuid().ToString("N")[..8];

        var (containerA, _) = await sandbox.CreateJailOnSegmentAsync(repoA, SharedAgentId);
        var (containerB, _) = await sandbox.CreateJailOnSegmentAsync(repoB, SharedAgentId);

        var nameA = (await sandbox.InspectAsync(containerA)).Name.TrimStart('/');
        var nameB = (await sandbox.InspectAsync(containerB)).Name.TrimStart('/');

        Assert.Equal(ContainerSpecBuilder.ContainerName(repoA, SharedAgentId), nameA);
        Assert.Equal(ContainerSpecBuilder.ContainerName(repoB, SharedAgentId), nameB);
        Assert.NotEqual(nameA, nameB);
    }

    /// <summary>Docker reports a stopped container as running for a moment after the stop returns; poll
    /// rather than assume, or the liveness assertion measures the poll timing instead of the label match.</summary>
    private static async Task WaitUntilStoppedAsync(IDockerClient docker, string containerId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await docker.Containers.InspectContainerAsync(containerId);
            if (state.State?.Running != true)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"container {containerId} was still running 30s after StopAsync");
    }
}
