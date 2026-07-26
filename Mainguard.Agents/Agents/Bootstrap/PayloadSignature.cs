using System;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>Which privileged artifact is being checked. Kept explicit so a future verifier can apply a
/// different trust root per artifact (an Authenticode cert for the Windows exes, an npm provenance
/// attestation for adapter tarballs, a detached signature for the daemon payload) without every call
/// site being rewritten.</summary>
public enum SignedArtifactKind
{
    /// <summary>The daemon build promoted into <c>/opt/mainguard</c> and restarted as root.</summary>
    DaemonPayload,

    /// <summary>An agent-CLI tarball fetched from a registry and installed inside the VM.</summary>
    AdapterPayload,

    /// <summary>The elevated installer helper, launched with <c>runas</c> across the UAC boundary.</summary>
    ElevatedHelper,

    /// <summary>The executable registered as the elevated ONLOGON resume Scheduled Task.</summary>
    ResumeTarget,
}

/// <summary>The three answers a signature check can give. Three, not two, is the whole point: "I could
/// not check" must never be spelled the same way as "I checked and it is fine".</summary>
public enum SignatureVerdictKind
{
    /// <summary>A verifier checked a real signature against a real trust root and it held.</summary>
    Verified,

    /// <summary>A verifier checked and the signature was absent, malformed, or not from a trusted
    /// signer. Callers MUST refuse the artifact.</summary>
    Rejected,

    /// <summary>No signature could be checked at all, because this build has no signing identity to
    /// check against. Callers proceed — with the gap logged — because refusing everything would mean
    /// refusing every install. This is the default on every shipped build today.</summary>
    NotAvailable,
}

/// <summary>One signature answer plus the sentence explaining it (for the log the user can read).</summary>
public sealed record SignatureVerdict(SignatureVerdictKind Kind, string Reason)
{
    /// <summary>True only for <see cref="SignatureVerdictKind.Rejected"/>: a verifier was present, it
    /// looked, and it said no. <see cref="SignatureVerdictKind.NotAvailable"/> is deliberately NOT a
    /// refusal — see <see cref="UnsignedBuildSignatureVerifier"/> for why that is honest rather than
    /// convenient.</summary>
    public bool MustRefuse => Kind == SignatureVerdictKind.Rejected;

    public static SignatureVerdict Verified(string reason) => new(SignatureVerdictKind.Verified, reason);

    public static SignatureVerdict Rejected(string reason) => new(SignatureVerdictKind.Rejected, reason);

    public static SignatureVerdict NotAvailable(string reason) => new(SignatureVerdictKind.NotAvailable, reason);
}

/// <summary>
/// <b>The single place code signing plugs into Mainguard.</b>
///
/// <para>Every path in this app that promotes bytes to a higher privilege — the daemon build copied
/// over <c>/opt/mainguard</c> and restarted as root, an agent-CLI tarball installed into the shared
/// adapters prefix, the helper launched across the UAC boundary, the exe registered as an elevated
/// ONLOGON task — ought to verify that those bytes came from us. None of them can today, because
/// <b>Mainguard has no code-signing identity</b>: nothing this repository builds is signed, no
/// certificate exists, and the dormant <c>SignMainguardExecutables</c> MSBuild target
/// (Mainguard.Pro.App.csproj) is gated on a <c>$(MainguardSigningCertPath)</c> that no CI job ever
/// sets. Hash pins cover byte-for-byte reproduction, not provenance — they prove "the same bytes I saw
/// before", never "the bytes Mainguard published".</para>
///
/// <para>This interface exists so that gap has ONE named shape and ONE set of call sites instead of
/// being a comment in five files. When a certificate exists, implement this once (Authenticode via
/// <c>WinVerifyTrust</c> for the Windows exes; a detached signature or an npm provenance attestation
/// for the payloads), assign it to <see cref="PayloadSignature.Verifier"/> at startup, and every
/// privileged promote in the app starts enforcing it — with no call-site changes.</para>
/// </summary>
public interface IPayloadSignatureVerifier
{
    /// <summary>Verifies an artifact that exists on disk (an exe, a payload directory).</summary>
    SignatureVerdict VerifyFile(SignedArtifactKind kind, string path);

    /// <summary>Verifies an artifact held in memory (a freshly downloaded tarball).
    /// <paramref name="description"/> names it for the log (e.g. <c>claude-code@2.1.218</c>).</summary>
    SignatureVerdict VerifyBytes(SignedArtifactKind kind, string description, byte[] content);
}

/// <summary>
/// The default verifier: an explicit, documented <b>no-op</b>.
///
/// <para>It answers <see cref="SignatureVerdictKind.NotAvailable"/> — never
/// <see cref="SignatureVerdictKind.Verified"/> — for everything, because <b>no signature is checked and
/// none exists to check</b>. Returning <c>Verified</c> here would be a lie that every call site and
/// every future reader would then rely on; returning <c>Rejected</c> would refuse every legitimate
/// install on every machine. <c>NotAvailable</c> is the only truthful answer, and it makes the missing
/// assurance visible in the log rather than assumed away.</para>
///
/// <para><b>Do not "implement" this class.</b> Verification belongs in a NEW implementation of
/// <see cref="IPayloadSignatureVerifier"/> installed at <see cref="PayloadSignature.Verifier"/>; this
/// one's entire job is to state, in code, that Mainguard currently ships unsigned.</para>
/// </summary>
public sealed class UnsignedBuildSignatureVerifier : IPayloadSignatureVerifier
{
    /// <summary>The one sentence every unverified promote logs. Deliberately blunt: it is the honest
    /// residual risk, and a reader of oobe.log should not have to infer it.</summary>
    public const string NoSigningIdentity =
        "no signature was checked: this Mainguard build ships no code-signing identity, so the "
        + "artifact's origin is unverified (hash pins prove the bytes are unchanged, never that they "
        + "are ours)";

    public SignatureVerdict VerifyFile(SignedArtifactKind kind, string path)
        => SignatureVerdict.NotAvailable($"{kind} '{path}': {NoSigningIdentity}.");

    public SignatureVerdict VerifyBytes(SignedArtifactKind kind, string description, byte[] content)
        => SignatureVerdict.NotAvailable($"{kind} '{description}': {NoSigningIdentity}.");
}

/// <summary>
/// The process-wide signature seam. Static because this codebase has no DI container (the
/// <c>App.Settings</c> pattern), and because the verifier is one machine-wide policy rather than
/// per-call state.
///
/// <para>Call sites use <see cref="VerifyFile"/>/<see cref="VerifyBytes"/>, log the verdict's
/// <see cref="SignatureVerdict.Reason"/>, and refuse when <see cref="SignatureVerdict.MustRefuse"/> is
/// true. With the default <see cref="UnsignedBuildSignatureVerifier"/> that is never true — which is
/// exactly the state of assurance this build has, stated rather than papered over.</para>
/// </summary>
public static class PayloadSignature
{
    private static IPayloadSignatureVerifier _verifier = new UnsignedBuildSignatureVerifier();

    /// <summary>The active verifier. Assign a real implementation once a signing identity exists —
    /// that single assignment turns on enforcement across every privileged promote in the app.</summary>
    public static IPayloadSignatureVerifier Verifier
    {
        get => _verifier;
        set => _verifier = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Restores the shipped default. Tests that install a fake verifier must call this in a
    /// finally block — a leaked verifier would silently change every later test's trust decision.</summary>
    public static void ResetToDefault() => _verifier = new UnsignedBuildSignatureVerifier();

    /// <summary>Verifies a file, converting a THROWING verifier into a
    /// <see cref="SignatureVerdictKind.Rejected"/>. A verifier that faults is a verifier that did not
    /// approve, and reading an exception as "carry on" is how signature checks get quietly skipped.</summary>
    public static SignatureVerdict VerifyFile(SignedArtifactKind kind, string path)
    {
        try
        {
            return Verifier.VerifyFile(kind, path);
        }
        catch (Exception ex)
        {
            return SignatureVerdict.Rejected($"{kind} '{path}': the signature verifier failed ({ex.Message}).");
        }
    }

    /// <summary>Verifies in-memory bytes; a throwing verifier is a rejection (see <see cref="VerifyFile"/>).</summary>
    public static SignatureVerdict VerifyBytes(SignedArtifactKind kind, string description, byte[] content)
    {
        try
        {
            return Verifier.VerifyBytes(kind, description, content);
        }
        catch (Exception ex)
        {
            return SignatureVerdict.Rejected($"{kind} '{description}': the signature verifier failed ({ex.Message}).");
        }
    }
}
