using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mainguard.Server.Gateway;

/// <summary>
/// MG-4 / MG-20 — the daemon-side custody boundary for model credentials.
///
/// <para><b>The defect.</b> <c>SandboxAgentLauncher.BuildSecrets</c> writes the raw BYOK provider key
/// verbatim into <c>/run/secrets/agent.env</c>, which is agent-uid-owned mode 0400 — i.e. readable by
/// the agent. The intended confinement (the agent holds only a Mainguard token; a gateway injects the
/// real key at the network hop) did not exist: <c>ModelProxyMiddleware.BuildUpstreamRequest</c> passed
/// inbound headers straight through with no key substitution, and there was no Mainguard-token concept
/// at all. Separately (MG-20) the gateway's notion of *which agent is calling* fell back to the
/// client-supplied <c>x-mainguard-agent</c> header, which an agent can simply set to another agent's id
/// to evade its own budget or attribute spend and 429-pauses to a victim.</para>
///
/// <para><b>This type is the fix's foundation.</b> It mints an opaque per-agent token that the jail
/// receives <i>instead of</i> the provider key, and holds the real provider key daemon-side keyed by
/// agent id. The token is the agent's authenticated identity at the gateway (replacing the spoofable
/// header) and the thing the gateway swaps for the real key on the way upstream. The provider key never
/// enters the jail.</para>
///
/// <para>Memory-only and process-lifetime, exactly like <c>SessionKeyCache</c> — the durable BYOK store
/// is host-side (P2-01); the daemon has no keyring. Tokens are revoked on agent stop.</para>
/// </summary>
public sealed class AgentGatewayCredentials
{
    /// <summary>The prefix that marks a Mainguard gateway token, so a real provider key accidentally
    /// reaching the jail is distinguishable from the token that is supposed to be there.</summary>
    public const string TokenPrefix = "mg_sess_";

    private readonly ConcurrentDictionary<string, string> _agentByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenByAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _providerKeyByAgent = new(StringComparer.Ordinal);

    /// <summary>
    /// Issues (or re-issues) this agent's gateway token and takes custody of its real provider key.
    /// The returned token is what the jail receives; <paramref name="providerApiKey"/> stays here.
    /// A null/blank provider key still yields a token — the agent may be using an interactive CLI
    /// login rather than BYOK, and the gateway simply has no key to inject for it.
    /// </summary>
    public string Issue(string agentId, string? providerApiKey)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        // Re-issuing for a live agent must not orphan the previous token in the reverse map.
        if (_tokenByAgent.TryGetValue(agentId, out var previous))
        {
            _agentByToken.TryRemove(previous, out _);
        }

        var token = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokenByAgent[agentId] = token;
        _agentByToken[token] = agentId;

        if (!string.IsNullOrWhiteSpace(providerApiKey))
        {
            _providerKeyByAgent[agentId] = providerApiKey;
        }

        return token;
    }

    /// <summary>
    /// The authenticated agent behind a presented gateway token, or null when the token is unknown.
    /// This is the ONLY trustworthy identity source at the gateway — never a client-supplied header.
    /// </summary>
    public string? ResolveAgent(string? token) =>
        !string.IsNullOrEmpty(token) && _agentByToken.TryGetValue(token, out var agentId) ? agentId : null;

    /// <summary>The real provider key held for an agent, or null when none is in custody.</summary>
    public string? ProviderKeyFor(string? agentId) =>
        !string.IsNullOrEmpty(agentId) && _providerKeyByAgent.TryGetValue(agentId, out var key) ? key : null;

    /// <summary>The token currently issued to an agent (for the spawn path), or null.</summary>
    public string? TokenFor(string? agentId) =>
        !string.IsNullOrEmpty(agentId) && _tokenByAgent.TryGetValue(agentId, out var token) ? token : null;

    /// <summary>Drops the agent's token and its provider key — called on stop so a torn-down agent's
    /// credential cannot be replayed and the key does not outlive the session that supplied it.</summary>
    public void Revoke(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        if (_tokenByAgent.TryRemove(agentId, out var token))
        {
            _agentByToken.TryRemove(token, out _);
        }

        _providerKeyByAgent.TryRemove(agentId, out _);
    }
}
