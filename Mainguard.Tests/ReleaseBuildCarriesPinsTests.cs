using System;
using System.IO;
using System.Reflection;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F57 — the test the audit says does not exist: a release build carries pins.</b>
///
/// <para>The finding was not that signature pinning is unimplemented. It is implemented, and well. The
/// finding is that <c>$(MainguardPinnedThumbprints)</c> defaults EMPTY, so a build that forgot to set it
/// silently selects <see cref="UnsignedBuildSignatureVerifier"/> — whose verdict is never
/// <see cref="SignatureVerdictKind.Rejected"/> — and every privileged promote then proceeds on
/// <see cref="SignatureVerdictKind.NotAvailable"/>. Nothing connected "this is a release" to "this build
/// set the pins", and no test asserted it, so a release shipping unsigned would look exactly like a
/// developer build at runtime.</para>
///
/// <para>Two halves are pinned here, because either alone is defeatable:</para>
/// <list type="number">
///   <item>The BUILD-TIME guard exists in <c>Mainguard.Agents.csproj</c> — a release build with no pins
///   fails to compile. Asserted by reading the csproj, so deleting the target fails CI instead of
///   quietly disarming the invariant.</item>
///   <item>The RUNTIME invariant holds for whatever assembly is actually loaded: if it says it is an
///   attested release, it must have usable pins. On a developer build (the normal case here) the
///   implication is vacuously satisfied and the test still asserts the honest state — no stamp, no
///   pins, the unsigned verifier — so a stamp appearing without pins would break it.</item>
/// </list>
/// </summary>
public class ReleaseBuildCarriesPinsTests
{
    private static Assembly AgentsAssembly => typeof(SigningPolicy).Assembly;

    [Fact]
    public void AttestedReleaseAssembly_MustCarryUsablePins()
    {
        var attested = BuildProvenanceStamp.ReadStamp(AgentsAssembly);
        var policy = SigningPolicy.FromAssembly(AgentsAssembly);

        // The implication, in the direction that matters: attested ⇒ usable pins. On a developer build
        // it is vacuous, which is precisely why the csproj guard below is the other half — this
        // assertion alone can only catch a STAMPED build that forgot to pin, and a build only becomes
        // stamped in the release workflow.
        Assert.True(!attested || policy.HasUsablePins,
            "This assembly is stamped MainguardAttestedRelease=true but carries no usable signing pin, "
            + "so PayloadSignature selects the unsigned verifier and no privileged promote is ever "
            + "refused (audit F57). Build with -p:MainguardPinnedThumbprints=<thumbprint>.");

        // True on every build: a pin that was configured but is not a thumbprint can never match, and
        // a build that believes it enforces signatures while enforcing nothing is the failure mode the
        // whole finding is about.
        Assert.Empty(policy.MalformedPins);

        // And the verifier the build actually selected agrees with the policy it read — the seam where
        // "configured" turns into "enforcing".
        Assert.Equal(policy.SigningEnabled,
            PayloadSignature.Verifier is not UnsignedBuildSignatureVerifier);
    }

    [Fact]
    public void TheBuildItselfRefusesAReleaseWithNoPins()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Mainguard.Agents", "Mainguard.Agents.csproj"));

        // The guard target, its trigger, and its error. Spelled out so that weakening any one of the
        // three — removing the target, removing the condition, downgrading Error to Warning — fails
        // here rather than shipping a release that verifies nothing.
        Assert.Contains("MainguardRequirePinsOnReleaseBuild", csproj, StringComparison.Ordinal);
        Assert.Contains("<Error", csproj, StringComparison.Ordinal);
        Assert.Contains("MG0057", csproj, StringComparison.Ordinal);
        Assert.Contains("'$(MainguardPinsRequired)' == 'true'", csproj, StringComparison.Ordinal);
        Assert.Contains("'$(MainguardPinnedThumbprints)' == ''", csproj, StringComparison.Ordinal);

        // And that an attested release turns the requirement ON by itself — a release pipeline must not
        // have to remember a second flag.
        Assert.Contains("'$(MainguardAttestedRelease)' == 'true'\">true</MainguardPinsRequired>",
            csproj, StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit F57's other half: the elevated paths must not proceed on
    /// <see cref="SignatureVerdictKind.NotAvailable"/> once the build claims it can check. Driven
    /// through the pure <see cref="PayloadSignatureGate"/> so both answers are testable on any OS.
    /// </summary>
    [Theory]
    // A rejection is always fatal, on every build.
    [InlineData(SignatureVerdictKind.Rejected, false, false, true)]
    [InlineData(SignatureVerdictKind.Rejected, true, false, true)]
    // NotAvailable on a build that CAN check (pins configured, or stamped as a release) is fatal too:
    // "could not check" must not be the polite spelling of "the check failed".
    [InlineData(SignatureVerdictKind.NotAvailable, true, false, true)]
    [InlineData(SignatureVerdictKind.NotAvailable, false, true, true)]
    [InlineData(SignatureVerdictKind.NotAvailable, true, true, true)]
    // NotAvailable on an unsigned, unattested developer build proceeds — `dotnet run` from a checkout
    // must keep working, and there genuinely is nothing to check.
    [InlineData(SignatureVerdictKind.NotAvailable, false, false, false)]
    // A verified signature never refuses.
    [InlineData(SignatureVerdictKind.Verified, true, true, false)]
    public void CoveredKind_FailsClosedOnceTheBuildCanCheck(
        SignatureVerdictKind kind, bool signingEnabled, bool attestedRelease, bool expectedRefusal)
    {
        var verdict = new SignatureVerdict(kind, "test");
        var policy = SigningPolicy.Parse(signingEnabled ? new string('A', 40) : null);

        Assert.Equal(expectedRefusal, PayloadSignatureGate.MustRefuse(
            verdict, SignedArtifactKind.ElevatedHelper, policy, attestedRelease));
        Assert.Equal(expectedRefusal, PayloadSignatureGate.MustRefuse(
            verdict, SignedArtifactKind.ResumeTarget, policy, attestedRelease));
    }

    [Fact]
    public void UncoveredKinds_StillProceedOnNotAvailable_BecauseAuthenticodeCannotSpeakForThem()
    {
        // A Linux daemon payload and an npm tarball cannot carry an Authenticode signature at all, so
        // NotAvailable there is a statement about the ARTIFACT, not about a check that failed to run.
        // Refusing them would refuse every install on every build while establishing nothing; their
        // provenance is the build-attestation and npm-provenance gates instead.
        var notAvailable = new SignatureVerdict(SignatureVerdictKind.NotAvailable, "test");
        var signed = SigningPolicy.Parse(new string('A', 40));

        Assert.False(PayloadSignatureGate.MustRefuse(
            notAvailable, SignedArtifactKind.DaemonPayload, signed, attestedRelease: true));
        Assert.False(PayloadSignatureGate.MustRefuse(
            notAvailable, SignedArtifactKind.AdapterPayload, signed, attestedRelease: true));

        // …but a REJECTION of one of them is still a rejection.
        Assert.True(PayloadSignatureGate.MustRefuse(
            new SignatureVerdict(SignatureVerdictKind.Rejected, "test"),
            SignedArtifactKind.DaemonPayload, signed, attestedRelease: false));
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException(
            $"Could not find Mainguard.slnx above '{AppContext.BaseDirectory}', so the release-pin guard "
            + "could not be read out of the csproj. This is a failure, not a skip.");
    }
}
