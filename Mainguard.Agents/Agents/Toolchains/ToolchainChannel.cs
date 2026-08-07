using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;

namespace Mainguard.Agents.Agents.Toolchains;

/// <summary>Why a toolchain operation was refused or failed. Typed so the settings surface can say what
/// happened instead of surfacing an exit code.</summary>
public enum ToolchainChannelError
{
    /// <summary>The manifest curates no toolchain with that id.</summary>
    UnknownToolchain,
    /// <summary>The payload could not be fetched into the VM.</summary>
    DownloadFailed,
    /// <summary>The fetched payload's SHA-256 did not match the manifest pin — install refused.</summary>
    HashMismatch,
    /// <summary>The payload could not be unpacked.</summary>
    ExtractFailed,
    /// <summary>The toolchain landed but does not run.</summary>
    ProbeFailed,
    /// <summary>The toolchain runs but is not the pinned version.</summary>
    VersionMismatch,
    /// <summary>The toolchain could not be removed.</summary>
    RemoveFailed,
}

/// <summary>The typed refusal/failure of a toolchain operation.</summary>
public sealed class ToolchainChannelException : Exception
{
    public ToolchainChannelError Error { get; }

    public ToolchainChannelException(ToolchainChannelError error, string message)
        : base(message) => Error = error;
}

/// <summary>What the settings surface (and the spawn path) needs to know about one curated toolchain.</summary>
/// <param name="Entry">The manifest entry.</param>
/// <param name="IsInstalled">True only when the toolchain was just PROVEN to run at the pinned version
/// — never merely because a marker file exists. See <see cref="ToolchainChannel.ListAsync"/>.</param>
/// <param name="Detail">What the probe reported, or why it did not run. Always something a human can act
/// on.</param>
/// <param name="CouldNotCheck">
/// True when the check itself could not be performed — the environment could not be reached at all, so
/// nothing was learned about this toolchain either way.
///
/// <para><b>Why this is not folded into <paramref name="IsInstalled"/>.</b> "We ran the check and the
/// toolchain is absent" and "we could not run the check" are different facts with different remedies,
/// and collapsing them produces a confidently wrong diagnosis: telling someone whose WSL is not running
/// to "install it in Settings → Toolchains" sends them to a button that will also fail, for a reason
/// nothing on screen mentions. A raw exception at least did not claim to know. Swapping it for a
/// misleading sentence would not be an improvement, so the third state is carried explicitly.</para>
/// </param>
public sealed record ToolchainStatus(
    ToolchainEntry Entry, bool IsInstalled, string Detail, bool CouldNotCheck = false);

/// <summary>
/// The user-managed toolchain channel: installs a curated, pinned, checksum-verified language toolchain
/// INTO THE VM at a moment a human chose, and removes it again.
///
/// <para><b>Why this is not the image-layer provisioner.</b>
/// <see cref="Sandbox.ToolchainProvisioner"/> answers a different question — it builds a per-repo image
/// layer on the spawn path, which is right for a toolchain that needs system packages (<c>dotnet-10</c>
/// needs <c>libicu72</c> and <c>libfontconfig1</c> from apt, and no bind mount can deliver those) or the
/// baked nix store. It is wrong for the thing the owner actually asked for, because an image layer is
/// chosen by the REPOSITORY at spawn and cannot be chosen, inspected or removed by a HUMAN. A
/// self-contained relocatable tarball needs neither a build nor root, so it can be a directory the user
/// installs once and a read-only bind mount thereafter — the mechanism
/// <see cref="Adapters.AdapterChannel"/> already proved for agent CLIs, reused here down to the
/// <see cref="IAdapterInstallHost"/> seam so there is one way to run a command in the VM rather than
/// two.</para>
///
/// <para><b>The pin is verified where the bytes land.</b> The payload is fetched by the VM and hashed by
/// the VM, and the hex is compared here against the manifest. It is deliberately NOT streamed through
/// the host the way an adapter payload is: an adapter tarball is tens of megabytes and a toolchain is
/// ~350 MB, which base64 over a WSL stdin pipe is not a reasonable way to move. Nothing is weakened by
/// that — the checksum still gates extraction, and it is the same fetch-then-verify-then-execute
/// discipline <c>images/mainguard-agent-base/Dockerfile</c> holds itself to.</para>
///
/// <para><b>An install is only an install once it RUNS.</b> The marker is written last, after a probe
/// that executes the toolchain and matches the pinned version in its output, and
/// <see cref="ListAsync"/> re-probes rather than trusting the marker. That is not ceremony: the adapter
/// channel shipped a bug where <c>--ignore-scripts</c> left a launcher stub, the marker said healthy,
/// and it stayed wrong for eleven days. A status derived from a file is a status that can lie.</para>
/// </summary>
public sealed class ToolchainChannel
{
    private readonly ToolchainManifest _manifest;
    private readonly IAdapterInstallHost _host;
    private readonly Action<string>? _log;
    private readonly string _root;

    /// <param name="host">How commands run in the VM.</param>
    /// <param name="manifest">The curated set (defaults to the shipped one).</param>
    /// <param name="log">Daemon log sink.</param>
    /// <param name="vmRoot">Where toolchains install. Defaults to <see cref="ToolchainPaths.VmRoot"/>;
    /// overridden ONLY by tests, which need a throwaway root they can populate and delete rather than
    /// the one real directory every jail mounts.</param>
    public ToolchainChannel(
        IAdapterInstallHost host, ToolchainManifest? manifest = null, Action<string>? log = null,
        string? vmRoot = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _manifest = manifest ?? ToolchainManifest.Shipped;
        _log = log;
        _root = string.IsNullOrWhiteSpace(vmRoot) ? ToolchainPaths.VmRoot : vmRoot.TrimEnd('/');
    }

    /// <summary>Where this channel installs toolchains.</summary>
    public string VmRoot => _root;

    /// <summary>The manifest this channel serves.</summary>
    public ToolchainManifest Manifest => _manifest;

    /// <summary>
    /// Every curated toolchain and whether it is really usable, established by RUNNING each one. Costs
    /// one exec per entry; a settings page that opened faster by reading marker files would be a
    /// settings page that can show "Installed" for something that does not work.
    /// </summary>
    public async Task<IReadOnlyList<ToolchainStatus>> ListAsync(CancellationToken ct = default)
    {
        var statuses = new List<ToolchainStatus>();
        foreach (var entry in _manifest.Entries)
        {
            statuses.Add(await StatusAsync(entry, ct).ConfigureAwait(false));
        }

        return statuses;
    }

    /// <summary>The status of one curated toolchain, from its probe.</summary>
    public async Task<ToolchainStatus> StatusAsync(ToolchainEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var outcome = await ProbeAsync(entry, ct).ConfigureAwait(false);
        var probe = outcome.Result;

        if (outcome.HostFailure is { } hostFailure)
        {
            // NOT "Not installed" — nothing was learned about the toolchain. See ToolchainStatus.
            return new ToolchainStatus(
                entry,
                IsInstalled: false,
                Detail: $"Could not check — this Mainguard environment could not be reached ({hostFailure})",
                CouldNotCheck: true);
        }

        if (!probe.Succeeded)
        {
            return new ToolchainStatus(entry, false, "Not installed");
        }

        if (!probe.Stdout.Contains(entry.Version, StringComparison.Ordinal))
        {
            // Present but not what is pinned. Reported as NOT installed, because what a repository
            // declaring this id would get is not what this Mainguard pinned, and quietly accepting a
            // different version is how a verification jail comes to disagree with the developer's
            // machine about what "the tests passed" means.
            return new ToolchainStatus(entry, false,
                $"A different version is present — expected {entry.Version}, the probe reported: "
                + AdapterChannel.Detail(probe, 160));
        }

        return new ToolchainStatus(entry, true, AdapterChannel.Detail(probe, 160));
    }

    /// <summary>
    /// Installs <paramref name="id"/> into the VM at the pinned version, or confirms it is already there.
    /// Idempotent: a green probe at the pinned version does nothing.
    /// </summary>
    public async Task<ToolchainStatus> InstallAsync(
        string id, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var entry = _manifest.TryGet(id)
            ?? throw new ToolchainChannelException(ToolchainChannelError.UnknownToolchain,
                $"No toolchain '{id}' is curated by this Mainguard release. Available: "
                + string.Join(", ", _manifest.KnownIds));

        var existing = await StatusAsync(entry, ct).ConfigureAwait(false);
        if (existing.IsInstalled)
        {
            return existing;
        }

        var installDir = ToolchainPaths.VmInstallDir(entry.Id, _root);
        var incoming = ToolchainPaths.VmStagingInstallDir(entry.Id, _root);
        var stagedPayload = $"{ToolchainPaths.StageDir(_root)}/{entry.Id}-{entry.Version}.tar.gz";

        // Any residue from an interrupted previous attempt. An install that extracts over a half-tree
        // is how a toolchain comes to probe green while being subtly wrong.
        await RunAsync(new[] { "rm", "-rf", incoming }, ct).ConfigureAwait(false);
        await RunAsync(
            new[] { "mkdir", "-p", ToolchainPaths.StageDir(_root), incoming, ToolchainPaths.RegistryDir(_root) },
            ct).ConfigureAwait(false);

        progress?.Report($"Downloading {entry.DisplayName} {entry.Version}…");
        var download = await RunAsync(
            new[]
            {
                "curl", "--proto", "=https", "--tlsv1.2", "-fsSL", "--retry", "3", "--retry-delay", "2",
                "-o", stagedPayload, entry.PayloadUrl,
            }, ct).ConfigureAwait(false);
        if (!download.Succeeded)
        {
            await CleanupAsync(incoming, stagedPayload, ct).ConfigureAwait(false);
            throw new ToolchainChannelException(ToolchainChannelError.DownloadFailed,
                $"{entry.DisplayName} could not be downloaded (curl exit {download.ExitCode}): "
                + AdapterChannel.Detail(download));
        }

        progress?.Report("Verifying the checksum…");
        // sha256sum prints "<hex>  <path>"; the comparison happens HERE rather than through
        // `sha256sum -c` so no shell pipeline is constructed and the failure is ours to describe.
        var sum = await RunAsync(new[] { "sha256sum", stagedPayload }, ct).ConfigureAwait(false);
        var actual = sum.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!sum.Succeeded || actual is null || !string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
        {
            await CleanupAsync(incoming, stagedPayload, ct).ConfigureAwait(false);
            throw new ToolchainChannelException(ToolchainChannelError.HashMismatch,
                $"{entry.DisplayName}'s download did not match the pinned checksum, so it was discarded and "
                + $"nothing was unpacked. Expected sha256 {entry.Sha256}, got {actual ?? "no checksum at all"}.");
        }

        progress?.Report("Unpacking…");
        var extract = await RunAsync(
            new[]
            {
                "tar", "-xzf", stagedPayload, "-C", incoming,
                "--strip-components=" + entry.StripComponents.ToString(CultureInfo.InvariantCulture),
            }, ct).ConfigureAwait(false);
        if (!extract.Succeeded)
        {
            await CleanupAsync(incoming, stagedPayload, ct).ConfigureAwait(false);
            throw new ToolchainChannelException(ToolchainChannelError.ExtractFailed,
                $"{entry.DisplayName} could not be unpacked (tar exit {extract.ExitCode}): "
                + AdapterChannel.Detail(extract));
        }

        await RunAsync(new[] { "rm", "-f", stagedPayload }, ct).ConfigureAwait(false);

        // Swap into place only now. Until this moment the previous install (if any) is untouched, so a
        // failure above leaves a working toolchain working.
        await RunAsync(new[] { "rm", "-rf", installDir }, ct).ConfigureAwait(false);
        var move = await RunAsync(new[] { "mv", incoming, installDir }, ct).ConfigureAwait(false);
        if (!move.Succeeded)
        {
            await CleanupAsync(incoming, stagedPayload, ct).ConfigureAwait(false);
            throw new ToolchainChannelException(ToolchainChannelError.ExtractFailed,
                $"{entry.DisplayName} was unpacked but could not be moved into place (exit {move.ExitCode}): "
                + AdapterChannel.Detail(move));
        }

        progress?.Report("Checking that it runs…");
        var probe = (await ProbeAsync(entry, ct).ConfigureAwait(false)).Result;
        if (!probe.Succeeded)
        {
            throw new ToolchainChannelException(ToolchainChannelError.ProbeFailed,
                $"{entry.DisplayName} was installed but does not run: its check "
                + $"({string.Join(' ', entry.ProbeCommand(ToolchainPaths.VmInstallDir(entry.Id, _root), Sandbox.PackageCachePolicy.SandboxMount))}) "
                + $"exited {probe.ExitCode} — {AdapterChannel.Detail(probe)}");
        }

        if (!probe.Stdout.Contains(entry.Version, StringComparison.Ordinal))
        {
            throw new ToolchainChannelException(ToolchainChannelError.VersionMismatch,
                $"{entry.DisplayName} installed but reported the wrong version (pinned {entry.Version}; "
                + $"it reported: {AdapterChannel.Detail(probe, 160)}).");
        }

        // LAST, and only after the toolchain has been seen to run at the pinned version.
        await _host.WriteFileAsync(
            ToolchainPaths.RegistryMarkerPath(entry.Id, _root),
            JsonSerializer.Serialize(new InstalledToolchainMarker(
                entry.Id, entry.Version, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))),
            ct).ConfigureAwait(false);

        _log?.Invoke($"toolchain installed: {entry.Id} {entry.Version}");
        return new ToolchainStatus(entry, true, AdapterChannel.Detail(probe, 160));
    }

    /// <summary>
    /// Removes an installed toolchain and its marker. The marker goes FIRST: if the directory removal
    /// only partly succeeds, what is left behind must not be something the daemon believes in.
    /// </summary>
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        var entry = _manifest.TryGet(id)
            ?? throw new ToolchainChannelException(ToolchainChannelError.UnknownToolchain,
                $"No toolchain '{id}' is curated by this Mainguard release.");

        await RunAsync(new[] { "rm", "-f", ToolchainPaths.RegistryMarkerPath(entry.Id, _root) }, ct).ConfigureAwait(false);
        var removed = await RunAsync(
            new[] { "rm", "-rf", ToolchainPaths.VmInstallDir(entry.Id, _root) }, ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            throw new ToolchainChannelException(ToolchainChannelError.RemoveFailed,
                $"{entry.DisplayName} could not be removed (exit {removed.ExitCode}): {AdapterChannel.Detail(removed)}");
        }

        _log?.Invoke($"toolchain removed: {entry.Id}");
    }

    /// <summary>What a probe attempt produced, including the case where it never ran.</summary>
    /// <param name="Result">The command result (synthetic when <paramref name="HostFailure"/> is set).</param>
    /// <param name="HostFailure">Why the probe could not be STARTED, or null when it ran.</param>
    private sealed record ProbeOutcome(AdapterCommandResult Result, string? HostFailure);

    /// <summary>
    /// Runs the entry's probe. <b>A probe that cannot be STARTED is reported, never thrown</b> — and is
    /// reported as its own outcome rather than as a failed probe.
    ///
    /// <para>Found by running the end-to-end test for the first time. The probe names the interpreter by
    /// absolute path, so before an install that path does not exist; a host that launches processes
    /// directly then throws <see cref="System.ComponentModel.Win32Exception"/> ("No such file or
    /// directory") straight out of <see cref="StatusAsync"/>. The production host happens to hide this —
    /// it shells into the VM, so a missing binary comes back as a non-zero exit from <c>wsl</c> — but
    /// relying on that is relying on an accident of one implementation, and it is the SAME code path
    /// that must report a stopped distro or a missing <c>wsl.exe</c>.</para>
    ///
    /// <para>What it costs in production is not hypothetical: <see cref="StatusAsync"/> is what
    /// <c>SandboxAgentLauncher.EnsureMountedToolchainsAsync</c> calls before every spawn, and the entire
    /// purpose of that path is to turn "toolchain missing" into a typed, human-readable refusal.</para>
    /// </summary>
    private async Task<ProbeOutcome> ProbeAsync(ToolchainEntry entry, CancellationToken ct)
    {
        var argv = entry.ProbeCommand(
            ToolchainPaths.VmInstallDir(entry.Id, _root), Sandbox.PackageCachePolicy.SandboxMount);

        try
        {
            return new ProbeOutcome(await RunAsync(argv, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            // A caller-requested cancellation is not a host failure and must propagate untouched.
            throw;
        }
        catch (Exception ex)
        {
            // 127 is the shell's "command not found", so the synthetic result reads correctly anywhere
            // it is surfaced; the REASON is what callers use to avoid claiming the toolchain is absent.
            return new ProbeOutcome(
                new AdapterCommandResult(127, string.Empty, ex.Message),
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct) =>
        _host.RunAsync(command, ct);

    private async Task CleanupAsync(string incoming, string stagedPayload, CancellationToken ct)
    {
        try
        {
            await RunAsync(new[] { "rm", "-rf", incoming }, ct).ConfigureAwait(false);
            await RunAsync(new[] { "rm", "-f", stagedPayload }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cleanup must never replace the failure it follows.
            _log?.Invoke($"toolchain cleanup after a failed install did not complete: {ex.Message}");
        }
    }
}

/// <summary>The on-disk install marker. Written only after a green, version-matched probe.</summary>
/// <param name="Id">The toolchain id.</param>
/// <param name="Version">The version proven to run.</param>
/// <param name="InstalledUtc">When it was proven.</param>
public sealed record InstalledToolchainMarker(string Id, string Version, string InstalledUtc);
