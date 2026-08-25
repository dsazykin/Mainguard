using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
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

    [Fact]
    public async Task SendPrompt_WritesSelectorThenPromptWithCarriageReturn_ThenCompletes()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UncontactedClient());
        var requests = new FakeRequestStream();
        orchestrator.AttachTerminalOverride = _ => FakeCall(requests);

        await orchestrator.SendPromptAsync("agent-1", "fix the failing test");

        Assert.Equal(2, requests.Written.Count);
        Assert.Equal("agent-1", requests.Written[0].AgentId); // raw-mode selector first
        Assert.Equal("fix the failing test\r", requests.Written[1].Data.ToStringUtf8());
        Assert.True(requests.Completed);
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
