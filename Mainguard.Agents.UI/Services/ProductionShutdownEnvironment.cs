using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Editions;
using Mainguard.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IAppShutdownEnvironment"/> — the real exit teardown behind the shutdown
/// window. It exposes ONLY what already ran on <c>desktop.Exit</c> (release the keep-alive; when
/// StopVmOnExit is on, the scoped <c>wsl --terminate MainguardEnv</c>), both routed through the App's
/// idempotent guards so the shutdown window and the framework Exit backstop can never double-run the
/// terminate. No new teardown behavior.
/// </summary>
internal sealed class ProductionShutdownEnvironment : IAppShutdownEnvironment
{
    private readonly Action _releaseKeepAlive;
    private readonly Func<CancellationToken, Task> _stopVm;
    private readonly Action<string> _log;

    public ProductionShutdownEnvironment(
        Action releaseKeepAlive, Func<CancellationToken, Task> stopVm, Action<string> log)
    {
        _releaseKeepAlive = releaseKeepAlive ?? throw new ArgumentNullException(nameof(releaseKeepAlive));
        _stopVm = stopVm ?? throw new ArgumentNullException(nameof(stopVm));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // Only the Pro edition constructs this shutdown env (its ProDesktopHost), so the agent-platform gate
    // that used to read App.Edition.HasAgentPlatform is implicit here — a client machine never reaches
    // this path (a client must never `wsl --terminate MainguardEnv`). The StopVmOnExit user setting is read
    // through the shell-wired settings seam (step 2f — this assembly must not name the shell's App.Settings).
    public bool StopVmOnExit => ProComposition.Settings?.Current.StopVmOnExit ?? false;

    public void ReleaseKeepAlive() => _releaseKeepAlive();

    public Task StopVmAsync(CancellationToken ct) => _stopVm(ct);

    // The shared tracker both provisioning entry points register with — see AppShutdownSequence for why
    // an in-flight build vetoes the terminate.
    public bool SandboxProvisioningInFlight => SandboxImageProvisioningTracker.Shared.IsProvisioning;

    public void Log(string message) => _log(message);
}
