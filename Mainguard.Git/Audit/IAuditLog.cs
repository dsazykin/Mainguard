using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// The minimal append/read audit seam (G-17). Every agent-initiated ref mutation,
/// spawn/kill, plan approval, and merge decision appends one <see cref="AuditEvent"/>
/// here. Deliberately narrow (append + read, no chaining/crypto concept) — which is what
/// let P2-15 land the hash-chained, encrypted, persisted implementation
/// (<see cref="ChainedAuditLog"/>, behind <see cref="IChainedAuditLog"/>) with zero
/// call-site changes. The Phase-2 test <c>AuditProbe</c> fixture wraps this interface.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Appends one event. Implementations preserve append order.
    ///
    /// <para><b>The chained implementation THROWS on store failure — deliberately.</b> A caller
    /// that must never block on audit availability (the kill switch, RT-D3) catches and arms its
    /// gap-marker path (<c>killswitch_audit_gap</c> on recovery); swallowing here would turn every
    /// audit outage into silent record loss instead.</para>
    /// </summary>
    void Append(AuditEvent auditEvent);

    /// <summary>
    /// All appended events, oldest first (the chained store decrypts; a redacted record surfaces
    /// with a <c>redacted</c> marker field).
    ///
    /// <para><b>Since P2-15 this store is real:</b> the daemon registers <see cref="ChainedAuditLog"/>
    /// whenever its SQLite DB opens (hash-chained, AES-GCM at rest, append-only at the schema
    /// level, file-mirrored, 90-day retention-as-redaction), and the journal has production
    /// readers — the <c>AuditService</c> RPCs (<c>VerifyAudit</c>/<c>ReadAudit</c>) and the offline
    /// <c>mainguardd audit verify</c> CLI. <see cref="InMemoryAuditLog"/> remains only as the
    /// no-DB fallback (logged loudly) and the test double.</para>
    ///
    /// <para><b>Still true, and still the rule:</b> an <c>Append</c> is evidence, not the
    /// user-visible record of something. Anything a person has to find out about NOW needs a log
    /// line, a typed refusal, or a UI notice <i>in addition</i> — the audit chain is where an
    /// investigation looks later, not where a human is told today. The paths that destroy work
    /// (<c>boot_swarm_reconcile</c>, <c>boot_leader_reattach</c>, <c>agent_rescue_empty</c>) each
    /// pair their audit event with exactly that.</para>
    /// </summary>
    IReadOnlyList<AuditEvent> Read();
}

/// <summary>
/// One audit record: a stable <paramref name="Type"/> discriminator (e.g. "spawn",
/// "killswitch", "plan_approved") plus opaque string fields. Kept intentionally flat
/// and string-typed so P2-15's hash chain serializes it deterministically (the fields
/// dictionary becomes the canonical-JSON payload) without this seam knowing the chain format.
/// </summary>
public sealed record AuditEvent(string Type, IReadOnlyDictionary<string, string> Fields)
{
    public AuditEvent(string type) : this(type, new Dictionary<string, string>()) { }
}
