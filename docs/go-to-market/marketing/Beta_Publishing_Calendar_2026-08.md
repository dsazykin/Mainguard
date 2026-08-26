# Mainguard — Six-Week Publishing Calendar & Anchor Post

**Date:** 2026-08-20 · **Register:** brand · **Cadence assumption:** a few hours a week, alongside development
**Reads under:** [`ContentCalendar.md`](ContentCalendar.md) (the pieces themselves), [`LaunchCampaignPlan.md`](LaunchCampaignPlan.md)
(the two-act structure), [`SocialLaunchReserve.md`](SocialLaunchReserve.md) (Show HN bodies — **held**, see §1),
[`Manifesto.md`](Manifesto.md) (the anchor essay, already final).
**Binding:** the honesty contract, [`../../creative/Narrative.md`](../../creative/Narrative.md) §0 — shipped vs
**[Horizon]** tense, "audit-grade" never "legally required," honest 4–6-agent capacity.

---

## 1. Two decisions this calendar makes, and why

### Decision 1 — the manifesto moves from last to first

`ContentCalendar.md` schedules *Ungoverned AI merges are a time bomb* at **L2 − 1 week**, on the logic that
the thesis lands just before the product answers it. That sequencing assumes a product launch is imminent.
It isn't: the merge-queue pipeline is still in integration and there is no demo video, so there is no L2 to
sit one week ahead of.

**So the manifesto becomes the opening move rather than the closing one.** This is the right piece for
exactly the constraints we have: it is finished, it is entirely argument, it needs no demo, no binary and no
screenshot to be true, and it mentions Mainguard once in a self-discounting disclosure. A campaign built on
argument is the only kind that works when the product is honest-but-unfinished — and the audience we want
(the 87% who distrust agent output) is the audience that reads arguments.

The rest of the calendar then runs *toward* a launch instead of away from one. When the pipeline works, the
Show HN lands on an audience that already knows the thesis.

### Decision 2 — Hacker News is held in reserve, deliberately

**Do not Show HN in these six weeks.** Both Show HN bodies in `SocialLaunchReserve.md` stay in the drawer.

The reasoning is unsentimental: Show HN is effectively one shot per product, an HN front page for
"safe-to-merge for AI agents" will ask within four comments for a video and a binary, and the honest answers
today are "not yet" and "unsigned, and the agent half isn't in it." Spending the shot now converts your best
distribution event into a mediocre one and burns the "new" framing you only get once.

**What HN is for in these six weeks:** reading and commenting. Threads about agent orchestration, merge
queues, Conductor, GitKraken Agent Mode, Windows dev tooling. Substantive comments under your own name, no
links unless someone asks. This builds the account and the reputation that make the eventual Show HN land —
and it is genuinely where you'll learn the objections before they're aimed at you.

**The gate that unlocks L1/L2:** an end-to-end run — spawn → agent works → verify → review → merge —
recorded, reproducible, and survivable under a stranger clicking around. Not a date. When that exists,
switch to `LaunchCampaignPlan.md` and the reserve.

---

## 2. Channels, and what each is actually for

| Channel | Role | Cadence | Why |
|---|---|---|---|
| **LinkedIn** | **Primary.** Long-form posts, native (not link-outs) | 2×/week | The only channel that doubles as warm-up for the Dutch outreach in [`../sales/Beta_Outreach_Sequences_2026-08.md`](../sales/Beta_Outreach_Sequences_2026-08.md). Every target's engineering leads are here. A prospect who has seen three of your posts is a different email |
| **Your own blog** (mainguard.dev) | **Canonical.** Every substantial piece lives here first | Per piece | You own it, it accrues SEO against MergeLoom's content wall, and it's what you link to for the rest of the product's life |
| **X / Twitter** | Build-in-public, agent-tooling crowd | 2–3×/week | Where the Claude Code / Codex practitioner conversation happens. Short, screenshot-led |
| **Reddit** | Selective, per piece | ~1×/3 weeks | r/dotnet and r/ClaudeAI are real audiences. **Participate for two weeks before you post anything of your own** — both subs punish drive-by promotion, and a removed post costs more than it gains |
| **Hacker News** | **Comment only.** No submissions | Opportunistic | See §1 |
| **Lobsters** | Optional, for the technical essays | Per piece | Small but high-quality; needs an invite |

**Two things to set up in week 0 before anything publishes:**

1. **A blog on mainguard.dev.** Everything else points here. Without it the LinkedIn posts have nowhere to
   go and the essays live on someone else's platform.
2. **A profile that isn't empty.** A brand-new account whose first post is a 2,000-word manifesto reads as a
   marketing drop. Two or three small build-in-public posts first — a screenshot of the commit graph, a note
   about a bug you fixed, the walkthrough you ran yesterday — make the manifesto land as a person's argument
   rather than a campaign's opening.

---

## 3. The six weeks

Week 0 starts **Monday 24 August 2026**. Dates are guides; the sequence is not.

### Week 0 (24–30 Aug) — foundation, no campaign yet

| Item | Channel | Notes |
|---|---|---|
| Blog live on mainguard.dev | Site | Minimal: title, body, RSS. Do not redesign anything |
| 3 short build-in-public posts | LinkedIn, X | Commit-graph screenshot; the HTTP/2 queue-starvation bug you found and fixed during the live walkthrough (a genuinely good story: the terminal stream was starving the queue stream on a shared connection); one on why the client has no account wall |
| Start commenting | HN, r/dotnet, r/ClaudeAI | Two or three substantive comments. No links |
| **Also, from the sales track** | — | First 10 outreach sends go out this week. The two tracks should start together — the posts make the emails land better |

### Week 1 (31 Aug – 6 Sep) — **the anchor: the manifesto**

| Item | Channel | Notes |
|---|---|---|
| *Ungoverned AI merges are a time bomb* | Blog (canonical) | Publish as-is from [`Manifesto.md`](Manifesto.md) — it's final. Keep the source ledger visible at the bottom; it is the piece's armour |
| LinkedIn long-form adaptation | LinkedIn | §4.1 below. Native post, link in first comment |
| X thread | X | §4.2 below |
| Reply to every comment | All | Budget an hour. The comments are the point — the source ledger exists so you can answer challenges with a citation instead of an assertion |

**Do not post this to HN.** If someone else submits it, engage in the thread — that's ideal and doesn't
spend your Show HN.

### Week 2 (7–13 Sep) — the craft piece

| Item | Channel | Notes |
|---|---|---|
| *A 60fps commit graph without a chart library* | Blog, LinkedIn, X | Already **drafted** in `ContentCalendar.md` §2.4. Pure engineering, no product claims, entirely about shipped work — the perfect second piece because everything in it is present-tense true |
| Reddit: r/dotnet | Reddit | Only if you've participated for two weeks. This is the piece that fits there — Avalonia, Skia, .NET perf |
| 2 short posts | LinkedIn, X | Screenshots from the client. Themes: line-level staging validated against `git apply`; the four themes |

### Week 3 (14–20 Sep) — the argument gets technical

| Item | Channel | Notes |
|---|---|---|
| *The merge queue that re-verifies* | Blog, LinkedIn | **Drafted** (`ContentCalendar.md` §2.5). The single most differentiating idea you have, and it stands as a design argument without a running demo. Mark the implementation status plainly |
| 2 short posts | LinkedIn, X | Architecture diagrams. **This is where screenshots-and-architecture is a strength, not a limitation** — a clear diagram of "verification verdict read by the daemon from outside the container" is more persuasive than a screen recording |

### Week 4 (21–27 Sep) — the trust asset

| Item | Channel | Notes |
|---|---|---|
| Publish [`docs/security-architecture.md`](../../security-architecture.md) publicly | Site | The proof asset the market research names for exactly this audience: *"this audience converts on verifiable claims."* Publishing it while the product is unfinished is a *stronger* move than publishing it at launch — it says you designed for scrutiny before you needed to survive it |
| A post about the egress-proxy design | Blog, LinkedIn, X | Default-deny egress, model APIs and registries reachable, **the git host is not** — so an agent can't clone or exfiltrate. This is the most concrete, most checkable claim you own |
| Dutch-language LinkedIn post | LinkedIn | The EU-hosting / local-first angle. Every serious Dutch target raised it independently (Topicus: *"Data mag niet naar Big Tech"*). This one post does more for the sales track than anything else on the calendar |

### Week 5 (28 Sep – 4 Oct) — the comparison

| Item | Channel | Notes |
|---|---|---|
| *Mainguard vs GitKraken* | Blog | **Drafted** (`ContentCalendar.md` §2.1). Update GitKraken's column against their current changelog before publishing — they move fast. Note this page already concedes, honestly, that GitKraken ships agents today and you don't. Keep that; it's why it's credible |
| **Hold** *Mainguard vs Conductor* | — | Its own gate: publishes only once the pipeline demonstrably works, because its Mainguard column has to be present-tense |
| 2 short posts | LinkedIn, X | Progress on the merge-queue pipeline. Honest, specific, unglamorous |

### Week 6 (5–11 Oct) — in person

| Item | Channel | Notes |
|---|---|---|
| **Hacktoberfest at Arcady, Zwolle — 8 October** (and 15th) | In person | An hour from Enschede, no introduction required, and a target company hosting. Go |
| *The `.git/index.lock` problem* | Blog, LinkedIn | Outlined in `ContentCalendar.md` §3.1. The origin story of the whole product, and it is about shipped behaviour |
| Review and re-plan | — | Six weeks of reply rates and call notes. Decide whether the next six weeks are more of the same or the L1 run-up |

**Standing through all six weeks:** comment substantively where the conversation already is — under
Sjoerd's Claude Code post at De Voorhoede, on Jettro Coenradie's spec-driven-development pieces, in Q42's
and Euricom's repos, in HN threads on agent orchestration. **Ten minutes of a real reply on someone else's
post reaches more of the right people than an hour on your own**, at this stage, from a standing start.

---

## 4. The anchor post, adapted per channel

The essay is final in [`Manifesto.md`](Manifesto.md) and publishes verbatim to the blog. What follows is the
channel packaging only — same argument, same honesty, different container.

### 4.1 LinkedIn — native long-form

Post natively (LinkedIn suppresses reach on link-outs); put the blog link in the first comment.

> **Hope is not a merge strategy.**
>
> A time bomb has one defining property: the damage happens later than the mistake. At the moment of the
> mistake, everything looks fine.
>
> Merging code you didn't verify works exactly like that. The merge is painless. The build stays green. The
> standup stays short. The cost arrives weeks later — a refactor that touches the module nobody really read,
> an incident review that needs to know why a query changed, an auditor asking a question your Git history
> cannot answer.
>
> Three things are now true at once:
>
> → Coding agents made branches cheap. Teams using AI assistance merge roughly twice as many PRs; one
> vendor's coding agent opened over a million in its first five months.
> → Review did not get cheap. PR review time is up 91%, against a review capacity that is fixed. There are
> no more senior-engineer hours this quarter than last.
> → Trust collapsed on schedule. 87% of developers say they're concerned about agent accuracy. Under high AI
> adoption, code churn is up 861%.
>
> Cheap branches, fixed review, falling trust. There's no way to hold those three lines together without
> something giving — and what gives is diligence. Not because engineers got lazy. Because the arithmetic left
> them no other variable to cut.
>
> Only 31% of developers run agents today. This is what the *early* innings look like.
>
> The failure isn't the agents. It's a practice older than they are, now scaled past what it was ever safe
> for: **the blind merge** — code nobody fully read, verified against a main that has since moved, written by
> a process nobody can attribute, merged because the diff was too long and the queue was too deep.
>
> One clause deserves special attention, because almost every tool on the market gets it wrong. *"It passed
> tests" — an hour ago, against an older main.* A branch is verified. A different branch merges. Main moves.
> Nothing conflicts textually, so it merges anyway — on evidence from a world that no longer exists.
> **Validated-then-stale is unvalidated.** Every merge queue on the market re-runs CI. None re-runs
> *verification* on the post-rebase state of the branches behind it.
>
> Defusing this needs five properties, and any team can demand them of any tooling — including tooling they
> build themselves:
>
> 1. Verification is deterministic and local to the gate — a fact, not an opinion.
> 2. Verification is fresh. When main moves, every verified branch is stale until re-verified.
> 3. Work is attributable — which process, under which instruction, produced which lines.
> 4. Execution is contained.
> 5. The record is tamper-evident.
>
> Disclosure: I'm building a product in this space, and I've marked plainly what's shipped and what isn't.
> Discount my incentives accordingly — the argument doesn't need my product to be true. Build the five
> properties into your own pipeline with your own tools and this has done its job.
>
> Every one of us has committed a blind merge. The diff was long, the sprint was ending, the tests were green
> an hour ago, and we hoped.
>
> At human speed, hope was a survivable vice. At agent speed, it's a wiring diagram.
>
> *Full essay with every figure sourced — link in the comments.*

**First comment:** `Full version, with the source for every number: {link}` — nothing else.

### 4.2 X — thread

> **1/** A time bomb has one defining property: the damage happens later than the mistake.
>
> At the moment of the mistake, everything looks fine.
>
> That's what merging unverified code is. A thread on why ungoverned AI merges are a time bomb 🧵
>
> **2/** Three things are simultaneously true right now:
>
> • AI-assisted teams merge ~2x more PRs
> • PR review time is up 91%
> • 87% of devs say they don't trust agent accuracy
>
> Cheap branches. Fixed review capacity. Falling trust. Something has to give.
>
> **3/** What gives is diligence.
>
> Not because engineers got lazy — because the arithmetic left them no other variable to cut.
>
> And only 31% of developers run agents today. This is the *early* innings.
>
> **4/** The failure isn't the agents. Agents are why this is worth doing at all.
>
> The failure is a practice older than they are, scaled past what it was safe for.
>
> The blind merge.
>
> **5/** The blind merge: code nobody fully read, verified against a main that has since moved, written by a
> process nobody can attribute, merged because the diff was long and the queue was deep.
>
> Every clause is a separate mechanism.
>
> **6/** The one almost every tool gets wrong:
>
> "It passed tests" — an hour ago, against an older main.
>
> Branch verified → another branch merges → main moves → nothing conflicts textually → it merges anyway, on
> evidence from a world that no longer exists.
>
> **7/** Validated-then-stale is unvalidated.
>
> Every merge queue on the market re-runs CI.
>
> None of them re-runs *verification* on the post-rebase state of the branches behind it.
>
> **8/** "We added AI review" doesn't fix it either.
>
> An opinion about code is not evidence about code. In one audit ~35% of an AI reviewer's comments were
> useful — which trains you to skim the other 65%.
>
> Skimming is the habit that produced the blind merge.
>
> **9/** Five properties defuse it. Demand them of any tooling, including your own:
>
> 1. Verification is deterministic
> 2. Verification is fresh — main moves, everything goes stale
> 3. Work is attributable
> 4. Execution is contained
> 5. The record is tamper-evident
>
> **10/** Disclosure: I'm building a product on exactly these five. Discount my incentives — the argument
> doesn't need it to be true.
>
> Every one of us has done a blind merge. The diff was long, the sprint was ending, the tests were green an
> hour ago, and we hoped.
>
> **11/** At human speed, hope was a survivable vice.
>
> At agent speed, it's a wiring diagram.
>
> **Hope is not a merge strategy.**
>
> Full essay, every figure sourced: {link}

### 4.3 Reddit — if and when you post it

**Where it fits:** r/ExperiencedDevs and r/ClaudeAI. Not r/programming (strict), not r/dotnet (wrong topic
for this piece — save r/dotnet for the 60fps graph essay).

**Rules for this one:** post the *essay text* in the body, not just a link; put the disclosure in the first
paragraph rather than at the end, because Reddit will find it and will be much harsher if it feels buried;
be present in the comments for the first three hours or don't post at all.

---

## 5. What "posting" means when you have no demo

Since Act Two is what you're leading with and the pipeline isn't finished, the content has to carry weight
that a video normally would. Three formats do that well, and they're all cheap to produce:

**Architecture diagrams beat screen recordings for this specific claim.** "The verification verdict is the
container's real exit code, read by the trusted daemon from outside the container, so an agent cannot forge
a pass" is *clearer* as a diagram than as a video of a green checkmark. Lean into it.

**The bug story is the best build-in-public format you have.** Yesterday's walkthrough produced a real one:
the queue stream and the terminal PTY stream shared an HTTP/2 connection, and an attached terminal's
flow-control window could starve queue delivery — which looked exactly like "my agent never appeared in the
queue." You found it by instrumenting, fixed it by giving the queue its own connection, and added a
regression test. That post writes itself, demonstrates real engineering, and claims nothing unshipped.

**Honest status posts outperform polished ones at this stage.** "Here's the review cockpit. Here's what
works. Here's the three things in it that are still wrong" earns more trust from this audience than any
amount of polish — and it is the same posture that makes the outreach emails work.

---

## 6. What to measure, and what to ignore

**Measure:** replies and DMs from people who run agents; waitlist signups with a company domain; inbound
that mentions a specific essay; calls booked off a post. Four numbers, written down weekly.

**Ignore:** impressions, follower count, likes. At this volume they measure the algorithm, not the thesis.

**The one that matters:** how many of the ~10 outreach conversations per fortnight say some version of *"I
read your thing about stale verification."* When that starts happening, the campaign is working and the two
tracks have joined up. Until then, the posts are just practice — useful practice, but not yet leverage.
