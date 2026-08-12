using System;
using System.Collections.Generic;
using System.Linq;
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
/// The external-PR-intake settings page: that it is REACHABLE, and that everything it edits goes to the
/// daemon.
///
/// <para><b>The defect.</b> <c>PrIntakeSettingsView</c> shipped as a complete top-level
/// <c>Window</c> with zero references anywhere in the repository — no menu item, no button, no test, no
/// harness — so external PR intake had a full settings dialog nothing could open and the feature was
/// unconfigurable in the shipped app. It could not have been dropped into the Settings rail either:
/// Avalonia refuses to host a <c>Window</c> as <c>ContentControl.Content</c>.</para>
///
/// <para><b>The other half of the defect</b> is why it stayed unwired: the ViewModel took the daemon's
/// own <c>IPrIntakeStore</c> and DEFAULTED to an in-process one, so the obvious wiring would have
/// produced a page that saves successfully into an object the daemon never reads.</para>
/// </summary>
public class PrIntakeSettingsPageTests
{
    // ---- reachable ------------------------------------------------------

    /// <summary>
    /// The page is a row in the Settings window's rail, next to the other agent-platform pages. This is
    /// the assertion that would have failed for the entire life of the feature.
    /// </summary>
    [Fact]
    public void SettingsRail_UnderTheAgentPlatform_OffersThePrIntakePage()
    {
        var settings = BuildSettings(hasAgentPlatform: true);

        var row = Assert.Single(settings.Pages, p => p.Id == "PrIntake");
        Assert.Equal("PR Intake", row.Label);
        Assert.IsType<PrIntakeSettingsViewModel>(row.Content);
    }

    /// <summary>…and it is agent-platform-only, like every other Pro page: the free Git client has no
    /// daemon to configure and must not be offered a page that can only fail.</summary>
    [Fact]
    public void SettingsRail_WithoutTheAgentPlatform_OmitsThePrIntakePage()
    {
        Assert.DoesNotContain(BuildSettings(hasAgentPlatform: false).Pages, p => p.Id == "PrIntake");
    }

    /// <summary>
    /// The last mile of "reachable": the page's ViewModel resolves to a real View through the app's
    /// <c>ViewLocator</c> name transform, and that View is a <see cref="Avalonia.Controls.UserControl"/>.
    ///
    /// <para>Both halves are the bug. A missing type makes the Settings pane render the literal
    /// <c>"Not Found: …"</c> TextBlock, and a <see cref="Avalonia.Controls.Window"/> — which is what this
    /// View was — throws when set as <c>ContentControl.Content</c>, so the rail row would fail at the
    /// moment a human clicked it. Asserted on the TYPE rather than by instantiating, so it needs no
    /// Avalonia application and can live in the plain unit tier.</para>
    /// </summary>
    [Fact]
    public void ThePageViewModel_ResolvesToARealUserControl()
    {
        var viewName = typeof(PrIntakeSettingsViewModel).FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var view = typeof(PrIntakeSettingsViewModel).Assembly.GetType(viewName);

        Assert.NotNull(view);
        Assert.True(
            typeof(Avalonia.Controls.UserControl).IsAssignableFrom(view),
            $"{viewName} must be a UserControl to be hosted as a Settings page; a Window throws when "
            + "assigned to ContentControl.Content, which is why this surface could never be a page.");
    }

    /// <summary>The Pro tools surface builds it, which is what the shell's rail row calls through.</summary>
    [Fact]
    public void ProToolsSurface_BuildsThePage_OverTheComposedGateway()
    {
        var gateway = new RecordingGateway();
        var previous = ProComposition.PrIntakeGatewayFactory;
        ProComposition.PrIntakeGatewayFactory = () => gateway;
        try
        {
            Assert.IsType<PrIntakeSettingsViewModel>(new ProToolsSurface().CreatePrIntakePage());
        }
        finally
        {
            ProComposition.PrIntakeGatewayFactory = previous;
        }
    }

    // ---- and everything it edits goes to the daemon ---------------------

    /// <summary>
    /// There is no gateway-less constructor. A default would be the whole bug: a page that quietly edits
    /// storage the daemon cannot see is worse than a page that does not exist, because it reports success.
    /// </summary>
    [Fact]
    public void ViewModel_HasNoStorageOfItsOwn_ToFallBackTo()
    {
        Assert.DoesNotContain(
            typeof(PrIntakeSettingsViewModel).GetConstructors(),
            c => c.GetParameters().All(p => p.IsOptional));

        Assert.Throws<ArgumentNullException>(() => new PrIntakeSettingsViewModel(null!));
    }

    /// <summary>Save sends the page's values to the daemon, and then re-renders from what the DAEMON
    /// persisted — not from what the user typed. The gateway here clamps like the daemon does, so a page
    /// that echoed its own input would show 5 while the poller ran at 10.</summary>
    [Fact]
    public async Task Save_WritesThroughTheGateway_AndRendersWhatCameBack()
    {
        var gateway = new RecordingGateway();
        var vm = new PrIntakeSettingsViewModel(gateway);
        await vm.LoadAsync();

        vm.IntakeEnabled = false;
        vm.PollIntervalSeconds = 5;                 // below the daemon's floor
        vm.BotAuthors = "renovate[bot], , copilot"; // blanks dropped, entries trimmed
        await vm.SaveAsync();

        Assert.Equal(1, gateway.Saves);
        Assert.False(gateway.Enabled);
        Assert.Equal(new[] { "renovate[bot]", "copilot" }, gateway.LastAuthorsRequested);

        // Re-rendered from the daemon's answer, so the field shows the cadence actually in force.
        Assert.Equal(10, vm.PollIntervalSeconds);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.StatusMessage);
    }

    /// <summary>Subscribing goes to the daemon too, and the list the page shows is the daemon's, not a
    /// client-side echo of what this session happened to add.</summary>
    [Fact]
    public async Task Subscribe_WritesThroughTheGateway_AndListsTheDaemonsSources()
    {
        var gateway = new RecordingGateway();
        var vm = new PrIntakeSettingsViewModel(gateway);
        await vm.LoadAsync();

        vm.NewOwner = "acme";
        vm.NewRepo = "app";
        await vm.AddSourceAsync();

        Assert.Equal(("github.com", "acme", "app"), gateway.LastSubscribe);
        Assert.Equal("github.com/acme/app", Assert.Single(vm.Sources).Key);

        // A repeat is reported as already-subscribed rather than as a failure, and adds no row.
        vm.NewOwner = "acme";
        vm.NewRepo = "app";
        await vm.AddSourceAsync();
        Assert.Single(vm.Sources);
        Assert.Contains("already subscribed", vm.StatusMessage);
    }

    /// <summary>
    /// A daemon that refuses or cannot be reached leaves the page saying so. The one behaviour this
    /// surface must never have is looking saved when nothing was saved — which is precisely what a
    /// swallowed exception, or a fallback to local defaults, would produce.
    /// </summary>
    [Fact]
    public async Task WhenTheDaemonRefuses_ThePageSaysSo_AndClaimsNoSuccess()
    {
        var gateway = new RecordingGateway { Fail = true };
        var vm = new PrIntakeSettingsViewModel(gateway);

        await vm.LoadAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsLoaded);

        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
    }

    // ---- harness --------------------------------------------------------

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

    /// <summary>An <see cref="IPrIntakeGateway"/> that records what the page asked for and answers the
    /// way the daemon does — clamping the cadence rather than echoing the request.</summary>
    private sealed class RecordingGateway : IPrIntakeGateway
    {
        private readonly List<PrIntakeSourceItem> _sources = new();

        public bool Fail { get; init; }
        public int Saves { get; private set; }
        public bool Enabled { get; private set; } = true;
        public int PollIntervalSeconds { get; private set; } = 60;
        public IReadOnlyList<string> LastAuthorsRequested { get; private set; } = Array.Empty<string>();
        public (string Host, string Owner, string Repo)? LastSubscribe { get; private set; }

        public Task<PrIntakeConfiguration> LoadAsync(CancellationToken ct = default)
            => Fail ? Refused<PrIntakeConfiguration>() : Task.FromResult(Snapshot());

        public Task<PrIntakeConfiguration> SaveAsync(
            bool enabled, int pollIntervalSeconds, IReadOnlyList<string> botAuthors, CancellationToken ct = default)
        {
            if (Fail) return Refused<PrIntakeConfiguration>();

            Saves++;
            Enabled = enabled;
            PollIntervalSeconds = Math.Clamp(pollIntervalSeconds, 10, 3600);
            LastAuthorsRequested = botAuthors.ToList();
            return Task.FromResult(Snapshot());
        }

        public Task<(bool Added, PrIntakeConfiguration Configuration)> SubscribeAsync(
            string host, string owner, string repo, string? authorFilter, CancellationToken ct = default)
        {
            if (Fail) return Refused<(bool, PrIntakeConfiguration)>();

            LastSubscribe = (host, owner, repo);
            var item = new PrIntakeSourceItem(host, owner, repo, authorFilter ?? string.Empty);
            var added = !_sources.Contains(item);
            if (added) _sources.Add(item);
            return Task.FromResult((added, Snapshot()));
        }

        private PrIntakeConfiguration Snapshot() => new(
            Enabled, PollIntervalSeconds, LastAuthorsRequested.ToList(), _sources.ToList());

        private static Task<T> Refused<T>()
            => Task.FromException<T>(new InvalidOperationException("daemon unreachable"));
    }

    /// <summary>Only the intake page is real; the rest of the Pro surface is not under test here and
    /// must never be built (its factories reach the WSL VM).</summary>
    private sealed class StubProTools : IProToolsSurface
    {
        public object CreateAiProvidersPage() => throw new NotSupportedException();
        public object CreateAgentClisPage() => throw new NotSupportedException();
        public object CreateToolchainsPage() => throw new NotSupportedException();
        public object CreateDaemonLogsPage() => throw new NotSupportedException();
        public object CreatePrIntakePage() => new PrIntakeSettingsViewModel(new RecordingGateway());
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
