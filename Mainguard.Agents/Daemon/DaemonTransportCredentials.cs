using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mainguard.Agents.Daemon;

/// <summary>
/// The file names of the daemon's per-session mTLS material, shared by the server (which writes it)
/// and the client (which reads it) so neither hard-codes the other's layout — the same discipline
/// <see cref="DaemonPaths"/> applies to the session token. The material lives in the SAME directory as
/// <c>daemon.token</c>, which is what lets the client pair a token with the certificates of the very
/// daemon that minted it (see <see cref="DaemonTokenLocator.TryResolveSession"/>).
/// </summary>
public static class DaemonTransportFiles
{
    /// <summary>The daemon's self-signed server certificate, public part only (DER). The client PINS it.</summary>
    public const string ServerCertificateFileName = "daemon-server.cer";

    /// <summary>The client certificate + private key (PKCS#12, no password). Secret: written 0600 / user-only DACL.</summary>
    public const string ClientCertificateFileName = "daemon-client.pfx";

    public static string ServerCertificatePath(string directory)
        => Path.Combine(directory, ServerCertificateFileName);

    public static string ClientCertificatePath(string directory)
        => Path.Combine(directory, ClientCertificateFileName);
}

/// <summary>
/// The client half of the MG-19 control-plane trust boundary: the client certificate this process
/// presents, plus the pinned SHA-256 fingerprint of the server certificate it will accept.
///
/// <para><b>Why this exists (MG-19).</b> The control plane used to be loopback h2c with a bearer token as
/// the <em>sole</em> gate. Under WSL2 <c>localhostForwarding</c> the in-VM <c>127.0.0.1:5250</c> listener is
/// reachable from any process in the Windows user's session (measured, not assumed — see
/// <c>docs/security-architecture.md</c>), so "loopback" bought no isolation, the token crossed the wire in
/// cleartext on every RPC, and a process that squatted the port before the daemon started would be handed
/// that token by a client with no way to tell it was talking to an impostor.</para>
///
/// <para>A Unix-domain socket with <c>SO_PEERCRED</c> — the other option in the finding — was measured and
/// <b>rejected</b>: the daemon runs inside the WSL2 VM and the GUI runs on Windows, and an AF_UNIX socket
/// inside the VM is not connectable from Windows (the <c>\\wsl.localhost</c> 9P share exposes it as a
/// reparse point, and <c>connect()</c> fails <c>WSAENETDOWN</c>). See the doc for the full measurement.</para>
///
/// <para>Fingerprints are SHA-256 over the DER encoding, never the legacy SHA-1
/// <see cref="X509Certificate2.Thumbprint"/> property.</para>
/// </summary>
public sealed class DaemonTransportCredentials : IDisposable
{
    private readonly byte[] _pinnedServerFingerprint;
    private bool _disposed;

    /// <summary>The certificate this client presents to the daemon.</summary>
    public X509Certificate2 ClientCertificate { get; }

    /// <summary>Hex SHA-256 fingerprint of the server certificate this client will accept, and no other.</summary>
    public string PinnedServerFingerprint => Convert.ToHexString(_pinnedServerFingerprint).ToLowerInvariant();

    private DaemonTransportCredentials(X509Certificate2 clientCertificate, byte[] pinnedServerFingerprint)
    {
        ClientCertificate = clientCertificate;
        _pinnedServerFingerprint = pinnedServerFingerprint;
    }

    /// <summary>SHA-256 fingerprint of a certificate's DER encoding.</summary>
    public static byte[] Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.RawData);
    }

    /// <summary>
    /// Loads the material a client needs from <paramref name="directory"/> (the directory holding the
    /// session token). Throws an actionable <see cref="InvalidOperationException"/> when either file is
    /// missing — the client NEVER falls back to plaintext, because a silent downgrade would hand the
    /// bearer token to exactly the impostor this pinning exists to detect.
    /// </summary>
    public static DaemonTransportCredentials Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var serverPath = DaemonTransportFiles.ServerCertificatePath(directory);
        var clientPath = DaemonTransportFiles.ClientCertificatePath(directory);
        if (!File.Exists(serverPath) || !File.Exists(clientPath))
        {
            throw new InvalidOperationException(
                "The Mainguard daemon's transport credentials are missing — the daemon has probably not "
                + $"started since this build was installed. Expected '{serverPath}' and '{clientPath}'. "
                + "Start mainguardd (or run Mainguard setup) and try again. "
                + "The client will not fall back to an unauthenticated connection.");
        }

        using var serverCertificate = X509CertificateLoader.LoadCertificateFromFile(serverPath);
        var clientCertificate = LoadPkcs12(File.ReadAllBytes(clientPath));
        return new DaemonTransportCredentials(clientCertificate, Fingerprint(serverCertificate));
    }

    /// <summary>
    /// Loads a PKCS#12 blob so its private key is usable by <see cref="SslStream"/> on this OS.
    /// On Windows, Schannel cannot use an ephemeral key set for TLS client authentication, so the key
    /// must be loaded into a key set. On macOS, ephemeral keys are not supported at all
    /// (<see cref="X509KeyStorageFlags.EphemeralKeySet"/> throws: Security.framework can only hand
    /// SslStream a keychain-backed key), so the default set is used — .NET imports it into a
    /// process-temporary keychain, not the user's login keychain. Elsewhere an ephemeral
    /// (memory-only) key is both usable and preferable, since it never touches disk.
    /// </summary>
    public static X509Certificate2 LoadPkcs12(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
            : OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable
                : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12(blob, password: null, keyStorageFlags: flags);
    }

    /// <summary>
    /// The pinning predicate: true only when <paramref name="presented"/> is byte-for-byte the
    /// certificate this client pinned. Chain, issuer, host name and validity window are deliberately
    /// NOT consulted — the daemon's certificate is self-signed and session-scoped, so exact-identity
    /// pinning is the control, and a clock skew between the Windows host and the WSL2 VM must never be
    /// able to fail an otherwise-correct connection.
    /// </summary>
    public bool IsPinnedServerCertificate(X509Certificate? presented)
    {
        if (presented is null)
        {
            return false;
        }

        var actual = SHA256.HashData(presented.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(actual, _pinnedServerFingerprint);
    }

    /// <summary>
    /// TLS options for a channel to the daemon: present the client certificate, and accept the server
    /// only when it is the pinned one. Because the pin is enforced during the handshake, a client that
    /// reaches an impostor aborts <b>before</b> any HTTP/2 frame — so the bearer token is never sent to it.
    /// </summary>
    public SslClientAuthenticationOptions BuildSslOptions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SslClientAuthenticationOptions
        {
            ClientCertificates = [ClientCertificate],
            // The pin replaces chain validation outright; see IsPinnedServerCertificate.
            RemoteCertificateValidationCallback = (_, certificate, _, _) => IsPinnedServerCertificate(certificate),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClientCertificate.Dispose();
    }
}
