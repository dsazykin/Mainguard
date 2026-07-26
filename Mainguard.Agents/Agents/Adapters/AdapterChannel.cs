using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

using Mainguard.Git;
namespace Mainguard.Agents.Agents.Adapters;

/// <summary>Why an adapter install was refused/failed. Typed so callers react, never a bare throw.</summary>
public enum AdapterChannelError
{
    /// <summary>The manifest names no adapter with the requested id.</summary>
    UnknownAdapter,
    /// <summary>The fetched payload's SHA-256 did not match the manifest pin — install refused.</summary>
    HashMismatch,
    /// <summary>The in-VM install command exited non-zero.</summary>
    InstallFailed,
    /// <summary>The health probe exited non-zero (the CLI is not runnable).</summary>
    ProbeFailed,
    /// <summary>The health probe ran but did not report the pinned version (wrong version installed).</summary>
    VersionMismatch,

    /// <summary>The requested update did not move the pin strictly FORWARD (MG-14) — it was older than,
    /// equal to, or not orderable against the version currently pinned, so it was refused rather than
    /// applied. A distinct code because callers must not present it as an install failure: nothing
    /// broke, an unsafe or pointless move was declined.</summary>
    UpdateRefused,

    /// <summary>A configured <see cref="IPayloadSignatureVerifier"/> actively REJECTED the payload's
    /// signature. Never produced by the default verifier, which has no signing identity to check
    /// against and answers <see cref="SignatureVerdictKind.NotAvailable"/> instead.</summary>
    SignatureRejected,

    /// <summary>MG-9: the adapter's declared <see cref="AdapterProvenanceLevel"/> could not be met — no
    /// registry signature under a pinned npm key, bytes that do not hash to the signed integrity, or a
    /// missing/mis-bound build-provenance attestation. Distinct from
    /// <see cref="HashMismatch"/> (which only ever compares against a pin we ourselves wrote) because
    /// this is the check that establishes ORIGIN rather than sameness.</summary>
    ProvenanceRejected,
}

/// <summary>The typed refusal/failure of an adapter operation.</summary>
public sealed class AdapterChannelException : Exception
{
    public AdapterChannelError Error { get; }

    public AdapterChannelException(AdapterChannelError error, string message)
        : base(message) => Error = error;
}

/// <summary>Outcome of <see cref="AdapterChannel.EnsureAsync"/>.</summary>
public enum AdapterEnsureResult
{
    /// <summary>The pinned version was already installed and healthy — nothing was done (idempotent).</summary>
    AlreadyHealthy,
    /// <summary>The pinned version was fetched, verified, installed, shimmed, and probed green.</summary>
    Installed,
}

/// <summary>The Mainguard-owned channel: serves the manifest and the hash-pinned adapter payloads over
/// HTTPS. Behind an interface so the pin-survival simulation drives it with a fixture registry.</summary>
public interface IAdapterChannelSource
{
    Task<string> FetchManifestAsync(CancellationToken ct);
    Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct);

    /// <summary>
    /// True when this source IS the local shipped truth (the bundled starter manifest): its
    /// manifest must then outrank whatever an older app version cached, or an app update's new pins
    /// would never take effect (audit fix #7 — the cache-shadowing bug). A remote/hosted channel
    /// keeps the default <c>false</c>: there the cache is deliberate (explicit refresh only, so a
    /// breaking upstream is never auto-adopted).
    /// </summary>
    bool IsAuthoritativeLocal => false;
}

/// <summary>Result of one in-VM command.</summary>
public sealed record AdapterCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs adapter install/probe commands INSIDE the VM (never on the host). The real impl wraps
/// the P2-05 <see cref="IWslRunner"/> (<c>wsl -d MainguardEnv --</c>); the fake drives the logic tests.</summary>
public interface IAdapterInstallHost
{
    Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct);
    Task WriteFileAsync(string path, string content, CancellationToken ct);

    /// <summary>Materialises the hash-VERIFIED payload bytes as a file inside the VM and returns its
    /// in-VM path — what an <c>installCmd</c>'s <c>{payload}</c> placeholder expands to. Installing
    /// from the staged file is what makes the sha256 pin real: an install command that re-downloads
    /// from a registry would install bytes the pin never covered.</summary>
    Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct);
}

/// <summary>
/// The fixed in-VM layout for dynamically installed agent CLIs. One shared npm-style prefix so every
/// adapter's entry point lands in ONE bin dir; the sandbox engine bind-mounts <see cref="VmRoot"/>
/// READ-ONLY at <see cref="SandboxMount"/> (agents can run the CLIs but never tamper with the shared
/// binaries), and the agent-base image carries <c>/opt/mainguard/adapters/bin</c> on PATH permanently —
/// so a CLI installed AFTER provisioning reaches every new sandbox with no image rebuild.
/// </summary>
public static class AdapterPaths
{
    /// <summary>The VM-side adapters root (the npm <c>--prefix</c>): bins in <c>bin/</c>, staged
    /// payloads in <c>stage/</c>, install markers in <c>registry/</c>. Under the fixed VM user's home
    /// (the tarball's <c>/etc/wsl.conf</c> pins <c>default=mainguard</c>).</summary>
    public const string VmRoot = "/home/mainguard/mainguard/adapters";

    /// <summary>Where <see cref="VmRoot"/> appears inside every agent sandbox (read-only).</summary>
    public const string SandboxMount = "/opt/mainguard/adapters";

    /// <summary>Staging dir for hash-verified payload files awaiting install.</summary>
    public const string VmStageDir = VmRoot + "/stage";

    /// <summary>One JSON marker per installed adapter (<c>registry/&lt;id&gt;.json</c>) recording
    /// id/version/launch — the artifact the daemon's <see cref="InstalledAdapterCatalog"/> reads to
    /// map <c>agentKind</c> → the CLI argv. Written LAST, only after a green health probe.</summary>
    public const string VmRegistryDir = VmRoot + "/registry";

    public static string RegistryMarkerPath(string adapterId) => $"{VmRegistryDir}/{adapterId}.json";
}

/// <summary>Persists the last-fetched manifest under appdata so installs work offline and refresh is
/// independent of app releases.</summary>
public interface IAdapterManifestCache
{
    string? Read();
    void Write(string manifestJson);
}

/// <summary>
/// The pinned adapter channel (P2-22 §J-5). Installs agent CLIs INSIDE the VM at the exact version the
/// manifest pins — never <c>@latest</c> — verifying a content-hash before install and a version-matched
/// health probe after. Because the install command and probe both carry the pinned version, a breaking
/// upstream release cannot change what is installed: pin survival is structural, and the simulation test
/// proves it. Refresh (re-fetching the manifest) is a separate, explicit call so app updates and adapter
/// updates move independently (perpetual-fallback licenses keep working — market v2 §5.3).
/// </summary>
public sealed class AdapterChannel
{
    private readonly IAdapterChannelSource _source;
    private readonly IAdapterInstallHost _host;
    private readonly IAdapterManifestCache _cache;
    private readonly IAdapterPinOverrideStore? _pins;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Backoff before each install retry. The failure this exists for is a cold VM whose networking has
    /// not settled: the CLI installs run seconds after MainguardEnv's FIRST boot, while WSL's NAT and
    /// dockerd's iptables are still coming up, so npm's own fetch-retries all expire inside one dead
    /// window and the whole install exits ETIMEDOUT. Waiting out the window is the fix; these delays span
    /// ~14s, comfortably longer than the races observed, and only ever elapse on a failure path.
    /// </summary>
    private static readonly TimeSpan[] InstallRetryBackoff =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    };

    /// <param name="pins">User-applied pin overrides (accepted CLI updates / reverts). Null = the
    /// manifest's pins always apply verbatim (OOBE flows and every pre-update caller).</param>
    public AdapterChannel(
        IAdapterChannelSource source,
        IAdapterInstallHost host,
        IAdapterManifestCache cache,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IAdapterPinOverrideStore? pins = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _pins = pins;
        // Injected so the retry tests assert the backoff without ever sleeping.
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The spec that actually governs installs: the manifest pin, unless the user accepted
    /// an update — then their override (a concrete version + sha256 of the exact accepted bytes)
    /// replaces version/payload/hash/probe-substring together.</summary>
    public AdapterSpec EffectiveSpec(AdapterSpec spec)
        => _pins?.TryGet(spec.Id) is { } pin ? pin.Apply(spec) : spec;

    /// <summary>
    /// Whether a failed in-VM install looks like a transient network fault worth retrying, as opposed to
    /// a real failure (a bad tarball, a broken postinstall, no disk). Deliberately matches on the
    /// network error CODES npm/node emit rather than prose, so it neither depends on npm's wording nor
    /// fires on an unrelated failure that merely mentions the word "network". A misjudged transient only
    /// costs the backoff above and one more attempt; a misjudged permanent fails exactly as it does now.
    /// </summary>
    internal static bool IsTransientInstallFailure(AdapterCommandResult result)
    {
        string[] codes =
        {
            "ETIMEDOUT",     // the observed first-boot failure
            "ECONNRESET",
            "ECONNREFUSED",
            "ENOTFOUND",     // DNS not up yet
            "EAI_AGAIN",     // resolver not up yet
            "ENETUNREACH",
            "EHOSTUNREACH",
            "ERR_SOCKET_TIMEOUT",
            "socket hang up",
        };

        var text = result.Stderr + "\n" + result.Stdout;
        return codes.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a failed HOST-side payload fetch is a transient network fault. This is a SEPARATE network
    /// path from the in-VM install (<see cref="IsTransientInstallFailure"/>) and fails differently: the
    /// host pulls the pinned tarball over HTTPS, so a cold network surfaces as an HttpRequestException
    /// ("An error occurred while sending the request") rather than an npm error code. On a real setup
    /// this is how `codex` failed while the other two died in-VM — one outage, two distinct failure
    /// modes, so retrying only the install would still have left this one broken.
    /// <para>A caller-requested cancellation is NEVER transient: it must propagate immediately.</para>
    /// </summary>
    internal static bool IsTransientFetchFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        return ex is HttpRequestException     // connection refused/reset, DNS, TLS-level transport failure
            || ex is TaskCanceledException    // HttpClient's own timeout surfaces here (ct is NOT cancelled)
            || ex is TimeoutException
            || ex is IOException;             // socket torn down mid-body
    }

    /// <summary>Fetches the pinned payload, retrying transient network faults on the same backoff as the
    /// install. The hash pin is verified after this returns, so a retry can never widen what gets
    /// installed — every attempt must still match the manifest's sha256.</summary>
    private async Task<byte[]> FetchPayloadWithRetryAsync(AdapterSpec spec, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _source.FetchPayloadAsync(spec, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < InstallRetryBackoff.Length && IsTransientFetchFailure(ex, ct))
            {
                await _delay(InstallRetryBackoff[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Fetches and caches a fresh manifest from the channel (explicit — never implicit on
    /// install, so a breaking upstream is never auto-adopted). Returns the validated manifest.</summary>
    public async Task<AdapterManifest> RefreshAsync(CancellationToken ct = default)
    {
        var json = await _source.FetchManifestAsync(ct).ConfigureAwait(false);
        var manifest = AdapterManifest.Parse(json); // validates before we ever cache/act on it
        _cache.Write(json);
        return manifest;
    }

    /// <summary>
    /// Loads the manifest: a local-authoritative source (the bundled starter channel) is always
    /// re-read — it is the shipped truth and reading it is free, so an app update's new pins take
    /// effect immediately instead of being shadowed forever by an older version's cache (audit fix
    /// #7). A remote source stays cache-first with a single refresh on an empty cache, preserving
    /// the explicit-refresh design (a breaking upstream is never auto-adopted).
    /// </summary>
    public async Task<AdapterManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        if (_source.IsAuthoritativeLocal)
        {
            return await RefreshAsync(ct).ConfigureAwait(false);
        }

        var cached = _cache.Read();
        return cached is not null ? AdapterManifest.Parse(cached) : await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the pinned adapter <paramref name="adapterId"/> is installed and healthy in the VM. If
    /// the probe already reports the pinned version it is a no-op. Otherwise: fetch payload → verify
    /// SHA-256 against the pin (refuse on mismatch) → run install in VM → write config shims → probe;
    /// the probe must exit 0 AND report the pinned version substring.
    /// </summary>
    public async Task<AdapterEnsureResult> EnsureAsync(string adapterId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(ct).ConfigureAwait(false);
        var spec = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal))
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter, $"No adapter '{adapterId}' in the channel manifest.");
        spec = EffectiveSpec(spec); // a user-accepted update moves the pin; the discipline is unchanged

        // Idempotent: a green probe at the pinned version means nothing to do.
        var pre = await _host.RunAsync(spec.HealthProbe!.Command, ct).ConfigureAwait(false);
        if (pre.Succeeded && pre.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, StringComparison.Ordinal))
            return AdapterEnsureResult.AlreadyHealthy;

        // Fetch the pinned payload and verify the content hash BEFORE running anything. Retried on
        // transient network faults (see FetchPayloadWithRetryAsync) — the hash check below is unchanged
        // and still gates every byte, so retrying weakens nothing.
        var payload = await FetchPayloadWithRetryAsync(spec, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(spec.Sha256.ToLowerInvariant())))
        {
            throw new AdapterChannelException(AdapterChannelError.HashMismatch,
                $"Adapter '{adapterId}' payload hash did not match the pinned sha256; install refused.");
        }

        // The hash above answers "are these the bytes the pin covers?" — never "did these bytes come
        // from someone we trust?". For a pin the USER's own accepted update wrote, the answer to the
        // second question is circular: the hash was computed from whatever the registry served. This is
        // the single seam where provenance would be established. What this does with each verdict:
        //   Rejected     → refuse, before anything is staged into the VM.
        //   NotAvailable → proceed, with the reason carried in the exception/log text. Expected here on
        //                  EVERY build, signed or not: an npm tarball is a third-party artifact we do
        //                  not sign, so SigningPolicy.Covers excludes this kind and the reason names
        //                  what does cover it (npm provenance attestations, plan step 2).
        //   Verified     → proceed; not reachable today for this kind, by that same exclusion.
        var signature = PayloadSignature.VerifyBytes(
            SignedArtifactKind.AdapterPayload, $"{spec.Id}@{spec.Version}", payload);
        if (signature.MustRefuse)
        {
            throw new AdapterChannelException(AdapterChannelError.SignatureRejected,
                $"Adapter '{adapterId}' payload failed its signature check; install refused: {signature.Reason}");
        }

        // Stage the VERIFIED bytes into the VM and expand {payload} in the install command — the
        // install must consume the exact file the pin covered, never re-download from a registry.
        var installCmd = spec.InstallCmd;
        if (installCmd.Any(t => t.Contains(PayloadToken, StringComparison.Ordinal)))
        {
            var stagedPath = await _host.StagePayloadAsync(
                StagedFileName(spec), payload, ct).ConfigureAwait(false);
            installCmd = installCmd
                .Select(t => t.Replace(PayloadToken, stagedPath, StringComparison.Ordinal))
                .ToArray();
        }

        // Install INSIDE the VM at the pinned version (installCmd carries the pin — never @latest).
        //
        // Retried on TRANSIENT network faults only. Installing a staged tarball still resolves that
        // package's DEPENDENCIES from the registry, so the install needs working egress — and it runs
        // moments after the VM's first boot, when egress often is not up yet. That raced and failed every
        // CLI on a real setup (all three exited ETIMEDOUT); the pins make retrying safe, since every
        // attempt installs the same hash-verified bytes at the same pinned version. A permanent failure
        // still throws on the first attempt — we never burn the backoff on a genuinely broken install.
        var install = await _host.RunAsync(installCmd, ct).ConfigureAwait(false);
        for (var attempt = 0; !install.Succeeded && IsTransientInstallFailure(install)
                              && attempt < InstallRetryBackoff.Length; attempt++)
        {
            await _delay(InstallRetryBackoff[attempt], ct).ConfigureAwait(false);
            install = await _host.RunAsync(installCmd, ct).ConfigureAwait(false);
        }

        if (!install.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Adapter '{adapterId}' install exited {install.ExitCode}: {install.Stderr}");

        // Config shims (e.g. non-interactive flags) so the CLI never blocks the daemon on a prompt.
        foreach (var shim in spec.ConfigShims ?? Array.Empty<ConfigShim>())
            await _host.WriteFileAsync(shim.Path, shim.Content, ct).ConfigureAwait(false);

        // Verify: exit 0 AND the pinned version is what actually landed.
        var post = await _host.RunAsync(spec.HealthProbe.Command, ct).ConfigureAwait(false);
        if (!post.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.ProbeFailed,
                $"Adapter '{adapterId}' health probe exited {post.ExitCode}.");
        if (!post.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, StringComparison.Ordinal))
            throw new AdapterChannelException(AdapterChannelError.VersionMismatch,
                $"Adapter '{adapterId}' probe reported the wrong version (pinned '{spec.Version}').");

        // LAST, after the green probe: the install marker the daemon reads to wire agentKind → this
        // CLI's launch argv (InstalledAdapterCatalog). Launch-less adapters are tools, not agents.
        if (spec.Launch is { Count: > 0 })
        {
            await _host.WriteFileAsync(
                AdapterPaths.RegistryMarkerPath(spec.Id),
                InstalledAdapterMarker.Serialize(
                    new InstalledAdapterMarker(
                        spec.Id, spec.Version, spec.Launch, spec.ApiKeyEnvVar, spec.EgressHosts,
                        spec.CredentialPaths, spec.BaseUrlEnvVar)),
                ct).ConfigureAwait(false);
        }

        return AdapterEnsureResult.Installed;
    }

    /// <summary>The installCmd placeholder expanded to the staged, hash-verified payload path.</summary>
    public const string PayloadToken = "{payload}";

    /// <summary>
    /// The staged payload's file name: <c>&lt;id&gt;-&lt;version&gt;&lt;ext&gt;</c>, where the extension is
    /// carried over from the payload URL. <b>The extension is load-bearing, not cosmetic:</b> `npm install
    /// &lt;file&gt;` dispatches on it — a neutral name (`.payload`) makes npm treat the tarball as a DIRECTORY
    /// and fail with <c>ENOTDIR … /package.json</c> (verified against the real payload image). Deriving it
    /// from the URL keeps the channel format-agnostic: an npm `.tgz` stages as `.tgz`, a future `.zip` as `.zip`.
    /// </summary>
    internal static string StagedFileName(AdapterSpec spec)
    {
        var extension = string.Empty;
        if (Uri.TryCreate(spec.PayloadUrl, UriKind.Absolute, out var url))
        {
            var last = url.AbsolutePath.AsSpan()[(url.AbsolutePath.LastIndexOf('/') + 1)..];
            var dot = last.LastIndexOf('.');
            if (dot > 0)
                extension = last[dot..].ToString();
        }

        return $"{spec.Id}-{spec.Version}{extension}";
    }
}

/// <summary>The real HTTPS channel source. Refuses a non-HTTPS channel URL (a plaintext channel would
/// let a MITM swap the manifest before the hash pin can protect the payloads).</summary>
public sealed class HttpsAdapterChannelSource : IAdapterChannelSource
{
    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;
    private readonly Func<AdapterSpec, Uri> _payloadUrl;

    public HttpsAdapterChannelSource(Uri manifestUrl, Func<AdapterSpec, Uri>? payloadUrl = null, HttpMessageHandler? handler = null)
    {
        if (manifestUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The adapter channel must be HTTPS.", nameof(manifestUrl));
        _manifestUrl = manifestUrl;
        _payloadUrl = payloadUrl ?? SpecDeclaredPayloadUrl;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    /// <summary>Default resolver: the spec's own <c>payloadUrl</c> (schema-validated to be HTTPS).</summary>
    public static Uri SpecDeclaredPayloadUrl(AdapterSpec spec) =>
        spec.PayloadUrl is { Length: > 0 } url
            ? new Uri(url)
            : throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"Adapter '{spec.Id}' declares no payloadUrl and no payload resolver was configured.");

    public async Task<string> FetchManifestAsync(CancellationToken ct) =>
        await _http.GetStringAsync(_manifestUrl, ct).ConfigureAwait(false);

    public async Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct)
    {
        var url = _payloadUrl(spec);
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Adapter payloads must be fetched over HTTPS.");
        return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The bundled starter channel: the manifest ships INSIDE the app (the embedded
/// <c>adapters.starter.json</c> — pinned versions + sha256 the release was tested with), while the
/// payloads are fetched over HTTPS from each spec's <c>payloadUrl</c> (hash-verified before any
/// install). This is what makes CLI selection work OUT OF THE BOX with no hosted Mainguard channel yet;
/// a hosted channel later is just an <see cref="HttpsAdapterChannelSource"/> pointed at the same
/// manifest schema, and <see cref="AdapterChannel.RefreshAsync"/>-ing it updates the cache the same way.
/// </summary>
public sealed class BundledAdapterChannelSource : IAdapterChannelSource
{
    private readonly HttpClient _http;

    /// <summary>The embedded manifest is the shipped truth — it outranks any older cached copy.</summary>
    public bool IsAuthoritativeLocal => true;

    public BundledAdapterChannelSource(HttpMessageHandler? handler = null) =>
        _http = handler is null ? new HttpClient() : new HttpClient(handler);

    /// <summary>The embedded starter manifest JSON (validated by <see cref="AdapterManifest.Parse"/>
    /// in tests so a bad edit fails CI, not a user's install).</summary>
    public static string StarterManifestJson()
    {
        var assembly = typeof(BundledAdapterChannelSource).Assembly;
        const string resource = "Mainguard.Agents.Agents.Adapters.adapters.starter.json";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing from Mainguard.Agents.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task<string> FetchManifestAsync(CancellationToken ct) => Task.FromResult(StarterManifestJson());

    public async Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct)
    {
        var url = HttpsAdapterChannelSource.SpecDeclaredPayloadUrl(spec);
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Adapter payloads must be fetched over HTTPS.");
        return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }
}

/// <summary>The real in-VM install host: every command runs <c>wsl -d MainguardEnv --</c> via the hardened
/// P2-05 runner. Config shims are written with <c>tee</c> over stdin (no shell redirection surface).</summary>
public sealed class WslAdapterInstallHost : IAdapterInstallHost
{
    private readonly IWslRunner _wsl;

    public WslAdapterInstallHost(IWslRunner wsl) => _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));

    public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro(command.ToArray()), stdin: null, ct).ConfigureAwait(false);
        return new AdapterCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
        await _wsl.RunAsync(WslCommands.InDistro("mkdir", "-p", dir), stdin: null, ct).ConfigureAwait(false);
        var write = await _wsl.RunAsync(WslCommands.InDistro("tee", path), stdin: content, ct).ConfigureAwait(false);
        if (!write.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed, $"Writing config shim '{path}' failed.");
    }

    /// <summary>
    /// Stages verified payload bytes into the VM at <see cref="AdapterPaths.VmStageDir"/>. The runner's
    /// stdin is text-only, so the bytes travel base64 over stdin to <c>tee</c> and are decoded in-VM
    /// (<c>base64 -d</c> into the final file); the transient <c>.b64</c> is removed. Fixed, Mainguard-owned
    /// paths only — no user input reaches the script.
    /// </summary>
    public async Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
    {
        var safeName = string.Concat(fileName.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
        var b64Path = $"{AdapterPaths.VmStageDir}/{safeName}.b64";
        var finalPath = $"{AdapterPaths.VmStageDir}/{safeName}";

        await _wsl.RunAsync(WslCommands.InDistro("mkdir", "-p", AdapterPaths.VmStageDir), stdin: null, ct).ConfigureAwait(false);

        var upload = await _wsl.RunAsync(
            WslCommands.InDistro("tee", b64Path), stdin: Convert.ToBase64String(content), ct).ConfigureAwait(false);
        if (!upload.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Staging the verified payload into the VM failed (tee exit {upload.ExitCode}): {upload.StdErr}".Trim());

        var decode = await _wsl.RunAsync(
            WslCommands.InDistro("bash", "-c", $"base64 -d '{b64Path}' > '{finalPath}' && rm -f '{b64Path}'"),
            stdin: null, ct).ConfigureAwait(false);
        if (!decode.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Decoding the staged payload failed (exit {decode.ExitCode}): {decode.StdErr}".Trim());

        return finalPath;
    }
}

/// <summary>File-backed manifest cache under <c>%LocalAppData%\Mainguard\adapters\adapters.json</c>.</summary>
public sealed class FileAdapterManifestCache : IAdapterManifestCache
{
    private readonly string _path;

    public FileAdapterManifestCache(string? path = null)
    {
        // MainguardPaths, not GetFolderPath: the latter returns "" on Unix for a not-yet-materialized
        // home subdir, which would silently make this cache path relative under a service context.
        _path = path ?? System.IO.Path.Combine(MainguardPaths.DataRoot(), "adapters", "adapters.json");
    }

    public string? Read() => File.Exists(_path) ? File.ReadAllText(_path) : null;

    public void Write(string manifestJson)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, manifestJson);
    }
}
