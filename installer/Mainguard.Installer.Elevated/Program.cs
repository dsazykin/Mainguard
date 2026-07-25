using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Installer.Elevated;

/// <summary>
/// The P2-21 elevated helper (the single UAC boundary). It performs EXACTLY the two enumerated
/// privileged actions from <see cref="InstallerCommands.PrivilegedActionCatalog"/> —
/// <list type="number">
///   <item>Enable the two Windows optional features (<c>Enable-WindowsOptionalFeature</c>).</item>
///   <item>Register the elevated ONLOGON resume Scheduled Task — but only when enabling the features
///   actually requires a reboot (never a one-shot registry autostart, which would resume unelevated).
///   A machine that already has WSL2 on needs no reboot, so no resume task is left behind — and any
///   STALE resume task from a previous interrupted setup is deleted (the inverse of this same action,
///   not a new privileged capability: an elevated task can't reliably be removed unelevated, and a
///   lingering one re-runs setup elevated at every logon, racing the wizard over the state files).</item>
/// </list>
/// — then reports back to the unelevated OOBE via a process exit code + a JSON result file. No other
/// privileged work ever moves in here (plan §7 rejection trigger). The actual DISM/schtasks execution
/// is Windows-only and validated by the human install matrix; this file compiles everywhere.
///
/// Usage: <c>Mainguard.Installer.Elevated --resume-target &lt;oobe.exe&gt; --result &lt;result.json&gt;</c>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var (resumeTarget, resultPath) = ParseArgs(args);
        if (resumeTarget is null || resultPath is null)
        {
            Console.Error.WriteLine("usage: Mainguard.Installer.Elevated --resume-target <oobe.exe> --result <result.json>");
            return (int)ElevatedHelperExitCode.BadArguments;
        }

        // MG-9: we are ALREADY ELEVATED at this point, and --resume-target came straight off argv. It
        // used to be handed to schtasks unchecked, which turned "can invoke this helper" into "can
        // register any executable on the machine as an ONLOGON task running as administrator with no
        // UAC prompt" — a permanent, silent privilege escalation from a single argument. Validate
        // before anything privileged happens, and refuse loudly rather than proceeding: an argument we
        // cannot vouch for is the one case where doing nothing is unambiguously correct.
        //
        // The install root is THIS helper's own directory: the packaged build co-locates the helper
        // with the exe it resumes, so a legitimate target is always a sibling. (Honest limit: an
        // attacker who can write to that directory can replace the sibling in place and this check
        // still passes — see TrustedExecutablePath. On the per-user install layout that directory is
        // writable by the user, so this bounds the target, it does not authenticate it.)
        if (!TrustedExecutablePath.TryValidate(
                resumeTarget, AppContext.BaseDirectory, out var validatedResumeTarget, out var refusal))
        {
            var message = $"refusing --resume-target '{resumeTarget}': {refusal}.";
            Console.Error.WriteLine(message);
            WriteResult(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = false,
                RebootRequired = false,
                ResumeTaskRegistered = false,
                Error = message,
            });
            return (int)ElevatedHelperExitCode.BadArguments;
        }

        resumeTarget = validatedResumeTarget;

        // Action 1: enable the two features. The raw command was surfaced to the user before the UAC prompt.
        // DISM's own RestartNeeded flag (read back from the script's marker line) decides the reboot — a
        // machine that already has WSL2 enabled reports RestartNeeded=false and is never rebooted again.
        if (!TryEnableFeatures(out var rebootRequired, out var error))
        {
            WriteResult(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = false,
                RebootRequired = false,
                ResumeTaskRegistered = false,
                Error = error,
            });
            return (int)ElevatedHelperExitCode.FeatureEnableFailed;
        }

        // Action 2: register the elevated resume Scheduled Task — but ONLY when a reboot will actually
        // interrupt setup. Its sole purpose is to re-enter the OOBE (elevated) after the reboot; when no
        // reboot is needed the same OOBE process continues straight to VM import, so registering it would
        // just leave a stale ONLOGON task behind.
        var resumeTaskRegistered = false;
        if (rebootRequired)
        {
            if (!TryRegisterResumeTask(resumeTarget, out error))
            {
                WriteResult(resultPath, new ElevatedHelperResult
                {
                    FeaturesEnabled = true,
                    RebootRequired = true,
                    ResumeTaskRegistered = false,
                    Error = error,
                });
                return (int)ElevatedHelperExitCode.ResumeTaskRegistrationFailed;
            }
            resumeTaskRegistered = true;
        }
        else
        {
            // No reboot → no resume task is needed. Best-effort delete any resume task a PREVIOUS
            // interrupted setup left behind (a run that required a reboot but never reached Done, or a
            // registration pointing at a since-moved/retired exe). We are elevated here — the only
            // reliable place to remove an elevated task (the unelevated OOBE's delete can be denied).
            // A lingering ONLOGON task silently re-runs setup ELEVATED at every logon and races the
            // wizard over the shared oobe-state/result files, so it must never survive a no-reboot run.
            TryRun("schtasks.exe", InstallerCommands.UnregisterResumeTask(), out _, out _);
        }

        WriteResult(resultPath, new ElevatedHelperResult
        {
            FeaturesEnabled = true,
            RebootRequired = rebootRequired,
            ResumeTaskRegistered = resumeTaskRegistered,
        });
        await Task.CompletedTask;
        return (int)ElevatedHelperExitCode.Success;
    }

    private static bool TryEnableFeatures(out bool rebootRequired, out string? error)
    {
        // One PowerShell invocation running the exact command InstallerCommands surfaces to the user.
        var ps = InstallerCommands.EnableFeaturesPowerShell();
        if (!TryRun("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", ps }, out error, out var stdout))
        {
            rebootRequired = false;
            return false;
        }
        rebootRequired = ParseRestartNeeded(stdout);
        return true;
    }

    /// <summary>Reads DISM's reboot decision back from the script's <c>MAINGUARD_RESTART_NEEDED=</c> marker.
    /// Absent/unparseable output is treated as "reboot needed" — the safe side (never skip a required
    /// reboot), so only an explicit <c>False</c> suppresses it.</summary>
    private static bool ParseRestartNeeded(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf(InstallerCommands.RestartNeededMarker, StringComparison.Ordinal);
            if (i < 0)
                continue;
            var value = line[(i + InstallerCommands.RestartNeededMarker.Length)..].Trim();
            return !bool.TryParse(value, out var restart) || restart;
        }
        return true;
    }

    /// <summary>Registers the resume task. The builder re-validates the target against this helper's
    /// own directory (MG-9) — a second check on an already-checked value, deliberately, so the
    /// privileged shape is safe no matter which path reaches it.</summary>
    private static bool TryRegisterResumeTask(string resumeTarget, out string? error)
    {
        try
        {
            var args = InstallerCommands.RegisterResumeTask(resumeTarget, AppContext.BaseDirectory);
            return TryRun("schtasks.exe", args, out error, out _);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryRun(string exe, IReadOnlyList<string> args, out string? error, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                // P2-48: windowless — powershell/schtasks must never flash a console during the
                // elevated step (the only UI the user sees is the Windows UAC consent dialog).
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null)
            {
                error = $"Could not start {exe}.";
                return false;
            }
            // Drain stdout async while reading stderr sync — reading both streams synchronously can
            // deadlock if either child buffer fills.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            stdout = stdoutTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0)
            {
                error = $"{exe} exited {p.ExitCode}: {stderr.Trim()}";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // On non-Windows (this sandbox) powershell/schtasks are absent — the helper only runs on the
            // real Windows install matrix. Surface the reason honestly rather than pretend success.
            error = $"{exe} could not be run on this platform: {ex.Message}";
            return false;
        }
    }

    private static void WriteResult(string path, ElevatedHelperResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, result.Serialize());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write result file '{path}': {ex.Message}");
        }
    }

    private static (string? resumeTarget, string? resultPath) ParseArgs(string[] args)
    {
        string? resumeTarget = null, resultPath = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--resume-target": resumeTarget = args[++i]; break;
                case "--result": resultPath = args[++i]; break;
            }
        }
        return (resumeTarget, resultPath);
    }
}
