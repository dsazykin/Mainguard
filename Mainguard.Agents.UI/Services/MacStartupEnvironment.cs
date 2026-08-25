using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Daemon;
using Mainguard.Agents.UI.ViewModels;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The macos-host <see cref="IAppStartupEnvironment"/> — the same startup sequence as
/// <see cref="ProductionStartupEnvironment"/> with the WSL legs replaced by their host-local
/// equivalents: there is no VM to wake or hold, so "wake" means "ensure the local mainguardd is
/// running from the app payload", the tier-1 refresh restarts that process
/// (<see cref="MacDaemonUpdater"/>), the tier-2 OS upgrade does not exist (no OS to upgrade —
/// always declined), and the sandbox-image probe/build runs against the host's Docker engine
/// through <see cref="HostCommandRunner"/>. Nothing here throws — every failure is a typed
/// answer or a logged skip, same contract as the WSL implementation.
/// </summary>
internal sealed class MacStartupEnvironment : IAppStartupEnvironment
{
    private static readonly TimeSpan ReachableProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly MacDaemonController _daemon = new();
    private readonly Action<string> _log;

    public MacStartupEnvironment(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Kept for interface parity with the loading window; the tier-2 surface never shows.</summary>
    public StartupWindowViewModel? Host { get; set; }

    public bool VmUpgradeDeclinedThisSession { get; set; }

    public void Log(string message) => _log(message);

    /// <summary>No VM to hold awake on this substrate — the engine manages its own lifetime.</summary>
    public void StartKeepAlive()
    {
    }

    public async Task WakeVmAsync(CancellationToken ct)
    {
        try
        {
            var payload = MacDaemonController.DefaultPayloadDirectory();
            var started = await _daemon.EnsureStartedAsync(payload, ct).ConfigureAwait(false);
            if (!started)
            {
                _log($"startup: no daemon payload at '{payload}' — reachability will diagnose.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"startup: local daemon start failed (non-fatal here): {ex.Message}");
        }
    }

    public async Task<bool> IsDaemonReachableAsync(CancellationToken ct)
    {
        try
        {
            using var daemon = DaemonClient.ForLoopback();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReachableProbeTimeout);
            await daemon.GetDaemonInfoAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return true; // a pre-GetDaemonInfo daemon still ANSWERED — it's reachable
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The macos-host legs, mirroring the WSL diagnosis discipline: each arm reports only
    /// what its own check established. The distro leg does not exist here; the process leg asks
    /// whether the payload daemon is running at all.</summary>
    public async Task<DaemonConnectDiagnosis> DiagnoseDaemonConnectAsync(CancellationToken ct)
    {
        try
        {
            var payload = MacDaemonController.DefaultPayloadDirectory();

            // Leg 1 — is the daemon process up on this host?
            if (!await _daemon.IsRunningAsync(payload, ct).ConfigureAwait(false))
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.DaemonProcessNotRunning,
                    File.Exists(Path.Combine(payload, "Mainguard.Server.dll"))
                        ? "no mainguardd process is running on this Mac."
                        : $"no mainguardd process, and this build ships no daemon payload at '{payload}'.");
            }

            // Leg 2 — has it published a session this app can read?
            var sessionDirectory = DaemonTokenLocator.TryResolveSessionDirectory();
            if (sessionDirectory is null)
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.NoSessionToken,
                    $"Paths probed: {string.Join(", ", DaemonTokenLocator.CandidatePaths())}.");
            }

            // Leg 3 — is that session one this build can authenticate against?
            var serverCertificate = DaemonTransportFiles.ServerCertificatePath(sessionDirectory);
            var clientCertificate = DaemonTransportFiles.ClientCertificatePath(sessionDirectory);
            if (!File.Exists(serverCertificate) || !File.Exists(clientCertificate))
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.TransportCredentialsMissing,
                    $"Its session directory '{sessionDirectory}' holds a token but not "
                    + $"{DaemonTransportFiles.ServerCertificateFileName} and "
                    + $"{DaemonTransportFiles.ClientCertificateFileName}.");
            }

            // Leg 4 — everything is in place, so let the call's own status code speak.
            try
            {
                using var daemon = DaemonClient.ForLoopback();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReachableProbeTimeout);
                await daemon.GetDaemonInfoAsync(timeout.Token).ConfigureAwait(false);
                return DaemonConnectDiagnosis.Reachable;
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
            {
                return DaemonConnectDiagnosis.Reachable;
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.TokenRejected, $"The call returned {ex.StatusCode}.");
            }
            catch (Grpc.Core.RpcException ex)
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.NotListening, $"The call returned {ex.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DaemonConnectDiagnosis(DaemonConnectStage.Undiagnosed, ex.Message);
        }
    }

    public async Task<DaemonRepairOutcome> RepairDaemonAsync(CancellationToken ct)
    {
        var payload = MacDaemonController.DefaultPayloadDirectory();
        if (!Directory.Exists(payload))
        {
            return new DaemonRepairOutcome(false, $"this build ships no daemon payload at '{payload}'.");
        }

        try
        {
            var result = await new MacDaemonUpdater().RefreshAsync(payload, ct).ConfigureAwait(false);
            return new DaemonRepairOutcome(result.Succeeded, result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DaemonRepairOutcome(false, ex.Message);
        }
    }

    public async Task<DaemonRefreshOutcome> RefreshDaemonAsync(CancellationToken ct)
    {
        var appVersion = (Assembly.GetEntryAssembly() ?? typeof(MacStartupEnvironment).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(appVersion))
        {
            return new DaemonRefreshOutcome(
                DaemonRefreshOutcomeKind.Faulted, null, null, "app version unknown — tier-1 skipped");
        }

        DaemonRefreshOutcome? captured = null;
        try
        {
            using var daemon = DaemonClient.ForLoopback();
            await DaemonAutoRefresh.RunAsync(
                appVersion,
                queryDaemonInfo: c => QueryDaemonInfoAsync(daemon, c),
                updater: new MacDaemonUpdater(),
                payloadDirectory: MacDaemonController.DefaultPayloadDirectory(),
                log: _log,
                ct,
                onOutcome: outcome =>
                {
                    captured = outcome;
                    DaemonUpdateToastPublisher.Publish(outcome);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"startup: tier-1 daemon refresh faulted (non-fatal): {ex.Message}");
        }

        return captured
            ?? new DaemonRefreshOutcome(DaemonRefreshOutcomeKind.Faulted, null, null, "no tier-1 outcome");
    }

    /// <summary>No MainguardOS on this substrate — there is no VM image to upgrade, ever.</summary>
    public Task<VmUpgradeAvailability> CheckVmUpgradeAsync(CancellationToken ct)
        => Task.FromResult(new VmUpgradeAvailability(OfferUpgrade: false, "", ""));

    public Task<VmUpgradeDecision> OfferVmUpgradeAsync(VmUpgradeAvailability availability, CancellationToken ct)
        => Task.FromResult(VmUpgradeDecision.Declined);

    public async Task<System.Collections.Generic.IReadOnlyList<SandboxImageSpec>> ProbeSandboxImagesAsync(
        CancellationToken ct)
    {
        try
        {
            var needs = await new SandboxImageProvisioner(new HostCommandRunner())
                .ProbeNeedsProvisionAsync(ct).ConfigureAwait(false);
            return needs.Select(n => n.Image).ToArray();
        }
        catch (Exception ex)
        {
            _log($"startup: sandbox image probe failed (non-fatal): {ex.Message}");
            return Array.Empty<SandboxImageSpec>();
        }
    }

    public void KickSandboxImageBuild(System.Collections.Generic.IReadOnlyList<SandboxImageSpec> missing)
    {
        var progress = new Progress<string>(line => _log($"sandbox images: {line}"));

        var joined = false;
        _ = SandboxImageProvisioningTracker.Shared.RunExclusiveAsync(
            () => SandboxImageInstaller.RunAsync(_log, progress),
            onJoinedExisting: () =>
            {
                joined = true;
                _log("startup: a sandbox image build is already running — joined it instead of starting a second");
            });

        if (!joined)
        {
            Dispatcher.UIThread.Post(() => Editions.ProComposition.ShowShellToast(
                "Updating sandbox images — this takes a few minutes. Leave Mainguard running.",
                false));
        }
    }

    private static async Task<DaemonVersionInfo?> QueryDaemonInfoAsync(DaemonClient daemon, CancellationToken ct)
    {
        try
        {
            return await daemon.GetDaemonInfoAsync(ct).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return null; // pre-GetDaemonInfo daemon — the skew signal itself
        }
    }
}
