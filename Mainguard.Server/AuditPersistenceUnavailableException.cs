using System;

namespace Mainguard.Server;

/// <summary>
/// The daemon refused to start because the tamper-evident audit chain could not be opened SAFELY:
/// the key ring will not hold the audit master key (no at-rest protector and no explicit opt-in),
/// will not give it back (a tampered or foreign ring), or is wrapped under a protector this process
/// cannot use.
///
/// <para><b>Why this is fatal rather than a fallback.</b> The in-memory journal is a legitimate
/// stand-in for a daemon DB that would not open — there the choice is "no app at all" versus "an app
/// whose audit trail does not survive a restart", and the user can see the difference immediately.
/// It is not a legitimate stand-in for a key-ring refusal: the daemon binds, every RPC succeeds,
/// <c>VerifyAudit</c> quietly answers <c>persistent=false</c>, and the security store that exists to
/// be trusted later is silently keeping nothing. A daemon that cannot audit must say so at the point
/// a human is still looking at it, and both remedies (set a key-ring passphrase, or opt into the
/// unprotected posture deliberately) are named in the inner exception's message.</para>
/// </summary>
public sealed class AuditPersistenceUnavailableException : Exception
{
    public AuditPersistenceUnavailableException(Exception inner)
        : base(
            "The daemon will not start: the audit chain cannot be opened without losing its "
            + "tamper-evidence guarantees. " + inner.Message
            + " — refusing to fall back to the in-memory journal, which would bind the daemon, look "
            + "healthy, and lose every audit event at shutdown.",
            inner)
    {
    }
}
