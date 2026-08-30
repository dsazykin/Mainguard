using System.Collections.Generic;

namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The <c>mainguard-plan</c> executable the daemon writes into a <b>worker</b> jail's read-only IPC dir
/// (on the launch wrapper's PATH). It is the worker's half of the phase-2 plan gate: the worker inspects
/// its repository, writes the plan it authored, presents it, and <b>blocks</b> until a human decides.
///
/// <para>The block is real, not advisory: <c>await</c> holds the channel open until the daemon answers,
/// and the daemon answers only on a human decision. That is as true of the file-framed outbox transport
/// as of the socket — the answer file is not written until the handler returns (see
/// <see cref="AgentIpcShimTransport"/>). On approval the response carries the task prompt the
/// daemon had been withholding — which is why a worker cannot meaningfully start early. It does not have
/// the task.</para>
///
/// <para>The coordinator's jail never receives this shim, and this jail never receives
/// <see cref="AgentSpawnShim"/> — the daemon dispatches on the endpoint's role, so neither can reach the
/// other's operations even by sending the other's op name (<c>WorkerIpcRoleScopingTests</c> pins that).</para>
///
/// <para>python3 is part of the pre-baked jail toolchain (P2-07), so the shim needs no compiled binary
/// baked into the image (G-16 stays intact). <c>MAINGUARD_IPC_SOCKET</c> / <c>MAINGUARD_IPC_OUTBOX</c>
/// override the paths for tests only; inside a jail the default mount paths are the ones that exist.</para>
/// </summary>
public static class WorkerPlanShim
{
    /// <summary>
    /// The CLI verb for every op a worker endpoint serves — <b>single-sourced</b>, and the only place the
    /// two spellings of one operation are written down together.
    ///
    /// <para>A worker meets each op twice: as the verb it types (<c>present</c>) and as the wire op the
    /// daemon dispatches (<c>present_plan</c>). Keeping the correspondence in an object rather than in
    /// three prose renderings is what lets <c>AgentOperatingInstructionsTests</c> pin the worker's
    /// instructions against <see cref="AgentIpcRequest.WorkerOps"/> the way the coordinator's have been
    /// pinned since phase 3 §13.5: an op added without a verb, or a verb the instructions never teach, is
    /// a test failure rather than a capability the worker never learns it has.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Verbs =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            [AgentIpcRequest.BriefOp] = "brief",
            [AgentIpcRequest.PresentPlanOp] = "present",
            [AgentIpcRequest.RevisePlanOp] = "revise",
            [AgentIpcRequest.RescopePlanOp] = "rescope",
            [AgentIpcRequest.AwaitDecisionOp] = "await",
            [AgentIpcRequest.CommitWorkOp] = "commit",
        };

    /// <summary>
    /// The argument form of the re-scope op, interpolated into the shim's usage text, the shim's own
    /// refusal, <see cref="AgentOperatingInstructions.Worker"/>, and the daemon's refusals — the same
    /// single-sourcing <see cref="AgentSpawnShim.SpawnUsage"/> exists for, and for the same reason.
    ///
    /// <para><b>The id is required and is the APPROVED plan's id</b> — not a bare
    /// <c>rescope &lt;plan.json&gt;</c>. Naming what is being widened is what makes the daemon able to say
    /// "that plan is pending / was rejected / has escalated" instead of guessing which of a worker's plans
    /// the request meant; and it is the same shape as <c>revise</c>, so the two verbs differ in exactly one
    /// place — the word — rather than in their argument lists as well.</para>
    /// </summary>
    /// <remarks>
    /// The program name is deliberately NOT part of this string — the worker's shim is on PATH as
    /// <c>mainguard-plan</c> and reached by absolute path in its operating instructions, so a usage line
    /// carrying one spelling would be wrong in the other place. Same shape as
    /// <see cref="AgentSpawnShim.SpawnUsage"/>.
    /// </remarks>
    public const string RescopeUsage = "rescope <approved-plan-id> <plan.json>";

    /// <summary>The shim's full script text (LF newlines; written mode 0755 by the daemon). Composed
    /// from the shared <see cref="AgentIpcShimTransport"/>, which is the ONLY place either shim's
    /// transport is written.</summary>
    public static readonly string Script = $$""""
#!/usr/bin/env python3
"""mainguard-plan: present the plan YOU authored, and wait for the human.

You are a Mainguard worker. You do not start work until a human approves your plan.

Usage:
  mainguard-plan brief                  what you are here to plan (never the task itself)
  mainguard-plan present <plan.json>    present the plan you authored, then wait
  mainguard-plan revise <id> <plan.json>  re-present after a REJECTION, then wait
  mainguard-plan {{RescopeUsage}}
                                        widen an APPROVED plan, then wait
  mainguard-plan await <id>             block until the human decides
  mainguard-plan commit "<message>"     record the approved work on your own branch

plan.json is {"scope": ["path", ...], "approach": "...", "testStrategy": "..."}. Write it
OUTSIDE the repository (/tmp/plan.json) — /workspace is the tree your commit records.

`present`, `revise` and `rescope` block until the decision arrives and print it. On approval
the output includes TASK: followed by the work you are cleared to do — the daemon withholds
it until then, so there is nothing to start on before approval.

`revise` and `rescope` are NOT interchangeable, and the daemon will tell you which one you
wanted. `revise` answers a rejection: the human sent your plan back and you owe a new one,
and it spends a revision from your budget. `rescope` follows an APPROVAL: your plan was
accepted, you started work, and you found that doing the job properly needs a file the
approved scope does not cover. It spends no revision, and it is the ONLY legal way to widen.

While a re-scope is waiting on the human your EXISTING approval still stands: you are cleared
for exactly the scope that was approved before, and nothing you have already done is undone.
If it is refused you are still cleared for the original scope. Ask before you widen; if you
already touched the extra file, ask anyway -- every file outside the approved scope is put in
front of the human at verification either way, and a re-scope is how you say why.

`commit` is how finished work leaves this jail. The daemon commits everything in
/workspace onto your own branch; you supply only the message. Work you have not
committed does not survive the agent being stopped.

The message is ONE quoted argument, and it is a real git message: a subject line of at
most 72 characters, then a BLANK line, then as much body as you want. The daemon records
it verbatim and REFUSES anything it cannot record -- it will not shorten your subject or
flatten your paragraphs. Quote the whole thing: a shell splits an unquoted message on
whitespace, and your blank lines are gone before this program starts.

  mainguard-plan commit "fix(auth): recompute token expiry in UTC

  The clock read the host's local zone, so a token minted at 23:30 expired an hour
  early. Boundary tests cover the DST transition in both directions."
"""
""""
        + "\n" + AgentIpcShimTransport.PythonSource + "\n" + $$""""

def read_plan(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def report(response):
    status = response.get("status") or ""
    # Set only on a re-scope's decision: the id of the approved plan this one was widening. It is what
    # makes the three outcomes below say something DIFFERENT for a re-scope, and the difference is the
    # whole point -- a refused re-scope has not taken anything away, and a worker told the generic
    # "STOP" would abandon work it is still cleared to do.
    rescope_of = response.get("rescopeOf")
    if status == "Approved":
        if rescope_of:
            print("APPROVED: wider scope — plan %s replaces %s as what you are cleared to do."
                  % (response.get("planId", ""), rescope_of))
            print("WHEN DONE: mainguard-plan commit \"<subject>\\n\\n<body>\"  (uncommitted work is lost)")
            return 0
        print("APPROVED: plan %s" % response.get("planId", ""))
        print("TASK: %s" % (response.get("taskPrompt") or ""))
        # Said HERE, at the one moment the worker is cleared to start, because this is the only
        # output it is guaranteed to read after the gate opens. A finished worker that never
        # commits leaves nothing behind: the worktree goes with the jail.
        print("WHEN DONE: mainguard-plan commit \"<subject>\\n\\n<body>\"  (uncommitted work is lost)")
        return 0
    if status == "Rejected":
        remaining = response.get("revisionsRemaining")
        print("REJECTED: %s" % (response.get("feedback") or "(no feedback given)"))
        if rescope_of:
            print("STILL APPROVED: plan %s — you are cleared for its scope, exactly as before."
                  % rescope_of)
        print("REVISE: mainguard-plan revise %s <plan.json>  (%s revision(s) left)"
              % (response.get("planId", ""), remaining))
        return 0
    if status == "Escalated":
        print("ESCALATED: the revision budget is spent (%s of %s used)."
              % (response.get("revision"), response.get("maxRevisions")))
        if rescope_of:
            print("STILL APPROVED: plan %s — you are cleared for its scope, exactly as before."
                  % rescope_of)
            print("STOP WIDENING: do not ask to re-scope again. Finish what plan %s covers, or "
                  "report to the human and wait." % rescope_of)
            return 3
        print("STOP: do not attempt another plan. Report to the human and wait.")
        return 3
    print(json.dumps(response))
    return 0


def main(argv):
    # `await` has no deadline by design: this is the gate, and a human may take hours.
    timeout = None
    if len(argv) >= 2 and argv[1] == "brief":
        request = {"op": "brief"}
        timeout = 60
    elif len(argv) >= 3 and argv[1] == "present":
        request = {"op": "present_plan", "planJson": read_plan(argv[2]),
                   "title": " ".join(argv[3:]) or None}
    elif len(argv) >= 4 and argv[1] == "{{Verbs[AgentIpcRequest.RevisePlanOp]}}":
        request = {"op": "{{AgentIpcRequest.RevisePlanOp}}", "planId": argv[2],
                   "planJson": read_plan(argv[3]), "title": " ".join(argv[4:]) or None}
    elif len(argv) >= 2 and argv[1] == "{{Verbs[AgentIpcRequest.RescopePlanOp]}}":
        # The id is REQUIRED, and refusing here rather than defaulting to "whatever plan this worker
        # has" is the same call §13.3 made about a missing --title: a re-scope that guessed which
        # approval it was widening would produce a plausible card for the wrong authorisation. The
        # daemon refuses this too -- that is the enforcement; this is the affordance that costs no
        # round trip.
        if len(argv) < 4:
            sys.stderr.write(
                "mainguard-plan: rescope names the APPROVED plan you are widening, and the file\n"
                "holding the wider plan. Run: mainguard-plan {{RescopeUsage}}\n"
                "(`mainguard-plan brief` prints the id of your live plan.)\n")
            return 2
        request = {"op": "{{AgentIpcRequest.RescopePlanOp}}", "planId": argv[2],
                   "planJson": read_plan(argv[3]), "title": " ".join(argv[4:]) or None}
    elif len(argv) >= 3 and argv[1] == "await":
        request = {"op": "await_decision", "planId": argv[2]}
    elif len(argv) >= 2 and argv[1] == "commit":
        # The message is ALL the worker contributes. The repo, the worktree and the branch are the
        # daemon's to compute from this endpoint's identity, so there is nothing else to send.
        #
        # ONE argument, and a second one is REFUSED rather than joined (G4). ' '.join(argv[2:]) was
        # the other half of the message-destroying path: by the time a shell has split an unquoted
        # message on whitespace, the subject/blank-line/body the worker wrote is already gone, and
        # rejoining it with single spaces hides that a structure was lost. A commit message is the
        # durable record of what this agent did; a slip in it is worth a turn to correct.
        if len(argv) > 3:
            sys.stderr.write(
                "mainguard-plan: commit takes ONE quoted argument and %d were given. A shell splits an\n"
                "unquoted message on whitespace, so the subject, blank line and body you wrote are\n"
                "already one flat line by the time this runs. Quote the whole message -- newlines\n"
                "inside the quotes are kept.\n" % (len(argv) - 2))
            return 2
        request = {"op": "commit_work", "message": argv[2] if len(argv) > 2 else ""}
        timeout = 300
    else:
        sys.stderr.write(__doc__ or "usage: mainguard-plan present <plan.json>\n")
        return 2

    try:
        response = call(request, timeout)
    except (OSError, ValueError) as error:
        sys.stderr.write("mainguard-plan: cannot reach the Mainguard daemon: %s\n" % error)
        return 1

    if response.get("ok"):
        if response.get("brief") is not None:
            print(response["brief"])
            # The live plan's id and state, printed HERE because this is the only place a worker can
            # learn them without having kept the output of a call it may have made hours ago -- and
            # `rescope` REQUIRES that id. The id-less rescope refusal points at this command, so if this
            # line is missing that refusal is advice that does not work (the exact shape of defect G3).
            if response.get("planId"):
                print("PLAN: %s (%s)" % (response["planId"], response.get("status") or "unknown"))
            return 0
        if response.get("committed") is not None:
            # Distinguished, not collapsed: "nothing to commit" is an ok answer, and reporting it as a
            # commit would tell a worker its work is safe while its branch has not moved.
            if response.get("committed"):
                print("COMMITTED: %s on %s"
                      % (response.get("commitSha", ""), response.get("status", "")))
            else:
                print("NOTHING TO COMMIT: %s"
                      % (response.get("feedback") or "the worktree is clean"))
            return 0
        return report(response)

    for line in response.get("planErrors") or []:
        sys.stderr.write("mainguard-plan: plan rejected by schema: %s\n" % line)
    sys.stderr.write("mainguard-plan: %s\n" % response.get("error", "request refused"))
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
"""";
}
