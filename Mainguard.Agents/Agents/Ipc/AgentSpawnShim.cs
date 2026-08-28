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
    /// <summary>The shim's full script text (LF newlines; written mode 0755 by the daemon). Composed
    /// from the shared <see cref="AgentIpcShimTransport"/>, which is the ONLY place either shim's
    /// transport is written.</summary>
    public static readonly string Script = """"
#!/usr/bin/env python3
"""mainguard-agent: the Mainguard Coordinator's complete set of operations.

These four tools are ALL you can do. There is no fifth. You cannot merge, approve or reject
a plan, read another agent's terminal, or act on a worker you did not spawn — the daemon
refuses those, so there is nothing to be gained by trying.

Usage:
  mainguard-agent spawn <agent-kind> [task prompt ...]   spawn_worker
  mainguard-agent status [<agent-id>]                    get_worker_status
  mainguard-agent prompt <agent-id> <text ...>           send_worker_prompt
  mainguard-agent verify <agent-id>                      request_verification

  mainguard-agent list                                   alias of `status`

A spawned worker does NOT receive its task until a human approves the plan the worker
itself authors after inspecting the repository. Until then `prompt` and `verify` are
refused for it, and that is the gate working, not an error to route around.
"""
""""
        + "\n" + AgentIpcShimTransport.PythonSource + "\n" + """"

def main(argv):
    if len(argv) >= 3 and argv[1] == "spawn":
        request = {"op": "spawn", "agentKind": argv[2], "taskPrompt": " ".join(argv[3:])}
    elif len(argv) >= 2 and argv[1] in ("status", "list"):
        # `list` is the pre-phase-3 spelling of the same tool, kept so an existing coordinator
        # transcript does not break. Both carry an optional agent id.
        request = {"op": "status", "agentId": argv[2] if len(argv) >= 3 else None}
    elif len(argv) >= 4 and argv[1] == "prompt":
        request = {"op": "prompt", "agentId": argv[2], "prompt": " ".join(argv[3:])}
    elif len(argv) >= 3 and argv[1] == "verify":
        request = {"op": "verify", "agentId": argv[2]}
    else:
        sys.stderr.write(__doc__ or "usage: mainguard-agent spawn <agent-kind> [prompt]\n")
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
