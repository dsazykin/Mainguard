using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;

namespace Mainguard.Agents.Agents;

/// <summary>
/// A live pseudoterminal session wrapping a spawned child process. The child runs under a real
/// PTY (ConPTY on Windows, forkpty on Linux/macOS via <see cref="Porta.Pty"/>) so
/// <c>isatty()</c> is true inside it, resize propagates as <c>SIGWINCH</c>, and <c>Ctrl+C</c>
/// (0x03) reaches the foreground process — the whole reason a redirected-pipe <see cref="System.Diagnostics.Process"/>
/// is a rejection trigger for this task.
///
/// <para><see cref="IO"/> is a single bidirectional stream: reads drain PTY output, writes push
/// keystrokes/paste toward the child. <see cref="Dispose"/> is idempotent and reaps the child.</para>
/// </summary>
public sealed class PtySession : ITerminalSession
{
    private readonly IPtyConnection _connection;
    private readonly PtyDuplexStream _io;
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal PtySession(IPtyConnection connection)
    {
        _connection = connection;
        _io = new PtyDuplexStream(connection.ReaderStream, connection.WriterStream);
        _connection.ProcessExited += OnProcessExited;
    }

    /// <summary>Bidirectional PTY stream: read = child output, write = keystrokes toward the child.</summary>
    public Stream IO => _io;

    /// <summary>Completes with the child's exit code when the process exits.</summary>
    public Task<int> ExitCode => _exit.Task;

    /// <summary>The child process id (useful for probes/diagnostics).</summary>
    public int Pid => _connection.Pid;

    /// <summary>Propagates a new terminal size to the child (SIGWINCH / ClosePseudoConsole resize).</summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                cols <= 0 ? nameof(cols) : nameof(rows), "Terminal dimensions must be positive.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _connection.Resize(cols, rows);
    }

    /// <summary>Force-terminates the child (SIGKILL / ClosePseudoConsole). Safe to call repeatedly.</summary>
    public void Kill()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _connection.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited — nothing to kill.
        }
    }

    /// <summary>
    /// F59: lets go of the child instead of reaping it — unhooks the exit event, completes any waiter,
    /// and disposes the PTY streams. <b>No <c>Kill</c>, and no <c>IPtyConnection.Dispose</c></b>, whose
    /// contract includes reaping the process this method exists not to touch.
    ///
    /// <para>This is what daemon shutdown calls. Before it existed, <see cref="BoundTerminalSession"/>'s
    /// detach path ended here in <see cref="Dispose"/> — i.e. in <c>Kill</c> — so "a daemon restart no
    /// longer kills every bound CLI" was true of the test doubles and false of the only session type
    /// production uses.</para>
    ///
    /// <para><b>The residual, stated rather than hidden.</b> Closing the master fd hangs the child's
    /// controlling terminal up, and a child that dies of SIGHUP is just as gone as one that dies of
    /// SIGKILL. What makes the agent survive is not this method alone but where the agent actually runs:
    /// the child here is the <c>docker exec -i -t</c> CLIENT, and the process it fronts belongs to the
    /// container engine, which keeps it running when its client detaches. Releasing rather than killing
    /// is what makes that detach an ordinary client disconnect instead of a dead client.</para>
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.ProcessExited -= OnProcessExited;

        // A waiter on ExitCode must not hang, and this session is no longer watching the child, so its
        // exit can never be observed here again. -1 is the same "unknown" the reap path reports.
        _exit.TrySetResult(-1);

        _io.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.ProcessExited -= OnProcessExited;

        try
        {
            _connection.Kill();
        }
        catch
        {
            // Best-effort reap; the process may already be gone.
        }

        // Ensure a waiter never hangs if the exit event never fired (e.g. immediate teardown).
        _exit.TrySetResult(TryReadExitCode());

        _io.Dispose();
        _connection.Dispose();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
        => _exit.TrySetResult(e.ExitCode);

    private int TryReadExitCode()
    {
        try
        {
            return _connection.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>
/// Spawns <see cref="PtySession"/>s. One <see cref="Spawn"/> API; the platform split (ConPTY vs
/// forkpty) lives inside <see cref="Porta.Pty"/>. The command is exec'd directly with its
/// arguments — never wrapped in a command-shell interpreter (a global rejection trigger) — and the
/// caller supplies the complete environment and a worktree-locked <paramref name="cwd"/>.
/// </summary>
public static class PtyProcessShim
{
    /// <summary>
    /// Spawns <paramref name="command"/> with <paramref name="args"/> under a real PTY sized
    /// <paramref name="cols"/>×<paramref name="rows"/>, rooted at <paramref name="cwd"/>, with
    /// exactly the environment in <paramref name="env"/>.
    /// </summary>
    public static PtySession Spawn(
        string command,
        IReadOnlyList<string> args,
        string cwd,
        IReadOnlyDictionary<string, string> env,
        int cols,
        int rows)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A command to exec is required.", nameof(command));
        }

        if (cols <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                cols <= 0 ? nameof(cols) : nameof(rows), "Terminal dimensions must be positive.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in env)
        {
            environment[kv.Key] = kv.Value;
        }

        var commandLine = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            commandLine[i] = args[i];
        }

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            App = command,
            CommandLine = commandLine,
            Cwd = cwd,
            Cols = cols,
            Rows = rows,
            Environment = environment,
        };

        // Porta exposes only an async spawn; blocking here keeps the synchronous contract. There is
        // no captured synchronization context in the daemon/host so this cannot deadlock.
        var connection = PtyProvider.SpawnAsync(options, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        return new PtySession(connection);
    }
}

/// <summary>
/// Adapts <see cref="Porta.Pty"/>'s split reader/writer streams into the single bidirectional
/// <see cref="Stream"/> the <see cref="PtySession.IO"/> contract exposes. Reads pull from the PTY
/// output stream; writes push to the PTY input stream.
/// </summary>
internal sealed class PtyDuplexStream : Stream
{
    private readonly Stream _reader;
    private readonly Stream _writer;

    public PtyDuplexStream(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _reader.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _reader.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _reader.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _writer.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _writer.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _writer.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _writer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writer.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _reader.Dispose();
            }
            catch
            {
                // Reader teardown races with process exit; ignore.
            }

            try
            {
                _writer.Dispose();
            }
            catch
            {
                // Writer teardown races with process exit; ignore.
            }
        }

        base.Dispose(disposing);
    }
}
