namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The <c>mainguard-agent</c> executable the daemon writes into the coordinator's read-only IPC dir
/// (on the launch wrapper's PATH). A coordinator CLI runs it to spawn sub-agent CLIs through the
/// daemon — the worker then goes through the SAME spawn chain as an RPC spawn, so it lands in the
/// session store, streams to the UI as a subagent (P2-13), and gets its own jail + terminal.
///
/// <para>python3 is part of the pre-baked jail toolchain (P2-07 — verified by
/// <c>PrebakedToolchain_ShouldBeAvailableInLiveSession</c>), so the shim needs no compiled binary
/// baked into the image (G-16 stays intact). It speaks the newline-delimited JSON of
/// <see cref="AgentIpcProtocol"/> over the jail's bind-mounted Unix socket — or, where the mount
/// cannot carry a socket (macOS), over the file-framed outbox. Neither involves network egress, so the
/// channel stays A6-clean either way; see <see cref="AgentIpcShimTransport"/> for which is chosen and
/// why. <c>MAINGUARD_IPC_SOCKET</c> / <c>MAINGUARD_IPC_OUTBOX</c> override the paths for tests only;
/// inside a jail the default mount paths are the ones that exist.</para>
/// </summary>
public static class AgentSpawnShim
{
    /// <summary>
    /// The argument form of <c>spawn_worker</c>, <b>single-sourced</b> — it is interpolated into the
    /// shim's own <c>--help</c>, into the shim's refusal messages, and into
    /// <see cref="AgentOperatingInstructions.Coordinator"/>. Those are the only three places a model
    /// ever reads this command, and three spellings of one CLI is how they come to disagree.
    ///
    /// <para><b>Why this spelling, and not a positional title (contract §3 change, 2026-08-29).</b> The
    /// shim used to be <c>spawn &lt;agent-kind&gt; &lt;task prompt ...&gt;</c> and sent no title at all,
    /// which is what made <c>mainguard-plan brief</c> return the task verbatim. The obvious repair —
    /// a second positional, <c>spawn &lt;kind&gt; &lt;title&gt; &lt;task ...&gt;</c> — is unusable by a
    /// language model: an unquoted title silently eats the first word of the task, an unquoted task
    /// silently extends the title, and the caller cannot tell either happened. Two named flags fix both
    /// halves:</para>
    /// <list type="bullet">
    /// <item><b>Order cannot be inverted</b>, because each argument says which one it is. A positional
    /// pair can be swapped by a model that read the usage once and remembered the wrong order, and the
    /// result — the task on the approval card and the title withheld — is exactly backwards.</item>
    /// <item><b>The short argument's quoting slip is DETECTABLE.</b> <c>--title</c> takes exactly one
    /// argument, so an unquoted multi-word title leaves stray words where <c>--task</c> must be, and the
    /// shim refuses with that exact diagnosis instead of shipping a one-word title. That detectability is
    /// the property the positional form cannot have.</item>
    /// </list>
    ///
    /// <para><b>Defect G3 (2026-08-29): <c>--task</c> is QUOTED, and this used to say the opposite.</b>
    /// The original spelling ended <c>--task &lt;the task ...&gt;</c> and the instructions told a
    /// coordinator the long argument "needs no quotes at all", on the reasoning that the parser joins
    /// every remaining word. The parser does — but a shell parses the line first. The one command a
    /// coordinator has is reached through its CLI's Bash tool, so <c>--task rewrite add() and
    /// multiply()</c> never becomes argv at all: <c>bash: syntax error near unexpected token '('</c>,
    /// exit 2. In a stress run two of three first spawns died exactly there, and task text describing
    /// code carries <c>()</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>$</c>, <c>*</c> and quotes constantly. The
    /// taught form is now one the shell survives; the parser still joins a multi-word tail, so a
    /// coordinator using the old form keeps working whenever its text happens to be shell-safe.</para>
    ///
    /// <para><b>What quoting cannot fix, and what was done instead.</b> Nothing in this file executes
    /// after a shell has refused to parse the line — so no shim change can make that failure visible, and
    /// none is claimed to. What IS now visible is every spawn that reaches the shim and cannot be built:
    /// see <c>report_refused_spawn</c> below.</para>
    /// </summary>
    public const string SpawnUsage =
        "spawn <agent-kind> --title \"<short title>\" --task \"<the task ...>\"";

    /// <summary>The shim's full script text (LF newlines; written mode 0755 by the daemon). Composed
    /// from the shared <see cref="AgentIpcShimTransport"/>, which is the ONLY place either shim's
    /// transport is written.</summary>
    public static readonly string Script = $""""
#!/usr/bin/env python3
"""mainguard-agent: the Mainguard Coordinator's complete set of operations.

These four tools are ALL you can do. There is no fifth. You cannot merge, approve or reject
a plan, read another agent's terminal, or act on a worker you did not spawn — the daemon
refuses those, so there is nothing to be gained by trying.

Usage:
  mainguard-agent {SpawnUsage}
  mainguard-agent status [<agent-id>]                    get_worker_status
  mainguard-agent prompt <agent-id> <text ...>           send_worker_prompt
  mainguard-agent verify <agent-id>                      request_verification

  mainguard-agent list                                   alias of `status`

--title is the worker's BRIEF: a short, human-readable headline, and it is what the human
sees on the plan-approval card. --task is the work itself.

QUOTE BOTH. A shell parses this command line before Mainguard sees any of it, so an
unquoted task describing code dies before anything runs -- exit 2, no worker, and nothing
in the daemon's log, because nothing ran:

  right: --title "Validate arithmetic inputs" --task "rewrite add() and multiply()"
  wrong: --title "Validate arithmetic inputs" --task rewrite add() and multiply()
         -> bash: syntax error near unexpected token `('   (and no request is sent)

Use single quotes when the text itself contains double quotes. --title is exactly ONE
argument: an unquoted multi-word title is detected and refused rather than silently eating
the first words of your task.

A spawned worker does NOT receive its --task until a human approves the plan the worker
itself authors after inspecting the repository. Until then all it has is the --title, and
`prompt` and `verify` are refused for it — that is the gate working, not an error to route
around. This is why the two are separate arguments: a title that repeats the task is not a
brief, and the daemon refuses it.
"""

# Single-quoted on purpose: SpawnUsage carries the double quotes that make `--title` ONE
# argument, and they must survive into the text the model is shown.
SPAWN_ARGS = '{SpawnUsage}'
""""
        + "\n" + AgentIpcShimTransport.PythonSource + "\n" + """"

def spawn_form(argv):
    return "%s %s" % (argv[0], SPAWN_ARGS)


def spawn_request(argv):
    """Parse `spawn <agent-kind> --title <title> --task <task ...>`.

    Returns (request, None) or (None, refusal). Both flags are REQUIRED and ordered, and that
    is the point: with a fixed shape, every quoting slip lands on a token this parser can see,
    so it is refused with a diagnosis instead of silently mis-split. Deriving a title from the
    task is exactly the fallback this whole change removes, so it is never done here.
    """
    if len(argv) < 3 or not argv[2].strip() or argv[2].startswith("-"):
        return None, "spawn needs an agent kind first.\n  " + spawn_form(argv)
    if len(argv) < 4 or argv[3] != "--title":
        return None, (
            "spawn needs --title before --task. The title is the BRIEF the worker plans against "
            "and the headline the human decides from; the task is withheld from the worker until "
            "its plan is approved.\n  " + spawn_form(argv))
    if len(argv) < 5 or not argv[4].strip():
        return None, "--title needs a value.\n  " + spawn_form(argv)
    if len(argv) < 6 or argv[5] != "--task":
        stray = argv[5] if len(argv) > 5 else ""
        return None, (
            "--task must come next, and --title must be ONE quoted argument"
            + (" (found %s where --task was expected — quote the title)" % repr(stray) if stray else "")
            + ".\n  " + spawn_form(argv))
    task = " ".join(argv[6:]).strip()
    if not task:
        return None, "--task needs the task text.\n  " + spawn_form(argv)
    return {"op": "spawn", "agentKind": argv[2], "title": argv[4], "taskPrompt": task}, None


def report_refused_spawn(argv):
    """Tell the daemon a spawn was attempted here and could not be built.

    Defect G3: a spawn that failed before becoming a request left NOTHING anywhere. In a stress
    run two of three first spawns exited 2 with zero daemon log lines, and the outage read as
    three coordinators thinking. A refusal only this process can see is a refusal nobody can
    debug, so the attempt goes over the channel and the daemon refuses it there — which is what
    produces the warning and the `shim_spawn_refused` audit entry.

    It is sent as the incomplete spawn it is. NOTHING is derived: a field this parse did not
    establish is sent absent, and a spawn missing either a title or a task is refused at the
    channel before anything is minted (WorkerPlanGate.RefuseBrief, AgentSpawnService), so this
    can never become a worker. The diagnosis the coordinator reads is still the local one below,
    because only this process saw argv and can name the quoting slip.

    Best effort by construction: an unreachable daemon must not turn a usage error into a
    different error.
    """
    attempt = {"op": "spawn"}
    if len(argv) > 2 and argv[2].strip() and not argv[2].startswith("-"):
        attempt["agentKind"] = argv[2]
    if len(argv) > 4 and argv[3] == "--title":
        attempt["title"] = argv[4]
    try:
        call(attempt, 10)
    except Exception:
        pass


def main(argv):
    if len(argv) >= 2 and argv[1] == "spawn":
        request, refusal = spawn_request(argv)
        if refusal:
            report_refused_spawn(argv)
            sys.stderr.write("mainguard-agent: %s\n" % refusal)
            return 2
    elif len(argv) >= 2 and argv[1] in ("status", "list"):
        # `list` is the pre-phase-3 spelling of the same tool, kept so an existing coordinator
        # transcript does not break. Both carry an optional agent id.
        request = {"op": "status", "agentId": argv[2] if len(argv) >= 3 else None}
    elif len(argv) >= 4 and argv[1] == "prompt":
        request = {"op": "prompt", "agentId": argv[2], "prompt": " ".join(argv[3:])}
    elif len(argv) >= 3 and argv[1] == "verify":
        request = {"op": "verify", "agentId": argv[2]}
    else:
        sys.stderr.write(__doc__ or "usage: mainguard-agent " + SPAWN_ARGS + "\n")
        return 2

    try:
        response = call(request)
    except (OSError, ValueError) as error:
        sys.stderr.write("mainguard-agent: cannot reach the Mainguard daemon: %s\n" % error)
        return 1

    if response.get("ok"):
        if response.get("agentId"):
            print(response["agentId"])
        for agent in response.get("agents") or []:
            print(agent)
        if response.get("status") and not response.get("agents") and not response.get("agentId"):
            print(response["status"])
        return 0

    sys.stderr.write("mainguard-agent: %s\n" % response.get("error", "request refused"))
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
"""";
}
