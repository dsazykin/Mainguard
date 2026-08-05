using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR3 — the control center's "Start coordinator" flow over fakes: the card gates on the CLI-host
/// seam + no live coordinator, the picker lists installed CLIs, a successful start shows the
/// coordinator's inline terminal (no per-agent workspace routing), Stop/Restart drive the session,
/// a refusal renders its honest message, and the exit guard's live-agent count reads off the same
/// projection.
/// </summary>
public class CoordinatorCliStartTests
{
    [AvaloniaFact]
    public async Task StartCoordinator_Spawns_MarksLive_AndShowsItsTerminalInline()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        Assert.True(vm.CanStartCoordinator);
        Assert.False(vm.IsCoordinatorLive);
        Assert.False(vm.ShowCoordinatorTerminal);
        Assert.Equal(2, vm.InstalledClis.Count);
        Assert.Equal("claude-code", vm.SelectedCli!.Id); // first installed preselected

        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Equal("claude-code", host.StartedWith!.Id); // the picked CLI is what spawned
        Assert.True(vm.IsCoordinatorLive);
        Assert.False(vm.CanStartCoordinator);
        Assert.Equal("", vm.CoordinatorStartError);

        // The coordinator's terminal is inline on the coordinator surface — NOT a per-agent workspace
        // document. Start keeps the coordinator focused and never routes through SelectAgent.
        Assert.True(vm.IsCoordinatorFocus);
        Assert.True(vm.ShowCoordinatorTerminal);
        Assert.Null(vm.SelectedAgentId);
        Assert.Null(vm.Workspace);
    }

    [AvaloniaFact]
    public async Task StopCoordinator_Confirmed_EndsTheSession_AndReturnsToStartable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        Assert.True(vm.IsCoordinatorLive);
        var startedId = host.CoordinatorAgentId!;

        // Stop now asks first (a full teardown is worth one deliberate click).
        vm.StopCoordinatorCommand.Execute(null);
        Assert.NotNull(vm.StopPrompt);
        Assert.Equal("Stop the coordinator?", vm.StopPrompt!.Title);
        Assert.Empty(host.EndedAgentIds); // nothing torn down until confirmed

        await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(startedId, host.EndedAgentIds); // the live coordinator was ended
        Assert.False(vm.IsCoordinatorLive);
        Assert.False(vm.ShowCoordinatorTerminal);
        Assert.True(vm.CanStartCoordinator);            // startable again over the (now gone) session
        Assert.Null(vm.StopPrompt);                     // overlay cleared after teardown
    }

    [AvaloniaFact]
    public async Task StopCoordinator_Cancelled_KeepsItRunning_AndTearsNothingDown()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        Assert.True(vm.IsCoordinatorLive);

        vm.StopCoordinatorCommand.Execute(null);
        Assert.NotNull(vm.StopPrompt);
        vm.StopPrompt!.CancelCommand.Execute(null); // "Keep running"

        Assert.Null(vm.StopPrompt);
        Assert.True(vm.IsCoordinatorLive);   // still running
        Assert.Empty(host.EndedAgentIds);    // nothing ended
    }

    [AvaloniaFact]
    public async Task StopCoordinator_WhileStarting_CancelsTheLaunch_Quietly_AndReturnsToStartable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost { BlockStartUntilCancelled = true };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        // The launch blocks (models a spawn that never returns) — Stop is the escape hatch.
        var startTask = vm.StartCoordinatorCommand.ExecuteAsync(null);
        Assert.True(vm.IsStartingCoordinator);
        Assert.True(vm.ShowStopCoordinator); // reachable mid-launch
        Assert.False(vm.IsCoordinatorLive);

        vm.StopCoordinatorCommand.Execute(null);
        Assert.NotNull(vm.StopPrompt);
        Assert.Equal("Cancel startup?", vm.StopPrompt!.Title); // wording adapts to the not-yet-live launch

        await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
        await startTask; // the cancelled spawn unwinds

        Assert.False(vm.IsStartingCoordinator);
        Assert.False(vm.IsCoordinatorConnecting);
        Assert.True(vm.CanStartCoordinator);
        Assert.Equal("", vm.CoordinatorStartError); // a user cancel is quiet, not an error
        Assert.Null(vm.StopPrompt);
        Assert.Equal(0, host.StartCalls);           // the spawn never completed
    }

    [AvaloniaFact]
    public async Task ConnectStall_PastTheTimeout_ShowsTheStalledState_NotAnEndlessSpinner()
    {
        var previous = ControlCenterViewModel.CoordinatorConnectTimeout;
        ControlCenterViewModel.CoordinatorConnectTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            var host = new FakeCliHost { BlockStartUntilCancelled = true };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));
            await vm.LoadInstalledClisAsync();

            var startTask = vm.StartCoordinatorCommand.ExecuteAsync(null);
            Assert.True(vm.IsCoordinatorConnecting);
            Assert.False(vm.CoordinatorConnectTimedOut);

            await WaitUntilAsync(() => vm.CoordinatorConnectTimedOut, TimeSpan.FromSeconds(2));

            Assert.True(vm.CoordinatorConnectTimedOut); // the loader stops pretending
            Assert.True(vm.ShowStopCoordinator);        // and Stop is still the way out

            // Cancel to unwind; the stalled flag clears with the connecting state.
            vm.StopCoordinatorCommand.Execute(null);
            await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await startTask;
            Assert.False(vm.IsCoordinatorConnecting);
            Assert.False(vm.CoordinatorConnectTimedOut);
        }
        finally
        {
            ControlCenterViewModel.CoordinatorConnectTimeout = previous;
        }
    }

    /// <summary>
    /// A launch-progress line from the daemon is shown as progress AND suppresses the stall banner.
    ///
    /// <para>This is the second half of the coordinator-start failure the owner hit. The first start for
    /// a repository builds its toolchain image — ~2.9 GB, minutes, inside the spawn call — and the 45 s
    /// connect budget expired long before it finished, so a healthy launch was reported as "the
    /// coordinator isn't responding … use Stop to cancel and try again". Following that advice killed
    /// the build, and the next attempt started it over: the same shape as the sandbox-image loop closed
    /// in PR #300, where the offered recovery guaranteed the failure repeated.</para>
    ///
    /// <para>The budget here is shortened to 50 ms and then deliberately OVERSHOT, so a build that is
    /// merely slower than the budget cannot pass this by accident — it passes only because the progress
    /// line rearmed the watchdog on the working budget.</para>
    /// </summary>
    [AvaloniaFact]
    public async Task DaemonProgressLine_ReadsAsProgress_AndTheStallBannerStaysDown()
    {
        var previous = ControlCenterViewModel.CoordinatorConnectTimeout;
        ControlCenterViewModel.CoordinatorConnectTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            var host = new FakeCliHost { BlockStartUntilCancelled = true };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));
            await vm.LoadInstalledClisAsync();

            var startTask = vm.StartCoordinatorCommand.ExecuteAsync(null);
            Assert.True(vm.IsCoordinatorConnecting);
            Assert.False(vm.HasCoordinatorStartDetail); // nothing said yet — the generic explainer shows

            var building = ToolchainProvisioner.BuildingMessage(ToolchainDeclarationResolver.Parse("dotnet-10"));
            host.AnnounceCoordinatorProgress(building);
            await WaitUntilAsync(() => vm.HasCoordinatorStartDetail, TimeSpan.FromSeconds(2));

            Assert.Equal(building, vm.CoordinatorStartDetail);
            Assert.Contains("dotnet-10", vm.CoordinatorStartDetail, StringComparison.Ordinal);

            // Well past the 50 ms silence budget. The banner must NOT appear: the daemon told us what it
            // is doing, so silence is not the diagnosis and Stop must not be advertised as the remedy.
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.CoordinatorConnectTimedOut);
            Assert.True(vm.IsCoordinatorConnecting); // still loading, just honestly

            vm.StopCoordinatorCommand.Execute(null);
            await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await startTask;

            // The progress line belongs to the launch, not to the surface: it clears with connecting, so
            // a later start never opens showing the previous one's stale message.
            Assert.False(vm.HasCoordinatorStartDetail);
        }
        finally
        {
            ControlCenterViewModel.CoordinatorConnectTimeout = previous;
        }
    }

    /// <summary>
    /// The other half of the same property, and what keeps the test above from being vacuous: with the
    /// daemon saying NOTHING over the identical budget, the stall banner still fires. Suppression is
    /// evidence-driven, not a blanket disabling of the watchdog — a launch that really is wedged must
    /// still be reported.
    /// </summary>
    [AvaloniaFact]
    public async Task WithNoProgressLine_TheStallBannerStillFires()
    {
        var previous = ControlCenterViewModel.CoordinatorConnectTimeout;
        ControlCenterViewModel.CoordinatorConnectTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            var host = new FakeCliHost { BlockStartUntilCancelled = true };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));
            await vm.LoadInstalledClisAsync();

            var startTask = vm.StartCoordinatorCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.CoordinatorConnectTimedOut, TimeSpan.FromSeconds(2));

            Assert.True(vm.CoordinatorConnectTimedOut);
            Assert.False(vm.HasCoordinatorStartDetail);

            vm.StopCoordinatorCommand.Execute(null);
            await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await startTask;
        }
        finally
        {
            ControlCenterViewModel.CoordinatorConnectTimeout = previous;
        }
    }

    /// <summary>A progress line that arrives AFTER the banner already fired takes it back down: a slow
    /// first report must not leave the destructive-advice panel on screen for the rest of the build.</summary>
    [AvaloniaFact]
    public async Task AProgressLineArrivingLate_TakesTheStallBannerBackDown()
    {
        var previous = ControlCenterViewModel.CoordinatorConnectTimeout;
        ControlCenterViewModel.CoordinatorConnectTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            var host = new FakeCliHost { BlockStartUntilCancelled = true };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));
            await vm.LoadInstalledClisAsync();

            var startTask = vm.StartCoordinatorCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.CoordinatorConnectTimedOut, TimeSpan.FromSeconds(2));
            Assert.True(vm.CoordinatorConnectTimedOut);

            host.AnnounceCoordinatorProgress(
                ToolchainProvisioner.BuildingMessage(ToolchainDeclarationResolver.Parse("dotnet-10")));
            await WaitUntilAsync(() => !vm.CoordinatorConnectTimedOut, TimeSpan.FromSeconds(2));

            Assert.False(vm.CoordinatorConnectTimedOut);
            Assert.True(vm.HasCoordinatorStartDetail);

            vm.StopCoordinatorCommand.Execute(null);
            await vm.StopPrompt!.ConfirmCommand.ExecuteAsync(null);
            await startTask;
        }
        finally
        {
            ControlCenterViewModel.CoordinatorConnectTimeout = previous;
        }
    }

    [AvaloniaFact]
    public async Task RestartCoordinator_StopsTheOld_ThenSpawnsAFreshOne()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        var firstId = host.CoordinatorAgentId!;
        Assert.Equal(1, host.StartCalls);

        await vm.RestartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains(firstId, host.EndedAgentIds);         // the old one was stopped
        Assert.Equal(2, host.StartCalls);                     // a fresh one was spawned
        Assert.True(vm.IsCoordinatorLive);
        Assert.NotEqual(firstId, host.CoordinatorAgentId);    // a new session id
    }

    [AvaloniaFact]
    public async Task StartCoordinator_Refusal_RendersTheHonestMessage_AndStaysStartable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            StartFailure = new InvalidOperationException("No repo is provisioned for agents yet — open a repository first."),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains("No repo is provisioned", vm.CoordinatorStartError);
        Assert.False(vm.IsCoordinatorLive);
        Assert.True(vm.CanStartCoordinator);
    }

    [AvaloniaFact]
    public async Task EmptyCatalog_ExplainsWhereToInstall()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost { Installed = Array.Empty<InstalledCliOption>() };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Empty(vm.InstalledClis);
        Assert.Contains("Settings", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonWithoutTheRpc_NamesTheVersionSkew_NotUnreachable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            ListFailure = new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unimplemented, "unknown method")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Contains("older than this app", vm.CoordinatorStartError);
        Assert.DoesNotContain("could not reach", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonUnreachable_KeepsTheReconnectMessage()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            ListFailure = new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "connection refused")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Contains("could not reach its agent daemon", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public async Task StartCoordinator_RpcFailure_ShowsTheDaemonsOwnDetail_NotTheEnvelope()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            StartFailure = new Grpc.Core.RpcException(new Grpc.Core.Status(
                Grpc.Core.StatusCode.FailedPrecondition,
                "Mainguard OS is missing the agent sandbox image (mainguard-agent-base) — it is "
                + "provisioned by setup; re-run Mainguard setup or rebuild the image, then try again.")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains("sandbox image", vm.CoordinatorStartError);
        Assert.DoesNotContain("Status(", vm.CoordinatorStartError); // never the RpcException envelope
        Assert.True(vm.CanStartCoordinator); // still startable after the failure
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonDownAtStartup_RetriesUntilItAnswers_AndPopulates()
    {
        var previousDelay = ControlCenterViewModel.CliLoadRetryDelay;
        ControlCenterViewModel.CliLoadRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            // Down for the first three answers — the cold-boot / tier-1-restart window.
            var host = new FakeCliHost { ListFailuresRemaining = 3 };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await vm.LoadInstalledClisUntilAvailableAsync(cts.Token);

            Assert.True(host.ListCalls >= 4); // failed attempts + the answered one
            Assert.Equal(2, vm.InstalledClis.Count);
            Assert.Equal("", vm.CoordinatorStartError);
        }
        finally
        {
            ControlCenterViewModel.CliLoadRetryDelay = previousDelay;
        }
    }

    [AvaloniaFact]
    public async Task LoadClis_HonestEmptyAnswer_StopsRetrying()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost { Installed = Array.Empty<InstalledCliOption>() };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await Task.Delay(200); // let the ctor's own retry loop land its single answered call

        var callsBefore = host.ListCalls;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await vm.LoadInstalledClisUntilAvailableAsync(cts.Token); // one answered call, then done
        await Task.Delay(200);

        Assert.Equal(callsBefore + 1, host.ListCalls); // an honest answer ends the retrying
        Assert.Contains("Settings", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public void LiveAgentCount_CountsOnlyNonTerminalStates()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents =
            {
                Agent("a1", AgentLifecycleState.Working),
                Agent("a2", AgentLifecycleState.AwaitingReview),
                Agent("a3", AgentLifecycleState.Dead),
                Agent("a4", AgentLifecycleState.Merged),
                Agent("a5", AgentLifecycleState.TornDown),
            },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        Assert.Equal(2, vm.LiveAgentCount);
    }

    [AvaloniaFact]
    public void CoordinatorRole_GatesTheCard_AndStaysOffTheWorkersRail()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents =
            {
                Agent("c1", AgentLifecycleState.Working, AgentRoles.Coordinator),
                Agent("w1", AgentLifecycleState.Working, AgentRoles.Managed),
            },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        Assert.True(vm.IsCoordinatorLive);
        Assert.False(vm.IsCoordinatorDead);
        Assert.False(vm.CanStartCoordinator);

        // The coordinator is its own entity, owned by the coordinator surface — NEVER a row among
        // the worker agents. The rail carries workers only, with the quiet role word.
        var worker = Assert.Single(vm.Agents);
        Assert.Equal("w1", worker.AgentId);
        Assert.Equal("subagent", worker.RoleLabel);
        Assert.Equal("", new AgentRowViewModel(Agent("m1", AgentLifecycleState.Working)).RoleLabel);

        // The exit guard still counts the LIVE coordinator (it is a live agent in the VM).
        Assert.Equal(2, vm.LiveAgentCount);
    }

    [AvaloniaFact]
    public void DeadCoordinator_IsHonest_UngatesStart_AndStillShowsItsTerminal()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents = { Agent("c1", AgentLifecycleState.Dead, AgentRoles.Coordinator) },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        // Honest death: a NEW coordinator is startable over the corpse, and the dead coordinator
        // neither counts as live nor rides the workers rail.
        Assert.False(vm.IsCoordinatorLive);
        Assert.True(vm.IsCoordinatorDead);
        Assert.True(vm.CanStartCoordinator);
        Assert.Empty(vm.Agents);
        Assert.Equal(0, vm.LiveAgentCount);

        // Its terminal region still shows — the daemon keeps the bound session's replay, so the terminal
        // shows the CLI's final output (the why of the death). Behind the fake host there's no live PTY,
        // so the surface renders the terminal placeholder rather than a wired terminal VM.
        Assert.True(vm.ShowCoordinatorTerminal);
    }

    [Fact]
    public void ApiKeyProviderMap_MapsKnownEnvVars_AndOnlyThose()
    {
        Assert.Equal("anthropic", ApiKeyProviderMap.ProviderForEnvVar("ANTHROPIC_API_KEY"));
        Assert.Equal("openai", ApiKeyProviderMap.ProviderForEnvVar("OPENAI_API_KEY"));
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar(""));       // interactive-login adapter
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar(null));
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar("SOME_OTHER_KEY"));
        Assert.Equal("llm_anthropic", ApiKeyProviderMap.KeystoreKeyFor("anthropic"));
    }

    // ---- helpers -----------------------------------------------------------

    private static AgentInfo Agent(string id, AgentLifecycleState state, string role = AgentRoles.Manual) =>
        new(id, id, $"agent/{id}", state, "", DateTimeOffset.UtcNow, role);

    /// <summary>Pumps the (Avalonia) dispatcher until <paramref name="predicate"/> holds or the deadline
    /// passes — for asserting an off-thread watchdog result (CoordinatorConnectTimedOut) deterministically.</summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>Bundle: the fake CLI host behind the Agents seam, the slow-tick mock behind the rest.</summary>
    private static OrchestratorServices BundleWith(FakeCliHost host, MockOrchestrator mock) =>
        new(host, mock, mock, mock, mock, mock, Owner: null);

    private sealed class FakeCliHost : IAgentService, ICliAgentHost
    {
        public List<AgentInfo> Agents { get; } = new();

        public IReadOnlyList<InstalledCliOption> Installed { get; set; } = new[]
        {
            new InstalledCliOption("claude-code", "2.1.0", "ANTHROPIC_API_KEY"),
            new InstalledCliOption("opencode", "1.4.2", ""),
        };

        public Exception? StartFailure { get; set; }

        /// <summary>When true, <see cref="StartCoordinatorAsync"/> blocks until its token is cancelled, then
        /// throws — models a spawn that never returns (the "loads forever" case) so Stop-cancel and the
        /// connect watchdog can be exercised over the fake.</summary>
        public bool BlockStartUntilCancelled { get; set; }

        public Exception? ListFailure { get; set; }

        /// <summary>When &gt; 0, the next list calls fail with <see cref="ListFailure"/> (or a
        /// generic fault) and decrement — models a daemon that is down during app startup and
        /// comes back (the cold-boot / tier-1-restart race the retry loop exists for).</summary>
        public int ListFailuresRemaining { get; set; }

        public int ListCalls { get; private set; }

        public InstalledCliOption? StartedWith { get; private set; }

        /// <summary>How many times the coordinator was spawned (Start + each Restart's start leg).</summary>
        public int StartCalls { get; private set; }

        /// <summary>Every agent id passed to <see cref="EndAgentAsync"/> (Stop + Restart's stop leg).</summary>
        public List<string> EndedAgentIds { get; } = new();

        // ---- ICliAgentHost ----

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct)
        {
            ListCalls++;
            if (ListFailuresRemaining > 0)
            {
                ListFailuresRemaining--;
                return Task.FromException<IReadOnlyList<InstalledCliOption>>(
                    ListFailure ?? new Grpc.Core.RpcException(
                        new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "connection refused")));
            }

            return ListFailure is null
                ? Task.FromResult(Installed)
                : Task.FromException<IReadOnlyList<InstalledCliOption>>(ListFailure);
        }

        public async Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            if (BlockStartUntilCancelled)
            {
                var blocked = new TaskCompletionSource();
                using (ct.Register(() => blocked.TrySetResult()))
                {
                    await blocked.Task;
                }

                ct.ThrowIfCancellationRequested(); // the spawn was cancelled before it ever completed
            }

            StartCalls++;
            StartedWith = cli;
            var id = $"coord-{StartCalls}";
            CoordinatorAgentId = id;
            Agents.Add(new AgentInfo(id, cli.Id, $"agent/{id}",
                AgentLifecycleState.Working, "", DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            return id;
        }

        // ---- IAgentService ----

        public IReadOnlyList<AgentInfo> ListAgents() => Agents.ToArray();

        public event Action<AgentEvent>? EventReceived;

        /// <summary>
        /// Models what the daemon does WHILE a spawn is still in flight: the session record exists from
        /// the moment it is created (<c>AgentSessionStore.Spawn</c> broadcasts before the launch runs),
        /// and a launch-progress line then arrives as a state delta whose <c>reason</c> moves while the
        /// state word stays <c>Starting</c>. That delta lands in <c>AgentInfo.Detail</c>.
        /// </summary>
        public void AnnounceCoordinatorProgress(string detail)
        {
            var id = CoordinatorAgentId ?? "coord-inflight";
            CoordinatorAgentId = id;
            Agents.RemoveAll(a => a.AgentId == id);
            Agents.Add(new AgentInfo(id, "claude-code", $"agent/{id}",
                AgentLifecycleState.Provisioning, detail, DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            EventReceived?.Invoke(new AgentEvent(1, "State", id, detail, DateTimeOffset.UtcNow));
        }

        /// <summary>Models the daemon tearing the session down: the agent leaves the list, so the VM's
        /// projection flips out of the live state.</summary>
        public Task EndAgentAsync(string agentId)
        {
            EndedAgentIds.Add(agentId);
            Agents.RemoveAll(a => a.AgentId == agentId);
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
}
