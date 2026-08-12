using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// A thread-safe in-memory <see cref="IAuditLog"/> — the pre-P2-15 journal. Used by
/// the daemon skeleton and by tests (through <c>AuditProbe</c>). P2-15 replaces this
/// with the hash-chained, persisted implementation behind the same interface.
///
/// <para><b>This is what the shipped daemon registers</b>, so every audited event lives in the daemon
/// process's heap and nowhere else: it does not survive a restart, and — because
/// <see cref="IAuditLog.Read"/> has no production caller — it cannot be read even while the process is
/// alive. See <see cref="IAuditLog.Read"/> for the decision and what an <c>Append</c> may and may not be
/// relied on for until P2-15 lands.</para>
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEvent> _events = new();
    private readonly object _gate = new();

    public void Append(AuditEvent auditEvent)
    {
        lock (_gate)
        {
            _events.Add(auditEvent);
        }
    }

    public IReadOnlyList<AuditEvent> Read()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }
}
