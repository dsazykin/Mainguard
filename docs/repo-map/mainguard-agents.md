<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
# `Mainguard.Agents/` — agent platform

Built ON `Mainguard.Git`. Orchestration, sandbox/container control (`Docker.DotNet`), terminal/PTY, adapters, daemon-side seams.

- **`Daemon/DaemonLogSubsystems.cs`** — the 12 canonical daemon-log **subsystem names**
  (`lifecycle · migration · rpc · spawn · egress · gateway · terminal · merge · approval · killswitch · coordinator · intake`)
  + `LogsDirectory()` = `DataRoot()/logs`. Lives in Core so BOTH the App's read surface and the
  daemon's category derivation (`DaemonLogCategories`) share ONE list — a new subsystem adds a name
  here + a `DaemonLogCategories` constant (the P2-46/P2-49 extension point). It is an explicit name
  list, not ambient logging (Core stays log-free).
  - **`Agents/Bootstrap/DaemonLogReader.cs`** — the read-only tail of the in-VM daemon logs behind the
    App's "Daemon logs" panel, over the same `IWslRunner` seam `WslDaemonHealthProbe` uses:
    `ReadRecentAsync` (`journalctl -u mainguardd`) + `ReadSubsystemAsync`
    (`tail /home/mainguard/.mainguard/logs/<subsystem>.log`); never throws (empty string on
    non-zero/failure).
- **`Terminal/`** — the engine-agnostic terminal seam (P2-03).
  - `ITerminalView.cs` (the interface the ViewModel talks to —
    `FeedOutput`/`InputAvailable`/`Resize`/opaque `GetStateSnapshot`/`RestoreState`; never leaks a
    renderer type so P2-18's libvterm engine swaps in with no ViewModel change).
  - `VtBoundaryDetector.cs` (pure `SafeFlushLength`: Ground/Esc/Csi/Osc/Dcs/Ss3 + UTF-8 continuation
    counting; returns the largest prefix that never splits a VT sequence or UTF-8 codepoint — the
    correctness heart, split-at-every-offset tested).
- **`Terminal/Vterm/`** — the **P2-18 server-side terminal engine** (daemon-only at runtime; the
  client never loads native terminal code).
  - `VtermNative.cs` (P/Invoke over the pinned libvterm 0.3.3 — `build/libvterm/`; resolver:
    `MAINGUARD_LIBVTERM` → beside-the-binary `libvterm.so` → system; layout validated: 40-byte cells,
    LSB-first attr bitfields, masked 4-byte colour unions, `0xFFFFFFFF` wide-spacer marker; the
    callbacks struct is POINTER-stored by libvterm so it lives pinned in unmanaged memory).
  - `VtermSession.cs` (one per agent PTY, single-threaded by contract with a reentrancy guard; screen
    callbacks accumulate an ordered change log — scrollback pushes WITH content → rect scrolls → damage
    rects — that `DrainDelta()` coalesces per 16 ms frame into a `VtermGridDelta`: a one-line scroll is
    a scroll op + the exposed row, never a full grid; 10k-line scrollback ring answers `sb_popline` so
    resize round-trips content; `Snapshot()`/`Resize` (sets `SnapshotPending` — the
    one-authoritative-grid-size rule), `GetScrollback` (absolute-indexed, thread-safe), `IsSupported`
    (native probe, never throws)).
  - `VtermModel.cs` (the engine-neutral currency: `VtermCell` — `HasContent` distinguishes written
    spaces from positioned gaps, the selection-copy contract's backbone — `VtermGrid`, `VtermGridDelta`,
    `VtermGridOp`, `VtermModes`; proto-free so this assembly stays proto-free).
  - `TerminalModeTracker.cs` (pure streaming DECSET/OSC scanner for what libvterm doesn't surface:
    bracketed paste 2004, DECCKM, SGR-mouse 1006, and OSC 52 clipboard SETs — queries are NEVER answered
    (the jail must not read the host clipboard); bounded capture, split-safe).
- **`Daemon/`** (P2-02) — client/server shared daemon seams (no server-assembly dependency from the
  client): `DaemonPaths.cs` (the single source for the per-user session-token file path + default
  loopback port), `ConnectionState.cs` (`Connected/Degraded/Down`, surfaced by `DaemonClient`,
  rendered by the P2-13 Activity Bar), `DaemonTokenLocator.cs` (resolves the session ACROSS the
  host/VM boundary — local `%LocalAppData%` file vs the in-VM file over `\\wsl.localhost`, freshest
  write wins; `TryResolveTokenPath`/`TryResolveSessionDirectory`/`ResolveSessionDirectory` are the
  **single selection point** so the token and the MG-19 certificates always come from the same
  daemon), and **`DaemonTransportCredentials.cs` (MG-19)** — the client half of the mTLS boundary:
  `DaemonTransportFiles` names the material (`daemon-server.cer`, `daemon-client.pfx`),
  `Load(directory)` reads it and **throws rather than downgrade** when it is absent,
  `IsPinnedServerCertificate` is the SHA-256 pin that makes a port squatter's handshake fail *before*
  any HTTP/2 frame carries the bearer token, and `BuildSslOptions()` is the one place a client's TLS
  posture is defined.
  - `LoadPkcs12` centralises the OS split (Windows Schannel cannot use an ephemeral key set for client
    auth; elsewhere the key stays memory-only).
- **`Agents/`** — the Phase-2 control-center **prototype seam** (Lane E Part 3; no daemon, no
  Docker, no real agents).
  - `OrchestrationModels.cs` (enums/records shaped like the future gRPC contract: `AgentLifecycleState`
    per OPS §4.1, `WorkerMergeState` per P2-10,
    `AgentInfo`/`QueueEntry`/`VerificationRecord`/`FlaggedItem`/`TaskPlan`/`ChatLine`/`AgentEvent`/`SandboxEvent`/`ResourceSample`/`Checkpoint`/`DeployStatus`),
    `IOrchestrationServices.cs` (the service interfaces the control-center ViewModels consume:
    `IAgentService`, `IMergeQueueService` (+ `VerificationOutcome`; **`RunVerificationAsync` is the
    UI-facing verification trigger** — the rung whose absence left the whole verification mechanism
    without a production caller, so queue entries sat at `not verified yet` forever. It is a *trigger*
    only: every decision stays in the daemon's `MergeQueue.RunVerificationAsync`, which an automatic
    phase-2 caller drives directly for identical gates/jail execution/transitions),
    (+ `ResumeEntryAsync`/`QueueEntryResumeOutcome` — the human's way out for a STRANDED entry: the daemon
    spawns a jail onto that entry's existing agent id and branch, so it can be verified and merged instead
    of only discarded. Adoption, not re-creation — same id, same branch, same row; see
    `docs/design/resume-stranded-queue-entry.md`. `QueueEntry.HasLiveSandbox` is the three-valued fact it
    keys off: `false` = the daemon says this entry has no jail, `null` = the projection could not say, and
    only `false` offers Resume or withholds Verify),
    `ICoordinatorService`, `IKillSwitchService`,
    `ITelemetryService` (**P2-47 #4** adds `GetSpendBudgetAsync`/`SetSpendBudgetAsync` over the Core
    `SpendBudget` DTO so the Resource Monitor displays + edits the per-day cap, round-tripping the whole
    cap record through the `SetBudgets` RPC), `IVibeService` — designed so a `DaemonClient` adapter can
    replace the mock with zero View/ViewModel changes; **P2-47** delivered that swap for the shipped
    app), `OrchestratorServices.cs` (**P2-47**: the bundle record grouping the six seams + an
    `IDisposable` `Owner`, so `ControlCenterViewModel` depends on the interfaces — not the concrete
    `MockOrchestrator` — which is what made the shipped mock→real swap possible; `FromSingle<T>` wraps
    one object that implements the whole set. The shipped app builds a `DaemonBackedOrchestrator` bundle
    via `App.CreateOrchestratorServices`; the design render harness explicitly injects a mock bundle via
    `App.OrchestratorServicesFactory`), `Mock/MockOrchestrator.cs` (the scripted in-memory stand-in: 4
    seeded agents, a live stale cascade on `ConfirmMergeAsync`, CanMerge gate reasons, plan approval
    that spawns a worker, freeze-first kill switch, telemetry random walk, scripted deploy phases;
    timer-threaded — consumers marshal). Also `PtyProcessShim.cs` (P2-03): the real cross-platform PTY
    shim — `PtySession` (bidirectional `IO` stream, `Resize`/`Kill`/`ExitCode`, idempotent dispose that
    reaps the child) + `PtyProcessShim.Spawn` (ConPTY on Windows / forkpty on Linux via the `Porta.Pty`
    package; execs `command`+`args` directly — never a shell wrapper) + the internal `PtyDuplexStream`
    adapter. `isatty()` is true inside; Ctrl+C (0x03) reaches the foreground process. **PR3:**
  - `ITerminalSession.cs` — the engine-agnostic live-terminal seam (`IO`/`Resize`/`Kill`/`ExitCode`)
    `PtySession` implements, so the daemon's CLI binding is testable with duplex-pipe fakes;
  - `AgentRoles.cs` — the shared role-string contract (""/`coordinator`/`managed`) for
    `SpawnAgentRequest.role`; and `Agents/Ipc/` — the coordinator→daemon spawn channel's pure pieces:
    `AgentIpcProtocol.cs` (fixed in-jail layout `AgentIpcPaths` — `/opt/mainguard/ipc` read-only mount
    with `daemon.sock` + the shim — plus the newline-delimited JSON request/response codec) and
    `AgentSpawnShim.cs` (the `mainguard-agent` python3 shim script the daemon writes into the
    coordinator's IPC dir; python3 is pre-baked jail toolchain, so nothing new is baked into the image —
    G-16).
  - **`Agents/` (P2-06 repo provisioner — daemon-side, no UI).**
    - `RepoPathHasher.cs` (pure: a normalized Windows repo path → a stable lowercase-hex SHA-256;
      case-folds + unifies slashes + strips the trailing separator so `C:\Repo\` and `c:/repo` map to one
      mirror; Unicode-stable).
    - `RepoProvisioner.cs` (`IRepoProvisioner` — `Provision` + the P2-47 `BareRepoPathFor(repoHash)` (the
      mirror path; the spawn path gates real-jail launch on its existence, the merge-diff bridge locates
      the agent branch through it) + `ProvisionResult(RepoHash, BareRepoPath, VmRemoteUrl)`: first
      provision `git clone --bare` → `<vmRoot>/repos/<hash>.git` with
      `core.untrackedCache`/`receive.denyNonFastForwards`/`receive.denyDeletes` set, and — **MG-3** —
      `MirrorMaintenance.ApplyGcPolicy` re-asserted on EVERY provision so an implicit `gc --auto` can
      never prune objects the per-agent repos are borrowing through their alternate; subsequent provisions
      incremental-fetch the default branch by **explicit source URL** — worktrees share the bare's config
      and repurpose `origin`, so a configured source remote can't be relied on; the fetched branch is
      resolved from the **source's** HEAD (not the mirror's, which goes stale on a `master→main` rename
      and would fetch a ref the source no longer has — the 2026-07-22 "couldn't find remote ref master"
      copy failure); re-clones cleanly if the bare dir was deleted; `vmRoot` + a `vmRemoteUrl` resolver
      are injected).
    - `AgentRepo.cs` (**MG-3** — `AgentRepoLayout` (pure: `<vmRoot>/agents/<hash>/<agentId>.git`,
      `agent/<id>`, `refs/heads/agent/<id>`, and the ONE agent-id gate — `[A-Za-z0-9._-]`, no leading
      `.`/`-`, no `..`, no `.lock` suffix, ≤64 chars — because the id names both a directory under
      `agents/` and a ref component, so one check closes path traversal and ref injection together) +
      `AgentRepoManager` (`Create` = `git clone --bare --shared <mirror>` — `--shared` writes a one-line
      `objects/info/alternates` and copies **no** objects, measured ~26 KB per agent on a seed repo — then
      drops the cloned `origin`, sets `core.sharedRepository=group` + `gc.auto=0`, prunes other agents'
      `agent/*` refs (`DropForeignAgentRefs` — the `update-ref -d` exit code is CHECKED and a failure
      throws, because a surviving `refs/heads/agent/<other>` is a branch this agent can check out with
      another agent's work in it: the MG-3 isolation boundary, and the spawn is refused rather than
      completed with it unheld), and group-shares the tree; plus
      `Remove`/`Exists`/`ListAgentIds`/`AnyAttached`, the last being the “is a borrower still
      attached?” question the prune policy turns on)).
    - `AgentBranchGuard.cs` (**agent branch confinement** — the two non-boundary layers that keep an
      agent's work where the merge queue can see it, after live phase-1 testing found an agent could
      `git checkout -b …`, commit, and have the work become *invisible* with no warning, error or queue
      entry. `AgentBranchGuard.InstallHook` writes a `reference-transaction` hook into the agent's own
      repo refusing writes to any `refs/heads/` other than `agent/<id>` — **ERGONOMICS, NOT SECURITY**:
      the agent can delete it or run `git -c core.hooksPath=/dev/null`, all measured, so it is a guard
      rail and never a reason to relax the daemon-side rule in `AgentRefMediator`. Four measured
      exemptions keep ordinary git working — decide only in `prepared`, scope to `refs/heads/` (so
      stash/tag/fetch and the `ORIG_HEAD`/`AUTO_MERGE`/`REBASE_HEAD` pseudo-refs pass, and HEAD moves
      stay legal or rebase would break), allow deletions, and allow no-op rewrites so `git pack-refs`/
      `git gc` still work in a jail that is ALREADY stranded. The daemon needs no exemption: its git runs
      `-c core.hooksPath=/dev/null` (MG-1). `AgentBranchGuard.Probe` + `AgentBranchAlignment` are the
      backstop — measured `symbolic-ref`/`rev-parse`/`merge-base --is-ancestor`, never a guess, with
      `Unknown` deliberately distinct from "aligned"; `Describe` is the operator report naming the actual
      branch, the expected branch and a recovery that was actually computed. Auto-recovery is
      **rejected** — see `docs/design/agent-branch-confinement.md` §4).
    - `AgentRefMediator.cs` (**MG-3 stage 2** — the ONE path by which anything an agent produced reaches
      the shared mirror, and the reason the design chose daemon-FETCH over push-to-daemon: with a push
      model the agent proposes `old new refname` triples the daemon must validate forever and correctly,
      whereas here it cannot name a ref at all. The four rules are nevertheless enforced in code —
      destination is `refs/heads/agent/<thatAgentId>`, fast-forward only (`merge-base --is-ancestor`),
      never a delete (an absent/unfetchable source leaves the mirror's ref exactly where it was), and
      never the repo's integration branch (checked against the mirror's OWN HEAD, not the literal `main`).
      The fetch lands in `refs/mainguard/incoming/<id>` FIRST and the decision is taken afterwards,
      because fetching straight into the destination would make git's refspec semantics the policy — one
      `+` character would silently turn the fast-forward rule off — and the final move is an
      `update-ref <target> <new> <old>` compare-and-swap against the value the decision was taken on.
      Returns an `AgentRefPublishResult`/`AgentRefPublishOutcome` and NEVER throws: every caller is on a
      path that must not be taken down by housekeeping. Also `AgentRefWatcher` — the other half of the
      resolved §7 trigger ("both"): ONE sweep loop over every watched agent, snapshotting the loose ref
      bytes + `packed-refs` size/mtime so an idle agent costs two `stat`s rather than a `git` process, and
      recording the snapshot ONLY on a successful publish so a refused tick keeps retrying instead of
      silently giving up. `PollOnce()` is public so the behaviour is testable without sleeping on a
      background loop — but `Watch` STARTS that loop, so a caller that hand-cranks `PollOnce` is racing
      the sweep for the same snapshot delta and must pass the `DriveManually` interval, which runs no loop
      at all; `Publish` is likewise serialized per agent, because §7's "both" trigger means a watcher tick
      and a pre-verification publish overlap by design and the loser used to report
      `Failed`/`NothingToPublish` for a mirror that was in fact current).
    - `MirrorMaintenance.cs` (**MG-3 §4** — the object-lifetime policy for a mirror other repos borrow
      from: **pruning breaks borrowers, repacking does not**. `ApplyGcPolicy` (`gc.auto=0` +
      `maintenance.auto=false` — BOTH checked: the second exists because a newer git's background
      maintenance is a second door, so a `maintenance.auto` that silently failed to apply would let it
      prune objects alternates borrowers need, breaking the agent repos later with missing objects and
      nothing pointing back at the cause), `RepackWithoutPrune` (`git repack -A -d` — `-A` is load-bearing: measured
      on git 2.53, `-a -d` DELETES an unreachable object that lives in a pack while `-A -d` loosens it,
      and deletion is the one thing an alternates borrower cannot survive), `PruneWhenIdle`
      (repack-then-`git prune`, refused outright while any agent is attached — `git prune` only ever
      removes LOOSE objects, so the repack first is what actually reclaims the tail),
      `CountLooseObjects`/`MeasureObjectStoreBytes` + the `SizeWarningBytes` guard, and
      `AfterAgentDetached` (the teardown hook: a fast path below the loose-object threshold so a stop
      never scales with repository size)).
    - `WorktreeManager.cs` (`IAgentWorktreeManager`: `CreateAgentWorktree` creates the agent's OWN
      repository (**MG-3**) then `git worktree add -b agent/<id> <path> <defaultBranch>` **off that**,
      then the **quarantine remote** — remove any inherited `origin`,
      `git remote add origin <agentRepoPath>` so the worktree's remotes are exactly
      `{origin → the agent's own repo}`: `git push origin` succeeds entirely inside the agent's writable
      space (LLM CLIs push reflexively) and the shared mirror is not a remote it can name at all — plus
      the non-fatal `pnpm install` hook (fires only when `pnpm-lock.yaml` is present, via an injectable
      runner delegate + warning sink); `AgentRepoPathFor` (the read-write mount the jail gets) and
      `PublishAgentBranch` (the daemon-side carry of `refs/heads/agent/<id>` from the agent's repo into
      the mirror — the daemon names BOTH refs, so the agent never proposes a ref update at all);
      `RemoveAgentWorktree(force)` (dirty non-force → typed refusal; force → `remove --force` +
      `branch -D` + delete the whole per-agent repo + the `MirrorMaintenance` idle hook, no residue);
      **`AdoptAgentWorktree`** (the RESUME half — `worktree add <path> agent/<id>` with **no `-b`**, so a
      jail spawned for a stranded queue entry starts on that entry's existing branch with its commits
      intact: rescue-publish the dead jail's own repo into the mirror first (a crash can leave commits the
      mirror never saw — and that rescue's OUTCOME is read: a transient `Failed` refuses the whole
      adoption with `AgentBranchRescueFailedException` so `ClearWorktreeResidue` never deletes the only
      copy, while `NothingToPublish` warns + raises the G-17 `agent_rescue_empty` event), then require
      `refs/heads/agent/<id>` or throw `AgentBranchMissingException` —
      never a silent fresh branch off main — then clear the residue and re-clone; `CreateAgentWorktree`
      and this one are each other's mirror image, one refusing when the branch exists and the other when
      it does not) and **`RemoveAgentWorktreeKeepingBranch`** (the resume's rollback: the same clear
      WITHOUT the `branch -D`, because on that path the branch is the only surviving copy of the work —
      its interface default THROWS rather than falling back to the branch-deleting removal, and the
      launcher swallows the throw so the worst outcome is residue, never lost commits); `FinishWorktree`
      is the shared tail (quarantine remote, pnpm hook, `GroupShareRecursive`, branch guard, publish) so
      a create and an adoption cannot drift into differently-configured worktrees; a
      REFUSED publish also raises the G-17 `agent_ref_refused` audit event
      (`WorktreeManager.AgentRefRefusedEvent`) alongside the warning, because "an agent tried to rewrite
      history the mirror already published" must leave a durable record and not just a log line, `Prune`,
      and `List` (the union over the per-agent repos, filtered to `agent/*`, via the pure
      `WorktreePorcelainParser`); duplicate agent id → typed refusal before any mutation; spawn also
      installs the `AgentBranchGuard` hook (last, after both `GroupShareRecursive` passes, so it keeps its
      narrower non-group-writable mode) and `CheckAgentBranch` exposes the drift probe the verification
      path consults).
    - `IAgentEnvironment.cs` (the substrate facade + `SyncRemote`/`SubstrateCapabilities` records: holds
      `Repos`/`Worktrees` and — **added by P2-07** — `Sandboxes` (`ISandboxEngine`) + `Egress`
      (`IEgressPolicy`), plus `ResolveSyncRemote(hash)`; the Health/Upgrade/Teardown lifecycle stays
      deferred to a future task — documented on the interface).
    - `Wsl2AgentEnvironment.cs` (the WSL2 impl: `SubstrateId="wsl2"`, capabilities
      `(false,false,"9p","wsl")`, resolves the sync remote to a `\\wsl.localhost\…\repos\<hash>.git` UNC
      handle — **the only place the `mainguard-vm` name literal lives**, SC-2; now also constructs the
      P2-07 `DockerSandboxEngine` + `EgressProxyConfigurator` over a lazily-created Docker client —
      building the client requires no live daemon).
    - `AgentGitCommand.cs` (internal checked wrapper — delegates to the one audited `GitService.RunGit`
      primitive and maps a non-zero exit to a typed exception; NOT a second runner, spawns nothing.
      Carries the **MG-1** hardening on every daemon-side git — `core.hooksPath=/dev/null`,
      `core.fsmonitor=`, `protocol.ext.allow=never` via command-line `-c` plus
      `GIT_CONFIG_NOSYSTEM`/`GIT_CONFIG_GLOBAL=/dev/null` — which **MG-3 narrowed rather than retired**:
      the shared mirror is now read-only to every jail, so its `config`/`hooks` are no longer an attack
      surface at all, and what remains to defend is the per-agent repo + worktree, which the daemon also
      runs git against). All git routes through `GitService.RunGit`; the only process spawn here is the
      injectable pnpm runner.
  - **`Agents/Bootstrap/`** (P2-05 MainguardOS bootstrapper — client-side; gets a WSL2-enabled
    Windows machine to a health-checked `mainguardd`).
    - `WslConfigMerger.cs` (the **pure**, IO-free INI merge for `%UserProfile%\.wslconfig`: adds only
      Mainguard's `[wsl2]` keys, an existing user value always wins, every other section/key/comment/blank
      line preserved byte-for-byte, CRLF/LF newline style preserved — fixture-tested).
    - `WslRunner.cs` (hardened `wsl.exe` invoker via `ProcessStartInfo.ArgumentList` — never a shell —
      decoding UTF-16LE output and stripping BOM/NUL; the `IWslRunner` seam; `WslRunResult`; `WslCommands`
      pure argument-list builders scoped to our `MainguardEnv` distro only — lifecycle is
      `--terminate`→poll→`--unregister`, **never the VM-wide shutdown verb (G-12)**;
      `WslRunner.ParseDistroList` for `--list --quiet`).
    - `IBootstrapStep.cs` (the check/act step interface +
      `BootstrapStageState`/`BootstrapProgress`/`BootstrapOptions`, the `IBootstrapFileSystem` and
      `IDaemonHealthProbe` seams, plus `IBootstrapStepDiagnostics` (a step names its own unmet condition
      so a post-run re-check failure isn't the dead-end 'state check still failed'),
      `IDaemonHealthDiagnostics` (unit state + journal tail behind a failed health check), and
      `IDaemonStableHealthWaiter` (the probe-native consecutive-healthy wait — ONE in-VM spawn instead of
      a host-side per-second poll)).
    - `BootstrapFileSystem.cs` (default `IBootstrapFileSystem`: `.wslconfig`
      read/timestamped-backup-before-write/write, tarball existence, RAM via `GC.GetGCMemoryInfo`). The
      six ordered steps — `DetectDistroStep` (WSL-present guard; a missing `wsl.exe` surfaces
      `WslNotInstalledException` before any act), `ImportDistroStep` (`--import` from the versioned
      tarball; unregisters a failed partial before the typed throw), `WslConfigMergeStep`
      (`memory`=min(50% RAM,8GB) via `ComputeMemoryValue` + `autoMemoryReclaim=gradual`,
      backup-before-write), `FirstBootStep` (**G2 control (2): sets `kernel.yama.ptrace_scope=2` VM-wide +
      `fs.inotify.max_user_watches=524288`, persists both to `/etc/sysctl.d/`, check phase asserts
      ptrace_scope ≥ 2** — P2-07 depends on this — plus the `/etc/wsl.conf` dockerd boot command and a
      `docker info` readiness poll. **MG-17:** also writes `/etc/subuid`+`/etc/subgid` BEFORE the
      daemon.json that turns on `userns-remap` (dockerd refuses to start with a remap user that has no
      range), drains mainguard's containers/networks with the OLD daemon on the one boot that flips the
      storage root, provisions the `mainguard-jail` group + the two bind-mount sources' shared ownership,
      and its check phase asserts the remap is IN EFFECT — `docker info` reports `name=userns` AND its
      `DockerRootDir` is the base this VM's ownership was provisioned against — via the framed,
      non-vacuous `UsernsRemapPolicy` probe), `StartDaemonStep` (systemd start `mainguardd`),
      `HealthCheckStep` (bounded `IDaemonHealthProbe` poll requiring `RequiredConsecutiveHealthy`=2
      **consecutive** healthy answers — a crash-looping daemon's flap used to slip one lucky 'healthy'
      through; prefers a probe's native `IDaemonStableHealthWaiter.WaitForStableHealthyAsync` when it
      implements one, which does the whole wait in ONE `wsl.exe` spawn instead of one per second — the
      per-iteration spawn-burst class that drove the WSL service into `Wsl/Service/E_UNEXPECTED`; every
      failure names the daemon's real unit state + journal tail via `IDaemonHealthDiagnostics`).
    - `MainguardOsBootstrapper.cs` (the ordered state machine + `BootstrapContext`: skip satisfied steps →
      zero acts on a healthy re-run; execute+re-verify otherwise; resume after failure; every failure a
      typed `BootstrapException` naming the step). Real WSL behavior is the manual matrix (`RequiresWsl`)
      in the PR.
    - `VmKeepAlive.cs` (holds MainguardEnv awake for the app's lifetime with one hidden
      `wsl.exe -d MainguardEnv --exec sleep infinity` session, restarted with capped backoff — WSL
      idle-terminates the distro seconds after its last wsl.exe client exits (gRPC connections don't
      count), which stopped mainguardd between RPCs and once killed it mid-migration; G-12-safe, Dispose
      kills the holder).
    - `SandboxImageProvisioningTracker.cs` (process-wide "is an image build running right now?" gate —
      the 2026-08-05 field fix. Provisioning a stale jail image is a MINUTES-long `docker build` (the
      agent-base `nix profile install` layer pulls a toolchain from cache.nixos.org, ~4-5 min, and it is
      a SINGLE layer so a kill mid-download discards all of it). Both entry points — the startup
      auto-provision (`KickSandboxImageBuild`) and Tools → Rebuild — were discarded `Task.Run`s that
      nothing tracked, so (a) they could run two builds of the same tag at once, and (b) nothing stopped
      the shutdown sequence's `StopVmOnExit` leg from running `wsl --terminate MainguardEnv` straight
      through an in-flight build — killing it ~10ms after the terminate, every launch, so the images
      could never become current and the coordinator kept refusing to start. `RunExclusiveAsync` makes a
      concurrent request JOIN the run already in flight (same images, same sources) instead of starting a
      rival build, and `IsProvisioning` is the signal `AppShutdownSequence` (and the framework-Exit
      backstop in `ProDesktopHost`) vetoes the VM terminate on.)
    - `DaemonConnectDiagnosis.cs` (the app→daemon connect path's per-leg verdict: `DaemonConnectStage`
      (distro not running / daemon process down / no session token / transport credentials missing /
      not listening / token rejected / undiagnosed), the `DaemonConnectDiagnosis` record whose `Banner`
      is built FROM the observation that produced it, and `DaemonRepairOutcome`. Exists because every
      one of those legs used to surface as one sentence — "daemon isn't reachable" — and the
      credentials-missing leg (a daemon predating MG-19's pinned mTLS, which publishes a token but no
      certificates) could never heal, since the tier-1 refresh that installs the matching daemon only
      runs AFTER a successful connect. `IsRepairableByDaemonRefresh` marks the one leg the app may
      repair itself.)
    - `DaemonUpdater.cs` (the tier-1 daemon fast-path — the field-outage fix for the daemon baked into the
      MainguardOS tarball never advancing with the app, so every new RPC answered `Unimplemented`:
      `DaemonVersionInfo` (the proto-free `GetDaemonInfo` result), `DaemonUpdatePolicy` (pure skew
      decision — refresh when the daemon's informational version ≠ the app's, build metadata after `+`
      ignored; a `null` info = the daemon answered `Unimplemented`, which IS the skew signal; daemon-down
      is never passed here), `DaemonUpdateCommands` (pure argument-list builders scoped in-distro as root
      — stop unit → stage into `/opt/mainguard.new` (payload dir /mnt-translated via `HostPathTranslator`,
      contents `cp -r <dir>/.`) → probe+rename apphost `Mainguard.Server`→`mainguardd` + `chmod 0755` →
      swap keeping `/opt/mainguard.old` as rollback → start unit; `AllBuilders()` for the G-12 test),
      `IDaemonUpdater`/`DaemonUpdater` (runs the sequence over `IWslRunner`; on failure restores the
      rollback iff the install was retired-but-not-promoted and always re-starts the unit; typed
      `DaemonRefreshResult`, never a bare throw), and `DaemonAutoRefresh` (the one fire-and-forget call
      `App.axaml.cs` makes at control-center startup: bounded query retry while the VM boots, skew
      decision, missing/empty-payload skip, refresh + oobe.log breadcrumbs — daemon-unreachable is a
      silent skip and nothing escapes; an optional `onOutcome` callback carries the typed
      `DaemonRefreshOutcome` (`DaemonRefreshOutcomeKind` + old/new versions) — invoked at most once, after
      the log line, throw-swallowed — and the pure `DaemonRefreshToast.TryCompose` policy turns only
      Refreshed/RefreshFailed into a `DaemonRefreshToastContent`, which the App's
      `DaemonUpdateToastPublisher` posts to the shell toast stack). `VmUpgradeOrchestrator.cs` (the tier-2
      in-place MainguardOS VM upgrade wired end to end — P2-21 §3.6:
      `IVmUpgradeOrchestrator`/`VmUpgradeOrchestrator` drives `VmUpgradePlan.Steps()` in order over
      `IWslRunner` — clear stale staging → `--import` the bundled tarball as `MainguardEnv-staging` →
      quiesce BOTH daemons (old for at-rest DB copies; staging's, which systemd auto-starts on first boot)
      → migrate `/home/mainguard/mainguard` + `/home/mainguard/.mainguard` (minus the rotating
      `daemon.token`) old→staging via tar archives written to a HOST temp file (`tar -cpf /mnt/…` then
      `-xpf` — two argv-only calls; a host-side pipe through the string-typed, fully-buffered `IWslRunner`
      stdin/stdout would corrupt binary tar and cap size, and a shell pipe string is banned) → **invariant
      3:** validate by diffing depth-bounded in-distro `find` listings of old vs staging
      (`VmUpgradeMigrator.FindMissingFromListings`; one spawn per tree, never a per-path `test -e` burst)
      and ONLY then `--terminate`/`--unregister` the old distro → unlock staging, MOVE its `ext4.vhdx` to
      the canonical install dir BEFORE unregistering staging (unregister deletes the install dir's
      contents) — the move is bounded-retried and, when WSL's shared utility VM holds the VHDX (ANY
      running distro keeps the hold alive; field incident 2026-07), falls back to copy-then-cleanup: COPY
      the VHDX to the canonical path (reads pass under the hold), verify existence+length, and only THEN
      unregister staging (REORDERED vs the move path — that unregister deletes the original; the copy
      briefly doubles the VHDX on disk, a disk-headroom preflight is a tracked follow-up) — then
      `--import-in-place` it as `MainguardEnv`, best-effort `systemctl start mainguardd`. Typed
      `VmUpgradeResult`, never a bare throw: failure before the retire → `OldDistroIntact` (old distro
      still registered, its daemon re-started, staging cleaned up best-effort — retryable); failure after
      → `StrandedAfterRetire` (terminal only when BOTH promote strategies fail — the message then names
      both the move and the copy failure) naming the exact VHDX path holding the newest verified copy of
      the migrated data (canonical once the fallback copy verified, staging otherwise) + the manual
      `--import-in-place` recovery command (that VHDX is never deleted); `VmUpgradeResult.PromoteStrategy`
      records move vs copy-then-cleanup for the App's log. Host IO (temp dir, VHDX move/copy/length)
      behind `IVmUpgradeHostFileSystem`. Also `VmUpgradeCheck` (the one post-tier-1 App call, never
      throws: expected version from the app-bundled `payload/mainguardos-release` stamp, installed from
      `GetDaemonInfo`'s payload version with an in-distro `/etc/mainguardos-release` read as the
      daemon-down fallback; unknowable on either side = no offer). The offer decision (`VmUpgradePolicy` —
      proper version compare, offered ONLY when installed < expected, so a payload newer than the app is
      never downgraded) and the stamp parser (`MainguardOsReleaseStamp`) live in `VmUpgrade.cs` alongside
      the new staging-scoped builders (`InStagingAsRoot`/`TerminateStaging`/`StopUnitInStaging`, the tar
      export/extract/chown builders, `EnumerateOldTree`/`EnumerateStagingTree`,
      `ReadInstalledReleaseStamp` — all in `AllBuilders()` for the G-12 proof).
      `SandboxImageProvisioner.cs` (v1 sandbox-image provisioning — the 2026-07-17 field fix, hit twice:
      the CI-built jail images never reach installed VMs, and the tier-2 upgrade correctly does not
      migrate the docker image store (it lives outside `/home/mainguard`), so the first spawn failed on a
      fresh import AND after the upgrade. `SandboxImages` (the two specs:
      `mainguard-agent-base:latest`/`mainguard-egress-proxy:latest` + their bundled source dir names),
      `SandboxImageCommands` (pure distro-scoped argv builders — the
      `docker image inspect --format {{.Id}}` presence probe and the
      `docker build -t <tag> <'/mnt'-translated bundled source>` build, the automated form of the manual
      field unblock; `AllBuilders()` for the G-12 test; a PROVISIONING-time build — G-16 forbids only
      agent-RUNTIME builds), `ISandboxImageProvisioner`/`SandboxImageProvisioner` (over `IWslRunner`:
      probe which images are missing, then strictly SERIALIZED builds — never two at once, generous 20-min
      per-build budget — yielding typed per-image `SandboxImageBuildResult`s: Built / SkippedMissingSource
      naming the path / BuildFailed carrying the docker error tail, never a throw; one image's failure
      never stops the next), and `SandboxImageAutoProvision` (the one App startup call, sequenced after
      the tier-1/tier-2 daemon checks: all-present and no-bundled-sources are silent log-only skips, every
      outcome lands in oobe.log, and the pure `SandboxImageToast.TryCompose` policy composes a toast for
      Installed/Updated/InstallFailed. **Version anchor + staleness detect/repair (Item 1 COMPLETE,
      0.2.4):** every image is stamped `mainguard.image.version=<source-hash>` at build time
      (`SandboxImageCommands.BuildImage --label`); `ProbeNeedsProvisionAsync` returns each image as
      **Missing** (inspect-id fails) or **Stale** (`InspectImageLabel` ≠ the committed
      `SandboxImageVersions` constant — an unlabelled old image reads `<no value>` ⇒ stale — keyed on the
      *untagged* name so a `MAINGUARD_AGENT_IMAGE` tag override still version-checks), and provisioning
      prefers a `docker load` of the bundled CI `<name>.tar` (approach B — CI bytes = VM bytes, offline,
      the label rides the tar) over a labelled in-VM `docker build` (fallback A), yielding
      Loaded/Built/SkippedMissingSource/BuildFailed; `force:true` (the Settings **Mainguard OS** page's
      `RebuildSandboxImagesCommand` — formerly a standalone Tools → Rebuild sandbox images action with no
      dialog of its own, now folded into `MainguardOsPageViewModel`) reprovisions every image. The
      committed constants + the shared override-honoring `AgentBaseRef()` accessor live in
      `Agents/Sandbox/SandboxImageVersions.cs`, `Agents/Sandbox/SandboxImageSourceHasher.cs` recomputes
      them from each image's curated inputs, and `Mainguard.Tests/SandboxImageVersionsGuardTests.cs` fails
      on any un-versioned Dockerfile/COPY'd-file drift (printing the new hash to paste).
      `BuildProvenance.cs` (**MG-9 / code-signing plan step 2 — GitHub Artifact Attestations for the
      artifacts Mainguard builds itself.** `DaemonPayloadManifest` proves the COPY is faithful and both
      its sides come from the same directory, so it catches a truncated `cp` and nothing else; an
      attestation is signed by the CI run that PRODUCED the artifact, so whoever swaps the artifact cannot
      regenerate it. `BuildArtifactKind` (DaemonPayload | MainguardOsTarball), `BuildProvenanceStamp` (the
      **compiled-in** requirement: an `AssemblyMetadata("MainguardAttestedRelease")` stamped only by a
      `-p:MainguardAttestedRelease=true` build, plus the pinned `dsazykin/Mainguard` source repo —
      compiled in because a requirement living beside the artifact is deleted along with the attestation
      and the check then "passes" by not running), `BuildProvenancePolicy` (pure, fail-closed: on a
      stamped build, no attestation / a verifier that could not run / another repository / a subject
      digest that is not this artifact's are all `Refused`; an unstamped developer build reports
      `NotAttestedBuild` and proceeds with the gap named), `IBuildAttestationVerifier` +
      `GhCliBuildAttestationVerifier`
      (`gh attestation verify --repo … --predicate-type https://slsa.dev/provenance/v1 --format json`
      behind an injectable process runner, so the JSON parsing is unit-tested and only the launch is
      manual-matrix; a non-zero exit is a failure whatever it printed, and a throwing launch is a failed
      check, never an escape), and `BuildProvenanceGate` (hashes the artifact on disk and requires the
      attestation to name that digest; `BundlePathFor` locates an offline Sigstore bundle). Wired
      fail-closed at `ImportDistroStep` and `VmUpgradeOrchestrator`'s `import-staging` (both refuse BEFORE
      the running VM is touched) and at `DaemonUpdater.RefreshAsync`, where the payload — a directory — is
      covered via `DaemonPayloadManifest.ToCanonicalText()`/`CanonicalDigest()`: CI attests that manifest
      file (`DaemonUpdater.AttestedManifestPathFor`, a SIBLING of the payload dir so it never hashes
      itself) and the app requires the payload on disk to reproduce it byte for byte before the
      attestation counts. CI mints both attestations in `ci.yml`'s `payload-reproducible` job via
      `actions/attest-build-provenance@v2` on push.) **Code signing & the elevated-components relocation
      (audit MG-15, `docs/design/code-signing-plan.md` steps 1+3):**
      `Agents/Bootstrap/ProtectedInstallLocation.cs` — `ProtectedLocationPolicy` (the pure,
      platform-independent "can an unprivileged user write here?" rule: allow-list of administrator-owned
      roots, deny-list of user roots which WINS, unresolvable ⇒ not protected; host roots come from
      `MainguardPaths.ProtectedInstallRoots()`/`UserWritableRoots()` so the `GetFolderPath` guard holds),
      `ElevatedComponentLayout` (`%ProgramFiles%\Mainguard\elevated` + the per-user `elevated-stage`;
      style-aware `Join`, never `Path.Combine`, so Windows paths resolve on the Linux CI leg),
      **`ResumeTaskPolicy`** (THE MG-15 fix: `/RL HIGHEST` is derived, never chosen, and granted only when
      the target sits under an administrator-owned root — the per-user app therefore registers
      `/RL LIMITED` and the no-UAC-prompt escalation primitive is gone;
      `InstallerCommands.RegisterResumeTask` takes the policy and derives the level in the BUILDER, as it
      already does for path validation), and `ElevatedHelperResolution` (protected copy preferred; the
      stage wins only when NEWER, which is the UAC-prompted update launch that promotes itself; a source
      build falls back to the co-located helper and logs the degradation).
      `Agents/Bootstrap/ElevatedComponentInstaller.cs` — `ElevatedComponentPolicy` (monotonic promote:
      never downgrade the administrator-owned binary, refuse an unorderable pair,
      same-version-different-fingerprint reinstalls), `ElevatedComponentPlan` (`TryForHost` REFUSES a
      machine with no protected root rather than falling back somewhere writable; `InstallFootprint` vs
      `RemovalTargets` so "the uninstall removes everything the install created" is a test), and
      `ElevatedComponentInstaller` (plain `System.IO` replace-copy + marker, so install/update/uninstall
      mechanics run on Linux CI against temp dirs; the elevated helper promotes its OWN directory — no
      source or destination ever crosses the elevation boundary, which is why the third entry in
      `PrivilegedActionCatalog` is not an arbitrary-write-as-admin primitive; the uninstaller drives it
      through the new `Uninstaller` `remove-elevated-components` step, escalating via the helper's
      `--uninstall` inverse and REPORTING any leftover rather than leaking it).
      `Agents/Bootstrap/PinnedSignature.cs` — `SigningPolicy` (pins arrive as assembly metadata from
      `$(MainguardPinnedThumbprints)`, so swapping in a real certificate is configuration; `Covers` is an
      explicit list — Windows PE artifacts only, the ELF daemon payload and npm adapter tarballs answer
      `NotAvailable` NAMING what covers them), `IAuthenticodeInspector`/`WindowsAuthenticodeInspector`
      (`WinVerifyTrust` + thumbprint, in that order — extracting the certificate without verifying the
      digest would prove nothing; an untrusted chain is EXPECTED for a self-signed key, a bad digest never
      is) and `PinnedThumbprintSignatureVerifier` (on a signing-enabled build an unsigned covered artifact
      is `Rejected`, never `NotAvailable`). `PayloadSignature` now selects its default from the build's
      own configuration, so no entry point opts in. Packaging: the Pro head's
      `StageElevatedComponentsToPublish` target self-contained-publishes the helper into `elevated-stage/`
      at PUBLISH (never on a dev build) — an EMPTY stage is now an `<Error>`, not a `<Warning>` buried in
      the publish log, since it means the packaged app silently falls back to the per-user helper, i.e.
      exactly the MG-15 escalation — `SignMainguardExecutables` signs the staged copy too, and
      `build/velopack/pack.ps1` derives the runtime pin from the signing certificate and asserts the stage
      is present. `package-smoke.yml` asserts it too, so the check is no longer release-box-only. **`build/signing/`** — `new-signing-cert.ps1` (self-signed code-signing cert; refuses to
      write inside the repo) + `README.md` (the two halves, the rollover recipe, and what is unit-proven
      vs manual-matrix). `Mainguard.Tests/ElevatedComponentsTests.cs` +
      `Mainguard.Tests/PinnedSignatureTests.cs` prove the policies on Linux; the `WinVerifyTrust` P/Invoke
      and Program Files' real ACLs are manual-matrix. `UpdateVersion.cs` (**MG-14/MG-15** — the SemVer
      2.0.0 §11 precedence primitive every update decision orders against: build metadata ignored,
      prerelease ordering, numeric identifiers compared numerically; an unparseable version returns `null`
      and **every caller treats `null` as refuse, never as equality** — guessing there hands the downgrade
      straight back). `TrustedExecutablePath.cs` (**MG-15** — the pure, platform-independent confinement
      rule for anything launched elevated: refuses relative paths, `.`/`..` (refused, never *resolved*),
      UNC/device paths, NTFS ADS forms, quoting/wildcard metacharacters, trailing dots/spaces, and any
      path outside the install root, compared segment-wise so `Mainguard-evil\` fails against
      `Mainguard\`; applied in the schtasks **builder** rather than at the call sites so no caller can
      forget it. Syntactic and platform-independent on purpose — the Windows cases run on Linux CI.)
  - **`Agents/Sandbox/`** (P2-07 sandbox hardening + default-deny egress — daemon-side, no UI; the
    launch-tier prompt-injection exfiltration control). Adds `Docker.DotNet` to `Mainguard.Agents`
    (never referenced from `Mainguard.App.Shell` — G-18). **Pure, unit-tested heart:**
    - `ContainerSpecBuilder.cs` (**P2-48**: also mounts the dynamic-CLI root `AdapterPaths.VmRoot`
      **read-only** at `/opt/mainguard/adapters` when `AdaptersRootPath` is supplied (same G-11 ext4
      rejection as the worktree), and the `/home/agent` tmpfs now carries `uid=`/`gid=` — **without them
      the tmpfs is root-owned and mode 0700 locked the agent out of its OWN $HOME**, so every agent CLI
      that writes state under `~/.local`/`~/.config` died with EACCES on first run (verified with opencode
      in a real jail; same class as the `/run/secrets` 0711 note, and unhit until a CLI actually ran).
      `(repoHash,agentId,worktreePath,imageRef,limits,networkName,CredTmpfsSpec,proxyUrl,usernsMode)` → a
      hardened `CreateContainerParameters`: `no-new-privileges` + the G2 seccomp denylist,
      `CapDrop ALL`/no `CAP_SYS_PTRACE`, userns, memory/pids, `ReadonlyRootfs`, ext4-only `/workspace`
      bind, `/dev/shm`+`/run/secrets` tmpfs, proxy-only `Env`; **each secret now lives in its OWNER'S own
      tmpfs directory** (`/run/secrets/agent` `uid=<agent>`, `/run/secrets/supervisor` `uid=<supervisor>`,
      both `0700`, under a root-owned `0711` `/run/secrets`) and `AssertSecretDirsOwned` re-derives each
      directory from the secret's actual path and refuses a spec where the two disagree — that layout is
      what removed an in-jail `chown` which could **never** succeed: a non-root `User` plus
      `no-new-privileges` leaves even a uid-0 exec with an EMPTY permitted set on Docker 20.10.24 (the
      engine `MainguardEnv` ships), so every secret write died `EPERM` and no coordinator could start;
      **MG-3** adds the `AgentRepoPath` mount
      (the per-agent repository, READ-WRITE at its identical VM path so the worktree's `gitdir:` pointer
      resolves in-jail — the ONE git dir the jail may write, and exactly one jail mounts it) and turns the
      shared-mirror mount into a READ-ONLY one gated by the named `MirrorMountReadOnly` constant (the
      mirror is still mounted at its identical path because the per-agent repo's `objects/info/alternates`
      names it by absolute path — the jail READS the history there and writes nothing); **throws
      `SandboxSpecException` at construction** on a `/mnt/<drive>`/UNC/`C:\` source (G-11) or a dropped G2
      control; asserts controls 1/3/4 and that `ptrace_scope` is absent from the request — that's VM-wide
      P2-05's job; also `SandboxLimits`, `CredTmpfsSpec.Create(agentUid,supervisorUid)` enforcing
      supervisor-uid ≠ agent-uid).
    - `SeccompProfile.cs` (loads the **default-deny** profile — the canonical moby default
      (`defaultAction SCMP_ACT_ERRNO`) with `process_vm_readv`/`process_vm_writev`/`ptrace` removed from
      every allow rule and explicitly denied — embedded verbatim from
      `images/mainguard-agent-base/seccomp.json` as the single source of truth; `SecurityOptValue` =
      `seccomp=<json>`, never `unconfined`. `DescribeDenialGap(json)` parses the profile's RULES and
      answers whether the three memory-inspection syscalls are really denied — each named in an
      `SCMP_ACT_ERRNO` group AND in no `SCMP_ACT_ALLOW` group, because libseccomp applies the first
      matching rule so an allow entry shadows a denial outright. It exists because
      `ContainerSpecBuilder.AssertG2Controls` used to substring-search the whole `seccomp=<json>` blob
      for each NAME, and **stock moby carries all three names in its allow group** — so the guard for
      this profile's sole hardening delta was one the un-hardened upstream profile also passes. Pinned
      by `SeccompProfileTests`, which asserts both that the new guard rejects an upstream-shaped
      document and that the OLD guard's predicate accepts it).
    - `EgressAllowlist.cs` (model + JSON persistence + `allowlist_changed` audit events; `DefaultEntries`
      = model APIs + package registries with **no git host** (A6);
      `EgressAllowlistEntry.DefeatsA6`/`LooksLikeGitHost` flag a git-host entry).
      **Edits are now DURABLE.** `ToPersistedForm`/`FromPersistedForm` had no production callers on
      either side: `Wsl2AgentEnvironment` built `WithDefaults(audit)` on every daemon start, so an
      `EgressGrpcService` add/remove mutated an in-memory list that was audited, re-rendered onto the
      live proxy, and silently reverted by the next restart or WSL idle-stop — the user re-approving the
      same host forever while the audit log logged each one as a fresh decision, and, in the removal
      direction, a host the user cut off quietly coming back. `IEgressAllowlistStore` +
      `FileEgressAllowlistStore` (`<vmRoot>/egress-allowlist.json`, atomic temp-file+replace) hold the
      state; `LoadOrDefaults` is the production entry point and falls back to `DefaultEntries` on a
      missing or corrupt file rather than refusing to boot — the SAFE direction, since the fallback is
      the shipped restrictive set. `EmitChange` saves after the audit append, so a store failure can
      never cost the security record. `CombinedWith` deliberately carries NO store: it is the
      render-time union with installed CLIs' auto-permitted hosts, not a user edit. Pinned by
      `Mainguard.Tests/EgressAllowlistPersistenceTests.cs`.
    - `EgressProxyConfig.cs` (pure renderer: tinyproxy allow-filter + dnsmasq pinned-DNS + iptables
      backstop from the allowlist. The backstop is rendered as a complete `*filter` table piped to
      **`iptables-restore`**, i.e. applied in ONE netlink transaction: it used to be `iptables -F` plus
      ~13 `iptables -A` processes, and measured on a live proxy the chain immediately after the flush is
      `-P INPUT DROP` with **zero rules** — a total blackhole including ESTABLISHED traffic — for the 131
      ms the re-apply took. Since a config push runs on every agent spawn, spawning one agent broke every
      other agent's egress: 20 of 118 probes lost across 5 reloads, with 1.0 s `time_connect` values (a
      dropped SYN recovering on the retransmit). This is NOT a return to the pre-MG-18 append bug — a
      restored table replaces, so reloads stay idempotent; the replacement is just one operation instead
      of fourteen. Guarded by
      `EgressSegmentationTests.Backstop_IsAppliedAtomically_NeverFlushThenRepopulate` (shape) and
      `SandboxNetworkIsolationDockerTests.ReapplyingTheBackstop_NeverDropsAPacket` (live). That live test
      asserts by failure MODE, because the two outage mechanisms have opposite curl signatures — proven by
      direct experiment: **a filter drop** leaves the SYN unanswered (a ~1 s `time_connect` when the
      retransmit lands, or `rc=28 Timeout` when it does not), while **a daemon restart** closes the port
      and the kernel RSTs immediately (`rc=7 … after 0 ms`). That `rc=7` class — the daemon-restart outage
      — is **closed by MG-41** in `images/mainguard-egress-proxy/reload.sh`: a push restarts the daemons
      only when the rendered config differs from what the live ones were started from, and the
      entrypoint's boot reload yields to any reload that has run or is running (`--boot`), so nothing
      restarts the proxy after `EnsureReadyAsync` returns. Guarded live by `EgressProxyReloadDockerTests`;
      the `REFUSED` counter stays reported-not-asserted because it names a different mechanism than the
      one that test pins; the dnsmasq render carries **`filter-AAAA`** — the agent fabric is IPv4-only by
      construction (no network is created with IPv6, and the agent segments are `Internal`), so a AAAA
      handed to a jail is an address it can never route to, and which family a tool picks is up to that
      tool: **verified on a live jail that without it node's `dns.resolve6` is handed the real AAAA for an
      allowlisted host**. `getent` cannot see this — glibc skips the AAAA query entirely on an IPv4-only
      host — but the agent CLIs are Node/Go binaries carrying their own resolvers, which is exactly the
      population the control is for; the P2-08 `RenderTinyproxyUpstreams` fronts model-API hosts through
      the AI-gateway `host:port`).
    - `DeclaredDependencyResolver.cs` (F5: `go.mod`/`package.json`/`package-lock.json` → the exact
      `DeclaredDependencySet`; `EnsureAllowed` throws `DeclaredDependencyDeniedException` out of scope).
    - `DaemonGitProxy.cs` (A6: fetch/`upload-pack`-only, prefix-allowlisted; **no `receive-pack`/push
      method exists** — a non-fetch service hits the single structural refusal → `egress_denied` audit +
      transparency line + typed `GitProxyRefusedException`).
    - `NetworkTransparencyLog.cs` (P2-17 seam: `INetworkTransparencyLog`/`TransparencyLine` + in-memory
      impl). **PR3:**
    - `ContainerSpecRequest`/`SandboxSpawnRequest` gain an optional `IpcDirPath` — the coordinator's
      per-agent IPC dir bind-mounted READ-ONLY at `/opt/mainguard/ipc` (same G-11 ext4-only rejection;
      workers get no spawn channel), and `SandboxCliLaunch.cs` is the pure `docker exec -i -t` argv
      builder that starts an installed CLI inside its jail under a daemon-side PTY (agent uid, an explicit
      in-jail `TERM` via `-e TERM=xterm-256color` (`InJailTerm` — never docker's implicit bare `xterm`),
      `/workspace` workdir, and a fixed argv-safe `sh -c` wrapper that sources
      `CredTmpfsSpec.DefaultCredentialPath` — `/run/secrets/agent/agent.env`, spelled through the constant
      because the wrapper's `[ -r … ]` guard makes a stale copy of the path fail SILENTLY — and puts the
      IPC mount on PATH before `exec "$@"`). **Engine-agnostic seams (no Docker.DotNet in the
      signature):**
    - `ISandboxEngine.cs` (`SpawnAsync`/`ExecAsync`/`PauseAsync`/`UnpauseAsync` (P2-09 yield-timeout
      `docker pause`/`unpause`)/`StopAsync`/`RemoveAsync`/`ImageExistsAsync` (the v1 spawn-preflight image
      probe; defaults true — an engine/fake with no separate image store has nothing to preflight, the
      Docker impl really inspects)/**`ImageDigestAsync` (MG-27 — the immutable `sha256:` content digest a
      mutable ref resolves to; defaults null)** + `SandboxSpawnRequest`/`SandboxSecrets`/`SandboxHandle`
      + **`SandboxSettingsFile(Root, RelativePath, Content)`** (the CLI-settings restore payload, carried
      on `SandboxSpawnRequest.CliSettingsFiles` rather than on `SandboxSecrets` because settings are NOT
      secrets — their durable home is a per-repo JSON file, not the OS keychain);
      **MG-36:** `SandboxSpawnRequest` also carries the optional per-agent `NetworkName`/`ProxyUrl`) and
      `IEgressPolicy.cs` (`Allowlist`/`NetworkName`/`ProxyUrl`/`EnsureReadyAsync`/`Evaluate` + the
      **MG-36** `EnsureAgentSegmentAsync`/`RemoveAgentSegmentAsync` →
      `AgentSegment(NetworkName, ProxyAddress)`; both are default-implemented as "do not segment" so the
      substrate-less test doubles keep compiling, and the shipped configurator always segments; **MG-4:**
      `CanProxyReachAsync(hostPort)` — can the egress proxy actually open a TCP connection to the daemon's
      model gateway? The caller's contract is **false ⇒ do not confine**, because a confined jail has no
      other route and pointing it at an address the proxy cannot dial is an outage, not a degradation.
      Default-implemented as `true` for the test doubles, which never run with a gateway configured).
    - `UsernsRemapPolicy.cs` (**MG-17**, pure — the ONE definition of what "user-namespaced" means for
      MainguardEnv: `userns-remap: mainguard` with a PINNED `/etc/subuid`+`/etc/subgid` range
      `100000:65536`, so container uid 0 → host 100000 and the agent's uid 1000 → host 101000; `HostIdFor`
      throws outside the range rather than silently mapping to `nobody`. Also owns the boot probe
      (`ProbeScript` + `DescribeUnsatisfied`: sentinel-FRAMED `MGUSERNS[…]MGROOT[…]MGGROUPS[…]`, so
      "docker info answered nothing" is a distinct reason and can never read as a pass, and the
      `DockerRootDir` must be `/var/lib/docker/100000.100000` — "some remap" is not the invariant, the
      mount ownership is provisioned against THIS base) and the idempotent root scripts `FirstBootStep`
      runs: `PreFlipDrainScript` (remove mainguard containers/networks with the OLD daemon before the
      storage root moves — the one moment the proxy is recreated), `GroupProvisionScript`
      (`mainguard-jail` at gid 101000 + the daemon's membership, printing `MGCHANGED` only on a real
      change so mainguardd is restarted only when it must be), `MountOwnershipScript`
      (`repos/`+`worktrees/`+`agents/` → `2775 mainguard:mainguard-jail`, setgid so MG-3's new per-agent
      dirs inherit the group BY CONSTRUCTION — `agents/` is created here ahead of use precisely because
      `docs/design/mg-3-mediated-ref-updates.md` §6.2 asks for that ordering; the read-only `adapters/`
      gets `a+rX` and deliberately no group write; `~/.mainguard` is never touched). **Why a group and not
      a chown to the remapped uid:** the daemon CREATES and keeps writing those trees and runs
      unprivileged as uid 1000, so it can neither chown to 101000 nor write a tree owned by it — sharing
      through a group whose gid IS the remapped agent gid is what lets both use them, and leaves the jail
      owning neither).
    - `ExecStdinTransport.cs` (**the exec-stdin transport, and the reason there is one.**
      `IExecStdinTransport` + `DockerSocketExecStdinTransport`, which speaks exec create /
      start-with-`Upgrade: tcp` / inspect straight to the daemon's unix socket, pinned to API `v1.41` —
      the newest the EOL Docker 20.10.24 in today's `MainguardEnv` serves and still served by modern
      engines, so one version spans the upgrade. **Docker.DotNet 3.125.15 is the latest published version
      and CANNOT do this:** measured against Engine 29.4.3 on a jail-shaped container, an exec with
      `AttachStdin = true` delivers neither the payload nor the half-close — the in-jail file is created
      and left **0 bytes** and the exec never leaves `Running`, so the wait never returns, in all four
      variants (with/without a delay before `CloseWrite`, draining vs polling
      `InspectContainerExecAsync`). Since every spawn-path secret rides exec stdin, upgrading the VM's
      dockerd would have broken every agent spawn with a 30-second timeout as its only symptom.
      **`PUT /containers/{id}/archive` is not the way out:** Docker refuses to extract into a
      read-only-rootfs container unless the destination is inside a volume or bind mount, and
      `HostConfig.Tmpfs` entries are neither, so both secret paths are rejected
      `400 "container rootfs is marked read-only"`; declaring the same tmpfs via `HostConfig.Mounts`
      passes that check and is worse — the call reports success while the bytes land in the image layer
      *underneath* the tmpfs, invisible to the container. Both facts are pinned live by
      `SecretDeliveryDockerTests`, and `DockerStdinRegressionGuardTests` fails the build if
      `AttachStdin = true` returns to production source.)
    - `SandboxImageDigest.cs` (**MG-27**, pure: `IsDigest`/`Normalize`/`SameImage` — the digest algebra
      behind "pin by `@sha256:`, verify at spawn". `SameImage` is the ONE place a container's reported
      image is compared with the ref it was created from, and it understands Docker's abbreviated short-id
      form: that comparison drives destructive recreate paths (a jail, and the shared proxy whose
      replacement strands every running jail's egress), so a naive `!=` against a digest would recreate
      the world on every spawn). **Docker impls:**
    - `DockerSandboxEngine.cs` (persistent jail keyed by repo+agent — `docker start` a stopped one,
      recreate on base-image change; **no runtime image-build** — G-16; writes secrets to per-owner 0400
      tmpfs via stdin exec, never argv/env — that stdin rides `ExecStdinTransport.cs` and NOT
      Docker.DotNet. **The write execs AS THE SECRET'S OWNER and never chowns** — a non-root `User` plus
      `no-new-privileges` leaves even a uid-0 exec with an EMPTY permitted set on Docker 20.10.24, so the
      old "root, so chown is permitted" premise was false and every spawn died `EPERM`; the owner instead
      creates its own file in the tmpfs directory Docker mounted owned by it. `HasOwnedSecretDirsAsync`
      adds `staleSecretLayout` to the reuse staleness list, because tmpfs entries are fixed at create and
      reusing a pre-upgrade jail would exec a non-root owner into a directory that does not exist —
      resurrecting the same EPERM for every container that outlived the upgrade.
      `RestoreCliCredentialsAsync`/**`RestoreCliSettingsAsync`** run on BOTH the create and the reuse
      paths, write-if-absent as the AGENT uid over exec stdin — `docker cp` would write UNDER the tmpfs
      `$HOME` and report success while the container sees nothing, and write-if-absent stops the host's
      older copy clobbering a live jail's fresher tokens or approvals. **`SettingsRootPath(root)`** is the
      ONE `AdapterSettingsRoot`→in-jail-directory mapping, shared with the harvest side so the two legs of
      the round trip cannot drift apart. **`ApplyWorkspaceSettingsIgnoreAsync`** appends the WORKSPACE
      settings paths to `$GIT_DIR/info/exclude` — `/workspace` IS the agent's git worktree and agents run
      `git add -A`, so without this the feature would commit the user's permission allowlist into their
      repository and merge it to main. Driven by `SandboxSpawnRequest.WorkspaceIgnorePaths` (the
      adapter's DECLARATION) unioned with anything restored, because the session that most needs the
      ignore is the FIRST one — nothing to restore, and the CLI creates the file itself. The exclude file
      lives in the per-agent repo the daemon deletes at teardown, so nothing tracked is touched and no
      state outlives the agent) and `EgressProxyConfigurator.cs` (internal `mainguard-agents` network + egress leg +
      the `mainguard-egress-proxy` container (image `DefaultImageRef` — the ref the v1 spawn preflight
      probes); renders + pushes the allowlist config; a `gatewayUpstream` ctor arg pushes the P2-08
      model-host fronting, and an `installedAdapterHosts` provider unions each installed CLI's declared
      egress hosts (`EgressAllowlist.CombinedWith`, direct-route `AgentService` kind) at render time —
      **auto-permit on install**, so e.g. claude-code reaches `platform.claude.com` with no hand-editing.
      **MG-4 — `gatewayReachableAt`:** a SEPARATE ctor arg from `gatewayUpstream`, and the separation is
      load-bearing. It adds the gateway's own host to the rendered filter as a direct-route entry
      (`CombineGatewayHost`/`GatewayHostOf`, port stripped — tinyproxy filters on hostname) and emits no
      `upstream` directive, so a confined jail can reach the daemon while every existing host keeps the
      route it had. `gatewayUpstream` would instead front the model hosts for EVERY agent, dragging OAuth
      traffic through the gateway to be 401'd, which is why production passes only the former.
      `CanProxyReachAsync` performs the real connect from inside the proxy (bash `/dev/tcp`; the proxy
      image has no HTTP client), cached on success only.
      **MG-36 — per-agent segmentation:** every jail used to attach to the one flat `mainguard-agents`
      network, so agent A could dial agent B's container IP and ports;
      `EnsureAgentSegmentAsync(repoHash, agentId)` now gives each agent its OWN internal network
      (`AgentSegmentName` = `mainguard-agent-<containerNameStem>`) whose only other member is the shared
      proxy, and `RemoveAgentSegmentAsync` reclaims it at teardown (Docker's local bridge pool is ~32
      networks deep). Docker's one intra-network knob, `enable_icc=false`, is all-or-nothing — measured
      against a real daemon it drops jail→proxy along with jail→jail — so "one network, tenants isolated
      but not from the proxy" is not expressible and segments are the only shape that works without
      host-level iptables (the daemon runs as an unprivileged VM user whose only capability is the docker
      socket). Segmenting is **additive**: attaching a running proxy to a new network leaves its pid and
      its existing legs' addresses/MACs untouched, so it does not re-open the "recreating the proxy
      strands running jails" wound. It is folded INTO the one retried adopt→start→wait→push sequence,
      never bolted alongside it. `IsDefaultDenyAgentNetwork` is the predicate every fail-closed gate keys
      on (MG-7's resolver pin, MG-18's posture check) — a gate that compared against the literal
      `mainguard-agents` would have silently switched itself off the moment a second network appeared.
      `ProxyAddressesOf` collects the proxy's address on EVERY segment so the MG-18 backstop admits each
      (a single-address render would DROP agents 2..N). **MG-27:** the proxy's image ref is resolved to
      its content digest and both compared and created against that).
    - `EgressBlockDetector.cs` (pure: a CLI's failure output → the egress host the default-deny proxy
      refused, skipping already-allowlisted + git hosts — the "what was blocked" core behind the Fix-2
      unblock/keep prompt). **Fix-2 block-notification is wired end-to-end:** a dead CLI's reason rides
      `StateChange.reason` (agent.proto) → `DaemonBackedOrchestrator` runs the detector → raises
      `EgressBlocked` → `ControlCenterViewModel` shows the `EgressBlockPromptViewModel` overlay
      (Unblock/Keep); **Unblock** adds the host over the new `EgressService` (`EgressGrpcService`, backed
      by `IEgressPolicy` — adds to the live `EgressAllowlist` + re-renders the running proxy) and retries
      the coordinator.
    - `DockerResourceSampler.cs` (**the Resource Monitor's data source, which did not exist**: the tab
      shipped rendering per-agent CPU/RAM over a sampler nobody wrote, so the client hard-coded
      `CpuPercent: 0, RamGb: 0` and every agent read a convincing 0% forever. `IContainerResourceSampler`
      (the seam the daemon join + the gRPC stream + the client projection are all driven through with
      KNOWN values end to end), `ContainerResourceSample` (**a null reading means NOT MEASURED, never
      zero** — the two are different facts and the UI renders them differently), `DockerResourceSampler`
      (one-shot `stats?stream=false` per jail, parallel + bounded at `MaxConcurrentSamples`, with the
      reading taken through `SynchronousProgress<T>` — **never `Progress<T>`**, which raises its callback
      asynchronously so the awaited call can complete while the value is still queued, losing a reading
      the engine actually returned;
      **deliberately never `one-shot=true`**, which zeros `precpu_stats` so the CPU delta is uncomputable
      and a naive percentage reads a fabricated 0%), and `UnavailableContainerResourceSampler` (no engine
      ⇒ every agent explicitly unknown). `TryComputeCpuPercent`/`TryComputeMemoryBytes` are pure + unit
      tested; both cgroup generations are handled because the generation follows the HOST KERNEL, not the
      engine. **Verified on BOTH Docker 20.10.24** (the EOL engine in today's `MainguardEnv`, max API
      v1.41) **and 29.4.3** (CI/Docker Desktop) — Docker.DotNet 3.125.15 delivers exactly one message and
      completes on both, so the `AttachStdin` hang that forced `ExecStdinTransport.cs` onto a raw socket
      does not affect this endpoint)
    - `DockerAgentLister.cs` (P2-08: lists `mainguard.agent`-labelled containers → `AgentContainerState`
      for the swarm reconciler — Docker as the sole liveness truth). Seccomp/proxy images live under
      `images/` (built in CI, never at runtime). **MG-42 — per-repo verification toolchain** (the curated
      base image has no .NET/Rust/JDK/Ruby/PHP, so the merge queue could not verify Mainguard's own repo,
      nor most languages the product targets): `ToolchainCatalog.cs` (the **closed, product-owned** set of
      toolchain recipes — `dotnet-10` from the version-addressed, sha512-pinned official SDK tarball, plus
      `rust-stable`/`jdk-21`/`ruby-3`/`php-8` from the SAME pinned `NIXPKGS_REV` the agent base image
      bakes, which `ToolchainDeclarationTests` reads out of the Dockerfile so the two cannot drift. A repo
      names an **id**; it never supplies a URL, a checksum, a revision or a command — that asymmetry is
      the whole security design, because a declaration file that described an *installation* would be an
      install-time arbitrary-code-execution surface in a file an agent can write. `ToolchainDelivery`
      splits the catalog into `ImageLayer` recipes — built on the spawn path, required whenever the
      toolchain needs apt packages (`dotnet-10` aborts without `libicu72`) or the baked nix store — and
      `RuntimeMount` entries, which come from the user-managed manifest below. `All` unions both, so ONE
      table still answers "may a repository declare this id?").
    - **`Agents/Toolchains/`** — the **user-managed** half of the same feature (the base image's
      nixpkgs `python3` has no pip and the rootfs is read-only, so a Python repo could commit tests that
      could never run; see [`docs/design/python-toolchain.md`](../design/python-toolchain.md)):
      - `toolchains.starter.json` (the curated manifest, embedded — pinned version, upstream-published
        sha256, HTTPS payload, PATH/env, and the probe. Adding Node or Go is an edit to THIS FILE only).
      - `ToolchainManifest.cs` (`ToolchainEntry`/`ToolchainProbe` + a validating parser that refuses a
        non-HTTPS payload, a short hash, a path-shaped id or an empty probe — so a bad edit fails CI
        rather than a user's install. `{toolchain}`/`{cache}` tokens expand against the VM path when an
        install is probed and the in-jail mount path when a container is built).
      - `ToolchainChannel.cs` (install/remove/list into the VM over the SAME `IAdapterInstallHost` seam
        the agent-CLI channel uses. Fetches and checksums **in the VM** — a ~350 MB payload is not
        base64-over-stdin — refuses to unpack on a mismatch, and writes the install marker LAST, only
        after a probe that RUNS the toolchain at the pinned version. `ListAsync` re-probes rather than
        reading markers: PR #305's marker reported healthy for eleven days).
      - `ToolchainPaths.cs` (`/home/mainguard/mainguard/toolchains` → `/opt/mainguard/toolchains`,
        bind-mounted **read-only** so one shared tree cannot be rewritten by a jail to change what
        another agent's verification runs).
    - `ToolchainDeclaration.cs` (`.mainguard/toolchain` — one catalogued id per line, `#` comments;
      `ToolchainDeclarationResolver` mirrors `VerificationCommandResolver` input-for-input — branch vs
      main vs a human-owned out-of-branch pin — and arms the same RT-D2 `ChangedTestCommandGate` under its
      new `ToolchainItem`. **The one deliberate asymmetry:** the verify resolver *runs* the branch's
      command and flags the drift, while this one *provisions* **main's baseline** and flags the drift —
      flagging an install after it has already run as root is not a control, so a branch-side edit never
      reaches the provisioner at all).
    - `RepoToolchainConfig.cs` (reads the declaration out of git —
      `git show <mainBranch>:.mainguard/toolchain` against the daemon-side bare mirror, which no jail can
      write).
    - `ToolchainProvisioner.cs` (takes an optional `IProgress<string>` alongside its daemon-log sink and
      reports `BuildingMessage(declaration)` **before** the build, `StillBuildingMessage(…)` every 20 s
      **during** it (`ToolchainBuildHeartbeat.cs`), `WaitingForBuildMessage(…)` while queued behind another
      spawn's identical build, and `BuiltMessage` after — only on a real
      build and never on a cache hit — this is the one launch step that runs for MINUTES, and without a
      user-facing line for it the coordinator surface had nothing to show and fell through to "the
      coordinator isn't responding … use Stop to cancel and try again", which killed the build. **The
      build gate is `ToolchainBuildGate.Shared`, NOT a field on this object**: the launcher builds a new
      provisioner per spawn, so the old per-instance `SemaphoreSlim` serialised nothing across spawns and
      two agents really could run two identical multi-GB builds — the comment claimed otherwise and was
      wrong about the only case that mattered. Builds the
      layer
      `FROM <the bare digest the spawn preflight just resolved>`, so **MG-27's pin is inherited rather
      than bypassed**, and hands the launcher the layer's OWN content digest. **The FROM is a BARE digest
      and never `<name>@sha256:…`** — that form is a canonical REGISTRY reference, matched against an
      image's `RepoDigests` and PULLED when absent, and a locally built image has no registry manifest
      digest, so on a classic overlay2 image store the build dies at step 1 with
      `pull access denied for mainguard-agent-base` (measured: exactly what CI did). It passed on a dev
      box only because the containerd image store synthesises `RepoDigests=[name@<its own Id>]` for
      locally built images — two engines, opposite behaviour, and the green one green by accident; the
      guard is the environment-INDEPENDENT string test
      `TheGeneratedDockerfile_UsesABareImageId_NotAResolvableRegistryReference`, because a Docker leg
      alone passes on the permissive engine. The pin does not rest on the FROM form:
      `VerifyBuiltOnBaseAsync` proves parentage AFTER the build from the image format itself — a derived
      image's `RootFS.Layers` begins with its parent's, so the base's diffIDs must be an exact prefix of
      the layer's (verified against real images: base 7 diffIDs, layer 9, first 7 identical). That is the
      check a label cannot do — `mainguard.toolchain.base-digest` is a string the builder chose, a diffID
      is content-addressed — and it runs on the CACHE-HIT path too, so every spawn re-confirms the layer
      sits on the base the preflight just verified. An indeterminate answer is a refusal, never a skip;
      the image tag is content-addressed over base digest × declaration, so N repos with one declaration
      share one layer and a comment edit rebuilds nothing, while a layer built on an older base is a
      different artefact by construction; a cache hit re-checks the provenance labels because a local tag
      is a name, not evidence).
    - `ToolchainBuildGate.cs` (the **process-wide** "one build per toolchain image, across every spawn"
      gate, keyed by the content-addressed image TAG — the same inputs (base digest × declaration ×
      rendered Dockerfile) that decide whether two builds would produce the same layer, so two repos
      declaring `dotnet-10` share one build and a changed declaration never queues behind a build that
      cannot satisfy it. It **serialises rather than joins** (unlike
      `SandboxImageProvisioningTracker`, which hands the second caller the first's task): joined callers
      share a fate, so one user's Stop would cancel another spawn's build, and the second caller would
      inherit the first's cancellation token and progress sink. The waiter reports every 20 s, because a
      silent wait is indistinguishable from a hang to every watchdog upstream. Ref-counted, so a daemon
      running for weeks keeps no dead semaphore per declaration.)
    - `ToolchainBuildHeartbeat.cs` (keeps a running build AUDIBLE: one line per interval for as long as
      the build call is outstanding, carrying elapsed time and the engine's last output. Deliberately
      **not** gated on engine output — `dotnet-10`'s slowest step is a silent `curl -fsSL … | tar`, so a
      beat that required engine chatter would go quiet during the healthiest, longest part of the build,
      which is the original bug in a new costume. Disposal is awaited, so no line can arrive after the
      build it describes ended.)
    - `DockerToolchainImageBuilder.cs` (the Docker implementation: a **one-file** in-memory tar build
      context — the generated Dockerfile has no `COPY`/`ADD` and the context has nothing to copy, so the
      layer's only inputs are the pinned base plus the recipes' checksum-verified fetches; it reads the
      build's NDJSON `error` field, without which a failed `RUN` reports success and leaves no image, and
      retains `status`/`errorDetail` frames as well as `stream` — a build that dies resolving its FROM
      emits its one step line as `stream` and the whole pull-and-refusal story as `status`, which is why
      the first CI failure's "build log (tail)" contained exactly the one line nobody needed.
      `DescribeBuildContextAsync` appends the engine version/driver and what the daemon actually holds for
      the base — present-by-digest, and the base-by-name's id/repoTags/**repoDigests** — on the failure
      path only, since an empty `repoDigests` IS the diagnosis for that class of failure). **Egress:** the
      build runs on the **VM's** network via the daemon's Docker client — never the jail's `internal`
      per-agent segment — so nothing here touches `EgressAllowlist` and the git host stays off it (A6);
      that is precisely *why* the toolchain must be a layer rather than an in-jail install. **Timing:**
      provisioning-time, on the spawn path before any container exists (G-16 forbids only agent-runtime
      builds), which is also why the layer is chosen at spawn — a live jail's image cannot be swapped
      underneath it. **Failure:** typed `ToolchainProvisioningException`/`UnknownToolchainException`
      (`Mainguard.Git/Exceptions/`), never a degrade;
    - `MergeQueueProvisioner` additionally runs each recipe's catalogued probe **inside the worker's own
      jail** before every verification, so "our records say provisioned, the container says otherwise"
      surfaces as a provisioning failure instead of an exit-127 that reads like the agent's code being
      broken. **MG-43 — the daemon-owned package cache** (#269 gave the jail a .NET SDK, so verification
      could finally *start*; it still could not *finish*. The jail's `$HOME` is a **256 MiB tmpfs** —
      verified as a hard limit, a `dd` of 400 MiB stops at 256 — and `Mainguard.slnx`'s NuGet closure
      measures **1.7 GB**, so `dotnet test Mainguard.slnx --configuration Release` died at `ENOSPC`
      *inside the gate that decides whether an agent's work may merge*, and the queue recorded an ordinary
      failed verification. Two non-solutions were refused on purpose: weakening the verify command — the
      gate is only as good as the command behind it — and redirecting the cache into `/workspace`, which
      puts gigabytes of untracked files *inside the tree under verification*, per agent, where
      `git add -A` commits them): `PackageCachePolicy.cs` (pure — the layout
      `<vmRoot>/caches/<repoHash>/<agentId>` on ext4, bind-mounted READ-WRITE at the fixed target
      `/var/cache/mainguard`, which `ContainerSpecBuilder` re-asserts is outside BOTH `/workspace` and
      `$HOME`; the cache-ONLY environment
      `NUGET_PACKAGES`/`NUGET_HTTP_CACHE_PATH`/`NUGET_PLUGINS_CACHE_PATH` + `npm_config_cache` +
      `PIP_CACHE_DIR` + `GOMODCACHE`/`GOCACHE` + `CARGO_HOME` — Maven/Gradle/Bundler/Composer are
      deliberately NOT mapped, because their knobs name an *install* location rather than a cache and
      pointing them here would change where code executes from; a 16 GiB budget with a 4 GiB floor, the
      floor being a real guard because a budget that cannot hold two closures evicts what the last spawn
      downloaded, i.e. a permanent cache MISS that is strictly worse than no cache; and the
      sentinel-framed in-jail writability probe with its pure parser, framed exactly like
      `UsernsRemapPolicy.ProbeScript` because a dead container, a missing shell or a dropped transport all
      produce EMPTY stdout and a `Contains("OK")` reads that as failure while `!Contains("UNWRITABLE")`
      reads it as PASS — a missing frame is its own reported reason, "the probe did not run"). **The cache
      is PER AGENT and never shared.** A cache two agents can write is a supply-chain path *between
      tenants*: agent A drops a poisoned assembly under `<packages>/<id>/<version>/lib/`, NuGet treats a
      package directory carrying its `.nupkg.metadata` completion marker as already-installed and does not
      re-hash the extracted content, and agent B's **verification** then compiles and runs A's bytes — the
      merge gate decided by another tenant. MG-36 segmented the network against exactly this reasoning, so
      "they are all the same user's agents" is no more true here than it was there; the cardinality is
      MG-3's for `AgentRepoPath` — one directory, mounted into exactly one jail. The two sharing designs
      were considered and REFUSED: a read-only shared lower with a per-agent overlay upper cannot be
      mounted in either direction (in-jail needs `CAP_SYS_ADMIN`, which `CapDrop ALL` +
      `no-new-privileges` remove; host-side needs a privileged daemon, which MG-17 is specifically about
      not being), and a daemon-populated read-only fallback folder (NuGet's own `fallbackPackageFolders`,
      which would deduplicate) requires the DAEMON to run restore — evaluating an agent-writable
      repository's MSBuild files and fetching what they name, on the host side of every sandbox boundary
      this subsystem maintains. Moving untrusted-input processing out of the jail to save disk is a
      strictly worse trade. The cost of per-agent caches is duplication, answered with a budget and
      eviction rather than with a shared writable surface.
    - `PackageCacheManager.cs` (the IO half: create + lease at spawn, whole-cache LRU eviction — **never
      partial**, because a half-deleted global-packages folder is not a cache miss but a package restore
      reports as installed and whose assemblies are gone; **never a leased cache**, because a package
      folder deleted under a live restore is corruption presenting as someone else's build failure; and if
      only leased caches remain over budget that is a typed `PackageCacheOverBudgetException` rather than
      making room by corrupting a live one. `Release` runs on the ONE teardown path every caller already
      goes through, `RemoveAgentWorktree`, so eviction stays the residue path — a crashed daemon, an
      unclean removal — rather than the routine one. Both the size walk and the delete are symlink-safe by
      explicit construction: this is the first agent-WRITABLE tree the daemon walks with delete rights, so
      `evil -> /home/mainguard` planted inside an agent's own cache would otherwise have eviction remove
      the daemon's keyring, tokens and SQLite, and .NET's recursive helpers are not documented to
      guarantee that on every platform. `PackageCacheUsage.Describe()` is the
      used/budget/percent/entries/evicted line the spawn path logs — the "make the size observable" half
      of the bound). **MG-3 is not undone by adding a writable mount.** Only the LEAF directory is
      mounted, so a jail cannot traverse to a sibling cache (inside a container, `..` from a bind-mount
      root is the CONTAINER's parent, not the host's — measured); the LRU marker is a SIBLING file
      (`<agentId>.lastused`) in a directory no jail mounts, so an agent cannot touch its own marker to
      dodge eviction; and `ContainerSpecBuilder` refuses, structurally, any package-cache source that is
      not inside a `caches/` tree (a whole-SEGMENT test, so `repos-caches-backup` is not one), which is
      what stops this mount ever being edited into a second writable path at the mirror. **MG-17:**
    - `caches/` joins `repos/`/`worktrees/`/`agents/` in `MountOwnershipScript`'s single group-share loop,
      so every per-agent directory inherits gid `AgentHostGid` and the setgid bit *by construction*;
      nothing chowns into the remapped range and the daemon still cannot. **Egress: ZERO allowlist entries
      added.** Restore runs INSIDE the jail over the same default-deny tinyproxy, pinned dnsmasq and
      iptables backstop, and every host it needs was already in `EgressAllowlist.DefaultEntries` as a
      `PackageRegistry` entry — `api.nuget.org` + `www.nuget.org` for .NET (plus `registry.npmjs.org`,
      `pypi.org`/`files.pythonhosted.org`, `crates.io`/`static.crates.io`, `proxy.golang.org`) — which
      take the DIRECT CONNECT route rather than the P2-08 model-API gateway. `builds.dotnet.microsoft.com`
      stays OFF the jail allowlist: #269 fetches the SDK tarball at image-BUILD time on the VM's network,
      which is precisely why the toolchain is a layer and not an in-jail install, and a package cache is
      not a reason to revisit that. **Failure is typed and loud, never a fall-through:**
    - `PackageCacheException`/`PackageCacheUnavailableException`/`PackageCacheOverBudgetException`
      (`Mainguard.Git/Exceptions/`) stop the spawn; the cache environment and the cache mount travel
      together in BOTH directions (environment naming `/var/cache/mainguard` with nothing mounted there
      points a package manager at the READ-ONLY rootfs and kills a restore halfway, and a mount with no
      environment is a bind mount nothing uses while the tmpfs fills anyway); a persistent jail created
      before the cache existed is RECREATED, because mounts are fixed at create; and `DockerSandboxEngine`
      re-proves the mount **from inside the started container** on the create path AND the reuse path,
      because the daemon's record of what it asked for is not evidence about the container — MG-42 paid
      for that conflation once already. **Measured end to end (`VerifyInJailDockerTests`, opt-in via
      `MAINGUARD_VERIFY_E2E=1`):** this repository's own `.mainguard/verify` now completes in a real
      hardened jail on its own default-deny segment. The guards are `PackageCachePolicyTests.cs` (the
      environment-INDEPENDENT half: the mount is outside `/workspace` and outside `$HOME`, every mapped
      ecosystem points inside it, `caches/` is in the boot script's group-share loop, and the probe parser
      refuses an absent frame — checked as strings because a Docker leg for any of them passes on a
      permissive engine or an unmapped runner for reasons that have nothing to do with the property,
      exactly as #269's real guard turned out to be a Dockerfile string assertion),
      `PackageCacheManagerTests.cs` (budget/LRU/lease/symlink, driven at the REAL 4 GiB floor using sparse
      files so no test-only budget bypass has to exist in shipping code),
      `ContainerSpecPackageCacheTests.cs` (the create request, one property per test because a test stops
      at its first failing assertion), `PackageCacheDockerTests.cs` (the facts a dictionary cannot answer:
      the tmpfs really is 256 MiB, a 320 MiB write really does fail there and really does succeed in the
      cache, one jail really cannot read another's cache at the mount point OR at its VM path, and the
      probe really distinguishes OK/MISSING/UNWRITABLE in a real container) and
      `VerifyInJailDockerTests.cs`. **The numbers, from that run:**
      `dotnet test Mainguard.slnx --configuration Release` exits **0** in **8m13s** wall clock — 3119
      tests passed, 0 failed (the 110 skips are the RequiresDocker legs, which correctly find no docker
      socket inside a jail) — with **2.2 GB** in the cache and **448 KB** in the 256 MiB tmpfs `$HOME`,
      and with the cache provisioned by the SHIPPED `PackageCacheManager` against an unprovisioned VM root
      (grant `ModeOnly`, root gid 1000 vs jail gid 101000) rather than by a chmod the test did. Two things
      were found by running it rather than by reasoning about it, and both are recorded in the test: with
      the cache in place the restore and build finally succeeded and then 169 of 2716 tests failed
      identically on `libfontconfig.so.1` (fixed in the `dotnet-10` recipe — see `ToolchainCatalog`), and
      a first attempt at 6 CPUs / 8 GiB died at exit **137**, which is SIGKILL rather than a verdict:
      MSBuild sizes its node count from the CPU ceiling and the box's whole docker VM has 9.7 GB, so the
      host OOM killer fired before the cgroup limit could. A verification jail is not an agent jail —
      `SandboxLimits.Default` (2 GiB / 512 pids / 2 CPUs) is nowhere near what a Release build of a
      15-project solution needs, and the test asserts on 137 SEPARATELY so a resourcing result can never
      be read as a failing verification. **The grant is established by the daemon, not assumed from boot
      (post-#270).** The first cut set `g+rwX`+setgid on the LEAF only and relied on `caches/` having been
      made `2775 mainguard:mainguard-jail` by the boot step — true for the canonical VM root, false for
      every other.
    - `MergeQueueEndToEndDockerTests` builds a fresh VM root under `/tmp` per test, so the leaf came out
      `0755` owned by the test user and a jail that is neither its owner nor in its group could not write
      a byte; five of those seven tests failed on `PackageCacheUnavailableException`. It was invisible on
      a developer box for the MG-3 reason exactly: there the container's uid 1000 IS the daemon's uid, so
      the jail is the OWNER and writes regardless of the group. So `PackageCacheManager` now forms the
      whole chain itself (root → per-repo → leaf, each shared as it is created — the MG-17 invariant is a
      property of the PARENT, so a leaf made correctly under an unprovisioned root still breaks setgid
      propagation) and picks its mechanism from what the root's gid actually is:
      `PackageCacheGrant.SharedJailGroup` (`2775`) when it IS `AgentHostGid` — the shipped substrate,
      unchanged and never world-writable — and `PackageCacheGrant.ModeOnly` (`2777` on the LEAF alone)
      when it is not, which is the only channel left to an unprivileged daemon that cannot chown into the
      remapped range and cannot name a group the jail is in. That fallback does not weaken the
      cross-tenant property, which rests on the mount topology rather than the mode — no jail mounts the
      root or the per-repo directory, and the parents keep `o+rx` for traversal but never `o+w` — and it
      is REPORTED in `PackageCacheUsage.Describe()` on every spawn rather than taken quietly, so a
      production daemon that finds itself on the lower rung has a boot step that did not run and says so.
      The root's gid is read with `stat -c %g` (the same tool, on the same fact, that
      `MountOwnershipScript` guards its own recursive pass with — .NET exposes a file's mode but not its
      owning gid), resolved once per daemon and refused-to-`ModeOnly` when unreadable. Guarded
      environment-independently by `DecideGrant`/`LeafMode`/`ParentMode` tests, and in a real jail by
      `ACachePreparedByTheShippedManager_IsWritableByAJailWhoseUidOwnsNothing`, which runs the jail as
      **uid 1002** on purpose: at the image's default 1000 the probe passes on a developer box whether or
      not the grant works, so it would have been the same green-for-the-wrong-reason instrument that let
      this ship. At 1002 the jail owns nothing and is in no shared group on ANY machine, and the probe
      fails before the fix and passes after it in both environments. Every one of those stdin execs is
      TIME-BOUNDED (`SandboxEngineOptions.SecretWriteTimeout`, default 30s) and raises the typed
      `SandboxExecTimeoutException`, because an endpoint that accepts an exec attach and never delivers
      stdin otherwise hangs the spawn forever with nothing to diagnose; on expiry the staged `.partial` is
      unlinked in-jail and, on the create path, the container it was going into is destroyed.
  - **`Agents/` (P2-08 AI gateway + admission + swarm reconciler — daemon-side, no UI).**
    - `TokenBucket.cs` (pure, injected-clock: two coupled requests/min + tokens/min buckets seeded from
      P2-01 `KeyHealth`; continuous fractional refill; FIFO waiter queue granted in order via `Pump`;
      `Release(lease, actual)` reconciles estimate→actual conserving tokens — the burst/refill/fairness
      property target).
    - `AiGateway.cs` (`IAiGateway` = `AcquireAsync`/`Report429`/`GetSnapshot` + records
      `GatewayLease`/`GatewaySnapshot`/`AgentSpendSnapshot`, the `IAgentSupervisor`
      pause/resume/mark-state seam + `NullAgentSupervisor`, and the pure `GatewayBackoff` —
      Retry-After-as-floor exponential; composes the bucket + ledger; budget exhaustion pauses with a
      typed reason + `budget_exceeded` audit, never kills).
    - `BudgetLedger.cs` (per-agent + per-day token + cost caps `BudgetCaps`, the static `ModelPriceTable`,
      `ISpendStore`/`InMemorySpendStore`, `Record`/`IsExhausted`/`GetSpendSince` cost-per-merged-change
      hook + `SpendRecorded` stream event).
    - `AdmissionController.cs` (`CanSpawn(out reason)` — injectable `/proc/meminfo` sampler
      `MemorySample`, ≤5 s cache, 85%-used default, honest "N GB supports X–Y" message).
    - `SwarmReconciler.cs` (Docker-as-truth reconcile: expected-but-dead → `RemoveAgentWorktree(force)` +
      mark `Dead`, orphan-live → adopt-or-stop per `OrphanPolicy`;
      `IExpectedAgentStore`/`InMemoryExpectedAgentStore`; the RT-D1 ordered `DaemonBootSequence` with the
      merge-reconcile slot FIRST — the daemon now supplies the real P2-10 `MergeReconcileTask` there;
      `MergeReconcilePlaceholderTask` remains only as `DaemonBootSequence.Build`'s no-arg fallback — then
      `SwarmReconcileTask`, which **reads** its `ReconcileReport` (`LastReport`, a boot log line, and a
      `boot_swarm_reconcile` audit entry naming every pruned/adopted/stopped agent when the pass changed
      something — narrowing it to a bare `Task` left a user whose agents were destroyed overnight with no
      artifact at all; `RecordsOutcome` pins the daemon's audit wiring, `DaemonBootSequence.Tasks`
      exposes the steps to assert it); **no PID/lock-file reads**).
    - `GatewayPersistence.cs` (SQLite-backed `DbSpendStore`/`DbExpectedAgentStore`/`DbBudgetStore` + the
      `IBudgetStore`/`InMemoryBudgetStore` seam, each op on a short-lived `AppDbContext`;
      `IBudgetStore.Set` takes per-agent AND per-day caps — P2-13 carried-in from P2-08). Models
      `SpendRecord`/`ExpectedAgent`/`GatewayBudget` (in `Models/`), exception `BudgetExhaustedException`
      (in `Exceptions/`).
  - **`Agents/Orchestrator/`** (P2-09 agent lifecycle: cooperative yield + keep-alive rebase +
    session durability + teardown — daemon-side, no UI).
    - `GitMutationGuard.cs` (the **pure**, unit-tested heart: `CanMutate(GitDirState)` → skip verdict when
      the worktree is mid-`rebase-merge`/`rebase-apply`, on a detached HEAD, or mid-merge (`MERGE_HEAD`);
      `Inspect(worktreePath)` reads those preconditions off the resolved per-worktree gitdir;
      `RunGuarded<T>(IYieldToken, isLockHeld, action, …)` runs the mutation once `.git/index.lock` clears
      with injectable exponential backoff (base 100 ms ×2, cap 5) and **requires an active yield token**
      so no worktree mutation is reachable without a completed yield (invariant 2) — a persistent lock →
      typed `GitMutationLockException`; spawns nothing, the action routes through the shared runner).
    - `YieldProtocol.cs` (`IYieldProtocol.RequestYieldAsync` sends `[IPC_UPDATE_REQUESTED]` on the
      dedicated `IAgentControlChannel` — a named pipe / second channel, **not** the interactive PTY —
      awaits `[IPC_UPDATE_READY]` ≤ 10 s, else `ISandboxEngine.PauseAsync`; always returns an
      `IYieldToken` (the sole mutation gateway) whose `Resume`/`Dispose` unpauses the jail / signals
      resume; `YieldOutcome` ByReady/ByPause).
    - `KeepAliveRebaser.cs` (`IKeepAliveRebaser`: one cycle = yield → `GitMutationGuard` check (skip on
      the agent's own mid-rebase) → dirty? `add -A` + `commit -m "wip: sync"` → `git rebase <main>` onto
      the already-fetched mirror main → conflict? status `Conflict` + route the worktree to the T-04
      resolver via `ConflictHandoff`, keep the PTY paused, **no automatic `rebase --abort`** (rejection
      trigger) → success? resume; `NotifyMainMoved` is the P2-10 hook; human edits reach worktrees ONLY
      via this Git cycle (invariant 1); records `AgentWorktreeLocation`/`RebaseCycleResult`; git via the
      shared `AgentGitCommand` — not a second runner).
    - `AgentLifecycle.cs` (`AgentContext : IDisposable`/`IAsyncDisposable` — ordered, idempotent,
      failure-tolerant teardown from an injected `TeardownPlan`: kill PTY (leader) → stop container (per
      policy) → `RemoveAgentWorktree(force:true)` (also deletes `agent/<id>`) → emit the terminal event →
      **verify clean** (residual worktrees/containers surfaced as a `Residue` warning event, aggregated
      into a `TeardownReport` that fails tests on residue); the P2-09 `AgentRunState` enum incl.
      `Conflict`; `AgentLifecycleEvent`).
    - `SessionLeader.cs` + `LeaderRegistry.cs` (the persistent PTY-fd owner intended to outlive the
      daemon: `Register`/`Kill`/`PauseInput`/`ResumeInput`/`IsPaused` per agent, and boot
      `Reattach(liveContainers)` reconciling registry sessions toward Docker truth — a session whose
      container is dead is reaped; `LeaderRegistry` is the durable **leader-owned** JSON state (atomic
      temp-then-rename), the daemon reads it on boot — **no daemon-side pidfiles**).
    - `LeaderReattachTask.cs` (the `IBootTask` that runs the reattach after the P2-08 swarm reconcile →
      boot order containers → leaders → PTY reattach; appended via `DaemonBootSequence.Build`'s new
      `leaderReattach` param. Its `LeaderReconcileReport` is **read**, not discarded — `LastReport`, a
      boot log line, and a `boot_leader_reattach` audit entry whenever the pass reaped a session, because
      reaping kills the agent's PTY and drops it from the durable registry; `RecordsOutcome` pins the
      daemon's audit wiring). Exception `GitMutationLockException` (in `Exceptions/`).
  - **`Agents/Orchestrator/` (P2-10 merge queue + verification runs + stale invalidation — the
    product spine, daemon-side, no UI).**
    - `MergeQueue.cs` (the exhaustive, persisted `IMergeQueue` state machine —
      `GetState`/`RunVerificationAsync`/`NotifyMainMoved`/`CanMerge`; every legal transition enumerated,
      every illegal one throws typed `InvalidMergeStateTransitionException`; each transition persisted in
      the same transaction (restart resumes; `ResumeAfterRestartAsync(hasLiveJail)` + its background
      `BeginResumeAfterRestart`/`LastResume` pair re-drive an interrupted `Verifying` — see the
      restart-resume note below); `NotifyMainMoved` flips every fresh `Verified`/verified-`AwaitingReview` →
      `StaleVerified` and auto re-queues FIFO by original verification time; `CanMerge` false unless
      `Verified`/fresh AND every composable `IMergeGate` allows; the human merge (`ConfirmHumanMerge` →
      `Merged`) + `RequestReview`/`Reject`/`NotifyNewCommits` are **not** on `IMergeQueue`
      (`NoAutoMergePathExists`); the loud override is a separate `RecordStaleOverrideUse`
      (`stale_override_used` audit; `CanMerge` stays false); the `Orchestrator`-namespace
      `VerificationRecord` (with RT-D2 `ResolvedCommand`/`ConfigHash`) is distinct from the UI-prototype
      `Agents.VerificationRecord`; reuses `Agents.WorkerMergeState`;
      `IMergeQueueStore`/`InMemoryMergeQueueStore`; **P2-12** adds a per-entry `MergeEntryOrigin`
      (`EnsureEntry(agentId, origin)` enters a new PR at `Working` + stamps origin, `GetOrigin`, `Cancel`
      — the closed-PR path that forgets an entry rather than reaching a terminal state — and
      `IMergeQueueStore.Delete`; origin is persisted + hydrated so the merge dispatch routes correctly
      after a restart). **Human entry lifecycle** (also NOT on `IMergeQueue`, for the same reason the
      merge is not — an agent-reachable discard is a way to erase the evidence blocking its own branch):
      `TryDiscard(agentId, by, reason)` walks any non-terminal entry to the new terminal
      `WorkerMergeState.Discarded` — distinct from `Rejected` (a verdict on the CODE, reachable only
      from `AwaitingReview`) and from `Merged` — persisting a `QueueEntryDiscard` (`GetDiscard`) on the
      entry's own row in the same `Save` and appending the `queue_entry_discarded` audit event; it
      refuses an untracked id (`SetStateLocked` would otherwise invent the entry, since every unknown
      agent defaults to `Working`) and any terminal one. A discarded entry leaves **`Agents`** — the
      stream snapshot's source — while staying in `_states`, which is what makes the discard a decision
      rather than a deletion: nothing resurrects the id (`EnsureEntry` only creates what `_states` lacks),
      `GetState` still answers `Discarded`, and the row + audit record survive. `DiscardedAgents` exposes
      the other side. `TryClearStalledVerification` returns a `Verifying` entry with no run behind it to
      `Working`, refusing while `IsVerificationInFlight` is true (that would make the live run's own
      completion an illegal `Working → Verified`); `IsVerificationInFlight` is also what `CanMerge` now
      consults so a rehydrated-but-idle `Verifying` reads "verification stalled — no run in progress"
      instead of claiming an activity that is not happening, and what
      `SettleAfterVerificationLocked` guards with so a run finishing after a discard is dropped rather
      than throwing out of an unawaited continuation. **Restart resume (the wiring, not just the
      method):** `ResumeAfterRestartAsync` shipped complete and had **no production caller** — every
      reference was its own definition, a comment observing the gap, or a test, one of which asserted the
      absence — so a daemon killed mid-verification rehydrated `Verifying` with nothing behind it and the
      entry reported "verifying" forever about a run that no longer existed; `TryClearStalledVerification`
      was the human mitigation for it. It now takes a **`hasLiveJail` probe** and splits into two arms,
      returning a `RestartResumeReport(ReRun, Stranded)`: jail alive → the verification is genuinely
      re-executed in it (the real re-drive), jail gone → the entry cannot verify at all (§3.2 — host
      execution is a rejection trigger) so it is moved straight to `Working` rather than flapping
      `Verifying → Verifying →` failed-run `→ Working`. Runs actually in flight are skipped, so it is safe
      on a live queue. Both arms append the `verification_restart_resume` audit event with an
      `outcome` field (`rerun`/`stranded`) — deliberately NOT `stalled_verification_cleared`, whose `by`
      field names a person. `BeginResumeAfterRestart` runs a pass on a background task and publishes it on
      `LastResume` (the `LastCascade` posture: tests await it, production fires and forgets), because the
      only production caller is inside a gRPC handler and a pass runs whole test suites. The pass re-reads
      each entry's state after the (Docker-blocking) probe, so a discard landing in that window is not
      reported as a run; and `RunVerificationAsync` now **undoes its `_verifying` mark when the
      `→ Verifying` transition is refused** — it marked first and transitioned second, so an illegal
      `Discarded → Verifying` left the id in the in-flight set permanently and `IsVerificationInFlight`
      answered true forever for an entry with no run (this subsystem's own defect shape, since every path
      that removes an id is downstream of that throw).
      `VerificationRunner.cs` (runs the configured test command in the worker sandbox
      via `ISandboxEngine.ExecAsync` — pass/fail is the **daemon-observed container-runtime exit code (OPS
      SA-1), never a supervisor `VerifyResult` frame**; captures the full log artifact; the RT-D2
      `VerificationCommandResolver` (resolve from the main-side baseline, SHA-256 the config, detect
      drift, honor a human command pin) + the composable `ChangedTestCommandGate : IMergeGate`).
      `VerificationStore.cs` (`IVerificationStore` — **insert-only, no update** (invariant 2);
      `InMemoryVerificationStore`/`DbVerificationStore`). `MergeQueuePersistence.cs` (`DbMergeQueueStore`
      + the RT-D1 `IMergeLeaseStore`/`InMemoryMergeLeaseStore`/`DbMergeLeaseStore` — one outstanding lease
      per repo). `MergeReconcileTask.cs` (the RT-D1 `IBootTask` in the boot merge-reconcile slot: replays
      the T-19 journal for an outstanding lease → synthesizes a missing `ConfirmMerge` + fires
      `NotifyMainMoved` for a committed-but-unrecorded merge, else releases the lease + surfaces the
      interrupted attempt — exactly once or none). `MergeQueueRegistry.cs`
      (`IMergeQueueRegistry`/`MergeQueueRegistry` + `MergeQueueContext` — the per-repo queue+leases the
      gRPC service resolves through). Models `MergeQueueRow`/`VerificationRow`/`MergeLeaseRow` (in
      `Models/`). **`MergeBranchDiffService.cs`** (P2-47 #7 — `IMergeBranchDiffService`: the daemon-side
      bridge behind `GetMergeDiff` that reuses the audited git path (`git diff main...agent/<id>` in the
      bare mirror via `AgentGitCommand`) + the pure T-06 `PatchParser`, returning the parsed `FilePatch`
      list the review cockpit's `ReviewCockpitContext.MergeDiff` needs; no new diff algorithm).
      `MergeQueueProvisioner.cs` (**MG-10 — the missing constructor call.** `new MergeQueue(...)` +
      `registry.Register(...)` existed ONLY in the test projects, so the registry stayed empty for the
      daemon's whole lifetime and every merge-queue RPC answered NOT_FOUND — the P2-10 guarantees were
      neither enforced nor bypassable, they simply were not running. Builds a repo's queue on the events
      that make a repo active (ProvisionRepo / CreateWorktree / a jailed spawn) over the same persisted
      stores and, load-bearingly, the **same `IMergeLeaseStore` singleton** the foreground merge,
      `BeginMerge` and `MergeDispatch` contend for — the one-outstanding-merge-per-repo invariant only
      spans origins while they share one store (MG-23). **P2-11 wiring:** `Build` now composes BOTH gates
      into the queue (`ChangedTestCommandGate` AND `FlaggedChangeGate`) and hangs the latter off
      `MergeQueueContext.FlaggedChanges` so the ack RPC can reach it; `ArmFlaggedChangeReview` runs the
      required `IMergeBranchDiffService` + `FlaggedChangeDetector.DetectFlagged` at verification time (the
      same cadence the RT-D2 gate is armed at, so a re-push re-classifies and drops stale acks). A diff
      that cannot be computed leaves the store **unset** — an empty set reads as fully acknowledged, so
      writing one would fail open; the branch is denied by the gate's MG-40 default-DENY instead, and the
      verification result is left untouched. The optional `resolveApprovedPlan` (agentId → approved
      `TaskPlan`) turns on the SA-1/F6 out-of-scope arm; it is **null in the daemon** because no
      agent→approved-plan binding exists yet. **The whole optional tail is now data**:
      `WiredOptionalControls` (a set of the argument names actually passed) plus `AuditLog`, asserted
      EXACTLY at the composition root — measured, deleting `audit:`/`log:`/`publishAgentRef:` from
      `GatewayServiceRegistration` left 504 tests green, and each substitutes a weaker behaviour silently
      (a throwaway audit sink, no merge log, verification against whatever the ref watcher last saw).
      `resolveApprovedPlan`'s absence is pinned as hard as the others' presence. **Restart-resume wiring:** `EnsureQueue`'s `created` branch
      now also calls `MergeQueue.BeginResumeAfterRestart`, passing `resolveContainerId` as the jail probe
      — this is `ResumeAfterRestartAsync`'s production caller. It is here rather than in
      `DaemonBootSequence` because a boot task would have nothing to iterate: the registry is empty at
      boot by design and `ActiveRepoIndex` is memory-only, so a boot-sequence resume step would run
      against zero queues forever — the same no-caller defect one level up. `EnsureQueue` is the moment a
      repo's persisted queue state re-enters the process, and every path into it (`ProvisionRepo`, a
      jailed spawn, the PR-intake target resolver) is an RPC handler, so the pass necessarily lands after
      merge-reconcile and the swarm reconciler.)
  - **`Agents/Orchestrator/` (P2-11 review-cockpit rules — flag detection + provenance emit + gate
    wiring, pure/daemon-side, no UI).**
    - `FlaggedChangeDetector.cs` (the **pure** flag detector + F6 scope: `Detect(mergeDiff)` → the
      flag-worthy `(path, category)` pairs ({ExecutableConfig, CiWorkflow, GitHooks,
      SecuritySensitivePath}); `DetectFlagged(mergeDiff, approvedPlan?, managed)` → the full must-ack
      `FlaggedChange` set incl. the F6 `out-of-approved-scope` item for a managed worker touching a file
      outside its `TaskPlan.Scope` (plan-less runs skip the scope compare); `FromLockfileDeltas` folds
      §3.6 CVE/script rows in; plus the pure `ScopeMatcher` glob (`**`/`*`/`?`)).
    - `FlaggedChangeGate.cs` (the P2-11 `IMergeGate` ANDed into `CanMerge` — holds a per-agent
      `Review.AcknowledgmentStore`; blocks until every flagged item for the current hash is acked
      item-by-item; the security boundary against hostile agent code. Fails closed on an agent it has
      never seen (MG-40). **Now actually wired**: `MergeQueueProvisioner.Build` constructs it into every
      repo queue — it previously existed only in tests and one dead ViewModel branch. `StoreFor` creates
      on demand and is for the review that classifies a diff; every READ path (the daemon's flagged-item
      projection, the ack RPC) uses **`PeekStore`**, which never creates — a fresh store holds no items and
      an empty set is `AllAcknowledged`, so creating one from a read would manufacture the "reviewed and
      clean" record the default-DENY exists to refuse.)
    - `AgentTraceEmitter.cs` (orchestrator-side provenance: `EmitTrace`/`SerializeTrace` write the
      Cognition/Cursor-style Agent Trace JSON artifact that `Review.ProvenanceReader` reads back, + the
      pure `BuildTrailers` that appends idempotent `Agent:`/`Task:`/`Plan:` commit trailers as the durable
      in-history fallback). The RT-D2 `changed-test-command` item reuses P2-10's existing
      `ChangedTestCommandGate` (not duplicated).
  - **`Agents/Orchestrator/` (P2-12 external agent PR intake — subscribe bot PRs into the same
    verify→review→merge pipeline; daemon-side, no UI).**
    - `ExternalPrIntake.cs` (`ExternalPrSource(Host,Owner,Repo,AuthorFilter)` +
      `IExternalPrIntake`/`ExternalPrIntake`: `Subscribe` (idempotent via the store) + `PollOnceAsync` —
      lists **open** PRs through the ONE audited T-23 `IPullRequestService`, filters by the configurable
      bot-author list (`DefaultBotAuthors` = `codex[bot]`/`google-jules[bot]`/`copilot`; a source's own
      `AuthorFilter` overrides), materializes each new/moved-head PR as `agent/pr-<n>` (**the jail FIRST
      via `IPrWorkerHost.EnsureWorkerAsync` — which creates the worktree as part of the ordinary spawn
      chain** → fetch head via `IPrHeadFetcher` → `MergeQueue.EnsureEntry(..., External)` at `Working`),
      invalidates+re-queues on a moved head (`NotifyNewCommits`), and cancels+prunes a PR closed upstream
      (`Queue.Cancel` + `IPrWorkerHost.ReleaseWorkerAsync` + untrack). **A DISCARDED entry short-circuits
      the whole of `MaterializeAsync`, before `EnsureWorkerAsync`** — materializing is re-asked on every
      poll, so without it the intake would keep re-provisioning the jail and its MG-36 network segment
      (the bridge pool is ~32 deep) for a pull request the human explicitly dropped, and would hold the
      worker until the PR closed upstream. The worker is released instead, so a discard means the same
      thing for an intake'd PR as for a local agent. **A refused or failed worker
      materializes NOTHING** — no worktree, no entry, and no seen-head, so the PR is retried on a later
      poll rather than entering a queue it could never leave (an entry with no jail can never be verified,
      since verification runs in the worker's own jail; an unbounded external queue that spawned
      regardless would be a DoS on the user's own box). A typed rate-limit from the host client is caught
      into per-source exponential backoff (`BackoffUntil`, never a tight retry loop), and **any other
      transport fault is caught per source** into an `external_pr_poll_failed` audit — `RunAsync` catches
      only cancellation, so one unauthenticated subscription used to kill the daemon's whole intake loop
      silently; `RunAsync` is the poll-interval scheduler loop. **INVARIANT: only the read (list) surface
      of the transport is ever called — zero upstream writes without an explicit user action; a
      `new HttpClient` here is a rejection trigger, all host traffic stays in T-23.**
      `IPrHeadFetcher`/`PrHeadFetcher.cs` (the git-CLI materialization seam — fetch `pull/<n>/head` into
      the agent worktree + hard-reset + return the head SHA, over the shared `AgentGitCommand` primitive;
      the daemon provisioning-plane fetch, not HTTP. An optional `hostUrl` resolver overrides only WHERE
      the fetch points, so the end-to-end suite drives the real fetcher against a local fixture host; the
      host KIND — and hence the `refs/pull/<n>/head` shape — is always classified from the canonical host
      name). **`PrWorkerHost.cs` (the intake's spawn seam —
      `IPrWorkerHost`/`PrWorkerOutcome`/`PrWorkerResult` + `ExternalPrIntake.WorkerAgentKind` =
      `external-pr`).** The intake used to create a worktree and an entry and **spawn nothing**, so an
      intake'd PR had no jail to be verified in and could never leave `Working` — the one criterion-4 leg
      that could not be demonstrated end to end. `EnsureWorkerAsync(repoHash, agentId, prNumber, ct)` is
      idempotent (a live worker costs no cap and starts nothing) and never throws for an ordinary
      refusal/provisioning failure; `ReleaseWorkerAsync` tears down the whole worker (jail + MG-36 segment
      + package cache + worktree), not just the worktree. Implemented daemon-side by
      `Runtime/ExternalPrWorkerHost.cs`. `MergeDispatch.cs` (`IMergeDispatch`/`MergeDispatch` — the
      pluggable per-entry merge step: a `Local` origin routes to the P2-10 `IForegroundMergeService` (its
      own `onMerged` fires `NotifyMainMoved`), an `External` origin to the P2-12
      `IExternalPrMergeExecutor` then `Queue.ConfirmHumanMerge`→`NotifyMainMoved`; the human review gate
      (P2-11 cockpit) is unchanged. Both origins take the SAME per-repo lease and re-read `CanMerge` + the
      expected `main@sha` **under** it (MG-23). **No production caller** — the shipped merge is driven
      from the Windows GUI (`DaemonBackedOrchestrator.ConfirmMergeAsync`), because both transports need
      host-side things the daemon lacks: the user's checkout, and the host token, which lives only in the
      host OS keychain and is never copied into the VM. It performs no host call of its own — it delegates
      to the same executor the GUI runs, so there is one answer to "how does an upstream PR merge"; if it
      is ever wired it must contend for the daemon's single `IMergeLeaseStore`.) `PrIntakeStore.cs`
      (`IPrIntakeStore` — subscriptions + seen head SHAs + tracked-PR set;
      `InMemoryPrIntakeStore`/`DbPrIntakeStore`, the P2-10 daemon-store shape). Models
      `PrIntakeSubscriptionRow`/`PrIntakeHeadRow` (`Models/PrIntakeRows.cs`).
  - **`Agents/Orchestrator/` (P2-14 plan approval + dual-mode orchestration — the governance spine,
    daemon-side, no UI).**
    - `TaskPlan.cs` (the pure `TaskPlanSchema.Validate(json)` → `TaskPlanValidationResult` with the **full
      deterministic error set** (never fail-fast) + the `TaskPlanFields{Scope,Approach,TestStrategy}`
      triple; **unknown top-level fields are rejected** (forward-compat honesty) + oversized guards; the
      fields combine into the ONE canonical `Mainguard.Agents.Agents.TaskPlan` the whole stack shares —
      there is deliberately no second `TaskPlan` type).
    - `PlanApprovalService.cs` (the pending-plan queue + approve/reject; **the approver identity is passed
      in daemon-derived, never client-supplied (SA-1/F2)** and persisted with the plan; restart-safe via
      `JsonPlanApprovalStore` (a second instance rehydrates every plan + identity — no EF migration,
      mirrors `LeaderRegistry`); the **S-8** anti-approval-fatigue caps live here — `Draft` enforces the
      per-coordinator concurrent-`PlanPending` ceiling + a drafting rate limit, excess →
      `ResourceExhausted` + `plan_draft_rejected` audit, and `PressureSignal` renders the "N plans
      pending" fact line; `Approve` fires `PlanApproved` so the P2-09 spawn proceeds (admission+budget
      apply THERE — a pending plan consumes neither); `InMemoryPlanApprovalStore`/`IPlanApprovalStore`).
    - `CoordinatorTools.cs` (the four capped tools — `SpawnWorker` (the two-phase gate:
      frozen-queue/active-worker-cap/admission/per-day-budget checks, then **drafts a pending plan — never
      spawns directly**), `GetWorkerStatus`, `SendWorkerPromptAsync`, `RequestVerificationAsync` — over an
      `IWorkerControl` seam; `CoordinatorToolResult{Ok,Rejected,ResourceExhausted}`; the coordinator has
      no worktree/credentials/code/merge power).
    - `CoordinatorAgent.cs` (the system-prompted chat tool-loop over the P2-08 gateway —
      `ICoordinatorModel` seam, per-turn lease, dispatch through `CoordinatorTools`; provider-agnostic).
    - `CoordinatorConversation.cs` (**P2-47 #9** — the daemon-side coordinator conversation
      `CoordinatorService` streams: the real seq-ordered transcript store + `Changed` event; `SendAsync`
      appends the human turn, drives the optional `ICoordinatorReplyEngine` — the production
      `CoordinatorAgentReplyEngine` is a thin bridge onto `CoordinatorAgent.SendAsync`, no new
      orchestration — and appends the coordinator's reply; with no engine wired it records an honest
      system turn rather than fabricating a reply. `ConversationRole`/`ConversationTurn`).
    - `KillSwitch.cs` (the emergency stop: **freeze the queue FIRST** synchronously via the shared
      `KillSwitchGate` (SA-1/F4 — before any await, so no `BeginMerge`/spawn slips the fan-out window;
      `QueueFrozenException`), then yield-all fan-out over an `IKillTarget` (timeout →
      `PauseAsync`/`docker pause`), then a journal snapshot via `IKillJournal` before returning;
      `KillSwitchTiming` holds the **RT-D4 fixed absolute `Ceiling`** —
      `FanOutDeadline = min(ceiling, max(5 s, 50×RTT))`, the ceiling a compile-time constant INDEPENDENT
      of the measured RTT, and `RttWouldExceedCeiling` feeds the P2-08 A3 `Unresponsive` signal; RT-D3:
      audit is best-effort during the kill (never blocks), a store outage records a pending gap that
      `NotifyAuditStoreRecovered` flushes as the chained `killswitch_audit_gap{killEpochId,observedAt}`;
      `KillSnapshot`/`KillReport` both carry `RttMeasured`, because
      `KillSwitchTiming.UnmeasuredRtt` — the named sentinel the daemon passes, since P2-09's
      `IAgentControlChannel` has no production transport — must not read as a measured-healthy channel
      (`KillSwitch.MeasuresControlChannelRtt` is the same fact, asserted at the composition root);
      `KillSwitch.WiredOptionalControls`/`Journal`/`AuditLog` expose the composition so the daemon's
      whole optional tail is pinned by a test rather than by a line in `DaemonHost`; journals are
      `IKillJournal` — `JsonKillJournal` (append-only JSONL beside the session token, **what the daemon
      registers**, so a kill epoch survives the restart that follows an emergency stop) and
      `InMemoryKillJournal` (tests only), both with `ReadAll`).
  - **`Agents/Adapters/`** (P2-22 pinned adapter channel — the real mechanism to run version-pinned
    agent CLIs inside the VM, replacing P2-14's interim spawn shape; daemon-side, no UI).
    - `AdapterManifest.cs` (the `adapters.json` schema —
      `AdapterManifest`/`AdapterSpec`/`ConfigShim`/`HealthProbe`/`PlatformBinaryLink` records +
      `AdapterManifest.Parse` with
      **strict** validation (`JsonUnmappedMemberHandling.Disallow` → unknown fields rejected): typed
      `AdapterManifestException`/`AdapterManifestError` on malformed JSON, a missing health probe, a
      non-64-hex `sha256`, a duplicate id, a `platformBinary` with no candidate sources or an
      absolute/`..`-escaping path (`BadPlatformBinary` — those paths are handed to `ln`/`cp` in the VM),
      and — critically — an **unpinned version**
      (`latest`/`@latest`/a range → refused; `@latest` can't even parse). Also validates
      `settingsPaths`: an unknown `root`, an escaping path, a duplicate entry, or a path shared with
      `credentialPaths` are all refused — the last because credentials go to the OS keychain and
      settings to a plaintext per-repo file, so one path in both lists would divert a credential).
    - `AdapterSettingsPath.cs` (the `settingsPaths` declaration — the NON-credential twin of
      `credentialPaths`, so a CLI's permission allowlist survives a spawn instead of the user
      re-approving every command. `AdapterSettingsRoot` (`home` = the tmpfs `$HOME`, `workspace` = the
      per-agent worktree — both wiped every spawn, and the workspace one is where claude-code records
      "don't ask again"), `AdapterSettingsPath.TryParseRoot`/`SpellRoot`/`IsWellFormed` (an unknown root
      is refused, never defaulted — guessing would decide whether an allowlist lands in the throwaway
      home or the user's checkout), and `AdapterSettingsPolicy.MaxFileBytes` (256 KiB ceiling on a
      harvest: the jail's occupant can write these files). Scope is PER REPOSITORY —
      see [`docs/design/agent-cli-settings-persistence.md`](../design/agent-cli-settings-persistence.md)).
    - `AdapterChannel.cs` (`AdapterChannel.EnsureAsync(id)` — idempotent: green probe at the pinned
      version → no-op; else fetch payload → verify SHA-256 against the pin (typed `HashMismatch` refusal)
      → run `installCmd` INSIDE the VM at the pinned version → write config shims → **place the platform
      executable** when the spec declares one → probe (exit 0 AND the
      pinned version substring); pin survival is structural — the install cmd + probe both carry the pin,
      so a breaking upstream never changes what's installed (the simulation test). Seams:
      `IAdapterChannelSource` (+ real `HttpsAdapterChannelSource`, HTTPS-only), `IAdapterInstallHost` (+
      real `WslAdapterInstallHost` over `IWslRunner` `wsl -d MainguardEnv --`), `IAdapterManifestCache` (+
      real `FileAdapterManifestCache` under appdata — refresh is explicit, so app + adapter updates move
      independently); typed `AdapterChannelException`/`AdapterChannelError`). **P2-48 wires this from a
      solid-but-uncalled mechanism into the shipped DYNAMIC-CLI feature** (the user picks CLIs at setup
      and adds more later; nothing is baked into the agent image): `adapters.starter.json` (the **bundled
      starter channel**, embedded into Mainguard.Agents — Claude Code / OpenAI Codex CLI / Gemini CLI /
      Qwen Code / OpenCode, each with a concrete version, the sha256 of its exact npm tarball, and its
      `payloadUrl`; ships so CLI selection works with **no hosted channel yet** — a hosted channel later
      serves the same schema and refreshes over it. `AgentCliCatalogTests` runs `AdapterManifest.Parse`
      over the shipped file so a bad pin edit fails CI, not a user's install).
      **`platformBinary` (`AdapterChannel.ProbeAsync`/`PlacePlatformBinaryAsync`)** closes the
      owner-reported "update available, but the health probe exits 1" failure. claude-code and opencode
      ship an npm package that is only a LAUNCHER — the real ~300 MB executable is a platform
      subpackage that a `postinstall` hardlinks over a placeholder in the launcher's `bin/`. Since
      `--ignore-scripts` (MG-9) that postinstall never runs, so the placeholder survives, and the
      placeholder is a stub that prints "native binary not installed" and **exits 1** — the probe then
      fails on a perfectly good install, identically for a pinned install and an update (it only
      *surfaced* on update because the update was the first thing to re-run the install since the flag
      landed; the pre-flag install marker had read green throughout). The flag is NOT dropped and
      nothing the vendor ships is executed: Mainguard performs the same file operation itself from the
      reviewed manifest (`ln -f`, falling back to `cp -f`, then `chmod 0755`), against a subpackage npm
      had already downloaded as an exact-versioned `optionalDependency` (`--ignore-scripts` suppresses
      lifecycle hooks, never dependency resolution — no network here). `sources` is an ORDERED candidate
      list and each is validated by the adapter's REAL health probe, first one that runs wins — the
      vendors' own algorithm, covering the AVX2/baseline and glibc/musl variants without reimplementing
      CPU detection. Paths carry no version, so a version bump needs no manifest edit. **The pin/provenance
      still cover the LAUNCHER tarball only** for these two CLIs — the bytes that execute come from the
      unpinned dependency closure the manifest already documents (third residual gap there).
    - `AdapterPaths` (the fixed layout: VM-side `/home/mainguard/mainguard/adapters` — one npm `--prefix`
      with `bin/`, `stage/`, `registry/` — bind-mounted **read-only** into every jail at
      `/opt/mainguard/adapters`, so a CLI installed AFTER provisioning reaches every new sandbox with no
      image rebuild and an agent can never tamper with the binaries another agent executes).
    - `InstalledAdapterCatalog.cs` (`InstalledAdapterMarker` + the daemon-side catalog: one
      `registry/<id>.json` marker per installed CLI — written LAST, only after a green version-matched
      probe, so a marker means 'runnable' — read **fresh per call** (installs happen while the daemon
      runs; caching would make a new CLI unlaunchable until restart) to answer `TryGetLaunch(agentKind)` →
      the argv the daemon execs in the jail. This is the `agentKind`→CLI wiring `SandboxAgentLauncher`
      used to ignore. The marker also carries `credentialPaths` and `settingsPaths` across the host/VM
      boundary — the ONLY declarations of what the daemon may restore into / harvest from a jail).
    - `AgentCliInstaller.cs` (the user-facing service the OOBE picker + the settings 'add more later'
      surface both drive: `ListAsync` (offered CLIs × live installed state via the same probe the
      channel's idempotence uses, so the picker never lies) and `InstallAsync` (per-CLI
      **failure-isolated** — one CLI failing never fails the OOBE, and every typed channel refusal maps to
      an actionable sentence)).
    - `AdapterPinOverrideStore.cs` (`AdapterPinOverride`/`AdapterPinSnapshot` + `IAdapterPinOverrideStore`
      and the file-backed store under appdata `adapters/pin-overrides.json`: a USER-ACCEPTED update moves
      the effective pin — version/payloadUrl/sha256/probe-substring together via
      `AdapterPinOverride.Apply` — with the replaced pin kept as one-step revert history;
      `AdapterChannel.EffectiveSpec` applies it inside `EnsureAsync`, `Set` validates like the manifest
      parser so a hand-edited entry can never weaken an install).
    - `AgentCliUpdateService.cs` (the Mainguard-managed CLI updater — the in-CLI self-updaters are
      disabled in every jail via `DISABLE_AUTOUPDATER=1`: `CheckForUpdatesAsync` sweeps npm for newer
      releases of npm-sourced CLIs (per-CLI failures silent — harmless at launch), `ApplyUpdateAsync`
      downloads the exact new tarball, sha256-pins it as an override and installs through the channel's
      verify path (a failure rolls the override back), `RevertAsync` restores the previous pin, and
      `EnsureLatestAsync` is the INSTALL policy: resolve the registry's current release and pin THAT —
      there is no fixed default install version; the bundled pins are the offline fallback.
      `AgentCliInstaller` composes it in `CreateDefault`; the Pro launch sequence
      (`ProDesktopHost.KickAgentCliUpdateCheck`) toasts when an installed CLI has a newer release, and the
      Agent CLIs settings rows carry Update/Revert).
    - `AdapterSpec`/`InstalledAdapterMarker` also carry **`baseUrlEnvVar`** (MG-4 stage 2 — the env var
      the CLI reads its API BASE URL from, e.g. `ANTHROPIC_BASE_URL`; validated as an env-var name and
      carried across the manifest → marker → daemon hop so the spawn path can point a CLI at the daemon's
      model gateway, which is what allows the jail to hold only a Mainguard session token while the real
      provider key stays daemon-side. Null = this CLI cannot be redirected, so BYOK confinement does not
      apply to it. `AdapterBaseUrlEnvVarTests` covers parse/validate/round-trip + legacy markers) and
      **`modelHost`** (the provider host the gateway forwards that CLI's traffic to; the pair is
      all-or-nothing because `TryConfineToGateway` requires both).
      **The bundled channel now declares the pair**, verified 2026-08-05 against the pinned tarballs
      themselves rather than vendor docs: `claude-code` → `ANTHROPIC_BASE_URL`/`api.anthropic.com`,
      `gemini-cli` → `GOOGLE_GEMINI_BASE_URL`/`generativelanguage.googleapis.com`; `codex`, `qwen-code`
      and `opencode` declare NEITHER because no usable base-URL env var exists in their shipped binaries
      (codex takes its endpoint from `config.toml` only) — a plausible-looking name would produce a
      confinement that silently does nothing. `AdapterBaseUrlEnvVarTests` pins that table as a change
      detector and enforces the all-or-nothing invariant; `docs/design/oauth-budgeting.md` carries the
      evidence.
    - `AdapterSpec` gained `payloadUrl` + `launch`, and later `credentialPaths` — the $HOME-relative files
      where the CLI keeps its interactive-login state (validated by
      `AdapterManifest.IsHomeRelativeFilePath`: relative, no `..`/`~`/backslash — the ONE gate every
      restore/harvest path trusts), carried on the install marker so the daemon restores them into the
      jail's tmpfs home at spawn and harvests them at stop (the CLI login round-trip);
    - `IAdapterInstallHost` gained `StagePayloadAsync` and `AdapterChannel` expands the **`{payload}`**
      token to the staged, hash-verified file — **the install must consume the bytes the pin covered**,
      never re-resolve a registry (that would make the pin decorative). **Honest caveat:** npm still
      fetches each CLI's platform-specific `optionalDependencies` from the registry during install, so the
      sha256 pin covers the wrapper tarball only, and the VM needs `registry.npmjs.org` at install time
      (already an `EgressAllowlist` default; the agent jail itself needs no new egress — the CLI is
      already installed by the time an agent runs). **The two surfaces that make this a FEATURE rather
      than a mechanism** (a service with no UI does not exist for the user): the OOBE's
      `OobePhase.AgentClis` picker step and the `AgentCliSettingsView` settings window — both drive this
      one `AgentCliInstaller`.
    - `NpmProvenance.cs` (**MG-9 / code-signing plan step 2 — the fix for the CIRCULAR update hash.**
      `AgentCliUpdateService.ApplyUpdateAsync` used to hash the tarball it had just downloaded and store
      that as the pin, so the pin verified against itself and anything able to serve the artifact also
      chose the digest that "verified" it. This file replaces that with an expected digest that arrives
      inside a signature: `NpmSigningKeys` (npm's registry ECDSA public key, **compiled in** — fetching it
      at check time would put the trust anchor on the same wire as the artifact),
      `NpmRegistrySignatureVerifier` (pure P-256/SHA-256 verify of `{name}@{version}:{integrity}`; a
      signature naming an UNPINNED key id is ignored, never trusted), `NpmIntegrity` (SRI parse/compare;
      SHA-1 refused outright), `AdapterProvenanceLevel` (the ordered per-adapter rung:
      `npm-build-provenance` > `npm-registry-signature` > `none`, declared in `adapters.starter.json` and
      MANDATORY — `AdapterManifest.Parse` throws `MissingProvenance` rather than defaulting, so nobody
      adds a CLI without stating what can be verified about it), `NpmProvenancePolicy` (the pure,
      fail-closed decision: signature under a pinned key → tarball must hash to the signed integrity → for
      the top rung a SLSA `https://slsa.dev/provenance/v1` attestation whose in-toto subject digest binds
      to these exact bytes; anything missing/malformed/mis-bound is `Refused`, and there is NO fallback to
      the self-derived hash), and the `INpmProvenanceGate`/`NpmProvenanceGate` +
      `INpmProvenanceSource`/`HttpNpmProvenanceSource` seams (the gate is the whole fetch+decide unit so
      the pinned key stays unreachable from outside it; an unreachable registry is a REFUSAL, not a pass).
      Wired at the one point that MOVES a pin (`ApplyUpdateAsync` → typed
      `AdapterChannelError.ProvenanceRejected`); `EnsureLatestAsync` refuses the registry's bytes and
      falls back to the REVIEWED bundled pin, loudly. **Measured 2026-07-26:** only `@openai/codex`
      publishes npm build provenance; claude-code / gemini-cli / qwen-code / opencode-ai 404 on the
      attestations endpoint, so they sit at `npm-registry-signature` — a statement about upstream, not a
      Mainguard gap. **Residual, stated in the manifest too:** the pin-override file is user-writable
      JSON, so a hand-written override still bypasses this gate — the UPDATE PATH is provenance-verified,
      adapter installs as a whole are not.)

- **`Services/`** — the three merge services that live in `Mainguard.Agents`, not `Mainguard.Git` (they are agent-platform concerns, and `ForegroundMergeService` is the human-gated one):
  - `IForegroundMergeService.cs` / `ForegroundMergeService.cs` — the P2-10 Windows-side, human-gated
    "Merge to Main" (the only path to `Merged`). Fetches the SC-2-resolved sync remote
    (`IAgentEnvironment.ResolveSyncRemote` — never a hardcoded literal), then merges `agent/<id>` onto
    main under the **A5 ref-level compare-and-swap** — `git merge --ff-only` IS the atomic CAS on
    `refs/heads/main` (a verified branch was keep-alive-rebased onto its main, so it fast-forwards; a
    moved main makes `--ff-only` refuse → the CAS loses → no merge → re-verify), **not** an
    `index.lock`-scoped read. A refused merge is CLASSIFIED rather than assumed
    (`ClassifyFailedFfMerge`): the old code discarded git's exit detail and reported every non-zero exit
    as "verification is stale" with `CasLost: true`, which is wrong — and expensively wrong — for
    `index.lock` contention, a refusing pre-merge hook, a full disk or unrelated histories, since it
    also told the queue to throw the verification away. It now asks
    `merge-base --is-ancestor main <source>`: still an ancestor ⇒ report git's own stderr with `CasLost`
    false, not an ancestor ⇒ the staleness message, earned; a probe that cannot answer ⇒ "cause could
    not be established". `ExternalPrMergeService` does the same at its local leg. Wrapped in one T-19
    `IOperationJournal` op (undoable/replayable — reuses the single journal, not a second one). The RT-D1 two-step conversation (`BeginMerge` lease →
    `PerformJournaledMerge` → `ConfirmMerge`); post-merge dependency refresh is **always
    `--ignore-scripts`** (poisoned lifecycle hooks never run) wrapped in NTFS `EPERM`/`EBUSY` retry.
  - `ExternalPrMergeService.cs` — the P2-12 **external** counterpart of that middle leg
    (`IExternalPrMergeExecutor`), for an `External` (intake'd upstream PR) entry: the merge happens **on
    the host**, never by fast-forwarding the mirrored `agent/pr-<n>` branch — that local ff would
    "succeed" while the PR stayed open upstream, which is divergent state that reads as correct from
    inside the app.
    - `IHostPullRequestGateway`/`HostPullRequestGateway` is the deliberately two-method host seam (read a
      PR, merge a PR — so this path structurally cannot close/comment/review, P2-12 invariant 1) over the
      ONE audited T-23 transport; it is also what lets tests drive a fake host instead of live GitHub.
      Order: local preconditions first (clean tree, host remote resolvable, HEAD on main, main still ==
      the lease's expected sha, verified head read from the sync mirror) **because an upstream merge
      cannot be taken back**; then the upstream state check (already merged / closed unmerged / draft /
      `mergeable_state` `dirty`=conflict, `blocked`=required checks, `behind`; head moved since
      verification ⇒ CasLost); then the merge under the host's own `sha` head-CAS; then the reconcile.
      **"Merged" requires all three: the host merged and named a commit, that commit is provably reachable
      from `<hostRemote>/main` after a fetch (`merge-base --is-ancestor` — git's word, not the API's), and
      local `refs/heads/main` fast-forwards onto it (journaled T-19).** Only then may the caller
      `ConfirmMerge`. A PR merged upstream that this checkout cannot converge onto records **nothing** and
      says so — the queue may only claim a merge it can point at a commit for; the intake prunes the entry
      on its next poll. Every host failure gets its own sentence off
      `GitOperationException.HostStatusCode`/`HostUnreachable` (401/403 permission, 403+rate-limit, 404,
      405 not-mergeable, 409 head-moved ⇒ CasLost, 5xx, unreachable), never a substring match on host
      prose. Holds **no** lease (MG-23: the one `IMergeLeaseStore` is the daemon's) and no gate (MG-11:
      `BeginMerge` enforced it).

## Role in the solution

- **`Mainguard.Agents`** (step 2b) — the **agent platform** built ON `Mainguard.Git`: orchestration, bootstrap/OOBE, adapters, daemon-side sandbox/container control (Docker.DotNet) + terminal/PTY (Porta.Pty), and the human-gated `Services/ForegroundMergeService`. No UI dependency; references `Mainguard.Git` and keeps Docker.DotNet + Porta.Pty.
  - `Agents/` (orchestration, `Bootstrap/`, `Adapters/`, `Orchestrator/`, `Sandbox/`, `Ipc/`, `Mock/`), `Daemon/`, `Terminal/`, and `Services/ForegroundMergeService`.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
