using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>One agent CLI as offered to the user (OOBE picker / settings), with its live install state.</summary>
/// <param name="Version">The version this channel would install today (the effective pin).</param>
/// <param name="IsInstalled">A runnable copy of this CLI answered its health probe — at ANY version,
/// not necessarily <paramref name="Version"/>. See <see cref="AgentCliInstaller.ListAsync"/>.</param>
/// <param name="InstalledVersion">The version that probe actually reported, or null when nothing is
/// installed. Equal to <paramref name="Version"/> in the ordinary case; different when the installed
/// copy has drifted from the pin this build ships.</param>
public sealed record AgentCliOption(
    string Id,
    string DisplayName,
    string Version,
    bool IsInstalled,
    string? InstalledVersion = null);

/// <summary>The outcome of installing one CLI — never a bare throw at the UI layer.</summary>
/// <param name="Error">Null on success; otherwise an actionable, user-facing sentence.</param>
public sealed record AgentCliInstallOutcome(string Id, bool Succeeded, string? Error = null);

/// <summary>
/// The user-facing agent-CLI install service (P2-22 §J-5) — what the OOBE's "choose your CLIs" step
/// and the settings "add more later" surface both drive. Thin, deliberately: it lists what the
/// channel offers against what is already installed, and installs a chosen set through the pinned,
/// hash-verified <see cref="AdapterChannel"/>. All policy (pin survival, hash refusal, version-matched
/// probe, idempotence) stays in the channel.
///
/// <para><b>Why CLIs are dynamic, not baked into the agent image:</b> the user chooses during setup and
/// can add more at any time. Installs land in ONE VM-side prefix that every sandbox bind-mounts
/// read-only, so a CLI installed after provisioning is available to the next agent with no image
/// rebuild — and an agent can never modify the binaries another agent executes.</para>
///
/// <para><b>Failure posture:</b> installing CLIs must never be able to fail the whole OOBE. Each CLI
/// is independent: one failure is reported for that CLI and the rest continue. A user with zero CLIs
/// still gets a working Mainguard — they simply add one later from settings.</para>
/// </summary>
public sealed class AgentCliInstaller
{
    private readonly AdapterChannel _channel;
    private readonly IAdapterInstallHost _host;
    private readonly AgentCliUpdateService? _updater;

    /// <param name="updater">When present, installs resolve the registry's CURRENT release and pin
    /// that (the bundled pin becomes the offline fallback) — there is no fixed default install
    /// version. Null keeps the pure bundled-pin behavior (tests and offline compositions).</param>
    public AgentCliInstaller(AdapterChannel channel, IAdapterInstallHost host, AgentCliUpdateService? updater = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _updater = updater;
    }

    /// <summary>The default composition: the bundled starter channel installing into the MainguardEnv VM,
    /// with the managed updater on top — an install resolves the registry's current release and
    /// sha256-pins it, so a fresh install is never a stale shipped version; the bundled pins remain
    /// the offline fallback and the user's accepted-update overrides are always honored.</summary>
    public static AgentCliInstaller CreateDefault(IWslRunner wsl)
        => CreateDefault(new WslAdapterInstallHost(wsl));

    /// <summary>The same composition over any install host — the macos-host substrate passes the
    /// container-backed host, since its CLIs execute in-jail (linux), never on the macOS host.</summary>
    public static AgentCliInstaller CreateDefault(IAdapterInstallHost host)
    {
        var pins = new FileAdapterPinOverrideStore();
        var channel = new AdapterChannel(new BundledAdapterChannelSource(), host, new FileAdapterManifestCache(),
            pins: pins);
        return new AgentCliInstaller(channel, host, new AgentCliUpdateService(channel, pins));
    }

    /// <summary>
    /// The CLIs on offer, each flagged with whether a runnable copy is installed in the VM and, when
    /// one is, WHICH version its health probe reported.
    ///
    /// <para><b>"Installed" here means installed AT ALL, not installed at today's pin.</b> This used to
    /// require the probe's stdout to contain the currently-offered version substring — the same check
    /// <see cref="AdapterChannel"/> uses — and that is the wrong question for a picker. The pin is an
    /// offline FLOOR, not a ceiling: <see cref="AgentCliUpdateService.EnsureLatestAsync"/> installs the
    /// registry's current release, and an app update can ship a newer pin than the copy already on
    /// disk. Either way the installed version stops containing the offered substring, and the picker
    /// then reported "Not installed" — with an Install button — for a CLI that was at that moment
    /// running a live coordinator (walkthrough W6: the probe said 2.1.223, the manifest pinned
    /// 2.1.218). The drift is surfaced separately, off <see cref="AgentCliOption.InstalledVersion"/>,
    /// as an annotation on an INSTALLED row rather than as an absence.
    ///
    /// <para>The channel's own exact-match probes are deliberately untouched: install-idempotence and
    /// post-install verification ask "are the bytes we just placed the ones we asked for", which is a
    /// different question and must stay strict.</para></para>
    /// </summary>
    public async Task<IReadOnlyList<AgentCliOption>> ListAsync(CancellationToken ct = default)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var options = new List<AgentCliOption>();
        foreach (var raw in manifest.Adapters)
        {
            var spec = _channel.EffectiveSpec(raw); // an accepted update moves the offered version too
            var installed = await ProbeInstalledVersionAsync(spec, ct).ConfigureAwait(false);
            options.Add(new AgentCliOption(
                spec.Id, spec.DisplayName, spec.Version,
                IsInstalled: installed is not null,
                InstalledVersion: installed));
        }

        return options;
    }

    /// <summary>
    /// Installs each chosen CLI, reporting progress per CLI. Independent and failure-isolated: a CLI
    /// that fails yields a typed outcome with an actionable message and the others still install.
    /// Idempotent — an already-installed CLI is a no-op.
    /// </summary>
    public async Task<IReadOnlyList<AgentCliInstallOutcome>> InstallAsync(
        IReadOnlyList<string> adapterIds,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapterIds);
        var outcomes = new List<AgentCliInstallOutcome>();

        foreach (var id in adapterIds)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Installing {id}…");
            try
            {
                // Latest-first: resolve the registry's current release and pin it; the bundled pin
                // is the offline fallback, never a ceiling.
                if (_updater is not null)
                    await _updater.EnsureLatestAsync(id, ct).ConfigureAwait(false);
                else
                    await _channel.EnsureAsync(id, ct).ConfigureAwait(false);
                outcomes.Add(new AgentCliInstallOutcome(id, true));
                progress?.Report($"{id} is ready.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AdapterChannelException ex)
            {
                var message = Explain(id, ex);
                outcomes.Add(new AgentCliInstallOutcome(id, false, message));
                progress?.Report(message);
            }
            catch (Exception ex)
            {
                var message = $"{id} could not be installed: {ex.Message} You can try again from "
                    + "Settings once setup finishes; Mainguard works without it.";
                outcomes.Add(new AgentCliInstallOutcome(id, false, message));
                progress?.Report(message);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// The version a runnable copy of <paramref name="spec"/> reports in the VM, or null when there is
    /// nothing installed. Two independent conditions, and both are needed to keep the genuinely-absent
    /// case honest once the pin match is gone:
    ///
    /// <para><b>Exit 0.</b> Every health probe in the channel is
    /// <c>&lt;prefix&gt;/bin/&lt;cli&gt; --version</c>, so a CLI that was never installed exits 127 (no
    /// such file) and a launcher-only package whose platform executable was never placed exits 1 — that
    /// placeholder prints "native binary not installed", see <see cref="PlatformBinaryLink"/>. Exit
    /// status alone already rejects both.</para>
    ///
    /// <para><b>A version in stdout.</b> A <c>--version</c> that exits 0 while printing no version at
    /// all is not a working CLI, so requiring one keeps a degenerate probe from reading as installed —
    /// and it is what gives the row a real version to show. What the five shipped CLIs actually print:
    /// <c>2.1.223 (Claude Code)</c>, <c>codex-cli 0.145.0</c>, and a bare <c>0.52.0</c> / <c>0.20.1</c>
    /// / <c>1.18.4</c>. The first dotted-numeric token covers every one of those shapes without
    /// assuming the version sits at any particular position in the line.</para>
    /// </summary>
    private async Task<string?> ProbeInstalledVersionAsync(AdapterSpec spec, CancellationToken ct)
    {
        if (spec.HealthProbe is null)
            return null;
        try
        {
            var probe = await _host.RunAsync(spec.HealthProbe.Command, ct).ConfigureAwait(false);
            return probe.Succeeded ? TryParseVersion(probe.Stdout) : null;
        }
        catch
        {
            return null; // no VM / no CLI → simply "not installed"; the picker still renders
        }
    }

    /// <summary>The first version-shaped token in a <c>--version</c> line, or null when there is none.</summary>
    internal static string? TryParseVersion(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        var match = VersionToken.Match(stdout);
        return match.Success ? match.Value : null;
    }

    /// <summary><c>major.minor[.patch…][-prerelease|+build]</c> — dotted-NUMERIC, so a CLI name or a
    /// path sharing the line cannot be mistaken for the version.</summary>
    private static readonly Regex VersionToken = new(
        @"\d+\.\d+(?:\.\d+)*(?:[-+][0-9A-Za-z][0-9A-Za-z.\-+]*)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Turns a typed channel refusal into a sentence naming a real cause and a real next step
    /// (every OOBE error must be actionable — an opaque one costs the user a debugging round).</summary>
    private static string Explain(string id, AdapterChannelException ex) => ex.Error switch
    {
        AdapterChannelError.HashMismatch =>
            $"{id} was not installed: the downloaded file did not match Mainguard's published checksum, so"
            + "it was refused. This usually means the download was corrupted or intercepted — check your "
            + "network (proxy/VPN) and try again.",
        AdapterChannelError.InstallFailed =>
            $"{id} could not be installed inside the Mainguard VM: {ex.Message} You can try again from "
            + "Settings once setup finishes; Mainguard works without it.",
        AdapterChannelError.ProbeFailed =>
            $"{id} installed but would not start, so it was not enabled. Try again from Settings once "
            + "setup finishes.",
        AdapterChannelError.VersionMismatch =>
            $"{id} installed as a different version than Mainguard pinned, so it was not enabled. "
            + "Try again from Settings once setup finishes.",
        AdapterChannelError.UnknownAdapter =>
            $"{id} is not offered by this Mainguard version's CLI channel.",
        _ => $"{id} could not be installed: {ex.Message}",
    };
}
