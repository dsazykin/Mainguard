using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Daemon;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-19 — the control plane's transport trust boundary.
///
/// <para>The finding: the daemon bound loopback but served cleartext h2c with a bearer token as the
/// <b>sole</b> gate, and under WSL2 <c>localhostForwarding</c> that in-VM listener is reachable from any
/// process in the Windows user's session. The fix is mutually-authenticated TLS with both ends pinned to
/// the daemon session. These tests assert the properties that fix must have, against a REAL Kestrel
/// listener — the <see cref="DaemonFixture"/> in-proc tier swaps Kestrel for a <c>TestServer</c> and
/// therefore cannot observe the transport at all.</para>
///
/// <para>Every test here holds a <b>valid bearer token</b>. That is the point: each one proves the token
/// is no longer sufficient on its own.</para>
/// </summary>
public sealed class DaemonTransportSecurityTests
{
    private static string TempTokenPath()
        => Path.Combine(
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mg-transport-" + Guid.NewGuid().ToString("n"))).FullName,
            "daemon.token");

    /// <summary>
    /// A real Kestrel daemon on a port nothing else in this process was given, retried if a foreign
    /// process steals it before the bind (<see cref="TestDaemonHost"/>). The port is an OUTPUT — none of
    /// these tests care which one they get, only that the daemon and the client agree on it.
    /// </summary>
    private static Task<TestDaemonHost.RunningDaemon> StartDaemonAsync()
        => TestDaemonHost.StartAsync(new DaemonOptions
        {
            LocalDev = true,
            TokenPath = TempTokenPath(),
        });

    private static Metadata Bearer(string token) => new() { { "authorization", $"bearer {token}" } };

    private static Task CallAsync(GrpcChannel channel, string token)
        => new AgentService.AgentServiceClient(channel)
            .ListAgentsAsync(new ListAgentsRequest(), Bearer(token), deadline: DateTime.UtcNow.AddSeconds(15))
            .ResponseAsync;

    /// <summary>
    /// Asserts the call was refused by the TRANSPORT. Every caller of this passes a <b>valid</b> bearer
    /// token, so the refusal cannot be the token gate — which is the whole point of MG-19.
    /// <see cref="StatusCode.Unavailable"/> and <see cref="StatusCode.Internal"/> are the two shapes a
    /// failed TLS handshake surfaces as through Grpc.Net (refused at connect vs. torn down mid-stream);
    /// which one you get is a timing detail, not a security property. A
    /// <see cref="StatusCode.PermissionDenied"/> here would be a real failure: it would mean the peer got
    /// past the handshake and was only stopped by the bearer interceptor.
    /// </summary>
    private static async Task AssertTransportRefusedAsync(GrpcChannel channel, string token)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => CallAsync(channel, token));
        Assert.True(
            ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal,
            $"expected a transport-level refusal, got {ex.StatusCode}: {ex.Status.Detail}");
    }

    // ---------------------------------------------------------------------------------------------
    // Positive control. Keeps every negative test below honest: if the pinned path ever stopped
    // working, the refusals would pass for the wrong reason (nothing can connect at all).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PinnedMutualTls_WithValidToken_Succeeds()
    {
        await using var daemon = await StartDaemonAsync();

        using var channel = PinnedDaemonChannel.Pinned(daemon.Port, daemon.TokenPath);
        var response = await new AgentService.AgentServiceClient(channel).ListAgentsAsync(
            new ListAgentsRequest(), Bearer(daemon.Token), deadline: DateTime.UtcNow.AddSeconds(15));

        Assert.Empty(response.Agents);
    }

    // ---------------------------------------------------------------------------------------------
    // 1. Cleartext h2c is gone. This is the "no downgrade" property: an attacker must not be able to
    //    reach the gRPC dispatcher over plaintext just by declining to speak TLS.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlaintextH2c_WithValidToken_IsRefused()
    {
        await using var daemon = await StartDaemonAsync();

        using var channel = PinnedDaemonChannel.Plaintext(daemon.Port);

        await AssertTransportRefusedAsync(channel, daemon.Token);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. THE CORE PROPERTY. A peer holding a valid bearer token but no client certificate is refused.
    //    Before this change that peer was fully authorized; the token was the whole boundary.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ValidToken_WithoutClientCertificate_IsRefused()
    {
        await using var daemon = await StartDaemonAsync();

        using var channel = PinnedDaemonChannel.WithoutClientCertificate(daemon.Port, daemon.TokenPath);

        await AssertTransportRefusedAsync(channel, daemon.Token);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. The server pins the client's IDENTITY, not merely the presence of a certificate. A foreign
    //    self-signed client certificate (another daemon session's) plus a valid token is refused.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ValidToken_WithUnpinnedClientCertificate_IsRefused()
    {
        await using var daemon = await StartDaemonAsync();

        // A second, independent daemon session's material — well-formed, correctly shaped for client
        // auth, and simply not the certificate this daemon pinned.
        var foreignTokenPath = TempTokenPath();
        File.WriteAllText(foreignTokenPath, "not-the-real-token");
        using var foreignCertificates = SessionTransportCertificates.Create(foreignTokenPath);
        using var foreignClientCertificate = DaemonTransportCredentials.LoadPkcs12(
            File.ReadAllBytes(DaemonTransportFiles.ClientCertificatePath(
                PinnedDaemonChannel.SessionDirectory(foreignTokenPath))));

        using var channel = PinnedDaemonChannel.WithForeignClientCertificate(
            daemon.Port, daemon.TokenPath, foreignClientCertificate);

        await AssertTransportRefusedAsync(channel, daemon.Token);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Anti-port-squatting: the CLIENT pins the server. A rogue listener that claims the port before
    //    the daemon starts must not be able to collect the bearer token. The pin fails the handshake,
    //    so no HTTP/2 frame — and therefore no authorization header — is ever written to the impostor.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Client_RefusesServerCertificateItDidNotPin()
    {
        await using var daemon = await StartDaemonAsync();

        var credentials = DaemonTransportCredentials.Load(PinnedDaemonChannel.SessionDirectory(daemon.TokenPath));

        // An impostor's certificate: correctly formed, self-signed, and not the pinned one.
        var impostorTokenPath = TempTokenPath();
        File.WriteAllText(impostorTokenPath, "impostor");
        using var impostor = SessionTransportCertificates.Create(impostorTokenPath);

        Assert.False(
            credentials.IsPinnedServerCertificate(impostor.ServerCertificate),
            "the client accepted a server certificate it never pinned — a port squatter could harvest the bearer token");

        // ...and the genuine one is accepted, so the refusal above is about identity, not blanket denial.
        using var genuine = X509CertificateLoader.LoadCertificateFromFile(
            DaemonTransportFiles.ServerCertificatePath(PinnedDaemonChannel.SessionDirectory(daemon.TokenPath)));
        Assert.True(credentials.IsPinnedServerCertificate(genuine));
    }

    // ---------------------------------------------------------------------------------------------
    // 5. The credentials are session-scoped: every daemon start mints fresh material, so a stale
    //    certificate captured from an earlier session cannot be replayed against a restarted daemon.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RestartedDaemon_MintsFreshCredentials_StaleOnesAreRefused()
    {
        // The restart is defined by the SESSION (same token path → same credential directory, re-minted),
        // not by the TCP port: each start leases its own, because a port is not a property under test here
        // and re-binding a just-released one is its own source of flake.
        var tokenPath = TempTokenPath();
        var template = new DaemonOptions { LocalDev = true, TokenPath = tokenPath };

        var first = await TestDaemonHost.StartAsync(template);
        var staleCredentials = DaemonTransportCredentials.Load(PinnedDaemonChannel.SessionDirectory(tokenPath));
        var staleFingerprint = staleCredentials.PinnedServerFingerprint;
        await first.DisposeAsync();

        await using var second = await TestDaemonHost.StartAsync(template);
        var freshCredentials = DaemonTransportCredentials.Load(PinnedDaemonChannel.SessionDirectory(tokenPath));

        Assert.NotEqual(staleFingerprint, freshCredentials.PinnedServerFingerprint);

        // The restarted daemon does not accept the previous session's client certificate.
        using var channel = PinnedDaemonChannel.WithForeignClientCertificate(
            second.Port, tokenPath, staleCredentials.ClientCertificate);

        await AssertTransportRefusedAsync(channel, second.Token);
    }

    // ---------------------------------------------------------------------------------------------
    // 6. The client's private key is protected exactly as the token is (0600 on Unix). A world-readable
    //    client certificate would hand the new peer credential to any local user.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TransportCredentialFiles_AreUserOnly()
    {
        var tokenPath = TempTokenPath();
        File.WriteAllText(tokenPath, "token");
        using var certificates = SessionTransportCertificates.Create(tokenPath);

        Assert.True(File.Exists(certificates.ClientCertificatePath));
        Assert.True(File.Exists(certificates.ServerCertificatePath));

        if (OperatingSystem.IsWindows())
        {
            return; // DACL assertions are covered by the token file's own Windows test tier.
        }

        const UnixFileMode groupOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.Equal(default, File.GetUnixFileMode(certificates.ClientCertificatePath) & groupOther);
        Assert.Equal(default, File.GetUnixFileMode(certificates.ServerCertificatePath) & groupOther);
    }

    // ---------------------------------------------------------------------------------------------
    // 7. The client refuses to construct a connection at all when the transport credentials are absent,
    //    rather than silently falling back to the plaintext path it used to use.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MissingTransportCredentials_ThrowRatherThanDowngrade()
    {
        var emptyDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mg-empty-" + Guid.NewGuid().ToString("n"))).FullName;

        var ex = Assert.Throws<InvalidOperationException>(() => DaemonTransportCredentials.Load(emptyDirectory));
        Assert.Contains("will not fall back", ex.Message);
    }
}
