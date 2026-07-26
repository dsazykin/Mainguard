using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Installer.Elevated;

/// <summary>
/// The P2-21 elevated helper (the single UAC boundary). It performs EXACTLY the enumerated privileged
/// actions from <see cref="InstallerCommands.PrivilegedActionCatalog"/> —
/// <list type="number">
///   <item>Enable the two Windows optional features (<c>Enable-WindowsOptionalFeature</c>).</item>
///   <item>Register the resume ONLOGON Scheduled Task — but only when enabling the features
///   actually requires a reboot (never a one-shot registry autostart, which would resume unelevated).
///   A machine that already has WSL2 on needs no reboot, so no resume task is left behind — and any
///   STALE resume task from a previous interrupted setup is deleted (the inverse of this same action,
///   not a new privileged capability: an elevated task can't reliably be removed unelevated, and a
///   lingering one re-runs setup elevated at every logon, racing the wizard over the state files).</item>
///   <item><b>(MG-15)</b> Install Mainguard's elevated components into the administrator-owned install
///   root, so the binary that runs with administrator rights stops living in a directory the
///   unprivileged user can rewrite. Source and destination are both derived — never taken from argv;
///   see <see cref="InstallerCommands.PrivilegedActionCatalog"/> for why that distinction is the whole
///   safety argument. Removing that install (<c>--uninstall</c>) is the inverse of the same action.</item>
/// </list>
/// — then reports back to the unelevated OOBE via a process exit code + a JSON result file. No other
/// privileged work ever moves in here (plan §7 rejection trigger). The actual DISM/schtasks execution
/// is Windows-only and validated by the human install matrix; this file compiles everywhere.
///
/// Usage: <c>Mainguard.Installer.Elevated --resume-target &lt;oobe.exe&gt; --result &lt;result.json&gt;</c>
/// or:    <c>Mainguard.Installer.Elevated --uninstall</c>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The uninstall inverse: remove the protected install and nothing else. Handled before argument
        // parsing because it takes no arguments at all — there is nothing a caller could point it at.
        if (args.Contains("--uninstall"))
            return RemoveElevatedComponents();

        var (resumeTarget, resultPath) = ParseArgs(args);
        if (resumeTarget is null || resultPath is null)
        {
            Console.Error.WriteLine("usage: Mainguard.Installer.Elevated --resume-target <oobe.exe> --result <result.json>");
            Console.Error.WriteLine("   or: Mainguard.Installer.Elevated --uninstall");
            return (int)ElevatedHelperExitCode.BadArguments;
        }

        var protection = ProtectedLocationPolicy.ForHost();

        // The root a legitimate --resume-target must live under. DERIVED, never received: when this
        // helper is the staged copy it sits inside the app's own install; when it is the promoted copy
        // it reads the app root back out of the promote marker, which lives in the administrator-owned
        // directory and therefore carries the same trust as the helper itself. Accepting this root as
        // an argument would let a caller declare C:\Windows\System32 to be "the install" and get
        // cmd.exe registered as an elevated ONLOGON task.
        var appRoot = ResolveAppRoot(protection);

        // MG-9: we are ALREADY ELEVATED at this point, and --resume-target came straight off argv. It
        // used to be handed to schtasks unchecked, which turned "can invoke this helper" into "can
        // register any executable on the machine as an ONLOGON task running as administrator with no
        // UAC prompt" — a permanent, silent privilege escalation from a single argument. Validate
        // before anything privileged happens, and refuse loudly rather than proceeding: an argument we
        // cannot vouch for is the one case where doing nothing is unambiguously correct.
        //
        // The install root is the DERIVED app root (see above), not this helper's own directory — once
        // the helper is promoted into Program Files the exe it resumes is no longer a sibling.
        // (Honest limit, unchanged: an attacker who can write the app directory can replace the target
        // in place and this check still passes — see TrustedExecutablePath. What MG-15 changes is that
        // such a target can no longer be granted a no-prompt elevated run level; see ResumeTaskPolicy.)
        if (!TrustedExecutablePath.TryValidate(
                resumeTarget, appRoot, out var validatedResumeTarget, out var refusal))
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

        // The resume target is about to be handed to a Windows facility that will launch it after the
        // reboot. This is the SECOND enforcement point for the pinned signature, and the one that
        // matters most once the components below are installed: the copy of this code doing the
        // checking then lives in an administrator-owned directory, so its verdict holds even against
        // malware running as the user. On a build with no signing identity this answers NotAvailable
        // and we proceed with the gap named; on a signed build an unsigned resume target is a refusal.
        var resumeSignature = PayloadSignature.VerifyFile(SignedArtifactKind.ResumeTarget, resumeTarget);
        Console.Error.WriteLine($"resume target signature: {resumeSignature.Kind} — {resumeSignature.Reason}");
        if (resumeSignature.MustRefuse)
        {
            WriteResult(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = false,
                RebootRequired = false,
                ResumeTaskRegistered = false,
                Error = $"refusing to register the resume task: {resumeSignature.Reason}",
            });
            return (int)ElevatedHelperExitCode.BadArguments;
        }

        // Action 3 (MG-15), FIRST because everything after it benefits: move the elevated components
        // out of the per-user install directory into an administrator-owned root. This is the one
        // moment in Mainguard's life when it has both the rights to do it and the user's explicit
        // consent to a prompt that says so. Best-effort by design — a machine with no writable Program
        // Files must still be able to finish setup — but the outcome is reported, never assumed.
        var (componentsInstalled, componentsDetail) = InstallElevatedComponents(protection, appRoot);
        Console.Error.WriteLine($"elevated components: {componentsDetail}");

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
                ElevatedComponentsInstalled = componentsInstalled,
                ElevatedComponentsDetail = componentsDetail,
                Error = error,
            });
            return (int)ElevatedHelperExitCode.FeatureEnableFailed;
        }

        // Action 2: register the resume Scheduled Task — but ONLY when a reboot will actually
        // interrupt setup. Its sole purpose is to re-enter the OOBE after the reboot; when no
        // reboot is needed the same OOBE process continues straight to VM import, so registering it would
        // just leave a stale ONLOGON task behind. Its RUN LEVEL is derived from where the target lives
        // (MG-15) — see InstallerCommands.RegisterResumeTask / ResumeTaskPolicy.
        var resumeTaskRegistered = false;
        if (rebootRequired)
        {
            if (!TryRegisterResumeTask(resumeTarget, protection, out error))
            {
                WriteResult(resultPath, new ElevatedHelperResult
                {
                    FeaturesEnabled = true,
                    RebootRequired = true,
                    ResumeTaskRegistered = false,
                    ElevatedComponentsInstalled = componentsInstalled,
                    ElevatedComponentsDetail = componentsDetail,
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
            ElevatedComponentsInstalled = componentsInstalled,
            ElevatedComponentsDetail = componentsDetail,
        });
        await Task.CompletedTask;
        return (int)ElevatedHelperExitCode.Success;
    }

    /// <summary>
    /// MG-15 action 3: promote this helper's own directory into the administrator-owned install root.
    ///
    /// <para><b>Both ends are derived, not received.</b> The SOURCE is
    /// <see cref="AppContext.BaseDirectory"/>'s stage — the directory this very process was launched
    /// from — so there is no argument a caller could aim elsewhere. The DESTINATION comes from
    /// <see cref="ElevatedComponentPlan.TryForHost"/>, which refuses outright when the machine exposes
    /// no protected root rather than falling back to somewhere writable.</para>
    ///
    /// <para>Failure is not fatal: a machine where Program Files cannot be written must still be able
    /// to finish setup, and the launcher's fallback (the co-located helper behind the UAC prompt) is
    /// exactly the pre-MG-15 behaviour. What must never happen is failing SILENTLY — the reason is
    /// carried back to the OOBE in the result file and written to the log.</para>
    /// </summary>
    private static (bool Installed, string Detail) InstallElevatedComponents(
        ProtectedLocationPolicy protection, string appRoot)
    {
        try
        {
            // The stage is the helper's own directory. When the helper is ALREADY running from the
            // protected root there is nothing to promote — it is the promoted copy.
            var stageDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            if (protection.IsProtected(stageDir))
                return (false, $"already running from the protected root ('{stageDir}') — nothing to install");

            if (!ElevatedComponentPlan.TryForHost(protection, appRoot, out var plan, out var refusal))
                return (false, $"not installed: {refusal}");

            // TryForHost derives the stage as <app>\elevated-stage; here the stage IS this directory,
            // because the helper was launched from it. Re-point the plan at the real source.
            plan = plan with { StageDir = stageDir };

            var result = ElevatedComponentInstaller.Install(plan, HelperVersion(), appRoot);
            return (result.Installed, result.Message);
        }
        catch (Exception ex)
        {
            return (false, $"not installed: {ex.Message}");
        }
    }

    /// <summary>
    /// Where a legitimate resume target lives. Three cases, in order:
    /// <list type="number">
    ///   <item>This helper is the PROMOTED copy (its own directory is administrator-owned) — the app
    ///   root is whatever the promote recorded in the marker, a file only an administrator can write.</item>
    ///   <item>This helper is the STAGED copy (<c>&lt;app&gt;\elevated-stage</c>) — the app root is the
    ///   parent directory.</item>
    ///   <item>Neither (a source build, helper beside the app) — the app root is its own directory,
    ///   which is exactly the pre-relocation behaviour.</item>
    /// </list>
    /// </summary>
    private static string ResolveAppRoot(ProtectedLocationPolicy protection)
    {
        var here = AppContext.BaseDirectory.TrimEnd('\\', '/');

        if (protection.IsProtected(here) && protection.PreferredInstallRoot is { } installBase)
        {
            var recorded = ElevatedComponentIdentity
                .TryParse(ElevatedComponentInstaller.TryReadMarker(
                    ElevatedComponentLayout.MarkerPath(installBase)))?
                .AppRoot;
            if (!string.IsNullOrWhiteSpace(recorded))
                return recorded!;
        }

        var leaf = here[(here.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return string.Equals(leaf, ElevatedComponentLayout.StageFolderName, StringComparison.OrdinalIgnoreCase)
            ? ParentOf(here)
            : here;
    }

    /// <summary>The inverse of action 3, invoked by the uninstaller (elevated). Removes only the
    /// protected root this helper creates; it never touches the per-user install, which the
    /// unelevated uninstaller owns.</summary>
    private static int RemoveElevatedComponents()
    {
        var protection = ProtectedLocationPolicy.ForHost();
        if (!ElevatedComponentPlan.TryForHost(protection, AppContext.BaseDirectory, out var plan, out var refusal))
        {
            Console.Error.WriteLine($"nothing to remove: {refusal}");
            return (int)ElevatedHelperExitCode.Success;
        }

        var result = ElevatedComponentInstaller.Remove(plan);
        Console.Error.WriteLine(result.Message);
        return result.Clean ? (int)ElevatedHelperExitCode.Success : (int)ElevatedHelperExitCode.NotElevated;
    }

    /// <summary>The staged helper's own informational version — the value the monotonic promote guard
    /// orders on. Falls back to the file version, then to "0.0.0" (which orders below everything, so an
    /// unstamped build can never downgrade a stamped one).</summary>
    private static string HelperVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>The directory containing <paramref name="path"/>, in <paramref name="path"/>'s own
    /// style (never <see cref="Path.GetDirectoryName(string)"/>, which is host-relative and answers
    /// <c>""</c> for a Windows path on Linux).</summary>
    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut <= 0 ? trimmed : trimmed[..cut];
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
    private static bool TryRegisterResumeTask(
        string resumeTarget, ProtectedLocationPolicy protection, out string? error)
    {
        try
        {
            var args = InstallerCommands.RegisterResumeTask(resumeTarget, AppContext.BaseDirectory, protection);
            Console.Error.WriteLine(ResumeTaskPolicy.Explain(
                resumeTarget, ResumeTaskPolicy.RunLevelFor(resumeTarget, protection)));
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
