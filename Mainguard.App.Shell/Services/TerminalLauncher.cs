using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mainguard.App.Shell.Services;

/// <summary>
/// The single place a local folder is opened in the OS terminal (mirrors
/// <see cref="FileExplorerLauncher"/>'s reveal path, same hygiene: refuse anything that is not an
/// existing directory, never crash the caller). macOS uses <c>open -a Terminal</c> — the user's
/// default terminal replacement (iTerm etc.) can be a later preference; Windows prefers Windows
/// Terminal and falls back to cmd; Linux asks the alternatives system.
/// </summary>
public static class TerminalLauncher
{
    public static void OpenTerminal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", "Terminal", path } });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows Terminal when installed (its per-user execution alias resolves from
                // PATH — no GetFolderPath, which the repo-wide guard bans outside MainguardPaths),
                // classic host otherwise.
                try
                {
                    Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", path }, UseShellExecute = true });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = path, UseShellExecute = true });
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("x-terminal-emulator") { WorkingDirectory = path });
            }
        }
        catch
        {
            // Best-effort: a missing terminal must not take the app down.
        }
    }
}
