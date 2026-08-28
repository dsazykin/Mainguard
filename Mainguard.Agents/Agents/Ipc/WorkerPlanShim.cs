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
    /// <summary>The shim's full script text (LF newlines; written mode 0755 by the daemon). Composed
    /// from the shared <see cref="AgentIpcShimTransport"/>, which is the ONLY place either shim's
    /// transport is written.</summary>
    public static readonly string Script = """"
#!/usr/bin/env python3
"""mainguard-plan: present the plan YOU authored, and wait for the human.

You are a Mainguard worker. You do not start work until a human approves your plan.

Usage:
  mainguard-plan brief                  what you are here to plan (never the task itself)
  mainguard-plan present <plan.json>    present the plan you authored, then wait
  mainguard-plan revise <id> <plan.json>  re-present after a rejection, then wait
  mainguard-plan await <id>             block until the human decides
  mainguard-plan commit <message ...>   record the approved work on your own branch

plan.json is {"scope": ["path", ...], "approach": "...", "testStrategy": "..."}. Write it
OUTSIDE the repository (/tmp/plan.json) — /workspace is the tree your commit records.

`present` and `revise` block until the decision arrives and print it. On approval the
output includes TASK: followed by the work you are cleared to do — the daemon withholds
it until then, so there is nothing to start on before approval.

`commit` is how finished work leaves this jail. The daemon commits everything in
/workspace onto your own branch; you supply only the message. Work you have not
committed does not survive the agent being stopped.
"""
""""
        + "\n" + AgentIpcShimTransport.PythonSource + "\n" + """"

def read_plan(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def report(response):
    status = response.get("status") or ""
    if status == "Approved":
        print("APPROVED: plan %s" % response.get("planId", ""))
        print("TASK: %s" % (response.get("taskPrompt") or ""))
        # Said HERE, at the one moment the worker is cleared to start, because this is the only
        # output it is guaranteed to read after the gate opens. A finished worker that never
        # commits leaves nothing behind: the worktree goes with the jail.
        print("WHEN DONE: mainguard-plan commit <message>  (uncommitted work is lost)")
        return 0
    if status == "Rejected":
        remaining = response.get("revisionsRemaining")
        print("REJECTED: %s" % (response.get("feedback") or "(no feedback given)"))
        print("REVISE: mainguard-plan revise %s <plan.json>  (%s revision(s) left)"
              % (response.get("planId", ""), remaining))
        return 0
    if status == "Escalated":
        print("ESCALATED: the revision budget is spent (%s of %s used)."
              % (response.get("revision"), response.get("maxRevisions")))
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
    elif len(argv) >= 4 and argv[1] == "revise":
        request = {"op": "revise_plan", "planId": argv[2], "planJson": read_plan(argv[3]),
                   "title": " ".join(argv[4:]) or None}
    elif len(argv) >= 3 and argv[1] == "await":
        request = {"op": "await_decision", "planId": argv[2]}
    elif len(argv) >= 2 and argv[1] == "commit":
        # The message is ALL the worker contributes. The repo, the worktree and the branch are the
        # daemon's to compute from this endpoint's identity, so there is nothing else to send.
        request = {"op": "commit_work", "message": " ".join(argv[2:])}
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
