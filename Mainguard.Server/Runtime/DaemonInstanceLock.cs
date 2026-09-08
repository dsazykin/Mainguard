using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Mainguard.Server.Runtime;

/// <summary>
/// F55: the daemon's cross-process single-instance guard — one <c>mainguardd</c> per data root, proven
/// by an exclusive OS file lock rather than by asking <c>pgrep</c> whether one seems to be running.
///
/// <para><b>What went wrong without it.</b> Two daemons could reach <c>ConfigureServices</c> at the same
/// time. Both minted a session token and fresh mTLS material and wrote them over the live daemon's files;
/// both ran the EF migration path, whose <c>ClearStaleMigrationLock</c> deletes <c>__EFMigrationsLock</c>
/// on the assumption that a lock row at boot must be orphaned; and only then did the loser discover the
/// port was taken and exit. The survivor kept serving with credentials no client held any more, and had
/// had its migration lock pulled out from under it. The <c>pgrep -f</c> check in
/// <c>MacDaemonController</c> is a check-then-act with a multi-second window, and <c>KeepAlive=true</c>
/// in the LaunchAgent re-ran the whole sequence every time the loser exited.</para>
///
/// <para><b>Why a lock file and not the port.</b> The port is the resource that is contended, but it is
/// not the resource that is damaged: a second daemon on a <i>different</i> port against the same data
/// root does every one of the things above and never trips a bind error at all. The lock is therefore
/// taken on the data root — the directory holding <c>daemon.token</c>, the transport material, the plan
/// stores and the SQLite DB — which is exactly the shared state. The port race is closed separately, by
/// deferring the credential writes until Kestrel has actually bound (see
/// <see cref="Auth.SessionTokenFile.Persist"/>).</para>
///
/// <para><b>Why <c>FileShare.None</c> is a real lock.</b> On Unix .NET implements it with
/// <c>flock(LOCK_EX|LOCK_NB)</c> and on Windows with a share-mode-zero <c>CreateFile</c>. Both are held
/// by the open file description, so the kernel releases them when the process dies — however it dies. A
/// pidfile cannot make that claim, which is why there is no pidfile here (and why the daemon has never
/// had one: Docker labels are the liveness source of truth everywhere else in this codebase).</para>
///
/// <para><b>Test isolation falls out for free.</b> The in-proc tier gives every <c>DaemonFixture</c> its
/// own temp token directory, so every host locks its own file and none of them contend. Two real
/// daemons against <c>~/.mainguard</c> do contend, which is the whole point.</para>
/// </summary>
public sealed class DaemonInstanceLock : IDisposable
{
    /// <summary>The lock file's name inside the data root, beside <c>daemon.token</c>.</summary>
    public const string FileName = "daemon.lock";

    private readonly FileStream _stream;
    private bool _disposed;

    private DaemonInstanceLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>The absolute path of the held lock file.</summary>
    public string Path { get; }

    /// <summary>The lock file that guards <paramref name="directory"/>.</summary>
    public static string PathIn(string directory) =>
        System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), FileName);

    /// <summary>
    /// Takes the exclusive lock on <paramref name="directory"/>, or throws
    /// <see cref="DaemonAlreadyRunningException"/> naming the holder when another daemon has it.
    ///
    /// <para>Called BEFORE anything is minted or written, so a losing instance leaves the winner's data
    /// root byte-for-byte untouched. The holder's pid is written into the file for diagnosis only —
    /// nothing reads it to make a decision, because a pid is not a lock.</para>
    /// </summary>
    public static DaemonInstanceLock Acquire(string directory) => Acquire(directory, stamp: true);

    /// <summary>
    /// The non-throwing form: takes the lock, or returns <c>null</c> when another daemon holds it.
    ///
    /// <para>For work that must not run while a daemon is live but is <b>optional</b> — the caller has a
    /// correct "do nothing" branch. Its one production caller is
    /// <c>GatewayServiceRegistration.ClearStaleMigrationLock</c>: clearing an orphaned
    /// <c>__EFMigrationsLock</c> row is only ever safe when nobody else is running, and skipping it is
    /// safe by construction (a genuinely live holder's row is not stale, and EF's own watchdog covers the
    /// case where it is).</para>
    ///
    /// <para>Does <b>not</b> stamp the holder pid: this lock is taken and released in a breath, and a
    /// record naming a process that is about to stop holding it would make the refusal message worse
    /// rather than better.</para>
    /// </summary>
    /// <param name="wait">
    /// How long to keep trying before concluding that a real daemon holds the root. A daemon holds this
    /// lock for its whole life, so a genuine conflict never clears and the wait is pure latency; what the
    /// wait is FOR is the other transient holder — two hosts coming up together, which the in-proc test
    /// tier does routinely because every fixture shares one data root by design. Without it a
    /// millisecond-wide overlap between two transient acquires would read as "a daemon is running" and
    /// silently drop a test host onto the in-memory stores.
    /// </param>
    public static DaemonInstanceLock? TryAcquire(string directory, TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(2));
        while (true)
        {
            try
            {
                return Acquire(directory, stamp: false);
            }
            catch (DaemonAlreadyRunningException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(50);
            }
        }
    }

    private static DaemonInstanceLock Acquire(string directory, bool stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = PathIn(directory);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new DaemonAlreadyRunningException(path, DescribeHolder(path), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DaemonAlreadyRunningException(path, DescribeHolder(path), ex);
        }

        if (!stamp)
        {
            return new DaemonInstanceLock(path, stream);
        }

        try
        {
            stream.SetLength(0);
            var pidBytes = Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
            stream.Write(pidBytes);
            stream.Flush(flushToDisk: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // ...and the same pid in an UNLOCKED sibling, purely so the refusal message can name the
            // holder. The lock file itself cannot be read while it is held: .NET takes a real `flock` for
            // every FileStream on Unix, so any attempt to read it conflicts with the holder's exclusive
            // lock — which is the correct behaviour for a lock and useless for a diagnostic.
            //
            // This is NOT a pidfile in the sense the daemon deliberately does not have one: nothing reads
            // it to decide anything. A stale value here changes no behaviour at all; the kernel's answer
            // to "is the lock held" is the decision, and this is a string in an error message.
            File.WriteAllText(
                HolderPathIn(System.IO.Path.GetDirectoryName(path)!),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stamp is diagnostics. Failing to write it must never fail an acquired lock.
        }

        return new DaemonInstanceLock(path, stream);
    }

    /// <summary>The unlocked sibling that records the holder's pid for diagnosis only.</summary>
    private static string HolderPathIn(string directory) =>
        System.IO.Path.Combine(directory, FileName + ".holder");

    /// <summary>
    /// Best-effort "who holds it" for the refusal message. Reads the pid the holder recorded and, when
    /// that process still exists, names it. Never throws and never decides anything — a stale pid beside
    /// a file the OS says is locked simply means the record lost a race with a restart.
    /// </summary>
    private static string DescribeHolder(string path)
    {
        try
        {
            var text = File.ReadAllText(HolderPathIn(System.IO.Path.GetDirectoryName(path)!)).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                try
                {
                    using var holder = Process.GetProcessById(pid);
                    return $"pid {pid} ({holder.ProcessName})";
                }
                catch (ArgumentException)
                {
                    return $"a process that did not record its pid (the recorded pid {pid} is gone)";
                }
            }
        }
        catch (Exception)
        {
            // No record, or one we cannot read. Say nothing rather than guess.
        }

        return "another process";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}

/// <summary>
/// Holds the process's <see cref="DaemonInstanceLock"/> for as long as the host lives.
///
/// <para>Registered as a singleton in <c>ConfigureServices</c> (so container disposal releases the lock
/// on a clean shutdown) but ACQUIRED later, from the Kestrel options callback — the one hook that runs
/// on a real listening daemon and never under the in-proc <c>TestServer</c> tier, where every fixture
/// shares one data root by design and a single-instance guard would refuse the second host.</para>
/// </summary>
public sealed class DaemonInstanceLockHolder : IDisposable
{
    private DaemonInstanceLock? _held;

    /// <summary>The acquired lock, or null when this host never took one (the in-proc test tier).</summary>
    public DaemonInstanceLock? Held => _held;

    /// <summary>
    /// Takes the lock on <paramref name="directory"/>. Idempotent per host: a second call on a holder
    /// that already owns the lock is a no-op, so a Kestrel options re-materialization cannot deadlock
    /// against itself.
    /// </summary>
    public void Acquire(string directory)
    {
        if (_held is not null)
        {
            return;
        }

        _held = DaemonInstanceLock.Acquire(directory);
    }

    public void Dispose()
    {
        _held?.Dispose();
        _held = null;
    }
}

/// <summary>
/// Thrown when a second daemon tries to come up against a data root another daemon already holds.
/// Distinct from <see cref="DaemonStartupException"/> (a port failure) because the operator action is
/// different: nothing is wrong with the port, there is simply already a daemon.
/// </summary>
public sealed class DaemonAlreadyRunningException : Exception
{
    public DaemonAlreadyRunningException(string lockPath, string holder, Exception? inner = null)
        : base($"Another Mainguard daemon is already running against this data root — {holder} holds "
               + $"'{lockPath}'. THIS instance has stopped and written nothing: the daemon that holds "
               + "the data root keeps running, and its session token and mTLS material are untouched, so "
               + "any client already talking to it is unaffected. Stop that daemon before starting "
               + "another, or give this one its own data root (MAINGUARD_DATA_ROOT) as well as its own "
               + "--port. Two daemons on one data root rotate each other's session token and mTLS "
               + "material and clear each other's migration lock, which is why this is refused.",
               inner)
    {
        LockPath = lockPath;
        Holder = holder;
    }

    /// <summary>The lock file that could not be taken.</summary>
    public string LockPath { get; }

    /// <summary>A human description of the holder, for the operator-facing message.</summary>
    public string Holder { get; }
}
