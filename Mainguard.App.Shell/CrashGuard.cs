using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git;

namespace Mainguard.App.Shell;

/// <summary>
/// The app-wide last line of defence against an unhandled exception, installed by
/// <see cref="ShellEntryPoint.RunDesktop"/> for BOTH heads.
///
/// <para><b>Why this exists (ISSUES-LOG #12).</b> An exception that escapes a dispatcher job takes the
/// whole process down — .NET finds no handler on the stack, calls <c>abort()</c>, and macOS files a
/// SIGABRT crash report. Clicking Coordinator → Restart did exactly that on-device: the client died
/// mid-<c>SpawnAgent</c>, killing every open stream RPC with it. A GUI has no business dying because one
/// command faulted, and the post-mortem <c>.ips</c> carries no managed exception type — so the crash was
/// simultaneously fatal and undiagnosable.</para>
///
/// <para>Two jobs, in this order of importance:
/// <list type="number">
/// <item><b>Survive.</b> <see cref="Dispatcher.UnhandledException"/> is Avalonia's sanctioned hook for
/// exactly this; marking it handled keeps the message loop alive. An `async void` rethrow (which is what
/// <c>[RelayCommand]</c>'s <c>AsyncRelayCommand</c> does with a faulted command body — it posts the
/// exception back onto the synchronization context) lands here, and so does anything thrown out of a
/// <c>Dispatcher.UIThread.Post</c> callback.</item>
/// <item><b>Say what happened.</b> Every catch writes the full exception — type, message, stack, inner
/// chain — to <c>&lt;data root&gt;/logs/client-crash.log</c>, and raises a shell toast so the user is not
/// left guessing why an action did nothing. The client had no file log at all before this; the only
/// evidence a crash left behind was an unsymbolicated native stack.</item>
/// </list></para>
///
/// <para>This is a safety net, NOT a licence to leave faulting commands unguarded — a command that can
/// fail should still catch its own failure and render it in context. The net only guarantees that
/// forgetting to do so costs a toast instead of the session.</para>
/// </summary>
public static class CrashGuard
{
    private static int _installed;

    /// <summary>Path the guard writes to — beside the daemon's own logs so a bug report picks both up.</summary>
    public static string LogPath { get; } =
        Path.Combine(MainguardPaths.DataRoot(), "logs", "client-crash.log");

    /// <summary>Installs the handlers. Idempotent, and safe to call before a window exists.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        // The one that actually prevents the abort: anything thrown out of a dispatcher job (posted
        // callbacks, async-void rethrows, command bodies) is reported and swallowed rather than fatal.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Report(e.Exception, "dispatcher");
            e.Handled = true;
        };

        // A background-thread escape cannot be handled — the runtime is already committed to dying — but
        // it CAN be recorded, which is the whole difference between a diagnosable crash and this bug.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, e.IsTerminating ? "fatal" : "appdomain", toast: false);

        // Fire-and-forget tasks (`_ = SomethingAsync()`) whose failure nobody ever looked at. Not fatal by
        // default on .NET Core, but silently losing them is how a dead pump goes unnoticed for a session.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, "unobserved-task", toast: false);
            e.SetObserved();
        };
    }

    /// <summary>Writes one entry and (optionally) surfaces it. Never throws: a failure to log a crash
    /// must not become the crash.</summary>
    public static void Report(Exception? ex, string origin, bool toast = true)
    {
        if (ex is null) return;

        try
        {
            var text = new StringBuilder()
                .Append("==== ")
                .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append("  origin=").Append(origin)
                .Append("  thread=").Append(Environment.CurrentManagedThreadId)
                .AppendLine()
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch
        {
            // No log directory / read-only volume — the toast below is then the only report, which is
            // still better than dying silently.
        }

        try { Console.Error.WriteLine($"[crash-guard:{origin}] {ex}"); } catch { /* no stderr */ }

        if (!toast) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
            {
                shell.ShowToast(
                    $"Something went wrong: {ex.Message} (details in {LogPath})", isError: true);
            }
        }
        catch
        {
            // No window yet, or the toast surface itself is the thing that faulted — the file entry stands.
        }
    }
}
