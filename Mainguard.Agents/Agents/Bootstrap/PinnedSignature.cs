using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>What an Authenticode inspection found. Four answers, because "not signed" and "signed with
/// a broken digest" are different facts and only one of them means the file was tampered with after
/// signing.</summary>
public enum AuthenticodeStatus
{
    /// <summary>The embedded signature's digest matches the file. The certificate chain may or may not
    /// be publicly trusted — see <see cref="AuthenticodeInspection.ChainTrusted"/>; for pinning it does
    /// not need to be.</summary>
    Valid,

    /// <summary>The file carries no signature at all.</summary>
    NotSigned,

    /// <summary>A signature is present but did not verify — the classic case being
    /// <c>TRUST_E_BAD_DIGEST</c>: the bytes changed after they were signed.</summary>
    Invalid,

    /// <summary>Authenticode cannot be evaluated here (a non-Windows host). Not a verdict about the
    /// file — a statement that this machine cannot answer.</summary>
    Unsupported,
}

/// <summary>One Authenticode inspection: what was found, whose certificate it was, and the sentence for
/// the log.</summary>
/// <param name="Sha1Thumbprint">Uppercase hex SHA-1 of the signer certificate (what
/// <c>certutil</c>/<c>New-SelfSignedCertificate</c> call "the thumbprint"), or null.</param>
/// <param name="Sha256Thumbprint">Uppercase hex SHA-256 of the same certificate, or null. Accepted as a
/// pin too, so a future move off SHA-1 thumbprints is a config change.</param>
public sealed record AuthenticodeInspection(
    AuthenticodeStatus Status,
    string? Sha1Thumbprint,
    string? Sha256Thumbprint,
    string? Subject,
    bool ChainTrusted,
    string Detail);

/// <summary>The Authenticode seam. Behind an interface so the pinning POLICY — which is the part that
/// can be got wrong — is unit-tested on any OS, while the Win32 call stays in one place.</summary>
public interface IAuthenticodeInspector
{
    AuthenticodeInspection Inspect(string path);
}

/// <summary>
/// <b>Which certificates this build trusts, and which artifacts the pin covers.</b>
///
/// <para><b>Configuration, not code.</b> The pinned thumbprints arrive as assembly metadata
/// (<c>&lt;AssemblyMetadata Include="MainguardPinnedThumbprints" …&gt;</c> in
/// <c>Mainguard.Agents.csproj</c>, fed by <c>$(MainguardPinnedThumbprints)</c>). A build that sets the
/// property ships a verifier; a build that does not ships the honest no-op. Swapping the self-signed
/// certificate for a real one — Azure Trusted Signing, step 4 of the plan of record — changes the
/// property's value and nothing else. That is the entire reason the paid certificate is ordered last.</para>
///
/// <para><b>A public trust anchor is not needed for the job this does.</b> The threat is a REPLACED
/// binary at a legitimate path. An attacker who overwrites the file cannot produce a signature that
/// chains to our private key, so a self-signed certificate detects the replacement exactly as well as
/// a $400 one. What the paid certificate buys is SmartScreen reputation, which is a distribution
/// property, not a security one.</para>
///
/// <para><b>Coverage is explicit, and narrow on purpose.</b> Authenticode signs Windows PE files. The
/// two artifacts on the elevation path are PE files and are covered. The daemon payload is a set of
/// Linux ELF binaries and the adapter payloads are npm tarballs — neither can carry an Authenticode
/// signature, and inventing a second bespoke signature format for them here would duplicate what step 2
/// of the plan (GitHub artifact attestations / npm provenance) does properly. Those kinds therefore
/// answer <see cref="SignatureVerdictKind.NotAvailable"/> with a reason that NAMES the gap, rather than
/// silently reading as approval.</para>
/// </summary>
public sealed class SigningPolicy
{
    /// <summary>The assembly-metadata key the build writes the pins into.</summary>
    public const string MetadataKey = "MainguardPinnedThumbprints";

    private static readonly Lazy<SigningPolicy> CurrentPolicy =
        new(() => FromAssembly(typeof(SigningPolicy).Assembly));

    private readonly HashSet<string> _pins;

    private SigningPolicy(
        bool configured, IReadOnlyList<string> pins, IReadOnlyList<string> malformed)
    {
        SigningEnabled = configured;
        PinnedThumbprints = pins;
        MalformedPins = malformed;
        _pins = new HashSet<string>(pins, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when this build was configured with at least one pin ENTRY — even a malformed one.
    /// A build that meant to be signed and fumbled its configuration must not silently fall back to the
    /// unsigned no-op; see <see cref="HasUsablePins"/>.</summary>
    public bool SigningEnabled { get; }

    /// <summary>The usable pins, normalised to uppercase hex.</summary>
    public IReadOnlyList<string> PinnedThumbprints { get; }

    /// <summary>Entries that were configured but are not thumbprints. Surfaced so a typo shows up in the
    /// refusal message instead of quietly matching nothing forever.</summary>
    public IReadOnlyList<string> MalformedPins { get; }

    /// <summary>Signing is on AND at least one pin is usable. When this is false while
    /// <see cref="SigningEnabled"/> is true, the verifier refuses everything it covers — fail-closed,
    /// because the alternative is a signed-build badge over a check that can never pass.</summary>
    public bool HasUsablePins => SigningEnabled && PinnedThumbprints.Count > 0;

    /// <summary>This build's policy, read once from its own assembly metadata.</summary>
    public static SigningPolicy Current => CurrentPolicy.Value;

    /// <summary>Reads the pins out of an assembly's <see cref="AssemblyMetadataAttribute"/> set.</summary>
    public static SigningPolicy FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var raw = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))?
            .Value;
        return Parse(raw);
    }

    /// <summary>
    /// Parses a <c>;</c>- or <c>,</c>-separated thumbprint list. Spaces and colons are stripped (both
    /// certmgr and <c>certutil</c> render thumbprints with separators, and a pin copied out of either
    /// must not silently fail to match). A usable pin is 40 hex characters (SHA-1) or 64 (SHA-256);
    /// anything else is recorded as malformed rather than kept as an entry that can never match.
    /// </summary>
    public static SigningPolicy Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new SigningPolicy(configured: false, Array.Empty<string>(), Array.Empty<string>());

        var pins = new List<string>();
        var malformed = new List<string>();
        foreach (var entry in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
                continue;

            var normalized = Normalize(trimmed);
            if (normalized is null)
                malformed.Add(trimmed);
            else if (!pins.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                pins.Add(normalized);
        }

        if (pins.Count == 0 && malformed.Count == 0)
            return new SigningPolicy(configured: false, Array.Empty<string>(), Array.Empty<string>());

        return new SigningPolicy(configured: true, pins, malformed);
    }

    /// <summary>Whether a thumbprint (in any common rendering) is one of ours.</summary>
    public bool IsPinned(string? thumbprint)
    {
        var normalized = Normalize(thumbprint);
        return normalized is not null && _pins.Contains(normalized);
    }

    /// <summary>
    /// The artifact kinds an Authenticode pin can speak for. Written as an explicit set rather than a
    /// default so that adding a <see cref="SignedArtifactKind"/> does not silently inherit either
    /// answer — a new kind is uncovered until someone decides what covers it.
    /// </summary>
    public static bool Covers(SignedArtifactKind kind)
        => kind is SignedArtifactKind.ElevatedHelper or SignedArtifactKind.ResumeTarget;

    /// <summary>Why an uncovered kind is uncovered — the sentence the log carries, so nobody reads
    /// <see cref="SignatureVerdictKind.NotAvailable"/> here as "checked and fine".</summary>
    public static string WhyNotCovered(SignedArtifactKind kind) => kind switch
    {
        SignedArtifactKind.DaemonPayload =>
            "the daemon payload is a set of Linux executables, which cannot carry an Authenticode "
            + "signature; its provenance is covered by build attestations, not by this pin",
        SignedArtifactKind.AdapterPayload =>
            "an agent-CLI tarball is fetched from a public registry and is not ours to sign; its "
            + "provenance is covered by npm provenance attestations, not by this pin",
        _ => $"{kind} is not covered by Authenticode pinning on this build",
    };

    private static string? Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return null;

        Span<char> buffer = stackalloc char[128];
        var length = 0;
        foreach (var c in thumbprint)
        {
            if (c is ' ' or ':' or '-' or '\t' or '\r' or '\n')
                continue;
            if (!char.IsAsciiHexDigit(c) || length == buffer.Length)
                return null;
            buffer[length++] = char.ToUpperInvariant(c);
        }

        return length is 40 or 64 ? new string(buffer[..length]) : null;
    }
}

/// <summary>
/// <b>The step-3 verifier: an Authenticode signature checked against a pinned thumbprint.</b>
///
/// <para><b>Where this must live, and why the ordering is not negotiable.</b> A verifier is only worth
/// as much as the directory it is loaded from. Inside the per-user app — <c>%LocalAppData%\Mainguard</c>
/// under the Velopack layout — this class's own assembly is writable by exactly the account it is
/// defending against, so same-user malware replaces the verifier rather than defeating it. That is why
/// step 1 (<see cref="ElevatedComponentInstaller"/>) comes FIRST and is the load-bearing half: the
/// elevated helper is promoted into <c>%ProgramFiles%\Mainguard\elevated</c>, and the copy of this code
/// that runs THERE — while elevated, deciding whether to register a Scheduled Task and what to run —
/// is administrator-owned. Signature checks made from that copy hold against same-user malware.
/// Signature checks made from the per-user app do not, and are not claimed to: they defend against a
/// corrupted download, a hostile mirror, and a partially-written update, which is a real threat and a
/// smaller one. Do not move this check into the writable side and describe it as closing MG-15.</para>
///
/// <para><b>Untrusted chain is expected, and is not a failure.</b> With a self-signed certificate
/// <c>WinVerifyTrust</c> reports <c>CERT_E_UNTRUSTEDROOT</c>: the digest matched, the chain did not
/// terminate in a store the machine trusts. For pinning that is precisely the right state — we do not
/// want the machine's trust store to have a vote, we want OUR key to. What is never tolerated is a
/// digest that does not match (<c>TRUST_E_BAD_DIGEST</c> — the file was modified after signing) or a
/// signature that is absent.</para>
/// </summary>
public sealed class PinnedThumbprintSignatureVerifier : IPayloadSignatureVerifier
{
    private readonly SigningPolicy _policy;
    private readonly IAuthenticodeInspector _inspector;

    public PinnedThumbprintSignatureVerifier(SigningPolicy policy, IAuthenticodeInspector inspector)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    /// <summary>
    /// The verdict table, in full — every branch below is one row, and the two that matter are the last
    /// two: on a build that IS supposed to be signed, an artifact we cover that is unsigned or altered
    /// is <see cref="SignatureVerdictKind.Rejected"/>. It is never
    /// <see cref="SignatureVerdictKind.NotAvailable"/>, because "I could not check" on a build that can
    /// check is indistinguishable from "the check failed and I shrugged".
    /// </summary>
    public SignatureVerdict VerifyFile(SignedArtifactKind kind, string path)
    {
        if (!_policy.SigningEnabled)
        {
            return SignatureVerdict.NotAvailable(
                $"{kind} '{path}': {UnsignedBuildSignatureVerifier.NoSigningIdentity}.");
        }

        if (!SigningPolicy.Covers(kind))
        {
            return SignatureVerdict.NotAvailable(
                $"{kind} '{path}': no signature was checked — {SigningPolicy.WhyNotCovered(kind)}.");
        }

        if (!_policy.HasUsablePins)
        {
            // Configured to be signed, but with nothing checkable. Fail closed: the alternative is a
            // build that believes it enforces signatures while enforcing nothing.
            return SignatureVerdict.Rejected(
                $"{kind} '{path}': this build enables signature pinning but none of its configured "
                + $"thumbprints are valid ({string.Join(", ", _policy.MalformedPins)}) — refusing rather "
                + "than running an artifact nothing can vouch for.");
        }

        var inspection = _inspector.Inspect(path);
        switch (inspection.Status)
        {
            case AuthenticodeStatus.Unsupported:
                return SignatureVerdict.NotAvailable(
                    $"{kind} '{path}': {inspection.Detail} (Authenticode is a Windows facility; the "
                    + "privileged paths this gate protects only run on Windows).");

            case AuthenticodeStatus.NotSigned:
                return SignatureVerdict.Rejected(
                    $"{kind} '{path}' carries no code signature, but this Mainguard build is a SIGNED "
                    + $"build ({_policy.PinnedThumbprints.Count} pinned certificate(s)) — an unsigned "
                    + "artifact here means the file is not the one we shipped. Reinstall Mainguard from "
                    + "a trusted source.");

            case AuthenticodeStatus.Invalid:
                return SignatureVerdict.Rejected(
                    $"{kind} '{path}' failed its code-signature check: {inspection.Detail}. The file was "
                    + "altered after it was signed.");

            case AuthenticodeStatus.Valid when _policy.IsPinned(inspection.Sha1Thumbprint)
                    || _policy.IsPinned(inspection.Sha256Thumbprint):
                return SignatureVerdict.Verified(
                    $"{kind} '{path}' is signed by a pinned Mainguard certificate "
                    + $"({inspection.Sha1Thumbprint}, subject {inspection.Subject ?? "<none>"}; chain "
                    + $"{(inspection.ChainTrusted ? "publicly trusted" : "self-signed — pinning is the trust anchor")}).");

            case AuthenticodeStatus.Valid:
                return SignatureVerdict.Rejected(
                    $"{kind} '{path}' is signed, but by a certificate this build does not pin "
                    + $"({inspection.Sha1Thumbprint}, subject {inspection.Subject ?? "<none>"}). A valid "
                    + "signature from someone else is not a signature from us.");

            default:
                return SignatureVerdict.Rejected(
                    $"{kind} '{path}': the signature inspector returned an unknown status "
                    + $"({inspection.Status}) — treating an answer we do not understand as a refusal.");
        }
    }

    /// <summary>In-memory artifacts (the adapter tarballs) carry no Authenticode signature — they are
    /// third-party npm payloads. Answers <see cref="SignatureVerdictKind.NotAvailable"/> naming what
    /// would cover them, never <see cref="SignatureVerdictKind.Verified"/>.</summary>
    public SignatureVerdict VerifyBytes(SignedArtifactKind kind, string description, byte[] content)
        => _policy.SigningEnabled
            ? SignatureVerdict.NotAvailable(
                $"{kind} '{description}': no signature was checked — {SigningPolicy.WhyNotCovered(kind)}.")
            : SignatureVerdict.NotAvailable(
                $"{kind} '{description}': {UnsignedBuildSignatureVerifier.NoSigningIdentity}.");
}

/// <summary>Picks the inspector this host can actually use.</summary>
public static class AuthenticodeInspector
{
    /// <summary>The Windows implementation on Windows; an honest "cannot answer here" elsewhere.</summary>
    public static IAuthenticodeInspector ForHost()
        => OperatingSystem.IsWindows() ? new WindowsAuthenticodeInspector() : new UnsupportedAuthenticodeInspector();
}

/// <summary>The non-Windows answer: <see cref="AuthenticodeStatus.Unsupported"/> for everything. Not a
/// stub standing in for the real thing — a statement that this host cannot evaluate a PE signature,
/// which the verifier turns into <c>NotAvailable</c> rather than into approval.</summary>
public sealed class UnsupportedAuthenticodeInspector : IAuthenticodeInspector
{
    public AuthenticodeInspection Inspect(string path) => new(
        AuthenticodeStatus.Unsupported, null, null, null, ChainTrusted: false,
        "Authenticode signatures can only be evaluated on Windows");
}

/// <summary>
/// The real check: <c>WinVerifyTrust</c> with <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>, then the signer
/// certificate's thumbprints.
///
/// <para><b>Order matters.</b> The certificate is read only AFTER the digest verifies.
/// <see cref="X509Certificate.CreateFromSignedFile"/> on its own would be worthless as a check — it
/// EXTRACTS the embedded certificate without validating anything, so an attacker could paste our public
/// certificate into their binary and "pass". <c>WinVerifyTrust</c> is what establishes that the signed
/// digest matches the bytes on disk; the thumbprint pin is what establishes whose key signed it. Either
/// alone proves nothing.</para>
///
/// <para><b>Not unit-tested — manual Windows matrix.</b> Everything above this class is exercised on
/// Linux CI through <see cref="IAuthenticodeInspector"/>; the P/Invoke below cannot be, and pretending
/// otherwise with a mocked Win32 layer would test the mock. The matrix case is: sign a build, flip one
/// byte of the signed exe, confirm the launch is refused with a bad-digest message.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAuthenticodeInspector : IAuthenticodeInspector
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSafeMode = 0x00000100;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    private const int Ok = 0;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public AuthenticodeInspection Inspect(string path)
    {
        int status;
        try
        {
            status = VerifyTrust(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new AuthenticodeInspection(
                AuthenticodeStatus.Unsupported, null, null, null, false,
                $"wintrust.dll is unavailable on this machine ({ex.Message})");
        }

        // A signature that is absent, or in a form no trust provider recognises, is "not signed" —
        // never "fine".
        if (status is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
        {
            return new AuthenticodeInspection(
                AuthenticodeStatus.NotSigned, null, null, null, false,
                $"WinVerifyTrust reported no usable signature (0x{status:X8})");
        }

        // Digest verified. An untrusted/incomplete CHAIN is the expected state for our self-signed
        // certificate and must not be read as tampering — the pin, not the machine's trust store, is
        // this build's trust anchor.
        var chainTrusted = status == Ok;
        if (status is not (Ok or CertEUntrustedRoot or CertEChaining))
        {
            return new AuthenticodeInspection(
                AuthenticodeStatus.Invalid, null, null, null, false,
                status == TrustEBadDigest
                    ? "the signed digest does not match the file's bytes (TRUST_E_BAD_DIGEST)"
                    : $"WinVerifyTrust failed with 0x{status:X8}");
        }

        try
        {
            // X509Certificate, not X509Certificate2: this only needs the thumbprints and the subject.
            //
            // SYSLIB0057 tells us to use X509CertificateLoader — which cannot do this. The loader reads
            // certificate FILES; extracting the signer certificate embedded in a signed PE has no
            // replacement API in .NET, and the alternative (CryptQueryObject / CryptMsg*) is a much
            // larger P/Invoke surface for the same answer. Suppressed narrowly, over one call, because
            // the deprecation's actual hazard — "loading certificate data does not validate it" — is
            // already handled above: WinVerifyTrust established the digest BEFORE we get here, and this
            // certificate is used only to read a thumbprint that is then matched against a pin.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new AuthenticodeInspection(
                AuthenticodeStatus.Valid,
                certificate.GetCertHashString().ToUpperInvariant(),
                Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)),
                certificate.Subject,
                chainTrusted,
                chainTrusted
                    ? "signature verified and the chain is publicly trusted"
                    : "signature verified; the chain is not publicly trusted (expected for a self-signed build)");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.IO.IOException)
        {
            return new AuthenticodeInspection(
                AuthenticodeStatus.Invalid, null, null, null, chainTrusted,
                $"the signature verified but its certificate could not be read: {ex.Message}");
        }
    }

    private static int VerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPtr = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                // SAFE_MODE keeps the trust provider from running any code the file itself supplies;
                // CACHE_ONLY_URL_RETRIEVAL keeps a verification from reaching the network (this runs on
                // the elevation path, where a hang is a broken install).
                dwProvFlags = WtdSafeMode | WtdCacheOnlyUrlRetrieval,
            };

            dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, fDeleteOld: false);

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            // The provider allocated state on VERIFY; CLOSE is what frees it. Skipping this leaks a
            // handle per check, on a path that runs at every launch.
            var closing = Marshal.PtrToStructure<WinTrustData>(dataPtr);
            closing.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(closing, dataPtr, fDeleteOld: true);
            _ = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            return result;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(dataPtr);
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
