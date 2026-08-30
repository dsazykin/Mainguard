using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// The agent document's Send used to be a hardcoded no-op that reported success and typed nothing.
/// These pin the real delivery at the frame level: a short-lived attach that writes the raw-mode
/// selector first, then the prompt + CR, then completes — and propagates the daemon's
/// PermissionDenied (a managed worker's locked terminal) instead of swallowing it.
/// </summary>
public sealed class SendPromptDeliveryTests
{
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private sealed class FakeRequestStream : IClientStreamWriter<Proto.TerminalInput>
    {
        public List<Proto.TerminalInput> Written { get; } = new();
        public bool Completed { get; private set; }
        public Exception? ThrowOnWrite { get; set; }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.TerminalInput message)
        {
            if (ThrowOnWrite is { } ex) return Task.FromException(ex);
            Written.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyResponseStream : IAsyncStreamReader<Proto.TerminalOutput>
    {
        public Proto.TerminalOutput Current => throw new InvalidOperationException();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // Stay pending until the call is cancelled — the real server keeps the read side open.
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }
    }

    private static AsyncDuplexStreamingCall<Proto.TerminalInput, Proto.TerminalOutput> FakeCall(
        FakeRequestStream requests) =>
        new(
            requests,
            new EmptyResponseStream(),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    /// <summary>
    /// A steer at the length the composer really sends. The short literal this used to assert is
    /// submitted correctly even by the encoding that shipped defect J2.
    /// </summary>
    private const string RealisticPrompt =
        "Add one more assertion to test.js covering the empty-input case, then re-run the suite and "
        + "record the result in your mainguard-plan commit.";

    /// <summary>
    /// <b>Defect J2 on the UI side.</b> The prompt body and the CR that submits it go as <b>separate
    /// frames</b>, so they reach the CLI in separate reads. Sent as one frame — which is what this
    /// asserted, at 19 bytes, and passed — the daemon writes body+CR in one go and the CLI takes the CR
    /// as pasted content rather than Enter: short prompts submit, realistic ones silently do not.
    /// Measured against claude-code v2.1.251, §17.8.
    /// </summary>
    [Fact]
    public async Task SendPrompt_WritesTheBodyAndTheCarriageReturnAsSeparateFrames_ThenCompletes()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UncontactedClient());
        var requests = new FakeRequestStream();
        orchestrator.AttachTerminalOverride = _ => FakeCall(requests);

        await orchestrator.SendPromptAsync("agent-1", RealisticPrompt);

        Assert.Equal(3, requests.Written.Count);
        Assert.Equal("agent-1", requests.Written[0].AgentId); // raw-mode selector first

        // The body, carrying NO terminator of its own — there is nothing for the CLI to coalesce.
        Assert.Equal(RealisticPrompt, requests.Written[1].Data.ToStringUtf8());
        Assert.DoesNotContain("\r", requests.Written[1].Data.ToStringUtf8(), StringComparison.Ordinal);

        // Enter, alone, in its own frame.
        Assert.Equal("\r", requests.Written[2].Data.ToStringUtf8());
        Assert.True(requests.Completed);
    }

    /// <summary>
    /// The frames are separated in <b>time</b> as well as in count. Two writes issued back to back are
    /// coalesced by the PTY into a single read and the defect returns intact — measured — so splitting
    /// the frames without the wait would be a fix in appearance only.
    /// </summary>
    [Fact]
    public async Task SendPrompt_HoldsTheTerminatorBack_SoItCannotBeCoalescedWithTheBody()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UncontactedClient());
        var requests = new FakeRequestStream();
        orchestrator.AttachTerminalOverride = _ => FakeCall(requests);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await orchestrator.SendPromptAsync("agent-1", RealisticPrompt);
        started.Stop();

        Assert.True(
            started.Elapsed >= TerminalSubmit.TerminatorSeparation,
            $"the terminator followed the body after only {started.ElapsedMilliseconds}ms — with no "
            + "separation the PTY hands the CLI one read and the CR is swallowed as pasted content");
    }

    [Fact]
    public async Task SendPrompt_PropagatesTheLockedTerminalRefusal_AsAReadableSentence()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UncontactedClient());
        var requests = new FakeRequestStream
        {
            ThrowOnWrite = new RpcException(new Status(StatusCode.PermissionDenied, "input locked")),
        };
        orchestrator.AttachTerminalOverride = _ => FakeCall(requests);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SendPromptAsync("agent-1", "hello"));

        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendPrompt_WithNothingToSay_SendsNothing()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UncontactedClient());
        var requests = new FakeRequestStream();
        orchestrator.AttachTerminalOverride = _ => FakeCall(requests);

        await orchestrator.SendPromptAsync("agent-1", "");

        Assert.Empty(requests.Written);
    }
}
