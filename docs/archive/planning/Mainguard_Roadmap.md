# Mainguard: Technical Roadmap & Architecture Blueprint

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

Mainguard is a premium, cross-platform desktop **Git GUI & Multi-Agent Control Center** built natively in C# and Avalonia UI. It serves as a command-and-control dashboard where developers manage complex Git workflows alongside multiple autonomous AI agents (Claude Code, AGY CLI, OpenCode) running concurrently in isolated, sandboxed Git worktrees.

> **Revision note (July 2026):** This document was revised after verifying the original architecture against official documentation. Key changes: the "Hollow-Core" 9P bind-mount design is replaced by a Git-native sync boundary (Git's builtin fsmonitor has no Linux backend and inotify does not propagate over 9P mounts); the nested "sbx inside WSL2" engine is replaced by raw Docker Engine in WSL2 for v1 (Docker Sandboxes run natively on Windows Hypervisor Platform, not nested); interactive rebase moves from LibGit2Sharp to the git CLI (unsupported in libgit2); AF_VSOCK is demoted to a research fallback (mirrored networking solves the VPN problem); licensing is locked as source-available (FSL) backend + commercial GUI; scale claims are corrected to realistic hardware limits with Cloud Worktrees as the growth path.

---

## 1. Core Vision & Architectural Goals

- **The Swarm Coordinator:** A dual-mode orchestration system allowing the user to either manually pilot multiple agents, or delegate tasks to a "Lead Agent" that automatically spawns and manages worker sub-agents. Workers execute only **human-approved plans** (see Phase 7.5) and merge only through **human-gated foreground merges**.
- **Client-Server Split Architecture:** The Avalonia UI runs as a native Windows client, communicating with a headless `.NET` daemon running inside a dedicated `MainguardOS` WSL2 distribution. Transport is **gRPC over localhost** using WSL2 mirrored networking (Windows 11 22H2+) with per-session token authentication; `dnsTunneling`/`autoProxy` handle VPN environments. AF_VSOCK is retained only as a fallback research item (Kestrel has no built-in VSOCK transport).
- **The Sandbox Engine (Raw Docker Engine in `MainguardOS`):** v1 runs agents in hardened Docker containers (user namespaces, seccomp, `no-new-privileges`, default-deny egress) inside the `MainguardOS` WSL2 boundary. The WSL2 VM itself is the hardware boundary between agents and the Windows host, so agents can run in "YOLO mode" without threatening the host kernel. **Docker Sandboxes (`sbx`) microVMs — running natively on Windows via Windows Hypervisor Platform — are a post-v1 optional "high-security backend"** for users who want per-agent hardware isolation. sbx is never nested inside WSL2.
- **The Git-Native Sync Boundary (replaces "Hollow-Core"):** All agent I/O happens at native `ext4` speed inside the VM. On project open, the daemon clones the user's repository into a bare repo on `ext4` and creates all agent worktrees there — file watchers (inotify) work, `git status` is fast, and PNPM hardlinking works natively. The user's checkout stays untouched on `C:\` and simply gains a git remote pointing at the VM repo. Windows↔Linux state exchange happens **exclusively through Git objects** (`fetch`/`merge`), never through cross-OS file mounts.
- **Zero-Conflict Concurrency via Worktrees:** Agents work on private Git worktrees on `ext4`. When a task is complete and verified, the human reviews via `vscode-remote` and merges by clicking "Merge to Main," which executes `git fetch mainguard-vm && git merge agent/{id}` on the Windows repository — a pure Git object transfer.
- **Security-First Defaults:** Default-deny egress with a provider allowlist, per-sandbox credential isolation (no shared auth-directory mounts), `--ignore-scripts` on host-side installs, and loud review flagging of executable-config changes (`package.json` scripts, lockfiles, `.github/workflows/`, `.vscode/`, git hooks).
- **Native OS Terminals (Server-Side Emulation):** Rejects browser WebViews (`xterm.js`). Mainguard uses native OS pseudo-terminals (`Porta.Pty` via ConPTY/forkpty). **Target engine:** VT emulation runs *in the daemon* on `libvterm` (the battle-tested C core powering Neovim's `:terminal`), streaming screen-grid damage updates over gRPC to a thin first-party Skia grid renderer in Avalonia. This buys conformance-grade correctness for Ink-based TUIs (Claude Code), makes terminal state survive client restarts (Session Durability), and pre-builds the Cloud Worktrees remote protocol.
- **Honest Scale, Cloud Growth:** Target experience is **several agents (4–6 on 16 GB hardware), perfectly managed**, enforced by memory-aware admission control. Larger swarms are served by Cloud Worktrees (Phase 9), not by overselling local hardware.

---

## 2. Technical Stack & Dependencies

- **Desktop Framework:** Avalonia UI (**11.3.x**; Avalonia 12 migration tracked as a maintenance item)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (8.4.x)
- **Git Engine:** `LibGit2Sharp` (**0.31.0**) for reads, status, commits, and diffs. **Policy: rebase (including interactive), worktree, and merge operations drive the `git` CLI** — interactive rebase is unsupported in libgit2, and libgit2sharp's worktree API is incomplete.
- **Local Database:** SQLite via EF Core — WAL mode, **single-owner process** (the daemon). A SQLite file is never shared across the 9P boundary; all cross-boundary access goes through gRPC.
- **Windowing/Layout:** `Dock.Avalonia`
- **Terminal PTY Layer:** `Porta.Pty` (replaces `Pty.Net`; `microsoft/vs-pty.net` is effectively unmaintained)
- **Terminal Engine (staged):** *Interim (7.1a):* `Iciclecreek.Avalonia.Terminal` as a **vendored fork** (the entire native .NET terminal chain — Porta.Pty, XTerm.NET, Iciclecreek — is a single maintainer at v1.0.x, and the dormant alternatives XtermSharp/VtNetCore are no better). *Target (7.1b):* **`libvterm` in the daemon via P/Invoke** + first-party Avalonia/Skia grid renderer. Both stages gated by the VT conformance & replay test harness (7.1c).
- **Container Control:** `Docker.DotNet` against the `MainguardOS` engine socket
- **Distribution:** Velopack

---

## 3. Project Structure

```text
Mainguard/
├── Mainguard.Agents/
│   ├── AppDbContext.cs
│   ├── Models/
│   ├── Services/
│   ├── Graph/
│   └── Agents/                         # Swarm Sandbox Engine
│       ├── IAgentExecutor.cs
│       ├── RepoProvisioner.cs          # ext4 bare clone + VM remote registration
│       ├── WorktreeManager.cs          # Drives `git worktree add/remove` (CLI)
│       ├── PtyProcessShim.cs           # Manages Porta.Pty streams and lifecycle
│       ├── AiGateway.cs                # Global token-bucket, budgets, 429 backoff
│       └── Orchestrator/
│           ├── CoordinatorAgent.cs     # Main agent chat interface
│           ├── WorkerAgent.cs          # Isolated sub-task runner
│           └── MergeQueue.cs           # Re-verification + staleness tracking
│
├── Mainguard.App.Shell/
│   ├── ViewModels/
│   │   ├── ActivityBarViewModel.cs
│   │   ├── AgentSandboxViewModel.cs
│   │   └── TerminalViewModel.cs
│   └── Views/
│       ├── ActivityBarView.axaml
│       └── AgentSandboxView.axaml
│
└── Mainguard.Tests/
```

---

## 4. Phase-by-Phase Implementation Plan

### 🚀 Phase 1: Scaffolding & Workspace Manager (COMPLETED)
* **Phase 1.1: Project Scaffolding & Solution Setup (COMPLETED)**
  - [x] Initialize the `Mainguard.Agents` class library, `Mainguard.App.Shell` Avalonia MVVM application, and `Mainguard.Tests` xUnit test suite on .NET 10.0.
  - [x] Wire assemblies together with project references and construct the solution map (`Mainguard.slnx`).
* **Phase 1.2: Dependencies & Local config.json Store (COMPLETED)**
  - [x] Install NuGet dependencies: `LibGit2Sharp`, `Microsoft.EntityFrameworkCore.Sqlite`, `LiveChartsCore.SkiaSharpView.Avalonia`.
  - [x] Design a strongly typed preferences model (`config.json`) targeting local AppData and implement O(1) in-memory settings service (theme).
* **Phase 1.3: Database Scaffolding & Bookmarks Store (COMPLETED)**
  - [x] Setup SQLite EF Core `AppDbContext` and migrations to handle Workspace Categories and Repository bookmarks.
* **Phase 1.4: Debounced Watcher & CLI Fallback scaffold (COMPLETED)**
  - [x] Implement the `GitService` interface supporting direct `LibGit2Sharp` methods.
  - [x] Design the strict `IDisposable` C-handle release block patterns.
  - [x] Implement a debounced `FileSystemWatcher` targeted at `.git/refs`, `.git/index`, and `.git/HEAD` that suppresses intermediate bursts and emits a debounced (300-500ms) final state reload.
* **Phase 1.5: Modern Shell & Sidebar UI (COMPLETED)**
  - [x] Build main window grid layout with a sidebar category browser, workspace tabs, and a local directory crawler to bookmark `.git` folders.

### 🛠️ Phase 2: Staging, Diffs, & Committing (COMPLETED)
* **Phase 2.1: Staging Status & Index Inspector (COMPLETED)**
  - [x] Query direct repo statuses via LibGit2Sharp to group files (Staged, Modified, Untracked, Deleted).
  - [x] Create a side panel in `RepoDashboardView` showing the file change trees with stage/unstage checkboxes.
* **Phase 2.2: Plain-Text DiffViewerControl (COMPLETED)**
  - [x] Implement line-by-line patch generation comparing working directories against the index or HEAD.
  - [x] Build the custom `DiffViewerControl` displaying unified or side-by-side lines with plain light-green/red line background accents (with tokenization deferred to keep UI thread load flat).
* **Phase 2.3: Commit Composer Pane (COMPLETED)**
  - [x] Design the commit message composer with emoji auto-replacements.
  - [x] Implement staged committing in `GitService`, handling author signatures, and triggering a post-commit local watcher refresh.
* **Phase 2.4: Push/Pull & Remote Sync (COMPLETED)**
  - [x] Query upstream tracking references to calculate `Ahead` and `Behind` commit indices.
  - [x] Implement LibGit2Sharp Network Push/Pull commands with credential callbacks.

### 🧬 Phase 3: High-Performance Commit History & Graph (COMPLETED)
* **Phase 3.1: Chunked Commit Querying & Virtual Timeline (COMPLETED)**
  - [x] Implement `GetRecentCommits` with skip/take chunked paging.
  - [x] Design scrollable commit card items inside Avalonia `ListBox` with `VirtualizingStackPanel`.
* **Phase 3.2: Isolated DAG Lane-Routing Engine (COMPLETED)**
  - [x] Create the `CommitGraphRouter` logical module inside `Mainguard.Agents.Graph` completely decoupled from UI controls.
  - [x] Support incremental 500-commit topological mapping with a "Fringe State" contract to stitch seams between adjacent pages.
  - [x] Implement a comprehensive suite of unit tests under `Mainguard.Tests` validating octopus merges and complex overlapping track lanes.
* **Phase 3.3: Virtualized Vector CommitGraphCanvas (COMPLETED)**
  - [x] Build the custom `CommitGraphCanvas` control utilizing a DrawingContext.
  - [x] Bind canvas rendering to only draw glowing path tracks and node circles intersecting the visible viewport's row indexes.

### 🌿 Phase 4: Branch Management & Interactive Merging (IN PROGRESS)
* **Phase 4.1: Branch Tree & Checkout Control (COMPLETED)**
  - [x] Query local and remote heads to render a nested branch browser in the sidebar.
  - [x] Implement checkout safety validation checks (safely handling uncommitted changes).
* **Phase 4.2: Stashing & Creation Management (COMPLETED)**
  - [x] Build stashing list control and stash push/pop commands.
  - [x] Design new branch dialogs with safety tracking checkboxes.
* **Phase 4.3: Advanced Branch Context Menus (COMPLETED)**
  - [x] Implement a deeply nested UI architecture for branch interactions.
  - [x] Implement dynamic `MenuItemViewModel` trees and `TreeDataTemplate` rendering.
  - [x] Hook up Checkout, New Branch, Update, Push, and Delete Branch safely.
* **Phase 4.4: In-App Code Editor & Conflict Resolution (3-Way Merge)**
  - [x] Upgrade the DiffViewer to an interactive AvaloniaEdit control for direct code modifications and quick fixes.
  - [x] Implement a 3-way merge UI and parsing engine for resolving merge conflicts directly within the app.
* **Phase 4.5: Advanced Git Operations (Rebase, Worktrees, Diffs)**
  - [x] Implement rebase support (standard rebase state inspection via `LibGit2Sharp`).
  - Implement **interactive rebase by driving the `git` CLI** (todo-list generation, `GIT_SEQUENCE_EDITOR` shim, progress parsing) — interactive rebase is unsupported in libgit2, so LibGit2Sharp is used only to inspect resulting state.
  - Implement Git Worktree integration by driving the `git` CLI (`worktree add/remove/prune`).
  - Implement working tree diffs against specific arbitrary commits.

### 📊 Phase 5: Repository Analytics & Churn (Premium Polish)
* **Phase 5.1: Asynchronous gitignore-Aware Language Parser**
  - Build directory tree crawler that parses `.gitignore` recursively.
  - [x] Process language byte counts in the background and wire data up to SkiaSharp Donut Charts.
* **Phase 5.2: Churn & Punch Card Calculations**
  - Asynchronously traverse history to compile Code Churn stats (net additions/deletions over time) and developer activity Punch Cards.
* **Phase 5.3: UI Transitions & Micro-Animations**
  - Apply clean transitions to tab navigation and analytics loading indicators.
* **Phase 5.4: Ghost Loading / Skeleton Screens**
  - Implement a highly polished ghost loading/skeleton screen overlay for the `RepoDashboardView`.
  - When opening a new repository, render a pulsing skeleton frame of the Staging Panel, Diff Viewer, and Timeline while LibGit2Sharp executes parsing in a background thread.
  - Ensures the UI immediately reacts to repository switching without hard blocking or appearing unstyled during I/O delays.

### ☁️ Phase 6: Agent Profiles & Secure Keyring Sync (Opt-In Extension)
* **Phase 6.1: Audited Cross-Platform Secure Keyring**
  - [x] Implement a JetBrains-style internal credential manager that intercepts Git auth prompts and caches credentials securely using OS native storage (DPAPI on Windows, Keychain on macOS, Secret Service on Linux).
* **Phase 6.2: Decentralized Device Flow Client**
  - [x] Implement secure client-to-GitHub OAuth 2.0 Device Flow browser integrations.
* **Phase 6.3: Remote Repository Cloner panel**
  - [x] Fetch user repository lists asynchronously over REST.
  - [x] Design a dedicated "Clone Remote Repository" dashboard allowing one-click staging into local categories.
* **Phase 6.4: LLM API Key Management (BYOK)**
  - Expand the secure keyring (DPAPI/Keychain) to safely encrypt and store user-provided OpenAI/Anthropic API keys locally, keeping them out of plaintext configuration files.
  - **Key health checks:** validate tier/rate limits at key entry so the user learns their realistic swarm ceiling before their first 429.
  - **ToS disclosure:** when a user connects an agent via consumer-subscription OAuth (e.g., Claude Pro/Max), display an explicit notice that this path is subject to provider ToS enforcement (Anthropic's April 2026 third-party OAuth restrictions); document API-key / pay-as-you-go as the primary supported path.

### 🤖 Phase 7: Integrated Multi-Agent Control Center
* **Phase 7.1: The JetBrains Terminal Engine (Staged)**
  - **7.1a — Interim (ship fast):** native OS pseudo-terminals via `Porta.Pty` (ConPTY for Windows, forkpty for Linux/macOS) to prevent `isatty()` degradation. Frame PTY bytes into 16ms chunks on the daemon before streaming over gRPC, with a **stateful VT boundary detector** so a tick never cleaves a multi-byte ANSI sequence. Render via the vendored `Iciclecreek.Avalonia.Terminal` fork behind a stable `ITerminalView` ViewModel interface. Zero-allocation buffers (`ArrayPool<byte>`), 60 FPS `DispatcherTimer` rendering, strict 10,000-line circular scrollback.
  - **7.1b — Target engine (before beta): server-side emulation on `libvterm`.** Write P/Invoke bindings for `libvterm` (small C99 API; daemon-side Linux-x64 only — trivial packaging); run one emulator instance per agent PTY in the daemon with a scrollback ring buffer implemented on the `sb_pushline` callbacks; stream **screen-grid damage updates** (cells, attributes, cursor) over the existing 16ms gRPC framing; replace the client control with a first-party **Avalonia/Skia grid renderer + keyboard/mouse encoder** (~1–2k lines, fully owned) behind the same `ITerminalView` interface. Payoffs: battle-tested conformance for Ink TUIs (libvterm powers Neovim's `:terminal`), terminal state survives client crashes/restarts (implements Phase 7.3 Session Durability), and the grid-update protocol works unchanged for Phase 9 Cloud Worktrees.
  - **7.1c — VT Conformance & Replay Harness (non-negotiable, gates both stages):** `vttest`/`esctest` conformance runs in CI, plus **golden-transcript replay tests** — record real Claude Code, OpenCode, vim, htop, and tmux sessions (`script`/asciinema), replay them through the emulator, and snapshot-compare the resulting grids. Coverage must include alternate screen, synchronized output (DEC 2026), truecolor, CJK/emoji width, bracketed paste, mouse reporting, and OSC 8 hyperlinks.
  - **7.1d — Break-glass fallback (feature-flagged, unadvertised):** an `xterm.js` terminal pane inside the already-shipping WebView2/CefGlue host, so terminal bugs can never block a launch. Never marketed.
* **Phase 7.2: The Sandbox Engine, Repo Provisioner & AI Gateway**
  - **`MainguardOS` Bootstrapper:** Silently import a minimal WSL2 tarball; generate `.wslconfig` (memory cap, processors, `autoMemoryReclaim`) at bootstrap; inject `fs.inotify.max_user_watches=524288` into `sysctl.conf` for large monorepo watchers on ext4; start `dockerd` inside the instance.
  - **Repo Provisioner (Git-Native Sync Boundary):** On project open, clone/fetch the Windows repository into a bare repo on `ext4` (`~/mainguard/repos/<hash>.git`), create agent worktrees on `ext4`, and register the VM repo as a remote of the Windows repository. Enable `core.untrackedCache=true`. No Windows-path bind mounts.
  - **Container Hardening Defaults:** user namespaces, `no-new-privileges`, default seccomp profile, per-container `--memory` and `--pids-limit` caps.
  - **Egress Firewall (launch-tier, promoted from Phase 8):** default-deny outbound from all agent containers with a provider allowlist (model APIs, package registries). This is the primary prompt-injection exfiltration control.
  - **Credential Isolation:** each sandbox receives only its own agent's credential material in `tmpfs`, read-only where the CLI permits. No global `~/.claude:rw`-style mounts.
  - **AI Gateway (launch-blocking):** global token-bucket across all agents, request queueing, 429/backoff interception that pauses workers instead of letting CLIs crash and lose context, per-agent/per-day token budgets, and cost telemetry.
  - **Resource Monitoring & Admission Control:** monitor VM memory; block new agent spawns near limits (user-overridable in Settings). Honest target: 4–6 concurrent agents on 16 GB hardware.
  - **Persistent Per-Repo Jails:** automate sandbox lifecycles; PNPM global content-addressable store hardlinks `node_modules` across worktrees.
  - **Zombie Swarm Prevention:** on boot, interrogate the engine via `Docker.DotNet` as the sole source of truth for container lifecycle (no static lockfiles); `git worktree prune` for dead containers.
* **Phase 7.3: The Agent Lifecycle & Merging Workflow**
  - Implement Worktree-Safe Syncing: suspend agent, commit worktree, `git rebase main` inside the worktree. Resume.
  - Implement the "Middle Manager" Architecture: the Coordinator acts strictly as a manager (no code, no worktree) that spawns and monitors isolated Worker Agents.
  - **Merge Queue with Re-Verification:** each merge into `main` invalidates every other worker's semantic-verification result; workers re-enter the queue for keep-alive rebase + re-test. The UI displays "verified against `main@<sha>`" and flags stale results — merges on stale verification are blocked by default.
  - **Foreground Integration:** workers flag for human review via Remote IDE Attach. "Merge to Main" executes `git fetch mainguard-vm && git merge agent/{id}` on the Windows repository (pure Git object transfer). Post-merge host `npm install` runs with **`--ignore-scripts` by default**; any diff touching `package.json` scripts, lockfiles, `.github/workflows/`, `.vscode/`, or git hooks is surfaced as a distinct, loudly-flagged review category. If rejected, Mainguard deletes the branch and runs `npm prune` inside the sandbox.
  - Implement the Cooperative Yield Protocol (stateless IPC triads like `[IPC_UPDATE_REQUESTED]`/`READY`) to safely pause agents before rebasing, preventing race conditions and `.git/index.lock` collisions.
  - **Session Durability:** agent PTY sessions are owned by persistent session leaders inside the VM (tmux-style) so a daemon or UI crash never kills a running agent; the daemon reattaches on restart.
  - Automate teardown and cleanup routines (kill PTY, force remove worktree, delete temporary agent branch, and explicitly `Close()` floating `Dock.Avalonia` windows to prevent UI leaks).
* **Phase 7.4: The Split Activity Bar & Docking UI**
  - Implement `Dock.Avalonia` for customizable Agent Workspaces (containing the Native Terminal, Code Diff Viewer, and Staging Tree).
  - Build the split Activity Bar (Coordinator pinned top, dynamic LIFO worker list bottom) with colored Status Micro-Badges (Running, Awaiting Merge, Conflict, **Stale-Verified**).
  - Animate the Coordinator Tab to pulse red/yellow when human intervention or manual approvals are required; add **OS-level notifications** when a background agent enters a waiting state.
* **Phase 7.5: Dual-Mode Orchestration**
  - Manual Mode: `[+]` spawning and Read-Write terminal interactions for direct user orchestration.
  - Coordinator Mode: Read-Only terminal locking for workers and configurable Max Subagent Limits.
  - **Plan-Approval Dry Runs:** the Coordinator emits a structured task plan per worker (files in scope, approach, test strategy) which the human approves *before* any code is written. Approved plans make diff review cheap and prevent scope creep.
  - Semantic Conflict Verification: before flagging a worker's branch for human review, the Coordinator triggers a containerized test-suite execution to catch semantic drift.
  - **Kill Switch:** a single control that pauses all agents, freezes all sandboxes, and snapshots state.
  - The Human Handoff: the Coordinator surfaces readiness states; humans review via `vscode-remote` (native ext4 IntelliSense); merging happens in the foreground through the merge queue.

### 🛡️ Phase 8: Enterprise AI Governance & Security
* **Phase 8.1: Source-Available Trust Architecture (LOCKED DECISION)**
  - License the **backend daemon, sandbox/worktree engine, and agent adapters under the FSL (Functional Source License)** — source-available, auditable by any security team, legally prohibited from competing use, converting to Apache-2.0 after two years. The **Avalonia GUI, Coordinator orchestration intelligence, and enterprise governance features remain proprietary/commercial.**
  - Rationale: the component with write access to customer source code and keys must be auditable to be adoptable by the target audience; FSL forbids competing use, and .NET IL decompiles trivially so closed source adds little technical protection anyway — velocity, distribution, and brand are the real copy protection.
  - Publish a detailed **security architecture document** (egress allowlisting, credential isolation, sandbox boundaries, exactly what leaves the machine) and commission an **independent security audit with a published report** before enterprise GA.
  - All LLM API calls route directly from the local machine to the provider (BYOK); Mainguard infrastructure never proxies or observes keys or prompts. Ship an **in-app network transparency view** showing every outbound connection the daemon and sandboxes make.
* **Phase 8.2: Tamper-Evident Swarm Auditing**
  - Construct a chronological record of all swarm activity: exact model version per inference, full input prompts, raw outputs, and the verified identity of the authorizing developer.
  - **Mechanism:** hash-chained append-only log with periodic external anchoring (RFC 3161 timestamping) — "tamper-evident" is a design property, not a marketing word.
  - **Log governance:** encryption at rest, configurable retention, and redaction — prompt logs contain proprietary source and are themselves a crown-jewel data store.
* **Phase 8.3: SIEM Exportability & Human-in-the-Loop Telemetry**
  - Log every human intervention (plan approvals, code modifications, merge approvals/rejections) as structured audit events streamed to enterprise SIEM platforms.
* **Phase 8.4: Enterprise Access & Policy**
  - RBAC/SSO/SCIM: who may spawn agents, approve plans, approve merges, or change egress rules.
  - Centralized policy: model allowlists, egress rules, per-team token budgets (enforced via the AI Gateway).
* **Phase 8.5: Supply-Chain & Secrets Compliance**
  - Secrets-manager integration (HashiCorp Vault, AWS Secrets Manager) alongside OS keyrings.
  - **SCA/license scanning at the merge gate:** heuristically scan agent diffs for copyleft contamination before merge approval is offered.
  - *Note:* these features support **customers'** compliance programs; SOC 2 for Mainguard-the-company is a separate organizational track (change management, access reviews, incident response) and is not satisfied by product features.

### ☁️ Phase 9: Cloud Worktrees (Compute-as-a-Service)
* Reuse the existing client-server gRPC contract: the daemon runs in a managed cloud pod instead of local WSL2; the native Windows UI is unchanged.
* Solves the local hardware ceiling (the honest 4–6 agent limit) and provides the usage-based revenue lever that local BYOK deliberately forfeits.
* Target: private beta within two quarters of desktop GA.

---

# Mainguard Velopack Distribution & OOBE First-Run Roadmap

Mainguard utilizes **Velopack** for zero-friction cross-platform distribution (Windows `.exe`, macOS `.dmg`, Linux `.AppImage`). Velopack silently drops the binaries onto the system and immediately launches Mainguard. The "Installer" is an **Out of Box Experience (OOBE) First-Run Wizard** built directly into the Avalonia UI, dynamically branching the system-level heavy lifting (WSL for Windows, Colima for Mac, native Docker for Linux).

---

## 1. Core Vision & Goals

- **One Installer, One Flow:** There is no "Vibe Coder vs Developer" fork in the installer. A *simplified view* is an in-app preference the user can toggle at any time — never an installer-locked identity.
- **Upfront Requirements Gating:** Verify Windows 11 x86_64, hardware virtualization, and disk space *before* any system modification. Publish and enforce a support matrix (ARM64 explicitly out of scope for v1 — `sbx` is x86_64-only; WSL2-on-ARM support is a tracked decision).
- **Upfront Elevation:** Request Administrator privileges natively at the moment of system modification, bypassing jarring mid-flow UAC prompts.
- **Deep OS Integration:** Register context menus ("Open with Mainguard") and the `mainguard://` URL handler — **for non-secret deep links only** (OAuth uses loopback redirects, see below).
- **Pristine Teardown:** A proper uninstaller guarantees the hidden Linux VM and all containers are completely wiped, leaving no orphaned virtual drives.

---

## 2. Phase-by-Phase Installer Roadmap

### 🚀 Phase 1: Diagnostics & Requirements Gate
* **Phase 1.1: System Hardware Diagnostics**
  - Silently query the host: Windows 11 x86_64 check, WMI (`Win32_ComputerSystem`) VT-x/AMD-V verification, WSL2 state, free disk space. (On Mac: Colima. On Linux: Docker Engine.)
  - Fail fast with actionable guidance before any system change is attempted.

### 🛠️ Phase 2: OS Enablement & The Unavoidable Reboot
* **Phase 2.1: Feature Enablement (Conditional)**
  - If WSL2 is missing and virtualization is verified, trigger Windows APIs to enable the Virtual Machine Platform, surfacing the raw PowerShell commands being executed (transparency is the default; there is no "hidden mode").
* **Phase 2.2: The Auto-Resume Reboot**
  - If feature enablement occurs, prompt for a reboot and create an elevated Windows Scheduled Task (rather than a `RunOnce` key which drops privileges) so setup resumes exactly where it left off.

### 📦 Phase 3: The `MainguardOS` Extraction
* **Phase 3.1: The Pre-Baked Payload (The Tarball)**
  - Extract `MainguardOS.tar.gz`: a stripped-down rootfs containing `dockerd`, `git`, Node.js, and Python. It does *not* contain the agents themselves.
* **Phase 3.2: Silent Import**
  - Execute `wsl --import MainguardEnv` in the background; generate `.wslconfig` resource caps.
* **Phase 3.3: The `MainguardOS` Update Pipeline (NEW)**
  - Versioned tarball with an in-place VM upgrade path and a defined CVE patch cadence for the embedded `dockerd` and base OS. Velopack updates the app binary; this pipeline updates the VM. (This is the first question every enterprise security team asks.)

### 🔗 Phase 4: Windows Deep Integration
* **Phase 4.1: Context Menus**
  - Register registry keys to add "Open with Mainguard" to Windows File Explorer context menus.
* **Phase 4.2: OAuth via Loopback Redirect (REVISED)**
  - Agent/deployment OAuth flows use the **RFC 8252 loopback pattern (`127.0.0.1` ephemeral port) with PKCE**. Custom URI schemes are invocable by any website and leak bearer tokens into URL logs, so `mainguard://` is registered **only for non-secret deep links** (e.g., "open this repo").

### ✨ Phase 5: Agent Provisioning & App Launch
* **Phase 5.1: Agent Selection & Authentication**
  - After the WSL environment is verified, prompt the user to select their Agentic CLIs (Claude Code, AGY, OpenCode) and authenticate via API key (primary path) or the CLI's own browser OAuth (with ToS disclosure per Phase 6.4).
* **Phase 5.2: Pinned Adapter Channel (REVISED)**
  - Do **not** install "the absolute latest" CLI versions. Install per-release **tested, pinned adapter versions**, updated through a separately versioned adapter channel that ships independently of app releases. Agent CLIs break monthly; "latest" guarantees OOBE breakage.
* **Phase 5.3: The Clean Launch**
  - Finish and launch `Mainguard.exe` into a fully authenticated, ready-to-code workspace.

---

## 3. Clean Teardown (The Uninstaller)

- Execute `wsl.exe --terminate MainguardEnv`, poll `wsl.exe -l -v` until "Stopped" (releasing `.vhdx` locks), then `wsl.exe --unregister MainguardEnv`. Never use `wsl --shutdown` (it kills the user's personal WSL instances).
- **Data Safety:** the user's source code lives on the Windows drive; the VM repo is a mirror. Teardown deletes the VM, engine, and all worktrees with zero data-loss risk — the Windows repository simply loses a git remote.

---

# Mainguard Vibe Mode: Technical Roadmap & Vision (POST-V1)

Mainguard "Vibe Mode" is a specialized experience for users with little traditional development experience. **Sequencing decision (locked):** Vibe Mode ships *after* developer-mode v1, and its end-state is cloud delivery ("Mainguard Web" — chat + live preview on Cloud Worktrees), where the segment's zero-install expectations and the infrastructure economics align. The `VibeOrchestrator` backend is built as part of the shared architecture (the developer-mode Coordinator reuses it), so this investment is not deferred — only the standalone local Vibe product is.

It achieves **abstraction with a designed escape hatch** (not "zero-knowledge" — the circuit breaker's existence admits failures reach the human) via a "Virtual User" (the `VibeOrchestrator`) in the Mainguard backend, driving the same Linux environment, PTY streams, and agent CLIs as Developer Mode.

---

## 1. Core Vision & Goals

- **Unified Foundation:** Setup is identical across the app. Mainguard manages the pre-baked `MainguardOS` environment silently.
- **Authentication:** **API key / pay-as-you-go is the primary documented path.** Native CLI OAuth (e.g., Claude Code browser login) is supported with an explicit ToS-risk disclosure — Anthropic's April 2026 terms prohibit subscription OAuth tokens in third-party products, and while Mainguard drives the official CLI binary, this path is one policy clarification from closure.
- **Unaltered Core Technologies:** agent CLIs, WSL2 boundaries, and Git worktrees function exactly as in Developer Mode, driven programmatically by the backend orchestrator.
- **The "Dumb" Frontend:** the Avalonia UI becomes purely a Chat Interface and Live App Preview subscribing to simplified backend events.
- **Offline Durability:** backed by Phase 7.3 Session Durability — the daemon continues compiling, auto-healing, and checkpointing with the UI closed.

---

## 2. Phase-by-Phase Vibe Mode Roadmap

### 🌟 Phase 1: The Unified Foundation & Orchestrator
* **Phase 1.1:** Shared `MainguardOS` environment (identical to Developer Mode; see main roadmap Phase 7.2).
* **Phase 1.2: The `VibeOrchestrator` Engine**
  - Backend "puppet master" service hooking into PTY streams to drive agent CLIs programmatically.
* **Phase 1.3: Headless OAuth Authentication**
  - When an agent CLI emits an OAuth URL, the backend detects it in the stream (with `state=<agent_uuid>` routing) and forwards it to the UI, which opens the browser via the **loopback redirect + PKCE** flow. API keys remain the primary path.

### 🛡️ Phase 2: Autonomous Git Abstraction
* **Phase 2.1: Auto-Checkpoints**
  - Automatically generate clean commits (Checkpoints) each time the agent successfully completes a chat request.
* **Phase 2.2: Autonomous Conflict Resolution**
  - Catch merge conflicts in the backend and feed conflict markers to the agent CLI for resolution, finalizing the merge on success.

### 🚑 Phase 3: In-Memory Auto-Healing & The Escape Hatch
* **Phase 3.1: Stream Interception**
  - The `VibeOrchestrator` monitors Dev Server PTY output in memory on the Linux server.
* **Phase 3.2: The Autonomous Fix Loop & Circuit Breaker**
  - On crash, capture the stack trace and pipe it into the agent CLI's input with a fix prompt, bypassing the frontend.
  - **Circuit Breaker:** hash the stack trace; if the same error hash occurs 3 times, or 5 errors occur in 10 minutes, hard-suspend the agent and escalate.
* **Phase 3.3: Escalation UX (NEW — the most important Vibe Mode feature)**
  - A plain-language triage screen for circuit-breaker states with exactly three actions:
    1. **"Try a different approach"** — re-prompt the agent with the failure context.
    2. **"Go back to when it worked"** — one-click restore to the last green auto-checkpoint (the highest-value safety feature; checkpoints already exist, this makes them usable).
    3. **"Get help"** — export a diagnostic bundle.

### 🎨 Phase 4: The Vibe Mode UI Interface
* **Phase 4.1: Seamless Mode Toggling**
  - An in-app switch (not an installer fork) hides Developer Mode dock panels and transitions to the Chat/Preview layout.
* **Phase 4.2: Live Embedded Preview (WebView2/CefGlue)**
  - Embedded browser auto-navigates to the dev server port. **Hot reload works because the dev server and source files live together on `ext4` with functioning inotify watchers**; the preview reaches the server through the localhost bridge. (The prior claim that hot reload "works natively via a shared bind mount" was false — inotify does not propagate over 9P mounts.)
* **Phase 4.3: Chat-to-Orchestrator Bridge**
  - UI chat input is proxied via gRPC to the `VibeOrchestrator`, which pipes it into the agent CLI. The terminal engine (raw PTY interim; grid damage updates once 7.1b lands) preserves native CLI animations, spinners, and colors either way.

### 🚀 Phase 5: One-Click Deployment
* **Phase 5.1: Cloud Integrations**
  - Secure OAuth flows (loopback + PKCE) for Vercel/Netlify.
* **Phase 5.2: "Publish to Web"**
  - A single button triggering final commit, push to GitHub, and cloud build, returning a live URL.
