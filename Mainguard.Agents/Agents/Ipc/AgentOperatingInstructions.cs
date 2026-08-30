using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Orchestrator;

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
    /// <param name="workerMode">
    /// The mode the workers this coordinator spawns will be held under. With plan mode off, four
    /// paragraphs of this text — the withheld task, "you do not write task plans", the refusal of
    /// <c>prompt</c>/<c>verify</c> at the gate, and the cap-refusal advice — describe a gate the daemon
    /// is not applying. A coordinator that believed them would report a stall that is not happening and
    /// would treat a working <c>prompt</c> as a bug.
    /// </param>
    public static string Coordinator(
        InstalledAdapterCatalog adapters, WorkerPlanMode workerMode = WorkerPlanMode.Gated)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var shimPath = AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator);
        var installedKinds = adapters.InstalledKinds();
        var gated = workerMode == WorkerPlanMode.Gated;

        // The two halves of every mode-dependent paragraph, kept next to each other so a change to one
        // is a change made while looking at the other. Interpolated below at the point each replaces.
        var briefSection = gated
            ? """
        ## `--title` is the worker's brief; `--task` is the work, and it is withheld

        These are two different things and the daemon keeps them apart. **`--title` is all the worker
        gets up front** — it runs its own shim's `brief`, reads that title, and inspects the repository
        against it. **`--task` is withheld until a human approves the plan the worker writes.** The
        title is also the headline on that approval card, so it is what the human decides from.
        """
            : """
        ## `--title` is the worker's headline; `--task` is the work

        These are two different things and the daemon keeps them apart. **`--title` is a short headline**
        — it names the worker on the merge queue and in `status`, and it is what a human reads first.
        **`--task` is the work**, and with plan mode off the worker is given it at once.
        """;

        var planAuthorshipSection = gated
            ? """
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
        """
            : """
        **Plan mode is off.** The operator turned plan approvals off, so a worker receives its task at
        spawn and starts implementing immediately. There is no plan, no approval card, and no worker
        waiting on a human — `prompt` and `verify` work from the moment a worker exists.

        **You still do not write task plans.** You cannot see the code, so anything you wrote would be a
        guess the worker would have to undo. Write a clear `--task` and let the worker decide how.

        ## When spawning is refused

        If `spawn` is refused for the worker cap, the workers holding those slots are genuinely working.
        Do not retry in a loop: report that the cap is full and wait for one to finish. `status` tells
        you what each of them is doing; say that.
        """;

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

        {briefSection}

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

        {planAuthorshipSection}

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
    ///
    /// <para><b>The opening half is mode-dependent (2026-08-30).</b> With the operator's plan-mode toggle
    /// off, every sentence about withholding, presenting and blocking is <i>false</i> for this worker —
    /// and instructions that assert a gate the daemon is not applying are worse than none: a worker that
    /// followed them would present a plan nobody is reviewing and then block on <c>await</c> forever,
    /// holding a jail, having already been handed its task. The closing half (commit, or the work is
    /// lost) is unconditional and is shared verbatim between the two, because it is the half that has
    /// nothing to do with plans and everything to do with the worktree dying with the jail.</para>
    /// </summary>
    public static string Worker(WorkerPlanMode mode = WorkerPlanMode.Gated)
    {
        var shimPath = AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker);

        var opening = mode == WorkerPlanMode.Ungated
            ? $$"""
        # You are a Mainguard worker

        You have a repository checked out at `/workspace`, and a task waiting for you.

        ## What to do first

        ```
        {{shimPath}} task                        the work you are here to do
        ```

        Then read the code it points at — actually read it — and do the work.

        ## Plan mode is off for this run

        There is no plan to write and no approval to wait for: the operator turned plan approvals off, so
        the task above is yours the moment you ask for it. `present` is refused, and refusing it is the
        point — **nobody is reviewing plans, so a plan you presented would wait forever.** Do the work.

        A human still reviews the finished change: your branch is verified and put in front of them
        before anything merges. The step that moved is the one before you started, not the one after.
        """
            : $$"""
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
        {{shimPath}} revise <id> <plan.json>     re-present after a REJECTION, then wait
        {{shimPath}} {{WorkerPlanShim.RescopeUsage}}   widen an APPROVED plan, then wait
        {{shimPath}} await <id>                  block until the human decides
        ```

        `plan.json` is `{"scope": ["path", ...], "approach": "...", "testStrategy": "..."}`. Write it
        **outside the repository** — `/tmp/plan.json` — because everything in `/workspace` is what your
        commit records, and your plan file is not part of the change.

        ## The part that matters

        `present`, `revise` and `rescope` **block** until the human decides, and print the decision. On approval the
        output includes `TASK:` followed by the work you are cleared to do. **The daemon withholds that
        text until then**, so there is genuinely nothing to start on before approval — not because you
        are being polite, but because the work has not been given to you.

        A rejection is feedback, not death: it comes back with the reason, and you revise and re-present.
        Your revision budget is finite and the daemon reports what is left; when it is spent the task
        escalates to the human rather than looping.

        Once you are approved the task is yours to re-read at any time, and asking again costs nothing:

        ```
        {{shimPath}} task                        the work you were cleared to do
        ```

        Asked BEFORE approval, that same command tells you what you are still waiting on rather than a
        task you have not been given — the honest answer, and never a head start.

        ## If the work needs a file your approved scope does not cover, ASK

        ```
        {{shimPath}} {{WorkerPlanShim.RescopeUsage}}
        ```

        The `scope` in the plan a human approved is what you are cleared to change. Discovering mid-task
        that the job cannot be done properly without touching a neighbouring file is normal, and there is
        exactly one legal move: present a wider plan against the approved one with `rescope`, and let the
        human decide it the way they decided the first.

        - **`rescope` is not `revise`.** `revise` answers a rejection and spends a revision from your
          budget. `rescope` follows an approval, spends none, and is the only way to widen. Pick the wrong
          one and the daemon refuses it and names the other — but read this once and you will not have to.
        - **Your existing approval stands the whole time.** While the human is deciding you are cleared
          for exactly the scope that was approved before, and nothing you have already done is undone. If
          they refuse, you are still cleared for the original scope — continue there, or stop and say why.
        - **Ask even if you already touched the file.** Every file outside the approved scope is put in
          front of the human at verification and blocks the merge until they acknowledge it, whether or
          not you asked. Asking is how they hear your reason before they see the diff; not asking only
          means they see it without one.
        - **A widening the human keeps refusing eventually stops being available.** A refused re-scope is
          revised and re-presented like any other plan, on its own budget, and the daemon reports what is
          left; once that is spent you may not ask to widen again. Finish what your approved plan covers,
          or report to the human and wait.
        """;

        // The declaration is asked of a worker that has an approved `approach` to depart from, and of no
        // one else. Both halves are written here, next to each other, so a change to one is made while
        // looking at the other — the same discipline the coordinator's mode-dependent paragraphs use.
        var gatedWorker = mode != WorkerPlanMode.Ungated;

        // The commit line an UNGATED worker is shown carries no declaration flags: it has no approved
        // approach, so the daemon neither requires nor records one, and teaching a flag whose answer would
        // be "not recorded — there is no approved approach" is how instructions start describing a
        // mechanism that is not applied here (MG-12, one layer up).
        var commitLine = gatedWorker
            ? $"{shimPath} {WorkerPlanShim.CommitUsage}"
            : $"{shimPath} commit \"<message>\"          record your work on your own branch";

        var commitExample = $$"""
        {{shimPath}} commit "fix(auth): recompute token expiry in UTC

        The clock read the host's local zone, so a token minted at 23:30 expired an hour early.

        Boundary tests cover the DST transition in both directions."{{(gatedWorker ? " --no-deviations" : "")}}
        """;

        var deviationSection = !gatedWorker
            ? ""
            : """
        ### Say whether you departed from your approved approach

        Exactly one of these is REQUIRED on every commit, after the message:

        ```
        --no-deviations                          this work follows the approved approach
        --deviated "<what you did differently, and why>"     it does not, in this way
        ```

        `--deviated` may be repeated, once per departure. A commit that carries neither is refused —
        nothing is committed, nothing is lost, and you run it again with an answer.

        **Why you are being asked, since nothing can check it.** The human approved a `scope` and an
        `approach`. The scope is a file list and the daemon compares your diff against it. The approach is
        prose, and nothing compares anything against it: your branch's tests are tests **you wrote**, so a
        change that does the opposite of what you proposed passes them exactly as well as one that does
        not. This declaration is the only place that difference can surface before a human merges it.

        So the bar is not "did I stay inside the file list" — that is a different question, already
        answered elsewhere. The bar is: **would the person who read my plan be surprised by my diff?** If
        the plan said you would leave existing behaviour alone and you changed it; if it said you would
        not add validation and you added it; if you replaced the technique you described with a different
        one — that is a deviation, and it is a normal thing to have done. Declare it and say why. A
        departure you declare is a line the reviewer reads next to your approach; one you do not is
        something they find in the diff, or do not find at all.

        `--no-deviations` is an assertion, not a default. Make it when you have re-read your approved
        approach against what you actually wrote — not because it is the shorter flag.

        """;

        return opening + "\n\n" + $$"""
        ## When the work is done, commit it — nothing else will

        ```
        {{commitLine}}
        ```

        **This is the only way your work leaves this jail.** Your worktree is deleted when the agent is
        stopped, so an uncommitted change is simply lost: no review, no merge-queue entry, no record that
        you did anything at all. That has already happened once — a worker finished its task, stopped
        with the diff uncommitted, and the work went with the sandbox.

        What that command does, so you know what you are asking for: the daemon commits **everything in
        `/workspace`** onto **your own branch** — the one already checked out, and the only one you have.
        You supply the message and nothing else. It never pushes, never merges, and never touches the
        user's main branch; a human decides all of that later, after reading your diff.

        ### The message is a real git message, and it is recorded verbatim

        A subject line of **at most 72 characters**, then a **blank line**, then as much body as the
        change deserves. That is the shape git means and the shape a human reads at review, and the
        daemon writes exactly what you send — it will not shorten your subject or flatten your
        paragraphs. **A message it cannot record is refused, with the reason, and nothing is committed;**
        fix the message and run the command again.

        **Quote the whole message as ONE argument.** A shell reads your command line first, so an
        unquoted message loses its blank lines before this program ever starts — and a second argument
        is refused rather than joined, because a message that arrived flattened cannot be un-flattened.

        ```
        {{commitExample}}
        ```

        {{deviationSection}}
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
    /// <param name="mode">
    /// The operator's plan-mode setting as it applies to THIS jail — the worker's own mode, or (for a
    /// coordinator) the mode the workers it is about to spawn will get. Defaults to
    /// <see cref="WorkerPlanMode.Gated"/> so a caller that has not been taught about the toggle renders
    /// the text that describes a gate, never the text that promises there is none.
    /// </param>
    public static string For(
        AgentIpcEndpointRole role, InstalledAdapterCatalog adapters,
        WorkerPlanMode mode = WorkerPlanMode.Gated) =>
        role == AgentIpcEndpointRole.Coordinator ? Coordinator(adapters, mode) : Worker(mode);
}
