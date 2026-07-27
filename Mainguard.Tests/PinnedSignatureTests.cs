using System;
using System.Collections.Generic;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Step 3 — a signature checked against a pinned thumbprint.</b>
///
/// <para>The property that matters, and the one it would be easiest to get wrong:
/// <see cref="SignatureVerdictKind.NotAvailable"/> must not quietly become the answer for "this build
/// can check, and the check failed". On a build with no signing identity NotAvailable is honest. On a
/// build that ships pins, an artifact the pin covers is <see cref="SignatureVerdictKind.Verified"/> or
/// <see cref="SignatureVerdictKind.Rejected"/> — never a shrug.</para>
///
/// <para><b>Scope of proof.</b> Everything above the <see cref="IAuthenticodeInspector"/> seam is proven
/// here and runs on Linux CI: pin parsing, the pin match, per-kind coverage, and every row of the
/// verdict table. Everything below it — <see cref="WindowsAuthenticodeInspector"/>'s
/// <c>WinVerifyTrust</c> P/Invoke — is manual Windows matrix, because a mocked Win32 layer would test
/// the mock. See build/signing/README.md for the matrix case.</para>
/// </summary>
public class PinnedSignatureTests
{
    private const string PinA = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4";
    private const string PinB = "0102030405060708090A0B0C0D0E0F1011121314";

    /// <summary>An inspector with a scripted answer, so the POLICY can be exercised without a signed
    /// file, a certificate, or Windows.</summary>
    private sealed class ScriptedInspector : IAuthenticodeInspector
    {
        public required AuthenticodeInspection Answer { get; init; }

        public List<string> Asked { get; } = new();

        public AuthenticodeInspection Inspect(string path)
        {
            Asked.Add(path);
            return Answer;
        }
    }

    private static AuthenticodeInspection SignedWith(string sha1, bool chainTrusted = false) =>
        new(AuthenticodeStatus.Valid, sha1, null, "CN=Mainguard", chainTrusted, "signature verified");

    private static PinnedThumbprintSignatureVerifier VerifierWith(
        string? pins, AuthenticodeInspection answer)
        => new(SigningPolicy.Parse(pins), new ScriptedInspector { Answer = answer });

    // ---- the pins are configuration ---------------------------------------------------------------

    [Fact]
    public void NoPinsConfigured_IsAnUnsignedBuild()
    {
        var policy = SigningPolicy.Parse(null);

        Assert.False(policy.SigningEnabled);
        Assert.False(policy.HasUsablePins);
        Assert.Empty(policy.PinnedThumbprints);
    }

    [Fact]
    public void TheShippedBuildOfThisRepositoryHasNoPins()
    {
        // The default $(MainguardPinnedThumbprints) is empty, so this build has no signing identity. If
        // this ever fails, someone hardcoded an identity into the repository — which is exactly what the
        // plan says NOT to do (the key belongs in a credential store, the pin in a build property).
        //
        // It is also the live end of the configuration path: build with
        // /p:MainguardPinnedThumbprints=<thumbprint> and this test flips, which is what proves the
        // property → assembly metadata → SigningPolicy.Current wiring is real and not decorative.
        Assert.False(SigningPolicy.Current.SigningEnabled);
    }

    [Fact]
    public void TheDefaultVerifierIsChosenFromTheBuildsOwnConfiguration()
    {
        // No entry point opts in. A new Main cannot ship with signature checking silently off because
        // someone forgot a setup call — the build's own configuration decides.
        if (SigningPolicy.Current.SigningEnabled)
            Assert.IsType<PinnedThumbprintSignatureVerifier>(PayloadSignature.Verifier);
        else
            Assert.IsType<UnsignedBuildSignatureVerifier>(PayloadSignature.Verifier);
    }

    [Theory]
    // The renderings a thumbprint is actually copied out of: certmgr (spaced), certutil (colons),
    // lower case from a script, and PowerShell's bare form.
    [InlineData("a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90 a1 b2 c3 d4")]
    [InlineData("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4")]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4")]
    public void AThumbprintMatchesInEveryRenderingItIsCopiedOutOf(string pin)
    {
        var policy = SigningPolicy.Parse(pin);

        Assert.True(policy.HasUsablePins);
        Assert.True(policy.IsPinned(PinA));
        Assert.False(policy.IsPinned(PinB));
    }

    [Fact]
    public void SeveralPinsAreSupported_BecauseThatIsHowACertificateRollsOver()
    {
        var policy = SigningPolicy.Parse($"{PinA};{PinB}");

        Assert.Equal(2, policy.PinnedThumbprints.Count);
        Assert.True(policy.IsPinned(PinA));
        Assert.True(policy.IsPinned(PinB));
    }

    [Fact]
    public void ATypoedPin_IsRecordedAsMalformed_NotKeptAsAnEntryThatCanNeverMatch()
    {
        var policy = SigningPolicy.Parse("not-a-thumbprint");

        Assert.True(policy.SigningEnabled);   // the build MEANT to be signed…
        Assert.False(policy.HasUsablePins);   // …but nothing here can be checked
        Assert.Contains("not-a-thumbprint", policy.MalformedPins);
    }

    [Fact]
    public void ABuildWithOnlyMalformedPins_RefusesEverythingItCovers()
    {
        // Fail closed. The alternative — falling back to "unsigned build, carry on" — would give a
        // fumbled release configuration the same behaviour as a deliberate unsigned build, silently.
        var verdict = VerifierWith("oops", SignedWith(PinA))
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Rejected, verdict.Kind);
        Assert.True(verdict.MustRefuse);
        Assert.Contains("none of its configured thumbprints are valid", verdict.Reason);
    }

    // ---- the verdict table ------------------------------------------------------------------------

    [Theory]
    [InlineData(SignedArtifactKind.ElevatedHelper)]
    [InlineData(SignedArtifactKind.ResumeTarget)]
    public void APinnedSignature_IsVerified(SignedArtifactKind kind)
    {
        var verdict = VerifierWith(PinA, SignedWith(PinA)).VerifyFile(kind, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Verified, verdict.Kind);
        Assert.False(verdict.MustRefuse);
    }

    [Fact]
    public void AnUntrustedChain_IsNotAFailure_BecausePinningIsTheTrustAnchor()
    {
        // Self-signed is the whole point: we do not want the machine's trust store to have a vote.
        var verdict = VerifierWith(PinA, SignedWith(PinA, chainTrusted: false))
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Verified, verdict.Kind);
        Assert.Contains("pinning is the trust anchor", verdict.Reason);
    }

    [Fact]
    public void AValidSignatureFromSomeoneElse_IsRejected()
    {
        // "Signed" is not the property we need — "signed by us" is. An attacker can buy a certificate.
        var verdict = VerifierWith(PinA, SignedWith(PinB))
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Rejected, verdict.Kind);
        Assert.True(verdict.MustRefuse);
        Assert.Contains("does not pin", verdict.Reason);
    }

    [Fact]
    public void AnAlteredBinary_IsRejected()
    {
        var altered = new AuthenticodeInspection(
            AuthenticodeStatus.Invalid, null, null, null, false,
            "the signed digest does not match the file's bytes (TRUST_E_BAD_DIGEST)");

        var verdict = VerifierWith(PinA, altered)
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Rejected, verdict.Kind);
        Assert.Contains("altered after it was signed", verdict.Reason);
    }

    [Fact]
    public void AnUnsignedBinaryOnASigningEnabledBuild_IsARefusal_NotAShrug()
    {
        // THE property this whole step exists for. A signed build that meets an unsigned elevated helper
        // has learned something — that the file is not the one it shipped — and must act on it. Reading
        // this as NotAvailable would make signing decorative.
        var unsigned = new AuthenticodeInspection(
            AuthenticodeStatus.NotSigned, null, null, null, false, "no signature");

        var verdict = VerifierWith(PinA, unsigned)
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.Rejected, verdict.Kind);
        Assert.True(verdict.MustRefuse);
        Assert.NotEqual(SignatureVerdictKind.NotAvailable, verdict.Kind);
    }

    [Fact]
    public void AnUnsignedBinaryOnAnUnsignedBuild_IsNotAvailable_AndDoesNotBreakTheInstall()
    {
        var unsigned = new AuthenticodeInspection(
            AuthenticodeStatus.NotSigned, null, null, null, false, "no signature");

        var verdict = VerifierWith(pins: null, unsigned)
            .VerifyFile(SignedArtifactKind.ElevatedHelper, @"C:\x\helper.exe");

        Assert.Equal(SignatureVerdictKind.NotAvailable, verdict.Kind);
        Assert.False(verdict.MustRefuse);
        Assert.Contains("no code-signing identity", verdict.Reason);
    }

    // ---- coverage is explicit ---------------------------------------------------------------------

    [Theory]
    [InlineData(SignedArtifactKind.ElevatedHelper, true)]
    [InlineData(SignedArtifactKind.ResumeTarget, true)]
    [InlineData(SignedArtifactKind.DaemonPayload, false)]
    [InlineData(SignedArtifactKind.AdapterPayload, false)]
    public void OnlyWindowsExecutablesAreCoveredByAnAuthenticodePin(SignedArtifactKind kind, bool covered)
        => Assert.Equal(covered, SigningPolicy.Covers(kind));

    [Theory]
    [InlineData(SignedArtifactKind.DaemonPayload)]
    [InlineData(SignedArtifactKind.AdapterPayload)]
    public void AnUncoveredKind_SaysWhatWouldCoverIt_RatherThanReadingAsApproval(SignedArtifactKind kind)
    {
        // A Linux ELF set and a third-party npm tarball cannot carry an Authenticode signature. Saying
        // NotAvailable here is correct — saying it WITHOUT naming the gap is how a reader concludes the
        // payload was checked.
        var verdict = VerifierWith(PinA, SignedWith(PinA)).VerifyFile(kind, "/opt/payload");

        Assert.Equal(SignatureVerdictKind.NotAvailable, verdict.Kind);
        Assert.False(verdict.MustRefuse);
        Assert.Contains("attestation", verdict.Reason);
        Assert.DoesNotContain("verified", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUncoveredKindIsNotEvenInspected()
    {
        var inspector = new ScriptedInspector { Answer = SignedWith(PinA) };
        var verifier = new PinnedThumbprintSignatureVerifier(SigningPolicy.Parse(PinA), inspector);

        verifier.VerifyFile(SignedArtifactKind.DaemonPayload, "/opt/payload");

        Assert.Empty(inspector.Asked);
    }

    // ---- the host inspector -----------------------------------------------------------------------

    [Fact]
    public void OnANonWindowsHost_TheInspectorSaysItCannotAnswer_RatherThanApproving()
    {
        var inspection = new UnsupportedAuthenticodeInspector().Inspect("/tmp/x.exe");

        Assert.Equal(AuthenticodeStatus.Unsupported, inspection.Status);
        Assert.NotEqual(AuthenticodeStatus.Valid, inspection.Status);

        var verdict = VerifierWith(PinA, inspection)
            .VerifyFile(SignedArtifactKind.ElevatedHelper, "/tmp/x.exe");
        Assert.Equal(SignatureVerdictKind.NotAvailable, verdict.Kind);
    }

    [Fact]
    public void TheHostInspectorMatchesTheHost()
    {
        var inspector = AuthenticodeInspector.ForHost();

        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsAuthenticodeInspector>(inspector);
        else
            Assert.IsType<UnsupportedAuthenticodeInspector>(inspector);
    }

    // ---- in-memory artifacts ----------------------------------------------------------------------

    [Fact]
    public void InMemoryPayloads_AreNeverClaimedVerified()
    {
        var verdict = VerifierWith(PinA, SignedWith(PinA))
            .VerifyBytes(SignedArtifactKind.AdapterPayload, "tool@1.2.3", new byte[] { 1, 2, 3 });

        Assert.Equal(SignatureVerdictKind.NotAvailable, verdict.Kind);
        Assert.NotEqual(SignatureVerdictKind.Verified, verdict.Kind);
    }
}
