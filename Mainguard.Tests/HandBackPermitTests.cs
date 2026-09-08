using System;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F44 (residual) — the conflict hand-back permit really is worth one rewrite.</b>
///
/// <para>"Let the agent resolve" is a human authorising ONE rewrite of published history, which is the
/// single exception to the ref mediator's fast-forward rule. The mark that carries that authorisation
/// had neither of the properties the word "one" implies: no expiry, so Monday's decision still
/// permitted Friday's rewrite against a mirror nobody had looked at; and consumption only on the
/// publish that actually took the rewrite path, so an ordinary fast-forward publish of the same branch
/// left it armed behind itself.</para>
///
/// <para>The expiry half is pinned here, on the store, with an injected clock. The consumption half is
/// the mediator's, and is pinned against real git in
/// <c>AgentRefMediationTests.Publish_FastForward_SpendsAnArmedHandBackPermit</c>.</para>
/// </summary>
public class HandBackPermitTests
{
    private static readonly DateTimeOffset Granted = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private const string Repo = "abc123def456";
    private const string Agent = "a1";

    private static (RebaseConflictParkingStore Store, Func<DateTimeOffset> Set) NewStore()
    {
        var now = Granted;
        var store = new RebaseConflictParkingStore(() => now);
        return (store, () => now);
    }

    [Fact]
    public void AFreshHandBack_PermitsTheRewrite()
    {
        var now = Granted;
        var store = new RebaseConflictParkingStore(() => now);
        store.MarkHandedBack(Repo, Agent);

        Assert.True(store.IsHandedBack(Repo, Agent));
    }

    [Fact]
    public void AHandBackStillPermitsTheRewrite_RightUpToItsLifetime()
    {
        var now = Granted;
        var store = new RebaseConflictParkingStore(() => now);
        store.MarkHandedBack(Repo, Agent);

        now = Granted + RebaseConflictParkingStore.HandBackLifetime - TimeSpan.FromMinutes(1);

        Assert.True(store.IsHandedBack(Repo, Agent));
    }

    /// <summary>The residual finding itself: an authorisation nobody acted on must stop authorising.</summary>
    [Fact]
    public void AHandBackNobodyActedOn_StopsPermittingTheRewriteOnceItExpires()
    {
        var now = Granted;
        var store = new RebaseConflictParkingStore(() => now);
        store.MarkHandedBack(Repo, Agent);

        now = Granted + RebaseConflictParkingStore.HandBackLifetime + TimeSpan.FromMinutes(1);

        Assert.False(store.IsHandedBack(Repo, Agent));
    }

    /// <summary>Expiry is not a latch: a human who decides again gets a fresh grant, not a dead one.</summary>
    [Fact]
    public void AHandBackGrantedAgainAfterAnExpiry_PermitsTheRewriteAgain()
    {
        var now = Granted;
        var store = new RebaseConflictParkingStore(() => now);
        store.MarkHandedBack(Repo, Agent);

        now = Granted + RebaseConflictParkingStore.HandBackLifetime + TimeSpan.FromHours(1);
        Assert.False(store.IsHandedBack(Repo, Agent));

        store.MarkHandedBack(Repo, Agent);
        Assert.True(store.IsHandedBack(Repo, Agent));
    }

    [Fact]
    public void ConsumingTheMark_DisarmsIt()
    {
        var (store, _) = NewStore();
        store.MarkHandedBack(Repo, Agent);

        Assert.True(store.ClearHandedBack(Repo, Agent));
        Assert.False(store.IsHandedBack(Repo, Agent));
        Assert.False(store.ClearHandedBack(Repo, Agent));
    }

    /// <summary>The key is the (repo, agent) PAIR — the external-PR intake names its entries
    /// <c>pr-&lt;n&gt;</c>, so two subscribed repositories both hold a <c>pr-7</c>, and one repo's
    /// authorisation must never excuse a rewrite in another.</summary>
    [Fact]
    public void AHandBackInOneRepository_DoesNotPermitARewriteInAnother()
    {
        var (store, _) = NewStore();
        store.MarkHandedBack("repo-one", "pr-7");

        Assert.True(store.IsHandedBack("repo-one", "pr-7"));
        Assert.False(store.IsHandedBack("repo-two", "pr-7"));
    }

    [Fact]
    public void NoHandBackAtAll_PermitsNothing()
    {
        var (store, _) = NewStore();
        Assert.False(store.IsHandedBack(Repo, Agent));
    }
}
