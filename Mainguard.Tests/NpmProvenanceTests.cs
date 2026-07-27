using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>MG-9, third-party half.</b> The update flow used to compute its pin hash from the very bytes it
/// had just downloaded, so a malicious tarball verified against itself — trust-on-first-use wearing a
/// verification costume. These tests cover the replacement: an expected digest that arrives inside an
/// ECDSA signature made by a key <b>compiled into Mainguard</b>, and a per-adapter requirement that
/// fails closed when it cannot be met.
///
/// <para><b>What is proved here (pure, offline, on Linux CI):</b> the signature algorithm and message
/// format, the integrity→bytes binding, every branch of the policy, the manifest's refusal to accept an
/// adapter with no declared rung, and the fact that the updater refuses to move a pin on a refused
/// verdict. <b>What is NOT:</b> the live registry round-trip, which is a single opt-in
/// <see cref="RequiresNpmRegistryFactAttribute"/> test that skips visibly when offline, plus the manual
/// matrix.</para>
/// </summary>
public class NpmProvenanceTests
{
    // A throwaway P-256 key pair standing in for npm's. The real pinned key's PRIVATE half is
    // (obviously) unavailable, so every "a good signature verifies" case injects this pair through the
    // policy's `keys` parameter. The pinned table itself is asserted separately, by shape.
    private sealed class TestKey : IDisposable
    {
        public readonly ECDsa Ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public const string KeyId = "SHA256:test-key";

        public IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(Ecdsa.ExportSubjectPublicKeyInfo()),
        };

        public NpmRegistrySignature Sign(string package, string version, string integrity)
        {
            var message = NpmRegistrySignatureVerifier.SignedMessage(package, version, integrity);
            var der = Ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new NpmRegistrySignature(KeyId, Convert.ToBase64String(der));
        }

        public void Dispose() => Ecdsa.Dispose();
    }

    private static readonly byte[] Tarball = Encoding.UTF8.GetBytes("the-real-tarball-bytes");
    private static readonly byte[] Evil = Encoding.UTF8.GetBytes("a-malicious-substitute");

    private static string IntegrityOf(byte[] bytes) => "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));

    private static NpmProvenanceEvidence Evidence(
        TestKey key, byte[] bytes, bool withProvenanceAttestation = false, byte[]? attestSubject = null)
    {
        var integrity = IntegrityOf(bytes);
        var predicates = new List<string> { NpmProvenancePolicy.NpmPublishPredicate };
        var subjects = new List<string>();
        if (withProvenanceAttestation)
        {
            predicates.Add(NpmProvenancePolicy.SlsaProvenancePredicate);
            subjects.Add(Convert.ToHexString(SHA512.HashData(attestSubject ?? bytes)).ToLowerInvariant());
        }

        return new NpmProvenanceEvidence(
            "pkg", "1.0.0", integrity, new[] { key.Sign("pkg", "1.0.0", integrity) }, predicates, subjects);
    }

    // ---- The signature itself ---------------------------------------------------------------------

    [Fact]
    public void SignedMessage_IsExactlyWhatNpmSigns()
    {
        // Captured from the live registry: this is the string npm's key covers. If this format drifts,
        // every signature stops verifying, so it is asserted literally rather than derived.
        Assert.Equal(
            "@anthropic-ai/claude-code@2.1.218:sha512-abc==",
            Encoding.UTF8.GetString(NpmRegistrySignatureVerifier.SignedMessage(
                "@anthropic-ai/claude-code", "2.1.218", "sha512-abc==")));
    }

    [Fact]
    public void Signature_VerifiesUnderAPinnedKey_AndFailsWhenTheVersionIsSwapped()
    {
        using var key = new TestKey();
        var integrity = IntegrityOf(Tarball);
        var signature = key.Sign("pkg", "1.0.0", integrity);

        Assert.True(NpmRegistrySignatureVerifier.Verify("pkg", "1.0.0", integrity, new[] { signature }, key.Keys));

        // The version is INSIDE the signed message, so a signature lifted from one release cannot be
        // replayed onto another — that is what stops a downgrade being dressed up as a valid pin.
        Assert.False(NpmRegistrySignatureVerifier.Verify("pkg", "0.9.0", integrity, new[] { signature }, key.Keys));
        Assert.False(NpmRegistrySignatureVerifier.Verify("other", "1.0.0", integrity, new[] { signature }, key.Keys));
    }

    [Fact]
    public void Signature_FromAnUnpinnedKeyId_IsIgnored_NotTrusted()
    {
        using var attacker = new TestKey();
        var integrity = IntegrityOf(Tarball);
        var forged = attacker.Sign("pkg", "1.0.0", integrity);

        // The attacker signs correctly — with THEIR key. Naming a key id we do not pin must not be a
        // way to nominate your own trust anchor.
        Assert.False(NpmRegistrySignatureVerifier.Verify(
            "pkg", "1.0.0", integrity, new[] { forged },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["SHA256:some-other-key"] = "AAAA" }));
    }

    [Fact]
    public void Signature_MissingOrMalformed_IsFalse_NeverAnException()
    {
        using var key = new TestKey();
        var integrity = IntegrityOf(Tarball);
        Assert.False(NpmRegistrySignatureVerifier.Verify("pkg", "1.0.0", integrity, null, key.Keys));
        Assert.False(NpmRegistrySignatureVerifier.Verify("pkg", "1.0.0", integrity, Array.Empty<NpmRegistrySignature>(), key.Keys));
        Assert.False(NpmRegistrySignatureVerifier.Verify("pkg", "1.0.0", null, new[] { key.Sign("pkg", "1.0.0", integrity) }, key.Keys));
        Assert.False(NpmRegistrySignatureVerifier.Verify(
            "pkg", "1.0.0", integrity, new[] { new NpmRegistrySignature(TestKey.KeyId, "not-base64!!") }, key.Keys));
    }

    [Fact]
    public void PinnedKeyTable_CarriesNpmsCurrentKey_AndNotTheExpiredOne()
    {
        Assert.True(NpmSigningKeys.Pinned.ContainsKey(NpmSigningKeys.CurrentKeyId));
        // The 2025 key expired; keeping it would be a silent downgrade path.
        Assert.DoesNotContain("SHA256:jl3bwswu80PjjokCgh0o2w5c2U4LhQAE57gj9cz1kzA", NpmSigningKeys.Pinned.Keys);
        // It must be a usable P-256 SPKI, not a typo that would fail every check at runtime.
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(NpmSigningKeys.Pinned[NpmSigningKeys.CurrentKeyId]), out _);
        Assert.Equal(256, ecdsa.KeySize);
    }

    // ---- Integrity parsing / binding --------------------------------------------------------------

    [Theory]
    [InlineData("sha1-abc=")]          // broken hash: refused outright
    [InlineData("md5-abc=")]
    [InlineData("sha512")]             // no separator
    [InlineData("sha512-")]            // no digest
    [InlineData("sha512-not base64")]  // whitespace = an SRI list; a pin must be unambiguous
    [InlineData("")]
    [InlineData(null)]
    public void Integrity_Malformed_OrWeak_IsRefused(string? integrity)
        => Assert.False(NpmIntegrity.TryParse(integrity, out _, out _));

    [Fact]
    public void Integrity_BindsToTheBytes()
    {
        var integrity = IntegrityOf(Tarball);
        Assert.True(NpmIntegrity.Matches(integrity, Tarball));
        Assert.False(NpmIntegrity.Matches(integrity, Evil));
    }

    // ---- The policy: the actual MG-9 decision ------------------------------------------------------

    [Fact]
    public void Policy_RegistrySignatureRung_AcceptsSignedBytes_AndSaysWhatItDoesNotProve()
    {
        using var key = new TestKey();
        var verdict = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmRegistrySignature, Evidence(key, Tarball), Tarball, key.Keys);

        Assert.Equal(NpmProvenanceOutcome.RegistrySignatureVerified, verdict.Outcome);
        Assert.True(verdict.IsVerified);
        Assert.False(verdict.MustRefuse);
        // The reason must not overstate: this rung is not publisher provenance.
        Assert.Contains("does NOT prove the publisher", verdict.Reason);
    }

    [Fact]
    public void Policy_RefusesTheSubstitutedTarball_TheExactMG9CASE()
    {
        using var key = new TestKey();

        // The registry (or anything standing in for it) signed the REAL artifact, then served evil
        // bytes. Under the old self-derived hash this installed cleanly, because the hash was computed
        // from the evil bytes themselves. Here the signed integrity is what the bytes must match.
        var verdict = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmRegistrySignature, Evidence(key, Tarball), Evil, key.Keys);

        Assert.Equal(NpmProvenanceOutcome.Refused, verdict.Outcome);
        Assert.True(verdict.MustRefuse);
        Assert.Contains("not the bytes npm signed", verdict.Reason);
    }

    [Fact]
    public void Policy_RefusesWhenTheRegistryOffersNoSignature()
    {
        using var key = new TestKey();
        var unsigned = Evidence(key, Tarball) with { Signatures = Array.Empty<NpmRegistrySignature>() };

        var verdict = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmRegistrySignature, unsigned, Tarball, key.Keys);

        // Fail-closed: "the metadata simply had no signature" must not be a way through.
        Assert.True(verdict.MustRefuse);
    }

    [Fact]
    public void Policy_RefusesWhenNoEvidenceAtAll()
    {
        var verdict = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmRegistrySignature, evidence: null, Tarball);
        Assert.True(verdict.MustRefuse);
    }

    [Fact]
    public void Policy_BuildProvenanceRung_RequiresTheSlsaAttestation_NotJustThePublishOne()
    {
        using var key = new TestKey();

        // Every package with any attestation carries npm's publish attestation. Accepting that as build
        // provenance would make the top rung indistinguishable from the one below it.
        var publishOnly = Evidence(key, Tarball, withProvenanceAttestation: false);
        var refused = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmBuildProvenance, publishOnly, Tarball, key.Keys);
        Assert.True(refused.MustRefuse);
        Assert.Contains("publishes none", refused.Reason);

        var withProvenance = Evidence(key, Tarball, withProvenanceAttestation: true);
        var accepted = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmBuildProvenance, withProvenance, Tarball, key.Keys);
        Assert.Equal(NpmProvenanceOutcome.BuildProvenanceVerified, accepted.Outcome);
    }

    [Fact]
    public void Policy_BuildProvenanceRung_RefusesAnAttestationForDifferentBytes()
    {
        using var key = new TestKey();

        // A real, published SLSA attestation — for some OTHER artifact. Present-but-unbound is exactly
        // the mistake a "does an attestation exist?" check makes.
        var misbound = Evidence(key, Tarball, withProvenanceAttestation: true, attestSubject: Evil);
        var verdict = NpmProvenancePolicy.Decide(
            "tool", AdapterProvenanceLevel.NpmBuildProvenance, misbound, Tarball, key.Keys);

        Assert.True(verdict.MustRefuse);
        Assert.Contains("does not name these bytes as its subject", verdict.Reason);
    }

    [Fact]
    public void Policy_NoneRung_ProceedsButNeverReadsAsVerified()
    {
        var verdict = NpmProvenancePolicy.Decide("tool", AdapterProvenanceLevel.None, evidence: null, Tarball);

        Assert.Equal(NpmProvenanceOutcome.KnownUnverified, verdict.Outcome);
        Assert.False(verdict.MustRefuse);   // it installs …
        Assert.False(verdict.IsVerified);   // … but nothing anywhere may call it verified
        Assert.Contains("NOTHING about the origin", verdict.Reason);
        Assert.Contains("trust-on-first-use", verdict.Reason);
    }

    // ---- The registry JSON shapes (captured from the live registry, replayed offline) --------------

    [Fact]
    public void Source_ParsesTheRealRegistryShapes()
    {
        // Trimmed from the actual responses for @openai/codex@0.145.0 on 2026-07-26. The DSSE payload is
        // a base64 in-toto statement whose subject digest is the tarball's sha512 — the binding the top
        // rung checks.
        const string metadata = """
        {
          "name": "@openai/codex",
          "version": "0.145.0",
          "dist": {
            "integrity": "sha512-abc==",
            "signatures": [{ "keyid": "SHA256:DhQ8wR5APBvFHLF/+Tc+AYvPOdTpcIDqOhxsBHRwC7U", "sig": "MEQCIA==" }]
          }
        }
        """;

        var statement = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"_type":"https://in-toto.io/Statement/v1","subject":[{"name":"pkg:npm/%40openai/codex@0.145.0","digest":{"sha512":"FCF48F485BA3"}}]}"""));
        var attestations = $$"""
        {
          "attestations": [
            { "predicateType": "https://github.com/npm/attestation/tree/main/specs/publish/v0.1",
              "bundle": { "dsseEnvelope": { "payload": "{{statement}}" } } },
            { "predicateType": "https://slsa.dev/provenance/v1",
              "bundle": { "dsseEnvelope": { "payload": "{{statement}}" } } }
          ]
        }
        """;

        var evidence = HttpNpmProvenanceSource.Parse("@openai/codex", "0.145.0", metadata, attestations);

        Assert.Equal("sha512-abc==", evidence.Integrity);
        Assert.Equal(NpmSigningKeys.CurrentKeyId, Assert.Single(evidence.Signatures).KeyId);
        Assert.Contains(NpmProvenancePolicy.SlsaProvenancePredicate, evidence.AttestationPredicateTypes);
        // Only the SLSA statement's subject counts, and it is normalised to lowercase hex.
        Assert.Equal("fcf48f485ba3", Assert.Single(evidence.ProvenanceSubjectDigestsSha512));
    }

    [Fact]
    public void Source_MalformedJson_YieldsEmptyEvidence_WhichEveryRungRefuses()
    {
        var evidence = HttpNpmProvenanceSource.Parse("pkg", "1.0.0", "{ not json", null);
        Assert.Null(evidence.Integrity);
        Assert.True(NpmProvenancePolicy
            .Decide("tool", AdapterProvenanceLevel.NpmRegistrySignature, evidence, Tarball).MustRefuse);
    }

    // ---- The manifest requirement -------------------------------------------------------------------

    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string ManifestWith(string provenanceField) => $$"""
    {
      "adapters": [
        { "id": "x", "displayName": "X", "version": "1.0.0", {{provenanceField}}
          "sha256": "{{Sha}}", "installCmd": ["npm", "install"], "configShims": [],
          "healthProbe": { "command": ["x", "--version"], "expectedVersionSubstring": "1.0.0" } }
      ]
    }
    """;

    [Fact]
    public void Manifest_RefusesAnAdapterThatDeclaresNoProvenanceRung()
    {
        // The point of the field is to force an answer. A default would answer it silently.
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(ManifestWith("")));
        Assert.Equal(AdapterManifestError.MissingProvenance, ex.Error);
    }

    [Fact]
    public void Manifest_RefusesAnUnknownRung_RatherThanFallingBackToAWeakerOne()
    {
        var ex = Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(ManifestWith("\"provenance\": \"npm-registry-signatur\",")));
        Assert.Equal(AdapterManifestError.MissingProvenance, ex.Error);
    }

    [Theory]
    [InlineData("npm-build-provenance", AdapterProvenanceLevel.NpmBuildProvenance)]
    [InlineData("npm-registry-signature", AdapterProvenanceLevel.NpmRegistrySignature)]
    [InlineData("none", AdapterProvenanceLevel.None)]
    public void Manifest_ParsesEveryKnownRung(string wire, AdapterProvenanceLevel expected)
    {
        var manifest = AdapterManifest.Parse(ManifestWith($"\"provenance\": \"{wire}\","));
        Assert.Equal(expected, manifest.Adapters[0].ProvenanceLevel);
    }

    /// <summary>
    /// The shipped starter channel, checked against what the registry actually publishes. Only
    /// <c>@openai/codex</c> opted into npm provenance (verified 2026-07-26 at both the pinned and the
    /// then-current versions); the other four 404 on the attestations endpoint and therefore sit at the
    /// registry-signature rung. This test is the guard that stops that from drifting into a comfortable
    /// fiction — raising an adapter's rung must be a deliberate edit that someone re-checked.
    /// </summary>
    [Fact]
    public void ShippedManifest_DeclaresTheRungEachPublisherActuallySupports()
    {
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
        var levels = manifest.Adapters.ToDictionary(a => a.Id, a => a.ProvenanceLevel, StringComparer.Ordinal);

        Assert.Equal(AdapterProvenanceLevel.NpmBuildProvenance, levels["codex"]);
        foreach (var id in new[] { "claude-code", "gemini-cli", "qwen-code", "opencode" })
            Assert.Equal(AdapterProvenanceLevel.NpmRegistrySignature, levels[id]);

        // Nothing shipped may sit on the rung that verifies nothing.
        Assert.DoesNotContain(AdapterProvenanceLevel.None, levels.Values);
    }

    // ---- The updater refuses to move a pin on a refused verdict --------------------------------------

    [Fact]
    public async Task ApplyUpdate_IsRefused_AndLeavesThePinUntouched_WhenProvenanceFails()
    {
        var f = new UpdaterFixture();
        f.Provenance.Verdict = new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused, "forged tarball");

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(
            () => f.Updater.ApplyUpdateAsync("tool", "2.0.0"));

        Assert.Equal(AdapterChannelError.ProvenanceRejected, ex.Error);
        // The refusal must happen BEFORE the pin moves: a pin written by a refused update would be
        // installed by the next EnsureAsync without any further check.
        Assert.Empty(f.Pins.Pins);
        Assert.Contains(f.Log, l => l.Contains("forged tarball"));
    }

    [Fact]
    public async Task ApplyUpdate_AsksAboutTheDeclaredRung_ForTheExactPackageAndVersion()
    {
        var f = new UpdaterFixture();
        f.Source.PayloadToServe = UpdaterFixture.PayloadNew; // the channel now serves the accepted bytes
        await f.Updater.ApplyUpdateAsync("tool", "2.0.0");

        var call = Assert.Single(f.Provenance.Calls);
        Assert.Equal(("tool", AdapterProvenanceLevel.NpmRegistrySignature, "tool", "2.0.0"),
            (call.AdapterId, call.Level, call.Package, call.Version));
        Assert.Single(f.Pins.Pins);
    }

    [Fact]
    public async Task EnsureLatest_ProvenanceRefusal_FallsBackToTheREVIEWEDShippedPin_Loudly()
    {
        var f = new UpdaterFixture();
        f.Provenance.Verdict = new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused, "unsigned");

        await f.Updater.EnsureLatestAsync("tool");

        // Refusing the registry's bytes is non-negotiable; refusing the USER an agent is not. The
        // fallback installs the manifest pin, whose hash is a constant reviewed into the repo.
        Assert.Empty(f.Pins.Pins);
        Assert.Contains(f.Updater.RefusedUpdates, r => r.Contains("failed its provenance check"));
        Assert.Equal("1.2.3", f.Host.InstalledVersion);
    }

    /// <summary>A minimal updater world: the fake channel/host from the adapter tests plus a driveable
    /// provenance gate. Kept local so this file states its own preconditions.</summary>
    private sealed class UpdaterFixture
    {
        internal static readonly byte[] PayloadNew = Encoding.UTF8.GetBytes("tool-payload-2.0.0");
        private static readonly byte[] PayloadOld = Encoding.UTF8.GetBytes("tool-payload-1.2.3");

        public readonly AgentCliUpdateServiceTests.FakeProvenanceGate Provenance = new();
        public readonly AgentCliUpdateServiceTests.InMemoryPinStore Pins = new();
        public readonly AdapterChannelTests.FakeInstallHost Host = new();
        public readonly AdapterChannelTests.FakeSource Source;
        public readonly List<string> Log = new();
        public readonly AgentCliUpdateService Updater;

        public UpdaterFixture()
        {
            var manifest = $$"""
            {
              "adapters": [
                {
                  "id": "tool",
                  "displayName": "Tool",
                  "version": "1.2.3",
                  "provenance": "npm-registry-signature",
                  "sha256": "{{Convert.ToHexString(SHA256.HashData(PayloadOld)).ToLowerInvariant()}}",
                  "installCmd": ["npm", "install", "-g", "--prefix", "/home/mainguard/mainguard/adapters", "{payload}"],
                  "configShims": [],
                  "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "1.2.3" },
                  "payloadUrl": "https://registry.npmjs.org/tool/-/tool-1.2.3.tgz",
                  "launch": ["/opt/mainguard/adapters/bin/tool"]
                }
              ]
            }
            """;

            Source = new AdapterChannelTests.FakeSource { ManifestToServe = manifest, PayloadToServe = PayloadOld };
            var channel = new AdapterChannel(Source, Host, new AdapterChannelTests.FakeCache(manifest),
                delay: (_, _) => Task.CompletedTask, pins: Pins);
            Updater = new AgentCliUpdateService(
                channel, Pins, new NpmStub(PayloadNew), Log.Add, Provenance);
        }

        /// <summary>Serves just enough registry for the updater's own fetches; provenance comes from the
        /// gate, not from here.</summary>
        private sealed class NpmStub : System.Net.Http.HttpMessageHandler
        {
            private readonly byte[] _tarball;
            public NpmStub(byte[] tarball) => _tarball = tarball;

            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                if (url.EndsWith("/latest", StringComparison.Ordinal))
                    return Json("""{ "version": "2.0.0" }""");
                if (url.EndsWith(".tgz", StringComparison.Ordinal))
                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.ByteArrayContent(_tarball),
                    });
                return Json("""{ "dist": { "tarball": "https://registry.npmjs.org/tool/-/tool-2.0.0.tgz" } }""");
            }

            private static Task<System.Net.Http.HttpResponseMessage> Json(string json) =>
                Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"),
                });
        }
    }

    // ---- The one test that touches the real registry -------------------------------------------------

    /// <summary>
    /// <b>End-to-end against npmjs.org, skipped (visibly) when the registry is unreachable.</b>
    ///
    /// <para>Everything above replays captured shapes; this proves the chain against the live service:
    /// npm's real signature, under the key COMPILED INTO this build, over the real integrity, matching
    /// the real tarball, matching the sha256 pinned in <c>adapters.starter.json</c>. If npm rotates its
    /// key or changes the signed message format, this is what notices.</para>
    ///
    /// <para>It is a <see cref="Xunit.FactAttribute"/> subclass rather than an early <c>return</c>
    /// because an early return reports a green "Passed" while asserting nothing.</para>
    /// </summary>
    [RequiresNpmRegistryFact]
    public async Task LiveRegistry_TheWholeChainHolds_ForEveryShippedAdapter()
    {
        // Generous, because this runs alongside the rest of the suite and pulls five multi-MB tarballs;
        // a transient stall must not read as "npm rotated its key". Retried for the same reason — what
        // is under test is the cryptographic chain, not the network.
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var source = new HttpNpmProvenanceSource(http);
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());

        foreach (var spec in manifest.Adapters)
        {
            var package = AgentCliUpdateService.TryParseNpmPackage(spec.PayloadUrl);
            Assert.NotNull(package);

            var evidence = await Retry(() => source.FetchAsync(package!, spec.Version, CancellationToken.None));
            var tarball = await Retry(() => http.GetByteArrayAsync(spec.PayloadUrl!));

            // The pinned sha256 must be the hash of what the URL serves — and the rung must hold under
            // the REAL pinned key (no `keys` override here; that is the whole point).
            Assert.Equal(spec.Sha256.ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(tarball)).ToLowerInvariant());

            var verdict = NpmProvenancePolicy.Decide(spec.Id, spec.ProvenanceLevel, evidence, tarball);
            Assert.False(verdict.MustRefuse, $"{spec.Id}: {verdict.Reason}");
            Assert.True(verdict.IsVerified, $"{spec.Id} declares {spec.ProvenanceLevel} but: {verdict.Reason}");
        }
    }

    /// <summary>Three attempts with a short backoff. Only transport faults are retried; an assertion
    /// failure inside the loop body is not possible because nothing is asserted here.</summary>
    private static async Task<T> Retry<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}
