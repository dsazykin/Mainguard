using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="KillSwitchService"/> (P2-14). Validation + dispatch only — the
/// freeze-first ordering (SA-1/F4), the RT-D4 hard-ceiling fan-out timing, the journal snapshot, and the
/// RT-D3 audit-gap discipline all live in the daemon-side <see cref="KillSwitch"/>.
/// </summary>
public sealed class KillSwitchGrpcService : KillSwitchService.KillSwitchServiceBase
{
    private readonly KillSwitch _killSwitch;
    private readonly ILogger _log;

    public KillSwitchGrpcService(KillSwitch killSwitch, ILoggerFactory loggerFactory)
    {
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.KillSwitch);
    }

    public override async Task<EngageKillResponse> Engage(EngageKillRequest request, ServerCallContext context)
    {
        _log.LogWarning("Engage: freezing everything (kill switch requested)");
        var report = await _killSwitch.EngageAsync(context.CancellationToken).ConfigureAwait(false);
        var yielded = report.Agents.Count(a => a.Outcome == KillAgentOutcome.Yielded);
        var paused = report.Agents.Count(a => a.Outcome is KillAgentOutcome.Paused or KillAgentOutcome.PauseFailed);
        _log.LogWarning(
            "Engaged: epoch={Epoch} queueFrozen={Frozen} yielded={Yielded} paused={Paused} deadline={Deadline}s",
            report.KillEpochId, report.QueueFrozen, yielded, paused, report.Deadline.TotalSeconds);
        return new EngageKillResponse
        {
            KillEpochId = report.KillEpochId,
            QueueFrozen = report.QueueFrozen,
            AgentsYielded = yielded,
            AgentsPaused = paused,
            DeadlineSeconds = report.Deadline.TotalSeconds,
        };
    }

    public override async Task<ResumeKillResponse> Resume(ResumeKillRequest request, ServerCallContext context)
    {
        var report = await _killSwitch.ResumeAsync(context.CancellationToken).ConfigureAwait(false);
        var resumed = report.Agents.Count(a => a.Outcome == KillResumeOutcome.Resumed);
        var failed = report.Agents.Count(a => a.Outcome == KillResumeOutcome.ResumeFailed);

        // Logged at Warning when anything is still frozen: "Resume: unfrozen" was previously the daemon's
        // ONLY word about a release that had not un-paused a single jail (ISSUES-LOG #17).
        if (failed > 0)
        {
            _log.LogWarning(
                "Resume: queue unfrozen but {Failed} agent(s) could NOT be unpaused (epoch={Epoch}, resumed={Resumed})",
                failed, report.KillEpochId, resumed);
        }
        else
        {
            _log.LogInformation("Resume: unfrozen — {Resumed} agent(s) released (epoch={Epoch})",
                resumed, report.KillEpochId);
        }

        return new ResumeKillResponse
        {
            // The freeze is always cleared; `resumed` now means the WHOLE release succeeded, jails included.
            Resumed = failed == 0,
            QueueUnfrozen = !report.QueueFrozen,
            AgentsResumed = resumed,
            AgentsResumeFailed = failed,
        };
    }
}
