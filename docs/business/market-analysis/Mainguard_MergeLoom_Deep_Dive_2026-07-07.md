# MergeLoom (mergeloom.ai) — Deep Competitive Dive

**Date:** 2026-07-07 · **Prepared for:** Mainguard competitive planning (phase-2 / Master Implementation Doc v2, branch `phase2`)
**Method:** Direct page fetches of mergeloom.ai (homepage, /product/* ×8, /solutions/* ×4, /pricing, /docs, /docs/getting-started/install-worker, /compare/* ×3, /subprocessors, /terms, /refund-policy, /blog, sitemap), GitHub org fetch, LinkedIn company page fetch, 5 web searches.
**Evidence standard:** Every claim tagged **[V]** = verified by direct fetch of the cited page, **[S]** = verified only via search snippet, **[I]** = inferred/unverified. Note: fetches were summarized by an extraction model, so exact wording may vary slightly from the live page; feature claims are as-marketed, not independently tested.

**Note on task inputs:** The referenced `Mainguard_Competitor_Research_2026-07-07.md` does not exist in the repo; the "§New threats" summary supplied in the tasking was used as-is and independently confirmed against the live site. The v2 master doc exists only on the `phase2` branch (`docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`); all P2-xx references below are from that file.

---

## 0. What MergeLoom is, in one paragraph

MergeLoom is a **headless, workflow-embedded "governed ticket-to-code" pipeline**: an approved ticket in Jira/Linear/GitHub/GitLab/Azure Boards/monday.dev triggers a run that assembles cross-repo context from a pre-built index ("Context Engine"), has an AI provider implement the change, pushes it through a six-gate validation-and-repair runway ("Quality Agents" + "Diff Guard"), and hands a validated PR/MR back to the code host for **mandatory human review** — with a ticket→run→validation→PR audit trail and per-run cost telemetry. It has **no IDE, no desktop app, no Git client, and no interactive agent session**; the human touchpoints are the tracker, the code host PR page, and a web "Controller." Pricing is outcome-based: £4 (cloud) / £2 (self-hosted) per opened PR/MR after 50 free runs. [V — homepage, /product/, /pricing/]

The company is **extremely small and new**: LinkedIn shows **1 employee, founded 2025, 25 followers** [V — LinkedIn company page]; the GitHub org has 2 repos (installer + Helm chart, ≤1 star, created/updated May–June 2026) [V]; blog and terms date from May–June 2026 [V]; no funding, founder, customer, or press trail exists anywhere searched [V — multiple searches returned nothing off-domain]. This is a threat because of its **positioning** (it occupies exactly the "governed AI delivery" ground Mainguard's P2 plan targets, and it is *live and billing today*), not because of its resources.

---

## 1. Complete feature inventory (by lifecycle stage)

### 1.1 Intake

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Tracker integrations | Ticket intake from **Jira, GitHub Issues, GitLab Issues, Azure Boards, monday.dev, Linear** | /product/work-intake-integrations/ | **[V]** |
| Label/status/query routing | Labels, ticket statuses, and query filters decide what gets picked up; configured once in Controller UI; repository aliases route work to repos | /product/work-intake-integrations/ | **[V]** |
| Approval-gated start | Runs start only from *approved* work items (status/label/assignment triggers) — "starts from approved work, not developer-created issues" | /solutions/ticket-to-code-automation/, /compare/vibe-kanban/ | **[V]** |
| Epic import & sync | Jira Epic → "delivery campaign"; child issues become campaign nodes; "Sync Epic" re-syncs when Jira changes; epic-level repo labels with child-level overrides | /solutions/jira-epic-delivery/ | **[V]** |
| Notifications | Slack + Microsoft Teams listed as notification integrations | /product/work-intake-integrations/ | **[V]** (depth unknown) |
| Confluence as context source | Confluence listed as an approved context source | /product/work-intake-integrations/, /product/repository-context-rules/ | **[V]** |

### 1.2 Context

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Context Engine / "vault" | Indexes approved repos + docs into a persistent knowledge base ("reusable system memory, not throwaway context windows") | /product/repository-context-rules/ | **[V]** |
| Cross-repo architecture graph | Relationship graph of "files, symbols, APIs, events, docs, and service relationships"; tracks upstream APIs, downstream services, packages, events, background jobs — read-only across repos | same | **[V]** |
| Delta sync | After baseline index, tracks added/modified/deleted files instead of full re-index; monthly AI-credit "refresh budgets" cap auto-update cost | same | **[V]** |
| Bounded context packs | Each run gets a scoped context pack with "exact source paths, confidence metrics, and indexed commit histories" attached through to the PR | same | **[V]** |
| AGENTS.md-aware | Repository guidance via AGENTS.md files, validation commands, and coding rules | same | **[V]** |
| Include/exclude scoping | Approved-sources whitelist, path include/exclude rules | same | **[V]** |

### 1.3 Generation

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Cloud model execution | **Anthropic only** in cloud mode, via MergeLoom-managed credits | /product/work-intake-integrations/, /product/cloud-hosted-ai-coding/ | **[V]** |
| Self-hosted BYO provider | **Codex CLI, Claude Code CLI, OpenAI-compatible tool-calling endpoints, Vertex AI, AWS Bedrock, Azure Foundry**; static creds or workload identity / IAM roles | /product/self-hosted-ai-coding-infrastructure/, /docs/getting-started/install-worker/ | **[V]** |
| Standardized run inputs | Every run gets same "Ticket, Context, Rules" input phase — anti-"vibe coding" repeatability pitch | /product/consistent-prompting-validation/ | **[V]** |
| Interactive steering mid-run | **Absent.** No terminal, no chat, no mid-run human interaction is described anywhere | all pages | **[V absent]** |

### 1.4 Validation / repair

The "six-gate quality runway" [V — /product/consistent-prompting-validation/]:

| Gate | What it does | Status |
|---|---|---|
| 1. Clarity Check | Validates ticket scope + acceptance criteria before any code | **[V]** |
| 2. Investigation | Reads target files, system context, related services | **[V]** |
| 3. Validation | Runs setup, lint, typecheck, tests, custom commands — configured per repository (unit/integration/regression) | **[V]** |
| 4. Repair Loop | Bounded auto-fix of validation failures, "repair or stop" — halts if repairs exceed scope limits; all attempts logged | **[V]** |
| 5. Specialist Review ("Review Agent") | AI review of the diff across product fit, technical risk, quality dimensions | **[V]** |
| 6. Diff Guard | Blocks oversized or off-scope changes before handoff (scope drift / line-volume policing) | **[V]** |

Plus: **failed/blocked runs are not billed** [V — /pricing/ FAQ], which financially aligns them with gate strictness.

### 1.5 Review / handoff

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| PR/MR handoff with evidence | Clean branch + PR/MR to GitHub/GitLab/Azure Repos with validation results, run history, cost telemetry attached | /product/, /solutions/automated-pr-validation/ | **[V]** |
| Mandatory human review | "No Auto-Merge" — merge stays behind the customer's branch protection and approval rules, always | multiple pages | **[V]** |
| Review UI of their own | **Absent.** Review happens in the code host; MergeLoom provides no diff/review surface beyond the PR itself | all pages | **[V absent]** |

### 1.6 Merge / coordination

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Epic Delivery slices | Epic broken into dependency-and-risk-ordered "delivery slices"; parallel independent workstreams; earlier slices establish contracts/data shapes for later ones | /product/epic-delivery/, /solutions/jira-epic-delivery/ | **[V]** |
| Campaign controls | Pause/resume, replan, skip/retry slice, refresh review; per-slice cost + gate status dashboard | same | **[V]** |
| Merge queue / stale re-verification | **Absent.** No re-validation when main moves, no cross-PR conflict handling, no queue. Slices meet only at PR time | all pages | **[V absent]** |

### 1.7 Audit / reporting

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Ticket→PR traceability chain | Links ticket, run, requester, repo, provider path, validation output, PR/MR | /product/audit-trails-attribution/ | **[V]** |
| Three audit views | **Controller Audit** (workspace), **Ticket Audit** (work item history), **Code Audit** (line-level change attribution) | same | **[V]** |
| Retry/repair history | All repair attempts logged as evidence | /product/consistent-prompting-validation/ | **[V]** |
| Cost telemetry | Per-run token spend, infra vs token split, per-repo and per-provider allocation, latency/duration, connected to the PR outcome | /solutions/reduce-ai-costs/ | **[V]** |
| Honest scope disclaimer | "Strong attribution applies to work routed through MergeLoom" only — local AI work untracked | /product/audit-trails-attribution/ | **[V]** |
| Tamper-evidence / hash chain / SIEM export | **Absent.** No cryptographic integrity, signing, or SIEM claims found | all pages | **[V absent]** |
| Compliance certifications | **None claimed.** "Security Review Ready" documentation for procurement; GDPR/data/cookie policy pages; no SOC 2 / ISO 27001 | /product/cloud-hosted-ai-coding/, legal pages | **[V absent]** |

### 1.8 Admin / governance

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Agent Fleets | Named codebase agents with "mandate, scope, budget, file rules, cadence, and review boundary" — recurring maintenance (security hygiene, doc drift, dependency updates, standards) | /product/agent-fleets/ | **[V]** |
| Rate/budget caps | Daily PR/MR cap (example: 6/day), open-review cap (example: 8), AI-usage budget per fleet; pause/resume per agent | same | **[V]** |
| Self-learning system | "Learns from rejected PRs and validation failures; incorporates Review Agent findings into future runs" | homepage | **[V claim, mechanics unverified — [I] likely context/rules feedback, not model training]** |
| Controller UI | Central web app (controller.mergeloom.ai): integration setup, routing rules, queue state, dashboards, billing | /docs/, homepage nav | **[V]** |
| OIDC/SAML | Enterprise tier only | /pricing/ | **[V]** |
| RBAC / SCIM / roles | **Not mentioned anywhere.** | all pages | **[V absent]** |
| No-training guarantee | "Customer data is not used to train models" | homepage footer | **[V]** |

### 1.9 Deployment / infra

| Feature | What it does | Evidence | Status |
|---|---|---|---|
| Cloud hosted | MergeLoom-managed runtime, tenant-isolated; temp execution data deleted post-run; logs/prompts/traces follow workspace settings | /product/cloud-hosted-ai-coding/ | **[V]** |
| Self-hosted worker | Checkout, context assembly, provider calls, validation, branch push all inside customer VPC; **controller (SaaS) still coordinates** — queue state, routing, PR metadata leave the boundary | /product/self-hosted-ai-coding-infrastructure/ | **[V]** |
| Install path | Docker Compose (`MergeLoom/mergeloom-worker-install`, install.sh) or Helm (`mergeloom/mergeloom-worker` OCI chart v1.0.4); image `mergeloom/mergeloom:1.0`; gateway + scalable executors; local worker UI at `127.0.0.1:8010`; **Linux/K8s only** | /docs/getting-started/install-worker/ | **[V]** |
| Secrets | Platform-managed secrets (K8s/Docker), workload identity; "no vendor-held API keys in MergeLoom cloud" for self-hosted | same | **[V]** |
| Sandbox / egress hardening | **Not described.** No microVM, default-deny egress, or container-hardening claims found | all pages | **[V absent]** |
| Env vars prefixed `JCA_*` | e.g. `JCA_WORKER_CONTROL_PLANE_URL` — suggests a pre-rename codebase ("J... Coding Agent(s)", plausibly Jira-first origin) | /docs/getting-started/install-worker/ | **[V var names; interpretation [I]]** |
| Public API / CLI / status page | **None found.** Docs have no API reference; no status page discovered | /docs/, searches | **[V absent]** |

---

## 2. Business model

**Pricing** [V — /pricing/]:

| Tier | Price | Notes |
|---|---|---|
| Cloud | **£4 per PR/MR** after 50 free runs | Includes 2,500 AI credits; extra credits £20 min for 2,000; Anthropic-only |
| Self-hosted | **£2 per PR/MR** after 50 free runs | BYO provider, no credits included |
| Enterprise | Custom volume, **500+ runs/month minimum** | Adds OIDC/SAML; cloud or self-hosted |

**Outcome mechanics** [V]: only a run that *opens* a PR/MR bills; blocked/failed/validation-failed/policy-blocked runs are free. Extra runs bought in bundles of 5. No seats, no contracts, cancel anytime, 14-day refund on first charge [V — /refund-policy/]. Marketing claims **~£6–£7 all-in per completed run** (fee + AI + infra) vs "~£100 per ticket" of engineer time → "90%+ savings" [V — /solutions/reduce-ai-costs/; the £100 baseline is their assertion, [I] as to realism].

**Target buyer & motion** [V/I]: engineering leaders and platform/compliance-minded teams ("prevents vibe coding from becoming an organisational risk"); GBP pricing + GDPR page suite → UK/EU-first [I]. Motion is self-serve (register → 50 free runs) plus Cal.com demo booking; paid acquisition via **Reddit Ads and LinkedIn Ads pixels** [V — /subprocessors/ lists both with IDs]; and a **161-post SEO blog where essentially every post is dated 4 June 2026** — a bulk AI-generated content farm [V — /blog/; characterization [I]].

**Momentum signals** [V unless noted]:
- LinkedIn: **founded 2025, 1 employee, 25 followers**.
- GitHub org: 2 public repos (installer May 2026, Helm chart June 2026), ~1 star total, no public members.
- Site/terms/blog all first appear May 2026 → **public launch ≈ May–June 2026**.
- Worker image `1.0`, chart `1.0.4` → very early release cadence.
- **No funding, founders, customers, logos, testimonials, HN/Reddit/press mentions found anywhere** (multiple searches). mergeloom.com is an unrelated domain-for-sale.
- Legal entity, jurisdiction, and registration are **not disclosed** on terms/refund/subprocessor pages [V absent].
- Subprocessors: Anthropic, Stripe, Resend, Cloudflare, Google Analytics, Reddit, LinkedIn, Crisp, Cal.com [V].
- SLA: explicitly **best-efforts** unless separately negotiated [V — /terms-and-conditions/].

**Read:** a solo-founder (or near-solo) bootstrap that shipped a real, coherent v1.0 and is buying its way into the exact "governed AI delivery" search space. Low resources, high positioning overlap, zero visible traction — but 6–12 months ahead of Mainguard's P2 milestones on the *governance pipeline* story.

---

## 3. Feature-by-feature comparison

Legend: **SHIPPED** = on Mainguard `main` today (v1, T-xx). **PLANNED P2-xx** = specified in Master Doc v2 (phase2 branch). **NOTHING** = not shipped and not in any plan.

| MergeLoom feature | Mainguard shipped today | Mainguard planned | Mainguard has nothing |
|---|---|---|---|
| Tracker intake: Jira/Linear/Azure Boards/monday/GitLab Issues | — (T-24 GitHub Issues **panel** only — read/browse, not run-triggering) | — | **NOTHING** (multi-tracker, approval-triggered run intake is in no plan) |
| Ticket-approval-triggered autonomous runs | — | Partial: P2-14 plan approval gates agent work, but intake is user-initiated in-app, not tracker-driven | Mostly nothing |
| Persistent cross-repo Context Engine (vault, delta sync, confidence-scored evidence) | — (T-11 blame/T-12 file history are per-repo primitives) | — (P2-06 provisions repos; no knowledge index) | **NOTHING** |
| AGENTS.md-style repo guidance consumed by runs | — | Implicit in agent CLIs Mainguard launches (they read AGENTS.md themselves) | Partial gap (no first-class rules surface) |
| Standardized run inputs / consistent prompting | — | Partial: P2-14 plan approval + P2-08 gateway normalize the *channel*, not the prompt content | Partial gap |
| Vendor-neutral providers (Codex/Claude/OpenAI-compat/Vertex/Bedrock/Foundry) | — | **P2-01** BYOK keystore, **P2-08** AI Gateway, **P2-22** pinned adapter channel (agent-CLI-neutral) | — (planned parity; Mainguard adds *interactive* agents) |
| Validation gate: repo-level test/lint/typecheck before review | — | **P2-10** verification runs (configured test command in worker sandbox, immutable records tied to main@sha) | — |
| **Repair loop** (bounded auto-fix of validation failures) | — | — (P2-10 surfaces failure to human; no auto-repair) | **NOTHING** |
| **Clarity check** (ticket scope/AC validation pre-run) | — | — (P2-14 approves *plans*, doesn't grade ticket quality) | **NOTHING** |
| **AI specialist Review Agent** on the diff | — | — (P2-11 risk-ranks mechanically via RiskClassifier; no LLM reviewer) | **NOTHING** |
| **Diff Guard** (block oversized/off-scope diffs) | — | Partial: **P2-11** flagged-changes gate + RiskCategory ranking covers risky *categories*; no size/scope policy blocking | Partial gap |
| PR/MR handoff with evidence attached | T-23/T-25/T-26 (PR create/review/checks panels) | **P2-10** verification records, **P2-11** provenance, **P2-12** external PR intake | — |
| Mandatory human merge | **SHIPPED philosophy** (human-gated merges) | **P2-10** rejection trigger: "auto-merge of any kind" | — (parity by design) |
| Merge queue + stale invalidation when main moves | — | **P2-10** (the product spine; MergeLoom has nothing here) | — Mainguard **leads** |
| Epic decomposition into dependency-ordered slices | — | Partial: **P2-14** dual-mode orchestration approves plans; no epic→slice decomposition engine | Mostly **NOTHING** |
| Campaign controls (pause/resume/replan/skip/retry) | — | Partial: **P2-09** lifecycle (spawn/kill/yield), **P2-08** admission | Partial gap (no replan/skip semantics) |
| Ticket→run→validation→PR audit chain | T-19 undo journal (local ops) | **P2-15** hash-chained tamper-evident audit + **P2-16** SIEM export (stronger than MergeLoom's) | — Mainguard **leads on integrity**, trails on ticket-linkage |
| Line-level "Code Audit" attribution | T-11 blame, T-32 blame→PR | **P2-11** per-hunk provenance on **Agent Trace** standard (first renderer) | — Mainguard **leads when shipped** |
| Per-run cost telemetry tied to work item | T-22 analytics (local usage) | **P2-08** GetBudgets/StreamSpend | Partial gap (cost→ticket linkage not specified) |
| **Agent Fleets** (recurring scheduled mandates + daily PR caps + open-review caps) | — | Partial: **P2-08** admission control + budgets are the substrate; no cadence/mandate scheduler | Mostly **NOTHING** |
| **Self-learning from rejected PRs / review findings** | — | — | **NOTHING** |
| Cloud-hosted execution | — | **P2-25** cloud worktrees (guardrails now, impl post-GA) | — (later) |
| Self-hosted/VPC worker (Docker/Helm, workload identity) | — (Mainguard is local-desktop = inherently self-hosted) | **P2-05/06/07** WSL2 sandbox is the local analogue; no K8s/server worker planned | Different topology; server-farm mode: NOTHING |
| Sandbox hardening / egress control | — | **P2-07** default-deny egress + hardened containers (MergeLoom claims nothing here — Mainguard **leads**) | — |
| Tenant isolation / managed SaaS controller | n/a | n/a (desktop; P2-25 later) | By design |
| OIDC/SAML | — | **P2-23** RBAC/SSO/SCIM (broader than MergeLoom's) | — |
| Local worker/admin UI | Whole product is a rich native UI | **P2-13** activity bar/docking | — Mainguard **leads massively** |
| Slack/Teams notifications | T-27 (GitHub notifications panel) | — (no chat-ops integration planned) | Partial gap |
| Confluence/context-source ingestion | — | — | **NOTHING** |
| Interactive terminals into running agents | — | **P2-03/04/18** VT-conformant terminal engine (MergeLoom: absent) | — Mainguard **leads** |
| Cross-worktree conflict radar | — | **P2-19** (MergeLoom: absent) | — Mainguard **leads** |
| Agent commit-stream curation | — | **P2-20** (MergeLoom: absent — their agents emit one PR) | — Mainguard **leads** |
| Full native Git client (graph, rebase, conflicts, LFS, submodules, signing…) | **SHIPPED** T-01…T-33, 1,042 tests | — | — Mainguard **leads absolutely** |

---

## 4. Gaps where MergeLoom beats our plan — match + leapfrog

### G1. Tracker-driven work intake (their core loop; our biggest hole)
- **They have:** approved ticket in Jira/Linear/Azure Boards/monday/GitHub/GitLab auto-triggers a governed run; label/status/query routing; Controller-configured once. [V]
- **Match (proposed P2-27 "Work intake adapters"):** extend the T-23/T-24 host-client layer + P2-12 intake so a labeled/approved ticket (start with GitHub Issues + Jira) enqueues a P2-14 plan-approval item; reuse the audited one-transport-per-host rule.
- **Leapfrog:** intake lands in the **local verified merge queue** (P2-10) with conflict radar (P2-19) — ticket-triggered agents that *coordinate with each other and with your own working tree*, which a headless SaaS structurally cannot see. Sell it as "ticket-to-**merged**, not ticket-to-PR": MergeLoom stops at PR handoff; Mainguard governs the last mile (stale re-verification, conflict-aware merge order, undoable journaled merge).

### G2. Persistent cross-repo Context Engine
- **They have:** indexed vault across repos + Confluence, delta sync, confidence-scored evidence packs attached to PRs, refresh budgets. [V]
- **Match (proposed P2-28 "Context vault"):** daemon-side persistent repo index (symbols, APIs, docs, AGENTS.md rules) with delta refresh keyed on Git objects; attach bounded context packs to agent spawns (P2-09) and to review (P2-11).
- **Leapfrog:** build evidence from **Git-native primitives they don't have** — blame (T-11), file history (T-12), reflog (T-20) — so every context claim links to a commit, and render evidence **inside the review cockpit next to the hunk it influenced** (P2-11 provenance). Their evidence is a PR attachment; ours becomes a navigable review surface.

### G3. Repair loop + AI Review Agent + Diff Guard (validation depth)
- **They have:** six gates including bounded auto-repair, LLM specialist review, and oversized/off-scope diff blocking. [V]
- **Match:** (a) add a bounded **repair iteration** to P2-10 (`RunVerificationAsync` fail → one scoped repair prompt in the same sandbox → re-verify, attempts capped + journaled); (b) add **diff-size/off-scope policy** to P2-11's flagged-changes gate (RiskClassifier already classifies; add line-volume + touched-paths-vs-plan checks); (c) optional LLM review pass writing findings into the cockpit.
- **Leapfrog:** repairs happen in a **visible terminal (P2-03/18) the human can take over mid-repair** and in a hardened sandbox with default-deny egress (P2-07). MergeLoom's repair is a black box that either passes or stops; Mainguard's is a glass box with a human escape hatch — "pairing mode for repairs."

### G4. Agent Fleets (recurring mandates with caps)
- **They have:** named agents with mandate/scope/budget/cadence, daily PR cap, open-review cap, pause/resume. [V]
- **Match (proposed P2-29 "Fleet scheduler"):** scheduled agent profiles on top of P2-08 admission control (cadence, repo scope, path rules, daily PR-producing-run cap, open-review cap) + P2-15 audit of mandate/scope/budget.
- **Leapfrog:** fleet output enters the **stale-invalidation queue + conflict radar**, so recurring agents *never* land conflicting or stale PRs — the reviewer-flooding problem MergeLoom mitigates with caps, Mainguard eliminates with coordination. Also: fleets can run **overnight on the developer's own hardware at ~£0 marginal cost** (see §5 pricing attack).

### G5. Self-learning from rejected PRs
- **They have (claim):** rejected PRs, validation failures, and Review Agent findings feed future runs. [V claim, mechanics unverified]
- **Match:** persist P2-11 review verdicts + P2-10 failure records as per-repo "lessons" injected into subsequent agent context (cheap: it's a rules file the adapter prepends).
- **Leapfrog:** make the lessons file **human-editable, versioned in-repo, and hash-chain audited** (P2-15) — "governed learning" with a review trail, vs their opaque claim. An auditor can see exactly what the system learned and when.

### G6. Epic decomposition into delivery slices
- **They have:** epic → dependency-and-risk-ordered slices, parallel workstreams, per-slice cost/gates, replan/skip/retry. [V]
- **Match:** extend P2-14 so an approved plan can be a **multi-task plan mapped to N worktrees** with declared dependencies; per-worktree cost via P2-08.
- **Leapfrog:** schedule slices **through the merge queue with conflict-radar-aware ordering** (P2-19 predicts which slices will collide *before they run*). MergeLoom orders slices by human-declared dependency and still collides at PR time; Mainguard can order by *measured* overlap.

### G7. Cost-per-outcome telemetry tied to the work item
- **They have:** per-run token/infra split, per-repo/per-provider allocation, cost attached to the PR. [V]
- **Match:** specify in P2-08 that `StreamSpend` records are keyed to task/ticket + branch and surfaced in the cockpit and on the PR (P2-12 path too).
- **Leapfrog:** report **cost per *merged* change and cost of rejected work** — the number a VP actually wants and one MergeLoom can't compute well because it never sees post-handoff outcomes locally. Publish a comparison calculator: BYOK local run ≈ token cost only, vs their £2–£4 platform fee per PR.

### G8. Time-to-market on the governance story (meta-gap)
- **They are live, billing, with 50 free runs and a working self-hosted worker — today.** Mainguard's equivalent story lands at M7/M7.5. Mitigation: ship P2-10/P2-11 vertical slice early (already the plan's spine), and publicly stake the "governed **merge**" claim before their SEO wall owns the category vocabulary. [V/I]

---

## 5. Where we beat them — and how to press it

1. **A shipped, deep native Git client (T-01…T-33, 1,042 tests).** They have *no* client at all — review happens on the code-host PR page. Press: market every P2 governance feature as *inside the tool you already review and merge with*; demo "ticket → agent → conflict-resolved, rebased, signed, undoable merge" end-to-end in one window. Nothing in their stack can render a graph, resolve a conflict, or undo a merge.
2. **Local execution + hardened sandbox (P2-05/06/07).** Their self-hosted mode still requires Linux/K8s infra **and a live SaaS controller**; they publish zero sandbox/egress hardening. Press: "your laptop is the VPC" — zero-infra self-hosting on Windows/WSL2 with default-deny egress and a published security architecture (P2-17), vs their undisclosed legal entity and best-efforts SLA.
3. **Merge queue + stale invalidation (P2-10).** They stop at "PR opened"; a PR validated an hour ago is stale the moment main moves, and they never re-verify. Press: name the failure mode publicly ("validated-then-stale is unvalidated") — it is intrinsic to their architecture and the exact thing our spine fixes.
4. **Review cockpit depth (P2-11) + Agent Trace provenance.** Their "Code Audit" is line attribution in a web controller; ours is risk-ranked hunks with per-hunk provenance in a native diff stack (T-13), first renderer of the emerging Agent Trace standard. Press: publish Agent Trace support loudly; propose they can't follow without building a client.
5. **Interactive agents (P2-03/04/18) + commit curation (P2-20) + conflict radar (P2-19).** Their runs are fire-and-forget; no steering, no takeover, no cross-run coordination. Press: "govern the agent *while it works*, not just its output."
6. **Stronger audit integrity (P2-15/16).** Hash-chained, tamper-evident, SIEM-exportable, timed for the EU AI Act window (2026-08-02) — vs their plain traceability with no integrity guarantees and no certifications. Press: publish an "audit-grade vs audit-flavored" comparison; ship `audit verify`.
7. **Structural pricing attack.** Their whole model is £2–£4 per PR *platform tax* + AI cost. Mainguard BYOK local runs cost tokens only; a one-time licence (or sub) with unlimited runs undercuts any team producing >~50 PRs/month. Press: cost calculator on the website; "no per-PR meter on your own hardware."
8. **Windows.** Their worker is Linux/K8s-only; Mainguard is Windows-first with WSL2 handled invisibly (P2-05/21/22). Press: the unserved Windows-enterprise flank identified in our own market docs.

---

## 6. Their weaknesses / attack surface

- **Headless & web-only.** No desktop app, no review UI, no terminal, no interactive steering, no local Git tooling. Every human touchpoint is rented (tracker + code host). [V]
- **No merge-coordination layer.** No queue, no stale re-verification, no cross-PR conflict awareness; epic slices collide at PR time. [V absent]
- **Cloud = Anthropic-only** despite "vendor-neutral" positioning; neutrality exists only in self-hosted mode. [V]
- **Self-hosted still phones home.** Controller SaaS is mandatory (enrollment token, queue state, PR metadata cross boundary); not air-gappable. [V]
- **No sandbox/egress hardening story** for the thing that executes AI-written code — a governance vendor with no runtime-security page. [V absent]
- **Audit without integrity.** No hash chain, no signatures, no SIEM, no SOC 2/ISO, no RBAC/SCIM; OIDC/SAML gated to enterprise. [V absent]
- **One-person company selling governance.** Founded 2025, 1 LinkedIn employee, 25 followers, no disclosed legal entity/jurisdiction, best-efforts SLA, no customers/logos/reviews anywhere, no funding trail. Procurement-killing facts for their own target buyer. [V]
- **SEO content farm optics.** 161 blog posts nearly all stamped 4 June 2026; Reddit/LinkedIn ad pixels; zero organic community footprint (no HN/Reddit/X discussion found). Their category ownership is bought, not earned — contestable with genuine content. [V/I]
- **Platform coverage gaps:** Linux/K8s worker only; no Bitbucket; no API/CLI/webhooks documented; no status page; GBP-only pricing. [V absent]
- **Per-PR pricing friction at scale** and a £6–£7/run all-in claim that invites CFO scrutiny (their £100/ticket baseline is self-serving). [V claim, [I] critique]
- **`JCA_*` env prefix** hints at a recent rename/pivot from a Jira-specific tool — consistent with a young, thin codebase behind broad marketing. [V vars, [I] interpretation]

---

## 7. Source URLs

**Fetched directly (verified):**
- https://mergeloom.ai/ · https://mergeloom.ai/product/ · https://mergeloom.ai/pricing/
- https://mergeloom.ai/product/repository-context-rules/ · /product/agent-fleets/ · /product/consistent-prompting-validation/ · /product/audit-trails-attribution/ · /product/epic-delivery/ · /product/work-intake-integrations/ · /product/self-hosted-ai-coding-infrastructure/ · /product/cloud-hosted-ai-coding/
- https://mergeloom.ai/solutions/reduce-ai-costs/ · /solutions/jira-epic-delivery/ · /solutions/ticket-to-code-automation/ · /solutions/self-hosted-ai-coding-agents/ · /solutions/automated-pr-validation/
- https://mergeloom.ai/docs/ · https://mergeloom.ai/docs/getting-started/install-worker/
- https://mergeloom.ai/compare/vibe-kanban/ · /compare/coderabbit-alternative/ · /compare/beezi-ai/
- https://mergeloom.ai/subprocessors/ · /terms-and-conditions/ · /refund-policy/ · /blog/ · /sitemap.xml · /sitemap-0.xml
- https://github.com/MergeLoom · https://www.linkedin.com/company/mergeloom-ai

**Search-snippet only:** https://mergeloom.ai/product/jira-ticket-to-pr/ (fetch 404'd; exists per search index) · https://mergeloom.com/ (unrelated domain-for-sale) · LinkedIn post https://www.linkedin.com/posts/mergeloom-ai_ai-coding-is-powerful-but-inside-a-real-activity-7458165332747186176-zNSU

**Negative results (searched, nothing found):** MergeLoom on HN, Reddit, Crunchbase, funding press, founder names, customer logos, status page, API docs.

**Mainguard internal:** `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (branch `phase2`) — §1.1 baseline, §1.2 market traceability, §3 build order, P2-01…P2-26 specs; `docs/business/market-analysis/Mainguard_Naming_And_Competitive_Landscape_2026-07.md`.
