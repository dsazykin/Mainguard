using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The attach-before-bind wait (<see cref="TerminalSessionManager"/>): an attach that races in while
/// the agent is still "Starting" waits for the CLI bind instead of latching into echo — the fix for a
/// coordinator terminal showing echo instead of the live CLI — but never waits when no bind is coming
/// (a session-only agent / the echo path), and never waits forever.
/// </summary>
public sealed class TerminalSessionManagerTests
{
    private const string Repo = "aaaaaaaaaaaa1111";

    private static AgentSessionKey Key(string agentId) => new(Repo, agentId);

    [Fact]
    public async Task WaitForBound_ReturnsTheSession_WhenTheBindLandsDuringTheWait()
    {
        using var mgr = new TerminalSessionManager();
        mgr.MarkBindPending(Key("a1")); // spawn in flight — a bind is coming

        var wait = mgr.WaitForBoundAsync(Key("a1"), CancellationToken.None);

        using var bound = new BoundTerminalSession("a1", new StubSession());
        mgr.Bind(Key("a1"), bound);

        Assert.Same(bound, await wait);
        Assert.False(mgr.IsBindPending(Key("a1"))); // Bind cleared the pending flag
    }

    [Fact]
    public async Task WaitForBound_ReturnsNull_WhenPendingClearsWithoutABind()
    {
        using var mgr = new TerminalSessionManager();
        mgr.MarkBindPending(Key("a2"));

        var wait = mgr.WaitForBoundAsync(Key("a2"), CancellationToken.None);
        mgr.ClearBindPending(Key("a2")); // session-only / bind failed → stop waiting, echo now

        Assert.Null(await wait);
    }

    [Fact]
    public async Task WaitForBound_ReturnsNullImmediately_WhenNoBindIsPending()
    {
        using var mgr = new TerminalSessionManager();
        // No MarkBindPending — the echo path / a session-only agent must never wait.
        Assert.Null(await mgr.WaitForBoundAsync(Key("a3"), CancellationToken.None));
    }

    [Fact]
    public async Task WaitForBound_TimesOut_WhenTheBindNeverLands()
    {
        var prevTimeout = TerminalSessionManager.BindWaitTimeout;
        var prevPoll = TerminalSessionManager.BindWaitPollInterval;
        TerminalSessionManager.BindWaitTimeout = TimeSpan.FromMilliseconds(120);
        TerminalSessionManager.BindWaitPollInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var mgr = new TerminalSessionManager();
            mgr.MarkBindPending(Key("a4")); // pending forever (a hung spawn) — the wait must not hang forever
            Assert.Null(await mgr.WaitForBoundAsync(Key("a4"), CancellationToken.None));
        }
        finally
        {
            TerminalSessionManager.BindWaitTimeout = prevTimeout;
            TerminalSessionManager.BindWaitPollInterval = prevPoll;
        }
    }

    private sealed class StubSession : ITerminalSession
    {
        public Stream IO { get; } = new MemoryStream();
        public Task<int> ExitCode { get; } = new TaskCompletionSource<int>().Task;
        public void Resize(int cols, int rows) { }
        public void Kill() { }
        public void Dispose() { }
    }
}
