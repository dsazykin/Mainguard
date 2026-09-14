using System;
using System.IO;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The engine-agnostic live terminal session behind an agent's CLI: a bidirectional byte stream
/// (read = child output, write = keystrokes toward the child), resize, kill, and the child's exit.
/// <see cref="PtySession"/> is the real implementation (ConPTY/forkpty via Porta.Pty); tests supply
/// duplex-pipe fakes so the daemon's PTY-binding and attach plumbing is verifiable cross-platform
/// without a real PTY or a Docker jail.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Bidirectional stream: read = child output, write = input toward the child.</summary>
    Stream IO { get; }

    /// <summary>Completes with the child's exit code when the process exits.</summary>
    Task<int> ExitCode { get; }

    /// <summary>Propagates a new terminal size to the child (SIGWINCH).</summary>
    void Resize(int cols, int rows);

    /// <summary>Force-terminates the child. Safe to call repeatedly.</summary>
    void Kill();

    /// <summary>
    /// Releases the DAEMON's half of this session — the event hook and the streams — and leaves the
    /// child process alone. The opposite of <see cref="IDisposable.Dispose"/>, which reaps it.
    ///
    /// <para><b>F59, and why the interface needs both.</b> Daemon shutdown has to let go of a session
    /// without killing the agent inside it, and "let go" and "reap" were the same call: every teardown
    /// path ended in <c>Dispose</c>, and <see cref="PtySession.Dispose"/> is a <c>Kill</c>. So the
    /// shutdown path that claimed to detach still SIGKILLed the CLI child of every bound session, and
    /// the claim was prose. A session type that does not terminate anything on
    /// <see cref="IDisposable.Dispose"/> needs nothing here, which is why the default is to dispose;
    /// <see cref="PtySession"/> overrides it, because it is the one that kills.</para>
    ///
    /// <para><b>What it does not promise.</b> Releasing closes the PTY master, and a child whose
    /// controlling terminal is hung up may still be signalled by the kernel. What it removes is the
    /// daemon's own deliberate SIGKILL; for the jail case the agent survives because the process is the
    /// container engine's, not this one's. Safe to call repeatedly, and mutually exclusive with
    /// <see cref="IDisposable.Dispose"/> — whichever lands first wins.</para>
    /// </summary>
    void Release() => Dispose();
}
