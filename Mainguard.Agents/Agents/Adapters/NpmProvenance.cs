using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// How much origin assurance an agent-CLI tarball is expected to carry, declared PER ADAPTER in
/// <c>adapters.starter.json</c> because upstream publishers differ and pretending otherwise is the
/// bug this type exists to prevent (MG-9).
///
/// <para><b>The ladder is ordered.</b> Each rung includes everything below it. The declared rung is a
/// REQUIREMENT, not a hint: <see cref="NpmProvenancePolicy.Decide"/> refuses a payload that cannot meet
/// the rung its adapter declares, and an adapter that carries no assurance must SAY so
/// (<see cref="None"/>) rather than silently landing on the weakest check.</para>
/// </summary>
public enum AdapterProvenanceLevel
{
    /// <summary>
    /// <b>No origin assurance at all.</b> The only thing that governs the install is the sha256 pin,
    /// and for a pin the updater derived from its own download that is trust-on-first-use — it proves
    /// the bytes have not changed since we first saw them, never that they came from the publisher.
    ///
    /// <para>Declaring this is not a way to opt out of verification; it is a way to make the absence
    /// of verification <i>legible</i>. Every install of such an adapter reports
    /// <see cref="NpmProvenanceOutcome.KnownUnverified"/>, which callers surface. No adapter shipped in
    /// the starter channel uses this rung.</para>
    /// </summary>
    None,

    /// <summary>
    /// The npm registry's own ECDSA signature over <c>{name}@{version}:{integrity}</c>, checked against
    /// a <b>compiled-in</b> public key (<see cref="NpmSigningKeys"/>) and then used to gate the bytes:
    /// the tarball we hold must hash to the <c>integrity</c> that signature covers.
    ///
    /// <para><b>Why this breaks the circularity.</b> The old flow hashed the bytes it had just fetched
    /// and stored that hash as the pin — the pin then verified against itself, so anything able to
    /// serve the tarball also chose the hash. Here the expected digest arrives inside a signature that
    /// only npm's private key can produce, and that key's public half ships inside Mainguard. A
    /// registry mirror, a CDN edge, a proxy, or anything else on the wire can no longer substitute a
    /// tarball: it would have to forge a P-256 signature.</para>
    ///
    /// <para><b>What it does NOT prove:</b> that the <i>publisher</i> built those bytes. npm signs what
    /// it stored; a compromised publishing account, or npm itself, is outside this rung. That is
    /// <see cref="NpmBuildProvenance"/>'s job.</para>
    /// </summary>
    NpmRegistrySignature,

    /// <summary>
    /// Everything in <see cref="NpmRegistrySignature"/>, plus a published <b>build provenance</b>
    /// attestation (SLSA <c>https://slsa.dev/provenance/v1</c>) whose in-toto subject digest binds to
    /// the exact tarball we hold — i.e. the publisher's CI, not a laptop, produced these bytes.
    ///
    /// <para>Only packages whose publisher opted into <c>npm publish --provenance</c> have this. See the
    /// per-adapter notes in <c>adapters.starter.json</c> for who actually does.</para>
    ///
    /// <para><b>Stated limit:</b> Mainguard checks that the attestation exists and that it is bound by
    /// digest to our bytes. It does <b>not</b> validate the Sigstore certificate chain (Fulcio) or the
    /// Rekor inclusion proof in-process — that needs a full Sigstore verifier, and the honest place for
    /// it today is <c>npm audit signatures</c> in the manual matrix. So this rung's cryptographic root
    /// is still the pinned npm key of the rung below; the attestation adds a build binding on top of
    /// it, and its absence is a hard refusal. Do not read it as full SLSA verification.</para>
    /// </summary>
    NpmBuildProvenance,
}

/// <summary>What a provenance check concluded. Four outcomes, not two, for the same reason
/// <c>SignatureVerdictKind</c> has three: "checked and fine", "checked and rejected", and "not checked"
/// must never share a spelling.</summary>
public enum NpmProvenanceOutcome
{
    /// <summary>A build-provenance attestation was present and digest-bound to these bytes, and the
    /// registry signature held under a pinned key.</summary>
    BuildProvenanceVerified,

    /// <summary>The registry signature held under a pinned key and covered these exact bytes. Not
    /// publisher provenance — see <see cref="AdapterProvenanceLevel.NpmRegistrySignature"/>.</summary>
    RegistrySignatureVerified,

    /// <summary>The adapter declares <see cref="AdapterProvenanceLevel.None"/>: nothing was verified and
    /// the caller is being told so explicitly. Never produced for an adapter that declares a higher
    /// rung — that case is <see cref="Refused"/>.</summary>
    KnownUnverified,

    /// <summary>The adapter's declared rung could not be met. The install/update MUST NOT proceed.</summary>
    Refused,
}

/// <summary>One provenance answer plus the sentence that explains it (for the log the user reads).</summary>
public sealed record NpmProvenanceVerdict(NpmProvenanceOutcome Outcome, string Reason)
{
    /// <summary>True only for <see cref="NpmProvenanceOutcome.Refused"/>. Callers refuse on this.</summary>
    public bool MustRefuse => Outcome == NpmProvenanceOutcome.Refused;

    /// <summary>True when something cryptographic actually held. <see cref="NpmProvenanceOutcome.KnownUnverified"/>
    /// is deliberately excluded — an unverified adapter must never read as verified anywhere.</summary>
    public bool IsVerified => Outcome is NpmProvenanceOutcome.BuildProvenanceVerified
        or NpmProvenanceOutcome.RegistrySignatureVerified;
}

/// <summary>One <c>dist.signatures</c> entry from the npm registry.</summary>
/// <param name="KeyId">The npm key fingerprint (e.g. <c>SHA256:DhQ8wR5APBvFHLF/+Tc+AYvPOdTpcIDqOhxsBHRwC7U</c>).</param>
/// <param name="Signature">Base64 DER ECDSA-P256/SHA-256 over <c>{name}@{version}:{integrity}</c>.</param>
public sealed record NpmRegistrySignature(string KeyId, string Signature);

/// <summary>
/// Everything the npm registry said about one exact <c>package@version</c> that bears on provenance.
/// Deliberately a plain record with no I/O so the whole policy above it stays pure and runs on Linux CI.
/// </summary>
/// <param name="Integrity">The signed <c>dist.integrity</c> (<c>sha512-&lt;base64&gt;</c>).</param>
/// <param name="Signatures">The <c>dist.signatures</c> entries.</param>
/// <param name="AttestationPredicateTypes">Predicate types published at the attestations endpoint.</param>
/// <param name="ProvenanceSubjectDigestsSha512">Lowercase-hex sha512 subject digests carried by the
/// SLSA build-provenance attestation(s). These are what bind an attestation to specific bytes.</param>
public sealed record NpmProvenanceEvidence(
    string Package,
    string Version,
    string? Integrity,
    IReadOnlyList<NpmRegistrySignature> Signatures,
    IReadOnlyList<string> AttestationPredicateTypes,
    IReadOnlyList<string> ProvenanceSubjectDigestsSha512)
{
    public static NpmProvenanceEvidence Empty(string package, string version) =>
        new(package, version, null, Array.Empty<NpmRegistrySignature>(),
            Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// The npm registry's public signing keys, <b>compiled into Mainguard</b>.
///
/// <para>This being a constant is the entire point. Fetching the key list from
/// <c>https://registry.npmjs.org/-/npm/v1/keys</c> at verification time would put the trust anchor on
/// the same wire as the artifact, which is the shape of the bug we are fixing: whoever can serve you a
/// tarball could also serve you the key that "verifies" it. Pinning the key here means a substituted
/// tarball has to forge a P-256 signature.</para>
///
/// <para>Refreshing: npm rotates rarely and publishes both keys with an <c>expires</c> field. When a new
/// key appears, add it here in a reviewed commit — never load one at runtime.
/// Verified against the live registry 2026-07-26.</para>
/// </summary>
public static class NpmSigningKeys
{
    /// <summary>npm's current (non-expiring) registry signing key: keyid → base64 SPKI (P-256).</summary>
    public const string CurrentKeyId = "SHA256:DhQ8wR5APBvFHLF/+Tc+AYvPOdTpcIDqOhxsBHRwC7U";

    private const string CurrentKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEY6Ya7W++7aUPzvMTrezH6Ycx3c+HOKYCcNGybJZSCJq/fd7Qa8uuAKtdIkUQtQiEKERhAmE5lMMJhP8OkDOa2g==";

    /// <summary>keyid → base64 SubjectPublicKeyInfo. The expired 2025 key is deliberately absent: an
    /// expired key must not verify anything, and keeping it here would be a silent downgrade path.</summary>
    public static readonly IReadOnlyDictionary<string, string> Pinned =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CurrentKeyId] = CurrentKeySpki,
        };
}

/// <summary>Parses and checks Subresource-Integrity strings (<c>sha512-&lt;base64&gt;</c>) — the form npm
/// uses for <c>dist.integrity</c>. Pure.</summary>
public static class NpmIntegrity
{
    /// <summary>The hash algorithms an integrity string may name. SHA-1 is refused outright: npm still
    /// carries a legacy <c>dist.shasum</c>, and accepting it here would let a downgrade to a broken hash
    /// pass as verification.</summary>
    private static readonly string[] Allowed = { "sha512", "sha384", "sha256" };

    /// <summary>Splits <c>&lt;alg&gt;-&lt;base64&gt;</c>. Returns false for anything malformed, empty,
    /// multi-entry, or naming a disallowed algorithm.</summary>
    public static bool TryParse(string? integrity, out string algorithm, out byte[] digest)
    {
        algorithm = string.Empty;
        digest = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(integrity))
            return false;

        var value = integrity.Trim();
        // SRI permits a whitespace-separated list; a pin must be unambiguous, so refuse a list.
        if (value.Any(char.IsWhiteSpace))
            return false;

        var dash = value.IndexOf('-');
        if (dash <= 0 || dash == value.Length - 1)
            return false;

        var alg = value[..dash].ToLowerInvariant();
        if (!Allowed.Contains(alg))
            return false;

        try
        {
            digest = Convert.FromBase64String(value[(dash + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        algorithm = alg;
        return digest.Length > 0;
    }

    /// <summary>True when <paramref name="payload"/> hashes to <paramref name="integrity"/> under the
    /// algorithm the integrity string names. Constant-time comparison — this decides whether bytes get
    /// installed.</summary>
    public static bool Matches(string? integrity, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!TryParse(integrity, out var algorithm, out var expected))
            return false;

        var actual = algorithm switch
        {
            "sha512" => SHA512.HashData(payload),
            "sha384" => SHA384.HashData(payload),
            "sha256" => SHA256.HashData(payload),
            _ => Array.Empty<byte>(),
        };

        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Lowercase hex of the integrity's digest — the form an in-toto attestation subject uses,
    /// so the two can be compared. Null when the integrity string is unusable.</summary>
    public static string? ToHexDigest(string? integrity) =>
        TryParse(integrity, out _, out var digest) ? Convert.ToHexString(digest).ToLowerInvariant() : null;
}

/// <summary>Verifies an npm registry <c>dist.signatures</c> entry against the pinned public keys. Pure —
/// no network, no clock, no ambient state, so every case runs on Linux CI.</summary>
public static class NpmRegistrySignatureVerifier
{
    /// <summary>The exact bytes npm signs: <c>{name}@{version}:{integrity}</c>, UTF-8.</summary>
    public static byte[] SignedMessage(string package, string version, string integrity) =>
        Encoding.UTF8.GetBytes($"{package}@{version}:{integrity}");

    /// <summary>
    /// True when at least one signature verifies under a pinned key over the exact
    /// <c>{name}@{version}:{integrity}</c> message. A signature naming an UNPINNED key id is ignored
    /// rather than trusted — an attacker who could name their own key id would otherwise verify
    /// themselves.
    /// </summary>
    /// <param name="keys">keyid → base64 SPKI. Injected so tests drive a locally generated key pair
    /// instead of needing npm's private key (which is, obviously, unavailable).</param>
    public static bool Verify(
        string package,
        string version,
        string? integrity,
        IReadOnlyList<NpmRegistrySignature>? signatures,
        IReadOnlyDictionary<string, string>? keys = null)
    {
        if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(version))
            return false;
        if (!NpmIntegrity.TryParse(integrity, out _, out _))
            return false;
        if (signatures is null || signatures.Count == 0)
            return false;

        var pinned = keys ?? NpmSigningKeys.Pinned;
        var message = SignedMessage(package, version, integrity!.Trim());

        foreach (var signature in signatures)
        {
            if (signature is null || !pinned.TryGetValue(signature.KeyId ?? string.Empty, out var spki))
                continue;

            byte[] key, sig;
            try
            {
                key = Convert.FromBase64String(spki);
                sig = Convert.FromBase64String(signature.Signature ?? string.Empty);
            }
            catch (FormatException)
            {
                continue;
            }

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                if (ecdsa.VerifyData(message, sig, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // A malformed key or signature is a failed check, never a pass.
            }
        }

        return false;
    }
}

/// <summary>
/// <b>The MG-9 decision function.</b> Given what an adapter DECLARES it must carry and what the registry
/// actually offered, decides whether these bytes may be installed — and says, in one sentence a user can
/// read, exactly which assurance held.
///
/// <para>Pure and total: no network, no I/O, no statics beyond the compiled-in key table (itself
/// injectable). Every branch is unit-tested on Linux, which is the whole reason the policy lives apart
/// from the fetching.</para>
///
/// <para><b>Fail-closed where it applies.</b> For <see cref="AdapterProvenanceLevel.NpmRegistrySignature"/>
/// and <see cref="AdapterProvenanceLevel.NpmBuildProvenance"/> a missing, malformed, unpinned-key, or
/// non-matching attestation is <see cref="NpmProvenanceOutcome.Refused"/> — never a warning that
/// proceeds. There is no "degrade to the hash" path, because degrading to a self-derived hash is
/// precisely the finding.</para>
///
/// <para><b>Degrade-loudly only at the bottom rung.</b> <see cref="AdapterProvenanceLevel.None"/> returns
/// <see cref="NpmProvenanceOutcome.KnownUnverified"/> and lets the install proceed. That rung exists so a
/// CLI whose publisher signs nothing can still be offered — but only after someone wrote "none" into the
/// manifest in a reviewed commit, and only with every install saying out loud that nothing was verified.
/// It is not reachable by accident: <c>AdapterManifest.Parse</c> refuses a spec with no
/// <c>provenance</c> field at all.</para>
/// </summary>
public static class NpmProvenancePolicy
{
    /// <summary>The SLSA v1 build-provenance predicate npm publishes for <c>--provenance</c> packages.</summary>
    public const string SlsaProvenancePredicate = "https://slsa.dev/provenance/v1";

    /// <summary>The registry's own publish attestation. Present for every package that has any
    /// attestation at all, so it is explicitly NOT accepted as build provenance.</summary>
    public const string NpmPublishPredicate = "https://github.com/npm/attestation/tree/main/specs/publish/v0.1";

    public static NpmProvenanceVerdict Decide(
        string adapterId,
        AdapterProvenanceLevel declared,
        NpmProvenanceEvidence? evidence,
        byte[] payload,
        IReadOnlyDictionary<string, string>? keys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(payload);

        if (declared == AdapterProvenanceLevel.None)
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.KnownUnverified,
                $"'{adapterId}' declares provenance 'none': NOTHING about the origin of these bytes was "
                + "verified. The sha256 pin proves only that they are the bytes we pinned, and for a pin "
                + "this app derived from its own download that is trust-on-first-use, not authenticity.");
        }

        if (evidence is null)
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}' requires {Describe(declared)}, but no registry provenance metadata could "
                + "be obtained at all — refusing to install unverified bytes.");
        }

        if (!NpmRegistrySignatureVerifier.Verify(
                evidence.Package, evidence.Version, evidence.Integrity, evidence.Signatures, keys))
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}' requires {Describe(declared)}, but the npm registry signature for "
                + $"{evidence.Package}@{evidence.Version} did not verify under any pinned npm key "
                + $"(pinned: {NpmSigningKeys.CurrentKeyId}) — refusing to install.");
        }

        // The signature covers `name@version:integrity`, so the integrity is now attested. Bind it to the
        // bytes actually in hand: without this the signature would merely prove that SOME tarball with
        // that digest exists, not that we hold it.
        if (!NpmIntegrity.Matches(evidence.Integrity, payload))
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}': the downloaded tarball for {evidence.Package}@{evidence.Version} does NOT "
                + $"hash to the signed integrity '{evidence.Integrity}' — the bytes on the wire are not the "
                + "bytes npm signed. Refusing to install.");
        }

        if (declared == AdapterProvenanceLevel.NpmRegistrySignature)
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.RegistrySignatureVerified,
                $"'{adapterId}': npm registry signature verified under pinned key "
                + $"{NpmSigningKeys.CurrentKeyId} and the tarball matches the signed integrity. This proves "
                + "the bytes are the ones npmjs.org attested for this version; it does NOT prove the "
                + "publisher's build produced them (that package publishes no build provenance).");
        }

        // NpmBuildProvenance from here down.
        if (!evidence.AttestationPredicateTypes.Contains(SlsaProvenancePredicate, StringComparer.Ordinal))
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}' requires an npm BUILD PROVENANCE attestation ({SlsaProvenancePredicate}), "
                + $"but {evidence.Package}@{evidence.Version} publishes none "
                + $"(offered: {Join(evidence.AttestationPredicateTypes)}). A package that stops publishing "
                + "provenance is a change we must notice, not absorb — refusing to install.");
        }

        var expected = NpmIntegrity.ToHexDigest(evidence.Integrity);
        if (expected is null
            || !evidence.ProvenanceSubjectDigestsSha512.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}': the build-provenance attestation for {evidence.Package}@{evidence.Version} "
                + "does not name these bytes as its subject (no sha512 subject digest matched the signed "
                + "integrity) — an attestation for a different artifact proves nothing about this one. "
                + "Refusing to install.");
        }

        return new NpmProvenanceVerdict(NpmProvenanceOutcome.BuildProvenanceVerified,
            $"'{adapterId}': npm build-provenance attestation ({SlsaProvenancePredicate}) is published for "
            + $"{evidence.Package}@{evidence.Version} and its in-toto subject digest binds to these exact "
            + $"bytes; the registry signature also verified under pinned key {NpmSigningKeys.CurrentKeyId}. "
            + "NOTE: the Sigstore certificate chain and Rekor inclusion proof are NOT validated in-process "
            + "(see AdapterProvenanceLevel.NpmBuildProvenance) — `npm audit signatures` covers that in the "
            + "manual matrix.");
    }

    private static string Describe(AdapterProvenanceLevel level) => level switch
    {
        AdapterProvenanceLevel.NpmBuildProvenance => "an npm build-provenance attestation",
        AdapterProvenanceLevel.NpmRegistrySignature => "a pinned-key npm registry signature",
        _ => "no provenance",
    };

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);
}

/// <summary>
/// What <see cref="AgentCliUpdateService"/> actually asks before it moves a pin: "may these bytes be
/// installed as <c>package@version</c> for this adapter?".
///
/// <para>The seam is the WHOLE gate — fetch plus policy — rather than just the fetch, on purpose. If a
/// caller could inject the evidence but not the decision, then the pinned npm key would have to be
/// overridable from outside <see cref="NpmProvenancePolicy"/> for tests to drive a passing case, and a
/// pinned trust anchor that production code can swap is not pinned. This way
/// <see cref="NpmSigningKeys.Pinned"/> is reachable only from inside <see cref="NpmProvenanceGate"/>,
/// and the key-swapping tests go through the pure policy's own <c>keys</c> parameter.</para>
/// </summary>
public interface INpmProvenanceGate
{
    Task<NpmProvenanceVerdict> EvaluateAsync(
        string adapterId, AdapterProvenanceLevel declared, string package, string version,
        byte[] payload, CancellationToken ct);
}

/// <summary>
/// The production gate: fetch what the registry publishes about this exact version, then run the pure
/// <see cref="NpmProvenancePolicy"/> against it with the compiled-in npm keys.
///
/// <para><b>An unreachable registry is a REFUSAL, not a pass.</b> Elsewhere in the updater a network
/// failure degrades to the shipped pin, which is safe because that pin is a reviewed constant. Here the
/// bytes in question are about to BECOME a pin, so "could not check" must be spelled the same as "did
/// not verify" — otherwise dropping one metadata request is a cheaper bypass than forging a signature.
/// The exception text is kept so the log can tell an outage from a bad signature; the outcome is the
/// same either way.</para>
/// </summary>
public sealed class NpmProvenanceGate : INpmProvenanceGate
{
    private readonly INpmProvenanceSource _source;

    public NpmProvenanceGate(INpmProvenanceSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<NpmProvenanceVerdict> EvaluateAsync(
        string adapterId, AdapterProvenanceLevel declared, string package, string version,
        byte[] payload, CancellationToken ct)
    {
        NpmProvenanceEvidence evidence;
        try
        {
            evidence = await _source.FetchAsync(package, version, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (declared == AdapterProvenanceLevel.None)
            {
                // Nothing was going to be checked anyway; say it with the same words as always.
                return NpmProvenancePolicy.Decide(adapterId, declared, null, payload);
            }

            return new NpmProvenanceVerdict(NpmProvenanceOutcome.Refused,
                $"'{adapterId}': could not obtain npm provenance metadata for {package}@{version} "
                + $"({ex.Message}). Unverifiable is not verified — refusing to move the pin.");
        }

        return NpmProvenancePolicy.Decide(adapterId, declared, evidence, payload);
    }
}

/// <summary>
/// Where provenance evidence comes from. A seam because the real source is two HTTPS round-trips to
/// registry.npmjs.org, and the policy above it must be testable without either.
/// </summary>
public interface INpmProvenanceSource
{
    /// <summary>Evidence for one exact <c>package@version</c>. Returning
    /// <see cref="NpmProvenanceEvidence.Empty"/> (rather than throwing) is the right answer for a package
    /// that simply publishes no attestations; throw only when the registry could not be reached at all —
    /// callers translate a throw into a refusal, never into a pass.</summary>
    Task<NpmProvenanceEvidence> FetchAsync(string package, string version, CancellationToken ct);
}

/// <summary>
/// The real registry source: <c>/{pkg}/{version}</c> for the signed <c>dist</c> block, and
/// <c>/-/npm/v1/attestations/{pkg}@{version}</c> for the published attestations (404 = "publishes none",
/// which the policy turns into a refusal only for adapters that require them).
/// <para>The parsing is separated into static pure methods so the JSON shapes are unit-tested against
/// captured real responses without any network.</para>
/// </summary>
public sealed class HttpNpmProvenanceSource : INpmProvenanceSource
{
    private readonly HttpClient _http;

    public HttpNpmProvenanceSource(HttpClient http) =>
        _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<NpmProvenanceEvidence> FetchAsync(string package, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var metadata = await _http.GetStringAsync(
            new Uri($"https://registry.npmjs.org/{Escape(package)}/{Uri.EscapeDataString(version)}"), ct)
            .ConfigureAwait(false);

        string? attestations = null;
        using (var response = await _http.GetAsync(
                   new Uri($"https://registry.npmjs.org/-/npm/v1/attestations/{Escape(package)}@{Uri.EscapeDataString(version)}"),
                   ct).ConfigureAwait(false))
        {
            // 404 is the registry's way of saying "this version publishes no attestations". That is
            // evidence, not an error — the policy decides whether the adapter can live without them.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                attestations = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        return Parse(package, version, metadata, attestations);
    }

    /// <summary>npm scopes contain a <c>/</c> that must survive as <c>%2f</c> in a single path segment.</summary>
    private static string Escape(string package) => Uri.EscapeDataString(package);

    /// <summary>Builds evidence from the two raw responses. Pure — malformed JSON yields EMPTY evidence
    /// (which every non-<c>none</c> rung refuses), never a partial "looks fine" result.</summary>
    public static NpmProvenanceEvidence Parse(
        string package, string version, string? versionMetadataJson, string? attestationsJson)
    {
        var integrity = (string?)null;
        var signatures = new List<NpmRegistrySignature>();

        if (!string.IsNullOrWhiteSpace(versionMetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(versionMetadataJson);
                if (doc.RootElement.TryGetProperty("dist", out var dist))
                {
                    if (dist.TryGetProperty("integrity", out var i) && i.ValueKind == JsonValueKind.String)
                        integrity = i.GetString();
                    if (dist.TryGetProperty("signatures", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in sigs.EnumerateArray())
                        {
                            var keyid = entry.TryGetProperty("keyid", out var k) ? k.GetString() : null;
                            var sig = entry.TryGetProperty("sig", out var s) ? s.GetString() : null;
                            if (keyid is { Length: > 0 } && sig is { Length: > 0 })
                                signatures.Add(new NpmRegistrySignature(keyid, sig));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return NpmProvenanceEvidence.Empty(package, version);
            }
        }

        var predicates = new List<string>();
        var subjects = new List<string>();
        if (!string.IsNullOrWhiteSpace(attestationsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(attestationsJson);
                if (doc.RootElement.TryGetProperty("attestations", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attestation in list.EnumerateArray())
                    {
                        var predicate = attestation.TryGetProperty("predicateType", out var p)
                            ? p.GetString() : null;
                        if (predicate is not { Length: > 0 })
                            continue;
                        predicates.Add(predicate);

                        if (!string.Equals(predicate, NpmProvenancePolicy.SlsaProvenancePredicate, StringComparison.Ordinal))
                            continue;

                        // Only the SLSA statement's own subject digests count as a build binding — the
                        // registry's publish attestation carries the same subject but claims nothing
                        // about who built the artifact.
                        foreach (var digest in ReadSubjectSha512(attestation))
                            subjects.Add(digest);
                    }
                }
            }
            catch (JsonException)
            {
                return new NpmProvenanceEvidence(
                    package, version, integrity, signatures, Array.Empty<string>(), Array.Empty<string>());
            }
        }

        return new NpmProvenanceEvidence(package, version, integrity, signatures, predicates, subjects);
    }

    /// <summary>The in-toto statement lives base64 inside the DSSE envelope; its <c>subject[].digest.sha512</c>
    /// is what binds the attestation to concrete bytes.</summary>
    private static IEnumerable<string> ReadSubjectSha512(JsonElement attestation)
    {
        if (!attestation.TryGetProperty("bundle", out var bundle)
            || !bundle.TryGetProperty("dsseEnvelope", out var envelope)
            || !envelope.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        byte[] statementBytes;
        try
        {
            statementBytes = Convert.FromBase64String(payload.GetString()!);
        }
        catch (FormatException)
        {
            yield break;
        }

        JsonDocument statement;
        try
        {
            statement = JsonDocument.Parse(statementBytes);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (statement)
        {
            if (!statement.RootElement.TryGetProperty("subject", out var subjects)
                || subjects.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var subject in subjects.EnumerateArray())
            {
                if (subject.TryGetProperty("digest", out var digest)
                    && digest.TryGetProperty("sha512", out var sha512)
                    && sha512.ValueKind == JsonValueKind.String
                    && sha512.GetString() is { Length: > 0 } value)
                {
                    yield return value.ToLowerInvariant();
                }
            }
        }
    }
}
