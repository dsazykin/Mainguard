using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.Editions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Settings → Agent Jails (2026-09-04): reachable from the rail under the agent platform only, resolves to
/// a real page, and everything it edits goes to the daemon — a save re-renders from what the daemon
/// persisted (clamped), and a refused save is an error on the page, never a silent success.
/// </summary>
public sealed class JailLimitsSettingsPageTests
{
    [Fact]
    public void SettingsRail_UnderTheAgentPlatform_OffersTheAgentJailsPage()
    {
        var row = Assert.Single(BuildSettings(hasAgentPlatform: true).Pages, p => p.Id == "AgentJails");
        Assert.Equal("Agent Jails", row.Label);
        Assert.IsType<JailLimitsSettingsViewModel>(row.Content);
    }

    [Fact]
    public void SettingsRail_WithoutTheAgentPlatform_OmitsIt()
    {
        Assert.DoesNotContain(BuildSettings(hasAgentPlatform: false).Pages, p => p.Id == "AgentJails");
    }

    [Fact]
    public void ThePageViewModel_ResolvesToARealUserControl()
    {
        var viewName = typeof(JailLimitsSettingsViewModel).FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var view = typeof(JailLimitsSettingsViewModel).Assembly.GetType(viewName);
        Assert.NotNull(view);
        Assert.True(typeof(Avalonia.Controls.UserControl).IsAssignableFrom(view));
    }

    [Fact]
    public void ProToolsSurface_BuildsThePage_OverTheComposedGateway()
    {
        var previous = ProComposition.JailLimitsGatewayFactory;
        ProComposition.JailLimitsGatewayFactory = () => new RecordingGateway();
        try
        {
            Assert.IsType<JailLimitsSettingsViewModel>(new ProToolsSurface().CreateJailLimitsPage());
        }
        finally
        {
            ProComposition.JailLimitsGatewayFactory = previous;
        }
    }

    [Fact]
    public async Task Load_RendersTheDaemonsCeiling_AndSave_RendersWhatItPersisted()
    {
        var gateway = new RecordingGateway { MemoryGiB = 3, Cpus = 1.5, IsDefault = false };
        var vm = new JailLimitsSettingsViewModel(gateway);
        await vm.LoadAsync();

        Assert.True(vm.IsLoaded);
        Assert.Equal(3, vm.MemoryGiB);
        Assert.Equal(1.5, vm.Cpus);
        Assert.False(vm.IsDefault);
        Assert.Equal(0.5, vm.MinMemoryGiB);
        Assert.Equal(64, vm.MaxCpus);

        vm.MemoryGiB = 0.1; // below the band: the daemon clamps, and the page shows the clamped value
        vm.Cpus = 4;
        await vm.SaveAsync();

        Assert.Equal((0.1, 4d), gateway.LastSave);
        Assert.Equal(0.5, vm.MemoryGiB);
        Assert.Equal(4, vm.Cpus);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.##} GiB", 0.5), vm.StatusMessage);
        Assert.Contains("jails started from now on", vm.StatusMessage);
    }

    [Fact]
    public async Task ARefusedSave_IsAnErrorOnThePage_NotASilentSuccess()
    {
        var gateway = new RecordingGateway();
        var vm = new JailLimitsSettingsViewModel(gateway);
        await vm.LoadAsync();
        gateway.Fail = true;

        await vm.SaveAsync();

        Assert.Contains("did not save", vm.ErrorMessage);
        Assert.Contains("daemon unreachable", vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task ResetToDefaults_OnlyChangesThePage_UntilSaved()
    {
        var gateway = new RecordingGateway { MemoryGiB = 8, Cpus = 6, IsDefault = false };
        var vm = new JailLimitsSettingsViewModel(gateway);
        await vm.LoadAsync();

        vm.ResetToDefaults();

        Assert.Equal(2, vm.MemoryGiB);
        Assert.Equal(2, vm.Cpus);
        Assert.Null(gateway.LastSave);
        Assert.Contains("Save to apply", vm.StatusMessage);
    }

    private static SettingsViewModel BuildSettings(bool hasAgentPlatform)
    {
        var noop = new RelayCommand<string>(_ => { });
        return new SettingsViewModel(
            new FakeSettings(),
            hasAgentPlatform,
            noop,
            noop,
            onPinsChanged: () => { },
            buildShortcutSettings: () => null!,
            currentRepoPath: () => null,
            refreshCurrentWorkspace: null,
            proTools: hasAgentPlatform ? new StubProTools() : null);
    }

    /// <summary>Answers the way the daemon does: clamps rather than echoes.</summary>
    private sealed class RecordingGateway : IJailLimitsGateway
    {
        public double MemoryGiB = 2;
        public double Cpus = 2;
        public bool IsDefault = true;
        public bool Fail;
        public (double MemoryGiB, double Cpus)? LastSave;

        public Task<JailLimitsView> LoadAsync(CancellationToken ct = default)
            => Fail ? Refused() : Task.FromResult(Snapshot());

        public Task<JailLimitsView> SaveAsync(double memoryGiB, double cpus, CancellationToken ct = default)
        {
            if (Fail) return Refused();
            LastSave = (memoryGiB, cpus);
            MemoryGiB = Math.Clamp(memoryGiB, 0.5, 64);
            Cpus = Math.Clamp(cpus, 0.5, 64);
            IsDefault = MemoryGiB == 2 && Cpus == 2;
            return Task.FromResult(Snapshot());
        }

        private JailLimitsView Snapshot() => new(MemoryGiB, Cpus, 512, IsDefault, 0.5, 64, 0.5, 64);

        private static Task<JailLimitsView> Refused()
            => Task.FromException<JailLimitsView>(new InvalidOperationException("daemon unreachable"));
    }

    private sealed class StubProTools : IProToolsSurface
    {
        public object CreateAiProvidersPage() => throw new NotSupportedException();
        public object CreateAgentClisPage() => throw new NotSupportedException();
        public object CreateToolchainsPage() => throw new NotSupportedException();
        public object CreateDaemonLogsPage() => throw new NotSupportedException();
        public object CreatePrIntakePage() => throw new NotSupportedException();
        public object CreateJailLimitsPage() => new JailLimitsSettingsViewModel(new RecordingGateway());
        public object? CreateMainguardOsPage(Avalonia.Controls.Window owner) => null;
        public Task RebuildSandboxImagesAsync() => Task.CompletedTask;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public UserPreferences Current { get; } = new();
        public void Update(Action<UserPreferences> updateAction) => updateAction(Current);
        public void Load() { }
        public void Save() { }
    }
}
