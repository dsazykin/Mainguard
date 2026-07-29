namespace Mainguard.Agents.Agents;

/// <summary>
/// The orchestration roles a spawned agent session can carry (the free-form-string contract of
/// <c>SpawnAgentRequest.role</c> / <c>AgentInfo.role</c>). Shared by the daemon (spawn workflow,
/// terminal locking) and the App (coordinator surface, subagent badging).
/// </summary>
public static class AgentRoles
{
    /// <summary>A manually started agent (the default; empty string on the wire).</summary>
    public const string Manual = "";

    /// <summary>The operator-facing coordinator CLI: its jail gets the daemon-mediated
    /// <c>mainguard-agent</c> spawn channel, and its terminal is fully interactive.</summary>
    public const string Coordinator = "coordinator";

    /// <summary>
    /// A worker the daemon spawned rather than the operator: a coordinator's subagent, or an external-PR
    /// verification jail (<c>pr-&lt;n&gt;</c>). Appears as a subagent in the activity bar, and its terminal
    /// input is daemon-locked (P2-14 — read-only, steering goes through prompts).
    ///
    /// <para>This is one population on purpose: <c>CoordinatorSpawnGate</c> caps the number of ACTIVE
    /// managed workers, and both daemon-driven spawn paths are admitted through it, so an arriving bot
    /// pull request draws from the same <c>MaxActiveWorkers</c> allowance as a coordinator's fan-out
    /// instead of a private one it could exhaust the machine with.</para>
    /// </summary>
    public const string Managed = "managed";
}
