using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Mainguard.App.Shell.Services;

namespace Mainguard.Tests;

/// <summary>
/// Client-side thin twin of TI-P2-02 §9 (RpcWithoutDeadline) + the token-file auth path.
/// The wrong-token → PermissionDenied twin runs server-side in Mainguard.Server.Tests.
/// </summary>
public sealed class DaemonAuthTests
{
    // TI.9 — RpcWithoutDeadline_ShouldBeImpossible: every DaemonClient RPC method takes a
    // CancellationToken, so no call site can omit a deadline/cancellation path.
    [Fact]
    public void EveryRpcMethod_ShouldRequireCancellationToken()
    {
        var rpcMethods = typeof(DaemonClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(IsRpcMethod)
            .ToList();

        Assert.NotEmpty(rpcMethods);
        foreach (var method in rpcMethods)
        {
            Assert.Contains(method.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
        }
    }

    [Fact]
    public void ForLoopback_MissingTokenFile_ShouldThrowWhenReadingToken()
    {
        // MG-19: give the client valid transport credentials so the ONLY thing missing is the token —
        // otherwise the credential gate would fire first and this would stop testing the token read.
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-missing-" + Guid.NewGuid().ToString("N"));
        TestTools.DaemonTransportMaterial.Write(dir);
        var missing = Path.Combine(dir, "daemon.token");
        using var client = DaemonClient.ForLoopback(FreePort(), missing);

        // The token is read from the file when the call builds its auth metadata — a
        // missing file surfaces as an IO error, proving the client reads the token file.
        Assert.ThrowsAny<IOException>(() =>
        {
            try
            {
                client.ListAgentsAsync(CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (AggregateException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        });
    }

    [Fact]
    public async Task ForLoopback_WithTokenFile_ShouldReadTokenAndFailAtNetwork_NotAtTokenRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-tok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "daemon.token");
        await File.WriteAllTextAsync(path, "deadbeef");
        TestTools.DaemonTransportMaterial.Write(dir); // MG-19: a complete session, so only the daemon is absent

        using var client = DaemonClient.ForLoopback(FreePort(), path);

        // Token read succeeds; the failure is the dead network (RpcException), not an IO error.
        await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(CancellationToken.None, TimeSpan.FromSeconds(2)));
    }

    // MG-19 — the client must refuse to build a connection when the transport credentials are absent,
    // rather than silently reverting to the cleartext h2c channel it used to open. A downgrade here
    // would hand the bearer token to whatever process happened to be listening on the port.
    [Fact]
    public void ForLoopback_MissingTransportCredentials_ShouldThrow_NotDowngradeToPlaintext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-nocreds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "daemon.token");
        File.WriteAllText(path, "deadbeef"); // a valid token, and deliberately nothing else

        using var client = DaemonClient.ForLoopback(FreePort(), path);

        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            try
            {
                client.ListAgentsAsync(CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (AggregateException aggregate) when (aggregate.InnerException is not null)
            {
                throw aggregate.InnerException;
            }
        });

        Assert.Contains("will not fall back", ex.Message);
    }

    private static bool IsRpcMethod(MethodInfo method)
    {
        var t = method.ReturnType;
        if (t == typeof(Task))
        {
            return true;
        }

        if (!t.IsGenericType)
        {
            return false;
        }

        var def = t.GetGenericTypeDefinition();
        return def == typeof(Task<>) || def == typeof(IAsyncEnumerable<>);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
