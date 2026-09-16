using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>One installed CLI's model settings, as the daemon holds them.</summary>
/// <param name="AgentKind">The adapter id, which is also what the surface renders — the daemon reads the
/// installed marker, which carries no display name.</param>
/// <param name="CanSetModel">Whether this CLI declares how it takes a model. False means the surface says
/// so rather than offering a picker whose value would be dropped at spawn.</param>
/// <param name="KnownModels">Models this CLI is known to accept. <b>Suggestions, never the complete
/// set</b> — vendors add models continuously, so a typed value is always allowed.</param>
/// <param name="CoordinatorModel">The chosen coordinator model, empty for "the CLI's own default".</param>
/// <param name="WorkerModel">The chosen worker model, empty for "the CLI's own default".</param>
public sealed record AgentModelOptionView(
    string AgentKind,
    bool CanSetModel,
    IReadOnlyList<string> KnownModels,
    string CoordinatorModel,
    string WorkerModel);

/// <summary>Everything the Agent Defaults page renders.</summary>
/// <param name="PlanModeEnabled">Whether a delegated worker must have an approved plan before it is
/// given its task.</param>
/// <param name="PlanModeSummary">The DAEMON's own sentence for that state — not a second wording of it,
/// so the page and the gate that refuses the coordinator cannot disagree about what is on.</param>
/// <param name="Models">One row per installed CLI.</param>
/// <param name="Error">Why the last write did nothing, empty otherwise. Rendered verbatim.</param>
public sealed record AgentDefaultsView(
    bool PlanModeEnabled,
    string PlanModeSummary,
    IReadOnlyList<AgentModelOptionView> Models,
    string Error = "")
{
    public static AgentDefaultsView Empty { get; } =
        new(true, string.Empty, Array.Empty<AgentModelOptionView>());
}

/// <summary>
/// The Agent Defaults page's daemon seam: the plan-approval gate and the per-(role, CLI) model.
///
/// <para>Both are DAEMON state and neither could be anything else. The gate is enforced where the daemon
/// serves the call, and the model has to be on a launch line the daemon builds — for a worker there is
/// no client in the loop at all, since the coordinator spawns it from inside its own jail. A
/// client-held preference for either would be read by nothing on the path that enforces it, which is the
/// decorative-control shape this codebase keeps finding (MG-12).</para>
/// </summary>
public interface IAgentDefaultsGateway
{
    Task<AgentDefaultsView> LoadAsync(CancellationToken ct = default);

    /// <summary>Turns the plan-approval gate on or off. Answers with the state the daemon now holds.</summary>
    Task<AgentDefaultsView> SetPlanModeAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Sets one (role, CLI) model, or clears it with a blank <paramref name="model"/>.</summary>
    Task<AgentDefaultsView> SetModelAsync(
        AgentModelRoleView role, string agentKind, string model, CancellationToken ct = default);
}

/// <summary>Which population a model choice applies to, in the UI layer's own vocabulary.</summary>
public enum AgentModelRoleView
{
    Coordinator,
    Worker,
}
