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

    /// <summary>
    /// The role's operating instructions, written beside its shim. Phase 3 found that nothing ever told
    /// a jailed CLI its shim existed (see <see cref="AgentOperatingInstructions"/>), and the IPC dir is
    /// where the file goes because it is the one location already staged per-agent, per-role, and mounted
    /// into every jail. A worker additionally gets a copy at its worktree root, where a CLI that reads
    /// only its working directory will find it; a coordinator's <c>/workspace</c> is an empty tmpfs with
    /// no host side to write to, so for that role this path plus the launch flag are the delivery.
    /// </summary>
    public const string InstructionsFileName = "MAINGUARD.md";

    /// <summary>The instructions path as the jail sees it.</summary>
    public const string SandboxInstructionsPath = SandboxMount + "/" + InstructionsFileName;

    // ---- The outbox: the same channel, framed as files, for substrates whose mount cannot carry a
    // socket ---------------------------------------------------------------------------------------
    //
    // Docker's macOS file sharing (virtiofs / gRPC-FUSE) does NOT proxy AF_UNIX across the host/VM
    // boundary. The daemon runs natively on the Mac while jails run in the engine's Linux VM, so
    // <see cref="SocketFileName"/> is bind-mounted into the jail as an inert inode: it stat()s as a
    // socket and every connect() to it fails ECONNREFUSED. Measured, not inferred — a jail could not
    // reach a listening daemon at all, which made every coordinator tool dead on that platform.
    //
    // The outbox is that channel re-framed as regular files, which the same mount DOES carry in both
    // directions: the shim drops one request file and polls for its answer, the daemon polls for
    // requests and writes answers. Same JSON, same daemon handler, same blocking semantics (a plan
    // presentation still parks for as long as the human takes, because the answer file is simply not
    // written until the handler returns). What it costs is one READ-WRITE mount, nested inside the
    // read-only IPC dir, on the substrates that need it — see the mount's comment in
    // ContainerSpecBuilder for why that dir and nothing else.

    /// <summary>The outbox directory's name (inside the IPC dir).</summary>
    public const string OutboxDirName = "outbox";

    /// <summary>The outbox as the jail sees it — the ONE writable path in a coordinator jail.</summary>
    public const string SandboxOutboxPath = SandboxMount + "/" + OutboxDirName;

    /// <summary>Suffix of a request the shim has finished writing and the daemon may claim.</summary>
    public const string OutboxRequestSuffix = ".req";

    /// <summary>Suffix the shim stages a request under before renaming it to <see cref="OutboxRequestSuffix"/>
    /// — the rename is what makes "the daemon never reads half a request" structural rather than lucky.</summary>
    public const string OutboxStagingSuffix = ".tmp";

    /// <summary>Suffix a claimed request carries while its handler runs, in <see cref="InflightDirName"/>
    /// rather than in the outbox. Claiming by rename is what stops a second poll pass dispatching the
    /// same request again while the first is parked on a human; claiming ACROSS the writable boundary is
    /// what stops the jail editing a request the daemon has already decided to read.</summary>
    public const string OutboxClaimSuffix = ".busy";

    /// <summary>Suffix the daemon stages a response under, distinct from <see cref="OutboxStagingSuffix"/>
    /// so the two writers never name the same file.</summary>
    public const string OutboxResponseStagingSuffix = ".out";

    /// <summary>Suffix of a response the shim may read.</summary>
    public const string OutboxResponseSuffix = ".res";

    /// <summary>
    /// The daemon-side directory a claimed request is renamed INTO. It is a sibling of the outbox inside
    /// the IPC dir, which means it is inside the READ-ONLY mount and outside the read-write one: the jail
    /// can see its own claimed request and can touch nothing about it.
    ///
    /// <para><b>Why the claim leaves the jail's directory.</b> Claiming used to rename a request within
    /// the outbox, so the claimed path stayed in a directory the jail can write. Every check the daemon
    /// then made about that path — is it a link, how big is it — was a separate syscall from the read
    /// that followed, on a name the jail could still replace in between. Renaming ACROSS the boundary
    /// makes the claimed entry immutable to the jail, and that is what turns those checks from advice
    /// into guarantees: after this rename there is no second writer, so what was stat'd is what is
    /// opened. See <c>AgentIpcServer</c>'s outbox remarks for the defect this closes.</para>
    /// </summary>
    public const string InflightDirName = "inflight";

    /// <summary>
    /// The largest request the daemon will read off the outbox. A request line is a few hundred bytes;
    /// this is the bound on the one new thing a writable mount grants a jail — the ability to put bytes
    /// in the daemon's data root.
    ///
    /// <para>It is enforced by the READ — the daemon stops after this many bytes and refuses — not by a
    /// stat that precedes it. A stat is a promise about a path, and the outbox is a path the jail owns;
    /// the read is a promise about the daemon's own memory, which is the thing actually being
    /// protected.</para>
    /// </summary>
    public const int MaxOutboxRequestBytes = 64 * 1024;

    /// <summary>
    /// How many files one agent's outbox may hold before the daemon stops reading it and reclaims it.
    ///
    /// <para><see cref="MaxOutboxRequestBytes"/> bounds ONE request and nothing else, which left the
    /// directory unbounded in count: a jail could fill the host's disk inside the daemon's data root 64
    /// KiB at a time, and make the 100 ms sweep walk an ever-growing directory while it did. A shim
    /// writes exactly one request and blocks until its answer appears, so a healthy outbox holds a
    /// handful of files; sixty-four is roughly twenty concurrent calls, far past any legitimate burst and
    /// far below anything that costs the daemon.</para>
    /// </summary>
    public const int MaxOutboxFiles = 64;

    /// <summary>
    /// How many bytes one agent's outbox may hold in total before the daemon stops reading it and
    /// reclaims it. Sixteen full-size requests — the same reasoning as <see cref="MaxOutboxFiles"/>, for
    /// the axis a file count does not bound.
    /// </summary>
    public const long MaxOutboxBytes = 16L * MaxOutboxRequestBytes;

    /// <summary>
    /// The daemon-side outbox directory inside an agent's IPC dir. One function, used by the daemon that
    /// creates it, the launcher that names it as a mount source, and the spec builder that vets it — so
    /// "the read-write mount is that dir and nothing else" is a single fact rather than three spellings.
    /// </summary>
    public static string OutboxIn(string ipcDir) => System.IO.Path.Combine(ipcDir, OutboxDirName);

    /// <summary>The daemon-only claim directory inside an agent's IPC dir (see
    /// <see cref="InflightDirName"/>). Never a mount source of its own — it rides the read-only IPC
    /// mount, which is exactly what makes it daemon-only.</summary>
    public static string InflightIn(string ipcDir) => System.IO.Path.Combine(ipcDir, InflightDirName);

    /// <summary>The in-jail shim path for a role — what the instructions tell that CLI to run.</summary>
    public static string SandboxShimPath(AgentIpcEndpointRole role) =>
        SandboxMount + "/" + (role == AgentIpcEndpointRole.Worker ? PlanShimFileName : SpawnShimFileName);
}

/// <summary>Which shim an IPC endpoint publishes, and therefore which ops the daemon will serve on it.</summary>
public enum AgentIpcEndpointRole
{
    /// <summary>A coordinator jail: the four tools of coordinator contract §3, and nothing else.</summary>
    Coordinator,

    /// <summary>A managed worker jail: the plan gate ops.</summary>
    Worker,
}

/// <summary>One line-delimited JSON request from an in-jail shim.</summary>
/// <param name="PlanJson">A worker-authored plan document, validated against <c>TaskPlanSchema</c>.</param>
/// <param name="AgentId">
/// The worker a coordinator op names (<c>status</c> / <c>prompt</c> / <c>verify</c>). Phase 3: this is the
/// ONLY field on the wire that names another agent, and it is never trusted — every op that reads it
/// resolves the target through <c>OwnedWorker</c>, which requires the session to be a live child of the
/// calling coordinator <b>in the calling coordinator's own repo</b> (contract §7). A coordinator naming a
/// stranger's worker is answered exactly as it is for a worker that does not exist, so the channel is not
/// an existence oracle for other coordinators' fan-out.
/// </param>
/// <param name="Prompt">The steering text of a <c>prompt</c> op (contract §3 <c>send_worker_prompt</c>).</param>
/// <param name="Message">The commit subject of a <c>commit_work</c> op. The ONLY thing a worker
/// contributes to that commit: what is committed, where, and onto which branch are all computed
/// daemon-side from the endpoint's identity.</param>
public sealed record AgentIpcRequest(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("agentKind")] string? AgentKind = null,
    [property: JsonPropertyName("taskPrompt")] string? TaskPrompt = null,
    [property: JsonPropertyName("planId")] string? PlanId = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("planJson")] string? PlanJson = null,
    [property: JsonPropertyName("agentId")] string? AgentId = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("message")] string? Message = null)
{
    // ---- Coordinator ops — coordinator contract §3, and the list is EXHAUSTIVE -----------------
    //
    // These four constants ARE the coordinator's surface. `CoordinatorOps` below is the allow-list the
    // daemon dispatches against; anything absent from it is refused by name. Adding a member here is a
    // deliberate contract change (§3: "Adding to this list is a deliberate contract change, reviewed as
    // such — not an implementation detail"), and `CoordinatorRoleLockTests` is what makes it one: it
    // pins this set's exact contents, and pins that the daemon's served surface set-equals it.
    //
    // NOTE what that test does NOT do, because a citation is exactly what stops a reviewer checking:
    // it asserts against a hardcoded expectation in the test file, NOT against docs/design/
    // coordinator-contract.md. Nothing reads the contract markdown. Keeping this list and §3 in
    // agreement is a human review step, and calling it automated would be the more expensive lie.

    /// <summary><c>spawn_worker</c> — start a worker on a described task.</summary>
    public const string SpawnOp = "spawn";

    /// <summary><c>get_worker_status</c> — status of the workers this coordinator owns.</summary>
    public const string ListOp = "list";

    /// <summary><c>get_worker_status</c>, single-worker form. Same tool, scoped to one owned worker.</summary>
    public const string StatusOp = "status";

    /// <summary><c>send_worker_prompt</c> — steer a worker this coordinator owns.</summary>
    public const string PromptOp = "prompt";

    /// <summary><c>request_verification</c> — propose an owned worker's branch for daemon verification.</summary>
    public const string VerifyOp = "verify";

    /// <summary>
    /// The complete set of ops a coordinator endpoint will serve (contract §3). The daemon dispatches
    /// against this set rather than against a <c>switch</c>'s reachable cases, so "the list is exhaustive"
    /// is one testable object instead of a property of control flow that has to be re-read to be believed.
    ///
    /// <para>That sentence was aspirational until it was made true: <c>AgentSpawnService</c> used to route
    /// on a bare <c>switch</c> and this set was referenced by nothing but a test, so an added case served a
    /// fifth, unlisted coordinator tool with the suite green. The service now builds its handler table
    /// against this set and exposes the result as <c>ServedCoordinatorOps</c>, which the role-lock test
    /// requires to set-equal this — so a handler added without a contract change is unreachable, and one
    /// added with a contract change goes red here.</para>
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> CoordinatorOps =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            SpawnOp, ListOp, StatusOp, PromptOp, VerifyOp,
        };

    // Worker plan-gate ops (phase 2).

    /// <summary>What am I here to plan? Returns the brief — never the task prompt.</summary>
    public const string BriefOp = "brief";

    /// <summary>Present the plan I authored after inspecting the repo.</summary>
    public const string PresentPlanOp = "present_plan";

    /// <summary>Re-present a plan revised against the human's rejection feedback.</summary>
    public const string RevisePlanOp = "revise_plan";

    /// <summary>
    /// <c>rescope_plan</c> — present a revised plan against an <b>approved</b> one, because the work
    /// turned out to need a file the approved scope does not cover.
    ///
    /// <para><b>The defect this closes.</b> There is one live plan per worker and, until this op, no way
    /// to change what an approved one authorises. A worker that discovered mid-task that it had to touch a
    /// neighbouring file was answered <c>Plan '…' is already approved for this worker.</c> by
    /// <see cref="PresentPlanOp"/> and <c>only a rejected plan can be revised</c> by
    /// <see cref="RevisePlanOp"/>. Both refusals are correct about their own op and together they left a
    /// worker trying to stay legal with nowhere to go: exceed the scope silently, or stop. Measured on this
    /// branch before the fix, at the daemon, over the real socket.</para>
    ///
    /// <para><b>Why not <see cref="RevisePlanOp"/>.</b> A revision answers a REJECTION and spends the
    /// revision budget; a re-scope follows an APPROVAL and is the worker reporting that the job is bigger
    /// than it looked. They are refused in complementary states — <c>revise</c> requires
    /// <c>Rejected</c>, this requires <c>Approved</c> — so picking the wrong one is always refused,
    /// never silently accepted, and each refusal names the other by name. That mutual exclusion is what
    /// makes two similarly-spelled verbs safe for a model to be handed (§13.2's argument for detectable
    /// slips over plausible ones).</para>
    /// </summary>
    public const string RescopePlanOp = "rescope_plan";

    /// <summary>Block until the human decides. This call is the worker's gate.</summary>
    public const string AwaitDecisionOp = "await_decision";

    /// <summary>
    /// <c>commit_work</c> — record the approved work on this worker's own branch, so it exists after the
    /// jail does.
    ///
    /// <para><b>The defect this closes.</b> In the first end-to-end run a worker did the approved work and
    /// stopped with a 20-line UNCOMMITTED diff. Stopping it deleted the worktree, the diff went with it,
    /// <c>agent/&lt;id&gt;</c> carried no commit, and its merge-queue row joined the dead "the agent's
    /// sandbox is gone" entries. The verification trigger observes <c>refs/heads/agent/&lt;id&gt;</c>
    /// ADVANCING and then going quiet (<c>AgentRefWatcher</c> → <c>WorkerReadinessTrigger</c>); with no
    /// commit there was never anything to observe, so the loop ended one step short of the merge queue,
    /// silently. The worker's operating instructions had never mentioned committing.</para>
    ///
    /// <para><b>Why an op, and not "tell the worker to run git".</b> Measured against claude-code 2.1.251
    /// under the jail's real posture — default permission mode, one <c>--allowedTools</c> grant, for its
    /// own shim — a worker asked to commit could not even run <c>git status</c>: both attempts came back
    /// refused and it stopped without committing. In an interactive jail that is not a refusal but an
    /// approval prompt nobody can answer, which is the stall <c>preApprovedCommandArg</c> exists to fix.
    /// Widening the grant to raw <c>git</c> was the alternative and is strictly worse: the daemon would
    /// then be trusting a CLI to name the right branch. Here the worker supplies a MESSAGE and nothing
    /// else — repository, worktree and branch are computed daemon-side from the endpoint's own identity,
    /// the shape that makes <c>AgentRefMediator</c> safe (the agent cannot name a ref at all). It grants
    /// no capability the worker lacked: <c>WorktreeManager</c> gives every agent its own repository
    /// precisely so that committing is available to it.</para>
    /// </summary>
    public const string CommitWorkOp = "commit_work";

    /// <summary>
    /// The complete set of ops a worker endpoint will serve. Disjoint from <see cref="CoordinatorOps"/>
    /// by construction — the two sets share no member, and a test pins that, because the whole point of
    /// fixing the role on the endpoint is that neither role can reach the other's operations.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> WorkerOps =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            BriefOp, PresentPlanOp, RevisePlanOp, RescopePlanOp, AwaitDecisionOp, CommitWorkOp,
        };
}

/// <summary>
/// One line-delimited JSON response toward the shim. Errors are honest prose, no stacks.
/// </summary>
/// <param name="TaskPrompt">
/// The withheld task, released <b>only</b> alongside an approved decision. Everything before that point
/// answers with this field empty — a worker literally does not possess its task until a human approves.
/// </param>
/// <param name="RescopeOf">
/// On a <see cref="AgentIpcRequest.RescopePlanOp"/> decision, the id of the <b>approved</b> plan this one
/// was widening. It is what lets the shim report a refused re-scope truthfully: the worker's existing
/// authorisation is untouched, so the generic "STOP: do not attempt another plan" would tell it to abandon
/// work it is still cleared to do. Null on every ordinary decision.
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
    [property: JsonPropertyName("planErrors")] string[]? PlanErrors = null,
    [property: JsonPropertyName("commitSha")] string? CommitSha = null,
    [property: JsonPropertyName("committed")] bool? Committed = null,
    [property: JsonPropertyName("rescopeOf")] string? RescopeOf = null);

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
