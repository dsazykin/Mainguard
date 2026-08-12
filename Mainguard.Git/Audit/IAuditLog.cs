using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// The minimal append/read audit seam (G-17). Every agent-initiated ref mutation,
/// spawn/kill, plan approval, and merge decision appends one <see cref="AuditEvent"/>
/// here. Before P2-15 lands this is a plain ordered journal; P2-15 supplies the
/// hash-chained, tamper-evident implementation behind this same interface — so this
/// seam is deliberately narrow (append + read) and carries no chaining/crypto concept.
/// The Phase-2 test <c>AuditProbe</c> fixture wraps this interface.
/// </summary>
public interface IAuditLog
{
    /// <summary>Appends one event. Implementations preserve append order.</summary>
    void Append(AuditEvent auditEvent);

    /// <summary>
    /// All appended events, oldest first.
    ///
    /// <para><b>Nothing in production calls this, and that is a recorded decision rather than an
    /// oversight — but it is also a real, dated gap, so read this before adding another
    /// <see cref="Append"/> call site.</b> There are 28 <c>Append</c> calls across 13 production files
    /// (queue discards, stale overrides, restart resumes, branch drift, kill epochs, plan approvals,
    /// egress verdicts, boot reconciles) and <b>zero</b> production readers: no RPC on any <c>.proto</c>
    /// exposes the journal, no ViewModel renders it, and the shipped implementation
    /// (<c>InMemoryAuditLog</c>) is a <c>List&lt;T&gt;</c> that grows until the daemon exits and is then
    /// gone. So every one of those events is, today, written to something no human can look at — during
    /// or after the incident it describes.</para>
    ///
    /// <para><b>The plan is P2-15, and this method is the seam it lands behind.</b> P2-15 supplies the
    /// hash-chained, persisted implementation plus the surface that reads it; the interface is
    /// deliberately append + read (and no chaining/crypto concept) so that swap needs no call-site
    /// change. Until then, treat an <c>Append</c> as evidence for a future investigation and NEVER as the
    /// user-visible record of something: anything a person has to find out about now needs a log line, a
    /// typed refusal, or a UI notice <i>in addition</i>. The paths in this change that destroy work —
    /// <c>boot_swarm_reconcile</c>, <c>boot_leader_reattach</c>, <c>agent_rescue_empty</c> — each pair
    /// their audit event with exactly that, for exactly this reason.</para>
    /// </summary>
    IReadOnlyList<AuditEvent> Read();
}

/// <summary>
/// One audit record: a stable <paramref name="Type"/> discriminator (e.g. "spawn",
/// "killswitch", "plan_approved") plus opaque string fields. Kept intentionally flat
/// and string-typed so P2-15's hash chain can serialize it deterministically without
/// this seam having to know the chain format.
/// </summary>
public sealed record AuditEvent(string Type, IReadOnlyDictionary<string, string> Fields)
{
    public AuditEvent(string type) : this(type, new Dictionary<string, string>()) { }
}
