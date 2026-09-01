using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Terminal;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// P2-47 — the real, DaemonClient-backed implementation of the control-center seams, replacing
/// <see cref="Mainguard.Agents.Agents.Mock.MockOrchestrator"/> in the shipped app. Every surface is a <b>live
/// projection off a daemon RPC</b> — nothing here is a mock or a hardcoded-empty stub:
/// <list type="bullet">
/// <item>agents — <c>AgentService.StreamAgentEvents</c> (snapshot-then-deltas);</item>
/// <item>merge queue — <c>MergeQueueService.StreamQueue</c> for the active repo handle, and the human
/// merge routes <c>BeginMerge</c>→<c>ConfirmMerge</c>;</item>
/// <item>plan approval — <c>PlanApprovalService.StreamPlans</c>, and Approve/Reject hit the daemon;</item>
/// <item>kill switch — <c>KillSwitchService.Engage</c>/<c>Resume</c>;</item>
/// <item>telemetry — <c>GatewayService.StreamSpend</c> + <c>Get/SetBudgets</c>;</item>
/// <item>coordinator chat — <c>CoordinatorService.StreamConversation</c> + <c>SendMessage</c>.</item>
/// </list>
/// Each streaming pump runs in the background with reconnect (the DaemonClient stream is single-shot;
/// this wraps it in a backoff loop that tolerates an unreachable daemon / a not-yet-active queue). The
/// projections are what the ViewModels read; steering calls route to the matching RPC.
///
/// <para><b>Vibe</b> is intentionally inert here — it is a separate future app (decision 2026-07-11),
/// not part of the shipped control center; MainWindow never routes to it.</para>
/// </summary>
/// <summary>The review-cockpit merge diff fetched over GetMergeDiff: the agent branch + its parsed patches.</summary>
public sealed record MergeDiffResult(string Branch, IReadOnlyList<Mainguard.Git.Models.FilePatch> Files);

/// <summary>An agent CLI died because the default-deny egress proxy refused a host (Fix 2 fallback).</summary>
/// <param name="AgentId">The agent whose CLI hit the block.</param>
/// <param name="AgentLabel">A human label for the agent (its kind, e.g. <c>claude-code</c>).</param>
/// <param name="Host">The egress host that was refused (candidate for the unblock).</param>
public sealed record EgressBlockInfo(string AgentId, string AgentLabel, string Host);

public sealed class DaemonBackedOrchestrator :
    IAgentService, IMergeQueueService, ICoordinatorService,
    IKillSwitchService, ITelemetryService, IVibeService, ICliAgentHost, IDisposable
{
    private const string DefaultCoordinatorId = "coordinator-1";

    /// <summary>The branch the merge queue lands on. The daemon's <c>BeginMerge</c> takes its lease against
    /// this same name, so the two legs of the RT-D1 conversation must agree on it.</summary>
    private const string MainBranchName = "main";

    /// <summary>Delay between a pump's stream ending/faulting and its re-subscribe.</summary>
    /// <remarks>Settable so a reconnect test observes a second subscribe in milliseconds rather than
    /// seconds. It changes the CADENCE only, never whether the re-subscribe happens.</remarks>
    internal static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Test seam: the agent-event stream <see cref="AgentPumpAsync"/> subscribes to. Never set in
    /// production, where it is <c>DaemonClient.StreamAgentEventsAsync</c>.
    ///
    /// <para>It exists because the property under test is <b>re-subscription</b>: that the pump opens
    /// the stream AGAIN after the previous subscription ended. That is unobservable through the real
    /// client — its own transport loop only ever returns on a terminal
    /// <c>PermissionDenied</c> or on cancellation, which is exactly the case the missing
    /// <see cref="ReconnectLoopAsync"/> made permanent.</para>
    /// </summary>
    internal Func<CancellationToken, IAsyncEnumerable<Proto.AgentEvent>>? AgentEventStreamOverride { get; set; }

    /// <summary>
    /// Test seam: the merge-queue stream <see cref="QueuePumpAsync"/> subscribes to — the exact analogue
    /// of <see cref="AgentEventStreamOverride"/>, and it exists for the same reason. Never set in
    /// production, where it is <c>DaemonClient.StreamQueueAsync</c>.
    ///
    /// <para>The property under test is <b>re-subscription of the QUEUE stream specifically</b>. ISSUES-LOG
    /// #11 (found live 2026-08-20) was a merge-queue rail that went to "Nothing queued" mid-session and
    /// stayed there: the daemon's log recorded exactly ONE <c>StreamQueue</c> call, ended after 32 ms, and
    /// not one retry over the next 5m45s while every other RPC on the same daemon kept succeeding. The
    /// agent pump's reconnect property had a regression test; this one did not, so nothing pinned it.</para>
    /// </summary>
    internal Func<string, CancellationToken, IAsyncEnumerable<Proto.QueueUpdate>>? QueueStreamOverride { get; set; }

    /// <summary>
    /// Test seam: the authoritative <c>ListAgents</c> answer <see cref="MergeAgentListing"/> folds in.
    /// Never set in production, where it is <c>DaemonClient.ListAgentsAsync</c>.
    ///
    /// <para>It exists because the property under test is the ISSUES-LOG #19 repair itself: that a
    /// projection which has lost an agent's ROLE — the one field no delta ever carries — gets it back
    /// from the listing, rather than stranding a live coordinator as an anonymous worker forever.</para>
    /// </summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Proto.AgentInfo>>>? AgentListOverride { get; set; }

    /// <summary>
    /// How often the login-harvest pump sweeps every live agent's CLI login state into the host OS
    /// keychain. A minute is a compromise: each sweep is one <c>ListAgents</c> plus one read-only
    /// <c>HarvestAgentCredentials</c> per agent (a <c>base64</c> of a few small files inside the jail),
    /// and the window it bounds is how much of a fresh interactive login a hard crash can lose.
    /// </summary>
    private static readonly TimeSpan DefaultLoginHarvestInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long <see cref="Dispose"/> will wait for the FINAL harvest before giving up. App
    /// close must stay responsive, so this is a budget, not a guarantee — the periodic sweep is what
    /// makes the keychain warm enough that losing this one costs at most the last interval.</summary>
    private static readonly TimeSpan ShutdownHarvestBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the daemon may say NOTHING about a spawn in flight before the client gives up on it.
    ///
    /// <para>This replaces a flat 5-minute gRPC deadline on <c>SpawnAgent</c>, which was measuring the
    /// wrong thing. SpawnAgent runs the whole provision chain — including, on a first run for a
    /// repository, the toolchain image build, which for <c>dotnet-10</c> is ~2.9 GB and routinely takes
    /// longer than five minutes on a fresh machine. The deadline then cancelled the server call, the
    /// daemon tore the half-made spawn down as a failure, and the next attempt started the same build
    /// over: a healthy launch reported as a hang, on every fresh environment.</para>
    ///
    /// <para>A bigger constant would only move the cliff. Since PR #319/#320 the daemon reports launch
    /// progress as state deltas on the spawning session, and this client already reads that stream —
    /// so the budget bounds SILENCE instead of duration (<see cref="SpawnProgressWatchdog"/>). A build
    /// that keeps reporting runs as long as it needs; a spawn that goes quiet is still reported, with
    /// what it last said.</para>
    /// </summary>
    /// <remarks>Settable so a test can drive the whole event→watchdog wiring in milliseconds instead of
    /// minutes — the same shape as <c>ControlCenterViewModel.CoordinatorConnectTimeout</c>. It changes the
    /// budget only, never what is measured.</remarks>
    internal static TimeSpan SpawnSilenceBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The outer bound on a spawn, carried as the gRPC deadline, so removing the false timeout does not
    /// create an unbounded wait. It is deliberately far past any healthy launch (a cold toolchain build
    /// plus a container start), because it exists to stop a pathological case — a daemon that keeps
    /// emitting lines while getting nowhere would never trip the silence budget — and not to police a
    /// slow one.
    /// </summary>
    private static readonly TimeSpan SpawnHardCap = TimeSpan.FromMinutes(60);

    private readonly DaemonClient _client;
    private readonly bool _ownsClient;
    private readonly Func<string, string?> _keystoreLookup;
    private readonly Func<string, IReadOnlyList<string>> _keystoreList;
    private readonly Action<string, string> _keystoreSave;
    private readonly Func<Mainguard.Git.Services.IOperationJournal> _journalFactory;
    private readonly Lazy<Mainguard.Agents.Services.IHostPullRequestGateway> _hostPullRequests;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _loginHarvestInterval;
    private readonly object _gate = new();

    // ---- projections (all guarded by _gate) ----
    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private readonly List<QueueEntry> _queue = new();
    private readonly Dictionary<string, (bool CanMerge, string Reason)> _gate_ = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeEntryOrigin> _origins = new(StringComparer.Ordinal);
    private readonly List<TaskPlan> _plans = new();
    private readonly List<WorkerPlanCard> _workerPlans = new();
    private OrchestrationBackpressure _backpressure = OrchestrationBackpressure.None;
    private PlanModeView _planMode = PlanModeView.Unknown;
    private readonly List<ChatLine> _transcript = new();
    private readonly List<ResourceSample> _samples = new();
    private readonly Dictionary<string, (long Tokens, long UsdMicros)> _agentSpend = new(StringComparer.Ordinal);
    /// <summary>Latest per-agent CPU/RAM tick from the daemon's sampler, keyed by agent id. Absence of an
    /// entry — or a null field within one — is "not measured" and must stay distinguishable from zero.</summary>
    private readonly Dictionary<string, (double? Cpu, double? RamBytes, bool Metered)> _agentResources =
        new(StringComparer.Ordinal);
    /// <summary>Whether a resource tick has EVER arrived. Before the first one there is nothing to report,
    /// which is not the same as a fleet reading zero.</summary>
    private bool _haveResourceTick;
    /// <summary>The spawns currently in flight, each watching for the daemon going silent. A list rather
    /// than a single slot because a coordinator start and a queue-entry resume can overlap, and a launch
    /// that is making progress must not be cancelled by another one's quiet.</summary>
    private readonly List<SpawnProgressWatchdog> _spawnWatchdogs = new();
    private string _mainSha = string.Empty;
    private long _totalUsdMicros;
    private long _totalTokens;
    private bool _frozen;
    private KillSwitchPhase _phase = KillSwitchPhase.Armed;
    private string _phaseText = string.Empty;

    private string? _repoHandle;
    private string? _repoLocalPath;
    private string _syncRemoteName = string.Empty;
    private string? _coordinatorAgentId;
    private Task? _agentPump;
    private Task? _planPump;
    private Task? _spendPump;
    private Task? _resourcePump;
    private Task? _conversationPump;
    /// <summary>Where a repository's saved CLI settings live between agents. Per repo by construction —
    /// approving a command here must not pre-approve it somewhere else.</summary>
    private readonly CliSettingsStore _cliSettings;
    private Task? _queuePump;
    private Task? _loginHarvestPump;
    private CancellationTokenSource? _queuePumpCts;
    private long _seq;

    /// <param name="keystoreLookup">Reads a P2-01 BYOK key by keystore name (e.g. <c>llm_anthropic</c>);
    /// defaults to the OS keyring. Injectable so tests never touch a real keyring.</param>
    /// <param name="keystoreList">Enumerates stored keystore key NAMES by prefix (drives the custom
    /// <c>llm_env_*</c> injection); defaults to the OS keyring, injectable like the lookup.</param>
    /// <param name="keystoreSave">Writes a keystore entry (persists the CLI login state a stop
    /// harvested — <c>cli_login_*</c>); defaults to the OS keyring, injectable like the lookup.</param>
    /// <param name="journalFactory">Supplies the T-19 operation journal the human merge is recorded in.
    /// Defaults to the app's own journal — the SAME undo journal the repo dashboard shows, so a merge
    /// driven from the agent surface is undoable from the git surface. Injectable so a test can point it
    /// at a temp database; it redirects only where the journal is written, never what the merge does.</param>
    /// <param name="hostPullRequests">
    /// The host PR seam an <see cref="MergeEntryOrigin.External"/> merge drives (P2-12). Defaults to the
    /// audited T-23 transport reading this host's keyring.
    /// <para><b>Why the client and not the daemon:</b> the host token lives only in the host OS keychain —
    /// nothing copies <c>token_&lt;host&gt;</c> into the VM — so the daemon simply has no credential to
    /// merge a pull request with. The lease and the gate stay daemon-side regardless; only the transport
    /// runs here, exactly like the local leg's <c>git merge</c>. Injectable so tests drive a fake host and
    /// never the live GitHub API.</para>
    /// </param>
    /// <param name="loginHarvestInterval">How often the background login-harvest pump sweeps live agents
    /// into the keychain (defaults to <see cref="DefaultLoginHarvestInterval"/>). Injectable so the
    /// round-trip test can drive a sweep in seconds instead of minutes; it changes the CADENCE only,
    /// never whether the sweep happens.</param>
    /// <param name="cliSettings">The PER-REPO store for a CLI's saved settings — the approved-command
    /// list. Deliberately not the keychain: settings are configuration the owner should be able to read
    /// and delete, while logins stay keychain-only. Injectable so tests write to a temp directory
    /// instead of the user's real store.</param>
    public DaemonBackedOrchestrator(
        DaemonClient client, bool ownsClient = true, Func<string, string?>? keystoreLookup = null,
        Func<string, IReadOnlyList<string>>? keystoreList = null,
        Action<string, string>? keystoreSave = null,
        Func<Mainguard.Git.Services.IOperationJournal>? journalFactory = null,
        Func<Mainguard.Agents.Services.IHostPullRequestGateway>? hostPullRequests = null,
        TimeSpan? loginHarvestInterval = null,
        CliSettingsStore? cliSettings = null)
    {
        _cliSettings = cliSettings ?? new CliSettingsStore();
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _loginHarvestInterval = loginHarvestInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultLoginHarvestInterval;
        _ownsClient = ownsClient;
        _keystoreLookup = keystoreLookup ?? DefaultKeystoreLookup;
        _keystoreList = keystoreList ?? DefaultKeystoreList;
        _keystoreSave = keystoreSave ?? DefaultKeystoreSave;
        _journalFactory = journalFactory ?? (() => new Mainguard.Git.Services.OperationJournal());
        _hostPullRequests = new Lazy<Mainguard.Agents.Services.IHostPullRequestGateway>(
            hostPullRequests ?? DefaultHostPullRequestGateway);
    }

    /// <summary>The shipped host seam: the ONE audited T-23 transport over this host's keyring. Built once
    /// (its HttpClient is shared — a per-merge client is socket exhaustion) and only if a merge needs it.</summary>
    private static Mainguard.Agents.Services.IHostPullRequestGateway DefaultHostPullRequestGateway()
        => new Mainguard.Agents.Services.HostPullRequestGateway(
            new Mainguard.Git.Services.PullRequestService(new Mainguard.Git.Services.GitService()));

    private static string? DefaultKeystoreLookup(string name)
    {
        try
        {
            return ((Mainguard.Git.Security.ISecureKeyStore)new Mainguard.Git.Security.SecureKeyring()).Get(name);
        }
        catch (Exception)
        {
            // No keyring on this box — the CLI authenticates interactively in its terminal instead.
            return null;
        }
    }

    private static IReadOnlyList<string> DefaultKeystoreList(string prefix)
    {
        try
        {
            return ((Mainguard.Git.Security.ISecureKeyStore)new Mainguard.Git.Security.SecureKeyring()).List(prefix);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static void DefaultKeystoreSave(string name, string value)
    {
        try
        {
            ((Mainguard.Git.Security.ISecureKeyStore)new Mainguard.Git.Security.SecureKeyring()).Set(name, value);
        }
        catch (Exception)
        {
            // No keyring on this box — the login state simply isn't persisted (the CLI will ask
            // again next launch, the pre-vault behavior), never an app crash on Stop.
        }
    }

    /// <summary>The user's custom env-var keys (<c>llm_env_*</c>) as name→value pairs, ready for
    /// SpawnAgent's <c>extra_env</c>. Values come fresh from the keyring per spawn.
    ///
    /// <para><b>Internal, not private, on purpose (ISSUES-LOG #37).</b> This is the leg that decides
    /// whether a "Custom key" saved in AI Providers reaches an agent at all, and nothing covered it:
    /// a walkthrough leg that spawned agents through a raw <c>SpawnAgent</c> RPC (bypassing this
    /// method entirely) saw an empty <c>agent.env</c> in the jail and read it as a delivery defect in
    /// the product. It is not — but "the client really does read the keyring and really does put the
    /// pairs on the wire" was unprovable without a test, so `CustomEnvKeyDeliveryTests` pins it.</para>
    /// </summary>
    internal IReadOnlyDictionary<string, string> CollectCustomEnvKeys()
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var keystoreKey in _keystoreList(ApiKeyProviderMap.CustomEnvKeyPrefix))
        {
            if (ApiKeyProviderMap.EnvVarForCustomKey(keystoreKey) is { } envVar
                && _keystoreLookup(keystoreKey) is { Length: > 0 } value)
            {
                extra[envVar] = value;
            }
        }

        return extra;
    }

    /// <summary>The shipped-app bundle: a loopback DaemonClient behind every control-center seam.</summary>
    public static OrchestratorServices CreateBundle()
    {
        var adapter = new DaemonBackedOrchestrator(DaemonClient.ForLoopback());
        adapter.Start();
        return OrchestratorServices.FromSingle(adapter);
    }

    /// <summary>Starts every background stream pump (idempotent). Construction never blocks on the daemon;
    /// each pump tolerates an unreachable daemon (reconnects with a fixed delay until cancelled).</summary>
    public void Start()
    {
        if (_agentPump is not null)
        {
            return;
        }

        _agentPump = Task.Run(() => AgentPumpAsync(_cts.Token));
        _planPump = Task.Run(() => PlanPumpAsync(_cts.Token));
        _spendPump = Task.Run(() => SpendPumpAsync(_cts.Token));
        _resourcePump = Task.Run(() => ResourcePumpAsync(_cts.Token));
        _conversationPump = Task.Run(() => ConversationPumpAsync(_cts.Token));
        _loginHarvestPump = Task.Run(() => LoginHarvestPumpAsync(_cts.Token));
    }

    /// <summary>P2-47 #5: a live terminal gateway over the daemon's <c>TerminalService.Attach</c> bidi stream,
    /// sharing this adapter's DaemonClient. The caller (the agent workspace) owns + disposes it per attach.</summary>
    public ITerminalGateway CreateTerminalGateway() => new DaemonTerminalGateway(_client);

    /// <summary>The live egress-allowlist seam over this adapter's DaemonClient — the same factory shape
    /// as <see cref="CreateTerminalGateway"/>. Without it the allowlist editor had no gateway it could be
    /// shown with except the hardcoded in-memory seed, so the sandbox egress policy was enforced but
    /// neither inspectable nor editable.</summary>
    public IEgressAllowlistGateway CreateEgressAllowlistGateway() => new DaemonEgressAllowlistGateway(_client);

    /// <summary>The live external-PR-intake configuration seam over this adapter's DaemonClient — the
    /// same factory shape as the two above. Without it the intake settings page had nothing to write to
    /// except an in-process store the daemon never reads, which is why the page shipped with no way to
    /// open it at all.</summary>
    public IPrIntakeGateway CreatePrIntakeGateway() => new DaemonPrIntakeGateway(_client);

    /// <summary>The DEV-ONLY queue-seeding seam (docs/design/queue-seeding.md), same factory shape as
    /// the gateways above. The repo handle is read live so the panel always seeds the repo the rail is
    /// showing. On a daemon without the seeding boot flag the gateway's availability probe answers
    /// false (the service is unmapped) and the panel never appears.</summary>
    public IQueueSeedingGateway CreateQueueSeedingGateway()
        => new DaemonQueueSeedingGateway(_client, () => _repoHandle);

    /// <summary>Fix 2: raised when a spawned agent's CLI DIED on a host the default-deny proxy refused,
    /// so the operator can unblock it and retry. Fired from the agent-event pump thread (the consumer
    /// marshals to the UI thread).</summary>
    public event Action<EgressBlockInfo>? EgressBlocked;

    /// <summary>Adds <paramref name="host"/> to the daemon-owned egress allowlist and re-renders the running
    /// proxy — the "unblock" action behind the block-notification prompt. Throws the daemon's reason on
    /// failure (the caller surfaces it).</summary>
    public async Task AddAllowlistHostAsync(
        string host, Mainguard.Agents.Agents.Sandbox.EgressEntryKind kind, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await _client.AddAllowlistHostAsync(name: host, hostPattern: host, kind: kind.ToString(), cts.Token)
            .ConfigureAwait(false);
    }

    private static bool IsTerminal(AgentLifecycleState state) =>
        state is AgentLifecycleState.Dead or AgentLifecycleState.TornDown
            or AgentLifecycleState.Rejected or AgentLifecycleState.Merged;

    /// <summary>Point the merge-queue projection at a repo handle (from the daemon's <c>ProvisionRepo</c>).
    /// Restarts the queue pump so the merge rail + review cockpit reflect that repo's live queue.
    ///
    /// <para><paramref name="localRepoPath"/> and <paramref name="syncRemoteName"/> are the OTHER half of
    /// the same <c>ProvisionRepo</c> answer, and they are what makes the merge button able to merge: the
    /// human foreground merge lands on the user's own host checkout (never the daemon mirror, which is
    /// staging), fetching the agent branch over the SC-2-resolved sync remote registered on it. Without
    /// them the queue is observable but not mergeable, and <see cref="ConfirmMergeAsync"/> says so rather
    /// than recording a merge it never performed.</para></summary>
    /// <summary>Provision <paramref name="repoPath"/> into the daemon over THIS adapter's shared
    /// mTLS channel, with the same 5-minute deadline the OOBE/Add-Repos path uses — a cold bare
    /// clone of a real repository routinely outlives the 5-second budget the old per-call client
    /// gave it, and that timeout was swallowed, which made the whole agent platform look dead on
    /// any non-trivial repo. Throws on failure; the caller owns surfacing the reason.</summary>
    public Task<ProvisionedRepo> ProvisionRepoAsync(string repoPath, CancellationToken ct) =>
        _client.ProvisionRepoAsync(repoPath, ct, deadline: TimeSpan.FromMinutes(5));

    /// <summary>Detach the merge-queue projection from its repo: stop the queue pump and empty the
    /// queue/gate/origin state so the rail reflects NOTHING rather than the previously opened repo.
    /// Called before provisioning a newly opened repo — without it, a failed or slow provision left
    /// this adapter pointed at the old repo while the user looked at the new one, and a Merge would
    /// have fast-forwarded the OLD checkout.</summary>
    public void ClearActiveRepo()
    {
        lock (_gate)
        {
            _queuePumpCts?.Cancel();
            _queuePumpCts?.Dispose();
            _queuePumpCts = null;
            _queuePump = null;
            _repoHandle = null;
            _repoLocalPath = null;
            _syncRemoteName = string.Empty;
            _queue.Clear();
            _gate_.Clear();
            _origins.Clear();
            _mainSha = string.Empty;
        }

        RaiseIsolated(() => Changed?.Invoke());
    }

    /// <summary>True when this adapter is already bound to <paramref name="repoHandle"/> with its queue
    /// pump alive — lets a caller skip a redundant <see cref="ClearActiveRepo"/>+re-provision round trip
    /// for a repo that's already open and working (see <see cref="SetActiveRepo"/>'s same-handle no-op,
    /// which this mirrors for callers that sit above it and don't have a live handle to compare against
    /// without asking first).</summary>
    public bool IsBoundTo(string repoHandle)
    {
        lock (_gate)
        {
            return _repoHandle == repoHandle && _queuePump is not null;
        }
    }

    public void SetActiveRepo(string repoHandle, string? localRepoPath = null, string? syncRemoteName = null)
    {
        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return;
        }

        lock (_gate)
        {
            // The local binding is refreshed even when the handle is unchanged — a re-provision can hand
            // back a renamed sync remote, and a merge against the previous name would fail its fetch.
            if (!string.IsNullOrWhiteSpace(localRepoPath))
            {
                _repoLocalPath = localRepoPath;
            }

            if (!string.IsNullOrWhiteSpace(syncRemoteName))
            {
                _syncRemoteName = syncRemoteName!;
            }

            if (_repoHandle == repoHandle && _queuePump is not null)
            {
                return;
            }

            _repoHandle = repoHandle;
            _queuePumpCts?.Cancel();
            _queuePumpCts?.Dispose();
            _queuePumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var ct = _queuePumpCts.Token;
            _queuePump = Task.Run(() => QueuePumpAsync(repoHandle, ct));
        }
    }

    // ---- pumps -----------------------------------------------------------

    /// <summary>
    /// The agent-event pump. Reconnects like every other pump — it did not, and that was the single
    /// most damaging version of the bug in this class.
    ///
    /// <para><b>Why it mattered.</b> This is the stream every other agent surface is derived from: the
    /// rail, the coordinator card, the spawn watchdogs, the egress-block prompt. It used to run one
    /// bare <c>await foreach</c> inside <c>catch (Exception) { }</c>, so ANY fault ended the task for
    /// good — and <see cref="Start"/> guards on <c>_agentPump is not null</c>, so nothing could ever
    /// restart it for the lifetime of the process. Worse, the fault did not have to come from the
    /// daemon: <see cref="ApplyAgentEvent"/> raises <see cref="EventReceived"/>, <see cref="Changed"/>
    /// and <see cref="EgressBlocked"/> synchronously on this thread, so one throwing UI subscriber
    /// killed the stream. Nothing reported it either — <c>DaemonClient.State</c> stays
    /// <c>Connected</c> because the connection is fine, so there is no banner. The visible symptom is
    /// an agent list frozen at its last good snapshot, forever, with the app looking healthy.</para>
    /// </summary>
    private async Task AgentPumpAsync(CancellationToken ct)
    {
        var stream = AgentEventStreamOverride ?? (token => _client.StreamAgentEventsAsync(token));
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var e in stream(token).ConfigureAwait(false))
            {
                ApplyAgentEvent(e);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the agent-event pump against <paramref name="ct"/> for a test, so the reconnect
    /// property is asserted without <see cref="Start"/>'s once-per-process guard or the five other pumps.</summary>
    internal Task RunAgentPumpForTestAsync(CancellationToken ct) => AgentPumpAsync(ct);

    private async Task PlanPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamPlansAsync(string.Empty, token).ConfigureAwait(false))
            {
                ApplyPlanUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task SpendPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var sample in _client.StreamSpendAsync(token).ConfigureAwait(false))
            {
                ApplySpendSample(sample);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task ResourcePumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var snapshot in _client.StreamAgentResourcesAsync(token).ConfigureAwait(false))
            {
                ApplyResourceSnapshot(snapshot);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task ConversationPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamConversationAsync(DefaultCoordinatorId, token).ConfigureAwait(false))
            {
                ApplyConversationUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The login-harvest pump — the production caller of <see cref="PersistLiveAgentLoginsAsync"/>.
    ///
    /// <para><b>Why this exists.</b> The round-trip's durable half is the host OS keychain, and the only
    /// thing that ever wrote to it was <see cref="EndAgentAsync"/>: the client persisted whatever the
    /// <c>StopAgent</c> RPC handed back. <see cref="PersistLiveAgentLoginsAsync"/> — the sweep that keeps
    /// the keychain warm while agents RUN — had no callers anywhere in the repository, so every path that
    /// is not an explicit in-app Stop lost the login outright: app close, a daemon restart, a VM stop, a
    /// crash, or simply leaving the agent running. The user then had to sign in again inside the jail on
    /// every single session, which is the exact symptom the credentialPaths round-trip was built to
    /// remove. Restore was wired; harvest was not.</para>
    ///
    /// <para>Best-effort and silent by construction: a down daemon, an agent with no jail, and a CLI that
    /// has not been signed into yet all yield nothing, and an empty harvest never clobbers a good
    /// keychain entry (<see cref="PersistHarvestedLogin"/> ignores it). Nothing here stores a credential
    /// agent-side — the bytes go from the jail's tmpfs straight into the host keychain.</para>
    /// </summary>
    private async Task LoginHarvestPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_loginHarvestInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await PersistLiveAgentLoginsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A failed sweep must never end the pump — the next one retries.
            }
        }
    }

    private async Task QueuePumpAsync(string repoHandle, CancellationToken ct)
    {
        var stream = QueueStreamOverride ?? ((handle, token) => _client.StreamQueueAsync(handle, token));
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in stream(repoHandle, token).ConfigureAwait(false))
            {
                ApplyQueueUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the merge-queue pump against <paramref name="ct"/> for a test, so its reconnect
    /// property is asserted without going through <see cref="SetActiveRepo"/>'s binding bookkeeping.</summary>
    internal Task RunQueuePumpForTestAsync(string repoHandle, CancellationToken ct)
        => QueuePumpAsync(repoHandle, ct);

    /// <summary>Runs a single-shot stream body, reconnecting with a fixed delay on any fault (an
    /// unreachable daemon, a NOT_FOUND queue, a dropped stream) until cancelled. This is what makes an
    /// empty projection <b>live</b> — it keeps trying — rather than a hardcoded stub.</summary>
    private static async Task ReconnectLoopAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await body(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Transient (daemon down, queue not yet active, stream dropped) — back off and re-subscribe.
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- projection appliers --------------------------------------------

    /// <remarks>Internal rather than private so a test can drive the daemon→watchdog wiring with real
    /// <c>AgentEvent</c> messages; the live agent-event pump is the only production caller.</remarks>
    internal void ApplyAgentEvent(Proto.AgentEvent e)
    {
        switch (e.EventCase)
        {
            case Proto.AgentEvent.EventOneofCase.Snapshot:
                lock (_gate)
                {
                    _agents.Clear();
                    foreach (var a in e.Snapshot.Agents)
                    {
                        _agents[a.AgentId] = MapInfo(a);
                    }

                    // The coordinator is whichever live session carries the role (reconnect-safe);
                    // a snapshot without one clears it (the coordinator was stopped).
                    _coordinatorAgentId = _agents.Values
                        .FirstOrDefault(a => a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator)?.AgentId;
                }

                // ISSUES-LOG #19: confirm the snapshot against ListAgents. The snapshot is not the same
                // data by another route — the daemon builds it as a positional `id:kind:state:role`
                // string split on ',' and ':', while ListAgents sends typed fields — and it is the ONLY
                // thing that ever repopulates this projection wholesale. When the two disagree the RPC
                // wins, and it is newer besides (it is issued after the snapshot was taken). One extra
                // unary call per stream connect, which happens on connect and reconnect, not per event.
                _ = ResyncAgentsAsync();
                break;

            case Proto.AgentEvent.EventOneofCase.State:
                var resync = false;
                var newState = MapState(e.State.State);
                var reason = e.State.Reason ?? string.Empty;
                string? agentKind = null;
                lock (_gate)
                {
                    if (_agents.TryGetValue(e.AgentId, out var existing))
                    {
                        _agents[e.AgentId] = existing with
                        {
                            State = newState,
                            // Carry the transition reason (e.g. a Dead CLI's exit tail) into the one live
                            // fact slot, so the surface can show WHY and run the egress block-detector on it.
                            Detail = string.IsNullOrEmpty(reason) ? existing.Detail : reason,
                        };
                        agentKind = existing.Name;
                    }
                    else
                    {
                        // A state delta can outrun the snapshot for a freshly spawned agent, and the
                        // delta carries neither kind nor role. A fabricated role-less record here is
                        // what made a just-started coordinator render as a plain worker row (field
                        // bug, 2026-07-17) — so place the placeholder for instant UI, then resync
                        // the authoritative kind/role off ListAgents.
                        _agents[e.AgentId] = new AgentInfo(
                            e.AgentId, e.AgentId, $"agent/{e.AgentId}",
                            newState, reason, DateTimeOffset.UtcNow);
                        resync = true;
                    }
                }

                if (resync)
                {
                    _ = ResyncAgentsAsync();
                }

                // A still-provisioning session that reports a reason is the daemon telling us what a
                // spawn is DOING (the toolchain build's progress lines). That is the evidence the spawn
                // watchdogs measure, so it is fed to them here — the only place it arrives. Restricted to
                // the provisioning state on purpose: a running agent's chatter must not vouch for a
                // different spawn that is wedged.
                if (newState == AgentLifecycleState.Provisioning && reason.Length > 0)
                {
                    NoteSpawnProgress(reason);
                }

                // Egress block-notification fallback (Fix 2): a CLI that DIED with a "couldn't reach HOST"
                // reason was almost certainly refused by the default-deny proxy — surface it so the operator
                // can unblock the host and retry, instead of a silent exit-1.
                if (IsTerminal(newState) && reason.Length > 0
                    && Mainguard.Agents.Agents.Sandbox.EgressBlockDetector.TryDetectBlockedHost(reason) is { Length: > 0 } blockedHost)
                {
                    RaiseIsolated(() => EgressBlocked?.Invoke(
                        new EgressBlockInfo(e.AgentId, agentKind ?? e.AgentId, blockedHost)));
                }

                break;

            case Proto.AgentEvent.EventOneofCase.Log:
                // Log lines feed the terminal (its own attach stream); nothing to project here.
                break;
        }

        RaiseIsolated(() => EventReceived?.Invoke(new AgentEvent(
            Interlocked.Increment(ref _seq), e.EventCase.ToString(), e.AgentId, string.Empty, DateTimeOffset.UtcNow)));
        RaiseIsolated(() => Changed?.Invoke());
    }

    /// <summary>
    /// Raises one subscriber-facing event without letting a bad subscriber take anything else down.
    ///
    /// <para>The projection appliers run ON the pump thread and raise <see cref="EventReceived"/>,
    /// <see cref="Changed"/> and <see cref="EgressBlocked"/> synchronously, so an exception out of any
    /// handler used to propagate all the way to the pump. Two things went wrong at once: every later
    /// raise in the same call was skipped (a throwing <see cref="EventReceived"/> subscriber meant
    /// <see cref="Changed"/> never fired, so the rail did not redraw even though the projection had
    /// already been updated under the lock), and the exception ended the stream. Reconnecting alone
    /// would not have fixed the second half — a handler that throws on every snapshot would simply
    /// re-throw on the re-subscribe and spin. The projection is authoritative and is already committed
    /// by the time we get here, so a handler fault is contained to that handler.</para>
    /// </summary>
    private static void RaiseIsolated(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception)
        {
            // One subscriber's fault must never stop the stream that feeds every other surface.
        }
    }

    /// <summary>
    /// Feeds one launch-progress line to every spawn currently in flight. Called from the agent-event
    /// pump thread.
    ///
    /// <para>Fanned out rather than routed by agent id, and that is deliberate: the client does not learn
    /// the id until <c>SpawnAgent</c> RETURNS, which is precisely the moment the wait it is trying to
    /// survive has already ended. Over-crediting is bounded — only provisioning-state deltas reach here,
    /// so the only thing that can extend a spawn's budget is another spawn genuinely provisioning — and
    /// the hard cap bounds it regardless.</para>
    /// </summary>
    private void NoteSpawnProgress(string line)
    {
        SpawnProgressWatchdog[] watching;
        lock (_gate)
        {
            if (_spawnWatchdogs.Count == 0)
            {
                return;
            }

            watching = _spawnWatchdogs.ToArray();
        }

        foreach (var watchdog in watching)
        {
            watchdog.NoteProgress(line);
        }
    }

    /// <summary>
    /// Runs one spawn RPC under the silence budget: registers a watchdog for the duration, gives the call
    /// the hard cap as its gRPC deadline, and unregisters at the end.
    ///
    /// <para>Both spawn routes go through here — starting a coordinator and resuming a stranded queue
    /// entry — because both run the identical daemon provision chain, toolchain build included, and the
    /// resume path inherited exactly the same five-minute cliff.</para>
    /// </summary>
    internal async Task<T> SpawnUnderWatchdogAsync<T>(
        Func<CancellationToken, TimeSpan, Task<T>> call, CancellationToken ct)
    {
        var watchdog = new SpawnProgressWatchdog(SpawnSilenceBudget);
        lock (_gate)
        {
            _spawnWatchdogs.Add(watchdog);
        }

        try
        {
            return await watchdog.RunAsync(token => call(token, SpawnHardCap), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _spawnWatchdogs.Remove(watchdog);
            }
        }
    }

    internal void ApplyQueueUpdate(Proto.QueueUpdate update)
    {
        lock (_gate)
        {
            _mainSha = update.MainSha ?? string.Empty;
            _queue.Clear();
            _gate_.Clear();
            _origins.Clear();
            foreach (var entry in update.Entries)
            {
                // P2-12 origin: an External entry is an upstream PR that merges through the host API, not
                // by fast-forwarding a local branch. It is carried so the merge path can refuse rather
                // than land PR commits on the user's main behind the host's back.
                _origins[entry.AgentId] = Enum.TryParse<MergeEntryOrigin>(
                    entry.Origin, ignoreCase: true, out var origin) ? origin : MergeEntryOrigin.Local;

                var state = Enum.TryParse<WorkerMergeState>(entry.State, ignoreCase: true, out var s)
                    ? s : WorkerMergeState.Working;

                // P2-11 step 4: the daemon's must-acknowledge items. This was hardcoded to an EMPTY list,
                // which made the review surface structurally unable to show what was blocking a merge: the
                // gate is daemon-side, the acknowledgment RPC is addressed by item id, and the only place
                // an id could have come from was this projection. A blocked branch therefore rendered with
                // no acknowledge control at all — the daemon refused the merge and the human had no way to
                // clear it.
                var flagged = entry.FlaggedItems.Count == 0
                    ? Array.Empty<FlaggedItem>()
                    : entry.FlaggedItems
                        .Select(f => new FlaggedItem(f.Id, f.Path, f.Category, f.Fact, f.Acknowledged))
                        .ToArray();

                // H4 — the entry's last verification VERDICT. This was hardcoded to `null`, which is this
                // projection's own way of saying "never verified": every entry the daemon served, the ones
                // whose tests had just gone red included, reached the rail claiming no verification had
                // ever happened. Three real wire fields, and only three.
                //
                // `HasLastVerificationPassed` is protobuf's field-presence test, and it is the whole
                // mechanism here: proto3 would default the bool to false, and a false meaning "never
                // verified" is indistinguishable from one meaning "the tests failed" — the exact
                // conflation this change exists to end. Unset stays null, i.e. no record.
                //
                // Note what is NOT built here. The old client-side record carried TestsPassed/TestsTotal,
                // which no wire has ever carried and nothing in this system measures — verification watches
                // a process exit code in a jail and parses nobody's test runner. There is no honest source
                // for those numbers, so the type lost them rather than this projection inventing them; a
                // fabricated "58 of 58 green" in a review surface is worse than no number at all.
                var verdict = entry.HasLastVerificationPassed
                    ? new VerificationVerdict(
                        entry.LastVerificationPassed,
                        entry.LastVerificationCommand ?? string.Empty,
                        ParseTimestamp(entry.LastVerificationAt))
                    : null;

                _queue.Add(new QueueEntry(
                    entry.AgentId, entry.AgentId, $"agent/{entry.AgentId}", state,
                    entry.GateReason ?? string.Empty, Verification: verdict, FlaggedItems: flagged,
                    // Carried from the daemon rather than inferred from the state: a client that guessed
                    // "Verifying ⇒ a run is happening" would be wrong for exactly the entries this matters
                    // for — the ones a restart left frozen mid-verification.
                    VerificationInFlight: entry.VerificationInFlight,
                    // Three-valued on purpose. `HasHasLiveSandbox` is protobuf's field-presence test: a
                    // daemon that predates the field leaves it unset, and mapping that to `false` would
                    // render every one of its entries as stranded and offer to spawn jails for agents that
                    // are running. Unset means unknown, and unknown changes nothing.
                    HasLiveSandbox: entry.HasHasLiveSandbox ? entry.HasLiveSandbox : null,
                    // The daemon has always sent this and the client has always thrown it away, which is
                    // why the review cockpit's "verified @ <sha>" stamp never rendered: the value existed
                    // on the wire and stopped here.
                    VerifiedMainSha: string.IsNullOrEmpty(entry.VerifiedMainSha) ? null : entry.VerifiedMainSha,
                    // What the human APPROVED for this branch — the half of the review that is not the
                    // diff. Empty means the entry has no approved plan (manual agent, external-PR head,
                    // plan mode off), and the surface then draws no approval panel at all rather than an
                    // empty one asserting an approval nobody gave.
                    ApprovedPlanId: NullIfEmpty(entry.ApprovedPlanId),
                    ApprovedPlanTitle: NullIfEmpty(entry.ApprovedPlanTitle),
                    ApprovedApproach: NullIfEmpty(entry.ApprovedPlanApproach),
                    DeviationDeclaration: NullIfEmpty(entry.DeviationDeclaration),
                    // The facts behind a conflict card. `RebaseConflict` is a MESSAGE field, so protobuf's
                    // presence test is exact: absent means nothing is parked, and it must not be mapped to
                    // an empty conflict — an empty path list renders as "nothing conflicts", which is the
                    // one thing a conflict card must never say.
                    RebaseConflict: entry.RebaseConflict is null
                        ? null
                        : new QueueRebaseConflict(
                            entry.RebaseConflict.Worktree ?? string.Empty,
                            entry.RebaseConflict.MainBranch ?? string.Empty,
                            entry.RebaseConflict.Paths.ToArray(),
                            ParseTimestamp(entry.RebaseConflict.ParkedAt))));
                _gate_[entry.AgentId] = (entry.CanMerge, entry.GateReason ?? string.Empty);
            }
        }

        // Isolated for the same reason ApplyAgentEvent's raises are (see RaiseIsolated): this runs ON the
        // queue pump thread, so a throwing subscriber propagated straight out of the `await foreach` and
        // tore the queue stream down. The reconnect loop caught it and re-subscribed, which meant the rail
        // silently lost every push in the gap and re-threw on the next one — a stream cycling every
        // ReconnectDelay for as long as the handler kept faulting, with no banner and no log line.
        RaiseIsolated(() => Changed?.Invoke());
    }

    private void ApplyPlanUpdate(Proto.PlanUpdate update)
    {
        lock (_gate)
        {
            _plans.Clear();
            _workerPlans.Clear();
            foreach (var p in update.Plans)
            {
                // Pending plans are approvable cards; ESCALATED ones are kept too, because a worker that
                // stopped after spending its revision budget is the one state that most needs a human and
                // would otherwise vanish from the surface entirely. Approved/Rejected stay in daemon history.
                var pending = string.Equals(p.Status, "Pending", StringComparison.OrdinalIgnoreCase);
                var escalated = string.Equals(p.Status, "Escalated", StringComparison.OrdinalIgnoreCase);
                if (!pending && !escalated)
                {
                    continue;
                }

                if (pending)
                {
                    _plans.Add(new TaskPlan(
                        p.PlanId, p.Title, p.Scope.ToArray(), p.Approach, p.TestStrategy,
                        (decimal)p.BudgetUsd, DateTimeOffset.UtcNow));
                }

                _workerPlans.Add(new WorkerPlanCard(
                    p.PlanId, p.WorkerAgentId, p.CoordinatorId, p.Title, p.Scope.ToArray(),
                    p.Approach, p.TestStrategy, (decimal)p.BudgetUsd, DateTimeOffset.UtcNow,
                    p.Status, p.Revision, p.RevisionsRemaining, update.MaxPlanRevisions, p.RejectionFeedback,
                    // Carried, never re-derived. What the previous scope WAS is a fact the daemon captured
                    // when the re-scope was presented; a client that looked it up from whatever plan that
                    // id names *now* would render a different claim as soon as a second re-scope existed.
                    p.SupersedesPlanId, p.PreviousScope.ToArray(), p.RescopeCount));
            }

            // Carried verbatim from the daemon rather than re-derived here: the number that refuses the
            // coordinator a spawn and the number the human reads must be the same number.
            _backpressure = new OrchestrationBackpressure(
                update.BlockedWorkerCount, update.EscalatedWorkerCount, update.ActiveWorkerCount,
                update.MaxActiveWorkers, update.MaxPlanRevisions, update.BackpressureSignal);

            // Carried on the plan stream, so the state and the cards it explains arrive together. A
            // separate poll would let the screen say "no plans waiting" and "plan mode is on" out of step
            // with each other, which is the pair of facts a human reads as "nothing is running".
            _planMode = new PlanModeView(update.PlanModeEnabled, update.PlanModeSummary);
        }

        Changed?.Invoke();
    }

    private void ApplyConversationUpdate(Proto.ConversationUpdate update)
    {
        lock (_gate)
        {
            _transcript.Clear();
            foreach (var turn in update.Turns.OrderBy(t => t.Seq))
            {
                _transcript.Add(new ChatLine(
                    MapChatKind(turn.Role), turn.Text, DateTimeOffset.UtcNow,
                    string.IsNullOrEmpty(turn.PlanId) ? null : turn.PlanId));
            }
        }

        Changed?.Invoke();
    }

    private void ApplySpendSample(Proto.SpendSample sample)
    {
        lock (_gate)
        {
            _totalUsdMicros += sample.UsdMicrosSpent;
            _totalTokens += sample.TokensSpent;
            if (!string.IsNullOrEmpty(sample.AgentId))
            {
                _agentSpend.TryGetValue(sample.AgentId, out var acc);
                _agentSpend[sample.AgentId] = (acc.Tokens + sample.TokensSpent, acc.UsdMicros + sample.UsdMicrosSpent);
            }

            AppendSampleLocked();
        }

        Sampled?.Invoke();
    }

    /// <summary>
    /// Replaces the per-agent CPU/RAM map from one daemon tick. Whole-set replacement (not merge) on
    /// purpose: an agent that has gone away must lose its numbers rather than keep showing its last ones
    /// forever, which would be a stale reading presented as a live one.
    /// </summary>
    private void ApplyResourceSnapshot(Proto.AgentResourcesSnapshot snapshot)
    {
        lock (_gate)
        {
            _agentResources.Clear();
            foreach (var row in snapshot.Agents)
            {
                if (string.IsNullOrEmpty(row.AgentId)) continue;
                // HasCpuPercent/HasMemBytes are proto3 explicit presence: false means the daemon could
                // not measure it. Reading row.CpuPercent unconditionally would silently yield 0.0 and
                // recreate the exact bug this feature fixes.
                _agentResources[row.AgentId] = (
                    row.HasCpuPercent ? row.CpuPercent : null,
                    row.HasMemBytes ? row.MemBytes : null,
                    row.Metered);
            }

            _haveResourceTick = true;
            AppendSampleLocked();
        }

        Sampled?.Invoke();
    }

    /// <summary>
    /// Appends one combined history point. Totals are sums of the readings that EXIST: if nothing has been
    /// measured yet, the total is null (unknown) rather than 0, so the monitor's header can say so.
    /// Caller holds <c>_gate</c>.
    /// </summary>
    private void AppendSampleLocked()
    {
        double? cpu = null;
        double? ramGb = null;
        if (_haveResourceTick)
        {
            foreach (var (agentCpu, agentRam, _) in _agentResources.Values)
            {
                if (agentCpu is { } c) cpu = (cpu ?? 0) + c;
                if (agentRam is { } r) ramGb = (ramGb ?? 0) + r / (1024.0 * 1024.0 * 1024.0);
            }
        }

        // Spend is reported only when at least one live agent is actually metered. The ledger only ever
        // counts gateway-transited traffic, so with nothing metered the running total is structurally 0 —
        // and "$0.00" would read as "you have spent nothing" rather than "this is not being measured".
        decimal? spend = AnyMeteredLocked() ? _totalUsdMicros / 1_000_000m : null;

        _samples.Add(new ResourceSample(DateTimeOffset.UtcNow, cpu, ramGb, spend));
        if (_samples.Count > 120)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>True when any live agent's spend is measurable. Caller holds <c>_gate</c>.</summary>
    private bool AnyMeteredLocked()
    {
        foreach (var (_, _, metered) in _agentResources.Values)
        {
            if (metered) return true;
        }

        return false;
    }

    /// <summary>
    /// Fetches the authoritative agent list (kind + role) and folds it into the projection.
    ///
    /// <para>Driven from three places, and each is load-bearing: after a state delta for an agent the
    /// projection had never seen (the delta carries neither kind nor role), after every stream
    /// <b>snapshot</b>, and once a minute from <see cref="PersistLiveAgentLoginsAsync"/> — see
    /// <see cref="MergeAgentListing"/> for why the projection needs a standing repair path at all.</para>
    ///
    /// <para>Best-effort: a daemon hiccup simply leaves the projection as it was, and the next caller
    /// tries again within the minute.</para>
    /// </summary>
    private async Task ResyncAgentsAsync()
    {
        IReadOnlyList<Proto.AgentInfo> listed;
        try
        {
            listed = await ListAgentsFromDaemonAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // the periodic repair pass retries
        }

        if (MergeAgentListing(listed))
        {
            RaiseIsolated(() => Changed?.Invoke());
        }
    }

    /// <summary>
    /// ISSUES-LOG #19 — folds an authoritative <c>ListAgents</c> answer into the projection, and is the
    /// projection's only way back from being wrong.
    ///
    /// <para><b>Why this exists.</b> The projection is fed by <c>StreamAgentEvents</c>, whose snapshot is
    /// one destructive replacement taken at subscribe time; after that only deltas arrive, and a delta
    /// carries <b>neither kind nor role</b>. A delta for an unseen agent therefore fabricates a
    /// <b>role-less</b> placeholder, and the single <c>ListAgents</c> call that was supposed to repair it
    /// gave up permanently on one failure. That is enough to strand a live coordinator in the projection
    /// as an anonymous worker: the Coordinator panel filters on <c>Role == "coordinator"</c> and finds
    /// nothing, so it reports "No coordinator running" — for 20+ minutes, while the daemon's own
    /// <c>ListAgents</c> answered <c>role=coordinator</c> for that agent once a minute the whole time, to
    /// this same client, on a call whose answer was thrown away except for the ids.</para>
    ///
    /// <para><b>What it will and will not overwrite.</b> Kind and role are identity — the listing is
    /// authoritative for them and they are always corrected. <see cref="AgentInfo.State"/> and the live
    /// <c>Detail</c> are NOT: those flow on deltas, which are newer than any poll, and a listing taken a
    /// moment ago would otherwise walk a just-Dead agent back to Working and wipe the exit tail the
    /// surface is showing. An agent the projection has never seen is added whole.</para>
    ///
    /// <para>A merge never deletes — removal stays the snapshot's job (and a stop's own delta), so a
    /// listing that raced a spawn cannot make a live agent disappear.</para>
    /// </summary>
    /// <summary>The authoritative listing, through the test seam when one is set.</summary>
    private Task<IReadOnlyList<Proto.AgentInfo>> ListAgentsFromDaemonAsync(CancellationToken ct) =>
        AgentListOverride is { } listing ? listing(ct) : _client.ListAgentsAsync(ct);

    /// <returns>True when something changed, so the caller can raise <see cref="Changed"/>.</returns>
    private bool MergeAgentListing(IReadOnlyList<Proto.AgentInfo> listed)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var a in listed)
            {
                if (string.IsNullOrEmpty(a.AgentId))
                {
                    continue;
                }

                var kind = string.IsNullOrEmpty(a.AgentKind) ? a.AgentId : a.AgentKind;
                var role = a.Role ?? string.Empty;

                if (!_agents.TryGetValue(a.AgentId, out var existing))
                {
                    _agents[a.AgentId] = MapInfo(a);
                    changed = true;
                    continue;
                }

                if (string.Equals(existing.Name, kind, StringComparison.Ordinal)
                    && string.Equals(existing.Role, role, StringComparison.Ordinal))
                {
                    continue;
                }

                _agents[a.AgentId] = existing with { Name = kind, Role = role };
                changed = true;
            }

            var coordinator = _agents.Values
                .FirstOrDefault(a => a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator)?.AgentId;
            if (coordinator is not null && !string.Equals(coordinator, _coordinatorAgentId, StringComparison.Ordinal))
            {
                _coordinatorAgentId = coordinator;
                changed = true;
            }
        }

        return changed;
    }

    private static AgentInfo MapInfo(Proto.AgentInfo a) =>
        new(a.AgentId, string.IsNullOrEmpty(a.AgentKind) ? a.AgentId : a.AgentKind,
            $"agent/{a.AgentId}", MapState(a.State), string.Empty, DateTimeOffset.UtcNow,
            Role: a.Role ?? string.Empty);

    internal static AgentLifecycleState MapState(string? state)
    {
        if (Enum.TryParse<AgentLifecycleState>(state, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // The daemon's wire vocabulary (G-14, free-form strings) is wider than the enum names:
        // "Starting" is a spawn record whose jail is still provisioning, and "Stopped" is a session
        // the daemon removed. "Stopped" falling into the Working default was the ghost-coordinator
        // field bug (2026-07-22): a torn-down coordinator projected as alive forever, so the surface
        // spun on its startup loader and Stop looked like a no-op.
        return state switch
        {
            "Starting" => AgentLifecycleState.Provisioning,
            "Stopped" => AgentLifecycleState.TornDown,
            _ => AgentLifecycleState.Working,
        };
    }

    // The daemon sends the Core ConversationRole enum name as a free-form string (G-14).
    private static ChatLineKind MapChatKind(string? role) => role switch
    {
        "Human" => ChatLineKind.Human,
        "Coordinator" => ChatLineKind.Coordinator,
        "ToolCall" => ChatLineKind.ToolCall,
        "PlanCard" => ChatLineKind.PlanCard,
        _ => ChatLineKind.SystemLine,
    };

    // ---- IAgentService (LIVE) -------------------------------------------

    public IReadOnlyList<AgentInfo> ListAgents()
    {
        lock (_gate)
        {
            return _agents.Values.OrderByDescending(a => a.SpawnedAt).ToArray();
        }
    }

    public event Action<AgentEvent>? EventReceived;

    public async Task EndAgentAsync(string agentId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var outcome = await _client.StopAgentAsync(agentId, cts.Token).ConfigureAwait(false);
            PersistHarvestedLogin(outcome);
        }
        catch (Exception) { /* daemon unreachable — surfaced via ConnectionState, not an app crash. */ }
    }

    /// <summary>
    /// Pulls the CURRENT login state of every live agent into the host OS keychain, without stopping
    /// anything. Harvest otherwise only happens on an explicit StopAgent, so a daemon shutdown / VM
    /// stop / crash lost the login entirely and the user had to sign in again on every launch.
    ///
    /// <para>Driven from exactly two places, and both are load-bearing: <see cref="LoginHarvestPumpAsync"/>
    /// (periodically, while agents run) and <see cref="Dispose"/> (once more at app close). This method
    /// existed with NO callers at all, which made the whole round-trip inert outside an in-app Stop —
    /// so a change that removes either caller is removing the fix, not tidying it.</para>
    ///
    /// <para>Best-effort by design — a daemon that is down or an agent that has not signed in yet
    /// simply yields nothing, and an empty harvest never clobbers a good keychain entry
    /// (<see cref="PersistHarvestedLogin"/> ignores it).</para>
    /// </summary>
    public async Task PersistLiveAgentLoginsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Proto.AgentInfo> listed;
        try
        {
            listed = await ListAgentsFromDaemonAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // daemon unreachable — surfaced via ConnectionState, never an app crash.
        }

        // ISSUES-LOG #19: this sweep already holds the authoritative kind/role for every live agent and
        // used to keep only the ids. Folding it into the projection first makes this the projection's
        // standing repair pass — a coordinator that lost its role on a fabricated placeholder is a
        // coordinator again within the sweep interval, with no extra RPC, off the very answer that was
        // demonstrably correct throughout the outage.
        if (MergeAgentListing(listed))
        {
            RaiseIsolated(() => Changed?.Invoke());
        }

        var agentIds = listed
            .Select(a => a.AgentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        foreach (var agentId in agentIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                PersistHarvestedLogin(await _client.HarvestAgentCredentialsAsync(agentId, ct).ConfigureAwait(false));
            }
            catch (Exception)
            {
                // One agent failing to harvest must never stop the others from being persisted.
            }
        }
    }

    /// <summary>Folds the login-state files a stop harvested from the jail into the host OS
    /// keychain (<c>cli_login_&lt;kind&gt;</c>) — the durable half of the login round-trip; the
    /// next spawn of this kind restores them so the CLI boots signed in.</summary>
    private void PersistHarvestedLogin(AgentStopOutcome outcome)
    {
        PersistHarvestedSettings(outcome);

        if (outcome.CliCredentials.Count == 0 || string.IsNullOrWhiteSpace(outcome.AgentKind))
        {
            return;
        }

        var keystoreKey = CliLoginVault.KeystoreKeyFor(outcome.AgentKind);
        if (CliLoginVault.MergeAndSerialize(_keystoreLookup(keystoreKey), outcome.CliCredentials) is { } vault)
        {
            _keystoreSave(keystoreKey, vault);
        }
    }

    /// <summary>
    /// Folds the settings a harvest returned into <b>that agent's own repository's</b> store, so the
    /// commands the user approved in this session are already approved in the next agent.
    ///
    /// <para>The repo comes from the outcome, never from whichever repository happens to be open: the
    /// harvest sweep walks every agent on the daemon, so filing by the open repo would put one
    /// repository's approved-command list under another's name — the precise cross-repo leak the
    /// per-repo scope exists to prevent. A harvest with no repo handle is dropped rather than filed
    /// under a blank scope.</para>
    ///
    /// <para>The daemon already refused to harvest anything from an unattended or untrusted session,
    /// so an empty list here is the normal, correct outcome for those and simply writes nothing.</para>
    /// </summary>
    private void PersistHarvestedSettings(AgentStopOutcome outcome)
    {
        if (outcome.CliSettings.Count == 0
            || string.IsNullOrWhiteSpace(outcome.AgentKind)
            || string.IsNullOrWhiteSpace(outcome.RepoHandle))
        {
            return;
        }

        _cliSettings.Save(outcome.RepoHandle, outcome.AgentKind, outcome.CliSettings);
    }

    /// <summary>Whether ANY credential is stored for this CLI — a BYOK key for its provider, a custom
    /// llm_env_* key, or a harvested interactive login. False means the CLI will ask the human to sign
    /// in inside its terminal after the spawn; the start surface says so up front rather than leaving
    /// the first-run coordinator looking stuck at a login prompt nobody mentioned.</summary>
    public bool HasStoredCredentialFor(InstalledCliOption cli)
    {
        if (ApiKeyProviderMap.ProviderForEnvVar(cli.ApiKeyEnvVar) is { } provider
            && !string.IsNullOrEmpty(_keystoreLookup(ApiKeyProviderMap.KeystoreKeyFor(provider))))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(_keystoreLookup(CliLoginVault.KeystoreKeyPrefix + cli.Id)))
        {
            return true;
        }

        return _keystoreList(ApiKeyProviderMap.CustomEnvKeyPrefix).Count > 0;
    }

    /// <summary>Human per-agent pause over the PauseAgent RPC (docker pause on the jail). A refusal —
    /// no live jail, kill switch engaged — is thrown with the daemon's reason; an old daemon that
    /// predates the RPC answers Unimplemented, mapped to an honest sentence (the GetDaemonInfo
    /// convention). The state flip arrives back on the agent-event stream; nothing is set optimistically.</summary>
    public async Task PauseAgentAsync(string agentId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Proto.PauseAgentResponse response;
        try
        {
            response = await _client.PauseAgentAsync(agentId, cts.Token).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            throw new InvalidOperationException("Can't pause — this daemon predates per-agent pause; update it.");
        }

        if (!response.Paused)
        {
            throw new InvalidOperationException($"Can't pause — {response.Reason}.");
        }
    }

    /// <summary>Human per-agent resume over the UnpauseAgent RPC. The daemon refuses (self-clearingly)
    /// while the keep-alive rebase briefly holds the jail — the reason says to try again in a moment.</summary>
    public async Task ResumeAgentAsync(string agentId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Proto.UnpauseAgentResponse response;
        try
        {
            response = await _client.UnpauseAgentAsync(agentId, cts.Token).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            throw new InvalidOperationException("Can't resume — this daemon predates per-agent pause; update it.");
        }

        if (!response.Unpaused)
        {
            throw new InvalidOperationException($"Can't resume — {response.Reason}.");
        }
    }

    /// <summary>How long a one-shot prompt delivery may take end to end (attach + write + close).</summary>
    internal static TimeSpan SendPromptBudget { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Test seam: replaces the attach call the prompt delivery writes through.</summary>
    internal Func<CancellationToken, Grpc.Core.AsyncDuplexStreamingCall<Proto.TerminalInput, Proto.TerminalOutput>>?
        AttachTerminalOverride
    { get; set; }

    /// <summary>
    /// Delivers the agent document's composer prompt into the agent's LIVE PTY — a short-lived attach
    /// on the same <c>TerminalService.Attach</c> bidi stream the terminal pane uses (a bound session
    /// multiplexes subscribers, so this never steals the pane's attach): frame 1 selects the agent
    /// (raw mode), frame 2 writes the prompt body, frame 3 writes the CR that submits it, then the write
    /// side completes and the call closes. A managed worker's daemon-side input lock arrives as
    /// <c>PermissionDenied</c> and is PROPAGATED — the caller renders the refusal; for most of this
    /// method's life it was a hardcoded no-op that reported success and typed nothing.
    ///
    /// <para><b>Why the CR is a frame of its own, with a wait in front of it (defect J2).</b> This wrote
    /// <c>prompt + "\r"</c> in one frame, so the daemon wrote body and terminator to the PTY in one go
    /// and the CLI — which classifies input as typed or pasted by the read burst it arrives in — took the
    /// CR as pasted content rather than Enter. Short prompts submitted, realistic ones silently did not.
    /// Splitting the frames is necessary but not sufficient: two frames written back to back are still
    /// coalesced into one read, measured. The wait is what puts them in separate reads. Unlike the
    /// daemon's own path this side cannot watch for the CLI's echo, so it uses the fixed
    /// <see cref="TerminalSubmit.TerminatorSeparation"/> fallback. See
    /// <c>docs/design/coordinator-phase-3-decisions.md</c> §17.8.</para>
    /// </summary>
    public async Task SendPromptAsync(string agentId, string prompt)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrEmpty(prompt))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(SendPromptBudget);

        var attach = AttachTerminalOverride ?? (token => _client.AttachTerminal(token));
        using var call = attach(cts.Token);

        // Drain responses in the background so a server that pushes scrollback before our write can't
        // fill the window and stall the request stream.
        var drain = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in call.ResponseStream.ReadAllAsync(cts.Token).ConfigureAwait(false)) { }
            }
            catch { /* the drain ends when the call does; its errors surface on the write below */ }
        });

        try
        {
            if (!TerminalSubmit.TryEncodeSubmission(prompt, out var body, out var terminator))
            {
                return;
            }

            await call.RequestStream.WriteAsync(new Proto.TerminalInput { AgentId = agentId })
                .ConfigureAwait(false);
            await call.RequestStream.WriteAsync(new Proto.TerminalInput
            {
                Data = Google.Protobuf.ByteString.CopyFrom(body),
            }).ConfigureAwait(false);

            // The whole point of the split: without this the two frames reach the PTY back to back, the
            // CLI reads them as one burst, and the CR is absorbed into the message instead of submitting
            // it — which is the defect, not a nicety.
            await Task.Delay(TerminalSubmit.TerminatorSeparation, cts.Token).ConfigureAwait(false);

            await call.RequestStream.WriteAsync(new Proto.TerminalInput
            {
                Data = Google.Protobuf.ByteString.CopyFrom(terminator),
            }).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
        {
            throw new InvalidOperationException(
                "Can't send — the daemon has this terminal locked (a managed worker's input is read-only).");
        }
        finally
        {
            cts.Cancel();
            try { await drain.ConfigureAwait(false); } catch { /* drained */ }
        }
    }
    public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
    public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
    public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
    public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();

    // ---- IMergeQueueService (LIVE via StreamQueue) ----------------------

    public string MainSha { get { lock (_gate) return _mainSha; } }

    public IReadOnlyList<QueueEntry> GetQueue()
    {
        lock (_gate)
        {
            return _queue.ToArray();
        }
    }

    public bool CanMerge(string agentId, out string reason)
    {
        lock (_gate)
        {
            if (_gate_.TryGetValue(agentId, out var g))
            {
                reason = g.Reason;
                return g.CanMerge;
            }
        }

        reason = "not in the merge queue";
        return false;
    }

    /// <summary>
    /// Asks the daemon to verify this agent's branch now — the missing rung that left the whole
    /// verification mechanism without a production caller.
    ///
    /// <para><b>Everything that decides anything lives on the daemon.</b> This method resolves the active
    /// repo handle and makes one RPC; it holds no policy, no state machine, and no retry. The daemon's
    /// <c>MergeQueue.RunVerificationAsync</c> owns the Verifying transition, runs the test command in the
    /// <i>agent's own jail</i> (host execution is a rejection trigger), writes the immutable record, and
    /// lands the branch on <c>Verified</c> or back on <c>Working</c>. The queue stream then republishes
    /// that state, so this method deliberately mutates no local projection.</para>
    ///
    /// <para>A refusal the run never started with — no live jail, no configured test command, a jail
    /// missing the declared toolchain — arrives as gRPC <c>FailedPrecondition</c> and is returned as
    /// <c>Ran: false</c> with the daemon's reason verbatim. A suite that genuinely failed is
    /// <c>Ran: true, Passed: false</c>, which is a result and not an error.</para>
    /// </summary>
    public async Task<VerificationOutcome> RunVerificationAsync(string agentId)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return new VerificationOutcome(
                Ran: false, Passed: false,
                Reason: "Can't verify — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            // Verification builds a toolchain image on first use and then runs the repo's whole test
            // command in the jail, so it is minutes-scale work — the per-call deadline has to allow for
            // that. The default RPC deadline would abort a perfectly healthy first run.
            var response = await _client
                .RunVerificationAsync(repoHandle!, agentId, cts.Token, VerificationDeadline)
                .ConfigureAwait(false);
            return new VerificationOutcome(
                Ran: true,
                Passed: response.Passed,
                Reason: response.Passed
                    ? $"verified against main@{Short(response.MainSha)}"
                    : $"tests failed — {response.ResolvedCommand}");
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
        {
            // The daemon's typed refusal: quotable, and specifically NOT a test failure.
            return new VerificationOutcome(Ran: false, Passed: false, Reason: $"Can't verify — {ex.Status.Detail}");
        }
        catch (Grpc.Core.RpcException ex)
        {
            return new VerificationOutcome(
                Ran: false, Passed: false,
                Reason: $"Can't verify — the daemon didn't answer ({ex.StatusCode}).");
        }
    }

    /// <summary>A verification runs the repo's full test command in a jail, after possibly building its
    /// toolchain image; it is the longest-running RPC the surface issues.</summary>
    private static readonly TimeSpan VerificationDeadline = TimeSpan.FromMinutes(30);

    /// <summary>
    /// H4 — <b>reads</b> the last verification's output. It runs nothing: this is the whole reason it is a
    /// separate call from <see cref="RunVerificationAsync"/>. Before it existed, the only way for a human
    /// to find out why a branch had gone red was to press Verify again, which spends minutes of real test
    /// time in a jail and can legitimately answer differently — so the surface charged the human a second
    /// run for information the daemon already had on disk.
    ///
    /// <para>The three answers the daemon keeps apart are kept apart here too: no record at all, the log,
    /// and a record whose artifact could not be read. A daemon we could not reach is a FOURTH answer and is
    /// reported as such — never as "no record", which is a claim about the entry rather than about the
    /// call.</para>
    ///
    /// <para>The text is jail-produced, so it goes through <see cref="JailText.Sanitize"/> here rather than
    /// at each surface: sanitizing at the projection boundary is what makes it impossible for a consumer to
    /// forget.</para>
    /// </summary>
    public async Task<VerificationLog> GetVerificationLogAsync(string agentId)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return VerificationLog.Unreachable("no repository is active for agents yet");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var response = await _client
                .GetVerificationLogAsync(repoHandle!, agentId, cts.Token)
                .ConfigureAwait(false);

            if (!response.HasRecord)
            {
                return new VerificationLog(
                    HasRecord: false, Passed: false, ResolvedCommand: "", MainSha: "", When: null,
                    Text: "", Truncated: false, UnavailableReason: "");
            }

            return new VerificationLog(
                HasRecord: true,
                Passed: response.Passed,
                ResolvedCommand: response.ResolvedCommand ?? string.Empty,
                MainSha: response.MainSha ?? string.Empty,
                When: ParseTimestamp(response.When),
                Text: JailText.Sanitize(response.Log),
                Truncated: response.Truncated,
                UnavailableReason: response.UnavailableReason ?? string.Empty);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return VerificationLog.Unreachable($"the daemon didn't answer ({ex.StatusCode})");
        }
    }

    /// <summary>
    /// The daemon's ISO-8601 round-trip ("O") timestamps, or null when it sent none. Null rather than a
    /// sentinel date: "the daemon did not say when" and "this happened at the epoch" are different facts,
    /// and only one of them should ever reach a surface that ages a verdict.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed : null;

    /// <summary>
    /// proto3's empty string mapped to "the daemon said nothing". Used for the approved-plan fields,
    /// where an empty approach and an absent approval must not render the same: one is a panel with a
    /// blank paragraph in it, the other is no panel.
    /// </summary>
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 8 ? sha[..8] : sha);

    /// <summary>
    /// The human foreground merge — the whole RT-D1 conversation (P2-10 §3.7), driven from the Merge
    /// button: <c>BeginMerge</c> (the daemon takes the repo's one lease and enforces <c>CanMerge</c> under
    /// it) → <b>the real host-side <c>git merge --ff-only</c> on the user's own checkout</b> →
    /// <c>ConfirmMerge</c> with the sha main ACTUALLY moved to, or <c>AbandonMerge</c> when nothing landed.
    ///
    /// <para><b>The middle leg used to be missing.</b> This method took the lease and then went straight to
    /// <c>ConfirmMerge</c>, passing the cached <see cref="MainSha"/> projection — the PRE-merge value — as
    /// the post-merge sha. Pressing Merge therefore consumed the repo's merge lease, walked the branch to
    /// the terminal <c>Merged</c> state, and wrote the RT-D1 idempotency record asserting the merge had
    /// landed, while <c>refs/heads/main</c> had not moved by a single commit. The agent's work was dropped
    /// from the queue without ever reaching main, and the boot reconcile would afterwards read the
    /// confirmed lease as proof of a merge that does not exist. A merge queue whose merge step is absent
    /// does not fail safe — it fails silently, and the queue is the record everything downstream trusts.</para>
    ///
    /// <para>The human still drives: this runs only from the Merge button, and there is still no
    /// auto-merge RPC. What is connected here is the drive, not an automation of it.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The merge did not happen; the message is the reason,
    /// already phrased for display. Queue state is unchanged in every one of those cases.</exception>
    /// <returns>What the merge did — the origin whose transport landed it, and the sha main really moved
    /// to. Only a merge that reached RT-D1 step 3 returns; every other path throws.</returns>
    public async Task<MergeOutcome> ConfirmMergeAsync(string agentId)
    {
        string? repoHandle;
        string? repoPath;
        string syncRemote;
        MergeEntryOrigin origin;
        lock (_gate)
        {
            repoHandle = _repoHandle;
            repoPath = _repoLocalPath;
            syncRemote = _syncRemoteName;
            origin = _origins.TryGetValue(agentId, out var o) ? o : MergeEntryOrigin.Local;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't merge — no repository is active for agents yet.");
        }

        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(syncRemote))
        {
            // Refuse rather than take the lease: a merge we cannot perform must not consume the repo's
            // one outstanding merge, and must certainly not be recorded as having happened.
            throw new InvalidOperationException(
                "Can't merge — this repository isn't bound to a local checkout yet. Reopen it so Mainguard "
                + "can register the sync remote, then merge.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        // RT-D1 step 1 — the daemon's lease. BeginMerge is also where CanMerge is enforced, UNDER the
        // lease (MG-11), so a refusal here is the gate speaking and nothing has been touched.
        var begun = await _client.BeginMergeAsync(repoHandle!, agentId, cts.Token).ConfigureAwait(false);
        if (!begun.Granted)
        {
            throw new InvalidOperationException($"Can't merge — {begun.Reason}.");
        }

        // BOTH halves of the identity come from the grant, not from our own queue projection: the
        // projection is a stream snapshot and may be a revision behind the main — and the branch tip — the
        // daemon just authorized against. K3: the branch sha is what makes `agent/<id>` in this checkout an
        // identity rather than a name, and it is what ConfirmMerge compares the reported post-merge main
        // against.
        var lease = new Mainguard.Git.Models.MergeLeaseRow
        {
            RepoHash = repoHandle!,
            LeaseId = begun.LeaseId,
            AgentId = agentId,
            ExpectedMainSha = begun.ExpectedMainSha ?? string.Empty,
            ExpectedBranchSha = begun.ExpectedBranchSha ?? string.Empty,
            MainBranch = MainBranchName,
        };

        var mergeRequest = new Mainguard.Agents.Services.ForegroundMergeRequest(
            RepoPath: repoPath!,
            RepoHash: repoHandle!,
            AgentId: agentId,
            ExpectedMainSha: lease.ExpectedMainSha,
            MainBranch: MainBranchName,
            ExpectedBranchSha: lease.ExpectedBranchSha);

        Mainguard.Agents.Services.ForegroundMergeResult result;
        try
        {
            // RT-D1 step 2 — the merge itself. WHICH transport is the entry's origin's business; the lease
            // (step 1) and the recording (step 3) are identical for both, which is the whole point: the
            // daemon arbitrates one merge per repo regardless of where the merge is performed.
            //
            // The lease is the daemon's, so neither executor is built with a lease store: a second store
            // would be a second arbiter of "one merge per repo" (MG-23).
            result = origin == MergeEntryOrigin.External

                // P2-12 — an upstream pull request merges on its HOST, then this checkout is brought up to
                // date with the merge the host performed. A local fast-forward would "succeed" here (the
                // entry's agent/pr-<n> branch really is in the mirror) while leaving the pull request open
                // upstream: main advances, the queue records a merge, and the two records disagree forever.
                ? await CreateExternalMergeExecutor(syncRemote)
                    .MergeExternalPrAsync(mergeRequest, lease, cts.Token).ConfigureAwait(false)

                // Local — the real git merge --ff-only on the user's own checkout. Synchronous git work,
                // moved off the UI thread.
                : await Task.Run(
                    () => CreateMergeExecutor(syncRemote).PerformJournaledMerge(mergeRequest, lease),
                    cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The merge threw rather than refusing. Hand the lease back before surfacing it, or this repo
            // stays unmergeable until the daemon restarts.
            await TryAbandonAsync(repoHandle!, agentId, begun.LeaseId, ex.Message).ConfigureAwait(false);
            throw;
        }

        if (!result.Merged || string.IsNullOrEmpty(result.NewMainSha))
        {
            // NOTHING LANDED. The one thing that must not happen here is ConfirmMerge: it would move the
            // branch to Merged and fire NotifyMainMoved, telling every other agent in the repo that main
            // advanced — on the strength of a merge that did not occur. Release, and say why.
            var reason = result.Reason ?? "the merge did not complete";
            await TryAbandonAsync(repoHandle!, agentId, begun.LeaseId, reason).ConfigureAwait(false);
            throw new InvalidOperationException($"Can't merge — {reason}.");
        }

        // RT-D1 step 3 — record the outcome against the sha main REALLY moved to. The daemon re-checks the
        // gate and the CAS under its queue lock before it writes anything (MG-11), so a race lost between
        // the two legs is refused there rather than papered over here.
        await _client.ConfirmMergeAsync(repoHandle!, agentId, begun.LeaseId, result.NewMainSha!, cts.Token)
            .ConfigureAwait(false);

        // The origin that actually ran the merge, reported with the sha main really moved to. It is the
        // SAME `origin` the transport was chosen by above — read once, under the lock — so what the human
        // is told cannot describe a transport other than the one that ran.
        return new MergeOutcome(origin, agentId, MainBranchName, result.NewMainSha!);
    }

    /// <summary>
    /// The host-side merge leg, bound to this repo's SC-2 sync remote and the app's T-19 journal.
    /// Built per merge because the sync-remote binding is per active repo.
    /// </summary>
    private Mainguard.Agents.Services.IJournaledMergeExecutor CreateMergeExecutor(string syncRemoteName)
        => new Mainguard.Agents.Services.ForegroundMergeService(
            resolveSyncRemote: _ => new Mainguard.Agents.Agents.SyncRemote(syncRemoteName, string.Empty),
            journal: _journalFactory(),
            leases: null); // the lease is the daemon's; see the ctor doc on why this must not be a store.

    /// <summary>
    /// The P2-12 external leg: the same shape as <see cref="CreateMergeExecutor"/>, built per merge for the
    /// same reason (the sync-remote binding is per active repo), holding no lease for the same reason
    /// (there is one <c>IMergeLeaseStore</c> and it is the daemon's, MG-23). The sync remote is still
    /// needed here — it is where the VERIFIED pull-request head is read from, which is what the host merge
    /// is compare-and-swapped against.
    /// </summary>
    private Mainguard.Agents.Services.IExternalPrMergeExecutor CreateExternalMergeExecutor(string syncRemoteName)
        => new Mainguard.Agents.Services.ExternalPrMergeService(
            resolveSyncRemote: _ => new Mainguard.Agents.Agents.SyncRemote(syncRemoteName, string.Empty),
            host: _hostPullRequests.Value,
            journal: _journalFactory());

    /// <summary>
    /// Hands a granted lease back after a merge that did not land. Best-effort by construction: it is the
    /// cleanup arm of a failure the caller is already reporting, so a transport fault here must not replace
    /// that reason with a worse one. A lease that survives this is still swept by the RT-D1 boot reconcile.
    /// </summary>
    private async Task TryAbandonAsync(string repoHandle, string agentId, string leaseId, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _client.AbandonMergeAsync(repoHandle, agentId, leaseId, reason, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Daemon unreachable mid-merge — surfaced through ConnectionState like every other call.
        }
    }

    /// <summary>P2-11 step 4 — acknowledge one flagged item on the DAEMON, which is where the merge gate
    /// reads acknowledgments from. This was <c>Task.CompletedTask</c>: the cockpit's acks lived only in its
    /// own in-process store, so nothing a human acknowledged ever reached the gate. No active repo → nothing
    /// to acknowledge against; a transport failure surfaces through ConnectionState like every other call.
    /// <para>The seam-shaped overload returns nothing, so it cannot tell a caller whether the gate actually
    /// moved — see <see cref="AcknowledgeFlaggedChangeReportedAsync"/>, which both surfaces' acks now run
    /// through. One path, one RPC; only the reporting differs.</para></summary>
    public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId)
        => AcknowledgeFlaggedChangeReportedAsync(agentId, itemId, CancellationToken.None);

    /// <summary>
    /// The human drops a queue entry. Every part of what makes this mean anything — the terminal
    /// transition, the persisted record, the audit event, the refusal while a merge is in flight — is
    /// daemon-side; this method only carries the request and reports the daemon's answer.
    ///
    /// <para><b>A refusal is thrown, never swallowed.</b> The daemon answers a refused discard with
    /// <c>discarded=false</c> and a reason (already terminal, unknown entry, merge in progress), which is
    /// a successful RPC — so a caller that only checked for exceptions would report a removal that did
    /// not happen, and the entry would reappear on the next queue snapshot with nothing said. That is the
    /// same "nothing visibly happened" failure <see cref="MergeActionRunner"/> exists to prevent.</para>
    /// </summary>
    public async Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't discard — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var response = await _client
            .DiscardEntryAsync(repoHandle!, agentId, reason ?? string.Empty, cts.Token)
            .ConfigureAwait(false);

        if (!response.Discarded)
        {
            throw new InvalidOperationException($"Can't discard — {response.Reason}.");
        }

        DateTimeOffset? at = DateTimeOffset.TryParse(
            response.DiscardedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

        return new QueueEntryDiscardOutcome(agentId, response.DiscardedBy ?? "", at);
    }

    /// <summary>The cockpit's Bring local: fetch <c>agent/&lt;id&gt;</c> from the sync remote into the
    /// user's checkout as a local branch (journaled, non-forced — see <see cref="Mainguard.Agents.Services.BringLocalService"/>).
    /// Refuses when no repo binding exists, in the same wording family as <see cref="ConfirmMergeAsync"/>.</summary>
    public async Task<Mainguard.Agents.Services.BringLocalResult> BringBranchLocalAsync(
        string agentId, CancellationToken ct)
    {
        string? repoPath;
        string syncRemote;
        lock (_gate)
        {
            repoPath = _repoLocalPath;
            syncRemote = _syncRemoteName;
        }

        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(syncRemote))
        {
            return Mainguard.Agents.Services.BringLocalResult.Refused(
                "this repository isn't bound to a local checkout yet — reopen it so Mainguard can register the sync remote");
        }

        var service = new Mainguard.Agents.Services.BringLocalService(_journalFactory());
        return await Task.Run(() => service.BringLocal(repoPath!, syncRemote, agentId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Rejects a verified entry in review — same refusal discipline as
    /// <see cref="DiscardEntryAsync"/>: the daemon's "no" is thrown with its reason, never swallowed.</summary>
    public async Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't reject — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var response = await _client
            .RejectEntryAsync(repoHandle!, agentId, reason ?? string.Empty, cts.Token)
            .ConfigureAwait(false);

        if (!response.Rejected)
        {
            throw new InvalidOperationException($"Can't reject — {response.Reason}.");
        }

        DateTimeOffset? at = DateTimeOffset.TryParse(
            response.RejectedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

        return new QueueEntryRejectOutcome(agentId, response.RejectedBy ?? "", at);
    }

    /// <summary>
    /// Clears a <c>Verifying</c> entry with no run behind it. Refusals — chiefly "a verification is
    /// running for this entry right now" — are thrown for the same reason as above: they are the answer
    /// the human asked for, and a silently-ignored one is indistinguishable from a button that does
    /// nothing.
    /// </summary>
    public async Task ClearStalledVerificationAsync(string agentId)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't clear this verification — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var response = await _client
            .ClearStalledVerificationAsync(repoHandle!, agentId, cts.Token)
            .ConfigureAwait(false);

        if (!response.Cleared)
        {
            throw new InvalidOperationException($"Can't clear this verification — {response.Reason}.");
        }
    }

    /// <summary>
    /// Hands a parked rebase conflict back to the worker that produced half of it: the daemon unpauses the
    /// jail and delivers an instruction through its own prompt path.
    ///
    /// <para><b>This adapter asserts nothing</b>, exactly like <see cref="ResumeEntryAsync"/>. Whether a
    /// rebase is really parked, whether it is still in progress, whether a jail exists and whether the
    /// instruction was actually submitted to the CLI are all daemon-side facts; a client that guessed at
    /// them would be building the control in the UI layer again.</para>
    ///
    /// <para><b>A refusal is thrown, never swallowed</b> — the daemon answers a decline with
    /// <c>handed_back=false</c> and a reason on an otherwise successful RPC, so "no exception" is not
    /// evidence an agent woke up.</para>
    /// </summary>
    public async Task ResolveConflictWithAgentAsync(string agentId)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't hand this back — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var response = await _client
            .ResolveConflictWithAgentAsync(repoHandle!, agentId, cts.Token)
            .ConfigureAwait(false);

        if (!response.HandedBack)
        {
            throw new InvalidOperationException($"Can't hand this back — {response.Reason}.");
        }
    }

    /// <summary>Aborts a parked rebase. Same refusal discipline: the daemon's "no" carries its reason and
    /// leaves the worktree exactly as it was.</summary>
    public async Task AbortRebaseAsync(string agentId)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't abort this rebase — no repository is active for agents yet.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var response = await _client
            .AbortRebaseAsync(repoHandle!, agentId, cts.Token)
            .ConfigureAwait(false);

        if (!response.Aborted)
        {
            throw new InvalidOperationException($"Can't abort this rebase — {response.Reason}.");
        }
    }

    /// <summary>
    /// Resumes a stranded entry: asks the daemon to spawn a jail onto the id the entry ALREADY has, with
    /// the worktree standing on its existing <c>agent/&lt;id&gt;</c> branch.
    ///
    /// <para><b>This adapter asserts nothing.</b> It supplies the repo handle, the CLI the human picked and
    /// that CLI's credentials, and the daemon answers. Whether the entry exists, whether its branch
    /// survives, whether the id already has a session, whether a merge is open — none of those questions
    /// are asked here, because the answers live in the daemon's own state and a client that guessed at them
    /// would be building the control in the UI layer again.</para>
    ///
    /// <para><b>A refusal is thrown, never swallowed</b>, for the same reason a discard's is: the daemon
    /// answers a refused resume with <c>resumed=false</c> and a reason on an otherwise successful RPC, so
    /// "no exception" is not evidence a jail exists. <see cref="MergeActionRunner"/> turns the throw into a
    /// warning toast carrying the daemon's words.</para>
    /// </summary>
    public async Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "Can't resume — no repository is active for agents yet.");
        }

        if (string.IsNullOrWhiteSpace(agentKind))
        {
            // Not a security check (the daemon rejects a blank kind too) — it is the difference between
            // naming what the human has to choose and reporting gRPC's INVALID_ARGUMENT at them.
            throw new InvalidOperationException(
                "Can't resume — pick which agent CLI should take over this branch first.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        // The resumed jail needs the same credentials a fresh spawn of this CLI would get: its BYOK key
        // under the adapter's DECLARED env-var name, and the saved login state from the host OS keychain
        // (the jail's $HOME is tmpfs, so without it the human signs in again). The adapter is looked up
        // rather than assumed — a CLI that declares no key variable authenticates interactively and no key
        // may travel for it.
        var installed = await ListInstalledClisAsync(cts.Token).ConfigureAwait(false);
        var cli = installed.FirstOrDefault(c => string.Equals(c.Id, agentKind, StringComparison.Ordinal));
        var provider = ApiKeyProviderMap.ProviderForEnvVar(cli?.ApiKeyEnvVar ?? string.Empty);
        var key = provider is null ? null : _keystoreLookup(ApiKeyProviderMap.KeystoreKeyFor(provider));
        var savedLogin = CliLoginVault.Parse(_keystoreLookup(CliLoginVault.KeystoreKeyFor(agentKind)));

        // Same provision chain as a fresh spawn (toolchain build included), so the same silence-bounded
        // wait rather than a flat deadline that a cold first build outruns.
        var response = await SpawnUnderWatchdogAsync(
            (token, deadline) => _client.ResumeAgentAsync(
                repoHandle!, agentId, agentKind, key ?? string.Empty, token,
                deadline: deadline,
                extraEnv: CollectCustomEnvKeys(),
                cliCredentials: savedLogin.Count > 0 ? savedLogin : null),
            cts.Token).ConfigureAwait(false);

        if (!response.Resumed)
        {
            throw new InvalidOperationException($"Can't resume — {response.Reason}.");
        }

        var state = Enum.TryParse<WorkerMergeState>(response.State, ignoreCase: true, out var s)
            ? s : WorkerMergeState.Working;
        Changed?.Invoke();
        return new QueueEntryResumeOutcome(
            response.AgentId, response.Branch, state, response.ClearedStalledVerification);
    }

    /// <summary>
    /// Whether an acknowledgment made right now could actually reach the daemon's gate. There is exactly
    /// one way it cannot: no repo is active, so there is no handle to address the RPC with and
    /// <see cref="AcknowledgeFlaggedChangeReportedAsync"/> has nothing to call. A surface that renders an
    /// enabled acknowledge control in that state is claiming to have unblocked a merge it never touched.
    /// </summary>
    public bool CanAcknowledgeFlaggedChange(out string reason)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            reason = "no repository is open in the daemon — acknowledging here would clear nothing.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// The same acknowledgment RPC, with the daemon's OWN answer handed back: whether the gate recorded
    /// the acknowledgment, whether the merge is now permitted, and the gate's reason verbatim. The
    /// merge-blocking gate is daemon-side, so "the call was made" is not evidence the item was cleared —
    /// an id the gate does not recognise is accepted by the transport and clears nothing.
    /// </summary>
    public async Task<FlaggedAckOutcome> AcknowledgeFlaggedChangeReportedAsync(
        string agentId, string itemId, CancellationToken ct)
    {
        if (!CanAcknowledgeFlaggedChange(out var unavailable))
        {
            return FlaggedAckOutcome.Refused(unavailable);
        }

        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            var response = await _client
                .AcknowledgeFlaggedChangeAsync(repoHandle!, agentId, itemId, cts.Token)
                .ConfigureAwait(false);
            return new FlaggedAckOutcome(response.Acknowledged, response.CanMerge, response.Reason ?? "");
        }
        catch (Exception ex)
        {
            // The daemon never heard it. Reported as a refusal rather than swallowed, so the surface says
            // so instead of drawing a checkmark over a gate that is still shut.
            return FlaggedAckOutcome.Refused($"the daemon did not record this acknowledgment — {ex.Message}");
        }
    }

    /// <summary>P2-47 #7: fetch the agent-branch-vs-main diff (over the new GetMergeDiff RPC) so the review
    /// cockpit can build its <c>ReviewCockpitContext.MergeDiff</c> — which the queue stream doesn't carry.
    /// Returns null when no repo is active or the daemon is unreachable (the caller degrades gracefully).</summary>
    public async Task<MergeDiffResult?> GetMergeDiffAsync(string agentId, CancellationToken ct)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return null;
        }

        try
        {
            var (branch, _, files) = await _client.GetMergeDiffAsync(repoHandle, agentId, ct).ConfigureAwait(false);
            return new MergeDiffResult(branch, files);
        }
        catch (Exception)
        {
            // No such branch / daemon unreachable — surfaced via ConnectionState; the cockpit stays empty.
            return null;
        }
    }

    // ---- ICoordinatorService (LIVE via StreamConversation + StreamPlans) -

    public IReadOnlyList<ChatLine> GetTranscript()
    {
        lock (_gate)
        {
            return _transcript.ToArray();
        }
    }

    public IReadOnlyList<TaskPlan> GetPendingPlans()
    {
        lock (_gate)
        {
            return _plans.ToArray();
        }
    }

    public TaskPlan? GetPlan(string planId)
    {
        lock (_gate)
        {
            return _plans.FirstOrDefault(p => p.PlanId == planId);
        }
    }

    public event Action? Changed;

    public async Task SendAsync(string text)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try { await _client.SendCoordinatorMessageAsync(DefaultCoordinatorId, text, cts.Token).ConfigureAwait(false); }
        catch (Exception) { /* daemon unreachable — surfaced via ConnectionState. */ }
    }

    public IReadOnlyList<WorkerPlanCard> GetWorkerPlans()
    {
        lock (_gate)
        {
            return _workerPlans.ToArray();
        }
    }

    public OrchestrationBackpressure GetBackpressure()
    {
        lock (_gate)
        {
            return _backpressure;
        }
    }

    /// <summary>
    /// Submits the human's decision — and <b>lets a failure be seen</b>.
    ///
    /// <para>This used to end in <c>catch (Exception) { }</c>, justified as "surfaced via ConnectionState".
    /// It was not surfaced anywhere the operator was looking: a decision that never reached the daemon
    /// completed the same way a successful one did, so the panel had no way to distinguish "approved" from
    /// "silently lost" and no way to say which had happened. And the cost of guessing wrong is specific —
    /// the worker on that plan stays blocked, holding its jail and its slot against the worker cap, while
    /// the human believes they just unblocked it. "Already decided" deserves a message too, for the same
    /// reason. The caller reports it; swallowing it here made that impossible.</para>
    /// </summary>
    public async Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (approve)
        {
            await _client.ApprovePlanAsync(planId, cts.Token).ConfigureAwait(false);
        }
        else
        {
            // The reason is delivered to the worker as the feedback it revises against, so an empty
            // one is a wasted round of the revision budget. The placeholder is honest about that
            // rather than pretending the operator said something useful.
            var reason = string.IsNullOrWhiteSpace(feedback)
                ? "Rejected without written feedback — revise the plan and be more specific."
                : feedback!;
            await _client.RejectPlanAsync(planId, reason, cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The plan-mode toggle as the last plan update reported it.</summary>
    public PlanModeView GetPlanMode()
    {
        lock (_gate) return _planMode;
    }

    /// <summary>
    /// Turns the plan gate on or off, and adopts the DAEMON's answer rather than the requested value.
    ///
    /// <para>Not swallowed, for the same reason <see cref="SubmitPlanDecisionAsync"/> is not: a toggle
    /// that failed to reach the daemon would otherwise render as the state the operator asked for while
    /// the gate kept doing the opposite — and this particular disagreement is the one where the human
    /// believes there is an approval step and there is not.</para>
    /// </summary>
    public async Task SetPlanModeAsync(bool enabled)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var state = await _client.SetPlanModeAsync(enabled, cts.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _planMode = new PlanModeView(state.Enabled, state.Summary);
        }

        Changed?.Invoke();
    }

    // ---- ICliAgentHost (PR3: coordinator-as-CLI) --------------------------

    /// <summary>The live coordinator's agent id (from the snapshot's role field), or null.</summary>
    public string? CoordinatorAgentId
    {
        get { lock (_gate) return _coordinatorAgentId; }
    }

    /// <summary>The installed agent CLIs, over the daemon's ListInstalledAdapters RPC.</summary>
    public async Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct)
    {
        var adapters = await _client.ListInstalledAdaptersAsync(ct).ConfigureAwait(false);
        return adapters
            .Select(a => new InstalledCliOption(a.Id, a.Version, a.ApiKeyEnvVar ?? string.Empty))
            .ToArray();
    }

    /// <summary>
    /// Starts the coordinator: resolves the CLI's BYOK key from the P2-01 keystore (by the
    /// adapter's declared env-var name — none means interactive login, no key travels), then
    /// SpawnAgent with the coordinator role against the active repo handle.
    /// </summary>
    public async Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "No repo is provisioned for agents yet — open a repository first.");
        }

        var provider = ApiKeyProviderMap.ProviderForEnvVar(cli.ApiKeyEnvVar);
        var key = provider is null ? null : _keystoreLookup(ApiKeyProviderMap.KeystoreKeyFor(provider));

        // The CLI's saved login state (host OS keychain → jail tmpfs $HOME), so an interactive
        // login performed in an earlier session survives into this one instead of prompting again.
        var savedLogin = CliLoginVault.Parse(_keystoreLookup(CliLoginVault.KeystoreKeyFor(cli.Id)));

        // THIS repository's saved settings — the commands the user already approved here. Loaded by
        // repo handle, so an approval made in another repository is not in this list at all.
        var savedSettings = _cliSettings.Load(repoHandle!, cli.Id);

        // The wait is bounded by SILENCE, not by duration: a first start for this repository builds its
        // toolchain image inside this call and legitimately outruns any fixed budget.
        var agentId = await SpawnUnderWatchdogAsync(
            (token, deadline) => _client.SpawnAgentAsync(
                repoHandle, taskPrompt: string.Empty, agentKind: cli.Id, modelApiKey: key ?? string.Empty,
                token, deadline: deadline, role: Mainguard.Agents.Agents.AgentRoles.Coordinator,
                extraEnv: CollectCustomEnvKeys(),
                cliCredentials: savedLogin.Count > 0 ? savedLogin : null,
                cliSettings: savedSettings.Count > 0 ? savedSettings : null),
            ct).ConfigureAwait(false);

        lock (_gate)
        {
            _coordinatorAgentId = agentId;
        }

        Changed?.Invoke();
        return agentId;
    }

    // ---- IKillSwitchService (LIVE via Engage/Resume) --------------------

    public bool IsFrozen { get { lock (_gate) return _frozen; } }
    public KillSwitchPhase Phase { get { lock (_gate) return _phase; } }
    public string PhaseText { get { lock (_gate) return _phaseText; } }

    event Action? IKillSwitchService.Changed { add { Changed += value; } remove { Changed -= value; } }

    public async Task EngageAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var report = await _client.EngageKillAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A successful Engage means the queue is frozen (freeze-first, SA-1/F4) — regardless of
                // how many agents were live to pause.
                _frozen = true;
                _phase = KillSwitchPhase.Frozen;
                _phaseText = $"queue frozen · {report.AgentsPaused + report.AgentsYielded} agents paused";
            }
        }
        catch (Exception)
        {
            // Daemon unreachable — leave state unchanged.
        }

        Changed?.Invoke();
    }

    public async Task ResumeAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var report = await _client.ResumeKillAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // The queue freeze is always lifted (the daemon clears it whatever the unpause fan-out
                // did), so the banner state follows the queue. A jail that refused to wake is NOT hidden:
                // the daemon marks that session Unresponsive with "the jail is STILL paused. Press Resume
                // again", which is what the Resource Monitor row renders — the surface that carried the
                // false "(recoverable)" claim in ISSUES-LOG #17.
                _frozen = false;
                _phase = KillSwitchPhase.Armed;
                _phaseText = report.AgentsResumeFailed > 0
                    ? $"{report.AgentsResumeFailed} jail(s) did not resume — press again to retry"
                    : string.Empty;
            }
        }
        catch (Exception)
        {
            // Daemon unreachable — leave state unchanged.
        }

        Changed?.Invoke();
    }

    // ---- ITelemetryService (LIVE via StreamSpend) -----------------------

    // No per-agent sandbox-event RPC on the contract — this is empty by contract, not a stubbed surface.
    public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null) => Array.Empty<SandboxEvent>();

    public ResourceSample Current
    {
        get
        {
            lock (_gate)
            {
                // Nothing sampled yet: every reading is unknown. It is NOT a fleet sitting at zero.
                return _samples.Count > 0
                    ? _samples[^1]
                    : new ResourceSample(DateTimeOffset.UtcNow, null, null, null);
            }
        }
    }

    public IReadOnlyList<ResourceSample> History
    {
        get { lock (_gate) return _samples.ToArray(); }
    }

    public IReadOnlyList<AgentResourceUsage> GetAgentUsage()
    {
        lock (_gate)
        {
            // The worker briefs, taken from the plan stream the surface is already receiving rather than
            // from a new wire field: the plan's Title IS the brief a worker was spawned against, and it is
            // the only human-legible name any of these sessions has. Without it the resource monitor can
            // only print the CLI kind, which is identical for every agent of that kind.
            var titles = _workerPlans
                .Where(p => p.WorkerAgentId.Length > 0 && p.Title.Length > 0)
                .GroupBy(p => p.WorkerAgentId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Title, StringComparer.Ordinal);

            return _agents.Values
                .Select(a =>
                {
                    _agentSpend.TryGetValue(a.AgentId, out var spend);
                    var haveReading = _agentResources.TryGetValue(a.AgentId, out var res);

                    // Spend is a number only where it is actually measured. An unmetered agent reports
                    // null, so the row can say "not tracked" instead of drawing a reassuring $0.00.
                    decimal? spendUsd = haveReading && res.Metered ? spend.UsdMicros / 1_000_000m : null;

                    return new AgentResourceUsage(
                        a.AgentId, a.Name, a.State.ToString(), a.State == AgentLifecycleState.Paused,
                        CpuPercent: haveReading ? res.Cpu : null,
                        RamGb: haveReading && res.RamBytes is { } bytes ? bytes / (1024.0 * 1024.0 * 1024.0) : null,
                        SpendUsd: spendUsd,
                        Task: a.Detail,
                        IsMetered: haveReading && res.Metered,
                        Role: a.Role,
                        Title: titles.TryGetValue(a.AgentId, out var title) ? title : string.Empty);
                })
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public event Action? Sampled;

    /// <summary>Reads the live per-agent + per-day budget caps.</summary>
    public async Task<Proto.Budget> GetBudgetsAsync(CancellationToken ct)
        => await _client.GetBudgetsAsync(ct).ConfigureAwait(false);

    /// <summary>Writes the per-agent + per-day budget caps (persisted + reflected in the live ledger).</summary>
    public async Task<Proto.Budget> SetBudgetsAsync(Proto.Budget budget, CancellationToken ct)
        => await _client.SetBudgetsAsync(budget, ct).ConfigureAwait(false);

    /// <summary>ITelemetryService budget seam (Core DTO): maps the live proto caps into the UI-facing
    /// record so the Resource Monitor can display + edit the per-day cap without touching proto types.</summary>
    public async Task<SpendBudget> GetSpendBudgetAsync(CancellationToken ct = default)
    {
        var b = await GetBudgetsAsync(ct).ConfigureAwait(false);
        return new SpendBudget(b.UsdMicrosCap, b.TokenCap, b.UsdMicrosCapPerDay, b.TokenCapPerDay);
    }

    /// <summary>Writes the whole cap record back through SetBudgets so an unedited cap is preserved.</summary>
    public async Task SetSpendBudgetAsync(SpendBudget budget, CancellationToken ct = default)
        => await SetBudgetsAsync(new Proto.Budget
        {
            UsdMicrosCap = budget.PerAgentUsdMicrosCap,
            TokenCap = budget.PerAgentTokenCap,
            UsdMicrosCapPerDay = budget.PerDayUsdMicrosCap,
            TokenCapPerDay = budget.PerDayTokenCap,
        }, ct).ConfigureAwait(false);

    // ---- IVibeService (separate future app — intentionally inert) --------

    public IReadOnlyList<Checkpoint> GetCheckpoints() => Array.Empty<Checkpoint>();
    public Checkpoint? LastVerifiedGreen => null;
    public Task RestoreCheckpointAsync(string sha) => Task.CompletedTask;
    public DeployStatus Deploy => new(DeployPhase.Idle, null, null);
    public event Action? DeployChanged { add { } remove { } }
    public Task PublishAsync() => Task.CompletedTask;

    public void Dispose()
    {
        // The LAST harvest, BEFORE anything is cancelled — this is the app-close leg of the login
        // round-trip. Closing Mainguard with agents still running used to lose every login performed in
        // this session (the jail's $HOME is tmpfs and dies with the VM/containers), so the sweep runs
        // once more here on its OWN token: _cts is cancelled immediately below, and a harvest bound to
        // it would be cancelled before it could issue a single RPC. Bounded by ShutdownHarvestBudget and
        // run off the calling thread, so a wedged daemon delays the exit by seconds rather than hanging
        // the UI thread on a sync-over-async wait.
        try
        {
            using var shutdownHarvest = new CancellationTokenSource(ShutdownHarvestBudget);
            Task.Run(() => PersistLiveAgentLoginsAsync(shutdownHarvest.Token), shutdownHarvest.Token)
                .Wait(ShutdownHarvestBudget);
        }
        catch { /* a failed final harvest costs at most the last sweep interval — never the exit */ }

        _cts.Cancel();
        try { _queuePumpCts?.Cancel(); } catch { /* ignore */ }
        try
        {
            Task.WaitAll(
                new[] { _agentPump, _planPump, _spendPump, _resourcePump, _conversationPump, _queuePump, _loginHarvestPump }
                    .Where(t => t is not null).Select(t => t!).ToArray(),
                TimeSpan.FromSeconds(2));
        }
        catch { /* pump cancellation */ }

        _queuePumpCts?.Dispose();
        _cts.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
