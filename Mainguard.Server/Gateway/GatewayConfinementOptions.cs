using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Server.Gateway;

/// <summary>
/// The provider hosts the gateway recognises as model traffic, derived from the ONE place those hosts
/// are already declared (<see cref="EgressAllowlist.DefaultEntries"/>, the <c>ModelApi</c> entries) so
/// the allowlist and the gateway can never drift into disagreeing about what a model host is.
///
/// <para>This list is only consulted for the legacy proxy-shaped request (Host = the provider). A
/// confined BYOK agent is routed by its per-agent upstream binding instead, which is why adding a new
/// provider adapter does not require touching this.</para>
/// </summary>
public static class ModelHosts
{
    /// <summary>Every default-allowlisted model-API host, lower-cased.</summary>
    public static IReadOnlyCollection<string> All { get; } = EgressAllowlist.DefaultEntries
        .Where(e => e.Kind == EgressEntryKind.ModelApi)
        .Select(e => e.HostPattern.Trim().ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// What the spawn path needs to know to confine a BYOK agent to the gateway: where to point the CLI,
/// and whether the gateway is running at all.
///
/// <para><b>Disabled is the default, and it means "behave exactly as before".</b> A daemon started
/// without a gateway bind address produces <c>Enabled: false</c> / <c>BaseUrl: null</c>, and
/// <c>SandboxAgentLauncher.BuildSecrets</c> then takes its original branch — the provider key goes into
/// the jail as it always did. That is what keeps this change additive: nothing about an existing
/// deployment moves until an operator turns the gateway on.</para>
/// </summary>
/// <param name="BaseUrl">The gateway URL a confined CLI's base-URL variable is set to, or null.</param>
/// <param name="Enabled">Whether the daemon actually bound a gateway listener.</param>
/// <param name="DisabledUnintentionally">
/// True when the gateway is absent because the bind AUTO-RESOLVED TO NOTHING rather than because the
/// operator turned it off. Both postures hand a BYOK jail the raw provider key, but only one of them was
/// asked for: the audit's B1 finding was a resolver that returned null on every normal Linux/WSL2 host,
/// which disabled the gateway fleet-wide and was indistinguishable in the log from a deliberate
/// <c>--gateway-bind off</c>. The launcher logs this variant as an error.
/// </param>
public sealed record GatewayConfinementOptions(
    string? BaseUrl, bool Enabled, bool DisabledUnintentionally = false)
{
    /// <summary>The posture of a daemon with no model gateway — the default everywhere.</summary>
    public static readonly GatewayConfinementOptions Disabled = new(null, false);

    /// <summary>True when a jail can actually be pointed somewhere useful.</summary>
    public bool CanConfine => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>
/// The default <see cref="IAgentPortMap"/>: no per-agent listener ports.
///
/// <para>Mainguard runs ONE gateway listener rather than a port per agent, so attribution comes from the
/// agent's authenticated token (<see cref="AgentGatewayCredentials"/>). This implementation exists so
/// that seam stays open — and returns null rather than guessing, because a wrong answer here would bill
/// one agent for another's tokens and pause the wrong PTY on a 429.</para>
/// </summary>
public sealed class NullAgentPortMap : IAgentPortMap
{
    public string? AgentForPort(int port) => null;
}
