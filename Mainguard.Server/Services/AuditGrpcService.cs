using System;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="AuditService"/> (P2-15) — the first production READERS of the
/// audit store. Transport only: verification and decryption live in <see cref="IChainedAuditLog"/>.
/// Registered on the coordinator-denied list (<see cref="Auth.RoleInterceptor"/>): the chain carries
/// other agents' prompts and the human's plan/merge decisions, none of which is an agent's to read.
///
/// <para>When the daemon runs on the in-memory fallback journal (no daemon DB), both RPCs still
/// answer — with <c>persistent=false</c>, so a caller can never mistake a heap journal that dies
/// with the process for tamper-evidence.</para>
///
/// <para><b>ReadAudit is doubly bounded (F64a):</b> by record count (<see cref="MaxTake"/>) and by
/// response bytes (<see cref="MaxResponseBytes"/>). The count cap alone said nothing about size —
/// records carry decrypted prompts, verification logs and merge diffs — so a full page could blow
/// past gRPC's 4 MB default receive limit and the caller would get ResourceExhausted instead of a
/// page. A short page is the normal resume signal: read on from the last seq returned.</para>
/// </summary>
public sealed class AuditGrpcService : AuditService.AuditServiceBase
{
    /// <summary>Cap on one ReadAudit page — the chain can hold full prompt/output payloads.</summary>
    private const int MaxTake = 500;

    /// <summary>
    /// Byte budget for one <c>ReadAudit</c> page (F64a). <see cref="MaxTake"/> bounds the record
    /// COUNT, which bounds nothing useful: a chain record carries a decrypted prompt, a verification
    /// log or a merge diff, so 500 of them can be tens of megabytes and gRPC's default 4 MB receive
    /// limit rejects the whole response client-side — the caller gets ResourceExhausted and no page
    /// at all, which is the worst of both worlds. So the page also stops at this budget and returns
    /// the records that fit.
    ///
    /// <para>3 MB leaves headroom under the 4 MB default for the envelope and for the last record
    /// that crosses the line (at least one record is always returned, so a single oversized record
    /// still makes progress rather than stalling the walk forever). A truncated page is not an error
    /// and needs no proto field to say so: pages are already resumed from the last <c>seq</c> the
    /// caller received, exactly as a short page from the end of the chain is.</para>
    /// </summary>
    private const int MaxResponseBytes = 3 * 1024 * 1024;

    private readonly IChainedAuditLog? _chained;
    private readonly IAuditLog _journal;

    public AuditGrpcService(IAuditLog journal, IChainedAuditLog? chained = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _chained = chained;
    }

    public override Task<VerifyAuditResponse> VerifyAudit(VerifyAuditRequest request, ServerCallContext context)
    {
        if (_chained is null)
        {
            // The in-memory journal cannot be tampered ON DISK because it is not on disk — "valid"
            // here states only internal consistency, and persistent=false states the caveat.
            return Task.FromResult(new VerifyAuditResponse
            {
                Valid = true,
                HasFirstBadSeq = false,
                HeadSeq = _journal.Read().Count,
                HeadHash = HashChain.GenesisHash,
                Persistent = false,
            });
        }

        var (valid, firstBad) = _chained.VerifyAll();
        var head = _chained.Head();
        return Task.FromResult(new VerifyAuditResponse
        {
            Valid = valid,
            HasFirstBadSeq = firstBad.HasValue,
            FirstBadSeq = firstBad ?? 0,
            HeadSeq = head?.Seq ?? 0,
            HeadHash = head?.Hash ?? HashChain.GenesisHash,
            Persistent = true,
        });
    }

    public override Task<ReadAuditResponse> ReadAudit(ReadAuditRequest request, ServerCallContext context)
    {
        var take = request.Take <= 0 ? MaxTake : Math.Min(request.Take, MaxTake);
        var fromSeq = Math.Max(request.FromSeq, 1);
        var response = new ReadAuditResponse { Persistent = _chained is not null };

        if (_chained is null)
        {
            // Legacy journal: no seqs/hashes to expose; surface types + fields as the payload so
            // the caller still sees SOMETHING during a DB outage rather than an empty pane.
            var events = _journal.Read();
            var journalBytes = 0;
            for (var i = (int)Math.Min(fromSeq - 1, events.Count); i < events.Count && response.Records.Count < take; i++)
            {
                var entry = new AuditRecordEntry
                {
                    Seq = i + 1,
                    Timestamp = string.Empty,
                    Type = events[i].Type,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(events[i].Fields),
                    PrevHash = string.Empty,
                    Hash = string.Empty,
                };
                if (!TryAdd(response, entry, ref journalBytes))
                {
                    break;
                }
            }

            return Task.FromResult(response);
        }

        var bytes = 0;
        foreach (var record in _chained.Read(fromSeq, take))
        {
            var entry = new AuditRecordEntry
            {
                Seq = record.Seq,
                Timestamp = record.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Type = record.Type,
                PayloadJson = record.PayloadJson,
                PrevHash = record.PrevHash,
                Hash = record.Hash,
            };
            if (!TryAdd(response, entry, ref bytes))
            {
                break;
            }
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Appends <paramref name="entry"/> unless it would push the page past
    /// <see cref="MaxResponseBytes"/>. The first record is always accepted, so one record larger than
    /// the whole budget is still returned (and the caller resumes past it) rather than wedging the
    /// walk at that seq forever.
    /// </summary>
    private static bool TryAdd(ReadAuditResponse response, AuditRecordEntry entry, ref int bytes)
    {
        var size = entry.CalculateSize();
        if (response.Records.Count > 0 && bytes + size > MaxResponseBytes)
        {
            return false;
        }

        bytes += size;
        response.Records.Add(entry);
        return true;
    }
}
