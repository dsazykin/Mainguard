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
    /// <b>The third half — the one the guard was missing.</b> A build-time error that no pipeline can
    /// trigger is documentation, not a guard. <c>MG0057</c> fires only for a build that declares itself
    /// a release, and for the entire life of the guard NOTHING in <c>.github/</c> or <c>build/</c>
    /// declared one: <c>pack.ps1</c> passed <c>/p:MainguardPinnedThumbprints</c> only when a pin
    /// happened to resolve and never passed a stamp, so an owner running <c>pack.ps1 -Channel pro</c>
    /// with no certificate still shipped a pin-less Pro head — byte-for-byte the state the finding
    /// describes — while <see cref="AttestedReleaseAssembly_MustCarryUsablePins"/> passed vacuously on
    /// every build CI produced.
    ///
    /// <para>So the release script's arming is asserted here, as text, for the same reason the csproj
    /// target is: it is the cheapest thing to delete and the most expensive to notice missing.</para>
    /// </summary>
    [Fact]
    public void TheReleaseScriptArmsTheGuard_AndRefusesAPinlessRelease()
    {
        var pack = File.ReadAllText(Path.Combine(RepoRoot(), "build", "velopack", "pack.ps1"));

        // Every publish declares itself a release, so MG0057 is reachable from the one script that
        // ships bytes to users. Unconditional: a pin that "happens to resolve" is not a guarantee.
        //
        // The STATEMENT, not the string: pack.ps1 explains this arming in a comment directly above it,
        // and an assertion on the bare property name passes on that prose alone — commenting the real
        // line out left this test green, which is the same "guard that cannot fail" shape as the
        // finding it is here to close.
        Assert.Contains("$publishArgs += \"/p:MainguardPinsRequired=true\"", pack, StringComparison.Ordinal);

        // And the script refuses in its own voice before spending minutes on a publish it cannot ship.
        Assert.Contains("-not $DryRun -and -not $PinnedThumbprints", pack, StringComparison.Ordinal);
        Assert.Contains("runtime signature pin", pack, StringComparison.Ordinal);

        // The stamp that means "GitHub minted an attestation for these bytes" must NOT be set by a
        // script that runs on the owner's release box, where no OIDC identity exists to mint one:
        // BuildProvenanceGate would then demand an attestation that was never created and the shipped
        // app would refuse its own payload. Being explicit here keeps a future edit from "fixing" the
        // arming by reaching for the wrong property.
        Assert.DoesNotContain("MainguardAttestedRelease=true", pack, StringComparison.Ordinal);
    }

    /// <summary>
    /// And CI must actually WATCH the guard fire. Without an expect-failure run, a disarmed guard and a
    /// working one produce identical green pipelines — which is exactly how this one stayed inert
    /// through the change that introduced it.
    /// </summary>
    [Fact]
    public void CiProvesTheGuardFires_BothWays()
    {
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        // The job KEY, at its YAML indent — not the bare name. A substring match is satisfied by
        // `release-pins-guard-DISABLED:`, and by the name sitting in a comment, so renaming the job out
        // of existence would leave this test green while CI stopped watching the guard entirely.
        Assert.Contains("\n  release-pins-guard:\n", ci, StringComparison.Ordinal);

        // The negative control: a release with no pins is built and required to fail with MG0057.
        Assert.Contains("-p:MainguardAttestedRelease=true", ci, StringComparison.Ordinal);
        Assert.Contains("error MG0057", ci, StringComparison.Ordinal);

        // The positive control, which is what stops "the guard fires" from being satisfied by a guard
        // that refuses every build regardless of pins — that would pass the negative check while making
        // a real, correctly-pinned release unbuildable.
        Assert.Contains("-p:MainguardPinnedThumbprints=", ci, StringComparison.Ordinal);
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
