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
        3. Write the plan you author to a JSON file — `{"scope": ["path", ...], "approach": "...",
           "testStrategy": "..."}` — and run `{{shimPath}} present <that file>`. It blocks until a human
           decides, and prints the decision.

        You do not have the task yet and must not start work. The daemon withholds it until your plan is
        approved, and `present` prints it after `TASK:` on approval. Nobody is watching this terminal, so
        do not stop to ask for input: everything you need comes from that one command.
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
    public static string? For(AgentIpcEndpointRole role, string shimPath) =>
        role == AgentIpcEndpointRole.Worker && !string.IsNullOrWhiteSpace(shimPath)
            ? Worker(shimPath)
            : null;
}
