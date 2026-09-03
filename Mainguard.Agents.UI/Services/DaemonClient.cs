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

/// <summary>
/// One CLI SETTINGS file crossing the host↔daemon boundary: sent on spawn to restore THIS
/// repository's saved settings (the approved-command list above all) into the jail, received on stop
/// or harvest to fold the session's approvals back into the per-repo store.
///
/// <para><see cref="Root"/> is the declared spelling — <c>"home"</c> or <c>"workspace"</c> — because a
/// CLI keeps user-level and project-level configuration in two different trees and both are wiped
/// every spawn. It stays a string on this side so the client never has to know the jail's directory
/// layout (G-14); the daemon resolves it, and refuses a spelling it does not know.</para>
/// </summary>
public sealed record CliSettingsFileEntry(string Root, string Path, byte[] Content);

/// <summary>A stop's result: whether a session was removed, its adapter kind, and the login-state
/// files harvested from the jail before its tmpfs $HOME evaporated (empty when none).</summary>
/// <param name="CliSettings">The CLI settings harvested alongside the login — empty when the session
/// was unattended, when the adapter declares none, or when nothing has been approved yet. Persisted
/// PER REPOSITORY by the caller: never into the keychain, and never across repos.</param>
/// <param name="RepoHandle">Which repository the session belonged to. The harvest sweep walks every
/// agent on the daemon, so this — not "whichever repo is open" — is what files the settings correctly.</param>
public sealed record AgentStopOutcome(
    bool Stopped, string AgentKind, IReadOnlyList<CliLoginFile> CliCredentials,
    IReadOnlyList<CliSettingsFileEntry> CliSettings, string RepoHandle = "");

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
    // A SEPARATE HTTP/2 connection for StreamQueueAsync — see its doc comment. Its own field/factory
    // call (not Channel()'s) so it never shares a connection, and therefore a flow-control window, with
    // the terminal's continuous PTY stream or anything else on the shared channel.
    private GrpcChannel? _streamChannel;
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
        IReadOnlyList<CliLoginFile>? cliCredentials = null,
        IReadOnlyList<CliSettingsFileEntry>? cliSettings = null)
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

        if (cliSettings is not null)
        {
            foreach (var file in cliSettings)
            {
                request.CliSettings.Add(new Mainguard.Protos.V1.CliSettingsFile
                {
                    Root = file.Root,
                    Path = file.Path,
                    Content = Google.Protobuf.ByteString.CopyFrom(file.Content),
                });
            }
        }

        var response = await client.SpawnAgentAsync(request, CallOptions(ct, deadline));
        return response.AgentId;
    }

    /// <summary>
    /// Resumes a stranded merge-queue entry: a jail spawned onto the id that entry ALREADY has, with the
    /// worktree standing on its existing <c>agent/&lt;id&gt;</c> branch.
    ///
    /// <para>A refusal comes back as an ordinary response with <c>Resumed == false</c> and a reason, not as
    /// an exception — so a caller must never read "no throw" as "it resumed". The request carries no actor
    /// and no role: the identity is daemon-derived, and a resume structurally cannot mint a coordinator.</para>
    /// </summary>
    public async Task<ResumeAgentResponse> ResumeAgentAsync(
        string repoHandle, string agentId, string agentKind, string modelApiKey,
        CancellationToken ct, TimeSpan? deadline = null,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<CliLoginFile>? cliCredentials = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var request = new ResumeAgentRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            AgentKind = agentKind ?? string.Empty,
            ModelApiKey = modelApiKey ?? string.Empty,
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

        return await client.ResumeAgentAsync(request, CallOptions(ct, deadline));
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

    /// <summary>Human per-agent pause (docker pause on the jail; refusal-as-response).</summary>
    public async Task<PauseAgentResponse> PauseAgentAsync(string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        return await client.PauseAgentAsync(new PauseAgentRequest { AgentId = agentId }, CallOptions(ct, deadline));
    }

    /// <summary>Human per-agent resume ("unpause" — ResumeAgent is the stranded-entry adoption).</summary>
    public async Task<UnpauseAgentResponse> UnpauseAgentAsync(string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        return await client.UnpauseAgentAsync(new UnpauseAgentRequest { AgentId = agentId }, CallOptions(ct, deadline));
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
        return new AgentStopOutcome(
            response.Stopped, response.AgentKind, credentials, SettingsOf(response.CliSettings),
            response.RepoHandle);
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
        // Stopped:false — the agent is still running; only the harvested payload matters here.
        return new AgentStopOutcome(
            false, response.AgentKind, credentials, SettingsOf(response.CliSettings), response.RepoHandle);
    }

    /// <summary>The wire settings entries as the client's own record. One place, so the stop leg and
    /// the live-harvest leg cannot diverge in how they read the same message.</summary>
    private static IReadOnlyList<CliSettingsFileEntry> SettingsOf(
        IEnumerable<Mainguard.Protos.V1.CliSettingsFile> files) =>
        files.Select(f => new CliSettingsFileEntry(f.Root, f.Path, f.Content.ToByteArray())).ToArray();

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
    /// caller re-subscribes to reconnect). No wall-clock deadline — long-lived, ended by cancellation.
    ///
    /// <para><b>On its own HTTP/2 connection</b> (<see cref="StreamChannel"/>), not the shared
    /// <see cref="Channel"/> — field bug, found live 2026-08-20: with a coordinator's terminal attached
    /// (<c>TerminalService/Attach</c>, a continuous PTY byte stream), a fresh queue entry's push could sit
    /// unsent for minutes on the shared connection's flow-control window, which the chatty terminal
    /// stream keeps saturated. The rail's data was correct the entire time — <c>GetQueue()</c> answered
    /// right away over a fresh connection — only THIS delivery was starved. Reproduced with the terminal
    /// idling, not even actively printing, so it is the open stream's flow-control reservation, not its
    /// throughput, that was the problem.</para>
    /// </summary>
    public async IAsyncEnumerable<QueueUpdate> StreamQueueAsync(
        string repoHandle, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(StreamChannel());
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

    /// <summary>
    /// RT-D1 step 3', the non-merge terminal: hands the repo's merge lease back with nothing recorded,
    /// after a Windows-side merge that refused or failed. Records no outcome and fires no stale cascade —
    /// its whole job is that a refused merge does not strand the repo's one lease.
    /// </summary>
    /// <returns>True when this call released the lease; false when it named no outstanding lease
    /// (already confirmed/released) — an idempotent no-op, never an error.</returns>
    public async Task<bool> AbandonMergeAsync(
        string repoHandle, string agentId, string leaseId, string reason,
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        var response = await client.AbandonMergeAsync(new AbandonMergeRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            LeaseId = leaseId,
            Reason = reason ?? string.Empty,
        }, CallOptions(ct, deadline));
        return response.Released;
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

    /// <summary>
    /// The human drops a queue entry (terminal <c>Discarded</c>, recorded daemon-side with an actor and a
    /// timestamp). The request carries no identity field on purpose — the daemon derives the actor from
    /// the connection, so there is nothing here for a client to assert.
    /// </summary>
    public async Task<DiscardEntryResponse> DiscardEntryAsync(
        string repoHandle, string agentId, string reason, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.DiscardEntryAsync(new DiscardEntryRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            Reason = reason ?? string.Empty,
        }, CallOptions(ct, deadline));
    }

    /// <summary>Rejects a VERIFIED entry in review (terminal). Same identity discipline as
    /// <see cref="DiscardEntryAsync"/>: the request carries no actor field — the daemon derives it.</summary>
    public async Task<RejectEntryResponse> RejectEntryAsync(
        string repoHandle, string agentId, string reason, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.RejectEntryAsync(new RejectEntryRequest
        {
            RepoHandle = repoHandle,
            AgentId = agentId,
            Reason = reason ?? string.Empty,
        }, CallOptions(ct, deadline));
    }

    /// <summary>Unpauses a jail parked mid-rebase and asks the worker to finish resolving its own
    /// conflict. The instruction is composed daemon-side — the request has no prompt field for a client to
    /// fill, by construction.</summary>
    public async Task<ResolveConflictWithAgentResponse> ResolveConflictWithAgentAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.ResolveConflictWithAgentAsync(
            new ResolveConflictWithAgentRequest { RepoHandle = repoHandle, AgentId = agentId },
            CallOptions(ct, deadline));
    }

    /// <summary><c>git rebase --abort</c> in the parked worktree, then the jail runs again.</summary>
    public async Task<AbortRebaseResponse> AbortRebaseAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.AbortRebaseAsync(
            new AbortRebaseRequest { RepoHandle = repoHandle, AgentId = agentId },
            CallOptions(ct, deadline));
    }

    /// <summary>Clears a <c>Verifying</c> entry with no run behind it, returning it to <c>Working</c>.</summary>
    public async Task<ClearStalledVerificationResponse> ClearStalledVerificationAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.ClearStalledVerificationAsync(
            new ClearStalledVerificationRequest { RepoHandle = repoHandle, AgentId = agentId },
            CallOptions(ct, deadline));
    }

    /// <summary>
    /// H4: the CONTENT of the entry's last verification artifact — never its daemon path (G-14). The
    /// daemon bounds it to a tail and says so via <c>Truncated</c>; this is a plain read that runs
    /// nothing, so it takes the ordinary RPC deadline rather than <c>RunVerification</c>'s minutes-long one.
    /// </summary>
    public async Task<GetVerificationLogResponse> GetVerificationLogAsync(
        string repoHandle, string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(Channel());
        return await client.GetVerificationLogAsync(
            new GetVerificationLogRequest { RepoHandle = repoHandle, AgentId = agentId },
            CallOptions(ct, deadline));
    }

    // ---- Dev-only queue seeding (docs/design/queue-seeding.md) -----------
    // A daemon started without MAINGUARD_ENABLE_QUEUE_SEEDING never maps this service, so these
    // calls answer UNIMPLEMENTED there — which is the dev panel's hide signal, not an error.

    /// <summary>The seeding availability probe + the daemon's enumeration of seeded entries.</summary>
    public async Task<GetSeedingStatusResponse> GetSeedingStatusAsync(
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new QueueSeedingService.QueueSeedingServiceClient(Channel());
        return await client.GetSeedingStatusAsync(new GetSeedingStatusRequest(), CallOptions(ct, deadline));
    }

    /// <summary>Seeds one ordered batch of queue entries (per-entry verbatim refusals in the body).</summary>
    public async Task<SeedQueueEntriesResponse> SeedQueueEntriesAsync(
        SeedQueueEntriesRequest request, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new QueueSeedingService.QueueSeedingServiceClient(Channel());
        return await client.SeedQueueEntriesAsync(request, CallOptions(ct, deadline));
    }

    /// <summary>Appends real commits to a seeded branch and drives the real new-commits invalidation.</summary>
    public async Task<PushCommitsResponse> PushSeedCommitsAsync(
        string repoHandle, string agentId, int count, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new QueueSeedingService.QueueSeedingServiceClient(Channel());
        return await client.PushCommitsAsync(
            new PushCommitsRequest { RepoHandle = repoHandle, AgentId = agentId, Count = count },
            CallOptions(ct, deadline));
    }

    /// <summary>Removes every seeded entry of a repo (structurally seed- scoped daemon-side).</summary>
    public async Task<ClearSeededEntriesResponse> ClearSeededEntriesAsync(
        string repoHandle, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new QueueSeedingService.QueueSeedingServiceClient(Channel());
        return await client.ClearSeededEntriesAsync(
            new ClearSeededEntriesRequest { RepoHandle = repoHandle }, CallOptions(ct, deadline));
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

    /// <summary>Asks an escalated worker for one fresh plan (contract §3.1, 2026-09-03).</summary>
    public async Task<bool> RequestNewPlanAsync(string planId, string guidance, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        var response = await client.RequestNewPlanAsync(
            new RequestNewPlanRequest { PlanId = planId, Guidance = guidance ?? string.Empty }, CallOptions(ct, deadline));
        return response.Requested;
    }

    /// <summary>
    /// Sets the operator's plan-mode toggle and returns the state the DAEMON now holds.
    ///
    /// <para>The response is read back rather than assumed: it is the daemon's own answer, so a client
    /// that renders it cannot show a gate the daemon is not applying.</para>
    /// </summary>
    public async Task<PlanModeState> SetPlanModeAsync(bool enabled, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        return await client.SetPlanModeAsync(
            new SetPlanModeRequest { Enabled = enabled }, CallOptions(ct, deadline));
    }

    /// <summary>Reads the operator's plan-mode toggle.</summary>
    public async Task<PlanModeState> GetPlanModeAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PlanApprovalService.PlanApprovalServiceClient(Channel());
        return await client.GetPlanModeAsync(new GetPlanModeRequest(), CallOptions(ct, deadline));
    }

    // ---- P2-14 kill switch (P2-47 #3) ----

    /// <summary>Engages the kill switch: freeze-queue-first, then yield fan-out (SA-1/F4 + RT-D4).</summary>
    public async Task<EngageKillResponse> EngageKillAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new KillSwitchService.KillSwitchServiceClient(Channel());
        return await client.EngageAsync(new EngageKillRequest(), CallOptions(ct, deadline));
    }

    /// <summary>
    /// Resumes from a kill: un-pauses every jail the kill switch itself froze, then clears the freeze.
    /// Returns the whole report — <c>AgentsResumeFailed &gt; 0</c> means those jails are still paused, which
    /// the caller must not paper over (ISSUES-LOG #17: the release used to clear a flag and nothing else).
    /// </summary>
    public async Task<ResumeKillResponse> ResumeKillAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new KillSwitchService.KillSwitchServiceClient(Channel());
        return await client.ResumeAsync(new ResumeKillRequest(), CallOptions(ct, deadline));
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

    /// <summary>
    /// Streams live per-agent CPU/RAM sampled from the container engine, plus whether each agent's spend
    /// can be measured at all. Whole-set snapshots, so a torn-down agent drops out rather than lingering.
    /// Sampling is driven by this subscription — dropping it stops the daemon's engine calls.
    /// </summary>
    public async IAsyncEnumerable<AgentResourcesSnapshot> StreamAgentResourcesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        using var call = client.StreamAgentResources(new StreamAgentResourcesRequest(), AuthOnly(ct));
        await foreach (var snapshot in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return snapshot;
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

    // ---- P2-12 external-PR intake configuration ----

    /// <summary>Reads the daemon's external-PR-intake configuration and its persisted subscriptions.</summary>
    public async Task<GetPrIntakeSettingsResponse> GetPrIntakeSettingsAsync(
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PrIntakeService.PrIntakeServiceClient(Channel());
        return await client.GetPrIntakeSettingsAsync(new GetPrIntakeSettingsRequest(), CallOptions(ct, deadline));
    }

    /// <summary>Writes the daemon's external-PR-intake configuration. Returns it AS PERSISTED (the daemon
    /// clamps the interval and substitutes its default bot list for an empty one), so a caller that
    /// renders the result is showing what the poller will actually run with.</summary>
    public async Task<PrIntakeSettings> UpdatePrIntakeSettingsAsync(
        PrIntakeSettings settings, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PrIntakeService.PrIntakeServiceClient(Channel());
        var response = await client.UpdatePrIntakeSettingsAsync(
            new UpdatePrIntakeSettingsRequest { Settings = settings }, CallOptions(ct, deadline));
        return response.Settings ?? new PrIntakeSettings();
    }

    /// <summary>Subscribes one source. <c>Added</c> is false for an already-subscribed
    /// <c>(host, owner, repo, filter)</c> — idempotent, never an error.</summary>
    public async Task<SubscribePrIntakeSourceResponse> SubscribePrIntakeSourceAsync(
        PrIntakeSource source, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new PrIntakeService.PrIntakeServiceClient(Channel());
        return await client.SubscribePrIntakeSourceAsync(
            new SubscribePrIntakeSourceRequest { Source = source }, CallOptions(ct, deadline));
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

    private GrpcChannel StreamChannel() => _streamChannel ??= _channelFactory();

    private void ResetChannel()
    {
        var old = _channel;
        _channel = null;
        old?.Dispose();

        var oldStream = _streamChannel;
        _streamChannel = null;
        oldStream?.Dispose();
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
