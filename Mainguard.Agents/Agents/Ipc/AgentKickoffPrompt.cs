using System;

namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The <b>first user turn</b> a jailed worker's CLI is started with — the one thing that makes the
/// phase-2 loop able to begin at all.
///
/// <para><b>The deadlock this closes, observed live.</b> A worker jail launched
/// <c>claude --append-system-prompt &lt;operating instructions&gt;</c> and nothing else. A vendor CLI does
/// not act on a system prompt: it renders its banner and waits at an empty input box for a USER turn. So
/// the worker never ran <c>mainguard-plan brief</c>, never inspected the repository, never presented a
/// plan — outbox empty after six minutes, no transcript. And the only mechanism that can deliver text to
/// a worker's CLI is the coordinator's <c>send_worker_prompt</c>, which is itself plan-gated:
/// <c>"&lt;worker-id&gt; has not presented a plan yet — no work is authorised."</c> The worker could not
/// present a plan without a first turn, and nothing could send it a first turn until it had presented a
/// plan. <see cref="AgentOperatingInstructions"/> fixed the "nobody told it what it is" half; this is the
/// "nobody ever asked it to start" half, and without it the branch's core loop cannot run once.</para>
///
/// <para><b>Why this is not the daemon handing over the task (phase 2 §2.2/§2.3).</b> The withheld thing
/// is the TASK, and it stays withheld. This text is a compile-time constant of <c>(role, shimPath)</c> —
/// look at the signature: there is no task, no title, no worker id, no coordinator id in scope, so it
/// <i>cannot</i> carry the work even by mistake. What it says is "ask the daemon what you are here to
/// plan", i.e. run the <c>brief</c> op, which phase 2 §2.2 already sanctions as the one thing a worker
/// IS given up front. Every enforcement point is untouched and still answers no:
/// <c>WorkerPlanGate.TryReleaseTask</c> still requires an approved plan and has no override,
/// <c>MayWork</c>/<c>MayReceivePrompt</c>/<c>MayRequestVerification</c> still refuse, and the gate is
/// still ANDed into the merge queue as an <c>IMergeGate</c>. A worker that follows this text to the
/// letter arrives at the gate and blocks there, which is exactly where phase 2 wanted it.</para>
///
/// <para><b>Why a launch argument rather than a write into the worker's pty.</b> Delivering the first
/// turn at spawn time over the terminal would create a SECOND, ungated writer to an agent's stdin —
/// a mechanism that, the moment it exists, is capable of carrying the task and is kept from doing so
/// only by convention. Convention is precisely what MG-12 and contract §5 say not to rely on. As a
/// launch argument the text is fixed before the process starts and there is no runtime delivery path at
/// all: <c>AgentCliBinder.TrySendPromptAsync</c> remains the only way to write to a worker's CLI, still
/// <c>internal</c>, still plan-gated.</para>
///
/// <para><b>These strings describe; they do not enforce</b> — the same standing rule as
/// <see cref="AgentOperatingInstructions"/>. If this text and the daemon disagree, the daemon wins and
/// the text is the bug.</para>
/// </summary>
public static class AgentKickoffPrompt
{
    /// <summary>
    /// The worker's first turn: start, ask for your brief, read the code, present a plan, block. Short by
    /// design — the detail is already in the operating instructions this CLI was started with, and this
    /// text is billed on every spawn.
    /// </summary>
    public static string Worker(string shimPath) =>
        $$"""
        Begin now — this is your first turn as a Mainguard worker, and nothing else will prompt you.

        1. Run `{{shimPath}} brief` to learn what you are here to plan.
        2. Read the code in /workspace that the brief points at. Actually read it: your plan is judged on
           whether it describes THIS repository.
        3. Write the plan you author to a JSON file OUTSIDE the repository — /tmp/plan.json —
           `{"scope": ["path", ...], "approach": "...", "testStrategy": "..."}` — and run
           `{{shimPath}} present /tmp/plan.json`. It blocks until a human decides, and prints the decision.
        4. Once your plan is approved and the work is done, run `{{shimPath}} commit <message>`. That is
           the only way your work leaves this jail: the worktree is deleted when the agent stops, so
           anything you have not committed is lost.

        You do not have the task yet and must not start work. The daemon withholds it until your plan is
        approved, and `present` prints it after `TASK:` on approval. Nobody is watching this terminal, so
        do not stop to ask for input: everything you need comes from that one command.
        """;

    /// <summary>
    /// The first turn for a worker spawned while <b>plan mode was off</b>: ask for your task, do it,
    /// commit it.
    ///
    /// <para><b>Still a pure function of (role, shim path) — the task is NOT a parameter.</b> That is the
    /// property this class's remarks rest on, and it survives the toggle intact: the text names a command
    /// to run, and the daemon answers that command only for a worker <c>WorkerPlanGate.MayWork</c>
    /// authorises. Passing the task in here was the obvious implementation and would have put the work in
    /// a process argument list for every ungated worker, trading a structural guarantee ("this text
    /// cannot carry the task") for a conditional one ("it carries the task only when the caller meant
    /// it to").</para>
    ///
    /// <para>The closing steps are deliberately the same words as the gated turn: the half of a worker's
    /// job that happens after it knows its task did not change, and a second wording of it would
    /// drift.</para>
    /// </summary>
    public static string WorkerUngated(string shimPath) =>
        $$"""
        Begin now — this is your first turn as a Mainguard worker, and nothing else will prompt you.

        1. Run `{{shimPath}} task` to learn what you are here to do.
        2. Read the code in /workspace that the task points at. Actually read it before you change it.
        3. Do the work.
        4. Run `{{shimPath}} commit <message>` when it is done, and again after any further change. That
           is the only way your work leaves this jail: the worktree is deleted when the agent stops, so
           anything you have not committed is lost.

        Plan mode is off for this run, so there is no plan to write and nothing to wait for — `present`
        is refused and nobody is reviewing plans. A human still reviews your finished change before it
        merges. Nobody is watching this terminal, so do not stop to ask for input: everything you need
        comes from that one command.
        """;

    /// <summary>
    /// Renders the first turn for a role, or <c>null</c> when the role gets none.
    ///
    /// <para><b>A coordinator deliberately gets none, and the asymmetry is not an oversight.</b> A
    /// worker's terminal is input-locked daemon-side (P2-14/MG-5: <c>AgentRoles.Managed</c> sessions are
    /// view-only), so a worker with no first turn cannot be rescued by a human typing into it — that is
    /// what makes the missing turn a deadlock rather than an inconvenience. A coordinator's terminal is
    /// NOT locked, and its first turn is the operator's actual request, which the daemon does not have
    /// and must not invent: a synthetic one would make a coordinator start fanning out workers for work
    /// nobody asked for. So the coordinator's first turn comes from the human, as it must.</para>
    /// </summary>
    /// <param name="mode">
    /// The worker's own plan mode. Defaults to <see cref="Orchestrator.WorkerPlanMode.Gated"/>, so a
    /// caller that has not been taught about the toggle sends the turn that stops at the gate rather than
    /// the one that says there is none.
    /// </param>
    public static string? For(
        AgentIpcEndpointRole role, string shimPath,
        Orchestrator.WorkerPlanMode mode = Orchestrator.WorkerPlanMode.Gated) =>
        role == AgentIpcEndpointRole.Worker && !string.IsNullOrWhiteSpace(shimPath)
            ? mode == Orchestrator.WorkerPlanMode.Ungated ? WorkerUngated(shimPath) : Worker(shimPath)
            : null;
}
