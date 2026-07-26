using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Mainguard.Agents.Daemon;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// Builds gRPC channels to a REAL Kestrel daemon over the MG-19 mutually-authenticated transport, plus
/// the deliberately-broken variants the transport tests need. The in-proc <see cref="DaemonFixture"/>
/// tier cannot exercise any of this: <c>WebApplicationFactory</c> replaces Kestrel with a
/// <c>TestServer</c>, so the TLS layer configured in <see cref="DaemonHost.Build"/> never runs there.
/// Transport regressions are only observable against a real listener.
/// </summary>
public static class PinnedDaemonChannel
{
    /// <summary>The session directory (holding the token and the transport credentials) for a token path.</summary>
    public static string SessionDirectory(string tokenPath)
        => Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;

    /// <summary>A correct client: presents the pinned client certificate, accepts only the pinned server.</summary>
    public static GrpcChannel Pinned(int port, string tokenPath)
    {
        var credentials = DaemonTransportCredentials.Load(SessionDirectory(tokenPath));
        return Build(port, credentials.BuildSslOptions());
    }

    /// <summary>
    /// Pins the server correctly but presents NO client certificate — the "I hold a valid bearer token
    /// but no peer credential" case that must now be refused.
    /// </summary>
    public static GrpcChannel WithoutClientCertificate(int port, string tokenPath)
    {
        var credentials = DaemonTransportCredentials.Load(SessionDirectory(tokenPath));
        return Build(port, new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                credentials.IsPinnedServerCertificate(certificate),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
    }

    /// <summary>
    /// Presents a well-formed but UNPINNED client certificate (a different daemon session's), proving the
    /// server checks the identity of the peer certificate and not merely that one was supplied.
    /// </summary>
    public static GrpcChannel WithForeignClientCertificate(
        int port, string tokenPath, X509Certificate2 foreignClientCertificate)
    {
        var credentials = DaemonTransportCredentials.Load(SessionDirectory(tokenPath));
        return Build(port, new SslClientAuthenticationOptions
        {
            ClientCertificates = [foreignClientCertificate],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                credentials.IsPinnedServerCertificate(certificate),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
    }

    /// <summary>A cleartext h2c channel — what the daemon used to accept, and must not any more.</summary>
    public static GrpcChannel Plaintext(int port)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
    }

    /// <summary>
    /// A TLS client that presents the pinned client certificate but accepts ANY server certificate.
    /// Used to show what the pin is worth: without it a client would happily hand its bearer token to an
    /// impostor listening on the port.
    /// </summary>
    public static GrpcChannel PinningDisabled(int port, string tokenPath)
    {
        var credentials = DaemonTransportCredentials.Load(SessionDirectory(tokenPath));
        return Build(port, new SslClientAuthenticationOptions
        {
            ClientCertificates = [credentials.ClientCertificate],
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
    }

    private static GrpcChannel Build(int port, SslClientAuthenticationOptions sslOptions)
    {
        var handler = new SocketsHttpHandler { SslOptions = sslOptions };
        return GrpcChannel.ForAddress(
            $"https://127.0.0.1:{port}",
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }
}
