# Mainguard Press / PR Kit — One-Pager & Supporting Materials

**Lane G Part 5b · Register: brand · Status: ready to attach to pitches.** Pitch mechanics (who, when, in what order) are locked in Master Doc §14.6 — journalist-first, 5-sentence personal pitch, embargo-free, screenshots + 90-second video attached, 15-minute call offered, Dutch pitches to Dutch outlets, **timed to land the same week as each Show HN** so the story is "launch," not "plans." This kit is what gets attached.

Two variants of the one-pager exist below: **Act One** (the client launch — the only version that exists until L2) and the **Act Two addendum** (appended only when the pipeline demonstrably works). Everything follows the honesty contract; a journalist who fact-checks every line should find every line boring to check.

---

## 1. The one-pager — Act One

> ## Mainguard
> ### A fast, native Git client for Windows — free, no login
>
> **What it is.** Mainguard is a natively-rendered Git desktop client built on .NET 10 and Avalonia — not an Electron shell around a web view. It is free, requires no account, and sends nothing off the user's machine.
>
> **Why it's news.** The polished-devtool wave of the last three years (Conductor, Warp, Raycast, Zed) shipped Mac-first, while Windows remains the largest developer OS — 59.2% of developers use it personally, ~48% professionally (Stack Overflow 2025). Mainguard is a deliberate inversion: premium native craft, Windows-first. Its free tier is also a deliberate inversion of category norms: no account wall, no private-repo restriction.
>
> **What it does (all shipped, exercised by a 1,000+ test suite):**
> - A commit graph that stays smooth on very large histories — a virtualized, vector-drawn lane router, not a chart library.
> - Staging down to the individual line, with every patch validated against `git apply`.
> - A synchronized 3-pane conflict resolver (Ours | Result | Theirs) with per-side accept/reject/undo.
> - An operation journal that makes ref moves undoable; force-push only ever with `--force-with-lease`.
> - One design system with five switchable themes.
>
> **The origin.** Mainguard began as an answer to `.git/index.lock` — the lock file a crashed tool leaves behind that makes the next Git operation fail without explanation. Its founding architectural rule: every repository handle opens and closes through a single deterministic path, so the app itself can never leave that lock behind.
>
> **Where it's going (roadmap — explicitly not built yet).** A control plane for AI coding agents: sandboxed local execution, branches that must pass the project's test suite before human review, a merge queue that re-verifies branches that go stale when main moves, per-line provenance, and an audit-grade record. Mainguard marks the shipped/roadmap line in every document, including this one, because the roadmap's whole subject is trust.
>
> **Facts.** Free tier: $0, no login. Platform: Windows-first; builds cross-platform (Avalonia). Stack: .NET 10, Avalonia + Skia, LibGit2Sharp, SQLite. Founder: Daniel Sazykin. Based: Enschede, the Netherlands. Available: [date] at [URL].
>
> **Founder quote.** "Coding agents made branches cheap and trust expensive. The industry is selling generation; the review side got a 91-percent-longer queue and a shrug. Mainguard ships the boring half first — a Git client you can trust with your work — because that's how you earn the right to build the part that decides what merges."
>
> **Second quote (craft angle).** "There's a belief that premium developer tools are a macOS genre. Windows developers are the largest group there is; they just stopped being catered to. Mainguard is rendered at 60 frames a second on the OS most developers actually use."
>
> **Contact:** [email] · Press assets: [link] · No embargo.

---

## 2. The one-pager — Act Two addendum

*(Appended below the Act One page when L2 ships; the Act One page's roadmap paragraph is then rewritten in the present tense.)*

> ### The second act: verification for AI-agent code
>
> **What shipped.** Mainguard's agent control plane: coding agents run in isolated local sandboxes (WSL2 on Windows), and their branches reach human review only after the project's own test suite passes inside the sandbox. When any branch merges and `main` moves, every other verified branch is automatically re-verified — the "stale verification" problem no merge queue on the market addresses (every existing queue re-runs CI; none re-runs verification on the post-rebase state).
>
> **The adoption hook.** Teams don't change how they run agents. Mainguard can subscribe to the pull requests their existing cloud agents (Codex, Jules, Copilot — any bot author) already open, run each through the same verify → review → merge pipeline locally, and merge back through the code host's own API. It writes nothing to the upstream PR without an explicit human action.
>
> **Vendor-neutral, local-first, honestly sized.** Any agent CLI, the user's own API keys (BYOK), everything on the user's hardware with no per-PR meter. Realistic capacity: a developer supervising roughly 4–6 agents on a 16 GB laptop — Mainguard explicitly does not market "agent swarms."
>
> **Pricing.** Client: free, no login, unchanged. Pro: $20/month or $199/year with a perpetual fallback. Team/Enterprise ($50+/seat): provenance, audit trail/SIEM, queue analytics — sold only now that the governance features exist.
>
> **Quote.** "Every merge queue on the market re-runs CI. None of them re-asks the only question that matters after main moves: is this branch still verified against the world it's about to join? A branch validated an hour ago, against an older main, is not validated. That one sentence is most of the product."

---

## 3. Fact sheet (for fact-checkers — keep current)

| | |
|---|---|
| Product | Mainguard — native Git client; roadmap: agent control plane |
| Founder | Daniel Sazykin |
| Company / base | [entity name per Master Doc Part XI] · Enschede (Kennispark/Twente), NL |
| Founded | [year] |
| Team | Founder + forming team (~5–6) |
| Funding | Bootstrapped [update if changed] |
| Stack | .NET 10 · Avalonia 11 + Skia · LibGit2Sharp · SQLite/EF Core |
| Platforms | Windows-first; cross-platform via Avalonia |
| Price | Client free (no account) · Pro $20/mo or $199/yr w/ perpetual fallback · Team $50+/seat [Act Two+] |
| Licensing | Client free; daemon source-available (FSL); security architecture published |
| Privacy | No login; opt-in telemetry with published schema; keys in OS keyring; BYOK |
| Tests | 1,000+ automated tests on the shipped client |

---

## 4. Angles by outlet type

- **Dev/product press & newsletters (Console.dev, TLDR, Pointer):** Act One — "the premium native Git client Windows never got, free without an account." Act Two — "the first merge queue that re-verifies stale agent branches."
- **HN-adjacent technical readers:** the engineering essays are the pitch (60fps graph architecture; the re-verifying queue design). Don't pitch the vision; pitch the mechanism.
- **Business/AI press:** the trust gap — 87% of developers distrust agent accuracy while agent PR volume compounds (1M+ PRs from one vendor's agent in five months); Mainguard monetizes verification, the layer the failed free-orchestration wave never could. Frame vs. the category's 2026 shakeout (two shutdowns), never vs. a named competitor as villain.
- **Tweakers (NL, at L1):** "Nederlandse ontwikkelaar bouwt native Git-client die AI-agents laat bewijzen dat hun code werkt" — product + local-founder angle (Master Doc §14.6 wording of record).
- **AG Connect (NL, at L2):** the enterprise/governance story — audit-grade agent records, .NET flagship craft ("proof you can build world-class native UI in .NET").
- **IO+ / Innovation Origins, regional (L1):** "Twente builds a global devtool" — Kennispark story.
- **Silicon Canals (milestones):** funding/design-partner news when it exists; never launch hype without a milestone.

---

## 5. Quotable lines (pre-cleared, verbatim)

- "Hope is not a merge strategy."
- "Validated-then-stale is unvalidated."
- "Your agents' work, test-verified before you see it." *(Act Two only)*
- "A test verdict is a fact, not an opinion."
- "Conductor for Windows — with verification." *(Act Two only; the sanctioned analogy)*
- "The client is the wedge; verification is the business."
- "No per-PR meter on your own hardware."

---

## 6. Asset list (what "press assets" contains)

All screenshots are real app captures (never mockups) at 2× on both Midnight Loom and Daylight Loom — a dark *and* a light set, so no outlet is forced to run a dark screenshot on a white page:

1. Hero: the full workspace — commit graph + staging + diff (`RepoDashboardView`).
2. The commit graph on a large, tangled history (`CommitTimelineView` / `CommitGraphCanvas`).
3. Line-level staging mid-drag (unified diff).
4. The 3-pane conflict resolver (Ours | Result | Theirs).
5. The five themes as a strip (same view × 5).
6. The force-push confirmation (the destructive-safety story in one image).
7. *(Act Two)* The merge-queue rail mid-stale-cascade, and the review cockpit with provenance chips — **captioned honestly**: prototype renders labeled as such until the shipped surface exists; from L2, live captures only.
8. The 90-second explainer video ([VideoScripts.md](VideoScripts.md) §1) + logo/wordmark pack.

---

## 7. Press FAQ (the five questions every journalist asks)

**Is this open source?** The client is free (not open source); the future daemon — the security boundary — is source-available (FSL) so it can be audited. The reasoning is published: two free-and-thin competitors died in 2026; source-available keeps the code inspectable and the company alive to maintain it.

**How is this different from GitHub Copilot's app / GitKraken / Conductor?** Each is honest about the other's strengths in our published comparison pages. The one-line map: first-party apps are single-vendor by design; GitKraken launches agents but ships nothing downstream of them; Conductor is the category leader and is Mac-only with a task queue, not a verification queue. Mainguard's bet is vendor-neutral verification, Windows-first, on a real Git client. *(Until L2, add: our verification layer is roadmap, marked as such.)*

**How does a free client make money?** It doesn't — it's the funnel. Revenue is the verification pipeline (Pro, $20/mo) and team governance ($50+/seat, sold only once the governance features exist). BYOK means Mainguard never marks up model inference.

**Is the AI-agent feature real?** *(Pre-L2)* No — and we're unusual in saying so this plainly. It's a published, dated roadmap; the shipped product is the client. *(Post-L2)* Yes — shipped [date]; the demo takes four minutes and we'll do it live on a call.

**Why the Netherlands / why Enschede?** Founder's base, University of Twente ecosystem (Kennispark), and a deliberate bet that a global devtool can be built from the Dutch tech corridor. *(For NL outlets, this is the lede, not the footnote.)*

---

## Self-gate (Part 5b)

- Two-variant structure enforces tense: Act One page marks the roadmap unbuilt in its own body; Act Two addendum exists only behind the "demonstrably works" gate; asset 7 carries the prototype-labeling rule (GTM Plan §10.2's no-silent-mocking norm extended to press).
- Every fact-sheet row and figure is sourced upstream (Windows share, test count, pricing, licensing — Master Doc §§2.1/7.1/8.1; MergeLoom Deep Dive §5); test count stated as "1,000+" so the sheet doesn't rot with each release.
- Framing: no competitor villain (the FAQ answers concede strengths); no EU-deadline scare anywhere; quotes pre-cleared from Narrative §3.3's sanctioned lines only.
