using System;
using System.Collections.Generic;
using Mainguard.Server.Gateway;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// F25 — the per-agent <c>mg_sess_</c> token rotates on a schedule.
///
/// <para>The finding: <c>Issue</c> ran once per spawn and the token was only revoked on stop, so a
/// leaked copy — and the token is agent-readable by design, so a prompt-injected worker can
/// <c>git add</c> it into a branch that gets published and merged — stayed valid for the whole life of
/// the agent. Rotation bounds that window.</para>
///
/// <para>The constraint is that rotation must not break a live agent. It is therefore gated on the new
/// token actually being DELIVERED to the jail, and the superseded token keeps resolving for an overlap
/// window so a request already under way finishes.</para>
/// </summary>
public sealed class AgentGatewayTokenRotationTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    [Fact]
    public void WithoutADeliveryHook_NothingRotates()
    {
        // A token we cannot redeliver must not be retired: that would break the agent instead of
        // protecting it. With no hook the behaviour is exactly the pre-F25 one.
        var (creds, advance) = Build(deliver: null);
        var token = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval * 4);

        Assert.Equal(0, creds.RotateStale());
        Assert.Equal("agent-1", creds.ResolveAgent(token));
        Assert.Equal(token, creds.TokenFor("agent-1"));
    }

    [Fact]
    public void AfterTheInterval_TheTokenIsReplacedAndDelivered()
    {
        var delivered = new List<(string Agent, string Token)>();
        var (creds, advance) = Build((agent, token) =>
        {
            delivered.Add((agent, token));
            return true;
        });

        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");
        advance(Interval + TimeSpan.FromMinutes(1));

        Assert.Equal(1, creds.RotateStale());

        var replacement = creds.TokenFor("agent-1");
        Assert.NotNull(replacement);
        Assert.NotEqual(original, replacement);
        Assert.StartsWith(AgentGatewayCredentials.TokenPrefix, replacement);
        Assert.Equal(new[] { ("agent-1", replacement!) }, delivered);

        // Custody of the real key and the upstream binding survive the rotation.
        Assert.Equal("sk-real", creds.ProviderKeyFor("agent-1"));
        Assert.Equal("api.anthropic.com", creds.UpstreamHostFor("agent-1"));
    }

    [Fact]
    public void TheSupersededToken_StillResolves_DuringTheOverlap()
    {
        var (creds, advance) = Build((_, _) => true);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval + TimeSpan.FromMinutes(1));
        creds.RotateStale();

        // A request that was already in flight (or a CLI process that started with the old value)
        // must not be failed by a rotation it never saw.
        Assert.Equal("agent-1", creds.ResolveAgent(original));
        Assert.Equal("agent-1", creds.ResolveAgent(creds.TokenFor("agent-1")));
    }

    [Fact]
    public void TheSupersededToken_StopsResolving_OnceTheOverlapElapses()
    {
        var (creds, advance) = Build((_, _) => true);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval + TimeSpan.FromMinutes(1));
        creds.RotateStale();
        advance(Overlap + TimeSpan.FromMinutes(1));

        // The whole point: a leaked copy is worth at most one interval plus one overlap.
        Assert.Null(creds.ResolveAgent(original));
        Assert.Equal("agent-1", creds.ResolveAgent(creds.TokenFor("agent-1")));
    }

    [Fact]
    public void ADeliveryFailure_KeepsTheOldTokenWorking_AndRetriesLater()
    {
        var deliverable = false;
        var (creds, advance) = Build((_, _) => deliverable);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval + TimeSpan.FromMinutes(1));
        Assert.Equal(0, creds.RotateStale());
        Assert.Equal(original, creds.TokenFor("agent-1"));
        Assert.Equal("agent-1", creds.ResolveAgent(original));

        // The jail is reachable again — the retry lands with no further prompting.
        deliverable = true;
        Assert.Equal(1, creds.RotateStale());
        Assert.NotEqual(original, creds.TokenFor("agent-1"));
    }

    [Fact]
    public void ADeliveryHookThatThrows_IsTreatedAsNotDelivered()
    {
        var (creds, advance) = Build((_, _) => throw new InvalidOperationException("jail is gone"));
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval * 2);

        Assert.Equal(0, creds.RotateStale());
        Assert.Equal("agent-1", creds.ResolveAgent(original));
    }

    [Fact]
    public void BeforeTheInterval_NothingRotates()
    {
        var (creds, advance) = Build((_, _) => true);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval - TimeSpan.FromMinutes(1));

        Assert.Equal(0, creds.RotateStale());
        Assert.Equal(original, creds.TokenFor("agent-1"));
    }

    [Fact]
    public void ResolveAgent_DrivesRotation_SoNoTimerIsNeeded()
    {
        var (creds, advance) = Build((_, _) => true);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval + TimeSpan.FromMinutes(1));

        // The presented (now stale) token still authenticates this request — and the rotation it
        // triggered means the NEXT one uses a fresh credential.
        Assert.Equal("agent-1", creds.ResolveAgent(original));
        Assert.NotEqual(original, creds.TokenFor("agent-1"));
    }

    [Fact]
    public void Revoke_KillsEveryTokenIncludingOneInsideItsOverlap()
    {
        var (creds, advance) = Build((_, _) => true);
        var original = creds.Issue("agent-1", "sk-real", "api.anthropic.com");

        advance(Interval + TimeSpan.FromMinutes(1));
        creds.RotateStale();
        var replacement = creds.TokenFor("agent-1");

        creds.Revoke("agent-1");

        // A stopped agent gets no grace: nothing it held may be replayed.
        Assert.Null(creds.ResolveAgent(original));
        Assert.Null(creds.ResolveAgent(replacement));
        Assert.Null(creds.ProviderKeyFor("agent-1"));
        Assert.Null(creds.IssuedAtFor("agent-1"));
    }

    [Fact]
    public void OneAgentsRotation_DoesNotTouchAnother()
    {
        var (creds, advance) = Build((_, _) => true);
        var first = creds.Issue("agent-1", "sk-a", "api.anthropic.com");
        advance(Interval - TimeSpan.FromMinutes(10));
        var second = creds.Issue("agent-2", "sk-b", "api.openai.com");

        advance(TimeSpan.FromMinutes(11)); // agent-1 is due, agent-2 is not

        Assert.Equal(1, creds.RotateStale());
        Assert.NotEqual(first, creds.TokenFor("agent-1"));
        Assert.Equal(second, creds.TokenFor("agent-2"));
    }

    private static (AgentGatewayCredentials Credentials, Action<TimeSpan> Advance) Build(
        Func<string, string, bool>? deliver)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var credentials = new AgentGatewayCredentials(() => now, Interval, Overlap)
        {
            TokenDelivery = deliver,
        };
        return (credentials, span => now = now.Add(span));
    }
}
