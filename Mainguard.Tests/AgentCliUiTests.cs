using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-22 §J-5 — the agent-CLI PICKER surfaces (the OOBE step + the "add more later" settings window).
/// The service layer being right is not the feature: with no working UI the user cannot choose a CLI
/// at all. These drive the real <see cref="AgentCliInstaller"/>/<see cref="AdapterChannel"/> over fake
/// channel/host seams, so they exercise the shipped code paths minus the VM.
///
/// <para>The bar these hold (hard-won in the setup incident): never a blank spinner, never a dead end,
/// never an opaque error. Concretely — a CLI failure must NEVER block finishing setup, a failure must
/// name an actionable cause, cancel must work, and the picker must never claim something is installed
/// when it isn't.</para>
/// </summary>
public class AgentCliUiTests
{
    // ---- the OOBE step ----

    [Fact]
    public async Task InstallFailure_ShouldNotBlockFinishingSetup()
    {
        // THE point of the step's failure posture: a broken CLI install is not a broken Mainguard.
        var fx = new Fixture();
        fx.Host.FailInstall = true;
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);
        SelectAll(vm.CliOptions);
        await vm.InstallSelectedClisCommand.ExecuteAsync(null);

        var row = vm.CliOptions.Single(o => o.Id == "claude-code");
        Assert.True(row.IsFailed);
        Assert.False(row.IsInstalled);
        // The user is never stranded: the step is still leavable, and leaving lands on Done.
        Assert.True(vm.FinishCliStepCommand.CanExecute(null));
        vm.FinishCliStepCommand.Execute(null);
        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    [Fact]
    public async Task InstallFailure_ShouldNameAnActionableCause_NotAnOpaqueError()
    {
        var fx = new Fixture();
        // The channel serves bytes OTHER than the ones the manifest pinned — the MITM/corruption case
        // the sha256 pin exists to refuse.
        fx.ServedPayload = Encoding.UTF8.GetBytes("tampered-bytes");
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);
        SelectAll(vm.CliOptions);
        await vm.InstallSelectedClisCommand.ExecuteAsync(null);

        var row = vm.CliOptions.Single(o => o.Id == "claude-code");
        Assert.True(row.IsFailed);
        // A real cause AND a real next step — not "install failed".
        Assert.Contains("checksum", row.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("try again", row.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneCliFailing_ShouldNotStopTheOthersInstalling()
    {
        // Failure isolation: the whole reason installs are per-CLI rather than one batch.
        var fx = new Fixture();
        fx.Host.FailInstallFor.Add("claude-code");
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);
        SelectAll(vm.CliOptions);
        await vm.InstallSelectedClisCommand.ExecuteAsync(null);

        Assert.True(vm.CliOptions.Single(o => o.Id == "claude-code").IsFailed);
        Assert.True(vm.CliOptions.Single(o => o.Id == "codex").IsInstalled);
    }

    [Fact]
    public async Task SkippingTheStep_ShouldStillFinishSetup_WithZeroClis()
    {
        // "A user may want none yet" — the step is genuinely optional, not a nag.
        var fx = new Fixture();
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);
        Assert.True(vm.ShowSkipClis); // nothing installed → the skip affordance is the one offered
        vm.FinishCliStepCommand.Execute(null);

        Assert.Equal(OobePhase.Done, vm.Phase);
        Assert.All(vm.CliOptions, o => Assert.False(o.IsInstalled));
        Assert.Empty(fx.Host.Installed);
    }

    [Fact]
    public async Task SuccessfulInstall_ShouldMarkTheRowInstalled_AndOfferContinue()
    {
        var fx = new Fixture();
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);
        vm.CliOptions.Single(o => o.Id == "claude-code").IsSelected = true;
        await vm.InstallSelectedClisCommand.ExecuteAsync(null);

        var row = vm.CliOptions.Single(o => o.Id == "claude-code");
        Assert.True(row.IsInstalled);
        Assert.False(row.IsFailed);
        Assert.Null(row.StatusMessage);
        Assert.Contains("claude-code", fx.Host.Installed);
        // Something is installed → the footer's Accent becomes Continue, and Skip retires.
        Assert.True(vm.AnyCliInstalled);
        Assert.True(vm.ShowContinueClis);
        Assert.False(vm.ShowSkipClis);
    }

    [Fact]
    public async Task AlreadyInstalledCli_ShouldRenderAsInstalled_NotOfferedAgain()
    {
        // The picker must never lie: "installed" comes from the version-matched in-VM probe.
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);

        var row = vm.CliOptions.Single(o => o.Id == "claude-code");
        Assert.True(row.IsInstalled);
        Assert.False(row.CanSelect); // nothing to decide about it
        Assert.True(vm.CliOptions.Single(o => o.Id == "codex").CanSelect);
    }

    [Fact]
    public async Task Cancel_ShouldStopTheRun_AndLeaveTheStepUsable()
    {
        // Cancel must WORK (the incident: a spinner with no way out). The in-flight install is
        // released, the row says so, and the step stays leavable.
        var fx = new Fixture();
        var vm = fx.CreateWizardVm();
        await fx.EnterCliStepAsync(vm);
        SelectAll(vm.CliOptions);

        fx.Host.BlockInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = vm.InstallSelectedClisCommand.ExecuteAsync(null);
        await fx.Host.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        vm.CancelCliInstallCommand.Execute(null);
        fx.Host.BlockInstall.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(vm.IsInstallingClis);
        Assert.DoesNotContain(vm.CliOptions, o => o.IsInstalling); // no row stuck spinning
        vm.FinishCliStepCommand.Execute(null);
        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    [Fact]
    public async Task CatalogReadFailure_ShouldExplainAndStillAllowSkipping()
    {
        var fx = new Fixture();
        fx.Source.ThrowOnManifest = new InvalidOperationException("the Mainguard environment did not answer");
        var vm = fx.CreateWizardVm();

        await fx.EnterCliStepAsync(vm);

        Assert.True(vm.HasCliLoadError);
        Assert.Contains("did not answer", vm.CliLoadError);
        Assert.False(vm.IsCliLoading); // the "checking…" line is cleared — never a permanent spinner
        vm.FinishCliStepCommand.Execute(null);
        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    [Fact]
    public async Task CompletedProvisioning_WithNoInstallerWired_GoesStraightToDone()
    {
        // The step is additive: a build/test path with no CLI installer must not gain a dead panel.
        var fx = new Fixture();
        var vm = fx.CreateWizardVm(withInstaller: false);

        await fx.RunToCompletionAsync(vm);

        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    // ---- the settings ("add more later") surface ----

    [Fact]
    public async Task Settings_ShouldListInstalledAndInstallableClis()
    {
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.IsLoading);
        Assert.False(vm.HasLoadError);
        Assert.True(vm.Clis.Single(c => c.Id == "claude-code").IsInstalled);
        Assert.True(vm.Clis.Single(c => c.Id == "codex").CanInstall);
        // The pinned version is surfaced, not hidden — the user sees exactly what they'd get.
        Assert.Equal("v2.1.210", vm.Clis.Single(c => c.Id == "claude-code").VersionLabel);
    }

    // ---- installed-but-pin-drifted (walkthrough W6) ---------------------------------------------
    //
    // The picker asked the WRONG QUESTION: it required the probe's stdout to contain the currently
    // OFFERED version substring, i.e. "is the pinned version installed", and rendered anything else as
    // "Not installed" with an Install button. But the pin is a floor, not a ceiling — EnsureLatestAsync
    // installs the registry's current release, and an app update can move the pin under an existing
    // install — so the versions legitimately differ. Live evidence: claude-code probed 2.1.223 while
    // the bundled manifest pinned 2.1.218, and Settings called it "Not installed" while a coordinator
    // was actively running on it. The channel's own exact-match probes (idempotence, post-install
    // verification) are a different question and stay strict.

    [Fact]
    public async Task Settings_InstalledCliAheadOfThePin_ShouldReadAsInstalled_NotAsMissing()
    {
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        fx.Host.ProbeStdout["claude-code"] = "2.1.223 (Claude Code)"; // manifest pins 2.1.210
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "claude-code");
        Assert.True(row.IsInstalled);
        Assert.False(row.CanInstall); // no Install button for a CLI that is right there and working
        // The drift is an ANNOTATION on an installed row, never an absence: the chip names what
        // actually runs, and the line names both versions so the two can't look contradictory.
        Assert.Equal("2.1.223", row.InstalledVersion);
        Assert.Equal("v2.1.223", row.VersionLabel);
        Assert.True(row.HasVersionDrift);
        Assert.Contains("Installed", row.InstalledLabel, StringComparison.Ordinal);
        Assert.Contains("2.1.223", row.InstalledLabel, StringComparison.Ordinal);
        Assert.Contains("2.1.210", row.InstalledLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_InstalledCliAtThePin_ShouldNotClaimDrift()
    {
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "claude-code");
        Assert.True(row.IsInstalled);
        Assert.Equal("2.1.210", row.InstalledVersion);
        Assert.False(row.HasVersionDrift);
        Assert.Equal("Installed — verified at the pinned version", row.InstalledLabel);
    }

    [Fact]
    public async Task Settings_CliThatWasNeverInstalled_ShouldStillReadAsNotInstalled()
    {
        // The half of this that must NOT move: loosening the version match must not loosen the
        // absence check. Nothing installed → the probe exits non-zero (127 in the VM, no such file).
        var fx = new Fixture();
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.All(vm.Clis, row =>
        {
            Assert.False(row.IsInstalled);
            Assert.Null(row.InstalledVersion);
            Assert.True(row.CanInstall);
        });
    }

    [Fact]
    public async Task Settings_ProbeExitingZeroWithNoVersion_ShouldReadAsNotInstalled()
    {
        // The other half: a broken CLI whose --version somehow exits 0 but reports no version is not
        // an install. This is what stops "probe.Succeeded alone" from being the whole condition — a
        // launcher whose platform binary was never placed is the real-world shape of this.
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        fx.Host.ProbeStdout["claude-code"] = "native binary not installed";
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "claude-code");
        Assert.False(row.IsInstalled);
        Assert.Null(row.InstalledVersion);
        Assert.True(row.CanInstall);
    }

    [Theory]
    [InlineData("2.1.223 (Claude Code)", "2.1.223")]
    [InlineData("codex-cli 0.145.0", "0.145.0")]
    [InlineData("0.52.0\n", "0.52.0")]
    [InlineData("1.18.4", "1.18.4")]
    [InlineData("3.0.0-rc.2", "3.0.0-rc.2")]
    [InlineData("", null)]
    [InlineData("native binary not installed", null)]
    public void VersionToken_ShouldMatchWhatTheShippedProbesActuallyPrint(string stdout, string? expected)
        => Assert.Equal(expected, AgentCliInstaller.TryParseVersion(stdout));

    [Fact]
    public async Task Settings_InstallOne_ShouldInstallOnlyThatCli()
    {
        var fx = new Fixture();
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "codex");
        await vm.InstallCommand.ExecuteAsync(row);

        Assert.True(row.IsInstalled);
        Assert.Equal(new[] { "codex" }, fx.Host.Installed);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Settings_InstallFailure_ShouldNameTheCause_AndLeaveTheRowRetryable()
    {
        var fx = new Fixture();
        fx.Host.FailInstall = true;
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "codex");
        await vm.InstallCommand.ExecuteAsync(row);

        Assert.True(row.IsFailed);
        Assert.False(row.IsInstalled);
        Assert.Contains("could not be installed", row.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.CanInstall); // retryable in place — not a dead row
        Assert.True(vm.InstallCommand.CanExecute(row));
    }

    [Fact]
    public async Task Settings_CatalogReadFailure_ShouldExplain_NotThrow()
    {
        var fx = new Fixture();
        fx.Source.ThrowOnManifest = new InvalidOperationException("boom");
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasLoadError);
        Assert.False(vm.IsLoading);
        Assert.Contains("Refresh", vm.LoadError); // names the next step
    }

    // ---- per-row command enablement (the "visible but not clickable" bug class) -----------------
    //
    // Install/Update/Revert live on the PARENT view model but their CanExecute predicates read ROW
    // state. [NotifyCanExecuteChangedFor] fires only from the parent's own IsBusy, so a row changing
    // raised PropertyChanged (the button turned VISIBLE) while CanExecuteChanged never fired — and a
    // Button caches its last CanExecute result. These pin the NOTIFICATION, which is what was missing;
    // RowCommandEnablementTests pins the rendered Button.IsEffectivelyEnabled it produces.

    [Fact]
    public void Settings_RowGainingAnUpdate_ShouldRepublishUpdateCanExecute()
    {
        var row = new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });
        Assert.False(vm.UpdateCommand.CanExecute(row));

        var raised = 0;
        vm.UpdateCommand.CanExecuteChanged += (_, _) => raised++;
        row.UpdateAvailableVersion = "2.1.220";

        Assert.True(vm.UpdateCommand.CanExecute(row));
        Assert.True(raised > 0,
            "UpdateCommand never raised CanExecuteChanged when the row gained an update — the button "
            + "shows but stays disabled.");
    }

    [Fact]
    public void Settings_RowGainingAPreviousVersion_ShouldRepublishRevertCanExecute()
    {
        var row = new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.220", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });
        Assert.False(vm.RevertCommand.CanExecute(row));

        var raised = 0;
        vm.RevertCommand.CanExecuteChanged += (_, _) => raised++;
        row.PreviousVersion = "2.1.210";

        Assert.True(vm.RevertCommand.CanExecute(row));
        Assert.True(raised > 0, "RevertCommand never raised CanExecuteChanged when the row gained a revert target.");
    }

    [Fact]
    public void Settings_RowBecomingInstallable_ShouldRepublishInstallCanExecute()
    {
        var row = new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });
        Assert.False(vm.InstallCommand.CanExecute(row));

        var raised = 0;
        vm.InstallCommand.CanExecuteChanged += (_, _) => raised++;
        row.IsInstalled = false;

        Assert.True(vm.InstallCommand.CanExecute(row));
        Assert.True(raised > 0, "InstallCommand never raised CanExecuteChanged when the row became installable.");
    }

    [Fact]
    public async Task Settings_RowsArrivingFromRefresh_ShouldRepublishRowCommandCanExecute()
    {
        // Rows are replaced wholesale by RefreshAsync (Clear + Add). The freshly-added rows must be
        // watched too — otherwise the fix would only hold for design-constructed lists.
        var fx = new Fixture();
        fx.Host.PreInstalled.Add("claude-code");
        var vm = new AgentCliSettingsViewModel(fx.CreateInstaller());
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.Clis.Single(c => c.Id == "claude-code");
        var raised = 0;
        vm.UpdateCommand.CanExecuteChanged += (_, _) => raised++;
        row.UpdateAvailableVersion = "2.1.220";

        Assert.True(vm.UpdateCommand.CanExecute(row));
        Assert.True(raised > 0, "a row created by RefreshAsync is not watched — its Update button stays dead.");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void SelectAll(IEnumerable<AgentCliRowViewModel> rows)
    {
        foreach (var row in rows.Where(r => r.CanSelect))
            row.IsSelected = true;
    }

    /// <summary>Two pinned adapters over fake channel/host seams — the real channel policy (hash
    /// verification, staged-payload install, version-matched probe) runs unchanged.</summary>
    private sealed class Fixture
    {
        public readonly FakeSource Source = new();
        public readonly FakeHost Host = new();

        /// <summary>The bytes the manifest's sha256 pin covers.</summary>
        public byte[] Payload = Encoding.UTF8.GetBytes("the-pinned-payload-bytes");

        /// <summary>What the channel actually serves. Defaults to <see cref="Payload"/> (hash matches);
        /// set it to something else to simulate corruption/interception.</summary>
        public byte[]? ServedPayload;

        public string ManifestJson()
        {
            var sha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
            return $$"""
            {
              "adapters": [
                {
                  "id": "claude-code",
                  "displayName": "Claude Code",
                  "version": "2.1.210",
                  "provenance": "npm-registry-signature",
                  "sha256": "{{sha}}",
                  "payloadUrl": "https://registry.example.com/claude-code-2.1.210.tgz",
                  "installCmd": ["npm", "install", "-g", "{payload}"],
                  "healthProbe": { "command": ["/bin/claude-code", "--version"], "expectedVersionSubstring": "2.1.210" },
                  "launch": ["/opt/mainguard/adapters/bin/claude"]
                },
                {
                  "id": "codex",
                  "displayName": "OpenAI Codex CLI",
                  "version": "0.144.4",
                  "provenance": "npm-registry-signature",
                  "sha256": "{{sha}}",
                  "payloadUrl": "https://registry.example.com/codex-0.144.4.tgz",
                  "installCmd": ["npm", "install", "-g", "{payload}"],
                  "healthProbe": { "command": ["/bin/codex", "--version"], "expectedVersionSubstring": "0.144.4" },
                  "launch": ["/opt/mainguard/adapters/bin/codex"]
                }
              ]
            }
            """;
        }

        public AgentCliInstaller CreateInstaller()
        {
            Source.Manifest = ManifestJson();
            Source.Payload = ServedPayload ?? Payload;
            return new AgentCliInstaller(new AdapterChannel(Source, Host, new FakeCache()), Host);
        }

        public OobeWizardViewModel CreateWizardVm(bool withInstaller = true)
        {
            var machine = new Mainguard.Agents.Agents.Bootstrap.OobeStateMachine(new FakeStore());
            var diagnostics = new Mainguard.Agents.Agents.Bootstrap.SystemDiagnostics(new PassingProbe(), new ReadyWslProbe());
            var bootstrapper = new Mainguard.Agents.Agents.Bootstrap.MainguardOsBootstrapper(
                new Mainguard.Agents.Agents.Bootstrap.IBootstrapStep[] { new SatisfiedStep() });
            return new OobeWizardViewModel(
                machine, diagnostics, new AutoElevationLauncher(), bootstrapper,
                cliInstaller: withInstaller ? CreateInstaller() : null);
        }

        /// <summary>Drives the real machine to Completed (features already enabled, import satisfied).</summary>
        public async Task RunToCompletionAsync(OobeWizardViewModel vm)
        {
            var run = vm.StartCommand.ExecuteAsync(null);
            // The consent gate is armed asynchronously by the machine's EnableFeatures stage.
            for (int i = 0; i < 600 && vm.Phase != OobePhase.Consent; i++)
                await Task.Delay(10);
            vm.ConstructSandboxCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task EnterCliStepAsync(OobeWizardViewModel vm)
        {
            await RunToCompletionAsync(vm);
            Assert.Equal(OobePhase.AgentClis, vm.Phase);
        }
    }

    private sealed class FakeSource : IAdapterChannelSource
    {
        public string Manifest = "";
        public byte[] Payload = Array.Empty<byte>();
        public Exception? ThrowOnManifest;

        public Task<string> FetchManifestAsync(CancellationToken ct) =>
            ThrowOnManifest is not null ? Task.FromException<string>(ThrowOnManifest) : Task.FromResult(Manifest);

        public Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct) => Task.FromResult(Payload);
    }

    private sealed class FakeCache : IAdapterManifestCache
    {
        private string? _json;
        public string? Read() => _json;
        public void Write(string manifestJson) => _json = manifestJson;
    }

    /// <summary>Fake VM: probes report a CLI's pinned version only once it has been "installed".</summary>
    private sealed class FakeHost : IAdapterInstallHost
    {
        public readonly List<string> Installed = new();
        public readonly HashSet<string> PreInstalled = new();
        public readonly HashSet<string> FailInstallFor = new();
        public bool FailInstall;
        public TaskCompletionSource? BlockInstall;
        public readonly TaskCompletionSource InstallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static readonly Dictionary<string, string> Versions = new()
        {
            ["claude-code"] = "2.1.210",
            ["codex"] = "0.144.4",
        };

        /// <summary>What an installed CLI's <c>--version</c> prints, overriding <see cref="Versions"/>.
        /// Lets a test stand up the real-world cases the pinned default cannot: an installed copy whose
        /// version has DRIFTED from the manifest pin, and a degenerate probe that exits 0 while naming
        /// no version at all.</summary>
        public readonly Dictionary<string, string> ProbeStdout = new();

        public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            // Probe: "/bin/<id> --version".
            if (command.Count == 2 && command[1] == "--version")
            {
                var id = command[0][(command[0].LastIndexOf('/') + 1)..];
                var isUp = PreInstalled.Contains(id) || Installed.Contains(id);
                return isUp
                    ? new AdapterCommandResult(
                        0, ProbeStdout.TryGetValue(id, out var stdout) ? stdout : Versions[id], "")
                    : new AdapterCommandResult(1, "", "not installed");
            }

            // Install: the staged payload path carries "<id>-<version>".
            var staged = command.LastOrDefault(t => t.Contains("/stage/", StringComparison.Ordinal));
            var name = staged is null ? "" : staged[(staged.LastIndexOf('/') + 1)..];
            var adapterId = name.StartsWith("claude-code", StringComparison.Ordinal) ? "claude-code" : "codex";

            InstallStarted.TrySetResult();
            if (BlockInstall is not null)
                await BlockInstall.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (FailInstall || FailInstallFor.Contains(adapterId))
                return new AdapterCommandResult(243, "", "npm exited 243 (network unreachable)");

            Installed.Add(adapterId);
            return new AdapterCommandResult(0, "", "");
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct) =>
            Task.FromResult($"/home/mainguard/mainguard/adapters/stage/{fileName}");
    }

    private sealed class FakeStore : Mainguard.Agents.Agents.Bootstrap.IOobeStateStore
    {
        private Mainguard.Agents.Agents.Bootstrap.OobeState? _state;
        public Mainguard.Agents.Agents.Bootstrap.OobeState? Load() => _state;
        public void Save(Mainguard.Agents.Agents.Bootstrap.OobeState state) => _state = state;
        public void Clear() => _state = null;
    }

    private sealed class AutoElevationLauncher : Mainguard.Agents.Agents.Bootstrap.IElevationLauncher
    {
        public Task<Mainguard.Agents.Agents.Bootstrap.ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct) =>
            Task.FromResult(new Mainguard.Agents.Agents.Bootstrap.ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = false,
                ResumeTaskRegistered = false,
            });
    }

    private sealed class PassingProbe : Mainguard.Agents.Agents.Bootstrap.ISystemProbe
    {
        public System.Runtime.InteropServices.Architecture OsArchitecture =>
            System.Runtime.InteropServices.Architecture.X64;
        public Mainguard.Agents.Agents.Bootstrap.OsBuildInfo GetOsBuild() => new(true, 10, 26100);
        public Mainguard.Agents.Agents.Bootstrap.VirtualizationInfo GetVirtualization() => new(true, true);
        public long GetFreeDiskBytes() => 200L * 1024 * 1024 * 1024;
        public bool IsUserAdministrator() => true;
    }

    private sealed class ReadyWslProbe : Mainguard.Agents.Agents.Bootstrap.IWslStatusProbe
    {
        public Task<Mainguard.Agents.Agents.Bootstrap.WslStatusReport> QueryAsync(CancellationToken ct) =>
            Task.FromResult(new Mainguard.Agents.Agents.Bootstrap.WslStatusReport(
                Mainguard.Agents.Agents.Bootstrap.WslInstallState.Wsl2Ready, "2", "2.1.5", "5.15"));
    }

    private sealed class SatisfiedStep : Mainguard.Agents.Agents.Bootstrap.IBootstrapStep
    {
        public string Name => "Fake import step";
        public Task<bool> IsSatisfiedAsync(CancellationToken ct) => Task.FromResult(true);
        public Task ExecuteAsync(IProgress<string> log, CancellationToken ct) => Task.CompletedTask;
    }
}
