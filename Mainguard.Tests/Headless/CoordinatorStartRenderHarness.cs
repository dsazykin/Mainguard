using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// PR3 — the "Start coordinator" card (the new visible surface) rendered in EVERY one of the five
/// themes for human visual review: the CLI picker + primary action + explainer in its rest state,
/// and the live-coordinator fact line + "Open terminal" in the started state. PNGs land in
/// artifacts_headless/.
/// </summary>
public class CoordinatorStartRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    [AvaloniaFact]
    public async Task CoordinatorStartCard_HeadlessPng_AllThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            using var vm = new ControlCenterViewModel(new OrchestratorServices(
                new RenderCliHost(), mock, mock, mock, mock, mock, Owner: null));
            await vm.LoadInstalledClisAsync();

            Assert.True(vm.CanStartCoordinator); // the card is what this harness exists to show

            var win = new Window
            {
                Width = 1280,
                Height = 800,
                Content = new ControlCenterView { DataContext = vm },
            };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"coordinator_start_{theme}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public async Task CoordinatorLiveFactLine_HeadlessPng_DefaultTheme()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new RenderCliHost();
        using var vm = new ControlCenterViewModel(new OrchestratorServices(
            host, mock, mock, mock, mock, mock, Owner: null));
        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        vm.FocusCoordinator(); // back to the coordinator surface — the live fact line shows

        Assert.True(vm.IsCoordinatorLive);

        var win = new Window
        {
            Width = 1280,
            Height = 800,
            Content = new ControlCenterView { DataContext = vm },
        };
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_live_factline.png"));
        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The coordinator loader in its three honest states, rendered for review because this is where a
    /// blocking bug hid: the first start for a repository builds its ~2.9 GB toolchain image inside the
    /// spawn call, the 45 s connect budget expired mid-build, and the surface said "the coordinator
    /// isn't responding … use Stop to cancel and try again" — advice that destroyed the build and made
    /// the next attempt start it over.
    ///
    /// <list type="number">
    ///   <item><c>connecting_quiet</c> — the daemon has said nothing yet: the generic explainer.</item>
    ///   <item><c>connecting_building</c> — the daemon's progress line, which is the whole fix: the wait
    ///     now reads as work, and the stall banner is not shown at all while it stands.</item>
    ///   <item><c>connecting_stalled</c> — nothing from the daemon past the budget. Still honest, but the
    ///     copy no longer recommends the destructive remedy.</item>
    /// </list>
    ///
    /// <para>The states are driven through the VM's real properties, so a PNG can only show a state the
    /// VM can actually reach.</para>
    /// </summary>
    [AvaloniaFact]
    public async Task CoordinatorLoader_QuietVsBuildingVsStalled_HeadlessPng()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var previous = ControlCenterViewModel.CoordinatorConnectTimeout;
        ControlCenterViewModel.CoordinatorConnectTimeout = TimeSpan.FromMilliseconds(80);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            var host = new StallingCliHost();
            using var vm = new ControlCenterViewModel(new OrchestratorServices(
                host, mock, mock, mock, mock, mock, Owner: null));
            await vm.LoadInstalledClisAsync();

            var start = vm.StartCoordinatorCommand.ExecuteAsync(null);
            Assert.True(vm.IsCoordinatorConnecting);
            Assert.False(vm.HasCoordinatorStartDetail);
            Capture(vm, "coordinator_loader_connecting_quiet");

            // 2 — the daemon says what it is doing. The progress line replaces the generic explainer and
            // re-arms the watchdog on the long working budget.
            host.Announce(ToolchainProvisioner.BuildingMessage(ToolchainDeclarationResolver.Parse("dotnet-10")));
            Settle();
            Assert.True(vm.HasCoordinatorStartDetail);
            await Task.Delay(300); // well past the 80 ms silence budget
            Settle();
            Assert.False(vm.CoordinatorConnectTimedOut); // the banner must NOT be what we render here
            Capture(vm, "coordinator_loader_connecting_building");

            // 3 — a genuinely silent launch still reaches the stall state, on a fresh VM so the
            // progress line from (2) cannot be what suppresses or shows it.
            using var mock2 = new MockOrchestrator(TimeSpan.FromHours(1));
            using var quiet = new ControlCenterViewModel(new OrchestratorServices(
                new StallingCliHost(), mock2, mock2, mock2, mock2, mock2, Owner: null));
            await quiet.LoadInstalledClisAsync();
            var quietStart = quiet.StartCoordinatorCommand.ExecuteAsync(null);
            for (var i = 0; i < 40 && !quiet.CoordinatorConnectTimedOut; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            Assert.True(quiet.CoordinatorConnectTimedOut);
            Capture(quiet, "coordinator_loader_connecting_stalled");

            quiet.StopCoordinatorCommand.Execute(null);
            await quiet.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await quietStart;

            vm.StopCoordinatorCommand.Execute(null);
            await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await start;
        }
        finally
        {
            ControlCenterViewModel.CoordinatorConnectTimeout = previous;
        }
    }

    private static void Capture(ControlCenterViewModel vm, string name)
    {
        var win = new Window { Width = 1280, Height = 800, Content = new ControlCenterView { DataContext = vm } };
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), name + ".png"));
        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
        }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }

    /// <summary>A CLI host whose start never completes — the shape of a launch that is still working
    /// (or wedged) — plus a hook to publish the daemon's launch-progress line the way a state delta
    /// does in production.</summary>
    private sealed class StallingCliHost : IAgentService, ICliAgentHost
    {
        private readonly List<AgentInfo> _agents = new();

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InstalledCliOption>>(new[]
            {
                new InstalledCliOption("claude-code", "2.1.0", "ANTHROPIC_API_KEY"),
            });

        public async Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            var blocked = new TaskCompletionSource();
            using (ct.Register(() => blocked.TrySetResult()))
            {
                await blocked.Task;
            }

            ct.ThrowIfCancellationRequested();
            return "coord-1";
        }

        /// <summary>The session record exists from the moment the daemon creates it, and progress arrives
        /// as a state delta whose reason moves while the state word stays <c>Starting</c>.</summary>
        public void Announce(string detail)
        {
            CoordinatorAgentId = "coord-1";
            _agents.RemoveAll(a => a.AgentId == "coord-1");
            _agents.Add(new AgentInfo("coord-1", "claude-code", "agent/coord-1",
                AgentLifecycleState.Provisioning, detail, DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            EventReceived?.Invoke(new Mainguard.Agents.Agents.AgentEvent(1, "State", "coord-1", detail, DateTimeOffset.UtcNow));
        }

        public IReadOnlyList<AgentInfo> ListAgents() => _agents.ToArray();

        public event Action<Mainguard.Agents.Agents.AgentEvent>? EventReceived;

        public Task EndAgentAsync(string agentId)
        {
            _agents.RemoveAll(a => a.AgentId == agentId);
            if (CoordinatorAgentId == agentId) CoordinatorAgentId = null;
            return Task.CompletedTask;
        }

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
    }

    /// <summary>A representative CLI host for the design render (two CLIs, instant start).</summary>
    private sealed class RenderCliHost : IAgentService, ICliAgentHost
    {
        private readonly List<AgentInfo> _agents = new();

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InstalledCliOption>>(new[]
            {
                new InstalledCliOption("claude-code", "2.1.0", "ANTHROPIC_API_KEY"),
                new InstalledCliOption("opencode", "1.4.2", ""),
            });

        public Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            CoordinatorAgentId = "coord-1";
            _agents.Add(new AgentInfo("coord-1", cli.Id, "agent/coord-1",
                AgentLifecycleState.Working, "planning", DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            return Task.FromResult("coord-1");
        }

        public IReadOnlyList<AgentInfo> ListAgents() => _agents.ToArray();

        public event Action<AgentEvent>? EventReceived
        {
            add { }
            remove { }
        }

        public Task EndAgentAsync(string agentId) => Task.CompletedTask;

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
    }
}
