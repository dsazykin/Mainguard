using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// PR3 (CLI-as-coordinator) — the spawn→PTY→attach walking skeleton over the in-proc daemon with a
/// fake substrate + fake terminal sessions, so every seam of the wiring is proven cross-platform
/// without Docker or a real PTY: SpawnAgent with an installed-CLI launch command BINDS a long-lived
/// terminal session; Attach streams the REAL CLI (not the echo fallback); a detach never kills the
/// CLI and a re-attach replays the missed output; the coordinator role materializes the in-jail
/// spawn channel; a shim spawn lands a MANAGED worker (locked terminal) in the same store; and the
/// new ListInstalledAdapters RPC lists the registry markers. The real-jail (docker exec) leg is the
/// RequiresDocker matrix + CI; the real-PTY leg is <see cref="TerminalPtyAttachTests"/>.
/// </summary>
public sealed class AgentCliWiringTests : IClassFixture<DaemonFixture>
{
    private const string RepoHandle = "fake-repo-hash-1";
    private readonly DaemonFixture _daemon;

    public AgentCliWiringTests(DaemonFixture daemon) => _daemon = daemon;

    // ---- the wiring under test, end to end -------------------------------

    [Fact]
    public async Task SpawnWithInstalledCli_BindsSession_AttachStreamsIt_DetachSurvives_ReplayOnReattach()
    {
        using var rig = WiringRig.Create(_daemon);

        // Spawn against the "provisioned" repo: the fake catalog maps the kind to a launch argv.
        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var spawn = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            TaskPrompt = "fix the bug",
        }, rig.Auth);

        // The launcher resolved the marker's argv and the binder got the container + launch command.
        var session = Assert.Single(rig.Sessions.Values);
        Assert.Equal(new[] { "claude", "--permission-mode", "plan" }, rig.LastSpec!.Launch);
        Assert.StartsWith("ctr-", rig.LastSpec.ContainerId, StringComparison.Ordinal);

        // The TTY contract for the EXACT spec the composition root produced (field, 2026-07-17: a
        // CLI without an interactive TTY prints its non-interactive not-logged-in line and exits,
        // and the binder marks the agent dead): `docker exec -i -t` (attached stdin + tty), an
        // explicit sane TERM on BOTH sides of the exec, a positive size, and no secret in the env.
        var plan = AgentCliBinder.BuildPtyLaunch(rig.LastSpec);
        Assert.Equal(SandboxCliLaunch.DockerBinary, plan.Command);
        Assert.Equal("exec", plan.Args[0]);
        Assert.Contains("-i", plan.Args);
        Assert.Contains("-t", plan.Args);
        Assert.Contains("TERM=" + SandboxCliLaunch.InJailTerm, plan.Args);       // in-jail, via -e
        Assert.Equal(SandboxCliLaunch.InJailTerm, plan.Environment["TERM"]);     // daemon-side PTY
        Assert.True(plan.Cols > 0 && plan.Rows > 0);
        Assert.DoesNotContain(plan.Environment.Keys,
            k => k.Contains("KEY", StringComparison.OrdinalIgnoreCase)
              || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));       // G-13

        // A bound session exists — the attach path streams the CLI, no echo fallback.
        Assert.NotNull(rig.Terminals.TryGetBound(spawn.AgentId));

        // Attach and read the CLI's output through the real gRPC bidi + streamer path.
        await session.EmitAsync("FAKE-CLI-READY\r\n");
        using (var attach = rig.Attach(spawn.AgentId, out var cts))
        {
            var seen = await ReadUntilAsync(attach, s => s.Contains("FAKE-CLI-READY"), cts.Token);
            Assert.Contains("FAKE-CLI-READY", seen);

            // Keystrokes reach the CLI; resize propagates.
            await attach.RequestStream.WriteAsync(new TerminalInput
            {
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("hello-cli\n")),
            });
            var received = await session.ReadInputAsync("hello-cli\n".Length);
            Assert.Equal("hello-cli\n", received);

            await attach.RequestStream.WriteAsync(new TerminalInput
            {
                Resize = new Resize { Cols = 133, Rows = 41 },
            });
            await WaitForAsync(() => session.LastResize == (133, 41));

            cts.Cancel(); // detach — the client goes away, the CLI must not
        }

        // The CLI survived the detach; output produced while detached lands in the replay.
        Assert.NotNull(rig.Terminals.TryGetBound(spawn.AgentId));
        Assert.False(session.Killed);
        await session.EmitAsync("WHILE-DETACHED\r\n");

        using (var reattach = rig.Attach(spawn.AgentId, out var cts))
        {
            var replayed = await ReadUntilAsync(
                reattach, s => s.Contains("FAKE-CLI-READY") && s.Contains("WHILE-DETACHED"), cts.Token);
            Assert.Contains("FAKE-CLI-READY", replayed);   // the pre-detach output, replayed
            Assert.Contains("WHILE-DETACHED", replayed);   // the missed output, replayed
            cts.Cancel();
        }

        // StopAgent kills the CLI and unbinds the session.
        var stopped = await agents.StopAgentAsync(new StopAgentRequest { AgentId = spawn.AgentId }, rig.Auth);
        Assert.True(stopped.Stopped);
        Assert.Null(rig.Terminals.TryGetBound(spawn.AgentId));
        Assert.True(session.Killed);
    }

    [Fact]
    public async Task CliExit_MarksDead_AndCarriesTheDyingOutputIntoTheAudit()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var spawn = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
        }, rig.Auth);

        // The CLI's dying words (VT-colored, like the real thing), then an exit. Attach first so
        // the frame is provably through the pump/replay before the kill.
        var session = Assert.Single(rig.Sessions.Values);
        await session.EmitAsync("\u001b[33mNot logged in · Please run /login\u001b[0m\r\n");
        using (var attach = rig.Attach(spawn.AgentId, out var cts))
        {
            var seen = await ReadUntilAsync(attach, s => s.Contains("Not logged in"), cts.Token);
            Assert.Contains("Not logged in", seen);
            cts.Cancel();
        }

        session.Kill();

        // The binder reflects the natural exit as a Dead state…
        var store = rig.Host.Services.GetRequiredService<AgentSessionStore>();
        await WaitForAsync(() => store.Find(spawn.AgentId)?.State == "Dead");

        // …and the audit carries the exit code AND the cleaned output tail — the diagnosis the
        // field never had (a bare "CLI exited (N)" names no cause).
        var audit = (Mainguard.Git.Audit.InMemoryAuditLog)rig.Host.Services
            .GetRequiredService<Mainguard.Git.Audit.IAuditLog>();
        await WaitForAsync(() => audit.Read().Any(ev => ev.Type == "cli_exited"));
        var exited = audit.Read().Single(ev => ev.Type == "cli_exited" && ev.Fields["agent_id"] == spawn.AgentId);
        Assert.Equal("137", exited.Fields["exit_code"]);
        // VT color sequences stripped, text intact — a human-readable diagnosis, character-exact.
        Assert.Equal("Not logged in · Please run /login", exited.Fields["output_tail"]);

        // The dead agent's bound session is NOT released: attaching still replays the last output,
        // so the coordinator surface's "open its terminal to see why" stays true.
        Assert.NotNull(rig.Terminals.TryGetBound(spawn.AgentId));
    }

    [Fact]
    public async Task CliExit_InkCursorColumnLayout_TailKeepsWordBoundaries_AndDetectorNamesTheRealHost()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var spawn = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
        }, rig.Auth);

        // claude-code's REAL death screen (captured from the live jail): Ink separates words with
        // absolute cursor-column moves (ESC[nG), not literal spaces. Stripping those to nothing
        // welded the words — "Failedtoconnecttoplatform.claude.com" — and the egress block-detector
        // then proposed that whole blob as the host to unblock (field bug, 2026-07-22).
        var session = Assert.Single(rig.Sessions.Values);
        await session.EmitAsync(
            "[2G[38;5;211mFailed[9Gto[12Gconnect[20Gto"
            + "[23Gplatform.claude.com:[42GERR_SOCKET_CLOSED[39m\r\r\n");
        using (var attach = rig.Attach(spawn.AgentId, out var cts))
        {
            var seen = await ReadUntilAsync(attach, s => s.Contains("ERR_SOCKET_CLOSED"), cts.Token);
            Assert.Contains("ERR_SOCKET_CLOSED", seen);
            cts.Cancel();
        }

        session.Kill();

        var store = rig.Host.Services.GetRequiredService<AgentSessionStore>();
        await WaitForAsync(() => store.Find(spawn.AgentId)?.State == "Dead");

        var audit = (Mainguard.Git.Audit.InMemoryAuditLog)rig.Host.Services
            .GetRequiredService<Mainguard.Git.Audit.IAuditLog>();
        await WaitForAsync(() => audit.Read().Any(ev => ev.Type == "cli_exited"));
        var exited = audit.Read().Single(ev => ev.Type == "cli_exited" && ev.Fields["agent_id"] == spawn.AgentId);

        // Cursor-column moves read as word separators; the sentence survives human-readable…
        Assert.Equal("Failed to connect to platform.claude.com: ERR_SOCKET_CLOSED", exited.Fields["output_tail"]);

        // …and the block-detector extracts the actual host, not the welded blob.
        Assert.Equal("platform.claude.com",
            Mainguard.Agents.Agents.Sandbox.EgressBlockDetector.TryDetectBlockedHost(exited.Fields["output_tail"]));
    }

    [Fact]
    public async Task UnprovisionedRepo_StaysSessionOnly_AttachFallsBackToEcho()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var spawn = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = "never-provisioned",
            AgentKind = "claude-code",
        }, rig.Auth);

        // No jail, no CLI, no bound session — honest degradation, and the attach echo still works.
        Assert.Null(rig.Terminals.TryGetBound(spawn.AgentId));
        Assert.Empty(rig.Sessions);

        using var attach = rig.Attach(spawn.AgentId, out var cts);
        await attach.RequestStream.WriteAsync(new TerminalInput
        {
            Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("echo-me")),
        });
        var seen = await ReadUntilAsync(attach, s => s.Contains("echo-me"), cts.Token);
        Assert.Contains("echo-me", seen);
        cts.Cancel();
    }

    [Fact]
    public async Task ListInstalledAdapters_ReturnsRegistryMarkers_NoSecrets()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var response = await agents.ListInstalledAdaptersAsync(new ListInstalledAdaptersRequest(), rig.Auth);

        var adapter = Assert.Single(response.Adapters);
        Assert.Equal("claude-code", adapter.Id);
        Assert.Equal("2.1.0", adapter.Version);
        Assert.Equal("ANTHROPIC_API_KEY", adapter.ApiKeyEnvVar); // the env-var NAME — never a value
    }

    [Fact]
    public async Task Roles_RideListAgents_AndTheSnapshotStream()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var spawn = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            Role = AgentRoles.Coordinator,
        }, rig.Auth);

        var list = await agents.ListAgentsAsync(new ListAgentsRequest(), rig.Auth);
        var info = Assert.Single(list.Agents, a => a.AgentId == spawn.AgentId);
        Assert.Equal(AgentRoles.Coordinator, info.Role);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = agents.StreamAgentEvents(new StreamAgentEventsRequest(), rig.Auth, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        var snapshot = stream.ResponseStream.Current.Snapshot;
        Assert.NotNull(snapshot);
        var snapshotInfo = Assert.Single(snapshot.Agents, a => a.AgentId == spawn.AgentId);
        Assert.Equal(AgentRoles.Coordinator, snapshotInfo.Role);
        cts.Cancel();
    }

    // ---- the coordinator spawn channel (Unix sockets: Linux CI is authoritative) ----

    [LinuxOnlyFact]
    public async Task CoordinatorSpawn_CreatesIpcEndpoint_AndShimSpawnLandsLockedManagedWorker()
    {
        using var rig = WiringRig.Create(_daemon);

        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var coordinator = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            Role = AgentRoles.Coordinator,
        }, rig.Auth);

        // The endpoint dir exists (it was the jail's mount source) with the shim + socket, and the
        // jail spec carried it read-only at the fixed mount point.
        var ipc = rig.Host.Services.GetRequiredService<CoordinatorIpcServer>();
        var dir = ipc.DirFor(coordinator.AgentId);
        Assert.True(File.Exists(Path.Combine(dir, Mainguard.Agents.Agents.Ipc.AgentIpcPaths.ShimFileName)));
        Assert.True(File.Exists(Path.Combine(dir, Mainguard.Agents.Agents.Ipc.AgentIpcPaths.SocketFileName)));
        Assert.Equal(dir, rig.Engine.Requests.Single().IpcDirPath);

        // The shim's wire protocol: one JSON line in, one out — a managed worker spawns through the
        // SAME chain (session store → jail → bound CLI) and streams to the UI as a subagent.
        var response = await ShimRoundTripAsync(dir,
            """{"op":"spawn","agentKind":"claude-code","taskPrompt":"split the work"}""");
        Assert.Contains("\"ok\":true", response, StringComparison.Ordinal);

        var list = await agents.ListAgentsAsync(new ListAgentsRequest(), rig.Auth);
        var worker = Assert.Single(list.Agents, a => a.Role == AgentRoles.Managed);
        Assert.Equal("claude-code", worker.AgentKind);
        Assert.NotNull(rig.Terminals.TryGetBound(worker.AgentId)); // its CLI is bound too

        // P2-14: the managed worker's terminal is read-only — banner delivered, input severed.
        using var attach = rig.Attach(worker.AgentId, out var cts);
        var banner = await ReadUntilAsync(attach, s => s.Contains("read-only"), cts.Token);
        Assert.Contains("read-only", banner);
        await attach.RequestStream.WriteAsync(new TerminalInput
        {
            Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("rm -rf /\n")),
        });
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await attach.ResponseStream.MoveNext(cts.Token))
            {
            }
        });
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        // Stopping the coordinator tears its endpoint down.
        await agents.StopAgentAsync(new StopAgentRequest { AgentId = coordinator.AgentId }, rig.Auth);
        Assert.False(Directory.Exists(dir));
    }

    [LinuxOnlyFact]
    public async Task MainguardAgentShim_RealScript_SpawnsWorkerOverTheSocket()
    {
        if (!IsOnPath("python3"))
        {
            return; // the pre-baked jail toolchain has python3; a bare CI box without it skips the leg
        }

        using var rig = WiringRig.Create(_daemon);
        var agents = new AgentService.AgentServiceClient(rig.Channel);
        var coordinator = await agents.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            Role = AgentRoles.Coordinator,
        }, rig.Auth);

        var ipc = rig.Host.Services.GetRequiredService<CoordinatorIpcServer>();
        var dir = ipc.DirFor(coordinator.AgentId);
        var shim = Path.Combine(dir, Mainguard.Agents.Agents.Ipc.AgentIpcPaths.ShimFileName);

        // Run the REAL shim the daemon wrote, exactly as a coordinator CLI would (socket path
        // overridden to the host-side dir — inside a jail the fixed mount path is the default).
        var psi = new System.Diagnostics.ProcessStartInfo("python3", $"\"{shim}\" spawn claude-code do the thing")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["MAINGUARD_IPC_SOCKET"] = Path.Combine(dir, Mainguard.Agents.Agents.Ipc.AgentIpcPaths.SocketFileName);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"shim failed: {stderr}");
        var workerId = stdout.Trim();
        var list = await agents.ListAgentsAsync(new ListAgentsRequest(), rig.Auth);
        Assert.Contains(list.Agents, a => a.AgentId == workerId && a.Role == AgentRoles.Managed);
    }

    [Fact]
    public async Task ShimSpawn_WhileFrozen_IsRefusedWithHonestError()
    {
        using var rig = WiringRig.Create(_daemon);
        var spawns = rig.Host.Services.GetRequiredService<AgentSpawnService>();
        var store = rig.Host.Services.GetRequiredService<AgentSessionStore>();
        var gate = rig.Host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.KillSwitchGate>();

        // A live coordinator session with a repo, directly against the workflow (no socket needed —
        // this leg is the policy, which must hold cross-platform).
        var coordinatorId = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);

        gate.Freeze();
        try
        {
            var refused = await spawns.HandleShimRequestAsync(
                new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "more work"),
                coordinatorId, default);
            Assert.False(refused.Ok);
            Assert.Contains("frozen", refused.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Single(store.List(), s => s.Kind == "claude-code"); // no worker record leaked
        }
        finally
        {
            gate.Resume();
        }
    }

    [Fact]
    public async Task ShimList_IsScopedToTheCallersOwnWorkers()
    {
        using var rig = WiringRig.Create(_daemon);
        var spawns = rig.Host.Services.GetRequiredService<AgentSpawnService>();

        // Two independent coordinators, each spawning a worker through its OWN shim socket.
        var coordA = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);
        var coordB = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);

        var a = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "work"), coordA, default);
        var b = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "work"), coordB, default);
        Assert.True(a.Ok, a.Error);
        Assert.True(b.Ok, b.Error);

        var listA = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("list"), coordA, default);
        Assert.True(listA.Ok);

        // A sees only its own worker — not B's worker, not B, not itself.
        var idsA = listA.Agents!.Select(line => line.Split('\t')[0]).ToArray();
        Assert.Equal(new[] { a.AgentId }, idsA);
        Assert.DoesNotContain(b.AgentId!, idsA);
        Assert.DoesNotContain(coordB, idsA);

        // ...and symmetrically for B.
        var listB = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("list"), coordB, default);
        Assert.Equal(new[] { b.AgentId }, listB.Agents!.Select(line => line.Split('\t')[0]).ToArray());
    }

    /// <summary>
    /// MG-37 flake — the in-proc daemon graph must read an INJECTED memory sample, never the box's
    /// <c>/proc/meminfo</c>.
    ///
    /// <para>This is the fix's own regression test, and it is deterministic on every platform because
    /// the asserted total is a sentinel no real machine reports: unfixed it fails on Linux (the real
    /// <c>MemTotal</c>) and on Windows alike (no <c>/proc/meminfo</c> → the sampler returns a zeroed
    /// sample). It does not depend on the box actually being under pressure, which is precisely what
    /// made the original failure unreproducible.</para>
    /// </summary>
    [Fact]
    public void InProcDaemonGraph_ReadsAnInjectedMemorySample_NotTheHostsProcMeminfo()
    {
        using var rig = WiringRig.Create(_daemon);
        var sample = rig.Host.Services.GetRequiredService<AdmissionController>().CurrentSample();

        Assert.Equal(DaemonFixture.RoomySample.MemTotalKb, sample.MemTotalKb);
        Assert.True(sample.UsedFraction < AdmissionController.DefaultUsedThreshold,
            $"the in-proc tier is admitting against a {sample.UsedFraction:P0}-used sample — a shim spawn "
            + "will be refused whenever the machine happens to be busy, which is the MG-37 flake.");
    }

    /// <summary>
    /// MG-37 flake, the mechanism — what a memory refusal actually does to
    /// <see cref="ShimList_IsScopedToTheCallersOwnWorkers"/>, pinned deterministically.
    ///
    /// <para>Admission is consulted on the wired shim-spawn path (MG-2), so a machine over the 85%
    /// threshold turns the worker spawn into <c>Ok:false</c> and the coordinator's own list into an
    /// EMPTY one — the exact shape the flake produced. Kept as a test rather than a comment because it
    /// is the evidence that the refusal is a legitimate, correctly-reported production behaviour and
    /// not something to be softened: the bug was letting a test tier take that input from the machine,
    /// not the refusal itself.</para>
    /// </summary>
    [Fact]
    public async Task ShimSpawn_UnderMemoryPressure_IsRefused_AndLeavesTheCallersListEmpty()
    {
        using var rig = WiringRig.Create(_daemon, services => services.Replace(
            ServiceDescriptor.Singleton(new AdmissionController(
                // ~94% used — over AdmissionController.DefaultUsedThreshold.
                sampler: () => new MemorySample(MemTotalKb: 16_000_000, MemAvailableKb: 1_000_000)))));
        var spawns = rig.Host.Services.GetRequiredService<AgentSpawnService>();

        var coord = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);

        var refused = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "work"), coord, default);
        Assert.False(refused.Ok);
        Assert.Contains("memory", refused.Error!, StringComparison.OrdinalIgnoreCase);

        var list = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("list"), coord, default);
        Assert.True(list.Ok);
        Assert.Empty(list.Agents!);
    }

    [Fact]
    public async Task ShimList_ForACoordinatorWithNoWorkers_IsEmpty_NotEveryAgent()
    {
        using var rig = WiringRig.Create(_daemon);
        var spawns = rig.Host.Services.GetRequiredService<AgentSpawnService>();

        // Another coordinator with a worker exists on this daemon...
        var other = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);
        await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "work"), other, default);

        // ...but a coordinator that has spawned nothing must see nothing (previously it saw everything).
        var idle = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);
        var list = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("list"), idle, default);

        Assert.True(list.Ok);
        Assert.Empty(list.Agents!);
    }

    [Fact]
    public async Task ShimSpawn_AtWorkerCap_IsRefused()
    {
        // A small cap so the test fills it quickly; the base graph enforces the real default (6).
        using var rig = WiringRig.Create(
            _daemon,
            configureServices: s => s.AddSingleton(
                new Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits(MaxActiveWorkers: 2)));
        var spawns = rig.Host.Services.GetRequiredService<AgentSpawnService>();
        var store = rig.Host.Services.GetRequiredService<AgentSessionStore>();

        var coordinatorId = await spawns.SpawnAsync(RepoHandle, "claude-code", null, AgentRoles.Coordinator, default);

        // Fill the cap: two managed workers admitted through the wired shim path.
        for (var i = 0; i < 2; i++)
        {
            var ok = await spawns.HandleShimRequestAsync(
                new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "work"),
                coordinatorId, default);
            Assert.True(ok.Ok, ok.Error);
        }

        Assert.Equal(2, store.List().Count(s => s.Role == AgentRoles.Managed));

        // The third is refused by the server-side cap — and no worker record leaks.
        var refused = await spawns.HandleShimRequestAsync(
            new Mainguard.Agents.Agents.Ipc.AgentIpcRequest("spawn", "claude-code", "too much"),
            coordinatorId, default);
        Assert.False(refused.Ok);
        Assert.Contains("cap", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.List().Count(s => s.Role == AgentRoles.Managed));
    }

    // MG-5: the OSC 52 clipboard gate only exists in production if the REAL daemon graph hands the
    // binder the lock registry. (This audit is full of "constructed only in tests" gaps — assert the
    // wiring, not just the behavior.) The binder is DI-constructed, so this pins that resolution.
    [Fact]
    public void ProductionGraph_HandsTheBinderTheTerminalLockRegistry()
    {
        var binder = _daemon.Services.GetRequiredService<AgentCliBinder>();
        var locks = typeof(AgentCliBinder)
            .GetField("_locks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(binder);

        Assert.NotNull(locks);
        Assert.Same(_daemon.Services.GetRequiredService<TerminalLockRegistry>(), locks);
    }

    // ---- rig --------------------------------------------------------------

    private static async Task<string> ShimRoundTripAsync(string ipcDir, string requestJson)
    {
        using var client = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(
            Path.Combine(ipcDir, Mainguard.Agents.Agents.Ipc.AgentIpcPaths.SocketFileName)));
        await using var stream = new System.Net.Sockets.NetworkStream(client);
        var bytes = Encoding.UTF8.GetBytes(requestJson + "\n");
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return (await reader.ReadLineAsync())!;
    }

    private static bool IsOnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, binary)));

    private static async Task<string> ReadUntilAsync(
        AsyncDuplexStreamingCall<TerminalInput, TerminalOutput> call,
        Func<string, bool> until,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            while (await call.ResponseStream.MoveNext(ct))
            {
                var output = call.ResponseStream.Current;
                if (output.FrameCase == TerminalOutput.FrameOneofCase.Raw)
                {
                    sb.Append(Encoding.UTF8.GetString(output.Raw.ToByteArray()));
                    if (until(sb.ToString()))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException)
        {
        }

        return sb.ToString();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    /// <summary>The per-test in-proc daemon with the fake substrate, catalog, and CLI factory.</summary>
    private sealed class WiringRig : IDisposable
    {
        private readonly string _tempRoot;
        private readonly List<CancellationTokenSource> _ctss = new();

        public required Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host { get; init; }
        public required FakeSandboxEngine Engine { get; init; }
        public required ConcurrentDictionary<string, FakeTerminalSession> Sessions { get; init; }
        public AgentCliLaunchSpec? LastSpec { get; set; }

        private WiringRig(string tempRoot) => _tempRoot = tempRoot;

        public GrpcChannel Channel => GrpcChannel.ForAddress(
            Host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Host.Server.CreateHandler() });

        public Metadata Auth => new()
        {
            { "authorization", $"bearer {Host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };

        public TerminalSessionManager Terminals => Host.Services.GetRequiredService<TerminalSessionManager>();

        public AsyncDuplexStreamingCall<TerminalInput, TerminalOutput> Attach(
            string agentId, out CancellationTokenSource cts)
        {
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _ctss.Add(cts);
            var client = new TerminalService.TerminalServiceClient(Channel);
            var call = client.Attach(Auth, cancellationToken: cts.Token);
            call.RequestStream.WriteAsync(new TerminalInput { AgentId = agentId }).GetAwaiter().GetResult();
            return call;
        }

        public static WiringRig Create(
            DaemonFixture daemon, Action<IServiceCollection>? configureServices = null)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "gl-cliwire-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(tempRoot, "repos", RepoHandle)); // "provisioned"

            // One installed CLI in a temp registry — the marker shape the VM installer writes.
            var registry = Path.Combine(tempRoot, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(Path.Combine(registry, "claude-code.json"), InstalledAdapterMarker.Serialize(
                new InstalledAdapterMarker("claude-code", "2.1.0",
                    new[] { "claude", "--permission-mode", "plan" }, "ANTHROPIC_API_KEY")));

            var engine = new FakeSandboxEngine();
            var sessions = new ConcurrentDictionary<string, FakeTerminalSession>(StringComparer.Ordinal);

            WiringRig rig = null!;
            var host = daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new FakeAgentEnvironment(tempRoot, engine));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
                services.AddSingleton(sp => new AgentCliBinder(
                    sp.GetRequiredService<TerminalSessionManager>(),
                    sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                    sp.GetRequiredService<AgentSessionStore>(),
                    sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(),
                    spec =>
                    {
                        rig.LastSpec = spec;
                        var session = new FakeTerminalSession();
                        sessions[spec.AgentId] = session;
                        return session;
                    }));
                configureServices?.Invoke(services);
            }));

            rig = new WiringRig(tempRoot) { Host = host, Engine = engine, Sessions = sessions };
            return rig;
        }

        public void Dispose()
        {
            foreach (var cts in _ctss)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch
                {
                }
            }

            Host.Dispose();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    // ---- fakes -------------------------------------------------------------

    /// <summary>A substrate whose repos/worktrees are temp dirs and whose jail is a recorded no-op.</summary>
    private sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly string _root;

        public FakeAgentEnvironment(string root, FakeSandboxEngine engine)
        {
            _root = root;
            Sandboxes = engine;
            Repos = new FakeProvisioner(root);
            Worktrees = new FakeWorktrees(root);
        }

        public string SubstrateId => "fake";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress { get; } = new FakeEgress();

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        private sealed class FakeProvisioner : IRepoProvisioner
        {
            private readonly string _root;

            public FakeProvisioner(string root) => _root = root;

            public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                throw new NotSupportedException("not exercised");

            public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
        }

        private sealed class FakeWorktrees : IAgentWorktreeManager
        {
            private readonly string _root;

            public FakeWorktrees(string root) => _root = root;

            public string CreateAgentWorktree(string repoHash, string agentId)
            {
                var path = Path.Combine(_root, "wt", repoHash, agentId);
                Directory.CreateDirectory(path);
                return path;
            }

            public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
            {
                try
                {
                    Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            public void Prune(string repoHash)
            {
            }

            public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                Array.Empty<Mainguard.Git.Models.WorktreeItem>();
        }

        private sealed class FakeEgress : IEgressPolicy
        {
            public EgressAllowlist Allowlist { get; } = EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
        }
    }

    /// <summary>Records spawn requests; containers are strings, never Docker.</summary>
    private sealed class FakeSandboxEngine : ISandboxEngine
    {
        public List<SandboxSpawnRequest> Requests { get; } = new();

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));
        }

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A scripted CLI behind the <see cref="ITerminalSession"/> seam: the test emits output
    /// and reads what the daemon wrote as input; resize and kill are recorded.</summary>
    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly Pipe _output = new(); // CLI → daemon
        private readonly Pipe _input = new();  // daemon → CLI
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stream _inputReader;

        public FakeTerminalSession()
        {
            IO = new DuplexStream(_output.Reader.AsStream(), _input.Writer.AsStream());
            _inputReader = _input.Reader.AsStream();
        }

        public Stream IO { get; }

        public Task<int> ExitCode => _exit.Task;

        public (int Cols, int Rows)? LastResize { get; private set; }

        public bool Killed { get; private set; }

        public void Resize(int cols, int rows) => LastResize = (cols, rows);

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(137);
            _output.Writer.Complete();
        }

        public void Dispose() => Kill();

        public async Task EmitAsync(string text)
        {
            await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _output.Writer.FlushAsync();
        }

        public async Task<string> ReadInputAsync(int length)
        {
            var buffer = new byte[length];
            var read = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (read < length)
            {
                var n = await _inputReader.ReadAsync(buffer.AsMemory(read, length - read), cts.Token);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        private sealed class DuplexStream : Stream
        {
            private readonly Stream _read;
            private readonly Stream _write;

            public DuplexStream(Stream read, Stream write)
            {
                _read = read;
                _write = write;
            }

            public override bool CanRead => true;

            public override bool CanWrite => true;

            public override bool CanSeek => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                _read.ReadAsync(buffer, ct);

            public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
                _write.WriteAsync(buffer, ct);

            public override void Flush() => _write.Flush();

            public override Task FlushAsync(CancellationToken ct) => _write.FlushAsync(ct);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
