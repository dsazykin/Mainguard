using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Settings → Agent Defaults: the plan-approval gate and the per-(role, CLI) model.
///
/// <para><b>The defect that moved the toggle here.</b> It lived inside the plan gate, and the control
/// centre renders that gate only when it has content — a pending plan, an escalation, backpressure, or
/// plan mode already OFF. With plan mode ON and nothing waiting, the toggle had no surface at all: a
/// human could turn the gate back on but never off. A settings page has no such condition, which is the
/// whole point of the move.</para>
/// </summary>
public class AgentDefaultsSettingsPageTests
{
    [AvaloniaFact]
    public async Task ThePage_LoadsBothSettingsFromTheDaemon()
    {
        var gateway = new FakeGateway();
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        Assert.True(vm.PlanModeEnabled);
        Assert.Equal("every worker needs an approved plan", vm.PlanModeSummary);
        Assert.Equal(new[] { "claude-code", "opencode" }, vm.Models.Select(m => m.AgentKind));
    }

    /// <summary>
    /// The move's whole purpose: with plan mode ON and nothing in the gate, the control exists and turns
    /// it OFF. This is the direction that was unreachable before.
    /// </summary>
    [AvaloniaFact]
    public async Task PlanApproval_CanBeTurnedOff_WithNothingWaitingInTheGate()
    {
        var gateway = new FakeGateway();
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        Assert.True(vm.PlanModeEnabled);

        await vm.TogglePlanModeCommand.ExecuteAsync(null);

        Assert.False(gateway.PlanModeEnabled);
        Assert.False(vm.PlanModeEnabled);
    }

    [AvaloniaFact]
    public async Task PlanApproval_CanBeTurnedBackOn()
    {
        var gateway = new FakeGateway { PlanModeEnabled = false };
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        await vm.TogglePlanModeCommand.ExecuteAsync(null);

        Assert.True(gateway.PlanModeEnabled);
        Assert.True(vm.PlanModeEnabled);
    }

    /// <summary>The page renders the DAEMON's answer, not the value it sent — or it can show an approval
    /// step the daemon never accepted.</summary>
    [AvaloniaFact]
    public async Task ADaemonThatRefuses_LeavesTheToggleWhereTheDaemonHasIt()
    {
        var gateway = new FakeGateway { RefusePlanMode = true };
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        await vm.TogglePlanModeCommand.ExecuteAsync(null);

        Assert.True(vm.PlanModeEnabled);
        Assert.True(vm.StatusIsError);
        Assert.Contains("refused", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task CoordinatorAndWorkerModels_AreSetSeparately()
    {
        var gateway = new FakeGateway();
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        var claude = vm.Models.Single(m => m.AgentKind == "claude-code");
        claude.CoordinatorModel = "sonnet";
        await claude.ApplyCoordinatorCommand.ExecuteAsync(null);
        claude.WorkerModel = "opus";
        await claude.ApplyWorkerCommand.ExecuteAsync(null);

        // The pair is the point: a cheap planner driving expensive workers is a thing to be able to say.
        Assert.Equal("sonnet", gateway.Model(AgentModelRoleView.Coordinator, "claude-code"));
        Assert.Equal("opus", gateway.Model(AgentModelRoleView.Worker, "claude-code"));
    }

    [AvaloniaFact]
    public async Task PickingASuggestion_AppliesItInOneGesture()
    {
        var gateway = new FakeGateway();
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        var claude = vm.Models.Single(m => m.AgentKind == "claude-code");
        Assert.True(claude.HasKnownModels);

        await claude.PickWorkerCommand.ExecuteAsync("fable");

        // Not "fills a box you must then press Apply on" — that would be two steps for one choice.
        Assert.Equal("fable", gateway.Model(AgentModelRoleView.Worker, "claude-code"));
        Assert.Equal("fable", claude.WorkerModel);
    }

    [AvaloniaFact]
    public async Task AnEmptyModel_ClearsTheChoice()
    {
        var gateway = new FakeGateway();
        gateway.Set(AgentModelRoleView.Worker, "claude-code", "opus");
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        var claude = vm.Models.Single(m => m.AgentKind == "claude-code");
        claude.WorkerModel = "";
        await claude.ApplyWorkerCommand.ExecuteAsync(null);

        Assert.Equal("", gateway.Model(AgentModelRoleView.Worker, "claude-code"));
    }

    /// <summary>A CLI with no declared flag says so rather than offering a picker whose value would be
    /// accepted and then dropped when the agent is launched.</summary>
    [AvaloniaFact]
    public async Task ACliThatCannotTakeAModel_SaysSo()
    {
        var gateway = new FakeGateway();
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        var opencode = vm.Models.Single(m => m.AgentKind == "opencode");
        Assert.False(opencode.CanSetModel);
        Assert.Contains("doesn't declare a model flag", opencode.NotSettableText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AnUnreachableDaemon_IsAnErrorOnThePage_NotASilentSuccess()
    {
        var gateway = new FakeGateway { Throw = true };
        var vm = new AgentDefaultsSettingsViewModel(gateway);
        await gateway.Settled;

        Assert.True(vm.StatusIsError);
        Assert.Contains("Couldn't reach the daemon", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>A gateway holding the state the daemon would, so the page is driven end to end.</summary>
    private sealed class FakeGateway : IAgentDefaultsGateway
    {
        private readonly Dictionary<(AgentModelRoleView, string), string> _models = new();
        private readonly TaskCompletionSource _settled = new();

        public bool PlanModeEnabled { get; set; } = true;

        public bool RefusePlanMode { get; set; }

        public bool Throw { get; set; }

        /// <summary>Completes once the constructor's fire-and-forget load has run.</summary>
        public Task Settled => _settled.Task;

        public string Model(AgentModelRoleView role, string kind) =>
            _models.TryGetValue((role, kind), out var m) ? m : "";

        public void Set(AgentModelRoleView role, string kind, string model) => _models[(role, kind)] = model;

        public Task<AgentDefaultsView> LoadAsync(CancellationToken ct = default)
        {
            _settled.TrySetResult();
            return Throw
                ? Task.FromException<AgentDefaultsView>(new InvalidOperationException("no daemon"))
                : Task.FromResult(View());
        }

        public Task<AgentDefaultsView> SetPlanModeAsync(bool enabled, CancellationToken ct = default)
        {
            if (RefusePlanMode)
            {
                return Task.FromResult(View("the daemon refused the change"));
            }

            PlanModeEnabled = enabled;
            return Task.FromResult(View());
        }

        public Task<AgentDefaultsView> SetModelAsync(
            AgentModelRoleView role, string agentKind, string model, CancellationToken ct = default)
        {
            var cleaned = (model ?? "").Trim();
            if (cleaned.Length == 0)
            {
                _models.Remove((role, agentKind));
            }
            else
            {
                _models[(role, agentKind)] = cleaned;
            }

            return Task.FromResult(View());
        }

        private AgentDefaultsView View(string error = "") => new(
            PlanModeEnabled,
            PlanModeEnabled ? "every worker needs an approved plan" : "workers start with no plan",
            new[]
            {
                new AgentModelOptionView(
                    "claude-code", CanSetModel: true,
                    new[] { "fable", "opus", "sonnet", "haiku" },
                    Model(AgentModelRoleView.Coordinator, "claude-code"),
                    Model(AgentModelRoleView.Worker, "claude-code")),
                // A CLI whose flag is not declared — the honest "not settable" row.
                new AgentModelOptionView(
                    "opencode", CanSetModel: false, Array.Empty<string>(), "", ""),
            },
            error);
    }
}
