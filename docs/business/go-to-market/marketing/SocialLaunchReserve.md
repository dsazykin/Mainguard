# Mainguard Social Launch Reserve — Fully Written

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../../BUSINESS.md), [`GTM.md`](../../../GTM.md) and [`GTM_Assets.md`](../../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Lane G Part 3 · Register: brand (founder first-person on social surfaces) · Status: ready to post, gated by the calendar.**

The launch-day operations file: everything postable, in one place, pre-written so launch days are spent in the comment threads, not the drafts folder. **Copy-of-record rule:** the Show HN act-one body and the founder story are owned by [`docs/creative/Narrative.md`](../../../creative/Narrative.md) (§5.2, §5.5) — they are mirrored here for one-stop launch-day use and must be reconciled against Narrative before posting; if they ever disagree, Narrative wins. Everything else in this file (the expanded FAQ, the act-two full body, all threads, the LinkedIn and Bluesky copy, the About-page trim) is original to this file and this is its home.

Every string honors the honesty contract (Narrative §0), the never-say list (Narrative §3.4), and the five-question gate. Founder-voice surfaces (HN, threads) use "I"; the product never does.

---

## 1. Show HN — Act One

**Title:** `Show HN: Mainguard – a fast, native Git GUI for Windows (free, no login)`

**Body** *(mirror of Narrative §5.2 — reconcile before posting; †† marks the substitution if launch precedes packaging)*:

> Mainguard is a Git client I've been building for about a year. It's a native desktop app — Avalonia + Skia on .NET 10, LibGit2Sharp underneath — not an Electron shell around a web view. It's free, there's no account, and nothing leaves your machine. ††(Today it's a build-from-source dev preview: `dotnet build`, launch `Mainguard.App.Shell`.)
>
> What it does:
>
> - A commit graph that stays smooth on large histories — a virtualized, vector-drawn DAG lane router rendered directly at 60fps, not a chart library.
> - Staging down to the line. Stage, unstage, or discard by hunk; drag-select individual lines in the unified view; accept or reject blocks side-by-side. The patch engine is validated against `git apply`, so what you stage is exactly what Git stages.
> - A synchronized 3-pane conflict resolver (Ours | Result | Theirs) with per-side accept/reject/undo. Merge, rebase, cherry-pick, and pull all route conflicts through it.
> - An operation-history journal so ref moves are undoable, and a reflog viewer for the ones that aren't. Force-push is `--force-with-lease`, never a bare `--force`.
> - Branch, tag, and worktree porcelain; interactive rebase; five switchable themes on one design system.
>
> Why it exists: I got tired of `.git/index.lock` roulette — two tools touch the index, one exits early, and the next operation fails with a message that blames nothing and suggests nothing. Mainguard's one non-negotiable architectural rule is that every repository handle opens and closes through a single deterministic path, so the app itself can never leave that lock behind. When it finds a stale lock some other process left, it says so plainly and tells you how to check whether it's safe to remove — it won't silently delete a file another process might hold.
>
> Where it's going, stated honestly: the roadmap is a control plane for coding agents — a merge queue that re-verifies branches that go stale when main moves, risk-ranked review with per-hunk provenance, hardened local sandboxes. None of that is built. Today it's a fast, precise Git client for one developer, and I'd rather you hold me to the roadmap than believe it already exists.
>
> Feedback I'd most value: does the graph stay smooth on your gnarliest repo, and does line-level staging behave exactly like `git apply` for you?

**Posting mechanics** (Master Doc §14.8 Playbook 2): Tue–Thu, 9am–12pm ET; direct download link, no signup wall; founder in the thread within the hour and available all day; same-week satellites (Console.dev, PH badge, subreddits, Tweakers/IO+) fire per [LaunchCampaignPlan §4](LaunchCampaignPlan.md).

---

## 2. The comment-thread FAQ — pre-drafted replies, ready to paste

Expansion of Narrative §5.4's kit into full first-person replies. The register for every answer: **concede what's true, state the fact, point at the object, never bristle.** Numbers and mechanisms only — an HN thread is an audit log with usernames. Adapt tone to the actual comment; never paste an answer to a question that wasn't asked.

**Q1 — "Why another Git client? This market is done."**
> Fair — the client market is mature, and Fork in particular set a high bar. Two honest answers. Near-term: I think there was still headroom on Windows specifically — native rendering (the big incumbent there is Electron), line-level staging validated against `git apply`, an undo journal for ref moves, and a free tier with no account. Long-term: the client is the foundation, not the pitch. The roadmap is verification and merge governance for agent-written code, and that layer is only buildable on a real Git engine. The client has to be excellent anyway, because you'll live in it.

**Q2 — "Why .NET/Avalonia and not Electron or Tauri?"**
> Deterministic native rendering and real native controls. The commit graph is a virtualized vector canvas — each visible row is a tiny control that draws only its own lines, so scrolling a huge history costs what scrolling a virtualized list costs. The diff and (roadmap) terminal surfaces need per-pixel control that's expensive through a browser engine. Benchmarks against the Electron incumbents are in the repo. Tauri solves the memory half but you're still in a web view for rendering; I wanted the graph drawn, not laid out.

**Q3 — "Why FSL and not MIT? / 'source-available' is not open source."**
> You're right that it isn't, and I won't pretend otherwise. The daemon is source-available (FSL) so the security boundary is auditable — that's the part where trust matters most, and you can read exactly what it does. Why not MIT for everything: this category buried two free-and-thin orchestration tools in 2026 alone; FSL keeps the code inspectable and the company alive to maintain it. The free client stays free with no account, forever — that part isn't a trial.

**Q4 — "GitKraken / Fork / Sublime Merge already exists."**
> They do, and they're good — Fork especially, and it's native too, so I won't claim that edge against it. Differences today: line-level staging validated against `git apply`, the operation journal (ref moves are undoable, not just reflog-recoverable), no account wall, and native rendering vs GitKraken's Electron. Difference in direction: none of them is building verification and merge governance for agent output — GitKraken launches agents (Agent Mode/Kepler are real and shipped), but nothing runs downstream of them: no verification, no re-verifying queue, no provenance. That downstream layer is my roadmap — and to be equally honest, my half of that comparison is unbuilt today.

**Q5 — "Won't GitHub/Anthropic/OpenAI/Cursor just ship this?"**
> They're shipping the generation side, single-vendor each. The structural bet: none of them is incentivized to make its GUI a better home for a rival's agents, and teams already mix vendors per task. Vendor-neutral verification with deterministic local test gates is structurally Switzerland's job. The one I watch most closely is Cursor's Origin — the only announced product aiming at agent-scale review plus a merge queue. It's a cloud forge; I'm local-first. If Origin ships local execution plus provenance, that's my tripwire and I've said so publicly.

**Q6 — "What do you collect? What phones home?"**
> Nothing without opt-in. No login, no account, telemetry is opt-in with a published schema, keys go in the OS keyring, and the sandbox roadmap is default-deny egress. The security architecture doc is linked from the repo. The no-login free tier isn't generosity — a Git client sits between you and your code; I think it has to earn trust structurally, not ask for it.

**Q7 — "Is the agent stuff vaporware?"**
> It's a roadmap, and the post labels it as one — I'd rather you hold me to shipping it than believe it exists. What's real today is the client, exercised by 1,042 tests. The design for the merge queue is published in the same spirit: written down in advance so it can be checked against what ships.

**Q8 — "Does it run on Linux/macOS?"**
> Avalonia is cross-platform and the app builds and runs there, but I'm Windows-first deliberately — it's the largest developer OS and the least served by the current wave of polished dev tools, which shipped Mac-first. Linux/macOS polish comes after Windows earns it. If you hit something broken on your platform, I want the issue anyway.

**Q9 — "Binaries? I'm not building from source."**
> Completely fair. ††[If preview: Today it's `dotnet build` — packaged builds (Velopack: exe/dmg/AppImage) are the next distribution step, and the repo README tracks it.] [If packaged: Direct download at the link — no installer ceremony, no account.]

**Q10 — "I use lazygit/magit/the CLI. Why would I switch?"**
> You might not, and I won't pretend a GUI beats a workflow that's already in your fingers. Where a GUI earns its place for me: drag-selecting individual lines to stage across a big refactor, a 3-pane conflict resolver with per-side undo, and seeing a tangled DAG instead of remembering it. If the CLI serves you, the CLI serves you — the roadmap's bet is that supervising several agents' branches is where a purpose-built surface stops being taste and starts being necessary.

**Q11 — "60fps is a marketing number. Prove it."**
> The claim is specific: the graph is a virtualized vector canvas — per-row controls, chunked lane routing with a carried fringe, so cost tracks the viewport, not the repository. The router essay explains the design with the type names, and the benchmark methodology is in the repo. What I actually want is falsification: if you have a repository that makes it stutter, that's the most useful bug report I'll get this week.

**Q12 — "What's the business model? / How do you not die like the free orchestrators?"**
> The client is free forever, no login — that's the funnel, and it has standalone value, which is exactly what the dead free-orchestration tools lacked. Revenue is the roadmap's verification pipeline: Pro at $20/mo (unlimited local agents, BYOK — your keys, no inference markup), teams later at $50+/seat for governance (provenance, audit, queue analytics) — and that tier doesn't get sold before those features exist. I wrote up the failure ledger of this category; the pricing is designed against it.

**Thread hygiene:** answer fast, concede first, link the essay instead of re-arguing it, never dunk on a competitor by name beyond the stated deltas, and log every objection we didn't pre-answer — it becomes this file's Q13.

---

## 3. Show HN — Act Two

**Ships only when every sentence is true** (Narrative §5.3's rule). Title and lede are Narrative's copy of record; the body below completes them and is owned here.

**Title:** `Show HN: Run coding agents in sandboxes that must pass your tests before you review their code`

**Body:**

> A few weeks ago I posted Mainguard, a native Git client (thanks for the brutal and useful feedback — the graph got faster). This is the part I said wasn't built yet. It works now: spawn agents into isolated sandboxed worktrees, and their branches only reach your review queue after your test suite passes inside their sandbox. Merge one branch and every other "verified" branch goes stale and re-verifies automatically — because validated-then-stale is unvalidated. Vendor-neutral: Claude Code, Codex, OpenCode, and PRs from cloud agents all go through the same pipeline. Local, BYOK, no meter on your own hardware.
>
> The part I most want to show: you don't have to change how you run agents to use this. If your team already has Codex or Jules or Copilot opening PRs, point Mainguard at them — each PR is fetched into the pipeline, verified against your actual main with your actual test suite, reviewed in a cockpit that ranks hunks by risk, and merged back through the host's own PR API. Mainguard writes nothing to the upstream PR unless you explicitly act. Your agents keep working exactly as they do today; what changes is what you can trust when they finish.
>
> What's underneath, mechanically:
>
> - Every agent runs in a hardened local sandbox (WSL2 on Windows) with default-deny egress. Its `origin` is a daemon-owned bare repo — promotion to your real remote happens only through the verified pipeline, so a prompt-injected force-push at your main is structurally impossible, not just firewalled.
> - Verification is deterministic: your test command, run in the agent's sandbox, recorded against the exact main SHA it ran on. A fact, not an opinion.
> - The merge gate states its reasons in words — "verification is stale, re-verifying" · "1 flagged item unacknowledged" — and there's no silent override; the loud one is journaled and audited.
> - Review is risk-ranked with per-hunk provenance: which agent, under which approved plan, wrote these lines.
>
> Honest limits, so you don't have to dig for them: the realistic capacity is a developer supervising roughly 4–6 agents on a 16 GB laptop — this is not a "run 50 agents" product, and I think tools claiming that are writing checks consumer hardware can't cash. If your test suite is slow or flaky, the queue inherits that honestly — it makes your suite's reliability visible, which isn't always comfortable. And the client from act one is unchanged: free, native, no login.
>
> Feedback I'd most value this time: run your ugliest real workflow through the queue — parallel branches that touch the same files — and tell me where the staleness model surprises you.

**First-hour additions for act two** (beyond §2's kit): *"My CI already does this"* → CI re-runs on the PR as-is; it doesn't re-verify other branches when main moves, and it isn't a local pre-review gate — the queue is upstream of review, CI stays downstream of merge. *"Flaky tests make this useless"* → concede directly; the queue surfaces flakiness rather than hiding it, quarantine-listing is on the roadmap, and a flaky-suite team can run the queue in advisory mode. *"What data leaves my machine"* → nothing new: BYOK straight to your provider, host-API calls only for the PRs you subscribed, audit log stays local.

---

## 4. Thread series — X / Bluesky / LinkedIn

### 4.1 X thread — Act One (launch day)

> **1/** Mainguard is out. A native Git GUI for Windows — free, no account, nothing leaves your machine. Not an Electron shell: Avalonia + Skia on .NET 10, LibGit2Sharp underneath. [clip: graph scrolling a huge repo]
>
> **2/** The commit graph is drawn, not charted. Each visible row is a tiny control that renders only its own lines; lane routing streams in chunks. Scrolling 100k commits costs what scrolling a list costs. [clip: pathological DAG]
>
> **3/** Staging goes down to the individual line. Drag-select lines in the unified diff; the patch engine is validated against `git apply` — what you stage is exactly what Git stages. [clip: line-drag staging]
>
> **4/** Conflicts get a synchronized 3-pane resolver — Ours | Result | Theirs, per-side accept/reject/undo. Merge, rebase, cherry-pick, and pull all route here. [clip: resolver]
>
> **5/** You can undo ref moves. An operation journal makes them reversible; a reflog viewer covers the rest. Force-push is `--force-with-lease`, never a bare `--force`. The whole app is built around never losing your work.
>
> **6/** Why I built it: `.git/index.lock` roulette. Two tools touch the index, one dies early, your next operation fails blaming nothing. Mainguard's one non-negotiable rule: every repo handle opens and closes through a single deterministic path. The app can't leave that lock behind.
>
> **7/** One design system, five themes — Midnight Loom, Daylight Loom, Command Deck, Atelier, Loom Aurora — switchable live. Shape and spacing never change; only color does. [clip: theme flip]
>
> **8/** Where it's going, honestly: a control plane for coding agents — sandboxes, a merge queue that re-verifies stale branches, provenance in review. None of that is built yet. Today it's a fast, precise Git client, and I'd rather be held to the roadmap than believed in advance.
>
> **9/** Free. No login. Show HN is live today — I'm in the thread all day. [link]

### 4.2 X thread — Act Two (wedge-led; ships only when true)

> **1/** A few weeks ago I shipped Mainguard, a native Git client. Today, the part I said wasn't built: coding agents in sandboxes whose branches must pass YOUR tests before you review a line. [clip: verification passing]
>
> **2/** The problem, precisely: agents made branches cheap. Reviews didn't get cheaper. Review time is up 91% against a fixed human ceiling, and 87% of developers say they don't trust agent accuracy. Every vendor sells generation. Nobody sells trust.
>
> **3/** Here's the core mechanic. A branch passes tests → "verified." Then a different branch merges and main moves. Your verified branch was verified against a main that no longer exists. Every tool on the market merges it anyway.
>
> **4/** Mainguard doesn't. Merge one branch and watch every other verified branch flip to Stale ↻ and re-verify against the new main — automatically, before it can merge. Validated-then-stale is unvalidated. [clip: the stale cascade rippling down the queue rail]
>
> **5/** And you don't have to change how you run agents. Codex, Jules, Copilot already opening PRs? Point Mainguard at them — each PR enters the same pipeline: sandboxed verification, risk-ranked review, merge back through the host's own API. Nothing written upstream without your action.
>
> **6/** Verification is deterministic — your test command, in the agent's sandbox, recorded against the exact main SHA. A fact, not an opinion. (An audited ~35% of AI-review comments are genuinely useful. A test verdict has no such ratio.)
>
> **7/** Honest limits: ~4–6 agents supervised comfortably on a 16 GB laptop. Local-first, BYOK, no meter on your own hardware. The free client from act one is unchanged.
>
> **8/** Show HN round two is live — come break the staleness model. [link]

### 4.3 X thread — Engineering (rides the §2.4 essay)

> **1/** How do you render a 100,000-commit graph at 60fps without a chart library? You don't render the graph. Thread on Mainguard's commit-graph architecture:
>
> **2/** The router is a pure function: (chunk of commits, fringe) → (rows, new fringe). The fringe is just the parent SHAs earlier rows still wait to meet. History streams — nothing ever routes the whole DAG.
>
> **3/** Each visible row is its own tiny control bound to one GraphNode. It draws only what crosses its row: pass-through verticals, merge curves, its dot. The "graph" is an emergent property of stacked rows that each know almost nothing.
>
> **4/** Virtualization comes for free — rows in a virtualized list. Scroll cost tracks the viewport, not the repository.
>
> **5/** Pens are 2px with round caps — the thread-like character of the linework is the design language, so the animation doesn't have to be. Lane colors are theme tokens, resolved live; switch themes and the graph re-inks itself.
>
> **6/** Because the router lives in Core and has never heard of a pixel, lane assignment is unit-tested against synthetic topologies. Full write-up, with type names: [link]

### 4.4 Bluesky adaptation

Same threads, three retones: drop the clip callouts where video isn't attached (link the blog's captures instead); merge 4.1's posts 4–5 and 4.2's posts 6–7 (the dev audience there prefers fewer, denser posts); self-label the launch posts plainly ("I built this — launch day, happy to answer anything"). No other changes — the register is already right for the room.

### 4.5 LinkedIn — two long-form posts

**Post 1 — act one (founder story adaptation, for the Dutch/B2B mirror per Master Doc §14.1):**

> After about a year of building, Mainguard is public: a native Git client for Windows — free, no account required.
>
> It started with a small, infuriating bug class. Anyone who runs multiple Git tools has met `.git/index.lock`: a process dies early, leaves a lock behind, and the next operation fails with a message that blames nothing. Mainguard's first architectural rule is that the app can never leave that lock behind — every repository handle opens and closes through one deterministic path. When it finds a lock some other tool abandoned, it says so plainly and refuses to silently delete a file another process might hold.
>
> That rule became the product's thesis: a tool that guards your work doesn't guess on your behalf. It shows up everywhere — force-push is always `--force-with-lease`, destructive actions state what changes and what stays recoverable before you click, and ref moves are undoable through an operation journal.
>
> The larger direction: coding agents now produce branches faster than teams can review them — review time is up 91%, and most developers say they don't fully trust what agents write. Mainguard's roadmap is the governance layer for that: sandboxed execution, test-verification before review, a merge queue that re-verifies stale branches, and an audit-grade record of what each agent did. I mark that plainly as roadmap, not product — the whole thesis is trust, and that starts with tense.
>
> Today's release is the foundation: a fast, precise, natively-rendered Git client. If your team is Windows-based and living in an Electron Git GUI, I'd value your first impressions most of all.

**Post 2 — act two (the buyer angle: engineering managers; ships only when true):**

> Your team's AI agents are opening more pull requests than your reviewers can read. That's not a prediction — AI-assisted teams merge roughly twice the PRs, review time is up 91%, and the usual answer is a human skimming a firehose of diffs and hoping.
>
> Hope is not a merge strategy.
>
> Mainguard's control plane shipped this week, and the part most teams can use on day one requires changing nothing about how you run agents: point it at the PRs your agents already open — Codex, Jules, Copilot, any bot author. Each PR runs your actual test suite in a local sandbox before anyone reviews it. Review is ordered by risk, with per-line provenance: which agent, under which approved plan, wrote this. Merges go back through your host's own API, and nothing touches the upstream PR without an explicit human action.
>
> The detail your auditors will care about: every plan approval, verification verdict, and merge is recorded in a tamper-evident local audit trail — the record of "who approved what, and was it tested" that procurement questionnaires are already starting to ask for.
>
> Honest scope: this supervises a handful of agents per developer (4–6 on typical hardware), locally, with your own API keys. It is not a swarm platform, and the free Git client underneath remains free with no account.
>
> If your review queue is the bottleneck your standups keep circling, I'd like to show you the queue re-verify a stale branch live — it's the moment the product makes sense.

---

## 5. The founder story

**Copy of record:** Narrative §5.5 — "Why I'm building Mainguard," final draft (the lock file · the instrument · the trust problem). It is the pre-launch essay and the About page; it is not duplicated here.

**About-page short version** (~140 words, owned here — for the site's About block above the full essay link):

> Mainguard began with `.git/index.lock` — the lock file a crashed tool leaves behind, and the unhelpful failure every Git user eventually meets. The first architectural rule was that this app must never leave that lock behind; the rule grew into a thesis: a tool that guards your work doesn't guess on your behalf.
>
> Today Mainguard is a native, free, no-login Git client — a 60fps commit graph, staging down to the line validated against `git apply`, a 3-pane conflict resolver, and undo you can trust.
>
> Where it's going: coding agents made branches cheap and trust expensive. Mainguard's roadmap is the control plane that makes any agent's work verifiable before it merges — and none of it is built yet. We'd rather be held to that than believed in advance. [Read the full story →]

---

## Self-gate (Part 3)

- Copy-of-record discipline: HN act-one body and founder story are marked mirrors of Narrative §5.2/§5.5 with a reconcile-before-posting rule; everything original here (FAQ Q8–Q13, act-two body, threads, LinkedIn, About trim) extends rather than forks Narrative's claims.
- Act-two assets are all gated "ships only when true"; the wedge copy matches the P2-12 contract (fetch, verify, host-API merge-back, nothing-upstream-without-action).
- Every figure sourced (+91% Viability §1.3; 87% SO 2025; ~35% GTM Plan §5.3; 1,042 tests MergeLoom Deep Dive §5; 4–6 agents GTM Plan §2.4).
- Register held: concede-first FAQ answers, no exclamation marks, no competitor sneers (Q4 concedes GitKraken's shipped agents and Fork's parity), capacity honesty stated unprompted in act two.
