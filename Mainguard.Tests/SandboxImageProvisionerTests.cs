using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Sandbox-image provisioning (field failure 2026-07-17, twice: the CI-built jail images never reach
/// installed VMs — a fresh MainguardEnv import and the tier-2 upgrade both leave an empty docker store,
/// so the first spawn fails). Covers the in-distro probe (presence + version-label staleness), the
/// exact /mnt-translated build/load argv (G-12 distro-scoped, never a VM-wide verb), the
/// <c>--label mainguard.image.version=…</c> stamp, the serialized build order, the load-else-build
/// preference + fallback, per-image failure isolation, the missing-bundled-sources skip, and the
/// <see cref="SandboxImageAutoProvision"/> orchestration + toast policy (installed vs updated) — all
/// over a fake <see cref="IWslRunner"/>, never real docker.
/// </summary>
public class SandboxImageProvisionerTests : IDisposable
{
    private readonly string _tempRoot;

    public SandboxImageProvisionerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gl-imgprov-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Creates <c>&lt;root&gt;/&lt;name&gt;/Dockerfile</c> for the given specs and returns the root.</summary>
    private string BundledSources(params SandboxImageSpec[] specs)
    {
        Directory.CreateDirectory(_tempRoot);
        foreach (var spec in specs)
        {
            var dir = Path.Combine(_tempRoot, spec.SourceDirName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM scratch\n");
        }

        return _tempRoot;
    }

    // ---- argv predicates (the probe now issues an id-inspect then a label-inspect per image) -----

    private static bool IsIdInspect(IReadOnlyList<string> args) => args.Contains("{{.Id}}");

    private static bool IsLabelInspect(IReadOnlyList<string> args) =>
        args.Any(a => a.Contains("index .Config.Labels", StringComparison.Ordinal));

    private static string ExpectedLabelFor(IReadOnlyList<string> args) =>
        args.Contains(SandboxImages.EgressProxy.ImageTag)
            ? SandboxImageVersions.EgressProxy
            : SandboxImageVersions.AgentBase;

    // ---- /mnt path translation ----------------------------------------------------------------

    [Fact]
    public void ToVmPath_TranslatesTheWindowsSourceDir_ToItsDrvfsForm()
    {
        Assert.Equal(
            "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base",
            SandboxImageProvisioner.ToVmPath(@"C:\Program Files\Mainguard\payload\images\mainguard-agent-base"));
    }

    [Fact]
    public void ToVmPath_PassesNativeLinuxPathsThrough()
    {
        Assert.Equal("/tmp/payload/images", SandboxImageProvisioner.ToVmPath("/tmp/payload/images"));
    }

    // ---- Command shapes (G-12: distro-scoped, never a VM-wide verb) ---------------------------

    [Fact]
    public void BuildImage_EmitsTheExactManualFieldUnblockArgv_WithTheVersionLabel()
    {
        Assert.Equal(
            new[]
            {
                "-d", "MainguardEnv", "--", "docker", "build",
                "--build-arg", "TARGETARCH=amd64",
                "--label", "mainguard.image.version=" + SandboxImageVersions.AgentBase,
                "-t", "mainguard-agent-base:latest",
                "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base",
            },
            SandboxImageCommands.BuildImage(
                "mainguard-agent-base:latest",
                "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base",
                SandboxImageVersions.AgentBase));
    }

    [Fact]
    public void InspectImageLabel_EmitsTheLabelReadArgv()
    {
        Assert.Equal(
            new[]
            {
                "-d", "MainguardEnv", "--", "docker", "image", "inspect", "--format",
                "{{index .Config.Labels \"mainguard.image.version\"}}", "mainguard-agent-base:latest",
            },
            SandboxImageCommands.InspectImageLabel("mainguard-agent-base:latest"));
    }

    [Fact]
    public void LoadImage_EmitsTheDockerLoadArgv()
    {
        Assert.Equal(
            new[]
            {
                "-d", "MainguardEnv", "--", "docker", "load", "-i",
                "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base.tar",
            },
            SandboxImageCommands.LoadImage(
                "/mnt/c/Program Files/Mainguard/payload/images/mainguard-agent-base.tar"));
    }

    [Fact]
    public void AllBuilders_AreDistroScoped_AndNeverEmitTheVmWideShutdownVerb()
    {
        foreach (var builder in SandboxImageCommands.AllBuilders())
        {
            Assert.Equal(new[] { "-d", "MainguardEnv", "--" }, builder.Take(3));
            Assert.DoesNotContain("--shutdown", builder);
        }
    }

    // ---- Probe (presence + staleness) ---------------------------------------------------------

    [Fact]
    public async Task Probe_ClassifiesMissingVsStale_ViaPresenceThenLabel()
    {
        // agent-base: present (id ok) but its label ≠ expected → Stale.
        // egress-proxy: absent (id inspect fails) → Missing (never label-probed).
        var wsl = new RecordingWslRunner
        {
            Responder = args =>
            {
                if (IsIdInspect(args))
                {
                    return args.Contains(SandboxImages.EgressProxy.ImageTag)
                        ? new WslRunResult(1, "", "Error: No such image: mainguard-egress-proxy:latest")
                        : new WslRunResult(0, "sha256:abc", "");
                }

                if (IsLabelInspect(args))
                {
                    return new WslRunResult(0, "an-old-source-hash", ""); // agent-base's stale label
                }

                return new WslRunResult(0, "", "");
            },
        };

        var needs = await new SandboxImageProvisioner(wsl).ProbeNeedsProvisionAsync(CancellationToken.None);

        Assert.Equal(2, needs.Count);
        Assert.Equal(
            SandboxImages.AgentBase,
            needs.Single(n => n.Reason == SandboxImageProvisionReason.Stale).Image);
        Assert.Equal(
            SandboxImages.EgressProxy,
            needs.Single(n => n.Reason == SandboxImageProvisionReason.Missing).Image);
    }

    [Fact]
    public async Task Probe_PresentAndCurrent_YieldsNoNeeds()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => IsLabelInspect(args)
                ? new WslRunResult(0, ExpectedLabelFor(args), "")
                : new WslRunResult(0, "sha256:abc", ""),
        };

        var needs = await new SandboxImageProvisioner(wsl).ProbeNeedsProvisionAsync(CancellationToken.None);

        Assert.Empty(needs);
    }

    [Fact]
    public async Task Probe_UnlabelledOldImage_IsStale()
    {
        // Present, but inspect prints "<no value>" for a missing label (an old, pre-versioning image).
        var wsl = new RecordingWslRunner
        {
            Responder = args => IsLabelInspect(args)
                ? new WslRunResult(0, "<no value>", "")
                : new WslRunResult(0, "sha256:abc", ""),
        };

        var needs = await new SandboxImageProvisioner(wsl).ProbeNeedsProvisionAsync(CancellationToken.None);

        Assert.All(needs, n => Assert.Equal(SandboxImageProvisionReason.Stale, n.Reason));
        Assert.Equal(SandboxImages.All.Count, needs.Count);
    }

    // ---- Provision (build/load) ---------------------------------------------------------------

    [Fact]
    public async Task Provision_BuildsSerialized_InDeclaredOrder_WithTheLabelledTranslatedSourcePath()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            SandboxImages.All, root, _ => { }, progress: null, CancellationToken.None);

        Assert.All(results, r => Assert.Equal(SandboxImageBuildKind.Built, r.Kind));
        Assert.Equal(1, wsl.MaxConcurrency); // never two builds in flight at once
        Assert.Equal(
            new[]
            {
                new[]
                {
                    "-d", "MainguardEnv", "--", "docker", "build",
                    "--build-arg", "TARGETARCH=amd64",
                    "--label", "mainguard.image.version=" + SandboxImageVersions.AgentBase,
                    "-t", "mainguard-agent-base:latest",
                    SandboxImageProvisioner.ToVmPath(Path.Combine(root, "mainguard-agent-base")),
                },
                new[]
                {
                    "-d", "MainguardEnv", "--", "docker", "build",
                    "--build-arg", "TARGETARCH=amd64",
                    "--label", "mainguard.image.version=" + SandboxImageVersions.EgressProxy,
                    "-t", "mainguard-egress-proxy:latest",
                    SandboxImageProvisioner.ToVmPath(Path.Combine(root, "mainguard-egress-proxy")),
                },
            },
            wsl.Calls);
    }

    [Fact]
    public async Task Provision_PrefersBundledTar_DockerLoad_OverSourceBuild()
    {
        // Both a <name>.tar AND a source dir exist for agent-base; the tar wins (load, not build).
        Directory.CreateDirectory(Path.Combine(_tempRoot, "mainguard-agent-base"));
        File.WriteAllText(Path.Combine(_tempRoot, "mainguard-agent-base", "Dockerfile"), "FROM scratch\n");
        File.WriteAllText(Path.Combine(_tempRoot, "mainguard-agent-base.tar"), "fake-tar-bytes");
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            new[] { SandboxImages.AgentBase }, _tempRoot, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.Loaded, results[0].Kind);
        Assert.Equal(
            new[]
            {
                "-d", "MainguardEnv", "--", "docker", "load", "-i",
                SandboxImageProvisioner.ToVmPath(Path.Combine(_tempRoot, "mainguard-agent-base.tar")),
            },
            wsl.Calls.Single());
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("build"));
    }

    [Fact]
    public async Task Provision_NoTar_FallsBackToSourceBuild()
    {
        var root = BundledSources(SandboxImages.AgentBase); // Dockerfile only, no .tar
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            new[] { SandboxImages.AgentBase }, root, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.Built, results[0].Kind);
        Assert.Contains(wsl.Calls, args => args.Contains("build"));
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("load"));
    }

    [Fact]
    public async Task Provision_OneImagesFailure_NeverStopsTheNext_AndCarriesTheDockerErrorTail()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("mainguard-agent-base:latest")
                ? new WslRunResult(1, "", "Step 3/9 : RUN apt-get update\nERROR: failed to solve: no route to host")
                : new WslRunResult(0, "", ""),
        };

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            SandboxImages.All, root, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.BuildFailed, results[0].Kind);
        Assert.Equal("mainguard-agent-base:latest", results[0].ImageTag);
        Assert.Contains("failed to solve: no route to host", results[0].Detail);
        Assert.Equal(SandboxImageBuildKind.Built, results[1].Kind);
        Assert.Equal(2, wsl.Calls.Count); // the second build still ran
    }

    [Fact]
    public async Task Provision_MissingBundledSource_IsATypedSkipNamingThePath_NoBuildIssued()
    {
        Directory.CreateDirectory(_tempRoot); // root exists, but carries no image source dirs or tars
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            new[] { SandboxImages.AgentBase }, _tempRoot, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.SkippedMissingSource, results[0].Kind);
        Assert.Contains(Path.Combine(_tempRoot, "mainguard-agent-base"), results[0].Detail);
        Assert.Empty(wsl.Calls);
    }

    // ---- The startup orchestration + toast policy ---------------------------------------------

    [Fact]
    public async Task AutoProvision_AllPresentAndCurrent_IsSilent_NothingBuilt_NoToast()
    {
        var wsl = new RecordingWslRunner
        {
            // Every id-inspect present; every label-inspect returns the expected version.
            Responder = args => IsLabelInspect(args)
                ? new WslRunResult(0, ExpectedLabelFor(args), "")
                : new WslRunResult(0, "sha256:abc", ""),
        };
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), BundledSources(), log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.AllPresent, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("build"));
        Assert.Contains(log, l => l.Contains("nothing to do"));
    }

    [Fact]
    public async Task AutoProvision_MissingWithBundledSources_Installs_AndComposesTheSuccessToast()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            // Both id-inspects miss (→ Missing, no label probe); both builds succeed.
            Responder = args => IsIdInspect(args) ? new WslRunResult(1, "", "no such image") : new WslRunResult(0, "", ""),
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Installed, outcome!.Kind);
        var toast = SandboxImageToast.TryCompose(outcome);
        Assert.Equal("Sandbox images installed.", toast!.Message);
        Assert.False(toast.IsWarning);
    }

    [Fact]
    public async Task AutoProvision_StaleImages_Rebuild_AndComposeTheUpdatedToast()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            // Present but wrong label → Stale; the source builds then succeed.
            Responder = args =>
            {
                if (IsIdInspect(args))
                {
                    return new WslRunResult(0, "sha256:abc", "");
                }

                if (IsLabelInspect(args))
                {
                    return new WslRunResult(0, "an-old-source-hash", "");
                }

                return new WslRunResult(0, "", "");
            },
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Updated, outcome!.Kind);
        var toast = SandboxImageToast.TryCompose(outcome);
        Assert.Equal("Sandbox images updated.", toast!.Message);
        Assert.False(toast.IsWarning);
    }

    [Fact]
    public async Task AutoProvision_Force_RebuildsEveryImage_SkippingTheProbe_UpdatedToast()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        // Even though every image would probe as present-and-current, force must rebuild anyway.
        var wsl = new RecordingWslRunner
        {
            Responder = args => IsLabelInspect(args)
                ? new WslRunResult(0, ExpectedLabelFor(args), "")
                : new WslRunResult(0, "sha256:abc", ""),
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o, force: true);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Updated, outcome!.Kind);
        Assert.Equal("Sandbox images updated.", SandboxImageToast.TryCompose(outcome)!.Message);
        // The probe was skipped entirely — only the two builds ran.
        Assert.DoesNotContain(wsl.Calls, IsIdInspect);
        Assert.DoesNotContain(wsl.Calls, IsLabelInspect);
        Assert.Equal(2, wsl.Calls.Count(args => args.Contains("build")));
    }

    [Fact]
    public async Task AutoProvision_BuildFailure_IsAWarningToastPointingAtOobeLog()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            Responder = args => IsIdInspect(args)
                ? new WslRunResult(1, "", "no such image")
                : new WslRunResult(1, "", "ERROR: failed to solve"),
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.InstallFailed, outcome!.Kind);
        var toast = SandboxImageToast.TryCompose(outcome);
        Assert.True(toast!.IsWarning);
        Assert.Contains("oobe.log", toast.Message);
    }

    [Fact]
    public async Task AutoProvision_MissingWithoutBundledSources_SkipsSilently_NamingThePath()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = _ => new WslRunResult(1, "", "no such image"),
        };
        var missingRoot = Path.Combine(_tempRoot, "payload", "images"); // never created
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), missingRoot, log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.Contains(log, l => l.Contains(missingRoot));
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("build"));
    }

    [Fact]
    public async Task AutoProvision_ProbeFault_NeverThrows_LogsAndStaysToastSilent()
    {
        var wsl = new RecordingWslRunner
        {
            Thrower = _ => new InvalidOperationException("wsl.exe vanished"),
        };
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), BundledSources(), log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Faulted, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.Contains(log, l => l.Contains("wsl.exe vanished"));
    }

    // ---- fake runner --------------------------------------------------------------------------

    /// <summary>Records every argv; scripted via <see cref="Responder"/> (default: everything
    /// succeeds) or <see cref="Thrower"/>. Tracks concurrent entries to prove serialization.</summary>
    private sealed class RecordingWslRunner : IWslRunner
    {
        private int _inFlight;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public int MaxConcurrency { get; private set; }

        public Func<IReadOnlyList<string>, WslRunResult>? Responder { get; init; }

        public Func<IReadOnlyList<string>, Exception>? Thrower { get; init; }

        public async Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                MaxConcurrency = Math.Max(MaxConcurrency, now);
                lock (Calls)
                {
                    Calls.Add(args.ToArray());
                }

                await Task.Yield();
                if (Thrower is not null)
                {
                    throw Thrower(args);
                }

                return Responder?.Invoke(args) ?? new WslRunResult(0, "", "");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
