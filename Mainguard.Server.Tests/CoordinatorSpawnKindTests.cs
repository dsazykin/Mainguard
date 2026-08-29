using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>Defect D1 — a coordinator may only spawn a CLI that is actually installed.</b>
///
/// <para>A real coordinator's first move was <c>mainguard-agent spawn coder "…"</c>. <c>coder</c> is not
/// an installed adapter, so <see cref="InstalledAdapterCatalog.TryGet"/> answered null, the jail was
/// created with NO launch command (<c>docker top</c> showed <c>sleep infinity</c> and nothing else), and
/// the shim answered <c>Ok, Status: AwaitingPlan</c>. The coordinator believed it had a worker; the dead
/// jail held a slot against the worker cap for the rest of the session.</para>
///
/// <para>A jail with no CLI stays a legitimate outcome of the <i>operator</i> path — an unknown kind
/// spawned by hand gets a bare jail plus a PTY on purpose, and the launcher's call site says so. It is
/// never a legitimate outcome of the coordinator path, where nobody is attached to that PTY: a Managed
/// worker's terminal is daemon-locked read-only, so the bare jail can only ever sit there.</para>
///
/// <para>Every assertion goes over the real Unix socket an in-jail shim writes to, for the reason
/// <see cref="CoordinatorRoleLockTests"/> states: contract §5 says a system prompt is not a boundary, so
/// nothing here asserts on prose or an in-process helper.</para>
/// </summary>
public sealed class CoordinatorSpawnKindTests : IDisposable
{
    private const string Repo = "spawn-kind-repo";
    private const string Installed = "probe-cli";
    private const string AlsoInstalled = "second-cli";

    private readonly SpawnKindRig _rig = SpawnKindRig.Create();
    private readonly List<string> _spawned = new();

    public void Dispose()
    {
        foreach (var id in _spawned)
        {
            try { _rig.Spawns.StopAsync(id, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* never fail a test from cleanup */ }
        }

        _rig.Dispose();
    }

    /// <summary>
    /// The defect itself. An agent kind that maps to no installed CLI is refused at the coordinator's
    /// spawn, so no session is minted, no jail is created, and no worker slot is consumed.
    /// </summary>
    [Fact]
    public async Task AnUninstalledAgentKind_IsRefused_AndMintsNoWorker()
    {
        var coordinator = await SpawnCoordinatorAsync();
        var before = _rig.Sessions.List().Count;

        var response = await CallAsync(coordinator, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "coder", TaskPrompt: "do the thing"));

        Assert.False(response.Ok, "an uninstalled agent kind was accepted — the jail it makes has no CLI in it.");
        Assert.Null(response.AgentId);
        Assert.Equal(before, _rig.Sessions.List().Count);
        Assert.DoesNotContain(_rig.Engine.Requests, r => r.AgentKind == "coder");
    }

    /// <summary>
    /// The refusal has to be actionable, because the operating instructions are the only other place the
    /// coordinator learns what a kind is. It names the offending kind and every installed one.
    /// </summary>
    [Fact]
    public async Task TheRefusal_NamesTheKindAndEveryInstalledOne()
    {
        var coordinator = await SpawnCoordinatorAsync();

        var response = await CallAsync(coordinator, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "coder", TaskPrompt: "do the thing"));

        var error = response.Error ?? string.Empty;
        Assert.Contains("coder", error, StringComparison.Ordinal);
        Assert.Contains(Installed, error, StringComparison.Ordinal);
        Assert.Contains(AlsoInstalled, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paired positive, without which every refusal above would pass on a handler that refused
    /// everything: an INSTALLED kind still spawns, and its jail is launched with that CLI's argv.
    /// </summary>
    [Fact]
    public async Task AnInstalledAgentKind_StillSpawns_AndItsJailGetsTheCli()
    {
        var coordinator = await SpawnCoordinatorAsync();

        var response = await CallAsync(coordinator, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: Installed, TaskPrompt: "do the thing",
            Title: "Plan the thing"));

        Assert.True(response.Ok, response.Error);
        _spawned.Add(response.AgentId!);
        Assert.Equal("AwaitingPlan", response.Status);

        var request = Assert.Single(_rig.Engine.Requests, r => r.AgentId == response.AgentId);
        Assert.Equal(Installed, request.AgentKind);
    }

    /// <summary>
    /// The carve-out the defect brief calls out by name: the OPERATOR path still gets a bare jail for an
    /// unknown kind. That path has a human on the PTY, which is the whole reason a CLI-less jail is useful
    /// there — so the refusal must be scoped to the coordinator's channel, not moved into
    /// <see cref="AgentSpawnService.SpawnAsync"/> where it would take the operator's shell away too.
    /// </summary>
    [Fact]
    public async Task TheOperatorPath_StillGetsABareJailForAnUnknownKind()
    {
        var id = await _rig.Spawns.SpawnAsync(
            Repo, "coder", modelApiKey: null, role: string.Empty, CancellationToken.None);
        _spawned.Add(id);

        var request = Assert.Single(_rig.Engine.Requests, r => r.AgentId == id);
        Assert.Equal("coder", request.AgentKind);
    }

    /// <summary>
    /// A box with NO adapters installed at all stays permissive — the documented meaning of
    /// <see cref="InstalledAdapterCatalog.HasAny"/> ("a dev/unprovisioned box"). Refusing there would
    /// break every headless/session-only spawn, and there is nothing to name in a refusal anyway: the
    /// message's whole value is the list of alternatives.
    /// </summary>
    [Fact]
    public async Task AnEmptyCatalog_StaysPermissive()
    {
        using var rig = SpawnKindRig.Create(withAdapters: false);
        var coordinator = await rig.Spawns.SpawnAsync(
            Repo, Installed, null, AgentRoles.Coordinator, CancellationToken.None);

        var response = await CallAsync(rig, coordinator, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "coder", TaskPrompt: "do the thing",
            Title: "Plan the thing"));

        Assert.True(response.Ok, response.Error);
        try { await rig.Spawns.StopAsync(response.AgentId!, CancellationToken.None); } catch { }
        try { await rig.Spawns.StopAsync(coordinator, CancellationToken.None); } catch { }
    }

    /// <summary>
    /// <b>The instructions and the installed set are one source.</b> The coordinator text names exactly the
    /// kinds this daemon has, so the list cannot rot into a description of an install that no longer
    /// exists (MG-12) — and it is the same set the refusal above enumerates.
    /// </summary>
    [Fact]
    public void TheCoordinatorsInstructions_NameExactlyTheInstalledKinds()
    {
        using var registry = new TempRegistry(Installed, AlsoInstalled);

        var instructions = AgentOperatingInstructions.For(
            AgentIpcEndpointRole.Coordinator, new InstalledAdapterCatalog(registry.Path));

        // The rendered list is EXACTLY the two markers in that registry — asserted as the whole line, so a
        // text that merely mentions them somewhere cannot pass. ("coder" does appear in the instructions,
        // named as a warning about inventing kinds; that is the opposite of offering it.)
        Assert.Contains("`" + Installed + "`, `" + AlsoInstalled + "`", instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the pairing that makes the previous test worth having: the text names the kinds and the
    /// enforcement refuses everything else, from ONE catalog. This is the MG-12 guard — the two cannot be
    /// made to disagree by editing either of them, because neither holds a list.
    /// </summary>
    [Fact]
    public void TheInstructionsAndTheRefusal_ReadTheSameSet()
    {
        using var registry = new TempRegistry(Installed, AlsoInstalled);
        var catalog = new InstalledAdapterCatalog(registry.Path);

        var instructions = AgentOperatingInstructions.For(AgentIpcEndpointRole.Coordinator, catalog);
        var refusal = CoordinatorSpawnGate.RefuseUnknownKind("coder", catalog.InstalledKinds());

        Assert.NotNull(refusal);
        foreach (var kind in catalog.InstalledKinds())
        {
            Assert.Contains(kind, instructions, StringComparison.Ordinal);
            Assert.Contains(kind, refusal!, StringComparison.Ordinal);
        }
    }

    // ---- G2: the two copies of one jail's instructions ------------------------------------------

    /// <summary>
    /// <b>Defect G2 — the same jail was handed two different instruction texts.</b>
    ///
    /// <para><c>SandboxAgentLauncher</c> rendered the <c>--append-system-prompt</c> copy from the daemon's
    /// catalog, while <c>AgentIpcServer</c> rendered the <c>MAINGUARD.md</c> copy from nothing at all — it
    /// simply omitted the optional argument. So in one and the same jail the flag named all six installed
    /// kinds and the file the CLI opens unprompted said <c>(none installed on this machine)</c>. Measured
    /// here on the FILE the daemon actually wrote, in a spawn that went through the real service.</para>
    /// </summary>
    [Fact]
    public async Task TheInstructionsFileAJailOpens_NamesTheKindsThisDaemonHas()
    {
        var coordinator = await SpawnCoordinatorAsync();

        var file = InstructionsFileFor(coordinator);

        Assert.Contains("`" + Installed + "`, `" + AlsoInstalled + "`", file, StringComparison.Ordinal);
        Assert.DoesNotContain(
            AgentOperatingInstructions.SpellKinds(Array.Empty<string>()), file, StringComparison.Ordinal);
    }

    /// <summary>
    /// The invariant the defect broke, asserted as one equality: the copy written beside the shim and the
    /// copy the launcher puts on the launch line are <b>the same bytes</b>. Two deliveries of one text can
    /// only disagree if something renders twice, so this is the assertion that a third delivery would also
    /// have to satisfy.
    /// </summary>
    [Fact]
    public async Task TheTwoDeliveriesOfOneJailsInstructions_AreTheSameText()
    {
        var coordinator = await SpawnCoordinatorAsync();

        var onTheLaunchLine = AgentOperatingInstructions
            .For(AgentIpcEndpointRole.Coordinator, _rig.Adapters).Replace("\r\n", "\n");

        Assert.Equal(onTheLaunchLine, InstructionsFileFor(coordinator));
    }

    /// <summary>
    /// <b>The structural half.</b> The divergence was possible because the installed set was an OPTIONAL
    /// argument: one call site passed it, the other did not, and both compiled. There is now no rendering
    /// entry point that can be reached without the catalog the enforcement itself reads — so a third call
    /// site cannot repeat this, whatever it forgets.
    ///
    /// <para>Asserted by reflection rather than by review, because "nobody will add an overload" is exactly
    /// the kind of promise this file exists to stop making.</para>
    /// </summary>
    [Fact]
    public void NoWayToRenderTheCoordinatorText_WithoutTheInstalledCatalog()
    {
        var renderers = typeof(AgentOperatingInstructions)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name is nameof(AgentOperatingInstructions.For)
                              or nameof(AgentOperatingInstructions.Coordinator))
            .ToArray();

        Assert.NotEmpty(renderers);
        foreach (var renderer in renderers)
        {
            var catalogArg = renderer.GetParameters()
                .SingleOrDefault(p => p.ParameterType == typeof(InstalledAdapterCatalog));
            Assert.True(
                catalogArg is not null,
                $"{renderer.Name} can render the coordinator's instructions without a catalog — that is "
                + "the G2 defect's shape: a caller that omits the installed set still compiles.");
            Assert.False(
                catalogArg!.IsOptional,
                $"{renderer.Name}'s catalog is optional, so it can be omitted at a call site.");
        }
    }

    private string InstructionsFileFor(string agentId) =>
        File.ReadAllText(Path.Combine(_rig.Ipc.DirFor(agentId), AgentIpcPaths.InstructionsFileName));

    /// <summary>A registry directory holding exactly the markers a test names, and nothing the developer
    /// happens to have installed.</summary>
    private sealed class TempRegistry : IDisposable
    {
        public TempRegistry(params string[] ids)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-kind-registry-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
            foreach (var id in ids)
            {
                File.WriteAllText(
                    System.IO.Path.Combine(Path, id + ".json"),
                    InstalledAdapterMarker.Serialize(
                        new InstalledAdapterMarker(id, "1.0.0", new[] { "/bin/" + id })));
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<string> SpawnCoordinatorAsync()
    {
        var id = await _rig.Spawns.SpawnAsync(
            Repo, Installed, null, AgentRoles.Coordinator, CancellationToken.None);
        _spawned.Add(id);
        return id;
    }

    private Task<AgentIpcResponse> CallAsync(string agentId, AgentIpcRequest request)
        => CallAsync(_rig, agentId, request);

    private static async Task<AgentIpcResponse> CallAsync(
        SpawnKindRig rig, string agentId, AgentIpcRequest request)
    {
        var socketPath = Path.Combine(rig.Ipc.DirFor(agentId), AgentIpcPaths.SocketFileName);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(client);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(AgentIpcProtocol.SerializeRequest(request) + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<AgentIpcResponse>(line!)!;
    }

    /// <summary>
    /// An in-proc daemon over a fake substrate whose adapter registry is a temp directory this test owns
    /// — never the host's real one. The catalog is otherwise resolved from
    /// <c>~/mainguard/adapters/registry</c>, which would make every assertion here depend on what the
    /// developer happens to have installed.
    /// </summary>
    internal sealed class SpawnKindRig : IDisposable
    {
        private readonly string _root;
        private readonly DaemonFixture _daemon;

        private SpawnKindRig(string root, DaemonFixture daemon)
        {
            _root = root;
            _daemon = daemon;
        }

        public required WebApplicationFactory<Program> Host { get; init; }

        internal required AgentSessionRepoScopingTests.RecordingEngine Engine { get; init; }

        public AgentSpawnService Spawns => Host.Services.GetRequiredService<AgentSpawnService>();

        public AgentIpcServer Ipc => Host.Services.GetRequiredService<AgentIpcServer>();

        public AgentSessionStore Sessions => Host.Services.GetRequiredService<AgentSessionStore>();

        /// <summary>The daemon's own catalog — the one source both the instructions and the refusal read.</summary>
        public InstalledAdapterCatalog Adapters => Host.Services.GetRequiredService<InstalledAdapterCatalog>();

        public static SpawnKindRig Create(bool withAdapters = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "mg-spawn-kind-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "repos", Repo)); // "provisioned"

            var registry = Path.Combine(root, "registry");
            Directory.CreateDirectory(registry);
            if (withAdapters)
            {
                foreach (var id in new[] { Installed, AlsoInstalled })
                {
                    File.WriteAllText(
                        Path.Combine(registry, id + ".json"),
                        InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                            id, "1.0.0", new[] { "/bin/" + id },
                            SystemPromptArg: "--append-system-prompt")));
                }
            }

            var engine = new AgentSessionRepoScopingTests.RecordingEngine();
            var daemon = new DaemonFixture();
            var host = daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(
                    new AgentSessionRepoScopingTests.FakeAgentEnvironment(root, engine));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
            }));
            _ = host.Services;

            return new SpawnKindRig(root, daemon) { Host = host, Engine = engine };
        }

        public void Dispose()
        {
            Host.Dispose();
            _daemon.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
