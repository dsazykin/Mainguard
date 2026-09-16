# Mainguard Email Sequences — Waitlist → Launch → Onboarding

**Lane G Part 5a · Register: brand (founder first-person, one degree warmer than product; personality unchanged) · Status: ready to load into the sender.**

**The structural constraint, stated first.** Mainguard's free tier requires no account, ever (trust posture, Master Doc §4.4) — so there is no "user email" to drip onto. Email reaches exactly two self-selected lists: the **waitlist** (landing page signups) and the **newsletter opt-in** offered at download. All sequences below are **time-keyed, never telemetry-keyed** — we don't know what a recipient did in the app, and the copy never pretends we do ("if you've already tried X" — never "we noticed you haven't"). This constraint is the trust posture working as designed; the emails say so out loud when relevant.

**Voice rules for email** (Bible V-rules, brand dialect): subject lines are plain statements, sentence case, no exclamation marks, no brackets-gimmicks, no "🚀". One idea per email; every email earns its send with something concrete (an essay, a build, a date). Contractions yes; "please"-padding no. Every email ends with the same one-line footer: *You're getting this because you joined the Mainguard waitlist. One click to leave: [unsubscribe].* Unsubscribes are honored silently — no "sorry to see you go" pages.

---

## Sequence A — Waitlist nurture (signup → L1)

Cadence: A1 immediately; A2–A4 ride the essay publishes (LaunchCampaignPlan §4), roughly bi-weekly; A5 at L1 − 3 days. If the calendar slips, the emails slip — never send filler to hold cadence.

### A1 — Welcome (instant)

**Subject:** `You're on the Mainguard waitlist — here's what that means`

> You signed up for Mainguard — thanks. Here's the deal, plainly.
>
> Mainguard is a native Git client for Windows — Avalonia + Skia on .NET 10, not an Electron shell. Free, no account, nothing leaves your machine. It ships this fall; waitlist cohorts get it first, in small batches, so we can fix what breaks before the next batch.
>
> Two honest notes while you wait. First: the multi-agent features you may have read about on the site are the roadmap, not the product — the launch is a fast, precise Git client, and we mark the difference everywhere. Second: because the app has no login, this list is the only way we can reach you. We'll use it rarely: essays worth your time, your cohort invite, and the two launch days. That's it.
>
> — Daniel

### A2 — The index.lock essay (rides essay (a))

**Subject:** `Why a crashed tool can corrupt your repo — and the rule that prevents it`

> Every developer who runs more than one Git tool eventually meets `.git/index.lock`: a process dies early, leaves the lock behind, and your next operation fails with a message that blames nothing and suggests nothing.
>
> Mainguard exists because of that failure. I wrote up the mechanics — why the lock exists, how it gets abandoned, why coding agents make it statistically inevitable, and the one architectural rule that means Mainguard can never leave that lock behind: [read the essay]
>
> The short version: a tool that guards your work doesn't guess on your behalf.
>
> — Daniel

### A3 — The commit-graph essay (rides §2.4 of the ContentCalendar)

**Subject:** `How Mainguard draws a 100,000-commit graph at 60fps`

> Most Git GUIs treat the commit graph as a diagram some chart library owns — and the diagram's cost grows with your history.
>
> Mainguard has no diagram. A pure routing function streams history in chunks, and each visible row is a tiny control that draws only its own lines. The graph you see is an emergent property of a virtualized list. Full write-up, with the actual type names and the testing story: [read the essay]
>
> When your cohort invite arrives, I'd like to know how it handles your gnarliest repository — that's the feedback that improves the router.
>
> — Daniel

### A4 — The trust posture (rides the trust assets shipping)

**Subject:** `No login, no telemetry by default — the boring documents that prove it`

> Before launch, we published the unglamorous part: the security architecture doc, the telemetry policy (opt-in, with a published schema of exactly what's sent), and the licensing statement (the client free forever; the future daemon source-available so its security boundary is auditable).
>
> A Git client sits between you and your code. I don't think it can ask for trust — it has to be structured so it doesn't need much. No account. Keys in the OS keyring. Nothing leaves your machine. The documents are here if you want to hold us to them: [links]
>
> — Daniel

### A5 — Launch heads-up (L1 − 3 days)

**Subject:** `Mainguard launches Thursday`

> Short one. Mainguard goes public this Thursday — free download, no signup. Waitlist cohorts already have it; the launch build is what you helped harden.
>
> If you find it useful on Thursday, the single most helpful thing you can do costs one minute: say something honest in the Hacker News thread — critical is as useful as kind. I'll be in the comments all day.
>
> — Daniel

---

## Sequence B — Launch sends

### B1 — Act one, launch day (whole list)

**Subject:** `Mainguard is out — free, native, no login`

> It's live: [download] — no account, no wall.
>
> What you get today: a commit graph that stays smooth on large histories; staging down to the individual line, validated against `git apply`; a synchronized 3-pane conflict resolver; an operation journal that makes ref moves undoable; five themes on one design system. What you don't get yet: the agent control plane — that's the roadmap, marked plainly in the README, and I'd rather you hold me to it than believe it early.
>
> The Show HN thread is here: [link]. I'm in it all day — bring your hardest questions and your ugliest repositories.
>
> — Daniel

### B2 — Act two, launch day (whole list; ships only when true)

**Subject:** `The part I said wasn't built — it is now`

> When Mainguard launched, the README marked the agent layer "roadmap, not built." Today it's built, and it's live: coding agents run in isolated local sandboxes, and their branches must pass your test suite — inside their sandbox, against the exact main they'll merge into — before they reach your review. Merge one branch, and every other verified branch goes stale and re-verifies automatically. Validated-then-stale is unvalidated.
>
> The part you can use without changing anything: if Codex, Jules, or Copilot already open PRs for your team, point Mainguard at them. Same pipeline — sandboxed verification, risk-ranked review, merge back through your host's own API. Nothing is written to the upstream PR unless you act.
>
> Honest limits, as always: this comfortably supervises about 4–6 agents on a 16 GB laptop; BYOK, local-first, and the free client is unchanged. Pro ($20/mo) unlocks the pipeline; the details are here: [pricing]
>
> Round-two Show HN: [link]. Come break the staleness model.
>
> — Daniel

---

## Sequence C — Onboarding drip (download-time newsletter opt-ins; time-keyed)

Cadence: C1 at day 1, C2 at day 4, C3 at day 10, C4 at day 21. Each stands alone (a reader who skips two loses nothing). Because there's no telemetry tie-in, every email frames features as "worth trying," never "we saw you didn't."

### C1 — Day 1: the first ten minutes

**Subject:** `Mainguard: the three things to try first`

> Three things worth your first ten minutes:
>
> **Open your biggest repository.** The graph is the app's signature — scroll it hard. It's drawn directly, not charted, and it should stay smooth where other tools give up.
>
> **Stage a single line.** In the unified diff, drag-select individual lines and stage exactly those. The patch engine is validated against `git apply`, so what you stage is precisely what Git records.
>
> **Switch themes.** File → Theme — five palettes on one design system, live. Daylight Loom if you're a light-theme person; nothing about the app assumes dark.
>
> Something broken or baffling? Reply to this email — it lands with me.
>
> — Daniel

### C2 — Day 4: the safety net

**Subject:** `The parts of Mainguard you'll be glad exist on a bad day`

> Git's scariest moments are why Mainguard is shaped the way it is. Worth knowing before you need them:
>
> **Undo exists for ref moves.** Operation history journals them — a bad reset or branch move is reversible, and the reflog viewer covers what isn't.
>
> **Force-push can't silently destroy teammates' work.** Mainguard only force-pushes with `--force-with-lease` — it refuses if anyone pushed since your last fetch, and the confirmation tells you what changes and what stays recoverable before you click.
>
> **Conflicts route to one place.** Merge, rebase, cherry-pick, and pull all land in the 3-pane resolver — Ours | Result | Theirs, per-side accept, reject, and undo.
>
> The product's core promise is that you never lose work to it. If you ever feel close, I want to hear about it more than any praise.
>
> — Daniel

### C3 — Day 10: the craft (and the ask)

**Subject:** `One design system, five themes — and one question`

> A short one about craft. Mainguard's five themes aren't five designs — shape, spacing, and type never change; only the color tokens do. It's why Command Deck and Daylight Loom feel like the same instrument in different light. The write-up on how the system stays coherent is here: [link]
>
> The question: what's the one thing Mainguard doesn't do that keeps another tool on your machine? Reply in a sentence — the answers genuinely steer the backlog.
>
> — Daniel

### C4 — Day 21: where this is going

**Subject:** `What Mainguard is becoming (and what it costs you: nothing)`

> You've had Mainguard for three weeks, so here's the direction, stated the way we state everything.
>
> The client you're using stays free, with no account, permanently. The roadmap on top of it is a control plane for coding agents: sandboxed local execution, branches that must pass your tests before you review them, a merge queue that re-verifies anything that goes stale, provenance and an audit-grade record. [If pre-L2: None of that is shipped yet — the design essays are public, and the waitlist for the first pipeline cohort is here: [link].] [If post-L2: That shipped — the tour is here: [link].]
>
> If your team is already drowning in agent PRs, that second act is for you. If you just wanted a fast Git client — that's a complete answer too, and it's the one we built first on purpose.
>
> — Daniel

---

## Self-gate (Part 5a)

- The no-login constraint is designed around, not ignored: every sequence is time-keyed, the copy never implies behavioral knowledge, and A1/A4 turn the constraint into the trust story.
- Tense discipline: B2 and the C4 post-L2 variant are gated "ships only when true"; A1, B1, C4-pre mark the roadmap unbuilt; capacity (4–6), BYOK, and audit framing match the honesty contract.
- Voice: plain-statement subjects, zero exclamation marks, one idea per email, concrete objects throughout (`git apply`, `--force-with-lease`, File → Theme), standing unsubscribe footer, replies routed to a human.
