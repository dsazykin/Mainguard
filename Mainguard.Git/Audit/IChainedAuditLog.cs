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

    /// <summary>
    /// Redacts record <paramref name="seq"/>: appends a chained <c>redaction</c> event carrying the
    /// original's hash, then tombstones the stored payload — never rewrites the chain. Returns the
    /// redaction event's seq.
    /// </summary>
    long Redact(long seq, string reason, string osIdentity);
}
