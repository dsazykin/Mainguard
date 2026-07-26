using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>Outcome of one <see cref="ResumeTaskGuard"/> sweep, for logging/diagnostics.</summary>
public enum ResumeTaskSweepResult
{
    /// <summary>No resume task was registered — nothing to do.</summary>
    NotRegistered,
    /// <summary>The task existed and was deleted.</summary>
    Deleted,
    /// <summary>The task exists and is legitimately awaiting the reboot — left in place.</summary>
    KeptAwaitingReboot,
    /// <summary>The task exists, deletion was attempted and DENIED (typically: an elevated task,
    /// an unelevated caller). The elevated resume launch itself will remove it on its next fire.</summary>
    DeleteDenied,
}

/// <summary>
/// The anti-zombie lifecycle guard for the <c>Mainguard-OOBE-Resume</c> Scheduled Task (P2-21/P2-48).
///
/// <para><b>Why this exists:</b> the resume task is an ELEVATED ONLOGON task. One left behind by an
/// abandoned or failed setup silently re-runs the whole OOBE — elevated, with no UAC prompt — at every
/// logon, racing the interactive wizard over the shared <c>oobe-state.json</c>/<c>elevated-result.json</c>
/// files. Deleting it only on reaching Done (the old behaviour) made it immortal on any path that never
/// reached Done. This guard makes it structurally short-lived:</para>
///
/// <list type="number">
///   <item><b>Self-delete on fire:</b> a <c>--resume</c> launch (the task just fired us; we are
///   elevated) deletes the task as its FIRST action, before any other work — an abandoned resumed
///   setup leaves nothing behind.</item>
///   <item><b>Delete on every non-reboot state:</b> at wizard startup and after every terminal pass
///   (done, blocked, error, cancel, start-over) the task is deleted unless the persisted stage is
///   <see cref="OobeStage.RebootPending"/> — the only state that legitimately needs it.</item>
///   <item><b>Identity check:</b> even while awaiting the reboot, a registration whose
///   <c>&lt;Command&gt;</c> does not point at the CURRENT install's resume target belongs to an older
///   build (e.g. the retired console driver) and is deleted, never trusted to fire.</item>
/// </list>
///
/// <para>All schtasks execution goes through an injected runner so the decision logic is unit-testable;
/// the default runner shells <c>schtasks.exe</c> windowless (Windows-only; a no-op elsewhere).</para>
/// </summary>
public sealed class ResumeTaskGuard
{
    /// <summary>Runs a schtasks invocation: returns exit code and stdout. Injected for tests.</summary>
    public delegate (int ExitCode, string StdOut) SchtasksRunner(IReadOnlyList<string> args);

    private readonly SchtasksRunner _run;
    private readonly Action<string>? _log;

    public ResumeTaskGuard(SchtasksRunner? runner = null, Action<string>? log = null)
    {
        _run = runner ?? RunSchtasks;
        _log = log;
    }

    /// <summary>
    /// The startup/terminal-path sweep. <paramref name="persistedStage"/> is the machine's current
    /// persisted stage (null = no state file); <paramref name="launchedByResumeTask"/> is whether this
    /// process was started with <c>--resume</c> (the task itself fired us — always delete, we are the
    /// elevated instance and the task's purpose is served); <paramref name="currentResumeTarget"/> is
    /// the exe a LEGITIMATE registration must point at (the running app).
    /// </summary>
    public ResumeTaskSweepResult Sweep(OobeStage? persistedStage, bool launchedByResumeTask, string? currentResumeTarget)
    {
        var (queryExit, queryXml) = TryRun(InstallerCommands.QueryResumeTaskXml());
        if (queryExit != 0)
        {
            _log?.Invoke("resume-task sweep: no task registered");
            return ResumeTaskSweepResult.NotRegistered;
        }

        if (!launchedByResumeTask && persistedStage == OobeStage.RebootPending)
        {
            // The one legitimate window for the task to exist — but only if it points where THIS
            // install points. A stale registration from an older install (retired exe) must not
            // survive to fire blind.
            //
            // MG-9: this was a raw case-insensitive string compare, which got even the path question
            // wrong — `C:\App\.\x.exe`, `C:\App\sub\..\x.exe` and `C:\App\x.exe` are one file and three
            // strings, so a registration could be spelled past the check and kept. Comparison now runs
            // on canonical forms, and BOTH sides must additionally validate as executables inside the
            // running install's directory, so a registration pointing anywhere else is deleted rather
            // than trusted.
            //
            // Be clear about what this is: a PATH check, not an identity check. It cannot tell a
            // replaced binary at the legitimate path from the real one. MG-15 makes that survivable
            // rather than detectable here: a registration whose target is user-writable is registered
            // /RL LIMITED (ResumeTaskPolicy), so a replaced binary at that path runs as the user who
            // replaced it and gains nothing. A signed build additionally detects it
            // (IPayloadSignatureVerifier, checked by the elevated helper before it registers).
            var command = InstallerCommands.ParseResumeTaskCommand(queryXml);
            var installRoot = TrustedExecutablePath.DirectoryOf(currentResumeTarget);
            if (TrustedExecutablePath.IsSameExecutable(command, currentResumeTarget, installRoot))
            {
                _log?.Invoke($"resume-task sweep: kept (awaiting reboot, target '{command}')");
                return ResumeTaskSweepResult.KeptAwaitingReboot;
            }

            _log?.Invoke($"resume-task sweep: registration points at '{command ?? "<unparseable>"}' "
                + $"but this install is '{currentResumeTarget}' — deleting the foreign task");
        }
        else
        {
            _log?.Invoke(launchedByResumeTask
                ? "resume-task sweep: fired by the task itself — self-deleting (first action)"
                : $"resume-task sweep: stage is '{persistedStage?.ToString() ?? "<none>"}' (not RebootPending) — deleting");
        }

        var (deleteExit, _) = TryRun(InstallerCommands.UnregisterResumeTask());
        if (deleteExit == 0)
        {
            _log?.Invoke("resume-task sweep: deleted");
            return ResumeTaskSweepResult.Deleted;
        }

        // Typically ERROR_ACCESS_DENIED: an elevated task, an unelevated caller. Not fatal — when the
        // task next fires it launches the wizard elevated with --resume, and THAT instance deletes it.
        _log?.Invoke($"resume-task sweep: delete DENIED (schtasks exit {deleteExit}) — "
            + "the elevated resume launch will self-delete on its next fire");
        return ResumeTaskSweepResult.DeleteDenied;
    }

    private (int, string) TryRun(IReadOnlyList<string> args)
    {
        try
        {
            return _run(args);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"resume-task sweep: schtasks could not run ({ex.Message})");
            return (-1, string.Empty);
        }
    }

    /// <summary>The real runner: windowless <c>schtasks.exe</c>. Throws on non-Windows (caught above).</summary>
    private static (int, string) RunSchtasks(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe did not start.");
        // Concurrent drains — sequential ReadToEnd on two redirected pipes is the deadlock pattern
        // the audit flagged (stderr fills while stdout is being read).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.GetAwaiter().GetResult());
    }
}
