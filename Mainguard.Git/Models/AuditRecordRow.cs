namespace Mainguard.Git.Models;

/// <summary>
/// One persisted P2-15 audit-chain record. APPEND-ONLY at the schema level: the
/// <c>AddAuditChain</c> migration installs SQLite triggers that ABORT any <c>DELETE</c> and any
/// <c>UPDATE</c> except the single legal transition — tombstoning the payload of a live row during
/// redaction (chain columns byte-identical, <see cref="Redacted"/> 0→1, payload/nonce nulled).
/// The store API exposes no update or delete either; the trigger exists so even raw SQL against
/// the file has to announce itself by first dropping the trigger — which the chain then catches.
/// </summary>
public class AuditRecordRow
{
    /// <summary>Chain sequence number, 1-based and contiguous. Assigned by the appender, never by the DB.</summary>
    public long Seq { get; set; }

    /// <summary>
    /// Append time as an ISO-8601 round-trip ("O") string — stored as text, not a DateTime column,
    /// because this exact string is hashed inside the canonical envelope and any storage round-trip
    /// that reformats it (tick truncation, offset normalization) would break the chain.
    /// </summary>
    public string TimestampText { get; set; } = string.Empty;

    /// <summary>The event-type discriminator ("plan_approved", "killswitch", "redaction", …).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// AES-GCM ciphertext of the canonical envelope (nonce ‖ tag ‖ ciphertext), or null once the
    /// row is redacted — redaction destroys the only payload copy on disk (the file mirror never
    /// carries payloads), so a tombstone is unrecoverable by construction.
    /// </summary>
    public byte[]? PayloadCiphertext { get; set; }

    /// <summary>The at-rest key id used for <see cref="PayloadCiphertext"/> (null once redacted).</summary>
    public string? KeyId { get; set; }

    /// <summary>The previous record's <see cref="Hash"/> (genesis constant for seq 1).</summary>
    public string PrevHash { get; set; } = string.Empty;

    /// <summary>SHA-256(PrevHash ‖ canonical envelope), lowercase hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>1 once the payload has been tombstoned by a chained <c>redaction</c> event.</summary>
    public bool Redacted { get; set; }
}
