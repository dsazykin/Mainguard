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
/// </summary>
public sealed class AuditGrpcService : AuditService.AuditServiceBase
{
    /// <summary>Cap on one ReadAudit page — the chain can hold full prompt/output payloads.</summary>
    private const int MaxTake = 500;

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
            for (var i = (int)Math.Min(fromSeq - 1, events.Count); i < events.Count && response.Records.Count < take; i++)
            {
                response.Records.Add(new AuditRecordEntry
                {
                    Seq = i + 1,
                    Timestamp = string.Empty,
                    Type = events[i].Type,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(events[i].Fields),
                    PrevHash = string.Empty,
                    Hash = string.Empty,
                });
            }

            return Task.FromResult(response);
        }

        foreach (var record in _chained.Read(fromSeq, take))
        {
            response.Records.Add(new AuditRecordEntry
            {
                Seq = record.Seq,
                Timestamp = record.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Type = record.Type,
                PayloadJson = record.PayloadJson,
                PrevHash = record.PrevHash,
                Hash = record.Hash,
            });
        }

        return Task.FromResult(response);
    }
}
