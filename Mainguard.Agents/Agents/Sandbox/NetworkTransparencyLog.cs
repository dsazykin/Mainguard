using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// One network-transparency line (P2-17): every proxied fetch and every egress verdict is recorded
/// here so the user can see exactly what an agent reached. Distinct from the tamper-evident audit log
/// (G-17): transparency is the human-visible "what happened on the wire" feed; audit is the
/// security-decision record. A denied request appears in <b>both</b>.
///
/// <para><b>Why <see cref="Verdict"/> is an <see cref="EgressVerdict"/> and not a string.</b> It was a
/// free-form string, and producers and consumers drifted apart in exactly the way an untyped field
/// invites: <see cref="DaemonGitProxy"/> wrote <c>"refused"</c>/<c>"allowed"</c> while the daemon's log
/// tee compared against <c>"Denied"</c>. The comparison was therefore ALWAYS false, so every egress
/// refusal — a jailed agent probing blocked hosts — was logged at Information and an operator filtering
/// at Warning saw nothing. Neither side was wrong on its own; nothing made them agree. The enum is what
/// makes them agree, and it makes the next drift a compile error rather than a silent downgrade.</para>
/// </summary>
public sealed record TransparencyLine(
    string Kind,
    string Host,
    string Detail,
    string AgentId,
    long Bytes,
    EgressVerdict Verdict,
    DateTimeOffset When)
{
    public static TransparencyLine Now(
        string kind, string host, string detail, string agentId, long bytes, EgressVerdict verdict)
        => new(kind, host, detail, agentId, bytes, verdict, DateTimeOffset.UtcNow);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "{0:O} [{1}] {2} {3} agent={4} bytes={5} => {6}", When, Kind, Host, Detail, AgentId, Bytes, Verdict);
}

/// <summary>The P2-17 transparency sink seam. P2-17 supplies the persisted/streamed implementation.</summary>
public interface INetworkTransparencyLog
{
    void Record(TransparencyLine line);
    IReadOnlyList<TransparencyLine> Lines { get; }
}

/// <summary>A thread-safe in-memory transparency log — the pre-P2-17 sink, used by the daemon and tests.</summary>
public sealed class InMemoryNetworkTransparencyLog : INetworkTransparencyLog
{
    private readonly List<TransparencyLine> _lines = new();
    private readonly object _gate = new();

    public void Record(TransparencyLine line)
    {
        lock (_gate) { _lines.Add(line); }
    }

    public IReadOnlyList<TransparencyLine> Lines
    {
        get { lock (_gate) { return _lines.ToArray(); } }
    }
}
