using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The P2-09 leader-reattach boot step. Runs <b>after</b> the P2-08 swarm (container) reconcile so the
/// reconcile order is containers → leaders → PTY reattach (contract §3.4): it lists the live containers
/// and hands them to <see cref="SessionLeader.Reattach"/>, which reattaches surviving PTY sessions and
/// reaps the rest toward Docker truth. Best-effort — a Docker hiccup must never block the daemon.
/// </summary>
public sealed class LeaderReattachTask : IBootTask
{
    /// <summary>The G-17 audit type for one boot leader-reattach pass.</summary>
    public const string ReattachedEvent = "boot_leader_reattach";

    private readonly SessionLeader _leader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> _listContainers;
    private readonly Mainguard.Git.Audit.IAuditLog? _audit;
    private readonly Action<string>? _log;

    /// <param name="leader">The session leader whose registry is reconciled toward Docker truth.</param>
    /// <param name="listContainers">The live container listing this pass reconciles against.</param>
    /// <param name="audit">G-17 sink for the pass's outcome. Optional so existing unit tests are
    /// unchanged; the daemon passes it.</param>
    /// <param name="log">Milestone sink (the daemon's boot log category).</param>
    public LeaderReattachTask(
        SessionLeader leader,
        Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> listContainers,
        Mainguard.Git.Audit.IAuditLog? audit = null,
        Action<string>? log = null)
    {
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _listContainers = listContainers ?? throw new ArgumentNullException(nameof(listContainers));
        _audit = audit;
        _log = log;
    }

    public string Name => "leader-reattach";

    /// <summary>The most recent pass's report — null before the first run.</summary>
    public LeaderReconcileReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var containers = await _listContainers(ct).ConfigureAwait(false);

        // The return value used to be discarded. Reaping a leader session KILLS the agent's PTY and drops
        // it from the durable registry, so a boot pass can silently end every terminal a user left running
        // — and the only record of which ones, and how many survived, was this object.
        var report = _leader.Reattach(containers);
        LastReport = report;

        _log?.Invoke(
            $"leader reattach: reattached={Describe(report.Reattached)} reaped={Describe(report.Reaped)}");

        // Only a pass that reaped something gets an audit entry: a reattach-only pass changed nothing a
        // user would come looking for, and an entry per boot would bury the ones that did.
        if (report.Reaped.Count > 0)
        {
            _audit?.Append(new Mainguard.Git.Audit.AuditEvent(
                ReattachedEvent, new Dictionary<string, string>
                {
                    ["reattached"] = string.Join(",", report.Reattached),
                    ["reaped"] = string.Join(",", report.Reaped),
                    ["when"] = DateTimeOffset.UtcNow.ToString(
                        "O", System.Globalization.CultureInfo.InvariantCulture),
                }));
        }
    }

    private static string Describe(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "none" : string.Join(",", ids);
}
