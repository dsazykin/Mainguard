using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The fixed layout of the per-agent IPC directory: a daemon-owned ext4 dir on the VM, bind-mounted
/// READ-ONLY into the agent's jail at <see cref="SandboxMount"/>. It carries the daemon-served Unix
/// socket plus <b>the one shim that agent's role is allowed</b>, which the launch wrapper puts on PATH.
/// Connecting to a Unix socket is not a filesystem write, so the read-only mount is sufficient — the jail
/// can talk, but can never swap the shim or the socket for another agent's.
///
/// <para><b>Role decides the shim</b> (least privilege, unchanged in spirit from phase 1):</para>
/// <list type="bullet">
/// <item>a <c>coordinator</c> jail gets <see cref="SpawnShimFileName"/> — it may start workers, and it
/// has no plan channel because it does not author plans;</item>
/// <item>a <b>plan-gated</b> <c>managed</c> worker jail — one the daemon is withholding a task from —
/// gets <see cref="PlanShimFileName"/>: it may present, revise and block on <i>its own</i> plan, and it
/// has no spawn channel;</item>
/// <item>every other session gets <b>no IPC dir at all</b>. That is every external-PR head (untrusted
/// code from outside this machine) and every manually spawned worker: neither is governed by the plan
/// gate, so neither has any use for the channel — and handing an untrusted head a socket that queues
/// approval cards in front of the human would be a capability granted for no reason.</item>
/// </list>
/// <para>Neither can reach the other's operations: identity is positional (only that agent's jail has the
/// mount) and the daemon dispatches on the endpoint's role, not on anything in the request.</para>
/// </summary>
public static class AgentIpcPaths
{
    /// <summary>Where the agent's IPC dir appears inside its jail.</summary>
    public const string SandboxMount = "/opt/mainguard/ipc";

    /// <summary>The daemon-served Unix socket file name (inside the IPC dir).</summary>
    public const string SocketFileName = "daemon.sock";

    /// <summary>The coordinator's spawn shim file name (inside the IPC dir; on the wrapper's PATH).</summary>
    public const string SpawnShimFileName = "mainguard-agent";

    /// <summary>The worker's plan shim file name (inside the IPC dir; on the wrapper's PATH).</summary>
    public const string PlanShimFileName = "mainguard-plan";

    /// <summary>Back-compat alias for the coordinator spawn shim's name.</summary>
    public const string ShimFileName = SpawnShimFileName;

    /// <summary>The socket path as the jail sees it (what the shims dial by default).</summary>
    public const string SandboxSocketPath = SandboxMount + "/" + SocketFileName;
}

/// <summary>Which shim an IPC endpoint publishes, and therefore which ops the daemon will serve on it.</summary>
public enum AgentIpcEndpointRole
{
    /// <summary>A coordinator jail: <c>spawn</c> / <c>list</c>.</summary>
    Coordinator,

    /// <summary>A managed worker jail: the plan gate ops.</summary>
    Worker,
}

/// <summary>One line-delimited JSON request from an in-jail shim.</summary>
/// <param name="PlanJson">A worker-authored plan document, validated against <c>TaskPlanSchema</c>.</param>
public sealed record AgentIpcRequest(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("agentKind")] string? AgentKind = null,
    [property: JsonPropertyName("taskPrompt")] string? TaskPrompt = null,
    [property: JsonPropertyName("planId")] string? PlanId = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("planJson")] string? PlanJson = null)
{
    // Coordinator ops.
    public const string SpawnOp = "spawn";
    public const string ListOp = "list";

    // Worker plan-gate ops (phase 2).

    /// <summary>What am I here to plan? Returns the brief — never the task prompt.</summary>
    public const string BriefOp = "brief";

    /// <summary>Present the plan I authored after inspecting the repo.</summary>
    public const string PresentPlanOp = "present_plan";

    /// <summary>Re-present a plan revised against the human's rejection feedback.</summary>
    public const string RevisePlanOp = "revise_plan";

    /// <summary>Block until the human decides. This call is the worker's gate.</summary>
    public const string AwaitDecisionOp = "await_decision";
}

/// <summary>
/// One line-delimited JSON response toward the shim. Errors are honest prose, no stacks.
/// </summary>
/// <param name="TaskPrompt">
/// The withheld task, released <b>only</b> alongside an approved decision. Everything before that point
/// answers with this field empty — a worker literally does not possess its task until a human approves.
/// </param>
public sealed record AgentIpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("agentId")] string? AgentId = null,
    [property: JsonPropertyName("agents")] string[]? Agents = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("planId")] string? PlanId = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("feedback")] string? Feedback = null,
    [property: JsonPropertyName("revision")] int? Revision = null,
    [property: JsonPropertyName("revisionsRemaining")] int? RevisionsRemaining = null,
    [property: JsonPropertyName("maxRevisions")] int? MaxRevisions = null,
    [property: JsonPropertyName("brief")] string? Brief = null,
    [property: JsonPropertyName("taskPrompt")] string? TaskPrompt = null,
    [property: JsonPropertyName("planErrors")] string[]? PlanErrors = null);

/// <summary>
/// The pure wire codec for the coordinator→daemon spawn channel: newline-delimited JSON, one
/// request line → one response line. Malformed input is a typed null (the server answers with an
/// error response), never an exception escaping to the socket loop.
/// </summary>
public static class AgentIpcProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses one request line; null when the line is not a valid request.</summary>
    public static AgentIpcRequest? TryParseRequest(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<AgentIpcRequest>(line, Options);
            return request is { Op.Length: > 0 } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes one response as a single line (no embedded newlines).</summary>
    public static string SerializeResponse(AgentIpcResponse response) =>
        JsonSerializer.Serialize(response, Options);

    /// <summary>Serializes one request as a single line (client/shim side; used by tests).</summary>
    public static string SerializeRequest(AgentIpcRequest request) =>
        JsonSerializer.Serialize(request, Options);
}
