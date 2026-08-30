<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.Protos/` (P2-02 gRPC contract, codegen)

- **`protos/mainguard/v1/`** — the `mainguard.v1` proto surface (package name binding; opaque
  handles only, no OS paths — G-14).
  - `common.proto` (`Handle`/`Empty`), `agent.proto` (`AgentService`:
    `SpawnAgent`/`StopAgent`/`ListAgents`/`StreamAgentEvents`; **`ResumeAgent`** gives a STRANDED
    merge-queue entry a live jail again — a spawn onto the agent id that entry ALREADY has, with the
    worktree standing on its existing `agent/<id>`. A separate RPC rather than an `agent_id` field on
    `SpawnAgentRequest` for two enforcement reasons: `RoleInterceptor` denies by METHOD (a field inside a
    shared message cannot be put on the coordinator's denied list), and `ResumeAgentRequest` carries no
    `role`, so a resume structurally cannot mint a coordinator. Like `DiscardEntryRequest` it carries no
    actor either. A refusal is `resumed=false` + a verbatim reason on a successful RPC, never a status
    code, so "no exception" is not evidence a jail exists; **`PauseAgent`/`UnpauseAgent`** — the human
    per-agent pause (docker pause on the jail; NOT containment — no terminal lock, one agent,
    recoverable): "Unpause" because `ResumeAgent` is the adoption above; a human pause is STICKY (the
    cascade's yield runs through a frozen jail but never wakes it) and a human unpause DEFERS to an
    in-flight machine hold with a self-clearing refusal (`HumanPauseLedger`); refusal-as-response, no
    actor field, both on the coordinator's denied list; + the PR3 **`ListInstalledAdapters`** —
    the installed agent CLIs as `InstalledAdapterInfo{id,version,api_key_env_var}`, env-var NAMES only,
    never values; plus **`GetDaemonInfo`** — the tier-1 skew probe returning `daemon_version` (the
    Mainguard.Server assembly informational version) + `payload_version` (the `/etc/mainguardos-release`
    `MAINGUARDOS_VERSION` stamp, "" when absent), versions only, no paths (G-14) — a daemon that
    predates the RPC answers `Unimplemented`, which the client treats as the skew signal itself;
    `AgentEvent` is snapshot-then-deltas; `SpawnAgentRequest.model_api_key` is `// SECRET`; PR3 adds
    `SpawnAgentRequest.role`/`AgentInfo.role` — "", "coordinator", or "managed"; the CLI **login**
    round-trip's `CliCredentialFile{path,content}` (`// SECRET`) on
    `SpawnAgentRequest`/`StopAgentResponse`/`HarvestAgentCredentialsResponse`; and the CLI **settings**
    round-trip's `CliSettingsFile{root,path,content}` on the same three, where `root` is `"home"` or
    `"workspace"` (a CLI keeps user- and project-level config in two trees, both wiped every spawn, and
    the workspace one holds "don't ask again" grants) — NOT secrets and not keychain-backed: the durable
    copy is a per-repo JSON file. Those responses also carry `repo_handle` (the same opaque handle the
    client supplied on spawn), because settings are stored per repository and the client's harvest sweep
    walks every agent on the daemon), `terminal.proto`
    (`TerminalService.Attach` bidi; output frame `oneof { bytes raw; GridUpdate grid; }` **from day
    one** — so P2-18 was not a proto break. P2-18 fleshed the grid contract out: `AttachOptions` — the
    grid-capable attach handshake (`agent_id` alone stays the raw path, flag mismatches degrade safely);
    `GridUpdate` — per-16 ms-tick coalesced deltas or full snapshots, with run-length `CellRun` rows
    (`packed`/`glyphs`/`blanks` — blanks encode never-written cells, the selection-copy distinction),
    first-class `GridOp` scroll/pop ops, `GridCursor`, `GridModes`
    (alt-screen/bracketed-paste/DECCKM/mouse+SGR); `ClipboardCopy` — daemon-decoded OSC 52 SETs (queries
    never answered); and the `GetScrollback` RPC — lazy absolute-indexed scrollback pages for
    reattach/recovery/thin clients).
  - `audit.proto` (P2-15: `AuditService` — `VerifyAudit` (chain + mirror walked daemon-side; head
    seq/hash; `persistent=false` flags the in-memory fallback journal so a heap verify can never
    pass as tamper-evidence) and `ReadAudit` (paged decrypted canonical envelopes) — the audit
    store's first production readers; both RPCs coordinator-denied at the RoleInterceptor, and
    redaction/retention deliberately have NO RPC).
  - `egress.proto` (`EgressService`: List/Add/RemoveAllowlistHost — the App's only path to the
    daemon-owned default-deny allowlist (ESC-I2); Add re-renders the running proxy live, powering the
    Fix-2 unblock. `agent.proto`'s `StateChange` carries a `reason` (a Dead CLI's exit tail) so the App
    can run the egress block-detector on it), `reposync.proto` (`RepoSyncService`: provision/worktree
    RPCs — bodies implemented in P2-06; `ProvisionRepoRequest.credential_token` is `// SECRET`;
    `ProvisionRepoResponse` carries `sync_remote_name`/`sync_remote_url` — Windows-facing opaque
    handles, G-14 — so the App registers the resolved remote without touching `IAgentEnvironment`),
    `gateway.proto` (`GatewayService`: budgets + `StreamSpend` — bodies in P2-08), `mergequeue.proto`
    (P2-10 `MergeQueueService`: `StreamQueue` snapshot-then-deltas, `RunVerification`, `CanMerge`, and
    the RT-D1 `BeginMerge`/`ConfirmMerge` — no auto-merge RPC by construction;
    **`BeginMergeResponse.expected_branch_sha`** (K3/§23.4) travels beside `expected_main_sha` and for the
    same reason: it is the `agent/<id>` tip the daemon's verification was measured on, so the client merges
    the branch the daemon authorized rather than whatever its own stream snapshot last saw, and
    `ConfirmMerge` can check the post-merge sha it reports against it; **P2-47 #7 adds
    `GetMergeDiff`** — the agent-branch-vs-main unified diff the review cockpit renders, which
    `StreamQueue` doesn't carry, parsed client-side by `PatchParser`; **`QueueEntry.flagged_items`**
    carries the daemon's must-acknowledge review items
    (`FlaggedItem{id,path,category,fact,acknowledged}`) — the gate that owns them is daemon-side and
    `AcknowledgeFlaggedChange` is addressed BY ITEM ID, so without them on the wire a flagged branch
    reaches the review surface with a refusal reason and no item to clear, which is a permanently
    unmergeable branch rather than a gate; **H2/H4 add the entry's last VERDICT and a way to read its
    output** — `QueueEntry.last_verification_passed` (**`optional`** for the same reason
    `has_live_sandbox` is: a proto3 `bool` defaults to false, so a plain field would make "never verified"
    and "failed" the same value again, which is the very defect the new `VerificationFailed` state fixes
    one layer up), `last_verification_command`, `last_verification_at`, and the **`GetVerificationLog`**
    rpc returning the artifact's CONTENT (never its daemon path — G-14), tail-bounded with an explicit
    `truncated`, and distinguishing "no record" from "a record whose artifact is gone" from "the log"; **`DiscardEntry`/`RejectEntry`/`ClearStalledVerification`** are the human
    entry-lifecycle RPCs — `DiscardEntry` walks an entry to the terminal `Discarded` (never `Merged`; it
    takes no lease, fires no cascade and writes no T-19 journal entry, so `NoAutoMergePathExists` is
    untouched); `RejectEntry` is the review verdict "no" — terminal `Rejected`, legal only from
    `Verified`/`AwaitingReview` (an unverified entry is refused toward Discard), and unlike a discard
    the rejected row STAYS on the queue stream as its terminal so the verdict is never silently lost;
    both, like `ApprovePlanRequest`, carry **no actor field** — the acting identity is
    daemon-derived, because an attribution the client fills in is one any token-holder can forge; all
    three RPCs are on the coordinator's denied list at `RoleInterceptor`. **`QueueEntry.has_live_sandbox`**
    (`optional`) says whether the entry still HAS a jail — the fact that decides whether it is workable at
    all, since verification runs only in the worker's own sandbox — so the rail can offer Resume on a
    stranded row and withhold Verify instead of leaving an enabled button whose only behaviour is an
    error. `optional` for the same reason `AgentResourceReading.cpu_percent` is: a proto3 `false` meaning
    "this daemon does not report liveness" is indistinguishable from "this entry's jail is gone", which
    would render every entry of an older daemon as stranded. **`QueueEntry.verification_in_flight`**
    says whether a run is really executing, which the state alone cannot: state is persisted per
    transition while the in-flight set is daemon memory, so a restart mid-run leaves a `Verifying` row
    with nothing behind it and a client that inferred "Verifying ⇒ busy" would be wrong for exactly the
    entries that need unsticking. **`mergequeue.proto` also carries `PrIntakeService`** — the P2-12
    external-PR-intake configuration surface, hosted here because the intake is the queue's other feeder:
    `GetPrIntakeSettings` (settings + persisted `PrIntakeSource` list), `UpdatePrIntakeSettings` and
    `SubscribePrIntakeSource`. Deliberately NO `repo_handle` on any of them — intake config is
    daemon-wide, and a subscription names an UPSTREAM `(host, owner, repo)` the daemon matches against
    the repos it has provisioned at poll time. `UpdatePrIntakeSettings` echoes what was PERSISTED
    (clamped cadence, defaulted bot list), never the request, so a client that renders the reply shows
    what the poller actually runs on. Both writes are on the coordinator's denied list at
    `RoleInterceptor`: subscribing is a provisioning act — it makes the daemon fetch PR heads and ask the
    gated spawn chain for a jail per open bot PR — so an agent able to call it could manufacture queue
    entries and jails, or widen the bot-author list until its own PRs were intake'd),
    `queueseeding.proto` (**dev-only `QueueSeedingService`** —
    `SeedQueueEntries`/`PushCommits`/`ClearSeededEntries`/`GetSeedingStatus`, the merge-queue
    seeding surface of `docs/design/queue-seeding.md`: legitimate entries in any `WorkerMergeState`
    produced by driving the REAL state-machine methods with synthetic input. Mapped by the daemon
    ONLY when `MAINGUARD_ENABLE_QUEUE_SEEDING=1` was set at process startup (disabled ⇒
    `UNIMPLEMENTED`, plus a `SeedingGateInterceptor` prefix-deny as the belt), and every method is
    on the coordinator's denied list unconditionally. Ids are daemon-assigned `seed-<n>` — the
    prefix is the clear-scope boundary; no actor fields (daemon-derived identity); refusals are
    per-entry verbatim strings, never status codes. Fields 8/9 of `SeedEntrySpec` are the coordinator
    phase-2/3 plan dimension the compat contract reserved them for: `with_plan` drives the REAL
    `WorkerPlanGate.Hold` → `PlanApprovalService.Present` → approve walk for the synthetic id, and
    `scope` is the approved `TaskPlan.Scope` — empty meaning "the seed's own path", anything else
    arming the real out-of-approved-scope must-ack item),
    `orchestrator.proto` (P2-14: `PlanApprovalService`
    `StreamPlans`/`ApprovePlan`/`RejectPlan` — **`ApprovePlanRequest` carries only `plan_id`; there is
    NO client approver/`osIdentity` field by design (SA-1/F2)**, the approver is daemon-derived — and
    `KillSwitchService` `Engage`/`Resume`; **P2-47 #9 adds `CoordinatorService`
    `StreamConversation`/`SendMessage`** — the coordinator chat bridge, snapshot-then-deltas
    conversation turns + a send-message RPC, carrying no merge/git/worktree capability.
    **Phase 2** (worker-authored plans): `PlanEntry` gains `worker_agent_id` (the plan is the
    *worker's* now — it wrote it after inspecting the repo), `revision`, `revisions_remaining` and
    `rejection_feedback`; `RejectPlanRequest.reason` becomes load-bearing — it is **delivered to the
    worker** as the feedback it revises against — and `RejectPlanResponse` gains `escalated` +
    `revisions_remaining`, because a bare `rejected: true` could not distinguish "the worker will
    revise" from "the worker has stopped". `PlanUpdate` gains the **backpressure** block
    (`blocked_worker_count`, `escalated_worker_count`, `active_worker_count`, `max_active_workers`,
    `max_plan_revisions`, `backpressure_signal`) — a blocked worker counts against the worker cap, so a
    saturated cap means the coordinator has stopped spawning, and the contract makes saying so a
    requirement rather than a nicety).
  - `Mainguard.Protos.csproj` runs `Grpc.Tools` with `GrpcServices="Both"`.

## Role in the solution

- **`Mainguard.Protos`** (P2-02) — proto-first gRPC contract (`Grpc.Tools` codegen, `GrpcServices="Both"`); package `mainguard.v1`, consumed by Server (service bases) and App/Tests (client stubs). No hand-written code.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
