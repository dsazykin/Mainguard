using System;
using Avalonia;
using Mainguard.App.Shell;
using Mainguard.App.Shell.Editions;
using Mainguard.UI;
using Mainguard.UI.Editions;
using ShellApp = Mainguard.App.Shell.App;

namespace Mainguard.Client.App;

/// <summary>
/// The plain Git client exe head (step 2f). Thin by design: run the shared git-editor / credential shims,
/// select the Client edition, then hand off to the shared shell entry point. References
/// Mainguard.App.Shell ONLY — never the agent platform — which is what makes its published closure exclude
/// Mainguard.Agents(.UI) / Mainguard.Protos / Docker.DotNet / Porta.Pty / Grpc / Dock entirely (THE PAYOFF,
/// proven by the .deps.json closure check).
/// </summary>
internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Git-editor / credential self-invocation shims run and return FIRST (before the single-instance
        // guard), so the app's own rebase/credential calls of itself are never blocked. The shim's exit
        // code is the contract with git: 0 means "the editor wrote your todo", so a failed shim MUST
        // report non-zero or git rebases with its own default plain-`pick` todo and calls it a success.
        if (ShellEntryPoint.TryHandleShim(args, out int shimExitCode))
        {
            Environment.ExitCode = shimExitCode;
            return;
        }

        // This process is the plain Git client — no agent platform (App.Edition defaults to Client, set
        // explicitly here for clarity and so the choice is co-located with the head).
        ShellApp.Edition = new ClientManifest();

        ShellEntryPoint.RunDesktop(args);
    }

    // Avalonia configuration / visual-designer entry point; delegates to the shell's shared builder.
    public static AppBuilder BuildAvaloniaApp() => ShellEntryPoint.BuildAvaloniaApp();
}
