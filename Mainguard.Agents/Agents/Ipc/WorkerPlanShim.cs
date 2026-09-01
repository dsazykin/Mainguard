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
            [AgentIpcRequest.TaskOp] = "task",
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

    /// <summary>
    /// The argument form of <c>commit</c> for a <b>plan-gated</b> worker, single-sourced into the shim's
    /// usage text, the shim's own refusal, <see cref="AgentOperatingInstructions.Worker"/> and the
    /// daemon's refusal — the same reason <see cref="RescopeUsage"/> exists.
    ///
    /// <para><b>The declaration is REQUIRED and has no silent form.</b> An optional flag would be absent
    /// on exactly the runs that needed it. The two spellings are mutually exclusive and one of them must
    /// be present, so "I checked, and I did not depart from the approach" is a thing a worker SAYS rather
    /// than a thing a reader infers from an empty field — which is the distinction the whole
    /// <see cref="Orchestrator.DeviationDeclaration"/> enum exists to keep.</para>
    /// </summary>
    public const string CommitUsage =
        "commit \"<message>\" --no-deviations | --deviated \"<what you did differently, and why>\"";

    /// <summary>The shim's full script text (LF newlines; written mode 0755 by the daemon). Composed
    /// from the shared <see cref="AgentIpcShimTransport"/>, which is the ONLY place either shim's
    /// transport is written.</summary>
    public static readonly string Script = $$""""
#!/usr/bin/env python3
"""mainguard-plan: present the plan YOU authored, and wait for the human.

You are a Mainguard worker. Your operating instructions say whether this run needs a plan.

Usage:
  mainguard-plan brief                  what you are here to plan (never the task itself)
  mainguard-plan task                   the work you are cleared to do
  mainguard-plan present <plan.json>    present the plan you authored, then wait
  mainguard-plan revise <id> <plan.json>  re-present after a REJECTION, then wait
  mainguard-plan {{RescopeUsage}}
                                        widen an APPROVED plan, then wait
  mainguard-plan await <id>             block until the human decides
  mainguard-plan {{CommitUsage}}
                                        record the approved work on your own branch

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
`task` prints that same work whenever the daemon is willing to give it to you: after your
plan is approved, or immediately when the operator has plan mode off. If it refuses, the
refusal says what you are waiting for. It is the same withheld text either way — there is
no second copy of it and no second decision about it.

`commit` is how finished work leaves this jail. The daemon commits everything in
/workspace onto your own branch; you supply only the message. Work you have not
committed does not survive the agent being stopped.

The message is ONE quoted argument, and it is a real git message: a subject line of at
most 72 characters, then a BLANK line, then as much body as you want. The daemon records
it verbatim and REFUSES anything it cannot record -- it will not shorten your subject or
flatten your paragraphs. Quote the whole thing: a shell splits an unquoted message on
whitespace, and your blank lines are gone before this program starts.

If your plan was approved you must also DECLARE whether the work departs from the
`approach` the human approved -- exactly one of:

  --no-deviations                       the work follows the approved approach
  --deviated "<what and why>"           it does not, in this way (repeat for each)

Say it truthfully. This is not a formality and it is not checked for you: the tests you
wrote pass either way, so this declaration is the only thing that tells the human their
approval and your diff came apart. A departure you declare is a row they read next to
your approach; one you do not is a surprise they find in the diff, or do not.

  mainguard-plan commit "fix(auth): recompute token expiry in UTC

  The clock read the host's local zone, so a token minted at 23:30 expired an hour
  early. Boundary tests cover the DST transition in both directions." --no-deviations
"""
""""
        + "\n" + AgentIpcShimTransport.PythonSource + "\n" + $$""""

# The one place the "and then commit, with your declaration" line is written. It is printed at every
# moment a worker is about to start or resume work, because that is the only output it is guaranteed to
# read -- and a finished worker that never commits leaves nothing behind at all.
WHEN_DONE = (
    'WHEN DONE: mainguard-plan commit "<subject>\\n\\n<body>" --no-deviations\n'
    '           ...or --deviated "<what you did differently, and why>" for each departure from the\n'
    '           approach your plan had approved. One of the two is required. (Uncommitted work is lost.)')


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
            print(WHEN_DONE)
            return 0
        print("APPROVED: plan %s" % response.get("planId", ""))
        print("TASK: %s" % (response.get("taskPrompt") or ""))
        # Said HERE, at the one moment the worker is cleared to start, because this is the only
        # output it is guaranteed to read after the gate opens. A finished worker that never
        # commits leaves nothing behind: the worktree goes with the jail.
        print(WHEN_DONE)
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
    elif len(argv) >= 2 and argv[1] == "{{Verbs[AgentIpcRequest.TaskOp]}}":
        # Bounded like `brief`, NOT like `await`: this asks a question the daemon can answer at once
        # (it either may hand the task over or it may not). It is the block that is unbounded, and
        # this is not the block.
        request = {"op": "{{AgentIpcRequest.TaskOp}}"}
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
        #
        # The declaration flags are parsed here and NOT required here. Whether this worker owes one
        # depends on whether it holds an approved plan, which only the daemon knows -- a shim that
        # guessed would refuse an ungated worker a commit it is entitled to make. So this refuses only
        # what is malformed however the run is gated, and the daemon refuses the missing declaration
        # with the form to use.
        message = None
        deviations = []
        no_deviations = False
        bad = None
        i = 2
        while i < len(argv):
            arg = argv[i]
            if arg == "--no-deviations":
                no_deviations = True
                i += 1
            elif arg == "--deviated":
                if i + 1 >= len(argv):
                    bad = ("--deviated needs the deviation itself, quoted as ONE argument:\n"
                           "  --deviated \"kept the helper synchronous; the approved approach said async\"")
                    break
                deviations.append(argv[i + 1])
                i += 2
            elif arg.startswith("--"):
                bad = 'unknown option %s. Run: mainguard-plan {{CommitUsage}}' % arg
                break
            elif message is None:
                message = arg
                i += 1
            else:
                # G4, unchanged: a second bare argument is REFUSED rather than joined, because by the
                # time a shell has split an unquoted message the subject/blank-line/body is already gone
                # and rejoining it with spaces hides that a structure was lost.
                bad = ("the message is ONE quoted argument, and a second one was given. A shell splits\n"
                       "an unquoted message on whitespace, so the subject, blank line and body you wrote\n"
                       "are already one flat line by the time this runs. Quote the whole message --\n"
                       "newlines inside the quotes are kept.")
                break

        if bad is None and no_deviations and deviations:
            # Both at once is not a stricter answer, it is two contradictory ones. Refused rather than
            # resolved by precedence: a rule about which wins would be invisible at the call site.
            bad = ("--no-deviations and --deviated say opposite things about the same work. Send one:\n"
                   "  --no-deviations              the work follows the approved approach\n"
                   "  --deviated \"<what and why>\"  it does not, in this way (repeat for each)")

        if bad is not None:
            # Said on every local commit refusal for the same reason the daemon says it: commit is the
            # only way work leaves this jail and an uncommitted worktree dies with it, so a worker that
            # read "refused" as "my diff is gone" might stop instead of retrying. The refusal costs a
            # turn, never the work.
            sys.stderr.write("mainguard-plan: %s\n"
                             "Nothing was committed and nothing is lost -- your worktree is untouched.\n"
                             "Run the same command again with the form above.\n" % bad)
            return 2

        request = {"op": "commit_work", "message": message if message is not None else ""}
        # Sent only when actually given, so "no answer" stays distinguishable on the wire from
        # "answered none" -- the daemon's refusal depends on being able to tell them apart.
        if deviations:
            request["deviations"] = deviations
        if no_deviations:
            request["noDeviations"] = True
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
        if response.get("status") == "Task":
            # Same two lines `present` prints on approval, for the same reason: this is the moment the
            # worker learns what to do, and it is the only output it is guaranteed to read before it
            # starts. A worker that finishes and never commits leaves nothing behind.
            print("TASK: %s" % (response.get("taskPrompt") or ""))
            print(WHEN_DONE)
            return 0
        if response.get("committed") is not None:
            # Distinguished, not collapsed: "nothing to commit" is an ok answer, and reporting it as a
            # commit would tell a worker its work is safe while its branch has not moved.
            if response.get("committed"):
                print("COMMITTED: %s on %s"
                      % (response.get("commitSha", ""), response.get("status", "")))
                # What the daemon did with your deviation declaration, in its own words -- including
                # "there was no approved approach to record it against", which is not a failure.
                if response.get("feedback"):
                    print("DECLARATION: %s" % response["feedback"])
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
