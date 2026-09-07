using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>One available agent-CLI update: the effective pin this update would replace vs the
/// registry's latest.</summary>
/// <param name="InstalledVersion">The current EFFECTIVE PIN (<c>spec.Version</c>), not a probed
/// installed version — this type has no probe access. It can be older than what is actually on disk
/// (see <see cref="AgentCliOption.InstalledVersion"/>, W6); callers that need the truth on-disk
/// version must cross-reference <see cref="AgentCliInstaller.ListAsync"/> themselves, as
/// <c>ProDesktopHost.KickAgentCliUpdateCheck</c> does.</param>
public sealed record AgentCliUpdate(string Id, string DisplayName, string InstalledVersion, string LatestVersion);

/// <summary>
/// The Mainguard-managed CLI updater. The in-CLI self-updaters are disabled in every jail (the
/// adapters mount is read-only and versions are pinned), so THIS is how a CLI moves forward: check
/// the npm registry for a newer release, and only on the user's explicit accept, download the exact
/// tarball, compute its sha256, store it as a pin OVERRIDE (with the current pin as the one-step
/// revert history), and run the same hash-verified <see cref="AdapterChannel.EnsureAsync"/> install
/// path the pinned channel always uses. Nothing here weakens the pin discipline: an update is a new
/// concrete pin chosen by the user, never a floating <c>@latest</c>.
///
/// <para><b>Revert</b> restores the previous pin the same way — the settings window offers it so a
/// CLI release that breaks the app is a one-click rollback, not a re-setup.</para>
/// </summary>
public sealed class AgentCliUpdateService
{
    private readonly AdapterChannel _channel;
    private readonly IAdapterPinOverrideStore _pins;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly INpmProvenanceGate _provenance;

    /// <param name="handler">Injected transport for offline tests; null → a real handler.</param>
    /// <param name="log">Where a REFUSED update goes (MG-14). A refusal that only manifests as "nothing
    /// happened" is indistinguishable from "already current", which is precisely how a registry moving
    /// its tag backwards would stay invisible; the callers that have a log pass one.</param>
    /// <param name="provenance">The MG-9 gate. Null → the real registry-backed gate over the same
    /// transport (so a test that already stubs <paramref name="handler"/> keeps a coherent world), with
    /// the npm keys pinned inside it.</param>
    /// <param name="autoAdoptRegistryLatest">
    /// <b>Audit F48 — off by default, deliberately.</b> When false (the shipped setting)
    /// <see cref="EnsureLatestAsync"/> installs the version the SHIPPED MANIFEST pins and merely
    /// RECORDS that a newer release exists; the pin only ever moves through
    /// <see cref="ApplyUpdateAsync"/>, i.e. on an explicit user accept.
    ///
    /// <para>It used to be unconditionally true, and that is what made the registry's <c>latest</c> tag
    /// a trust anchor it cannot be. Every check under it is a check on the BYTES — the npm signature
    /// proves npm stored them, the integrity proves we hold them — and none of them is a check on the
    /// CHOICE OF VERSION. A publisher account takeover, or npm itself, can publish a new release that
    /// is perfectly, legitimately signed; the whole ladder passes, and the malicious version installs
    /// itself on every machine at next launch with no human in the loop. Requiring an accept does not
    /// make that release safe, but it puts a person between "upstream published something" and
    /// "Mainguard runs it", which is the only control that addresses a signed-but-hostile version.</para>
    ///
    /// <para>The cost is that a fresh install lands on the shipped pin rather than on upstream's newest,
    /// until the user accepts the offered update. That is the same trade the rest of this file already
    /// makes ("the bundled pin is a FLOOR here"), applied to the one path that had opted out of it.</para>
    /// </param>
    public AgentCliUpdateService(
        AdapterChannel channel,
        IAdapterPinOverrideStore pins,
        HttpMessageHandler? handler = null,
        Action<string>? log = null,
        INpmProvenanceGate? provenance = null,
        bool autoAdoptRegistryLatest = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true);
        _log = log;
        _provenance = provenance ?? new NpmProvenanceGate(new HttpNpmProvenanceSource(_http));
        _autoAdoptRegistryLatest = autoAdoptRegistryLatest;
    }

    private readonly bool _autoAdoptRegistryLatest;

    /// <summary>
    /// Every update this service refused because it did not move FORWARD, newest last (MG-14). The
    /// install path cannot throw on a refusal — an install must still succeed off the pinned fallback —
    /// so the refusal has to be readable afterwards or it is lost. Callers/tests read this; the UI
    /// surfaces it alongside the install outcome.
    /// </summary>
    public IReadOnlyList<string> RefusedUpdates => _refusals;

    private readonly List<string> _refusals = new();

    private void Refuse(string message)
    {
        _refusals.Add(message);
        try
        {
            _log?.Invoke(message);
        }
        catch (Exception)
        {
            // A logging sink must never turn a refusal into a fault — the refusal already stands.
        }
    }

    /// <summary>The default composition: the bundled channel + the file-backed override store,
    /// installing into the MainguardEnv VM (mirrors <see cref="AgentCliInstaller.CreateDefault"/>).</summary>
    public static AgentCliUpdateService CreateDefault(IWslRunner wsl)
        => CreateDefault(new WslAdapterInstallHost(wsl));

    /// <summary>The same composition over any install host (macos-host passes the container-backed one).</summary>
    public static AgentCliUpdateService CreateDefault(IAdapterInstallHost host)
    {
        var pins = new FileAdapterPinOverrideStore();
        // ONE gate for both halves (audit F47): the channel now runs it on every override-governed
        // install, not just where a pin moves, and sharing the instance means the update path and the
        // install path can never end up holding different npm trust anchors.
        var provenance = new NpmProvenanceGate(new HttpNpmProvenanceSource(new HttpClient()));
        var channel = new AdapterChannel(
            new BundledAdapterChannelSource(), host, new FileAdapterManifestCache(),
            pins: pins, provenance: provenance);
        return new AgentCliUpdateService(channel, pins, provenance: provenance);
    }

    /// <summary>
    /// The npm package name a registry tarball URL pins (e.g.
    /// <c>https://registry.npmjs.org/@anthropic-ai/claude-code/-/claude-code-2.1.210.tgz</c> →
    /// <c>@anthropic-ai/claude-code</c>). Null for any non-npmjs payload — those CLIs simply have
    /// no update channel and never appear in the check.
    /// </summary>
    internal static string? TryParseNpmPackage(string? payloadUrl)
    {
        if (payloadUrl is null || !Uri.TryCreate(payloadUrl, UriKind.Absolute, out var url))
            return null;
        if (!string.Equals(url.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/');
        var separator = path.IndexOf("/-/", StringComparison.Ordinal);
        return separator > 0 ? path[..separator] : null;
    }

    /// <summary>
    /// Checks every npm-sourced CLI in the channel for a newer release. Per-CLI failures (registry
    /// unreachable, junk metadata) skip that CLI and never fail the sweep — an update check must be
    /// harmless at app launch. Returns only real, concrete-version upgrades.
    /// </summary>
    public async Task<IReadOnlyList<AgentCliUpdate>> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var updates = new List<AgentCliUpdate>();
        foreach (var raw in manifest.Adapters)
        {
            ct.ThrowIfCancellationRequested();
            var spec = _channel.EffectiveSpec(raw);
            var package = TryParseNpmPackage(spec.PayloadUrl);
            if (package is null)
                continue;

            string? latest;
            try
            {
                latest = await FetchLatestVersionAsync(package, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                continue; // this CLI's registry lookup failed — keep checking the rest
            }

            if (latest is null || !AdapterManifest.IsPinnedVersion(latest))
                continue;

            // MG-14: an UPDATE is a version that moves strictly forward, not merely one that differs.
            // This used to be `!string.Equals(latest, spec.Version)`, which offered the user a
            // one-click "update" to an OLDER release whenever the registry's `latest` tag moved
            // backwards (a maintainer un-publishing, an account takeover re-tagging, or a MITM
            // standing in for the registry on this plaintext-metadata path). Downgrading a coding
            // agent's CLI silently re-opens every fix the newer release shipped.
            if (!UpdateVersion.IsUpgrade(latest, spec.Version))
            {
                if (UpdateVersion.IsDowngrade(latest, spec.Version))
                {
                    Refuse($"'{spec.Id}': the registry's latest is {latest} but {spec.Version} is pinned — "
                        + "refusing to offer a DOWNGRADE as an update.");
                }

                continue; // equal, or a pair we cannot order → not an upgrade, so not an offer
            }

            updates.Add(new AgentCliUpdate(spec.Id, spec.DisplayName, spec.Version, latest));
        }

        return updates;
    }

    /// <summary>
    /// Applies a user-accepted update: fetch the exact registry tarball for
    /// <paramref name="version"/>, sha256 it (the new pin covers precisely these advertised bytes),
    /// store the override with the current pin as revert history, and install through the channel's
    /// normal verify→install→probe path. A failed install restores the prior override state so a
    /// broken update can never wedge the CLI's pin.
    ///
    /// <para><b>Refuses any version that does not move strictly forward</b> (MG-14) — see the check
    /// below. Revert deliberately does NOT come through here (it writes the previous pin directly), so
    /// the one legitimate backwards move keeps working.</para>
    ///
    /// <para><b>MG-9 — the circularity is closed HERE.</b> The sha256 stored below is still computed
    /// from the bytes just downloaded, and on its own that would be trust-on-first-use: whoever served
    /// the tarball would also be choosing the hash that later "verifies" it. So the pin is no longer
    /// written on the strength of its own arithmetic. Before it is stored, the bytes must clear
    /// <see cref="NpmProvenancePolicy"/> at the rung the adapter declares in the manifest — at minimum an
    /// npm registry ECDSA signature over <c>{name}@{version}:{integrity}</c> verified against a public
    /// key <b>compiled into this app</b> (<see cref="NpmSigningKeys"/>), with the downloaded tarball
    /// required to hash to that signed integrity. The expected digest therefore arrives inside something
    /// only npm's private key can produce, not out of the same response as the artifact. For an adapter
    /// declaring <see cref="AdapterProvenanceLevel.NpmBuildProvenance"/> a SLSA build-provenance
    /// attestation whose in-toto subject binds to those exact bytes is required on top.</para>
    ///
    /// <para><b>Fail-closed:</b> a refused verdict throws
    /// <see cref="AdapterChannelError.ProvenanceRejected"/> and the pin is never moved. There is no
    /// warn-and-continue and no fallback to the self-derived hash. The one rung that proceeds without
    /// verification is <see cref="AdapterProvenanceLevel.None"/>, which a maintainer must write into the
    /// manifest deliberately and which reports itself as unverified on every install.</para>
    ///
    /// <para><b>What is still NOT established:</b> that the <i>publisher</i> built the bytes, for the
    /// four CLIs that publish no build provenance; and the Sigstore chain behind the one that does is
    /// not validated in-process. See <see cref="AdapterProvenanceLevel"/> for the exact limits of each
    /// rung, and <c>adapters.starter.json</c> for who sits where.</para>
    /// </summary>
    public async Task ApplyUpdateAsync(string adapterId, string version, CancellationToken ct = default)
    {
        var current = await EffectiveSpecAsync(adapterId, ct).ConfigureAwait(false);
        var package = TryParseNpmPackage(current.PayloadUrl)
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"'{adapterId}' is not an npm-channel CLI — it has no update path.");

        // MG-14: the monotonic gate, enforced at the point that actually MOVES the pin rather than only
        // where updates are offered — CheckForUpdatesAsync is advisory, this is the mutation. An equal
        // version is a no-op (re-pinning identical bytes is pointless churn); anything older, or any
        // pair that cannot be ordered, is refused loudly.
        if (!UpdateVersion.IsUpgrade(version, current.Version))
        {
            var why = UpdateVersion.IsDowngrade(version, current.Version)
                ? $"{version} is OLDER than the pinned {current.Version} — refusing to downgrade '{adapterId}'"
                : UpdateVersion.TryCompare(version, current.Version) == 0
                    ? $"{version} is already the pinned version for '{adapterId}' — nothing to apply"
                    : $"'{version}' and the pinned '{current.Version}' cannot be ordered, so the update "
                      + $"for '{adapterId}' could not be shown to move forward";
            Refuse($"update refused: {why}.");
            throw new AdapterChannelException(AdapterChannelError.UpdateRefused, $"Update refused: {why}.");
        }

        var (tarballUrl, bytes) = await FetchTarballAsync(package, version, ct).ConfigureAwait(false);

        // MG-9: establish ORIGIN before the pin is written, so the hash below is a record of bytes that
        // already cleared an externally-anchored check rather than a self-signed certificate of their own
        // correctness. This runs on the bytes in hand — moving it after the pin write would let a refused
        // payload leave a pin behind for EnsureAsync to install.
        // The declared rung comes from the MANIFEST spec, never from the pin override: an override is a
        // user-writable file, and letting it carry the requirement would let it lower the requirement.
        var provenance = await _provenance
            .EvaluateAsync(adapterId, current.ProvenanceLevel, package, version, bytes, ct)
            .ConfigureAwait(false);
        if (provenance.MustRefuse)
        {
            Refuse($"update refused: {provenance.Reason}");
            throw new AdapterChannelException(AdapterChannelError.ProvenanceRejected, provenance.Reason);
        }

        _log?.Invoke(provenance.Reason);

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var pin = new AdapterPinOverride(
            version, tarballUrl, sha256,
            new AdapterPinSnapshot(current.Version, current.PayloadUrl!, current.Sha256));

        // Audit F54: INSTALL FIRST, then persist the pin.
        //
        // This used to write the override to pin-overrides.json and only then install, unwinding with a
        // best-effort RestorePin in a catch. A catch does not run when the process dies — a crash, a
        // kill, a power loss between the two left the NEW pin active on disk over the OLD binary, and
        // the next EnsureAsync then installed the new version silently, with no user accept and no
        // second confirmation. Passing the pin down as an already-provenance-verified value keeps the
        // user-writable file untouched until an install has actually succeeded, so the window closes
        // rather than shrinking: there is no interval in which a pin nobody installed is authoritative.
        await _channel.EnsureAsync(adapterId, new AdapterChannel.VerifiedPin(pin, provenance), ct)
            .ConfigureAwait(false);

        _pins.Set(adapterId, pin);
    }

    /// <summary>
    /// The install-time policy. By default (audit F48) it installs the EFFECTIVE PIN and records any
    /// newer registry release as an offer rather than adopting it; with
    /// <c>autoAdoptRegistryLatest</c> it resolves the registry's current release and installs that
    /// instead, falling back to the pin when the registry is unreachable. Either way "latest" is
    /// resolved to a concrete version whose exact tarball is sha256-pinned before anything installs —
    /// never a floating tag — so nothing runs that no pin covered.
    ///
    /// <para><b>Residual gap, stated because it is not fixed here.</b> The pin covers the TARBALL. The
    /// in-VM <c>npm install</c> that consumes it still resolves that package's dependency closure — and,
    /// for the launcher-style CLIs, the platform sub-package that actually executes — live from the
    /// registry with no lockfile. Pinning those needs a channel-format change (a Mainguard-authored
    /// lockfile per adapter plus an <c>npm ci</c>-shaped install command in
    /// <c>adapters.starter.json</c>), which is the FIRST RESIDUAL GAP that file already names.</para>
    ///
    /// <para><b>MG-14:</b> a registry <c>latest</c> that is OLDER than what we already pin is not
    /// installed — it is refused and recorded, and the install proceeds off the shipped pin. This is
    /// the install-side twin of the update-side guard: "latest" is the registry's word for it, and a
    /// registry that moves its tag backwards must not be able to drag a fresh install backwards with
    /// it. The bundled pin is a FLOOR here, not just an offline fallback.</para>
    /// </summary>
    public async Task EnsureLatestAsync(string adapterId, CancellationToken ct = default)
    {
        var current = await EffectiveSpecAsync(adapterId, ct).ConfigureAwait(false);

        string? latest = null;
        if (TryParseNpmPackage(current.PayloadUrl) is { } package)
        {
            try
            {
                latest = await FetchLatestVersionAsync(package, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Registry unreachable — the bundled/override pin below is the offline fallback.
            }
        }

        if (latest is not null && AdapterManifest.IsPinnedVersion(latest)
            && UpdateVersion.IsDowngrade(latest, current.Version))
        {
            // Loud, then fall through to the pinned install: refusing the DOWNGRADE must not refuse
            // the CLI. An install that throws here would leave the user with no agent at all over an
            // upstream mistake, so the safe move is "install the version we already trust, and say why".
            Refuse($"'{adapterId}': the registry's latest is {latest} but {current.Version} is pinned — "
                + "installing the pinned version instead of DOWNGRADING.");
            latest = null;
        }

        if (latest is null
            || !AdapterManifest.IsPinnedVersion(latest)
            || !UpdateVersion.IsUpgrade(latest, current.Version))
        {
            // Equal, unorderable, unpinned, or offline → install exactly what is pinned today.
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
            return;
        }

        if (!_autoAdoptRegistryLatest)
        {
            // MG-9 / audit F48: a real upgrade exists, and we install the SHIPPED PIN anyway. Every
            // check this service runs is a check on the bytes, never on the choice of version — a
            // legitimately signed release from a taken-over publisher account clears all of them. So
            // upstream publishing something is recorded as an OFFER (RefusedUpdates, and the update
            // check the UI reads) and the pin moves only through an explicit user accept.
            Refuse($"'{adapterId}': the registry's latest is {latest} but {current.Version} is pinned — "
                + "installing the pinned version. A newer release is not adopted automatically: the "
                + "signature checks establish that npm stored those bytes, never that the new version "
                + "is one anybody chose to trust. Accept the update in Settings to move the pin.");
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await ApplyUpdateAsync(adapterId, latest, ct).ConfigureAwait(false);
        }
        catch (AdapterChannelException ex) when (ex.Error == AdapterChannelError.ProvenanceRejected)
        {
            // MG-9, install side. The registry's `latest` did NOT clear its provenance rung, so those
            // bytes are refused outright — that part is fail-closed and non-negotiable. What we then do
            // is install the SHIPPED pin instead, and this is not a weakening: the bundled sha256 is a
            // constant reviewed into this repository, so falling back lands on bytes a human vouched
            // for rather than on bytes an attacker chose. Throwing here would instead let anyone who can
            // interfere with the metadata request deny the user an agent entirely.
            Refuse($"'{adapterId}': the registry's latest ({latest}) failed its provenance check — "
                + $"{ex.Message} Installing the shipped pin {current.Version} instead.");
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The version "Revert" would restore for <paramref name="adapterId"/>, or null when
    /// no accepted update left a previous pin behind.</summary>
    public string? PreviousVersion(string adapterId) => _pins.TryGet(adapterId)?.Previous?.Version;

    /// <summary>
    /// Reverts an accepted update to the pin it replaced (the settings escape hatch for a CLI
    /// release that breaks the app). Reverting to the bundled pin simply removes the override;
    /// reverting to an earlier accepted update re-pins it. Installs through the same verified path.
    /// </summary>
    public async Task RevertAsync(string adapterId, CancellationToken ct = default)
    {
        var before = _pins.TryGet(adapterId);
        if (before?.Previous is not { } previous)
            throw new InvalidOperationException($"'{adapterId}' has no previous version to revert to.");

        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var bundled = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal));
        if (bundled is not null
            && string.Equals(previous.Version, bundled.Version, StringComparison.Ordinal)
            && string.Equals(previous.Sha256, bundled.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _pins.Remove(adapterId); // back to the shipped truth — no override needed
        }
        else
        {
            _pins.Set(adapterId, new AdapterPinOverride(previous.Version, previous.PayloadUrl, previous.Sha256));
        }

        try
        {
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
        }
        catch
        {
            RestorePin(adapterId, before);
            throw;
        }
    }

    private void RestorePin(string adapterId, AdapterPinOverride? before)
    {
        try
        {
            if (before is null)
                _pins.Remove(adapterId);
            else
                _pins.Set(adapterId, before);
        }
        catch
        {
            // Restoring the pin is best-effort on an already-failing path.
        }
    }

    private async Task<AdapterSpec> EffectiveSpecAsync(string adapterId, CancellationToken ct)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var raw = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal))
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"No adapter '{adapterId}' in the channel manifest.");
        return _channel.EffectiveSpec(raw);
    }

    private async Task<string?> FetchLatestVersionAsync(string package, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            new Uri($"https://registry.npmjs.org/{package}/latest"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    private async Task<(string Url, byte[] Bytes)> FetchTarballAsync(string package, string version, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            new Uri($"https://registry.npmjs.org/{package}/{Uri.EscapeDataString(version)}"), ct).ConfigureAwait(false);
        string? tarball;
        using (var doc = JsonDocument.Parse(json))
        {
            tarball = doc.RootElement.TryGetProperty("dist", out var dist)
                && dist.TryGetProperty("tarball", out var url) ? url.GetString() : null;
        }

        // The registry names its own tarball URL; hold it to the same shape the manifest enforces.
        if (tarball is null
            || !Uri.TryCreate(tarball, UriKind.Absolute, out var tarballUri)
            || tarballUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(tarballUri.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"The npm registry did not name a usable tarball for {package}@{version}.");
        }

        var bytes = await _http.GetByteArrayAsync(tarballUri, ct).ConfigureAwait(false);
        return (tarball, bytes);
    }
}
