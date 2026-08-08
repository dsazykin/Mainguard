using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The <c>HarvestAgentCredentials</c> RPC's <b>transport contract</b>: argument validation, the
/// unknown-agent answer, authentication, and the property that harvesting does not stop the agent.
///
/// <para><b>Scope, stated because it was previously overstated.</b> These are edge/liveness cases and
/// nothing more. Every agent they spawn uses <c>unprovisioned-handle</c>, so it has no jail and no
/// credential to return — the harvest legitimately answers empty in all of them, and each would keep
/// passing if the harvest returned empty for a REAL jail too. This class therefore says nothing about
/// whether login state actually makes the round-trip; reading it as round-trip coverage is what let
/// the missing harvest caller go unnoticed. The two claims that matter are pinned elsewhere, at seams
/// where they can fail:</para>
/// <list type="bullet">
///   <item><c>CliLoginHarvestWiringTests</c> — the shipped client actually CALLS the harvest (the
///   periodic sweep and the shutdown one), asserting the bytes reach the keychain;</item>
///   <item><c>CliLoginRoundTripDockerTests</c> — login written in a real jail survives that jail's
///   teardown and reappears in a fresh one.</item>
/// </list>
///
/// <para><c>HarvestAgentCredentials</c> exists so the client can pull the current login while the
/// agent keeps running: harvest used to happen ONLY inside <c>AgentSpawnService.StopAsync</c>, so a
/// daemon stop, VM shutdown, app close, or crash never harvested at all — the tmpfs home died with the
/// container and the user signed in again on every launch.</para>
/// </summary>
public sealed class HarvestCredentialsRpcTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public HarvestCredentialsRpcTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task Harvest_RequiresAnAgentId()
    {
        var agents = new AgentService.AgentServiceClient(_daemon.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            agents.HarvestAgentCredentialsAsync(
                new HarvestAgentCredentialsRequest { AgentId = "" }, _daemon.AuthHeaders()).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // An unknown agent must answer empty rather than throwing: the client sweeps every live agent and
    // one that vanished mid-sweep is ordinary, not an error.
    [Fact]
    public async Task Harvest_UnknownAgent_IsEmpty_NotAnError()
    {
        var agents = new AgentService.AgentServiceClient(_daemon.CreateChannel());

        var reply = await agents.HarvestAgentCredentialsAsync(
            new HarvestAgentCredentialsRequest { AgentId = "no-such-agent" }, _daemon.AuthHeaders());

        Assert.Empty(reply.CliCredentials);
        Assert.Equal(string.Empty, reply.AgentKind);
    }

    // The whole point: harvesting must NOT stop the agent. Previously the only way to get credentials
    // out was StopAgent, which killed the very session the user had just signed into.
    [Fact]
    public async Task Harvest_LeavesTheAgentRunning()
    {
        var agents = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var spawn = await agents.SpawnAgentAsync(
            new SpawnAgentRequest { RepoHandle = "unprovisioned-handle", AgentKind = "claude-code" },
            _daemon.AuthHeaders());

        var before = await agents.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.Contains(before.Agents, a => a.AgentId == spawn.AgentId);

        await agents.HarvestAgentCredentialsAsync(
            new HarvestAgentCredentialsRequest { AgentId = spawn.AgentId }, _daemon.AuthHeaders());

        // Still listed — harvest is read-only.
        var after = await agents.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.Contains(after.Agents, a => a.AgentId == spawn.AgentId);

        await agents.StopAgentAsync(new StopAgentRequest { AgentId = spawn.AgentId }, _daemon.AuthHeaders());
    }

    // Repeated harvesting is how the client keeps the keychain warm, so it must be safe to call often.
    [Fact]
    public async Task Harvest_IsRepeatable_AndKeepsTheAgentAlive()
    {
        var agents = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var spawn = await agents.SpawnAgentAsync(
            new SpawnAgentRequest { RepoHandle = "unprovisioned-handle", AgentKind = "claude-code" },
            _daemon.AuthHeaders());

        for (var i = 0; i < 3; i++)
        {
            await agents.HarvestAgentCredentialsAsync(
                new HarvestAgentCredentialsRequest { AgentId = spawn.AgentId }, _daemon.AuthHeaders());
        }

        var listed = await agents.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.Contains(listed.Agents, a => a.AgentId == spawn.AgentId);

        await agents.StopAgentAsync(new StopAgentRequest { AgentId = spawn.AgentId }, _daemon.AuthHeaders());
    }

    // The RPC carries secrets, so it must be authenticated like every other method. (The
    // reflect-every-method auth theory in DaemonAuthTests covers this automatically too.)
    [Fact]
    public async Task Harvest_RequiresAuthentication()
    {
        var agents = new AgentService.AgentServiceClient(_daemon.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            agents.HarvestAgentCredentialsAsync(
                new HarvestAgentCredentialsRequest { AgentId = "a" }, _daemon.WrongTokenHeaders()).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }
}
