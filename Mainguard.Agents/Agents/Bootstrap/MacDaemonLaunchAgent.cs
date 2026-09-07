using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Optional launchd integration on the macos-host substrate: "keep the agent platform running at
/// login." Installs a per-user LaunchAgent that starts mainguardd from a STAGED copy of the app payload
/// at login and restarts it when it dies unexpectedly, so merge queues and agents survive reboots
/// without the app open. Uninstall boots the job out and removes the plist. Everything here is per-user
/// (<c>gui/&lt;uid&gt;</c>); nothing elevates.
///
/// <para><b>F63 — the five defects this file used to have, and what replaced each.</b></para>
/// <list type="number">
///   <item><b>(a) A bare <c>dotnet</c> in <c>ProgramArguments</c>.</b> launchd's <c>PATH</c> is
///   <c>/usr/bin:/bin:/usr/sbin:/sbin</c> — it contains neither the official installer's
///   <c>/usr/local/share/dotnet</c> nor any Homebrew prefix — so the job could not exec, exited at once,
///   and was respawned forever by (b). <see cref="InstallAsync"/> now RESOLVES an absolute muxer and
///   refuses to write a plist without one, rather than writing a job that cannot work.</item>
///   <item><b>(b) Unconditional <c>KeepAlive</c>.</b> "Always restart" turns any permanent failure into
///   an infinite respawn loop, and (pre-F55) each respawn rotated the live daemon's session token and
///   mTLS material on its way to failing. It is now a dict — restart on a crash or a non-zero exit, stay
///   down after a clean exit — plus a <c>ThrottleInterval</c> so a persistent failure costs one exec
///   every 30 s instead of ten a second.</item>
///   <item><b>(c) Unescaped XML.</b> Every interpolated path went into the plist raw. A path containing
///   <c>&amp;</c> — ordinary in a macOS home directory or an app name — produced a malformed plist that
///   <c>launchctl bootstrap</c> rejects, and the failure surfaced as "the login job just does not work".
///   Every interpolation now goes through <see cref="SecurityElement.Escape"/>.</item>
///   <item><b>(d) No <c>StandardErrorPath</c>.</b> The daemon's own file logging starts after its logging
///   pipeline is built; anything that kills it before that — a missing runtime, a bad payload, a
///   <c>DllNotFoundException</c> — printed to a stderr launchd discarded. Both streams now land beside the
///   daemon's other logs, which is the difference between "it does not start" and a stack trace.</item>
///   <item><b>(e) The job pointed inside the <c>.app</c> bundle.</b> A bundle replaced in place (any app
///   update) changes assemblies under a running process that loads them lazily, so a long-lived daemon
///   could bind a mix of old and new. The payload is now STAGED to
///   <c>~/.mainguard/daemon-payload/</c> at install time and the job runs from there; the bundle is
///   free to be replaced, and a refresh re-stages before restarting.</item>
/// </list>
/// </summary>
public sealed class MacDaemonLaunchAgent
{
    public const string Label = "com.mainguard.daemon";

    /// <summary>How long launchd waits between respawns of a job that keeps failing (F63b).</summary>
    private const int ThrottleSeconds = 30;

    private static string PlistPath() => Path.Combine(
        Mainguard.Git.MainguardPaths.HomeDirectory(), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>
    /// Where the LaunchAgent's copy of the daemon payload lives — under the data root, never inside the
    /// <c>.app</c> bundle (F63e). Stable across app upgrades, and outside anything an installer replaces
    /// wholesale while the daemon is running.
    /// </summary>
    public static string StagedPayloadDirectory() =>
        Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "daemon-payload");

    /// <summary>Where launchd's captured stdout/stderr land (F63d), beside the daemon's own logs.</summary>
    private static string LaunchdLogDirectory() =>
        Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "logs");

    /// <summary>True when the LaunchAgent plist is installed (the login-time contract; whether the
    /// job is currently loaded is launchd's business and heals at next login either way).</summary>
    public bool IsInstalled() => File.Exists(PlistPath());

    /// <summary>
    /// Stages the payload out of the bundle, writes the plist for the staged copy, and loads it.
    /// Idempotent — an existing job is booted out first so a moved payload path takes effect.
    ///
    /// <para>Returns false without writing anything when the payload is missing, or when no absolute
    /// dotnet muxer can be found (F63a): a login job built on a bare <c>dotnet</c> is not a degraded
    /// install, it is one that cannot start, and writing it would hide the real problem behind an
    /// infinite respawn.</para>
    /// </summary>
    public async Task<bool> InstallAsync(string payloadDirectory, CancellationToken ct = default)
    {
        var sourceDll = Path.Combine(payloadDirectory, "Mainguard.Server.dll");
        if (!File.Exists(sourceDll)) return false;

        // The muxer, not the payload apphost: same macOS name-pinning reasoning as MacDaemonController
        // (a copied apphost outside its first-run location is SIGKILLed). Absolute, or nothing.
        var dotnet = MacDaemonController.TryResolveAbsoluteMuxerPath();
        if (dotnet is null) return false;

        var staged = StagePayload(payloadDirectory);
        var dll = Path.Combine(staged, "Mainguard.Server.dll");
        var logs = LaunchdLogDirectory();
        Directory.CreateDirectory(logs);

        var plist = PlistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
        File.WriteAllText(plist, RenderPlist(dotnet, dll, staged, logs));

        await LaunchctlAsync(ct, "bootout", GuiDomain() + "/" + Label).ConfigureAwait(false); // tolerate "not loaded"
        var loaded = await LaunchctlAsync(ct, "bootstrap", GuiDomain(), plist).ConfigureAwait(false);
        return loaded == 0;
    }

    /// <summary>
    /// Renders the plist. Every interpolated value is XML-escaped (F63c) — a home directory or app name
    /// containing <c>&amp;</c>, <c>&lt;</c> or <c>'</c> is ordinary on macOS and used to produce a
    /// document <c>launchctl bootstrap</c> silently refused.
    /// </summary>
    /// <remarks>Internal so the plist can be asserted as parseable, escaped XML without touching launchd.</remarks>
    internal static string RenderPlist(string dotnet, string dll, string workingDirectory, string logDirectory)
    {
        var outLog = Escape(Path.Combine(logDirectory, "launchd.out.log"));
        var errLog = Escape(Path.Combine(logDirectory, "launchd.err.log"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Escape(Label)}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{Escape(dotnet)}</string>
                <string>{Escape(dll)}</string>
              </array>
              <key>WorkingDirectory</key><string>{Escape(workingDirectory)}</string>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key>
              <dict>
                <key>Crashed</key><true/>
                <key>SuccessfulExit</key><false/>
              </dict>
              <key>ThrottleInterval</key><integer>{ThrottleSeconds}</integer>
              <key>StandardOutPath</key><string>{outLog}</string>
              <key>StandardErrorPath</key><string>{errLog}</string>
              <key>EnvironmentVariables</key>
              <dict>
                <key>DOTNET_HOST_PATH</key><string>{Escape(dotnet)}</string>
                <key>PATH</key><string>{Escape(JobPath(dotnet))}</string>
              </dict>
              <key>ProcessType</key><string>Background</string>
            </dict>
            </plist>
            """;
    }

    /// <summary>
    /// The <c>PATH</c> the job runs with: launchd's default, plus the directory the muxer was found in,
    /// plus both Homebrew prefixes. The daemon shells out to <c>git</c>, <c>docker</c> and
    /// <c>launchctl</c>; under launchd's bare four-entry PATH a Homebrew <c>docker</c> is simply not
    /// there, which reads downstream as "the engine is unreachable" rather than as a PATH problem.
    /// </summary>
    private static string JobPath(string dotnet)
    {
        var muxerDir = Path.GetDirectoryName(dotnet);
        var entries = new[]
        {
            muxerDir,
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
        };

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var kept = new System.Collections.Generic.List<string>();
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry) && seen.Add(entry))
            {
                kept.Add(entry);
            }
        }

        return string.Join(':', kept);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>
    /// F63e: copies the payload out of whatever directory the app shipped it in (typically inside the
    /// <c>.app</c> bundle) into <see cref="StagedPayloadDirectory"/>, which no app update rewrites.
    ///
    /// <para>Copied rather than symlinked deliberately: a symlink into the bundle reintroduces exactly
    /// the failure mode — the running daemon lazily loads an assembly through the link and gets the NEW
    /// bundle's copy, mixed with the old ones it already loaded.</para>
    ///
    /// <para>The staged copy is REPLACED wholesale rather than merged, so a file removed from the payload
    /// between versions does not survive as a stale assembly. Returns the staged directory; on any IO
    /// failure it returns the source, because a login job pointed at the bundle is still better than no
    /// login job at all.</para>
    /// </summary>
    internal static string StagePayload(string payloadDirectory)
    {
        var staged = StagedPayloadDirectory();
        if (SamePath(payloadDirectory, staged))
        {
            return staged;
        }

        try
        {
            if (Directory.Exists(staged))
            {
                Directory.Delete(staged, recursive: true);
            }

            CopyDirectory(payloadDirectory, staged);
            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return payloadDirectory;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.Ordinal);

    /// <summary>Boots the job out and removes the plist. The daemon itself is left to launchd's
    /// bootout (which stops it); a manual daemon start still works exactly as before.</summary>
    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await LaunchctlAsync(ct, "bootout", GuiDomain() + "/" + Label).ConfigureAwait(false);
        try { File.Delete(PlistPath()); } catch (IOException) { }
    }

    /// <summary>
    /// Restarts the job in place — launchd stops the old process and starts a new one from the plist,
    /// with no window in which two daemons exist or none does. Used by <see cref="MacDaemonUpdater"/>
    /// instead of its own stop+start when the agent is installed.
    /// </summary>
    public Task<int> KickstartAsync(CancellationToken ct = default) =>
        LaunchctlAsync(ct, "kickstart", "-k", GuiDomain() + "/" + Label);

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
