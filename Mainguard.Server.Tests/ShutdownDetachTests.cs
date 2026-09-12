using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F59 — "a daemon restart no longer kills every bound CLI", asserted against the thing that kills.
///
/// <para>The claim shipped once as prose. <c>TerminalSessionManager.DetachAllForShutdown</c> called
/// <c>BoundTerminalSession.Detach</c>, which called <c>ITerminalSession.Dispose</c>, and the production
/// session is <see cref="PtySession"/> whose <c>Dispose</c> is <c>IPtyConnection.Kill</c> — so shutdown
/// SIGKILLed every CLI exactly as before, and no test could tell, because the doubles of the day did not
/// kill anything on disposal either. <see cref="RecordingSession"/> below therefore mirrors the real
/// session's contract rather than a convenient one: <b>Dispose reaps, Release lets go</b>. A regression
/// that routes detach back through Dispose fails here.</para>
/// </summary>
public sealed class ShutdownDetachTests
{
    private const string RepoHash = "repo-f59";
    private const string AgentId = "worker-f59";

    /// <summary>Daemon shutdown: the terminal goes, the agent does not.</summary>
    [Fact]
    public void DetachAllForShutdown_LeavesTheCliRunning()
    {
        using var manager = new TerminalSessionManager();
        var cli = new RecordingSession();
        manager.Bind(new AgentSessionKey(RepoHash, AgentId), new BoundTerminalSession(AgentId, cli));

        manager.DetachAllForShutdown();

        Assert.False(cli.Killed);
        Assert.True(cli.Released);
        Assert.Null(manager.TryGetBound(new AgentSessionKey(RepoHash, AgentId)));
    }

    /// <summary>...and the other half of the trade: StopAgent is still a kill. If detaching stopped
    /// killing by making NOTHING kill, the fix would be a leak rather than a fix.</summary>
    [Fact]
    public void Release_StillKillsTheCli()
    {
        using var manager = new TerminalSessionManager();
        var cli = new RecordingSession();
        var key = new AgentSessionKey(RepoHash, AgentId);
        manager.Bind(key, new BoundTerminalSession(AgentId, cli));

        manager.Release(key);

        Assert.True(cli.Killed);
        Assert.Null(manager.TryGetBound(key));
    }

    /// <summary>The same distinction one level down, where it is actually implemented.</summary>
    [Fact]
    public void BoundSession_DetachReleases_AndDisposeKills()
    {
        var detached = new RecordingSession();
        new BoundTerminalSession(AgentId, detached).Detach();
        Assert.False(detached.Killed);
        Assert.True(detached.Released);

        var disposed = new RecordingSession();
        new BoundTerminalSession(AgentId, disposed).Dispose();
        Assert.True(disposed.Killed);
    }

    /// <summary>
    /// Disposing the MANAGER is a shutdown event, not an instruction to stop the agents — the container
    /// teardown path a hosted daemon actually takes on restart.
    /// </summary>
    [Fact]
    public void DisposingTheManager_DoesNotKillBoundClis()
    {
        var cli = new RecordingSession();
        var manager = new TerminalSessionManager();
        manager.Bind(new AgentSessionKey(RepoHash, AgentId), new BoundTerminalSession(AgentId, cli));

        manager.Dispose();

        Assert.False(cli.Killed);
    }

    /// <summary>
    /// A CLI double with <see cref="PtySession"/>'s contract, which is the point: <see cref="Dispose"/>
    /// reaps the child (as <c>IPtyConnection.Kill</c> does) and <see cref="Release"/> only lets go of the
    /// daemon's half. Output is an empty pipe the bound session's pump can read from forever.
    /// </summary>
    private sealed class RecordingSession : ITerminalSession
    {
        private readonly Pipe _output = new();
        private readonly MemoryStream _input = new();
        private readonly TaskCompletionSource<int> _exit = new();
        private readonly DuplexStream _io;

        public RecordingSession() => _io = new DuplexStream(_output.Reader.AsStream(), _input);

        public Stream IO => _io;

        public Task<int> ExitCode => _exit.Task;

        /// <summary>True once the child was terminated — by <see cref="Kill"/> or by <see cref="Dispose"/>.</summary>
        public bool Killed { get; private set; }

        /// <summary>True once the daemon let go of the child without terminating it.</summary>
        public bool Released { get; private set; }

        public void Resize(int cols, int rows)
        {
        }

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(137);
        }

        public void Release()
        {
            Released = true;
            _exit.TrySetResult(-1);
        }

        public void Dispose()
        {
            // Exactly what PtySession.Dispose does, and the reason the old detach path was a no-op.
            Kill();
        }

        private sealed class DuplexStream(Stream reader, MemoryStream writer) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count) =>
                reader.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                reader.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (writer)
                {
                    writer.Write(buffer, offset, count);
                }
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                lock (writer)
                {
                    writer.Write(buffer.Span);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
