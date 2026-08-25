using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mainguard.App.Shell.Services;

/// <summary>
/// The single place a local folder is handed to the OS file explorer to be revealed
/// (mirrors <see cref="BrowserLauncher"/>'s open-a-URL path). Refuses anything that is
/// not an existing directory — callers may pass paths sourced from freshly-created
/// worktrees, and a typo or race should never launch an OS shell command against
/// garbage input. Revealing a folder is best-effort; failures never crash the caller.
/// </summary>
public static class FileExplorerLauncher
{
    public static void RevealFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // ArgumentList, not a pre-quoted argument string: Process.Start(cmd, arg)
                // passes the arg verbatim, so hand-added quotes become part of the path.
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path } });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { path } });
            }
        }
        catch
        {
            // Best-effort: a missing file manager/handler must not take the app down.
        }
    }
}
