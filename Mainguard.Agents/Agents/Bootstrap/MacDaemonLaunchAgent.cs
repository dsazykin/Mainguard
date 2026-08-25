using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Optional launchd integration on the macos-host substrate: "keep the agent platform running at
/// login." Installs a per-user LaunchAgent that starts mainguardd from the app payload at login
/// and restarts it if it dies (<c>KeepAlive</c>), so merge queues and agents survive reboots
/// without the app open. Uninstall boots the job out and removes the plist. With the agent
/// installed, a daemon refresh degenerates to "stop it and let launchd respawn from the payload"
/// — the same payload directory <see cref="MacDaemonUpdater"/> already restarts from, so the two
/// paths cannot drift. Everything here is per-user (`gui/<uid>`); nothing elevates.
/// </summary>
public sealed class MacDaemonLaunchAgent
{
    public const string Label = "com.mainguard.daemon";

    private static string PlistPath() => Path.Combine(
        Mainguard.Git.MainguardPaths.HomeDirectory(), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>True when the LaunchAgent plist is installed (the login-time contract; whether the
    /// job is currently loaded is launchd's business and heals at next login either way).</summary>
    public bool IsInstalled() => File.Exists(PlistPath());

    /// <summary>Writes the plist for the CURRENT app's payload and loads it. Idempotent — an
    /// existing job is booted out first so a moved payload path takes effect.</summary>
    public async Task<bool> InstallAsync(string payloadDirectory, CancellationToken ct = default)
    {
        var dll = Path.Combine(payloadDirectory, "Mainguard.Server.dll");
        if (!File.Exists(dll)) return false;

        var plist = PlistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);

        // The muxer, not the payload apphost: same macOS name-pinning reasoning as
        // MacDaemonController (a copied apphost outside its first-run location is SIGKILLed).
        var dotnet = MacDaemonController.DotnetMuxerPath();
        File.WriteAllText(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{dotnet}</string>
                <string>{dll}</string>
              </array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ProcessType</key><string>Background</string>
            </dict>
            </plist>
            """);

        await LaunchctlAsync(ct, "bootout", GuiDomain() + "/" + Label).ConfigureAwait(false); // tolerate "not loaded"
        var loaded = await LaunchctlAsync(ct, "bootstrap", GuiDomain(), plist).ConfigureAwait(false);
        return loaded == 0;
    }

    /// <summary>Boots the job out and removes the plist. The daemon itself is left to launchd's
    /// bootout (which stops it); a manual daemon start still works exactly as before.</summary>
    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await LaunchctlAsync(ct, "bootout", GuiDomain() + "/" + Label).ConfigureAwait(false);
        try { File.Delete(PlistPath()); } catch (IOException) { }
    }

    private static string GuiDomain() => "gui/" + Interop.GetUid();

    private static async Task<int> LaunchctlAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/launchctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var launchctl = Process.Start(psi);
        if (launchctl is null) return -1;
        await launchctl.WaitForExitAsync(ct).ConfigureAwait(false);
        return launchctl.ExitCode;
    }

    private static class Interop
    {
        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern uint getuid();

        internal static uint GetUid() => getuid();
    }
}
