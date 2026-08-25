using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Terminal;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="TerminalService"/>. The first input frame selects the agent; the
/// daemon then drives that agent's <see cref="PtySession"/> through a <see cref="TerminalStreamer"/>
/// — PTY output is batched into <c>raw</c> frames on the 16 ms cadence (never splitting a VT
/// sequence or UTF-8 codepoint), input <c>data</c> frames are written to the PTY, and <c>resize</c>
/// frames propagate as SIGWINCH.
///
/// <para>Until the P2-09 agent lifecycle binds real processes, <see cref="TerminalSessionManager"/>
/// has no PTY factory and <see cref="TerminalSessionManager.Create"/> returns <c>null</c>; the
/// attach then falls back to the P2-02 echo so the bidi contract still round-trips. Output frames
/// are <c>oneof { raw | grid }</c> from day one so P2-18 is not a proto break.</para>
///
/// <para>Transport only: session ownership + PTY plumbing live in Core/daemon services.</para>
/// </summary>
public sealed class TerminalGrpcService : TerminalService.TerminalServiceBase
{
    private readonly TerminalSessionManager _sessions;
    private readonly TerminalLockRegistry _locks;
    private readonly AgentSessionStore? _agents;

    /// <summary>
    /// The notice an attach gets when the agent EXISTS but no CLI terminal is bound to it —
    /// ISSUES-LOG #23. Raw PTY bytes, so CRLF: it is parsed by the client's terminal engine.
    ///
    /// <para>This is the case a daemon restart leaves behind. <see cref="Runtime.AgentSessionReconciler"/>
    /// adopts the surviving jail back into the session store, so <c>ListAgents</c> reports it correctly and
    /// every surface can see it — but the terminal cannot come back with it. The CLI runs under a
    /// <c>docker exec -it</c> whose daemon-side forkpty died with the old process, and the Docker API has
    /// no re-attach for a running exec, so the output of that CLI is gone for good. Before this notice the
    /// attach fell silently into the P2-02 echo and emitted <b>nothing at all</b>: the client's
    /// "the CLI is drawing" signal never fired, and the coordinator surface sat on "Still starting the
    /// coordinator" for hours against an agent the daemon knew was <c>Working</c>. Saying so costs one
    /// frame and is the difference between a wrong screen and a recoverable one.</para>
    /// </summary>
    public const string DetachedNotice =
        "\r\n[mainguard] No terminal is attached to this agent — nothing you type here reaches a CLI.\r\n"
        + "This is usually a daemon restart: the sandbox keeps running, but the terminal it was\r\n"
        + "started with belonged to the previous daemon process and cannot be reconnected. It also\r\n"
        + "happens when the agent never got a CLI at all (its repository was never provisioned).\r\n"
        + "Restart the agent to get a terminal you can talk to.\r\n";

    /// <param name="agents">
    /// The live session registry — read ONLY to tell "this agent exists and simply has no terminal"
    /// (<see cref="DetachedNotice"/>) from "we have never heard of this agent", which keeps the P2-02
    /// echo contract for the latter. Optional so a transport-only construction still works.
    /// </param>
    public TerminalGrpcService(
        TerminalSessionManager sessions, TerminalLockRegistry locks, AgentSessionStore? agents = null)
    {
        _sessions = sessions;
        _locks = locks;
        _agents = agents;
    }

    public override async Task Attach(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        try
        {
            // The first frame carries the agent_id selector.
            if (!await requestStream.MoveNext(ct))
            {
                return;
            }

            var first = requestStream.Current;
            var agentId = first.InputCase switch
            {
                TerminalInput.InputOneofCase.AgentId => first.AgentId,
                TerminalInput.InputOneofCase.Attach => first.Attach.AgentId,
                _ => null,
            };

            // P2-18: a grid-capable client asks via AttachOptions; it gets GridUpdate frames only
            // when the daemon's engine flag actually runs libvterm for this session — any mismatch
            // degrades to the raw contract, so flag skew between client and daemon is always safe.
            var wantsGrid = first.InputCase == TerminalInput.InputOneofCase.Attach && first.Attach.Grid;

            // P2-14 terminal input lock: a managed worker's terminal is read-only. The read (output)
            // stream stays open — a banner + the live output prove it — but input DATA frames are
            // refused server-side (the RoleInterceptor also severs them at the gRPC layer; this is
            // defense-in-depth so a direct service call is enforced too). Never UI-only.
            var locked = agentId is not null && _locks is not null && _locks.IsLocked(agentId);

            // The real agent path: a long-lived CLI session bound at spawn (P2-47 #3 wiring). Attach
            // subscribes (replay + live frames); detach only unsubscribes — the CLI keeps running.
            var bound = agentId is not null ? _sessions.TryGetBound(agentId) : null;

            // Attach-before-bind race: the client attaches the instant the agent appears ("Starting"),
            // but the CLI binds a few seconds later (container start + docker-exec-under-PTY). Wait for the
            // pending bind rather than latching into echo for the whole session (the bug that left the
            // coordinator terminal showing echo instead of the live CLI). Handles the locked (managed)
            // case too — PumpBoundAsync streams it read-only.
            if (bound is null && agentId is not null && _sessions.IsBindPending(agentId))
            {
                bound = await _sessions.WaitForBoundAsync(agentId, ct);
            }

            if (bound is not null)
            {
                if (wantsGrid && bound.GridEnabled)
                {
                    await PumpBoundGridAsync(bound, requestStream, responseStream, locked, ct);
                }
                else
                {
                    await PumpBoundAsync(bound, requestStream, responseStream, locked, ct);
                }

                return;
            }

            if (locked)
            {
                await LockedAttachAsync(requestStream, responseStream, first, ct);
                return;
            }

            // ISSUES-LOG #23: a KNOWN agent with no bound CLI and no bind coming. Say so instead of
            // dropping into an echo that emits nothing — an attach that never produces a frame reads,
            // from the client, as a CLI that is still starting up, forever. Unknown ids keep the P2-02
            // echo: this is a statement about a session we have, not a catch-all.
            if (agentId is { Length: > 0 } && _agents?.Find(agentId) is not null)
            {
                await DetachedAttachAsync(responseStream, requestStream, ct);
                return;
            }

            var session = agentId is not null ? _sessions.Create(agentId) : null;
            if (session is null)
            {
                // Interim: no PTY bound for this agent yet — echo so the attach still round-trips.
                await EchoAsync(requestStream, responseStream, first, ct);
                return;
            }

            await PumpPtyAsync(session, requestStream, responseStream, ct);
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
    }

    /// <summary>
    /// The bound-session attach: replay the missed tail, then pump live frames, while forwarding
    /// input/resize toward the CLI. A locked (managed-worker) attach keeps the read stream open but
    /// refuses input DATA frames with <see cref="StatusCode.PermissionDenied"/>. The client's
    /// detach unsubscribes only — the CLI's PTY belongs to the agent lifecycle, not the attach.
    /// </summary>
    private static async Task PumpBoundAsync(
        Runtime.BoundTerminalSession bound,
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        bool locked,
        System.Threading.CancellationToken ct)
    {
        var (replay, live) = bound.Subscribe(out var unsubscribe);
        try
        {
            if (locked)
            {
                await responseStream.WriteAsync(new TerminalOutput
                {
                    Raw = ByteString.CopyFromUtf8("[read-only - managed worker]\r\n"),
                });
            }

            // Single writer to the response stream: this pump task emits replay-then-live frames.
            var pump = Task.Run(async () =>
            {
                foreach (var frame in replay)
                {
                    await responseStream.WriteAsync(new TerminalOutput { Raw = ByteString.CopyFrom(frame) });
                }

                await foreach (var frame in live.ReadAllAsync(ct))
                {
                    await responseStream.WriteAsync(new TerminalOutput { Raw = ByteString.CopyFrom(frame) });
                }
            }, ct);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(ct))
                {
                    switch (input.InputCase)
                    {
                        case TerminalInput.InputOneofCase.Data:
                            if (locked)
                            {
                                throw new RpcException(new Status(StatusCode.PermissionDenied,
                                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
                            }

                            await bound.WriteInputAsync(input.Data.Memory, ct);
                            break;
                        case TerminalInput.InputOneofCase.Resize:
                            bound.Resize((int)input.Resize.Cols, (int)input.Resize.Rows);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached mid-stream — normal teardown; the session lives on.
            }

            // Keep streaming output until the client cancels or the CLI's session ends (a client
            // that completed its request stream is a legitimate read-only viewer). Never kill the
            // CLI from an attach teardown.
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Detach — normal.
            }
            catch (RpcException)
            {
                // The client went away mid-write — nothing to salvage; the session lives on.
            }
        }
        finally
        {
            unsubscribe();
        }
    }

    /// <summary>
    /// The P2-18 grid attach: an atomic full-grid snapshot, then live <c>GridUpdate</c> /
    /// <c>ClipboardCopy</c> frames, while forwarding input/resize toward the CLI exactly like the
    /// raw pump (locked attaches refuse input DATA frames the same way — the snapshot itself proves
    /// the read stream is open, so no raw banner is injected into the grid stream).
    /// </summary>
    private static async Task PumpBoundGridAsync(
        Runtime.BoundTerminalSession bound,
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        bool locked,
        System.Threading.CancellationToken ct)
    {
        var (snapshot, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            // Single writer to the response stream: this pump task emits snapshot-then-live frames.
            var pump = Task.Run(async () =>
            {
                await responseStream.WriteAsync(new TerminalOutput { Grid = snapshot });

                await foreach (var frame in live.ReadAllAsync(ct))
                {
                    await responseStream.WriteAsync(frame);
                }
            }, ct);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(ct))
                {
                    switch (input.InputCase)
                    {
                        case TerminalInput.InputOneofCase.Data:
                            if (locked)
                            {
                                throw new RpcException(new Status(StatusCode.PermissionDenied,
                                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
                            }

                            await bound.WriteInputAsync(input.Data.Memory, ct);
                            break;
                        case TerminalInput.InputOneofCase.Resize:
                            bound.Resize((int)input.Resize.Cols, (int)input.Resize.Rows);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached mid-stream — normal teardown; the session lives on.
            }

            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Detach — normal.
            }
            catch (RpcException)
            {
                // The client went away mid-write — nothing to salvage; the session lives on.
            }
        }
        finally
        {
            unsubscribe();
        }
    }

    private static async Task PumpPtyAsync(
        PtySession session,
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        System.Threading.CancellationToken ct)
    {
        using (session)
        using (var streamer = new TerminalStreamer())
        {
            // Single writer to the response stream: only the streamer emits raw frames.
            var pump = streamer.RunAsync(
                session.IO,
                (frame, token) => responseStream.WriteAsync(
                    new TerminalOutput { Raw = ByteString.CopyFrom(frame.Span) }),
                flushInterval: null,
                ct);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(ct))
                {
                    switch (input.InputCase)
                    {
                        case TerminalInput.InputOneofCase.Data:
                            var bytes = input.Data.Memory;
                            await session.IO.WriteAsync(bytes, ct);
                            await session.IO.FlushAsync(ct);
                            break;
                        case TerminalInput.InputOneofCase.Resize:
                            session.Resize((int)input.Resize.Cols, (int)input.Resize.Rows);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached mid-stream.
            }

            // Client's request stream ended → tear the child down so the PTY reaches EOF and the
            // streamer's read loop completes; then await the final drain.
            session.Kill();
            await pump;
        }
    }

    /// <summary>
    /// The P2-14 locked (read-only) attach path for a managed worker. Writes a read-only banner so the
    /// output (read) stream is demonstrably open, then reads input and rejects any <c>data</c> frame with
    /// <see cref="StatusCode.PermissionDenied"/> — the input stream is severed daemon-side, never UI-only.
    /// </summary>
    private static async Task LockedAttachAsync(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        TerminalInput first,
        System.Threading.CancellationToken ct)
    {
        // Read direction works: a banner is delivered on attach.
        await responseStream.WriteAsync(new TerminalOutput
        {
            Raw = ByteString.CopyFromUtf8("[read-only - managed worker]\r\n"),
        });

        // The first frame was the agent_id selector; if it somehow carried data, reject it too.
        if (first.InputCase == TerminalInput.InputOneofCase.Data)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "This terminal is locked (managed worker) — input is denied."));
        }

        await foreach (var input in requestStream.ReadAllAsync(ct))
        {
            if (input.InputCase == TerminalInput.InputOneofCase.Data)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
            }

            // Resize / stray agent_id frames are harmless and ignored.
        }
    }

    /// <summary>
    /// P2-18 lazy scrollback fetch: pages of the bound session's daemon-side scrollback ring
    /// (libvterm engine only — an unbound agent or an interim session returns an empty reply).
    /// Serves the snapshot/attach path's history leg for reattach/recovery and future thin clients.
    /// </summary>
    public override Task<ScrollbackReply> GetScrollback(ScrollbackRequest request, ServerCallContext context)
    {
        var bound = string.IsNullOrEmpty(request.AgentId) ? null : _sessions.TryGetBound(request.AgentId);
        if (bound is null)
        {
            return Task.FromResult(new ScrollbackReply());
        }

        // Page-size cap: a client asking for the whole 10k-line ring pages through it.
        var count = (int)Math.Min(request.Count, 1000);
        return Task.FromResult(bound.GetScrollback(request.Start, count));
    }

    /// <summary>
    /// The detached attach (ISSUES-LOG #23): the agent is real and has no terminal. Writes
    /// <see cref="DetachedNotice"/> so the client has an answer instead of silence, then holds the stream
    /// open and DISCARDS input — echoing keystrokes back would draw a terminal that looks like it is
    /// talking to the CLI while reaching nothing at all.
    /// </summary>
    private static async Task DetachedAttachAsync(
        IServerStreamWriter<TerminalOutput> responseStream,
        IAsyncStreamReader<TerminalInput> requestStream,
        System.Threading.CancellationToken ct)
    {
        await responseStream.WriteAsync(new TerminalOutput
        {
            Raw = ByteString.CopyFromUtf8(DetachedNotice),
        });

        try
        {
            await foreach (var _ in requestStream.ReadAllAsync(ct))
            {
                // Input has nowhere to go — a detached session has no PTY behind it.
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
    }

    /// <summary>
    /// The P2-02 interim echo path: reflects input <c>data</c> frames back as <c>raw</c> output
    /// frames. Used when no PTY is bound for the selected agent (until P2-09).
    /// </summary>
    private static async Task EchoAsync(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        TerminalInput first,
        System.Threading.CancellationToken ct)
    {
        if (first.InputCase == TerminalInput.InputOneofCase.Data)
        {
            await responseStream.WriteAsync(new TerminalOutput { Raw = first.Data });
        }

        await foreach (var input in requestStream.ReadAllAsync(ct))
        {
            if (input.InputCase != TerminalInput.InputOneofCase.Data)
            {
                continue;
            }

            await responseStream.WriteAsync(new TerminalOutput { Raw = input.Data });
        }
    }
}
