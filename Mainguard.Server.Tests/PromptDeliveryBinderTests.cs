using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Terminal;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The delivery layer of <c>send_worker_prompt</c> on its own — <see cref="AgentCliBinder"/> with a
/// terminal bound over a <see cref="RawModeCliDouble"/> and no IPC surface in front of it.
///
/// <para><b>Why separately from <see cref="CoordinatorToolPositivesTests"/>.</b> One guard here is
/// unreachable through the IPC surface by design: <c>AgentSpawnService.PromptAsync</c> rejects a blank
/// prompt with a usage sentence before the binder is ever called, so the binder's own refusal is
/// defence in depth — and a guard no test can turn red is indistinguishable from a guard that was
/// deleted. This file reaches it directly, so it stays mutation-checked.</para>
/// </summary>
public sealed class PromptDeliveryBinderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mg-prompt-delivery-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly TerminalSessionManager _terminals = new();
    private readonly AgentCliBinder _binder;
    private readonly AgentSessionKey _key = new("repo-hash", "pr-7");

    /// <summary>
    /// A steer at the length a coordinator really sends. The short literal this file used to pass
    /// ("narrow the try block") is submitted correctly even by the encoder that shipped defect J2 —
    /// length is the variable the defect lives on, so the fixture has to carry it.
    /// </summary>
    private const string RealisticSteer =
        "Add one more assertion to test.js covering the empty-input case, then re-run the suite and "
        + "record the result in your mainguard-plan commit.";

    /// <summary>
    /// The floor below is compared with one timer quantum of slack, and that is <b>not</b> a loosened
    /// tolerance hiding a real gap.
    ///
    /// <para><c>BoundTerminalSession</c> separates the two writes with <c>Task.Delay(TerminatorSeparation)</c>,
    /// which completes on the runtime's timer wheel; <see cref="RawModeCliDouble"/> stamps each write with
    /// <c>DateTime.UtcNow</c>, which is the system clock. The two need not agree to the millisecond, and a
    /// delay that really did elapse can be measured at marginally under its own length — CI read <b>50 ms</b>
    /// for a 50 ms separation and failed the <c>&gt;=</c> by less than half a millisecond, which is the
    /// assertion breaking at its own floor rather than the code failing to separate anything.</para>
    ///
    /// <para>What these assertions distinguish is "separated by the deliberate wait" from "not separated at
    /// all", and the unseparated case is two writes issued back to back — sub-millisecond, as the mutation
    /// check confirms when the fallback is deleted from <c>SubmitLineAndAwaitOutputAsync</c>. 5 ms is ten
    /// times the shortfall ever observed and a tenth of the separation, so it cannot mask the defect and
    /// does stop two disagreeing clocks failing a correct run.</para>
    /// </summary>
    private static readonly TimeSpan ClockQuantum = TimeSpan.FromMilliseconds(5);

    /// <summary>The separation floor as an assertion can honestly measure it — see <see cref="ClockQuantum"/>.</summary>
    private static readonly TimeSpan MeasurableSeparationFloor =
        TerminalSubmit.TerminatorSeparation - ClockQuantum;

    public PromptDeliveryBinderTests()
    {
        Directory.CreateDirectory(_root);
        var audit = new InMemoryAuditLog();
        _binder = new AgentCliBinder(
            _terminals,
            new SessionLeader(new LeaderRegistry(Path.Combine(_root, "leader.json"))),
            new AgentSessionStore(audit),
            audit);
    }

    public void Dispose()
    {
        _terminals.Release(_key);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A prompt that is nothing but whitespace is <b>refused</b>, and the CLI is left untouched.
    ///
    /// <para>The alternative — encoding it anyway — writes a bare CR, which is not a no-op: it is Enter,
    /// pressed at whatever the CLI currently has focused. A worker sitting on a permission dialog would
    /// have its highlighted option confirmed by a steer that said nothing.</para>
    /// </summary>
    [Fact]
    public async Task AWhitespaceOnlyPrompt_IsRefused_AndNoEnterIsPressedAtTheCli()
    {
        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        var delivery = await _binder.TrySendPromptAsync(_key, "  \t \n ", CancellationToken.None);

        Assert.False(delivery.Submitted);
        Assert.Contains("nothing to submit", delivery.Refusal ?? string.Empty, StringComparison.Ordinal);

        // The CLI saw no keystroke at all — not an empty line, not an Enter.
        Assert.Empty(cli.SubmittedLines);
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The control for the test above: real text through the same call IS submitted, so the refusal is
    /// the guard doing its job rather than the delivery path being inert.
    /// </summary>
    [Fact]
    public async Task RealText_ThroughTheSameCall_IsSubmittedAsALine()
    {
        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);

        Assert.True(delivery.Submitted);
        Assert.Null(delivery.Refusal);
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// The body is observed being consumed <b>before</b> Enter is pressed — which is what makes the CR a
    /// keystroke of its own rather than the tail of a paste, and is therefore the daemon's runtime
    /// detector for a J2 regression.
    ///
    /// <para>A CLI that repaints reports <c>Echoed</c>; a silent one reports it false and the delivery
    /// still succeeds, because the fallback is a timed separation rather than a refusal — a worker that
    /// was merely mid-turn must not be reported unreachable. Both readings submit.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBodyIsConsumedBeforeEnterIsPressed_AndThatIsReportedSeparately(bool redraws)
    {
        using var cli = new RawModeCliDouble(redraws);
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);

        Assert.True(delivery.Submitted);
        Assert.Equal(redraws, delivery.Echoed);
        Assert.Equal(redraws, delivery.Reacted);

        // Whichever way the observation went, the line was submitted and nothing was stranded.
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, cli.PendingInput);
    }

    /// <summary>
    /// When the CLI gives the daemon <b>no echo to key off</b>, the terminator is held back by
    /// <see cref="TerminalSubmit.TerminatorSeparation"/> instead.
    ///
    /// <para>The preferred separator is causal — a CLI that repainted has already read the body, so the
    /// CR cannot arrive in the same read. A silent CLI (mid-turn, not repainting its input line) offers
    /// nothing to wait on, and two writes issued back to back are coalesced by the PTY into one read,
    /// which is defect J2 intact. So the fallback is not a nicety; without it the silent case is exactly
    /// the broken case. Asserted as a floor only, so there is no upper bound to be flaky about.</para>
    /// </summary>
    [Fact]
    public async Task WithNoEchoToWaitOn_TheTerminatorIsStillSeparatedFromTheBody()
    {
        using var cli = new RawModeCliDouble(redraws: false);
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);

        Assert.True(delivery.Submitted);
        Assert.False(delivery.Echoed);

        // Measured BETWEEN THE TWO READS at the CLI, not around the call: a caller that idled before
        // writing anything would satisfy an outer stopwatch while still handing the CLI a single read.
        var writes = cli.Writes;
        Assert.Equal(2, writes.Count);
        var gap = writes[1].At - writes[0].At;
        Assert.True(
            gap >= MeasurableSeparationFloor,
            $"Enter followed the body after only {gap.TotalMilliseconds:0}ms with no echo to separate "
            + "them — the PTY would hand the CLI a single read and the CR would be swallowed as content");

        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The case the fallback actually exists for: a CLI whose output stream has already completed.
    ///
    /// <para>The echo wait returns false <b>immediately</b> then — the stream is done, there is nothing
    /// left to wait for — so unlike the ordinary no-echo path it reaches the terminator having waited no
    /// time at all. Without an explicit separation the two writes go out back to back, which is one read
    /// at the CLI and defect J2 intact. This is what makes the fallback a guard rather than dead code:
    /// on the common path the lapsed 250 ms echo window has already separated them, which is why
    /// removing it survived the first mutation pass.</para>
    /// </summary>
    [Fact]
    public async Task WhenTheEchoWaitReturnsInstantly_TheTerminatorIsStillHeldBack()
    {
        using var cli = new RawModeCliDouble();
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        cli.Kill(); // completes the output stream: the echo wait now returns false with no delay at all

        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);

        Assert.True(delivery.Submitted);
        Assert.False(delivery.Echoed);

        // BOTH halves are read off the interval BETWEEN THE TWO WRITES, which is the only interval either
        // half is about. An outer stopwatch around the whole call also times the reaction wait, the
        // fixture and every scheduling stall on the machine, and that is what made this test fail on a
        // loaded CI runner — inside a jail, four xUnit threads deep — while the code path it exists to pin
        // was exactly right. Timing the call to conclude something about one leg of it was the defect in
        // the assertion, not a tolerance that needed widening.
        var writes = cli.Writes;
        Assert.Equal(2, writes.Count);
        var gap = writes[1].At - writes[0].At;

        // (1) The writes were separated at all — the property under test.
        Assert.True(
            gap >= MeasurableSeparationFloor,
            $"the terminator followed the body after {gap.TotalMilliseconds:0}ms with nothing separating "
            + "them — one read at the CLI, and the CR is content rather than Enter");

        // (2) And it was the FALLBACK that separated them, not a lapsed echo window — without which this
        // test would pass on the 250 ms window alone and say nothing about the fallback being a guard
        // rather than dead code. A lapsed window cannot produce a gap below its own length.
        Assert.True(
            gap < AgentCliBinder.PromptEchoWindow,
            $"the two writes were {gap.TotalMilliseconds:0}ms apart, which is the echo window rather than "
            + "the fallback — the stream was expected to be completed, so the echo wait should have "
            + "returned instantly and this test proves nothing about the fallback");
    }

    /// <summary>
    /// The case the echo-gated fallback missed: a CLI that is <b>mid-turn</b> streams output whether or
    /// not it has read anything, so the echo wait returns true within a millisecond on an unsolicited
    /// frame. With the separation applied only when nothing echoed, the CR then went out straight behind
    /// the body — one read at the CLI, and J2 intact on precisely the worker a coordinator most wants to
    /// steer. The separation is a floor in every case; the echo is still reported for what it is.
    /// </summary>
    [Fact]
    public async Task ABusyCliThatEchoesUnsolicited_StillGetsTheTerminatorInAReadOfItsOwn()
    {
        using var cli = new RawModeCliDouble(redraws: true, chatty: true);
        using var bound = new BoundTerminalSession(_key.AgentId, cli);
        _terminals.Bind(_key, bound);

        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);

        Assert.True(delivery.Submitted);
        Assert.True(delivery.Echoed, "the chatty double is meant to satisfy the echo wait on its own output");

        var writes = cli.Writes;
        Assert.Equal(2, writes.Count);
        var gap = writes[1].At - writes[0].At;
        Assert.True(
            gap >= MeasurableSeparationFloor,
            $"Enter followed the body after only {gap.TotalMilliseconds:0}ms because an unsolicited frame "
            + "counted as the echo — the PTY would hand the CLI a single read and the CR would be swallowed");
        Assert.Equal(new[] { RealisticSteer }, await cli.WaitForSubmittedAsync(1, TimeSpan.FromSeconds(5)));
    }

    /// <summary>No bound CLI: nothing is claimed, and the caller supplies the "no live CLI" sentence.</summary>
    [Fact]
    public async Task WithNoBoundCli_NothingIsClaimed()
    {
        var delivery = await _binder.TrySendPromptAsync(_key, "steer", CancellationToken.None);

        Assert.False(delivery.Submitted);
        Assert.False(delivery.Reacted);
        Assert.Null(delivery.Refusal);
    }
}
