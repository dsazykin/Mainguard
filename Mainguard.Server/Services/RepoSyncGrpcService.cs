using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Exceptions;
using Mainguard.Protos.V1;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="RepoSyncService"/> (P2-06 bodies). Validation + dispatch only:
/// the actual bare-mirror provision, agent worktrees, and quarantine remotes live in the daemon
/// services held by <see cref="IAgentEnvironment"/>. Only opaque handles cross the wire (G-14) —
/// the repo hash, agent ids, and the Windows-facing <see cref="SyncRemote"/> URL; daemon
/// daemon-side filesystem paths never cross the wire (on WSL they stay in the VM; on macOS they stay the daemon's own).
/// </summary>
public sealed class RepoSyncGrpcService : RepoSyncService.RepoSyncServiceBase
{
    private const char HandleSeparator = ':';

    private readonly IAgentEnvironment _environment;
    private readonly MergeQueueProvisioner _mergeQueues;
    private readonly Runtime.ActiveRepoIndex _activeRepos;

    public RepoSyncGrpcService(
        IAgentEnvironment environment,
        MergeQueueProvisioner mergeQueues,
        Runtime.ActiveRepoIndex activeRepos)
    {
        _environment = environment;
        _mergeQueues = mergeQueues ?? throw new ArgumentNullException(nameof(mergeQueues));
        _activeRepos = activeRepos ?? throw new ArgumentNullException(nameof(activeRepos));
    }

    public override Task<ProvisionRepoResponse> ProvisionRepo(ProvisionRepoRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.OriginUrl))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "origin_url is required."));
        }

        return Task.FromResult(Guard(() =>
        {
            var result = _environment.Repos.Provision(request.OriginUrl);
            var remote = _environment.ResolveSyncRemote(result.RepoHash);

            // MG-10: provisioning is the moment a repo becomes ACTIVE, so it is where the repo's merge
            // queue comes into existence — the registry was previously written by nothing at all, which is
            // why every merge-queue RPC answered NOT_FOUND. Idempotent: a re-provision fetches main forward
            // and this reconciles the existing queue's authoritative main@sha with the mirror's, firing the
            // ordinary stale cascade rather than leaving branches "Verified" against a main that moved.
            _mergeQueues.EnsureQueue(result.RepoHash);

            // …and it is the only moment the daemon ever learns which repository a handle stands for: the
            // hash is one-way. Recorded in the daemon-openable form (the same translation Provision just
            // used) so the external-PR intake can read this repo's origin remote to decide whether a
            // subscribed source belongs to it.
            _activeRepos.Record(result.RepoHash, HostPathTranslator.ToDaemonOpenablePath(request.OriginUrl));

            return new ProvisionRepoResponse
            {
                RepoHandle = result.RepoHash,
                SyncRemoteName = remote.Name,
                SyncRemoteUrl = remote.Url,
            };
        }));
    }

    public override Task<CreateWorktreeResponse> CreateWorktree(CreateWorktreeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        return Task.FromResult(Guard(() =>
        {
            _environment.Worktrees.CreateAgentWorktree(request.RepoHandle, request.AgentId);

            // The agent now has a branch in this repo, so it is a queue member: without an entry the queue
            // tracks no agents and StreamQueue reports an empty repo however many branches exist.
            _mergeQueues.EnsureEntry(request.RepoHandle, request.AgentId, MergeEntryOrigin.Local);

            return new CreateWorktreeResponse
            {
                WorktreeHandle = MakeHandle(request.RepoHandle, request.AgentId),
            };
        }));
    }

    public override Task<ListWorktreesResponse> ListWorktrees(ListWorktreesRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle is required."));
        }

        return Task.FromResult(Guard(() =>
        {
            var response = new ListWorktreesResponse();
            foreach (var item in _environment.Worktrees.List(request.RepoHandle))
            {
                // The main (bare) worktree carries no agent branch; skip it.
                if (item.IsMain || string.IsNullOrEmpty(item.Branch))
                {
                    continue;
                }

                var agentId = System.IO.Path.GetFileName(item.Path.TrimEnd('/', '\\'));
                response.Worktrees.Add(new WorktreeInfo
                {
                    WorktreeHandle = MakeHandle(request.RepoHandle, agentId),
                    AgentId = agentId,
                    Branch = item.Branch,
                });
            }

            return response;
        }));
    }

    public override Task<RemoveWorktreeResponse> RemoveWorktree(RemoveWorktreeRequest request, ServerCallContext context)
    {
        if (!TryParseHandle(request.WorktreeHandle, out var repoHash, out var agentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "worktree_handle is malformed."));
        }

        return Task.FromResult(Guard(() =>
        {
            _environment.Worktrees.RemoveAgentWorktree(repoHash, agentId, force: false);
            return new RemoveWorktreeResponse { Removed = true };
        }));
    }

    private static string MakeHandle(string repoHash, string agentId) => $"{repoHash}{HandleSeparator}{agentId}";

    private static bool TryParseHandle(string handle, out string repoHash, out string agentId)
    {
        repoHash = string.Empty;
        agentId = string.Empty;
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        var idx = handle.IndexOf(HandleSeparator);
        if (idx <= 0 || idx >= handle.Length - 1)
        {
            return false;
        }

        repoHash = handle[..idx];
        agentId = handle[(idx + 1)..];
        return true;
    }

    // Maps the typed domain failures to gRPC status codes; unexpected faults stay Internal.
    private static T Guard<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (AgentWorktreeConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (RepoProvisioningException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
