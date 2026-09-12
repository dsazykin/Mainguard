using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;
using Mainguard.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The open leases a retention sweep must not tombstone evidence for, split BY KIND: a lease id is
/// only a lease id, and an agent id is only an agent id. Keeping them apart is what lets the hold
/// predicate compare a payload's <c>lease</c> field against lease ids and its <c>agent</c> field
/// against agent ids, instead of asking whether either string happens to occur anywhere in the
/// envelope text.
/// </summary>
internal sealed record LeaseReferences(
    IReadOnlyCollection<string> LeaseIds, IReadOnlyCollection<string> AgentIds)
{
    internal bool IsEmpty => LeaseIds.Count == 0 && AgentIds.Count == 0;

    /// <summary>How many distinct open leases this stands for, for the log line.</summary>
    internal int Count => LeaseIds.Count == 0 ? AgentIds.Count : LeaseIds.Count;
}

/// <summary>
/// P2-15 retention: once at boot and every 24 h, expire audit records older than 90 days — as
/// chained REDACTION events (payload tombstoned, row count unchanged, chain verifiable), never
/// deletion; the schema's triggers would refuse a delete anyway. A no-op when the daemon runs on
/// the in-memory journal (nothing persisted → nothing to expire).
///
/// <para><b>Open leases hold their evidence (F64b).</b> Retention used to redact purely by age, with
/// no reference check: an RT-D1 merge lease that is still outstanding — a merge in flight, or one
/// stranded by a crash and waiting for the boot reconcile — could have the very audit records that
/// describe it tombstoned out from under it. So the sweep first asks
/// <see cref="IMergeLeaseStore.AllOutstanding"/> which leases are open, and holds back every expired
/// record whose canonical envelope names one of them. Held records are not lost, only deferred: the
/// next sweep after the lease is confirmed or released expires them normally, and the count is logged
/// so a lease stuck open forever is visible rather than silent.</para>
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    /// <summary>The default retention window (master doc §P2-15: "retention default 90 d").</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Chain records read per page while walking for expiry.</summary>
    private const int ScanPageSize = 200;

    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public AuditRetentionService(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _log = loggerFactory.CreateLogger(DaemonLogCategories.Lifecycle);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var chained = _services.GetService<IChainedAuditLog>();
        if (chained is null)
        {
            return; // in-memory journal — dies with the process, retention is meaningless
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep(chained, _services.GetService<IMergeLeaseStore>());
            }
            catch (Exception ex)
            {
                // Retention failing must never take the daemon down; the next sweep retries.
                _log.LogWarning(ex, "audit retention sweep failed: {Message}", ex.Message);
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

    /// <summary>One retention pass. Internal so the lease-hold behaviour is directly testable
    /// without standing up a hosted service and waiting 24 h.</summary>
    internal void Sweep(IChainedAuditLog chained, IMergeLeaseStore? leases)
    {
        var references = References(leases?.AllOutstanding());
        if (references.IsEmpty)
        {
            // Nothing in flight: the store's own age sweep, unchanged.
            var expired = chained.ApplyRetention(RetentionPeriod);
            if (expired > 0)
            {
                _log.LogInformation("audit retention: {Count} record(s) redacted (>{Days}d)",
                    expired, RetentionPeriod.TotalDays);
            }

            return;
        }

        var (redacted, held) = SweepHoldingLeases(chained, references, DateTimeOffset.UtcNow - RetentionPeriod);
        if (redacted > 0 || held > 0)
        {
            _log.LogInformation(
                "audit retention: {Count} record(s) redacted (>{Days}d), {Held} held by {Leases} open merge lease(s)",
                redacted, RetentionPeriod.TotalDays, held, references.Count);
        }
    }

    /// <summary>
    /// The identifiers an open lease is recognised by inside an audit envelope: the lease id and the
    /// agent whose branch the lease authorizes. Not the repo hash — every record for a repository
    /// mentions that, so holding on it would stop retention for the whole repo the moment one merge
    /// started, which is a retention outage dressed up as a safety check.
    /// </summary>
    internal static LeaseReferences References(IReadOnlyList<MergeLeaseRow>? open)
    {
        var leaseIds = new HashSet<string>(StringComparer.Ordinal);
        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        if (open is null)
        {
            return new LeaseReferences(leaseIds, agentIds);
        }

        foreach (var lease in open)
        {
            if (lease.Confirmed)
            {
                continue; // AllOutstanding already filters these; belt and braces.
            }

            if (!string.IsNullOrEmpty(lease.LeaseId))
            {
                leaseIds.Add(lease.LeaseId);
            }

            if (!string.IsNullOrEmpty(lease.AgentId))
            {
                agentIds.Add(lease.AgentId);
            }
        }

        return new LeaseReferences(leaseIds, agentIds);
    }

    /// <summary>The payload keys that carry an AGENT identity. Two spellings because both are in the
    /// store: the merge-queue events write <c>agent</c>, the newer plan/worker events <c>agent_id</c>.</summary>
    private static readonly HashSet<string> AgentFields =
        new(StringComparer.Ordinal) { "agent", "agent_id", "worker", "worker_id" };

    /// <summary>The payload keys that carry a MERGE LEASE id, same two spellings.</summary>
    private static readonly HashSet<string> LeaseFields =
        new(StringComparer.Ordinal) { "lease", "lease_id" };

    /// <summary>
    /// True when this record's canonical envelope names an open lease in an IDENTITY field, i.e. it is
    /// evidence for an operation that has not finished and must not be tombstoned yet.
    ///
    /// <para>This used to be <c>payloadJson.Contains(reference)</c> over the whole envelope, which is
    /// not a predicate about leases at all — it is a substring search. Agent ids as short as
    /// <c>a1</c> or <c>pr-42</c> are valid, every envelope is full of hex shas and timestamps, and
    /// <c>a1</c> occurs in roughly every other sha: while one such lease was open, nearly every
    /// expired record in the store read as "held" and retention quietly stopped. Matching the parsed
    /// <c>agent</c>/<c>lease</c> fields against the right kind of id makes the predicate mean what
    /// its name says.</para>
    /// </summary>
    internal static bool IsHeldByOpenLease(string payloadJson, LeaseReferences references)
    {
        if (string.IsNullOrEmpty(payloadJson) || references.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // What a record surfaces is the canonical ENVELOPE
            // `{identity, payload, seq, timestamp, type}` — the caller's fields are the `payload`
            // object, not the root. (A bare field object is accepted too, which is the shape the
            // pure-predicate tests use.) `identity` is deliberately not searched: it is the OS user
            // who acted, never an agent or lease id.
            var fields = document.RootElement.TryGetProperty("payload", out var payload)
                         && payload.ValueKind == JsonValueKind.Object
                ? payload
                : document.RootElement;

            foreach (var property in fields.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if ((AgentFields.Contains(property.Name) && references.AgentIds.Contains(value))
                    || (LeaseFields.Contains(property.Name) && references.LeaseIds.Contains(value)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            // A payload that will not parse is a record we cannot reason about. Redaction is
            // irreversible and this branch only defers it while some lease is open, so the safe
            // reading is "held" — evidence kept over evidence destroyed.
            return true;
        }
    }

    /// <summary>
    /// The lease-aware expiry walk, used only while a lease is open. Pages the chain up to the head
    /// captured at entry (redactions append new records, and a fresh redaction event is never itself
    /// expired), redacting each expired record that no open lease claims.
    /// </summary>
    private static (int Redacted, int Held) SweepHoldingLeases(
        IChainedAuditLog chained, LeaseReferences references, DateTimeOffset cutoff)
    {
        var headSeq = chained.Head()?.Seq ?? 0;
        var redacted = 0;
        var held = 0;
        var seq = 1L;

        while (seq <= headSeq)
        {
            var page = chained.Read(seq, ScanPageSize);
            if (page.Count == 0)
            {
                break;
            }

            var expiredHere = new List<long>();
            foreach (var record in page)
            {
                seq = record.Seq + 1;
                if (record.Seq > headSeq
                    || record.Type == ChainedAuditLog.RedactionEventType
                    || record.PayloadJson == ChainedAuditLog.RedactedTombstone
                    || record.Timestamp >= cutoff)
                {
                    continue;
                }

                if (IsHeldByOpenLease(record.PayloadJson, references))
                {
                    held++;
                    continue;
                }

                expiredHere.Add(record.Seq);
            }

            // Redact after the page is materialized: Redact appends, and appending while enumerating
            // the same store is how a walk starts chasing its own tail.
            foreach (var expired in expiredHere)
            {
                chained.Redact(expired, "retention-expiry", "daemon");
                redacted++;
            }
        }

        return (redacted, held);
    }
}
