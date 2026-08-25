using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for the DEV-ONLY <see cref="QueueSeedingService"/> (docs/design/queue-seeding.md).
/// Validation + dispatch only — every state walk, every legality check and every git operation lives
/// in <see cref="QueueSeeder"/>, which drives the real <c>MergeQueue</c> transitions.
///
/// <para><b>Three gates stand in front of this handler</b>: the daemon maps the service only when the
/// boot flag was set (disabled ⇒ UNIMPLEMENTED — the primary); <see cref="SeedingGateInterceptor"/>
/// prefix-denies the service when disabled (the belt); and every method is on
/// <see cref="RoleInterceptor"/>'s coordinator-denied list unconditionally. The check in the
/// constructor is the last-resort brace behind all three: this type refuses to even construct on a
/// daemon that was not started for seeding.</para>
/// </summary>
public sealed class QueueSeedingGrpcService : QueueSeedingService.QueueSeedingServiceBase
{
    private readonly QueueSeeder _seeder;
    private readonly IApproverIdentityResolver _identity;
    private readonly ILogger _log;

    public QueueSeedingGrpcService(
        QueueSeeder seeder, QueueSeedingOptions options, IApproverIdentityResolver identity,
        ILoggerFactory loggerFactory)
    {
        if (options is null || !options.Enabled)
        {
            throw new InvalidOperationException(
                "QueueSeedingGrpcService constructed on a daemon without the queue-seeding boot flag — "
                + "the service must never be mapped in that configuration.");
        }

        _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Merge);
    }

    public override async Task<SeedQueueEntriesResponse> SeedQueueEntries(
        SeedQueueEntriesRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || request.Entries.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "repo_handle and at least one entry are required."));
        }

        var specs = request.Entries.Select(ToSpec).ToList();
        var actor = _identity.Resolve(context);

        SeedBatchReport report;
        try
        {
            report = await _seeder.SeedAsync(request.RepoHandle, specs, actor, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Mainguard.Git.Exceptions.RepoProvisioningException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }

        _log.LogWarning(
            "SeedQueueEntries repo={Repo} by={By} entries={Count} refused={Refused}",
            request.RepoHandle, actor, report.Results.Count,
            report.Results.Count(r => r.Refusal.Length > 0));

        var response = new SeedQueueEntriesResponse
        {
            MainSha = report.MainSha,
            ProvisionedVerifyConfig = report.ProvisionedVerifyConfig,
        };
        response.Results.AddRange(report.Results.Select(r => new SeedResult
        {
            AgentId = r.AgentId,
            ReachedState = r.ReachedState,
            Refusal = r.Refusal,
        }));
        return response;
    }

    public override Task<PushCommitsResponse> PushCommits(PushCommitsRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        var report = _seeder.PushCommits(request.RepoHandle, request.AgentId, request.Count);
        _log.Log(report.Pushed ? LogLevel.Information : LogLevel.Warning,
            "Seeding PushCommits repo={Repo} agent={Agent} pushed={Pushed} {Refusal}",
            request.RepoHandle, request.AgentId, report.Pushed, report.Refusal);

        return Task.FromResult(new PushCommitsResponse
        {
            Pushed = report.Pushed,
            Refusal = report.Refusal,
            NewTipSha = report.NewTipSha,
            State = report.State,
        });
    }

    public override async Task<ClearSeededEntriesResponse> ClearSeededEntries(
        ClearSeededEntriesRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle is required."));
        }

        var actor = _identity.Resolve(context);
        var report = await _seeder.ClearAsync(request.RepoHandle, actor).ConfigureAwait(false);
        _log.LogWarning("ClearSeededEntries repo={Repo} by={By} cleared={Cleared} failed={Failed}",
            request.RepoHandle, actor, report.Cleared.Count, report.Failures.Count);

        var response = new ClearSeededEntriesResponse();
        response.ClearedAgentIds.AddRange(report.Cleared);
        response.Failures.AddRange(report.Failures.Select(f => new SeedResult
        {
            AgentId = f.AgentId,
            ReachedState = f.ReachedState,
            Refusal = f.Refusal,
        }));
        return response;
    }

    public override Task<GetSeedingStatusResponse> GetSeedingStatus(
        GetSeedingStatusRequest request, ServerCallContext context)
    {
        var response = new GetSeedingStatusResponse { Enabled = true };
        response.SeededEntries.AddRange(
            _seeder.SeededEntries().Select(e => $"{e.RepoHash}/{e.AgentId}"));
        return Task.FromResult(response);
    }

    private static SeedSpec ToSpec(SeedEntrySpec spec)
    {
        if (!Enum.TryParse<WorkerMergeState>(spec.TargetState, ignoreCase: true, out var target))
        {
            // Reported per-entry downstream would be kinder, but an unparseable TARGET is a caller
            // bug, not a state-walk refusal — the typed InvalidArgument names the vocabulary.
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"target_state '{spec.TargetState}' is not a WorkerMergeState "
                + $"({string.Join("|", Enum.GetNames<WorkerMergeState>())})."));
        }

        var flavor = spec.Flavor.ToUpperInvariant() switch
        {
            "" or "PLAIN" => SeedFlavor.Plain,
            "FLAGGED" => SeedFlavor.Flagged,
            "CHANGED_TEST_COMMAND" or "CHANGEDTESTCOMMAND" => SeedFlavor.ChangedTestCommand,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"flavor '{spec.Flavor}' is not one of PLAIN|FLAGGED|CHANGED_TEST_COMMAND.")),
        };

        var stale = spec.StaleBehavior.ToUpperInvariant() switch
        {
            "" or "HOLD" => SyntheticStaleBehavior.Hold,
            "CASCADE" => SyntheticStaleBehavior.Cascade,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"stale_behavior '{spec.StaleBehavior}' is not one of HOLD|CASCADE.")),
        };

        return new SeedSpec(
            TargetState: target,
            Count: spec.Count,
            Flavor: flavor,
            VerificationFails: spec.VerificationFails,
            HoldSeconds: spec.HoldSeconds,
            StaleBehavior: stale,
            Reason: spec.Reason ?? string.Empty,
            WithPlan: spec.WithPlan,
            // Null, not an empty list: "no scope was named" is what selects the seed's own path as the
            // approved scope, whereas an empty TaskPlan.Scope puts EVERY file out of scope.
            Scope: spec.Scope.Count > 0 ? spec.Scope.ToList() : null);
    }
}
