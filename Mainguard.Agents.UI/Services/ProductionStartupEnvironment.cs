using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mainguard.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Daemon;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git;
using Mainguard.UI.ViewModels;
namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IAppStartupEnvironment"/> — the real WSL/DaemonClient side of the startup
/// sequence. It subsumes the old fire-and-forget block (WakeVmInBackground / RefreshDaemonInBackground
/// and its tier-2 + image calls): each seam method is one of those steps, now awaited in order by
/// <see cref="AppStartupSequence"/>. Nothing here throws — every failure is a typed answer or a
/// logged skip — so the sequence's control flow stays deterministic.
/// </summary>
internal sealed class ProductionStartupEnvironment : IAppStartupEnvironment
{
    private static readonly TimeSpan ReachableProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Action _startKeepAlive;
    private readonly Action<string> _log;

    public ProductionStartupEnvironment(Action startKeepAlive, Action<string> log)
    {
        _startKeepAlive = startKeepAlive ?? throw new ArgumentNullException(nameof(startKeepAlive));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The loading window's VM — the host for the tier-2 consent/upgrade surface. Set by the
    /// App before the sequence runs (the offer is presented inside the loading screen).</summary>
    public StartupWindowViewModel? Host { get; set; }

    public bool VmUpgradeDeclinedThisSession { get; set; }

    public void Log(string message) => _log(message);

    public void StartKeepAlive() => _startKeepAlive();

    public async Task WakeVmAsync(CancellationToken ct)
    {
        await new WslRunner()
            .RunAsync(WslCommands.InDistro("true"), stdin: null, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsDaemonReachableAsync(CancellationToken ct)
    {
        try
        {
            using var daemon = DaemonClient.ForLoopback();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReachableProbeTimeout);
            await daemon.GetDaemonInfoAsync(timeout.Token).ConfigureAwait(false);
            return true; // answered
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return true; // a pre-GetDaemonInfo daemon still ANSWERED — it's reachable
        }
        catch (Exception)
        {
            return false; // unreachable / still booting
        }
    }

    /// <summary>
    /// Walks the connect path leg by leg and reports the FIRST one that is actually broken. Each arm
    /// returns only what its own check established — the distro list, <c>pgrep</c>, the token file, the
    /// credential files beside it, then the RPC's own status code. Nothing here infers a cause from a
    /// neighbouring symptom, because a message that guesses is worse than one that says less.
    /// </summary>
    public async Task<DaemonConnectDiagnosis> DiagnoseDaemonConnectAsync(CancellationToken ct)
    {
        try
        {
            var wsl = new WslRunner();

            // Leg 1 — is the distro even running?
            var running = await wsl.RunAsync(WslCommands.ListRunning(), stdin: null, ct).ConfigureAwait(false);
            var isRunning = WslRunner.ParseDistroList(running.StdOut)
                .Any(d => string.Equals(d, WslCommands.DistroName, StringComparison.OrdinalIgnoreCase));
            if (!isRunning)
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.DistroNotRunning,
                    $"'wsl --list --running' did not list {WslCommands.DistroName}.");
            }

            // Leg 2 — is the daemon process up inside it?
            var probe = new WslDaemonHealthProbe(wsl);
            if (!await probe.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                var detail = await probe.DescribeUnhealthyAsync(ct).ConfigureAwait(false);
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.DaemonProcessNotRunning,
                    detail ?? "'pgrep -x mainguardd' found no process.");
            }

            // Leg 3 — has it published a session this app can read?
            var sessionDirectory = DaemonTokenLocator.TryResolveSessionDirectory();
            if (sessionDirectory is null)
            {
                return new DaemonConnectDiagnosis(
                    DaemonConnectStage.NoSessionToken,
                    $"Paths probed: {string.Join(", ", DaemonTokenLocator.CandidatePaths())}.");
            }

            // Leg 4 — is that session one this build can authenticate against? A daemon predating the
            // pinned-mTLS control plane writes daemon.token and nothing else, so the client refuses it.
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

            // Leg 5 — everything is in place, so let the call's own status code speak.
            try
            {
                using var daemon = DaemonClient.ForLoopback();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReachableProbeTimeout);
                await daemon.GetDaemonInfoAsync(timeout.Token).ConfigureAwait(false);
                return DaemonConnectDiagnosis.Reachable; // it answered on this attempt
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
            // Nothing was established — say exactly that, and carry the raw error.
            return new DaemonConnectDiagnosis(DaemonConnectStage.Undiagnosed, ex.Message);
        }
    }

    /// <summary>
    /// Redeploys the bundled daemon payload into the VM over WSL (stop unit → stage → verify → promote
    /// → start), which is the one repair that clears a pre-mTLS daemon. Reuses the tier-1
    /// <see cref="DaemonUpdater"/> directly rather than <see cref="DaemonAutoRefresh"/>, because that
    /// wrapper decides skew by asking the daemon over gRPC — the very thing that cannot be done here.
    /// </summary>
    public async Task<DaemonRepairOutcome> RepairDaemonAsync(CancellationToken ct)
    {
        var payload = DaemonUpdater.DefaultPayloadDirectory();
        if (!Directory.Exists(payload))
        {
            return new DaemonRepairOutcome(false, $"this build ships no daemon payload at '{payload}'.");
        }

        try
        {
            var result = await new DaemonUpdater(new WslRunner())
                .RefreshAsync(payload, ct).ConfigureAwait(false);
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
        // The head exe (entry assembly) carries the product version; fall back to this assembly if the
        // entry point is absent (e.g. a test host). Step 2f moved this env into the Pro-UI assembly, which
        // cannot name the shell's App — the entry assembly is the head, which is what tier-1 compares.
        var appVersion = (Assembly.GetEntryAssembly() ?? typeof(ProductionStartupEnvironment).Assembly)
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
                updater: new DaemonUpdater(new WslRunner()),
                payloadDirectory: DaemonUpdater.DefaultPayloadDirectory(),
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

    public async Task<VmUpgradeAvailability> CheckVmUpgradeAsync(CancellationToken ct)
    {
        using var daemon = DaemonClient.ForLoopback();
        return await VmUpgradeCheck.RunAsync(
            VmUpgradeCheck.DefaultPayloadStampPath(),
            queryDaemonInfo: c => QueryDaemonInfoAsync(daemon, c),
            wsl: new WslRunner(),
            log: _log,
            ct).ConfigureAwait(false);
    }

    public async Task<VmUpgradeDecision> OfferVmUpgradeAsync(VmUpgradeAvailability availability, CancellationToken ct)
    {
        var host = Host;
        if (host is null)
        {
            return VmUpgradeDecision.Declined;
        }

        var tarballPath = Path.Combine(AppContext.BaseDirectory, "payload", "MainguardOS.tar.gz");
        if (!File.Exists(tarballPath))
        {
            _log($"startup: tier-2 payload {availability.ExpectedVersion} expected but no tarball at "
                + $"'{tarballPath}' — offer skipped");
            return VmUpgradeDecision.Declined;
        }

        var dataRoot = MainguardPaths.DataRoot();
        var options = new VmUpgradeOptions(
            TarballPath: tarballPath,
            StagingInstallDir: Path.Combine(dataRoot, "vm-staging"),
            CanonicalInstallDir: Path.Combine(dataRoot, "vm"));

        var offer = new VmUpgradeOfferViewModel(
            new VmUpgradeOrchestrator(new WslRunner()),
            options,
            availability.InstalledVersion,
            availability.ExpectedVersion)
        {
            LogSink = _log,
        };

        var decision = new TaskCompletionSource<VmUpgradeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Later → session flag + CloseAction (below) resolves Declined; the run resolves via
        // the terminal-state watcher.
        offer.Declined = () => VmUpgradeDeclinedThisSession = true;
        offer.CloseAction = () => decision.TrySetResult(VmUpgradeDecision.Declined);
        offer.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VmUpgradeOfferViewModel.IsComplete) when offer.IsComplete:
                    decision.TrySetResult(VmUpgradeDecision.UpgradedOk);
                    break;
                case nameof(VmUpgradeOfferViewModel.IsRunning) when !offer.IsRunning && !offer.IsOffering && offer.HasError:
                    decision.TrySetResult(VmUpgradeDecision.UpgradeFailed);
                    break;
            }
        };

        await Dispatcher.UIThread.InvokeAsync(() => host.BeginVmUpgrade(offer)).GetTask().ConfigureAwait(false);
        var result = await decision.Task.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(host.EndVmUpgrade).GetTask().ConfigureAwait(false);
        return result;
    }

    public async Task<System.Collections.Generic.IReadOnlyList<SandboxImageSpec>> ProbeSandboxImagesAsync(
        CancellationToken ct)
    {
        try
        {
            var needs = await new SandboxImageProvisioner(new WslRunner())
                .ProbeNeedsProvisionAsync(ct).ConfigureAwait(false);
            // Missing OR stale — either kicks the background (re)build; the shell surfaces the outcome.
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
        // Fire-and-forget: the (minutes-long) build must never hold the loading screen. Reuses the
        // existing installer so the Installed/Updated/InstallFailed shell toast still fires. It
        // re-probes cheaply; that keeps the toast path single-sourced. The progress sink (previously
        // discarded) now leaves per-step build/load breadcrumbs in oobe.log while it runs.
        var progress = new Progress<string>(line => _log($"sandbox images: {line}"));
        _ = Task.Run(() => SandboxImageInstaller.RunAsync(_log, progress));
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
