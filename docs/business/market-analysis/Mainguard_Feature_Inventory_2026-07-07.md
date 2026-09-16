# Mainguard — Complete Competitor Feature Inventory & Gap Analysis

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../BUSINESS.md), [`GTM.md`](../../GTM.md) and [`GTM_Assets.md`](../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Date:** 2026-07-07
**Purpose:** Match-every-feature pass across the full competitive set, classifying each
competitor feature as SHIPPED / PLANNED / UNPLANNED for Mainguard, plus a consolidated gap
list, novel-feature proposals, and a priority ranking.
**Builds on (does not repeat):** `docs/business/market-analysis/Mainguard_Viability_And_Differentiation_2026-07.md`
and `docs/business/market-analysis/Mainguard_Naming_And_Competitive_Landscape_2026-07.md`.

### Scope & mapping notes (read first)

- The task brief referenced `Mainguard_Competitor_Research_2026-07-07.md` and a
  `Mainguard_Master_Implementation_Document_v2.md` with tasks `P2-01…P2-26` / `P2-C1…C3`.
  **Neither file exists in the repo.** The July strategic pass is the two market-analysis
  docs above; the v2 master doc is explicitly deferred ("receives this same
  deep-specification treatment in a v2 … once M3 is complete"). Therefore **PLANNED items
  are tagged with the real workstream IDs** from `Mainguard_Implementation_Strategy.md`
  (F6, G-7.x, H-8.x, I-9, J, K) and the differentiators D-1…D-6 from the viability doc.
  When the v2 doc lands, map these tags onto P2-xx.
- SHIPPED = T-01…T-33 (all merged to main per the master doc status line) plus the
  pre-existing baseline (stash push/list/pop/apply/drop, branch CRUD,
  reset/revert/cherry-pick/amend, basic worktrees, commit graph, GitHub device-flow auth,
  SQLite settings).
- Evidence labels: **[V]** = primary page fetched and read (vendor docs/changelog/blog);
  **[S]** = web-search snippet only, not independently verified. Sources at end.

---

## PART 1 — PER-COMPETITOR FEATURE INVENTORIES

Legend: ✅ = SHIPPED in Mainguard · 🔷 = PLANNED (workstream ID) · ❌ = UNPLANNED (gap; see Part 2)

### 1. GitHub Copilot app (GA June 17 2026; Win/mac/Linux) [V]

| Feature | Detail | Mainguard status |
|---|---|---|
| Sessions in auto-managed worktrees | one isolated worktree per agent session, zero manual setup/cleanup | 🔷 G-7.2b/G-7.3 (worktree porcelain itself ✅ T-07) |
| My Work view | one dashboard: active sessions + issues + PRs + background automations across repos | ❌ (we have per-repo PRs/issues/notifications T-23…T-27, no cross-repo unified dashboard) |
| Agent Merge | shepherds a PR through CI, required reviewers, failing checks; configurable automation level | 🔷 partial — D-1 merge queue verifies locally; host-side PR shepherding ❌ |
| Canvases | bidirectional work surfaces (plan, PR, browser session, terminal, deployment, dashboard); agent updates them, human edits/approves/redirects on same surface | ❌ (G-7.4 docking UI is panes, not shared editable surfaces) |
| Cloud sandboxes | ephemeral isolated Linux envs, no local resource limits | 🔷 I-9 (Phase 9, deferred) |
| Local sandboxes | restricted FS/network, centrally-enforced enterprise policy | 🔷 G-7.2c (ours is stricter: default-deny egress) |
| Copilot code review (agentic) | review agent w/ reasoning tiers, customizable via skills + MCP | ❌ (D-2 cockpit is risk-ranking/provenance, not an AI reviewer) |
| Security-review skill | dedicated security evaluation pass | ❌ (T-30 secret scanner ✅ is narrower) |
| Rubberduck skill | multi-model critique to find novel issues | ❌ |
| Memory++ | context maintained across devices and sessions | ❌ |
| Chronicle | query context from sessions across app/CLI/VS Code/GitHub | ❌ |
| Background automations | recurring agent jobs surfaced in My Work | ❌ |
| Partner agent apps | LaunchDarkly, Sonar, Endor Labs, PagerDuty, Miro etc. plug in as agents | ❌ (no extension surface) |
| Session continuity across machines | via cloud sandboxes + Memory++ | ❌ |

### 2. GitKraken platform — Desktop 12 + Kepler + GitLens/CLI [V]

**Kepler (ADE, shipped June 15 2026) [V]:**

| Feature | Detail | Mainguard status |
|---|---|---|
| Tasks | container of work spanning **multiple repos**, one worktree per repo, shared context | ❌ (Mainguard is single-repo per window) |
| Issue-tracker intake | start Tasks from Jira, Linear, Trello, GitHub Issues, GitLab Issues | ❌ (GitHub issues only, T-24 ✅) |
| PR-based intake | start a task directly from a PR in your queue | ✅ T-29 (PR→worktree checkout) |
| Kanban board | Exploration / In Development / In Review / Done pipeline | ❌ |
| Session status filtering | Needs Attention / Active / Idle / Errored / Inactive | 🔷 G-7.3/G-7.4 (status model planned; explicit filter UX unspecified) |
| Console per session + side-by-side multi-session comparison | | 🔷 G-7.1/G-7.4 |
| Mid-session redirection | steer with **text, images, or voice** | 🔷 text (G-7.5 manual mode); images/voice ❌ |
| Agent-agnostic | Claude Code, Codex, OpenCode | 🔷 G-7 adapters |
| Diff + staging + commit inside Kepler, linked to originating task/session | | ✅ core; session-linkage 🔷 G-7.3 |
| Git-provider breadth | GitHub, GH Enterprise, GitLab, GitLab Self-Managed | ❌ beyond GitHub (auth ✅ T-14, integrations ❌) |

**Desktop 12.x release notes [V]:** Agent Sessions View (worktree cards: branch, uncommitted,
ahead/behind, PRs — ✅/🔷 mix; we have worktree UI T-21 ✅, agent cards 🔷 G-7.4); per-worktree
setup commands (🔷 G-7.2b); multi-session terminal per worktree (🔷 G-7.1); PR merged-status
pills (✅ T-26); rebase context menus (✅ T-09); command-palette IDE detection (✅ T-18 partial);
shallow-clone settings (❌ minor); resizable repo-management columns (✅).

**Platform (GitLens sync, [V]/[S]):**

| Feature | Detail | Mainguard status |
|---|---|---|
| Launchpad | unified PR/issue/task dashboard across repos, categorized by needs-attention | ❌ |
| Workspaces | named repo groups synced across Desktop/GitLens/CLI/web, multi-repo actions | ❌ |
| Cloud Patches | share WIP diffs via GitKraken cloud **before commit/PR** | ❌ |
| Code Suggest | propose edits to anyone's PR across whole repo (beyond GitHub suggestion blocks) | ❌ |
| Insights (DORA) | velocity/DORA metrics | ❌ (T-22 ✅ is repo analytics, not team DORA) — skip-tier |
| Focus View (GitLens) | your PRs/issues inside VS Code | n/a (IDE surface) |
| GitKraken CLI | `gk` — workspace/graph/Launchpad from terminal | ❌ (no CLI surface) |
| AI commit messages / AI in graph | multiple models (Sonnet 4.6, Gemini 3.1) | ❌ deliberate skip per viability doc; reconsider as cheap BYOK checkbox |
| Predictive conflict detection | warns two humans' branches will conflict | 🔷 D-5 cross-worktree conflict radar (ours is agent-scale) |

### 3. Conductor (Mac, $22M Series A) [V — full changelog]

| Feature | Mainguard status |
|---|---|
| Dispatcher (⌘N prompt-first workspace creation) | ❌ (nice-to-have UX) |
| Create workspace from Linear/GitHub issue (⌘I) | ❌ Linear; GitHub issue ✅-adjacent (T-24/T-29) |
| Run scripts (multiple, auto-run after setup, per-repo config `conductor.json`) | 🔷 G-7.2b setup commands; multiple named run scripts ❌ |
| Browser preview: multiple named preview URLs, localhost cookie-auth support | ❌ for dev use (K-4 live preview is Vibe-mode only) |
| Checkpoints (save/restore agent session state) | ❌ (T-19 undo journal ✅ covers git refs, not conversation/session state) |
| Fork workspaces (duplicate a session to try another path) | ❌ |
| Plan mode + interactive planning | 🔷 G-7.5 plan approval (weaker: approval, not co-editing) |
| Code review in-app + custom review models | ❌ AI review; human diff review ✅ |
| PR create/edit, PR checks + Actions view, checkout PRs, PR comment sync | ✅ T-23/T-25/T-26/T-29 |
| Multiple chats per workspace, chat continuation, chat search, summaries/titles | ❌ (G-7 UI unspecified at this depth) |
| Message queues (queue follow-up prompts while agent busy) | ❌ |
| Tool approval system | 🔷 G-7.2d admission control (ours is gateway-level) |
| Terminal mode, "Claude reads your terminal", env vars, direnv | 🔷 G-7.1 |
| MCP support, Bedrock/Vertex/custom providers | 🔷 F6/G-7 adapters partial |
| Mark files viewed; historical diffs; markdown/file preview | ❌ mark-viewed; historical diffs ✅ (T-07/T-12) |
| Command palette, keyboard nav, workspace pinning/grouping/search, unread markers | ✅ palette T-18; workspace organization UX ❌ minor |
| Vercel deployment integration | ❌ (K-5 is Vibe-only) |
| Notes tab per workspace | ❌ minor |
| Archive workspaces | 🔷 G-7.3 session durability (persist/close); explicit archive UX unspecified |

### 4. Nimbalyst (open-source, Win/mac/Linux + iOS) [V]

| Feature | Mainguard status |
|---|---|
| Monaco code editor in-app | ❌ — deliberate-skip candidate (we are a Git client, not an editor) |
| WYSIWYG markdown editor w/ red-green diff approval of AI edits | ❌ — skip |
| Excalidraw canvas (AI-driven diagramming) | ❌ — skip |
| Mermaid diagrams, mockup/wireframe editor, CSV/spreadsheet editor, ERD/data-model editor | ❌ — skip |
| Extension marketplace w/ custom editors | ❌ — skip |
| Session kanban (backlog/planning/implementing/complete) | ❌ |
| Session search, resume, branching, archiving | ❌ search/branching; resume/archive 🔷 G-7.3 |
| Session-to-file linking (what the agent read/wrote) | ❌ (H-8.2 audit records it; surfacing as UX ❌) |
| iOS companion: start/monitor sessions, review diffs, voice instructions | ❌ |
| Task tracker + inline tags (@task @idea @bug @decision) + cross-plan dashboard | ❌ — skip-ish |
| Real-time multiplayer on docs/tasks/diagrams | ❌ — skip |
| Context graph linking plans/specs/diagrams/sessions/files/decisions | ❌ |
| MCP server integration, custom slash commands, skills, CLAUDE.md config | 🔷 partial (adapters); skills-management UI ❌ |
| Visual git interface, worktrees, diff viewing, branch management, file history w/ restore | ✅ |
| AI commit-message generation | ❌ (see GitKraken row) |
| Permission system w/ trust levels + FS access controls | 🔷 G-7.2c/d |
| Local-first plain-markdown storage; BYOK; SOC 2; MIT OSS | 🔷 F6 BYOK; H-8.1 source-available |

### 5. Superset (OSS core, teams $15/u/mo) [V — full changelog]

| Feature | Mainguard status |
|---|---|
| Scheduled Automations ("cron for agent sessions"), prompt version history, run queue w/ Mine/Team filter | ❌ |
| TypeScript SDK mirroring CLI 1:1 (workspaces/tasks/projects/automations from code/CI) | ❌ |
| Superset CLI (single static binary driving same backend) | ❌ (G-7.0 daemon+gRPC is the natural substrate) |
| MCP server — external agents drive Superset itself | ❌ |
| Slack bot: @superset spawns workspaces, triages tasks, reads thread context, link unfurling | ❌ |
| Remote workspaces (point local app at any Superset device; auto port-forwarding) | 🔷 I-9-adjacent; LAN-remote model ❌ |
| Persistent terminals — PTY daemon hands off FDs across app updates | 🔷 G-7.0/G-7.3 explicitly plans daemon-owned session durability ("zombie prevention") |
| Multi-tab/split/pane terminals, terminal search, presets, cross-workspace shell dropdown | 🔷 G-7.1/G-7.4 partial; terminal search ❌ minor |
| In-app browser (URL autocomplete, DevTools, history) + port detection/badges/scanner | ❌ |
| GitHub-style multi-file diff pane, changes sidebar, PR check status inline, edit-in-diff | ✅ mostly (T-13/T-25/T-26); edit-mode-in-diff ❌ minor |
| Review tab: read/filter/respond to PR review comments in-app | ✅ T-25 |
| File explorer, CodeMirror editor, fuzzy file search, git decorations | ❌ editor (skip); file tree ✅-partial |
| External editor deep links (VS Code, JetBrains, Windsurf…) | ❌ (small; "Open in Terminal" ✅ T-18) |
| Multi-agent support incl. Claude/Codex/Gemini/Copilot/Cursor + custom terminal agents | 🔷 G-7 adapters |
| Interactive tool approval; permission modes Auto/Semi/Manual | 🔷 G-7.2d |
| Task list w/ semantic search (embeddings); Linear create/update/triage; GitHub issue attach | ❌ Linear + semantic search |
| Theme marketplace, custom keybindings | ❌ minor (5 themes ✅) |
| AI branch-name suggestions; push auto-setup upstream | ❌ minor / ✅ |
| Mobile app (workspaces, task triage, agent chat, live terminal viewer) — in review | ❌ |
| macOS dock badges, native toasts, workspace activity strip | 🔷 G-7.4-adjacent; needs explicit spec |
| SOC 2 + pen test; relay security setting | 🔷 H-8.x |

### 6. Sculptor (Imbue) [V product page; docs 404'd]

| Feature | Mainguard status |
|---|---|
| Agents in **containers** (not worktrees), safe parallel execution | 🔷 G-7.2 (ours: WSL2 + hardened containers, default-deny egress — stricter) |
| Pairing Mode — one-click sync of agent's container work into local repo, **bidirectional** (agent sees your edits/comments live) | 🔷 partial — "Middle Manager" keep-alive rebase (roadmap) is one-way (main→agent); bidirectional pairing ❌ |
| Container snapshots (cheap, packfile-skipping) — reopen any past session with plans/chat/tool calls/code intact | ❌ (session-state snapshots; T-19 covers git refs only) |
| Session history & resume without re-prompting | 🔷 G-7.3 partial |
| Suggestions — automatic issue-flagging on agent code before merge (incl. "tests passed without real tests" deception detection, instruction-file compliance) | ❌ — closest competitor to our verification thesis; D-2 should absorb this |
| Multi-agent exploration — same task, several agents, switch between | ❌ (no compare-N-attempts UX planned) |
| Merge interface w/ automatic conflict detection/resolution options | ✅ T-02…T-04 (ours is deeper) |

### 7. Cursor 3 (April 2026) [S]

| Feature | Mainguard status |
|---|---|
| Agents Window — one pane over local, worktree, cloud-VM, and SSH agents | 🔷 G-7.4 (local/worktree); cloud/SSH ❌/🔷 I-9 |
| Multi-model comparison — one prompt → N models side-by-side, pick winner | ❌ |
| Plan Mode — researched, clarifying-questions, editable plan before build | 🔷 G-7.5 plan approval (approval ≠ co-edited plan doc) |
| Cursor Cloud sandboxed VMs | 🔷 I-9 deferred |
| Design Mode — annotate/target UI elements in embedded browser, agent iterates | ❌ — skip-tier for now |
| **Agent Trace** — open spec (JSON trace records: code ranges ↔ conversations/contributors; storage-agnostic, git-notes-friendly, content hashes survive refactors; backed by Cursor + Cognition + Google Jules) | ❌ — **high-fit gap**: D-2 plans proprietary `Agent:`/`Task:`/`Plan:` trailers; emitting/consuming the industry standard is unplanned |

### 8. OpenAI Codex app [V — official features page]

| Feature | Mainguard status |
|---|---|
| Multiple projects per window; threads pinned/archived/continued across projects; project-less chat threads | ❌ multi-project window (we're one-repo-per-window); thread mgmt 🔷 G-7.3 |
| Execution modes: local / worktree / cloud per thread | 🔷 local+worktree G-7.2; cloud I-9 |
| Built-in diff viewer + **inline comments on diffs that the agent addresses** | ❌ the comment→agent loop (human diff review ✅) |
| Stage/revert chunks or files; commit/push/PR from app; integrated terminal | ✅ T-06 etc. |
| Skills + skills picker + team skill libraries | ❌ skills-management UI |
| Standalone Automations (scheduled) + thread automations ("recurring wake-ups" preserving context) + results land in a **review queue** | ❌ |
| Subagents — parallel specialized agents w/ progress visibility | ❌ surfacing (vendor-side feature; our adapters just host the CLI) |
| In-app browser for local dev servers + file-backed previews | ❌ |
| Computer use (operate macOS/Windows apps) | ❌ — skip |
| Image generation + image input drag-drop | ❌ image input to agent; gen — skip |
| Voice dictation (Ctrl+M) | ❌ |
| Floating pop-out thread windows | ❌ minor (Avalonia multi-window is cheap) |
| Artifact viewer (PDF/spreadsheet/docs/slides) | ❌ — skip |
| Task sidebar tracking agent plans + artifacts; notifications; sleep-prevention toggle | 🔷 G-7.3/7.4 partial; sleep toggle ❌ trivial |
| IDE-extension sync (shared thread visibility, "files you viewed" context) | ❌ — skip-tier |
| MCP config, approval + sandbox settings, web search toggle | 🔷 G-7.2d |

### 9. Google Jules [V — changelog]

| Feature | Mainguard status |
|---|---|
| Cloud async agent: bugs/deps/migrations/features → PRs | n/a (vendor agent; Mainguard should **intake** its PRs — D-1 vendor-neutral intake 🔷) |
| REST API + Jules Tools CLI (parallel task execution, scriptable) | ❌ equivalent public API/CLI for Mainguard |
| GitHub Action (cron-triggered agent runs in CI) | ❌ |
| Scheduled tasks; suggested tasks (proactive #TODO scanning) | ❌ both |
| Auto-fix CI failures on its own PRs | ❌ (D-1 detects fail; remediation loop unplanned) |
| Read + respond to PR review comments | ❌ autonomous response |
| Memory (remembers corrections/preferences per repo) | ❌ |
| Interactive plan + Planning Critic (second agent reviews the plan) + Critic agent on code w/ visible reasoning | ❌ critic agents (G-7.5 plan approval is human-only) |
| Environment snapshots (cached setup for fast starts); env vars per repo; multi-runtime | 🔷 G-7.2a/b (MainguardOS image caching analogous) |
| Stacked diff viewer, side-by-side diffs, image rendering in diffs | ✅ T-13 |
| Commit authoring options (agent-only / co-authored / user-only) | ❌ small but audit-relevant |
| File output export as git patch | ❌ minor (patch export generally absent) |
| MCP integrations (Linear, Supabase, Neon…), Render build-fix integration | ❌ |
| Web-app testing w/ screenshots | ❌ (verification-adjacent; interesting) |

### 10. Minor orchestrators — features the majors lack [S]

- **Vibe Kanban** (OSS, community-run after Bloop's shutdown): kanban over 10+ agent
  backends; **MCP-driven task-card creation**; built-in browser **with devtools**;
  PR-style review. Gap rows already covered (kanban ❌, MCP server ❌, browser ❌).
- **Composio Agent Orchestrator** (OSS): fully autonomous PR lifecycle — plans tasks,
  spawns agents, **autonomously fixes CI, resolves merge conflicts, answers review
  comments**. The autonomy ceiling of the category; our governed answer is D-1 + policy.
- **Parallel Code** (MIT): same-task-across-multiple-models A/B comparison with one-click
  merge of the winner — cleanest expression of the multi-model compare gap (❌).
- **Orca** (stably.ai, OSS ADE): desktop + **mobile monitoring/steering**, notify-on-finish,
  follow-ups from phone; any CLI agent (12+ named). Reinforces mobile-remote gap (❌).
- **Pane / runpane** (OSS, terminal-first, **Windows x64+ARM64**/mac/Linux): "pane chat"
  global orchestrator terminal, agent loops, **self-hosted Remote Pane** (manage from
  phone), **isolated ports per agent**, **secrets sync**, cross-terminal context. Only
  other Windows-serious player; isolated-ports and secrets-sync ideas worth stealing into G-7.2.
- **Emdash** (YC W26, OSS Electron): breadth — **~22 CLI providers**; Linear/Jira/GitHub
  intake; kanban; capable in-app browser (search, zoom, screenshots).
- **Intent** (paid platform, positioned against Emdash): [S] closed platform, no distinct
  verified feature beyond the category set.
- **Cmux** (manaflow, OSS): every run gets an **isolated VS Code workspace** (cloud or
  local Docker) with diff view/terminal/dev-server preview; devcontainer.json standard;
  **Vercel preview URLs + preview comments per run**; scriptable embedded browser for
  agent-driven verification. "Verification interfaces" is literally their pitch — closest
  philosophical neighbor among the small tools.

### 11. Classic Git clients — remaining client-parity gaps

**Tower [V — all-features page]:** undo any op with Cmd/Ctrl+Z (✅ T-19 equivalent);
reflog (✅ T-20); drag-drop merge/cherry-pick/rebase (✅/partial T-09); AI commit messages
w/ custom prompts + provider choice Claude Code/Codex (❌); **commit templates** (❌ minor;
T-31 composer ✅ covers conventional commits); gitmoji (❌ trivial); **Custom Workflows**
(user-defined branching automation, parent-child branch sync) (❌); git-flow (❌ — skip);
**partial stash via drag, incl. untracked** (❌ — our stash UI is baseline); patch
create/apply (❌); services: GitHub+**GitLab+Bitbucket+Azure DevOps+Beanstalk** PR
management (❌ beyond GitHub); external diff-tool integration (❌); 1Password SSH agent
(❌ minor); multiple windows (❌ minor).

**Fork [S]:** visual interactive rebase drag-drop (✅ T-08/T-09); merge-conflict helper
(✅ T-04, ours deeper); image diff with **side-by-side / swipe / onion-skin modes**
(✅ T-13 image diff; the 3 modes unverified in ours — check parity); diff text search
Ctrl+F (❌ minor — verify); repo manager, blame, line-history (✅).

**Sublime Merge [S]:** command palette (✅ T-18); 40+ language syntax highlighting (✅ T-13);
3-pane merge tool (✅ T-04); **standalone merge-tool mode (`smerge` CLI helper — use the
client as `git mergetool` from any terminal)** (❌); packages/extensibility (❌ — skip-tier);
submodule + git-flow via palette (✅ submodules T-16; flow ❌ skip).

**GitButler [S]:** **virtual/parallel branches** — multiple independent branches applied
to one working directory simultaneously, commit to either (❌ — biggest classic-client
gap; see Part 2); **stacked branches with automatic restacking** on edit (❌); **oplog:
snapshot before *every* operation including working-directory contents**, full undo
timeline (✅-partial — T-19 restores refs w/ clean-tree guard but does **not** snapshot
uncommitted tree contents); CLI (`but`) praised independently (❌ — no Mainguard CLI);
agent integrations w/ branch-per-agent (🔷 G-7).

**lazygit [S]:** interactive rebase with single-key reorder/squash/fixup/edit/drop (✅);
**git bisect UI** (❌ — nothing in Mainguard ships or plans bisect); custom commands/config
(❌ — skip-tier); nested-panel worktree/stash/reflog browsing (✅).

---

## PART 2 — CONSOLIDATED "WE LACK IT" LIST

Every deduplicated feature Mainguard neither ships (T-01…T-33 + baseline) nor plans
(F6/G/H/I/J/K + D-1…D-6). Demand signal: strong = shipped by ≥3 competitors incl. a
major, or a top user-visible pitch; medium = 2+; weak = 1 or niche.

### Theme A — Orchestration UX

**A1. Session kanban / pipeline board** — Kepler, Nimbalyst, Vibe Kanban, Emdash. **Strong.**
Sketch: a `SessionBoardViewModel` over the G-7.3 session store (daemon-owned, gRPC-streamed
states: Queued/Planning/Working/NeedsAttention/Verifying/InReview/Merged/Archived — note our
pipeline naturally has *more* meaningful columns than competitors because verification and
merge-queue states are first-class). Avalonia `ItemsRepeater` columns of session cards
reusing the T-21 worktree-card visuals; drag between columns issues gRPC transitions;
filters mirror Kepler's Needs-Attention/Active/Idle/Errored. Ship as an alternate layout of
the G-7.4 activity bar, not a separate app area.

**A2. Multi-model / multi-agent one-prompt comparison ("bake-off")** — Cursor 3, Parallel
Code, Sculptor exploration, Conductor fork-workspace. **Strong.**
Sketch: "Dispatch to N" in the session-create dialog: same prompt, N adapter configs
(Claude/Codex/Gemini or same model different seeds), N sibling worktrees branched from the
same SHA. A comparison view diffs the N result branches pairwise (we already have
branch-vs-branch diff T-07/T-13) plus verification results per attempt from D-1 — our
differentiator: *ranked by verification outcome, not vibes*. One-click "adopt attempt k,
archive rest" (archive = branch kept + session snapshot).

**A3. Session checkpoints & fork-session** — Conductor checkpoints/fork, Sculptor container
snapshots, Codex forked threads, Nimbalyst session branching. **Strong.**
Sketch: two layers. Git layer exists (T-19 journal); add (a) conversation/state snapshots —
the G-7.0 daemon persists adapter transcript + PTY scrollback + env manifest to SQLite at
checkpoints (auto: before each user redirect; manual: button), and (b) worktree tree
snapshots via `git stash create`-style dangling commits recorded in the journal (also
closes the GitButler-oplog gap, F2 below). "Fork from checkpoint" = new worktree at the
checkpoint SHA + transcript replay into a fresh adapter session.

**A4. Message queue for busy agents** — Conductor. **Medium.**
Sketch: per-session FIFO in the daemon; UI composer gains "queue" state when the adapter
is streaming; delivered on idle. Trivial on top of G-7.0; disproportionate daily-use value.

**A5. Multi-repo tasks / workspaces** — Kepler Tasks, GitKraken Workspaces, Codex
multi-project window. **Medium** (strong for teams).
Sketch: a lightweight `RepoGroup` entity in `AppDbContext` (name + repo paths + shared
context note). MainWindow gains a group switcher; a Task can hold one session per member
repo, sharing the prompt/context blob. Keep `IGitService.ExecuteWithRepo` per-repo — the
group is purely an orchestration/UI construct; no cross-repo git operations in v1.

**A6. Dispatcher / prompt-first session creation** — Conductor, Copilot app. **Medium.**
Sketch: extend the T-18 command palette with a "New session:" mode — type prompt, pick
repo+agent+base branch inline, Enter spawns. Cheap; reuses palette infra.

**A7. Session/chat search** — Conductor, Nimbalyst, Superset semantic task search. **Medium.**
Sketch: FTS5 (SQLite, already in stack) over daemon-persisted transcripts + session
metadata; palette-integrated. Skip embeddings in v1.

**A8. Subagent/plan progress visibility** — Codex subagents, Codex task sidebar, Copilot
canvases (plan surface). **Medium.**
Sketch: parse adapter stream-json events (Claude Code and Codex both emit structured
tool/subagent events) into a live plan/task tree beside the terminal — read-only in v1.
This is also the substrate for audit (H-8.2) so it's dual-purpose.

### Theme B — Review

**B1. AI reviewer / automated suggestions on agent diffs** — Sculptor Suggestions, Copilot
code review + Rubberduck, Conductor review w/ custom models, Jules critic. **Strong.**
Sketch: absorb into D-2 as a "second-opinion pass": a *reviewer* adapter session (any
configured CLI, F6 BYOK) runs in a read-only sandbox against the candidate branch with a
fixed rubric prompt (tests-really-test-things, instruction-file compliance, security),
emitting SARIF-ish findings pinned to hunks in the review cockpit. Crucially: findings are
recorded in the H-8.2 audit chain — "an independent model reviewed this diff" becomes a
compliance artifact no competitor produces.

**B2. Inline diff comments the agent addresses** — Codex app; (Sculptor pairing comments). **Strong.**
Sketch: comment gutter in our diff view (T-13) writing to a session-scoped comment store;
"Send to agent" serializes file:line + comment + hunk context into the adapter session as
a steering message. Closes the loop that today requires copy-paste. Also feeds D-2's
review-throughput pitch.

**B3. Mark-files-viewed with rebase-surviving state** — Conductor (viewed), GitHub PR UX. **Medium.**
Sketch: per-review checkbox stored keyed by `git patch-id` (stable across rebases) rather
than path+sha — a Git-native twist competitors lack; surfaces in D-2's ranked list as
progress tracking.

**B4. Review queue for automation output** — Codex automations queue, Superset run queue. **Medium** (contingent on E1).
Sketch: the D-1 merge-queue panel gains a source column; scheduled runs (E1) deposit
result branches into the same verify→review pipeline instead of a separate inbox.

### Theme C — Collaboration / team

**C1. Cloud-Patch-style WIP sharing (pre-commit/pre-PR)** — GitKraken Cloud Patches +
Code Suggest. **Medium.**
Sketch: defer the hosted service; ship the Git-native 80%: "Share as patch link" = push a
`refs/mainguard/patches/<id>` ref to the existing remote (or export `.patch` file — also
closes Tower patch parity), with an import flow on the other side. A hosted relay is a
later SaaS decision.

**C2. Session sharing links / handoff** — Conductor links, Superset links+Slack unfurl,
Copilot cross-device continuity. **Medium.**
Sketch: `mainguard://session/<id>` deep link (J-4 already plans protocol registration for
OAuth loopback) resolving via daemon; same-machine/team-LAN first, cloud continuity waits
for I-9.

**C3. Real-time multiplayer docs/canvases** — Nimbalyst multiplayer, Copilot canvases. **Weak** for our buyer. Deliberately skip (see ranking).

### Theme D — Integrations

**D1. Issue-tracker intake: Linear + Jira (then Trello)** — Kepler (Jira/Linear/Trello),
Conductor (Linear), Superset (Linear), Emdash (Linear/Jira), Jules (Linear MCP). **Strong** — the single most uniform gap across every major.
Sketch: `IIssueProvider` abstraction in Core beside the existing GitHub client (T-24);
Linear GraphQL + Jira REST v3 with PAT/OAuth via the T-14 keyring; "Start session from
issue" (issue → branch name via T-31 conventions → worktree → adapter prompt seeded with
issue body) mirroring the shipped T-29 PR flow. Status write-back (In Progress/In Review)
on session transitions.

**D2. GitLab/Bitbucket/Azure DevOps PR-level parity** — Tower, GitKraken, Kepler
(GitLab incl. self-managed). **Strong** for enterprise/Windows (our wedge).
Sketch: generalize T-23…T-29's GitHub client behind an `IForgeProvider`; GitLab first
(MR list/create/review/checks/notifications map ~1:1; self-managed base-URL support —
auth already ships in T-14). Azure DevOps second (Windows-enterprise overlap), Bitbucket last.

**D3. Slack integration** — Superset bot, Jules API-to-Slack. **Medium.**
Sketch: outbound-only v1 — webhook notifications on NeedsAttention/verification-fail/
merge-complete with deep links (C2). A conversational bot is post-daemon-API (E2) work.

**D4. Deployment/preview integration (Vercel/Render)** — Conductor, cmux, Jules. **Weak-medium.** Defer; K-5 covers the Vibe path. Revisit if D-1 intake shows demand.

**D5. Partner/extension agent surface** — Copilot partner apps, Nimbalyst marketplace. **Weak** now. Skip until the daemon API (E2) exists — then it's "free" as an API consumer story.

### Theme E — Automation / API surface

**E1. Scheduled automations with reviewable output** — Codex Automations, Superset
automations (+prompt versioning), Jules scheduled tasks, Copilot background automations. **Strong.**
Sketch: daemon-side cron (Quartz.NET or a simple timer table in SQLite — daemon already
persists sessions) firing headless adapter sessions in G-7.2 sandboxes; every run lands as
a branch in the D-1 queue (B4). Windows-native bonus: register a scheduled task so runs
happen with the app closed (daemon is a service — J workstream already installs services).
Prompt version history = rows in the existing DB.

**E2. Public CLI + SDK + REST/gRPC API over the daemon** — Superset CLI/TS SDK/MCP, Jules
API/CLI, GitKraken CLI, GitButler CLI. **Strong.**
Sketch: G-7.0's gRPC contract *is* the API — commit to it as public: ship `mainguard` CLI
(thin gRPC client, single binary via NativeAOT), generate a small C# + TS SDK from the
protos, and add grpc-gateway-style JSON transcoding for REST. Everything the GUI can do,
scripts/CI can do — and it's mostly already-planned plumbing, just promoted to a contract.

**E3. MCP server exposing Mainguard to agents** — Superset, Vibe Kanban, Nimbalyst. **Strong** (cheap once E2 exists).
Sketch: MCP server (stdio + SSE) wrapping the gRPC API: `create_session`, `queue_merge`,
`get_verification_status`, `get_review_findings`, `undo_last_operation` (journal as a
safety tool for agents — unique), all gated by G-7.2d admission policy + H-8.2 logging.
"Your orchestrator agent drives Mainguard, and every call is on the audit chain."

**E4. Autonomous CI-fix / review-comment response loop (governed)** — Jules, Composio,
Copilot Agent Merge, Conductor comment sync. **Strong.**
Sketch: D-1 extension — on verification/CI failure or new PR review comment, policy may
auto-spawn a *remediation session* scoped to that branch (bounded attempts, diff-size cap,
flagged-paths still human-gated). Same for host-side: Agent-Merge-like shepherding built
on shipped T-26 checks + T-25 review data. Our angle vs Composio's full autonomy:
every remediation is a policy decision on the audit chain.

**E5. GitHub Action / CI-side trigger** — Jules action, Superset SDK-from-CI. **Medium.** Trivial consumer of E2; publish a marketplace action later.

**E6. Repo-level agent memory** — Jules memory, Copilot Memory++. **Medium.**
Sketch: per-repo `MemoryStore` (SQLite + optional committed `.mainguard/memory.md`) that
adapters get injected as context; entries written on explicit "remember this" and on
review-cockpit corrections (rejected-suggestion → note). Committed-file mode keeps it
team-shared and diff-reviewable — Git-native, unlike vendor cloud memory.

**E7. Suggested tasks (TODO/tech-debt scanning)** — Jules. **Weak.** Defer; cheap later on top of E1.

### Theme F — Client git features

**F1. Virtual/parallel + stacked branches** — GitButler (flagship). **Medium** demand,
high wow. Sketch: full GitButler parity is a rewrite of their core; ship the pragmatic
subset instead: (a) "split working copy into branches" wizard — cluster uncommitted
changes by path/hunk (reusing T-06's patch model) and commit selections to different
new branches without touching the index twice; (b) stacked-branch awareness: mark
branch B as stacked on A, auto-restack (rebase B) when A moves, using shipped T-08
plumbing + T-19 undo as the safety net. Explicitly *not* a persistent virtual-branch
working mode.

**F2. Working-directory snapshots (oplog-grade undo)** — GitButler oplog, Tower undo. **Medium.**
Sketch: before every mutating op, `git stash create`-equivalent dangling commit of the
dirty tree recorded in the T-19 journal row; undo restores refs *and* tree. Removes our
current clean-tree-only undo limitation; also powers A3 checkpoints. Low effort, directly
strengthens an already-shipped differentiator.

**F3. git bisect UI** — lazygit, (Fork). **Medium.**
Sketch: guided panel over `git bisect` CLI (right side of the G-7 policy split): pick
good/bad from the shipped graph, step with build/test-command auto-run (reuse D-1's
verification runner!) for `bisect run` automation, result annotated on the graph.
Verification-thesis synergy: "which agent commit broke it."

**F4. Standalone merge-tool mode** — Sublime Merge `smerge`. **Medium.**
Sketch: `mainguard mergetool <file>` CLI verb (E2) opening the shipped T-04 3-pane resolver
against `$LOCAL/$BASE/$REMOTE/$MERGED`; registerable as `git mergetool`. Turns our best
shipped surface into a trojan horse for terminal users.

**F5. External diff/merge tool hand-off** — Tower, Fork. **Weak-medium.** Settings entry +
process launch (via the shipped scheme-validated launcher pattern). Trivial parity item.

**F6. Partial stash + stash polish** — Tower (drag files→stash, untracked). **Weak-medium.**
Sketch: stash backend shipped; add UI: multi-select files → "Stash selected"
(`git stash push -- <paths>` CLI side), include-untracked toggle.

**F7. Patch file create/apply** — Tower, Jules export. **Weak.** `format-patch`/`am`
wrappers + drag-out; pairs with C1.

**F8. Custom workflows / branch automation** — Tower Custom Workflows, git-flow
(Tower/Sublime/GitKraken). **Weak** (declining pattern). Skip git-flow; a minimal
"branch naming + base rules per repo" setting covers 80%.

**F9. Commit templates + gitmoji** — Tower. **Weak.** Fold into shipped T-31 composer as
saved templates; gitmoji = one picker. Trivial.

**F10. AI commit messages (BYOK)** — Tower 16, GitKraken, Nimbalyst, Superset branch names. **Medium**
(checkbox demand; buyers screen for it). Sketch: despite the viability doc's "skip
gimmicks" stance, this is now a *parity* checkbox, not a differentiator — one prompt to a
BYOK model (F6 keys) over the staged diff, output into the T-31 composer with convention
enforcement. A day of work; removes a checklist loss.

**F11. Diff-view text search** — Fork. **Weak.** Verify ours; add Ctrl+F overlay if absent.

### Theme G — Editors / preview

**G1. In-app dev-server browser preview + port detection** — Conductor (named preview
URLs), Superset (browser+DevTools+ports), Codex, cmux, Emdash, Pane (isolated ports),
Vibe Kanban (devtools). **Strong** — near-universal in the category.
Sketch: promote K-4's Vibe-mode WebView to a general per-session preview pane: Avalonia
WebView (WebView2 on Windows — native, not Electron), port detection by polling
`/proc/net/tcp` inside the WSL2 sandbox (G-7.2 owns the netns, so *we know the ports
authoritatively* — cleaner than competitors' scanning), named preview URLs in repo config.
DevTools via WebView2's built-in. Sandbox-aware forwarding = our twist: preview traffic is
the *only* whitelisted ingress.

**G2. Lightweight in-app file editor** — Superset (CodeMirror), Nimbalyst (Monaco), Orca. **Medium.**
Sketch: keep Mainguard not-an-IDE, but add edit-in-place for small fixes during review:
AvaloniaEdit (native, already ecosystem-standard) behind an "Edit" toggle on the diff/file
view, save + auto-stage. Stop there; deep-link to VS Code/JetBrains (F5-style setting) for
real editing — matches Superset's external-editor pattern.

**G3. Visual-doc editors (WYSIWYG md, Excalidraw, Mermaid, CSV, ERD, mockups)** —
Nimbalyst suite. **Weak** for our buyer. Skip except: render-only Mermaid/markdown
*preview* in the diff/file viewer (review-relevant, cheap).

**G4. Voice input** — Codex dictation, Kepler voice redirection, Nimbalyst iOS voice. **Medium.**
Sketch: Windows-native `Windows.Media.SpeechRecognition` (or Whisper via BYOK) into the
session composer. Low effort, high demo value; not load-bearing.

**G5. Image input to agents** — Codex, Kepler, Jules. **Medium.** Paste/drag into
composer → temp file into sandbox mount → path reference in the adapter message.
Small; needed for UI-bug workflows.

**G6. Computer use / desktop automation / Design Mode browser control** — Codex, Superset
MCP tools, Cursor Design Mode. **Weak** for us. Skip (v1/v2); revisit Design-Mode-style
element annotation only after G1 lands.

### Theme H — Mobile / remote

**H1. Companion app / remote monitoring (phone)** — Nimbalyst iOS, Orca mobile, Superset
mobile, Pane Remote (self-host). **Strong** trendline (4 competitors), medium for our
enterprise buyer.
Sketch: don't build native mobile v1. The daemon (G-7.0) + E2 API enables a responsive
**local-web dashboard** (daemon serves a small SPA over localhost/LAN with the same gRPC-web
API; auth via device pairing) for monitor/approve/redirect — the Pane self-host model,
which also fits security-conscious buyers (no vendor cloud). A store app is a later
packaging of the same API.

**H2. Cross-device session continuity** — Copilot Memory++/cloud sandboxes. **Medium.** Deferred with I-9; H1's remote view covers the 80% (observe/steer from elsewhere).

### Theme I — AI assistance / provenance

**I1. Agent Trace (open standard) emission + consumption** — Cursor spec; Cognition +
Google Jules aligned. **Strong** strategic fit.
Sketch: adopt instead of invent — D-2's planned proprietary trailers become an Agent Trace
implementation: daemon writes trace records (JSON, git-notes storage per spec's
storage-agnostic design) mapping hunks→session/plan/model; blame/diff gutters (shipped
T-11/T-13) render them; importer consumes traces emitted by Cursor/Jules PRs arriving via
D-1 intake. We'd be the first *reviewer-side* consumer of the standard — pure upside for
the audit story (H-8.2 chains the records).

**I2. Critic/planning-critic agents** — Jules planning critic + code critic, Copilot
Rubberduck. **Medium.** Covered by B1's reviewer-session design (make the rubric
pluggable: plan-review vs code-review vs security).

**I3. Web-app testing with screenshots as verification evidence** — Jules, cmux. **Medium.**
Sketch: optional D-1 verification step: Playwright (already .NET-friendly) inside the
sandbox runs a smoke script, screenshots archived as verification artifacts next to test
logs. Extends "verification" beyond unit tests toward what cmux markets.

---

## PART 3 — "THINK OF MORE": NOVEL FEATURES NO COMPETITOR SHIPS

All chosen to compound the governed-verification thesis + real-Git-engine advantages.

1. **Merge-train simulation ("pre-flight octopus").** Before anything merges, dry-run the
   entire D-1 queue in order in a scratch worktree: sequential rebase+merge of all queued
   agent branches, reporting *pairwise and transitive* conflicts and a combined
   verification run. Competitors verify branches one-by-one against main; nobody shows
   "what main looks like after all five land."
2. **Verification cache & receipts.** Content-addressed verification results keyed by
   `(merge-base SHA, branch tree hash, test command hash)` — re-queues after unrelated
   merges hit cache instead of re-running; every pass/fail is a signed receipt on the
   H-8.2 chain. Turns D-1 from "runs tests" into an auditable, dedupe-able ledger.
3. **Review receipts + human-review coverage map.** Signed, hash-chained records that
   human X viewed hunk Y at commit Z (B3's patch-id state, made compliance-grade), and a
   repo heatmap of "lines merged with zero human eyes" by agent/model/date. The EU-AI-Act
   artifact nobody else can produce because nobody else owns both blame and review state.
4. **Per-agent signing identities.** Daemon mints an SSH signing key per agent identity
   (T-15 signing ✅ + keyring ✅); every agent commit is signed `agent-claude-3@daemon`,
   countersigned on human approval at merge. Attribution at the *Git object* level —
   survives clone/fork, unlike any vendor's metadata. (Complements I1, exceeds it.)
5. **Quarantine remotes (Git-native egress).** Each sandbox's only reachable remote is an
   ephemeral bare repo owned by the daemon; `push` from inside is always allowed (agent UX
   intact), but promotion from quarantine to the real remote happens only via the D-1
   verified pipeline. Prompt-injected `git push --force origin main` becomes structurally
   impossible — stronger than any competitor's "we block network."
6. **Semantic lockfile & manifest diff.** Parse package-lock/pnpm-lock/csproj/poetry.lock
   in the diff view: show the actual dependency delta (added pkg X\@v, maintainer changed,
   install scripts present), local OSV-database CVE check, integrated with the shipped
   T-30 scanner and D-1 flagged-paths gate. Reviewing a 9,000-line lockfile diff is today's
   worst agent-review moment; nobody addresses it.
7. **Agent flight recorder synced to the graph.** PTY recordings (G-7.1 owns the
   terminal) indexed by time→commit→hunk: select a hunk in review, scrub to the exact
   moment the agent wrote it, see the tool calls around it. Chronicle-like, but anchored
   in Git objects and replayable offline for audit.
8. **Test-impact-ordered verification.** Maintain a coverage map (test↔file) from prior
   verification runs; on queue entry, run the impacted subset first for a fast preliminary
   verdict, full suite before merge. Cuts the D-1 feedback loop from minutes to seconds
   for most branches — makes the merge queue feel instant vs competitors' full-CI waits.
9. **Undo journal as an agent-facing MCP tool.** Expose `checkpoint`, `undo_to`,
   `list_operations` (T-19 ✅) via E3 so agents can *safely* self-revert under policy
   instead of `git reset --hard`ing themselves into corruption. Wrappers can't offer this
   without owning a journal.
10. **Cross-worktree semantic conflict radar, symbol-level.** Upgrade planned D-5 from
    line-overlap to symbol overlap (tree-sitter parse of touched functions/types): "agent
    A and agent B are both editing `AuthService.Login` right now — pause one?" Predictive,
    live, N-way; GitKraken's version is human-branch, line-level, post-hoc.
11. **Review-sprint mode.** A timed, keyboard-only review flow over the D-2 ranked hunks
    with a per-session "risk budget" — the cockpit schedules exactly what fits your 20
    minutes, defers the tail, records deferred-unreviewed hunks in the coverage map (#3).
    Operationalizes the "five branches in twenty minutes" marketing promise.
12. **Sandbox health & exfiltration panel.** Live per-agent view of blocked egress
    attempts, secret-file access attempts, and anomalous process spawns from G-7.2c/d
    telemetry — "your agent tried to POST to pastebin at 14:02" as UI, streamed to the
    audit log. Verifiable trust as a *visible* daily feature, not a whitepaper.
13. **Reviewed-in-parts rebase preservation.** When an agent branch is squashed/rebased
    (D-5 commit-stream curation), migrate review state, comments, and Agent Trace records
    across via patch-id/content-hash mapping so curation never resets review progress.
    Every competitor loses review state on history rewrite.
14. **Merge-decision replay ("audit verify" for humans).** One command re-derives the
    entire chain for any commit on main: which plan approved it, which agent produced it,
    which verification receipt covered it, which human reviewed which hunks, hash-chain
    intact. The demo that closes enterprise deals; pure composition of #2/#3/#4 + H-8.2.
15. **Adapter-crash forensic resume.** When an agent CLI dies (429, OOM, network), daemon
    snapshots PTY + transcript + worktree state and offers "resume in fresh session with
    reconstructed context" — session durability (G-7.3) upgraded into recovery UX
    competitors handle by "start over."

---

## PART 4 — PRIORITY RANKING

### Must-match (parity gaps that cost deals today)
1. **D1 — Linear + Jira intake** (uniform across all majors; cheapest strong signal)
2. **G1 — In-app dev-server preview + sandbox-native port handling** (category table stakes)
3. **E1+B4 — Scheduled automations landing in the review queue** (Codex/Jules/Superset/Copilot all have it)
4. **A1 — Session kanban/status board** (the default mental model users now expect)
5. **E2/E3 — Public CLI/SDK + MCP server over the G-7.0 daemon** (promote planned plumbing to product; unlocks E5, D5, H1)
6. **A3+F2 — Checkpoints + working-tree snapshots** (Conductor/Sculptor headline; also fixes our undo's clean-tree limitation)
7. **B2 — Inline diff comments → agent** (the review loop-closer; feeds D-2)
8. **I1 — Agent Trace emit/consume** (standard is consolidating now; replaces planned proprietary trailers before they ship)
9. **B1/I2 — Governed AI reviewer pass** (Sculptor Suggestions/Copilot review parity, done audit-grade)
10. **D2 — GitLab MR parity** (enterprise/Windows wedge demands non-GitHub)

### Should-match (fast follows)
A2 multi-agent bake-off · E4 governed CI-fix/review-response loop · A4 message queue ·
A6 dispatcher · A7 session search · F3 bisect UI · F4 standalone mergetool · F10 AI commit
messages (checkbox) · G2 light in-place editing + external-editor deep links · G4/G5
voice + image input · H1 daemon-served remote/LAN dashboard · C1 patch-based WIP sharing ·
C2 deep links · E6 repo memory · D3 Slack notifications · F6/F7/F9/F11 small client polish ·
A5 repo groups (lite) · A8 plan/subagent visibility · I3 screenshot verification

### Deliberately skip (with reason)
- **Nimbalyst editor suite (Monaco/WYSIWYG/Excalidraw/mockups/CSV/ERD) + extension
  marketplace** — different product thesis (workspace/IDE); massive surface; our buyer
  reviews and merges, they edit elsewhere. Render-only md/Mermaid preview is the 5% worth taking.
- **Computer use / desktop automation / Design Mode** — off-thesis, security-surface
  explosion inside our own sandbox story.
- **Real-time multiplayer docs / canvases as editable surfaces** — infra-heavy
  (CRDT/hosting), weak signal from our buyer; revisit only with a team-cloud offering.
- **Cloud execution at Copilot/Cursor/Jules parity** — capital-intensive, off-thesis;
  unchanged Phase-9 stance. Instead *intake* their PRs (D-1) and offer H1 remote view.
- **DORA/Insights dashboards** — GitKraken checkbox for eng-managers, not our reviewer/
  compliance buyer; T-22 analytics suffices for v1.
- **git-flow automation** — declining workflow; superseded by trunk-based + merge queue (our D-1).
- **Full GitButler virtual-branch working mode** — architecturally invasive (their whole
  client is built around it); F1's split-wizard + stacked-restack captures the observed
  jobs-to-be-done at 10% of the cost.
- **Artifact viewers (PDF/slides), image generation, theme marketplace** — nice-to-haves
  with no thesis contribution.

---

## SOURCES

**[V] = page fetched and read directly; [S] = search-snippet evidence only.**

- [V] GitHub Copilot app announcement — https://github.blog/news-insights/product-news/github-copilot-app-the-agent-native-desktop-experience/
- [S] Copilot app GA coverage — https://webdeveloper.com/news/github-copilot-app-generally-available/ ; https://www.infoq.com/news/2026/06/github-copilot-app/ ; https://www.helpnetsecurity.com/2026/06/08/github-copilot-app-ai-coding-agents/
- [V] GitKraken Kepler — https://www.gitkraken.com/kepler
- [V] GitKraken Desktop release notes — https://help.gitkraken.com/gitkraken-desktop/current/
- [S] GitKraken platform (Launchpad/Workspaces/Cloud Patches/Code Suggest/CLI/Insights) — https://www.gitkraken.com/features/launchpad ; https://www.gitkraken.com/features/cloud-patches ; https://www.gitkraken.com/cli ; https://help.gitkraken.com/gitlens/gl-cloud-patches/
- [V] Conductor changelog — https://www.conductor.build/changelog (incl. 0.63.0 Dispatcher); [S] docs — https://docs.conductor.build/core/scripts
- [V] Nimbalyst features — https://nimbalyst.com/features/ ; [S] https://github.com/nimbalyst/nimbalyst
- [V] Superset changelog — https://superset.sh/changelog ; [S] docs — https://docs.superset.sh/automations ; https://docs.superset.sh/mcp ; https://docs.superset.sh/sdk/reference
- [V] Sculptor product page — https://imbue.com/sculptor/ ; [S] changelog — https://docs.imbue.com/changelog (features page 404'd during research)
- [S] Cursor 3 — https://cursor.com/changelog/3-0 ; https://www.digitalapplied.com/blog/cursor-3-agents-window-design-mode-complete-guide ; https://www.datacamp.com/blog/cursor-3
- [S] Agent Trace spec — https://github.com/cursor/agent-trace ; https://www.infoq.com/news/2026/02/agent-trace-cursor/
- [V] Codex app features — https://developers.openai.com/codex/app/features ; [S] https://developers.openai.com/codex/app/automations ; https://developers.openai.com/codex/subagents ; https://developers.openai.com/codex/skills
- [V] Jules changelog — https://jules.google/docs/changelog/ ; [S] API — https://developers.google.com/jules/api ; Action — https://github.com/google-labs-code/jules-action
- [S] Vibe Kanban — https://github.com/BloopAI/vibe-kanban ; https://vibekanban.com/
- [S] Composio Agent Orchestrator — https://github.com/ComposioHQ/agent-orchestrator
- [S] Parallel Code — https://parallelcode.app (via aggregator descriptions)
- [S] Orca — https://www.onorca.dev/ ; https://github.com/stablyai/orca
- [S] Pane — https://runpane.com/ ; https://github.com/dcouple/Pane
- [S] Emdash — https://emdash.sh/ ; https://github.com/generalaction/emdash ; Intent — https://www.augmentcode.com/tools/emdash-vs-intent
- [S] Cmux — https://github.com/manaflow-ai/cmux ; https://manaflow-ai-cmux.mintlify.app/features/browser
- [V] Tower all-features — https://www.git-tower.com/features/all-features ; [S] Tower 16 — https://alternativeto.net/news/2026/5/tower-16-for-mac-launches-with-ai-commit-messages-redesigned-working-copy-view-and-more/
- [S] Fork — https://git-fork.com/ ; https://fork.dev/blog/
- [S] Sublime Merge — https://www.sublimemerge.com/ ; https://www.sublimemerge.com/docs/command_palette
- [S] GitButler — https://gitbutler.com/ ; https://docs.gitbutler.com/features/branch-management/stacked-branches ; https://docs.gitbutler.com/features/branch-management/virtual-branches ; https://matduggan.com/gitbutler-cli-is-really-good/
- [S] lazygit — https://github.com/jesseduffield/lazygit ; https://viadreams.cc/en/blog/lazygit-guide/

**Internal references:** `docs/planning/Mainguard_Master_Implementation_Document.md` (T-01…T-33 shipped, §1 baseline),
`docs/planning/Mainguard_Implementation_Strategy.md` (F6/G-7.x/H-8.x/I-9/J/K planned workstreams),
`docs/business/market-analysis/Mainguard_Viability_And_Differentiation_2026-07.md` (D-1…D-6).
