using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;

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
///
/// <para><b>Every entry point here takes the daemon's own <see cref="InstalledAdapterCatalog"/>, and none
/// of them takes a shim path (defect G2).</b> Both used to be caller-supplied, one of them optional, and
/// two call sites in one spawn chain then disagreed: the launcher passed the catalog into the
/// <c>--append-system-prompt</c> copy while <c>AgentIpcServer</c> omitted it from the <c>MAINGUARD.md</c>
/// copy, so a jail with six CLIs installed was handed a file that said "(none installed on this machine)"
/// and a flag that listed all six. The defect was not the missing argument; it was that a rendering could
/// be reached without the thing it describes. It cannot now: the set comes from the catalog the refusal
/// itself reads, and the shim path is derived from the role — so a third call site gets the machine's
/// real state whatever it forgets, and DELIVERING the text (writing the file, appending the flag) is a
/// separate job from producing it.</para>
/// </summary>
public static class AgentOperatingInstructions
{
    /// <summary>The filename used when an adapter declares no preference of its own.</summary>
    public const string DefaultFileName = "AGENT_INSTRUCTIONS.md";

    /// <summary>
    /// How the installed agent kinds are spelled wherever they are named — in the coordinator's operating
    /// instructions, and in the refusal it gets for naming one that is not installed.
    ///
    /// <para>Shared on purpose: those are the same claim about the same set, made to the same reader
    /// seconds apart, and two renderings of one set is how they come to disagree. An empty set renders as
    /// a sentinel rather than as nothing, because "one of: " followed by silence reads as a bug in the
    /// text rather than as the real (and deliberately permissive) state of an unprovisioned box.</para>
    /// </summary>
    public static string SpellKinds(IReadOnlyCollection<string>? installedKinds) =>
        installedKinds is { Count: > 0 }
            ? string.Join(", ", installedKinds.Select(k => $"`{k}`"))
            : "(none installed on this machine)";

    /// <summary>
    /// The shipped coordinator's operating instructions: the four tools as the CLI actually spells them,
    /// the boundary, and the two behaviours a coordinator otherwise gets wrong (writing plans itself, and
    /// retrying into a cap that is full because humans have not decided yet).
    /// </summary>
    /// <param name="adapters">
    /// The daemon's catalog of installed agent CLIs — the same object the <c>spawn</c> refusal reads.
    ///
    /// <para><b>Why the catalog, and not prose or a list.</b> The first thing a real coordinator did with
    /// these instructions was run <c>spawn coder</c> — a natural reading of <c>spawn &lt;agent-kind&gt;</c>
    /// when nothing anywhere says what a kind is. <c>coder</c> is not an installed adapter, so its jail
    /// came up with no CLI in it at all and the shim answered <c>Ok</c> anyway. Writing the kinds into this
    /// text as prose would have been the other half of the same defect: a hardcoded list stops describing
    /// the machine the first time a user installs or removes a CLI (MG-12). Taking a caller-supplied LIST
    /// was that same half one layer down (G2): the argument was optional, one of the two call sites in a
    /// spawn omitted it, and the file copy of these instructions told a coordinator that nothing was
    /// installed. A catalog cannot be omitted and cannot be a stale copy — it re-reads the registry.</para>
    /// </param>
    public static string Coordinator(InstalledAdapterCatalog adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var shimPath = AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator);
        var installedKinds = adapters.InstalledKinds();

        return $"""
        # You are the Mainguard Coordinator

        You plan the shape of the work and delegate it. **You never write code, touch a worktree, or
        merge.** You have no repository in this jail — no worktree, no git credentials, no view of the
        code — because you do not need them for what you do. That is deliberate, not a misconfiguration,
        and there is nothing to be gained by looking for the repository.

        ## Your complete set of operations

        Everything you can do is one command, `{shimPath}`. There is no fifth operation:

        ```
        {shimPath} {AgentSpawnShim.SpawnUsage}
        {shimPath} status [<agent-id>]                    list your workers and why each is idle
        {shimPath} prompt <agent-id> <text ...>           steer a worker you spawned
        {shimPath} verify <agent-id>                      propose a worker's work for verification
        ```

        Run `{shimPath}` with no arguments for the full usage text.

        ## `--title` is the worker's brief; `--task` is the work, and it is withheld

        These are two different things and the daemon keeps them apart. **`--title` is all the worker
        gets up front** — it runs its own shim's `brief`, reads that title, and inspects the repository
        against it. **`--task` is withheld until a human approves the plan the worker writes.** The
        title is also the headline on that approval card, so it is what the human decides from.

        - **Quote both.** Your command line is read by a shell before Mainguard sees any of it, so an
          unquoted task describing code — `add()`, `a && b`, `$HOME`, `*.cs` — dies with
          `syntax error near unexpected token` before anything runs: no worker, and no record. Write
          `--task "rewrite add() and multiply() so they reject non-numbers"`, and use single quotes
          when the text itself contains a double quote.
        - **`--title` is exactly ONE argument.** An unquoted multi-word title is detected and refused,
          so that slip can never quietly eat the first words of your task.
        - **Write the title like a pull-request title** — short, specific, human-readable:
          `--title "Fix token expiry in the auth clock"`.
        - **The title must not be the task.** A spawn whose title repeats its task is refused: a brief
          that is the task is not a brief, and it would hand the worker its work before anyone had read
          a plan. Name the area to plan against in the title; put the work in `--task`.

        Both are required. A spawn missing either is refused rather than guessed at, and the refusal
        names the form to use.

        ## `<agent-kind>` is one of the CLIs installed on this machine

        {SpellKinds(installedKinds)}

        That is the complete list. Any other name — including plausible ones like `coder`, `worker` or
        `engineer` — is refused, and the refusal names the list again. Do not invent a kind, and do not
        pick one by what the task sounds like: pick the CLI you want to do the work.

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
    }

    /// <summary>
    /// A worker's operating instructions. Two sentences are load-bearing, and they are the two ends of the
    /// loop — each of which was found missing in production, a run apart.
    ///
    /// <para>At the start: the task does not exist yet. A worker that starts guessing at work before
    /// approval is the exact behaviour the plan gate was built to make impossible, and it will simply be
    /// refused.</para>
    ///
    /// <para>At the end: <b>work that is not committed does not exist.</b> The first end-to-end run ended
    /// with a worker that had done the approved work and stopped on a 20-line uncommitted diff — its
    /// instructions had never mentioned committing — and stopping the agent deleted the worktree with the
    /// work still in it. The daemon's readiness signal is the agent's branch advancing and then going
    /// quiet, so a worker that never commits is indistinguishable, to every mechanism downstream, from a
    /// worker that did nothing at all.</para>
    /// </summary>
    public static string Worker()
    {
        var shimPath = AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker);

        return $$"""
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

        `plan.json` is `{"scope": ["path", ...], "approach": "...", "testStrategy": "..."}`. Write it
        **outside the repository** — `/tmp/plan.json` — because everything in `/workspace` is what your
        commit records, and your plan file is not part of the change.

        ## The part that matters

        `present` and `revise` **block** until the human decides, and print the decision. On approval the
        output includes `TASK:` followed by the work you are cleared to do. **The daemon withholds that
        text until then**, so there is genuinely nothing to start on before approval — not because you
        are being polite, but because the work has not been given to you.

        A rejection is feedback, not death: it comes back with the reason, and you revise and re-present.
        Your revision budget is finite and the daemon reports what is left; when it is spent the task
        escalates to the human rather than looping.

        ## When the work is done, commit it — nothing else will

        ```
        {{shimPath}} commit <message>            record your work on your own branch
        ```

        **This is the only way your work leaves this jail.** Your worktree is deleted when the agent is
        stopped, so an uncommitted change is simply lost: no review, no merge-queue entry, no record that
        you did anything at all. That has already happened once — a worker finished its task, stopped
        with the diff uncommitted, and the work went with the sandbox.

        What that command does, so you know what you are asking for: the daemon commits **everything in
        `/workspace`** onto **your own branch** — the one already checked out, and the only one you have.
        You supply the message and nothing else. It never pushes, never merges, and never touches the
        user's main branch; a human decides all of that later, after reading your diff.

        Commit at meaningful points as you go, not only at the end, and commit again after any further
        change. Once your branch stops moving the daemon runs the repository's tests against it on its
        own and puts it in front of a human — so the last thing you do is commit, then report what you
        did, then stop.
        """;
    }

    /// <summary>Renders the instructions for a role. Unknown roles get the worker text: the conservative
    /// default is the one that cannot start work without a human.</summary>
    /// <param name="adapters">The daemon's installed-CLI catalog. Only a coordinator has a <c>spawn</c>,
    /// so only the coordinator text reads it — but it is required on this entry point too, because an
    /// optional one is exactly what let a caller render a coordinator's instructions from nothing.</param>
    public static string For(AgentIpcEndpointRole role, InstalledAdapterCatalog adapters) =>
        role == AgentIpcEndpointRole.Coordinator ? Coordinator(adapters) : Worker();
}
