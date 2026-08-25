using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// A thread-safe in-memory <see cref="IAuditLog"/>. Since P2-15 this is the FALLBACK and the test
/// double, no longer the shipped default: the daemon registers <see cref="ChainedAuditLog"/>
/// whenever its SQLite DB opens, and lands here only when the DB cannot be prepared — logged
/// loudly at registration ("EVENTS WILL NOT SURVIVE RESTART"), and flagged on the wire
/// (<c>AuditService</c> answers <c>persistent=false</c>) so a heap journal can never be mistaken
/// for tamper-evidence.
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
