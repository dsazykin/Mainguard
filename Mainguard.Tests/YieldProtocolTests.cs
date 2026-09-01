using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-09 tests 1 & 2 (pure, fakes): the cooperative-yield handshake. The ready path completes
/// without a pause and asserts the request-before-ready ordering; the timeout path invokes
/// <c>docker pause</c> before returning the token, and the token's resume unpauses.
/// </summary>
public sealed class YieldProtocolTests
{
    [Fact]
    public async Task Yield_ReadyPath_RoundTrip_NoPause()
    {
        var channel = new RecordingChannel(readyAnswer: true);
        var sandbox = new RecordingSandbox();
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(50));

        using var token = await protocol.RequestYieldAsync("a1");

        Assert.Equal(YieldOutcome.ByReady, token.Outcome);
        Assert.True(token.IsActive);
        // Ordering: the request marker was sent, then the ready ack was awaited.
        Assert.Equal(new[] { YieldProtocol.UpdateRequested }, channel.Sent);
        Assert.True(channel.RequestedBeforeWait);
        // No pause on the cooperative path.
        Assert.Equal(0, sandbox.PauseCount);
    }

    [Fact]
    public async Task Yield_Timeout_PausePath_ThenResumeUnpauses()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var sandbox = new RecordingSandbox();
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(10));

        var token = await protocol.RequestYieldAsync("a1");

        // docker pause was invoked before the token (and thus before any mutation) is handed back.
        Assert.Equal(YieldOutcome.ByPause, token.Outcome);
        Assert.Equal(1, sandbox.PauseCount);
        Assert.Equal("container-1", sandbox.LastPaused);
        Assert.Equal(0, sandbox.UnpauseCount);

        token.Resume();

        Assert.False(token.IsActive);
        Assert.Equal(1, sandbox.UnpauseCount);

        // Resume is idempotent.
        token.Resume();
        Assert.Equal(1, sandbox.UnpauseCount);
    }

    // ---- The human-pause arbiter (the cascade's pause vs the human's pause) ----

    [Fact]
    public async Task Yield_OverAHumanPausedJail_SkipsThePause_AndNeverWakesItOnResume()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var sandbox = new RecordingSandbox();
        var arbiter = new FakeArbiter { HumanPaused = true };
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(10), arbiter: arbiter);

        var token = await protocol.RequestYieldAsync("a1");

        // The jail is already frozen by the human — pausing again is skipped, but the machine's
        // critical section is still marked (a human unpause mid-rebase must be refusable).
        Assert.Equal(0, sandbox.PauseCount);
        Assert.Equal(1, arbiter.Holds);

        token.Resume();

        // THE rule: the machine never wakes a human-paused jail on its way out — and the hold clears.
        Assert.Equal(0, sandbox.UnpauseCount);
        Assert.Equal(0, arbiter.Holds);
    }

    [Fact]
    public async Task HumanPause_DuringTheMachineHold_IsHonoredAtResumeTime()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var sandbox = new RecordingSandbox();
        var arbiter = new FakeArbiter { HumanPaused = false };
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(10), arbiter: arbiter);

        var token = await protocol.RequestYieldAsync("a1");
        Assert.Equal(1, sandbox.PauseCount); // not human-paused at capture time → the machine paused

        // The human pauses WHILE the machine holds the jail. Checked at RESUME time, not capture time.
        arbiter.HumanPaused = true;
        token.Resume();

        Assert.Equal(0, sandbox.UnpauseCount);
        Assert.Equal(0, arbiter.Holds);
    }

    /// <summary>
    /// The conflict path's terminus: hand the machine's critical section back and leave the jail frozen.
    ///
    /// <para>Both halves are the point. The hold MUST go — <c>AgentPauseService.UnpauseAsync</c> refuses
    /// while one is outstanding, with a sentence ("the daemon is briefly holding this agent for a queue
    /// update — try again in a moment") whose entire promise is that it self-clears, so a hold that
    /// outlives the cycle refuses the human's unpause button forever on exactly the agents that need a
    /// human. And the jail MUST stay frozen — the worktree is parked mid-rebase, and waking the agent
    /// under it is what the conflict arm exists not to do.</para>
    /// </summary>
    [Fact]
    public async Task ReleaseWithoutResuming_HandsBackTheMachineHold_ButLeavesTheJailFrozen()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var sandbox = new RecordingSandbox();
        var arbiter = new FakeArbiter { HumanPaused = false };
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(10), arbiter: arbiter);

        var token = await protocol.RequestYieldAsync("a1");
        Assert.Equal(1, sandbox.PauseCount);
        Assert.Equal(1, arbiter.Holds);

        token.ReleaseWithoutResuming();

        Assert.Equal(0, arbiter.Holds);
        Assert.Equal(0, sandbox.UnpauseCount);
        // Settled either way: the mutation gateway closes exactly as a resume would have closed it, so
        // nothing can reach the parked worktree through this token afterwards.
        Assert.False(token.IsActive);

        // Idempotent, and mutually exclusive with Resume — a later Resume must not wake a jail that was
        // deliberately left frozen, nor underflow another holder's count.
        token.ReleaseWithoutResuming();
        token.Resume();
        Assert.Equal(0, arbiter.Holds);
        Assert.Equal(0, sandbox.UnpauseCount);
    }

    private sealed class FakeArbiter : IPauseArbiter
    {
        public bool HumanPaused { get; set; }
        public int Holds { get; private set; }

        public bool IsHumanPaused(string agentId) => HumanPaused;

        public IDisposable HoldForMachine(string agentId)
        {
            Holds++;
            return new Release(this);
        }

        private sealed class Release : IDisposable
        {
            private FakeArbiter? _owner;
            public Release(FakeArbiter owner) => _owner = owner;
            public void Dispose()
            {
                var o = Interlocked.Exchange(ref _owner, null);
                if (o is not null) o.Holds--;
            }
        }
    }

    [Fact]
    public async Task Yield_Timeout_NoLiveContainer_Throws()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var protocol = new YieldProtocol(_ => channel, new RecordingSandbox(), _ => null,
            defaultTimeout: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.RequestYieldAsync("a1"));
    }

    private sealed class RecordingChannel : IAgentControlChannel
    {
        private readonly bool _readyAnswer;
        private bool _sent;

        public RecordingChannel(bool readyAnswer) => _readyAnswer = readyAnswer;

        public List<string> Sent { get; } = new();

        public bool RequestedBeforeWait { get; private set; }

        public Task SendAsync(string marker, CancellationToken ct = default)
        {
            Sent.Add(marker);
            _sent = true;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForAsync(string marker, TimeSpan timeout, CancellationToken ct = default)
        {
            RequestedBeforeWait = _sent;
            return Task.FromResult(_readyAnswer);
        }
    }

    private sealed class RecordingSandbox : ISandboxEngine
    {
        public int PauseCount { get; private set; }

        public int UnpauseCount { get; private set; }

        public string? LastPaused { get; private set; }

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            PauseCount++;
            LastPaused = containerId;
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            UnpauseCount++;
            return Task.CompletedTask;
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
