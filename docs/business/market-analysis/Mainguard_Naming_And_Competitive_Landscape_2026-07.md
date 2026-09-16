# Competitive Landscape & Naming Research

Research pass covering (1) every company/product already using "Aegis"-family names in the
AI-agent space, (2) the real product competitors building the same "orchestrate multiple
coding agents via git worktrees" idea Mainguard's roadmap describes, and (3) a fresh round of
name candidates now that Trellis and Vertex are confirmed taken.

All findings come from web search snippets (not live site visits), so treat feature claims as
"what they publicly say," not independently verified.

---

## TL;DR

- **"Aegis" is the default name the entire AI-agent-governance industry reaches for.** At
  least 20 distinct companies/projects use it, including a named Forrester analyst framework.
  Naming risk here isn't cosmetic — it's category confusion.
- **The bigger threat isn't naming, it's the market.** "Desktop app that orchestrates multiple
  coding agents in isolated git worktrees" is now a crowded, fast-moving category with real
  funded competitors (Conductor: $22M Series A) and, more importantly, two giants already
  shipping this exact feature set: **GitKraken (Kepler + Agent Mode)** and **GitHub itself**
  (the new Copilot desktop app, GA June 2026). Both have existing install bases Mainguard doesn't.
- **Naming is hard right now because the whole English lexicon of "control/guard/converge"
  words is being claimed weekly.** Every fresh candidate checked in this pass (Gantry,
  Manifold, Tributary, Interlock, Switchyard, Roundhouse) already has an AI-agent-space
  collision. Crossbar is the cleanest of the batch, but the deeper lesson is that a common
  English noun is unlikely to stay clean long enough to build a brand around — see the
  Naming section for what that implies.

---

## Part 1 — "Aegis"-branded companies/projects

### Direct category overlap (AI agent governance / runtime security / agentic coding)

| Name | What it is | Status | Backing |
|---|---|---|---|
| **Forrester's AEGIS Framework** | Analyst framework: 6 domains, 39 controls, for CISOs securing agentic AI enterprise-wide. Cross-maps to NIST AI RMF, ISO 42001, OWASP LLM Top 10, EU AI Act, MITRE ATLAS. | Published research (not software) | Forrester (major analyst firm) |
| **agentlifylabs/Aegis** | Open-source control plane between agent frameworks and the outside world. Least-privilege `aegis-manifest.json` per skill, cryptographic (Ed25519/SHA-256) audit log, deterministic replay. Supports 9 Python frameworks + 4 JS/TS + Go. | Live, documented | Independent OSS |
| **aegissecurity.dev (Aegis)** | Runtime policy engine (Go + OPA), stops privilege escalation / data leaks / cost blowups, sub-200ms latency, supports LangChain/CrewAI/OpenAI/Anthropic/Gemini. | Live product, flat pricing | Startup |
| **antgroup/agent-aegis (AgentAegis)** | 5-layer defense-in-depth runtime protection plugin for the "OpenClaw" agent runtime (foundation scan, input perception, cognitive-state monitoring, decision alignment, + one more layer). | Live, open source | Ant Group (Alibaba-affiliated) |
| **rtmx-ai/aegis-cli** | Terminal-native "agentic engineering CLI" — coding agent built for defense/regulated environments (GCP Assured Workloads, AWS GovCloud, Azure Gov, IL4/IL5). Streaming, human-in-the-loop approval gate, air-gapped model support. | Live, Apache-2.0 | Independent |
| **cleburn/aegis-cli + aegis-spec + aegis-mcp** | Three-part open governance stack: CLI generates a schema-validated `.agentpolicy/` directory from a discovery conversation; spec defines roles/permissions/autonomy/sensitivity tiers; MCP server enforces it at runtime with zero token overhead. | Live, on npm | Independent (Cleburn Walker) |
| **BuildSomethingAI/aegis-framework** | AI + structured project management framework; `.context/` directory for cross-session agent memory. v1.0 stable. | Live, OSS | Independent |
| **signal-x-studio/aegis-framework** (also forked as nino-chavez/aegis-framework) | Blueprint-driven engineering framework: traceable/replayable dev, agent behavior contracts, CI-safe automation, deterministic replay harness with kill-switch. | Live, OSS, npm-installable | Independent |
| **FixingPixels/Aegis, GanyuanRan/Aegis** | Two more distinct OSS repos: "Intelligent AI Framework for Structured Development" and "make AI coding agents architecture-aware" respectively. | Live, OSS | Independent |
| **aegisaiagent.com** | General AI agent platform: 100+ tools, HIPAA/SOC2, autonomous multi-agent orchestration, cloud/desktop/self-hosted, device-locked licensing. | Live, paid (7-day trial) | Startup |
| **aegis-enterprise.com (Aegis Enterprise)** | Enterprise framework for building/deploying/managing LLM-powered agents + retrieval systems, 4-week implementation program. | Live, paid | Startup |
| **RedHatProductSecurity/aegis-ai** | genAI agent for CVE/security-advisory analysis (impact/CWE/CVSS suggestions), integrates OSIDB/RHTPA/MCP. | Live, MIT, on PyPI | **Red Hat** |
| **aegisagentcontrol.com** | "Proof and license for AI agents" — treats AI like an employee on probation; policy is attached to the data itself rather than to subject access. | Landing-page stage | Startup |
| **aegisplatform.ai (Aegis AI Ops)** | Governed, air-gappable memory layer for enterprise agents ("CortexDB" context engine); traces fact sources, verifies truth, enforces access control. | Live | Startup (Cowbell listed as founding partner) |
| **aegisprotocol.ai / Aegis Protocol** | No-code multi-platform bot builder (Telegram/WhatsApp/X/Discord) — different product from the arXiv "Aegis Protocol" paper on autonomous-agent security, and from an unrelated DeFi "Aegis Protocol" token. | Live, freemium | Startup |
| **Infocion Aegis™** | AgenticOps governance control plane for Life Sciences/Healthcare — HIPAA/FDA/GxP-aligned audit trails, "Governance-as-a-Service." | Live, consulting-led | Infocion |
| **Aegis Core AI (aegiscoreai.com)** | Managed cybersecurity service, 24/7 monitoring — not agent-specific. | Live | Startup |
| **Aegent (aegent.io)** | "AI-Powered Analytics for Data Teams" — different vertical (data analytics), same coined word. | Live | Startup |
| **Aegent Dev (aegentdev.com)** | "Agentic security scanner." | Waitlist stage | Startup |
| **AEGYS (aegys.io) / AEGYS Datalytics** | Fractional CISO + hands-on AI security consulting; separately, SOC/XDR + data-lake ops. | Live (services) | Consulting firms |

### Same name, unrelated industry (lower risk — pure trademark noise, not conceptual competitors)

- **aegis.com** — physical security/investigations consulting firm
- **Aegis Authenticator** (`getaegis.app`, GitHub `beemdevelopment/Aegis`) — well-known open-source Android 2FA app; likely the single most recognizable "Aegis" product to a random person
- **Aegiq** — funded UK photonic quantum-computing company
- **Aegis Sciences / aegislabs.com** — clinical drug-testing corporation
- **useaegis.com** — RFID-blocking wallet cards
- **tryaegis.com** — health/bioassessment startup
- **YC's Aegis** (`aegishealth.us`) — AI agents that fight denied health-insurance claims; uses "AI agents" language but entirely different vertical (healthcare billing)
- **Aegis Core Tech** (`aegiscoretech.com`) — Romanian embedded-software/cybersecurity/automotive engineering services shop (~30 people), not a product
- **Aegis Forge family** — `aegisforge.io` (ad-compliance advisory), `aegisforgeio.net` (AI governance/regulatory tracking), `aegissafeforge.com` (safety-engineering drafting workspace), plus literal `theaegisforge.com` (leatherworking) and `aegisforgeknives.com` (knives)
- **Aegis Group / Aegys Group** — TPA operations, MRI safety, unrelated verticals

**Bottom line on Part 1:** the "Aegis" name is not just crowded, it is *the* name the AI-agent-governance industry converges on by default — over a dozen live products/frameworks doing runtime policy enforcement, audit logging, and agent behavior contracts, exactly the shape of Mainguard's planned "Enterprise AI Governance" roadmap item. Using it invites a buyer or journalist to assume Mainguard *is* one of these headless governance layers, when it's actually a full visual git GUI with agent orchestration bolted on top.

---

## Part 2 — Real product competitors (multi-agent git-worktree orchestration)

This is the more consequential finding: regardless of what Mainguard is named, this exact
product category — desktop tool that runs several coding-agent CLIs in parallel, isolated by
git worktree, with a UI to review/merge — already has real, live, sometimes well-funded
players.

| Product | What it does | Status / backing | Platform |
|---|---|---|---|
| **GitHub Copilot app** | Standalone desktop app turning Copilot into a "control center" for parallel agent sessions. Each session = its own isolated git worktree, auto-managed (no manual setup/cleanup). "My Work" view aggregates sessions, issues, PRs, background automations across repos. Ships "Agent Merge" (shepherds PRs through checks), inspectable canvases for plans/terminal output, local **and cloud** sandboxes. | **GA June 17, 2026.** Microsoft/GitHub — the single largest possible distribution: every GitHub/Copilot user. | Windows, macOS, Linux |
| **GitKraken Kepler + Desktop 12.0 "Agent Mode"** | Kepler: standalone Agentic Development Environment for directing agents across **multiple repos**; a "Task" holds one worktree per repo, tracks sessions, carries shared context. Agent Mode (in GitKraken Desktop itself): one panel showing every active agent session as a live-status card. Agent-agnostic: Claude Code, Codex CLI, Copilot CLI, Cursor, OpenCode all connect. | Live, shipped by an established git-GUI vendor with an existing paying user base | Desktop (GitKraken's existing platforms) |
| **Conductor** (`conductor.build`) | Mac app running Claude Code / Codex / Cursor in parallel, each an isolated git worktree. Uses your existing Claude Code login/plan. Integrates with GitHub (open PRs, read diffs, respond to review comments) and Linear. | **$22M Series A**, YC-backed, ~6-person SF team, currently free (paid collab features not yet shipped) | macOS only |
| **Nimbalyst** (formerly **Crystal**) | Open-source "visual workspace" for Claude Code/Codex: full code editor with inline AI diff review, markdown/mockup/diagram/data-model editors, session kanban, one-click per-session worktrees, mobile companion app. | Live, MIT-licensed, free individual tier | macOS, Windows, Linux, iOS companion |
| **Superset** (`superset.sh`) | Terminal-centric orchestrator: any CLI agent, one-click deep links into Cursor/VS Code/Xcode/JetBrains, built-in side-by-side diff viewer, notifications when an agent needs attention. | Live, OSS (Apache-2.0), $15/user/mo team tier, enterprise tier | Desktop |
| **Parallel Code** (`parallelcode.app`, GitHub `johannesjo/parallel-code`) | Native desktop app running real terminal CLIs (Claude Code, Codex, Gemini, Copilot CLI, Antigravity) side by side, each auto-created worktree, one-click merge from a sidebar. | Live, free, MIT, no proxying of API keys | Desktop |
| **Vibe Kanban** | Kanban board (To Do/In Progress/Review/Done) over git-worktree-isolated agents; supports 10+ agent backends; MCP-driven task-card creation; built-in browser w/ devtools. | Open source, community-maintained — **the company behind it (Bloop) shut down in early 2026**, hosted service discontinued | Web app |
| **Composio Agent Orchestrator** | Full automation: agents in isolated worktrees each get their own PR, supervised from one dashboard; autonomously fixes CI failures and responds to review comments — pushes furthest into autonomous PR lifecycle management. | Live, OSS | Self-hosted |
| **Factory.ai (Droids)** | Not worktree-specific, but the same underlying idea at larger scale: specialized agents (CodeDroid, Review Droid, QA Droid) run **in parallel by the thousands**; explicitly researching "how to converge multiple solution paths into one shippable result." SOTA on Terminal-Bench. | Live, funded, enterprise-facing | Cloud |
| **Terragon** | Cloud-VM-based agent orchestration (agent runs remotely, clones repo, opens PR). | **Dead** (noted for completeness) | — |

### What they have that Mainguard doesn't (yet)

- **Distribution.** GitHub Copilot app rides GitHub's existing user base; GitKraken Kepler rides GitKraken's existing paying customers. Mainguard would be starting from zero against both.
- **Agent-agnostic breadth, already shipped.** Every tool above already supports 5–10+ agent CLIs (Claude Code, Codex, Cursor, Gemini, Copilot CLI, etc.) — this is table stakes now, not a differentiator Mainguard can claim as novel.
- **Cloud sandboxes + cross-device continuity.** GitHub's app offers ephemeral cloud-hosted sandboxes in addition to local ones, so a session can follow a developer across machines — Mainguard's roadmap only describes local Docker/microVM sandboxes.
- **Autonomous PR lifecycle management.** Composio's orchestrator and GitHub's "Agent Merge" already handle CI-failure fixing and review-comment response autonomously; Mainguard's "Semantic Conflict Verification" concept (run the test suite before surfacing to the human) is narrower in scope by comparison.
- **Existing momentum/funding.** Conductor alone has more capital than most solo/small-team competitors combined, and is moving fast (weekly release cadence per its own materials).

### How Mainguard currently differs (and where it can win)

- **Git-craft as the foundation, not a bolt-on.** Mainguard already has a mature, native (Avalonia/Skia, LibGit2Sharp) git client: 60fps commit graph, hunk/line-level partial staging validated against `git apply`, a synchronized 3-pane (Ours|Result|Theirs) conflict editor, full worktree/branch/tag porcelain. None of the competitors above are primarily git GUIs — they're CLI-orchestration layers or kanban boards *added onto* the idea of git worktrees, several explicitly built by wrapping terminal CLIs in a shell (Conductor, Superset, Parallel Code). Mainguard is the only one entering from "excellent git GUI first."
- **"Middle Manager" synchronous collaboration.** None of the competitors describe a background daemon doing keep-alive rebases so agents continuously inherit a human's *live* edits on `main` while they work — most treat the human and the agents as fully separate until merge time.
- **True Docker/microVM isolation + semantic verification.** Most competitors rely on git-worktree isolation alone (a shared machine, separate directories) — real, but not hardware-isolated. Mainguard's planned per-agent Docker sandbox plus running the actual test suite as a merge gate is stricter than worktree-only isolation, closer in spirit to what GitHub's cloud sandboxes are moving toward, but framed for local, single-developer use rather than GitHub's cloud infrastructure.
- **Native OS-level terminals.** Real ConPTY/forkpty via `Pty.Net`, rendered with Skia — likely faster/more correct than the xterm.js-in-Electron pattern common in this category (several of the above are Electron- or Tauri-based web-tech apps).
- **"Vibe Mode."** None of the competitors target non-technical founders/designers who want results without seeing a terminal — everyone above assumes a developer user.
- **BYOK zero-exfiltration + cryptographic audit trail streamed to SIEM.** Conceptually close to what the "Aegis"-branded governance tools do (Part 1), but embedded inside a full visual GUI rather than shipped as a separate headless control-plane product.

### User-base overlap

- **Direct overlap:** individual developers and small teams experimenting with running multiple coding agents locally — this is exactly Conductor's, Nimbalyst's, Superset's, and Parallel Code's audience, and is presumably Mainguard's near-term target too.
- **Larger existential overlap:** any team already using GitKraken (which already sells into teams/enterprises) or already paying for GitHub Copilot (nearly every GitHub org) can get this functionality with **zero switching cost**, since it's an extension of a tool they already have installed and billed. This is the most serious competitive risk, independent of naming — Mainguard needs a clear reason to switch away from a tool already in a team's stack.
- **Adjacent, not overlapping:** the security/governance buyers of the "Aegis"-branded tools in Part 1 are typically CISOs/platform-security teams procuring a compliance layer, not developers picking a git GUI — different buyer, different budget line, so low direct competitive overlap there, but high *brand-confusion* overlap if Mainguard's name and enterprise-governance pitch resemble theirs too closely.

---

## Part 3 — Naming: new candidates (Trellis and Vertex ruled out)

Every fresh word checked this pass turned out to already be claimed somewhere in the AI-agent
tooling wave:

| Candidate | Collision found |
|---|---|
| Gantry | Gantry (MLOps platform, $28.3M raised) + a separate "Gantry AI" CAD-agent app |
| Manifold | Two funded AI-agent-security companies: Manifold Security ($8M seed, agent runtime detection) and a separate Manifold.ai research-agent analytics platform |
| Tributary | Tributary AI — agent-accountability/dev studio already using almost identical positioning language ("accountability for AI agents," audit trails) |
| Interlock | Interlock AI — safety/certification layer for AI infrastructure |
| Switchyard | NVIDIA-backed NeMo Switchyard — LLM-API-format translator for coding agents |
| Roundhouse | Roundhouse Digital Ltd — AI agent deployment infrastructure company (was preparing a London listing) |
| **Crossbar** | Only unrelated-industry hits: a youth-sports SaaS (`crossbar.org`), a ReRAM memory/hardware company (`crossbar-inc.com`), and an older open-source IoT messaging server (`crossbar.io`). **No collision found in the AI-agent-tooling category specifically** — the cleanest candidate of this batch, though the word itself isn't unclaimed in software generally. |

**The strategic point this raises:** virtually any evocative, real English word — guardian
words (Aegis, Sentinel), connection words (Synapse, Nexus, Confluence, Vertex), structural
words (Trellis, Lattice, Cadence), and now industrial/mechanical words (Gantry, Manifold,
Interlock, Switchyard, Roundhouse) — is already spoken for by a funded AI-agent startup, often
one founded in the last 6–12 months. This isn't bad luck on any particular word; it's what a
gold-rush category looks like. Two ways out:

1. **Keep hunting real words**, accepting that each new idea has meaningful odds of already
   being claimed, and budget for several more rounds of checking (Crossbar survived one pass;
   it should get a proper domain/trademark check before relying on it).
2. **Shift to a coined or portmanteau word** (the way Nimbalyst did) — an invented string is
   far less likely to collide, at the cost of needing more marketing to explain what it means.
   This was the logic behind "Aegent" earlier in this process, though that specific coinage
   also turned out to be taken (`aegent.io`, `aegentdev.com`).

---

## Open questions for next steps

- Want a proper domain/WHOIS + trademark screen specifically on **Crossbar** before treating it
  as a live option (this pass only used search-engine evidence, not authoritative registry data
  — see the caveat from the earlier Aegis domain check).
- Want another round of coined-word brainstorming (Nimbalyst-style) given how saturated real
  words have proven to be?
- Given GitKraken and GitHub are both already shipping the core "agent orchestration" vision,
  is there an appetite to revisit which *specific* differentiators (Middle Manager sync,
  Docker/microVM sandboxing, native terminals, Vibe Mode) get built and marketed first, since
  "multi-agent orchestration" alone is no longer a novel pitch on its own?
