> **ARCHIVED (2026-07-12 consolidation) — superseded, full content preserved below unchanged.**
> - §1 Show HN, §2 founder story, §5 README hero → superseded by [`docs/creative/Narrative.md`](../creative/Narrative.md) §5 (copy of record) and operationalized in [`docs/business/go-to-market/marketing/SocialLaunchReserve.md`](../business/go-to-market/marketing/SocialLaunchReserve.md)
> - §3 comparison tables → superseded by `Narrative.md` §2
> - §4 agent naming → folded verbatim into the [Voice & Delight Bible](../creative/Mainguard_Voice_And_Delight_Bible.md) Appendix C
> - §6 release-notes voice guide → folded verbatim into the Voice Bible Appendix D

# Mainguard — Launch Reserve

**The reserve of launch / go-to-market copy, all in Mainguard's voice.**

Register: **brand** (external marketing), the pass PRODUCT.md flags as out of scope for in-app copy
and the Voice Bible reserves for launch. Brand register is warmer than product register, but the
personality does not change: **premium & precise** (PRODUCT.md). Every rule below is cited to the
[Voice & Delight Bible](../creative/Mainguard_Voice_And_Delight_Bible.md) (`V-#` voice, `N-#` naming) or to
[DESIGN.md](../../DESIGN.md) / [PRODUCT.md](../../PRODUCT.md).

## Honesty contract (read before quoting anything here)

Mainguard ships **one thing today**: a working, natively-rendered single-user Git client. The
multi-agent control center is **roadmap** (`phase2`), not code. Every capability claim in this
document is either:

- **true today** — on `main`, exercised by the test suite; or
- marked **[Horizon]** — specified for the `phase2` roadmap, *not shipped*. Never quote a
  **[Horizon]** line as a shipped feature.

Two standing honesty rules, carried from the market analysis, govern the forward-looking copy:

- **"audit-grade / tamper-evident," never "legally required crypto."** EU AI Act Article 12
  mandates event logging and traceability; it does **not** mandate cryptographic immutability.
  Hash-chaining is emerging best practice, so the audit story is marketed as *audit-grade evidence*,
  not *EU-required crypto* (Competitor Research 2026-07-07, probe (c)).
- **Honest capacity.** The roadmap target is a developer comfortably supervising **a handful of
  agents (about 4–6 on a 16 GB laptop)** — not "100 agents." Competitors who advertise 100 (Superset)
  state it as a *goal*; we state our real ceiling (Competitor Research §5, Viability §1.1).

Sources for every competitor claim: `docs/business/market-analysis/` — chiefly
`Mainguard_Competitor_Research_2026-07-07.md`, `Mainguard_MergeLoom_Deep_Dive_2026-07-07.md`,
`Mainguard_Viability_And_Differentiation_2026-07.md`, and
`Mainguard_Naming_And_Competitive_Landscape_2026-07.md`.

---

## 1. Show HN post

Honest about the split: a working Git client today, an agent merge-control plane on the roadmap.
Precise and calm (V-1, V-2); no exclamation theatrics; forward-looking claims explicitly marked
(V-6).

### Title

> **Show HN: Mainguard — a native Git client (Avalonia/.NET 10) becoming an agent merge-control plane**

### Body

> Mainguard is a Git GUI I'm building in the open. It's a native desktop app — Avalonia + Skia on
> .NET 10, LibGit2Sharp underneath — not an Electron shell around a web view.
>
> **What works today (single-user Git client, build-from-source dev preview):**
>
> - A commit graph that stays smooth on large histories: a virtualized, vector-drawn DAG lane
>   router rendered directly at 60fps, not a chart library.
> - Partial staging down to the line — stage/unstage/discard by hunk, drag-select individual lines
>   in the unified view, accept/reject blocks side-by-side. The patch engine is validated against
>   `git apply`, so what you stage is what Git stages.
> - A synchronized 3-pane conflict resolver (Ours | Result | Theirs) with per-side accept/reject/
>   undo. Merge, rebase, cherry-pick, and pull all route conflicts through it.
> - Branch / tag / worktree management, interactive rebase, and an operation-history journal so ref
>   moves are undoable — plus a reflog viewer for the ones that aren't.
> - Five switchable themes on one design system (Midnight Loom, Daylight Loom, Command Deck,
>   Atelier, Loom Aurora), swappable live.
>
> **Why I'm building it — and what is NOT built yet.** The pitch is not "another premium Git
> client." Agent CLIs now produce branches faster than anyone can review them; the bottleneck moved
> from writing code to *trusting* it. Mainguard's roadmap (`phase2`, not shipped) is a control plane
> for that: a merge queue that re-verifies any branch that goes stale when main moves; a review
> cockpit that ranks agent diffs by risk and shows per-hunk provenance; a hardened local sandbox
> with default-deny egress; and an audit-grade, tamper-evident record of what each agent did. All of
> that is design and specification right now — **I'd rather you hold me to shipping it than believe
> it already exists.**
>
> Honest scope: today it's a fast, precise Git client for one developer. The agent features are the
> destination, and the realistic near-term target is supervising a handful of agents (roughly 4–6 on
> a 16 GB laptop), not a swarm of a hundred.
>
> It's .NET 10, so you build from source for now (`dotnet build`, launch `Mainguard.App.Shell`). Feedback I'd
> most value: does the graph stay smooth on your gnarliest repo, and does line-level staging behave
> exactly like `git apply` for you?

*Self-check: every present-tense claim is on `main`; every roadmap claim is named as unbuilt
(V-6). No exclamation marks (V-2). Concrete objects, not vague reassurance (V-1).*

---

## 2. Founder story

The narrative of why Mainguard exists. Three beats: the footgun, the precision-instrument thesis, the
verification future. Told in the calm, exact register (V-1, V-2), honest about what is and isn't
built (V-6).

### The lock file

Every developer who runs more than one Git process against a repo has met `.git/index.lock`. A
process exits early — a crashed editor plugin, a killed script, two tools reaching for the index at
once — and leaves the lock behind. The next operation fails with a message that blames nothing and
suggests nothing. You delete a file you're not sure is safe to delete, and hope.

Mainguard started as an answer to that exact footgun. Its one non-negotiable architectural rule is
that every LibGit2Sharp handle is opened and disposed deterministically through a single path
(`IGitService.ExecuteWithRepo`) — no ad-hoc, long-lived repository handles that leak native state
and collide on the index. When Mainguard *does* find a stale lock it didn't create, it says so plainly
and refuses to silently remove a file another process might hold (V-1, V-6). **The bug this app
exists to prevent is losing your work to a tool that was supposed to protect it.**

### A precision instrument, not a wrapper

The second belief is that a Git client for high-stakes work should feel like an instrument, not a
web page in a frame. So Mainguard renders natively — the commit graph is vector-drawn at 60fps, the
surfaces are tuned per pixel, the motion is fast and functional (120–150ms, no bounce). There is one
design system with five switchable palettes and a single accent color reserved for the one place the
eye should land. This is deliberate opposition to the two things a serious tool should never feel
like: a generic Electron dev tool, or a bland enterprise dashboard (PRODUCT.md anti-references).

Destructive operations get the most care. Force-push uses `--force-with-lease`, never a bare
`--force`, and the confirmation states what changes, what's recoverable, and offers the safer path
first (V-4). Discards, hard-resets, and rebases all point you to the way back — reflog, undo journal,
stash — in the same breath (V-5). Precision here isn't decoration; it's the product's core promise.

### The verification future *(roadmap — not yet built)*

The reason to build the client first is that the client is the wedge into a bigger, harder problem.
Autonomous coding agents made it trivial to *produce* ten branches an hour. Nothing on the market
makes it safe to *merge* them. Review time is up sharply against a fixed human ceiling; a large
share of developers actively distrust agent output; and from August 2026 the EU AI Act asks for
attributable, auditable records of what automated systems did (Viability §1.3–1.4).

Mainguard's roadmap answers that as Git-native infrastructure: **[Horizon]** a merge queue that
re-verifies anything that goes stale when main moves; a review cockpit that ranks agent diffs by
blast radius and shows which agent, under which approved plan, wrote each hunk; a hardened local
sandbox with default-deny egress; and an audit-grade, tamper-evident trail that attributes every
agent action to the human who authorized it. Run your agents wherever you like — Claude Code, Codex,
cloud PR bots — and Mainguard becomes where their work turns into trustworthy commits on main.

None of that verification layer is shipped yet. The client is. Building the instrument first is how
we earn the right to build the control center — and how we make sure the control center is honest
about what it did, because the whole thesis is trust.

---

## 3. Competitor comparison

Two honest tables. **Table A** compares the *shipped* Git client to the incumbent Git GUIs.
**Table B** compares the *roadmap* control plane to the agent-orchestration field — every Mainguard
cell there that isn't shipped is marked **[Horizon]**.

Sources: `Mainguard_Competitor_Research_2026-07-07.md` (the ●◐○ capability matrix),
`Mainguard_MergeLoom_Deep_Dive_2026-07-07.md`, `Mainguard_Viability_And_Differentiation_2026-07.md §1.5`.

**Legend.** ● ships / verified today · ◐ partial or adjacent · ○ nothing found in public materials ·
**[Horizon]** = on Mainguard's `phase2` roadmap, specified but not shipped.

### Table A — Git client, today

| | **Mainguard** (dev preview) | **GitKraken Desktop** | **Tower** | **Fork** |
|---|---|---|---|---|
| Rendering | ● Native Avalonia + Skia, 60fps vector graph | ◐ Electron (web-tech shell) | ● Native | ● Native |
| Partial staging | ● Hunk **and line-level**, validated against `git apply` | ● Hunk / line | ● Hunk / line | ● Hunk / line |
| 3-pane conflict resolver | ● Ours \| Result \| Theirs, per-side accept/reject/undo | ● Merge tool | ● Merge tool | ● Merge tool |
| Undo of ref moves | ● Operation-history journal + reflog viewer | ◐ reflog-based | ◐ reflog-based | ◐ reflog-based |
| AI features in client | ○ None today (deliberate — no chat-with-repo gimmicks) | ● AI commit messages, predictive conflict detection | ● AI commit messages | ○ None |
| Pricing | ◐ Build-from-source dev preview (no price set) | ● Subscription | ● Subscription | ● $59.99 one-time |
| Platform | ● Windows-first, native cross-platform | ● Cross-platform | ● macOS / Windows | ● macOS / Windows |
| Agent orchestration | **[Horizon]** control plane (see Table B) | ● Kepler / Agent Mode (shipped) | ○ None | ○ None |

*Honest reading:* against Tower and Fork, native rendering is **not** Mainguard's edge — they are
native too; the edge is line-level staging validated against `git apply`, the operation-history undo
journal, and the agent-control-plane direction. Against GitKraken, the edge is native (non-Electron)
rendering and Git-surgery depth; GitKraken's edge today is a shipped agent mode and an existing paid
base (Competitor Research §2). Fork's genuine advantage is a $59.99 one-time price against
subscriptions (Viability §1.5).

### Table B — Agent control plane *(Mainguard column is roadmap)*

| Capability | **Mainguard** | **GitHub Copilot app** | **GitKraken Kepler** | **Conductor** | **Sculptor** (Imbue) | **MergeLoom** |
|---|---|---|---|---|---|---|
| Merge queue + stale re-verification | **[Horizon]** Planned lead (P2-10) | ○ Agent Merge = 1 PR, no cross-branch staleness | ○ | ○ "queue" = task queue, no re-verify | ○ | ○ Absent — slices meet only at PR time |
| Sandbox + default-deny egress | **[Horizon]** Planned (WSL2, default-deny, P2-07) | ◐ Local restricted + cloud VM; egress granularity unverified | ○ Worktree isolation only | ○ Worktree only | ◐ Docker container; network posture unstated | ○ No sandbox / egress story |
| Vendor-neutral external-agent PR intake | **[Horizon]** Planned (any agent → one pipeline, P2-12) | ○ Copilot-only | ◐ PR-based tasks, own agents | ◐ GitHub PR flow, own agents | ○ | ○ Produces PRs; does not intake others' |
| Per-hunk provenance in review UI | **[Horizon]** Planned (Agent Trace renderer, P2-11) | ○ | ○ | ○ | ○ | ◐ "Code Audit" line attribution in web controller, not in-diff |
| Audit-grade tamper-evident trail + SIEM | **[Horizon]** Planned (hash-chain + SIEM, P2-15/16) | ○ | ○ | ○ | ○ | ◐ Ticket→PR traceability; no hash-chain, no SIEM |
| Native Git client depth (staging/merge/rebase/undo) | ● **SHIPPED** — the wedge | ○ | ◐ GitKraken Desktop, not line-level | ○ | ○ | ○ No client at all — review on the code host |
| Windows support | ● Windows-first, WSL2 handled invisibly | ● | ● Desktop | ○ macOS-only | ◐ Runs via WSL, not native polish | ◐ Linux / K8s worker only (headless) |

*Honest reading:* the only ● in Mainguard's column is the **shipped Git client** — that is the real,
present differentiator against every agent tool, none of which is primarily a Git client (MergeLoom
has no client at all; Competitor Research §10, MergeLoom Deep Dive §5). Every agent-governance row is
**[Horizon]**: designed, specified on `phase2`, and *unbuilt*. The market-analysis verdict is that
these squares are empty across the field today — which is the opportunity, not a claim of having
filled them (Competitor Research "Where the field is empty").

**Quarantine remotes** *(Horizon):* a further planned differentiator — untrusted agent branches land
on a segregated "quarantine" remote and are marked `Quarantined` until verified, so unverified work
can never masquerade as reviewed on `origin` (Voice Bible N-3, ESC). No competitor ships this;
it is roadmap.

---

## 4. Swarm / agent naming

Per **N-4** (agents are precise, not pets — a stable neutral working name tied to a thread of work,
never a whimsical mascot) and **N-2** (draw on the loom / weave family *only where the metaphor
clarifies*; the plain engineering noun wins when the metaphor strains). An agent's display **always**
pairs its name with its verifiable status (N-3, V-6): a name alone never implies trust.

**All names below are for [Horizon] features — the multi-agent layer is not built.**

| Option | Form | Rationale | Fit vs N-4 |
|---|---|---|---|
| **Loom-1 … Loom-N** *(recommended)* | `Loom-3` | Already the Bible's worked example (N-4, V-6, T-4). Ties directly to the North Star ("the Precision Loom"); the loom is the machine, each agent a numbered station on it. Neutral, sortable, audit-legible. | Strongest — stable, neutral, thread-of-work. |
| **Shuttle-1 … Shuttle-N** | `Shuttle-2` | The shuttle is the tool that carries a thread across the loom — a precise metaphor for a worker carrying one task across the repo. Evocative without anthropomorphizing. | Strong — clarifies (a shuttle *does* the carrying), stays a tool not a character. |
| **Thread-1 … Thread-N** | `Thread-4` | Names the *unit of work* the agent owns (a thread of the weave). Reads naturally in a log: "Thread-4 verified." Mild collision with OS "thread" in an engineering context. | Good — plain, but the word is overloaded. |
| **Heddle / Warp / Weft** (roles, not IDs) | `Warp`, `Weft` | Reserve weave-part nouns for *role* distinctions (e.g. a coordinator vs workers) if a role split ever ships — not for per-agent IDs. | Conditional — only if a role taxonomy is real; otherwise strained (N-2). |

**Recommendation.** Use **Loom-N** as the primary identifier (matches the Bible verbatim, lowest
risk), with **Shuttle-N** as the sanctioned alternative if a warmer-but-still-precise label is
wanted for marketing screenshots. Agents may alternatively be named by their assigned branch or task
(N-4) where that is clearer than an index.

**Anti-patterns (do not use):** mascot or pet names ("Sparky", "Buddy"), mood words as identifiers
("Happy-path", "Ninja"), or any name that implies a verdict the agent hasn't earned ("Trusty-1").
These violate N-4 and the hobby-project anti-reference (V-3). The verdict word lives in the *status*,
never the *name* (N-3).

---

## 5. README hero rewrite

Replacement for the top of `README.md` (headline + subhead + first section), in brand register.
Premium & precise; leads with what is *true today* and marks the roadmap plainly (V-1, V-6). Drops
the current draft's emoji section-headers and the "blazing-fast" register for a cleaner, more
premium tone (PRODUCT.md anti-references). Emoji are permissible in marketing register (V-3) but
omitted here deliberately, to read as an instrument rather than a hobby project.

---

> # Mainguard
>
> **A native Git client for high-stakes work — becoming a control plane for the agent era.**
>
> Mainguard is a precise, natively-rendered Git GUI: a 60fps commit graph, line-level staging that
> behaves exactly like `git apply`, and a 3-pane conflict resolver, built on Avalonia and
> LibGit2Sharp with .NET 10. It's an instrument, not a web view in a frame. That client is
> shipping today. On top of it, we're building the harder thing: the place where autonomous agents'
> work becomes trustworthy commits on `main`.
>
> ## What's shipping today
>
> A fully working single-user Git client — this part is real and exercised by the test suite:
>
> - **Commit graph that stays smooth.** A virtualized, vector-drawn DAG lane router rendered
>   directly at 60fps — not a chart library — over large, tangled histories.
> - **Staging down to the line.** Stage, unstage, or discard by hunk; drag-select individual lines;
>   accept or reject blocks side-by-side. Every patch is validated against `git apply`.
> - **A conflict resolver that respects your work.** Synchronized Ours | Result | Theirs panes with
>   per-side accept/reject/undo; merge, rebase, cherry-pick, and pull all route here.
> - **Undo you can trust.** An operation-history journal makes ref moves reversible, backed by a
>   reflog viewer for the rest. Force-push uses `--force-with-lease`, never a bare `--force`.
> - **One design system, five themes.** Midnight Loom, Daylight Loom, Command Deck, Atelier, and
>   Loom Aurora — switchable live, persisted across sessions.
>
> ## Where it's going *(roadmap — not built yet)*
>
> The multi-agent control center below is **planned, not shipped.** We mark it clearly because the
> whole thesis is trust: a merge queue that re-verifies branches that go stale, a review cockpit
> with per-hunk provenance, a hardened local sandbox with default-deny egress, and an audit-grade,
> tamper-evident trail of what each agent did. Read the roadmap as the destination; the client above
> is the current state.

---

## 6. Release-notes voice guide

How Mainguard announces changes. Terse, honest, user-benefit-first — the same instrument voice, one
register warmer.

**Rules.**

1. **Lead with the user benefit, name the exact object** (V-1). "Line-level staging now matches
   `git apply` exactly," not "Improved staging engine."
2. **Terse. One or two sentences per entry.** No changelog padding, no "we're excited to."
3. **Calm, no theatrics** (V-2). No exclamation marks, no "HUGE update." Severity and importance are
   carried by *what changed*, not by louder words.
4. **Honest about scope** (V-6). Say what changed and what didn't. If something is a fix, call it a
   fix; if a feature is partial or behind a flag, say so. Never announce a **[Horizon]** item as
   shipped.
5. **Group by what the user does**, not by internal module: *Staging*, *Conflicts*, *Graph*,
   *Themes* — not `PatchBuilder`, `CommitGraphRouter`.
6. **Git terms stay lowercase and hyphenated** (N-6): `force-push`, `hard-reset`, `fast-forward`,
   `cherry-pick`, `index.lock`.
7. **No emoji in release-note bodies.** (Permissible in marketing register per V-3, but the changelog
   is close to the instrument — keep it clean.)

**Structure per release.** A one-line summary, then **Added / Changed / Fixed** groups. Each entry
is benefit-first.

### Example entry 1 — a feature

> ## 0.6 — Line-level staging
>
> Stage exactly the lines you mean, and trust that Git agrees.
>
> **Added**
> - **Drag-select individual lines to stage in the unified diff.** Previously staging stopped at the
>   hunk; you can now compose a commit line by line. The result is validated against `git apply`, so
>   what you stage is what Git records.
>
> **Fixed**
> - A stale `.git/index.lock` left by a crashed external process is now detected and explained,
>   rather than surfacing as an opaque failure. Mainguard does not remove a lock it didn't create — it
>   tells you how to check whether it's safe to remove.

### Example entry 2 — a smaller release

> ## 0.5.1 — Theme and conflict fixes
>
> **Changed**
> - **Loom Aurora** contrast raised on muted metadata so timestamps and hints stay legible on its
>   lighter panels. No layout or shape changed — color values only.
>
> **Fixed**
> - The 3-pane conflict resolver now keeps per-side undo history when you switch files mid-resolve,
>   instead of resetting it. Accept/reject on the wrong side is recoverable again.

---

## Appendix — self-gate & citations

- **Voice.** Copy is premium & precise; concrete objects over vague reassurance (V-1); calm, no
  exclamation theatrics (V-2); engineered, not cute (V-3, with the marketing-register emoji
  allowance noted and deliberately not used); destructive-safety and way-back framing carried into
  the founder story (V-4, V-5); honest about the machine throughout (V-6).
- **Honesty.** Every present-tense capability is on `main`; every roadmap capability is marked
  **[Horizon]** or "roadmap — not built." Audit framed as *audit-grade / tamper-evident*, not
  *legally required crypto*. Capacity framed honestly (~4–6 agents on 16 GB, not 100).
- **Naming.** Agent-name options stay in the loom / weave family and pair name with status (N-2,
  N-3, N-4); mascot/mood/verdict names explicitly rejected.
- **Sources.** Competitor tables and figures drawn from `docs/business/market-analysis/` (Competitor Research
  2026-07-07, MergeLoom Deep Dive 2026-07-07, Viability & Differentiation 2026-07, Naming &
  Competitive Landscape 2026-07); no blank cells; every competitor cell reflects those docs'
  findings, including their **unverified** caveats where relevant.
</content>
</invoke>
