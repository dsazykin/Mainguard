using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Daemon;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F55 — a second daemon instance must not damage the live one on its way to failing.
///
/// <para>The pre-fix shape: <c>ConfigureServices</c> minted AND WROTE the session token and the mTLS
/// material, and the port bind happened much later, in <c>app.Run()</c>. So a second <c>mainguardd</c>
/// against the same data root rotated <c>daemon.token</c>, <c>daemon-server.cer</c> and
/// <c>daemon-client.pfx</c> — the live daemon's credentials — and only afterwards discovered the port was
/// taken and exited. Every client then presented a client certificate the survivor does not pin, so the
/// TLS handshake failed before a single HTTP/2 frame, and the operator saw a healthy-looking daemon that
/// nothing could talk to. <c>KeepAlive=true</c> in the LaunchAgent re-ran the whole sequence on every
/// respawn.</para>
///
/// <para>These tests assert the two halves of the fix: the credentials are written only after the port is
/// won, and a second instance against the same data root refuses itself outright.</para>
/// </summary>
public sealed class DaemonSecondInstanceTests
{
    [Fact]
    public async Task SecondDaemon_LosingThePortRace_DoesNotRotateTheLiveTokenOrCertificates()
    {
        var tokenPath = TestDaemonHost.TempTokenPath("mainguard-f55");
        var directory = Path.GetDirectoryName(tokenPath)!;

        await using var live = await TestDaemonHost.StartAsync(
            new DaemonOptions { LocalDev = true, TokenPath = tokenPath });

        var tokenBefore = await File.ReadAllTextAsync(tokenPath);
        var serverCertBefore = await File.ReadAllBytesAsync(
            DaemonTransportFiles.ServerCertificatePath(directory));
        var clientCertBefore = await File.ReadAllBytesAsync(
            DaemonTransportFiles.ClientCertificatePath(directory));

        Assert.Equal(live.Token, tokenBefore);

        // A second daemon aimed at the SAME port and the SAME data root — the exact collision the
        // finding describes (a launchd respawn, a second app instance, an operator running the daemon by
        // hand next to the installed one).
        var loser = new DaemonOptions { LocalDev = true, TokenPath = tokenPath, Port = live.Port };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var second = await DaemonHost.StartAsync(loser, CancellationToken.None);
        });

        // Nothing the loser touched. Byte-for-byte, because "a fresh token that happens to look similar"
        // is precisely the failure this is about.
        Assert.Equal(tokenBefore, await File.ReadAllTextAsync(tokenPath));
        Assert.Equal(
            serverCertBefore,
            await File.ReadAllBytesAsync(DaemonTransportFiles.ServerCertificatePath(directory)));
        Assert.Equal(
            clientCertBefore,
            await File.ReadAllBytesAsync(DaemonTransportFiles.ClientCertificatePath(directory)));

        // And the live daemon is still the one those credentials belong to.
        Assert.Equal(live.Token, await File.ReadAllTextAsync(tokenPath));
    }

    /// <summary>
    /// The other half: a second daemon on a DIFFERENT port against the same data root. It binds fine —
    /// there is no port race at all — so nothing about the port could ever have caught it. The
    /// single-instance lock does.
    /// </summary>
    [Fact]
    public async Task SecondDaemon_OnADifferentPort_IsRefusedByTheInstanceLock()
    {
        var tokenPath = TestDaemonHost.TempTokenPath("mainguard-f55-lock");

        await using var live = await TestDaemonHost.StartAsync(
            new DaemonOptions { LocalDev = true, TokenPath = tokenPath });

        var tokenBefore = await File.ReadAllTextAsync(tokenPath);

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var second = await TestDaemonHost.StartAsync(
                new DaemonOptions { LocalDev = true, TokenPath = tokenPath });
        });

        Assert.Contains(
            Flatten(failure),
            message => message.Contains("already running", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(tokenBefore, await File.ReadAllTextAsync(tokenPath));
    }

    /// <summary>
    /// The lock is an OS lock, so it survives whatever the holder does — including dying without
    /// cleaning up. This pins the mechanism directly: a held lock refuses a second acquire, and releasing
    /// it lets the next one through.
    /// </summary>
    [Fact]
    public void InstanceLock_RefusesASecondAcquire_AndReleasesOnDispose()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "mainguard-instance-lock", Guid.NewGuid().ToString("N"));

        var first = DaemonInstanceLock.Acquire(directory);
        try
        {
            Assert.Throws<DaemonAlreadyRunningException>(() => DaemonInstanceLock.Acquire(directory));
        }
        finally
        {
            first.Dispose();
        }

        // Released — the next daemon may start.
        using var second = DaemonInstanceLock.Acquire(directory);
        Assert.True(File.Exists(DaemonInstanceLock.PathIn(directory)));
    }

    /// <summary>
    /// The lock file name is spelled in two assemblies — the daemon owns it, and
    /// <c>MacDaemonController</c> probes it to answer "is a daemon running" without a <c>pgrep</c>
    /// check-then-act. <c>Mainguard.Agents</c> cannot reference <c>Mainguard.Server</c> (the dependency
    /// runs the other way), so this test — in the one project that sees both — is what stops the
    /// duplication from drifting into a controller that probes a file nothing writes.
    /// </summary>
    [Fact]
    public void InstanceLockFileName_IsTheSameOnBothSidesOfTheAssemblyBoundary()
    {
        Assert.Equal(
            DaemonInstanceLock.FileName,
            Mainguard.Agents.Agents.Bootstrap.MacDaemonController.InstanceLockFileName);
    }

    private static string[] Flatten(Exception error)
    {
        var messages = new System.Collections.Generic.List<string>();
        for (var current = error; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    messages.AddRange(Flatten(inner));
                }
            }
        }

        return messages.ToArray();
    }
}
