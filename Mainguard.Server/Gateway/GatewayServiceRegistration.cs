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
    public static void Register(
        WebApplicationBuilder builder, string dbPath, Action<string>? log = null, DaemonOptions? options = null)
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

        // P2-15: the tamper-evident audit chain, riding the same posture decision. Constructed
        // EAGERLY (it needs no DI) so a store problem surfaces here in migration.log rather than as
        // a mid-flight RPC failure; on any failure the daemon still starts, on the in-memory
        // journal — which is exactly the pre-P2-15 behavior, now with the loss stated out loud.
        RegisterAuditLog(services, dbFactory, dbPath, log);

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
            // P2-11: the branch-vs-main diff the flagged-change review classifies at verification time.
            // The SAME service GetMergeDiff serves the cockpit from, so the diff a human is shown is the
            // diff the gate decided on — two diff paths would eventually disagree, and the disagreement
            // would be invisible.
            mergeDiff: sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.IMergeBranchDiffService>(),
            audit: sp.GetRequiredService<IAuditLog>(),
            log: log,
            // MG-3: the daemon-side publish. The queue's input contract is refs/heads/agent/<id> in the
            // mirror; with the agent now committing into its OWN repository, this is what carries it
            // there — immediately before verification, so the verified bytes are current (design §7).
            publishAgentRef: (repoHash, agentId) =>
                sp.GetRequiredService<IAgentEnvironment>().Worktrees.PublishAgentBranch(repoHash, agentId),
            // ...and the other half of the same question. The publish above carries ONLY
            // refs/heads/agent/<id>; this establishes whether the agent's worktree is actually on that
            // branch, so that work committed elsewhere is refused with the measurement instead of
            // verified as an empty branch. Passing it is what makes the check exist in the daemon at all —
            // the parameter defaults to null, and a null here would be the silent behaviour again.
            checkAgentBranch: (repoHash, agentId) =>
                sp.GetRequiredService<IAgentEnvironment>().Worktrees.CheckAgentBranch(repoHash, agentId),
            // ---- P2-10 §3.3 step 2: the keep-alive rebase, wired at last -----------------------------
            //
            // Without these three the stale cascade only RE-VERIFIES, and re-verification moves no branch:
            // the merge is `git merge --ff-only`, so once any agent merges, every co-tenant branch stops
            // being a fast-forward of main, passes its tests again against work that was never rebased,
            // returns to Verified, and has its merge refused as stale — forever, with no daemon-side way
            // out. Only one agent per repository could ever merge.
            //
            // Per repo, not per daemon: the yield's pause path resolves a container id, and an agent id is
            // unique only within a repository (an intake'd `pr-7` exists in as many repos as subscribe to
            // one). A single global protocol would pause another repo's agent to rebase this one's.
            yieldProtocolFor: repoHash => new YieldProtocol(
                // UnboundAgentControlChannel is a measurement, not a placeholder: nothing in the shipped
                // adapter wrapper writes [IPC_UPDATE_READY] back, so every yield takes the docker-pause
                // path. Saying so up front is what keeps the cascade fast — the alternative is a
                // ten-second cooperative window per agent per merge, spent waiting on a transport that
                // does not exist. The pause is the containment either way (freezer cgroup, no cooperation
                // required); the ack was only ever the courtesy that avoids it.
                channelFor: _ => UnboundAgentControlChannel.Instance,
                sandbox: sp.GetRequiredService<IAgentEnvironment>().Sandboxes,
                // The SAME (repo, agent) → jail answer the verification and the restart resume use, so
                // "does this agent have a live sandbox" has one rule in the daemon rather than three.
                containerIdFor: agentId =>
                    ResolveVerificationJail(sp.GetRequiredService<AgentSessionStore>(), repoHash, agentId),
                supervisor: sp.GetRequiredService<IAgentSupervisor>(),
                // The human-pause arbiter: the cascade's yield runs THROUGH a human-paused jail
                // (already quiescent) but never wakes it, and its own critical section refuses a
                // human unpause for its few seconds (see HumanPauseLedger).
                arbiter: sp.GetRequiredService<Mainguard.Server.Runtime.HumanPauseLedger>()),
            // Where the rebase happens. Only the worktree comes from here — the provisioner resolves the
            // mirror and the main branch from the same two calls that key the queue's own main@sha, so
            // the ref a branch is rebased ONTO and the ref its verification is pinned AGAINST cannot
            // drift apart. Null for a substrate with no real worktrees, which reads as "no worktree" and
            // is exactly right for one.
            locateAgentWorktree: (repoHash, agentId) =>
                (sp.GetRequiredService<IAgentEnvironment>().Worktrees as WorktreeManager)
                    ?.WorktreePathFor(repoHash, agentId),
            // The run states the cycle transitions through — Yielding, Rebasing and, the one that needs a
            // human, Conflict. PtyAgentSupervisor writes them into the session store, which streams them
            // to clients as agent state changes; AgentRunState.Conflict had no production writer at all
            // before this, so the one arm of the rebase that stops and waits for a person was also the one
            // arm nothing anywhere rendered.
            agentStates: sp.GetRequiredService<IAgentSupervisor>(),
            // ...and the one that carries the result out. A rebase is not a fast-forward, so MG-3's ref
            // mediator refuses it through the ordinary publish above as "the agent rewrote published
            // history" — which would leave every cycle succeeding and changing nothing anyone can see,
            // because the queue, the cockpit and the human's merge all read the mirror. PublishRebase
            // asks the question rule 2 stands for (was published work LOST) by patch-id instead.
            publishRebasedAgentRef: (repoHash, agentId) =>
                (sp.GetRequiredService<IAgentEnvironment>().Worktrees as WorktreeManager)
                    ?.PublishRebasedAgentBranch(repoHash, agentId) ?? false));
        // NOTE: `resolveApprovedPlan` is deliberately NOT passed, and its absence is load-bearing
        // information rather than an oversight. The SA-1/F6 out-of-approved-scope arm needs an
        // agent→approved-plan lookup, and the daemon has none to give: PlanApprovalService.PlanApproved has
        // no production subscriber, no spawn path accepts or records a plan id, and AgentSession carries
        // none. Passing a lambda that guessed (say, "any approved plan from this worker's coordinator")
        // would compare diffs against the wrong scope and report it as enforcement — worse than the honest
        // gap. The arm is wired and tested; it lights up when the plan-authorship pipeline supplies the
        // binding. Everything else the flagged-change gate blocks on is live now.

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

            // Both reconcile steps now RECORD what they did. Boot reconcile is the one pass that can
            // declare an agent Dead, force-remove its worktree, kill its PTY and drop it from the durable
            // leader registry — and both reports were being narrowed to a bare Task, so a user who left
            // agents running overnight had no artifact of any kind explaining why they were gone.
            var audit = sp.GetRequiredService<IAuditLog>();
            return DaemonBootSequence.Build(
                sp.GetRequiredService<SwarmReconciler>(),
                mergeReconcile: mergeReconcile,
                leaderReattach: new LeaderReattachTask(
                    sp.GetRequiredService<SessionLeader>(), BuildContainerLister(), audit, log),
                audit: audit,
                log: log);
        });

        // P2-47: the external-PR intake dependency chain (P2-12). Registering IExternalPrIntake here
        // lights up PrIntakeHostedService below — the poll loop now RUNS instead of idling. Persistence
        // is best-effort (like the gateway stores above): the DB-backed subscription/seen-head store when
        // the daemon DB opened, in-memory otherwise, so the daemon always starts.
        RegisterPrIntake(services, dbFactory);

        // ---- the model-proxy data path (MG-4 / P2-08 invariant #1) -------------------------------
        // These three types existed but were constructed ONLY in tests, so the gateway had no
        // production data path at all: no credential custody, no forwarder, and therefore no ledger
        // write on any real request. AiGateway.Acquire/Settle — the only writer to BudgetLedger — was
        // reachable exclusively from test code. Registering them here is what makes a BYOK agent's
        // spend actually charge, and an over-budget agent actually refuse.
        //
        // Registered unconditionally (not gated on the gateway being enabled) so the spawn path can ask
        // for credentials and get a coherent "no confinement configured" answer, rather than the DI
        // container throwing at spawn time on a daemon with the gateway off.
        services.AddSingleton<AgentGatewayCredentials>();
        services.AddSingleton<IAgentPortMap, NullAgentPortMap>();
        services.AddSingleton(sp => new GatewayForwarder(
            sp.GetRequiredService<AiGateway>(),
            // One pooled handler for every upstream call — the provider connection is TLS to the real
            // model host, established here, daemon-side, which is the whole point of the hop: the jail
            // never holds a credential that works against it.
            new HttpMessageInvoker(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })));

        // The confinement descriptor the spawn path reads. Null base URL ⇒ the gateway is disabled and
        // BuildSecrets keeps the pre-gateway behaviour exactly (the raw key goes to the jail). Since MG-4
        // item 3 the gateway is ON by default, so this is normally populated; `--gateway-bind off` (or
        // MAINGUARD_GATEWAY_BIND=off), or a host with no private address, still yields the old posture.
        services.AddSingleton(new GatewayConfinementOptions(
            BaseUrl: BuildGatewayBaseUrl(options),
            Enabled: options is not null && !string.IsNullOrWhiteSpace(options.GatewayBindAddress)));

        services.AddHostedService<GatewayHostedService>();
        // P2-13 carried-in from P2-12 (b): the external-PR intake poll loop runs from the daemon
        // scheduler. With IExternalPrIntake registered above (P2-47) it now runs the poll loop.
        services.AddHostedService<Runtime.PrIntakeHostedService>();

        // ISSUES-LOG #18/#20 — the live session store's reconcile against Docker. The two boot
        // reconcilers above make Docker the truth for the expected-agents table and the PTY leader
        // registry; NEITHER ever wrote to AgentSessionStore, which is what every surface actually
        // renders, so a restarted daemon showed zero agents while their jails kept running. This is
        // that missing half, and it keeps running afterwards to catch out-of-band drift.
        //
        // NOTE the lister: it lets a Docker failure PROPAGATE, unlike BuildContainerLister's
        // empty-on-error. An empty list here would read as "every jail vanished" and mark the whole
        // swarm Unresponsive the first time the engine was slow to answer.
        services.AddSingleton(sp => new Runtime.AgentSessionReconciler(
            sp.GetRequiredService<Runtime.AgentSessionStore>(),
            listContainers: async ct =>
            {
                using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
                return await DockerAgentLister.ListAsync(docker, ct).ConfigureAwait(false);
            },
            // Ownership: a jail belongs to this daemon iff this daemon hosts its repository's bare
            // mirror. The container engine is machine-wide and the labels carry no daemon identity, so
            // without this every daemon on the box would adopt every other one's jails — which the
            // in-proc test daemons demonstrated immediately by claiming a developer's live agent.
            ownsRepo: repoHash => !string.IsNullOrEmpty(repoHash)
                && System.IO.Directory.Exists(
                    sp.GetRequiredService<IAgentEnvironment>().Repos.BareRepoPathFor(repoHash)),
            audit: sp.GetRequiredService<IAuditLog>(),
            log: sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger<Runtime.AgentSessionReconciler>(),
            // ISSUES-LOG #24: the same pass also corrects merge-queue entries' jail-liveness, off the SAME
            // listing. Passed here rather than given its own hosted service because the answer is the same
            // answer — a second Docker timer would poll the engine twice for one fact and then have to
            // decide which copy wins.
            queues: sp.GetRequiredService<IMergeQueueRegistry>()));
        services.AddHostedService<Runtime.AgentSessionReconcilerService>();
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
    /// The base URL a confined jail's CLI is pointed at, or null when the gateway is disabled.
    ///
    /// <para>The address is the daemon's own gateway bind address — MEASURED to be reachable from a jail
    /// only via that jail's egress proxy, never directly (a container on an <c>Internal=true</c> network
    /// can reach its own bridge's host-side address and nothing else; see
    /// <c>docs/design/oauth-budgeting.md</c> for the measurements). Plain <c>http</c> is deliberate: the
    /// hop is jail → its own segment proxy → daemon, entirely inside the VM's private networking, and
    /// the credential it carries is a Mainguard session token rather than a provider key. The TLS that
    /// matters is the daemon → provider leg, which the forwarder establishes.</para>
    /// </summary>
    internal static string? BuildGatewayBaseUrl(DaemonOptions? options) =>
        BuildGatewayUpstream(options) is { } upstream ? "http://" + upstream : null;

    /// <summary>
    /// The gateway's <c>host:port</c> — the same address as <see cref="BuildGatewayBaseUrl"/> without the
    /// scheme, which is the shape the egress proxy wants (a tinyproxy filter matches a destination host,
    /// and an <c>upstream</c> directive names a <c>host:port</c>). Derived from the ONE place the bind is
    /// configured so the address the jail is pointed at and the address the proxy is told to permit cannot
    /// drift apart; a drift there would be invisible until a confined agent silently lost its egress.
    /// Null when the gateway is disabled.
    /// </summary>
    public static string? BuildGatewayUpstream(DaemonOptions? options) =>
        options is null || string.IsNullOrWhiteSpace(options.GatewayBindAddress)
            ? null
            : $"{options.GatewayBindAddress}:{options.GatewayPort}";

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

    /// <summary>
    /// P2-15: registers <see cref="IAuditLog"/> — the <see cref="ChainedAuditLog"/> over the daemon
    /// DB when it opened (with <see cref="IChainedAuditLog"/> alongside for the verify RPC/CLI and
    /// the retention job), the in-memory journal otherwise. The mirror sits beside the DB file and
    /// the AES-GCM master key lives in a <see cref="Mainguard.Git.Security.SecureKeyring"/> rooted
    /// beside it too — production puts both under the data root, in-proc test hosts under their
    /// isolated token directory, with no extra knob to drift.
    /// </summary>
    private static void RegisterAuditLog(
        IServiceCollection services, Func<AppDbContext>? dbFactory, string dbPath, Action<string>? log)
    {
        if (dbFactory is not null)
        {
            try
            {
                var directory = Path.GetDirectoryName(dbPath);
                var keyringDir = string.IsNullOrEmpty(directory)
                    ? "audit-keyring"
                    : Path.Combine(directory, "audit-keyring");
                var chained = new ChainedAuditLog(
                    dbFactory,
                    new AuditCrypto(new Mainguard.Git.Security.SecureKeyring(keyringDir)),
                    new AuditFileMirror(dbPath + ".audit-mirror"));
                services.AddSingleton<IAuditLog>(chained);
                services.AddSingleton<IChainedAuditLog>(chained);
                // The RFC 3161 anchor queue rides the same DB (heads enqueued by policy, tokens
                // stored beside the chain); the hosted sweep is a no-op until a TSA URL is set.
                services.AddSingleton(new AuditAnchorQueue(dbFactory));
                log?.Invoke("audit chain ready (db-backed, mirror recovered)");
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke($"audit chain unavailable → in-memory journal (EVENTS WILL NOT SURVIVE RESTART): {ex.Message}");
            }
        }
        else
        {
            log?.Invoke("audit chain on in-memory journal (no daemon db) — EVENTS WILL NOT SURVIVE RESTART");
        }

        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
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
    /// sandbox" and the stale cascade's auto re-verify failed for every branch. The queue came back up
    /// permanently unable to verify anything until each agent was spawned again — with no error naming the
    /// actual cause. That fallback matches on BOTH labels, so it disambiguates two repos' <c>pr-7</c> the
    /// same way the session key does.</para>
    ///
    /// <para>This method is now also the restart resume's jail probe:
    /// <see cref="MergeQueueProvisioner"/> hands it to <c>MergeQueue.BeginResumeAfterRestart</c>, which
    /// re-runs an interrupted verification when it answers and returns the entry to <c>Working</c> when it
    /// does not. That makes the Docker fallback above load-bearing twice over — without it every survivor
    /// would look jail-less on the one pass whose whole job is telling those two cases apart, and the
    /// resume would report every branch on the box as stranded.</para>
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
                using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
                return await DockerAgentLister.ListAsync(docker, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Array.Empty<AgentContainerState>();
            }
        };
    }
}
