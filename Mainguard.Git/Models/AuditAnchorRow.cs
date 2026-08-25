namespace Mainguard.Git.Models;

/// <summary>
/// One P2-15 RFC 3161 anchor: a chain head enqueued for external timestamping, and — once the TSA
/// answered — the third party's signed token over that head hash. NOT part of the hash chain and
/// not append-only: a forged or deleted anchor only weakens the anchor's own extra guarantee (the
/// token is TSA-signed, so it cannot be forged to match a rewritten chain), while the chain's
/// integrity stands on <see cref="AuditRecordRow"/> alone.
/// </summary>
public class AuditAnchorRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The chain head this anchor covers (seq of the record whose hash was timestamped).</summary>
    public long HeadSeq { get; set; }

    /// <summary>The head hash (lowercase hex SHA-256) submitted to the TSA.</summary>
    public string HeadHash { get; set; } = string.Empty;

    /// <summary>When the anchor was enqueued (ISO-8601 "O").</summary>
    public string RequestedAtText { get; set; } = string.Empty;

    /// <summary>The DER-encoded RFC 3161 token, or null while the request is still pending/retried.</summary>
    public byte[]? Token { get; set; }

    /// <summary>When the TSA answered (ISO-8601 "O"); null while pending.</summary>
    public string? AnchoredAtText { get; set; }
}
