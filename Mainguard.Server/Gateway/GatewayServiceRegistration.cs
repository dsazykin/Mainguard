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

        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;

        // Register the store instances behind their interfaces so a test host can override them (isolated
        // in-memory) via ConfigureTestServices; everything downstream resolves them from DI.
        services.AddSingleton(spendStore);
        services.AddSingleton(expectedStore);
        services.AddSingleton(budgetStore);
        services.AddSingleton(mergeLeaseStore);

        // P2-10 merge queue: the registry the gRPC service resolves per-repo queues through (populated as
        // repos' swarms come up). Empty at boot — an unknown handle is a typed NOT_FOUND.
        services.AddSingleton<IMergeQueueRegistry, MergeQueueRegistry>();

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
                onMerged: (agentId, postSha) =>
                {
                    // Fire the stale cascade on whichever active queue owns this agent (best-effort).
                    foreach (var handle in Array.Empty<string>())
                    {
                        registry.Resolve(handle)?.Queue.ConfirmHumanMerge(agentId, postSha);
                    }
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
    /// P2-47 — the P2-12 external-PR intake chain (PR transport → intake store → worktree manager → PR-head
    /// fetcher → target resolver), wired so <see cref="IExternalPrIntake"/> resolves and
    /// <see cref="Runtime.PrIntakeHostedService"/> runs its poll loop. Subscriptions persisted in the store
    /// are seeded into the running engine at construction. The per-source target resolver returns null until
    /// a repo's swarm maps its (host/owner/repo) → (repoPath, repoHash, MergeQueue) — the same "empty until a
    /// repo is active" posture the merge-reconcile and MergeQueueRegistry already take; a null target makes a
    /// poll list-and-skip (no upstream writes, invariant 1) rather than crash.
    /// </summary>
    private static void RegisterPrIntake(IServiceCollection services, Func<AppDbContext>? dbFactory)
    {
        services.AddSingleton<IPrIntakeStore>(_ =>
            dbFactory is null ? new InMemoryPrIntakeStore() : new DbPrIntakeStore(dbFactory));

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
            var intake = new ExternalPrIntake(
                prService: sp.GetRequiredService<IPullRequestService>(),
                store: store,
                worktrees: sp.GetRequiredService<IAgentEnvironment>().Worktrees,
                fetcher: sp.GetRequiredService<IPrHeadFetcher>(),
                // No active-repo index at boot: a source resolves to a target only once its swarm is up
                // (deferred to the swarm-lifecycle wiring — see the class doc). Null → list-and-skip.
                resolveTarget: _ => (PrIntakeTarget?)null,
                audit: sp.GetRequiredService<IAuditLog>());

            // Seed any persisted subscriptions into the running engine (idempotent on the store).
            foreach (var source in store.Subscriptions())
            {
                intake.Subscribe(source);
            }

            return intake;
        });
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
