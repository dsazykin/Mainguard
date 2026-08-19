using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Daemon;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Mainguard.Server.Runtime;
using Mainguard.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
namespace Mainguard.Server;

/// <summary>
/// Builds and runs the daemon host. Kept separate from the entry point so both the
/// real <c>Program</c> and the in-proc <c>WebApplicationFactory&lt;Program&gt;</c> test
/// tier share one configuration path, and the port-bound test can start a real host.
/// </summary>
public static class DaemonHost
{
    /// <summary>
    /// Configures services + interceptors + the gRPC service map on an existing builder.
    /// Shared by the entry point and by <see cref="WebApplicationFactory"/>-based tests.
    /// </summary>
    public static SessionTransportCertificates ConfigureServices(
        WebApplicationBuilder builder, DaemonOptions options)
    {
        // Wipe the framework's default providers, then (unless --smoke) install the daemon's two-sink
        // pipeline: a single-line journald-friendly console (systemd captures stdout under -u mainguardd)
        // + per-subsystem rolling files under ~/.mainguard/logs. --smoke stays byte-silent so the Windows
        // daemon-smoke CI job's "prints nothing" contract holds. G-13 is about secret TRANSPORT, not
        // silence — the masked pipeline below is compliant (SecretFieldMask still redacts every body).
        builder.Logging.ClearProviders();

        // Session token: created user-only-readable on disk; the interceptor compares
        // against it. The in-proc test tier isolates the path via Daemon:TokenPath
        // (env Daemon__TokenPath) and reads the created token back from that file.
        var tokenPath = options.TokenPath ?? builder.Configuration["Daemon:TokenPath"];
        var logsDir = ResolveLogsDirectory(tokenPath);
        if (!options.Smoke)
        {
            AddDaemonLogging(builder.Logging, logsDir);
        }

        // Startup + migration milestones run inside ConfigureServices, BEFORE the host is built (the
        // #194 migration-lock code is static), so they need their own bootstrap LoggerFactory over the
        // SAME sinks — the file writers are process-static, so the bootstrap and runtime factories share
        // one writer per file. Disposed at the end of this method; the shared writers survive it.
        using var bootstrap = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders();
            if (!options.Smoke)
            {
                AddDaemonLogging(logging, logsDir);
            }
        });
        var lifecycle = bootstrap.CreateLogger(DaemonLogCategories.Lifecycle);
        var migration = bootstrap.CreateLogger(DaemonLogCategories.Migration);
        lifecycle.LogInformation(
            "options parsed: port={Port} localDev={LocalDev} smoke={Smoke} logsDir={LogsDir}",
            options.Port, options.LocalDev, options.Smoke, logsDir);

        var tokenFile = SessionTokenFile.Create(tokenPath);
        builder.Services.AddSingleton(tokenFile);
        lifecycle.LogInformation("session token ready");

        // MG-19: the peer-authentication layer. Fresh per-session mTLS material is written beside the
        // token with the same user-only permissions, so the bearer token is no longer the sole gate on
        // the control plane. Returned to Build so the Kestrel listener can present/pin it.
        var transportCertificates = SessionTransportCertificates.Create(tokenFile.Path);
        builder.Services.AddSingleton(transportCertificates);
        lifecycle.LogInformation("session transport credentials ready (mutual TLS, pinned)");

        // P2-15: IAuditLog is registered by GatewayServiceRegistration.Register below — the audit
        // chain rides the same daemon-DB posture decision as the gateway stores (ChainedAuditLog
        // when the DB opens, InMemoryAuditLog fallback so the daemon always starts). Everything
        // here resolves it lazily through DI, so the later registration point changes nothing.
        builder.Services.AddSingleton<AgentSessionStore>();

        // P2-14 governance spine: role registry + terminal-lock registry (the RoleInterceptor enforces
        // both daemon-side), the daemon-derived approver-identity resolver (SA-1/F2), the plan-approval
        // service (restart-safe JSON store next to the session token), the shared kill-switch freeze gate
        // (SA-1/F4 — merge/spawn consult it), and the kill switch itself.
        builder.Services.AddSingleton<Auth.ConnectionRoleRegistry>();
        builder.Services.AddSingleton<Auth.TerminalLockRegistry>();
        builder.Services.AddSingleton<Auth.IApproverIdentityResolver, Auth.PeerCredentialIdentityResolver>();
        builder.Services.AddSingleton(sp => new Mainguard.Agents.Agents.Orchestrator.PlanApprovalService(
            store: new Mainguard.Agents.Agents.Orchestrator.JsonPlanApprovalStore(ResolvePlanStorePath(tokenPath)),
            audit: sp.GetRequiredService<IAuditLog>()));
        // P2-47 #9: the coordinator conversation the CoordinatorService streams. Registered with no reply
        // engine in the shipped daemon — the live LLM-backed CoordinatorAgent adapter is the one leg that
        // needs a real model (the documented un-verifiable leg); the transcript store + streaming are real
        // regardless, and the in-proc test injects a real CoordinatorAgent-backed engine to drive it.
        builder.Services.AddSingleton(_ => new Mainguard.Agents.Agents.Orchestrator.CoordinatorConversationService());

        builder.Services.AddSingleton<Mainguard.Agents.Agents.Orchestrator.KillSwitchGate>();

        // Human per-agent Pause/Resume (PauseAgent/UnpauseAgent) + the arbiter every repo's
        // YieldProtocol consults so the human's pause and the cascade's pause never fight.
        builder.Services.AddSingleton<Runtime.HumanPauseLedger>();
        builder.Services.AddSingleton<Runtime.AgentPauseService>();
        // MG-8: the wired kill target must CONTAIN, not relabel. SandboxKillTarget severs terminal input
        // (TerminalLockRegistry + SessionLeader) and docker-pauses the jail through the substrate's sandbox
        // engine, then marks the state — the state mark alone (the old SessionStoreKillTarget) left every
        // worker executing and every terminal typeable after the emergency stop. Resolved via a factory so
        // the target takes the ISandboxEngine directly (unit-testable with a fake engine) while production
        // still gets the one substrate facade's engine.
        builder.Services.AddSingleton<Mainguard.Agents.Agents.Orchestrator.IKillTarget>(sp =>
            new Runtime.SandboxKillTarget(
                store: sp.GetRequiredService<AgentSessionStore>(),
                sandboxes: sp.GetRequiredService<IAgentEnvironment>().Sandboxes,
                leader: sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                locks: sp.GetRequiredService<Auth.TerminalLockRegistry>(),
                loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
        // The DURABLE kill journal. Registered (rather than inlined) so something holds the reference: the
        // previous composition passed no journal at all, and KillSwitch's `?? new InMemoryKillJournal()`
        // built a sink no one could reach — step 3's "snapshot written BEFORE returning" wrote into an
        // object that died with the process, which is the one process an emergency stop is followed by
        // restarting. Lives next to the (test-isolated) session token like the plan store.
        builder.Services.AddSingleton<Mainguard.Agents.Agents.Orchestrator.IKillJournal>(
            new Mainguard.Agents.Agents.Orchestrator.JsonKillJournal(ResolveKillJournalPath(tokenPath)));

        // Every optional argument stated, because every one of them defaults to something weaker and
        // silent — `audit:` alone could be deleted here with the whole Kill-filtered suite still green.
        // KillSwitch.WiredOptionalControls records what was passed and CompositionRootResolutionTests pins
        // it, so this list cannot shrink unnoticed again.
        builder.Services.AddSingleton(sp => new Mainguard.Agents.Agents.Orchestrator.KillSwitch(
            gate: sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.KillSwitchGate>(),
            target: sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.IKillTarget>(),
            journal: sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.IKillJournal>(),
            audit: sp.GetRequiredService<IAuditLog>(),
            // NO control-channel RTT is measured in this daemon, and saying so is the point. P2-09's
            // IAgentControlChannel has no production transport — SandboxKillTarget.RequestYieldAsync
            // answers false without a round trip — so there is no honest EWMA to pass. The previous
            // silent `() => TimeSpan.Zero` default made RttWouldExceedCeiling a constant false, which
            // reads on a KillReport as "the control channel was healthy": a claim nothing here could
            // support. The sentinel stamps RttMeasured=false on the report and the journal snapshot
            // instead. Replace it with a real EWMA when a transport exists; the RT-D4 arm is already
            // covered in both directions by KillSwitchTests and KillContainmentTests.
            rttBudget: Mainguard.Agents.Agents.Orchestrator.KillSwitchTiming.UnmeasuredRtt,
            // ...and the sink that arm feeds, wired now so lighting up the measurement is a one-line
            // change rather than two. Dormant while rttBudget is the sentinel, by construction.
            onRttSpike: epoch => sp.GetRequiredService<IAuditLog>().Append(
                new Mainguard.Git.Audit.AuditEvent("killswitch_rtt_spike", new System.Collections.Generic.Dictionary<string, string>
                {
                    ["kill_epoch_id"] = epoch,
                    ["ceiling_seconds"] =
                        Mainguard.Agents.Agents.Orchestrator.KillSwitchTiming.Ceiling.TotalSeconds
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                }))));

        // P2-07: the network-transparency sink (P2-17 supplies the persisted/streamed impl). The
        // egress proxy + daemon git proxy record every fetch/verdict here; the allowlist change log
        // rides the IAuditLog above. Wrapped so each verdict also tees a summary into the Egress log
        // category (egress.log / journal) — the diagnostic complement to the P2-17 feed.
        builder.Services.AddSingleton<INetworkTransparencyLog>(sp =>
            new LoggingTransparencyLog(
                new InMemoryNetworkTransparencyLog(),
                sp.GetRequiredService<ILoggerFactory>()));

        // P2-06/P2-07: one substrate facade resolved per platform; RepoSyncGrpcService obtains the
        // provisioner/worktree manager, and the P2-07 spawn path obtains the hardened sandbox engine +
        // default-deny egress policy, from it. The per-platform choice lives in
        // AgentEnvironmentFactory (macOS → macos-host, everything else → WSL2 exactly as before).
        // (The A6 DaemonGitProxy is constructed per-repo from its allowlisted prefixes when the
        // sandbox spawn path wires it in.)
        // MG-4: the substrate is told the gateway address the daemon actually bound, so the egress proxy
        // PERMITS it. Without that entry a confined jail is pointed at an endpoint Mainguard's own
        // default-deny filter refuses — the confinement would break the agent instead of metering it.
        builder.Services.AddSingleton<IAgentEnvironment>(sp =>
            AgentEnvironmentFactory.CreateForHost(
                sp.GetRequiredService<IAuditLog>(),
                Gateway.GatewayServiceRegistration.BuildGatewayUpstream(options)));

        // P2-47 #8: the real sandboxed-spawn chain behind AgentService.SpawnAgent (provision worktree →
        // ensure default-deny egress → start hardened jail). Kept out of the gRPC class (validation+dispatch
        // only); degrades to a session-only record when the repo handle is not provisioned.
        // The installed-CLI catalog is shared (launcher + the ListInstalledAdapters RPC); it reads the
        // daemon-side registry fresh per call, so a singleton carries no staleness. CreateForHost
        // resolves the registry per substrate (the VM layout in the VM, ~/mainguard/adapters on mac).
        builder.Services.AddSingleton(
            _ => Mainguard.Agents.Agents.Adapters.InstalledAdapterCatalog.CreateForHost());
        builder.Services.AddSingleton<Runtime.SandboxAgentLauncher>();

        // Tier-1 daemon fast-path: the GetDaemonInfo skew probe's data source (daemon assembly
        // version + the /etc/mainguardos-release payload stamp). Instance-registered so the default
        // release-file path applies; tests override with a temp-file provider.
        builder.Services.AddSingleton(new Runtime.DaemonInfoProvider());

        // PR3 (CLI-as-coordinator): the spawn workflow shared by the RPC and the coordinator's in-jail
        // mainguard-agent channel — CLI-under-TTY binding (AgentCliBinder → TerminalSessionManager +
        // SessionLeader), the per-coordinator Unix-socket IPC server (endpoint dirs next to the
        // (test-isolated) session token), and the memory-only per-kind key cache.
        builder.Services.AddSingleton<Runtime.SessionKeyCache>();
        // P2-18: the daemon-wide terminal engine selection (libvterm|interim). Resolve() applies
        // the native-availability degrade, so a libvterm flag on a box without the library still
        // yields working (interim) terminals. New bound sessions pick this up via AgentCliBinder.
        builder.Services.AddSingleton(Terminal.TerminalEngineConfig.Resolve(options.TerminalEngine));
        builder.Services.AddSingleton<Runtime.AgentCliBinder>();
        builder.Services.AddSingleton(new Runtime.CoordinatorIpcServer(ResolveAgentIpcRoot(tokenPath)));
        // MG-2: the active-Managed-worker ceiling the wired shim spawn path (AgentSpawnService) enforces
        // server-side, so a coordinator agent cannot fan out unlimited workers via mainguard-agent spawn.
        builder.Services.AddSingleton(new Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits());
        builder.Services.AddSingleton<Runtime.AgentSpawnService>();
        // The human-only resume path for a stranded merge-queue entry (AgentService.ResumeAgent). It
        // depends on the merge-queue registry registered by GatewayServiceRegistration below — DI resolves
        // at first use, so the ordering here is presentational, not a dependency.
        builder.Services.AddSingleton<Runtime.AgentResumeService>();

        // The Resource Monitor's data source. The tab shipped rendering per-agent CPU/RAM over a sampler
        // that was never written — the client hard-coded both to 0 — so every agent read a convincing 0%.
        // The Docker client is built here and shared rather than per call: this one is consulted on a poll
        // loop, and a fresh client per tick would churn connections to the daemon socket for no reason.
        builder.Services.AddSingleton<Docker.DotNet.IDockerClient>(
            _ => Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient());

        // macos-host: hold a sleep assertion while any jail runs (App Nap / idle sleep must not
        // stall a verification); tied to the daemon's own lifetime via caffeinate -w.
        if (OperatingSystem.IsMacOS())
        {
            builder.Services.AddHostedService<Runtime.MacSleepAssertion>();
        }
        builder.Services.AddSingleton<Mainguard.Agents.Agents.Sandbox.IContainerResourceSampler>(sp =>
        {
            try
            {
                return new Mainguard.Agents.Agents.Sandbox.DockerResourceSampler(
                    sp.GetRequiredService<Docker.DotNet.IDockerClient>());
            }
            catch (Exception ex)
            {
                // No engine to talk to. Report every agent as explicitly unknown rather than failing the
                // RPC — and emphatically rather than reporting zeros, which would look like a working
                // monitor observing an idle fleet.
                return new Mainguard.Agents.Agents.Sandbox.UnavailableContainerResourceSampler(
                    ex.GetType().Name);
            }
        });
        builder.Services.AddSingleton(sp => new Runtime.AgentResourceProbe(
            sp.GetRequiredService<Runtime.AgentSessionStore>(),
            sp.GetRequiredService<Mainguard.Agents.Agents.Sandbox.IContainerResourceSampler>(),
            sp.GetService<Gateway.AgentGatewayCredentials>()));
        // Which repositories this daemon has provisioned, and where the user's copy of each one is. The
        // repo hash is one-way, so without this the daemon could not name the repo a handle refers to —
        // which is exactly why the external-PR intake's target resolver was hardwired to null.
        builder.Services.AddSingleton<Runtime.ActiveRepoIndex>();

        // P2-47 #7: the merge-diff bridge behind MergeQueueService.GetMergeDiff — the agent-branch-vs-main
        // diff the review cockpit renders (StreamQueue doesn't carry it). Reuses the audited git path +
        // pure PatchParser over the daemon's bare mirror.
        builder.Services.AddSingleton<Mainguard.Agents.Agents.Orchestrator.IMergeBranchDiffService>(sp =>
            new Mainguard.Agents.Agents.Orchestrator.MergeBranchDiffService(
                sp.GetRequiredService<IAgentEnvironment>().Repos,
                // MG-3: publish the agent's branch from its own repository into the mirror before
                // diffing it — the review cockpit reads the mirror.
                (repoHash, agentId) =>
                    sp.GetRequiredService<IAgentEnvironment>().Worktrees.PublishAgentBranch(repoHash, agentId)));

        // Terminal sessions: agents launched with an installed CLI get a long-lived BOUND session
        // (AgentCliBinder → docker exec under a real PTY) that Attach streams with replay across
        // re-attaches. The per-attach factory ctor remains the TI-P2-03 wiring-test shape; with
        // neither, the attach falls back to the P2-02 echo.
        builder.Services.AddSingleton<TerminalSessionManager>();

        // P2-09: the session leader owns the per-agent PTY fds and the durable, leader-owned registry
        // the daemon reattaches through on boot (no daemon-side pidfiles). The registry lives next to
        // the (test-isolated) session token so each in-proc host gets its own.
        var leaderRegistryPath = ResolveLeaderRegistryPath(tokenPath);
        builder.Services.AddSingleton(new Mainguard.Agents.Agents.Orchestrator.LeaderRegistry(leaderRegistryPath));
        builder.Services.AddSingleton<Mainguard.Agents.Agents.Orchestrator.SessionLeader>();

        // P2-08: the AI gateway (token bucket + budgets + admission + boot reconciler). Persisted to
        // the daemon SQLite DB when available, in-memory otherwise so the daemon always starts. The DB
        // sits next to the (test-isolated) session token so each in-proc host gets its own DB. The
        // migration log delegate makes the #194 lock-hang diagnosable from migration.log/journal in
        // seconds (the "preparing db / stale lock cleared / migrate ok / watchdog fired" milestones).
        Gateway.GatewayServiceRegistration.Register(
            builder,
            ResolveDataPath(options, builder.Configuration, tokenPath),
            log: message => migration.LogInformation("{Milestone}", message),
            options: options);

        // P2-15 retention: 90-day expiry as chained redactions (once at boot + daily). No-op on the
        // in-memory fallback journal.
        builder.Services.AddHostedService<Runtime.AuditRetentionService>();
        // P2-15 anchoring: hourly best-effort RFC 3161 sweep — heads queue by policy; nothing is
        // sent unless MAINGUARD_TSA_URL names an endpoint (no default third-party traffic).
        builder.Services.AddHostedService<Runtime.AuditAnchorService>();

        builder.Services.AddGrpc(o =>
        {
            // EVERY RPC is authenticated (no public-method allowlist), then role/terminal-lock enforced
            // (P2-14 — coordinator denied merge/approval RPCs, locked-worker input severed), then
            // access-logged through the secret field mask. Order: authenticate, authorize, log.
            o.Interceptors.Add<BearerTokenInterceptor>();
            o.Interceptors.Add<RoleInterceptor>();
            o.Interceptors.Add<SecretMaskingInterceptor>();
        });

        lifecycle.LogInformation("gRPC pipeline configured; services mapping next");
        return transportCertificates;
    }

    /// <summary>
    /// The daemon's two-sink logging, shared by the runtime host builder and the pre-DI bootstrap
    /// factory so both feed the same per-subsystem files: a single-line, color-free, ISO-8601 console
    /// (systemd captures stdout under <c>-u mainguardd</c>) + <see cref="SubsystemFileLoggerProvider"/>.
    /// The floor is <c>MAINGUARD_LOG_LEVEL</c> (default Information); framework noise
    /// (Microsoft.AspNetCore / Grpc) is filtered to Warning so idle volume stays ~zero.
    /// </summary>
    private static void AddDaemonLogging(Microsoft.Extensions.Logging.ILoggingBuilder logging, string logsDir)
    {
        var minLevel = ResolveMinLevel();
        logging.SetMinimumLevel(minLevel);
        logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.ColorBehavior = LoggerColorBehavior.Disabled;
            o.TimestampFormat = "O ";
        });
        logging.AddProvider(new SubsystemFileLoggerProvider(logsDir, minLevel: minLevel));
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddFilter("Grpc", LogLevel.Warning);
    }

    /// <summary>The daemon log floor: <c>MAINGUARD_LOG_LEVEL</c> (Trace/Debug/Information/…) for deep
    /// dives, Information by default; an unparseable value falls back to Information.</summary>
    private static LogLevel ResolveMinLevel()
        => Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("MAINGUARD_LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;

    /// <summary>
    /// The per-subsystem logs directory: next to the (test-isolated) session token so each in-proc host
    /// writes its own logs (cleaned up with the temp dir); otherwise <c>~/.mainguard/logs</c>. Mirrors
    /// <see cref="ResolveDataPath"/> / <see cref="ResolveLeaderRegistryPath"/> so tests never pollute the
    /// real user data root, while production always resolves to the upgrade-surviving canonical path.
    /// </summary>
    private static string ResolveLogsDirectory(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "logs");
            }
        }

        return DaemonLogSubsystems.LogsDirectory();
    }

    /// <summary>
    /// The daemon SQLite path for the P2-08 spend ledger. Explicit <see cref="DaemonOptions.DataPath"/>
    /// or <c>Daemon:DataPath</c> wins; otherwise it sits next to the session token (so the in-proc test
    /// tier's per-host temp token dir also isolates the DB); otherwise the OS app-data default.
    /// </summary>
    private static string ResolveDataPath(DaemonOptions options, Microsoft.Extensions.Configuration.IConfiguration config, string? tokenPath)
    {
        var explicitPath = options.DataPath ?? config["Daemon:DataPath"];
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "mainguard-daemon.db");
            }
        }

        // MainguardPaths, not GetFolderPath: the latter returns "" on Unix for a not-yet-materialized
        // home subdir — this fallback must never yield a relative path under a service context.
        return Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "mainguard-daemon.db");
    }

    /// <summary>
    /// The P2-09 leader-registry path: next to the (test-isolated) session token so each in-proc host
    /// gets its own leader-owned state; otherwise the OS app-data default.
    /// </summary>
    private static string ResolveLeaderRegistryPath(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "mainguard-leader-sessions.json");
            }
        }

        return Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "mainguard-leader-sessions.json");
    }

    /// <summary>
    /// The per-coordinator agent-IPC root (Unix sockets + spawn shims): next to the (test-isolated)
    /// session token so each in-proc host gets its own; otherwise the OS app-data default. On the VM
    /// this is an ext4 path (a G-11-legal mount source).
    /// </summary>
    private static string ResolveAgentIpcRoot(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "agent-ipc");
            }
        }

        return Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "agent-ipc");
    }

    /// <summary>
    /// The P2-14 plan-approval JSON store path: next to the (test-isolated) session token so each in-proc
    /// host gets its own restart-safe store; otherwise the OS app-data default.
    /// </summary>
    private static string ResolvePlanStorePath(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "mainguard-plans.json");
            }
        }

        return Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "mainguard-plans.json");
    }

    /// <summary>Where the durable kill journal lands — beside the (test-isolated) session token, exactly
    /// like the plan store, so an in-proc test host never appends to the real daemon's record.</summary>
    private static string ResolveKillJournalPath(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "mainguard-kills.jsonl");
            }
        }

        return Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "mainguard-kills.jsonl");
    }

    /// <summary>Maps the gRPC services. Shared by entry point and tests.</summary>
    public static void MapServices(WebApplication app)
    {
        app.MapGrpcService<AgentGrpcService>();
        app.MapGrpcService<TerminalGrpcService>();
        app.MapGrpcService<RepoSyncGrpcService>();
        app.MapGrpcService<GatewayGrpcService>();
        app.MapGrpcService<MergeQueueGrpcService>();
        app.MapGrpcService<PrIntakeGrpcService>();
        app.MapGrpcService<PlanApprovalGrpcService>();
        app.MapGrpcService<KillSwitchGrpcService>();
        app.MapGrpcService<CoordinatorGrpcService>();
        app.MapGrpcService<EgressGrpcService>();
        app.MapGrpcService<AuditGrpcService>();
    }

    /// <summary>
    /// Builds a real (Kestrel) daemon host bound to loopback only on
    /// <see cref="DaemonOptions.Port"/>. Never binds a wildcard / non-loopback
    /// address (invariant 2).
    /// </summary>
    /// <param name="configureServices">
    /// An optional last word on the service collection, applied AFTER the daemon has registered
    /// everything (so a later <c>AddSingleton</c> wins) and BEFORE the container is built.
    ///
    /// <para>Null in every production path — this changes nothing for a daemon started by the installer,
    /// the systemd unit, or the dev loop. It exists because the model gateway can only be exercised
    /// end to end against a REAL Kestrel listener (<c>WebApplicationFactory</c> swaps in a
    /// <c>TestServer</c>, so a jail has nothing to connect to), and the one thing such a test cannot use
    /// verbatim is the forwarder's upstream leg: <see cref="Gateway.ModelProxyMiddleware"/> always dials
    /// <c>https://&lt;bound upstream&gt;</c>, i.e. the live provider. Overriding the upstream transport is
    /// what lets the test drive the whole real path — real jail, real proxy, real middleware, real
    /// ledger — while stopping at the network boundary instead of billing a real key.</para>
    /// </param>
    public static WebApplication Build(DaemonOptions options, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();

        // Services first: the session mTLS material must exist before the listener that presents it.
        var certificates = ConfigureServices(builder, options);
        configureServices?.Invoke(builder.Services);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback only, and MUTUALLY AUTHENTICATED (MG-19). "Loopback" is not an isolation
            // boundary here: under WSL2 localhostForwarding this in-VM listener is reachable from any
            // process in the Windows user's session (measured — docs/security-architecture.md), so the
            // bearer token used to be the entire gate and crossed the wire in cleartext. The listener now
            // requires a client certificate pinned to this session and is rejected at the TLS handshake
            // without one — before HTTP/2, gRPC, or the bearer interceptor. There is deliberately NO
            // plaintext fallback: a downgrade knob is a downgrade attack.
            kestrel.Listen(IPAddress.Loopback, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificates.ServerCertificate;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // Self-signed and session-scoped: there is no chain to build and no CRL to fetch.
                    // Exact-fingerprint pinning replaces both, and setting this callback overrides
                    // Kestrel's default chain validation (which would reject the untrusted issuer).
                    https.CheckCertificateRevocation = false;
                    https.ClientCertificateValidation =
                        (certificate, _, _) => certificates.IsPinnedClientCertificate(certificate);
                });
            });

            // MG-13/MG-4: the model gateway is the one listener allowed off loopback, because the
            // agent jail is on an Internal=true network where 127.0.0.1 is the container itself, so a
            // loopback gateway is unreachable by the agents it fronts. Disabled unless explicitly
            // configured, and GatewayBindPolicy refuses a wildcard or public bind outright — a
            // misconfigured value fails startup loudly rather than quietly exposing the gateway.
            // HTTP/1.1 because model-API clients speak it; the per-agent token is its authentication.
            if (TryResolveGatewayBind(options, out var gatewayAddress))
            {
                kestrel.Listen(gatewayAddress, options.GatewayPort,
                    listen => listen.Protocols = HttpProtocols.Http1);
            }
        });

        var app = builder.Build();
        UseModelGateway(app, options);
        MapServices(app);
        RegisterLifecycleLogging(app, options);
        return app;
    }

    /// <summary>
    /// Puts <see cref="Gateway.ModelProxyMiddleware"/> on the request path — the wiring whose absence
    /// meant the P2-08 gateway had no production data path at all (no <c>UseMiddleware</c> call existed
    /// anywhere in the daemon, so <c>BudgetLedger</c> was only ever written from tests).
    ///
    /// <para><b>Branched on the gateway port, and that is load-bearing.</b> The gRPC control plane and
    /// the model gateway share one Kestrel host but are different trust surfaces: the control plane is
    /// loopback + mutual TLS (MG-19), while the gateway is a private-address HTTP listener authenticated
    /// by a per-agent token. Running this middleware on the control port would put an unauthenticated
    /// HTTP shim in front of mutually-authenticated gRPC. <see cref="UseWhen"/> keeps it strictly on the
    /// gateway listener, and the branch is not added at all when the gateway is disabled.</para>
    /// </summary>
    internal static void UseModelGateway(WebApplication app, DaemonOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GatewayBindAddress))
        {
            return; // gateway disabled (the default) — the pipeline is exactly what it was.
        }

        var gatewayPort = options.GatewayPort;
        app.UseWhen(
            context => context.Connection.LocalPort == gatewayPort,
            branch => branch.UseMiddleware<Gateway.ModelProxyMiddleware>(
                (object)Gateway.ModelHosts.All));
    }

    /// <summary>
    /// Resolves the model-gateway bind address, or false when the gateway is disabled (the default).
    /// A CONFIGURED but impermissible address throws: silently falling back to loopback would leave
    /// the gateway unreachable by agents and the operator none the wiser, and silently binding it
    /// anyway is the exposure <see cref="Gateway.GatewayBindPolicy"/> exists to prevent.
    /// </summary>
    private static bool TryResolveGatewayBind(DaemonOptions options, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(options.GatewayBindAddress))
        {
            return false; // gateway disabled — nothing is bound beyond loopback gRPC.
        }

        if (!IPAddress.TryParse(options.GatewayBindAddress, out var parsed))
        {
            throw new ArgumentException(
                $"Gateway bind address '{options.GatewayBindAddress}' is not a valid IP address.");
        }

        if (!Gateway.GatewayBindPolicy.IsPermitted(parsed, out var reason))
        {
            throw new ArgumentException($"Refusing to bind the model gateway: {reason}");
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Binds the daemon's Lifecycle log to the host's start/stop signals: <c>ApplicationStarted</c> →
    /// "bound 127.0.0.1:{port}", <c>ApplicationStopping</c>/<c>Stopped</c> → the shutdown lines. Uses the
    /// built host's <see cref="ILoggerFactory"/>, so under <c>--smoke</c> (no providers) these are
    /// silent, and the log-capture test tier observes them.
    /// </summary>
    private static void RegisterLifecycleLogging(WebApplication app, DaemonOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(DaemonLogCategories.Lifecycle);
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() =>
            logger.LogInformation("bound 127.0.0.1:{Port} — daemon ready", options.Port));
        lifetime.ApplicationStopping.Register(() =>
            logger.LogInformation("shutdown requested — draining"));
        lifetime.ApplicationStopped.Register(() =>
            logger.LogInformation("stopped"));
    }

    /// <summary>
    /// Starts a real daemon host, mapping a bind failure (port already in use) to a
    /// typed <see cref="DaemonStartupException"/> naming the port.
    /// </summary>
    /// <param name="configureServices">See <see cref="Build(DaemonOptions, Action{IServiceCollection})"/> —
    /// null on every production path.</param>
    public static async Task<WebApplication> StartAsync(
        DaemonOptions options, CancellationToken ct = default,
        Action<IServiceCollection>? configureServices = null)
    {
        var app = Build(options, configureServices);
        try
        {
            await app.StartAsync(ct);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            throw new DaemonStartupException(options.Port,
                $"Mainguard daemon could not bind loopback port {options.Port} (already in use?).", ex);
        }

        return app;
    }

    /// <summary>
    /// The <c>--local-dev --smoke</c> path: start, self-probe an authenticated
    /// <c>ListAgents</c> over the loopback endpoint, exit. Prints nothing on success;
    /// returns a non-zero code on failure.
    /// </summary>
    public static async Task<int> RunSmokeAsync(DaemonOptions options)
    {
        await using var app = await StartAsync(options);
        var tokenFile = app.Services.GetRequiredService<SessionTokenFile>();

        // MG-19: the self-probe goes through the same mutually-authenticated, pinned transport a real
        // client uses — reading the credentials off disk exactly as the client does, so the smoke fails
        // if the daemon ever stops writing usable material.
        using var credentials = DaemonTransportCredentials.Load(
            Path.GetDirectoryName(Path.GetFullPath(tokenFile.Path))!);
        using var handler = new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = credentials.BuildSslOptions(),
        };

        using var channel = GrpcChannel.ForAddress(
            $"https://127.0.0.1:{options.Port}",
            new GrpcChannelOptions { HttpHandler = handler });
        var client = new AgentService.AgentServiceClient(channel);
        var metadata = new Grpc.Core.Metadata { { "authorization", $"bearer {tokenFile.Token}" } };
        var deadline = DateTime.UtcNow.AddSeconds(10);

        await client.ListAgentsAsync(new ListAgentsRequest(), metadata, deadline);

        await app.StopAsync();
        return 0;
    }
}
