using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The role a chat turn came from.</summary>
public enum CoordinatorRole { System, Human, Assistant, Tool }

/// <summary>One turn in the coordinator conversation.</summary>
public sealed record CoordinatorMessage(CoordinatorRole Role, string Content);

/// <summary>A tool call the model requested (name + already-parsed arguments).</summary>
public sealed record CoordinatorToolCall(string Tool, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>A model turn: either free text to the human, or one/more tool calls to dispatch.</summary>
public sealed record CoordinatorModelTurn(string? Text, IReadOnlyList<CoordinatorToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;

    public static CoordinatorModelTurn Say(string text) => new(text, Array.Empty<CoordinatorToolCall>());
    public static CoordinatorModelTurn Call(params CoordinatorToolCall[] calls) => new(null, calls);
}

/// <summary>
/// The model seam the coordinator drives. A real adapter forwards to the LLM through the P2-08 gateway;
/// the scripted end-to-end test supplies a deterministic implementation. Kept minimal so the coordinator
/// loop is provider-agnostic (G-14).
/// </summary>
public interface ICoordinatorModel
{
    /// <summary>One model turn given the running transcript.</summary>
    Task<CoordinatorModelTurn> NextAsync(IReadOnlyList<CoordinatorMessage> transcript, CancellationToken ct);
}

/// <summary>
/// P2-14 coordinator chat agent (contract §2): a system-prompted tool loop over the P2-08 gateway with
/// <b>no code, no worktree, no merges</b> — its only capabilities are the four <see cref="CoordinatorTools"/>.
/// Each turn acquires a gateway lease (rate/budget shared with workers), asks the model for the next turn,
/// and dispatches any tool calls back into the capped tool surface, appending typed results to the
/// transcript. The loop cannot escalate: it never sees a merge RPC, a git credential, or a worktree.
/// </summary>
public sealed class CoordinatorAgent
{
    /// <summary>
    /// The system prompt that states the trust model to the model itself.
    ///
    /// <para>It <b>describes</b> the boundaries; it does not create them. Every limit named here is
    /// enforced daemon-side and has a test that fails when the check is removed (contract §5). If this
    /// string and the daemon ever disagree, the daemon wins and the string is the bug.</para>
    /// </summary>
    public const string SystemPrompt =
        "You are the Mainguard Coordinator. You plan the shape of the work and delegate it; you never " +
        "write code, touch a worktree, or merge. Decompose the operator's request into independent tasks " +
        "and start one worker per task via spawn_worker(title, task_prompt, budget_usd) — no human " +
        "approval is needed to spawn, only the caps. You do NOT write task plans: each worker inspects " +
        "the repository, authors its own plan, presents it to the human and blocks until it is approved, " +
        "so do not describe scope, approach or test strategy yourself — you cannot see the code. " +
        "A worker waiting on plan approval still occupies a worker slot, so if spawn_worker is refused " +
        "for the worker cap, report that plans are waiting on the human instead of retrying. " +
        "Use get_worker_status, send_worker_prompt, and request_verification to steer; both of the latter " +
        "are refused for a worker whose plan is not yet approved. Serialize dependent tasks; parallelize " +
        "independent ones.";

    private readonly string _coordinatorId;
    private readonly ICoordinatorModel _model;
    private readonly CoordinatorTools _tools;
    private readonly IAiGateway? _gateway;
    private readonly int _maxToolIterations;
    private readonly List<CoordinatorMessage> _transcript = new();

    public CoordinatorAgent(
        string coordinatorId,
        ICoordinatorModel model,
        CoordinatorTools tools,
        IAiGateway? gateway = null,
        int maxToolIterations = 16)
    {
        _coordinatorId = string.IsNullOrWhiteSpace(coordinatorId)
            ? throw new ArgumentException("coordinatorId is required.", nameof(coordinatorId))
            : coordinatorId;
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _gateway = gateway;
        _maxToolIterations = maxToolIterations;
        _transcript.Add(new CoordinatorMessage(CoordinatorRole.System, SystemPrompt));
    }

    /// <summary>The running transcript (system + human + assistant + tool turns).</summary>
    public IReadOnlyList<CoordinatorMessage> Transcript => _transcript.ToList();

    /// <summary>
    /// Handles one operator message: run the model → tool loop until the model returns text (or the tool
    /// iteration cap is hit). Every model turn acquires a gateway lease first (shared budget). Returns the
    /// coordinator's textual reply.
    /// </summary>
    public async Task<string> SendAsync(string humanMessage, CancellationToken ct = default)
    {
        _transcript.Add(new CoordinatorMessage(CoordinatorRole.Human, humanMessage ?? ""));

        for (var iteration = 0; iteration < _maxToolIterations; iteration++)
        {
            // Per-turn gateway lease: the coordinator shares the shared-key rate budget with its workers,
            // so a chatty coordinator can't starve them (P2-08). Estimate is small — chat, not generation.
            if (_gateway is not null)
            {
                var lease = await _gateway.AcquireAsync(_coordinatorId, estimatedTokens: 512, ct).ConfigureAwait(false);
                // The lease is a rate GATE here — the real spend is settled by the model adapter on its
                // own pass through the proxy, so this one is handed straight back. MG-24: it now carries
                // a provisional budget debit, and a lease that is merely discarded would hold 512 tokens
                // of phantom spend per turn until the daemon restarts. Abandon keeps the request permit
                // spent (which is the whole point of the gate) and refunds only the token estimate.
                _gateway.Abandon(lease);
            }

            var turn = await _model.NextAsync(_transcript, ct).ConfigureAwait(false);

            if (!turn.HasToolCalls)
            {
                var text = turn.Text ?? "";
                _transcript.Add(new CoordinatorMessage(CoordinatorRole.Assistant, text));
                return text;
            }

            if (!string.IsNullOrEmpty(turn.Text))
            {
                _transcript.Add(new CoordinatorMessage(CoordinatorRole.Assistant, turn.Text));
            }

            foreach (var call in turn.ToolCalls)
            {
                var result = await DispatchAsync(call, ct).ConfigureAwait(false);
                _transcript.Add(new CoordinatorMessage(CoordinatorRole.Tool, $"{call.Tool} → [{result.Status}] {result.Message}"));
            }
        }

        var capped = "Reached the tool-iteration cap without a final reply.";
        _transcript.Add(new CoordinatorMessage(CoordinatorRole.Assistant, capped));
        return capped;
    }

    private async Task<CoordinatorToolResult> DispatchAsync(CoordinatorToolCall call, CancellationToken ct)
    {
        switch (call.Tool)
        {
            case "spawn_worker":
                return await DispatchSpawnAsync(call, ct).ConfigureAwait(false);
            case "get_worker_status":
                return _tools.GetWorkerStatus(Str(call, "agent_id"));
            case "send_worker_prompt":
                return await _tools.SendWorkerPromptAsync(Str(call, "agent_id") ?? "", Str(call, "prompt") ?? "", ct).ConfigureAwait(false);
            case "request_verification":
                return await _tools.RequestVerificationAsync(Str(call, "agent_id") ?? "", ct).ConfigureAwait(false);
            default:
                return CoordinatorToolResult.Rejected($"Unknown tool '{call.Tool}'.");
        }
    }

    /// <summary>
    /// <c>spawn_worker</c> now takes only (title, task_prompt, budget_usd). Any plan fields the model
    /// supplies are <b>discarded, not honoured</b> — plan authorship belongs to the worker (contract §2),
    /// and silently accepting a coordinator-authored scope here would reinstate the inverted gate through
    /// the back door.
    /// </summary>
    private async Task<CoordinatorToolResult> DispatchSpawnAsync(CoordinatorToolCall call, CancellationToken ct)
    {
        var budget = call.Arguments.TryGetValue("budget_usd", out var b) && b is not null && decimal.TryParse(b.ToString(), out var d) ? d : 0m;
        return await _tools.SpawnWorkerAsync(
            Str(call, "title") ?? "Untitled task", Str(call, "task_prompt") ?? "", budget, ct).ConfigureAwait(false);
    }

    private static string? Str(CoordinatorToolCall call, string key) =>
        call.Arguments.TryGetValue(key, out var v) ? v?.ToString() : null;
}
