using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>One row of the macOS first-run checklist.</summary>
public partial class MacOobeStepViewModel : ViewModelBase
{
    public MacOobeStepViewModel(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _glyph = "•";      // • pending, … running, ✓ ok, ✕ failed
    [ObservableProperty] private string _stateBrushKey = "TextMuted";
    [ObservableProperty] private bool _failed;

    public void Pending(string detail = "") => Set("•", "TextMuted", detail, failed: false);
    public void Running(string detail) => Set("…", "InfoBrush", detail, failed: false);
    public void Ok(string detail) => Set("✓", "SuccessBrush", detail, failed: false);
    public void Fail(string detail) => Set("✕", "DangerBrush", detail, failed: true);

    private void Set(string glyph, string brushKey, string detail, bool failed)
    {
        Glyph = glyph;
        StateBrushKey = brushKey;
        Detail = detail;
        Failed = failed;
    }
}

/// <summary>A selectable agent CLI row in the first-run picker.</summary>
public partial class MacOobeCliRowViewModel : ViewModelBase
{
    public MacOobeCliRowViewModel(AgentCliOption option)
    {
        Id = option.Id;
        Label = $"{option.DisplayName} {option.Version}";
        IsInstalled = option.IsInstalled;
        IsSelected = option.IsInstalled;
    }

    public string Id { get; }
    public string Label { get; }
    public bool IsInstalled { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";
}

/// <summary>
/// The macos-host first-run flow — the mac analogue of the WSL OOBE, shaped by what this
/// substrate actually needs: no elevation, no VM import, no reboot-resume. Sequential checks
/// (Docker engine → file-sharing canary → jail images → daemon), then the agent-CLI picker and
/// the optional start-at-login toggle, then done. Every failure is a named, retryable row —
/// never a dead end — and Continue is gated only on the ESSENTIAL steps (engine + daemon);
/// images keep building in the background exactly like the control-center path's kick.
/// </summary>
public partial class MacOobeViewModel : ViewModelBase
{
    private readonly Action _onCompleted;
    private readonly MacDaemonController _daemon = new();

    public MacOobeViewModel(Action onCompleted)
    {
        _onCompleted = onCompleted;
        Steps.Add(EngineStep = new MacOobeStepViewModel("Docker engine"));
        Steps.Add(SharingStep = new MacOobeStepViewModel("File sharing"));
        Steps.Add(ImagesStep = new MacOobeStepViewModel("Sandbox images"));
        Steps.Add(DaemonStep = new MacOobeStepViewModel("Agent daemon"));
    }

    public ObservableCollection<MacOobeStepViewModel> Steps { get; } = new();
    public MacOobeStepViewModel EngineStep { get; }
    public MacOobeStepViewModel SharingStep { get; }
    public MacOobeStepViewModel ImagesStep { get; }
    public MacOobeStepViewModel DaemonStep { get; }

    public ObservableCollection<MacOobeCliRowViewModel> Clis { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canContinue;
    [ObservableProperty] private bool _startAtLogin;
    [ObservableProperty] private bool _cliListLoaded;

    /// <summary>Kicked by the window's Opened; retry re-enters it.</summary>
    [RelayCommand]
    public async Task RunChecksAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            var engineOk = await RunEngineStepAsync().ConfigureAwait(true);
            if (engineOk)
            {
                await RunSharingStepAsync().ConfigureAwait(true);
                _ = RunImagesStepAsync();   // minutes — deliberately not awaited (matches the
                                            // control-center path's background image kick)
                var daemonOk = await RunDaemonStepAsync().ConfigureAwait(true);
                CanContinue = daemonOk;
                if (daemonOk) _ = LoadClisAsync();
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<bool> RunEngineStepAsync()
    {
        EngineStep.Running("Looking for a Docker engine…");
        try
        {
            var (uri, source) = DockerEndpointResolver.Resolve();
            using var docker = DockerEndpointResolver.CreateClient();
            var version = await Task.Run(() => docker.System.GetVersionAsync()).ConfigureAwait(true);
            EngineStep.Ok($"Docker {version.Version} ({version.Os}/{version.Arch}) via {source}"
                + (uri is null ? "" : $" ({uri})"));
            return true;
        }
        catch (Exception ex)
        {
            EngineStep.Fail("No Docker engine answered. Install and start Docker Desktop, OrbStack "
                + $"or Colima, then retry. ({Brief(ex)})");
            return false;
        }
    }

    private async Task RunSharingStepAsync()
    {
        SharingStep.Running("Verifying the engine can reach ~/mainguard…");
        try
        {
            var root = System.IO.Path.Combine(Mainguard.Git.MainguardPaths.HomeDirectory(), "mainguard");
            System.IO.Directory.CreateDirectory(root);
            var canary = System.IO.Path.Combine(root, ".canary");
            var nonce = Guid.NewGuid().ToString("N");
            await System.IO.File.WriteAllTextAsync(canary, nonce).ConfigureAwait(true);

            // The agent-base image when present; the engine's tiny stock image otherwise (first
            // run has no jail image yet, and a ~2 MB pull is exactly what install-time is for).
            var image = await ImagePresentAsync(SandboxImageVersions.AgentBaseRef()).ConfigureAwait(true)
                ? SandboxImageVersions.AgentBaseRef()
                : "busybox:latest";
            var result = await HostCommandRunner.RunProcessAsync(
                new[] { "docker", "run", "--rm", "-v", $"{root}:{root}:ro", image, "cat", canary },
                stdin: null, CancellationToken.None).ConfigureAwait(true);

            if (result.ExitCode == 0 && result.StdOut.Contains(nonce, StringComparison.Ordinal))
                SharingStep.Ok("Containers see the substrate root (virtiofs).");
            else
                SharingStep.Fail("The engine cannot read ~/mainguard from a container. Add your "
                    + "home directory to the engine's file-sharing settings and retry. "
                    + $"(exit {result.ExitCode}: {result.StdErr.Trim()})");
        }
        catch (Exception ex)
        {
            SharingStep.Fail($"Canary failed: {Brief(ex)}");
        }
    }

    private async Task RunImagesStepAsync()
    {
        ImagesStep.Running("Checking the jail images…");
        try
        {
            var provisioner = new SandboxImageProvisioner(new HostCommandRunner());
            var needs = await provisioner.ProbeNeedsProvisionAsync(CancellationToken.None).ConfigureAwait(true);
            if (needs.Count == 0)
            {
                ImagesStep.Ok("Both jail images present and current.");
                return;
            }

            ImagesStep.Running($"Building {needs.Count} image(s) — takes minutes; you can continue…");
            await Services.SandboxImageInstaller
                .RunAsync(Editions.ProComposition.LogOobe).ConfigureAwait(true);

            var after = await provisioner.ProbeNeedsProvisionAsync(CancellationToken.None).ConfigureAwait(true);
            if (after.Count == 0) ImagesStep.Ok("Jail images built.");
            else ImagesStep.Fail($"{after.Count} image(s) still missing — see oobe.log; retry from Tools.");
        }
        catch (Exception ex)
        {
            ImagesStep.Fail($"Image provisioning failed: {Brief(ex)}");
        }
    }

    private async Task<bool> RunDaemonStepAsync()
    {
        DaemonStep.Running("Starting mainguardd…");
        try
        {
            var payload = MacDaemonController.DefaultPayloadDirectory();
            var started = await _daemon.EnsureStartedAsync(payload, CancellationToken.None).ConfigureAwait(true);
            if (!started)
            {
                DaemonStep.Fail($"This build ships no daemon payload at '{payload}' — build the "
                    + "solution (the Pro head bundles it) and retry.");
                return false;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var client = Services.DaemonClient.ForLoopback();
                    var info = await client.GetDaemonInfoAsync(CancellationToken.None).ConfigureAwait(true);
                    DaemonStep.Ok($"mainguardd {info.DaemonVersion} answering on loopback (pinned mTLS).");
                    return true;
                }
                catch when (attempt < 19)
                {
                    await Task.Delay(500).ConfigureAwait(true);
                }
            }
            DaemonStep.Fail("The daemon started but never answered — see ~/.mainguard/logs.");
            return false;
        }
        catch (Exception ex)
        {
            DaemonStep.Fail($"Daemon start failed: {Brief(ex)}");
            return false;
        }
    }

    private async Task LoadClisAsync()
    {
        try
        {
            var installer = AgentCliInstaller.CreateDefault(new ContainerAdapterInstallHost(
                AdapterPaths.DaemonSideRoot(), Mainguard.Agents.Agents.Toolchains.ToolchainPaths.DaemonSideRoot()));
            var options = await installer.ListAsync().ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Clis.Clear();
                foreach (var option in options) Clis.Add(new MacOobeCliRowViewModel(option));
                CliListLoaded = true;
            });
        }
        catch (Exception ex)
        {
            Editions.ProComposition.LogOobe($"mac oobe: CLI list failed (pick later from Tools): {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        CanContinue = false;
        try
        {
            // Install what was ticked (skippable — Tools → Agent CLIs is the add-more-later path).
            var wanted = Clis.Where(c => c.IsSelected && !c.IsInstalled).ToList();
            if (wanted.Count > 0)
            {
                var installer = AgentCliInstaller.CreateDefault(new ContainerAdapterInstallHost(
                    AdapterPaths.DaemonSideRoot(), Mainguard.Agents.Agents.Toolchains.ToolchainPaths.DaemonSideRoot()));
                foreach (var row in wanted)
                {
                    row.Status = "installing…";
                    var outcome = await installer
                        .InstallAsync(new[] { row.Id }).ConfigureAwait(true);
                    row.Status = outcome.FirstOrDefault()?.Succeeded == true ? "installed" : "failed — retry from Tools";
                }
            }

            if (StartAtLogin)
            {
                await new MacDaemonLaunchAgent()
                    .InstallAsync(MacDaemonController.DefaultPayloadDirectory()).ConfigureAwait(true);
            }

            MacOobeState.MarkCompleted();
            _onCompleted();
        }
        finally
        {
            CanContinue = true;
        }
    }

    private static async Task<bool> ImagePresentAsync(string imageRef)
    {
        var probe = await HostCommandRunner.RunProcessAsync(
            new[] { "docker", "image", "inspect", "--format", "{{.Id}}", imageRef },
            stdin: null, CancellationToken.None).ConfigureAwait(true);
        return probe.ExitCode == 0;
    }

    private static string Brief(Exception ex) => ex.GetBaseException().Message;
}
