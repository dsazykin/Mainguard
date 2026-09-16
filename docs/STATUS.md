# Status

**19 shipped · 5 partial · 6 prototype · 32 planned** — of 62 tasks in the
[master doc](phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md), which
does not itself record status. Plus [4 open items](#open-items-outside-the-task-list) that are not
tasks at all.

`Shipped` implemented · `Partial` some of it is real · `Prototype` UI renders, engine returns
nothing · `Planned` no code.

Verified by reading the code. A merged PR is **not** evidence — see [Traps](#traps).

## Tasks

| Task | | Status | Evidence, or what is missing |
|---|---|---|---|
| P2-01 | BYOK key store | Shipped | `Mainguard.Git/Security/ApiKeyHealthService.cs`, `SecureKeyring.cs` |
| P2-02 | Daemon + gRPC contract | Shipped | `Mainguard.Server/DaemonHost.cs`, `Mainguard.Protos/protos/mainguard/v1/` |
| P2-03 | Terminal, interim PTY | Shipped | `Agents/Terminal/VtBoundaryDetector.cs`, `PtyProcessShim.cs` |
| P2-04 | VT conformance harness | Partial | Harness + goldens ship. Real `vttest`/`esctest` never driven; hand-written fixtures stand in |
| P2-05 | MainguardOS bootstrapper | Shipped | `Bootstrap/MainguardOsBootstrapper.cs`, `WslConfigMerger.cs` |
| P2-06 | Repo provisioner | Shipped | `Agents/RepoProvisioner.cs`, `WorktreeManager.cs` |
| P2-07 | Sandbox + default-deny egress | Shipped | `Sandbox/DockerSandboxEngine.cs`, `EgressProxyConfigurator.cs`, `SeccompProfile.cs` |
| P2-08 | AI gateway + reconciler | Shipped | `Agents/AiGateway.cs`, `BudgetLedger.cs`, `SwarmReconciler.cs` |
| P2-09 | Agent lifecycle, keep-alive | Shipped | `Orchestrator/YieldProtocol.cs`, `KeepAliveRebaser.cs` |
| P2-10 | Merge queue | Shipped | `Orchestrator/MergeQueue.cs`, `VerificationRunner.cs` |
| P2-11 | Review cockpit | Shipped | `Mainguard.Git/Review/RiskClassifier.cs`, `ProvenanceReader.cs`. Review-sprint mode not built |
| P2-12 | External PR intake | Shipped | `Orchestrator/ExternalPrIntake.cs`, `PrIntakeHostedService.cs`. Private-repo creds are an open TODO |
| P2-13 | Activity bar & docking | Shipped | `ControlCenterViewModel.cs`, `AgentRailViewModel.cs` |
| P2-14 | Plan approval | Shipped | `Orchestrator/PlanApprovalService.cs`, `KillSwitch.cs`. Plans are **worker**-authored |
| P2-15 | Tamper-evident audit log | Shipped | `Mainguard.Git/Audit/` — `HashChain`, `Rfc3161Anchor`. `audit replay` absent |
| P2-16 | SIEM exporter | Planned | — |
| P2-17 | Network transparency | Partial | Log tee only (`LoggingTransparencyLog.cs`). No view, no stream RPC. `LICENSE` still Apache-2.0 |
| P2-18 | libvterm engine | Shipped | `Terminal/Vterm/VtermSession.cs`. **Still defaults to Interim** behind `TerminalEngine=` |
| P2-19 | Conflict radar | Planned | — |
| P2-20 | Commit-stream curation | Planned | — |
| P2-21 | Installer 1: diagnostics | Shipped | `Bootstrap/SystemDiagnostics.cs`, `OobeStateMachine.cs` |
| P2-22 | Installer 2: Windows | Shipped | `Bootstrap/WindowsIntegration.cs`, `LoopbackOAuthListener.cs` |
| P2-23 | RBAC / SSO / SCIM | Planned | `RoleInterceptor.cs` is P2-14's token role — no user identity, IdP or SCIM |
| P2-24 | Supply-chain + secrets | Partial | Lockfile diff only. **No SPDX gate, no Vault/AWS key backend** |
| P2-25 | Cloud worktrees, guardrails | Planned | — |
| P2-26 | VibeOrchestrator engine | Planned | — |
| P2-27 | Ticket → PR pipeline | Planned | — |
| P2-28 | Multi-repo tasks | Planned | — |
| P2-29 | Session board | Planned | — |
| P2-30 | Automations & scheduling | Planned | — |
| P2-31 | Dispatcher, multi-candidate | Planned | — |
| P2-32 | SDK / MCP / webhooks | Planned | — |
| P2-33 | Dev-server preview | Planned | — |
| P2-34 | Context vault | Planned | — |
| P2-35 | Verification depth | Planned | PR #177 merged **docs only**. No `DiffGuard`, no repair loop |
| P2-36 | Governed lessons | Planned | — |
| P2-37 | Session checkpoints | Planned | `OperationJournal`'s snapshot is the T-19 **ref** snapshot — the limitation this task removes |
| P2-38 | Review loop-closers | Partial | Emit-only `ViewedStateEvent`; in-memory, unaudited. No comments → agent |
| P2-39 | Orchestration UX pack | Prototype | Queued-prompt UI over inert seams. Dispatch + search absent |
| P2-40 | Composer conveniences | Planned | — |
| P2-41 | Remote dashboard | Planned | — |
| P2-42 | Merge-train simulation | Planned | — |
| P2-43 | Agent signing keys | Planned | `SigningKey` is the per-profile T-15 column, not per-agent |
| P2-44 | Sandbox health panel | Prototype | `TelemetryPanelViewModel` renders; `GetSandboxEvents` → empty, no `SandboxEvent` in protos |
| P2-45 | Flight recorder | Planned | 512 KiB in-memory replay ring is re-attach only |
| P2-46 | Toolchain resolver | Planned | PR #171 merged **docs only**. The `Toolchain*` subsystem is per-repo declarations, not `tool add` |
| P2-47 | Alpha integration | Shipped | `AlphaLoopSmokeTests.cs`, `ControlCenterLiveWiringTests.cs`. Full leg is a manual runbook |
| P2-48 | Unified install + OOBE | Shipped | `OobeWizardViewModel.cs`, `LaunchRouter.cs`. Windows-first |
| P2-49 | Agent-CLI lifecycle | Partial | **Revert needs the network** and the registry still serving the old version. No N-1 retention |
| P2-C1/2/3 | Client parity | Planned | Command palette exists as a prerequisite only |
| P2-C4 | Split & restack | Planned | — |
| P2-C5 | Client polish pack | Planned | — |
| P3-01 | Auto-checkpoints | Prototype | `IVibeService` marked in source "intentionally inert" |
| P3-02 | Escalation UX | Prototype | Triage screen built to spec; trip is `SimulateSnag`, reachable from no menu |
| P3-03 | Vibe UI | Prototype | UI real, engine inert. No LivePreview — zero WebView2/CefGlue in repo |
| P3-04 | One-click deploy | Prototype | Deploy UI + `DeployPhase`; no provider layer at all |
| P3-05 | Mainguard Web | Planned | — |
| P3-06 | Cloud worktrees impl | Planned | — |
| P3-07 | Host parity | Planned | `GitLabProvider.cs` is OAuth token acquisition. PR providers throw, `IsImplemented => false` |
| P3-08 | Skills marketplace | Planned | — |
| P3-09 | CI/CD janitor | Planned | — |
| P3-10 | Team collaboration | Planned | — |

## Open items outside the task list

Real work, named inside other docs rather than as a `P2-*` task. Listed here so it is not buried.

| Item | State | Source |
|---|---|---|
| **App/Server version-lockstep CI guard** | **Unbuilt, and untracked.** No guard exists in `.github/workflows/` or `build/ci/`. The rule it would enforce is *binding* and fails silently: both update tiers deploy on version comparison, not content, so a daemon fix at an unchanged version is never deployed. Review is the only guard. | [mainguardos-updates](mainguardos-updates.md#versioning-discipline-binding) |
| **`DEBIAN_SNAPSHOT` cadence has lapsed** | Pinned at `20250601T000000Z`; the line has not moved since 2026-07-14, against a documented first-week-of-month bump. Missed security-snapshot advances. | [mainguardos-updates](mainguardos-updates.md#cadence) |
| **Host-un-forgeable presence factor** | Deferred (OPS §10.1). The mTLS residual: a same-uid process can read `daemon-client.pfx` and impersonate the client. | [security-architecture](security-architecture.md#residuals) |
| **Approver identity from the client certificate** | Unblocked but not done — the connection now carries a genuine peer credential; deriving approver identity from it was never implemented. | [security-architecture](security-architecture.md#residuals) |

Two further items there are **accepted risks, not work**: the F5 package-proxy exfiltration channel,
and MG-33 (`kernel.yama.ptrace_scope` is non-namespaced, so it affects the user's other WSL distros).
Both are stated deliberately — read them before claiming the sandbox is airtight.

## Traps

Things that look done and are not:

- **A merged PR is not evidence.** #177 (P2-35) and #171 (P2-46) merged documentation only.
  `origin/feature/P2-*` branches exist for finished *and* unstarted work — ignore branch names.
- **A type is not a feature.** The six `Prototype` rows have real UI over engines returning
  `Array.Empty` / `null` / `Task.CompletedTask`. `MockOrchestrator` is the only implementation.
- **Null objects are not stubs.** `UnboundAgentControlChannel` and `MergeReconcilePlaceholderTask`
  return `Task.CompletedTask` by design; the real paths are `AgentIpcServer.cs` and
  `MergeReconcileTask.cs`.

Things that look missing and are not — the code diverges from the spec deliberately:

| Spec says | Code does |
|---|---|
| `Mainguard.Agents/Security\|Review\|Audit` | All under **`Mainguard.Git/`**. Those paths do not exist (P2-01, P2-11, P2-15) |
| Vendored renderer under `external/` | Written from scratch — `VtScreen.cs`, `TerminalControl.cs` |
| Coordinator authors the plan | Worker authors it — `WorkerPlanAuthor.cs`, `WorkerPlanGate.cs` |
| Ref-level CAS for merge freshness | `git merge --ff-only` |

## Updating this

A PR that finishes a task updates its row **in the same PR** — same rule as
[`repo-map/`](repo-map/README.md). A row moves to Shipped only when you can name the file that
implements it. Move the task's plan doc to [`archive/`](archive/README.md) at the same time.
