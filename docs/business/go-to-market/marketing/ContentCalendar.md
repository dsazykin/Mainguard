# Mainguard Content Calendar & Drafted Post Backlog

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../../BUSINESS.md), [`GTM.md`](../../../GTM.md) and [`GTM_Assets.md`](../../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Lane G Part 2 · Register: brand · Status: calendar + publish-ready drafts.**

The editorial layer of [`LaunchCampaignPlan.md`](LaunchCampaignPlan.md): what publishes when, and the full drafts. Every piece conforms to the [Voice & Delight Bible](../../../creative/Mainguard_Voice_And_Delight_Bible.md) (brand register — one degree warmer, personality unchanged), honors the honesty contract ([Narrative §0](../../../creative/Narrative.md)), and uses the competitor sentences of Narrative §2 as its public comparison register. Comparison pages exist for a strategic reason on record: contest the category vocabulary before MergeLoom's 161-post SEO wall owns it (Master Doc §14.5 #8; the Nimbalyst playbook — copy their content machine, not their features).

Dates key off **L1** (act-one Show HN) and **L2** (act-two Show HN, L1+4–8 weeks).

---

## 1. The calendar

| When | Piece | Type / channel | Status |
|---|---|---|---|
| L1 − 4 wks | *Why I'm building Mainguard* (founder story) | Essay → About page, blog, HN | **Final** — Narrative §5.5 is copy of record |
| L1 − 2 wks | *The `.git/index.lock` problem* | Engineering essay → blog, HN | Outlined (§3.1) — the GTM Plan §7.1 essay (a) |
| L1 − 1 wk | *A 60fps commit graph without a chart library* | Engineering essay → blog, HN | **Drafted** (§2.4) |
| **L1** | Show HN act one + launch-week posts | HN, Reddit, Console.dev, press | **Final** — SocialLaunchReserve §1–2 |
| L1 + 1 wk | *Mainguard vs Fork* | Comparison page | **Drafted** (§2.2) |
| L1 + 2 wks | *Mainguard vs GitKraken* | Comparison page | **Drafted** (§2.1) |
| L1 + 3 wks | *Sandboxing coding agents on Windows/WSL2* | Engineering essay → blog, HN | Outlined (§3.2) — essay (b) |
| L1 + 4 wks | *The merge queue that re-verifies* | Design essay → blog, HN | **Drafted** (§2.5) — essay (c), evolved |
| L2 − 1 wk | *Ungoverned AI merges are a time bomb* | Manifesto → blog, HN | **Drafted** — [Manifesto.md](Manifesto.md) |
| **L2** | Show HN act two + wedge posts | HN, r/ClaudeAI, creators, press | **Final** — SocialLaunchReserve §3 |
| L2 + 1 wk | *Mainguard vs Conductor* | Comparison page | **Drafted** (§2.3) — publishes only once the pipeline demonstrably works (its Mainguard column must be present-tense) |
| L2 + 2 wks | *A fact, not an opinion* (AI-review fatigue) | Essay | Outlined (§3.3) |
| L2 + 4 wks | *No meter on your own hardware* | Pricing-logic essay | Outlined (§3.4) |
| Rolling | Backlog (§3.5) | Mixed | Outlined |

Cadence commitment: one substantial piece every two weeks minimum, indefinitely. Release notes (every release) follow the release-notes voice guide — Voice & Delight Bible **Appendix D** (folded from LaunchReserve §6) — and are not scheduled here.

---

## 2. Drafted pieces

### 2.1 Comparison — *Mainguard vs GitKraken: native instrument vs Electron incumbent*

*(Web page. Sources: Competitor Research §2, Feature Inventory §11, GTM Plan §5.3, Narrative §2.1. Comparison tables of record: LaunchReserve §3 Table A (archived at `docs/archive/LaunchReserve.md`; narrative teardown of record is Narrative §2). Update the GitKraken column against their changelog before every publish — they move fast.)*

---

> GitKraken is the largest paid Git GUI, and it earned that: broad platform support, a polished feature set, and — since April 2026 — a shipped Agent Mode in Desktop 12 plus the standalone Kepler agentic development environment. If you want an established, actively developed Git client with agent-launching built in today, GitKraken is a serious choice. This page is for deciding whether it's *your* choice.
>
> **Where the two differ today**
>
> | | Mainguard (dev preview) | GitKraken Desktop |
> |---|---|---|
> | Rendering | Native Avalonia + Skia; the commit graph is a virtualized vector canvas | Electron — a web-technology shell; GitKraken maintains an official performance-troubleshooting page for it |
> | Free tier | Free, no account, no private-repo restriction | Requires an account; free tier blocks private repos |
> | Partial staging | Hunk **and** individual-line staging, validated against `git apply` | Hunk / line staging |
> | Undo | An operation-history journal makes ref moves reversible, plus a reflog viewer | Reflog-based |
> | AI in the client | None today, deliberately | AI commit messages, predictive conflict detection |
> | Agents | Roadmap — not built yet (see below) | Agent Mode and Kepler, shipped |
>
> **What GitKraken does that Mainguard doesn't.** Ship agents today. Agent Mode and Kepler are real, distributed to an existing paid base, with multi-repo tasks and issue-tracker intake. Mainguard's agent layer is a published roadmap, not code — if you need agent launching *now* inside a Git GUI, GitKraken has it and we don't.
>
> **What Mainguard does that GitKraken doesn't.** Render natively — the graph, diffs, and every surface are drawn directly, not through a browser engine, which is why the app stays smooth where Electron's weight is GitKraken users' most common complaint. Stage down to the single line with the result validated against `git apply`. Journal ref moves so they're undoable. And open with no account: nothing about the free tier phones home or fences your private repos.
>
> **The difference in direction.** GitKraken's agent features launch agents and execute them in worktrees on your host. Mainguard's roadmap is the layer *after* launching: sandboxed execution, a merge queue that re-verifies branches that go stale, risk-ranked review with per-hunk provenance. Checked directly against Kepler's own materials, none of that appears there — no merge queue, no verification runs, no sandbox isolation, no provenance, no audit. In one sentence: **GitKraken launches agents. Mainguard is built for what happens after they finish.** And in fairness to tense: GitKraken's half of that sentence is shipped; Mainguard's half is a roadmap we publish precisely so you can hold us to it.
>
> **Choose GitKraken if** you want shipped agent features inside an established client today, or you depend on its integrations. **Choose Mainguard if** you want the fastest native client on Windows with no account wall — and you'd rather bet on verification than generation for the agent era.

---

### 2.2 Comparison — *Mainguard vs Fork: two native clients, one open question*

*(Web page. Sources: Viability §1.5, Feature Inventory §11, Narrative §2.1. The hardest comparison to write honestly, so it leads with the concession.)*

---

> Let's start with the concession: against Fork, "native rendering" is not Mainguard's edge. Fork is genuinely native, genuinely fast, and genuinely loved — and at $59.99 one-time, it's standing proof that individual developers will pay once for craft. Fork set the bar for what a paid-once native client owes its user. Mainguard's client is built to honor that bar, not to pretend it isn't there.
>
> **Where the two differ today**
>
> | | Mainguard (dev preview) | Fork |
> |---|---|---|
> | Rendering | Native (Avalonia + Skia) | Native |
> | Price | Free, no account | $59.99 one-time |
> | Partial staging | Hunk and line-level, validated against `git apply` | Hunk / line |
> | Undo | Operation-history journal + reflog viewer; force-push is `--force-with-lease`, never bare `--force` | Reflog-based |
> | Themes | One design system, five switchable themes | Platform look |
> | AI / agents | None in the client today; agent control plane on the roadmap | None, by choice |
>
> **What Fork does well** needs no table: it is fast, it is focused, and it has stayed excellent for years without chasing a trend. If what you want is exactly a Git client, Fork deserves its reputation and your $59.99.
>
> **Where Mainguard differs today** is depth of the safety mechanics: staging down to the individual line with every patch validated against `git apply` (so what you stage is exactly what Git stages), an operation journal that makes ref moves undoable rather than merely reflog-recoverable, and destructive-action copy that states what changes and what's recoverable before you click.
>
> **Where the two part ways** is the question Fork — reasonably, deliberately — doesn't ask: *what do you do when most of your diffs weren't written by you?* Coding agents now produce branches faster than anyone reviews them; review time is up 91% while most developers say they don't fully trust agent output. Mainguard's roadmap (not built yet, marked plainly) is the answer layer: sandboxed agents, test-verification before review, a re-verifying merge queue. Fork has no agent story of any kind, by choice.
>
> **Choose Fork if** you want a mature, paid-once native client and your diffs are your own. **Choose Mainguard if** you want comparable native craft for free today — and the agent era is already your daily reality.

---

### 2.3 Comparison — *Mainguard vs Conductor: the orchestrator and the control plane*

*(Web page. **Publishes only at L2+, once the verification pipeline demonstrably works** — this page's Mainguard column must be written in the present tense or not at all. Sources: Competitor Research §3, Master Doc §3.3.3, Narrative §2.2. Re-verify the Conductor column at publish; $22M funds fast changes.)*

---

> Conductor is the funded leader of the agent-orchestrator category — a $22M Series A, a small team shipping weekly, and a genuinely pleasant product: parallel Claude Code / Codex / Cursor agents, each in its own per-workspace worktree, with GitHub and Linear integration, free. If you're on a Mac and you want to run several agents side by side today, Conductor is the default for a reason.
>
> **Where the two differ**
>
> | | Mainguard | Conductor |
> |---|---|---|
> | Platform | Windows-first, native; WSL2 handled invisibly | macOS only — no Windows or Linux signals in the changelog |
> | Foundation | A full native Git client (staging to the line, 3-pane conflict resolver, interactive rebase, undo journal) | An orchestrator; Git depth delegated to other tools |
> | Isolation | Sandboxed execution, default-deny egress | Worktree isolation on the host |
> | After the agent finishes | Branches must pass your test suite in their sandbox before review; a merge queue re-verifies anything that goes stale when main moves | A task queue; review and merge via the GitHub PR flow |
> | Provenance / audit | Per-hunk provenance and an audit-grade, tamper-evident record | Not offered |
> | Price | Client free, no account; verification pipeline in Pro | Free (riding your agent subscriptions) |
>
> **What Conductor does well:** the spawning experience, the cadence, and the discipline of staying free by using your existing agent subscriptions — a model we kept (Mainguard is BYOK for the same reason). **Where it stops:** at the moment the agent finishes. Its queue sequences *tasks*, not *verification*; a branch that passed tests an hour ago, against an older main, merges on yesterday's evidence. And it's Mac-only, in a market where Windows is the largest developer OS.
>
> If you want the one-line version that journalists reach for: **Mainguard is Conductor for Windows — with verification.** The analogy flatters both sides, and we'll keep using it as long as it stays true.
>
> **Choose Conductor if** you're on macOS and want the smoothest way to run parallel agents today. **Choose Mainguard if** you're on Windows, or you've decided that the hard part was never running the agents — it's trusting what they merged.

---

### 2.4 Engineering essay — *A 60fps commit graph without a chart library*

*(Blog + HN. Present tense throughout — everything here is shipped and exercised by the test suite (`CommitGraphRouterTests`). Types named are real: `Mainguard.Agents/Graph/CommitGraphRouter.cs`, `GraphModels.cs`, `Mainguard.App.Shell/Controls/CommitGraphCanvas.cs`.)*

---

> Every Git GUI has a commit graph, and most of them get slow the same way: they treat the graph as a *diagram* — one big visual object that some charting layer owns — and the diagram's cost grows with the history. Scroll a 100,000-commit repository and the tool re-lays-out a data structure the size of the repository.
>
> Mainguard's graph makes the opposite bet: **there is no diagram.** There is a pure routing function and a stack of very small drawings.
>
> **The router is pure and chunked.** `CommitGraphRouter.RouteCommits` takes a chunk of commits in topological order plus a `GraphFringeState` — the list of "active lanes," meaning the parent SHAs that earlier rows are still waiting to meet. It walks the chunk once: each commit either lands in the lane that was waiting for its SHA or claims the first free lane; its first parent inherits the lane (that's what keeps a branch a straight vertical line); merge parents fork lines out to their own lanes. Out comes a `GraphRouteResult` — a list of `GraphNode`s, each knowing only its row, its lane, and the line segments passing through its row — plus the *outgoing* fringe.
>
> That fringe is the whole trick. Because routing is a fold over (chunk, fringe) → (rows, fringe), history streams: we route what's near the viewport and shelve the fringe. Nothing ever routes the whole DAG. It also makes the router trivially testable — `CommitGraphRouterTests` feeds it synthetic topologies and asserts lane assignments with no UI in sight, because the router lives in `Mainguard.Agents` and has never heard of a pixel.
>
> **Each row draws itself.** On the UI side, `CommitGraphCanvas` is a small Avalonia `Control` — one per visible row — bound to its `GraphNode`. Its `Render` override draws exactly what crosses its own row: the pass-through verticals, the curves into and out of merges, its commit dot. Pens are 2 px with `PenLineCap.Round` — the round caps are where the "thread" character of the design lives; the linework is the metaphor, so the animation doesn't have to be. Because rows are independent controls inside a virtualized list, scrolling a huge history costs what scrolling any virtualized list costs. The graph you perceive is an emergent property of stacked rows that each know almost nothing.
>
> **Colors are tokens, not constants.** Lane colors resolve from the active theme's resources (`Lane1`–`Lane5`) with a cache that invalidates on `ThemeManager.ThemeChanged` — so the graph re-inks itself live when you switch among the five themes. The lanes are deliberately decoupled from the semantic status colors (success/danger/warning), so graph topology can never accidentally imply a verdict.
>
> **One refinement worth its complexity:** pinned refs. When you pin branches, their tip SHAs pre-seed the leftmost lanes *before* routing — but a reserved lane draws nothing until its tip actually arrives, so there are no dangling stubs, and when nothing is pinned the seeding is skipped entirely: the un-pinned graph is byte-for-byte identical to what it was before the feature existed. That property is asserted, not hoped.
>
> The result is a graph that stays smooth on large, tangled histories, follows theme switches instantly, and is testable down to the lane index. If you have a repository you consider pathological, I'd genuinely like to know how it behaves — that's the feedback that improves the router.

---

### 2.5 Design essay — *The merge queue that re-verifies (a design, published to be held to)*

*(Blog + HN. This is GTM Plan §7.1 essay (c), matured. **Tense discipline is the point of the piece**: it presents an unshipped design honestly, which is itself the differentiator. Sources: Master Implementation Document v2 P2-10/P2-12, ControlCenterDesign §3, Narrative §2.2/§3.3.)*

---

> This essay describes something that does not exist yet. Mainguard today is a shipped, native Git client; the merge queue below is its roadmap's centerpiece, specified but unbuilt. I'm publishing the design anyway, for a self-interested reason: a design published in advance is a promise you can be held to, and this product's entire thesis is that trust should be checkable.
>
> **The problem, precisely.** Every merge queue on the market re-runs *CI*. None re-runs *verification on the post-rebase state of agent branches* — checked across the field, that square is empty. Here's why it matters. An agent finishes a branch; your test suite passes; the branch is "verified." Twenty minutes later a different branch merges, and main moves. Your verified branch was verified **against a main that no longer exists**. Its tests passed in a world that's gone. Nothing conflicts textually, so every tool on the market will happily merge it — on evidence from the old world. A branch validated an hour ago, against an older main, is not validated. **Validated-then-stale is unvalidated.**
>
> **The design.** Each agent branch carries a verification record: the test command, the results, and — load-bearing — the exact main SHA it was verified against. The queue's state machine is small: `Working → Verifying → Verified → AwaitingReview → Merged`, with two honest exits (`Rejected`, and the one that matters here, `StaleVerified`). When any merge lands, the queue notifies every `Verified` entry that main moved; each one flips to `StaleVerified` and re-enters verification against the new main — automatically, visibly, before it can be offered for merge again. In the UI design, this is a ripple you can watch run down the queue rail: one merge lands, and every other branch's "Verified" chip flips to "Stale ↻" as its event arrives. The user watches the *reason* their branches must re-verify, rather than being told to trust a spinner.
>
> **The gate is a fact, not a mood.** A branch is mergeable only when the daemon's `CanMerge` says so, and when it says no it says why, in words: *verification is stale — re-verifying* · *1 flagged item unacknowledged* · *no test command configured*. There is deliberately no silent override: merging a stale branch requires an explicit, loudly-labeled setting, and doing it is journaled and audited. The design's rule is that every non-fresh path warns and records.
>
> **Two consequences fall out of keying the queue on branches, not processes.** First, verification runs *inside the agent's sandbox* — a deterministic test verdict, not an AI reviewer's opinion. Your suite passed or it didn't: a fact, not an opinion. Second, the queue doesn't care who produced the branch. A PR opened by a cloud agent — Codex, Jules, Copilot, any bot author you subscribe — can be fetched into the same pipeline, verified against the same main, and merged back through the host's own PR API, with nothing written to the upstream PR without an explicit human action. The queue keys on a branch, not on a terminal.
>
> **What could make this design wrong** — stated because a design essay that can't be falsified is marketing: re-verification cost could be prohibitive on slow suites (the design accepts staleness *detection* is cheap even when re-verification isn't, and surfaces the queue depth honestly); and a team's suite may be too flaky for deterministic gating to feel deterministic (in which case the queue's verdicts inherit the flakiness — it makes your tests' honesty visible, which is not always comfortable).
>
> None of this is built. The Git client underneath it is — free, native, no login — and it's how we intend to earn the benefit of the doubt this essay is spending. Hold me to the design.

---

## 3. Outlined backlog

### 3.1 *The `.git/index.lock` problem* (essay (a), publishes L1 − 2 wks)
The founding footgun as a technical narrative. Beats: what the lock is and why Git needs it; the abandonment failure mode (crashed plugin, killed script, two tools racing); why agents make it *statistically inevitable* (n processes × one index); Mainguard's architectural answer — every repository handle opens and disposes through one deterministic path, so the app can never leak the lock; and the honesty detail: when Mainguard finds a *foreign* stale lock it names the file and refuses to silently delete what another process might hold. Sources: founder story beats 1 (Narrative §5.5), Voice Bible V-1/V-6 surfaces. Everything present-tense — this is shipped behavior.

### 3.2 *Sandboxing coding agents on Windows/WSL2 with default-deny egress* (essay (b), interlude)
Design essay, [Horizon] tense like §2.5. Beats: why "worktree isolation" isn't isolation (the process can still touch anything); the WSL2 substrate choice; default-deny egress as the posture that makes an agent *wrong without being dangerous*; quarantine remotes — the agent's `origin` is a daemon-owned bare repo, so a prompt-injected `git push --force origin main` is structurally impossible, not merely firewalled. Fence: keep to the published substrate/ESC material; no security-implementation detail beyond the docs.

### 3.3 *A fact, not an opinion* (post-L2)
The antidote-to-AI-review-fatigue essay. Spine: an audited ~35% of AI-review comments are genuinely useful (GTM Plan §5.3) — the other 65% are attention tax; a deterministic test verdict has no such ratio. Not anti-AI-review (longer-term they're an optional signal); anti-*opinion-as-gate*.

### 3.4 *No meter on your own hardware* (post-L2, with the cost calculator)
The pricing-logic essay from Narrative §4.3: per-PR platform taxes vs flat license + BYOK; the ~50 PRs/month crossover; overnight fleet runs costing ~nothing marginal; budget caps vs usage meters ("metering bills the problem; a budget gateway prevents it").

### 3.5 Rolling candidates
*Why Avalonia and not Electron/Tauri* (with the benchmark data the HN kit promises) · *Five themes, one system* (design-craft piece; Daylight Loom as proof "premium" ≠ "dark") · *Mainguard vs GitKraken Kepler* and *vs the Copilot app* (post-L2, same honesty rules) · *vs MergeLoom* (architecture-facts only — no client, no queue, no re-verification; publish the "validated-then-stale" argument, never the company-size observation: teardown in analysis, respect in public) · release-note deep-dives when a release genuinely warrants one.

---

## Self-gate (Part 2)

- Every drafted piece: five-question gate passed; no exclamation marks; no banned vocabulary; competitor respect held (each draft opens by conceding the competitor's real strength; the sharpest line in each is Narrative §2's sanctioned sentence).
- Tense: §2.4 is 100% shipped claims against named real types (`CommitGraphRouter`, `GraphFringeState`, `GraphNode`, `CommitGraphCanvas`, `PenLineCap.Round`, `ThemeManager.ThemeChanged`); §2.5 and §3.2 declare themselves unshipped designs in their first paragraph; §2.3 is gated on the pipeline working before publish.
- Figures carried with sources (+91% Viability §1.3; ~35% GTM Plan §5.3; $59.99 Viability §1.5; $22M/Mac-only Competitor Research §3; account-walled free tier GTM Plan §5.3).
- Calendar honors the campaign sequencing rules (manifesto before L2, never before L1; comparison-vs-orchestrator only when present-tense).
