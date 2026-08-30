using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.Agents.Orchestrator;
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
            gap >= TerminalSubmit.TerminatorSeparation,
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

        var started = System.Diagnostics.Stopwatch.StartNew();
        var delivery = await _binder.TrySendPromptAsync(_key, RealisticSteer, CancellationToken.None);
        started.Stop();

        Assert.True(delivery.Submitted);
        Assert.False(delivery.Echoed);

        // It returned fast, so the echo window did NOT lapse — and the writes were separated anyway.
        Assert.True(
            started.Elapsed < AgentCliBinder.PromptEchoWindow,
            "the echo wait was expected to return instantly on a completed stream");

        var writes = cli.Writes;
        Assert.Equal(2, writes.Count);
        var gap = writes[1].At - writes[0].At;
        Assert.True(
            gap >= TerminalSubmit.TerminatorSeparation,
            $"the terminator followed the body after {gap.TotalMilliseconds:0}ms with nothing separating "
            + "them — one read at the CLI, and the CR is content rather than Enter");
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
