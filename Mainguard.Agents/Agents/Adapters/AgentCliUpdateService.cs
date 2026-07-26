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

/// <summary>One available agent-CLI update: the effective installed pin vs the registry's latest.</summary>
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

    /// <param name="handler">Injected transport for offline tests; null → a real handler.</param>
    /// <param name="log">Where a REFUSED update goes (MG-14). A refusal that only manifests as "nothing
    /// happened" is indistinguishable from "already current", which is precisely how a registry moving
    /// its tag backwards would stay invisible; the callers that have a log pass one.</param>
    public AgentCliUpdateService(
        AdapterChannel channel,
        IAdapterPinOverrideStore pins,
        HttpMessageHandler? handler = null,
        Action<string>? log = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true);
        _log = log;
    }

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
    {
        var host = new WslAdapterInstallHost(wsl);
        var pins = new FileAdapterPinOverrideStore();
        var channel = new AdapterChannel(
            new BundledAdapterChannelSource(), host, new FileAdapterManifestCache(), pins: pins);
        return new AgentCliUpdateService(channel, pins);
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
    /// <para><b>What the sha256 below is and is NOT.</b> It is computed from the bytes we just
    /// downloaded, so it pins THOSE bytes for every later install/reinstall of this version — that is
    /// real, and it is what stops a second fetch from installing something different. It is <b>not</b>
    /// authenticity: a registry (or anything able to impersonate it on this hop) that serves a
    /// malicious tarball has its tarball hashed and pinned, and the pin then verifies against itself.
    /// This is trust-on-first-use and cannot be closed here — closing it needs an independently-known
    /// good hash (a signature over the version, or an npm provenance attestation verified against a
    /// trusted root). <see cref="IPayloadSignatureVerifier"/> is where that plugs in when it exists;
    /// today it reports <see cref="SignatureVerdictKind.NotAvailable"/> and nothing is authenticated.</para>
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
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var before = _pins.TryGet(adapterId);
        _pins.Set(adapterId, new AdapterPinOverride(
            version, tarballUrl, sha256,
            new AdapterPinSnapshot(current.Version, current.PayloadUrl!, current.Sha256)));
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

    /// <summary>
    /// The install-time policy: resolve the registry's CURRENT release and install that, falling
    /// back to the effective pin only when the registry is unreachable (or the CLI is not
    /// npm-sourced / already current). "Latest" is resolved to a concrete version whose exact
    /// tarball gets sha256-pinned before anything installs — never a floating tag — so installs
    /// track upstream without ever running bytes no pin covered.
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

        await ApplyUpdateAsync(adapterId, latest, ct).ConfigureAwait(false);
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
