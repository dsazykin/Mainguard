using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IAgentDefaultsGateway"/>: the plan-mode pair on
/// <c>PlanApprovalService</c> and the model pair on <c>AgentService</c>, over the daemon client.
/// Holds no state; every load is a fresh read of both.
/// </summary>
public sealed class DaemonAgentDefaultsGateway : IAgentDefaultsGateway
{
    private readonly DaemonClient _client;

    public DaemonAgentDefaultsGateway(DaemonClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<AgentDefaultsView> LoadAsync(CancellationToken ct = default)
    {
        var planMode = await _client.GetPlanModeAsync(ct).ConfigureAwait(false);
        var models = await _client.GetAgentModelsAsync(ct).ConfigureAwait(false);
        return ToView(planMode, models);
    }

    public async Task<AgentDefaultsView> SetPlanModeAsync(bool enabled, CancellationToken ct = default)
    {
        // The daemon's answer, not the requested value: what is rendered has to be what is enforced, or
        // the page can show an approval step the daemon never accepted.
        var planMode = await _client.SetPlanModeAsync(enabled, ct).ConfigureAwait(false);
        var models = await _client.GetAgentModelsAsync(ct).ConfigureAwait(false);
        return ToView(planMode, models);
    }

    public async Task<AgentDefaultsView> SetModelAsync(
        AgentModelRoleView role, string agentKind, string model, CancellationToken ct = default)
    {
        var models = await _client
            .SetAgentModelAsync(RoleWire(role), agentKind, model, ct)
            .ConfigureAwait(false);
        var planMode = await _client.GetPlanModeAsync(ct).ConfigureAwait(false);
        return ToView(planMode, models);
    }

    /// <summary>Lower-case, matching the wire's documented spelling. The daemon REFUSES an unknown role
    /// rather than coercing one, so this is the half that has to be right.</summary>
    private static string RoleWire(AgentModelRoleView role) =>
        role == AgentModelRoleView.Coordinator ? "coordinator" : "worker";

    private static AgentDefaultsView ToView(PlanModeState planMode, AgentModelSettings models) => new(
        planMode.Enabled,
        planMode.Summary ?? string.Empty,
        models.Options.Select(ToOption).ToList(),
        models.Error ?? string.Empty);

    private static AgentModelOptionView ToOption(AgentModelOption option) => new(
        option.AgentKind,
        // The declared flag is what makes the model settable at all — its absence is the one honest
        // reason to disable the picker rather than let a value be stored and dropped at spawn.
        CanSetModel: !string.IsNullOrWhiteSpace(option.ModelArg),
        KnownModels: option.KnownModels.ToList(),
        CoordinatorModel: option.CoordinatorModel ?? string.Empty,
        WorkerModel: option.WorkerModel ?? string.Empty);
}
