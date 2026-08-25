using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Mainguard.Git.Audit;

/// <summary>
/// One chained audit record (P2-15). <paramref name="PayloadJson"/> is the CANONICAL plaintext
/// payload (<see cref="CanonicalJson"/>) — the hash input — regardless of how the store encrypts
/// it at rest. A redacted record surfaces with a tombstone payload; its stored <paramref name="Hash"/>
/// is then vouched for by the later <c>redaction</c> event rather than recomputed (see
/// <c>ChainedAuditLog.VerifyAll</c>).
/// </summary>
public sealed record AuditRecord(
    long Seq,
    DateTimeOffset Timestamp,
    string Type,
    string PayloadJson,
    string PrevHash,
    string Hash);

/// <summary>
/// The pure P2-15 hash chain: SHA-256 over <c>prevHash ‖ payload</c>, lowercase hex. Pure and
/// dependency-free by contract (master doc §P2-15 — "HashChain pure + property-tested") so the
/// verifier in the CLI, the daemon, and the tests are one function that cannot drift.
/// </summary>
public static class HashChain
{
    /// <summary>The chain origin — <c>PrevHash</c> of the first record (seq 1).</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>SHA-256(prevHash ‖ canonicalPayload), lowercase hex.</summary>
    public static string ComputeHash(string prevHash, string canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        ArgumentNullException.ThrowIfNull(canonicalPayload);

        var input = new byte[Encoding.UTF8.GetByteCount(prevHash) + Encoding.UTF8.GetByteCount(canonicalPayload)];
        var written = Encoding.UTF8.GetBytes(prevHash, input);
        Encoding.UTF8.GetBytes(canonicalPayload, input.AsSpan(written));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>
    /// Walks <paramref name="records"/> (ascending seq) and verifies every link: seq contiguity,
    /// prev-hash linkage, and each record's hash recomputed from its payload. Returns the seq of
    /// the FIRST bad record on failure — the tamper-sweep contract is "fails at exactly that seq".
    ///
    /// <para>A slice that starts mid-chain (seq &gt; 1) anchors on its first record's own
    /// <c>PrevHash</c> — internal linkage is verified, the anchor itself is vouched for by the
    /// records before the slice. A chain starting at seq 1 must anchor on <see cref="GenesisHash"/>.</para>
    /// </summary>
    public static (bool Valid, long? FirstBadSeq) Verify(IEnumerable<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        string? prevHash = null;
        long expectedSeq = -1;
        foreach (var record in records)
        {
            if (expectedSeq == -1)
            {
                expectedSeq = record.Seq;
                prevHash = record.Seq == 1 ? GenesisHash : record.PrevHash;
            }

            if (record.Seq != expectedSeq
                || record.PrevHash != prevHash
                || ComputeHash(record.PrevHash, record.PayloadJson) != record.Hash)
            {
                return (false, record.Seq);
            }

            prevHash = record.Hash;
            expectedSeq++;
        }

        return (true, null);
    }
}
