# Status — what is built, and what is not

**This is the one place that answers "is X done?"** The binding spec
([`phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`](phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md))
defines 63 task headings but records status for almost none of them. That is why this page exists.

**Of the 62 real tasks (`P2-50` is a backlog heading, not a task): 19 shipped, 5 partial, 6 are a UI
prototype over an inert engine, and 32 have no implementing code.**

## How this was derived, and what that changes

Every row was checked **against the code** — files opened and read, not just matched by name. Three
rules came out of doing it, and they are why this page disagrees with the docs in places:

1. **A merged PR is not evidence.** `feature/P2-35-review-constraints` (#177) and
   `feature/P2-46-runtime-toolchain-resolver` (#171) both merged — and both contained **documentation
   only**. Both tasks are unbuilt. Branch names are worse than useless: stale `origin/feature/P2-*`
   branches exist for finished *and* unstarted work alike.
2. **A type existing is not a feature existing.** Several surfaces are declared and deliberately
   return nothing. They get their own section below rather than passing as shipped.
3. **The spec's own paths can be wrong.** `Mainguard.Agents/Security/`, `/Review/` and `/Audit/` do
   not exist; that code shipped under `Mainguard.Git/`. Following the spec to check three *finished*
   tasks finds nothing.

| Mark | Means |
|---|---|
| **Shipped** | Implemented, with the file or type named as proof. |
| **Partial** | Part is real; the missing part is stated explicitly. |
| **Prototype** | UI exists and renders, but the engine behind it is inert. No behaviour. |
| **Planned** | No implementing code found. |

## Shipped — 19

| Task | What | Evidence | Caveat |
|---|---|---|---|
| P2-01 | BYOK key store + key health | `Mainguard.Git/Security/ApiKeyHealthService.cs`, `ISecureKeyStore.cs`, `SecureKeyring.cs`, `CredentialInjector.cs` | — |
| P2-02 | Daemon + gRPC v1 contract | `Mainguard.Server/DaemonHost.cs`, `Auth/BearerTokenInterceptor.cs`, `Logging/SecretFieldMask.cs`, `Mainguard.Protos/protos/mainguard/v1/` | — |
| P2-03 | Terminal engine (interim PTY) | `Agents/Terminal/VtBoundaryDetector.cs`, `PtyProcessShim.cs`, `Mainguard.Server/Terminal/TerminalStreamer.cs` | Renderer written from scratch, not vendored |
| P2-05 | MainguardOS bootstrapper | `Agents/Bootstrap/MainguardOsBootstrapper.cs`, `WslConfigMerger.cs`, `FirstBootStep.cs` | — |
| P2-06 | Repo provisioner | `Agents/RepoProvisioner.cs`, `WorktreeManager.cs` (1297 ln), `Mainguard.Server/Services/RepoSyncGrpcService.cs` | — |
| P2-07 | Sandbox hardening + default-deny egress | `Sandbox/DockerSandboxEngine.cs` (1563 ln), `EgressProxyConfigurator.cs` (1581 ln), `SeccompProfile.cs`, `UsernsRemapPolicy.cs` | — |
| P2-08 | AI gateway + swarm reconciler | `Agents/AiGateway.cs`, `TokenBucket.cs`, `BudgetLedger.cs`, `AdmissionController.cs`, `SwarmReconciler.cs` (522 ln) | Live-provider smoke deferred |
| P2-09 | Agent lifecycle + keep-alive rebase | `Orchestrator/YieldProtocol.cs`, `KeepAliveRebaser.cs`, `SessionLeader.cs`, `GitMutationGuard.cs` | — |
| P2-10 | Merge queue + stale invalidation | `Orchestrator/MergeQueue.cs` (2448 ln), `VerificationRunner.cs`, `MergeReconcileTask.cs` | — |
| P2-11 | Review cockpit | `Mainguard.Git/Review/RiskClassifier.cs`, `ProvenanceReader.cs`, `LockfileReview.cs`, `FlaggedChangeDetector.cs` | "Review-sprint mode" extension not built |
| P2-12 | External agent PR intake | `Orchestrator/ExternalPrIntake.cs` (497 ln), `PrIntakeStore.cs`, `Mainguard.Server/Runtime/PrIntakeHostedService.cs` | Private-repo credential injection is an open TODO |
| P2-13 | Activity bar & docking | `ControlCenterViewModel.cs` (1752 ln), `AgentRailViewModel.cs`, `Headless/DockTeardownMemoryTests.cs` | — |
| P2-14 | Plan approval + orchestration | `Orchestrator/PlanApprovalService.cs` (1578 ln), `CoordinatorAgent.cs`, `KillSwitch.cs`, `Mainguard.Server/Auth/ApproverIdentityResolver.cs` | Plans are now **worker**-authored, not coordinator-authored |
| P2-15 | Tamper-evident audit log | `Mainguard.Git/Audit/` — `HashChain`, `ChainedAuditLog`, `Rfc3161Anchor`, `AuditCrypto`; `Mainguard.Server/Services/AuditGrpcService.cs` | `mainguardd audit replay` absent |
| P2-18 | libvterm engine + Skia grid renderer | `Agents/Terminal/Vterm/VtermSession.cs`, `TerminalGridControl.cs` | **Still defaults to Interim** behind `TerminalEngine=`; OSC 8 gap |
| P2-21 | Installer part 1 | `Bootstrap/SystemDiagnostics.cs`, `OobeStateMachine.cs`, `build/mainguardos/build.sh` | Fresh-machine matrix stays manual |
| P2-22 | Installer part 2 | `Bootstrap/WindowsIntegration.cs`, `Mainguard.Git/Security/LoopbackOAuthListener.cs`, `Adapters/AdapterChannel.cs` | — |
| P2-47 | Alpha integration pass | `Mainguard.Server.Tests/AlphaLoopSmokeTests.cs`, `CompositionRootResolutionTests.cs`, `ControlCenterLiveWiringTests.cs` | Spawn→verify→merge leg is a manual runbook |
| P2-48 | Unified installed experience | `OobeWizardViewModel.cs`, `Services/LaunchRouter.cs`, `build/velopack/pack.ps1` | Windows-first; macOS uses `MacOobeWindow` |

## Partial — 5

| Task | Built | Not built |
|---|---|---|
| P2-04 | The harness — `Mainguard.Tests/Terminal/VtConformanceTests.cs`, committed `.golden` transcripts, the shrink-only allowlist and its CI guard | Real `vttest` / `esctest` binaries are **never driven**; hand-written fixtures stand in, which the test's own header declares a follow-up. |
| P2-17 | A log **tee** — `Mainguard.Server/Logging/LoggingTransparencyLog.cs`, `Sandbox/NetworkTransparencyLog.cs`, recording for `DaemonGitProxy` | No transparency view, no stream RPC (`egress.proto` has allowlist RPCs only). The "source-available" half is a doc section — `LICENSE` is still Apache-2.0 with no CI check. The log's own comment calls it the *pre*-P2-17 seam. |
| P2-24 | The lockfile-delta half — `Mainguard.Git/Review/LockfileSemanticDiff.cs` | **Both of its own deliverables are at zero**: no SPDX/license gate, and no Vault or AWS Secrets Manager `ISecureKeyStore` backend. (`NpmProvenance.cs` is real but belongs to P2-22/P2-49, not here.) |
| P2-38 | An **emit-only** seam — `ViewedStateEvent`, whose comment reads "emitted now; P2-38 consumes later" | In-memory, keyed `Path`+`HunkIndex` not patch-id, unaudited. No inline-comments→agent round trip. |
| P2-49 | The user-owned update model — `Adapters/AgentCliUpdateService.cs`, pin overrides | **Both headline MUSTs fail.** Revert re-fetches the tarball via `AdapterChannel.EnsureAsync`, so it needs the network *and* the registry still serving the old version. No versioned-directory layout, no N-1 retention. |

## Prototype — UI exists, engine is inert — 6

These render, and read as done if you look for the type name. Only `MockOrchestrator` supplies
behaviour; the real `DaemonBackedOrchestrator` returns empty.

| Task | UI that exists | Engine |
|---|---|---|
| P2-39 | `AgentDocumentViewModel` queued-prompt strip | `GetQueuedPrompts` / `CancelQueuedPromptAsync` / `GetPlanTree` inert. Prompt-first dispatch and session search absent entirely. |
| P2-44 | `TelemetryPanelViewModel` + `TelemetryPanelView.axaml`, harness-rendered | `GetSandboxEvents` → `Array.Empty`. No `SandboxEvent` in the protos or daemon at all. |
| P3-01 | — | `GetCheckpoints`/`LastVerifiedGreen`/`RestoreCheckpointAsync` marked in source "intentionally inert". No daemon checkpoint service. |
| P3-02 | `VibeModeViewModel` triage screen, built **to spec**: three commands, honest-disabled `CanGoBack`, details expander, ≥3-escalation copy | The trip is a prototype `SimulateSnag`; "Get help" only adds a card — no diagnostic bundle, no audit events. Reachable from no menu. |
| P3-03 | `VibeModeViewModel` + `VibeModeView.axaml` | `IVibeService` inert. No LivePreview control — zero WebView2/CefGlue in the repo. |
| P3-04 | Publish/deploy UI, `DeployPhase`/`DeployStatus` | No provider layer: no `IDeployProvider`, no Vercel/Netlify clients. The only "vercel" string is a fake URL in `MockOrchestrator`. |

## Planned — no implementing code — 32

P2-16 SIEM · P2-19 conflict radar · P2-20 commit curation · P2-23 RBAC/SSO/SCIM · P2-25 cloud
worktrees · P2-26 VibeOrchestrator · P2-27 ticket→PR · P2-28 multi-repo tasks · P2-29 session board ·
P2-30 automations · P2-31 dispatcher · P2-32 SDK/MCP/webhooks · P2-33 dev-server preview ·
P2-34 context vault · **P2-35 verification depth** · P2-36 governed lessons · P2-37 session
checkpoints · P2-40 composer conveniences · P2-41 remote dashboard · P2-42 merge-train simulation ·
P2-43 agent signing keys · P2-45 flight recorder · **P2-46 toolchain resolver** ·
P2-C1/C2/C3 client parity · P2-C4 split & restack · P2-C5 client polish ·
P3-05 Mainguard Web · P3-06 cloud worktrees impl · **P3-07 host parity** · P3-08 skills marketplace ·
P3-09 CI/CD janitor · P3-10 team collaboration

Four of these will look built to a search and are not:

- **P2-35** and **P2-46** have merged PRs (#177, #171) that were documentation only.
- **P2-46** — the large `Toolchain*` subsystem is real, but it is per-repo `.mainguard/toolchain`
  declarations and curated channel installs, *not* P2-46's agent-initiated `mainguard tool add`.
  `security-architecture.md` still lists P2-46 as an open residual.
- **P3-07** — `GitLabProvider.cs` is OAuth **token acquisition only** (a P2-22 deliverable). Every
  GitLab/Bitbucket/Azure DevOps PR, issue and check provider derives from an `Unsupported*Provider`
  base whose methods throw, with `IsImplemented => false`.
- **P2-23** — `Mainguard.Server/Auth/RoleInterceptor.cs` is a substantial default-deny allowlist, but
  it is P2-14's coordinator-vs-operator *token* role. No user identity, no IdP, no SCIM.
- **P2-37** — `OperationJournal`'s "snapshot" is the shipped T-19 **ref** snapshot: precisely the
  clean-tree-only limitation this task exists to remove.

## Keeping this true

1. **A PR that finishes a task updates its row here, in the same PR** — the same rule as
   [`repo-map/`](repo-map/README.md). A status change without a row change is an incomplete change.
2. **Evidence, not assertion.** A row moves to Shipped when you can name the file or type that
   implements it. A merged PR does not qualify, as #177 and #171 demonstrate.
3. When a task ships, move its plan doc to [`archive/`](archive/README.md) with an `ARCHIVED` banner.
