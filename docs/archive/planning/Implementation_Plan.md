# Mainguard Multi-Agent Control Center: Implementation Details

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

This document outlines the concrete implementation specifications for integrating concurrent AI CLI agents (Claude Code, AGY, OpenCode) into the Mainguard architecture using native OS terminals and Git worktrees.

> **Revision note (July 2026):** Rewritten to match the revised roadmap. The 9P bind-mount "Hollow-Core" design, the fsmonitor shim, the nested-sbx engine, the AF_VSOCK transport, and the global auth-directory mounts are removed (each failed verification against official documentation). Replacements: Git-native sync boundary on ext4, raw Docker Engine in `MainguardOS` with hardening + egress firewall, localhost gRPC over mirrored networking, per-sandbox credential isolation, the AI Gateway, and the merge queue with re-verification.

---

## 1. The Environment: The Client-Server Split Architecture

*   **The Engine (WSL2 → Raw Docker):** The Mainguard Windows installer silently provisions a private, lightweight WSL2 instance (`MainguardOS`) running `dockerd`. Agents execute in hardened Docker containers (user namespaces, `no-new-privileges`, seccomp, default-deny egress). The WSL2 VM is itself a hardware boundary between agents and the Windows host, so agents can operate in full "YOLO mode" without risk to the host kernel. **Docker Sandboxes (`sbx`) microVMs — installed natively on Windows via `winget` and Windows Hypervisor Platform — are a post-v1 optional high-security backend; they are never nested inside WSL2.**
*   **Persistent Per-Repo Isolation (Blast Radius Protection):** When a user opens a repository, `Mainguard.Server` creates a dedicated, persistent sandbox. The Agentic CLIs are jailed within it, preserving `node_modules` caches and agent sessions across app restarts.
*   **The Git-Native Sync Boundary (replaces "Hollow-Core"):** On project open, the daemon clones/fetches the Windows repository into a bare repo on `ext4` (`~/mainguard/repos/<hash>.git`) and creates all agent worktrees on `ext4` (`~/mainguard/worktrees/<repo>/<agent>`). Agents get native filesystem speed, working inotify watchers, and fast `git status`. The user's checkout stays untouched on `C:\Code\Project` and gains a git remote pointing at the VM repo. Windows↔Linux exchange happens exclusively through Git objects — never through cross-OS file mounts. (Verified rationale: Git's builtin fsmonitor has no Linux backend, and inotify does not propagate over 9P mounts; the prior bind-mount topology could not deliver its promised performance or hot reload.)
*   **The Remote IDE & Post-Merge Sync:** Code review uses **VS Code Remote Attach** reading the ext4 worktree natively (perfect IntelliSense, no file copying). Upon "Approve & Merge", Mainguard executes `git fetch mainguard-vm && git merge agent/{id}` on the Windows host — a pure Git object transfer — then runs `npm install --ignore-scripts` (see §6). If rejected, Mainguard runs `npm prune` inside the sandbox.

---

## 2. Terminal Emulation: The JetBrains Native Approach (Staged)

To ensure interactive CLIs behave perfectly (accepting keystrokes, rendering curses UIs), Mainguard rejects web-based `xterm.js` WebViews in favor of a fully native stack. Because no battle-tested, actively maintained VT emulator exists in .NET (the tomlm stack — Porta.Pty/XTerm.NET/Iciclecreek — is one maintainer at v1.0.x; XtermSharp and VtNetCore are dormant), the terminal ships in two stages behind one stable `ITerminalView` interface, gated by a conformance harness.

### A. The PTY Layer: `Porta.Pty` (both stages)
*   **Windows:** `ConPTY` API. **Linux/macOS:** standard `forkpty`.
*   **Implementation:** spawn with `Cwd` locked to the agent's ext4 worktree, forcing the OS to report a physical terminal and preventing `isatty()` failures.

### B. Stage 7.1a (Interim): Vendored Native Avalonia VT100
*   PTY byte streams bind to the **vendored fork** of `Iciclecreek.Avalonia.Terminal`, parsing VT100/ANSI natively and rendering via Skia. Keystrokes (`Ctrl+C`) are intercepted by the Avalonia window and written directly into the PTY byte stream.
*   **Performance Throttling:** zero-allocation buffers (`ArrayPool<byte>`) into a background ring buffer; a ~16ms (60 FPS) `DispatcherTimer` invalidates the control.
*   **Memory Bounds:** strict 10,000-line circular scrollback limit.

### C. Stage 7.1b (Target, before beta): Server-Side Emulation on `libvterm`
*   The daemon runs one **`libvterm`** instance per agent PTY (P/Invoke bindings; C99, callback-driven, allocation-free in steady state — the core powering Neovim's `:terminal`, hence conformance-proven against exactly the Ink-style TUIs Mainguard hosts). Linux-x64 daemon-side only, so native packaging is trivial.
*   Scrollback is a daemon-side ring buffer implemented on libvterm's `sb_pushline` callbacks (10,000-line cap).
*   The daemon streams **screen-grid damage updates** (dirty rects of cells, attributes, cursor) over the existing 16ms gRPC framing instead of raw ANSI bytes.
*   The Avalonia client becomes a first-party **Skia grid renderer + keyboard/mouse encoder** (~1–2k lines, fully owned), swapped in behind the same `ITerminalView` interface.
*   **Structural payoffs:** terminal state lives in the daemon, so a UI crash/restart re-syncs the full screen instantly (this *is* the §6 Session Durability mechanism for terminals); the grid-update protocol works unchanged when the daemon moves to a cloud pod (Phase 9).

### D. VT Conformance & Replay Harness (gates both stages)
*   CI runs `vttest`/`esctest` conformance suites against the active engine.
*   **Golden-transcript replay tests:** record real Claude Code, OpenCode, vim, htop, and tmux sessions (`script`/asciinema); replay through the emulator; snapshot-compare resulting grids. Required coverage: alternate screen, synchronized output (DEC 2026), truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks.

### E. Break-Glass Fallback (feature-flagged, unadvertised)
*   An `xterm.js` pane inside the already-shipping WebView2/CefGlue host, disabled by default. Exists solely so terminal bugs can never block a launch; never marketed.

---

## 3. Swarm Mechanics: Git Worktree Isolation

Multiple agents run concurrently on the same repository without file-locking collisions.

*   **Initialization (The Spawner):** executed inside the VM against the ext4 bare repo:
    1. `git branch agent/uuid-1234 main`
    2. `git worktree add ~/mainguard/worktrees/<repo>/agent_uuid-1234 agent/uuid-1234`
*   **Worktree operations always drive the `git` CLI** — LibGit2Sharp's worktree API is incomplete (empty-worktree creation bug, no working-directory property). LibGit2Sharp is used for reads/status/commit/diff.
*   **Execution & Resource Limits:** the PTY process spawns with CWD locked to the worktree. Containers get per-container `--memory` and `--pids-limit` caps. Mainguard monitors VM memory; approaching the safe limit (e.g. 85%) disables agent spawning and alerts the user (override in Settings). **Honest capacity target: 4–6 concurrent agents on 16 GB hardware.**

---

## 4. UI/UX Architecture: Mission Control

### A. The Split Activity Bar (Sidebar) & Resource Monitor
Implemented as a multi-row layout:
*   **Resource Monitor (Pinned Top):** `LiveChartsCore` real-time CPU/RAM of the `MainguardOS` boundary over the last 60 seconds, with drill-down sparklines per agent, **plus token-spend counters fed by the AI Gateway**.
*   **Pinned Core Tabs:** core app icons and the **Main Coordinator Agent** tab, which pulses red/yellow when human intervention is required. **OS-level notifications** fire when a background agent enters a waiting state.
*   **Bottom Half (Dynamic):** a `VirtualizingStackPanel` within an `ItemsControl` bound to `ObservableCollection<AgentViewModel>`; LIFO insertion via `Collection.Insert(0, newAgent)`.
*   **Memory Management:** Weak Event Pattern (`WeakReferenceMessenger`) and deterministic teardown (`Dispose()` on tab close, halting `DispatcherTimer`, disposing `WebView2`) to prevent dangling Skia visual trees.
*   **Status Micro-Badges:** 🟢 Running, 🟡 Awaiting Merge, 🔴 Conflict, ⚪ **Stale-Verified** (verification invalidated by a newer merge to `main`).

### B. The Dockable Sandbox (`Dock.Avalonia`)
Clicking an Agent Tab loads a customizable docking layout: Native Terminal, Code Diff Viewer (agent-branch vs `main`), and Staging Tree.

### C. The Master PR Dashboard & Merge Queue
*   **Central Control Tab:** high-level overview of all sub-agents' progress and states.
*   **Merge Queue View:** ready-to-merge agents ordered in the queue, each showing "verified against `main@<sha>`" freshness; conflicts open the 3-way merge tool. Merging on stale verification is blocked by default (re-verify runs automatically).

---

## 5. Dual Operating Modes

### A. Coordinator Mode (Delegated Swarm)
*   **Plan-Approval Dry Runs:** before any code is written, the Coordinator emits a structured task plan per worker (files in scope, approach, test strategy) for human approval. Approved plans keep diff review cheap and scope tight.
*   **Workflow:** the user chats with the Main Agent tab; it spawns Worker Agents via internal API (respecting the **Max Subagents Limit** and AI Gateway budgets).
*   **Read-Only Terminals:** worker terminals are `IsReadOnly = true` — CCTV-style observation with a 🔒 *Managed by Coordinator* banner.
*   **Convergence:** finished workers enter the merge queue behind the **Human Approval Gate**. Conflicts surface Mainguard's native 3-way merge tool.
*   **Kill Switch:** a single control pauses all agents, freezes sandboxes, and snapshots state.

### B. Manual Mode (User Orchestrator)
*   `[+]` button spawns independent agents with Read-Write terminals; the user types prompts directly into the PTY.

---

## 6. The Agent Lifecycle & Merging Workflow

1.  **The Middle Manager Architecture (Anti-Poisoning):**
    *   **Strict Isolation:** every Worker operates in its own ext4 worktree; workers never merge into each other.
    *   **The Middle Manager (Coordinator):** no code, no merges, no worktree — purely an Engineering Manager spawning, prompting, and monitoring Workers via internal APIs.
    *   **Semantic Conflict Verification:** before human review, the Coordinator runs the project's test suite in the sandbox to catch logic that merges cleanly but fails functionally.
    *   **Merge Queue with Re-Verification:** every merge into `main` invalidates all other workers' verification results. Invalidated workers automatically re-enter: Cooperative Yield → keep-alive rebase onto new `main` → re-run verification. The UI blocks merges on stale results by default.
    *   **User-Initiated Integration:** verified workers flag `Awaiting Human Review`. The human reviews (Remote IDE Attach), then clicks "Merge to Main" — a deliberate foreground action on the primary repository.
2.  **Cross-Agent Dependency Resolution:** if Worker B needs Worker A's code, the Coordinator instructs B to wait; after A merges, B performs a standard keep-alive rebase against `main`. (Note: dependent task graphs execute serially — parallelism gains apply to *independent* tasks, and the Coordinator's task-partitioning quality is a first-class KPI.)
3.  **Host-Side Install Protection:** post-merge `npm install` on Windows runs with **`--ignore-scripts` by default** (a poisoned `postinstall` must never execute on the host via the user's approval click). Any diff touching `package.json` scripts, lockfiles, `.github/workflows/`, `.vscode/`, or git hooks is surfaced as a distinct, loudly-flagged review category before the merge button is enabled. The install is wrapped in a `Polly` retry policy (3 attempts, 1500ms exponential backoff) against NTFS `EPERM`/`EBUSY` contention.
4.  **Session Durability:** agent PTYs are owned by persistent session leaders inside the VM (tmux-style); a daemon or UI crash never kills a running agent, and the daemon reattaches on restart.
5.  **Teardown & Cleanup:** prompt for unmerged changes (Merge or Discard); kill the PTY; `git worktree remove --force`; delete the agent branch; verify a clean filesystem.

---

## 7. AI Autonomous Implementation Instructions (Phases 6.4 - 7.5)

### 🤖 Instruction: Phase 6.4: LLM API Key Management (BYOK)
**Objective:** Securely store user-provided LLM API keys without ever writing them to a plaintext file.
**Implementation Steps:**
1.  **OS-Native Keyring Integration:** in `Mainguard.Agents.Security`, implement a cross-platform `ISecureKeyStore` interface.
2.  **Windows:** `ProtectedData.Protect` (DPAPI) or the Windows Credential Manager API. **macOS/Linux:** `Security.framework` (Keychain) and `libsecret` (Secret Service).
3.  **ViewModel Binding:** `ApiKeySettingsViewModel` with a `PasswordBox`; never hold the raw string longer than necessary.
4.  **Key Health Check:** on entry, validate the key and probe its tier/rate limits; display the realistic concurrent-agent ceiling to the user.
5.  **Environment Injection (tmpfs):** decrypt in-memory on the Windows side, pass over a localized pipe, and inject strictly into a `tmpfs` RAM disk (`--mount type=tmpfs,destination=/dev/shm`), writing `/dev/shm/.env` so secrets never touch a persistent block device or `ps aux` arguments.
6.  **ToS Disclosure:** when the user connects an agent via consumer-subscription OAuth, display the provider-policy notice (Anthropic's April 2026 restriction on subscription OAuth in third-party products) and document API-key / pay-as-you-go as the primary supported path.

### 🤖 Instruction: Phase 7.1: The JetBrains Terminal Engine (Staged: 7.1a interim → 7.1b libvterm)
**Objective:** Native pseudo-terminals so CLI tools function without `isatty()` crashes, with VT emulation ultimately running server-side on a battle-tested core.
**Implementation Steps — 7.1a (interim, ship fast):**
1.  **Backend Dependency:** install the `Porta.Pty` NuGet package in `Mainguard.Agents`.
2.  **Process Spawner:** build `PtyProcessShim.cs` invoking the PTY spawn API with the agent CLI, arguments, and `Cwd` set to the ext4 worktree.
3.  **Stable Interface First:** define `ITerminalView` (feed output, send input, resize, read/save state) in `Mainguard.Agents` so the 7.1b engine swap never touches ViewModels.
4.  **Frontend Binding:** integrate the vendored `Iciclecreek.Avalonia.Terminal` control in `Mainguard.App.Shell` behind `ITerminalView`.
5.  **Stream Piping & VT100 Stateful Throttling:** `Mainguard.Server` flushes PTY bytes over gRPC every 16ms (60 FPS). A stateful VT boundary detector prevents a tick from cleaving multi-byte ANSI sequences — mid-sequence bytes buffer to the next frame.
6.  **Memory Guard:** strict circular-overwrite scrollback limit (10,000 lines) on the terminal's backing model.
**Implementation Steps — 7.1b (target engine, before beta):**
7.  **libvterm Bindings:** write P/Invoke bindings for `libvterm` in `Mainguard.Agents.Terminal.Native` (small C99 API surface; bundle a Linux-x64 build with the daemon — no client-side native binaries needed).
8.  **Daemon Emulator Sessions:** one `libvterm` instance per agent PTY, owned by the persistent session leader (§6 Session Durability). Implement the scrollback ring buffer on `sb_pushline`/`sb_popline` callbacks (10,000-line cap).
9.  **Damage-Update Protocol:** replace raw-byte streaming with a gRPC grid protocol — dirty-rect cell runs (glyph, fg/bg, attrs), cursor state, scroll events — coalesced into the same 16ms framing. On client (re)connect, send a full-grid snapshot + scrollback page-in; this makes UI crash recovery and future Cloud Worktrees attach (Phase 9) the same code path.
10. **First-Party Grid Renderer:** implement `TerminalGridControl` in `Mainguard.App.Shell` (Avalonia/Skia): monospace cell layout, glyph-run caching, selection/clipboard, IME composition, and a keyboard/mouse encoder translating Avalonia input into VT sequences written back to the PTY. Swap in behind `ITerminalView`.
**Implementation Steps — 7.1c (harness, gates both stages):**
11. **Conformance CI:** run `vttest`/`esctest` against the active engine.
12. **Golden-Transcript Replays:** record real Claude Code, OpenCode, vim, htop, and tmux sessions; replay through the emulator in CI; snapshot-compare grids. Required coverage: alternate screen, DEC 2026 synchronized output, truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks.
**Implementation Steps — 7.1d (fallback):**
13. **Break-Glass Pane:** feature-flagged `xterm.js` terminal inside the existing WebView2/CefGlue host, default-off, never marketed.

### 🤖 Instruction: Phase 7.2: The Sandbox Engine, Repo Provisioner & AI Gateway
**Objective:** Isolate agents per-repository with hardened Docker containers, provision all agent I/O on ext4, and govern all model traffic through a local gateway.
**Implementation Steps:**
1.  **The `MainguardOS` Bootstrapper:** on first launch, execute `wsl --import MainguardEnv` with the bundled tarball; generate `%UserProfile%\.wslconfig` (memory cap, processors, `autoMemoryReclaim`); inject `fs.inotify.max_user_watches=524288` into `/etc/sysctl.conf` (for large monorepo watchers on ext4); start `dockerd` via a background daemon.
2.  **Repo Provisioner (Git-Native Sync Boundary):** on project load, clone/fetch the Windows repo into a bare ext4 repo (`~/mainguard/repos/<hash>.git`); register the VM repo as a remote (`mainguard-vm`) of the Windows repository; enable `core.untrackedCache=true`. **Do NOT bind-mount Windows paths into containers** (inotify does not cross 9P; builtin fsmonitor does not exist on Linux).
3.  **Container Lifecycle & Hardening:** if the project container doesn't exist, `docker create` with: user namespaces, `no-new-privileges`, default seccomp, `--memory`/`--pids-limit` caps, and the worktree directory mounted from ext4. **Do NOT mount global auth directories (`~/.claude` etc.)** — each sandbox receives only its own agent's credential material in tmpfs, read-only where the CLI permits.
4.  **Egress Firewall (default-deny):** route all container egress through a proxy enforcing a provider allowlist (model APIs, package registries). A prompt-injected agent must be physically unable to exfiltrate source or secrets to arbitrary hosts.
5.  **Dynamic Toolchain Sideloading:** never `docker build` at runtime (it severs active PTY sessions). Use a static base image with Nix/Devbox; `devbox add <package>` installs toolchains into the running container.
6.  **Worktree Manager & PNPM Hardlinking:** `git worktree add ~/mainguard/worktrees/<repo>/agent_{id} agent/{id}` (all ext4). Run `pnpm install` immediately — the global content-addressable store hardlinks dependencies so N agents cost ~1 agent of disk.
7.  **AI Gateway:** implement `AiGateway.cs` — a global token-bucket across all agents, request queueing, and 429 interception that pauses workers with exponential backoff instead of letting CLIs crash and lose context. Enforce per-agent/per-day token budgets; emit cost telemetry to the Resource Monitor.
8.  **Zombie Swarm Prevention:** on launch, interrogate the engine socket via `Docker.DotNet` as the sole source of truth for container lifecycle (no static lockfiles — PID recycling). If a container is dead, `git worktree prune`.
9.  **PTY Routing & Execution:** `docker exec -e DOTENV_CONFIG_PATH=/dev/shm/.env -it <container_id> <agent_cli>`, streams captured via the PTY shim and piped over gRPC to the Avalonia UI.
10. **Transport:** gRPC binds to localhost inside the VM; the Windows client connects via WSL2 **mirrored networking** (Windows 11 22H2+, with `dnsTunneling`/`autoProxy` for VPN environments) using a per-session auth token. AF_VSOCK is a fallback research item only — Kestrel has no built-in VSOCK transport (dotnet/aspnetcore#34050). A thin port-forward exposes the sandbox dev server for the preview pane.

### 🤖 Instruction: Phase 7.3: The Agent Lifecycle & Merging Workflow
**Objective:** Manage agent worktree lifecycles, the merge queue, and pristine teardowns.
**Implementation Steps:**
1.  **State Failsafes & Process Suspension:** before Git mutations, check for `.git/worktrees/<id>/rebase-merge/` or detached HEAD; abort if found. Implement the **Cooperative Yield Protocol** (`[IPC_UPDATE_REQUESTED]` → await `[IPC_UPDATE_READY]`); wrap Git operations in exponential-backoff retries for `index.lock` errors.
2.  **Merge Queue & Re-Verification:** maintain `MergeQueue.cs`. Each merge to `main` marks all other workers' verification results stale; stale workers automatically re-enter (yield → keep-alive rebase → re-run test verification). The UI exposes per-worker "verified against `main@<sha>`" freshness and blocks stale merges by default.
3.  **The Middle Manager Lifecycle (Foreground Integration):**
    *   **Keep-Alive Rebase:** suspend the agent; inside its worktree, stage, commit, `git rebase main`; resume.
    *   **Awaiting Human Review:** the Coordinator flags the worker UI; the worker pauses.
    *   **Remote IDE Review:** "Review in IDE" launches `code --folder-uri vscode-remote://...` against the ext4 worktree (native IntelliSense, no file copying).
    *   **Foreground Merge (User Action):** "Merge to Main" executes `git fetch mainguard-vm && git merge agent/{id}` on the Windows repository. Post-merge, run `npm install --ignore-scripts` on the host inside a `Polly` retry policy (3 attempts, 1500ms exponential backoff) against NTFS locking. Diffs touching `package.json` scripts, lockfiles, `.github/workflows/`, `.vscode/`, or git hooks must have been explicitly acknowledged in the flagged-changes review panel before the merge button enables.
    *   **Rejection Cleanup:** delete the agent branch; `npm prune` inside the sandbox.
4.  **Session Durability:** spawn agent PTYs under persistent session leaders in the VM; on daemon restart, reattach to live sessions instead of respawning.
5.  **Strict Isolation Enforcement:** no automated cross-agent sibling merges, ever.
6.  **Cleanup Engine:** `IDisposable` agent context — kill PTY, `git worktree remove --force`, `git branch -D agent/{id}`, verify clean state.

### 🤖 Instruction: Phase 7.4: The Split Activity Bar & Docking UI
**Objective:** Build the ATC (Air Traffic Control) interface for the swarm.
**Implementation Steps:**
1.  **Docking Layout:** `Dock.Avalonia` default layout: Terminal, Diff Viewer, Staging Tree.
2.  **Activity Bar Grid:** 2-row grid in `ActivityBarView.axaml`; Coordinator tab pinned in Row 0 with an `IsAttentionRequired`-driven pulse animation; Row 1 uses a `VirtualizingStackPanel` bound to `ObservableCollection<WorkerAgentViewModel>` with LIFO insertion.
3.  **Micro-Badges:** badge `Fill` bound to `AgentStatus` via a value converter (Running → Green, Awaiting → Yellow, Conflict → Red, **StaleVerified → Gray**).
4.  **OS Notifications:** fire a system notification whenever an agent transitions to a waiting/blocked state.
5.  **Deterministic Teardown & Floating Window Leaks:** `WeakReferenceMessenger` for global events; on tab close, traverse the `IDock` layout factory and `Close()` any floating `IWindow` views; in `Deactivate`, halt the 60 FPS `DispatcherTimer`, dispose `WebView2`, clear terminal buffers.

### 🤖 Instruction: Phase 7.5: Dual-Mode Orchestration
**Objective:** Implement the state machine governing Manual vs. Coordinator interactions.
**Implementation Steps:**
1.  **Plan-Approval API:** the Coordinator produces a structured plan artifact per worker task (scope, files, approach, test strategy). The UI renders it for approval; workers only start on approved plans. Persist plan + approval identity to the audit log.
2.  **Terminal Locking:** bind `IsReadOnly` to `IsCoordinatorManaged`; show the 🔒 banner.
3.  **Coordinator API:** internal tool-calling API for spawning sub-agents up to `MaxSubagentsLimit`, subject to AI Gateway budgets.
4.  **The Human Handoff (Approval Gate):** verified workers flag Yellow (Awaiting Review); the Coordinator never executes merges.
5.  **Kill Switch:** one command pauses all agents (Cooperative Yield), freezes containers, and snapshots state.
6.  **Foreground Conflict Resolution:** conflicts during the human's foreground merge open the 3-way `DiffViewerControl`; the Coordinator is unaffected because it never merges.

---

# Mainguard Velopack & OOBE Implementation Details

Velopack extracts the application silently. When `Mainguard.exe` starts, it checks `SetupComplete`; if false, it loads the OOBE View.

## 1. The First-Run Flow (No Identity Fork)
*   There is **no "Vibe vs Dev" installer fork**. One flow, full transparency (raw commands surfaced). A simplified UI view is an in-app preference toggled any time.
*   **Requirements gate first:** Windows 11 x86_64 check, WMI (`Win32_ComputerSystem`) VT-x/AMD-V verification, disk-space check — fail fast with actionable guidance before any system change.

## 2. System Diagnostics & On-Demand Elevation
*   The setup UI runs unelevated; it requests UAC only when the user clicks "Construct Sandbox".
*   Feature check: `(Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State`; enable if `Disabled`.
*   **Reboot handling:** create a high-privilege Windows Scheduled Task with a `--resume` flag (not a `RunOnce` key, which drops privileges); resume exactly where setup left off.

## 3. WSL Abstraction & `MainguardOS` Import
*   **Tarball payload:** barebones rootfs with `dockerd`, `git`, `python3`, `node/npm`.
*   **Silent import:** `wsl.exe --import MainguardEnv "<AppData path>" "<tarball>"`, then generate `.wslconfig` resource caps.
*   **Update pipeline (NEW):** the tarball is versioned with an in-place VM upgrade path and a defined CVE patch cadence for `dockerd` and the base OS — Velopack updates the app; this pipeline updates the VM.

## 4. Windows OS Integration
*   **Context Menus:** `HKEY_CLASSES_ROOT\Directory\shell\Mainguard` → "Open with Mainguard".
*   **OAuth (REVISED):** all OAuth flows use the **RFC 8252 loopback redirect (`127.0.0.1` ephemeral port) with PKCE**. Custom URI schemes are invocable by any website and leak tokens into URL logs; `mainguard://` is registered **only for non-secret deep links**. Backend-detected agent OAuth URLs include `state=<agent_uuid>` so the callback routes to the correct sandbox.

## 5. Agent Provisioning & Authentication (Post-Verify)
*   Wizard presents supported CLIs (Claude Code, AGY, OpenCode) with API key input (primary) or the CLI's own OAuth (with ToS disclosure).
*   **Pinned Adapter Channel (REVISED):** install per-release **tested, pinned** CLI adapter versions — never "latest". Adapters ship through a separately versioned channel updated independently of app releases, so monthly CLI breakage doesn't require full app updates (this also keeps perpetual-fallback licenses functional).

## 6. Clean Teardown (The Uninstaller)
*   `wsl.exe --terminate MainguardEnv` → poll `wsl.exe -l -v` until "Stopped" (releases `.vhdx` locks) → `wsl.exe --unregister MainguardEnv`. Never `wsl --shutdown` (kills the user's personal instances).
*   **Data Safety:** the user's source lives on the Windows drive; the VM repo is a mirror. Teardown is zero-data-loss — the Windows repository simply loses the `mainguard-vm` remote.

---

# Mainguard Vibe Mode: Implementation Details (POST-V1)

**Architectural Law:** the environment is 100% identical to Developer Mode — same `MainguardOS`, PTY streams, agent CLIs, and Git engine — driven programmatically by the **`VibeOrchestrator`** inside `Mainguard.Server`. **Sequencing:** the orchestrator ships with developer-mode v1 (the Coordinator reuses it); the standalone Vibe product ships later, targeting cloud delivery.

## 1. The Unified Shared Environment
### A. The `MainguardOS` Pre-Baked Sandbox
*   Identical to the main roadmap Phase 7.2; the daemon auto-checks required CLI adapters and updates them via the pinned adapter channel.

### B. Authentication (REVISED)
*   **Primary path: API key / pay-as-you-go.** The backend PTY parser still supports CLI OAuth: on detecting an auth URL, it emits `[AUTH_REQUIRED]` with the URL (containing `state=<agent_uuid>`), the UI opens the browser, and the loopback+PKCE callback routes the token to the correct sandbox.
*   **ToS-risk disclosure is mandatory** for subscription OAuth (Anthropic's April 2026 restriction); Mainguard drives the official CLI binary, but this path is treated as at-risk.

### C. Dev Server Port Harvesting
*   The PTY parser detects `http://localhost:([0-9]+)` and emits `[APP_READY_ON_PORT_X]` to the `VibeOrchestrator` and the client.

## 2. The Backend `VibeOrchestrator` Layer
### A. In-Memory Stream Routing (Auto-Healing)
*   The Orchestrator hooks the Dev Server and Agent CLI PTY streams in memory on the Linux server. On `ERR!`/stack traces, it writes the error directly into the agent CLI's stdin with a fix prompt — no bytes sent to the Windows UI.
*   **Resiliency:** backed by Session Durability (persistent session leaders) — the loop continues with the UI closed.
*   **Circuit Breaker:** hash the stack trace; 3 identical hashes or 5 errors in 10 minutes → hard-suspend and escalate to the **Escalation UX**.

### B. Escalation UX (NEW)
*   Plain-language triage screen with exactly three actions: **"Try a different approach"** (re-prompt with failure context), **"Go back to when it worked"** (one-click restore to the last green auto-checkpoint), **"Get help"** (diagnostic bundle export).

### C. Autonomous Git Abstraction (GAL)
*   **Auto-Checkpoints:** on successful generation loops, `GitService.StageAll()` + `Commit(author, "Auto-Checkpoint")` in the agent worktree.
*   **Autonomous Conflict Resolution:** on `MergeConflictException`, feed conflict markers to the agent CLI with a resolve prompt; finalize on success; escalate on failure.

## 3. The "Dumb" Vibe Mode UI
### A. Layout Toggle
*   Vibe Mode hides `TerminalViewModel`, `DiffViewerControl`, and `StagingTree`; locks a 2-pane split: Chat (left) + Embedded Web Preview (right). This is an in-app mode, not an install-time identity.

### B. Chat-to-Orchestrator Bridge
*   Chat input → gRPC → `VibeOrchestrator` → agent CLI stdin. The terminal engine preserves native CLI animations, spinners, and colors with no ANSI stripping (raw PTY rendering in 7.1a; grid damage updates convey identical visuals once 7.1b lands).

### C. The Embedded Live Preview (REVISED)
*   `LivePreviewControl` (`WebView2`/`CefGlue`) navigates to the dev server on `[APP_READY_ON_PORT_X]`, reached through the localhost bridge port-forward.
*   **Hot reload works because the dev server and the source files live together on ext4 with functioning inotify watchers.** (The prior claim that hot reload "works natively" via a shared Windows bind mount was false — inotify does not propagate over 9P mounts.)
