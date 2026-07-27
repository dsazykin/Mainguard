using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Daemon;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>One CLI login-state file (a $HOME-relative path + SECRET bytes) crossing the
/// host↔daemon boundary: sent on spawn to restore a saved login into the jail's tmpfs home,
/// received on stop to persist the latest login into the host OS keychain.</summary>
public sealed record CliLoginFile(string Path, byte[] Content);

/// <summary>A stop's result: whether a session was removed, its adapter kind, and the login-state
/// files harvested from the jail before its tmpfs $HOME evaporated (empty when none).</summary>
public sealed record AgentStopOutcome(bool Stopped, string AgentKind, IReadOnlyList<CliLoginFile> CliCredentials);

/// <summary>
/// The App's sole daemon touch-point (G-18): a gRPC client over loopback. Owns channel
/// creation, bearer-token metadata (read from the daemon's session-token file),
/// reconnect-with-exponential-backoff+jitter (cap ~30 s), and a
/// <see cref="ConnectionState"/> observable property the P2-13 Activity Bar binds to
/// (plain <see cref="INotifyPropertyChanged"/> — no Rx). <see cref="StreamAgentEventsAsync"/>
/// resumes via the server's snapshot-then-deltas design after any drop.
///
/// Every RPC method takes a <see cref="CancellationToken"/> and applies a deadline —
/// there is no deadline-less call path (P2-02 rejection trigger).
/// </summary>
public sealed class DaemonClient : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

    private readonly Func<GrpcChannel> _channelFactory;
    private readonly Func<string> _tokenProvider;
    private readonly BackoffPolicy _backoff;
    private GrpcChannel? _channel;
    private ConnectionState _state = ConnectionState.Down;

    public DaemonClient(Func<GrpcChannel> channelFactory, Func<string> tokenProvider, BackoffPolicy? backoff = null)
    {
        _channelFactory = channelFactory;
        _tokenProvider = tokenProvider;
        _backoff = backoff ?? BackoffPolicy.Default;
    }

    /// <summary>
    /// Production factory: loopback channel + token resolved across the host/VM boundary. With no
    /// explicit <paramref name="tokenPath"/> the token comes from <see cref="DaemonTokenLocator"/> —
    /// which knows the in-VM daemon writes its token INSIDE MainguardEnv (read over
    /// <c>\\wsl.localhost</c>), not under <c>%LocalAppData%</c>; reading only the local file was the
    /// audit-found reason the shipped control center could never authenticate. Re-read per call, so a
    /// daemon restart (fresh token) heals on the next RPC.
    /// </summary>
    public static DaemonClient ForLoopback(int port = DaemonPaths.DefaultLoopbackPort, string? tokenPath = null)
    {
        // MG-19: mutually-authenticated TLS with both ends pinned to this daemon session — NOT h2c.
        // The session directory is resolved once per channel so the token and the certificates always
        // come from the SAME daemon; pairing a fresh token with a stale daemon's certificates (or the
        // reverse) would be an authentication failure that looks like a network fault.
        return new DaemonClient(
            () => CreateChannel(port, tokenPath),
            tokenPath is null
                ? () => DaemonTokenLocator.ReadToken()
                : () => File.ReadAllText(tokenPath).Trim());
    }

    /// <summary>
    /// Builds a pinned mTLS channel to the loopback daemon. There is no plaintext fallback: if the
    /// transport credentials are missing the call throws rather than downgrading, because a downgrade
    /// would hand the bearer token to whatever answered on the port — the exact port-squatting theft the
    /// pin exists to prevent.
    /// </summary>
    private static GrpcChannel CreateChannel(int port, string? tokenPath)
    {
        var sessionDirectory = tokenPath is null
            ? DaemonTokenLocator.ResolveSessionDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;

        // Deliberately NOT disposed here: the client certificate it owns must stay alive for as long as
        // the handler that presents it. The channel disposes the handler, and the certificate goes with it.
        var credentials = DaemonTransportCredentials.Load(sessionDirectory);
        var handler = new SocketsHttpHandler { SslOptions = credentials.BuildSslOptions() };
        return GrpcChannel.ForAddress(
            $"https://127.0.0.1:{port}",
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fires on every connection-state transition (non-XAML consumers).</summary>
    public event Action<ConnectionState>? ConnectionStateChanged;

    /// <summary>Raised for each agent event received on the live stream.</summary>
    public event Action<AgentEvent>? AgentEventReceived;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            ConnectionStateChanged?.Invoke(value);
        }
    }

    /// <summary>Lists agents (authenticated, deadlined).</summary>
    public async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.ListAgentsAsync(new ListAgentsRequest(), CallOptions(ct, deadline));
        return response.Agents;
    }

    /// <summary>Spawns an agent (authenticated, deadlined). The model key, every
    /// <paramref name="extraEnv"/> value, and every <paramref name="cliCredentials"/> content are
    /// `// SECRET` fields; <paramref name="cliCredentials"/> is the CLI's saved login state from the
    /// host OS keychain, restored into the jail's tmpfs $HOME so the user isn't asked to sign in on
    /// every launch. <paramref name="role"/> is "" (manual), "coordinator", or "managed"
    /// (see <c>AgentRoles</c>).</summary>
    public async Task<string> SpawnAgentAsync(
        string repoHandle, string taskPrompt, string agentKind, string modelApiKey,
        CancellationToken ct, TimeSpan? deadline = null, string role = "",
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<CliLoginFile>? cliCredentials = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var request = new SpawnAgentRequest
        {
            RepoHandle = repoHandle,
            TaskPrompt = taskPrompt,
            AgentKind = agentKind,
            ModelApiKey = modelApiKey,
            Role = role ?? string.Empty,
        };
        if (extraEnv is not null)
        {
            foreach (var (name, value) in extraEnv)
            {
                request.ExtraEnv.Add(new EnvEntry { Name = name, Value = value });
            }
        }

        if (cliCredentials is not null)
        {
            foreach (var file in cliCredentials)
            {
                request.CliCredentials.Add(new CliCredentialFile
                {
                    Path = file.Path,
                    Content = Google.Protobuf.ByteString.CopyFrom(file.Content),
                });
            }
        }

        var response = await client.SpawnAgentAsync(request, CallOptions(ct, deadline));
        return response.AgentId;
    }

    /// <summary>The tier-1 skew probe (authenticated, deadlined): the daemon's own version + the
    /// MainguardOS payload version. A pre-<c>GetDaemonInfo</c> daemon throws <c>Unimplemented</c> —
    /// that IS the skew signal; the caller maps it (see <c>DaemonAutoRefresh</c>), not this method.</summary>
    public async Task<Mainguard.Agents.Agents.Bootstrap.DaemonVersionInfo> GetDaemonInfoAsync(
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.GetDaemonInfoAsync(new GetDaemonInfoRequest(), CallOptions(ct, deadline));
        return new Mainguard.Agents.Agents.Bootstrap.DaemonVersionInfo(response.DaemonVersion, response.PayloadVersion);
    }

    /// <summary>The agent CLIs installed in the VM the daemon can launch (ids/versions/env-var
    /// names only — never key values). What the "Start coordinator" picker lists.</summary>
    public async Task<IReadOnlyList<InstalledAdapterInfo>> ListInstalledAdaptersAsync(
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.ListInstalledAdaptersAsync(
            new ListInstalledAdaptersRequest(), CallOptions(ct, deadline));
        return response.Adapters;
    }

    /// <summary>
    /// Provisions the host repo's bare mirror in the VM (P2-06) and returns the resolved sync
    /// remote (name + opaque URL handle) the App registers via its <c>SyncRemoteRegistrar</c>.
    /// The name is whatever the daemon's substrate resolved — the App never hardcodes it.
    /// </summary>
    public async Task<ProvisionedRepo> ProvisionRepoAsync(
        string originPath, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new RepoSyncService.RepoSyncServiceClient(Channel());
        var response = await client.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = originPath }, CallOptions(ct, deadline));
        return new ProvisionedRepo(response.RepoHandle, response.SyncRemoteName, response.SyncRemoteUrl);
    }

    /// <summary>Stops an agent (authenticated, deadlined). The result carries the CLI login-state
    /// files the daemon harvested from the jail's tmpfs $HOME just before teardown (SECRET contents)
    /// — the caller persists them into the host OS keychain so the login survives the relaunch.</summary>
    public async Task<AgentStopOutcome> StopAgentAsync(string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.StopAgentAsync(new StopAgentRequest { AgentId = agentId }, CallOptions(ct, deadline));
        var credentials = response.CliCredentials
            .Select(f => new CliLoginFile(f.Path, f.Content.ToByteArray()))
            .ToArray();
        return new AgentStopOutcome(response.Stopped, response.AgentKind, credentials);
    }

    /// <summary>
    /// Harvests a LIVE agent's CLI login-state without stopping it, so the host keychain can be kept
    /// current while the agent runs. Harvest used to happen only inside <see cref="StopAgentAsync"/>,
    /// so a daemon shutdown, VM stop or crash lost the login and the user signed in again every
    /// launch. An empty result is normal (no jail, nothing declared, or not signed in yet) and the
    /// caller must treat it as "nothing new", never as "clear the keychain".
    /// </summary>
    public async Task<AgentStopOutcome> HarvestAgentCredentialsAsync(
        string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.HarvestAgentCredentialsAsync(
            new HarvestAgentCredentialsRequest { AgentId = agentId }, CallOptions(ct, deadline));
        var credentials = response.CliCredentials
            .Select(f => new CliLoginFile(f.Path, f.Content.ToByteArray()))
            .ToArray();
        // Stopped:false — the agent is still running; only the credential payload matters here.
        return new AgentStopOutcome(false, response.AgentKind, credentials);
    }

    /// <summary>Reads the daemon-owned egress allowlist (P2-07).</summary>
    public async Task<IReadOnlyList<AllowlistEntry>> ListAllowlistAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new EgressService.EgressServiceClient(Channel());
        var response = await client.ListAllowlistAsync(new ListAllowlistRequest(), CallOptions(ct, deadline));
        return response.Entries;
    }

    /// <summary>Adds a host to the egress allowlist and re-renders the running proxy. Returns whether the
    /// host was newly added and whether it re-opens a direct git-host route (A6). No actor is sent: the
    /// daemon derives the <c>allowlist_changed</c> actor from the authenticated connection (SA-1/F2), so
    /// the change log records who the daemon SAW, not who the caller claimed to be.</summary>
    public async Task<(bool Added, bool DefeatsA6)> AddAllowlistHostAsync(
        string name, string hostPattern, string kind, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new EgressService.EgressServiceClient(Channel());
        var response = await client.AddAllowlistHostAsync(new AddAllowlistHostRequest
        {
            Name = name ?? string.Empty,
            HostPattern = hostPattern ?? string.Empty,
            Kind = kind ?? string.Empty,
        }, CallOptions(ct, deadline));
        return (response.Added, response.DefeatsA6);
    }

    /// <summary>Removes a host from the egress allowlist and re-renders the running proxy. The audit actor
    /// is daemon-derived (see <see cref="AddAllowlistHostAsync"/>).</summary>
    public async Task<bool> RemoveAllowlistHostAsync(string hostPattern, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new EgressService.EgressServiceClient(Channel());
        var response = await client.RemoveAllowlistHostAsync(new RemoveAllowlistHostRequest
        {
            HostPattern = hostPattern ?? string.Empty,
        }, CallOptions(ct, deadline));
        return response.Removed;
    }

    /// <summary>
    /// Runs the agent-event stream with reconnect. Yields every <see cref="AgentEvent"/>
    /// (also raised on <see cref="AgentEventReceived"/>). On a transient fault it marks
    /// <see cref="ConnectionState.Degraded"/>, backs off (capped, jittered), rebuilds the
    /// channel, and re-subscribes — the fresh server snapshot re-syncs the client. A
    /// missing/wrong token is terminal (no retry storm): the state goes
    /// <see cref="ConnectionState.Down"/> and the loop exits until re-invoked (a re-read
    /// of the token file). Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> StreamAgentEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var faulted = false;
            var permissionDenied = false;
            IAsyncEnumerator<AgentEvent>? enumerator = null;
            try
            {
                var client = new AgentService.AgentServiceClient(Channel());
                var call = client.StreamAgentEvents(new StreamAgentEventsRequest(), AuthOnly(ct));
                enumerator = call.ResponseStream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                permissionDenied = true;
            }
            catch (RpcException)
            {
                faulted = true;
            }

            if (permissionDenied)
            {
                State = ConnectionState.Down;
                yield break;
            }

            if (enumerator is not null)
            {
                while (true)
                {
                    AgentEvent? current = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await enumerator.DisposeAsync();
                        State = ConnectionState.Down;
                        yield break;
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
                    {
                        permissionDenied = true;
                        break;
                    }
                    catch (RpcException)
                    {
                        faulted = true;
                        break;
                    }

                    // First successful frame → healthy; reset backoff.
                    State = ConnectionState.Connected;
                    attempt = 0;
                    AgentEventReceived?.Invoke(current);
                    yield return current;
                }

                await enumerator.DisposeAsync();
            }

            if (permissionDenied)
            {
                State = ConnectionState.Down;
                yield break;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Stream ended or faulted → reconnect with backoff.
            _ = faulted;
            State = ConnectionState.Degraded;
            ResetChannel();
            try
            {
                await Task.Delay(_backoff.Delay(attempt++), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        State = ConnectionState.Down;
    }

    /// <summary>
    /// Opens the terminal <c>Attach</c> bidi stream (authenticated, no wall-clock deadline — it is
    /// long-lived, ended by cancelling <paramref name="ct"/>). The caller writes the first
    /// <c>agent_id</c> frame, then input/resize frames, and reads <c>raw</c> output frames.
    /// </summary>
    public AsyncDuplexStreamingCall<TerminalInput, TerminalOutput> AttachTerminal(CancellationToken ct)
    {
        var client = new TerminalService.TerminalServiceClient(Channel());
        return client.Attach(AuthOnly(ct));
    }

    // ---- P2-10 merge queue (P2-47 #1) ----

    /// <summary>Streams the P2-10 merge-queue snapshot-then-deltas for a repo handle (one attach; the
    /// caller re-subscribes to reconnect). No wall-clock deadline — long-lived, ended by cancellation.</summary>
    public async IAsyncEnumerable<QueueUpdate> StreamQueueAsync(
        string repoHandle, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        using var call = client.StreamQueue(new StreamQueueRequest { RepoHandle = repoHandle }, AuthOnly(ct));
        await foreach (var update in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Runs the configured verification in the agent's sandbox (daemon-observed exit).</summary>
    public async Task<RunVerificationResponse> RunVerificationAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.RunVerificationAsync(
            new RunVerificationRequest { RepoHandle = repoHandle, AgentId = agentId },
            CallOptions(ct, deadline));
    }

    /// <summary>The CanMerge gate query (daemon-authoritative reason string, rendered verbatim).</summary>
    public async Task<CanMergeResponse> CanMergeAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.CanMergeAsync(
            new CanMergeRequest { RepoHandle = repoHandle, AgentId = agentId }, CallOptions(ct, deadline));
    }

    /// <summary>RT-D1 step 1: take the per-repo merge lease before the human foreground merge.</summary>
    public async Task<BeginMergeResponse> BeginMergeAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = repoHandle, AgentId = agentId }, CallOptions(ct, deadline));
    }

    /// <summary>RT-D1 step 3: record the merge outcome, release the lease, fire the stale cascade.</summary>
    public async Task<bool> ConfirmMergeAsync(
        string repoHandle, string agentId, string leaseId, string newMainSha,
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        var response = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            LeaseId = leaseId,
            NewMainSha = newMainSha,
        }, CallOptions(ct, deadline));
        return response.Confirmed;
    }

    /// <summary>P2-11 step 4: acknowledge ONE must-acknowledge flagged item daemon-side, and read the gate
    /// back in the same round trip. The daemon owns the acknowledgment ledger the merge gate consults — a
    /// client-side ack alone never unblocked anything.</summary>
    public async Task<AcknowledgeFlaggedChangeResponse> AcknowledgeFlaggedChangeAsync(
        string repoHandle, string agentId, string itemId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            ItemId = itemId,
        }, CallOptions(ct, deadline));
    }

    /// <summary>P2-47 #7: the agent-branch-vs-main diff for the review cockpit, parsed into <see cref="FilePatch"/>
    /// via the pure T-06 <c>PatchParser</c> on the client. Returns the resolved branch + main + patch list.</summary>
    public async Task<(string Branch, string MainBranch, IReadOnlyList<Mainguard.Git.Models.FilePatch> Files)> GetMergeDiffAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        var response = await client.GetMergeDiffAsync(
            new GetMergeDiffRequest { RepoHandle = repoHandle, AgentId = agentId }, CallOptions(ct, deadline));
        var files = Mainguard.Git.Services.PatchParser.Parse(response.UnifiedDiff ?? string.Empty);
        return (response.Branch, response.MainBranch, files);
    }

    // ---- P2-14 plan approval (P2-47 #2) ----

    /// <summary>Streams the P2-14 pending + recently-decided plans snapshot-then-deltas.</summary>
    public async IAsyncEnumerable<PlanUpdate> StreamPlansAsync(
        string coordinatorId, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        using var call = client.StreamPlans(new StreamPlansRequest { CoordinatorId = coordinatorId }, AuthOnly(ct));
        await foreach (var update in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Approves a pending plan (approver identity is daemon-derived — SA-1/F2).</summary>
    public async Task<ApprovePlanResponse> ApprovePlanAsync(string planId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        return await client.ApprovePlanAsync(new ApprovePlanRequest { PlanId = planId }, CallOptions(ct, deadline));
    }

    /// <summary>Rejects a pending plan — nothing spawns, no worktree residue.</summary>
    public async Task<bool> RejectPlanAsync(string planId, string reason, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        var response = await client.RejectPlanAsync(
            new RejectPlanRequest { PlanId = planId, Reason = reason ?? string.Empty }, CallOptions(ct, deadline));
        return response.Rejected;
    }

    // ---- P2-14 kill switch (P2-47 #3) ----

    /// <summary>Engages the kill switch: freeze-queue-first, then yield fan-out (SA-1/F4 + RT-D4).</summary>
    public async Task<EngageKillResponse> EngageKillAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new KillSwitchService.KillSwitchServiceClient(Channel());
        return await client.EngageAsync(new EngageKillRequest(), CallOptions(ct, deadline));
    }

    /// <summary>Resumes from a kill: clears the freeze, unpauses agents.</summary>
    public async Task<bool> ResumeKillAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new KillSwitchService.KillSwitchServiceClient(Channel());
        var response = await client.ResumeAsync(new ResumeKillRequest(), CallOptions(ct, deadline));
        return response.Resumed;
    }

    // ---- P2-08 gateway / telemetry (P2-47 #4) ----

    /// <summary>Streams live per-agent token/USD spend samples (the ledger row feed).</summary>
    public async IAsyncEnumerable<SpendSample> StreamSpendAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var client = new GatewayService.GatewayServiceClient(Channel());
        using var call = client.StreamSpend(new StreamSpendRequest(), AuthOnly(ct));
        await foreach (var sample in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    /// <summary>Reads the per-agent + per-day budget caps.</summary>
    public async Task<Budget> GetBudgetsAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new GatewayService.GatewayServiceClient(Channel());
        var response = await client.GetBudgetsAsync(new GetBudgetsRequest(), CallOptions(ct, deadline));
        return response.Budget ?? new Budget();
    }

    /// <summary>Writes the per-agent + per-day budget caps (persisted + reflected in the live ledger).</summary>
    public async Task<Budget> SetBudgetsAsync(Budget budget, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new GatewayService.GatewayServiceClient(Channel());
        var response = await client.SetBudgetsAsync(new SetBudgetsRequest { Budget = budget }, CallOptions(ct, deadline));
        return response.Budget ?? new Budget();
    }

    // ---- P2-14 / P2-47 #9 coordinator conversation ----

    /// <summary>Streams the coordinator conversation snapshot-then-deltas.</summary>
    public async IAsyncEnumerable<ConversationUpdate> StreamConversationAsync(
        string coordinatorId, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = new CoordinatorService.CoordinatorServiceClient(Channel());
        using var call = client.StreamConversation(
            new StreamConversationRequest { CoordinatorId = coordinatorId }, AuthOnly(ct));
        await foreach (var update in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Sends one operator message into the coordinator conversation.</summary>
    public async Task<bool> SendCoordinatorMessageAsync(
        string coordinatorId, string text, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new CoordinatorService.CoordinatorServiceClient(Channel());
        var response = await client.SendMessageAsync(
            new SendMessageRequest { CoordinatorId = coordinatorId, Text = text }, CallOptions(ct, deadline));
        return response.Accepted;
    }

    private GrpcChannel Channel() => _channel ??= _channelFactory();

    private void ResetChannel()
    {
        var old = _channel;
        _channel = null;
        old?.Dispose();
    }

    private Metadata AuthHeaders() => new() { { "authorization", $"bearer {_tokenProvider()}" } };

    private CallOptions CallOptions(CancellationToken ct, TimeSpan? deadline)
        => new(headers: AuthHeaders(), deadline: DateTime.UtcNow.Add(deadline ?? DefaultDeadline), cancellationToken: ct);

    // Streaming calls carry no wall-clock deadline (they are long-lived) but always a
    // cancellation token — the caller ends them by cancelling.
    private CallOptions AuthOnly(CancellationToken ct) => new(headers: AuthHeaders(), cancellationToken: ct);

    public void Dispose() => ResetChannel();
}

/// <summary>The P2-06 provision result the App needs: the opaque repo handle plus the resolved
/// sync remote (name + opaque URL handle) to register on the host repo.</summary>
public sealed record ProvisionedRepo(string RepoHandle, string SyncRemoteName, string SyncRemoteUrl);

/// <summary>
/// Exponential backoff with full jitter, capped. Extracted so it is unit-testable
/// without a network (the client-side thin test asserts the cap).
/// </summary>
public sealed class BackoffPolicy
{
    public static readonly BackoffPolicy Default = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30));

    private readonly TimeSpan _base;
    private readonly TimeSpan _cap;
    private readonly Random _random;

    public BackoffPolicy(TimeSpan @base, TimeSpan cap, Random? random = null)
    {
        _base = @base;
        _cap = cap;
        _random = random ?? Random.Shared;
    }

    public TimeSpan Cap => _cap;

    /// <summary>The (jittered) delay for a zero-based attempt, never exceeding the cap.</summary>
    public TimeSpan Delay(int attempt)
    {
        // base * 2^attempt, clamped to cap, then full jitter in [0, ceiling].
        var exponent = Math.Min(attempt, 30);
        var ceilingMs = Math.Min(_cap.TotalMilliseconds, _base.TotalMilliseconds * Math.Pow(2, exponent));
        var jittered = _random.NextDouble() * ceilingMs;
        return TimeSpan.FromMilliseconds(jittered);
    }
}
