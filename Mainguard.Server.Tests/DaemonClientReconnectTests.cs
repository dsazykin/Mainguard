using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.Daemon;
using Mainguard.Agents.UI.Services;
using Mainguard.Protos.V1;
using Mainguard.Server;
using Mainguard.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Mainguard.Server.Tests;

/// <summary>
/// TI-P2-02 §4/§5 / plan §6 row 4 — the real App <see cref="DaemonClient"/> drives real
/// loopback daemons: it reconnects with backoff after a daemon restart (the socket breaks),
/// resumes the event stream (snapshot-then-deltas), and transitions
/// Connected → Degraded → Connected. Uses real Kestrel hosts so the drop is a genuine
/// transport fault, not an in-memory quirk.
/// </summary>
public sealed class DaemonClientReconnectTests
{
    private static readonly BackoffPolicy FastBackoff =
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), new Random(1));

    [Fact]
    public async Task StreamAgentEvents_ShouldResume_AfterDaemonRestart_AndTransitionStates()
    {
        var tokenPath1 = TempToken();
        var tokenPath2 = TempToken();
        var host1 = await Fixtures.TestDaemonHost.StartAsync(new DaemonOptions { LocalDev = true, TokenPath = tokenPath1 });
        var host2 = await Fixtures.TestDaemonHost.StartAsync(new DaemonOptions { LocalDev = true, TokenPath = tokenPath2 });

        var currentPort = host1.Port;
        var currentTokenPath = tokenPath1;
        var currentToken = host1.Token;
        var token2 = host2.Token;

        // MG-19: the control plane is pinned mutual TLS, so reconnecting means re-reading the TARGET
        // daemon's credentials as well as its token — each host mints its own session material.
        var client = new DaemonClient(
            () => Fixtures.PinnedDaemonChannel.Pinned(
                Volatile.Read(ref currentPort), Volatile.Read(ref currentTokenPath)!),
            () => Volatile.Read(ref currentToken)!,
            FastBackoff);

        var states = new List<ConnectionState>();
        client.ConnectionStateChanged += s => { lock (states) { states.Add(s); } };

        var firstSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0;
        using var cts = new CancellationTokenSource();

        var pump = Task.Run(async () =>
        {
            await foreach (var evt in client.StreamAgentEventsAsync(cts.Token))
            {
                if (evt.EventCase == AgentEvent.EventOneofCase.Snapshot)
                {
                    var n = Interlocked.Increment(ref snapshots);
                    if (n == 1)
                    {
                        firstSnapshot.TrySetResult();
                    }
                    else if (n == 2)
                    {
                        secondSnapshot.TrySetResult();
                    }
                }
            }
        });

        try
        {
            // Connected + first snapshot from host1.
            await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains(ConnectionState.Connected, Snapshot(states));

            // Restart: kill host1 (breaks the socket), point the client at host2.
            await host1.StopAsync();
            await host1.DisposeAsync();
            Volatile.Write(ref currentToken, token2);
            Volatile.Write(ref currentTokenPath, tokenPath2);
            Volatile.Write(ref currentPort, host2.Port);

            // Reconnect + fresh snapshot from host2 (resume).
            await secondSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
        finally
        {
            cts.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(10));
            await host2.StopAsync();
            await host2.DisposeAsync();
            client.Dispose();
        }

        // Connected → Degraded → Connected subsequence.
        AssertSubsequence(Snapshot(states),
            ConnectionState.Connected, ConnectionState.Degraded, ConnectionState.Connected);
    }

    private static List<ConnectionState> Snapshot(List<ConnectionState> states)
    {
        lock (states)
        {
            return new List<ConnectionState>(states);
        }
    }

    private static void AssertSubsequence(IReadOnlyList<ConnectionState> actual, params ConnectionState[] wanted)
    {
        var w = 0;
        foreach (var s in actual)
        {
            if (w < wanted.Length && s == wanted[w])
            {
                w++;
            }
        }

        Assert.True(w == wanted.Length,
            $"expected subsequence [{string.Join(", ", wanted)}] within [{string.Join(", ", actual)}]");
    }

    private static string TempToken()
        => Path.Combine(Path.GetTempPath(), "mainguard-tok-" + Guid.NewGuid().ToString("N"), "daemon.token");
}
