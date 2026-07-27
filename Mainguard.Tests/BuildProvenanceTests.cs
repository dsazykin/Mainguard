using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>MG-9, our-own-artifacts half.</b> The daemon-payload manifest proves the COPY is faithful, and
/// both sides of that comparison come from the same directory — so it detects a truncated <c>cp</c> and
/// nothing else. A GitHub artifact attestation is different in kind: it is signed by the CI run that
/// produced the artifact, so whoever swaps the artifact cannot regenerate it.
///
/// <para><b>Unit-proven here:</b> the pure policy (every branch), the compiled-in requirement stamp,
/// the <c>gh attestation verify --format json</c> parser against captured output, the argv the verifier
/// builds, and the fail-closed wiring at the daemon-payload promote and the MainguardOS import.
/// <b>Manual-matrix only:</b> actually launching <c>gh</c> and round-tripping a real attestation, which
/// needs a GitHub-hosted runner and network.</para>
/// </summary>
public class BuildProvenanceTests
{
    private const string Repo = "dsazykin/Mainguard";
    private const string DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static BuildAttestationCheck Good(string digest = DigestA) =>
        new(true, "ok", "https://github.com/" + Repo, new[] { digest });

    // ---- The pure policy ------------------------------------------------------------------------

    [Fact]
    public void NonAttestedBuild_ChecksNothing_AndSaysSo()
    {
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.DaemonPayload, isAttestedRelease: false, Repo, DigestA, check: null);

        Assert.Equal(BuildProvenanceOutcome.NotAttestedBuild, verdict.Outcome);
        Assert.False(verdict.MustRefuse);
        Assert.Contains("was NOT checked", verdict.Reason);
    }

    [Fact]
    public void AttestedBuild_WithNoAttestation_Refuses()
    {
        // The deleted-attestation case. It must be a refusal, not a skip — a verifier an attacker
        // disables by removing a file is not a verifier.
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.MainguardOsTarball, isAttestedRelease: true, Repo, DigestA, check: null);

        Assert.True(verdict.MustRefuse);
        Assert.Contains("no build-provenance attestation could be obtained", verdict.Reason);
    }

    [Fact]
    public void AttestedBuild_VerifierCouldNotRun_Refuses()
    {
        // "gh is missing" and "the signature is bad" produce the same answer on purpose: otherwise
        // breaking the verifier is cheaper than forging a signature.
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.DaemonPayload, true, Repo, DigestA,
            new BuildAttestationCheck(false, "the gh attestation verifier could not run: not found"));

        Assert.True(verdict.MustRefuse);
    }

    [Fact]
    public void AttestedBuild_AttestationFromAnotherRepository_Refuses()
    {
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.DaemonPayload, true, Repo, DigestA,
            new BuildAttestationCheck(true, "ok", "https://github.com/attacker/Mainguard", new[] { DigestA }));

        Assert.True(verdict.MustRefuse);
        Assert.Contains("rather than", verdict.Reason);
    }

    [Fact]
    public void AttestedBuild_AttestationForDifferentBytes_Refuses()
    {
        // A genuine, correctly-signed attestation — over some other artifact. Present is not bound.
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.MainguardOsTarball, true, Repo, DigestA, Good(DigestB));

        Assert.True(verdict.MustRefuse);
        Assert.Contains("do not include the sha256 of the artifact on disk", verdict.Reason);
    }

    [Fact]
    public void AttestedBuild_MatchingAttestation_Verifies()
    {
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.MainguardOsTarball, true, Repo, DigestA, Good());

        Assert.Equal(BuildProvenanceOutcome.Verified, verdict.Outcome);
        Assert.False(verdict.MustRefuse);
    }

    [Theory]
    // A suffix match must respect the path boundary, or a look-alike owner satisfies the pin.
    [InlineData("https://github.com/evil-dsazykin/Mainguard", false)]
    [InlineData("https://github.com/evil/dsazykin/Mainguard", true)] // a real path segment; gh never emits this, but the rule is the rule
    [InlineData("https://github.com/dsazykin/Mainguard", true)]
    [InlineData("https://github.com/dsazykin/Mainguard/", true)]
    [InlineData("dsazykin/Mainguard", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RepositoryPin_MatchesOnAPathBoundary(string? reported, bool expected)
    {
        var verdict = BuildProvenancePolicy.Decide(
            BuildArtifactKind.DaemonPayload, true, Repo, DigestA,
            new BuildAttestationCheck(true, "ok", reported, new[] { DigestA }));

        Assert.Equal(expected, verdict.Outcome == BuildProvenanceOutcome.Verified);
    }

    // ---- The compiled-in requirement --------------------------------------------------------------

    [Fact]
    public void ThisBuildIsNotStampedAsAnAttestedRelease()
    {
        // A developer/CI build carries no stamp, so the gates are honest no-ops here. If this ever flips
        // by accident, every daemon promote in the test suite would start demanding gh.
        Assert.False(BuildProvenanceStamp.IsAttestedRelease);
        Assert.False(BuildProvenanceStamp.ReadStamp(typeof(BuildProvenanceTests).Assembly));
    }

    // ---- The gh output parser ----------------------------------------------------------------------

    [Fact]
    public void GhParser_ReadsTheRepositoryAndSubjectDigests()
    {
        const string json = """
        [
          {
            "attestation": { "bundle": {} },
            "verificationResult": {
              "signature": { "certificate": {
                "sourceRepositoryURI": "https://github.com/dsazykin/Mainguard",
                "sourceRepositoryOwnerURI": "https://github.com/dsazykin" } },
              "statement": {
                "predicateType": "https://slsa.dev/provenance/v1",
                "subject": [ { "name": "MainguardOS.tar.gz",
                               "digest": { "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } } ]
              }
            }
          }
        ]
        """;

        var check = GhCliBuildAttestationVerifier.ParseResult(0, json, "");

        Assert.True(check.Verified);
        Assert.Equal("https://github.com/dsazykin/Mainguard", check.SourceRepository);
        Assert.Equal(DigestA, Assert.Single(check.SubjectDigestsSha256!));
    }

    [Theory]
    [InlineData(0, "", "")]           // silence is not success
    [InlineData(0, "{ not json", "")] // unreadable output is not success
    [InlineData(0, "[]", "")]         // zero verified attestations is not success
    [InlineData(0, "{}", "")]         // not even an array
    public void GhParser_TreatsAnythingUnreadableAsAFailure(int exit, string stdout, string stderr)
        => Assert.False(GhCliBuildAttestationVerifier.ParseResult(exit, stdout, stderr).Verified);

    [Fact]
    public void GhParser_NonZeroExit_IsAFailure_EvenWhenTheOutputLooksPerfect()
    {
        // gh prints its verified-attestation array AND exits non-zero when a policy flag (--repo,
        // --predicate-type, --cert-identity) was not satisfied. Reading only the JSON would silently
        // discard exactly the enforcement we asked for, so the exit code is checked FIRST.
        const string perfect = """
        [ { "verificationResult": {
              "signature": { "certificate": { "sourceRepositoryURI": "https://github.com/dsazykin/Mainguard" } },
              "statement": { "subject": [ { "digest": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } } ] } } } ]
        """;

        Assert.True(GhCliBuildAttestationVerifier.ParseResult(0, perfect, "").Verified);
        Assert.False(GhCliBuildAttestationVerifier.ParseResult(1, perfect, "policy not satisfied").Verified);
    }

    [Fact]
    public void GhArgv_PinsTheRepositoryAndThePredicateType()
    {
        var argv = GhCliBuildAttestationVerifier.BuildArgv("/tmp/a.tar.gz", Repo, bundlePath: null);

        Assert.Equal(new[]
        {
            "gh", "attestation", "verify", "/tmp/a.tar.gz",
            "--repo", Repo,
            "--predicate-type", "https://slsa.dev/provenance/v1",
            "--format", "json",
        }, argv);

        // Offline verification points gh at a downloaded bundle; nothing else about the argv relaxes.
        var offline = GhCliBuildAttestationVerifier.BuildArgv("/tmp/a.tar.gz", Repo, "/tmp/a.sigstore.jsonl");
        Assert.Equal(new[] { "--bundle", "/tmp/a.sigstore.jsonl" }, offline.TakeLast(2));
    }

    [Fact]
    public async Task GhVerifier_AThrowingLaunch_IsAFailedCheck_NotAnEscape()
    {
        var verifier = new GhCliBuildAttestationVerifier(
            (_, _) => throw new System.ComponentModel.Win32Exception("gh not found"));

        var check = await verifier.VerifyAsync("/tmp/a", Repo, null, CancellationToken.None);

        Assert.False(check.Verified);
        Assert.Contains("could not run", check.Detail);
    }

    // ---- The gate over a real file ------------------------------------------------------------------

    private sealed class StubVerifier : IBuildAttestationVerifier
    {
        public BuildAttestationCheck Result = new(true, "ok", "https://github.com/" + Repo, Array.Empty<string>());
        public readonly List<(string Path, string Repo, string? Bundle)> Calls = new();

        /// <summary>When set, the reported subject digest is whatever the gate computed — the
        /// "attestation genuinely covers this file" case.</summary>
        public bool EchoDigest;
        public string? EchoedDigest;

        public Task<BuildAttestationCheck> VerifyAsync(
            string artifactPath, string expectedRepository, string? bundlePath, CancellationToken ct)
        {
            Calls.Add((artifactPath, expectedRepository, bundlePath));
            if (EchoDigest && File.Exists(artifactPath))
            {
                EchoedDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant();
                return Task.FromResult(Result with { SubjectDigestsSha256 = new[] { EchoedDigest } });
            }

            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Gate_HashesTheFileOnDisk_AndRequiresTheAttestationToNameIt()
    {
        var file = Path.Combine(Path.GetTempPath(), "mg-tarball-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "rootfs");
        try
        {
            var stub = new StubVerifier { EchoDigest = true };
            var gate = new BuildProvenanceGate(stub, isAttestedRelease: true, Repo);

            var ok = await gate.VerifyFileAsync(BuildArtifactKind.MainguardOsTarball, file, CancellationToken.None);
            Assert.Equal(BuildProvenanceOutcome.Verified, ok.Outcome);

            // Now the artifact is swapped after the attestation was made. Same attestation, new bytes.
            var attested = stub.EchoedDigest!;
            stub.EchoDigest = false;
            stub.Result = stub.Result with { SubjectDigestsSha256 = new[] { attested } };
            File.WriteAllText(file, "rootfs-with-a-backdoor");

            var refused = await gate.VerifyFileAsync(BuildArtifactKind.MainguardOsTarball, file, CancellationToken.None);
            Assert.True(refused.MustRefuse);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Gate_MissingFile_OnAnAttestedBuild_Refuses()
    {
        var gate = new BuildProvenanceGate(new StubVerifier(), isAttestedRelease: true, Repo);
        var verdict = await gate.VerifyFileAsync(
            BuildArtifactKind.MainguardOsTarball,
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.True(verdict.MustRefuse);
    }

    // ---- Wiring: the MainguardOS import ---------------------------------------------------------------

    [Fact]
    public async Task ImportDistro_IsRefused_BeforeAnyWslCommandRuns_WhenProvenanceFails()
    {
        var wsl = new RecordingRunner();
        var fs = new AlwaysPresentFileSystem();
        var options = new BootstrapOptions("/tmp/install", "/tmp/MainguardOS.tar.gz");
        var gate = new BuildProvenanceGate(
            new StubVerifier { Result = new BuildAttestationCheck(false, "no attestation") },
            isAttestedRelease: true, Repo);

        var step = new ImportDistroStep(wsl, fs, options, gate);

        var ex = await Assert.ThrowsAsync<BootstrapException>(
            () => step.ExecuteAsync(new Progress<string>(), CancellationToken.None));

        Assert.Contains("Refusing to import", ex.Message);
        // Nothing was imported: the refusal has to land before `wsl --import` touches the machine.
        Assert.Empty(wsl.Calls);
    }

    [Fact]
    public async Task ImportDistro_ProceedsOnANonAttestedBuild_WithTheGapNamedInTheLog()
    {
        var wsl = new RecordingRunner();
        var options = new BootstrapOptions("/tmp/install", "/tmp/MainguardOS.tar.gz");
        var log = new List<string>();
        var step = new ImportDistroStep(
            wsl, new AlwaysPresentFileSystem(), options,
            new BuildProvenanceGate(new StubVerifier(), isAttestedRelease: false, Repo));

        await step.ExecuteAsync(new Progress<string>(log.Add), CancellationToken.None);

        Assert.Contains(wsl.Calls, c => c.Contains("--import"));
        Assert.Contains(log, l => l.Contains("was NOT checked"));
    }

    /// <summary>Records every wsl argv and answers the staged-hash probe with <c>Sums</c>.</summary>
    private sealed class RecordingRunner : IWslRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        public string Sums = string.Empty;

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(args.Contains("sha256sum")
                ? new WslRunResult(0, Sums, "")
                : new WslRunResult(0, "", ""));
        }
    }

    [Fact]
    public async Task VmUpgrade_IsRefused_BeforeTheOldDistroIsTouched_WhenTheNewTarballHasNoProvenance()
    {
        var wsl = new RecordingRunner();
        var orchestrator = new VmUpgradeOrchestrator(
            wsl, moveRetryDelay: TimeSpan.Zero,
            provenance: new BuildProvenanceGate(
                new StubVerifier { Result = new BuildAttestationCheck(false, "no attestation") },
                isAttestedRelease: true, Repo));

        var result = await orchestrator.UpgradeAsync(
            new VmUpgradeOptions(
                TarballPath: Path.Combine(Path.GetTempPath(), "MainguardOS.tar.gz"),
                StagingInstallDir: "/tmp/staging",
                CanonicalInstallDir: "/tmp/vm"),
            progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        // The failure posture matters as much as the refusal: the running VM must still be intact.
        Assert.Equal(VmUpgradeFailureKind.OldDistroIntact, result.FailureKind);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("--import"));
    }

    private sealed class AlwaysPresentFileSystem : IBootstrapFileSystem
    {
        public string WslConfigPath => @"C:\Users\test\.wslconfig";
        public long TotalPhysicalMemoryBytes => 16L * 1024 * 1024 * 1024;
        public string? ReadWslConfig() => null;
        public void BackupWslConfig() { }
        public void WriteWslConfig(string content) { }
        public bool FileExists(string path) => true;
    }

    // ---- Wiring: the daemon payload promote -------------------------------------------------------------

    [Fact]
    public void DaemonPayloadManifest_CanonicalTextIsStable_AndSortedByPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mg-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, DaemonPayloadManifest.RequiredAssembly), "a");
            File.WriteAllText(Path.Combine(dir, "aaa.dll"), "b");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "c.dll"), "c");

            var text = DaemonPayloadManifest.Build(dir).ToCanonicalText();
            var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split("  ", 2)[1]).ToArray();

            Assert.Equal(new[] { "Mainguard.Server.dll", "aaa.dll", "sub/c.dll" }, paths);
            // Byte-stable: rebuilt from the same directory it must be identical, or CI could never
            // attest a manifest the app would reproduce.
            Assert.Equal(text, DaemonPayloadManifest.Build(dir).ToCanonicalText());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                DaemonPayloadManifest.Build(dir).CanonicalDigest());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AttestedManifestPath_IsASiblingOfThePayloadDir_NotAChildOfIt()
    {
        // A manifest INSIDE the payload would have to hash itself. It also has to be a stable name CI
        // can produce without knowing the install layout.
        Assert.Equal("/opt/app/payload/daemon.manifest",
            DaemonUpdater.AttestedManifestPathFor("/opt/app/payload/daemon"));
        Assert.Equal("/opt/app/payload/daemon.manifest",
            DaemonUpdater.AttestedManifestPathFor("/opt/app/payload/daemon/"));
    }

    [Fact]
    public async Task DaemonRefresh_OnAnAttestedBuild_RefusesWhenTheAttestedManifestIsMissing()
    {
        var payload = PayloadDir();
        try
        {
            var wsl = HashingRunner(payload);
            var updater = new DaemonUpdater(
                wsl, new BuildProvenanceGate(new StubVerifier(), isAttestedRelease: true, Repo));

            var result = await updater.RefreshAsync(payload, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("is missing or unreadable", result.Message);
            // Refused BEFORE the unit was stopped: the running daemon must survive a refusal.
            Assert.DoesNotContain(wsl.Calls, c => c.Contains("stop"));
        }
        finally
        {
            Directory.Delete(payload, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonRefresh_OnAnAttestedBuild_RefusesWhenThePayloadNoLongerReproducesTheAttestedManifest()
    {
        var payload = PayloadDir();
        try
        {
            // Write the manifest CI would have attested, then tamper with the payload afterwards —
            // exactly what an attacker with write access to the install dir does.
            var manifestPath = DaemonUpdater.AttestedManifestPathFor(payload);
            File.WriteAllText(manifestPath, DaemonPayloadManifest.Build(payload).ToCanonicalText());
            File.WriteAllText(Path.Combine(payload, DaemonPayloadManifest.RequiredAssembly), "backdoored");

            var wsl = HashingRunner(payload);
            var updater = new DaemonUpdater(
                wsl, new BuildProvenanceGate(new StubVerifier(), isAttestedRelease: true, Repo));

            var result = await updater.RefreshAsync(payload, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("altered since it was attested", result.Message);
            File.Delete(manifestPath);
        }
        finally
        {
            Directory.Delete(payload, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonRefresh_OnANonAttestedBuild_ProceedsWithTheGapNamedInTheResult()
    {
        var payload = PayloadDir();
        try
        {
            var updater = new DaemonUpdater(
                HashingRunner(payload),
                new BuildProvenanceGate(new StubVerifier(), isAttestedRelease: false, Repo));

            var result = await updater.RefreshAsync(payload, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains("was NOT checked", result.Message);
        }
        finally
        {
            Directory.Delete(payload, recursive: true);
        }
    }

    private static string PayloadDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mg-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DaemonPayloadManifest.RequiredAssembly), "managed-assembly-bytes");
        File.WriteAllText(Path.Combine(dir, "Mainguard.Server"), "apphost-bytes");
        return dir;
    }

    /// <summary>A VM that answers the staged-hash probe with the sums a faithful copy would produce.</summary>
    private static RecordingRunner HashingRunner(string payloadDir)
    {
        var lines = Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}  "
                + $"{DaemonUpdateCommands.StagingDir}/{Path.GetRelativePath(payloadDir, f).Replace('\\', '/')}");
        return new RecordingRunner { Sums = string.Join('\n', lines) + "\n" };
    }
}
