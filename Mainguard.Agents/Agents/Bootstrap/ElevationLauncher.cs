using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>Relaunches the elevated helper. Behind an interface so the OOBE flow's "exactly one UAC
/// relaunch, at Construct Sandbox only" property is testable and the real <c>runas</c> is Windows-only.</summary>
public interface IElevationLauncher
{
    /// <summary>Launches the elevated helper (UAC prompt), waits for it, and returns its result.</summary>
    Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct);
}

/// <summary>
/// The real Windows elevation launcher: starts <c>Mainguard.Installer.Elevated</c> with the
/// <c>runas</c> verb (the single UAC prompt), passing the resume-target exe path so the helper can
/// register the resume Scheduled Task, and reads back the JSON result file the helper writes. This is
/// the only place the OOBE crosses the elevation boundary.
///
/// <para>Relocated to <c>Mainguard.Agents</c> in P2-48 so the shipped in-app OOBE wizard and the P2-21
/// console driver share ONE launcher.</para>
///
/// <para><b>Threading:</b> <c>ShellExecuteEx</c>-based elevation is executed on a <b>dedicated
/// background STA thread</b>, never on the caller's thread. When the caller is the Avalonia UI thread
/// (which is STA), <see cref="Process.Start(ProcessStartInfo)"/> would otherwise run ShellExecuteEx
/// inline and block the UI thread for the whole UAC interaction — the window keeps painting through
/// the COM modal wait but stops dispatching input, which presents as "the app isn't frozen but the
/// buttons do nothing". The dedicated STA thread reproduces the exact configuration the P2-21 console
/// driver used successfully (its MTA main thread made the runtime spin up a private STA thread) while
/// leaving the UI thread free to pump.</para>
///
/// <para><b>Diagnosis:</b> every step of the launch is appended to <c>elevation.log</c> next to the
/// result file (best-effort), so a machine where the UAC prompt misbehaves leaves a precise trace —
/// timings, Win32 error codes, helper exit code, result-file state — instead of a guess.</para>
/// </summary>
public sealed class RunAsElevationLauncher : IElevationLauncher
{
    /// <summary>ERROR_CANCELLED — ShellExecuteEx's code for "the UAC prompt was declined, timed out,
    /// or could not be shown". Surfaced as a specific, actionable message, never a raw Win32 string.</summary>
    internal const int ErrorCancelled = 1223;

    private readonly string _helperExePath;
    private readonly string _resumeTargetExePath;
    private readonly string _resultPath;
    private readonly string _logPath;

    public RunAsElevationLauncher(string helperExePath, string resumeTargetExePath, string resultPath)
    {
        _helperExePath = helperExePath;
        _resumeTargetExePath = resumeTargetExePath;
        _resultPath = resultPath;
        _logPath = Path.Combine(Path.GetDirectoryName(resultPath) ?? ".", "elevation.log");
    }

    public async Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct)
    {
        // A result file left behind by a PREVIOUS helper run must never be read back as THIS run's
        // outcome — a launch that fails before the helper ever starts would otherwise "succeed" on
        // stale data (stale FeaturesEnabled/RebootRequired silently corrupting the state machine).
        // Delete it up front; if it cannot be deleted, fail honestly instead of risking a false pass.
        try
        {
            if (File.Exists(_resultPath))
            {
                File.Delete(_resultPath);
                Log($"deleted stale result file '{_resultPath}'");
            }
        }
        catch (Exception ex)
        {
            Log($"FAILED to delete stale result file '{_resultPath}': {ex}");
            throw new BootstrapException("EnableFeatures",
                $"A result file from a previous setup attempt ('{_resultPath}') could not be cleared: "
                + $"{ex.Message} Close any other Mainguard setup that may be running and try again.", ex);
        }

        // Resolve the helper from the running app's own directory. Fall back to that directory if the
        // caller handed us a bare/relative name, so a stray working directory can't hide the co-located
        // helper. Fail with a clear message (not the opaque "cannot find the file specified"
        // Win32Exception) if it is genuinely missing — this is the exact P2-21 hand-off failure point.
        var helperExe = _helperExePath;
        if (!File.Exists(helperExe))
        {
            var colocated = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(helperExe));
            if (File.Exists(colocated))
                helperExe = colocated;
        }

        if (!File.Exists(helperExe))
        {
            Log($"helper NOT FOUND at '{_helperExePath}' (also checked '{AppContext.BaseDirectory}')");
            throw new FileNotFoundException(
                $"The elevated helper 'Mainguard.Installer.Elevated.exe' was not found next to the app " +
                $"(looked at '{_helperExePath}'). The packaged build must co-locate it with the Mainguard " +
                $"executable; reinstall or rebuild Mainguard.",
                helperExe);
        }

        // MG-9: File.Exists was the ONLY check standing between a path and `runas`. "It exists" says
        // nothing about what it is — a helper path resolved from anywhere but our own install directory
        // would be launched as administrator on the user's consent to a prompt that names Mainguard.
        // Both the helper and the resume target must therefore be canonical, fully-qualified paths
        // inside AppContext.BaseDirectory (where the packaged build co-locates every Mainguard exe).
        //
        // Honest limit: this bounds WHERE the elevated binary comes from, not WHAT it is. Same-user
        // malware can overwrite the helper at its legitimate path and this passes — see the signature
        // gate immediately below, which is where that would be caught if Mainguard were signed.
        helperExe = RequireInstallRootPath(helperExe, "elevated helper");
        var resumeTarget = RequireInstallRootPath(_resumeTargetExePath, "resume target");

        // The one place a code-signing check belongs on this path. Today it answers NotAvailable — this
        // build has no signing identity — and we proceed with the gap written to elevation.log rather
        // than implied away. A real IPayloadSignatureVerifier makes this refuse without touching this
        // method again.
        var signature = PayloadSignature.VerifyFile(SignedArtifactKind.ElevatedHelper, helperExe);
        Log($"signature check: {signature.Kind} — {signature.Reason}");
        if (signature.MustRefuse)
        {
            throw new BootstrapException("EnableFeatures",
                $"Mainguard's elevated setup helper failed its signature check and was not run: "
                + $"{signature.Reason} Reinstall Mainguard from a trusted source.");
        }

        // UseShellExecute + Verb=runas is what raises the single UAC prompt. Arguments can't be an
        // ArgumentList with ShellExecute, so they are a carefully quoted string — and the two paths
        // interpolated below are the VALIDATED canonical forms, which by construction contain no quote
        // that could terminate an argument early and append attacker-chosen trailing arguments.
        // Note: CreateNoWindow is documented as ignored under ShellExecute, and a WindowStyle of Hidden
        // only sets the launched app's show-command — the helper is a windowless WinExe, so neither is
        // needed; both were removed to keep the elevation call free of contradictory flags. The only UI
        // the user sees is the Windows UAC consent dialog itself.
        var psi = new ProcessStartInfo
        {
            FileName = helperExe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--resume-target \"{resumeTarget}\" --result \"{_resultPath}\"",
        };

        Log($"launching elevated helper '{helperExe}' (caller thread apartment: "
            + $"{Thread.CurrentThread.GetApartmentState()}, resume target '{_resumeTargetExePath}')");
        var stopwatch = Stopwatch.StartNew();

        Process process;
        try
        {
            process = await StartOnDedicatedStaThreadAsync(psi).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log($"UAC denied/cancelled after {stopwatch.ElapsedMilliseconds} ms "
                + $"(Win32 {ex.NativeErrorCode}: {ex.Message})");
            throw new BootstrapException("EnableFeatures",
                "Windows did not grant administrator permission — the permission prompt was declined, "
                + "dismissed, or timed out before it was answered. Nothing on your machine was changed. "
                + "Try again and approve the prompt; if no prompt appears, look for a flashing shield "
                + "icon on the Windows taskbar and click it.", ex);
        }
        catch (Win32Exception ex)
        {
            Log($"Process.Start FAILED after {stopwatch.ElapsedMilliseconds} ms "
                + $"(Win32 {ex.NativeErrorCode}: {ex.Message})");
            throw new BootstrapException("EnableFeatures",
                $"Windows could not launch Mainguard's elevated setup helper (Win32 error "
                + $"{ex.NativeErrorCode}: {ex.Message}). Nothing on your machine was changed. "
                + $"Details were written to '{_logPath}'.", ex);
        }

        Log($"helper started (pid {SafePid(process)}) after {stopwatch.ElapsedMilliseconds} ms; waiting for exit");

        int exitCode;
        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        Log($"helper exited {exitCode} ({(ElevatedHelperExitCode)exitCode}) after {stopwatch.ElapsedMilliseconds} ms; "
            + $"result file exists: {File.Exists(_resultPath)}");

        if (!File.Exists(_resultPath))
        {
            throw new BootstrapException("EnableFeatures",
                $"The elevated setup helper exited with code {exitCode} without reporting a result. "
                + $"Try again; details were written to '{_logPath}'.");
        }

        var result = ElevatedHelperResult.Deserialize(File.ReadAllText(_resultPath));
        Log($"result: featuresEnabled={result.FeaturesEnabled} rebootRequired={result.RebootRequired} "
            + $"resumeTaskRegistered={result.ResumeTaskRegistered} error={result.Error ?? "<none>"}");
        return result;
    }

    /// <summary>
    /// Validates a path that is about to cross the elevation boundary against the running install's own
    /// directory, turning a refusal into the same actionable <see cref="BootstrapException"/> the rest
    /// of this flow raises (never a raw <see cref="ArgumentException"/> surfacing as "unexpected error").
    /// </summary>
    private string RequireInstallRootPath(string candidate, string what)
    {
        if (TrustedExecutablePath.TryValidate(
                candidate, AppContext.BaseDirectory, out var canonical, out var refusal))
        {
            return canonical;
        }

        Log($"REFUSED {what} '{candidate}': {refusal}");
        throw new BootstrapException("EnableFeatures",
            $"Mainguard refused to run setup with administrator rights because the {what} "
            + $"('{candidate}') is not part of this installation: {refusal}. Nothing on your machine "
            + $"was changed. Reinstall Mainguard and run setup again.");
    }

    /// <summary>Runs <see cref="Process.Start(ProcessStartInfo)"/> on a dedicated background STA
    /// thread. ShellExecuteEx blocks until the UAC prompt is answered; doing that on a private thread
    /// keeps the caller (typically the Avalonia UI thread) pumping. STA is what ShellExecuteEx wants;
    /// a raw <c>Task.Run</c> would land on an MTA pool thread instead.</summary>
    private static Task<Process> StartOnDedicatedStaThreadAsync(ProcessStartInfo psi)
    {
        var tcs = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var p = Process.Start(psi);
                if (p is null)
                    tcs.TrySetException(new BootstrapException("EnableFeatures",
                        "Windows reported no process handle for the elevated setup helper. Try again."));
                else
                    tcs.TrySetResult(p);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Mainguard-ElevationLauncher",
        };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch { return "?"; }
    }

    /// <summary>Best-effort append to <c>elevation.log</c> (next to the result file). Never throws —
    /// diagnosis must not be able to break the flow it is diagnosing.</summary>
    private void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(_logPath,
                $"{DateTimeOffset.UtcNow:O} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is diagnostic only.
        }
    }
}
