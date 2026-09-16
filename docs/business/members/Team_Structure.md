# Mainguard: Team Structure & Work Division

> **Status (July 2026):** Team scaling from 1 → 5–6. This document defines the workstream split, ownership seams, and the sequencing that lets pods run in parallel. It maps directly onto `Mainguard_Roadmap.md` and `Implementation_Plan.md`. Final per-person assignment happens after everyone completes `Team_Intake_Form.md`.

---

## 1. First Principle: Freeze the Contracts Before Splitting

The architecture is already cut along clean interfaces (`IAgentExecutor`, `ITerminalView`, `ISecureKeyStore`, the gRPC client-server split, the daemon-owns-SQLite rule). Parallel work only succeeds if the **inter-pod contracts are locked first**, in a ~1–2 week all-hands spike before pods diverge:

- **gRPC `.proto`** (daemon ⇄ Windows client) — the wall between backend and frontend.
- **`ITerminalView`** — the seam that lets the 7.1a → 7.1b terminal engine swap happen without touching ViewModels (see Implementation_Plan §2, step 3).
- **Orchestrator internal API** + the **agent lifecycle state machine** (spawn → cooperative-yield → keep-alive rebase → semantic verify → merge-queue → foreground merge → teardown), including the `[IPC_UPDATE_REQUESTED]` / `[IPC_UPDATE_READY]` triad.
- **`IAgentExecutor`** — the mock/real boundary that lets orchestration develop against a fake executor while the sandbox engine is still under construction.

Once frozen, three pods can build against mocks from day one.

---

## 2. Structure: Lead + 3 Pods

We deliberately avoid one-person-per-Phase silos (bus factor 1 on the riskiest infra). Pods are organized around the seams above.

### Lead / Architect (Daniel) — ~50% coding
- Owns the frozen contracts (§1), the **security architecture document + FSL licensing track** (Roadmap Phase 8.1), and cross-seam code review.
- Floats to whichever pod is on the critical path that month.
- Holds the whole-system model that no new hire has yet.

### Pod 1 — "Engine" (2 people)
The riskiest, most novel surface — staff it deep for bus factor 2.
- `MainguardOS` WSL2 bootstrapper; Docker hardening (userns, seccomp, `no-new-privileges`); Repo Provisioner (ext4 bare clone + `mainguard-vm` remote); gRPC server; `Docker.DotNet` lifecycle + zombie-swarm prevention.
- **AI Gateway, egress default-deny firewall, per-sandbox credential isolation** — launch-*blocking* security items (Roadmap Phase 7.2 / 8 launch tier), owned here, **not** deferred to a separate Phase 8 silo.
- **Needs:** the strongest Linux / containers / systems engineer.
- **Maps to:** Roadmap Phase 7.2; Implementation_Plan §1, §7.2.

### Pod 2 — "Swarm & UI" (2 people, tightly paired)
The orchestration state machine and the UI that surfaces it are two halves of one feature.
- **2A (backend):** Coordinator / Worker agents, `MergeQueue` + re-verification / staleness tracking, Cooperative Yield Protocol, plan-approval dry runs, Session Durability. **The merge-queue concurrency logic stays with one owner — do not split it.**
- **2B (Avalonia):** `Dock.Avalonia` sandbox layout, split Activity Bar + status micro-badges, 3-way merge UI, diff / staging panels, Phase 5 analytics polish. Develops against mock gRPC until Pod 1's server lands.
- **Maps to:** Roadmap Phases 4.4, 5, 7.3, 7.4, 7.5; Implementation_Plan §4, §5, §6, §7.3–7.5.

### Pod 3 — "Terminal" (1 specialist)
The terminal stack is a genuine specialist island and the *most* contract-isolated piece (behind `ITerminalView`, with its own conformance CI), so a solo owner is least dangerous here.
- `Porta.Pty` → `libvterm` P/Invoke bindings → VT conformance / golden-transcript replay harness → first-party Skia grid renderer.
- **Needs:** comfort with C interop / P/Invoke and systems programming.
- **Maps to:** Roadmap Phase 7.1 (7.1a–7.1d); Implementation_Plan §2, §7.1.

### 6th hire (if available)
Doubles up on Pod 3 (if strong on C interop) or backstops Pod 1 (containers/infra). Decide after intake forms.

---

## 3. Dependency Ordering (What Unblocks What)

1. **Weeks 1–2 — Contract freeze (all-hands).** Deliver the frozen `.proto`, `ITerminalView`, orchestrator API, and agent lifecycle state machine.
2. **Week 3+ — Parallel build:**
   - Pod 1 builds the real engine.
   - Pod 2A builds orchestration against a **mock `IAgentExecutor`**.
   - Pod 2B builds UI against **mock gRPC**.
   - Pod 3 builds terminal against a **real local PTY** (needs no daemon).
3. **Integration milestone:** Pod 1's real executor replaces the mock → Pod 2 goes live end-to-end; Pod 3 slots in behind `ITerminalView`.

---

## 4. Explicitly Not Staffed Yet

- **Phase 8 enterprise** (RBAC / SSO / SCIM, tamper-evident audit, SIEM export) — deferred. The launch-tier security subset already lives inside Pod 1.
- **Phase 9 Cloud Worktrees** — deferred; served by reusing the same gRPC contract later.
- **Vibe Mode** — post-v1. Note: the `VibeOrchestrator` is *shared* with the Coordinator, so Pod 2A should build for that reuse, not a throwaway.

---

## 5. Assignment Process

1. Every team member (including Daniel) fills out `Team_Intake_Form.md`.
2. Assign to pods by matching declared strengths/interests to the "Needs" line of each pod, prioritizing bus factor 2 on Pod 1 and a strong C-interop owner for Pod 3.
3. Confirm the contract-freeze spike owners before Week 1.
