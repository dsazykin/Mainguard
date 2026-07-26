using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>The artifacts Mainguard BUILDS ITSELF and can therefore attest at build time. Separate from
/// <see cref="SignedArtifactKind"/> on purpose: that enum spans third-party payloads too, and this one
/// must only ever name things a Mainguard CI run produces.</summary>
public enum BuildArtifactKind
{
    /// <summary>The <c>mainguardd</c> build promoted over <c>/opt/mainguard</c> and restarted as root.
    /// A directory, so what CI attests is the payload's canonical sha256 <b>manifest file</b> and the
    /// app requires the directory on disk to reproduce it exactly.</summary>
    DaemonPayload,

    /// <summary>The <c>MainguardOS.tar.gz</c> rootfs imported as the MainguardEnv WSL distro.</summary>
    MainguardOsTarball,
}

/// <summary>What a build-provenance check concluded.</summary>
public enum BuildProvenanceOutcome
{
    /// <summary>An attestation was verified against the expected source repository and this artifact's
    /// digest.</summary>
    Verified,

    /// <summary>This build is required to carry provenance for this artifact and it did not verify —
    /// missing, unreadable, for a different repository, or for different bytes. The caller MUST refuse.</summary>
    Refused,

    /// <summary>This build was not produced by an attesting release run (a developer build, a source
    /// checkout, a CI job that does not attest), so there is nothing to check. The caller proceeds with
    /// the gap named. Never returned for an attested build — see <see cref="BuildProvenanceStamp"/>.</summary>
    NotAttestedBuild,
}

/// <summary>One build-provenance answer plus the sentence explaining it.</summary>
public sealed record BuildProvenanceVerdict(BuildProvenanceOutcome Outcome, string Reason)
{
    public bool MustRefuse => Outcome == BuildProvenanceOutcome.Refused;
}

/// <summary>The raw result of asking a verifier about one artifact — deliberately dumb data so the
/// policy that interprets it stays pure.</summary>
/// <param name="Verified">The verifier ran to completion and the attestation held.</param>
/// <param name="Detail">Verifier output/diagnostic, carried into the verdict for the log.</param>
/// <param name="SourceRepository">The <c>sourceRepositoryURI</c> the attestation certificate carried.</param>
/// <param name="SubjectDigestsSha256">Lowercase-hex sha256 subject digests the statement named.</param>
public sealed record BuildAttestationCheck(
    bool Verified,
    string Detail,
    string? SourceRepository = null,
    IReadOnlyList<string>? SubjectDigestsSha256 = null);

/// <summary>
/// The seam behind <c>gh attestation verify</c>. It exists because that command needs a network round
/// trip to GitHub (or an on-disk Sigstore bundle plus a trusted root), which cannot run in a unit test —
/// so the process invocation lives on one side of this interface and every policy decision on the other.
/// </summary>
public interface IBuildAttestationVerifier
{
    /// <summary>Verifies the attestation covering <paramref name="artifactPath"/>.</summary>
    /// <param name="bundlePath">An on-disk Sigstore bundle for offline verification, or null to let the
    /// verifier fetch from GitHub.</param>
    Task<BuildAttestationCheck> VerifyAsync(
        string artifactPath, string expectedRepository, string? bundlePath, CancellationToken ct);
}

/// <summary>
/// Whether the running build claims to be an <b>attested release</b> — the single input that decides
/// whether build provenance is REQUIRED or merely absent.
///
/// <para><b>Why this is compiled in and not read from a file.</b> A verifier whose requirement lives
/// beside the artifact is not a verifier: an attacker who replaces the daemon payload deletes the
/// attestation next to it and the check politely skips. Here the requirement is an assembly-level
/// attribute baked into <c>Mainguard.Agents.dll</c> at build time, set only by the release workflow
/// (<c>-p:MainguardAttestedRelease=true</c>). Deleting the attestation from a release install therefore
/// does not disable the check — it fails it. Rewriting the stamp means rewriting a signed-and-relocated
/// app assembly, which is the threat step 1 and step 3 of the code-signing plan address.</para>
///
/// <para>Developer and source builds carry no stamp, so provenance is <see cref="BuildProvenanceOutcome.NotAttestedBuild"/>
/// there: `dotnet run` from a checkout must keep working, and pretending a local build was attested
/// would be the same lie the unsigned-verifier seam refuses to tell.</para>
/// </summary>
public static class BuildProvenanceStamp
{
    /// <summary>The <c>AssemblyMetadata</c> key the release build stamps.</summary>
    public const string MetadataKey = "MainguardAttestedRelease";

    /// <summary>The GitHub repository whose CI is the only acceptable attestation signer. Compiled in
    /// for the same reason as the requirement itself.</summary>
    public const string SourceRepository = "dsazykin/Mainguard";

    private static readonly bool Stamped = ReadStamp(typeof(BuildProvenanceStamp).Assembly);

    /// <summary>True only on a build produced by the attesting release workflow.</summary>
    public static bool IsAttestedRelease => Stamped;

    /// <summary>Reads the stamp off an arbitrary assembly. Public so the policy tests can exercise both
    /// answers without needing two differently-built assemblies.</summary>
    public static bool ReadStamp(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal)
                && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <b>The MG-9 decision function for artifacts we build ourselves.</b> Pure and total: it takes "is this
/// an attested release build", the expected repository and digest, and whatever the verifier reported,
/// and returns the verdict. No process launching, no file system, no network — so every branch runs on
/// Linux CI while the <c>gh</c> invocation itself stays in the manual matrix.
///
/// <para><b>Fail-closed.</b> On an attested build, every reachable failure is
/// <see cref="BuildProvenanceOutcome.Refused"/>: no attestation, an attestation from another repository,
/// an attestation naming a different digest, or a verifier that could not run. "Could not check" and
/// "checked and bad" produce the same refusal on purpose — the alternative rewards an attacker for
/// breaking the verifier instead of forging a signature.</para>
/// </summary>
public static class BuildProvenancePolicy
{
    public static BuildProvenanceVerdict Decide(
        BuildArtifactKind kind,
        bool isAttestedRelease,
        string expectedRepository,
        string expectedDigestSha256,
        BuildAttestationCheck? check)
    {
        if (!isAttestedRelease)
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.NotAttestedBuild,
                $"{kind}: build provenance was NOT checked — this build carries no "
                + $"'{BuildProvenanceStamp.MetadataKey}' stamp, so it did not come from the attesting "
                + "release workflow and no attestation is expected to exist for it.");
        }

        if (check is null)
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                $"{kind}: this is an attested release build, but no build-provenance attestation could be "
                + "obtained at all. Refusing — an artifact that is supposed to carry provenance and does "
                + "not is exactly the case this check exists for.");
        }

        if (!check.Verified)
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                $"{kind}: build-provenance verification failed — {check.Detail}. Refusing.");
        }

        if (!string.IsNullOrEmpty(expectedRepository)
            && !RepositoryMatches(check.SourceRepository, expectedRepository))
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                $"{kind}: the attestation verified, but it was signed for source repository "
                + $"'{check.SourceRepository ?? "(none reported)"}' rather than '{expectedRepository}'. A "
                + "valid attestation from somebody else's repository is not provenance for OUR artifact. "
                + "Refusing.");
        }

        var digests = check.SubjectDigestsSha256 ?? Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(expectedDigestSha256)
            || !digests.Contains(expectedDigestSha256, StringComparer.OrdinalIgnoreCase))
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                $"{kind}: the attestation verified, but its subject digests "
                + $"({(digests.Count == 0 ? "none" : string.Join(", ", digests))}) do not include the "
                + $"sha256 of the artifact on disk ({Describe(expectedDigestSha256)}). An attestation for "
                + "different bytes proves nothing about these. Refusing.");
        }

        return new BuildProvenanceVerdict(BuildProvenanceOutcome.Verified,
            $"{kind}: build provenance verified — attested by {expectedRepository}'s CI and bound to this "
            + $"artifact's sha256 ({Describe(expectedDigestSha256)}).");
    }

    /// <summary>The certificate carries a URI (<c>https://github.com/owner/repo</c>); the pin is
    /// <c>owner/repo</c>. Compare on the suffix, case-insensitively, and require a path boundary so
    /// <c>evil/Mainguard</c> cannot satisfy a pin of <c>dsazykin/Mainguard</c>.</summary>
    private static bool RepositoryMatches(string? reported, string expected)
    {
        if (string.IsNullOrWhiteSpace(reported))
            return false;
        var value = reported.Trim().TrimEnd('/');
        return value.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/" + expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(string? digest) =>
        string.IsNullOrWhiteSpace(digest) ? "(none)" : digest.Length <= 16 ? digest : digest[..16] + "…";
}

/// <summary>
/// The real verifier: shells out to <c>gh attestation verify &lt;artifact&gt; --repo &lt;owner/repo&gt;
/// --format json</c> and parses the result.
///
/// <para>The process launch is injected as a delegate so the <b>parsing</b> — which is where every
/// interesting mistake lives — is unit-tested against captured <c>gh</c> output with no <c>gh</c>, no
/// network, and no GitHub. The launch itself is manual-matrix only.</para>
/// </summary>
public sealed class GhCliBuildAttestationVerifier : IBuildAttestationVerifier
{
    /// <summary>argv → (exit code, stdout, stderr).</summary>
    public delegate Task<(int ExitCode, string StdOut, string StdErr)> ProcessRunner(
        IReadOnlyList<string> argv, CancellationToken ct);

    private readonly ProcessRunner _run;

    public GhCliBuildAttestationVerifier(ProcessRunner? run = null) => _run = run ?? RunGhAsync;

    /// <summary>The exact argv. Built here (not by the caller) so the enforced predicate type and the
    /// <c>--repo</c> pin cannot be loosened at a call site.</summary>
    public static IReadOnlyList<string> BuildArgv(string artifactPath, string repository, string? bundlePath)
    {
        var argv = new List<string>
        {
            "gh", "attestation", "verify", artifactPath,
            "--repo", repository,
            // gh enforces https://slsa.dev/provenance/v1 by default; naming it keeps the guarantee
            // explicit and immune to a future change of gh's default.
            "--predicate-type", "https://slsa.dev/provenance/v1",
            "--format", "json",
        };

        if (!string.IsNullOrWhiteSpace(bundlePath))
        {
            argv.Add("--bundle");
            argv.Add(bundlePath!);
        }

        return argv;
    }

    public async Task<BuildAttestationCheck> VerifyAsync(
        string artifactPath, string expectedRepository, string? bundlePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepository);

        try
        {
            var (exit, stdout, stderr) = await _run(
                BuildArgv(artifactPath, expectedRepository, bundlePath), ct).ConfigureAwait(false);
            return ParseResult(exit, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A verifier that cannot run has NOT approved. Returning Verified:false here is what makes
            // "gh is not installed" a refusal on an attested build rather than a silent skip.
            return new BuildAttestationCheck(false, $"the gh attestation verifier could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses <c>gh attestation verify --format json</c>: a JSON array whose entries carry
    /// <c>verificationResult.signature.certificate.sourceRepositoryURI</c> and
    /// <c>verificationResult.statement.subject[].digest.sha256</c>. Pure.
    /// <para>A non-zero exit is a failure regardless of what was printed, and unparseable output on a
    /// zero exit is ALSO a failure — a verifier whose result we cannot read has not told us it passed.</para>
    /// </summary>
    public static BuildAttestationCheck ParseResult(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return new BuildAttestationCheck(false,
                $"gh attestation verify exited {exitCode}: {(detail ?? string.Empty).Trim()}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
            return new BuildAttestationCheck(false, "gh attestation verify exited 0 but printed nothing.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            return new BuildAttestationCheck(false,
                $"gh attestation verify output could not be parsed: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return new BuildAttestationCheck(false,
                    "gh attestation verify returned no verified attestations.");
            }

            string? repository = null;
            var digests = new List<string>();

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("verificationResult", out var result))
                    continue;

                if (repository is null
                    && result.TryGetProperty("signature", out var signature)
                    && signature.TryGetProperty("certificate", out var certificate)
                    && certificate.TryGetProperty("sourceRepositoryURI", out var uri)
                    && uri.ValueKind == JsonValueKind.String)
                {
                    repository = uri.GetString();
                }

                if (result.TryGetProperty("statement", out var statement)
                    && statement.TryGetProperty("subject", out var subjects)
                    && subjects.ValueKind == JsonValueKind.Array)
                {
                    foreach (var subject in subjects.EnumerateArray())
                    {
                        if (subject.TryGetProperty("digest", out var digest)
                            && digest.TryGetProperty("sha256", out var sha256)
                            && sha256.ValueKind == JsonValueKind.String
                            && sha256.GetString() is { Length: > 0 } value)
                        {
                            digests.Add(value.ToLowerInvariant());
                        }
                    }
                }
            }

            return new BuildAttestationCheck(true, "gh attestation verify succeeded.", repository, digests);
        }
    }

    private static async Task<(int, string, string)> RunGhAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var info = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in argv.Skip(1))
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"could not start '{argv[0]}'");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

/// <summary>
/// The one place the app asks "may I promote these bytes?" for an artifact Mainguard built. Combines the
/// compiled-in requirement, the artifact's on-disk digest, and the verifier seam, and hands back a
/// verdict the caller refuses on.
///
/// <para>Constructed with a null verifier on a non-attested build so a developer run never pays for a
/// process launch it is going to ignore.</para>
/// </summary>
public sealed class BuildProvenanceGate
{
    private readonly IBuildAttestationVerifier? _verifier;
    private readonly bool _isAttestedRelease;
    private readonly string _repository;

    /// <param name="verifier">Null → the real <c>gh</c>-backed verifier (only ever constructed when it
    /// is actually going to be used).</param>
    /// <param name="isAttestedRelease">Null → the compiled-in stamp. Tests pass it explicitly.</param>
    public BuildProvenanceGate(
        IBuildAttestationVerifier? verifier = null,
        bool? isAttestedRelease = null,
        string? repository = null)
    {
        _isAttestedRelease = isAttestedRelease ?? BuildProvenanceStamp.IsAttestedRelease;
        _repository = repository ?? BuildProvenanceStamp.SourceRepository;
        _verifier = verifier ?? (_isAttestedRelease ? new GhCliBuildAttestationVerifier() : null);
    }

    /// <summary>The sidecar an attesting release ships next to an artifact: the downloaded Sigstore
    /// bundle, so verification works on a machine with no GitHub reachability. Its ABSENCE is a refusal
    /// on an attested build (gh is still asked to fetch, and if that also fails the gate refuses) —
    /// never a skip.</summary>
    public static string BundlePathFor(string artifactPath) => artifactPath + ".sigstore.jsonl";

    /// <summary>Verifies the file at <paramref name="artifactPath"/>. A missing/unreadable file is a
    /// refusal on an attested build.</summary>
    public async Task<BuildProvenanceVerdict> VerifyFileAsync(
        BuildArtifactKind kind, string artifactPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        if (!_isAttestedRelease || _verifier is null)
            return BuildProvenancePolicy.Decide(kind, false, _repository, string.Empty, null);

        string digest;
        try
        {
            await using var stream = File.OpenRead(artifactPath);
            digest = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)
                .ConfigureAwait(false)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BuildProvenanceVerdict(BuildProvenanceOutcome.Refused,
                $"{kind}: could not read '{artifactPath}' to compute its digest ({ex.Message}); an "
                + "artifact we cannot hash is an artifact we cannot verify. Refusing.");
        }

        var bundle = BundlePathFor(artifactPath);
        var check = await _verifier.VerifyAsync(
            artifactPath, _repository, File.Exists(bundle) ? bundle : null, ct).ConfigureAwait(false);

        return BuildProvenancePolicy.Decide(kind, true, _repository, digest, check);
    }

    /// <summary>Verifies an artifact whose digest the caller already computed — the daemon payload,
    /// which is a directory and is attested through its canonical manifest file.</summary>
    public async Task<BuildProvenanceVerdict> VerifyDigestAsync(
        BuildArtifactKind kind, string attestedFilePath, string digestSha256, CancellationToken ct)
    {
        if (!_isAttestedRelease || _verifier is null)
            return BuildProvenancePolicy.Decide(kind, false, _repository, string.Empty, null);

        var bundle = BundlePathFor(attestedFilePath);
        var check = await _verifier.VerifyAsync(
            attestedFilePath, _repository, File.Exists(bundle) ? bundle : null, ct).ConfigureAwait(false);

        return BuildProvenancePolicy.Decide(kind, true, _repository, digestSha256, check);
    }
}
