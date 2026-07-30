using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Mainguard.Agents;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Services;
using Mainguard.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Mainguard.Server.Gateway;

/// <summary>
/// Wires the P2-08 gateway stack into the daemon host: the token-bucket <see cref="AiGateway"/>, the
/// persisted <see cref="BudgetLedger"/>, the <see cref="AdmissionController"/>, and the boot
/// <see cref="SwarmReconciler"/> (run through the RT-D1 ordered <see cref="DaemonBootSequence"/>). All
/// persistence is best-effort: if the daemon SQLite DB cannot be opened/migrated the stack falls back
/// to in-memory stores so the daemon still starts (the gRPC surface must never fail to bind on a DB
/// hiccup).
/// </summary>
public static class GatewayServiceRegistration
{
    public static void Register(WebApplicationBuilder builder, string dbPath, Action<string>? log = null)
    {
        var services = builder.Services;

        // Best-effort DB-backed persistence; in-memory fallback keeps the daemon startable. The optional
        // log delegate records the milestones (preparing db / stale lock cleared / migrate ok / watchdog
        // fired / db unavailable) under the daemon's Migration category so the #194 lock-hang is
        // diagnosable from migration.log; it stays optional so the TryPrepareDatabase unit tests can drive
        // this path directly (and observe the milestones) without a host.
        ISpendStore spendStore;
        IExpectedAgentStore expectedStore;
        IBudgetStore budgetStore;
        IMergeLeaseStore mergeLeaseStore;
        Func<AppDbContext>? dbFactory = null;
        if (TryPrepareDatabase(dbPath, out var factory, log: log))
        {
            dbFactory = factory;
            spendStore = new DbSpendStore(factory);
            expectedStore = new DbExpectedAgentStore(factory);
            budgetStore = new DbBudgetStore(factory);
            mergeLeaseStore = new DbMergeLeaseStore(factory);
        }
        else
        {
            spendStore = new InMemorySpendStore();
            expectedStore = new InMemoryExpectedAgentStore();
            budgetStore = new InMemoryBudgetStore();
            mergeLeaseStore = new InMemoryMergeLeaseStore();
        }

        // The P2-10 queue-state + immutable-verification stores follow the same posture as the gateway
        // stores above: SQLite when the daemon DB opened, in-memory otherwise so the daemon always starts.
        // Bound to locals here (rather than inline in the provisioner registration) because the null check
        // has to happen ONCE, outside the lambdas — the daemon's persistence mode cannot change at runtime.
        Func<string, IMergeQueueStore> queueStoreFactory = dbFactory is null
            ? _ => new InMemoryMergeQueueStore()
            : _ => new DbMergeQueueStore(dbFactory);
        Func<string, IVerificationStore> verificationStoreFactory = dbFactory is null
            ? _ => new InMemoryVerificationStore()
            : _ => new DbVerificationStore(dbFactory);

        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;

        // Register the store instances behind their interfaces so a test host can override them (isolated
        // in-memory) via ConfigureTestServices; everything downstream resolves them from DI.
        services.AddSingleton(spendStore);
        services.AddSingleton(expectedStore);
        services.AddSingleton(budgetStore);
        services.AddSingleton(mergeLeaseStore);

        // P2-10 merge queue: the registry the gRPC service resolves per-repo queues through. Empty at boot —
        // an unknown handle is a typed NOT_FOUND — and populated by the MergeQueueProvisioner below as repos
        // come up. The CONCRETE type is the registration and the interface forwards to it (MG-10): the
        // provisioner needs Register/Remove, which are deliberately not on the read-only resolve interface,
        // and two registrations of MergeQueueRegistry would hand the writer and the reader different
        // dictionaries — a registry that is written to and never read from is the bug we are fixing.
        services.AddSingleton<MergeQueueRegistry>();
        services.AddSingleton<IMergeQueueRegistry>(sp => sp.GetRequiredService<MergeQueueRegistry>());

        // MG-10: the missing constructor call. `new MergeQueue(...)` and `registry.Register(...)` existed
        // ONLY in the test projects, so the registry stayed empty for the daemon's whole lifetime and every
        // merge-queue RPC answered NOT_FOUND — the P2-10 guarantees were neither enforced nor bypassable,
        // they were simply not running. The provisioner builds a repo's queue on the events that make a repo
        // active (ProvisionRepo / CreateWorktree / a jailed spawn) over the SAME persisted stores, and — the
        // load-bearing detail — the SAME IMergeLeaseStore singleton the foreground merge, BeginMerge and
        // MergeDispatch contend for, since the one-outstanding-merge-per-repo invariant only spans origins
        // while they share one store (MG-23).
        services.AddSingleton(sp => new MergeQueueProvisioner(
            registry: sp.GetRequiredService<MergeQueueRegistry>(),
            repos: sp.GetRequiredService<IAgentEnvironment>().Repos,
            leases: sp.GetRequiredService<IMergeLeaseStore>(),
            resolveContainerId: (repoHash, agentId) =>
                ResolveVerificationJail(sp.GetRequiredService<AgentSessionStore>(), repoHash, agentId),
            queueStore: queueStoreFactory,
            verificationStore: verificationStoreFactory,
            sandboxes: sp.GetRequiredService<IAgentEnvironment>().Sandboxes,
            artifactDirectory: ResolveVerificationArtifactDir(dbPath),
            audit: sp.GetRequiredService<IAuditLog>(),
            log: log,
            // MG-3: the daemon-side publish. The queue's input contract is refs/heads/agent/<id> in the
            // mirror; with the agent now committing into its OWN repository, this is what carries it
            // there — immediately before verification, so the verified bytes are current (design §7).
            publishAgentRef: (repoHash, agentId) =>
                sp.GetRequiredService<IAgentEnvironment>().Worktrees.PublishAgentBranch(repoHash, agentId)));

        services.AddSingleton(sp =>
        {
            var stored = sp.GetRequiredService<IBudgetStore>().Get();
            // MG-21: the per-day caps (3rd/4th args) were hardcoded to literal 0 — which BudgetCaps
            // defines as UNLIMITED — so a daily budget the user had set silently stopped being enforced
            // the moment the daemon restarted, while GetBudgets kept reporting the persisted value. Only
            // the per-agent lifetime caps survived. SetBudgets at runtime always set all four correctly;
            // the defect was isolated to this boot path, which is exactly where it is least visible.
            var caps = new BudgetCaps(
                stored.TokenCap, stored.UsdMicrosCap, stored.TokenCapPerDay, stored.UsdMicrosCapPerDay);
            return new BudgetLedger(sp.GetRequiredService<ISpendStore>(), clock, caps);
        });

        // Supervisor: P2-09 wires the REAL supervisor — the gateway's 429 / budget pause now drives a
        // real PTY input pause through the session leader and reflects the agent state in the session
        // store (streamed to clients as an AgentEvent state change), replacing NullAgentSupervisor.
        services.AddSingleton<IAgentSupervisor>(sp => new PtyAgentSupervisor(
            sp.GetRequiredService<SessionLeader>(),
            sp.GetRequiredService<AgentSessionStore>()));

        services.AddSingleton(sp => new AiGateway(
            TokenBucket.FromKeyHealth(null, clock),
            sp.GetRequiredService<BudgetLedger>(),
            sp.GetRequiredService<IAgentSupervisor>(),
            sp.GetRequiredService<IAuditLog>(),
            clock));

        // Admission control: current-agent count comes from the live session store; the /proc/meminfo
        // sampler is the default (real on the WSL2 VM, "unknown → admit" on a Windows dev box).
        services.AddSingleton(sp => new AdmissionController(
            runningAgentCount: () => sp.GetRequiredService<AgentSessionStore>().List().Count,
            clock: clock));

        services.AddSingleton(sp => new SwarmReconciler(
            listContainers: BuildContainerLister(),
            expected: sp.GetRequiredService<IExpectedAgentStore>(),
            worktrees: sp.GetRequiredService<IAgentEnvironment>().Worktrees,
            policy: OrphanPolicy.Adopt));

        // Boot order: merge-reconcile (RT-D1, FIRST — before admission) → swarm (container) reconcile →
        // P2-09 leader reattach (containers → leaders → PTY reattach; mismatches resolved toward Docker
        // truth). The merge-reconcile slot now carries the real RT-D1 journal-replay task (§3.7): for any
        // repo with an outstanding lease it replays the T-19 journal and synthesizes a missing
        // ConfirmMerge before any new BeginMerge is accepted.
        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<IMergeQueueRegistry>();
            IBootTask mergeReconcile = new MergeReconcileTask(
                leases: sp.GetRequiredService<IMergeLeaseStore>(),
                journal: dbFactory is null
                    ? new Mainguard.Git.Services.NullOperationJournal()
                    : new Mainguard.Git.Services.OperationJournal(dbFactory),
                resolveRepoPath: _ => null, // repos map in as their swarms come up; none at boot.
                onMerged: (repoHash, agentId, postSha) =>
                {
                    // MG-29: this was `foreach (var handle in Array.Empty<string>())` — a hardcoded
                    // no-op, so a merge replayed at boot NEVER fired the stale cascade and a co-tenant
                    // branch stayed "Verified" against a main that had already moved. The reconcile task
                    // now hands us the lease's repo hash, so the owning queue is a direct lookup.
                    // Best-effort: a repo whose swarm has not come up yet simply has no queue to notify.
                    registry.Resolve(repoHash)?.Queue.ConfirmHumanMerge(agentId, postSha);
                });

            return DaemonBootSequence.Build(
                sp.GetRequiredService<SwarmReconciler>(),
                mergeReconcile: mergeReconcile,
                leaderReattach: new LeaderReattachTask(sp.GetRequiredService<SessionLeader>(), BuildContainerLister()));
        });

        // P2-47: the external-PR intake dependency chain (P2-12). Registering IExternalPrIntake here
        // lights up PrIntakeHostedService below — the poll loop now RUNS instead of idling. Persistence
        // is best-effort (like the gateway stores above): the DB-backed subscription/seen-head store when
        // the daemon DB opened, in-memory otherwise, so the daemon always starts.
        RegisterPrIntake(services, dbFactory);

        services.AddHostedService<GatewayHostedService>();
        // P2-13 carried-in from P2-12 (b): the external-PR intake poll loop runs from the daemon
        // scheduler. With IExternalPrIntake registered above (P2-47) it now runs the poll loop.
        services.AddHostedService<Runtime.PrIntakeHostedService>();
    }

    /// <summary>
    /// P2-47 — the P2-12 external-PR intake chain (PR transport → intake store → <b>worker host</b> →
    /// PR-head fetcher → target resolver), wired so <see cref="IExternalPrIntake"/> resolves and
    /// <see cref="Runtime.PrIntakeHostedService"/> runs its poll loop. Subscriptions persisted in the store
    /// are seeded into the running engine at construction.
    ///
    /// <para>Two things were missing here and both are supplied below. (1) The intake had no way to give an
    /// entry a jail, so an intake'd pull request could never be verified — <see cref="ExternalPrWorkerHost"/>
    /// is that seam, and it runs the ordinary gated spawn chain under the id <c>pr-&lt;n&gt;</c>. (2) The
    /// per-source target resolver was the literal <c>_ =&gt; null</c>, which makes every poll list-and-skip,
    /// so the intake materialized nothing in production regardless — <see cref="PrIntakeTargetResolver"/>
    /// replaces it, matching a subscribed source against the origin remotes of the repos this daemon has
    /// actually provisioned. A source that matches nothing still resolves to null and is skipped (no
    /// upstream writes, invariant 1), which is the "empty until a repo is active" posture the
    /// merge-reconcile and MergeQueueRegistry already take.</para>
    /// </summary>
    private static void RegisterPrIntake(IServiceCollection services, Func<AppDbContext>? dbFactory)
    {
        services.AddSingleton<IPrIntakeStore>(_ =>
            dbFactory is null ? new InMemoryPrIntakeStore() : new DbPrIntakeStore(dbFactory));

        // The intake's spawn seam. `resolveRunningJail` is the SAME label-based lookup the merge queue
        // decides jail liveness with (above) — one rule for "is this agent jailed", not two.
        services.AddSingleton<IPrWorkerHost>(sp => new ExternalPrWorkerHost(
            spawns: sp.GetRequiredService<AgentSpawnService>(),
            sessions: sp.GetRequiredService<AgentSessionStore>(),
            launcher: sp.GetRequiredService<SandboxAgentLauncher>(),
            admission: sp.GetRequiredService<AdmissionController>(),
            limits: sp.GetRequiredService<CoordinatorLimits>(),
            resolveRunningJail: ResolveRunningJail,
            // The SAME worktree manager the boot reconciler watches through (above) — one MG-3 sweep, so
            // an adopted `pr-<n>` is registered in the same set a spawned agent's watch lands in.
            worktrees: sp.GetRequiredService<IAgentEnvironment>().Worktrees,
            audit: sp.GetRequiredService<IAuditLog>(),
            loggerFactory: sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // (host/owner/repo) → (repoPath, repoHash, queue), matched on each active repo's own origin remote.
        services.AddSingleton(sp => new PrIntakeTargetResolver(
            repos: sp.GetRequiredService<ActiveRepoIndex>(),
            queues: sp.GetRequiredService<MergeQueueProvisioner>(),
            remotes: path => new Mainguard.Git.Services.GitService().GetRemotes(path)));

        // The ONE audited T-23 read transport (list surface only — invariant 2). A fresh GitService is the
        // engine seam; host/token/slug resolve per-repo from the source's RepoPath inside the transport.
        services.AddSingleton<IPullRequestService>(_ =>
            new Mainguard.Git.Services.PullRequestService(new Mainguard.Git.Services.GitService()));

        // The PR-head materializer (P2-12 step 2): fetch pull/<n>/head into the agent worktree. The worktree
        // path comes from the substrate's own worktree manager so the fetch targets the real jail path.
        services.AddSingleton<IPrHeadFetcher>(sp =>
            new PrHeadFetcher((repoHash, agentId) =>
                (sp.GetRequiredService<IAgentEnvironment>().Worktrees as WorktreeManager)?.WorktreePathFor(repoHash, agentId)
                    ?? throw new InvalidOperationException(
                        "PR-head fetch requires a WorktreeManager-backed substrate worktree path.")));

        services.AddSingleton<IExternalPrIntake>(sp =>
        {
            var store = sp.GetRequiredService<IPrIntakeStore>();
            var resolver = sp.GetRequiredService<PrIntakeTargetResolver>();
            var intake = new ExternalPrIntake(
                prService: sp.GetRequiredService<IPullRequestService>(),
                store: store,
                // The jail. Every materialization now begins by asking for one, and materializes nothing
                // when it is refused — see ExternalPrWorkerHost for how the MG-2 gates apply.
                workers: sp.GetRequiredService<IPrWorkerHost>(),
                fetcher: sp.GetRequiredService<IPrHeadFetcher>(),
                // Resolved against the repos this daemon has provisioned, by their origin remotes. Still
                // null (list-and-skip) for a source whose repo is not open — that part was always right.
                resolveTarget: resolver.Resolve,
                audit: sp.GetRequiredService<IAuditLog>());

            // Seed any persisted subscriptions into the running engine (idempotent on the store).
            foreach (var source in store.Subscriptions())
            {
                intake.Subscribe(source);
            }

            return intake;
        });
    }

    /// <summary>
    /// Where the P2-10 verification log artifacts land: beside the daemon DB, so the in-proc test tier's
    /// per-host temp directory isolates them exactly like the DB, the leader registry and the plan store.
    /// </summary>
    private static string ResolveVerificationArtifactDir(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        return string.IsNullOrEmpty(dir)
            ? Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "verify-artifacts")
            : Path.Combine(dir, "verify-artifacts");
    }

    /// <summary>How long <see cref="TryPrepareDatabase"/> lets a migration run before falling back
    /// to in-memory stores. Generous — a real migration is sub-second; only a hang exceeds this.</summary>
    private static readonly TimeSpan MigrationWatchdog = TimeSpan.FromSeconds(60);

    internal static bool TryPrepareDatabase(
        string dbPath, out Func<AppDbContext> factory, TimeSpan? watchdog = null, Action<string>? log = null)
    {
        var effectiveWatchdog = watchdog ?? MigrationWatchdog;
        try
        {
            log?.Invoke($"preparing db path={dbPath}");
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ClearStaleMigrationLock(dbPath, log);

            // Migrate under a watchdog. A daemon killed mid-migration (e.g. a WSL idle-stop of the
            // whole distro) orphans EF's __EFMigrationsLock row, and EF retries acquiring it forever
            // (Thread.Sleep loop, no timeout) — a HANG here kept Kestrel from ever binding, the exact
            // outage this method's in-memory fallback exists to prevent. The watchdog turns that hang
            // into the failure path the catch below already handles.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var migrate = Task.Run(() =>
            {
                using var db = new AppDbContext(dbPath);
                db.Database.Migrate();
            });
            if (!migrate.Wait(effectiveWatchdog))
            {
                log?.Invoke(
                    $"migrate watchdog fired after {effectiveWatchdog.TotalSeconds:0}s → in-memory fallback");
                factory = null!;
                return false;
            }

            log?.Invoke($"migrate ok ({stopwatch.ElapsedMilliseconds}ms)");
            factory = () => new AppDbContext(dbPath);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"db unavailable → in-memory fallback: {ex.Message}");
            factory = null!;
            return false;
        }
    }

    /// <summary>
    /// The daemon is this DB's only writer (one systemd instance per VM; test hosts isolate their
    /// own paths), so a migration-lock row present at boot was orphaned by a previous instance that
    /// died mid-migration — clear it so <c>Migrate()</c> doesn't wait on a holder that no longer
    /// exists. Best-effort: on a fresh DB or a pre-lock EF schema the table is absent and the delete
    /// simply fails, leaving Migrate() + the watchdog to decide.
    /// </summary>
    private static void ClearStaleMigrationLock(string dbPath, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(dbPath))
            {
                log?.Invoke("no lock table (fresh db)");
                return;
            }

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM \"__EFMigrationsLock\";";
            var rows = command.ExecuteNonQuery();
            log?.Invoke(rows > 0 ? "stale migration lock cleared" : "no stale migration lock");
        }
        catch (Exception)
        {
            // Absent table / unreadable file — nothing to clear.
            log?.Invoke("no lock table");
        }
    }

    /// <summary>
    /// The jail a repo's merge queue verifies <paramref name="agentId"/> in — the ONE production answer to
    /// "does this agent have a live sandbox", named rather than inlined so a test can ask the daemon's own
    /// resolver instead of a re-implementation of it.
    ///
    /// <para>Verification runs in the worker's OWN jail (§3.2 — host execution is a rejection trigger).
    /// Scoped by repo hash, so one repo's queue can never reach into another's container even if agent ids
    /// ever collide. Agent ids now DO collide — an intake'd <c>pr-&lt;n&gt;</c> is unique only inside a
    /// repo — so the session lookup is by the full (repo, id) identity, and the repo-hash comparison is
    /// kept as that same guard restated: it can no longer fail, and it starts failing again the moment the
    /// key stops carrying the repo.</para>
    ///
    /// <para>When the session store has no answer, ASK THE CONTAINER RUNTIME, which P2-08 already
    /// designates the sole source of truth for jail liveness (there are no PID/lock files). This is not
    /// belt-and-braces. <see cref="AgentSessionStore"/> is memory-only and is written by exactly one
    /// thing, <c>AgentSpawnService</c> — while the jails are persistent (a restart re-STARTS a stopped
    /// container rather than recreating it) and the merge queue rehydrates its state from SQLite in its
    /// constructor. So after a daemon restart the queue resumed knowing about every branch, every jail was
    /// still running, and the session store was empty: every verification on the box refused with "no live
    /// sandbox", <c>ResumeAfterRestartAsync</c> drove each interrupted run straight back to Working, and
    /// the stale cascade's auto re-verify failed for every branch. The queue came back up permanently
    /// unable to verify anything until each agent was spawned again — with no error naming the actual
    /// cause. That fallback matches on BOTH labels, so it disambiguates two repos' <c>pr-7</c> the same way
    /// the session key does.</para>
    /// </summary>
    internal static string? ResolveVerificationJail(AgentSessionStore sessions, string repoHash, string agentId)
    {
        var session = sessions.Find(new AgentSessionKey(repoHash, agentId));
        if (session is not null
            && string.Equals(session.RepoHash, repoHash, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(session.ContainerId))
        {
            return session.ContainerId;
        }

        return ResolveRunningJail(repoHash, agentId);
    }

    /// <summary>
    /// The RUNNING jail for one (repo, agent), read off the <c>mainguard.repo</c>/<c>mainguard.agent</c>
    /// labels P2-07 stamps on every container — the same listing the swarm reconciler treats as the sole
    /// source of truth. Null when no such jail is up, which keeps the "no jail ⇒ no verification, never the
    /// host" rule exactly as it was: this widens where the daemon LOOKS for a jail, never whether one is
    /// required. Both the repo hash and the agent id must match, so one repo can never reach into another's
    /// container. Best-effort by construction — an unreachable Docker endpoint answers "no jail", which is
    /// the same refusal the caller would otherwise have produced.
    /// </summary>
    internal static string? ResolveRunningJail(string repoHash, string agentId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var jails = BuildContainerLister()(cts.Token).GetAwaiter().GetResult();
            return jails.FirstOrDefault(j =>
                    j.Running
                    && string.Equals(j.AgentId, agentId, StringComparison.Ordinal)
                    && string.Equals(j.RepoHash, repoHash, StringComparison.Ordinal))
                ?.ContainerId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A best-effort Docker lister for the reconciler: real listing on a Docker host, an empty listing
    /// when Docker is unreachable (Windows dev box / Docker-less CI) so boot never fails.
    /// </summary>
    private static Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> BuildContainerLister()
    {
        return async ct =>
        {
            try
            {
                using var docker = new DockerClientConfiguration().CreateClient();
                return await DockerAgentLister.ListAsync(docker, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Array.Empty<AgentContainerState>();
            }
        };
    }
}
