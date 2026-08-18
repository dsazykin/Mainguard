using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>One agent CLI as offered to the user (OOBE picker / settings), with its live install state.</summary>
public sealed record AgentCliOption(
    string Id,
    string DisplayName,
    string Version,
    bool IsInstalled);

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
    /// The CLIs on offer, each flagged with whether it is already installed (a version-matched probe
    /// in the VM — the same check the channel's idempotence uses, so the picker never lies).
    /// </summary>
    public async Task<IReadOnlyList<AgentCliOption>> ListAsync(CancellationToken ct = default)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var options = new List<AgentCliOption>();
        foreach (var raw in manifest.Adapters)
        {
            var spec = _channel.EffectiveSpec(raw); // an accepted update moves the offered version too
            options.Add(new AgentCliOption(
                spec.Id, spec.DisplayName, spec.Version,
                await IsInstalledAsync(spec, ct).ConfigureAwait(false)));
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

    private async Task<bool> IsInstalledAsync(AdapterSpec spec, CancellationToken ct)
    {
        if (spec.HealthProbe is null)
            return false;
        try
        {
            var probe = await _host.RunAsync(spec.HealthProbe.Command, ct).ConfigureAwait(false);
            return probe.Succeeded
                && probe.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, StringComparison.Ordinal);
        }
        catch
        {
            return false; // no VM / no CLI → simply "not installed"; the picker still renders
        }
    }

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
