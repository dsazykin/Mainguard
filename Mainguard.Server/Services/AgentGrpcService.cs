using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Git.Exceptions;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="AgentService"/>. Validation + dispatch ONLY — the spawn/stop
/// workflow lives in <see cref="AgentSpawnService"/> (shared with the coordinator's in-jail spawn
/// channel) and state in <see cref="AgentSessionStore"/>, so the behavior is unit-testable without
/// the transport (P2-02 rejection trigger: no business logic in gRPC classes).
/// </summary>
public sealed class AgentGrpcService : AgentService.AgentServiceBase
{
    /// <summary>
    /// How often a subscribed client is sent a fresh CPU/RAM tick.
    ///
    /// <para>Chosen against the floor the engine itself imposes: a one-shot stats call inherently takes
    /// ~1s, because the daemon must collect two readings to produce a CPU delta. Polling at 1-2s would
    /// therefore leave the sampler running essentially continuously for a readout nobody watches that
    /// closely. At 5s the engine is collecting roughly a fifth of the time, and a task-manager-style
    /// readout still updates faster than a human re-reads it. The calls are also driven BY THE
    /// SUBSCRIPTION — with the Resources tab closed and no client attached, the daemon makes no stats
    /// calls at all.</para>
    /// </summary>
    public static readonly System.TimeSpan ResourcePollInterval = System.TimeSpan.FromSeconds(5);

    private readonly AgentSessionStore _store;
    private readonly AgentSpawnService _spawns;
    private readonly InstalledAdapterCatalog _adapters;
    private readonly DaemonInfoProvider _info;
    private readonly AgentResourceProbe _resources;
    private readonly ILogger _log;

    public AgentGrpcService(
        AgentSessionStore store, AgentSpawnService spawns, InstalledAdapterCatalog adapters,
        DaemonInfoProvider info, AgentResourceProbe resources, ILoggerFactory loggerFactory)
    {
        _store = store;
        _spawns = spawns;
        _adapters = adapters;
        _info = info;
        _resources = resources;
        _log = (loggerFactory ?? throw new System.ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Spawn);
    }

    public override async Task<SpawnAgentResponse> SpawnAgent(SpawnAgentRequest request, ServerCallContext context)
    {
        try
        {
            System.Collections.Generic.Dictionary<string, string>? extraEnv = null;
            foreach (var entry in request.ExtraEnv)
            {
                extraEnv ??= new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
                extraEnv[entry.Name] = entry.Value;
            }

            System.Collections.Generic.List<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? cliCredentials = null;
            foreach (var file in request.CliCredentials)
            {
                cliCredentials ??= new System.Collections.Generic.List<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>();
                cliCredentials.Add(new Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile(
                    file.Path, file.Content.ToByteArray()));
            }

            var agentId = await _spawns.SpawnAsync(
                request.RepoHandle, request.AgentKind, request.ModelApiKey, request.Role,
                context.CancellationToken, extraEnv, cliCredentials).ConfigureAwait(false);
            return new SpawnAgentResponse { AgentId = agentId };
        }
        catch (System.ArgumentException ex)
        {
            _log.LogWarning("SpawnAgent rejected (invalid argument): {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (AgentSpawnRefusedException ex)
        {
            _log.LogWarning("SpawnAgent refused (policy): {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (AgentWorktreeConflictException ex)
        {
            _log.LogWarning("SpawnAgent refused (worktree conflict): {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (RepoProvisioningException ex)
        {
            _log.LogError(ex, "SpawnAgent failed (repo provisioning)");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (SandboxImageMissingException ex)
        {
            // The spawn preflight (both jail images) — actionable regardless of whether the
            // agent-base or the egress-proxy image is absent; the raw Docker mapping below
            // remains the belt-and-suspenders path if an image vanishes mid-spawn.
            _log.LogError("SpawnAgent failed (sandbox image missing): {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (ToolchainProvisioningException ex)
        {
            // MG-42: the repo declared a verification toolchain and it could not be put in the jail.
            // FailedPrecondition, named, and never a degraded spawn — a jail without the tools its
            // repo's verify command needs produces verification failures that read like the agent's
            // code is broken, which is the one misreading that decides merges.
            _log.LogError(ex, "SpawnAgent failed (toolchain provisioning): repo={Repo} ids={Ids}",
                ex.RepoHandle, string.Join(",", ex.ToolchainIds));
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (Docker.DotNet.DockerImageNotFoundException ex)
        {
            // Field failure 2026-07-17: the hardened jail image ships via CI/release, and an
            // installed VM without it answered a bare UNKNOWN. Name the real state and the repair.
            _log.LogError(ex, "SpawnAgent failed (docker image not found at container-create)");
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Mainguard OS is missing the agent sandbox image (mainguard-agent-base) — it is "
                + "provisioned by setup; re-run Mainguard setup or rebuild the image, then try again."));
        }
        catch (Docker.DotNet.DockerApiException ex)
        {
            _log.LogError(ex, "SpawnAgent failed (docker api)");
            throw new RpcException(new Status(StatusCode.Internal,
                $"The agent sandbox could not start: {ex.Message}"));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            // Last resort: a raw handler exception reaches the client as a bare UNKNOWN with no
            // detail — always surface the real message instead.
            _log.LogError(ex, "SpawnAgent failed (unexpected)");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<StopAgentResponse> StopAgent(StopAgentRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_id is required."));
        }

        var result = await _spawns.StopAsync(request.AgentId, context.CancellationToken).ConfigureAwait(false);
        var response = new StopAgentResponse { Stopped = result.Stopped, AgentKind = result.AgentKind };
        foreach (var file in result.CliCredentials)
        {
            response.CliCredentials.Add(new CliCredentialFile
            {
                Path = file.HomeRelativePath,
                Content = Google.Protobuf.ByteString.CopyFrom(file.Content),
            });
        }

        return response;
    }

    /// <summary>
    /// Harvests a live agent's CLI login-state WITHOUT stopping it, so the client can keep the host
    /// keychain current while the agent runs. Harvest used to happen only on StopAgent, so a daemon
    /// shutdown or crash lost the login entirely and the user re-authenticated on every launch.
    /// </summary>
    public override async Task<HarvestAgentCredentialsResponse> HarvestAgentCredentials(
        HarvestAgentCredentialsRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_id is required."));
        }

        var result = await _spawns.HarvestCredentialsAsync(request.AgentId, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new HarvestAgentCredentialsResponse { AgentKind = result.AgentKind };
        foreach (var file in result.CliCredentials)
        {
            response.CliCredentials.Add(new CliCredentialFile
            {
                Path = file.HomeRelativePath,
                Content = Google.Protobuf.ByteString.CopyFrom(file.Content),
            });
        }

        return response;
    }

    public override Task<ListAgentsResponse> ListAgents(ListAgentsRequest request, ServerCallContext context)
    {
        var response = new ListAgentsResponse();
        foreach (var session in _store.List())
        {
            response.Agents.Add(new AgentInfo
            {
                AgentId = session.Id,
                AgentKind = session.Kind,
                State = session.State,
                Role = session.Role,
            });
        }

        return Task.FromResult(response);
    }

    public override Task<ListInstalledAdaptersResponse> ListInstalledAdapters(
        ListInstalledAdaptersRequest request, ServerCallContext context)
    {
        // The VM-side registry markers, read fresh per call (installs happen while the daemon runs).
        // Ids/versions/env-var NAMES only — no paths, no secrets (G-14/G-13).
        var response = new ListInstalledAdaptersResponse();
        foreach (var marker in _adapters.List())
        {
            response.Adapters.Add(new InstalledAdapterInfo
            {
                Id = marker.Id,
                Version = marker.Version,
                ApiKeyEnvVar = marker.ApiKeyEnvVar ?? string.Empty,
            });
        }

        return Task.FromResult(response);
    }

    public override Task<GetDaemonInfoResponse> GetDaemonInfo(
        GetDaemonInfoRequest request, ServerCallContext context)
    {
        // The tier-1 skew probe (versions only — no paths, no secrets, G-14). A daemon that
        // predates this RPC answers Unimplemented, which the client treats as the skew signal.
        return Task.FromResult(new GetDaemonInfoResponse
        {
            DaemonVersion = _info.DaemonVersion,
            PayloadVersion = _info.PayloadVersion,
        });
    }

    public override async Task StreamAgentEvents(
        StreamAgentEventsRequest request,
        IServerStreamWriter<AgentEvent> responseStream,
        ServerCallContext context)
    {
        var reader = _store.Subscribe(out var unsubscribe);
        try
        {
            await foreach (var delta in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(Map(delta));
            }
        }
        catch (System.OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
        finally
        {
            unsubscribe();
        }
    }

    public override async Task StreamAgentResources(
        StreamAgentResourcesRequest request,
        IServerStreamWriter<AgentResourcesSnapshot> responseStream,
        ServerCallContext context)
    {
        try
        {
            // Emit immediately, then on the interval: a client that has just opened the Resources tab
            // must not stare at an empty table for a whole poll period.
            while (!context.CancellationToken.IsCancellationRequested)
            {
                var readings = await _resources.ReadAsync(context.CancellationToken).ConfigureAwait(false);
                var snapshot = new AgentResourcesSnapshot();
                foreach (var reading in readings)
                {
                    var row = new AgentResourceReading
                    {
                        AgentId = reading.AgentId,
                        Metered = reading.IsMetered,
                        UnavailableReason = reading.UnavailableReason ?? string.Empty,
                    };
                    // Assigned ONLY when measured. Leaving the optional field unset is what carries
                    // "unknown" across the wire; writing 0 here would recreate the bug this RPC fixes.
                    if (reading.CpuPercent is { } cpu) row.CpuPercent = cpu;
                    if (reading.RamBytes is { } ram) row.MemBytes = ram;
                    snapshot.Agents.Add(row);
                }

                await responseStream.WriteAsync(snapshot).ConfigureAwait(false);
                await Task.Delay(ResourcePollInterval, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
            // Client detached — normal stream teardown, and the sampling stops with it.
        }
    }

    private static AgentEvent Map(AgentDelta delta)
    {
        var evt = new AgentEvent { AgentId = delta.AgentId, Seq = delta.Seq };
        switch (delta.Kind)
        {
            case "snapshot":
                var snapshot = new AgentSnapshot();
                foreach (var entry in delta.Payload.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = entry.Split(':');
                    snapshot.Agents.Add(new AgentInfo
                    {
                        AgentId = parts[0],
                        AgentKind = parts.Length > 1 ? parts[1] : string.Empty,
                        State = parts.Length > 2 ? parts[2] : string.Empty,
                        Role = parts.Length > 3 ? parts[3] : string.Empty,
                    });
                }

                evt.Snapshot = snapshot;
                break;
            case "log":
                evt.Log = new LogLine { Line = delta.Payload };
                break;
            default:
                evt.State = new StateChange { State = delta.Payload, Reason = delta.Reason ?? string.Empty };
                break;
        }

        return evt;
    }
}
