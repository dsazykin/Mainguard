using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Services;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F64a — <c>ReadAudit</c> was bounded by record COUNT (500) and by nothing else. A chain record
/// carries a decrypted prompt, a verification log or a merge diff, so 500 of them can be tens of
/// megabytes: past gRPC's 4 MB default receive limit the caller gets ResourceExhausted and no page
/// at all, which is worse than a short page. The response now also stops at a byte budget.
/// </summary>
public sealed class AuditReadBoundTests
{
    /// <summary>ReadAudit reads nothing off the call context — the page shape is decided entirely by
    /// the request and the store — so the transport is not part of what is under test here.</summary>
    private static ServerCallContext Context() => null!;

    [Fact]
    public async Task ReadAudit_StopsAtTheByteBudget_RatherThanReturningAnUnsendablePage()
    {
        // 500 records × 512 KB = 256 MB if only the count cap applied.
        var payload = new string('p', 512 * 1024);
        var chained = new FatChainStub(records: 500, payloadJson: payload);
        var service = new AuditGrpcService(chained, chained);

        var response = await service.ReadAudit(new ReadAuditRequest { FromSeq = 1, Take = 500 }, Context());

        Assert.True(response.Records.Count < 500, "the page must be cut short by the byte budget");
        Assert.NotEmpty(response.Records);
        Assert.True(response.CalculateSize() < 4 * 1024 * 1024,
            $"the page must fit under gRPC's 4 MB default (was {response.CalculateSize()} bytes)");
    }

    /// <summary>A short page is the resume signal: the caller reads on from the last seq it got.
    /// So the page must be a contiguous prefix, not a sample.</summary>
    [Fact]
    public async Task TheTruncatedPage_IsAContiguousPrefix_SoTheWalkCanResume()
    {
        var chained = new FatChainStub(records: 500, payloadJson: new string('p', 512 * 1024));
        var service = new AuditGrpcService(chained, chained);

        var response = await service.ReadAudit(new ReadAuditRequest { FromSeq = 1, Take = 500 }, Context());

        Assert.Equal(
            Enumerable.Range(1, response.Records.Count).Select(i => (long)i).ToArray(),
            response.Records.Select(r => r.Seq).ToArray());
    }

    /// <summary>One record larger than the whole budget still comes back, so a walk cannot wedge
    /// forever at the seq that will not fit.</summary>
    [Fact]
    public async Task ASingleOversizedRecord_IsStillReturned()
    {
        var chained = new FatChainStub(records: 3, payloadJson: new string('p', 5 * 1024 * 1024));
        var service = new AuditGrpcService(chained, chained);

        var response = await service.ReadAudit(new ReadAuditRequest { FromSeq = 1, Take = 500 }, Context());

        Assert.Single(response.Records);
        Assert.Equal(1, response.Records[0].Seq);
    }

    [Fact]
    public async Task SmallPages_AreUnaffected()
    {
        var chained = new FatChainStub(records: 4, payloadJson: "{\"k\":\"v\"}");
        var service = new AuditGrpcService(chained, chained);

        var response = await service.ReadAudit(new ReadAuditRequest { FromSeq = 1, Take = 500 }, Context());

        Assert.Equal(4, response.Records.Count);
        Assert.True(response.Persistent);
    }

    /// <summary>A chain of identically-sized fat records, enough of them to blow the budget.</summary>
    private sealed class FatChainStub : IChainedAuditLog
    {
        private readonly int _records;
        private readonly string _payloadJson;

        public FatChainStub(int records, string payloadJson)
        {
            _records = records;
            _payloadJson = payloadJson;
        }

        public IReadOnlyList<AuditRecord> Read(long fromSeq, int take)
        {
            var list = new List<AuditRecord>();
            for (var seq = fromSeq; seq <= _records && list.Count < take; seq++)
            {
                list.Add(new AuditRecord(seq, DateTimeOffset.UtcNow, "fat_event", _payloadJson, "prev", "hash"));
            }

            return list;
        }

        public (long Seq, string Hash)? Head() => (_records, "hash");

        public long Append(string type, object payload, string osIdentity) => 0;

        public void Append(AuditEvent auditEvent) { }

        public IReadOnlyList<AuditEvent> Read() => Array.Empty<AuditEvent>();

        public (bool Valid, long? FirstBadSeq) VerifyAll() => (true, null);

        public long Redact(long seq, string reason, string osIdentity) => 0;

        public int ApplyRetention(TimeSpan retention) => 0;
    }
}
