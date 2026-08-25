using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The production <see cref="IQueueSeedingGateway"/> over <see cref="DaemonClient"/> — the same
/// factory shape as the terminal/egress/intake gateways on <c>DaemonBackedOrchestrator</c>. The repo
/// handle is resolved per call from the orchestrator's current handle, so the panel always seeds the
/// repo the rail is showing; no open repo is a verbatim refusal, not a fault.
/// </summary>
public sealed class DaemonQueueSeedingGateway : IQueueSeedingGateway
{
    private readonly DaemonClient _client;
    private readonly Func<string?> _repoHandle;

    public DaemonQueueSeedingGateway(DaemonClient client, Func<string?> repoHandle)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _repoHandle = repoHandle ?? throw new ArgumentNullException(nameof(repoHandle));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _client.GetSeedingStatusAsync(ct, deadline: TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            return status.Enabled;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented or StatusCode.PermissionDenied)
        {
            // The one expected shape of "no": the daemon was not started as a seeding daemon (or this
            // credential may not seed). Anything else — unreachable daemon, bad channel — propagates,
            // because "seeding is off" and "the daemon is down" must not render the same.
            return false;
        }
    }

    public async Task<SeedBatchResult> SeedAsync(
        IReadOnlyList<SeedEntryRequestItem> entries, CancellationToken ct = default)
    {
        var request = new SeedQueueEntriesRequest { RepoHandle = RequireRepo() };
        request.Entries.AddRange(entries.Select(e => new SeedEntrySpec
        {
            TargetState = e.TargetState,
            Count = e.Count,
            Flavor = e.Flavor,
            VerificationFails = e.VerificationFails,
            HoldSeconds = e.HoldSeconds,
            StaleBehavior = e.StaleBehavior,
            Reason = e.Reason,
        }));

        // Seeding a Merged/StaleVerified spec does real git work; give the batch room.
        var response = await _client.SeedQueueEntriesAsync(request, ct, deadline: TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);
        return new SeedBatchResult(
            response.Results.Select(r => new SeedResultItem(r.AgentId, r.ReachedState, r.Refusal)).ToList(),
            response.MainSha,
            response.ProvisionedVerifyConfig);
    }

    public async Task<SeedResultItem> PushCommitsAsync(
        string agentId, int count = 1, CancellationToken ct = default)
    {
        var response = await _client.PushSeedCommitsAsync(
            RequireRepo(), agentId, count, ct, deadline: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return new SeedResultItem(agentId, response.State, response.Refusal);
    }

    public async Task<(IReadOnlyList<string> Cleared, IReadOnlyList<SeedResultItem> Failures)> ClearAsync(
        CancellationToken ct = default)
    {
        var response = await _client.ClearSeededEntriesAsync(
            RequireRepo(), ct, deadline: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return (response.ClearedAgentIds.ToList(),
            response.Failures.Select(f => new SeedResultItem(f.AgentId, f.ReachedState, f.Refusal)).ToList());
    }

    private string RequireRepo() => _repoHandle()
        ?? throw new InvalidOperationException("No repository is open — open one before seeding its queue.");
}
