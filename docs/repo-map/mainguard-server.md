<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.Server/` (P2-02 daemon — ASP.NET Core gRPC host)

- **`Program.cs`** — thin entry point: parses `DaemonOptions`, runs the `--local-dev --smoke` self-probe or the daemon (`app.Run()`), maps a bind failure to a typed `DaemonStartupException`. `public partial class Program {}` so `WebApplicationFactory<Program>` can host it in-proc.
- **`DaemonHost.cs`** — the shared host configuration (services, interceptors, gRPC service map,
  loopback-only Kestrel bind, silent logging) used by both the entry point and the in-proc tests;
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
  `BudgetLedger`, `AdmissionController`, `SwarmReconciler`, `DaemonBootSequence`); best-effort
  DB-backed stores with an in-memory fallback so the daemon always starts, all resolved from DI so a
  test host can override them; **P2-47 adds `RegisterPrIntake`** — the P2-12 external-PR intake chain
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
  - **`Runtime/PrIntakeHostedService.cs`** (P2-13 carried-in from P2-12) — the daemon scheduler slot
    that drives `IExternalPrIntake.RunAsync` (the external-PR poll loop); **P2-47 registered the intake
    dependency chain (`RegisterPrIntake` in `GatewayServiceRegistration`) so this now RUNS the poll loop
    instead of idling** — it resolves `IExternalPrIntake` and cancels it on stop. The per-source target
    mapping is now real (`Runtime/PrIntakeTargetResolver.cs`); a source no active repo owns still
    resolves to null and makes a poll list-and-skip (no upstream writes), the same "empty until a repo
    is active" posture the merge-reconcile takes.
- **`Gateway/GatewayBindPolicy.cs`** (**MG-13**) — the pure bind-address rule for the model-gateway listener: loopback and private ranges are permitted; a wildcard bind and any public address are refused. The gateway fronts the user's provider key, so a listener that answers off-box is a credential-exposure surface rather than a convenience. Pure, so every case runs without a host.
- **`DaemonOptions.cs`** / **`DaemonStartupException.cs`** — parsed launch options (`--local-dev`/`--smoke`/`--port`, loopback-only by construction; `DataPath` overrides the P2-08 daemon SQLite path for test isolation) and the typed startup failure naming the port.
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
  client-asserted) is denied the merge RPCs (`BeginMerge`/`ConfirmMerge`/`AbandonMerge`), the flagged-item
  acknowledgment (`AcknowledgeFlaggedChange` — MG-11, merge power by another name), the human-only
  plan-approval RPCs (`ApprovePlan`/`RejectPlan`) and the scrollback read (`GetScrollback` — MG-30) with
  `PermissionDenied` (the coordinator can't merge or approve its own plans). **Note the honest caveat
  recorded in `coordinator-phase-3-decisions.md` §6:** `ConnectionRoleRegistry.IssueCoordinatorToken` has
  NO production callers, so this governs a credential class nothing currently mints — moot today only
  because the in-jail coordinator has no gRPC route at all (its only channel is the IPC socket, which is
  where phase 3 put the enforceable surface). **Terminal input lock:** wraps the `TerminalService.Attach` request
  stream so a `data` (input) frame toward a `TerminalLockRegistry`-locked (managed-worker) agent is
  rejected server-side while the read/output stream flows — never UI-only.
- **`Auth/ConnectionRoleRegistry.cs`** (P2-14) — maps a bearer token → `ConnectionRole` (primary
  session token = `Operator`; issued/registered coordinator tokens = `Coordinator`).
  - **`Auth/TerminalLockRegistry.cs`** — the set of agents whose terminal input is severed (managed
    workers `Lock`; manual-mode unlocked).
  - **`Auth/ApproverIdentityResolver.cs`** —
    `IApproverIdentityResolver`/`PeerCredentialIdentityResolver`: resolves the plan approver **from the
    connection** (Linux euid / OS user under the loopback same-host trust boundary), never from the
    request (SA-1/F2); the honest host-trust residual is documented in the file.
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
- **`Runtime/AgentSessionStore.cs`** — the in-memory daemon agent registry + snapshot-then-deltas
  event fan-out (host state, not transport); the gRPC classes dispatch here. Appends `spawn`/`stop`
  audit events via `IAuditLog`; `MarkState(agentId, state, reason)` (P2-09) updates a session's state
  + broadcasts a state delta (the sink the real supervisor drives so a pause/rate-limit streams to
  clients). **P2-47 #8:** the `AgentSession` record carries a daemon-side-only
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
    (`AgentStopResult`).
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
  - **`Runtime/AgentSpawnService.cs`** (PR3) — the ONE spawn/stop workflow behind BOTH entry points (the
    `SpawnAgent` RPC and the coordinator's in-jail `mainguard-agent` shim): kill-gate → session record
    (with role) → **the phase-2 task withhold** (`WorkerPlanGate.Hold`, armed the instant the id is
    minted and BEFORE any jail exists — a gate armed after the spawn returns is armed most of the time)
    → IPC endpoint (spawn shim for a coordinator, **plan shim for a worker**; best-effort, audited on
    failure) → worktree+jail
    (`SandboxAgentLauncher`) → CLI bind → managed-worker terminal lock (P2-14); stop tears down record,
    PTY, endpoint, lock, jail, worktree. Typed `AgentSpawnRefusedException` keeps it transport-agnostic.
    **Phase 3 — the role lock (coordinator contract §8).** The coordinator shim handler now serves the
    contract's four tools and nothing else: `spawn` / `status` (+`list`) / `prompt` / `verify`, with a
    deny-by-default `default:` case. Every op naming an agent resolves it through `OwnedWorker`, scoped
    `(RepoHash, AgentId)` to the caller's own fan-out — a stranger's worker is refused with the SAME string
    as a nonexistent one, so the channel is not an existence oracle. A coordinator spawn now sets
    `withoutRepositoryAccess`, so its jail gets no worktree, mirror, per-agent git dir or package cache,
    and it never becomes a merge-queue member (it has no branch, and §4 denies it declaring its own work
    merge-ready).
    Three optional parameters carry the external-PR intake's needs without forking the chain: `agentId`
    (the explicit `pr-<n>` id), `queueOrigin` (the merge-queue badge — the post-attach `EnsureEntry`
    overwrites the origin on every call, so a default `Local` stamp would silently undo the intake's
    `External` and route an upstream PR's merge into a local fast-forward), and `withoutHostCredentials`
    (**trust boundary** — an untrusted PR head inherits neither the per-repo cached `llm_env_*` nor any
    harvested CLI login, and seeds neither). **Phase 2** adds `heldTaskTitle`/`heldTaskPrompt`/
    `heldBudgetUsd` (the work the daemon withholds until the worker's own plan is approved — the shim's
    `taskPrompt` was previously parsed off the wire and then silently dropped, so a coordinator-spawned
    worker received no task at all) and `HandleWorkerPlanRequestAsync`, the worker plan shim's handler:
    `brief` / `present_plan` / `revise_plan` / `await_decision`, where present and revise **do not return
    until a human decides** and an approval is the only thing that yields the task prompt. Plan ownership
    is checked daemon-side and a foreign plan id answers "no plan '<id>'" — the same answer as a plan
    that does not exist, so the channel is not an existence oracle for other agents' work.
  - **`Runtime/AgentIpcServer.cs`** (PR3; renamed from `CoordinatorIpcServer.cs` in **phase 2**, because
    it now serves both roles and being named for one of its two clients would mislead about which agents
    have a channel) — the agent→daemon control channel: one Unix-domain socket per agent served from a
    daemon-owned ext4 dir (12-char agent-id prefix — sockaddr_un limit) that also carries **the one shim
    that agent's role is allowed** — `mainguard-agent` for a coordinator, `mainguard-plan` for a worker.
    The dir is created BEFORE the jail (it is a read-only mount source) and removed on stop. Identity is
    positional — only that agent's jail has the mount — and the **role is fixed on the endpoint**, so a
    worker cannot reach a coordinator op by naming it and vice versa. One newline-delimited JSON request
    per connection (`AgentIpcProtocol`); malformed input gets an error response. Each connection is served
    on its own task, which is what lets a worker's plan presentation **park on the socket for hours**
    without blocking the accept loop or another agent's request.
  - **`Runtime/SessionKeyCache.cs`** (PR3) — memory-only per-kind model-key cache (the daemon has no
    keystore; keys only arrive on `SpawnAgent`), so a coordinator-initiated worker of the same kind
    reuses the client-supplied key; also caches the per-kind CLI login-state files a client spawn
    restored (and a stop harvested), so an IPC-spawned worker boots signed in too; never persisted,
    never logged.
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
    release can never stop, adopt or unlock another's worker.
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
  with no launch command rather than failing the spawn. **v1 spawn preflight (field failure
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
  dispatch to the shared `AgentSpawnService` workflow (typed exceptions → status codes, incl. the v1
  spawn preflight's `SandboxImageMissingException` → actionable `FailedPrecondition` naming the
  missing jail image + repair; `role` rides the request/`AgentInfo`/snapshot), and the new
  **`ListInstalledAdapters`** RPC surfaces the `InstalledAdapterCatalog` markers —
  ids/versions/env-var NAMES only, no paths/secrets; **`GetDaemonInfo`** answers the tier-1 skew probe
  from the injected `Runtime/DaemonInfoProvider.cs` — the daemon's assembly informational version +
  the `MAINGUARDOS_VERSION` parsed from `/etc/mainguardos-release` (overridable path for tests;
  absent/unreadable stamp → "" — the probe never throws)), **`TerminalGrpcService.cs`** (P2-03/PR3: a
  **bound** CLI session streams replay-then-live frames — a detach only unsubscribes, a locked
  (managed) attach gets the banner + output but `PERMISSION_DENIED` on input; otherwise the per-attach
  `PtySession` factory path through `TerminalStreamer`, else the P2-02 echo. P2-18: an
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
  stream) / **`MergeQueueGrpcService.cs`** (P2-10: `StreamQueue` re-pushes on the queue's `Changed`
  event, each `QueueEntry` carries the P2-12 `origin` (via `MergeQueue.GetOrigin`) so the activity
  list can badge external-PR entries; `RunVerification`/`CanMerge`/`BeginMerge`/`ConfirmMerge` —
  resolves the per-repo `MergeQueue` via `IMergeQueueRegistry`, typed `NOT_FOUND` for an unknown
  handle; **P2-47 #7 adds `GetMergeDiff`** dispatching to the injected `IMergeBranchDiffService`,
  typed `NOT_FOUND` when the mirror/branch is missing) — validation/dispatch only (no business logic —
  rejection trigger). **P2-14:**
  - `MergeQueueGrpcService.BeginMerge`/`ConfirmMerge` and `AgentGrpcService.SpawnAgent` now consult the
    shared `KillSwitchGate` and return `FAILED_PRECONDITION` while frozen (SA-1/F4);
  - `TerminalGrpcService` writes a read-only banner + defensively rejects input `data` frames for a
    `TerminalLockRegistry`-locked agent.
  - **`Services/PlanApprovalGrpcService.cs`** (`StreamPlans`/`ApprovePlan`/`RejectPlan` over the
    daemon `PlanApprovalService`; **`ApprovePlan` resolves the approver via `IApproverIdentityResolver`
    from the connection — the request has no identity field**, SA-1/F2. **Phase 2:** the streamed
    `PlanEntry` carries the authoring worker, the revision index and the rejection feedback; `RejectPlan`
    reports whether that rejection **escalated** (spent the budget) rather than a bare `rejected: true`,
    which could not distinguish "the worker will revise" from "the worker has stopped"; and `PlanUpdate`
    carries the **backpressure** counts + the daemon's rendered stall line, taken from the same
    `WorkerPlanGate` and Managed-session population that refuses the coordinator a spawn — a surface that
    re-derived its own number could disagree with the gate it is rendering) and
    **`Services/KillSwitchGrpcService.cs`** (`Engage`/`Resume` over the daemon `KillSwitch`).
  - **`Runtime/SessionStoreKillTarget.cs`** — the interim `IKillTarget` over `AgentSessionStore` (marks
    live sessions `Paused` on a kill; the real cooperative-yield→`docker pause` target swaps in behind
    the seam).
  - `DaemonHost.cs` registers one `IAgentEnvironment` (`Wsl2AgentEnvironment`) as a singleton, the P2-14
    governance singletons (`ConnectionRoleRegistry`, `TerminalLockRegistry`,
    `IApproverIdentityResolver`, `CoordinatorLimits`, `PlanApprovalService` over a restart-safe
    `JsonPlanApprovalStore` (**limits injected** — the revision budget is enforced there, not prompted),
    the phase-2 `WorkerPlanGate` (also wired into `MergeQueueProvisioner` as an `IMergeGate` by
    `GatewayServiceRegistration`), the
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
