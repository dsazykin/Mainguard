using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>An in-proc daemon standing on a substrate whose bind mount CANNOT carry a Unix socket —
/// which is macOS, and the only place the agent-IPC outbox is load-bearing.</summary>
public sealed class OutboxSubstrateRig : IDisposable
{
    public const string RepoHandle = "fake-repo-hash-outbox";

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly string _root;

    internal AgentSessionRepoScopingTests.RecordingEngine Engine { get; }

    public OutboxSubstrateRig()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-outbox-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoHandle)); // "provisioned"
        Engine = new AgentSessionRepoScopingTests.RecordingEngine();
        var environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(
            _root, Engine, supportsBindMountedUnixSockets: false);
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services;
    }

    public AgentSpawnService Spawns => _host.Services.GetRequiredService<AgentSpawnService>();

    public AgentIpcServer Ipc => _host.Services.GetRequiredService<AgentIpcServer>();

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The wiring half of the macOS agent-IPC fix: does the daemon actually <b>hand the jail</b> the writable
/// outbox on a substrate that needs it?
///
/// <para><c>CoordinatorJailSpecTests</c> proves the builder mounts it correctly and refuses every other
/// source; <c>AgentIpcOutboxTests</c> proves the daemon serves it. Neither says the spawn path sets the
/// field — and a correct mechanism nobody passes the flag to is the MG-12 shape exactly, which is how the
/// coordinator role lock shipped as dead code once already. The paired negative lives in
/// <c>CoordinatorRoleLockTests</c>: on a substrate whose socket works, the same spawn sets no outbox and
/// the coordinator jail keeps zero writable bind mounts.</para>
/// </summary>
public sealed class CoordinatorOutboxWiringTests : IClassFixture<OutboxSubstrateRig>
{
    private readonly OutboxSubstrateRig _rig;

    public CoordinatorOutboxWiringTests(OutboxSubstrateRig rig) => _rig = rig;

    [Fact]
    public async Task OnASubstrateWhoseSocketCannotBeDialled_ACoordinatorJailIsGivenItsOutbox()
    {
        var coordinator = await _rig.Spawns.SpawnAsync(
            OutboxSubstrateRig.RepoHandle, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None);
        try
        {
            var request = Assert.Single(_rig.Engine.Requests, r => r.AgentId == coordinator);

            // The dir the daemon created IS the mount source, and the outbox is its fixed child — the
            // same function the spec builder re-derives to vet the read-write mount, so the two cannot
            // drift into naming different directories.
            Assert.Equal(_rig.Ipc.DirFor(coordinator), request.IpcDirPath);
            Assert.Equal(AgentIpcPaths.OutboxIn(request.IpcDirPath!), request.IpcOutboxPath);

            // And it exists on disk before the container would have been created, because it is a mount
            // source: a bind source that does not exist is a container-create failure, not a degradation.
            Assert.True(Directory.Exists(request.IpcOutboxPath));
        }
        finally
        {
            try { await _rig.Spawns.StopAsync(coordinator, CancellationToken.None); } catch { /* cleanup */ }
        }
    }
}
