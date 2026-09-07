using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Mainguard.Server.Gateway;

/// <summary>
/// MG-4 / MG-20 — the daemon-side custody boundary for model credentials.
///
/// <para><b>The defect.</b> <c>SandboxAgentLauncher.BuildSecrets</c> writes the raw BYOK provider key
/// verbatim into <c>/run/secrets/agent/agent.env</c>, which is agent-uid-owned mode 0400 — i.e. readable by
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

    /// <summary>
    /// F25 — how long a token may serve before it is replaced. Not a guess at "how long is safe": the
    /// token is agent-readable by design, so a prompt-injected worker can commit it, and the only thing
    /// that bounds the value of a leaked copy is how soon it stops working. A leaked token is useful for
    /// at most this long plus <see cref="DefaultOverlap"/>.
    /// </summary>
    public static readonly TimeSpan DefaultRotationInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a SUPERSEDED token keeps resolving after its replacement was delivered. This is the
    /// "must not break a live agent mid-request" half of the contract: a request that was already in
    /// flight (or that the CLI had already started with the old value) completes on the old token, and
    /// only then does it stop being an identity.
    /// </summary>
    public static readonly TimeSpan DefaultOverlap = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, string> _agentByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenByAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _providerKeyByAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _upstreamHostByAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issuedAtByAgent = new(StringComparer.Ordinal);

    /// <summary>Superseded tokens still inside their overlap window: token → (agentId, expiry).</summary>
    private readonly ConcurrentDictionary<string, (string AgentId, DateTimeOffset Until)> _retiring =
        new(StringComparer.Ordinal);

    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _rotationInterval;
    private readonly TimeSpan _overlap;
    private readonly object _rotationGate = new();

    /// <param name="clock">The sole time source, injected so rotation is testable on a virtual clock.</param>
    /// <param name="rotationInterval">Token lifetime before rotation; <c>null</c> uses
    /// <see cref="DefaultRotationInterval"/>.</param>
    /// <param name="overlap">How long a superseded token stays valid; <c>null</c> uses
    /// <see cref="DefaultOverlap"/>.</param>
    public AgentGatewayCredentials(
        Func<DateTimeOffset>? clock = null,
        TimeSpan? rotationInterval = null,
        TimeSpan? overlap = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _rotationInterval = rotationInterval ?? DefaultRotationInterval;
        _overlap = overlap ?? DefaultOverlap;
    }

    /// <summary>
    /// F25 — how a rotated token reaches the jail that has to present it. Returns true when the new
    /// token was delivered; a false (or an unset hook) means the agent still only holds the old one.
    ///
    /// <para><b>Rotation is gated on this returning true, and that gating is the whole safety
    /// argument.</b> The token lives in the jail's <c>/run/secrets/agent/agent.env</c>, which the launch
    /// wrapper sources; nothing in the daemon rewrites that file today. Retiring a token we could not
    /// redeliver would break the agent at the end of the overlap window, so instead the old token is
    /// KEPT and rotation is retried on the next evaluation. With no hook wired the behaviour is exactly
    /// what it was before this change — one token per spawn, revoked on stop — and wiring the hook is
    /// the single step that turns scheduled rotation on. See the PR body for the spawn-path edit.</para>
    /// </summary>
    public Func<string, string, bool>? TokenDelivery { get; set; }

    /// <summary>Raised after a token was rotated and delivered — (agentId, newToken). Observation only;
    /// <see cref="TokenDelivery"/> is the thing rotation is gated on.</summary>
    public event Action<string, string>? TokenRotated;

    /// <summary>
    /// Issues (or re-issues) this agent's gateway token and takes custody of its real provider key.
    /// The returned token is what the jail receives; <paramref name="providerApiKey"/> stays here.
    /// A null/blank provider key still yields a token — the agent may be using an interactive CLI
    /// login rather than BYOK, and the gateway simply has no key to inject for it.
    ///
    /// <para><paramref name="upstreamHost"/> is the PER-AGENT UPSTREAM BINDING, and it is what makes the
    /// gateway reachable at all. Once a CLI's base URL points at the gateway, the inbound request's
    /// <c>Host</c> is the GATEWAY — not <c>api.anthropic.com</c> — so a middleware that decides "is this a
    /// model request?" by matching the Host header against a model-host list never matches, and every real
    /// request falls through unfronted. Nothing else in the daemon records which provider a given agent's
    /// traffic belongs to, so it is captured HERE, at spawn, from the adapter that was launched: the agent's
    /// authenticated token is the key, and the upstream is a property of the agent rather than of the
    /// request. That also closes the obvious abuse — an agent cannot redirect its own traffic to an
    /// arbitrary host by setting a header, because it never gets a say in its upstream.</para>
    /// </summary>
    public string Issue(string agentId, string? providerApiKey, string? upstreamHost = null)
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

        var token = NewToken();
        _tokenByAgent[agentId] = token;
        _agentByToken[token] = agentId;
        _issuedAtByAgent[agentId] = _clock();

        if (!string.IsNullOrWhiteSpace(providerApiKey))
        {
            _providerKeyByAgent[agentId] = providerApiKey;
        }

        if (!string.IsNullOrWhiteSpace(upstreamHost))
        {
            _upstreamHostByAgent[agentId] = upstreamHost;
        }

        return token;
    }

    /// <summary>
    /// The provider host this agent's model traffic is forwarded to, or null when the agent has no
    /// upstream binding (it was not spawned under gateway confinement). Null is a REFUSAL, never a
    /// default: guessing a provider would forward one vendor's request to another's endpoint with the
    /// daemon's key attached.
    /// </summary>
    public string? UpstreamHostFor(string? agentId) =>
        !string.IsNullOrEmpty(agentId) && _upstreamHostByAgent.TryGetValue(agentId, out var host) ? host : null;

    /// <summary>
    /// The authenticated agent behind a presented gateway token, or null when the token is unknown.
    /// This is the ONLY trustworthy identity source at the gateway — never a client-supplied header.
    ///
    /// <para>F25: a token that has been superseded but is still inside its overlap window resolves too,
    /// so rotation can never fail a request that was already under way. An overlap that has elapsed
    /// resolves to null — that is the point of rotating.</para>
    ///
    /// <para>Rotation is evaluated here rather than on a timer. This is the one method every model
    /// request goes through, the check is a timestamp comparison, and an idle agent whose token nobody
    /// is presenting has nothing to rotate. A timer would add a daemon lifecycle for no reachable
    /// difference in exposure.</para>
    /// </summary>
    public string? ResolveAgent(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        RotateStale();

        if (_agentByToken.TryGetValue(token, out var agentId))
        {
            return agentId;
        }

        if (_retiring.TryGetValue(token, out var retiring))
        {
            if (retiring.Until > _clock())
            {
                return retiring.AgentId;
            }

            _retiring.TryRemove(token, out _);
        }

        return null;
    }

    /// <summary>
    /// F25 — replaces every token older than the rotation interval, provided the replacement can be
    /// delivered to the jail (<see cref="TokenDelivery"/>). Returns how many agents were rotated.
    ///
    /// <para>Safe to call from anywhere and as often as you like: it is a timestamp scan under one lock,
    /// it is idempotent within an interval, and it never touches an agent whose token is still young.
    /// Called opportunistically by <see cref="ResolveAgent"/>; also public so a daemon that wants a
    /// fixed cadence can drive it.</para>
    /// </summary>
    public int RotateStale()
    {
        var deliver = TokenDelivery;
        if (deliver is null || _rotationInterval <= TimeSpan.Zero)
        {
            // No way to hand the jail its new token — see TokenDelivery for why rotating anyway would
            // break the agent instead of protecting it.
            return 0;
        }

        var now = _clock();
        List<(string AgentId, string Token)> rotated;

        lock (_rotationGate)
        {
            var due = _issuedAtByAgent
                .Where(kv => now - kv.Value >= _rotationInterval)
                .Select(kv => kv.Key)
                .ToList();

            if (due.Count == 0)
            {
                DropExpiredRetirees(now);
                return 0;
            }

            rotated = new List<(string, string)>(due.Count);
            foreach (var agentId in due)
            {
                if (!_tokenByAgent.TryGetValue(agentId, out var previous))
                {
                    _issuedAtByAgent.TryRemove(agentId, out _);
                    continue;
                }

                var replacement = NewToken();

                // Deliver FIRST. A delivery that fails leaves the agent holding a token that still
                // works and this method retrying on the next call, which is strictly better than an
                // agent that authenticates with nothing.
                if (!TryDeliver(deliver, agentId, replacement))
                {
                    continue;
                }

                _tokenByAgent[agentId] = replacement;
                _agentByToken[replacement] = agentId;
                _issuedAtByAgent[agentId] = now;

                // The old token stops being a live identity but stays resolvable for the overlap, so an
                // in-flight request finishes on it.
                _agentByToken.TryRemove(previous, out _);
                _retiring[previous] = (agentId, now + _overlap);

                rotated.Add((agentId, replacement));
            }

            DropExpiredRetirees(now);
        }

        foreach (var (agentId, replacement) in rotated)
        {
            TokenRotated?.Invoke(agentId, replacement);
        }

        return rotated.Count;
    }

    /// <summary>When the agent's current token was minted, or null when it holds none. Test seam for the
    /// rotation clock, and the honest answer to "how old is this credential?".</summary>
    public DateTimeOffset? IssuedAtFor(string? agentId) =>
        !string.IsNullOrEmpty(agentId) && _issuedAtByAgent.TryGetValue(agentId, out var at) ? at : null;

    private static string NewToken() =>
        TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>A delivery hook is host-supplied code on a request path; a throw from it must not take
    /// the gateway down, and it means the same thing as "not delivered".</summary>
    private static bool TryDeliver(Func<string, string, bool> deliver, string agentId, string token)
    {
        try
        {
            return deliver(agentId, token);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DropExpiredRetirees(DateTimeOffset now)
    {
        foreach (var kv in _retiring)
        {
            if (kv.Value.Until <= now)
            {
                _retiring.TryRemove(kv.Key, out _);
            }
        }
    }

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

        // A stopped agent gets no overlap: the whole point of revoking is that nothing it held can be
        // replayed, and there is no in-flight request to protect once the jail is gone.
        foreach (var kv in _retiring)
        {
            if (string.Equals(kv.Value.AgentId, agentId, StringComparison.Ordinal))
            {
                _retiring.TryRemove(kv.Key, out _);
            }
        }

        _issuedAtByAgent.TryRemove(agentId, out _);
        _providerKeyByAgent.TryRemove(agentId, out _);
        _upstreamHostByAgent.TryRemove(agentId, out _);
    }
}
