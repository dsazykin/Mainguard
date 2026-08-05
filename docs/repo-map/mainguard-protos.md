<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.Protos/` (P2-02 gRPC contract, codegen)

- **`protos/mainguard/v1/`** — the `mainguard.v1` proto surface (package name binding; opaque
  handles only, no OS paths — G-14).
  - `common.proto` (`Handle`/`Empty`), `agent.proto` (`AgentService`:
    `SpawnAgent`/`StopAgent`/`ListAgents`/`StreamAgentEvents` + the PR3 **`ListInstalledAdapters`** —
    the installed agent CLIs as `InstalledAdapterInfo{id,version,api_key_env_var}`, env-var NAMES only,
    never values; plus **`GetDaemonInfo`** — the tier-1 skew probe returning `daemon_version` (the
    Mainguard.Server assembly informational version) + `payload_version` (the `/etc/mainguardos-release`
    `MAINGUARDOS_VERSION` stamp, "" when absent), versions only, no paths (G-14) — a daemon that
    predates the RPC answers `Unimplemented`, which the client treats as the skew signal itself;
    `AgentEvent` is snapshot-then-deltas; `SpawnAgentRequest.model_api_key` is `// SECRET`; PR3 adds
    `SpawnAgentRequest.role`/`AgentInfo.role` — "", "coordinator", or "managed"), `terminal.proto`
    (`TerminalService.Attach` bidi; output frame `oneof { bytes raw; GridUpdate grid; }` **from day
    one** — so P2-18 was not a proto break. P2-18 fleshed the grid contract out: `AttachOptions` — the
    grid-capable attach handshake (`agent_id` alone stays the raw path, flag mismatches degrade safely);
    `GridUpdate` — per-16 ms-tick coalesced deltas or full snapshots, with run-length `CellRun` rows
    (`packed`/`glyphs`/`blanks` — blanks encode never-written cells, the selection-copy distinction),
    first-class `GridOp` scroll/pop ops, `GridCursor`, `GridModes`
    (alt-screen/bracketed-paste/DECCKM/mouse+SGR); `ClipboardCopy` — daemon-decoded OSC 52 SETs (queries
    never answered); and the `GetScrollback` RPC — lazy absolute-indexed scrollback pages for
    reattach/recovery/thin clients).
  - `egress.proto` (`EgressService`: List/Add/RemoveAllowlistHost — the App's only path to the
    daemon-owned default-deny allowlist (ESC-I2); Add re-renders the running proxy live, powering the
    Fix-2 unblock. `agent.proto`'s `StateChange` carries a `reason` (a Dead CLI's exit tail) so the App
    can run the egress block-detector on it), `reposync.proto` (`RepoSyncService`: provision/worktree
    RPCs — bodies implemented in P2-06; `ProvisionRepoRequest.credential_token` is `// SECRET`;
    `ProvisionRepoResponse` carries `sync_remote_name`/`sync_remote_url` — Windows-facing opaque
    handles, G-14 — so the App registers the resolved remote without touching `IAgentEnvironment`),
    `gateway.proto` (`GatewayService`: budgets + `StreamSpend` — bodies in P2-08), `mergequeue.proto`
    (P2-10 `MergeQueueService`: `StreamQueue` snapshot-then-deltas, `RunVerification`, `CanMerge`, and
    the RT-D1 `BeginMerge`/`ConfirmMerge` — no auto-merge RPC by construction; **P2-47 #7 adds
    `GetMergeDiff`** — the agent-branch-vs-main unified diff the review cockpit renders, which
    `StreamQueue` doesn't carry, parsed client-side by `PatchParser`; **`QueueEntry.flagged_items`**
    carries the daemon's must-acknowledge review items
    (`FlaggedItem{id,path,category,fact,acknowledged}`) — the gate that owns them is daemon-side and
    `AcknowledgeFlaggedChange` is addressed BY ITEM ID, so without them on the wire a flagged branch
    reaches the review surface with a refusal reason and no item to clear, which is a permanently
    unmergeable branch rather than a gate), `orchestrator.proto` (P2-14: `PlanApprovalService`
    `StreamPlans`/`ApprovePlan`/`RejectPlan` — **`ApprovePlanRequest` carries only `plan_id`; there is
    NO client approver/`osIdentity` field by design (SA-1/F2)**, the approver is daemon-derived — and
    `KillSwitchService` `Engage`/`Resume`; **P2-47 #9 adds `CoordinatorService`
    `StreamConversation`/`SendMessage`** — the coordinator chat bridge, snapshot-then-deltas
    conversation turns + a send-message RPC, carrying no merge/git/worktree capability).
  - `Mainguard.Protos.csproj` runs `Grpc.Tools` with `GrpcServices="Both"`.

## Role in the solution

- **`Mainguard.Protos`** (P2-02) — proto-first gRPC contract (`Grpc.Tools` codegen, `GrpcServices="Both"`); package `mainguard.v1`, consumed by Server (service bases) and App/Tests (client stubs). No hand-written code.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
