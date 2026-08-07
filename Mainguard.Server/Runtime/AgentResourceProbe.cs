using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Gateway;

namespace Mainguard.Server.Runtime;

/// <summary>One agent's reading as the daemon reports it. Null CPU/RAM means NOT MEASURED, never zero.</summary>
/// <param name="IsMetered">Whether this agent's model spend is measurable at all — see
/// <see cref="AgentResourceProbe"/> for the predicate and why it is not "the user supplied a key".</param>
public sealed record AgentResourceReport(
    string AgentId,
    double? CpuPercent,
    double? RamBytes,
    bool IsMetered,
    string? UnavailableReason);

/// <summary>
/// Joins the three facts the Resource Monitor needs, per agent, once per tick: the daemon's session
/// registry (who is alive and in which container), the container engine (what it is using), and the
/// gateway credential store (whether its spend can be measured at all).
///
/// <para><b>The metering predicate.</b> An agent is metered exactly when
/// <see cref="AgentGatewayCredentials.TokenFor"/> returns a token for it — i.e. the daemon issued that
/// agent a gateway confinement token at spawn. This is deliberately NOT "the user supplied an API key",
/// which is the tempting and wrong answer. Metering happens by routing model traffic through the gateway,
/// and <c>SandboxAgentLauncher.TryConfineToGatewayAsync</c> requires ALL of: the gateway is bound and
/// enabled; it is REACHABLE from this jail's egress proxy (measured per spawn, not assumed); a provider
/// key was supplied; and the CLI declares BOTH a base-URL variable and a model host. That last one alone
/// excludes <c>codex</c>, <c>qwen-code</c> and <c>opencode</c>, which declare neither — so a BYOK codex
/// agent has a key, is charged real money, and is <b>not</b> measurable. "BYOK" is therefore not the
/// predicate; "a confinement token was actually issued" is, because it is the one fact that is true if and
/// only if the traffic transits the metering proxy.</para>
///
/// <para>Reading the fact from the credential store rather than recomputing the four conditions is the
/// point: two parallel derivations of the same predicate would eventually disagree, and the disagreement
/// would be invisible — the failure mode <c>docs/design/oauth-budgeting.md</c> already records for the
/// upstream binding.</para>
///
/// <para>An OAuth agent is never confined (it has no key to withhold and authenticates past the proxy
/// with a session Mainguard never issued), so it reports <c>IsMetered = false</c> and the client hides
/// its spend rather than drawing <c>$0.00</c>.</para>
/// </summary>
public sealed class AgentResourceProbe
{
    /// <summary>
    /// How long a tick's readings are reused before the engine is asked again. This is what keeps the
    /// cost bounded by TIME rather than by subscriber count: two clients (or one reconnecting mid-tick)
    /// share a reading instead of each driving its own set of stats calls. Half the stream's poll
    /// interval, so a subscriber polling on schedule still gets a fresh sample every tick.
    /// </summary>
    public static readonly TimeSpan DefaultCacheWindow = TimeSpan.FromMilliseconds(2500);

    private readonly AgentSessionStore _store;
    private readonly IContainerResourceSampler _sampler;
    private readonly AgentGatewayCredentials? _credentials;
    private readonly TimeSpan _cacheWindow;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;
    private IReadOnlyList<AgentResourceReport>? _cached;
    private DateTimeOffset _cachedAt;

    public AgentResourceProbe(
        AgentSessionStore store,
        IContainerResourceSampler sampler,
        AgentGatewayCredentials? credentials = null,
        TimeSpan? cacheWindow = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _credentials = credentials;
        _cacheWindow = cacheWindow ?? DefaultCacheWindow;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// One tick: every live session, each with its reading or an honest reason there is none. A session
    /// with no container yet (still spawning) is reported as unavailable rather than omitted — dropping it
    /// would make a starting agent look like it had gone away.
    /// </summary>
    public async Task<IReadOnlyList<AgentResourceReport>> ReadAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } fresh && _clock() - _cachedAt < _cacheWindow) return fresh;
            var readings = await SampleAsync(ct).ConfigureAwait(false);
            _cached = readings;
            _cachedAt = _clock();
            return readings;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task<IReadOnlyList<AgentResourceReport>> SampleAsync(CancellationToken ct)
    {
        var sessions = _store.List();
        if (sessions.Count == 0) return Array.Empty<AgentResourceReport>();

        var targets = sessions
            .Where(s => !string.IsNullOrEmpty(s.ContainerId))
            .Select(s => (s.Id, ContainerId: s.ContainerId!))
            .ToList();

        IReadOnlyList<ContainerResourceSample> samples;
        try
        {
            samples = await _sampler.SampleAsync(targets, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The engine is unreachable. Every agent becomes explicitly unknown — NOT a page of zeros,
            // which is the one outcome that would look like a working monitor reporting an idle fleet.
            return sessions
                .Select(s => new AgentResourceReport(s.Id, null, null, IsMetered(s.Id), ex.GetType().Name))
                .ToArray();
        }

        var byAgent = new Dictionary<string, ContainerResourceSample>(StringComparer.Ordinal);
        foreach (var sample in samples) byAgent[sample.AgentId] = sample;

        return sessions
            .Select(s =>
            {
                var metered = IsMetered(s.Id);
                if (byAgent.TryGetValue(s.Id, out var sample))
                    return new AgentResourceReport(s.Id, sample.CpuPercent, sample.RamBytes, metered, sample.UnavailableReason);

                return new AgentResourceReport(s.Id, null, null, metered, "no sandbox");
            })
            .ToArray();
    }

    /// <summary>The predicate, in one place: a gateway confinement token exists for this agent.</summary>
    private bool IsMetered(string agentId) => _credentials?.TokenFor(agentId) is not null;
}
