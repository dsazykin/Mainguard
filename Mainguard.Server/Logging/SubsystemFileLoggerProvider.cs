using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Logging;

/// <summary>
/// Routes each daemon log category to its own rolling file under the logs directory
/// (<c>&lt;subsystem&gt;.log</c>), size-capped (5 MB × 3 by default), flushed per line so a <c>tail</c>
/// reflects reality even mid-hang. One line per event:
/// <c>{ts:O} [LVL] [subsystem] (scope) message</c> with any exception on the following lines
/// (mirrors the App's <c>LogOobe</c> ISO-8601 UTC style).
///
/// <para><b>Process-static writers.</b> The pre-DI bootstrap <see cref="ILoggerFactory"/> (startup +
/// migration milestones) and the runtime DI factory both build a provider over the SAME directory;
/// their writers are shared through a process-static, lock-guarded map keyed by file path, so the two
/// never open a file twice or interleave a partial line. <see cref="Dispose"/> is a deliberate no-op on
/// the shared writers — the other provider still needs them and each write is flushed, so process exit
/// loses nothing.</para>
///
/// <para><b>Owner-only on disk (F56).</b> The logs directory is created <c>0700</c> and every log
/// file is pre-created <c>0600</c> before a byte is written, so a line never lands under the
/// process umask (the default <c>0022</c> made <c>rpc.log</c> world-readable). On Windows the
/// directory and each file get an inheritance-free single-ACE DACL for the current user, mirroring
/// <c>SessionTransportCertificates.WriteRestricted</c> — the repo's one pattern for this. Rolled
/// files inherit the mode through <see cref="File.Move(string,string,bool)"/>. Hardening is
/// best-effort like every other file operation here: a failure to tighten a mode drops through to
/// the write rather than costing the daemon its diagnostics.</para>
///
/// <para><b>Diagnostics must never break the daemon.</b> Every file operation is wrapped in a
/// try/catch that swallows: a read-only disk or a vanished directory silently drops the line rather
/// than faulting the logging call.</para>
/// </summary>
public sealed class SubsystemFileLoggerProvider : ILoggerProvider
{
    // Keyed by absolute file path so the bootstrap and runtime providers (same logsDir) share one
    // writer per file. Process-static — the whole point is cross-factory sharing.
    private static readonly ConcurrentDictionary<string, SubsystemWriter> Writers = new(StringComparer.Ordinal);

    private readonly string _logsDir;
    private readonly long _maxBytes;
    private readonly int _maxRoll;
    private readonly LogLevel _minLevel;

    public SubsystemFileLoggerProvider(
        string logsDir, long maxBytes = 5 * 1024 * 1024, int maxRoll = 3, LogLevel minLevel = LogLevel.Information)
    {
        _logsDir = logsDir;
        // Floor only guards against a zero/negative cap; production passes 5 MB. Kept small so a roll is
        // cheap to exercise in tests.
        _maxBytes = maxBytes < 64 ? 64 : maxBytes;
        _maxRoll = maxRoll < 1 ? 1 : maxRoll;
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(logsDir);
            RestrictedFiles.HardenDirectory(logsDir);
        }
        catch { /* first write retries the create; a create failure must never throw */ }
    }

    public ILogger CreateLogger(string categoryName)
    {
        var subsystem = DaemonLogCategories.Subsystem(categoryName);
        var path = Path.Combine(_logsDir, subsystem + ".log");
        var writer = Writers.GetOrAdd(path, p => new SubsystemWriter(p, _maxBytes, _maxRoll));
        return new SubsystemFileLogger(subsystem, writer, _minLevel);
    }

    public void Dispose()
    {
        // No-op: writers are process-static and shared with the other (bootstrap/runtime) provider.
        // Per-line flush means nothing is buffered to lose; the OS reclaims handles at process exit.
    }

    /// <summary>
    /// Owner-only creation for the logs directory and each log file (F56). Unix gets <c>0700</c> /
    /// <c>0600</c>; Windows gets an inheritance-free single full-control ACE for the current user.
    /// This mirrors <c>Mainguard.Server.Auth.SessionTransportCertificates.WriteRestricted</c> — the
    /// repo's existing pattern for a secret-bearing file — rather than inventing a second one.
    /// Every method is best-effort and never throws: a mode that cannot be tightened must not cost
    /// the daemon its logs.
    /// </summary>
    internal static class RestrictedFiles
    {
        private const UnixFileMode DirMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        /// <summary>Reduces an existing directory to owner-only.</summary>
        internal static void HardenDirectory(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RestrictWindowsDirectory(path);
                }
                else
                {
                    File.SetUnixFileMode(path, DirMode);
                }
            }
            catch
            {
                // Best effort — see the type doc.
            }
        }

        /// <summary>Creates <paramref name="path"/> owner-only when it does not exist yet. A no-op
        /// once the file is there, so it costs one <c>File.Exists</c> per line and never re-chmods a
        /// mode an operator deliberately widened.</summary>
        internal static void EnsureRestrictedFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return;
                }

                using (new FileStream(path, System.IO.FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RestrictWindowsFile(path);
                }
                else
                {
                    File.SetUnixFileMode(path, FileMode0600);
                }
            }
            catch
            {
                // Another writer won the create race, or the disk refused — the append below decides.
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void RestrictWindowsFile(string path)
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void RestrictWindowsDirectory(string path)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    /// <summary>The AsyncLocal scope stack rendered as <c>(scope)</c> — set by <c>ILogger.BeginScope</c>
    /// (e.g. the spawn chain's <c>BeginScope(agentId)</c>). Static so all subsystem loggers on one async
    /// flow render the same correlation id; each provider type keeps its own stack.</summary>
    private static class ScopeStack
    {
        private static readonly AsyncLocal<ScopeNode?> Head = new();

        public static IDisposable Push(object? state) => new ScopeNode(state, Head);

        /// <summary>The innermost non-empty scope's text, or "" when no scope is active.</summary>
        public static string Current
        {
            get
            {
                for (var node = Head.Value; node is not null; node = node.Parent)
                {
                    var text = node.State?.ToString();
                    if (!string.IsNullOrEmpty(text))
                        return text!;
                }

                return string.Empty;
            }
        }

        private sealed class ScopeNode : IDisposable
        {
            private readonly AsyncLocal<ScopeNode?> _slot;
            private bool _disposed;

            public ScopeNode(object? state, AsyncLocal<ScopeNode?> slot)
            {
                State = state;
                Parent = slot.Value;
                _slot = slot;
                slot.Value = this;
            }

            public object? State { get; }

            public ScopeNode? Parent { get; }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _slot.Value = Parent;
            }
        }
    }

    /// <summary>One subsystem file: lock-guarded append + size roll. Append-per-line (open/close each
    /// write) keeps the writer stream-free, so the roll's rename never fights an open handle and every
    /// line is durably flushed.</summary>
    private sealed class SubsystemWriter
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _maxRoll;
        private readonly object _gate = new();

        public SubsystemWriter(string path, long maxBytes, int maxRoll)
        {
            _path = path;
            _maxBytes = maxBytes;
            _maxRoll = maxRoll;
        }

        public void Write(string text)
        {
            lock (_gate)
            {
                try
                {
                    RollIfNeeded(text.Length);
                    // Pre-create owner-only: File.AppendAllText would otherwise create the file under
                    // the process umask, and the first line is already in it by the time a chmod runs.
                    RestrictedFiles.EnsureRestrictedFile(_path);
                    File.AppendAllText(_path, text, Utf8NoBom);
                }
                catch
                {
                    // Diagnostics must never break the daemon.
                }
            }
        }

        private void RollIfNeeded(int incomingChars)
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length + incomingChars < _maxBytes)
                    return;

                var dir = Path.GetDirectoryName(_path) ?? ".";
                var stem = Path.GetFileNameWithoutExtension(_path); // "spawn"
                var ext = Path.GetExtension(_path);                 // ".log"

                // Drop the oldest, shift the rest up (x.1→x.2 … x.(n-1)→x.n), then x.log → x.1.log.
                var oldest = Path.Combine(dir, $"{stem}.{_maxRoll}{ext}");
                if (File.Exists(oldest))
                    File.Delete(oldest);
                for (var i = _maxRoll - 1; i >= 1; i--)
                {
                    var src = Path.Combine(dir, $"{stem}.{i}{ext}");
                    if (File.Exists(src))
                        File.Move(src, Path.Combine(dir, $"{stem}.{i + 1}{ext}"), overwrite: true);
                }

                File.Move(_path, Path.Combine(dir, $"{stem}.1{ext}"), overwrite: true);
            }
            catch
            {
                // A failed roll falls through to append: a too-large file beats a dropped line.
            }
        }
    }

    private sealed class SubsystemFileLogger : ILogger
    {
        private readonly string _subsystem;
        private readonly SubsystemWriter _writer;
        private readonly LogLevel _minLevel;

        public SubsystemFileLogger(string subsystem, SubsystemWriter writer, LogLevel minLevel)
        {
            _subsystem = subsystem;
            _writer = writer;
            _minLevel = minLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => ScopeStack.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
                return;

            var sb = new StringBuilder(160);
            sb.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(Level3(logLevel))
                .Append("] [").Append(_subsystem)
                .Append("] (").Append(ScopeStack.Current).Append(") ")
                .Append(formatter(state, exception))
                .Append('\n');

            if (exception is not null)
            {
                sb.Append(exception.GetType().FullName).Append(": ").Append(exception.Message).Append('\n');
                if (exception.StackTrace is { Length: > 0 } stack)
                    sb.Append(stack).Append('\n');
            }

            _writer.Write(sb.ToString());
        }

        private static string Level3(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "LOG",
        };
    }
}
