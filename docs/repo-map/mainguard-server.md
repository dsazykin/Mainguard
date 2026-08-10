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
  `AcknowledgeFlaggedChange`), the human entry-lifecycle RPCs
  (`DiscardEntry`/`ClearStalledVerification` — a discard an agent could invoke erases the evidence
  blocking its own branch instead of clearing the gate, and clearing a stalled verification puts a
  branch into the state a re-verification starts from), **`AgentService/ResumeAgent`** (adoption is
  strictly MORE power than the merge RPCs above: an agent able to adopt an arbitrary id could attach a
  writable jail to another agent's branch and have the daemon verify what it put there — and because
  this interceptor dispatches by METHOD, that is why resume is its own RPC rather than a field on
  `SpawnAgentRequest`) and the human-only
  plan-approval RPCs (`ApprovePlan`/`RejectPlan`) with `PermissionDenied` (the coordinator can't merge
  or approve its own plans). **Terminal input lock:** wraps the `TerminalService.Attach` request
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
    (`AgentStopResult`). It owns the same two halves for the **CLI SETTINGS round-trip**:
    `FilterCliSettings` admits only (root, path) pairs the marker's `settingsPaths` declares and caps
    each file at `AdapterSettingsPolicy.MaxFileBytes` — the stakes are higher than for a login, because
    these files carry a permission allowlist and an unfiltered path would let a compromised client plant
    pre-approved commands anywhere in the home or the checkout — and `HarvestCliSettingsAsync` reads
    them back out (size-checked in the shell, so an oversized file never enters daemon memory),
    resolving each root through `DockerSandboxEngine.SettingsRootPath` so restore and harvest cannot
    address different directories.
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
    (with role) → coordinator-only IPC endpoint (best-effort, audited on failure) → worktree+jail
    (`SandboxAgentLauncher`) → CLI bind → managed-worker terminal lock (P2-14); stop tears down record,
    PTY, endpoint, lock, jail, worktree. Typed `AgentSpawnRefusedException` keeps it transport-agnostic.
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
    A further flag, `adoptExistingBranch`, is the RESUME flag: it
    routes the launcher to `AdoptAgentWorktree` (start on this id's EXISTING `agent/<id>`) instead of
    `CreateAgentWorktree`, and switches the post-failure cleanup to the branch-preserving one. It asks no
    authorization question — that is `AgentResumeService`'s job, and a spawn that could name any id
    without it would let one agent adopt another's branch.
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
  - **`Runtime/CoordinatorIpcServer.cs`** (PR3) — the coordinator→daemon spawn channel: one Unix-domain
    socket per coordinator served from a daemon-owned ext4 dir (12-char agent-id prefix — sockaddr_un
    limit) that also carries the executable `mainguard-agent` shim; the dir is created BEFORE the jail
    (it is a read-only mount source) and removed on stop. Identity is positional — only that
    coordinator's jail has the mount. One newline-delimited JSON request per connection
    (`AgentIpcProtocol`); malformed input gets an error response.
  - **`Runtime/SessionKeyCache.cs`** (PR3) — memory-only per-kind model-key cache (the daemon has no
    keystore; keys only arrive on `SpawnAgent`), so a coordinator-initiated worker of the same kind
    reuses the client-supplied key; also caches the per-kind CLI login-state files a client spawn
    restored (and a stop harvested), so an IPC-spawned worker boots signed in too; and the per-(repo,
    kind) **CLI settings** (`RememberCliSettings`/`TryGetCliSettings`), so an IPC-spawned worker inherits
    the repo's approved-command list instead of stalling on prompts. A blank repo handle forms no scope
    and is dropped rather than collapsed into a shared bucket (MG-6). Never persisted, never logged.
- **`Runtime/CoordinatorSpawnGate.cs`** (**MG-2**) — the pure admission decision in front of the
  coordinator's in-jail spawn shim: `Evaluate(activeManagedWorkers, maxActiveWorkers, admission)`
  returns a refusal reason or `null`. The cap is checked **before** admission, so a coordinator cannot
  fan out past `maxActiveWorkers` by racing the admission controller — the shim path previously had no
  approval, cap, admission or budget gate at all. **It now has a second caller:
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
  list can badge external-PR entries, plus `verification_in_flight` (via
  `MergeQueue.IsVerificationInFlight`) — the one fact no client can derive, since a restart mid-run
  leaves a persisted `Verifying` row with nothing executing — plus `has_live_sandbox` (`optional`, from
  the injected `AgentSessionStore` keyed on `(repoHandle, agentId)`): whether the entry still HAS a jail,
  which is what lets the rail offer Resume on a stranded row and withhold Verify instead of leaving an
  enabled button whose only behaviour is "has no live sandbox". `optional` because a proto3 `false`
  meaning "this daemon does not report liveness" would render every entry of an older daemon as stranded; `RunVerification`/`CanMerge`/`BeginMerge`/`ConfirmMerge` —
  resolves the per-repo `MergeQueue` via `IMergeQueueRegistry`, typed `NOT_FOUND` for an unknown
  handle; **P2-47 #7 adds `GetMergeDiff`** dispatching to the injected `IMergeBranchDiffService`,
  typed `NOT_FOUND` when the mirror/branch is missing; **P2-11 wiring:** `FlaggedItemsFor` projects the
  `FlaggedChangeGate`'s items (risk-hunk + out-of-approved-scope rows, addressed by
  `FlaggedChange.Id` = `kind|path|contentHash`) alongside the RT-D2 row — it read
  `ChangedTestCommandGate` alone, so a branch the daemon blocked reached the human with nothing to
  clear — and `AcknowledgeFlaggedChange` routes any non-RT-D2 item id to that gate's store. Both use
  `PeekStore`, never `StoreFor`: creating a store from a read/ack would fabricate a fully-acknowledged
  record and bypass the gate's default-DENY. **Entry lifecycle:** `DiscardEntry` refuses while this
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
  - **`Services/PlanApprovalGrpcService.cs`** (P2-14: `StreamPlans`/`ApprovePlan`/`RejectPlan` over the
    daemon `PlanApprovalService`; **`ApprovePlan` resolves the approver via `IApproverIdentityResolver`
    from the connection — the request has no identity field**, SA-1/F2) and
    **`Services/KillSwitchGrpcService.cs`** (`Engage`/`Resume` over the daemon `KillSwitch`).
  - **`Runtime/SessionStoreKillTarget.cs`** — the interim `IKillTarget` over `AgentSessionStore` (marks
    live sessions `Paused` on a kill; the real cooperative-yield→`docker pause` target swaps in behind
    the seam).
  - `DaemonHost.cs` registers one `IAgentEnvironment` (`Wsl2AgentEnvironment`) as a singleton, the P2-14
    governance singletons (`ConnectionRoleRegistry`, `TerminalLockRegistry`,
    `IApproverIdentityResolver`, `PlanApprovalService` over a restart-safe `JsonPlanApprovalStore`, the
    shared `KillSwitchGate`, `IKillTarget`, `KillSwitch`) + the `RoleInterceptor`, the P2-47
    `SandboxAgentLauncher` (real spawn chain) + `IMergeBranchDiffService` (merge-diff bridge)
    singletons, the PR3 CLI-agent singletons (shared `InstalledAdapterCatalog`, `SessionKeyCache`,
    `AgentCliBinder`, `CoordinatorIpcServer` rooted next to the test-isolated session token,
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
