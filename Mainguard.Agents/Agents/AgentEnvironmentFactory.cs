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
    public static IAgentEnvironment CreateForHost(IAuditLog auditLog, string? gatewayEndpoint) =>
        OperatingSystem.IsMacOS()
            ? new MacHostAgentEnvironment(auditLog: auditLog, gatewayEndpoint: gatewayEndpoint)
            : new Wsl2AgentEnvironment(auditLog: auditLog, gatewayEndpoint: gatewayEndpoint);
}
