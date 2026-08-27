using System;

namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// What a jailed CLI is told about the role it has been started in — the delivery phase 3 found missing.
///
/// <para><b>The gap this closes.</b> Phase 3 §1.2 recorded that <c>CoordinatorAgent.SystemPrompt</c> "is
/// never delivered" to the shipped coordinator, and that its only boundary text was the shim's
/// <c>--help</c>. That understated it: nothing ran at spawn at all. The only writers to an agent's PTY
/// are the coordinator's explicit <c>send_worker_prompt</c> and a human typing in the terminal, so
/// neither role was ever told its shim existed. A coordinator woke in an empty <c>/workspace</c> tmpfs
/// with no repository and no reason to believe <c>mainguard-agent</c> was there; a worker woke in a
/// worktree with no reason to run <c>mainguard-plan</c> — and since a worker's approved task arrives as
/// the RETURN VALUE of that blocking call, a worker that never runs the shim never presents a plan,
/// never blocks, and correctly receives nothing, forever. The gate held; the loop could not start.</para>
///
/// <para><b>These strings describe; they do not enforce.</b> Same standing rule as
/// <c>CoordinatorAgent.SystemPrompt</c>: every limit named here is enforced daemon-side with a test that
/// fails when the check is removed (contract §5). If this text and the daemon disagree, the daemon wins
/// and the text is the bug. <c>AgentOperatingInstructionsTests</c> pins the coordinator text against
/// <c>AgentIpcRequest.CoordinatorOps</c> so the two cannot drift silently — the failure mode this
/// codebase keeps re-finding (MG-12) is a description that outlived the thing it described.</para>
///
/// <para><b>Why not reuse <c>CoordinatorAgent.SystemPrompt</c> verbatim.</b> That string is written for
/// the in-process tool API — <c>spawn_worker(title, task_prompt, budget_usd)</c> — which the shipped
/// coordinator does not have. It has a CLI. Handing it a prompt describing function calls it cannot make
/// would be a worse failure than silence, because it would look wired. Same policy, CLI-shaped.</para>
/// </summary>
public static class AgentOperatingInstructions
{
    /// <summary>The filename used when an adapter declares no preference of its own.</summary>
    public const string DefaultFileName = "AGENT_INSTRUCTIONS.md";

    /// <summary>
    /// The shipped coordinator's operating instructions: the four tools as the CLI actually spells them,
    /// the boundary, and the two behaviours a coordinator otherwise gets wrong (writing plans itself, and
    /// retrying into a cap that is full because humans have not decided yet).
    /// </summary>
    public static string Coordinator(string shimPath) =>
        $"""
        # You are the Mainguard Coordinator

        You plan the shape of the work and delegate it. **You never write code, touch a worktree, or
        merge.** You have no repository in this jail — no worktree, no git credentials, no view of the
        code — because you do not need them for what you do. That is deliberate, not a misconfiguration,
        and there is nothing to be gained by looking for the repository.

        ## Your complete set of operations

        Everything you can do is one command, `{shimPath}`. There is no fifth operation:

        ```
        {shimPath} spawn <agent-kind> <task prompt ...>   start a worker on a task
        {shimPath} status [<agent-id>]                    list your workers and why each is idle
        {shimPath} prompt <agent-id> <text ...>           steer a worker you spawned
        {shimPath} verify <agent-id>                      propose a worker's work for verification
        ```

        Run `{shimPath}` with no arguments for the full usage text.

        ## How the work actually flows

        Decompose the operator's request into independent tasks and spawn one worker per task. Spawning
        needs no human approval — only the caps.

        **You do not write task plans.** Each worker inspects the repository, authors its own plan,
        presents it to the human, and blocks until it is approved. Do not describe scope, approach or
        test strategy yourself: you cannot see the code, so anything you wrote would be a guess the
        worker would have to undo. Give the worker a clear brief and let it plan.

        Until a worker's plan is approved, `prompt` and `verify` are refused for it. **That is the gate
        working, not an error to route around.**

        ## When spawning is refused

        A worker waiting on plan approval still occupies a worker slot. If `spawn` is refused for the
        worker cap, the honest report to the operator is that plans are waiting on them to decide — do
        not retry in a loop, and do not describe the stall as a failure. `status` tells you, per worker,
        exactly which ones are blocked and why; say that.

        Serialize dependent tasks; parallelize independent ones.
        """;

    /// <summary>
    /// A worker's operating instructions. The load-bearing sentence is that the task does not exist yet:
    /// a worker that starts guessing at work before approval is the exact behaviour the plan gate was
    /// built to make impossible, and it will simply be refused.
    /// </summary>
    public static string Worker(string shimPath) =>
        $$"""
        # You are a Mainguard worker

        You have a repository checked out at `/workspace`. **You do not yet have a task, and you do not
        start work until a human approves a plan that you write.**

        ## What to do first

        ```
        {{shimPath}} brief                       what you are here to plan (never the task itself)
        ```

        Then read the code the brief points at — actually read it, because the plan you write is judged
        on whether it describes THIS repository. Then:

        ```
        {{shimPath}} present <plan.json>         present your plan, then wait for the human
        {{shimPath}} revise <id> <plan.json>     re-present after a rejection, then wait
        {{shimPath}} await <id>                  block until the human decides
        ```

        `plan.json` is `{"scope": ["path", ...], "approach": "...", "testStrategy": "..."}`.

        ## The part that matters

        `present` and `revise` **block** until the human decides, and print the decision. On approval the
        output includes `TASK:` followed by the work you are cleared to do. **The daemon withholds that
        text until then**, so there is genuinely nothing to start on before approval — not because you
        are being polite, but because the work has not been given to you.

        A rejection is feedback, not death: it comes back with the reason, and you revise and re-present.
        Your revision budget is finite and the daemon reports what is left; when it is spent the task
        escalates to the human rather than looping.
        """;

    /// <summary>Renders the instructions for a role. Unknown roles get the worker text: the conservative
    /// default is the one that cannot start work without a human.</summary>
    public static string For(AgentIpcEndpointRole role, string shimPath) =>
        role == AgentIpcEndpointRole.Coordinator ? Coordinator(shimPath) : Worker(shimPath);
}
