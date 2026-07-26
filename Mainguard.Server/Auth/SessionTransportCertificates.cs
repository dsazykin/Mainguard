using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Mainguard.Agents.Daemon;

namespace Mainguard.Server.Auth;

/// <summary>
/// MG-19: the daemon's per-session mTLS material — the peer-authentication layer that stops the bearer
/// token from being the sole gate on the control plane.
///
/// <para>On every start the daemon mints two fresh self-signed P-256 certificates: a <b>server</b>
/// certificate Kestrel presents on the loopback gRPC listener, and a <b>client</b> certificate it will
/// accept and no other. The client certificate (with its private key) and the server certificate (public
/// part only) are written beside <c>daemon.token</c> with the same user-only permissions the token gets —
/// <c>0600</c> on Unix, an inheritance-free single-ACE DACL on Windows.</para>
///
/// <para><b>What this closes, honestly.</b> Three things the token alone could not:</para>
/// <list type="number">
///   <item><b>Cleartext token on the wire.</b> Every RPC used to carry the bearer token in plaintext h2c
///   across loopback and the WSL2 relay, harvestable by anything able to observe local traffic. It is now
///   inside TLS.</item>
///   <item><b>Port squatting.</b> A client used to hand its token to whatever answered on
///   <c>127.0.0.1:5250</c>. An unprivileged process that binds the port before mainguardd does could
///   collect the operator token on the first RPC. The client now pins this server certificate, so the
///   handshake with an impostor fails <em>before</em> a single HTTP/2 frame is written.</item>
///   <item><b>Unauthenticated reachability.</b> A peer with no pinned client certificate is rejected
///   during the TLS handshake — it never reaches the HTTP/2 parser, the gRPC dispatcher, or
///   <see cref="BearerTokenInterceptor"/>.</item>
/// </list>
///
/// <para><b>What it does not close.</b> A process running as the <em>same OS user</em> that can read the
/// token file can also read <c>daemon-client.pfx</c> beside it. No local transport defeats a same-uid
/// attacker — a <c>0600</c> Unix socket with <c>SO_PEERCRED</c> would not have either, since the peer uid
/// would match. The bar moves from "read one file" to "read two files and complete a mutual handshake",
/// and the remote-ish vectors above close outright; that is the accurate claim.</para>
/// </summary>
public sealed class SessionTransportCertificates : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private readonly byte[] _pinnedClientFingerprint;
    private bool _disposed;

    /// <summary>The certificate Kestrel presents on the control-plane listener (has its private key).</summary>
    public X509Certificate2 ServerCertificate { get; }

    /// <summary>Directory holding the written material (the session-token directory).</summary>
    public string Directory { get; }

    /// <summary>Absolute path of the written server certificate (public DER).</summary>
    public string ServerCertificatePath => DaemonTransportFiles.ServerCertificatePath(Directory);

    /// <summary>Absolute path of the written client certificate + key (PKCS#12).</summary>
    public string ClientCertificatePath => DaemonTransportFiles.ClientCertificatePath(Directory);

    private SessionTransportCertificates(
        X509Certificate2 serverCertificate, byte[] pinnedClientFingerprint, string directory)
    {
        ServerCertificate = serverCertificate;
        _pinnedClientFingerprint = pinnedClientFingerprint;
        Directory = directory;
    }

    /// <summary>
    /// Mints a fresh session pair and writes the client material beside <paramref name="tokenPath"/>
    /// (or beside the OS-default token path when null).
    /// </summary>
    public static SessionTransportCertificates Create(string? tokenPath = null)
    {
        tokenPath ??= SessionTokenFile.DefaultPath();
        var directory = Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;
        System.IO.Directory.CreateDirectory(directory);

        var serverCertificate = Mint("CN=Mainguard Daemon", ServerAuthOid, loopbackSubjectNames: true);
        using var clientCertificate = Mint("CN=Mainguard Daemon Client", ClientAuthOid, loopbackSubjectNames: false);

        WriteRestricted(
            DaemonTransportFiles.ServerCertificatePath(directory),
            serverCertificate.Export(X509ContentType.Cert));
        WriteRestricted(
            DaemonTransportFiles.ClientCertificatePath(directory),
            clientCertificate.Export(X509ContentType.Pkcs12));

        return new SessionTransportCertificates(
            RoundTripForTls(serverCertificate),
            DaemonTransportCredentials.Fingerprint(clientCertificate),
            directory);
    }

    /// <summary>
    /// The server-side pinning predicate Kestrel consults for every presented client certificate:
    /// true only for this session's client certificate, byte for byte. Chain, issuer and validity are
    /// deliberately not consulted — the pair is self-signed and session-scoped, so exact identity IS the
    /// control, and Windows-host/WSL2-VM clock skew must never fail a correct connection.
    /// </summary>
    public bool IsPinnedClientCertificate(X509Certificate2? presented)
    {
        if (presented is null)
        {
            return false;
        }

        var actual = SHA256.HashData(presented.RawData);
        return CryptographicOperations.FixedTimeEquals(actual, _pinnedClientFingerprint);
    }

    private static X509Certificate2 Mint(string subject, string extendedKeyUsageOid, bool loopbackSubjectNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        // ECDSA signs; it never does key transport, so DigitalSignature is the only correct usage bit.
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(extendedKeyUsageOid)], critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        if (loopbackSubjectNames)
        {
            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddIpAddress(IPAddress.Loopback);
            alternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
            alternativeNames.AddDnsName("localhost");
            request.CertificateExtensions.Add(alternativeNames.Build());
        }

        // The validity window is NOT the control — the fingerprint pin is, and both ends bypass chain and
        // time validation deliberately. A wide window exists only so that WSL2 clock drift (a real effect
        // after host sleep/resume) can never turn a correct, pinned connection into a handshake failure.
        // The material is rotated on every daemon start regardless.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(notBefore, notBefore.AddYears(10));
    }

    /// <summary>
    /// Re-imports a freshly minted certificate through PKCS#12 so its private key is in a form
    /// <see cref="System.Net.Security.SslStream"/> can use. On Windows a key produced by
    /// <see cref="CertificateRequest.CreateSelfSigned"/> is not usable by Schannel until it has been
    /// through a key set; elsewhere the round-trip is a cheap no-op that keeps one code path.
    /// </summary>
    private static X509Certificate2 RoundTripForTls(X509Certificate2 certificate)
    {
        using (certificate)
        {
            return DaemonTransportCredentials.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12));
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> user-only-readable, mirroring <see cref="SessionTokenFile"/>:
    /// on Unix the file is pre-created at <c>0600</c> so the bytes never land under a permissive mode and
    /// the mode is re-asserted after the write (umask can widen it); on Windows the file is created inside
    /// the user-scoped data root and its DACL is reduced to a single full-control ACE for this user.
    /// </summary>
    private static void WriteRestricted(string path, byte[] content)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        File.WriteAllBytes(path, content);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RestrictWindows(path);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRule(rule);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ServerCertificate.Dispose();
    }
}
