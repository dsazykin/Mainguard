using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mainguard.Agents.Daemon;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// Mints a valid MG-19 transport-credential pair into a directory, so the CLIENT-side tests can drive
/// <c>DaemonClient.ForLoopback</c> past the credential gate and go on asserting what they are actually
/// about (token reading, network failure shape).
///
/// <para>This deliberately duplicates a few lines of <c>Mainguard.Server.Auth.SessionTransportCertificates</c>
/// rather than referencing it: <c>Mainguard.Tests</c> is the client-side tier and must not take a
/// dependency on the server assembly (the same reason <c>RequiresLibvtermFact</c> exists twice). The
/// production pinning/validation logic under test is the SHARED
/// <see cref="DaemonTransportCredentials"/>, which both tiers do use.</para>
/// </summary>
internal static class DaemonTransportMaterial
{
    /// <summary>Writes <c>daemon-server.cer</c> + <c>daemon-client.pfx</c> into <paramref name="directory"/>.</summary>
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);

        using var server = Mint("CN=Mainguard Daemon Test", "1.3.6.1.5.5.7.3.1", loopbackNames: true);
        using var client = Mint("CN=Mainguard Daemon Client Test", "1.3.6.1.5.5.7.3.2", loopbackNames: false);

        File.WriteAllBytes(
            DaemonTransportFiles.ServerCertificatePath(directory), server.Export(X509ContentType.Cert));
        File.WriteAllBytes(
            DaemonTransportFiles.ClientCertificatePath(directory), client.Export(X509ContentType.Pkcs12));
    }

    private static X509Certificate2 Mint(string subject, string ekuOid, bool loopbackNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ekuOid)], true));

        if (loopbackNames)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
        }

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(notBefore, notBefore.AddYears(10));
    }
}
