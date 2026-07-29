using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The Phase-2 Control Center prototype shell (Lane E Part 3; docs/design/ControlCenterDesign.md).
/// Runs entirely on <see cref="MockOrchestrator"/> — the ViewModels consume only the service
/// interfaces, so the mock can later be swapped for a DaemonClient with zero View changes.
/// Refresh model: OPS §3.4 — events refresh the projection; every gate re-reads state.
/// </summary>
public partial class ControlCenterViewModel : ViewModelBase, IDisposable, Mainguard.UI.Editions.IAgentPlatformSurface
{
    private readonly IAgentService _agents;
    private readonly IMergeQueueService _queue;
    private readonly ICoordinatorService _coordinator;
    private readonly IKillSwitchService _kill;
    private readonly ITelemetryService _telemetry;
    private readonly IDisposable? _owner;
    private readonly Dictionary<string, AgentDocumentViewModel> _documents = new();

    // The agent rail (worker list + kill switch) as its own surface (2d): the shell reaches it only as
    // opaque object through AgentRailContent → ViewLocator → AgentRailView, never naming AgentRowViewModel
    // or the kill-switch members. A thin view over this VM — the single owner of the agent projection and
    // the kill-switch state (the coordinator surface's freeze banner binds the same IsFrozen).
    private readonly AgentRailViewModel _agentRail;

    // P2-13 #5/#6: the ONE reused per-agent dock workspace host (leak-free content-swap) + the live
    // terminal it hosts. The terminal + its daemon gateway/stream are rebuilt per agent and torn down here.
    private TerminalViewModel? _currentTerminal;
    private Services.ITerminalGateway? _currentTerminalGateway;
    private CancellationTokenSource? _terminalCts;

    // The coordinator's OWN inline interactive terminal (the way you talk to it) — distinct from the
    // per-agent workspace terminal above. Rebuilt when the coordinator's agent id changes, torn down when
    // no coordinator exists. The VM itself is exposed through the CoordinatorTerminal observable property.
    private Services.ITerminalGateway? _coordinatorTerminalGateway;
    private CancellationTokenSource? _coordinatorTerminalCts;
    private string? _coordinatorTerminalAgentId;

    // Cancels the in-flight coordinator spawn (Start/Restart). Stop cancels this so it also aborts a
    // launch still in progress — the daemon tears the partial spawn down on the cancelled RPC.
    private CancellationTokenSource? _startupCts;
    // True when a user Stop cancelled the startup, so the resulting OperationCanceledException stays
    // quiet (the Stop path owns the teardown + messaging) instead of rendering a spurious error.
    private bool _startupStopRequested;
    // The connect watchdog: fires CoordinatorConnectTimedOut when the connecting state overstays.
    private CancellationTokenSource? _connectWatchdogCts;

    /// <summary>How long the coordinator may sit "connecting" (spawning, or live-but-not-yet-drawing)
    /// before the loader admits it's stalled and points at Stop. Deliberately generous — a first-launch
    /// sandbox build is slow — and never auto-kills. Shortened by tests.</summary>
    internal static TimeSpan CoordinatorConnectTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public ObservableCollection<AgentRowViewModel> Agents { get; } = new();

    /// <summary>The agent rail as opaque content (an <see cref="AgentRailViewModel"/>) — the shell drops
    /// this into a <c>ContentControl</c> that resolves <c>AgentRailView</c> via <see cref="ViewLocator"/>,
    /// so it never names the Pro rail types. See
    /// <see cref="Editions.IAgentPlatformSurface.AgentRailContent"/>.</summary>
    public object? AgentRailContent => _agentRail;

    public QueueRailViewModel Queue { get; }
    public CoordinatorPanelViewModel Coordinator { get; }
    public TelemetryPanelViewModel Telemetry { get; }
    public VibeModeViewModel Vibe { get; }

    [ObservableProperty] private AgentDocumentViewModel? _selectedDocument;
    [ObservableProperty] private string? _selectedAgentId;

    /// <summary>P2-47 #7: the review cockpit overlay (non-null → shown), built from the live GetMergeDiff RPC.</summary>
    [ObservableProperty] private ReviewCockpitViewModel? _reviewCockpit;

    /// <summary>Fix 2: the egress block-notification prompt (non-null → shown) — an agent's CLI died on a
    /// host the sandbox proxy refused; Unblock adds it + retries, Keep blocked dismisses.</summary>
    [ObservableProperty] private EgressBlockPromptViewModel? _egressBlockPrompt;

    /// <summary>P2-13 #6: the per-agent dock workspace host (terminal + agent-diff + staging), reused across
    /// agent selections via <see cref="AgentWorkspaceViewModel.ShowAgent"/>. Null until an agent is opened.</summary>
    [ObservableProperty] private AgentWorkspaceViewModel? _workspace;

    // Kill switch (P2-14): quiet at rest, instant, recoverable — see §5.4 for why no confirm.
    [ObservableProperty] private bool _isFrozen;
    [ObservableProperty] private string _freezeBannerText = "";
    [ObservableProperty] private string _killSwitchLabel = "Stop all";

    // Activity bar row 0 (P2-13): resource sparkline + token spend.
    [ObservableProperty] private Points _cpuPoints = new();
    [ObservableProperty] private string _spendText = "$0.00";
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private bool _hasAttention;

    // Workspace layouts (revised 2026-07-11: two presets, The Loom removed; applies to
    // the coordinator surfaces only — the Repo viewer is untouched). Persisted like Theme.
    [ObservableProperty] private bool _isFlightDeck = true;
    [ObservableProperty] private bool _isConversationDeck;

    /// <summary>True: the coordinator conversation is the surface's center content;
    /// false: the selected agent's document is. Driven by the section rail.</summary>
    [ObservableProperty] private bool _isCoordinatorFocus = true;

    // ---- Coordinator-as-CLI (PR3): the "Start coordinator" affordance ----

    /// <summary>The installed agent CLIs the coordinator picker offers (daemon-backed only).</summary>
    public ObservableCollection<Services.InstalledCliOption> InstalledClis { get; } = new();

    /// <summary>Raised (on the caller's thread) the first time the daemon answers the installed-CLI
    /// RPC — the shell's cheapest "daemon reachable" signal, used to clear the degraded startup
    /// banner. May fire more than once (each successful reload); handlers must be idempotent.</summary>
    public event Action? DaemonReachable;

    [ObservableProperty] private Services.InstalledCliOption? _selectedCli;

    /// <summary>A start/restart is in flight (the spawn RPC is running). Also drives the loader and,
    /// crucially, keeps Stop reachable so a launch that never returns can still be cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStopCoordinator))]
    private bool _isStartingCoordinator;

    /// <summary>A stop/teardown is in flight — disables the lifecycle buttons so a full teardown
    /// (which cancels any startup + ends the CLI) isn't double-fired.</summary>
    [ObservableProperty] private bool _isStoppingCoordinator;

    [ObservableProperty] private string _coordinatorStartError = "";

    /// <summary>The confirm-before-stop overlay (non-null → shown). Stop fully terminates the CLI + sandbox
    /// and cancels a launch in progress, so it asks first; Confirm runs the teardown, Cancel keeps it running.</summary>
    [ObservableProperty] private CoordinatorStopPromptViewModel? _stopPrompt;

    /// <summary>A coordinator CLI session is live (its terminal is the way to talk to it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStopCoordinator))]
    private bool _isCoordinatorLive;

    /// <summary>The last coordinator session ended (Dead/torn down) — the card says so honestly
    /// and its terminal stays openable (the replay holds the CLI's final output: the why).</summary>
    [ObservableProperty] private bool _isCoordinatorDead;

    /// <summary>True when the backing services can start CLI agents (a daemon, not the design mock)
    /// and no coordinator is live yet — gates the "Start coordinator" card. A DEAD coordinator
    /// un-gates it: you can always start a new one over a corpse.</summary>
    [ObservableProperty] private bool _canStartCoordinator;

    /// <summary>The coordinator's inline interactive terminal — the built-in terminal is how you talk to
    /// it (and where the CLI logs in when no key is stored). Null on the mock/design harness (no daemon
    /// PTY behind it) and until a coordinator session exists; the View then shows a quiet placeholder.</summary>
    [ObservableProperty] private TerminalViewModel? _coordinatorTerminal;

    /// <summary>A coordinator session exists (live OR dead) — the surface shows its terminal instead of
    /// the "Start a coordinator" card. A coordinator that DIED keeps its terminal open for the
    /// final-output replay; a deliberate Stop blanks it (<see cref="TerminalViewModel.ClearView"/>)
    /// so the stop is visually unmistakable.</summary>
    [ObservableProperty] private bool _showCoordinatorTerminal;

    /// <summary>The coordinator is spawning, or its terminal hasn't drawn its first frame yet — the surface
    /// shows a loading animation over the (still-blank) terminal area until the CLI is up and drawing,
    /// instead of a blank pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStopCoordinator))]
    private bool _isCoordinatorConnecting;

    /// <summary>The connecting state has outlasted <see cref="CoordinatorConnectTimeout"/> with no first
    /// frame and no death — a startup that failed silently (the root of the "loads forever" trap). The
    /// loader stops pretending and says so, pointing at Stop; we never auto-kill, since a real first-launch
    /// sandbox build can legitimately be slow.</summary>
    [ObservableProperty] private bool _coordinatorConnectTimedOut;

    /// <summary>Stop is reachable whenever there is something to stop OR cancel: a live coordinator, a
    /// start still in flight, or a stalled connect. This is the escape hatch that keeps a wedged launch
    /// from trapping the surface on an endless loader.</summary>
    public bool ShowStopCoordinator => IsCoordinatorLive || IsStartingCoordinator || IsCoordinatorConnecting;

    /// <summary>When the coordinator has ended, the why (its last state Detail / exit reason) — shown so a
    /// death that produced no terminal output isn't a silent revert to the start card.</summary>
    [ObservableProperty] private string _coordinatorDeadReason = "";

    /// <summary>Default (design/harness) surface: runs on the scripted <see cref="MockOrchestrator"/>.
    /// The shipped app uses <see cref="ControlCenterViewModel(OrchestratorServices)"/> with a
    /// DaemonClient-backed bundle instead (P2-47).</summary>
    public ControlCenterViewModel() : this((MockOrchestrator?)null) { }

    /// <summary>Test seam: the headless harness injects a slow-tick mock for determinism.</summary>
    public ControlCenterViewModel(MockOrchestrator? mock)
        : this(OrchestratorServices.FromSingle(mock ?? new MockOrchestrator())) { }

    /// <summary>The real integration ctor (P2-47): the VM consumes only the seam interfaces, so the shipped
    /// app passes a DaemonClient-backed bundle and the design harness passes a mock — zero View changes.</summary>
    /// <summary>Cancels the installed-CLI retry loop on Dispose.</summary>
    private readonly System.Threading.CancellationTokenSource _cliLoadCts = new();

    public ControlCenterViewModel(OrchestratorServices services)
    {
        _agents = services.Agents;
        _queue = services.Queue;
        _coordinator = services.Coordinator;
        _kill = services.Kill;
        _telemetry = services.Telemetry;
        _owner = services.Owner;

        Queue = new QueueRailViewModel(_queue, OpenReview);
        Coordinator = new CoordinatorPanelViewModel(_coordinator);
        Telemetry = new TelemetryPanelViewModel(_telemetry);
        // Vibe is headed for its own app (decision 2026-07-11); the VM stays alive here so
        // the render harness and the future shell keep a working surface, but nothing in
        // MainWindow routes to it.
        Vibe = new VibeModeViewModel(services.Vibe, _coordinator, () => { });

        // The rail is a thin view over this VM; the shell hosts it as AgentRailContent (2d).
        _agentRail = new AgentRailViewModel(this);

        _agents.EventReceived += OnAgentEvent;
        // Fix 2: a CLI that dies on a blocked host raises this — show the unblock/keep prompt.
        if (_agents is Services.DaemonBackedOrchestrator dbo) dbo.EgressBlocked += OnEgressBlocked;
        // Changed is raised by both the coordinator and the kill switch (same requery pattern).
        _coordinator.Changed += OnChanged;
        _kill.Changed += OnChanged;
        _telemetry.Sampled += OnSampled;
        ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshAgents();
        RefreshKill();
        RefreshResources();
        RefreshCoordinatorCli();
        if (_agents is Services.ICliAgentHost)
        {
            // Retry until the daemon answers: this ctor runs in the app's first seconds, when the
            // VM is still cold-booting (and the tier-1 daemon auto-update may be bouncing mainguardd)
            // — a single fire-and-forget load lost that race on every cold start and left the
            // picker empty for the whole session (field bug, 2026-07-17).
            _ = LoadInstalledClisUntilAvailableAsync(_cliLoadCts.Token);
        }

        ApplyPreset(PersistedLayout(), persist: false); // restore File → Layout choice
        var first = Agents.FirstOrDefault();
        if (first is not null) SelectAgent(first.AgentId);
    }

    /// <summary>Live agents right now (the exit guard asks this before a VM-stopping full exit).
    /// Counts every non-terminal session INCLUDING a live coordinator; a dead one never counts.</summary>
    public int LiveAgentCount => _agents.ListAgents().Count(a => !IsTerminalState(a.State));

    private static string PersistedLayout()
    {
        try { return Mainguard.Agents.UI.Editions.ProComposition.Settings?.Current?.WorkspaceLayout ?? "FlightDeck"; }
        catch { return "FlightDeck"; }
    }

    // ---- event marshalling (events may arrive on the timer thread) ----

    private void OnAgentEvent(AgentEvent e) => Dispatcher.UIThread.Post(() =>
    {
        RefreshAgents();
        RefreshCoordinatorCli();
        Queue.Refresh();
        SelectedDocument?.Refresh();
        ReviewCockpit?.RefreshFromQueue();
        Vibe.OnOrchestratorEvent(e);
        if (e.Type is "attention_required" or "plan_pending") RefreshAttention();
    });

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        Coordinator.Refresh();
        RefreshAgents();
        RefreshCoordinatorCli();
        Queue.Refresh();
        // The queue stream is re-pushed after an acknowledgment (it moves no queue state, so nothing else
        // would): the overlay reads its items' acknowledged flags and its merge answer from that push.
        ReviewCockpit?.RefreshFromQueue();
        RefreshKill();
        RefreshAttention();
    });

    private void OnSampled() => Dispatcher.UIThread.Post(() =>
    {
        RefreshResources();
        Telemetry.Refresh();
        SelectedDocument?.Refresh();
        Vibe.OnTick();
    });

    // ---- projections ----

    private void RefreshAgents()
    {
        // The coordinator is NOT a row among the workers: it is its own entity, owned by the
        // coordinator surface (the card below). Only worker/manual agents populate the rail.
        var snapshot = _agents.ListAgents()
            .Where(a => a.Role != Mainguard.Agents.Agents.AgentRoles.Coordinator)
            .ToList();
        for (int i = Agents.Count - 1; i >= 0; i--)
            if (snapshot.All(a => a.AgentId != Agents[i].AgentId))
                Agents.RemoveAt(i);
        foreach (var info in snapshot.OrderByDescending(a => a.SpawnedAt)) // LIFO (P2-13)
        {
            var existing = Agents.FirstOrDefault(r => r.AgentId == info.AgentId);
            if (existing is null) Agents.Insert(0, new AgentRowViewModel(info));
            else existing.Update(info);
        }
        RefreshAttention();
    }

    private void RefreshAttention()
    {
        // The attention badge is a static count, never a pulse (§2.4 rationale). Derivation lives
        // in the pure AttentionPolicy so the rail count and the per-row flag agree.
        AttentionCount = _coordinator.GetPendingPlans().Count
                       + _agents.ListAgents().Count(a => AttentionPolicy.IsAttentionRequired(a.State));
        HasAttention = AttentionCount > 0;
    }

    // Live theme switch: the badge converter resolves against the active theme variant, so nudge
    // each row to re-run it. (WeakReferenceMessenger-style discipline: unsubscribed on Dispose.)
    private void OnThemeChanged() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in Agents) row.RefreshBadgeBrush();
    });

    // ---- Fix 2: egress block-notification prompt ----

    private void OnEgressBlocked(Services.EgressBlockInfo info) => Dispatcher.UIThread.Post(() =>
    {
        // One prompt at a time; a fresh block supersedes the old (the newest is what the user acts on).
        EgressBlockPrompt = new EgressBlockPromptViewModel(
            info.Host, info.AgentLabel, UnblockHostAsync, () => EgressBlockPrompt = null);
    });

    /// <summary>Unblock: add the refused host to the daemon allowlist (re-renders the proxy live), dismiss the
    /// prompt, and start a fresh coordinator so the retry uses the widened egress. No daemon (mock/design
    /// harness) → just dismiss.</summary>
    private async Task UnblockHostAsync(string host)
    {
        if (_agents is Services.DaemonBackedOrchestrator daemon)
        {
            await daemon.AddAllowlistHostAsync(
                host, Mainguard.Agents.Agents.Sandbox.EgressEntryKind.AgentService, CancellationToken.None);
        }

        EgressBlockPrompt = null;
        if (CanStartCoordinator)
        {
            await StartCoordinatorCommand.ExecuteAsync(null); // retry with the host now allowed
        }
    }

    /// <summary>Dismiss the egress block prompt (the overlay's backdrop / close).</summary>
    [RelayCommand]
    private void CloseEgressBlockPrompt() => EgressBlockPrompt = null;

    /// <summary>Terminal lifecycle states — the same set <see cref="LiveAgentCount"/> excludes.</summary>
    private static bool IsTerminalState(AgentLifecycleState state) =>
        state is AgentLifecycleState.Merged or AgentLifecycleState.Rejected
            or AgentLifecycleState.Dead or AgentLifecycleState.TornDown;

    /// <summary>
    /// Coordinator-CLI card state, derived from the coordinator-role sessions in the projection:
    /// LIVE when one is in a non-terminal state; DEAD (honestly, with the start card un-gated) when
    /// the newest coordinator record reached a terminal state. A started-but-not-yet-projected
    /// coordinator (<see cref="Services.ICliAgentHost.CoordinatorAgentId"/> set, no record yet)
    /// counts as live so the card never flickers "startable" mid-spawn.
    /// </summary>
    private void RefreshCoordinatorCli()
    {
        var host = _agents as Services.ICliAgentHost;
        var coordinators = _agents.ListAgents()
            .Where(a => a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator)
            .OrderByDescending(a => a.SpawnedAt)
            .ToList();

        var live = coordinators.FirstOrDefault(a => !IsTerminalState(a.State));
        var startedId = host?.CoordinatorAgentId is { Length: > 0 } id ? id : null;
        var startedUnprojected = startedId is not null
            && coordinators.All(a => a.AgentId != startedId)
            && live is null;

        IsCoordinatorLive = live is not null || startedUnprojected;
        IsCoordinatorDead = !IsCoordinatorLive && coordinators.Count > 0;
        CanStartCoordinator = host is not null && !IsCoordinatorLive;
        ShowCoordinatorTerminal = IsCoordinatorLive || IsCoordinatorDead;

        // Bind the coordinator's inline terminal to whichever session represents it: the live one, else the
        // newest record (dead-replay), else the just-started-but-not-yet-projected id. When no coordinator
        // exists at all, tear it down so the surface returns to the "Start a coordinator" card.
        var coordinatorId = live?.AgentId ?? coordinators.FirstOrDefault()?.AgentId ?? startedId;
        if (coordinatorId is { Length: > 0 })
            EnsureCoordinatorTerminal(coordinatorId);
        else
            TearDownCoordinatorTerminal();

        // The exit reason of a dead coordinator (so a death isn't a silent revert), and the loading state.
        CoordinatorDeadReason = IsCoordinatorDead ? (coordinators.FirstOrDefault()?.Detail ?? "") : "";
        UpdateConnecting();
    }

    /// <summary>Loading state: spawning, or live-but-the-terminal-hasn't-drawn-yet (the CLI is starting /
    /// connecting). Cleared once the terminal streams its first frame. No terminal (mock/design harness) →
    /// not "connecting" (the placeholder shows), so the loader never spins forever.</summary>
    private void UpdateConnecting() =>
        IsCoordinatorConnecting = IsStartingCoordinator || (IsCoordinatorLive && CoordinatorTerminal is { HasReceivedOutput: false });

    partial void OnIsStartingCoordinatorChanged(bool value) => UpdateConnecting();

    // The connect watchdog is armed on the false→true edge and disarmed on true→false: while connecting
    // holds, a single timer runs across the whole start→connect window. It never spans a spurious
    // re-arm, so a slow-but-real launch gets the full budget and a healthy one clears it early.
    partial void OnIsCoordinatorConnectingChanged(bool value)
    {
        _connectWatchdogCts?.Cancel();
        _connectWatchdogCts?.Dispose();
        _connectWatchdogCts = null;
        CoordinatorConnectTimedOut = false;

        if (value)
        {
            var cts = new CancellationTokenSource();
            _connectWatchdogCts = cts;
            _ = ConnectWatchdogAsync(cts.Token);
        }
    }

    /// <summary>After <see cref="CoordinatorConnectTimeout"/>, if the surface is STILL connecting (no first
    /// frame, no death), flip <see cref="CoordinatorConnectTimedOut"/> so the loader stops pretending and
    /// points the user at Stop — the honest end of the old "loads forever" trap. Never auto-kills.</summary>
    private async Task ConnectWatchdogAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CoordinatorConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // connecting cleared (drew a frame / died / stopped) — the watchdog is moot
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsCoordinatorConnecting)
            {
                CoordinatorConnectTimedOut = true;
            }
        });
    }

    // Raised on the gRPC stream-read thread (the gateway pumps PTY frames off the UI thread): marshal
    // before touching UI-bound state. Mutating the bound properties inline here throws on Avalonia's
    // thread check, and that exception unwinds INTO the gateway's read loop — killing the terminal
    // stream on its very first frame while the loader stays up.
    private void OnCoordinatorTerminalOutput(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.HasReceivedOutput))
            Dispatcher.UIThread.Post(UpdateConnecting);
    }

    /// <summary>Builds (and attaches) the coordinator's inline interactive terminal for <paramref name="agentId"/>,
    /// reusing the existing one when it is already attached to that id. No-op on the mock/design harness (no PTY
    /// behind it → the surface shows the terminal placeholder); the attach tolerates a daemon that is down.</summary>
    private void EnsureCoordinatorTerminal(string agentId)
    {
        if (_coordinatorTerminalAgentId == agentId && CoordinatorTerminal is not null)
            return; // already attached to this coordinator session

        TearDownCoordinatorTerminal();

        if (_agents is not Services.DaemonBackedOrchestrator daemon)
            return; // mock/design harness — the pane shows its placeholder

        var gateway = daemon.CreateTerminalGateway();
        var terminal = new TerminalViewModel(gateway);
        terminal.PropertyChanged += OnCoordinatorTerminalOutput; // clear the loader on first frame
        var cts = new CancellationTokenSource();
        _ = AttachTerminalAsync(terminal, agentId, cts.Token);

        CoordinatorTerminal = terminal;
        _coordinatorTerminalGateway = gateway;
        _coordinatorTerminalCts = cts;
        _coordinatorTerminalAgentId = agentId;
    }

    private void TearDownCoordinatorTerminal()
    {
        _coordinatorTerminalCts?.Cancel();
        if (CoordinatorTerminal is not null) CoordinatorTerminal.PropertyChanged -= OnCoordinatorTerminalOutput;
        CoordinatorTerminal?.Dispose();
        _coordinatorTerminalGateway?.Dispose();
        _coordinatorTerminalCts?.Dispose();
        CoordinatorTerminal = null;
        _coordinatorTerminalGateway = null;
        _coordinatorTerminalCts = null;
        _coordinatorTerminalAgentId = null;
    }

    /// <summary>Loads the installed-CLI picker once. Returns true when the daemon ANSWERED the list
    /// RPC (a populated or honestly-empty picker — no point retrying); false when it should be
    /// retried (unreachable, or an old daemon mid-tier-1-auto-update whose restart will bring the
    /// RPC). Tolerates every failure with an honest message, never a throw.</summary>
    public async Task<bool> LoadInstalledClisAsync()
    {
        if (_agents is not Services.ICliAgentHost host)
        {
            return true;
        }

        try
        {
            var clis = await host.ListInstalledClisAsync(CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                InstalledClis.Clear();
                foreach (var cli in clis) InstalledClis.Add(cli);
                SelectedCli ??= InstalledClis.FirstOrDefault();
                CoordinatorStartError = InstalledClis.Count == 0
                    ? "No agent CLIs are installed yet — add one in Settings → Agent CLIs."
                    : "";
            });

            // The daemon ANSWERED the installed-CLI RPC — the cheapest correct "daemon reachable"
            // signal off the existing reconnect/retry machinery. The shell clears its degraded
            // startup banner on this (see MainWindowViewModel), no new probing added.
            DaemonReachable?.Invoke();
            return true;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            // The daemon answered — it just predates this RPC (version skew). The tier-1 daemon
            // auto-update is normally refreshing it right now, so keep retrying: the restarted
            // daemon carries the RPC. The message stays honest for the skipped-update case.
            await Dispatcher.UIThread.InvokeAsync(() =>
                CoordinatorStartError = "Mainguard's environment is older than this app and doesn't support "
                    + "starting a coordinator yet — updating automatically; if this persists, see oobe.log.");
            return false;
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                CoordinatorStartError = "Mainguard could not reach its agent daemon — retrying automatically.");
            return false;
        }
    }

    /// <summary>Retries <see cref="LoadInstalledClisAsync"/> every 5 s until the daemon answers or
    /// the VM is disposed — the ctor's load races the VM cold boot (and the tier-1 daemon
    /// auto-update's restart) on every launch, so one attempt is never enough.</summary>
    public async Task LoadInstalledClisUntilAvailableAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await LoadInstalledClisAsync().ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(CliLoadRetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Retry cadence for the installed-CLI load (shortened by tests).</summary>
    internal static TimeSpan CliLoadRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Start the coordinator: spawn the picked CLI (role <c>coordinator</c>) in its own jail. Its fully
    /// interactive terminal then appears inline on this surface (RefreshCoordinatorCli binds it) — that
    /// terminal is how you talk to it, and where CLI login happens when no API key is stored.
    /// </summary>
    [RelayCommand]
    private Task StartCoordinatorAsync() => RunStartupAsync(StartCoordinatorCoreAsync);

    /// <summary>Restart the coordinator: stop the live one, then spawn a fresh one with the picked CLI. The
    /// new terminal replaces the old inline (RefreshCoordinatorCli rebinds on the new agent id). Restart is
    /// an intentional continuation, so — unlike Stop — it does not ask for confirmation.</summary>
    [RelayCommand]
    private Task RestartCoordinatorAsync() => RunStartupAsync(async ct =>
    {
        await StopCoordinatorCoreAsync();
        await StartCoordinatorCoreAsync(ct);
    });

    /// <summary>Runs a start/restart body under a fresh <see cref="_startupCts"/> so Stop can cancel it,
    /// holding <see cref="IsStartingCoordinator"/> across the spawn. Refuses to overlap another start or a
    /// stop-in-progress.</summary>
    private async Task RunStartupAsync(Func<CancellationToken, Task> body)
    {
        if (IsStartingCoordinator || IsStoppingCoordinator)
        {
            return;
        }

        _startupStopRequested = false;
        CoordinatorConnectTimedOut = false;
        IsStartingCoordinator = true;
        var cts = new CancellationTokenSource();
        _startupCts = cts;
        try
        {
            await body(cts.Token);
        }
        finally
        {
            IsStartingCoordinator = false;
            if (ReferenceEquals(_startupCts, cts))
            {
                _startupCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Stop the coordinator: open the confirm-before-stop overlay. Confirm fully terminates the
    /// CLI + sandbox and cancels any launch still in progress; Cancel keeps it running. Reachable even
    /// mid-launch — that is the escape hatch from a startup that never returns.</summary>
    [RelayCommand]
    private void StopCoordinator()
    {
        if (StopPrompt is not null || IsStoppingCoordinator)
        {
            return;
        }

        // A launch that hasn't yet produced a live session is a CANCEL; a running coordinator is a STOP.
        var cancelling = !IsCoordinatorLive;
        StopPrompt = new CoordinatorStopPromptViewModel(
            title: cancelling ? "Cancel startup?" : "Stop the coordinator?",
            message: cancelling
                ? "The coordinator is still starting. Cancelling aborts the launch and tears down anything its sandbox already created."
                : "This terminates the coordinator's CLI and its sandbox. Anything running in its terminal ends; its sub-agents keep running until you stop them.",
            confirmLabel: cancelling ? "Cancel startup" : "Stop coordinator",
            confirm: StopCoordinatorConfirmedAsync,
            cancel: () => StopPrompt = null);
    }

    /// <summary>The confirmed teardown: cancel any in-flight startup (the daemon tears the partial spawn
    /// down on the cancelled RPC), then end the coordinator session. Holds <see cref="IsStoppingCoordinator"/>
    /// so the lifecycle buttons don't double-fire, and clears the overlay when done.</summary>
    private async Task StopCoordinatorConfirmedAsync()
    {
        if (IsStoppingCoordinator)
        {
            return;
        }

        IsStoppingCoordinator = true;
        try
        {
            // Cancel a launch still in flight first, so Stop aborts the startup too, not just a live session.
            _startupStopRequested = true;
            _startupCts?.Cancel();
            CoordinatorConnectTimedOut = false;
            CoordinatorStartError = "";
            await StopCoordinatorCoreAsync();

            // A deliberate stop blanks the terminal so it is OBVIOUS the coordinator ended — the
            // dead-replay stays only for deaths the user didn't ask for. Guarded on the coordinator
            // actually being down: clearing a still-live mirror would desync it from later deltas.
            if (!IsCoordinatorLive)
            {
                CoordinatorTerminal?.ClearView();
            }
        }
        finally
        {
            IsStoppingCoordinator = false;
            StopPrompt = null;
        }
    }

    /// <summary>The spawn leg shared by Start and Restart. Stays on the coordinator surface and lets
    /// RefreshCoordinatorCli attach the inline terminal; renders the daemon's own refusal reason honestly.
    /// A cancel (Stop pressed mid-launch, or the daemon dropped the call) unwinds quietly.</summary>
    private async Task StartCoordinatorCoreAsync(CancellationToken ct)
    {
        if (_agents is not Services.ICliAgentHost host || SelectedCli is null)
        {
            return;
        }

        CoordinatorStartError = "";
        IsCoordinatorFocus = true; // the coordinator's terminal is this surface's center content
        try
        {
            await host.StartCoordinatorAsync(SelectedCli, ct);
            RefreshAgents();
            RefreshCoordinatorCli(); // attaches the coordinator's inline interactive terminal
        }
        catch (Exception ex) when (ex is OperationCanceledException
            || (ex is Grpc.Core.RpcException rpc && rpc.StatusCode == Grpc.Core.StatusCode.Cancelled))
        {
            // Stop cancelled the launch (the stop path owns teardown + messaging) — stay quiet. Over the
            // real channel a cancelled call surfaces as RpcException(Cancelled), not OperationCanceled,
            // so both shapes take this path. Any other cancellation just returns the surface to a
            // startable state without a spurious error.
            if (!_startupStopRequested)
            {
                CoordinatorStartError = "Starting the coordinator was cancelled.";
            }

            RefreshAgents();
            RefreshCoordinatorCli();
        }
        catch (Grpc.Core.RpcException ex)
        {
            // Show the daemon's own reason (Status.Detail), not the RpcException envelope text.
            CoordinatorStartError = ex.Status.Detail is { Length: > 0 } detail
                ? detail
                : $"The daemon refused the start ({ex.StatusCode}).";
        }
        catch (Exception ex)
        {
            CoordinatorStartError = ex.Message;
        }
    }

    /// <summary>The stop leg shared by the confirmed Stop and Restart. Resolves the coordinator (live
    /// session, else the host's last-started id) and ends it; the agent stream then transitions it out of
    /// the live state. When only a not-yet-projected launch existed, cancelling its RPC (above) is the
    /// teardown and there is no id to end.</summary>
    private async Task StopCoordinatorCoreAsync()
    {
        var coordinatorId = _agents.ListAgents()
            .Where(a => a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator)
            .OrderByDescending(a => a.SpawnedAt)
            .FirstOrDefault(a => !IsTerminalState(a.State))?.AgentId
            ?? (_agents as Services.ICliAgentHost)?.CoordinatorAgentId;
        if (coordinatorId is not { Length: > 0 })
        {
            RefreshAgents();
            RefreshCoordinatorCli();
            return;
        }

        try { await _agents.EndAgentAsync(coordinatorId); }
        catch (Exception ex) { CoordinatorStartError = ex.Message; }
        RefreshAgents();
        RefreshCoordinatorCli();
    }

    private void RefreshKill()
    {
        IsFrozen = _kill.IsFrozen;
        FreezeBannerText = _kill.PhaseText;
        KillSwitchLabel = IsFrozen ? "Frozen — resume" : "Stop all";
    }

    private void RefreshResources()
    {
        var history = _telemetry.History;
        var points = new Points();
        int n = Math.Min(60, history.Count);
        for (int i = 0; i < n; i++)
        {
            var s = history[history.Count - n + i];
            points.Add(new Point(i * (60.0 / Math.Max(1, n - 1)), 16 - s.CpuPercent / 100.0 * 16));
        }
        CpuPoints = points;
        SpendText = FormattableString.Invariant($"${_telemetry.Current.SpendTodayUsd:0.00}");
    }

    // ---- selection / navigation ----

    [RelayCommand]
    public void SelectAgent(string agentId)
    {
        SelectedAgentId = agentId;
        if (!_documents.TryGetValue(agentId, out var doc))
        {
            doc = new AgentDocumentViewModel(agentId, _agents, _queue, _telemetry);
            doc.SetDirectPrompting(AllowDirectPrompting);
            _documents[agentId] = doc;
        }
        doc.Refresh();
        SelectedDocument = doc;

        // Mount the agent into the ONE reused dock workspace host (leak-free content-swap): a live terminal
        // as the primary pane, the agent document as the diff pane. Opening another agent costs three
        // content swaps, not a fresh dock graph.
        Workspace ??= new AgentWorkspaceViewModel(agentId, WorkspaceLayoutKind);
        var terminal = CreateTerminalFor(agentId);
        Workspace.ShowAgent(agentId, terminal, doc, null);

        IsCoordinatorFocus = false;
    }

    /// <summary>Builds (and attaches) a fresh live terminal for <paramref name="agentId"/>, tearing down the
    /// previous agent's terminal + its daemon gateway/stream first. Returns null on the mock/design harness
    /// (no PTY behind it → the pane shows its placeholder), and the attach tolerates a daemon that is down.</summary>
    private object? CreateTerminalFor(string agentId)
    {
        _currentTerminal?.Dispose();
        _currentTerminalGateway?.Dispose();
        _terminalCts?.Cancel();
        _terminalCts?.Dispose();
        _currentTerminal = null;
        _currentTerminalGateway = null;
        _terminalCts = null;

        if (_agents is not Services.DaemonBackedOrchestrator daemon)
        {
            return null;
        }

        var gateway = daemon.CreateTerminalGateway();
        var terminal = new TerminalViewModel(gateway);
        var cts = new CancellationTokenSource();
        _ = AttachTerminalAsync(terminal, agentId, cts.Token);

        _currentTerminal = terminal;
        _currentTerminalGateway = gateway;
        _terminalCts = cts;
        return terminal;
    }

    private static async Task AttachTerminalAsync(TerminalViewModel terminal, string agentId, CancellationToken ct)
    {
        try
        {
            await terminal.AttachAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Daemon unreachable / PTY not yet bound / stream dropped — the pane stays empty (honest),
            // surfaced through the DaemonClient's ConnectionState rather than an app crash.
        }
    }

    [RelayCommand]
    public void FocusCoordinator() => IsCoordinatorFocus = true;

    /// <summary>Wires a task-manager resource monitor onto the same backing services; the owner disposes
    /// the returned VM. Kept returning the concrete type for direct (test/harness) callers — the shell
    /// reaches it only as <c>object?</c> through the explicit
    /// <see cref="Editions.IAgentPlatformSurface.CreateResourceMonitor"/> implementation below.</summary>
    public ResourceMonitorViewModel CreateResourceMonitor() => new(_agents, _telemetry);

    /// <summary>Interface surface (2d): the shell holds the resource monitor only as opaque <c>object</c>
    /// and drops it into a <c>ContentControl</c> that resolves <c>ResourceMonitorView</c> via ViewLocator.</summary>
    object? Mainguard.UI.Editions.IAgentPlatformSurface.CreateResourceMonitor() => CreateResourceMonitor();

    /// <summary>Direct-to-agent prompting toggle (File menu → Agent prompting): propagates
    /// to every open agent document; new documents inherit it.</summary>
    public bool AllowDirectPrompting { get; private set; } = true;

    public void SetDirectPrompting(bool allow)
    {
        AllowDirectPrompting = allow;
        foreach (var doc in _documents.Values) doc.SetDirectPrompting(allow);
    }

    // The merge rail's "review" action opens the P2-11 cockpit built from the real branch-vs-main diff.
    private void OpenReview(string agentId) => _ = OpenReviewAsync(agentId);

    /// <summary>P2-47 #7: build the <see cref="ReviewCockpitContext"/> from the live GetMergeDiff RPC and
    /// mount the cockpit. On the mock/design harness — or when no repo/diff is available — it degrades to
    /// opening the agent's document, so nothing is fabricated and the surface never dead-ends.</summary>
    private async Task OpenReviewAsync(string agentId)
    {
        if (_agents is Services.DaemonBackedOrchestrator daemon)
        {
            Services.MergeDiffResult? diff = null;
            try { diff = await daemon.GetMergeDiffAsync(agentId, System.Threading.CancellationToken.None); }
            catch { diff = null; }

            if (diff is not null)
            {
                var name = Agents.FirstOrDefault(a => a.AgentId == agentId)?.Name ?? agentId;
                var ctx = new ReviewCockpitContext(agentId, name, diff.Branch, diff.Files);

                // The overlay is built on the DAEMON's flagged items and the daemon's ack RPC — the same
                // path the agent document uses. It was built with none: `changedGate: null`, `queue: null`
                // and a private in-process acknowledgment store, so it surfaced no daemon-flagged item and
                // any checkmark it did draw would have cleared a store the merge gate never reads.
                ReviewCockpit = new ReviewCockpitViewModel(
                    ctx,
                    onMerge: id => _ = Services.MergeActionRunner.RunAsync(_queue, id),
                    live: new Services.DaemonFlaggedChangeSource(_queue));
                return;
            }
        }

        // No live diff (mock harness, no active repo, or daemon down): fall back to the agent document.
        SelectAgent(agentId);
    }

    /// <summary>Dismiss the review cockpit overlay.</summary>
    [RelayCommand]
    public void CloseReview() => ReviewCockpit = null;

    /// <summary>P2-47 #1: point the live merge-queue projection at the daemon-provisioned repo handle so
    /// the merge rail + review cockpit reflect that repo's queue. No-op on the mock/design harness.</summary>
    public void SetActiveRepo(string repoHandle)
    {
        if (_agents is Services.DaemonBackedOrchestrator daemon)
        {
            // The local checkout + sync-remote name from the SAME ProvisionRepo answer travel with the
            // handle: the human merge lands on the user's own repository, so an adapter that knows only
            // the opaque handle can observe the queue but cannot merge anything (see ConfirmMergeAsync).
            var binding = _lastProvisioned;
            var matches = binding is not null
                && string.Equals(binding.Value.RepoHandle, repoHandle, StringComparison.Ordinal);
            daemon.SetActiveRepo(
                repoHandle,
                matches ? binding!.Value.RepoPath : null,
                matches ? binding!.Value.SyncRemoteName : null);
        }
    }

    /// <summary>The most recent ProvisionRepo answer paired with the Windows path it was provisioned FROM
    /// (the daemon only ever hands back opaque handles — G-14 — so the path has to be remembered here).</summary>
    private (string RepoHandle, string RepoPath, string SyncRemoteName)? _lastProvisioned;

    /// <summary>Provision the just-opened repo into the daemon (P2-06) and return its sync-remote binding
    /// for the shell to register with its own IGitService (step 2f seam). Gated on the real
    /// DaemonBackedOrchestrator like <see cref="SetActiveRepo"/>, so the mock/design harness returns null
    /// (no daemon); any transport failure is swallowed to null — agents are simply unavailable until the
    /// daemon is up, and the Git client is unaffected.</summary>
    public async System.Threading.Tasks.Task<Mainguard.UI.Editions.RepoSyncBinding?> ProvisionRepoAsync(string repoPath)
    {
        if (_agents is not Services.DaemonBackedOrchestrator)
        {
            return null;
        }

        try
        {
            using var daemon = Services.DaemonClient.ForLoopback();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var provisioned = await daemon.ProvisionRepoAsync(repoPath, cts.Token).ConfigureAwait(false);
            _lastProvisioned = (provisioned.RepoHandle, repoPath, provisioned.SyncRemoteName);
            return new Mainguard.UI.Editions.RepoSyncBinding(
                provisioned.RepoHandle, provisioned.SyncRemoteName, provisioned.SyncRemoteUrl);
        }
        catch
        {
            return null;
        }
    }

    // ---- kill switch ----

    [RelayCommand]
    private async Task ToggleKillSwitchAsync()
    {
        if (_kill.IsFrozen) await _kill.ResumeAsync();
        else await _kill.EngageAsync(); // no confirm: instant, recoverable by design (§5.4)
        RefreshKill();
        RefreshAgents();
        Queue.Refresh();
    }

    // ---- presets & mode ----

    [RelayCommand]
    public void SetPreset(string preset) => ApplyPreset(preset, persist: true);

    private void ApplyPreset(string preset, bool persist)
    {
        // Unknown/legacy values (e.g. the retired "Loom") fall back to Flight Deck.
        IsConversationDeck = preset == "ConversationDeck";
        IsFlightDeck = !IsConversationDeck;
        if (persist)
        {
            try { Mainguard.Agents.UI.Editions.ProComposition.Settings?.Update(p => p.WorkspaceLayout = IsConversationDeck ? "ConversationDeck" : "FlightDeck"); }
            catch { /* settings unavailable (headless) — in-memory only */ }
        }
    }

    /// <summary>The persisted layout as the workspace-dock enum, so a dock workspace opens in the
    /// same arrangement the coordinator surface uses.</summary>
    public WorkspaceLayoutKind WorkspaceLayoutKind =>
        IsConversationDeck ? WorkspaceLayoutKind.ConversationDeck : WorkspaceLayoutKind.FlightDeck;

    public void Dispose()
    {
        _cliLoadCts.Cancel();
        _cliLoadCts.Dispose();
        _agents.EventReceived -= OnAgentEvent;
        if (_agents is Services.DaemonBackedOrchestrator dbo) dbo.EgressBlocked -= OnEgressBlocked;
        _coordinator.Changed -= OnChanged;
        _kill.Changed -= OnChanged;
        _telemetry.Sampled -= OnSampled;
        ThemeManager.ThemeChanged -= OnThemeChanged;

        // Abort an in-flight coordinator launch + its connect watchdog so nothing dangles past disposal.
        try { _startupCts?.Cancel(); } catch { /* already disposed */ }
        _startupCts?.Dispose();
        _connectWatchdogCts?.Cancel();
        _connectWatchdogCts?.Dispose();

        // Tear down the live terminal + its gateway/stream, then the dock workspace host (closes any floating
        // dock windows — the documented Dock.Avalonia leak this host owns).
        _terminalCts?.Cancel();
        _currentTerminal?.Dispose();
        _currentTerminalGateway?.Dispose();
        _terminalCts?.Dispose();
        TearDownCoordinatorTerminal();
        Workspace?.Dispose();

        _owner?.Dispose();
    }
}
