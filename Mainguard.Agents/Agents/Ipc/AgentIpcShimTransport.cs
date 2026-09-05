namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The transport half of both in-jail shims — <c>mainguard-agent</c> and <c>mainguard-plan</c> — as one
/// python3 source fragment they each embed. It is shared rather than duplicated because the two copies
/// of <c>call()</c> that existed before had already drifted once (one carried a timeout, the other did
/// not), and a jail's only route to the daemon is not a place where two implementations should be able
/// to disagree.
///
/// <para><b>Why there are two transports.</b> The Unix socket is the channel; it is also unreachable on
/// macOS. The daemon runs natively on the Mac while jails run inside the container engine's Linux VM,
/// and Docker's macOS file sharing (virtiofs / gRPC-FUSE) does not proxy AF_UNIX across that boundary:
/// the bind-mounted <c>daemon.sock</c> stat()s as a socket inside the jail and every <c>connect()</c> to
/// it fails <c>ECONNREFUSED</c>, with the daemon demonstrably listening on the other side. That took the
/// whole coordinator control path — all four tools of contract §3, and the worker plan gate — from
/// "shipped" to "cannot be called at all" on that platform.</para>
///
/// <para>So the shim tries the socket and, only when the socket cannot be reached AND the daemon has
/// mounted a WRITABLE outbox for this jail, falls back to the file-framed form of the same protocol.
/// The writability test is the discriminator on purpose: where the socket works the outbox is inside the
/// read-only mount, so a daemon that is genuinely down still fails as a daemon that is down — the
/// fallback cannot turn "no answer" into "wait forever".</para>
/// </summary>
public static class AgentIpcShimTransport
{
    /// <summary>
    /// The python3 fragment defining <c>call(request, timeout)</c>. Embedded verbatim by both shims;
    /// the module-level imports it needs are declared here, so it must be placed after each shim's
    /// docstring and before its own definitions.
    /// </summary>
    public static readonly string PythonSource = $$""""
import errno
import json
import os
import socket
import sys
import time
import uuid

SOCKET_PATH = os.environ.get("MAINGUARD_IPC_SOCKET", "{{AgentIpcPaths.SandboxSocketPath}}")
OUTBOX_PATH = os.environ.get("MAINGUARD_IPC_OUTBOX", "{{AgentIpcPaths.SandboxOutboxPath}}")
POLL_SECONDS = 0.1


def _call_socket(request, timeout):
    """The channel: one JSON line in, one JSON line out, over the daemon's Unix socket."""
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
        sock.settimeout(timeout)
        sock.connect(SOCKET_PATH)
        sock.sendall((json.dumps(request) + "\n").encode("utf-8"))
        data = b""
        while not data.endswith(b"\n"):
            chunk = sock.recv(65536)
            if not chunk:
                break
            data += chunk
    return json.loads(data.decode("utf-8"))


def _call_outbox(request, timeout):
    """The same channel where the mount cannot carry a socket (macOS: the daemon is on the host,
    this jail is in the engine's Linux VM, and virtiofs does not proxy AF_UNIX across that line).

    Identical JSON, identical daemon handler. The request is written under a staging name and
    RENAMED into place, so the daemon can never read half of one; the answer appears the same way.
    A call that blocks blocks here too -- the daemon writes the answer file when its handler
    returns, which for a plan presentation is whenever the human decides.
    """
    ticket = uuid.uuid4().hex
    staged = os.path.join(OUTBOX_PATH, ticket + "{{AgentIpcPaths.OutboxStagingSuffix}}")
    request_path = os.path.join(OUTBOX_PATH, ticket + "{{AgentIpcPaths.OutboxRequestSuffix}}")
    response_path = os.path.join(OUTBOX_PATH, ticket + "{{AgentIpcPaths.OutboxResponseSuffix}}")
    with open(staged, "w", encoding="utf-8") as handle:
        handle.write(json.dumps(request) + "\n")
    os.rename(staged, request_path)

    deadline = None if timeout is None else time.monotonic() + timeout
    try:
        while True:
            try:
                with open(response_path, "r", encoding="utf-8") as handle:
                    line = handle.read()
                if line.endswith("\n"):
                    return json.loads(line)
            except FileNotFoundError:
                pass
            if deadline is not None and time.monotonic() > deadline:
                raise OSError(errno.ETIMEDOUT, "the Mainguard daemon did not answer in %ss" % timeout)
            time.sleep(POLL_SECONDS)
    finally:
        for path in (staged, request_path, response_path):
            try:
                os.unlink(path)
            except OSError:
                pass


def call(request, timeout=60):
    try:
        return _call_socket(request, timeout)
    except OSError:
        # Fall back ONLY when the daemon has given this jail a writable outbox. Where the socket is
        # the real channel the outbox sits inside the read-only mount and this test fails, so a
        # daemon that is down still reports as a daemon that is down.
        if not os.access(OUTBOX_PATH, os.W_OK):
            raise
        return _call_outbox(request, timeout)
"""";
}
