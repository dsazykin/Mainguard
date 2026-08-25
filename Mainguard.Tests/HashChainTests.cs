using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-15 items 1–2: the pure chain, property-tested. Seeded randomness (never wall-clock
/// seeds) so a failure replays byte-for-byte.
/// </summary>
public class HashChainTests
{
    private static List<AuditRecord> BuildChain(Random rng, int count, long startSeq = 1)
    {
        var records = new List<AuditRecord>(count);
        var prevHash = HashChain.GenesisHash;
        for (var i = 0; i < count; i++)
        {
            var payload = CanonicalJson.Serialize(new Dictionary<string, string>
            {
                ["op"] = $"op-{rng.Next(1000)}",
                ["agent"] = $"agent-{rng.Next(100)}",
                ["detail"] = new string((char)('a' + rng.Next(26)), rng.Next(1, 40)),
            });
            var seq = startSeq + i;
            var hash = HashChain.ComputeHash(prevHash, payload);
            records.Add(new AuditRecord(seq, DateTimeOffset.UnixEpoch.AddSeconds(seq), "test_event", payload, prevHash, hash));
            prevHash = hash;
        }

        return records;
    }

    [Fact]
    public void ComputeHash_ShouldBeDeterministic_OverCanonicalJson()
    {
        var rng = new Random(4211);
        for (var i = 0; i < 200; i++)
        {
            var payload = CanonicalJson.Serialize(new { n = rng.Next(), s = $"v{rng.Next()}" });
            var prev = HashChain.ComputeHash(HashChain.GenesisHash, $"seed-{i}");
            Assert.Equal(HashChain.ComputeHash(prev, payload), HashChain.ComputeHash(prev, payload));
        }
    }

    [Fact]
    public void ComputeHash_ShouldBeLowercaseHexSha256()
    {
        var hash = HashChain.ComputeHash(HashChain.GenesisHash, "{}");
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
    }

    [Fact]
    public void Verify_ShouldPass_OnValidGeneratedChains()
    {
        var rng = new Random(97);
        for (var round = 0; round < 20; round++)
        {
            var chain = BuildChain(rng, rng.Next(1, 60));
            var (valid, firstBad) = HashChain.Verify(chain);
            Assert.True(valid);
            Assert.Null(firstBad);
        }
    }

    [Fact]
    public void Verify_ShouldPass_OnEmptyChain()
    {
        var (valid, firstBad) = HashChain.Verify(Array.Empty<AuditRecord>());
        Assert.True(valid);
        Assert.Null(firstBad);
    }

    [Fact]
    public void Verify_TamperSweep_ShouldFailAtExactSeq()
    {
        // TI-P2-15 item 2: for a 100-record chain, corrupt every record in every mutable
        // dimension — payload, prevHash, hash — and assert FirstBadSeq lands on the tampered
        // record, every time.
        var chain = BuildChain(new Random(2026), 100);
        for (var index = 0; index < chain.Count; index++)
        {
            foreach (var mutate in new Func<AuditRecord, AuditRecord>[]
            {
                r => r with { PayloadJson = r.PayloadJson + " " },
                r => r with { PrevHash = FlipLastChar(r.PrevHash) },
                r => r with { Hash = FlipLastChar(r.Hash) },
            })
            {
                var tampered = chain.ToList();
                tampered[index] = mutate(tampered[index]);
                var (valid, firstBad) = HashChain.Verify(tampered);
                Assert.False(valid);
                Assert.Equal(chain[index].Seq, firstBad);
            }
        }
    }

    [Fact]
    public void Verify_ShouldFail_OnReorderedRecords()
    {
        var chain = BuildChain(new Random(7), 10);
        (chain[4], chain[5]) = (chain[5], chain[4]);
        var (valid, firstBad) = HashChain.Verify(chain);
        Assert.False(valid);
        Assert.Equal(6, firstBad); // the first out-of-order record encountered is seq 6 at position 4
    }

    [Fact]
    public void Verify_ShouldFail_OnDroppedRecord()
    {
        var chain = BuildChain(new Random(8), 10);
        chain.RemoveAt(4); // drop seq 5 → seq 6 arrives where 5 was expected
        var (valid, firstBad) = HashChain.Verify(chain);
        Assert.False(valid);
        Assert.Equal(6, firstBad);
    }

    [Fact]
    public void Verify_ShouldAnchorOnFirstRecord_ForMidChainSlice()
    {
        var chain = BuildChain(new Random(11), 30);
        var slice = chain.Skip(10).ToList();
        var (valid, firstBad) = HashChain.Verify(slice);
        Assert.True(valid);
        Assert.Null(firstBad);
    }

    [Fact]
    public void Verify_ShouldRequireGenesisAnchor_WhenChainStartsAtSeqOne()
    {
        var chain = BuildChain(new Random(12), 3);
        chain[0] = chain[0] with { PrevHash = FlipLastChar(chain[0].PrevHash) };
        var (valid, firstBad) = HashChain.Verify(chain);
        Assert.False(valid);
        Assert.Equal(1, firstBad);
    }

    private static string FlipLastChar(string hex)
        => hex[..^1] + (hex[^1] == '0' ? '1' : '0');
}
