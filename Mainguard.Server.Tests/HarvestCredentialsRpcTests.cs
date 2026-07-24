using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Login persistence across a daemon restart.
///
/// <para><b>The bug.</b> The CLI's login-state files live in the jail's tmpfs <c>$HOME</c>, and the
/// durable store is the host OS keychain. The round-trip existed — <c>StopAgent</c> harvests, the
/// client persists, the next <c>SpawnAgent</c> restores — but harvest was called from exactly ONE
/// place: <c>AgentSpawnService.StopAsync</c>. Daemon shutdown only logs. So a daemon stop, VM
/// shutdown, app close without stopping agents, or any crash never harvested at all: the tmpfs home
/// died with the container and the user had to sign in again on every single launch.</para>
///
/// <para><c>HarvestAgentCredentials</c> lets the client pull the current login while the agent keeps
/// running, so the keychain can be kept warm instead of updated only at teardown.</para>
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
