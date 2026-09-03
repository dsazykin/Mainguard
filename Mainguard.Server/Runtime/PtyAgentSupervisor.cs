using System;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The real P2-09 <see cref="IAgentSupervisor"/> that replaces <c>NullAgentSupervisor</c> in the
/// daemon. It closes the P2-08 ↔ P2-09 integration: the gateway's 429 / budget pause now drives a real
/// PTY input pause via the <see cref="SessionLeader"/> (which owns the per-agent PTY fds), and the
/// agent's state (<c>RateLimited</c>, <c>Paused</c>, <c>Working</c>, …) is reflected in the
/// <see cref="AgentSessionStore"/> so it streams to clients as an <c>AgentEvent</c> state change.
/// </summary>
public sealed class PtyAgentSupervisor : IAgentSupervisor
{
    private readonly SessionLeader _leader;
    private readonly AgentSessionStore _store;

    public PtyAgentSupervisor(SessionLeader leader, AgentSessionStore store)
    {
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void PauseInput(string agentId) => _leader.PauseInput(agentId);

    public void ResumeInput(string agentId) => _leader.ResumeInput(agentId);

    public void MarkState(string agentId, string state, string? reason) => _store.MarkState(agentId, state, reason);

    public void MarkFrozen(string agentId, string? reason) => _store.MarkFrozen(agentId, reason);
}
