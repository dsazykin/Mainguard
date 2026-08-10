using System;
using System.Threading;
using Avalonia;
using Mainguard.Git;

namespace Mainguard.App.Shell;

/// <summary>
/// The shared entry-point plumbing both exe heads (Mainguard.Client.App / Mainguard.Pro.App) call from
/// their thin <c>Main</c> (step 2f). Each head first sets <see cref="App.Edition"/> (+ the Pro head its
/// composition seams), then delegates the edition-agnostic parts here: the git-editor / credential
/// self-invocation shims (which must run and return BEFORE anything else), the single-instance guard, and
/// the Avalonia app build. Keeping this in the shell means the two heads never duplicate the shim logic —
/// and the shims are the app invoking ITSELF, so both heads must expose them identically.
/// </summary>
public static class ShellEntryPoint
{
    /// <summary>
    /// Handles the app's self-invocation shims — the interactive-rebase todo/message editors and the
    /// credential-helper placeholder — that Git launches this executable to perform. Returns <c>true</c>
    /// when the process WAS such an invocation (it has done its work; the caller's <c>Main</c> must return
    /// immediately, before the single-instance guard, so the app's own rebase/credential calls of itself are
    /// never blocked). Returns <c>false</c> for an ordinary launch.
    ///
    /// <para><paramref name="exitCode"/> is not optional bookkeeping — <b>it is the contract with git</b>.
    /// Git reads exit 0 from GIT_SEQUENCE_EDITOR as "the editor wrote your todo" and otherwise falls back
    /// to its own default todo (a plain <c>pick</c> of every commit), silently discarding every reorder,
    /// squash, drop and fixup and then reporting the rebase as a success. The head MUST propagate this to
    /// <see cref="Environment.ExitCode"/> so a failed shim makes git abort instead. Out-parameter rather
    /// than a swallowed detail precisely so a head cannot forget it.</para>
    /// </summary>
    public static bool TryHandleShim(string[] args, out int exitCode)
    {
        exitCode = RebaseEditorShim.Success;

        if (args.Length >= 2 && args[0] == "credential")
        {
            // Placeholder for A-1.7
            return true;
        }

        if (args.Length >= 3 && args[0] == "--rebase-editor")
        {
            exitCode = RebaseEditorShim.WriteTodo(args[1], args[^1]);
            return true;
        }

        if (args.Length >= 3 && args[0] == "--rebase-msg")
        {
            // git invokes GIT_EDITOR once per reword and once per squash *chain*, passing the
            // message file (e.g. .git/COMMIT_EDITMSG). Same exit-code contract as the todo above.
            exitCode = RebaseEditorShim.WriteRebaseMessage(args[1], args[^1]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The ordinary launch: a single-instance guard (two live Mainguard processes would contend for the
    /// SQLite database lock and the second would hang forever on startup migration — the exact bug that
    /// leaves a dead-looking, windowless process), then the classic desktop lifetime. A killed instance
    /// frees the mutex automatically, so a crash never wedges the next launch. Call this AFTER
    /// <see cref="TryHandleShim"/> returned false and the head has selected its edition.
    /// </summary>
    public static void RunDesktop(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, "Mainguard.App.SingleInstance", out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Console.Error.WriteLine("Mainguard is already running.");
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
