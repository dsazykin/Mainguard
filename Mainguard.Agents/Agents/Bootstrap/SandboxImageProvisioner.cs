using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>One provisionable sandbox image: its docker tag + the name of its bundled source dir
/// (under <c>payload/images/</c> beside the app — the <c>$(MainguardImageSources)</c> copy step).</summary>
public sealed record SandboxImageSpec(string ImageTag, string SourceDirName);

/// <summary>
/// The two CI-built jail images every real spawn needs (P2-07). Field failure 2026-07-17, twice:
/// they were built in CI and NEVER shipped to installed VMs — a fresh <c>MainguardEnv</c> import has
/// an empty docker store, and the tier-2 VM upgrade correctly does not migrate it (the image store
/// lives outside <c>/home/mainguard</c>), so the first agent spawn failed on both.
/// </summary>
public static class SandboxImages
{
    /// <summary>The hardened agent jail base image (<c>images/mainguard-agent-base/</c>). Its tag honors
    /// the <c>MAINGUARD_AGENT_IMAGE</c> override via <see cref="SandboxImageVersions.AgentBaseRef"/> so the
    /// provisioner builds/labels exactly the tag the daemon preflights (a computed property, not a
    /// cached field, so a test/env override is picked up); the bundled source dir name is fixed.</summary>
    public static SandboxImageSpec AgentBase =>
        new(SandboxImageVersions.AgentBaseRef(), SandboxImageVersions.AgentBaseName);

    /// <summary>The default-deny egress proxy image (<c>images/mainguard-egress-proxy/</c>).</summary>
    public static readonly SandboxImageSpec EgressProxy =
        new(SandboxImageVersions.EgressProxyName + ":latest", SandboxImageVersions.EgressProxyName);

    /// <summary>Both images, in the (serialized) provisioning order.</summary>
    public static IReadOnlyList<SandboxImageSpec> All => new[] { AgentBase, EgressProxy };
}

/// <summary>
/// Pure argument-list builders for the in-VM image probe/build — the automated form of the manual
/// field unblock (<c>wsl -d MainguardEnv -- docker build -t &lt;tag&gt;:latest &lt;repo&gt;/images/&lt;name&gt;</c>).
/// Kept separate from the runner (like <see cref="WslCommands"/>/<see cref="DaemonUpdateCommands"/>)
/// so the command shapes — and the G-12 invariant that everything stays scoped in-distro to
/// <c>MainguardEnv</c> and no builder ever emits the VM-wide shutdown verb — are unit-testable
/// without a process. This is a PROVISIONING-time build; G-16 forbids only agent-RUNTIME builds.
/// </summary>
public static class SandboxImageCommands
{
    /// <summary>Exit 0 iff <paramref name="imageTag"/> is present in the distro's docker store
    /// (<c>--format</c> keeps the success output to the bare image id).</summary>
    public static IReadOnlyList<string> InspectImage(string imageTag) =>
        WslCommands.InDistro("docker", "image", "inspect", "--format", "{{.Id}}", imageTag);

    /// <summary>Prints <paramref name="imageTag"/>'s <see cref="SandboxImageVersions.LabelKey"/> label
    /// value (the source hash it was built with) — <c>&lt;no value&gt;</c> for an unlabelled/old image,
    /// which the probe reads as stale. Matches the <see cref="InspectImage"/> <c>--format</c> style.</summary>
    public static IReadOnlyList<string> InspectImageLabel(string imageTag) =>
        WslCommands.InDistro(
            "docker", "image", "inspect", "--format",
            $"{{{{index .Config.Labels \"{SandboxImageVersions.LabelKey}\"}}}}", imageTag);

    /// <summary>Builds <paramref name="imageTag"/> from <paramref name="vmSourceDir"/> (the
    /// /mnt-translated bundled Windows source dir), stamping the <see cref="SandboxImageVersions.LabelKey"/>
    /// label with <paramref name="version"/> so the staleness probe + spawn preflight can key on it.
    /// Passes <c>TARGETARCH=amd64</c> explicitly: this build always runs inside the WSL2
    /// <c>MainguardEnv</c> VM (always x86_64 — arm64 is the separate macos-host substrate), and
    /// <c>TARGETARCH</c> is a BuildKit-automatic build-arg that an older, non-BuildKit-by-default
    /// docker engine (confirmed: 20.10.24+dfsg1) never populates for a plain <c>docker build</c> —
    /// left unset, <c>images/mainguard-agent-base/Dockerfile</c>'s per-arch nix-installer case
    /// statement falls to its <c>*)</c> branch and fails every build on such an engine.</summary>
    public static IReadOnlyList<string> BuildImage(string imageTag, string vmSourceDir, string version) =>
        WslCommands.InDistro(
            "docker", "build",
            "--build-arg", "TARGETARCH=amd64",
            "--label", $"{SandboxImageVersions.LabelKey}={version}",
            "-t", imageTag, vmSourceDir);

    /// <summary>Loads an image from the bundled <paramref name="tarVmPath"/> (approach B — the CI-built
    /// tar, /mnt-translated). The label rides through <c>docker save</c>/<c>load</c>, so a loaded image
    /// is version-checked identically to a built one. Provisioning-time only (G-16 forbids agent-runtime
    /// builds/loads, never provisioning).</summary>
    public static IReadOnlyList<string> LoadImage(string tarVmPath) =>
        WslCommands.InDistro("docker", "load", "-i", tarVmPath);

    /// <summary>Every builder — used by the G-12 unit test to prove none emit the VM-wide shutdown
    /// verb and all stay scoped to <c>MainguardEnv</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        InspectImage("mainguard-agent-base:latest"),
        InspectImageLabel("mainguard-agent-base:latest"),
        BuildImage(
            "mainguard-agent-base:latest",
            "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base",
            SandboxImageVersions.AgentBase),
        LoadImage("/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base.tar"),
    };
}

/// <summary>How one image's provisioning attempt ended.</summary>
public enum SandboxImageBuildKind
{
    /// <summary>The in-VM <c>docker build</c> succeeded — the image is now in the store.</summary>
    Built,

    /// <summary>The bundled CI image tar was <c>docker load</c>ed (approach B — CI bytes = VM bytes,
    /// offline, seconds not minutes). The label rides the tar, so staleness detection is identical.</summary>
    Loaded,

    /// <summary>The bundled source dir (or its Dockerfile) is absent — skipped, naming the path
    /// (mirrors the daemon-payload "no payload — skipped" posture).</summary>
    SkippedMissingSource,

    /// <summary>The build (or load) ran and failed; <see cref="SandboxImageBuildResult.Detail"/> carries
    /// the docker error tail. Never a throw — the next image is still attempted.</summary>
    BuildFailed,
}

/// <summary>One image's typed provisioning outcome (never a bare throw at the caller).</summary>
public sealed record SandboxImageBuildResult(string ImageTag, SandboxImageBuildKind Kind, string Detail);

/// <summary>Why an image needs (re)provisioning — the signal the staleness probe returns.</summary>
public enum SandboxImageProvisionReason
{
    /// <summary>Absent from the VM's docker store (a fresh import / upgraded VM).</summary>
    Missing,

    /// <summary>Present, but its <see cref="SandboxImageVersions.LabelKey"/> label ≠ the expected
    /// version constant (or it carries no label at all — an old, pre-versioning image). This is the
    /// skew signal Item 1's fourth acceptance criterion closes.</summary>
    Stale,
}

/// <summary>One image the probe found needs provisioning, tagged with why.</summary>
public sealed record SandboxImageProvisionNeed(SandboxImageSpec Image, SandboxImageProvisionReason Reason);

/// <summary>The sandbox-image provisioning seam (interface-first, per Core convention).</summary>
public interface ISandboxImageProvisioner
{
    /// <summary>Probes the distro's docker store per <see cref="SandboxImages.All"/> image and returns
    /// each that needs (re)provisioning with the reason: <see cref="SandboxImageProvisionReason.Missing"/>
    /// (inspect-id fails) or <see cref="SandboxImageProvisionReason.Stale"/> (present but its
    /// <see cref="SandboxImageVersions.LabelKey"/> label ≠ the expected version). An image we don't
    /// version (a fully-renamed <c>MAINGUARD_AGENT_IMAGE</c> override — <see cref="SandboxImageVersions.For"/>
    /// returns null) is checked for presence only.</summary>
    Task<IReadOnlyList<SandboxImageProvisionNeed>> ProbeNeedsProvisionAsync(CancellationToken ct);

    /// <summary>Builds each of <paramref name="images"/> in-VM from its bundled source under
    /// <paramref name="bundledImagesRoot"/> (a Windows host path; translated to <c>/mnt/…</c> for
    /// the in-distro build). Builds are SERIALIZED — never two at once — and one image's failure
    /// never stops the next (typed per-image results, never a throw).</summary>
    Task<IReadOnlyList<SandboxImageBuildResult>> ProvisionAsync(
        IReadOnlyList<SandboxImageSpec> images, string bundledImagesRoot,
        Action<string> log, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Performs provisioning-time sandbox-image installs over the <see cref="IWslRunner"/> seam —
/// argument lists only, never a shell string, everything scoped in-distro to <c>MainguardEnv</c>
/// (G-12). Approach B (primary): <c>docker load</c> the app-bundled CI image tar; approach A
/// (fallback): build in the VM from the app-bundled source tree, stamping the
/// <see cref="SandboxImageVersions.LabelKey"/> version label. The probe
/// (<see cref="ProbeNeedsProvisionAsync"/>) reports each image Missing OR Stale (label ≠ the
/// committed <see cref="SandboxImageVersions"/> constant), closing Item 1's version/staleness
/// criterion. Provisioning-time only — G-16 forbids <c>docker build</c>/<c>load</c> at agent-runtime.
/// </summary>
public sealed class SandboxImageProvisioner : ISandboxImageProvisioner
{
    /// <summary>Generous per-image build budget — the Dockerfiles pin apt/Nix fetches that can take
    /// minutes on a cold cache; a build past this is stuck, not slow.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Bound on the per-image presence probe (a local docker-API round trip).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly IWslRunner _wsl;

    public SandboxImageProvisioner(IWslRunner wsl)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
    }

    /// <summary>Where the packaged app ships the image sources (the MSBuild
    /// <c>$(MainguardImageSources)</c> copy step in Mainguard.Pro.App.csproj) — mirrors how
    /// <c>payload/daemon</c> is resolved.</summary>
    public static string DefaultBundledImagesDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "payload", "images");

    /// <summary>The in-VM (<c>/mnt/&lt;drive&gt;/…</c>) form of a Windows source directory — the
    /// path <c>docker build</c> reads inside MainguardEnv. Pure (reuses <see cref="HostPathTranslator"/>
    /// pinned to the Linux branch; native Linux paths — tests, CI — pass through unchanged).</summary>
    public static string ToVmPath(string hostSourceDirectory) =>
        HostPathTranslator.ToDaemonOpenablePath(hostSourceDirectory, daemonIsWindows: false);

    public async Task<IReadOnlyList<SandboxImageProvisionNeed>> ProbeNeedsProvisionAsync(CancellationToken ct)
    {
        var needs = new List<SandboxImageProvisionNeed>();
        foreach (var image in SandboxImages.All)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            // 1) Presence: inspect-id fails ⇒ the image is absent from the store.
            var present = await _wsl
                .RunAsync(SandboxImageCommands.InspectImage(image.ImageTag), stdin: null, timeout.Token)
                .ConfigureAwait(false);
            if (!present.Succeeded)
            {
                needs.Add(new SandboxImageProvisionNeed(image, SandboxImageProvisionReason.Missing));
                continue;
            }

            // 2) Version: an image we don't version (a renamed override) is presence-only.
            var expected = SandboxImageVersions.For(image.ImageTag);
            if (expected is null)
            {
                continue;
            }

            // The installed label vs the expected constant; an unlabelled/old image prints
            // "<no value>" (or the inspect fails), either way ≠ expected ⇒ stale.
            var label = await _wsl
                .RunAsync(SandboxImageCommands.InspectImageLabel(image.ImageTag), stdin: null, timeout.Token)
                .ConfigureAwait(false);
            var installed = label.Succeeded ? label.StdOut.Trim() : string.Empty;
            if (!string.Equals(installed, expected, StringComparison.Ordinal))
            {
                needs.Add(new SandboxImageProvisionNeed(image, SandboxImageProvisionReason.Stale));
            }
        }

        return needs;
    }

    public async Task<IReadOnlyList<SandboxImageBuildResult>> ProvisionAsync(
        IReadOnlyList<SandboxImageSpec> images, string bundledImagesRoot,
        Action<string> log, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledImagesRoot);
        ArgumentNullException.ThrowIfNull(log);

        // Strictly serialized: one docker build at a time (two concurrent cold builds would fight
        // over the VM's CPU/network for no gain), and one failure never stops the next image.
        var results = new List<SandboxImageBuildResult>(images.Count);
        foreach (var image in images)
        {
            var result = await BuildOneAsync(image, bundledImagesRoot, progress, ct).ConfigureAwait(false);
            log($"sandbox images: {image.ImageTag} — {result.Kind}: {result.Detail}");
            results.Add(result);
        }

        return results;
    }

    private async Task<SandboxImageBuildResult> BuildOneAsync(
        SandboxImageSpec image, string bundledImagesRoot, IProgress<string>? progress, CancellationToken ct)
    {
        // Approach B first: a CI-built <name>.tar beside the sources ⇒ docker load (CI bytes = VM
        // bytes, offline, seconds). Absent (dev / size-trimmed builds) ⇒ fall back to the in-VM
        // build from the bundled source tree (approach A). Neither present ⇒ a typed skip.
        var tarPath = Path.Combine(bundledImagesRoot, image.SourceDirName + ".tar");
        if (File.Exists(tarPath))
        {
            return await LoadFromTarAsync(image, tarPath, progress, ct).ConfigureAwait(false);
        }

        var sourceDir = Path.Combine(bundledImagesRoot, image.SourceDirName);
        if (File.Exists(Path.Combine(sourceDir, "Dockerfile")))
        {
            return await BuildFromSourceAsync(image, sourceDir, progress, ct).ConfigureAwait(false);
        }

        return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.SkippedMissingSource,
            $"no bundled image tar at '{tarPath}' or source at '{sourceDir}' — skipped");
    }

    /// <summary>Approach B: <c>docker load</c> the bundled CI tar. The version label rides the tar, so
    /// no version is passed here — the probe/preflight read it back identically to a built image.</summary>
    private async Task<SandboxImageBuildResult> LoadFromTarAsync(
        SandboxImageSpec image, string tarPath, IProgress<string>? progress, CancellationToken ct)
    {
        var vmTarPath = ToVmPath(tarPath);
        progress?.Report($"Loading {image.ImageTag} from {vmTarPath}…");
        var failure = await TryRunStepAsync(image, SandboxImageCommands.LoadImage(vmTarPath), "docker load", ct)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        progress?.Report($"Loaded {image.ImageTag}.");
        return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.Loaded, $"loaded from '{vmTarPath}'");
    }

    /// <summary>Approach A (fallback): in-VM <c>docker build</c>, stamping the expected version label so
    /// a source-built image is version-checkable exactly like a loaded one.</summary>
    private async Task<SandboxImageBuildResult> BuildFromSourceAsync(
        SandboxImageSpec image, string sourceDir, IProgress<string>? progress, CancellationToken ct)
    {
        var vmSourceDir = ToVmPath(sourceDir);
        var version = SandboxImageVersions.For(image.ImageTag) ?? string.Empty;
        progress?.Report($"Building {image.ImageTag} from {vmSourceDir}…");
        var failure = await TryRunStepAsync(
                image, SandboxImageCommands.BuildImage(image.ImageTag, vmSourceDir, version), "docker build", ct)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        progress?.Report($"Built {image.ImageTag}.");
        return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.Built, $"built from '{vmSourceDir}'");
    }

    /// <summary>Runs one provisioning docker step (build or load) under the build budget, mapping a
    /// non-zero exit / timeout / fault to a typed <see cref="SandboxImageBuildKind.BuildFailed"/>
    /// result. Returns <c>null</c> on success (the caller composes the success result). Per-image
    /// isolation: e.g. a wsl.exe fault becomes a typed result, never a throw (only caller
    /// cancellation propagates).</summary>
    private async Task<SandboxImageBuildResult?> TryRunStepAsync(
        SandboxImageSpec image, IReadOnlyList<string> argv, string verb, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BuildTimeout);
        try
        {
            var result = await _wsl.RunAsync(argv, stdin: null, timeout.Token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed,
                    $"{verb} exited {result.ExitCode}: {ErrorTail(result)}");
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation (app shutdown) — not a build outcome
        }
        catch (OperationCanceledException)
        {
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed,
                $"{verb} timed out after {BuildTimeout.TotalMinutes:0} minutes");
        }
        catch (Exception ex)
        {
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed, ex.Message);
        }
    }

    /// <summary>The last few meaningful lines of the docker error output — enough to act on,
    /// small enough for one oobe.log breadcrumb.</summary>
    private static string ErrorTail(WslRunResult result)
    {
        var raw = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = raw
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        return lines.Length == 0 ? "(no docker output)" : string.Join(" | ", lines.TakeLast(5));
    }
}

/// <summary>How one <see cref="SandboxImageAutoProvision.RunAsync"/> attempt ended — the typed form
/// of the oobe.log breadcrumb, for the App's startup toast.</summary>
public enum SandboxImageProvisionOutcomeKind
{
    /// <summary>Both images are already in the VM's docker store; nothing was touched.</summary>
    AllPresent,

    /// <summary>Images are missing but the app ships no bundled sources — skipped.</summary>
    SkippedNoBundledSources,

    /// <summary>Every missing image was built/loaded — the first spawn will find them.</summary>
    Installed,

    /// <summary>At least one STALE (version-skewed) image was rebuilt/reloaded to the current version —
    /// the skew Item 1's fourth criterion detects, now auto-repaired.</summary>
    Updated,

    /// <summary>At least one build/load failed (details in oobe.log; the spawn preflight names the repair).</summary>
    InstallFailed,

    /// <summary>An unexpected fault in the flow itself (never thrown at the caller).</summary>
    Faulted,
}

/// <summary>One typed provisioning outcome (the callback payload of <see cref="SandboxImageAutoProvision.RunAsync"/>).</summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="Detail">The same human-readable text the oobe.log breadcrumb carries.</param>
public sealed record SandboxImageProvisionOutcome(SandboxImageProvisionOutcomeKind Kind, string Detail);

/// <summary>A composed startup-toast payload (proto- and UI-free; the App binds it to its toast host).</summary>
/// <param name="Message">The one-line toast text (Voice Bible pattern T: past tense, names the object).</param>
/// <param name="IsWarning">True for the failed-install warning tone; false for the quiet success pill.</param>
public sealed record SandboxImageToastContent(string Message, bool IsWarning);

/// <summary>
/// The outcome → toast policy: only an attempt that actually CHANGED something (or tried and
/// failed) earns a toast. All-present, no-sources, and internal faults stay silent — a startup
/// pill for "nothing happened" is noise. Pure so the trigger rule is unit-testable without Avalonia.
/// </summary>
public static class SandboxImageToast
{
    public static SandboxImageToastContent? TryCompose(SandboxImageProvisionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            SandboxImageProvisionOutcomeKind.Installed => new SandboxImageToastContent(
                "Sandbox images installed.", IsWarning: false),
            SandboxImageProvisionOutcomeKind.Updated => new SandboxImageToastContent(
                "Sandbox images updated.", IsWarning: false),
            SandboxImageProvisionOutcomeKind.InstallFailed => new SandboxImageToastContent(
                "Sandbox image install didn't complete — details in oobe.log.", IsWarning: true),
            _ => null,
        };
    }
}

/// <summary>
/// The one call the App makes at control-center startup (in the same background task as the
/// tier-1/tier-2 daemon checks, sequenced after them): probe which jail images are missing OR
/// version-stale, (re)provision each (load the bundled CI tar, else build) from the bundled sources,
/// log every outcome — and never throw. Silent when everything is present and current; a probe that
/// cannot run (docker/VM down) is a logged skip, and the daemon-side spawn preflight
/// (<c>SandboxImageMissingException</c>) remains the actionable backstop.
/// </summary>
public static class SandboxImageAutoProvision
{
    /// <param name="provisioner">The probe/build performer (fake in tests).</param>
    /// <param name="bundledImagesRoot">The app-shipped image-sources dir (Windows host path).</param>
    /// <param name="log">Outcome breadcrumbs (the App passes its oobe.log writer).</param>
    /// <param name="onOutcome">Optional typed-outcome callback (the App's startup toast). Invoked at
    /// most once, after the outcome is logged, on the caller's thread — never on cancellation, and a
    /// throwing callback is swallowed (this flow must never ripple into the app).</param>
    /// <param name="progress">Optional per-step progress lines (in addition to the log).</param>
    /// <param name="force">When true, skip the probe and (re)provision EVERY image — the user-triggered
    /// "Rebuild sandbox images" repair (Tools menu), recovery when auto-repair keeps failing.</param>
    public static async Task RunAsync(
        ISandboxImageProvisioner provisioner,
        string bundledImagesRoot,
        Action<string> log,
        CancellationToken ct,
        Action<SandboxImageProvisionOutcome>? onOutcome = null,
        IProgress<string>? progress = null,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledImagesRoot);
        ArgumentNullException.ThrowIfNull(log);

        // Log first, then report — and never let a throwing callback masquerade as a flow fault.
        void Report(SandboxImageProvisionOutcomeKind kind, string detail)
        {
            try
            {
                onOutcome?.Invoke(new SandboxImageProvisionOutcome(kind, detail));
            }
            catch (Exception)
            {
                // The outcome consumer is cosmetic (a toast); its failure never ripples back.
            }
        }

        try
        {
            // Force = a manual rebuild of every image (marked Stale so the outcome reads "updated");
            // otherwise probe for the missing/stale set.
            var needs = force
                ? SandboxImages.All
                    .Select(i => new SandboxImageProvisionNeed(i, SandboxImageProvisionReason.Stale))
                    .ToList()
                : await provisioner.ProbeNeedsProvisionAsync(ct).ConfigureAwait(false);
            if (needs.Count == 0)
            {
                var present = "sandbox images: "
                    + string.Join(", ", SandboxImages.All.Select(i => i.ImageTag))
                    + " present and current — nothing to do";
                log(present);
                Report(SandboxImageProvisionOutcomeKind.AllPresent, present);
                return;
            }

            var needNames = string.Join(", ", needs.Select(n => $"{n.Image.ImageTag} ({n.Reason})"));
            if (!Directory.Exists(bundledImagesRoot))
            {
                var noSources = $"sandbox images: {needNames} need provisioning but no bundled sources at "
                    + $"'{bundledImagesRoot}' — skipped";
                log(noSources);
                Report(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, noSources);
                return;
            }

            log($"sandbox images: {needNames} — provisioning from '{bundledImagesRoot}'");
            var results = await provisioner
                .ProvisionAsync(needs.Select(n => n.Image).ToList(), bundledImagesRoot, log, progress, ct)
                .ConfigureAwait(false);

            var failed = results.Where(r => r.Kind == SandboxImageBuildKind.BuildFailed).ToArray();
            if (failed.Length > 0)
            {
                var detail = "sandbox images: install FAILED for "
                    + string.Join(", ", failed.Select(r => r.ImageTag));
                log(detail);
                Report(SandboxImageProvisionOutcomeKind.InstallFailed, detail);
                return;
            }

            var provisioned = results
                .Where(r => r.Kind is SandboxImageBuildKind.Built or SandboxImageBuildKind.Loaded)
                .ToArray();
            if (provisioned.Length == 0)
            {
                // Every needing image was a per-image source skip (partial/empty payload dir).
                var detail = $"sandbox images: {needNames} need provisioning but their bundled sources are absent — skipped";
                log(detail);
                Report(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, detail);
                return;
            }

            // A repaired STALE image reads as "updated"; a purely-missing batch reads as "installed"
            // (a mix counts as updated — something was brought forward from a prior version).
            var staleTags = needs
                .Where(n => n.Reason == SandboxImageProvisionReason.Stale)
                .Select(n => n.Image.ImageTag)
                .ToHashSet(StringComparer.Ordinal);
            var anyUpdated = provisioned.Any(r => staleTags.Contains(r.ImageTag));
            var kind = anyUpdated
                ? SandboxImageProvisionOutcomeKind.Updated
                : SandboxImageProvisionOutcomeKind.Installed;
            var summary = $"sandbox images: {(anyUpdated ? "updated" : "installed")} "
                + string.Join(", ", provisioned.Select(r => r.ImageTag));
            log(summary);
            Report(kind, summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown mid-provisioning — nothing to log (and no outcome: nobody is listening).
        }
        catch (Exception ex)
        {
            // A failed install must never crash (or even ripple into) the app.
            log($"sandbox images: provisioning FAILED: {ex.Message}");
            Report(SandboxImageProvisionOutcomeKind.Faulted, ex.Message);
        }
    }
}
