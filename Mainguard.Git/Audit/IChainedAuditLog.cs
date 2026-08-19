using System;
using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// The P2-15 surface over the narrow <see cref="IAuditLog"/> seam — the master-doc contract
/// members, verbatim. The 28 existing <c>Append(AuditEvent)</c> call sites keep the narrow seam;
/// the verify CLI, the audit RPC, redaction, and retention depend on this one.
/// </summary>
public interface IChainedAuditLog : IAuditLog
{
    /// <summary>Canonicalizes, chains, persists; returns the new record's seq.</summary>
    long Append(string type, object payload, string osIdentity);

    /// <summary>Records with <c>Seq &gt;= fromSeq</c>, ascending, at most <paramref name="take"/>.
    /// <c>PayloadJson</c> is the decrypted canonical envelope; a redacted record carries the tombstone.</summary>
    IReadOnlyList<AuditRecord> Read(long fromSeq, int take);

    /// <summary>Walks the whole chain (+ the file mirror) — first bad seq on failure.</summary>
    (bool Valid, long? FirstBadSeq) VerifyAll();

    /// <summary>The chain head (highest seq + its hash), or null on an empty chain — what the
    /// verify CLI prints and the RFC 3161 anchor timestamps. No decryption involved.</summary>
    (long Seq, string Hash)? Head();

    /// <summary>
    /// Redacts record <paramref name="seq"/>: appends a chained <c>redaction</c> event carrying the
    /// original's hash, then tombstones the stored payload — never rewrites the chain. Returns the
    /// redaction event's seq.
    /// </summary>
    long Redact(long seq, string reason, string osIdentity);

    /// <summary>Expires records older than <paramref name="retention"/> as chained redactions —
    /// tombstones, never deletions (row count unchanged, chain verifiable). Redaction events are
    /// exempt (they vouch for earlier tombstones and carry no sensitive payload). Returns the
    /// number of records redacted.</summary>
    int ApplyRetention(TimeSpan retention);
}
