using System;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents;

/// <summary>
/// Resolves THE substrate for the host the daemon is running on — the one place the
/// per-platform facade choice is made (ESC §0.3). macOS gets the macos-host substrate; every
/// other platform keeps <see cref="Wsl2AgentEnvironment"/>: on Windows that is the local-dev /
/// pre-provisioning shape, and on Linux it IS the production in-VM daemon (the WSL-specific
/// pieces of that class are lazy, which is exactly how it already ran inside MainguardEnv and
/// on the ubuntu CI legs before this factory existed).
/// </summary>
public static class AgentEnvironmentFactory
{
    /// <param name="log">Where the substrate's best-effort paths report what they could not do — the
    /// sandbox engine's posture inspect and its in-place ceiling update. Both are non-fatal by design,
    /// and a non-fatal failure with no diagnostic is how an operator ends up reading a ceiling in
    /// Settings that no jail is actually running under. Null in the harnesses; the daemon passes its
    /// own logger.</param>
    public static IAgentEnvironment CreateForHost(
        IAuditLog auditLog, string? gatewayEndpoint, Action<string>? log = null) =>
        OperatingSystem.IsMacOS()
            ? new MacHostAgentEnvironment(auditLog: auditLog, gatewayEndpoint: gatewayEndpoint, log: log)
            : new Wsl2AgentEnvironment(auditLog: auditLog, gatewayEndpoint: gatewayEndpoint, log: log);
}
