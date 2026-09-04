<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.Server/` (P2-02 daemon — ASP.NET Core gRPC host)

- **`Program.cs`** — thin entry point: parses `DaemonOptions`, runs the `--local-dev --smoke` self-probe or the daemon (`app.Run()`), maps a bind failure to a typed `DaemonStartupException`. `public partial class Program {}` so `WebApplicationFactory<Program>` can host it in-proc.
- **`DaemonHost.cs`** — the shared host configuration (services, interceptors, gRPC service map,
  loopback-only Kestrel bind, silent logging) used by both the entry point and the in-proc tests;
  registers the durable `IKillJournal` (`JsonKillJournal` at `ResolveKillJournalPath`, beside the
  test-isolated session token like the plan store) and states the `KillSwitch`'s **whole** optional tail
  — `journal`, `audit`, the named `KillSwitchTiming.UnmeasuredRtt` sentinel (there is no
  control-channel RTT source in this daemon and the record must say so rather than imply a healthy
  channel) and the `onRttSpike` sink — because it previously passed only `gate`/`target`/`audit` and
  nothing asserted that composition at all;
  - `StartAsync` (typed port-bound failure) and `RunSmokeAsync` (authenticated loopback self-probe,
    which now goes over the real pinned mTLS transport); **MG-19: `ConfigureServices` runs BEFORE
    `ConfigureKestrel` and returns the `SessionTransportCertificates`, because the listener must present
    material that already exists — the control-plane listener is `UseHttps` +
    `ClientCertificateMode.RequireCertificate` + fingerprint-pinned `ClientCertificateValidation`, never
    h2c, with no plaintext fallback or downgrade knob. The model-gateway listener stays plain HTTP/1.1
    (model-API clients speak it; its own per-agent token is its authentication);** wires the P2-08
    gateway stack via `Gateway/GatewayServiceRegistration` (the spend-ledger SQLite path resolved next
    to the isolated session token, `ResolveDataPath`); also registers the P2-09 `SessionLeader` + its
    durable `LeaderRegistry` (path next to the token, `ResolveLeaderRegistryPath`).
- **`Gateway/GatewayServiceRegistration.cs`** — DI wiring for the P2-08 gateway (`AiGateway`,
  `BudgetLedger`, `AdmissionController`, `SwarmReconciler`, `DaemonBootSequence` — both boot reconcile
  steps get the daemon's `IAuditLog` + log sink so a pass that prunes agents or reaps PTY sessions
  leaves an artifact); best-effort
  DB-backed stores with an in-memory fallback so the daemon always starts, all resolved from DI so a
  test host can override them; **P2-15 adds `RegisterAuditLog`** — `IAuditLog` rides the same DB
  posture decision: the `ChainedAuditLog` (+ `IChainedAuditLog` for the verify surface + retention)
  over the daemon DB when it opened, with the mirror at `<db>.audit-mirror` and the AES-GCM key in a
  `SecureKeyring` rooted beside it, constructed EAGERLY so a store problem lands in migration.log;
  `InMemoryAuditLog` fallback otherwise, with the will-not-survive-restart loss logged out loud
  (note: the in-proc test tier's hosts share one run-scoped daemon DB, so Server.Tests audit
  assertions are repo/agent-scoped, and the chain itself re-reads its head per append); **P2-47 adds `RegisterPrIntake`** — the P2-12 external-PR intake chain
  (`IPullRequestService`→`PullRequestService`, `IPrIntakeStore`→`DbPrIntakeStore`/in-memory fallback,
  `IPrHeadFetcher`→`PrHeadFetcher` over the substrate worktree path,
  **`IPrWorkerHost`→`Runtime/ExternalPrWorkerHost` (the spawn seam, sharing the merge queue's
  label-based `ResolveRunningJail`), `PrIntakeTargetResolver` (replacing the hardwired
  `resolveTarget: _ => null` that made every poll list-and-skip, so the intake materialized nothing in
  production however many sources were subscribed)**, `IExternalPrIntake`→`ExternalPrIntake` seeded
  from the store's persisted subscriptions) so `PrIntakeHostedService` runs; **P2-09 wires the real
  `IAgentSupervisor` (`Runtime/PtyAgentSupervisor`) replacing `NullAgentSupervisor`** — the gateway's
  429/budget pause now drives a real PTY input pause via the `SessionLeader` + reflects the agent
  state into the `AgentSessionStore` — and appends the `LeaderReattachTask` to
  `DaemonBootSequence.Build` (containers → leaders → PTY reattach).
  - **`Gateway/ModelProxyMiddleware.cs`** — the in-path 429 interception: `GatewayForwarder` (acquire →
    forward → on 429 `Report429` + PTY pause + backoff + retry + resume → settle actuals; the CLI only
    ever sees a delayed 200) + `ModelUsageParser` (provider usage → actual tokens) + the ASP.NET
    `ModelProxyMiddleware` fronting model hosts with per-agent-port attribution (`IAgentPortMap`).
  - **`Runtime/GatewayHostedService.cs`** — runs the RT-D1 boot sequence + the token-bucket pump loop on
    host start.
  - **`Runtime/WorkerReadinessHostedService.cs`** — the boot slot whose ENTIRE job is to **resolve**
    `WorkerReadinessTrigger` (phase 2's automatic verification trigger, registered in
    `GatewayServiceRegistration` over the daemon's own `WorktreeManager.RefWatcher`). The trigger has no
    RPC and no other consumer, so a DI singleton nobody asks for would never be constructed, never
    subscribe and never sweep — registered-and-not-running, the same shape as MG-10's empty registry.
    Resolved with `GetService`, not `GetRequiredService`: a substrate that cannot supply a ref watcher
    still boots and still serves the human Verify button. Stop disposes the trigger (unsubscribe + wait
    for the sweep in flight). Asserted from the real composition root by
    `WorkerReadinessTriggerWiringTests`.
  - **`Runtime/AuditRetentionService.cs`** (P2-15) — hosted retention sweep: once at boot and every
    24 h, records older than 90 d are expired as chained REDACTIONS (tombstoned payloads, count
    unchanged, chain verifiable — the schema's triggers would refuse a delete anyway); a no-op on
    the in-memory fallback journal, and a failed sweep logs + retries next round, never taking the
    daemon down.
  - **`Runtime/AuditAnchorService.cs`** (P2-15) — hourly best-effort RFC 3161 sweep: heads queue by
    the `AuditAnchorQueue` policy (1000 records / 24 h) regardless, but nothing is SENT unless
    `MAINGUARD_TSA_URL` names an endpoint — no default install silently talks to a third party, and
    an operator who configures a TSA later gets the queued backlog anchored on the next sweep. A
    TSA failure leaves rows pending (anchoring is best-effort, chaining is not).
  - **`Runtime/MacSleepAssertion.cs`** — macos-host only (registered on macOS alone): while any
    `mainguard.agent`-labeled container is running, hold a sleep assertion via a child
    `caffeinate -im -w <daemon pid>` so idle sleep / App Nap cannot stall a verification; the
    `-w` ties the assertion to the daemon's own lifetime (a killed daemon never leaks a machine
    that refuses to sleep) and the child is killed the moment the last agent stops.
  - **`Runtime/PrIntakeHostedService.cs`** (P2-13 carried-in from P2-12) — the daemon scheduler slot
    that drives `IExternalPrIntake.RunAsync` (the external-PR poll loop); **P2-47 registered the intake
    dependency chain (`RegisterPrIntake` in `GatewayServiceRegistration`) so this now RUNS the poll loop
    instead of idling** — it resolves `IExternalPrIntake` and cancels it on stop. The per-source target
    mapping is now real (`Runtime/PrIntakeTargetResolver.cs`); a source no active repo owns still
    resolves to null and makes a poll list-and-skip (no upstream writes), the same "empty until a repo
    is active" posture the merge-reconcile takes.
- **`Gateway/GatewayBindPolicy.cs`** (**MG-13/MG-4**) — the pure bind-address rule for the model-gateway listener: loopback and private ranges are permitted; a wildcard bind and any public address are refused. `TryResolvePrivateHostAddress()` is the **default** bind since MG-4 turned the gateway on: a private, non-loopback IPv4 on an interface that is up, chosen deterministically so the address written into every confined jail is stable across restarts. Loopback is excluded as a default precisely because it binds cleanly and confines nothing — inside a container `127.0.0.1` is the container. It picks an address; whether a jail can REACH it is measured per spawn by `IEgressPolicy.CanProxyReachAsync`. The gateway fronts the user's provider key, so a listener that answers off-box is a credential-exposure surface rather than a convenience. Pure, so every case runs without a host.
- **`Gateway/GatewayConfinementOptions.cs`** — `GatewayConfinementOptions` (the spawn path's answer to "is there a gateway to point this jail at, and where"; `Disabled` is the default and means the provider key goes into the jail exactly as before), `ModelHosts` (the model-API hosts, derived from `EgressAllowlist.DefaultEntries` so the allowlist and the gateway cannot drift), and `NullAgentPortMap` (Mainguard runs one gateway listener, so attribution comes from the agent token; this returns null rather than guessing an agent).
- **`DaemonOptions.cs`** / **`DaemonStartupException.cs`** — parsed launch options (`--local-dev`/`--smoke`/`--port`, loopback-only by construction; `DataPath` overrides the P2-08 daemon SQLite path for test isolation; **`--gateway-bind`/`--gateway-port` control the model gateway, which is now ON BY DEFAULT (MG-4 item 3). It was previously settable only through `MAINGUARD_GATEWAY_BIND`, which nothing in the repo ever set, so in every supported deployment a BYOK jail received the raw provider key. `ResolveBindAddress` maps unset/`auto` → a private host address, `off` → disabled (the old posture), and an explicit address straight through to `GatewayBindPolicy`**) and the typed startup failure naming the port.
- **`Auth/SessionTokenFile.cs`** — generates a 256-bit session token (`RandomNumberGenerator.GetBytes(32)`) written user-only-readable (Linux `~/.mainguard/daemon.token` mode 0600; Windows `%LocalAppData%\Mainguard\daemon.token` current-user ACL); prints nothing (G-13). Path via `Core.Daemon.DaemonPaths`.
- **`Auth/SessionTransportCertificates.cs`** (MG-19) — the control plane's **peer-authentication**
  layer, so the bearer token is no longer the sole gate. Mints two fresh self-signed P-256
  certificates per daemon start (server: `serverAuth` + loopback SANs; client: `clientAuth`) and
  writes the client PKCS#12 + the server DER beside `daemon.token` with the same 0600 /
  single-ACE-DACL protection (`WriteRestricted`).
  - `IsPinnedClientCertificate` is the SHA-256 exact-fingerprint predicate Kestrel consults for every
    presented client certificate — chain/issuer/validity are deliberately not consulted (self-signed +
    session-scoped, and Windows-host↔WSL2-VM clock skew must never fail a correct connection).
    Rationale, the measured `localhostForwarding` exposure, and why a UDS + `SO_PEERCRED` was rejected
    for this topology: `docs/security-architecture.md`.
- **`Auth/BearerTokenInterceptor.cs`** — authenticates **every** RPC (unary/all-streaming) via a constant-time compare (`CryptographicOperations.FixedTimeEquals`); no public-method allowlist (invariant 1); mismatch → `PermissionDenied`.
- **`Auth/RoleInterceptor.cs`** (P2-14) — daemon-side role + terminal-lock enforcement at the gRPC
  layer (runs after auth, before the mask). **Role:** a `ConnectionRole.Coordinator` credential
  (looked up in `ConnectionRoleRegistry` by bearer token — role bound to the token, not
  client-asserted) is denied the merge RPCs (`BeginMerge`/`ConfirmMerge`/`AbandonMerge`/
  `AcknowledgeFlaggedChange` — MG-11, merge power by another name), the human entry-lifecycle RPCs
  (`DiscardEntry`/`RejectEntry`/`ClearStalledVerification` — a discard an agent could invoke erases the
  evidence blocking its own branch instead of clearing the gate, and clearing a stalled verification puts
  a branch into the state a re-verification starts from), the two parked-conflict actions
  (`ResolveConflictWithAgent`/`AbortRebase` — the first is `UnpauseAgent` plus the terminal input lock's
  whole purpose in one call, reached from a different service; the second rewrites a co-tenant branch's
  parentage and resumes its jail), **`AgentService/ResumeAgent`** (adoption is
  strictly MORE power than the merge RPCs above: an agent able to adopt an arbitrary id could attach a
  writable jail to another agent's branch and have the daemon verify what it put there — and because
  this interceptor dispatches by METHOD, that is why resume is its own RPC rather than a field on
  `SpawnAgentRequest`), the human-only plan-approval RPCs (`ApprovePlan`/`RejectPlan`/**`SetPlanMode`** — the last added
  2026-08-30 with the plan-mode toggle: a coordinator that could turn the gate off would hold it
  wholesale, for every worker it spawns from then on, with no card ever reaching a human), the scrollback
  read (`GetScrollback` — MG-30: serves any agent's daemon-side scrollback ring, up to 1000 rows per
  attach, which a coordinator could otherwise use to read another agent's terminal history it was never
  attached to), the P2-15 audit RPCs (`AuditService/VerifyAudit` + `ReadAudit` — the chain carries other
  agents' prompts/outputs and the human's decisions, none of it a coordinator's to read), and every
  `QueueSeedingService` method unconditionally (seeding composes EnsureEntry + a supplied verification
  outcome + the merge walk — every power this list denies piecemeal, reachable at once; the boot flag
  decides whether the OPERATOR gets the surface, never whether an agent does) — all with `PermissionDenied`
  (the coordinator can't merge, approve its own plans, or read what it shouldn't). **Note the honest
  caveat recorded in `coordinator-phase-3-decisions.md` §6:** `ConnectionRoleRegistry.IssueCoordinatorToken`
  has NO production callers, so this governs a credential class nothing currently mints — moot today only
  because the in-jail coordinator has no gRPC route at all (its only channel is the IPC socket, which is
  where phase 3 put the enforceable surface). **Terminal input lock:** wraps the `TerminalService.Attach` request
  stream so a `data` (input) frame toward a `TerminalLockRegistry`-locked (managed-worker) agent is
  rejected server-side while the read/output stream flows — never UI-only.
- **`Auth/SeedingGateInterceptor.cs`** — the dev-only queue-seeding BELT (docs/design/queue-seeding.md
  §7): `QueueSeedingOptions(bool Enabled)` is built once at boot (`MAINGUARD_ENABLE_QUEUE_SEEDING`
  via `DaemonOptions.QueueSeedingEnabled`; the in-proc test tier replaces the singleton —
  `DaemonFixture.EnableQueueSeeding`) and this interceptor `PermissionDenied`s the
  `/mainguard.v1.QueueSeedingService/` method prefix when disabled. Deliberately the belt, not the primary: the primary gate is that
  `DaemonHost.MapServices` never maps `QueueSeedingGrpcService` without the flag (disabled ⇒
  UNIMPLEMENTED — the client's hide-the-panel probe), and this layer exists so a refactor that made
  the mapping unconditional still refuses loudly.
- **`Auth/ConnectionRoleRegistry.cs`** (P2-14) — maps a bearer token → `ConnectionRole` (primary
  session token = `Operator`; issued/registered coordinator tokens = `Coordinator`).
  - **`Auth/TerminalLockRegistry.cs`** — the set of agents whose terminal input is severed (managed
    workers `Lock`; manual-mode unlocked).
  - **`Auth/ApproverIdentityResolver.cs`** —
    `IApproverIdentityResolver`/`PeerCredentialIdentityResolver`: resolves the plan approver from the
    **daemon's own** OS user under the loopback same-host trust boundary, never from the request
    (SA-1/F2); the honest host-trust residual (loopback TCP carries no peer credential, so the value is a
    constant that attributes the host session and cannot tell two callers apart) is documented in the
    file. **W5: one format on every platform — `os:<name>`.** The old Linux-only `uid:<euid>` branch was a
    leftover of the retracted `SO_PEERCRED` framing and made Windows/WSL2 (daemon in-VM as
    `User=mainguard`) render a bare `uid:1000`; `Environment.UserName` goes through `getpwuid`, not
    `$USER`, so it is not env-spoofable. `uid:<euid>` remains only as the last resort for a euid with no
    passwd entry, where `Environment.UserName` returns `""`.
- **`Logging/SecretFieldMask.cs`** + **`SecretMaskingInterceptor.cs`** — the G-13 registry of
  `(message, field number)` secrets (every `// SECRET` proto field) and the access-log formatter that
  redacts them (value/length/prefix never logged). **`SecretMaskingInterceptor` now also records
  handler faults** — a non-`RpcException` out of a handler is logged Error under the `Rpc` category
  (method/peer/type/message/stack) then rethrown, so a bare `Unknown` to the client is no longer
  invisible daemon-side.
- **`Logging/DaemonLogCategories.cs`** + **`SubsystemFileLoggerProvider.cs`** +
  **`LoggingTransparencyLog.cs`** — the in-depth per-subsystem daemon logging.
  - `DaemonLogCategories` maps each `DaemonLogSubsystems` name to an `ILogger` category
    (`mainguardd.Spawn`, …) and recovers the short name for file routing.
  - `SubsystemFileLoggerProvider` routes category→`~/.mainguard/logs/<subsystem>.log`, size-capped
    rolling (5 MB × 3), per-line flush, format `{ts:O} [LVL] [subsystem] (scope) message` + exception on
    the following lines; its **file writers are process-static + lock-guarded** so the pre-DI bootstrap
    factory and the runtime DI factory share one writer per file, and every file op is swallowed
    (diagnostics never break the daemon).
  - `LoggingTransparencyLog` decorates `INetworkTransparencyLog` to tee each egress verdict summary into
    the `Egress` category (schema kept stable for P2-17/P2-44). Wired in `DaemonHost` (journald console
    + files when `!Smoke`; a bootstrap `LoggerFactory` logs the Lifecycle/Migration startup milestones
    before the host is built, and the migration log delegate threads through
    `GatewayServiceRegistration`).
- **`Runtime/AgentResourceProbe.cs`** — the per-tick join behind `StreamAgentResources`: the session
  registry (who is alive, in which container) × the container engine (`IContainerResourceSampler`) ×
  the gateway credential store (**is this agent's spend measurable at all**). `AgentResourceReport`
  carries nullable CPU/RAM where null means NOT MEASURED, never zero. **The metering predicate lives
  here and nowhere else**: an agent is metered exactly when `AgentGatewayCredentials.TokenFor(agentId)`
  is non-null, i.e. the daemon actually issued it a gateway confinement token at spawn — deliberately
  NOT "the user supplied an API key", since `SandboxAgentLauncher.TryConfineToGatewayAsync` also
  requires the gateway to be bound, reachable from that jail's egress proxy, and the CLI to declare BOTH
  `baseUrlEnvVar` and `modelHost` (only `claude-code`/`gemini-cli` do, so a BYOK `codex`/`qwen-code`/
  `opencode` agent spends real money **unmetered**). Reading the daemon's own answer rather than
  recomputing those four conditions is the point: two derivations would eventually disagree invisibly.
  Results are cached for `DefaultCacheWindow` so N subscribers cannot multiply engine calls.
- **`Runtime/AgentSessionStore.cs`** — the in-memory daemon agent registry + snapshot-then-deltas
  event fan-out (host state, not transport); the gRPC classes dispatch here. Appends `spawn`/`stop`
  audit events via `IAuditLog`; `MarkState(agentId, state, reason)` (P2-09) updates a session's state
  + broadcasts a state delta (the sink the real supervisor drives so a pause/rate-limit streams to
  clients) — and broadcasts when the **reason** changed as well as the state word, holding the last one
  on `AgentSession.Detail` so a new reason is told from a repeat. Comparing only the state word silently
  swallowed every update reporting progress WITHIN a state, which is the only shape a long step has: a
  coordinator sits in `Starting` for the minutes its toolchain image builds, so each progress line died
  here and the client, hearing nothing, could only conclude the daemon had stopped responding. **P2-47 #8:** the `AgentSession` record carries a daemon-side-only
  `ContainerId`/`RepoHash` (never serialized), and `AttachSandbox` binds a real jail to a spawned
  session (state → `Working`, `sandbox_attach` audit). **`Spawn` takes an optional explicit
  `agentId`** — it could previously only MINT GUIDs, so a session (and therefore a jail) named
  `pr-<n>` was not expressible and the external-PR intake could not give an entry a sandbox at all;
  the id is simultaneously the worktree, the `agent/pr-<n>` branch, the container's `mainguard.agent`
  label, the per-agent package-cache directory and the merge-queue key, so it has to come from the
  pull request. **A session's identity is `AgentSessionKey(RepoHash, AgentId)`, not the id** — the
  store is daemon-global while `pr-<n>` is unique only *inside* a repo, so two subscribed repositories
  that each had a pull request #7 both wanted `pr-7` and the second was refused by name (its PR was
  never intake'd at all). Everything downstream was already repo-scoped —
  `ContainerSpecBuilder.ContainerName(repoHash, agentId)`, the `mainguard.repo`/`mainguard.agent`
  label pair, the MG-36 segment derived from that container name, the worktree, the
  `caches/<repoHash>/<agentId>` cache and the per-repo queue — the store was the one place two repos
  collided. The repo goes in at `Spawn` (the spawning caller always knows it) and
  `Find`/`Stop`/`MarkState`/`AttachSandbox` all take the key; `List()` is unchanged (every session on
  the daemon, so the box-wide `MaxActiveWorkers`/admission populations still count the right thing). A
  duplicate of the FULL key still throws rather than overwriting — replacing a live record would drop
  the running jail's container id and leak the container, its MG-36 segment and its cache lease with
  nothing left able to name them — while the same id under a different repo is not a duplicate and is
  admitted. The id-only `Find`/`Stop`/`MarkState` overloads remain for the daemon-global entry points
  whose caller has no repo to give (`StopAgent`/`HarvestAgentCredentials`, the PTY binder's exit
  watcher, the coordinator shim's own session); they resolve the UNIQUE holder and do **nothing** when
  two repos hold the id, never an arbitrary pick. `FindAll(agentId)` is the fan-out the kill switch
  needs, since `IKillTarget` is an id-only contract and an emergency stop must contain both repos'
  `pr-7`.
  - **`Runtime/SandboxAgentLauncher.cs`** (P2-47 #8) — the daemon-side spawn chain behind
    `AgentGrpcService.SpawnAgent` (kept out of the transport class): provision the agent worktree
    (`IAgentEnvironment.Worktrees`) → ensure default-deny egress (`Egress.EnsureReadyAsync`) → start the
    hardened jail (`Sandboxes.SpawnAsync`), returning the real container id;
  - `TryLaunchAsync` returns null (session-only, no jail) when the repo handle is not provisioned (the
    headless Alpha-loop-smoke path), cleans up a half-made worktree on failure, and `TeardownAsync`
    removes the jail + worktree on stop. It also owns the daemon half of the **CLI login round-trip**:
    `FilterCliCredentials` admits ONLY client-supplied files whose path exactly matches the installed
    adapter's declared `credentialPaths` (the marker is the allowlist — a compromised client can never
    seed arbitrary agent-home files), and `HarvestCliCredentialsAsync` reads those files back out of the
    jail's tmpfs `$HOME` (base64 over the exec pipe, best-effort — a failed harvest never blocks a stop)
    so `AgentSpawnService.StopAsync` can hand them to the client for the host OS keychain
    (`AgentStopResult`). Both harvests ask `IsFrozenAsync` FIRST: `docker exec` into a paused container is
    refused outright (`Conflict`), so a conflicted keep-alive rebase used to put one raw
    `Docker.DotNet.DockerApiException` stack trace per declared path into the operator log — a warning
    meaning "as expected", which is how the warnings that mean something stop being read. A frozen jail is
    skipped with one Information line saying nothing was lost; an engine that cannot answer is read as NOT
    frozen (guessing would skip a harvest that would have worked, costing the user their login), and a
    genuine failure on a live jail is still a warning with its exception.
    It owns the same two halves for the **CLI SETTINGS round-trip**:
    `FilterCliSettings` admits only (root, path) pairs the marker's `settingsPaths` declares and caps
    each file at `AdapterSettingsPolicy.MaxFileBytes` — the stakes are higher than for a login, because
    these files carry a permission allowlist and an unfiltered path would let a compromised client plant
    pre-approved commands anywhere in the home or the checkout — and `HarvestCliSettingsAsync` reads
    them back out (size-checked in the shell, so an oversized file never enters daemon memory),
    resolving each root through `DockerSandboxEngine.SettingsRootPath` so restore and harvest cannot
    address different directories. It also owns what Mainguard WRITES into a worker's checkout:
    `TryStageInstructionsFile` puts the role's operating instructions at the adapter's declared
    `instructionsFile` — validated as a plain relative path, and **never over a file the worktree already
    has**, because a git exclude does not cover a tracked file and the write would otherwise replace a
    user's own `CLAUDE.md` — and `DeclaredWorkspaceIgnorePaths` is the union (settings paths + that
    filename) the jail's `info/exclude` is built from, so what the daemon writes and what git ignores are
    decided by one field. `Worktrees` exposes this daemon's own worktree manager for the in-daemon
    caller that acts on a live agent's worktree (the worker's `commit_work`).
  - **`Runtime/PtyAgentSupervisor.cs`** (P2-09) — the real `IAgentSupervisor`:
    `PauseInput`/`ResumeInput` via the `SessionLeader`, `MarkState` via the `AgentSessionStore` (the
    P2-08↔P2-09 integration).
  - **`Runtime/TerminalSessionManager.cs`** (P2-03/PR3) — maps an agent id to its live terminal:
    `Bind`/`TryGetBound`/`Release` register the long-lived **bound** CLI sessions the spawn chain
    creates (the real agent path — attaches subscribe, a detach never kills the CLI), while the legacy
    injectable per-attach `PtySession` factory remains the TI-P2-03 wiring-test shape; with neither,
    attaches echo.
  - **`Runtime/BoundTerminalSession.cs`** (PR3) — one agent's long-lived CLI terminal: a continuous
    `TerminalStreamer` pump drains the PTY into VT-safe frames kept in a bounded 512 KB replay ring and
    fanned out to subscribers (re-attach renders the missed output composed; a stalled attach is
    completed, never unbounded), input/resize forwarding, and `Kill` only on StopAgent/teardown.
    `WriteInputAndAwaitOutputAsync` writes and then waits a bounded window for the CLI to produce
    output — the only in-band evidence the daemon has that a write was CONSUMED rather than merely
    accepted by the PTY master (necessary, not sufficient: a CLI already mid-turn satisfies it anyway;
    its weight is negative — an idle CLI that emits nothing after a keystroke did not see one).
  - **`Runtime/AgentCliBinder.cs`** (PR3) — binds a launched jail's CLI to a real TTY: the default
    factory spawns `docker exec -i -t` under a daemon-side forkpty PTY from the pure `BuildPtyLaunch`
    plan (`CliPtyLaunch`: `SandboxCliLaunch` argv, interactive tty + attached stdin, explicit `TERM` on
    both sides of the exec, positive size — the TTY contract `AgentCliWiringTests` pins so an
    unauthenticated CLI opens interactive login instead of dying non-interactive), registers the bound
    session with `TerminalSessionManager` + the P2-09 `SessionLeader` (PTY-fd ownership + kill), audits
    `cli_bound`/`cli_bind_failed` (bind failure degrades to session-only + echo, never fails the spawn),
    and marks the session `Dead` when the CLI exits — auditing `cli_exited` with the exit code + the
    VT-stripped output tail (`BoundTerminalSession.TailText`), the bound session staying registered so
    attaching to the dead agent's terminal still replays its final output (the why).
    Also owns `TrySendPromptAsync` — the ONLY write path into a worker's CLI (coordinator contract §3
    `send_worker_prompt`), returning `PromptDelivery(Submitted, Echoed, Reacted, Refusal)`. It encodes
    through `TerminalSubmit` (**CR, not LF** — the shipped `prompt + "\n"` typed into the input box and
    pressed nothing, so the tool had never once worked) and submits via
    `BoundTerminalSession.SubmitLineAndAwaitOutputAsync`, which writes the body and the CR as **two
    separate writes** — the CR appended to the body is read as pasted content, not Enter, so the CR-only
    fix worked at 3 bytes and failed at 139 (defect J2, §17.8). The writes are separated causally by the
    CLI's own echo (`PromptEchoWindow`, 250 ms) and by `TerminalSubmit.TerminatorSeparation` when there
    is no echo. `Echoed`/`Reacted` are **observations, never proof** — a mid-turn CLI satisfies both
    without reading anything.
  - **`Runtime/MirrorMainRefreshHostedService.cs`** (2026-09-04, owner decision) — the mirror-freshness
    sweep: every `CoordinatorLimits.MirrorRefreshSeconds` it calls
    `MergeQueueProvisioner.RefreshMainFromCheckout` for each live queue, so a pull made on main outside
    Mainguard reaches the queue without a repo-open and the rail's "refreshed N min ago" is a bound.
    `SweepOnce()` is public for the wiring test; the on-demand `RefreshMirrorMain` RPC is the same call.
  - **`Runtime/FrozenJailPolicy.cs`** — the frozen-jail predicate behind `send_worker_prompt`,
    `request_verification` and the human's Verify: the session state word (`Paused`/`Conflict`) OR the
    session store's pause axis (`AgentSessionStore.MarkFrozen`/`FrozenReason`), which the merge queue's
    state reflection cannot overwrite; plus the two refusal sentences an agent reads. Moved out of
    `AgentSpawnService.cs` on 2026-09-03 when it gained the axis.
  - **`Runtime/AgentSpawnService.cs`** (PR3) — the ONE spawn/stop workflow behind BOTH entry points (the
    `SpawnAgent` RPC and the coordinator's in-jail `mainguard-agent` shim): kill-gate → session record
    (with role) → **the phase-2 task withhold** (`WorkerPlanGate.Hold`, armed the instant the id is
    minted and BEFORE any jail exists — a gate armed after the spawn returns is armed most of the time)
    → IPC endpoint (spawn shim for a coordinator, **plan shim for a worker**; best-effort, audited on
    failure) → worktree+jail
    (`SandboxAgentLauncher`) → CLI bind → managed-worker terminal lock (P2-14); stop tears down record,
    PTY, endpoint, lock, jail, worktree. Typed `AgentSpawnRefusedException` keeps it transport-agnostic.
    **Phase 3 — the role lock (coordinator contract §8).** The coordinator shim handler now serves the
    contract's four tools and nothing else: `spawn` / `status` (+`list`) / `prompt` / `verify`.
    **The allow-list is the dispatcher**, not a comment above a `switch`: `BuildCoordinatorHandlers` /
    `BuildWorkerHandlers` bind op → method and run both tables through `LockToContract`, which throws
    unless the table set-equals `AgentIpcRequest.CoordinatorOps` / `WorkerOps`; they are built in the
    **constructor**, so a handler registered for an op §3 does not list takes the daemon down at startup
    instead of shipping an unreviewed fifth coordinator tool, and a listed op with no handler behind it is
    caught in the same place. `ServedCoordinatorOps`/`ServedWorkerOps` publish the result so the role-lock
    test can assert the surface rather than infer it from case labels. (It was a bare `switch` with a
    deny-by-default `default:` until then; the deny was real, but "and the surface is exactly these five"
    was a property of control flow, and adding one `case` served an unlisted op with the suite green.)
    Every op naming an agent resolves it through `OwnedWorker`, scoped
    `(RepoHash, AgentId)` to the caller's own fan-out — a stranger's worker is refused with the SAME string
    as a nonexistent one, so the channel is not an existence oracle. A coordinator spawn now sets
    `withoutRepositoryAccess`, so its jail gets no worktree, mirror, per-agent git dir or package cache,
    and it never becomes a merge-queue member (it has no branch, and §4 denies it declaring its own work
    merge-ready).
    **`FrozenJailPolicy`** (in this file) is the guard `prompt` and `verify` ask AFTER the plan gate: a
    worker whose jail is `docker pause`d — the state a conflicted keep-alive rebase leaves it in — is
    refused, because a prompt delivered into a SIGSTOPped process succeeds and means nothing (the tool
    answered `Ok` and the coordinator then polled a worker that could never reply), and verification runs
    its test command in that same frozen jail. The predicate is the session's own state word
    (`Paused` / `Conflict`), which is what `Row` and `ListAgents` already project — NOT
    `HumanPauseLedger.IsHumanPaused`, which answers the narrower "did a person press pause" and says no
    for exactly this case.
    **`CommitWork`** is the worker table's fifth op (`commit_work`) and the rung the loop was missing: a
    finished worker used to stop on an uncommitted diff that died with its worktree, leaving
    `agent/<id>` empty and the readiness trigger — which fires on that ref advancing — with nothing to
    observe. Pure transport: `WorkerPlanGate.MayWork` (the SAME predicate `prompt` and `verify` ask) then
    `WorktreeManager.CommitAgentWork`, which owns what/where/onto-which-branch. The worker supplies only
    a message; the (repo, agent) come from the endpoint, never from `request.AgentId`. `NothingToCommit`
    answers `ok:true, committed:false` rather than a commit.
    **(2026-08-31) It also settles the worker's DEVIATION DECLARATION** (`DeviationRefusal` /
    `RecordDeviations`): a worker holding an approved plan must send exactly one of `noDeviations` or a
    non-empty `deviations`, and a commit carrying neither — or both — is **refused BEFORE anything is
    committed**, which is the only reason this is safe to make mandatory (the worktree is untouched by a
    refusal, so it costs one re-run and no work). Required only where there is something to deviate from:
    `PlanApprovalService.ApprovedForWorker` decides that, the same single authority the F6 scope
    comparison uses, never a second reading of `PlanModeSwitch`. An ungated worker that volunteers a
    declaration anyway COMMITS and is told in `feedback` that it was not recorded — quiet discarding is
    what sits at the bottom of most of this subsystem's defects. Design:
    `docs/design/coordinator-phase-3-decisions.md` §26.
    **`RescopePlanAsync`** is the worker table's sixth op (`rescope_plan`, 2026-08-30 — contract §3.1 /
    phase 3 §23): the worker names the APPROVED plan it is widening (required, never inferred — a guessed
    target produces a plausible card for an authorisation nobody named, §13.3's call), passes the same
    plan-ownership and schema checks `revise` does, and then **blocks on the human exactly as `present`
    does**. What it deliberately does NOT touch is `MayWork`: a worker with a re-scope pending still holds
    the approval it is asking to widen, so steering, verification and `commit_work` keep answering off the
    old scope. Suspending it would make asking more expensive than widening quietly, and would refuse a
    running worker the one call that lets its work outlive its jail (F1). It DOES ask
    `WorkerPlanGate.RefusePlanPresentation` first, as `present`/`revise`/`await` do: without it an ungated
    worker fell through to the plan lookup and was told `no plan '<id>'` — true of the lookup and a lie
    about the world, since it reads as "you named the wrong id" and argues against the one correct
    response. `PlanApprovalService.Rescope` cannot say it (a plan id that resolves to nothing names no
    worker, and the mode lives on the worker), so it keeps `No plan` for a genuine miss. `DecisionResponse` carries
    `RescopeOf` on every decision about a re-scope, not only the approval — a declined one has taken
    nothing away, and the generic wording would send a still-authorised worker away from its work.
    Three optional parameters carry the external-PR intake's needs without forking the chain: `agentId`
    (the explicit `pr-<n>` id), `queueOrigin` (the merge-queue badge — the post-attach `EnsureEntry`
    overwrites the origin on every call, so a default `Local` stamp would silently undo the intake's
    `External` and route an upstream PR's merge into a local fast-forward), and `withoutHostCredentials`
    (**trust boundary** — an untrusted PR head inherits neither the per-repo cached `llm_env_*`, nor any
    harvested CLI login, **nor any CLI SETTINGS**, and seeds none of them). The settings gate is the
    stronger of the three: an inherited permission allowlist is inherited *execution*, so a jail holding
    a pull request's code must start asking about every command. `cliSettings` carries the repo's saved
    approvals in; `CliSettingsHarvestPolicy` (in this file) gates them flowing back OUT — only a
    HUMAN-ATTENDED session is harvested, because a `Managed` worker's terminal is daemon-locked
    read-only, so anything in its settings file was written by the agent, not approved by a person.
    Restore is deliberately wider than harvest (a Managed worker still receives the repo's approvals or
    it stalls on prompts nobody can answer). `AgentStopResult` carries `CliSettings` + `RepoHandle` so
    the client files them under the right repository rather than whichever one is open. See
    [`docs/design/agent-cli-settings-persistence.md`](../design/agent-cli-settings-persistence.md).
    **Phase 2** adds `heldTaskTitle`/`heldTaskPrompt`/
    `heldBudgetUsd` (the work the daemon withholds until the worker's own plan is approved — the shim's
    `taskPrompt` was previously parsed off the wire and then silently dropped, so a coordinator-spawned
    worker received no task at all) and `HandleWorkerPlanRequestAsync`, the worker plan shim's handler:
    `brief` / `present_plan` / `revise_plan` / `await_decision`, where present and revise **do not return
    until a human decides** and an approval is the only thing that yields the task prompt. Plan ownership
    is checked daemon-side and a foreign plan id answers "no plan '<id>'" — the same answer as a plan
    that does not exist, so the channel is not an existence oracle for other agents' work.
    A further flag, `adoptExistingBranch`, is the RESUME flag: it
    routes the launcher to `AdoptAgentWorktree` (start on this id's EXISTING `agent/<id>`) instead of
    `CreateAgentWorktree`, and switches the post-failure cleanup to the branch-preserving one. It asks no
    authorization question — that is `AgentResumeService`'s job, and a spawn that could name any id
    without it would let one agent adopt another's branch.
    **MG-4 credential release (stop):** takes a REQUIRED `AgentGatewayCredentials` and calls `Revoke` in
    the stop path. That method previously had **no production callers** — deleting it left both heads
    compiling — so with the gateway on by default every stopped BYOK agent left a live, replayable
    gateway token AND the daemon's copy of the user's provider key resident for the rest of the daemon's
    lifetime. It sits inside the `FindAll(agentId).Count == 0` block beside the leader/IPC/lock releases
    because the credential store is keyed by agent id ALONE: revoking while another repo's session still
    answers to that id would break a LIVE agent's next model call. The dependency is required rather than
    optional (it is registered unconditionally, gateway on or off) so a lost registration is a startup
    failure instead of a silent return to the defect. Pinned by
    `Mainguard.Server.Tests/Gateway/GatewayConfinementWiringTests.cs`.
  - **`Runtime/AgentResumeService.cs`** — the human-only resume for a STRANDED merge-queue entry (jail
    gone, branch intact), behind `AgentService.ResumeAgent`. Holds ALL of the authorization, keyed on
    `(RepoHash, AgentId)`: the entry must exist in THIS repo's live queue and be non-terminal, the id must
    have no live session, the repo must hold no merge lease naming it, and no verification may be in
    flight. Retracts a stale `Verifying` claim through the queue's own `TryClearStalledVerification` (one
    implementation of that transition, not two), reads the entry's ORIGIN off the queue so `EnsureEntry`
    cannot silently re-badge an intake'd PR as `Local`, refuses a resume that produced no sandbox (rolling
    the session back), and appends the `queue_entry_resumed` audit event. Every refusal is an ordinary
    result carrying its sentence — never an exception — and a refusal that follows a retraction says so.
    See `docs/design/resume-stranded-queue-entry.md`.
  - **`Runtime/AgentIpcServer.cs`** (PR3; renamed from `CoordinatorIpcServer.cs` in **phase 2**, because
    it now serves both roles and being named for one of its two clients would mislead about which agents
    have a channel) — the agent→daemon control channel: one Unix-domain socket per agent served from a
    daemon-owned ext4 dir (12-char agent-id prefix — sockaddr_un limit) that also carries **the one shim
    that agent's role is allowed** — `mainguard-agent` for a coordinator, `mainguard-plan` for a worker —
    and whose directory **outlives the daemon process** since 2026-09-03: `Dispose` (shutdown) only stops
    the listeners, because the jail bind-mounts that directory by inode and deleting it orphaned every
    surviving jail's channel; only `CloseEndpoint` (the stop path) removes it, and `CreateEndpoint` re-binds
    at the same path when the reconciler adopts the jail back (`AgentSpawnService.TryReattachEndpoint`) —
    **and beside it the role's `MAINGUARD.md` operating instructions**, written in the same call so the
    shim and the text that makes it discoverable cannot be staged independently: a shim is useless to a
    CLI that was never told it exists, which is what every jail was until now. Since **defect G2**
    (2026-08-29) `CreateEndpoint` takes that text as a REQUIRED argument and this class renders nothing —
    it used to call `AgentOperatingInstructions.For(role, shimPath)` and omit the then-optional
    installed-kind argument, so one jail carried two disagreeing briefings. The string it is handed is the
    one `SandboxAgentLauncher.InstructionsFor` put on the launch line.
    **Every endpoint also serves an `outbox/`** — the same JSON, the same handler, the same
    role, framed as request/response FILES the daemon polls every 100 ms — because on macOS the socket
    half is unreachable from a jail (daemon on the host, jail in the engine's Linux VM; virtiofs does not
    proxy AF_UNIX, so `connect()` is ECONNREFUSED against a listening daemon). Requests are claimed by
    RENAME, so a handler parked on a human for hours is never re-dispatched by the next sweep; responses
    are staged then renamed, so a shim can only observe a complete one; oversize requests are deleted
    unread, which is the bound on the one thing a writable mount grants a jail. The directory is created
    on every platform — whether the jail can WRITE it is the container spec's decision — so the code path
    is exercised everywhere rather than only where it is load-bearing.
    The dir is created BEFORE the jail (it is a read-only mount source) and removed on stop. Identity is
    **The outbox is jail-controlled, and treated that way (phase 3 §14).** The stated 64 KiB cap
    inspected the wrong object: `FileInfo.Length` on a SYMLINK is the length of the link, so `ln -s
    /dev/zero x.req` from inside a jail passed the cap, `File.Move` renamed the link, and the read
    followed it — measured at 4.2 GB resident and still climbing, i.e. one symlink kills the daemon and
    every running agent's control plane with it. A request is now claimed by renaming it into
    `inflight/`, a daemon-only SIBLING of the outbox (inside the read-only IPC mount, outside the
    read-write one), which removes the second writer and is what lets the checks that follow mean
    anything: a symlink is refused unread, so is anything that is not a plain non-empty regular file (a
    FIFO — creatable with no capability, indistinguishable from a file through every managed API — used
    to park the poll loop in `open()` forever), and the byte cap is enforced by the READ rather than by
    the stat that precedes it, which is what closes the grow-after-stat window. The directory is bounded
    in aggregate too (`MaxOutboxFiles` / `MaxOutboxBytes`): past either, everything in it is deleted
    unread and polling CONTINUES — a jail must not be able to fill the host's disk, and equally must not
    be able to switch off a control plane the human depends on. Leftovers from a daemon that died
    mid-call are cleared when the endpoint comes UP, never on a timer, because a claim that has sat for
    hours is the normal shape of the plan gate. Every refusal goes through the existing capped
    `ChannelObserver`, so there is no second reporting path.
    Identity is
    positional — only that agent's jail has the mount — and the **role is fixed on the endpoint**, so a
    worker cannot reach a coordinator op by naming it and vice versa. One newline-delimited JSON request
    per connection (`AgentIpcProtocol`); malformed input gets an error response. Each connection is served
    on its own task, which is what lets a worker's plan presentation **park on the socket for hours**
    without blocking the accept loop or another agent's request.
    **It also says things out loud now (defect C3).** This class had NO logger at all, so three refused
    connections from a live jail produced zero daemon-side entries and the outage was indistinguishable
    from a model sitting idle. `ChannelObserver` is the one place that changed, and the split inside it is
    the honest part: a REFUSED `connect()` is refused by the jail's own kernel and can never be logged
    here, so its only daemon-side shadow is **silence** — an endpoint that has served nothing after
    `DefaultFirstContactGrace` (90 s) reports itself ONCE, naming the agent, its role, its shim and both
    framings, and logs again if contact later arrives so a recovered channel is never left described as
    dead. An endpoint torn down inside the grace window says nothing. Malformed / oversize /
    handler-thrown requests that DO arrive are logged at Warning and audited (`ipc_request_rejected`),
    **capped** at five lines and ONE audit event per endpoint — the outbox is jail-writable, so an
    uncapped warning-per-rejection would be a log-flood and audit-flood primitive handed to a sandbox.
    Jail-supplied text (the `op`) is control-stripped and truncated before it reaches a line, and
    everything a healthy channel does stays at Debug. Registered from the DI provider (not
    instance-registered) so it gets the daemon's `ILoggerFactory` + `IAuditLog`, under the same
    `Coordinator` category `AgentSpawnService` already logs this subsystem's endpoint lifecycle to.
    Pinned by `AgentIpcObservabilityTests`.
  - **`Runtime/AgentPauseService.cs`** — the human per-agent Pause/Resume bodies behind
    `AgentService.PauseAgent`/`UnpauseAgent`, plus **`HumanPauseLedger`** (the `IPauseArbiter`
    singleton every repo's `YieldProtocol` consults). Not containment: no terminal lock, one agent.
    Fans over EVERY session behind the id (`pr-<n>` exists per-repo); tolerates "already paused" BY
    `ISandboxEngine.IsPausedAsync` inspect state, never by error-message substring (engine wordings
    differ per version). The arbitration rules: a human pause is sticky — the cascade's yield runs
    through a frozen jail and never wakes it (checked at RESUME time in `YieldProtocol`) — and a
    human unpause is refused while a machine hold is outstanding (self-clearing, seconds). Refusals
    are answers, not exceptions. Pinned by `Mainguard.Server.Tests/AgentPauseTests` (incl. a real
    docker pause→inspect→unpause leg) and the arbiter legs of `Mainguard.Tests/YieldProtocolTests`.
  - **`Runtime/AgentSessionReconciler.cs`** — **the live session store's reconcile against Docker**
    (ISSUES-LOG #18/#20), plus the `AgentSessionReconcilerService` `BackgroundService` that drives it at
    startup and every 30 s. Adoption reads the parent off `mainguard.agent.parent` and hands each adopted
    session to an `onAdopted` hook, which the composition root binds to
    `AgentSpawnService.TryReattachEndpoint` so an adopted coordinator's tools and an adopted worker's plan
    channel come back with it (before 2026-09-03 adoption rebuilt the record and nothing else). The two boot reconcilers (`SwarmReconciler` → the SQLite expected-agents
    table, `LeaderReattachTask` → the PTY leader registry) never wrote to `AgentSessionStore`, which is
    what `ListAgents`/`StreamAgentEvents`/the resource monitor/the kill switch actually render — so a
    restarted daemon reported zero agents while their jails kept running, and a `docker pause`/`unpause`
    run outside the app left a stale state word standing indefinitely. This pass: adopts a live jail with
    no session record (kind + orchestration role read off its own `mainguard.kind` /
    `mainguard.agent.role` labels), corrects the **pause axis only** toward Docker (never flattening
    `RateLimited`/`Yielding`/`AwaitingReview` to `Working`), and marks a session whose container is gone
    `Unresponsive` with the reason. It destroys nothing — no container is ever stopped or removed, and a
    stopped-but-present persistent jail is left alone because the engine re-starts those by name.
    Adoption is gated on `ownsRepo` (this daemon hosts the repository's bare mirror), since the container
    engine is machine-wide; the lister is deliberately allowed to THROW so an unreachable engine skips
    the pass instead of reading as "every jail vanished". `MAINGUARD_DISABLE_SESSION_RECONCILE=1` turns
    it off (the `Mainguard.Server.Tests` module initializer sets it — the Mac mirror root `~/mainguard`
    is not under `MAINGUARD_DATA_ROOT`, so an in-proc test daemon would otherwise adopt a developer's
    real jails). **ISSUES-LOG #24 — the pass now also sweeps every registered `IMergeQueueRegistry` queue
    through `MergeQueue.ReconcileJails`, off the listing it already took** (a second Docker timer would
    poll the engine twice for one fact and then have to decide which copy wins). Merge-queue rows had the
    identical push-only defect: stopping an agent is not a queue transition and a jail dying out of band
    is not one either, so entries kept reporting `Working` — with Verify enabled — about agents that had
    not existed for days (found live: 15 such rows against ONE real container). The sweep moves **no merge
    state**; it corrects the *jail-liveness axis* only, and its liveness rule is two-sided (Docker settles
    what the engine can see; the session store settles the starting-container window the spawn path has
    just written). Reported as `QueueStranded`/`QueueRecovered` on the report and in the `queue_stranded`/
    `queue_recovered` audit fields. Pinned by `Mainguard.Server.Tests/AgentSessionReconcileTests` +
    `AgentSessionReconcileDockerTests` + `MergeQueueJailReconcileDockerTests`.
  - **`Runtime/SessionKeyCache.cs`** (PR3) — memory-only per-kind model-key cache (the daemon has no
    keystore; keys only arrive on `SpawnAgent`), so a coordinator-initiated worker of the same kind
    reuses the client-supplied key; also caches the per-kind CLI login-state files a client spawn
    restored (and a stop harvested), so an IPC-spawned worker boots signed in too; and the per-(repo,
    kind) **CLI settings** (`RememberCliSettings`/`TryGetCliSettings`), so an IPC-spawned worker inherits
    the repo's approved-command list instead of stalling on prompts. A blank repo handle forms no scope
    and is dropped rather than collapsed into a shared bucket (MG-6). Never persisted, never logged.
- **`Runtime/CoordinatorSpawnGate.cs`** (**MG-2**) — the pure admission decision in front of the
  coordinator's in-jail spawn shim:
  `Evaluate(activeManagedWorkers, maxActiveWorkers, admission, planGate?)` returns a refusal reason or
  `null`. The cap is checked **before** admission, so a coordinator cannot fan out past
  `maxActiveWorkers` by racing the admission controller — the shim path previously had no approval, cap,
  admission or budget gate at all. **Phase 2:** workers blocked on plan approval were already inside the
  counted population (they are live Managed sessions, and the cap is a resource cap), but the refusal
  said *"let one finish before spawning another"* — wrong and unactionable when nothing is going to
  finish without the human. With a `planGate` it now names the cause and the count; without one it keeps
  the generic wording rather than asserting a cause it never checked. **It also has a second caller:
  `Runtime/ExternalPrWorkerHost.cs`**, so both daemon-driven spawn paths are admitted by one evaluator
  over one population.
  **Defect D1 — `RefuseUnknownKind(agentKind, installedKinds)`.** A real coordinator's first move was
  `mainguard-agent spawn coder "…"`; `coder` is no adapter id, so the launcher resolved no CLI, the jail
  came up running `sleep infinity` and nothing else, and the shim answered `Ok, Status: AwaitingPlan` —
  a dead worker the coordinator believed in, holding a slot against the worker cap. The refusal names the
  kind and every installed one, rendered by the same `AgentOperatingInstructions.SpellKinds` the
  coordinator's instructions use, so the text and the enforcement read one set. It is called ONLY from
  `AgentSpawnService.SpawnWorkerAsync` and deliberately not from `SpawnAsync`: a CLI-less jail is a wanted
  outcome of the operator path (a bare sandbox with a human on the PTY) and of `ExternalPrWorkerHost`
  (kind `external-pr`, which no adapter answers to by design), and neither may be taken away. An EMPTY
  catalog stays permissive — the documented meaning of `InstalledAdapterCatalog.HasAny`, and the only
  honest behaviour when there is no list of alternatives to offer. Covered by
  `Mainguard.Server.Tests/CoordinatorSpawnKindTests.cs`.
  **Contract §3 change, 2026-08-29 — the brief is refused, never derived.** `SpawnWorkerAsync` calls
  `WorkerPlanGate.RefuseBrief(request.Title, request.TaskPrompt)` before anything is minted, and
  `SpawnAsync` re-checks it before `_store.Spawn` so a refusal leaves no session record. The old
  `heldTaskTitle: request.Title ?? request.TaskPrompt ?? "Untitled task"` was the defect: the shim sent no
  title, so every worker's `mainguard-plan brief` returned its TASK. The channel check is required rather
  than merely defensive — `SpawnAsync` reads "neither title nor task" as *not plan-gated* (the operator's
  own spelling), so a title-less shim request would otherwise have produced an **ungated** Managed worker.
  Covered by `WorkerPlanChannelIpcTests.ASpawnWhoseBriefIsMissingOrIsTheTask_IsRefused_AndSpawnsNothing`
  and `Agents/AgentIpcJailDockerTests.TheRealShimsSpawn_*` (the real shim, in a real jail).
- **`Runtime/ExternalPrWorkerHost.cs`** — the daemon's `IPrWorkerHost`: gives an intake'd upstream
  pull request a REAL jail by running the ONE spawn chain (`AgentSpawnService.SpawnAsync`) under the
  id `pr-<n>`, kind `external-pr` (no installed adapter answers to it ⇒ no CLI, no launch command),
  `modelApiKey: null`, role `AgentRoles.Managed`, `queueOrigin: External`,
  `withoutHostCredentials: true`. **MG-2, none of it bypassed:** the kill switch (inside
  `SpawnAsync`), the `MaxActiveWorkers` cap and memory admission (`CoordinatorSpawnGate.Evaluate` over
  the SAME active-Managed population a coordinator's fan-out is capped by — an arriving bot PR gets no
  private allowance), and budget by construction (no model key, no gateway session, no CLI ⇒ nothing
  to spend). A refusal is typed, not thrown, and leaves no session behind holding a cap slot; a
  session-only result (unprovisioned mirror) is `Stop`ped and reported as `Failed` rather than
  admitted as a jail-less entry.
  - `EnsureWorkerAsync` is idempotent through two lookups — this daemon's session store, then the
    container runtime's `mainguard.repo`/`mainguard.agent` labels (the same source of truth the merge
    queue uses), which is what lets a restarted daemon adopt a still-running `pr-<n>` jail instead of
    failing to respawn one forever.
  - `ReleaseWorkerAsync` goes through the ordinary `StopAsync`, falling back to
    `SandboxAgentLauncher.TeardownAsync` for a jail whose session a restart forgot — otherwise the
    container and its MG-36 segment leak for the life of the VM (the bridge pool is ~32 deep). **That
    known limit is fixed:** `pr-<n>` is the right id *inside* a repo, and `AgentSessionStore` is now
    keyed by `(repo, id)` to match, so two subscribed repositories that each have a pull request #n each
    get their own session and their own jail — the second is no longer `Failed` by name. Every lookup
    here carries the repo (`EnsureWorkerAsync`/`ReleaseWorkerAsync` both take `repoHash`), so one repo's
    release can never stop, adopt or unlock another's worker. It then calls
    `IAgentWorktreeManager.DiscardAgentBranch` on **every** release path — the teardown itself now keeps
    any branch carrying a commit, which is right for a worker whose work exists nowhere else and wrong
    here twice over: an intake'd branch's commits were fetched FROM the pull request and still live
    there, and `pr-<n>` is a reused id, so a kept branch would make the next intake of that number
    collide with `CreateAgentWorktree`'s duplicate refusal on every poll forever. The early `return` on
    the stopped-a-live-session path was removed for that reason: a discard behind it would have been
    unreachable on exactly the case it exists for.
- **`Runtime/ActiveRepoIndex.cs`** — which repositories this daemon has provisioned, and where the
  user's copy of each one is (`ActiveRepo(Handle, RepoPath)`; recorded by
  `RepoSyncGrpcService.ProvisionRepo` in the daemon-openable form). Everything daemon-side is keyed by
  the one-way repo HASH, so once a repo was provisioned the daemon could no longer say which
  repository it was — fine for mirrors and worktrees, fatal for the external-PR intake, whose T-23
  transport resolves host, slug and token *from a repo path*. Memory-only by design (re-populated by
  the client's ordinary re-provision), the same posture as the merge-queue registry.
- **`Runtime/PrIntakeTargetResolver.cs`** — `ExternalPrSource` (`host/owner/repo`) →
  `PrIntakeTarget(repoPath, repoHash, MergeQueue)`, replacing the hardwired `_ => null`. Matching is
  on each active repo's OWN origin remote (`GitHostDetector.Detect` + `ParseOwnerRepo`,
  case-insensitive), so a subscription cannot point the intake at a repository it does not describe;
  the queue comes from `MergeQueueProvisioner.EnsureQueue` (idempotent, null when the mirror is not
  provisioned). Never throws — an unreadable repo is simply not a match, so one stale index entry
  cannot poison every source.
- **`Runtime/SandboxAgentLauncher.cs`** (P2-07/P2-47 #8) — the daemon-side spawn chain behind
  `AgentGrpcService.SpawnAgent`, kept out of the gRPC class (validation+dispatch only): provision the
  per-agent ext4 worktree → ensure the default-deny egress network/proxy → **ensure this agent's OWN
  network segment (MG-36: `Egress.EnsureAgentSegmentAsync`, reclaimed in `TeardownAsync`)** → start
  the hardened jail **from the content digest the preflight resolved (MG-27), on that segment, with
  `HTTP(S)_PROXY` pointed at the proxy's address ON that segment** (an address rather than the proxy's
  name, because one dnsmasq cannot answer the same name differently per segment); returns the real
  container id, or `null` when the repo isn't provisioned (session-only degradation, not a fabricated
  jail), cleaning up a half-made worktree on any failure after it exists. **P2-48 makes `agentKind`
  mean something:** it resolves through `InstalledAdapterCatalog.TryGetLaunch(agentKind)` to the
  launch argv of the CLI the user dynamically installed (returned on
  `SandboxLaunchResult.LaunchCommand`), and passes `AdaptersRootPath` so the jail bind-mounts the
  shared CLI root read-only whenever any CLI is installed. An unknown kind still spawns a correct jail
  with no launch command rather than failing the spawn. **`BuildLaunchArgv` is the one place that knows
  the launch line's ORDER** — `ApplyInitialPrompt` (the worker's first user turn,
  `AgentKickoffPrompt`) FIRST, then the role's operating instructions on `systemPromptArg`, then
  `ApplyShimPreApproval`'s single grant. The order is the fix, not a style choice: measured against
  claude-code 2.1.250, a turn appended last is swallowed by the variadic `--allowedTools <tools...>`
  and never reaches the model, so the CLI idles at an empty input box exactly as it did with no turn —
  which is the deadlock that stopped phase 2's plan loop from ever starting (a worker cannot present a
  plan without a first turn, and `send_worker_prompt` is refused until it has). All three channels are
  gated on `ipcDirPath` + the adapter's own declaration, so a jail with no shim, and every CLI that
  declares nothing, launches byte-identically to before. **v1 spawn preflight (field failure
  2026-07-17, twice):** before any worktree/jail work it verifies BOTH jail images present AND current
  — `ISandboxEngine.ImageExistsAsync` for presence, then `ISandboxEngine.ImageVersionAsync` (Docker
  `Config.Labels["mainguard.image.version"]`) vs `SandboxImageVersions.For(ref)` for staleness
  (agent-base via the shared `SandboxImageVersions.AgentBaseRef()` +
  `EgressProxyConfigurator.DefaultImageRef`) — and throws the typed `SandboxImageMissingException`
  (carrying reason-tagged `SandboxImagePreflightProblem`s) naming exactly the missing/outdated
  image(s) + the repair — one actionable error whether the agent-base OR the egress-proxy image is
  missing or stale (the egress one previously failed opaquely inside `Egress.EnsureReadyAsync`).
- **`Terminal/TerminalStreamer.cs`** (P2-03) — batches PTY output into gRPC `raw` frames on the 16
  ms cadence via `ArrayPool` buffers, `VtBoundaryDetector`-guarded so no VT sequence/UTF-8 codepoint
  is ever split, with a 4 KB holdback cap (flush-anyway for a malformed endless escape) and pooled
  buffers returned on every path (memory flat under a firehose). Deterministic `Ingest`/`TryDrain`
  core + a `RunAsync` pump.
- **`Terminal/TerminalEngineConfig.cs`** (P2-18) — the daemon's `TerminalEngine=libvterm|interim`
  flag (env `MAINGUARD_TERMINAL_ENGINE` / `--terminal-engine`, default interim), `Resolve()` degrading
  a libvterm request to interim where the native library is absent (Windows local-dev) so a
  misconfigured flag can never take terminals down. Registered as a DI singleton in `DaemonHost`;
  - `AgentCliBinder` passes it into each `BoundTerminalSession`.
- **`Terminal/GridUpdateBuilder.cs`** (P2-18) — engine-neutral vterm output → wire `GridUpdate`
  protos: run-length rows (same-style cells collapse; ASCII hot path packs one-char-per-cell into
  `CellRun.packed` so steady-scroll traffic stays ~2–3× content; `blanks` counts preserve the
  written-space vs positioned-gap distinction), first-class scroll/pop ops, cursor, modes, and the
  colour wire encoding (high byte 0=default/1=indexed/2=rgb) the client mirrors.
- **`Runtime/BoundTerminalSession.cs`** (P2-03, extended by P2-18) — the long-lived agent-bound
  session: raw replay ring + fan-out + `TailText` (unchanged), and with the libvterm engine also one
  `VtermSession` fed the same 16 ms VT-safe frames under the session gate — `SubscribeGrid` (atomic
  full snapshot + live `GridUpdate`/`ClipboardCopy` frames), `Resize` (PTY + vterm in the same breath,
  then a fresh snapshot — preceded by a ring-only update carrying the reflow's scrollback pushes/pops
  so the client ring never desyncs), `GetScrollback` (the lazy-fetch RPC's data source).
- **`Services/AgentGrpcService.cs`** (**PR3:** validation+mapping only — `SpawnAgent`/`StopAgent`
  dispatch to the shared `AgentSpawnService` workflow (typed exceptions → status codes via the shared
  `MapLaunchFailure`, incl. the v1
  spawn preflight's `SandboxImageMissingException` → actionable `FailedPrecondition` naming the
  missing jail image + repair; `role` rides the request/`AgentInfo`/snapshot); **`ResumeAgent`**
  dispatches to `AgentResumeService` and derives the actor from the connection (there is no actor field
  on the request), answering a refusal as an ordinary response with `resumed=false` + a verbatim reason
  rather than a status code — so a caller must not read "no exception" as "it resumed"; and the new
  **`ListInstalledAdapters`** RPC surfaces the `InstalledAdapterCatalog` markers —
  ids/versions/env-var NAMES only, no paths/secrets; **`GetDaemonInfo`** answers the tier-1 skew probe
  from the injected `Runtime/DaemonInfoProvider.cs` — the daemon's assembly informational version +
  the `MAINGUARDOS_VERSION` parsed from `/etc/mainguardos-release` (overridable path for tests;
  absent/unreadable stamp → "" — the probe never throws); **`StreamAgentResources`** streams live
  per-agent CPU/RAM + the `metered` flag from `Runtime/AgentResourceProbe.cs` on a
  `ResourcePollInterval` (5s) loop — **sampling is driven by the subscription**, so with no client
  attached the daemon makes no engine calls. Whole-set snapshots (a torn-down agent drops out rather
  than keeping stale numbers), and the `cpu_percent`/`mem_bytes` fields are proto3 `optional` so
  "unknown" is carried explicitly rather than defaulting to a 0 that reads as "idle"),
  **`TerminalGrpcService.cs`** (P2-03/PR3: a
  **bound** CLI session streams replay-then-live frames — a detach only unsubscribes, a locked
  (managed) attach gets the banner + output but `PERMISSION_DENIED` on input; otherwise the per-attach
  `PtySession` factory path through `TerminalStreamer`, else — for an agent the session store KNOWS
  but that has no bound CLI — the `DetachedNotice` attach (ISSUES-LOG #23: says so in one unprompted
  frame and discards input, instead of a silent echo that emitted nothing until the user typed and so
  read, client-side, as a CLI still starting up forever), else the P2-02 echo for an unknown id. P2-18: an
  `AttachOptions(grid:true)` first frame on a libvterm-engine session takes the grid pump instead —
  atomic snapshot then live `GridUpdate`/`ClipboardCopy` frames, same input/lock semantics — and the
  new `GetScrollback` RPC pages the session's daemon-side ring), **`RepoSyncGrpcService.cs`** (P2-06:
  real bodies — validate + dispatch to the injected `IAgentEnvironment`'s `Repos`/`Worktrees`;
  `ProvisionRepo` fills `sync_remote_name`/`sync_remote_url` from `ResolveSyncRemote`; typed domain
  failures map to gRPC status codes; opaque handles only) / **`GatewayGrpcService.cs`** (P2-08:
  `GetBudgets`/`SetBudgets` persisted via `IBudgetStore` + reflected into the live `BudgetLedger.Caps`
  — P2-13 carried-in from P2-08 maps the proto `Budget`'s per-day caps
  (`usd_micros_cap_per_day`/`token_cap_per_day`) too, so per-day is displayable+editable over gRPC;
  `StreamSpend` bridges the ledger's `SpendRecorded` row feed — replay-then-live — to the server
  stream) / **`MergeQueueGrpcService.cs`** (**`RunVerification` is FROZEN-JAIL guarded** — the human's Verify button reaches the merge queue by this path, which the sibling fix on the coordinator's `request_verification` op did not cover, so pressing it on a conflicted entry started a run whose `docker exec` answers "Container … is paused" and arrived as a provisioning failure on the one screen where that must never be confused with "your tests failed". The predicate is `FrozenJailPolicy.IsFrozen` — SHARED with that guard on purpose, so the two paths cannot drift apart on what "frozen" means — while the wording is this surface's own: the policy's sentences are written for an agent to act on in one turn, and this reader is a person, so the refusal names the two conflict controls beside it. An unknown or Working session is NOT refused: refusing from ignorance would strand every seeded row and every entry whose session died with a previous daemon. P2-10; **H3/H4: `RunVerification` now logs the RESULT** — it logged every
  refusal and never a verdict — and **`GetVerificationLog`** serves the run's artifact CONTENT, bounded to
  the last 256 KiB via `ReadTail` (the tail, because a runner prints its failures last) and answering "no
  record" / "artifact gone" / the log as three distinct things; `Snapshot` carries the entry's verdict,
  command and timestamp so a client can render what the state word stands for: `StreamQueue` re-pushes on the queue's `Changed`
  event, each `QueueEntry` carries the P2-12 `origin` (via `MergeQueue.GetOrigin`) so the activity
  list can badge external-PR entries, plus `verification_in_flight` (via
  `MergeQueue.IsVerificationInFlight`) — the one fact no client can derive, since a restart mid-run
  leaves a persisted `Verifying` row with nothing executing — plus `has_live_sandbox` (`optional`, from
  the injected `AgentSessionStore` keyed on `(repoHandle, agentId)`): whether the entry still HAS a jail,
  which is what lets the rail offer Resume on a stranded row and withhold Verify instead of leaving an
  enabled button whose only behaviour is "has no live sandbox". **(2026-08-31) `Snapshot` also carries
  what a human APPROVED** — `approved_plan_id`/`_title`/`_approach` and the three-valued
  `deviation_declaration`, read through `MergeQueueContext.ResolveApprovedWork`, i.e. the same callback
  the provisioner arms the flagged review from. Without it the review cockpit rendered a diff and nothing
  to compare it against, which is how a branch that shipped the opposite of its approved approach passed
  review with an empty flagged list; left empty for an entry with no approved plan so the surface draws
  no panel rather than an empty one. `optional` because a proto3 `false`
  meaning "this daemon does not report liveness" would render every entry of an older daemon as stranded;
  `Snapshot`'s entry order runs through `OrderForDisplay` (`internal static`, unit-tested in
  `Mainguard.Server.Tests/QueueDisplayOrderTests.cs`) — a stable partition putting actionable states
  ahead of the permanent Merged/Rejected record, since `MergeQueue.Agents`' raw dictionary-insertion
  order buries a fresh spawn behind however much terminal history a repo has accumulated (found live
  2026-08-20, reproduced the "spawned agent isn't in the queue" symptom exactly), and **within** the
  terminal group ordering newest-decision-first by `MergeQueue.LastChangedAt` — insertion order is
  SPAWN order, so the partition alone put a just-rejected branch dead last on the rail and the human
  who clicked Reject saw the entry leave the panel (ISSUES-LOG #13, filed HIGH against a row that was
  rendering below the fold the whole time); `RunVerification`/`CanMerge`/`BeginMerge`/`ConfirmMerge` —
  resolves the per-repo `MergeQueue` via `IMergeQueueRegistry`, typed `NOT_FOUND` for an unknown
  handle; **P2-47 #7 adds `GetMergeDiff`** dispatching to the injected `IMergeBranchDiffService`,
  typed `NOT_FOUND` when the mirror/branch is missing; **P2-11 wiring:** `FlaggedItemsFor` projects the
  `FlaggedChangeGate`'s items (risk-hunk + out-of-approved-scope rows, addressed by
  `FlaggedChange.Id` = `kind|path|contentHash`) alongside the RT-D2 row — it read
  `ChangedTestCommandGate` alone, so a branch the daemon blocked reached the human with nothing to
  clear — and `AcknowledgeFlaggedChange` routes any non-RT-D2 item id to that gate's store. Both use
  `PeekStore`, never `StoreFor`: creating a store from a read/ack would fabricate a fully-acknowledged
  record and bypass the gate's default-DENY. **L2/L4 audit (§20 of the phase-3 decisions doc):** `ConfirmMerge` derives the actor from
  `IApproverIdentityResolver` and passes `MergeAuthorization.ConfirmRpc(actor, leaseId)` into
  `TryConfirmHumanMerge` (which is where `queue_entry_merged` is appended), and `AcknowledgeFlaggedChange`
  passes the same daemon-derived actor into `ChangedTestCommandGate.Acknowledge`. This service also owns
  **`ConfirmRefusedEvent` (`merge_confirm_refused`)**, appended when a confirm is refused at either the
  lease, identity or gate stage — the one merge-conversation fact knowable only here: by `ConfirmMerge` time the
  git operation has ALREADY RUN on the user's checkout, so a refusal means the daemon and the user's
  repository may now disagree about what main is. Best-effort (swallowed into the daemon log), unlike the
  merge record itself, because the refusal reason must not be replaced by an audit-store error. A refused
  `BeginMerge` is deliberately NOT audited — it is a merge that has not happened.
  **K3/§23.4 merge identity:** `BeginMerge` puts BOTH halves of the identity on the lease — the queue's
  `CurrentMainSha` and `LastVerification(agent)?.BranchSha`, the `agent/<id>` tip the verification was
  measured on — and returns both to the client (`expected_branch_sha`), for the same reason
  `expected_main_sha` already travelled there: the client's projection is a stream snapshot. `ConfirmMerge`
  then SCREENS the `new_main_sha` the caller reports, which nothing used to look at even though the daemon
  wrote it into the idempotency record, set the queue's authoritative main to it, and cascaded every
  co-tenant onto it. Three checks, all before the transition and all against the daemon's own records:
  shape (7–64 hex — deliberately a shape check, since the daemon cannot resolve a sha in a repo it does
  not hold), non-triviality (a confirm reporting the main it was authorized against moved nothing), and —
  for a `Local` entry only — that the reported sha IS `lease.ExpectedBranchSha`, because
  `git merge --ff-only agent/<id>` leaves main AT the source's tip. Stated as a limit rather than
  stretched: the P2-12 external leg lands the host's merge commit, which is not the PR head, so the same
  equality would be false for every honest external merge (that path has its own head CAS, K4). Each
  refusal releases the lease and audits `merge_confirm_refused` with `stage = "identity"`. **Post-confirm mirror refresh:** `ConfirmMerge` now pulls origin's main forward into the bare
  mirror (`MergeQueueProvisioner.TryRefreshMirrorMainAfterMerge`, best-effort) — without it, a spawn
  between a merge and the next repo-open based its worktree on the stale mirror main and
  `EnsureQueue`'s reconcile walked the queue's authoritative main BACKWARDS to it, leaving
  coherent-but-unmergeable Verified entries (observed live; the E2E suite verifies before merging so
  it never walks that window). **Entry lifecycle:** `DiscardEntry` (and `RejectEntry`, its clone for the review verdict) refuses while this
  repo's outstanding merge lease names the entry — a terminal transition inside the
  `BeginMerge`→`ConfirmMerge` window would make `ConfirmMerge` refuse to record a merge that really
  landed — derives the actor from `IApproverIdentityResolver` (never the request; there is no such
  field), and answers a refusal as `discarded=false` + reason rather than a fault. It is deliberately
  **not** kill-switch-gated: freezing the queue stops merges, and is no reason to forbid tidying an
  entry that cannot merge either way. `ClearStalledVerification` returns a stalled `Verifying` entry to
  `Working`, refusing while a run is genuinely in flight) — validation/dispatch only (no business logic —
  rejection trigger). **P2-14:**
  - `MergeQueueGrpcService.BeginMerge`/`ConfirmMerge` and `AgentGrpcService.SpawnAgent` now consult the
    shared `KillSwitchGate` and return `FAILED_PRECONDITION` while frozen (SA-1/F4);
  - `TerminalGrpcService` writes a read-only banner + defensively rejects input `data` frames for a
    `TerminalLockRegistry`-locked agent.
  - **`Services/QueueSeedingGrpcService.cs`** (the DEV-ONLY seeding transport —
    docs/design/queue-seeding.md; validation + dispatch to `QueueSeeder`, actor daemon-derived via
    `IApproverIdentityResolver`, `RepoProvisioningException` → typed `NOT_FOUND`, per-entry verbatim
    refusals in the response body. Mapped by `DaemonHost.MapServices` ONLY when
    `QueueSeedingOptions.Enabled` (the primary gate; disabled ⇒ UNIMPLEMENTED, which is also the dev
    panel's hide probe), prefix-denied by `SeedingGateInterceptor` as the belt, coordinator-denied at
    `RoleInterceptor` unconditionally, and its constructor REFUSES to build on a flagless daemon as
    the last brace. `SeedEntrySpec`'s `with_plan`/`scope` map onto `SeedSpec.WithPlan`/`Scope` — an
    empty repeated `scope` deliberately becomes `null`, not an empty list, because "no scope named"
    selects the seed's own path while an empty `TaskPlan.Scope` would put every file out of scope.
    Seeds are logged at Warning — a seeding daemon should read loud in its own log.)
  - **`Services/PrIntakeGrpcService.cs`** (P2-12: `GetPrIntakeSettings`/`UpdatePrIntakeSettings`/
    `SubscribePrIntakeSource` over the daemon's `IPrIntakeStore`, mapped in `DaemonHost.MapServices`
    beside `MergeQueueGrpcService`. Validation + dispatch only. **`Update` persists, then re-READS and
    answers with what was stored** — the store clamps the cadence and substitutes the default bot list
    for an empty one, and echoing the request instead would show a human a cadence the poller is not
    using. `Subscribe` persists then tells the LIVE `IExternalPrIntake` (idempotent on the store, so the
    engine and the store cannot disagree and the source is polled without waiting for a restart to
    re-seed it); an incomplete source is `INVALID_ARGUMENT`, because a row with no repository can never
    resolve and would be skipped silently forever. Both writes are on `RoleInterceptor`'s
    coordinator-denied list — subscribing provisions jails. **Why it exists:** the App had a complete
    intake settings dialog with nowhere real to write, so the feature was unconfigurable; the daemon owns
    the configuration because the daemon is what polls and provisions.)
  - **`Services/PlanApprovalGrpcService.cs`** (`StreamPlans`/`ApprovePlan`/`RejectPlan` over the
    daemon `PlanApprovalService`; **`ApprovePlan` resolves the approver via `IApproverIdentityResolver`
    from the connection — the request has no identity field**, SA-1/F2. **Phase 2:** the streamed
    `PlanEntry` carries the authoring worker, the revision index and the rejection feedback; `RejectPlan`
    reports whether that rejection **escalated** (spent the budget) rather than a bare `rejected: true`,
    which could not distinguish "the worker will revise" from "the worker has stopped"; and `PlanUpdate`
    carries the **backpressure** counts + the daemon's rendered stall line, taken from the same
    `WorkerPlanGate` and Managed-session population that refuses the coordinator a spawn — a surface that
    re-derived its own number could disagree with the gate it is rendering. **The plan-mode toggle**
    adds `GetPlanMode`/`SetPlanMode` — the actor on the audit event is the same daemon-derived peer
    credential that records an approver, the response is read BACK from the switch rather than echoing the
    request, and `PlanUpdate` carries the state on every update including the empty one) and
    **`Services/KillSwitchGrpcService.cs`** (`Engage`/`Resume` over the daemon `KillSwitch`).
  - **`Services/AuditGrpcService.cs`** (P2-15) — transport for `AuditService` (`VerifyAudit`/
    `ReadAudit`): the audit store's first production readers. Verification/decryption live in
    `IChainedAuditLog`; on the in-memory fallback journal both RPCs still answer with
    `persistent=false` (a heap verify must never read as tamper-evidence). Coordinator-denied at
    the `RoleInterceptor`; `ReadAudit` pages are capped at 500 records (payloads carry full
    prompts/outputs).
  - **`Cli/AuditCommands.cs`** (P2-15) — the offline `mainguardd audit verify [--data <db>]` verb
    (dispatched in `Program.cs` before daemon options, so it can never bind a port): walks the
    chain + mirror via `ChainedAuditLog` and validates stored RFC 3161 anchor tokens structurally
    (an anchor that no longer matches its recorded head hash exits 2 like chain tamper), prints
    head seq/hash; exit contract 0 intact (missing store / pre-chain DB = intact by definition) /
    2 tampered with first-bad-seq printed / 64 usage / 1 cannot-verify.
  - **`Runtime/SandboxKillTarget.cs`** (MG-8) — the `IKillTarget` that actually **stops work**, in
    three ordered steps: sever terminal input (`TerminalLockRegistry` + `SessionLeader.PauseInput` —
    in-proc and I/O-free, so they run BEFORE any Docker round-trip and an unreachable engine can never
    leave keystrokes reaching a killed agent), then `docker pause` the jail via
    `ISandboxEngine.PauseAsync` (freezer cgroup — no cooperation needed from the untrusted agent),
    then mark session state, with an unpausable jail marked `Unresponsive` rather than `Paused`. It
    REPLACED `SessionStoreKillTarget`, which only wrote `MarkState(…, "Paused")` while every process
    kept executing and every terminal stayed typeable — containment that was really just relabelling.
    Also the **release** half (`UnpauseAsync`, ISSUES-LOG #17): it keeps a per-agent **causation ledger**
    of what it actually transitioned — which containers *it* paused, and whether *it* took the terminal
    lock / closed the leader's input gate — and `KillSwitch.ResumeAsync` reverses exactly those entries.
    A jail already frozen when the stop fired (a human pause, or the keep-alive rebase's yield hold —
    detected by engine STATE, never by error text) is reported as contained but is NOT recorded as ours,
    so Resume leaves it paused; the injected `IPauseArbiter` (the `HumanPauseLedger`) is re-checked at
    release time so a human pause that lands DURING the freeze also wins. A terminal lock taken at spawn
    (a role property) survives the cycle — that was the concern behind the original "Resume deliberately
    does NOT un-contain", honoured precisely instead of by refusing to recover at all. A container that
    no longer exists is released by definition (logged, skipped); an unpause the engine refuses marks the
    session `Unresponsive` with "the jail is STILL paused" and keeps it in the retry ledger.
  - `DaemonHost.cs` registers one `IAgentEnvironment` (`Wsl2AgentEnvironment`) as a singleton, the P2-14
    governance singletons (`ConnectionRoleRegistry`, `TerminalLockRegistry`,
    `IApproverIdentityResolver`, `CoordinatorLimits`, `PlanApprovalService` over a restart-safe
    `JsonPlanApprovalStore` (**limits injected** — the revision budget is enforced there, not prompted),
    the phase-2 `WorkerPlanGate` over a restart-safe `JsonHeldTaskStore` (`mainguard-held-tasks.json`
    beside the plan store — a gate whose held tasks died with the process was a gate until the first daemon
    update; also wired into `MergeQueueProvisioner` as an `IMergeGate` by `GatewayServiceRegistration`), the
    shared `KillSwitchGate`, `IKillTarget`, `KillSwitch`) + the `RoleInterceptor`, the P2-47
    `SandboxAgentLauncher` (real spawn chain) + `IMergeBranchDiffService` (merge-diff bridge)
    singletons, the PR3 CLI-agent singletons (shared `InstalledAdapterCatalog`, `SessionKeyCache`,
    `AgentCliBinder`, `AgentIpcServer` rooted next to the test-isolated session token,
    `AgentSpawnService`), the `DaemonInfoProvider` instance (default release-file path; tests override
    with a temp-file provider), and maps the gRPC services;
  - `GatewayServiceRegistration` wires the `IMergeQueueRegistry`, the `IMergeLeaseStore`, and the real
    RT-D1 `MergeReconcileTask` into the boot sequence.

#### Sandbox image versioning convention

Each jail image (`mainguard-agent-base`, `mainguard-egress-proxy`) carries a
`mainguard.image.version` docker **label** = a sha256 of that image's **curated build inputs** (an
explicit per-image file list — agent-base `[Dockerfile]`; egress-proxy
`[Dockerfile, entrypoint.sh, reload.sh]`; `README.md`/`seccomp.json` are excluded — seccomp is a
*runtime* input embedded in Core, never `COPY`'d). The expected value is a **committed Core
constant** (`Mainguard.Agents/Agents/Sandbox/SandboxImageVersions.cs`) that the app staleness probe,
the daemon spawn preflight, and CI (which stamps it as the label) all reference;
`SandboxImageVersionsGuardTests` recomputes it from `images/<name>/` and fails on drift, printing
the new hash to paste. **Discipline:** an image source change ⇒ a new hash constant ⇒ **both** the
app and the daemon must carry it (the daemon preflight compares it) ⇒ a **lockstep App/Server
version bump** — but **no `build/mainguardos/VERSION` cut** (the images ship *beside* the app as
`payload/images/`, not inside the MainguardOS tarball). Provisioning prefers `docker load` of the
bundled CI `<name>.tar` (approach B; the label rides `docker save`/`load`) and falls back to a
labelled in-VM `docker build`; both stay provisioning-time (G-16 forbids only *agent-runtime*
builds/loads). CI (`ci.yml` sandbox-security) builds the images with the label read from the
constant and `docker save`s them to `build/images/<name>.tar` (gitignored, uploaded as an artifact);
the App bundles them via `$(MainguardImageTars)` → `payload/images/<name>.tar` (warn-if-missing).
**MG-27 — the label is a STALENESS signal, not an integrity anchor.** A docker label is an arbitrary
string chosen by whoever built the image, so it cannot tell "the image we shipped" from "an image
someone stamped with our version", and `:latest` is a mutable pointer, so the preflight used to
verify whatever the tag named at check time while the create re-resolved the tag independently.
Integrity now comes from the **content digest**: the spawn preflight resolves each ref once via
`ImageDigestAsync` and the jail (and the proxy) are created from that `sha256:` digest, so the bytes
that were checked are the bytes that run. What the images build FROM is pinned too — both
Dockerfiles use `FROM debian:bookworm-slim@sha256:…`, and `mainguard-agent-base` replaced its two
`curl | sh` installs with version-addressed downloads verified by `sha256sum -c` (the Determinate
`nix-installer` binary at a pinned tag; the devbox binary from its pinned GitHub release, verified
byte-identical to what the old launcher-and-`find`-the-cache recipe produced). A *committed*
expected digest is deliberately NOT enforced: the in-VM `docker build` route is not reproducible
(apt, and network-fetched toolchains), so a committed digest could only ever match the CI-tar route
and would fail every local rebuild — recorded as follow-up behind reproducible image builds.
Coverage: `Mainguard.Tests/SandboxImageVersionsTests.cs` (the `For`/untagged-name/`AgentBaseRef`
logic), `Mainguard.Tests/SandboxImageDigestTests.cs` (MG-27 digest algebra),
`Mainguard.Tests/Headless/SandboxImageRenderHarness.cs` (the loading-screen "installing sandbox
images" state, all 5 themes), and `Mainguard.Server.Tests/SpawnImagePreflightTests.cs`
(label-mismatch → `FailedPrecondition`). See `docs/mainguardos-updates.md` §"Versioning discipline".

#### Daemon logging (mainguardd) convention

The daemon emits **in-depth, always-on, secret-safe** diagnostics organized as **one subsystem per
file**, two sinks fed by one pipeline (registered in `DaemonHost` only when `!options.Smoke`, so the
`--smoke` CI job stays byte-silent): a single-line **journald** stream (`journalctl -u mainguardd`)
and per-subsystem **rolling files** under `~/.mainguard/logs/<subsystem>.log` (5 MB × 3, tail-able,
survive tier-1/tier-2 upgrades). The 12 categories are `DaemonLogSubsystems.All`
(`lifecycle · migration · rpc · spawn · egress · gateway · terminal · merge · approval · killswitch · coordinator · intake`);
a new daemon subsystem adds one name there + one `DaemonLogCategories` constant (the P2-46/P2-49
extension point). **Mask discipline:** RPC bodies always go through `SecretFieldMask.Redact`; keep
`LoggingMaskTests` + the `// SECRET`-coverage test green — this is G-13 (secret *transport*, not
silence: masked logs are compliant). **Core stays log-free** — instrument Server-side
(`Mainguard.Server`) or tee from existing seams (`IAuditLog`, `INetworkTransparencyLog`); the
operational log is the *diagnostic complement* to the governance audit log, never a replacement.
`MAINGUARD_LOG_LEVEL` (default Information) sets the floor; per-frame paths (terminal streaming)
stay at Debug (off by default). The App reads recent lines through Core's `DaemonLogReader`
(Settings → Daemon logs…); a daemon-logging change is production-bound, so it ships with the
lockstep csproj version bump **and** a `build/mainguardos/VERSION` cut.

## Role in the solution

- **`Mainguard.Server`** (P2-02) — the headless daemon: ASP.NET Core gRPC host, loopback-only bind, session-token auth, secret-mask logging. Publishes linux-x64 for the WSL2 VM; runs on Windows via `--local-dev`. Validation/dispatch only in the gRPC classes — logic lives in Core/daemon services.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
