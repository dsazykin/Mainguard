using System;
using System.Threading;
using Avalonia;
using Avalonia.Diagnostics;

namespace GitLoom.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "credential")
        {
            // Placeholder for A-1.7
        }
        else if (args.Length >= 3 && args[0] == "--rebase-editor")
        {
            // git reads exit 0 as "the editor wrote your todo" and otherwise proceeds with its own
            // default todo (a plain `pick` of everything), discarding the user's plan while still
            // reporting success. RebaseEditorShim returns non-zero on any failure so git aborts.
            Environment.ExitCode = RebaseEditorShim.WriteTodo(args[1], args[^1]);
            return;
        }
        else if (args.Length >= 3 && args[0] == "--rebase-msg")
        {
            // git invokes GIT_EDITOR once per reword and once per squash *chain*, passing the
            // message file (e.g. .git/COMMIT_EDITMSG). Same exit-code contract as the todo above.
            Environment.ExitCode = RebaseEditorShim.WriteRebaseMessage(args[1], args[^1]);
            return;
        }

        // Single-instance guard. Two live GitLoom processes would contend for the SQLite database
        // lock and the second would hang forever on startup migration (the exact bug that leaves a
        // dead-looking, windowless process). One GUI per user session; a second launch exits at once.
        // Placed AFTER the helper-subprocess branches above (which already returned) so the rebase /
        // credential editor invocations the app makes of itself are never blocked. A killed instance
        // frees the mutex automatically, so a crash never wedges the next launch.
        using var singleInstance = new Mutex(initiallyOwned: true, "GitLoom.App.SingleInstance", out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Console.Error.WriteLine("GitLoom is already running.");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
