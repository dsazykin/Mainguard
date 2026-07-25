using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// One ordered stage of the P2-05 bootstrap state machine. Each step is a check/act pair: the
/// bootstrapper <see cref="IsSatisfiedAsync"/>-checks first and only <see cref="ExecuteAsync"/>-acts
/// when reality is not yet what the step wants — the property that makes the whole run idempotent and
/// resumable after a mid-bootstrap kill.
/// </summary>
public interface IBootstrapStep
{
    /// <summary>The checklist label shown in the progress UI.</summary>
    string Name { get; }

    /// <summary>Check phase: is the world already in this step's desired state? MUST NOT mutate.</summary>
    Task<bool> IsSatisfiedAsync(CancellationToken ct);

    /// <summary>Act phase: bring the world into this step's desired state, streaming log lines.</summary>
    Task ExecuteAsync(IProgress<string> log, CancellationToken ct);
}

/// <summary>
/// Optional companion to <see cref="IBootstrapStep"/>: names the exact unmet condition when the
/// step's satisfied-check is false. The bootstrapper consults it when a step's post-run re-check
/// fails, so the error card can say WHY (e.g. "mainguardd is crash-looping: &lt;journal line&gt;")
/// instead of the dead-end "state check still failed".
/// </summary>
public interface IBootstrapStepDiagnostics
{
    /// <summary>The best-known reason the step's desired state does not hold — or <c>null</c> when it
    /// cannot be determined (the caller falls back to its generic message). MUST NOT throw.</summary>
    Task<string?> DescribeUnsatisfiedAsync(CancellationToken ct);
}

/// <summary>The lifecycle state of a bootstrap stage, mirrored by the progress UI.</summary>
public enum BootstrapStageState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>A progress update the bootstrapper reports for a stage (state transition and/or log tail).</summary>
public sealed record BootstrapProgress(string StepName, BootstrapStageState State, string? Log);

/// <summary>Inputs to the bootstrapper: the distro to provision and where its tarball/install dir live.</summary>
public sealed record BootstrapOptions(string InstallDir, string TarballPath, string DistroName = WslCommands.DistroName);

/// <summary>
/// Thin filesystem seam for the <c>.wslconfig</c> merge/backup path and host RAM detection, so
/// <see cref="WslConfigMergeStep"/> is unit-testable without touching <c>%UserProfile%</c>.
/// </summary>
public interface IBootstrapFileSystem
{
    /// <summary>Absolute path to <c>%UserProfile%\.wslconfig</c>.</summary>
    string WslConfigPath { get; }

    /// <summary>The current <c>.wslconfig</c> content, or <c>null</c> when the file does not exist.</summary>
    string? ReadWslConfig();

    /// <summary>Writes a timestamped <c>.wslconfig.mainguard.bak</c> next to the file, BEFORE any write.
    /// No-op when the file does not exist. Never clobbers an existing backup (timestamped name).</summary>
    void BackupWslConfig();

    /// <summary>Writes the merged <c>.wslconfig</c> content. MUST be atomic (temp file + swap): the file
    /// is the machine's GLOBAL WSL2 config, so an interrupted in-place truncate would leave every distro
    /// — not just Mainguard's — reading a half-written file (audit MG-32).</summary>
    void WriteWslConfig(string content);

    /// <summary>Whether an arbitrary file (e.g. the distro tarball) exists.</summary>
    bool FileExists(string path);

    /// <summary>Total physical RAM in bytes, used to compute the <c>memory=</c> default.</summary>
    long TotalPhysicalMemoryBytes { get; }
}

/// <summary>
/// Health-probe seam for <see cref="HealthCheckStep"/>. The App supplies an implementation backed by
/// <c>DaemonClient</c>'s gRPC connection; tests supply a scripted one. Kept in Core so the
/// bootstrapper carries no UI dependency.
/// </summary>
public interface IDaemonHealthProbe
{
    /// <summary>True once the daemon's gRPC surface answers a health probe.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);
}

/// <summary>
/// Optional richer companion to <see cref="IDaemonHealthProbe"/>: explains WHY the daemon is not
/// healthy (service state + recent journal lines), so a failed health check surfaces the daemon's
/// actual crash reason — e.g. the missing-ICU abort — instead of a bare "did not report healthy".
/// </summary>
public interface IDaemonHealthDiagnostics
{
    /// <summary>A one-paragraph human-readable description of the daemon's current (unhealthy) state,
    /// or <c>null</c> when nothing could be gathered. MUST NOT throw.</summary>
    Task<string?> DescribeUnhealthyAsync(CancellationToken ct);
}

/// <summary>
/// Optional companion to <see cref="IDaemonHealthProbe"/>: performs the whole
/// "healthy-for-N-consecutive-seconds within M attempts" wait in ONE probe-native operation.
/// <see cref="HealthCheckStep"/> prefers this over its per-attempt polling loop when the probe
/// implements it — a WSL-backed probe polled per attempt spawns a fresh <c>wsl.exe</c> per second
/// (the process-spawn-burst class that drove the WSL service into <c>Wsl/Service/E_UNEXPECTED</c>);
/// the WSL implementation runs the whole loop inside the distro in a single spawn instead.
/// </summary>
public interface IDaemonStableHealthWaiter
{
    /// <summary>Waits until the daemon has answered healthy <paramref name="requiredConsecutive"/>
    /// times in a row (~1s apart), within a budget of <paramref name="attempts"/> checks. Returns
    /// whether stable health was reached. MUST NOT throw on an unhealthy daemon.</summary>
    Task<bool> WaitForStableHealthyAsync(int attempts, int requiredConsecutive, CancellationToken ct);
}
