# Mainguard `phase2` — Security & Correctness Findings

- **Branch:** `phase2`, HEAD `e9169a9`
- **Date:** 2026-07-23
- **Scope:** Static source review. Nothing was built or executed; items needing runtime confirmation are flagged.

## Notes on status

Much of the phase2 security wiring is unbuilt: the merge queue, coordinator role-tokens, the verification-command provenance resolver, the AI-gateway metering middleware, and the read-only daemon git proxy are constructed only in tests. Findings tagged `LATENT` describe code that is not yet wired into the running daemon.

Reachability tags: `AGENT` (reachable from a jailed/coordinator agent), `gRPC` (needs the daemon token; not reachable from the jail), `LOCAL` (needs local code-exec as the same user), `LATENT` (not reachable until the enclosing feature is wired).

Verified-correct: the daemon gRPC surface binds `127.0.0.1` only (`DaemonHost.cs:344`) and the agent network is `Internal=true` (`EgressProxyConfigurator.cs:88`), so the jail cannot reach gRPC.

---

## Group A — Live, agent-reachable

### MG-1 · Daemon-side git runs against the agent-writable bare mirror without config/hook neutralization
- **Severity:** Critical · **Reach:** `AGENT`
- **Locations:** `ContainerSpecBuilder.cs:129-141` (mount) · `GitServices.cs:935-957` (`RunGit`) · `WorktreeManager.cs:85,116,120,126,132,178` · `AgentGitCommand.cs:19-37` · related: `build/mainguardos/Dockerfile` (`usermod -aG docker mainguard`), `mainguardd.service` (`User=mainguard`)
- **Defect:** The bare mirror is bind-mounted into the jail read-write at its real VM path (`Source==Target==BareRepoPath`, `ReadOnly=false`), with no user-namespace remap (MG-17), so container uid 1000 equals the daemon's `mainguard` uid 1000 that owns the mirror. The jail can write `<bare>/hooks/*` or set `core.hooksPath`/`core.fsmonitor`/`filter.*.process` in `<bare>/config`. The daemon later runs git against that directory outside the jail (`worktree add/remove/prune`, `branch -D`, `status`), and `RunGit` sets only `GIT_TERMINAL_PROMPT=0` — no `-c core.hooksPath=/dev/null`, no `GIT_CONFIG_NOSYSTEM`, no fsmonitor/filter neutralization. A repo-local `config`/`hooks` cannot be disabled by env var, only by per-command `-c` or a read-only mount, neither of which is used.
  ```csharp
  // ContainerSpecBuilder.cs:137-139
  Source = request.BareRepoPath, Target = request.BareRepoPath, ReadOnly = false,
  // GitServices.cs:953 — the only env set
  psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
  ```
- **Fix:** Run every daemon-side git that touches agent-writable state with `-c core.hooksPath=/dev/null -c core.fsmonitor= -c protocol.allow=never GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null` (and neutralize `filter.*`), or mount the mirror read-only and mediate ref updates through a daemon API. Regression test: plant a `reference-transaction`/`fsmonitor` hook, assert the daemon never runs it.

### MG-2 · Coordinator spawn shim has no approval, cap, admission, or budget gate
- **Severity:** Critical · **Reach:** `AGENT` (coordinator role)
- **Locations:** `AgentSpawnService.cs:82-216` (`SpawnAsync`), `:256-303` (`HandleShimRequestAsync`) · `CoordinatorIpcServer.cs:102-190` (socket, `0666` at `:126-128`) · `PlanApprovalService.cs` (`PlanApproved` has no daemon subscriber) · `CoordinatorTools.SpawnWorker` (unwired path that holds the checks)
- **Defect:** The wired coordinator spawn path is the in-jail `mainguard-agent spawn` shim over a per-coordinator Unix socket bind-mounted into the coordinator jail. `HandleShimRequestAsync` handles `SpawnOp` by calling `SpawnAsync(...AgentRoles.Managed...)` directly. `SpawnAsync`'s only gate is `if (_killGate.IsFrozen)` — no plan-approval check, no `MaxActiveWorkers` cap, no admission (`CanSpawn`), no budget. Those checks exist only in `CoordinatorTools.SpawnWorker`, which is not the wired path. Socket identity is positional and the socket is `0666`.
  ```csharp
  // AgentSpawnService.cs:103 — the only gate
  if (_killGate.IsFrozen) { ...throw AgentSpawnRefusedException... }
  // AgentSpawnService.cs:284-286 — shim spawns Managed directly
  var agentId = await SpawnAsync(repoHandle, request.AgentKind,
      _keys.TryGet(request.AgentKind), AgentRoles.Managed, ct, _keys.TryGetExtraEnv());
  ```
- **Fix:** Route every spawn through one admission point that requires a matching approved-plan token (daemon subscribes to `PlanApproved`) and enforces `MaxActiveWorkers` + admission + budget server-side. Add a per-coordinator spawn quota; consider a per-connection token on the IPC handshake.

### MG-3 · Quarantine config (`denyNonFastForwards`/`denyDeletes`) does not cover direct writes to the RW mirror
- **Severity:** High · **Reach:** `AGENT`
- **Locations:** `RepoProvisioner.cs:117-118` (quarantine config) vs `ContainerSpecBuilder.cs:137-139` (RW mount)
- **Defect:** The merge-safety quarantine relies on `receive.denyNonFastForwards=true` + `receive.denyDeletes=true`, which only constrain the `git receive-pack` path. With read-write filesystem access to the bare repo (MG-1), `refs/heads/main`, `packed-refs`, or loose objects can be edited directly without invoking receive-pack.
- **Fix:** Do not give the jail direct write access to `refs`/`packed-refs`/`config`/`hooks`; mount read-only + mediate ref updates server-side (enforcing deny-non-ff/deny-delete + `agent/*`-only ref scope), or give each agent a namespace the daemon reconciles.

### MG-4 · BYOK provider key is written into an agent-readable secrets file; no gateway key confinement
- **Severity:** High · **Reach:** `AGENT`
- **Locations:** `SandboxAgentLauncher.cs:218-224` (`BuildSecrets`) · `ContainerSpecBuilder.cs:17-53` (`CredTmpfsSpec`) · `ModelProxyMiddleware.cs:262-285` (`BuildUpstreamRequest`)
- **Defect:** `BuildSecrets` writes the raw `modelApiKey` verbatim into `agentEnv[envVar]` (e.g. `ANTHROPIC_API_KEY`) → `/run/secrets/agent.env`. `CredTmpfsSpec` makes that file agent-uid-owned, mode `0400` — readable by the agent (only the OOB key in `oob.key` is supervisor-owned). The intended confinement (a gateway injects the key so the agent holds only a Mainguard token) is not implemented: `BuildUpstreamRequest` passes inbound headers through with no key substitution, and no Mainguard session-token concept exists.
  ```csharp
  // SandboxAgentLauncher.cs:220-224
  var envVar = adapter is null ? "ANTHROPIC_API_KEY" : adapter.ApiKeyEnvVar;
  if (envVar is { Length: > 0 }) agentEnv[envVar] = modelApiKey;
  ```
- **Fix:** If confinement is required, hold the key in the (unwired) gateway keyed to an identity the agent can't read, injecting it only at the network hop; otherwise document that BYOK keys are exposed to the agent and drop the "session token only" claim. Chains with MG-7/MG-13.

### MG-5 · OSC 52 clipboard-copy from PTY output is dispatched to the host unconditionally, including on locked sessions
- **Severity:** High · **Reach:** `AGENT` (terminal output)
- **Locations:** `TerminalModeTracker.cs:227-264` (`CompleteOsc`) · `BoundTerminalSession.cs:314-341` (`FeedGridLocked`) · `TerminalGridControl.cs:310-324` (`SetHostClipboardAsync`) · also `TerminalControl.cs`/`VtScreen.cs`
- **Defect:** An `ESC]52;c;<base64>BEL` from PTY output decodes (capped 100 000 chars) and raises `ClipboardCopyRequested`; `FeedGridLocked` dispatches it to the client unconditionally (`_pendingClipboard` fan-out), and the client calls `Clipboard.SetTextAsync(text)` with no focus/consent/rate check. This is the output pipeline, so it is not gated by the terminal input-lock and fires on locked, view-only sessions.
  ```csharp
  // BoundTerminalSession.cs:332-339 — fires regardless of locked status
  foreach (var text in _pendingClipboard)
      PublishGridLocked(new TerminalOutput { Clipboard = new ClipboardCopy { Text = text } });
  ```
- **Not a finding:** clipboard read/paste is defended — OSC 52 `?` query payloads are dropped (`CompleteOsc:247-250`) and paste happens only from a local paste-chord keystroke.
- **Fix:** Gate host-clipboard writes on pane focus/active-tab; consider a visible confirmation; decide explicitly whether OSC 52 copy-out is honored on input-locked sessions.

### MG-6 · `SessionKeyCache` is keyed only by agent kind, shared across sessions/repos
- **Severity:** High · **Reach:** `AGENT` (coordinator)
- **Locations:** `SessionKeyCache.cs:14-65` · `AgentSpawnService.cs:285` (`_keys.TryGet(request.AgentKind)`), `:242` (`RememberCliCredentials`)
- **Defect:** `SessionKeyCache` is keyed only by `agentKind`, shared across every session for the daemon's process lifetime, not scoped by session/repo. A coordinator shim spawn pulls whatever model key / harvested CLI OAuth files were last cached for that kind — potentially cached by a different repo/session. Compounds MG-2.
- **Fix:** Scope the key + credential cache per coordinator/session, not per bare kind.

### MG-7 · Agent container has no `HostConfig.Dns`; resolver is Docker's embedded one, not the NXDOMAIN-pinned dnsmasq
- **Severity:** Medium · **Reach:** `AGENT` · **Runtime-forward behavior not executed**
- **Locations:** `ContainerSpecBuilder.cs:190-235` (HostConfig — no `Dns`) · `EgressProxyConfigurator.cs:222` (dnsmasq pushed to the proxy only) · `EgressProxyConfig.cs:53-65`
- **Defect:** The agent container is created with no `HostConfig.Dns` (grep-confirmed: zero assignments repo-wide), so its resolver is Docker's embedded `127.0.0.11`, not the NXDOMAIN-pinned dnsmasq (rendered into the proxy container only). Whether Docker's embedded resolver forwards external names on an `Internal=true` network is version-dependent and was not executed here; the wiring gap is certain. The egress test that claims DNS is blocked queries a non-resolvable `.attacker.tld` (NXDOMAINs on any resolver), so it passes even with zero pinning.
- **Fix:** Set `HostConfig.Dns = [proxy dnsmasq IP]` (or a container-local dnsmasq); change the test to query a resolvable name and assert the query never leaves.

### MG-8 · Kill switch's wired target only relabels session state; no `docker pause`/`kill`/signal/input-pause
- **Severity:** High · **Reach:** `AGENT`-relevant
- **Locations:** `SessionStoreKillTarget.cs:28-42` · wired at `DaemonHost.cs:97` · `KillSwitch.cs:283-309`
- **Defect:** The wired `IKillTarget` is `SessionStoreKillTarget`: `RequestYieldAsync` always returns `false`, `PauseAsync` is `_store.MarkState(agentId,"Paused")` — a state-string mutation. There is no `docker pause`, `docker kill`, signal, or terminal-input pause; its own doc calls it "interim." A running worker keeps executing after the kill switch engages, and its terminal stays typeable.
- **Verified-correct:** the freeze half is sound — `EngageAsync` calls `_gate.Freeze()` synchronously before any await (`:222`) and merge/spawn RPCs consult the gate; the 30 s stop ceiling is a compile-time constant, RTT-independent and overflow-guarded (`KillSwitch.cs:18-58`), and `EngageKillRequest` has no client grace field.
- **Fix:** Wire the real `docker pause`-backed target (`YieldProtocol`/`ISandboxEngine.PauseAsync`) and have `KillSwitch` also pause terminal input; assert a killed agent's container is actually `Paused` per Docker.

### MG-9 · CLI/daemon update flow computes its pin hash from the fetched bytes; unpinned transitive deps; unverified downgrade-capable self-update
- **Severity:** High · **Reach:** `LOCAL`/user-action
- **Locations:** `AgentCliUpdateService.cs:120-179` (`ApplyUpdateAsync`/`EnsureLatestAsync`), `:244-275` · `AdapterChannel.cs:298-316` · `Bootstrap/DaemonUpdater.cs:38-56,206-260`
- **Defect (three linked):**
  1. `EnsureLatestAsync` (the default flow) queries `registry.npmjs.org/<pkg>/latest`, downloads the tarball, and computes the pin hash from the bytes it just fetched — the hash and the artifact come from the same fetch. The shipped pin is only an offline fallback. (There is an https + `registry.npmjs.org` host check, but no authenticity check against an independently-known hash.)
  2. Install is `npm install -g --prefix … <tarball>`, which resolves the full dependency tree live from the registry with scripts enabled, even in the strict-pin path (per the code's own comment).
  3. `DaemonUpdater.RefreshAsync` copies the payload, `chmod +x`, retires/promotes, restarts, with no hash/signature check; `IsRefreshNeeded` fires on any SemVer inequality (downgrade-capable), with no monotonic guard.
  ```csharp
  // AgentCliUpdateService.cs:128
  var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
  ```
- **Fix:** Verify against an independently-known-good hash / npm provenance attestation (or a Mainguard-signed hash allowlist); bundle deps + install `--ignore-scripts` with a verified lockfile; sign the daemon payload and gate `RefreshAsync` on it + a monotonic-version check.

---

## Group B — Guarantees inert because the wiring isn't built

### MG-10 · Merge queue is never instantiated outside tests
- **Severity:** High (systemic) · **Reach:** `LATENT`
- **Locations:** `GatewayServiceRegistration.cs:72` (empty registry) · `MergeQueueRegistry.Register` (no prod caller)
- **Defect:** `new MergeQueue(...)`, `new MergeQueueContext(...)`, and `registry.Register(...)` occur only in `*.Tests` (grep-verified). The gateway registers an empty `IMergeQueueRegistry`; nothing calls `.Register(...)`, so it stays empty for the daemon's lifetime. (`resolveRepoPath: _ => null`/`resolveTarget: _ => null` are on the boot `MergeReconcileTask` at `:120` and PR-intake at `:185`.) `MergeQueueGrpcService.Resolve` therefore throws `NOT_FOUND` for every handle; the client's queue pump swallows it and retries against an empty projection. The merge guarantees are neither enforced nor bypassable; future wiring inherits MG-3/MG-11 unless fixed first.
- **Fix:** Wire per-repo `MergeQueue` construction + `registry.Register` into the swarm-up lifecycle with the real gate set + verification runner; integration-test that a provisioned repo yields a non-`NOT_FOUND` `StreamQueue`.

### MG-11 · Merge confirm path enforces no gate server-side; provenance resolver has no prod caller; no freshness re-check
- **Severity:** High · **Reach:** `LATENT`/`gRPC`
- **Locations:** `MergeQueueGrpcService.cs:105-116` (`ConfirmMerge`) · `MergeQueue.cs:368-383` (`ConfirmHumanMerge`), `:310-340` · `ForegroundMergeService.cs:100-150` · `VerificationRunner.cs:121-155` (`VerificationCommandResolver.Resolve` — no prod caller) · `ControlCenterViewModel.cs:880`
- **Defect (three linked):**
  1. `ConfirmMerge` calls `ctx.Leases.Confirm(...)` then `ctx.Queue.ConfirmHumanMerge(...)` — no `CanMerge`, no staleness check, no flagged-change/changed-test-command gate, and it does not require a held lease (`Leases.Confirm` is a no-op if absent). `ConfirmHumanMerge` only checks state legality (`Verified/AwaitingReview → Merged`). `FlaggedChangeGate`/`ChangedTestCommandGate` are evaluated only in the client cockpit.
  2. `VerificationCommandResolver.Resolve` (computes `ChangedVsMain`/config hash) has no production caller; the cockpit builds `ReviewCockpitContext` 4-arg, leaving `ChangedTestCommand=false`, so a rewritten test command, an added dependency, or an out-of-plan edit produces no flag in the running app.
  3. `ConfirmHumanMerge` from `Verified` succeeds regardless of whether `record.MainSha == _currentMainSha`; the stale cascade is fire-and-forget (`Task.Run`), so a branch can be confirmed while still `Verified@old`.
- **Verified-correct:** guarantee (a) — pass/fail is the container's real exit code read via the Docker API outside the container (`DockerSandboxEngine.cs:119-132`), no stdout marker trusted.
- **Fix:** Make `ConfirmMerge`/`ForegroundMergeService` call `queue.CanMerge(agentId, out reason)` (gates included) and require a valid held lease + `record.MainSha == current` under lock before any `Merged` transition. Resolve + persist test-command provenance daemon-side at verification time.

### MG-12 · Role authorization is dead code and fails open to Operator
- **Severity:** High · **Reach:** `LATENT`
- **Locations:** `ConnectionRoleRegistry.cs:29-49` · `RoleInterceptor.cs:90-103` · `BearerTokenInterceptor.cs:23-27` · `RoleInterceptorTests.cs:24-42`
- **Defect:** (1) `IssueCoordinatorToken()` has zero production callers (grep-verified), so `_coordinatorTokens` is always empty and `Resolve()` returns `Operator` for every token. (2) `BearerTokenInterceptor` runs before `RoleInterceptor` and accepts only the operator token, so a coordinator token could never authenticate. (3) `RoleInterceptorTests` proves coordinator denial with a token rejected by `BearerTokenInterceptor` ("Invalid bearer token"), so the role gate is never exercised — the test passes for the wrong reason. Not a live bypass today (the agent reaches the daemon only via the spawn/list-only IPC socket; the operator token is never mounted into a jail).
- **Fix:** Wire `IssueCoordinatorToken` into the coordinator spawn path and teach `BearerTokenInterceptor` to accept role-scoped tokens (or delete the role machinery); make `Resolve` fail-closed (unknown → least privilege); rewrite the test so the coordinator token authenticates and is denied by the role layer.

### MG-13 · AI gateway (budgets/rate-limit/backoff) is unwired from the live model path
- **Severity:** High · **Reach:** `LATENT`
- **Locations:** `DaemonHost.cs:344` (single loopback listener, no middleware) · `EgressProxyConfigurator.cs:66` (`gatewayUpstream` defaults null) + `Wsl2AgentEnvironment.cs:60-66` (constructs it without passing one) · `EgressProxyConfigurator.cs:216-219` · `images/mainguard-egress-proxy/reload.sh:18-34` (never sources the upstreams file) · `ModelProxyMiddleware` (needs `IAgentPortMap`, no implementation)
- **Defect:** No `app.Use`/`UseMiddleware` anywhere (grep: zero hits). The production `Wsl2AgentEnvironment` constructs the egress proxy without `gatewayUpstream`, so `PushConfigAsync` never emits the tinyproxy upstream-chaining file; `reload.sh` wouldn't include it anyway. `IAgentPortMap` has zero implementations; `gatewayUpstream:` appears nowhere including tests. All model traffic goes agent → tinyproxy → provider directly, never touching `AiGateway`/`TokenBucket`/`BudgetLedger`. Caps set via `SetBudgets` have no runtime effect; `Report429`/backoff/PTY-pause never fire.
- **Fix:** Wire `ModelProxyMiddleware` into a real listener path (or implement tinyproxy-CONNECT chaining), supply a real `IAgentPortMap`, pass a non-null `gatewayUpstream`, and add an end-to-end test asserting a live call is throttled/budget-capped.

---

## Group C — Local-trust / operator-side / latent-until-wired

### MG-14 · `DaemonUpdater` swaps the privileged daemon binary with no integrity check; allows downgrade
- **Severity:** Medium · **Reach:** `LOCAL`
- **Location:** `DaemonUpdater.cs:38-56` (`IsRefreshNeeded`), `:206-260` (`RefreshAsync`)
- **Defect:** See MG-9(3). No hash/signature verification before promote; `IsRefreshNeeded` fires on any SemVer inequality, so an older build reinstall downgrades the daemon.
- **Fix:** Verify signature/hash before promote; add a monotonic-version guard or operator confirmation before moving backward.

### MG-15 · Auto-elevated execution from a user-writable install dir with no binary-integrity gate
- **Severity:** Medium (High when shipped) · **Reach:** `LOCAL`
- **Locations:** `InstallerCommands.cs:64-73` (`/RL HIGHEST /SC ONLOGON`) · `installer/resume/register-resume-task.ps1:30-37` · `ResumeTaskGuard.cs:79-89` (path-only compare) · `ElevationLauncher.cs:104-116` (`runas`, no signature check) · `installer/Mainguard.Installer.Elevated/app.manifest` (`requireAdministrator`, signing optional) · install: per-user Velopack under `%LocalAppData%\Mainguard`
- **Defect:** Both elevated entry points target binaries in a user-writable per-user install dir with no Authenticode/integrity verification: (1) the ONLOGON `RL HIGHEST` resume task runs the app exe with a full admin token and no UAC prompt, and `ResumeTaskGuard.Sweep` compares only the `<Command>` path, not a signature; (2) `RunAsElevationLauncher` resolves the helper from `AppContext.BaseDirectory` and `runas`-launches it unverified. Setup requires local admin, so the Highest token is a full admin token. Signing is optional (`SignMainguardExecutables` only if a cert is configured).
- **Caveat:** Velopack packaging/self-update is deferred on this branch (`build/velopack/README.md`), so this affects the shipped product, not the current dev tree. `ResumeTaskGuard` self-deletes on fire / on non-`RebootPending` startup, limiting the durable window to a first-install-needing-reboot.
- **Fix:** Verify the resume-target's/helper's Authenticode signature/publisher before it fires and before `runas`; harden install-dir ACLs (or place elevated targets in a non-user-writable location); make signing non-optional for the elevated helper.

### MG-16 · Plan-approver identity is the daemon's own euid, not the peer's credential
- **Severity:** Medium · **Reach:** `LOCAL` (audit integrity)
- **Location:** `ApproverIdentityResolver.cs:32-52`
- **Defect:** `Resolve` returns `$"uid:{geteuid()}"` — the daemon's own euid, constant for every caller (loopback TCP, no `SO_PEERCRED`), despite the docstring claiming `SO_PEERCRED`. Client forgery is prevented (`ApprovePlanRequest` has no identity field), but the recorded approver carries no real attribution.
- **Fix:** Derive a genuine peer credential (Unix socket + `SO_PEERCRED`, or a human-presence factor); fix the docstring; test the real resolver.

### MG-17 · "User-namespaced" is claimed but no userns remap is enabled
- **Severity:** Medium (load-bearing for MG-1) · **Reach:** —
- **Locations:** `FirstBootStep.cs:233-234` (`daemon.json` = `bip` + `default-address-pools` only) · `Wsl2AgentEnvironment.cs:68` / `DockerSandboxEngine.cs:15` (`UsernsMode` defaults `""`) · `ContainerSpecBuilder.cs:200`
- **Defect:** The daemon config sets no `userns-remap` and `SandboxEngineOptions.UsernsMode` defaults to `""`, so container uid 1000 == host `mainguard` uid 1000. The jail's writes to bind mounts land as a privileged VM uid.
- **Fix:** Enable `userns-remap` and chown mount sources to the remapped range, or drop the "user-namespaced" claim. (The primary fix for MG-1 is hardening daemon-side git.)

### MG-18 · Egress network reuse isn't re-verified as internal; iptables backstop is in the wrong namespace
- **Severity:** Medium · **Reach:** —
- **Locations:** `EgressProxyConfigurator.cs:174-192` (`EnsureNetworkAsync`) · `EgressProxyConfig.cs:72-86` (`RenderIptablesScript`)
- **Defect:** (1) On every spawn, `EnsureNetworkAsync` looks up `mainguard-agents` by name and returns its ID without checking `match.Internal == true`, so a pre-existing or drifted non-internal network with that name silently disables egress enforcement, undetected at runtime. (2) The iptables backstop is a `FORWARD`-chain policy applied in the proxy container's netns; agent egress originates in the agent's netns and is dropped host-side by the internal topology, so it never traverses the proxy's `FORWARD` chain. The `--dport 53`/`--dport 8888` ACCEPTs are to any destination. Egress reduces to the single `Internal=true` control.
- **Fix:** On reuse, assert `existing.Internal` (and label) or recreate/throw; enforce the DROP host-side on the internal bridge and constrain the 53/8888 ACCEPTs to the proxy's address; regression-test a pre-created non-internal same-named network.

### MG-19 · Loopback + h2c + bearer token is the whole trust boundary; WSL localhostForwarding exposure
- **Severity:** Medium · **Reach:** `LOCAL`
- **Locations:** `DaemonHost.cs:337-346` · `DaemonOptions.cs:5-9` · `SessionTokenFile.cs`
- **Defect:** The daemon binds `127.0.0.1` but serves cleartext HTTP/2 (h2c), no TLS, no peer authentication — the bearer token is the sole gate. Under WSL2 `localhostForwarding` (default), in-VM `127.0.0.1:5250` is reachable from any process in the Windows user's session; in default NAT WSL2, other distros share the stack. A same-user process can also read the token file. WSL cross-process/cross-distro reachability is reasoned from WSL2 networking, not measured.
- **Verified-correct:** 256-bit `RandomNumberGenerator` token, constant-time `FixedTimeEquals`, `0600`/DACL token file, never logged, never mounted into a jail, stateless reconnect with a fresh token on restart.
- **Fix:** Consider mTLS / a pinned self-signed cert, or a `0600` Unix-domain socket + `SO_PEERCRED`; document the localhostForwarding exposure.

### MG-20 · Budget/rate identity comes from an unauthenticated `x-mainguard-agent` header
- **Severity:** Medium · **Reach:** `LATENT` (behind MG-13)
- **Location:** `ModelProxyMiddleware.cs:233-235`
- **Defect:** `agentId = _portMap.AgentForPort(port) ?? Headers["x-mainguard-agent"].FirstOrDefault()`. `IAgentPortMap` has no implementation and the daemon opens one port, so the client-supplied header is the de-facto identity — no authentication, no cross-check against session identity. A spoofed header can evade the sender's own budget or attribute spend/429s to another agent (`_supervisor.PauseInput(victim)`).
- **Fix:** Derive `agentId` only from an authenticated source (bearer/session identity or a daemon-assigned id never echoed to the agent); test that a spoofed header is ignored.

### MG-21 · Daily budget caps reset to unlimited on daemon restart
- **Severity:** Medium · **Reach:** `LATENT` (behind MG-13)
- **Location:** `GatewayServiceRegistration.cs:76-78`
- **Defect:** Boot reads the persisted budget row, then constructs `new BudgetCaps(stored.TokenCap, stored.UsdMicrosCap, 0, 0)` — hardcoding the 3rd/4th args (`PerDayTokenCap`, `PerDayUsdMicrosCap`) to literal `0` (= unlimited) instead of `stored.TokenCapPerDay`/`stored.UsdMicrosCapPerDay`. Only per-agent lifetime caps survive restart. `SetBudgets` at runtime sets all four correctly; the bug is isolated to boot. `GetBudgets` still reports the persisted value.
- **Fix:** Use `stored.TokenCapPerDay, stored.UsdMicrosCapPerDay`; regression-test set-daily-cap → rebuild container → assert enforced.

### MG-22 · Unbounded `Resize` dimensions reach unchecked native libvterm allocation
- **Severity:** Medium · **Reach:** `gRPC`/`LATENT` · **Native behavior not executed**
- **Locations:** `VtermSession.cs:182-194` + ctor `:82-98` (validate only `<=0`) · `BoundTerminalSession.cs:185-206` · `TerminalGrpcService.cs:175-177,250-252` (Resize case not wrapped in `if(locked)`) · `terminal.proto:37-40` (`uint32 cols/rows`) · native libvterm 0.3.3 `vterm_set_size`/`alloc_buffer`
- **Defect:** A client `Resize.cols/rows` (`uint32`, cast to `int`) in `[1, 2^31-1]` passes every managed check (only `<=0` rejected) and flows to `vterm_set_size(rows, cols)`. Upstream libvterm allocates `sizeof(ScreenCell)*rows*cols` with no overflow/upper-bound check and dereferences the result unconditionally. An inflated `cols` also multiplies the 10 000-line scrollback ring's footprint. `Resize` is a gRPC input, not agent PTY output, so it is not agent-reachable today; it becomes live if a coordinator/thin-client gRPC path is wired.
- **Fix:** Clamp `cols`/`rows` to an administrative ceiling in `BoundTerminalSession.Resize` and `VtermSession.Resize`/ctor before native; treat out-of-range like the existing `<=0` (ignore).

### MG-23 · External-PR merge path bypasses the per-repo lease and the ff-only CAS
- **Severity:** Medium · **Reach:** `LATENT`/not-agent-reachable · **Stream-reported, not re-read this pass**
- **Location:** `MergeDispatch.cs:105-122`
- **Defect:** `MergeExternalAsync` calls `_prService.MergeAsync(...)` then `queue.ConfirmHumanMerge(...)` with no `TryBegin` lease, no `--ff-only` CAS, and no `CanMerge` check, so the one-outstanding-merge-per-repo serialization doesn't cover external entries. A foreground merge and an external-PR merge (or two external merges) on the same repo can run concurrently.
- **Fix:** Route external merges through the same lease + `CanMerge` gate; treat the host-API merge as the CAS and reconcile a lost race like an `--ff-only` failure.

### MG-24 · Budget check-then-act race lets concurrent requests overshoot the cap
- **Severity:** Medium · **Reach:** `LATENT` (behind MG-13)
- **Locations:** `AiGateway.cs:126-131` (`AcquireAsync`), `:205-210` (`Settle`) · `BudgetLedger.cs:124-183` (`IsExhausted`/`Record`)
- **Defect:** `AcquireAsync` checks `IsExhausted` once up front; the debit (`Record`) happens only after the upstream round-trip. Nothing reserves provisional spend, so N concurrent in-flight requests for the same agent all observe `IsExhausted==false`. Overshoot is bounded only by the shared 60 req/min token bucket.
- **Fix:** Reserve estimated spend at `AcquireAsync` (provisional debit reconciled in `Settle`); concurrency-test near the cap boundary.

---

## Group D — Robustness / hygiene

| # | Finding | Sev | Location | Fix |
|---|---|---|---|---|
| MG-25 | Egress-proxy container: `CapAdd NET_ADMIN,NET_RAW`, only `no-new-privileges`, no seccomp/read-only/limits | Low | `EgressProxyConfigurator.cs:162-164` | Drop `NET_RAW`; apply agent seccomp + `ReadonlyRootfs` + pids/mem limits |
| MG-26 | Agent jail sets only `Memory`+`PidsLimit`; no `NanoCPUs`/`CPUQuota`/ulimits | Low | `ContainerSpecBuilder.cs:202-203`; `SandboxLimits:11-14` | Add a CPU ceiling + nofile/nproc ulimits |
| MG-27 | Agent image referenced by floating `:latest`; integrity check compares a settable `mainguard.image.version` label, not a digest; base images / `curl\|sh` installers unpinned | Low-Med | `SandboxAgentLauncher.cs:100-113`; `SandboxImageVersions.cs:36` | Pin by `@sha256:` digest, verify at spawn; checksum toolchain installers |
| MG-28 | Wildcard allowlist entries render to an invalid tinyproxy regex — `host.Replace(".","\\.")` turns `*.example.com` into `^*\.example\.com$` (leading quantifier); policy view diverges from enforced filter | Low | `EgressProxyConfig.cs:23` | Translate `*.` to `^([a-z0-9-]+\.)*example\.com$`; unit-assert `Allows()` ≡ rendered filter |
| MG-29 | Boot merge-reconcile stale cascade is a hardcoded no-op — `foreach (var handle in Array.Empty<string>())` | Low | `GatewayServiceRegistration.cs:121-128` | Resolve the owning queue by `lease.RepoHash` and fire `NotifyMainMoved`; test a replayed merge re-stales a co-tenant branch |
| MG-30 | `GetScrollback` RPC has no ownership/role scoping (not in `CoordinatorDeniedMethods`); returns any agent's up-to-1000 scrollback rows | Low-Med | `TerminalGrpcService.cs:367-378` | Scope to the connection's authorized agent; add regression test (gRPC-only reach) |
| MG-31 | `RoleInterceptor.LockedInputReader` tracks only the `AgentId` oneof, never the P2-18 `Attach` oneof — the input-lock layer is a no-op for grid/Attach clients | Low-Med | `RoleInterceptor.cs:118-154` | Add an `Attach` branch mirroring `TerminalGrpcService.Attach`; add a test (service layer re-enforces the lock today) |
| MG-32 | Global `%UserProfile%\.wslconfig` read-modify-write is not concurrency-safe; `WriteWslConfig` uses `File.WriteAllText` (truncate-in-place), not atomic temp+replace | Low | `WslConfigMergeStep.cs`; `BootstrapFileSystem.cs:41` | Atomic temp+replace; timestamped backup already mitigates data loss |
| MG-33 | `kernel.yama.ptrace_scope=2` written to the shared WSL2 kernel affects every distro while the VM is up (non-namespaced); a hardening, not a weakening | Low | `FirstBootStep.cs:27,88-95` | Document/scope; resets on full WSL shutdown, persisted drop-in lives inside the removed distro |
| MG-34 | Uninstall stops the daemon with `pkill -f mainguardd` (matches any cmdline containing the string) vs the probe's `pgrep -x` | Low | `installer/Mainguard.Uninstall/Program.cs:68` | Prefer `systemctl stop mainguardd` or `pkill -x`; teardown-only |
| MG-35 | .NET/host deps not reproducibly pinned: no `packages.lock.json` / `Directory.Packages.props`, `global.json` `rollForward: latestFeature`. The MainguardOS rootfs build *is* deterministic (pinned base digest + `packages.pinned.txt` + deterministic repack, sha256-stamped, CI double-builds) — the gap is the .NET side only | Low | `global.json` (unpinned); `build/mainguardos/build.sh` (pinned) | Commit a NuGet lockfile (`--locked-mode`) + pin the SDK |
| MG-36 | Flat shared internal network — every jail attaches to one `mainguard-agents` segment, so agent A can reach agent B's container IP/ports; no east-west isolation | Low | `EgressProxyConfigurator.cs:24` | Per-agent networks or an intra-network isolation policy (egress-to-internet is contained; the daemon is unreachable) |
| MG-37 | `mainguard-agent list` (IPC) returns every session on the daemon, not just the caller coordinator's children | Low-Med | `AgentSpawnService.cs:294-298`; `AgentSessionStore.List` | Scope `list` to the caller's descendants |
| MG-38 | `ModelProxyMiddleware` forwards all inbound request headers and all upstream response headers unfiltered (strips only `transfer-encoding`) | Low | `ModelProxyMiddleware.cs:276-306` | Curate both directions against an allowlist when wired (`IsModelHost` uses exact match) |
| MG-39 | Latent kill-containment gaps once the real pause target is wired: (a) a worker can fake the `[IPC_UPDATE_READY]` cooperative-yield ACK to skip `docker pause`; (b) `KeepAliveRebaser` never checks `KillSwitchGate` and its `Resume()` calls `UnpauseAsync`, so a rebase cycle could unpause a killed jail | Low | `KillSwitch.cs:287-291`; `YieldProtocol.cs:121-145`; `KeepAliveRebaser.cs:108,172-176` | In the kill fan-out always `docker pause` regardless of the ACK; make the rebaser refuse to run / not resume while frozen |
| MG-40 | Misc lower-confidence hygiene: egress-allowlist audit actor `who` is client-supplied (`EgressGrpcService.cs:56`); Kestrel config-section env (`Kestrel__Endpoints__*`) could add a non-loopback endpoint (`DaemonHost.Build` is additive); the "every RPC authenticated" reflection test covers only 4/9 services (`DaemonAuthTests.cs`); `AdapterPinOverrideStore` validates on write but not read; `GitHostDetector.Detect` uses substring `Contains("dev.azure.com")`; `AdapterManifest.IsPinnedVersion` misclassifies `"1.x"` as pinned; `FlaggedChangeGate.Allows` fails open when an agent has no ack store; log masking is gRPC-interceptor-only (no sink-level scrub) | Low | see cells | derive audit actor daemon-side; clear config-driven endpoints + assert single loopback; drive the reflection set from the mapped services; validate pin-overrides on read; exact/suffix host match; fix `IsPinnedVersion`; default-deny the flagged gate; add a sink-level secret scrubber |

---

## Verified sound (no finding)

- **Sandbox controls applied & re-asserted:** seccomp default-deny (`SCMP_ACT_ERRNO`) with `ptrace`/`process_vm_readv`/`process_vm_writev` removed; `no-new-privileges`; `CapDrop ALL` + minimal caps (no `SYS_PTRACE`/`SYS_ADMIN`/`NET_ADMIN`); `ReadonlyRootfs=true` with tmpfs-only writable surfaces; non-root `User=1000`; `Privileged=false`; docker.sock never mounted into a jail; secrets delivered via stdin exec (never Env/argv/disk), builder rejects secret-shaped env vars.
- **Daemon reachability:** gRPC binds loopback only, unreachable from the jail (internal network, nothing published).
- **Auth primitives:** 256-bit token, constant-time compare, `0600`/DACL token file, never logged/mounted, stateless reconnect with a fresh token on restart.
- **Verification verdict (guarantee a):** pass/fail is the container's real exit code read via the Docker API outside the container — no stdout marker trusted.
- **Kill switch:** freeze-first ordering is correct and synchronous; the 30 s ceiling is RTT-independent and worker-un-stretchable.
- **OS integration:** no command/argument injection into `wsl.exe` or elevated processes (`ProcessStartInfo.ArgumentList` throughout; the one interpolated `bash -c` sanitizes to `[A-Za-z0-9._-]`); uninstaller scoped (`--unregister MainguardEnv`, deletes only `%LocalAppData%\Mainguard`); VM upgrade downgrade-protected, never deletes the data VHDX on failure; `OobeInstanceLock` crash-safe; host Docker Desktop / global config not clobbered (dedicated 10.202/10.203 subnet; `.wslconfig` merged not overwritten).
- **Egress:** default-deny is topology-enforced (`Internal=true`); exact-match host filter; git host blocked; daemon git proxy refuses everything but `git-upload-pack`.
- **Native terminal interop:** `VtermSession` reentrancy guard + delegate-rooting + dispose order (frees `_vt` before `_callbacksMem`); bounded replay ring (512 KB), bounded per-subscriber channels, bounded streamer holdback; P/Invoke `rows,cols` order consistent; OSC 52 paste/read blocked.
- **Orchestration:** PR intake cannot skip the pipeline (external entries face the same `CanMergeLocked` checks; `Merged` only via freeze-gated, coordinator-denied `ConfirmMerge`); `SessionLeader` is a single in-proc owner; `SwarmReconciler` adopt-policy is consistent with agents surviving a daemon restart.

## Not runtime-verified

- MG-7 embedded-resolver forwarding on an internal network is version-dependent, not executed.
- MG-19 WSL `localhostForwarding` cross-process/cross-distro reachability is reasoned, not measured.
- MG-1/MG-22 native/exec behaviors (hook execution, libvterm NULL-deref) are source-confirmed, not executed.
- Stream-reported, not re-read this pass: MG-23, MG-27 (label-compare), MG-39, MG-40 cluster.
- Not statically verifiable: exact `size_t` wraparound in libvterm, live tinyproxy regex-load behavior, `Porta.Pty` internal ConPTY/forkpty handle lifetime.
