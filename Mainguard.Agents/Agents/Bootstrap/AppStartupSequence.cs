using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>The four ordered stages the startup loading screen renders as a glyph checklist. The
/// enum value doubles as the checklist row index, so the App's <c>StartupWindowViewModel</c> maps a
/// <see cref="StartupProgress"/> straight onto its row.</summary>
public enum StartupStage
{
    /// <summary>Hold the VM awake (keep-alive) and wake MainguardEnv.</summary>
    PrepareEnvironment = 0,

    /// <summary>Reach a live Mainguard OS daemon within the reachability budget.</summary>
    ConnectDaemon = 1,

    /// <summary>Tier-1 daemon fast-path refresh, then the consented tier-2 OS upgrade offer.</summary>
    ApplyUpdates = 2,

    /// <summary>Probe the jail images (a missing-image build is kicked in the background, not awaited).</summary>
    SandboxImages = 3,
}

/// <summary>One progress tick the sequence reports: which stage, its lifecycle state, and the
/// one-line changing status text the loading screen shows underneath the checklist.</summary>
/// <param name="Stage">The checklist stage this tick advances.</param>
/// <param name="State">The stage's new lifecycle state (Running/Done/Failed).</param>
/// <param name="Status">The one-line status text (empty leaves the current line unchanged).</param>
public sealed record StartupProgress(StartupStage Stage, BootstrapStageState State, string Status);

/// <summary>Which tier-2 consent path the user (or the environment) took — the value the Core
/// sequence branches on to decide whether the daemon steps must re-run against a new VM.</summary>
public enum VmUpgradeDecision
{
    /// <summary>The user chose Later (or the offer could not run) — continue on the current VM.</summary>
    Declined,

    /// <summary>The blocking upgrade ran and succeeded — re-run the daemon steps against the new VM.</summary>
    UpgradedOk,

    /// <summary>The upgrade ran and failed; the old VM is intact — continue without re-running.</summary>
    UpgradeFailed,
}

/// <summary>The typed outcome the App carries from the loading screen into
/// <c>MainWindowViewModel</c>: whether the daemon came up, and the persistent degraded-entry banner
/// text (null when everything is ready). The banner is the honest, token-styled status line the
/// shell shows when an essential step exhausted its budget.</summary>
/// <param name="DaemonReachable">True once the daemon answered within the reachability budget.</param>
/// <param name="DegradedBanner">Non-null naming exactly what isn't ready; null when ready.</param>
public sealed record StartupResult(bool DaemonReachable, string? DegradedBanner)
{
    /// <summary>True when the app entered in a degraded state (a banner is showing).</summary>
    public bool IsDegraded => DegradedBanner is not null;

    /// <summary>The everything-ready result (daemon reachable, no banner).</summary>
    public static StartupResult Ready { get; } = new(true, null);
}

/// <summary>The user-facing status strings + the degraded banner text. Kept as constants so the
/// ordering tests assert the exact status-text sequence and the shell binds the exact banner.</summary>
public static class StartupStatus
{
    /// <summary>Stage 1: keep-alive + waking the VM.</summary>
    public const string WakingEnvironment = "Waking the Mainguard OS environment…";

    /// <summary>Stage 2: waiting for the daemon to answer.</summary>
    public const string ConnectingDaemon = "Connecting to the Mainguard OS daemon…";

    /// <summary>Stage 3: the tier-1 daemon fast-path check/refresh.</summary>
    public const string CheckingDaemon = "Checking the Mainguard OS daemon…";

    /// <summary>Stage 3: the tier-2 OS upgrade availability check.</summary>
    public const string CheckingOsUpdate = "Checking for a Mainguard OS update…";

    /// <summary>Stage 3: the consented tier-2 OS upgrade is running (fully blocking).</summary>
    public const string UpgradingOs = "Upgrading Mainguard OS…";

    /// <summary>Stage 3, after a successful upgrade: reconnecting to the new VM's daemon.</summary>
    public const string ReconnectingAfterUpgrade = "Reconnecting to the upgraded Mainguard OS…";

    /// <summary>Stage 4: probing the sandbox jail images.</summary>
    public const string CheckingImages = "Checking sandbox images…";

    /// <summary>Stage 4: a missing image is building in the background (non-blocking).</summary>
    public const string InstallingImages = "Installing sandbox images in the background…";

    /// <summary>Terminal: everything is ready.</summary>
    public const string Ready = "Mainguard is ready.";

    /// <summary>Stage 2, after the budget lapses: naming which leg of the connect path is broken.</summary>
    public const string DiagnosingDaemon = "Checking why the Mainguard OS daemon didn't answer…";

    /// <summary>Stage 2: redeploying the bundled daemon over one too old to speak this build's mTLS.</summary>
    public const string RepairingDaemon = "Installing the matching Mainguard OS daemon…";

    /// <summary>The status line shown on the loading screen the moment the daemon budget is exhausted.</summary>
    public const string DaemonUnreachableStatus =
        "Mainguard OS daemon isn't reachable — continuing with limited agent features.";

    /// <summary>The last-resort degraded banner, used only when the probe could not attribute the
    /// failure to a leg. Every attributable failure carries <see cref="DaemonConnectDiagnosis.Banner"/>
    /// instead — this generic sentence was the whole user-facing error before that existed.</summary>
    public const string DaemonUnreachableBanner =
        "Mainguard OS daemon isn't reachable yet — reconnecting; some agent features are unavailable.";
}

/// <summary>
/// The seam bundle the <see cref="AppStartupSequence"/> drives. Interface-first per the Core
/// convention: the App supplies a production implementation (WSL wake, DaemonClient probe, tier-1
/// <see cref="DaemonAutoRefresh"/>, tier-2 <see cref="VmUpgradeCheck"/> + the consent/upgrade
/// surface, the sandbox-image probe) and the tests supply a fake so the ordering/budget/degraded
/// branching is exercised without a VM. <b>No method throws</b> — each returns a typed answer — so
/// the sequence's control flow is deterministic.
/// </summary>
public interface IAppStartupEnvironment
{
    /// <summary>Starts the VM keep-alive holder (idempotent). Called FIRST so the VM is held from
    /// the sequence's first moment (see <see cref="VmKeepAlive"/>).</summary>
    void StartKeepAlive();

    /// <summary>Best-effort wake of MainguardEnv (starting any command boots the distro); never throws.</summary>
    Task WakeVmAsync(CancellationToken ct);

    /// <summary>One reachability probe: true when the daemon ANSWERED (including an
    /// <c>Unimplemented</c> pre-RPC daemon), false when unreachable. Never throws.</summary>
    Task<bool> IsDaemonReachableAsync(CancellationToken ct);

    /// <summary>
    /// Establishes WHICH leg of the connect path is broken, by checking each leg in turn. Called once,
    /// after the reachability budget lapses — the hot poll above stays a single cheap RPC. Never throws;
    /// an unattributable failure comes back as <see cref="DaemonConnectStage.Undiagnosed"/> carrying the
    /// raw error rather than a guess.
    /// </summary>
    Task<DaemonConnectDiagnosis> DiagnoseDaemonConnectAsync(CancellationToken ct);

    /// <summary>
    /// Redeploys the daemon build this app ships into the VM and restarts the unit — the repair for
    /// <see cref="DaemonConnectStage.TransportCredentialsMissing"/>, which the app cannot recover from by
    /// waiting. Runs over WSL, needing no daemon connection (the tier-1 refresh that would normally do
    /// this is gated behind a reachable daemon, which is precisely what is unavailable here). Never throws.
    /// </summary>
    Task<DaemonRepairOutcome> RepairDaemonAsync(CancellationToken ct);

    /// <summary>Runs the tier-1 daemon fast-path (skew decision + in-place refresh) and returns the
    /// typed outcome. Wraps <see cref="DaemonAutoRefresh"/>; never throws.</summary>
    Task<DaemonRefreshOutcome> RefreshDaemonAsync(CancellationToken ct);

    /// <summary>Runs the tier-2 availability check (<see cref="VmUpgradeCheck"/>); never throws.</summary>
    Task<VmUpgradeAvailability> CheckVmUpgradeAsync(CancellationToken ct);

    /// <summary>Session-scoped "the user picked Later" flag — set by the sequence on a declined
    /// offer so the same run never re-nags (the App also reads/persists it per session).</summary>
    bool VmUpgradeDeclinedThisSession { get; set; }

    /// <summary>Presents the consented tier-2 offer INSIDE the loading surface and, on accept, runs
    /// the upgrade fully blocking with its step checklist. Returns the decision; never throws.</summary>
    Task<VmUpgradeDecision> OfferVmUpgradeAsync(VmUpgradeAvailability availability, CancellationToken ct);

    /// <summary>Probes which jail images the VM's docker store is missing (fast, awaited so the
    /// state is known); returns an empty list on any failure. Never throws.</summary>
    Task<IReadOnlyList<SandboxImageSpec>> ProbeSandboxImagesAsync(CancellationToken ct);

    /// <summary>Kicks the (minutes-long) sandbox-image build in the BACKGROUND — fire-and-forget,
    /// never awaited by the sequence; the App surfaces its outcome as a toast.</summary>
    void KickSandboxImageBuild(IReadOnlyList<SandboxImageSpec> missing);

    /// <summary>oobe.log breadcrumb sink — the sequence is the first thing support reads.</summary>
    void Log(string message);
}

/// <summary>
/// The Core startup orchestrator (owner design, 2026-07-17): a loading screen holds while the
/// ESSENTIALS come up — VM awake, daemon reachable, the tier-1 daemon update, and (when the user
/// consents) the fully-blocking tier-2 OS upgrade — then hands the shell a typed
/// <see cref="StartupResult"/>. Slow one-offs do not block: the sandbox-image BUILD is kicked in the
/// background (only the fast PROBE is awaited). Every blocking step is budget-bounded; on exhaustion
/// the step is marked failed and the sequence CONTINUES to a degraded entry carrying the honest
/// banner (the existing reconnect machinery heals it). Ordering, budgets, the degraded branch, and
/// the tier-2 re-run are all here and unit-tested over a fake <see cref="IAppStartupEnvironment"/>.
/// </summary>
public sealed class AppStartupSequence
{
    /// <summary>Default total budget for VM-awake + daemon-reachable (owner: ~45 s).</summary>
    public static readonly TimeSpan DefaultReachableBudget = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan DefaultReachablePollDelay = TimeSpan.FromSeconds(2);

    private readonly IAppStartupEnvironment _env;
    private readonly TimeSpan _reachableBudget;
    private readonly TimeSpan _reachablePollDelay;

    /// <summary>Live constructor with the shipped budgets.</summary>
    public AppStartupSequence(IAppStartupEnvironment env)
        : this(env, null, null)
    {
    }

    /// <summary>Test seam: tightened budgets/poll delay keep the budget-exhaustion path fast and
    /// deterministic without a real ~45 s wait.</summary>
    internal AppStartupSequence(
        IAppStartupEnvironment env,
        TimeSpan? reachableBudget,
        TimeSpan? reachablePollDelay)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _reachableBudget = reachableBudget ?? DefaultReachableBudget;
        _reachablePollDelay = reachablePollDelay ?? DefaultReachablePollDelay;
    }

    /// <summary>Runs the whole sequence, reporting the ordered status-text/checklist stream through
    /// <paramref name="progress"/>, and returns the typed entry result. Never throws for an
    /// operational failure (only <see cref="OperationCanceledException"/> on a real cancel).</summary>
    public async Task<StartupResult> RunAsync(IProgress<StartupProgress>? progress, CancellationToken ct)
    {
        // 1) Keep-alive FIRST — the VM must be held from the first moment (both routes).
        _env.StartKeepAlive();
        _env.Log("startup: VM keep-alive started");

        // 2) PrepareEnvironment: wake the VM.
        Report(progress, StartupStage.PrepareEnvironment, BootstrapStageState.Running, StartupStatus.WakingEnvironment);
        try
        {
            await _env.WakeVmAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _env.Log($"startup: VM wake failed (non-fatal): {ex.Message}");
        }

        Report(progress, StartupStage.PrepareEnvironment, BootstrapStageState.Done, string.Empty);

        // 3) ConnectDaemon: reachable within the budget — else diagnose, repair what is repairable,
        // and degrade carrying the leg that actually failed.
        var connectFailure = await ConnectWithRepairAsync(progress, ct).ConfigureAwait(false);
        if (connectFailure is not null)
        {
            return connectFailure;
        }

        // 4) ApplyUpdates: tier-1 refresh, then the consented tier-2 offer.
        Report(progress, StartupStage.ApplyUpdates, BootstrapStageState.Running, StartupStatus.CheckingDaemon);
        await RunTier1Async(ct).ConfigureAwait(false);

        var upgradeResult = await RunTier2Async(progress, ct).ConfigureAwait(false);
        if (upgradeResult is StartupResult degraded)
        {
            return degraded;
        }

        Report(progress, StartupStage.ApplyUpdates, BootstrapStageState.Done, string.Empty);

        // 5) SandboxImages: fast probe awaited; the (slow) build is kicked in the background.
        await ProbeSandboxImagesAsync(progress, ct).ConfigureAwait(false);

        Report(progress, StartupStage.SandboxImages, BootstrapStageState.Done, StartupStatus.Ready);
        _env.Log("startup: ready");
        return StartupResult.Ready;
    }

    /// <summary>Polls reachability until the daemon answers or the budget is exhausted.</summary>
    private async Task<bool> ConnectDaemonAsync(
        IProgress<StartupProgress>? progress, TimeSpan budget, CancellationToken ct)
    {
        Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Running, StartupStatus.ConnectingDaemon);

        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await _env.IsDaemonReachableAsync(ct).ConfigureAwait(false))
            {
                Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Done, string.Empty);
                _env.Log("startup: daemon reachable");
                return true;
            }

            if (Stopwatch.GetElapsedTime(startTimestamp) >= budget)
            {
                return false;
            }

            try
            {
                await Task.Delay(_reachablePollDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Connect, and when the budget lapses find out why. A verdict the app can act on
    /// (<see cref="DaemonConnectDiagnosis.IsRepairableByDaemonRefresh"/>) earns exactly one repair
    /// attempt plus one fresh connect budget; everything else degrades immediately. Returns
    /// <c>null</c> when the daemon is up, or the degraded result to return from the sequence.
    /// </summary>
    private async Task<StartupResult?> ConnectWithRepairAsync(
        IProgress<StartupProgress>? progress, CancellationToken ct)
    {
        if (await ConnectDaemonAsync(progress, _reachableBudget, ct).ConfigureAwait(false))
        {
            return null;
        }

        Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Running, StartupStatus.DiagnosingDaemon);
        var diagnosis = await _env.DiagnoseDaemonConnectAsync(ct).ConfigureAwait(false);
        _env.Log($"startup: daemon unreachable within budget — {diagnosis.Stage}: {diagnosis.Detail}");

        if (!diagnosis.IsRepairableByDaemonRefresh)
        {
            return Degrade(progress, diagnosis);
        }

        Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Running, StartupStatus.RepairingDaemon);
        var repair = await _env.RepairDaemonAsync(ct).ConfigureAwait(false);
        _env.Log($"startup: daemon repair {(repair.Repaired ? "succeeded" : "did not complete")} — {repair.Detail}");

        if (!repair.Repaired)
        {
            // Report the repair's own failure; never imply the original leg healed.
            return Degrade(progress, diagnosis with
            {
                Detail = $"{diagnosis.Detail} Installing the matching daemon did not complete: {repair.Detail}",
            });
        }

        Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Running, StartupStatus.ConnectingDaemon);
        if (await ConnectDaemonAsync(progress, _reachableBudget, ct).ConfigureAwait(false))
        {
            return null;
        }

        // Re-diagnose: after a successful redeploy the pre-repair verdict is stale, and a banner must
        // describe what is true NOW, not what was true before the app changed the system.
        var after = await _env.DiagnoseDaemonConnectAsync(ct).ConfigureAwait(false);
        _env.Log($"startup: daemon still unreachable after repair — {after.Stage}: {after.Detail}");
        return Degrade(progress, after);
    }

    /// <summary>Marks the daemon step failed and continues to a degraded entry, carrying the banner
    /// for the leg that was actually found broken.</summary>
    private StartupResult Degrade(IProgress<StartupProgress>? progress, DaemonConnectDiagnosis diagnosis)
    {
        Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Failed, StartupStatus.DaemonUnreachableStatus);
        _env.Log($"startup: entering degraded mode ({diagnosis.Stage})");
        var banner = diagnosis.Banner is { Length: > 0 } text ? text : StartupStatus.DaemonUnreachableBanner;
        return new StartupResult(false, banner);
    }

    /// <summary>Tier-1 daemon fast-path — its own internal budget; failure never degrades entry (the
    /// daemon stays up on the previous build and the shell is still usable).</summary>
    private async Task RunTier1Async(CancellationToken ct)
    {
        var outcome = await _env.RefreshDaemonAsync(ct).ConfigureAwait(false);
        _env.Log($"startup: tier-1 daemon outcome {outcome.Kind}");
    }

    /// <summary>Tier-2: check availability, and when offered (and not declined this session) pause at
    /// consent inside the loading surface. A successful upgrade re-runs the daemon steps against the
    /// new VM; a re-run that can't reach the new daemon returns a degraded result.</summary>
    private async Task<StartupResult?> RunTier2Async(IProgress<StartupProgress>? progress, CancellationToken ct)
    {
        if (_env.VmUpgradeDeclinedThisSession)
        {
            return null;
        }

        Report(progress, StartupStage.ApplyUpdates, BootstrapStageState.Running, StartupStatus.CheckingOsUpdate);
        var availability = await _env.CheckVmUpgradeAsync(ct).ConfigureAwait(false);
        if (!availability.OfferUpgrade)
        {
            return null;
        }

        _env.Log($"startup: tier-2 upgrade offered ({availability.InstalledVersion} → {availability.ExpectedVersion})");
        var decision = await _env.OfferVmUpgradeAsync(availability, ct).ConfigureAwait(false);
        switch (decision)
        {
            case VmUpgradeDecision.Declined:
                _env.VmUpgradeDeclinedThisSession = true;
                _env.Log("startup: tier-2 upgrade declined (Later)");
                return null;

            case VmUpgradeDecision.UpgradeFailed:
                _env.Log("startup: tier-2 upgrade failed — old environment intact, continuing");
                return null;

            case VmUpgradeDecision.UpgradedOk:
                _env.Log("startup: tier-2 upgrade completed — re-running daemon steps against the new VM");
                Report(progress, StartupStage.ConnectDaemon, BootstrapStageState.Running,
                    StartupStatus.ReconnectingAfterUpgrade);
                var connectFailure = await ConnectWithRepairAsync(progress, ct).ConfigureAwait(false);
                if (connectFailure is not null)
                {
                    return connectFailure;
                }

                Report(progress, StartupStage.ApplyUpdates, BootstrapStageState.Running, StartupStatus.CheckingDaemon);
                await RunTier1Async(ct).ConfigureAwait(false);
                return null;

            default:
                return null;
        }
    }

    /// <summary>Fast image probe (awaited) — a missing image kicks a background build (not awaited).</summary>
    private async Task ProbeSandboxImagesAsync(IProgress<StartupProgress>? progress, CancellationToken ct)
    {
        Report(progress, StartupStage.SandboxImages, BootstrapStageState.Running, StartupStatus.CheckingImages);
        var missing = await _env.ProbeSandboxImagesAsync(ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            _env.Log($"startup: {missing.Count} sandbox image(s) missing — building in the background");
            _env.KickSandboxImageBuild(missing);
            Report(progress, StartupStage.SandboxImages, BootstrapStageState.Running, StartupStatus.InstallingImages);
        }
    }

    private static void Report(
        IProgress<StartupProgress>? progress, StartupStage stage, BootstrapStageState state, string status) =>
        progress?.Report(new StartupProgress(stage, state, status));
}
