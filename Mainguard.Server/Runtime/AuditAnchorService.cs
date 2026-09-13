using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// P2-15 step 3 (external anchoring), hosted: hourly, enqueue the chain head when the policy says
/// one is due (every 1000 records / 24 h — <see cref="AuditAnchorQueue"/>) and retry every pending
/// anchor against the TSA. Best-effort BY CONTRACT: an unreachable TSA leaves rows pending and the
/// chain never waits (edge row 4). The TSA endpoint comes from <c>MAINGUARD_TSA_URL</c>; unset —
/// the default — means heads still QUEUE by policy but nothing is sent, so an operator who
/// configures a TSA later gets the backlog anchored on the next sweep, and no default install
/// silently talks to a third party.
/// </summary>
public sealed class AuditAnchorService : BackgroundService
{
    /// <summary>Environment variable naming the RFC 3161 endpoint (e.g. an internal TSA).</summary>
    public const string TsaUrlVariable = "MAINGUARD_TSA_URL";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public AuditAnchorService(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _log = loggerFactory.CreateLogger(DaemonLogCategories.Lifecycle);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var chained = _services.GetService<IChainedAuditLog>();
        var queue = _services.GetService<AuditAnchorQueue>();
        if (chained is null || queue is null)
        {
            return; // in-memory journal — nothing durable to anchor
        }

        var tsaUrl = Environment.GetEnvironmentVariable(TsaUrlVariable);
        using var client = Uri.TryCreate(tsaUrl, UriKind.Absolute, out var uri)
            ? new Rfc3161TimestampClient(uri)
            : null;

        // F64c — the off-by-default posture is a DECISION, not an oversight, so it is stated once at
        // boot rather than being inferable only from a null client. Anchoring is the one part of the
        // audit chain that leaves the machine: it POSTs a hash of the chain head to a third-party
        // timestamp authority. Turning that on by default would make every install phone a TSA
        // (chosen by us, not by the operator) without being asked, and it would leak the fact and
        // cadence of a user's activity to that party. So the daemon queues heads unconditionally —
        // no backlog is lost — and sends nothing until an operator names an endpoint. An install
        // WITHOUT a TSA still has a tamper-evident chain and the file-mirror witness; what it lacks
        // is proof of the time a head existed, which is exactly the claim only a third party can make.
        if (client is null)
        {
            _log.LogInformation(
                "audit anchor: RFC 3161 timestamping is OFF (no {Variable}) — by design, so no install "
                + "contacts a third-party TSA unasked. Heads still queue by policy; set {Variable} to an "
                + "RFC 3161 endpoint and the backlog anchors on the next hourly sweep.",
                TsaUrlVariable, TsaUrlVariable);
        }
        else
        {
            _log.LogInformation("audit anchor: RFC 3161 timestamping ON via {Variable}", TsaUrlVariable);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (queue.EnqueueIfDue(chained))
                {
                    _log.LogInformation("audit anchor: head enqueued for RFC 3161 timestamping");
                }

                if (client is not null)
                {
                    var (anchored, pending) = await queue.ProcessPendingAsync(client, stoppingToken)
                        .ConfigureAwait(false);
                    if (anchored > 0 || pending > 0)
                    {
                        _log.LogInformation(
                            "audit anchor sweep: {Anchored} anchored, {Pending} still pending (TSA retried next sweep)",
                            anchored, pending);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anchoring is best-effort; the chain never depends on it. Next sweep retries.
                _log.LogWarning(ex, "audit anchor sweep failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
