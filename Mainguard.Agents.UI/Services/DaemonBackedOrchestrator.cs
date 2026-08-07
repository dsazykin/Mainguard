using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
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

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

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

    /// <summary>SpawnAgent runs the daemon's whole provision chain (worktree + hardened container +
    /// CLI bind under a PTY) — on a cold start that is well past the client's 10 s RPC default, whose
    /// expiry cancelled the server-side spawn mid-provision and tore it down (the "never starts on
    /// the first try" field bug). Stop still aborts the wait through the cancellation token, and the
    /// surface's connect watchdog keeps the long wait honest.</summary>
    private static readonly TimeSpan SpawnDeadline = TimeSpan.FromMinutes(5);

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
    /// audited T-23 transport reading this Windows host's keyring.
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
    public DaemonBackedOrchestrator(
        DaemonClient client, bool ownsClient = true, Func<string, string?>? keystoreLookup = null,
        Func<string, IReadOnlyList<string>>? keystoreList = null,
        Action<string, string>? keystoreSave = null,
        Func<Mainguard.Git.Services.IOperationJournal>? journalFactory = null,
        Func<Mainguard.Agents.Services.IHostPullRequestGateway>? hostPullRequests = null,
        TimeSpan? loginHarvestInterval = null)
    {
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
    /// SpawnAgent's <c>extra_env</c>. Values come fresh from the keyring per spawn.</summary>
    private IReadOnlyDictionary<string, string> CollectCustomEnvKeys()
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
    /// human foreground merge lands on the user's own Windows checkout (never the VM mirror, which is
    /// staging), fetching the agent branch over the SC-2-resolved sync remote registered on it. Without
    /// them the queue is observable but not mergeable, and <see cref="ConfirmMergeAsync"/> says so rather
    /// than recording a merge it never performed.</para></summary>
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

    private async Task AgentPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _client.StreamAgentEventsAsync(ct).ConfigureAwait(false))
            {
                ApplyAgentEvent(e);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

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
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamQueueAsync(repoHandle, token).ConfigureAwait(false))
            {
                ApplyQueueUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

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

    private void ApplyAgentEvent(Proto.AgentEvent e)
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

                // Egress block-notification fallback (Fix 2): a CLI that DIED with a "couldn't reach HOST"
                // reason was almost certainly refused by the default-deny proxy — surface it so the operator
                // can unblock the host and retry, instead of a silent exit-1.
                if (IsTerminal(newState) && reason.Length > 0
                    && Mainguard.Agents.Agents.Sandbox.EgressBlockDetector.TryDetectBlockedHost(reason) is { Length: > 0 } blockedHost)
                {
                    EgressBlocked?.Invoke(new EgressBlockInfo(e.AgentId, agentKind ?? e.AgentId, blockedHost));
                }

                break;

            case Proto.AgentEvent.EventOneofCase.Log:
                // Log lines feed the terminal (its own attach stream); nothing to project here.
                break;
        }

        EventReceived?.Invoke(new AgentEvent(
            Interlocked.Increment(ref _seq), e.EventCase.ToString(), e.AgentId, string.Empty, DateTimeOffset.UtcNow));
        Changed?.Invoke();
    }

    private void ApplyQueueUpdate(Proto.QueueUpdate update)
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

                _queue.Add(new QueueEntry(
                    entry.AgentId, entry.AgentId, $"agent/{entry.AgentId}", state,
                    entry.GateReason ?? string.Empty, Verification: null, FlaggedItems: flagged,
                    // Carried from the daemon rather than inferred from the state: a client that guessed
                    // "Verifying ⇒ a run is happening" would be wrong for exactly the entries this matters
                    // for — the ones a restart left frozen mid-verification.
                    VerificationInFlight: entry.VerificationInFlight,
                    // Three-valued on purpose. `HasHasLiveSandbox` is protobuf's field-presence test: a
                    // daemon that predates the field leaves it unset, and mapping that to `false` would
                    // render every one of its entries as stranded and offer to spawn jails for agents that
                    // are running. Unset means unknown, and unknown changes nothing.
                    HasLiveSandbox: entry.HasHasLiveSandbox ? entry.HasLiveSandbox : null));
                _gate_[entry.AgentId] = (entry.CanMerge, entry.GateReason ?? string.Empty);
            }
        }

        Changed?.Invoke();
    }

    private void ApplyPlanUpdate(Proto.PlanUpdate update)
    {
        lock (_gate)
        {
            _plans.Clear();
            foreach (var p in update.Plans)
            {
                // Only pending plans are approvable cards; decided ones stay in the daemon's history.
                if (!string.Equals(p.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _plans.Add(new TaskPlan(
                    p.PlanId, p.Title, p.Scope.ToArray(), p.Approach, p.TestStrategy,
                    (decimal)p.BudgetUsd, DateTimeOffset.UtcNow));
            }
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
    /// Fetches the authoritative agent list (kind + role) after a state delta arrived for an agent
    /// the projection had never seen — the delta carries neither, and the coordinator's role must
    /// never be dropped on the floor. Merges by agent id (a merge never deletes: removal is the
    /// snapshot's job) and recomputes the live-coordinator pointer. Best-effort: a daemon hiccup
    /// leaves the placeholder until the next snapshot corrects it.
    /// </summary>
    private async Task ResyncAgentsAsync()
    {
        IReadOnlyList<Proto.AgentInfo> listed;
        try
        {
            listed = await _client.ListAgentsAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // next snapshot resyncs
        }

        lock (_gate)
        {
            foreach (var a in listed)
            {
                _agents[a.AgentId] = MapInfo(a);
            }

            _coordinatorAgentId = _agents.Values
                .FirstOrDefault(a => a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator)?.AgentId
                ?? _coordinatorAgentId;
        }

        Changed?.Invoke();
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
        List<string> agentIds;
        try
        {
            agentIds = (await _client.ListAgentsAsync(ct).ConfigureAwait(false))
                .Select(a => a.AgentId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
        }
        catch (Exception)
        {
            return; // daemon unreachable — surfaced via ConnectionState, never an app crash.
        }

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

    // No per-agent pause/prompt/plan-tree RPCs exist on the daemon contract yet — these steer nothing.
    // They stay no-ops/empty (never fabricated); the terminal attach stream carries the live PTY instead.
    public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
    public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
    public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
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

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 8 ? sha[..8] : sha);

    /// <summary>
    /// The human foreground merge — the whole RT-D1 conversation (P2-10 §3.7), driven from the Merge
    /// button: <c>BeginMerge</c> (the daemon takes the repo's one lease and enforces <c>CanMerge</c> under
    /// it) → <b>the real Windows-side <c>git merge --ff-only</c> on the user's own checkout</b> →
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

        // The CAS old-OID comes from the grant, not from our own queue projection: the projection is a
        // stream snapshot and may be a revision behind the main the daemon just authorized against.
        var lease = new Mainguard.Git.Models.MergeLeaseRow
        {
            RepoHash = repoHandle!,
            LeaseId = begun.LeaseId,
            AgentId = agentId,
            ExpectedMainSha = begun.ExpectedMainSha ?? string.Empty,
            MainBranch = MainBranchName,
        };

        var mergeRequest = new Mainguard.Agents.Services.ForegroundMergeRequest(
            RepoPath: repoPath!,
            RepoHash: repoHandle!,
            AgentId: agentId,
            ExpectedMainSha: lease.ExpectedMainSha,
            MainBranch: MainBranchName);

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
    /// The Windows-side merge leg, bound to this repo's SC-2 sync remote and the app's T-19 journal.
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

        var response = await _client.ResumeAgentAsync(
            repoHandle!, agentId, agentKind, key ?? string.Empty, cts.Token,
            deadline: SpawnDeadline,
            extraEnv: CollectCustomEnvKeys(),
            cliCredentials: savedLogin.Count > 0 ? savedLogin : null).ConfigureAwait(false);

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

    public async Task SubmitPlanDecisionAsync(string planId, bool approve)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            if (approve)
            {
                await _client.ApprovePlanAsync(planId, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _client.RejectPlanAsync(planId, "rejected by operator", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* daemon unreachable / already decided — surfaced via ConnectionState. */ }
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

        var agentId = await _client.SpawnAgentAsync(
            repoHandle, taskPrompt: string.Empty, agentKind: cli.Id, modelApiKey: key ?? string.Empty,
            ct, deadline: SpawnDeadline, role: Mainguard.Agents.Agents.AgentRoles.Coordinator,
            extraEnv: CollectCustomEnvKeys(),
            cliCredentials: savedLogin.Count > 0 ? savedLogin : null).ConfigureAwait(false);

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
            await _client.ResumeKillAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _frozen = false;
                _phase = KillSwitchPhase.Armed;
                _phaseText = string.Empty;
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
                        IsMetered: haveReading && res.Metered);
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
