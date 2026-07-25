using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The exact, enumerated privileged surface of the P2-21 elevated helper. The helper does <b>only</b>
/// these two things — enable the two Windows optional features, and register the elevated resume
/// Scheduled Task — and nothing else ever moves into it (rejection trigger otherwise, plan §7). Every
/// command is a pure argument-list/script builder so the shapes are unit-testable without a process,
/// and the raw PowerShell is surfaced to the user in the OOBE before the single UAC prompt.
/// </summary>
public static class InstallerCommands
{
    /// <summary>The two Windows optional features WSL2 requires.</summary>
    public static readonly IReadOnlyList<string> RequiredFeatures = new[]
    {
        "Microsoft-Windows-Subsystem-Linux",
        "VirtualMachinePlatform",
    };

    /// <summary>The name of the elevated resume Scheduled Task. Scoped, self-deleting after resume.</summary>
    public const string ResumeTaskName = "Mainguard-OOBE-Resume";

    /// <summary>
    /// Marker line the <see cref="EnableFeaturesPowerShell"/> script writes to stdout so the elevated
    /// helper can read back DISM's authoritative reboot decision (<c>True</c>/<c>False</c>).
    /// </summary>
    public const string RestartNeededMarker = "MAINGUARD_RESTART_NEEDED=";

    /// <summary>
    /// The raw <c>Enable-WindowsOptionalFeature</c> PowerShell surfaced to the user (and run by the
    /// elevated helper). <c>-NoRestart</c> so the OOBE — not DISM — owns the reboot decision. This
    /// exact string is what the OOBE displays before the UAC prompt (transparency).
    ///
    /// <para>The script aggregates each call's authoritative <c>RestartNeeded</c> flag and emits it on a
    /// final <see cref="RestartNeededMarker"/> line. When a feature is already enabled the Enable call is
    /// a no-op that reports <c>RestartNeeded=$false</c>, so a machine that already has WSL2 on is never
    /// asked to reboot again (the helper reads this marker back instead of assuming a cold machine).</para>
    /// </summary>
    public static string EnableFeaturesPowerShell()
    {
        // One Enable-WindowsOptionalFeature call per feature, -NoRestart so we control the reboot, each
        // call's RestartNeeded OR'd into $restartNeeded and surfaced on the marker line for the helper.
        var lines = new List<string> { "$restartNeeded = $false" };
        foreach (var feature in RequiredFeatures)
        {
            lines.Add($"$r = Enable-WindowsOptionalFeature -Online -FeatureName {feature} -NoRestart -All");
            lines.Add("if ($r -and $r.RestartNeeded) { $restartNeeded = $true }");
        }
        lines.Add($"Write-Output \"{RestartNeededMarker}$restartNeeded\"");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The <c>schtasks.exe</c> argument list that registers the elevated, ONLOGON, run-as-highest
    /// resume task. <b>Never <c>RunOnce</c></b> (plan §7 rejection trigger) — a Scheduled Task
    /// survives the reboot and elevation cleanly where a <c>RunOnce</c> registry value would run
    /// unelevated. <paramref name="resumeExePath"/> is the OOBE exe relaunched in resume mode;
    /// <paramref name="installRoot"/> is the directory that install was launched from (the helper's
    /// own <c>AppContext.BaseDirectory</c>, where the packaged build co-locates every Mainguard exe).
    ///
    /// <para><b>MG-9: the path is validated here, in the builder, not at the call site.</b> This
    /// registration creates a task that runs <c>/RL HIGHEST</c> at <c>ONLOGON</c> — elevated, at every
    /// logon, <b>with no UAC prompt</b>. It used to interpolate whatever string it was handed, so any
    /// caller that could reach the elevated helper could name any executable on the machine and have
    /// Windows run it as administrator forever. Validating in the builder means no future caller can
    /// forget to; see <see cref="TrustedExecutablePath"/> for exactly how far that protection goes
    /// (it stops an ARBITRARY exe — it does not stop a REPLACED one at the legitimate path).</para>
    /// </summary>
    /// <exception cref="ArgumentException">The resume target is not a canonical, fully-qualified
    /// executable inside <paramref name="installRoot"/>. Callers must surface this, never swallow it.</exception>
    public static IReadOnlyList<string> RegisterResumeTask(string resumeExePath, string installRoot)
    {
        var target = TrustedExecutablePath.Require(resumeExePath, installRoot, "resume target");
        return new[]
        {
            "/Create",
            "/TN", ResumeTaskName,
            // The task runs the OOBE exe in resume mode. `target` is the validated canonical form:
            // it cannot contain a quote, so it cannot break out of this quoted /TR string.
            "/TR", $"\"{target}\" --resume",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",   // elevated — feature-enablement completion + VM import need it
            "/F",               // overwrite any stale registration (idempotent re-run)
        };
    }

    /// <summary>The <c>schtasks.exe</c> argument list that deletes the resume task — the helper/OOBE
    /// runs this once the resume completes, so the task is self-deleting (never lingers).</summary>
    public static IReadOnlyList<string> UnregisterResumeTask() => new[]
    {
        "/Delete",
        "/TN", ResumeTaskName,
        "/F",
    };

    /// <summary>The <c>schtasks.exe</c> argument list that queries the resume task's definition as XML
    /// (works unelevated). Used by the identity check: a registration left behind by an OLDER install
    /// points at a retired exe and must be removed, never fired.</summary>
    public static IReadOnlyList<string> QueryResumeTaskXml() => new[]
    {
        "/Query",
        "/TN", ResumeTaskName,
        "/XML",
    };

    /// <summary>
    /// Extracts the executable path from a <c>schtasks /Query /XML</c> dump's
    /// <c>&lt;Command&gt;</c> element (surrounding quotes stripped). Null when the XML has no command
    /// (malformed/foreign task). Pure — unit-testable without schtasks.
    /// </summary>
    public static string? ParseResumeTaskCommand(string taskXml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(taskXml);
            var command = doc
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Command")?
                .Value?.Trim();
            if (string.IsNullOrEmpty(command))
                return null;
            return command.Trim('"');
        }
        catch
        {
            return null; // not XML / truncated → treated as "identity unknown"
        }
    }

    /// <summary>The two enumerated privileged actions, so a test can prove the helper's scope never
    /// grows: exactly {enable features, register resume task}. Deleting the resume task is the
    /// INVERSE of the second action — lifecycle of the same registration, not a third capability —
    /// which is why the helper may also unregister it (a stale elevated ONLOGON task re-runs setup
    /// elevated at every logon and cannot reliably be removed unelevated).</summary>
    public static IReadOnlyList<string> PrivilegedActionCatalog() => new[]
    {
        "enable-windows-optional-features",
        "register-resume-scheduled-task",
    };
}

/// <summary>How the elevated helper reports its outcome to the unelevated OOBE: a process exit code
/// PLUS a JSON result file (so structured detail survives the elevation boundary).</summary>
public enum ElevatedHelperExitCode
{
    Success = 0,
    /// <summary>Enabling one or both features failed.</summary>
    FeatureEnableFailed = 10,
    /// <summary>Registering the resume Scheduled Task failed.</summary>
    ResumeTaskRegistrationFailed = 11,
    /// <summary>The helper was not actually running elevated.</summary>
    NotElevated = 12,
    /// <summary>Malformed/absent arguments.</summary>
    BadArguments = 13,
}

/// <summary>The JSON result file the elevated helper writes for the OOBE to read back.</summary>
public sealed record ElevatedHelperResult
{
    public required bool FeaturesEnabled { get; init; }
    public required bool RebootRequired { get; init; }
    public required bool ResumeTaskRegistered { get; init; }
    public string? Error { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ElevatedHelperResult Deserialize(string json) =>
        JsonSerializer.Deserialize<ElevatedHelperResult>(json, Options)
        ?? throw new System.IO.InvalidDataException("elevated helper result deserialized to null.");
}
