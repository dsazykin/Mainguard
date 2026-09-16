# Mainguard — GTM Assets

**The copy, kept ready.** One-pager, pitch decks, demo script, outreach sequences, site and email copy,
the scored target-company list, grant drafts, launch-day posts, press and video material.

**Everything here is a template to update, not a finished artifact.** Each was written against a
particular moment; check §1 before you send, post or present any of it.

| | |
|---|---|
| **The strategy it expresses** | [`BUSINESS.md`](BUSINESS.md) |
| **When each one fires** | [`GTM.md`](GTM.md) |
| **The full originals** | [`business/`](business/) — each section below links to its own |

---

## 1. How to use this file

**Before sending, posting, or presenting anything below, check it against four things.**

1. **The honesty contract** ([`creative/Narrative.md`](creative/Narrative.md) §0). Shipped is shipped;
   everything in the agent pipeline is **[Horizon]** and says "in development." Capacity is 4–6 agents, not
   fifty. The audit story is "audit-grade, where procurement is heading," not "legally required." Cloud
   figures are illustrative placeholders and are flagged at first use.
2. **The register rules.** Concede what's true before claiming anything. Sourced facts, no adjectives.
   Competitors get respect-then-stop — the enemy is the blind merge, never a company and never "AI." No
   exclamation marks. Leave the line, then stop.
3. **The never-say list.** "Swarm." "50 agents." Leading with "orchestration." These are the vocabulary of
   the companies that died.
4. **Dates.** Every template below carries assumptions about what exists. **Re-verify before every send** —
   job ads expire, people move, companies merge, grant parameters change annually.

**The five-question gate** every asset clears: does it point at a concrete object? · is there a way back
for the reader? · would it survive being read aloud in an audit? · can a word be deleted? · does the
severity ride the facts rather than the adjectives?

---

## 2. The one-pager

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) §15.3; [Press Kit](business/go-to-market/marketing/PressKit.md) for the press variant.*

Send 3–5 days ahead of any meeting. Keep it to one page.

> **Mainguard — make AI-agent code safe to merge.**
>
> **Problem.** 90% of developers now use AI daily and agents author code at scale (Copilot's agent alone:
> 1M+ PRs in five months) — but 87% of developers distrust agent accuracy, review time is up 91%, and
> delivery stability is measurably degrading. Every vendor ships "run agents in parallel"; nobody ships
> "trust what they produced."
>
> **Product.** A native desktop Git client (built, working — this is the demo) evolving into a control
> plane that runs agents in isolated sandboxes, requires your test suite to pass before a human reviews the
> diff, ranks review by risk with per-line agent attribution, re-verifies anything the merge queue lets go
> stale, and keeps an audit trail of every agent action. Vendor-neutral (Claude Code, Codex, OpenCode),
> local-first, Windows-first — the category leader is Mac-only and the largest developer OS is unserved.
>
> **Business.** Free Git client (no login) as the funnel → $20/mo Pro (verification pipeline, BYO agent
> subscriptions) → $50+/seat teams (merge governance + audit). The 2026 failure ledger (Bloop, Terragon)
> proves orchestration alone doesn't monetize; verification and governance price against CodeRabbit
> ($24–48/dev/mo) and Graphite (~$40).
>
> **Status.** Git core shipped (1,042 tests); verification pipeline in development. Pre-revenue by design
> until design partners validate.
>
> **What I want from an hour with you:** your judgment on launch sequencing, first paying customers, and
> bootstrap-vs-raise — plus anything you'd steal or kill in this plan.

*Swap the final paragraph per audience: for a fund, the round; for an angel, the intent letter that unlocks
VFF; for an operator, a design-partner team.*

---

## 3. The investor deck (seed / pre-seed master)

*Detail: the full decks with every speaker note — [Seed](business/go-to-market/decks/Mainguard_Investor_Deck_Seed.md), [Technical Deep-Dive](business/go-to-market/decks/Mainguard_Deck_Technical_DeepDive.md), [Advisor/Board Update](business/go-to-market/decks/Mainguard_Deck_Advisor_Board_Update.md), [Swarm Vision](business/go-to-market/decks/Mainguard_Deck_Swarm_Vision.md).*

**Narrative arc, locked:** problem → why now → the wedge → the moat → traction → market → business model →
the ask. **Fires when** the raise trigger does ([`GTM.md`](GTM.md) §6.2). **Do not present it with empty
traction slots — the deck ships when the slots do.**

**Slide 1 — Purpose.**
> **Mainguard makes AI-agent code safe to merge.**
> A native Git client, shipped. On top of it: the control plane where any agent's work becomes trustworthy
> commits on `main`.

*One sentence, then stop.* The subtitle carries the two-part structure the whole deck follows — something
real underneath, something valuable on top. Every later slide expands one of its words: *AI-agent* (why
now), *safe* (the pipeline), *merge* (the moat). Don't open with the demo or the architecture.

**Slide 2 — Problem: the blind merge.**
> Agent CLIs made it trivial to produce ten branches an hour. Reviewing them didn't get faster.
> - AI-assisted teams merge **~2× more PRs**; review time is up **91%** — against a fixed human ceiling
> - **87%** of developers are concerned about agent accuracy; **81%** about security
> - Copilot's coding agent alone: **1M+ PRs in five months**; code churn **+861%** under high AI adoption
>
> **And the code gets merged anyway.** Hope is not a merge strategy.

*Name the enemy as a practice, not a competitor and not "AI" — our users run six agents.* Every investor in
the room has either committed a blind merge or funded a team that does it daily. The gap between "merged"
and "verified" compounds without us spending a dollar — that is the shape of a good problem.

**Slide 3 — Why now.**
> The bottleneck moved from writing code to trusting it.
> - Agent CLIs went mainstream in 24 months — yet only **31%** of developers run agents today. Early innings.
> - "Agents in worktrees + a GUI" is now **free table stakes** from every platform vendor
> - Every vendor sells generation. **Nobody sells trust** — the capabilities that would create it are
>   verified empty across the field
> - Meta built **RADAR** internally (catching a 1/3 revert rate) because the verification layer couldn't be
>   bought

*Three moves happened in sequence:* generation got commoditized, trust collapsed, and the verification
layer stayed empty. The 31% is the timing argument — the trust problem is about to get 3× bigger as the
other 69% arrive. If asked "why hasn't anyone built it": because it requires a real Git engine underneath,
and everyone else in the category is a wrapper. That's slide 6.

**Slide 4 — The wedge.**
> The free, excellent, native Git client Windows never got.
> - **Shipped and working today** — 60fps commit graph, line-level staging validated against `git apply`,
>   3-pane conflict resolver. 1,042 tests.
> - **No login, no private-repo wall, nothing leaves your machine** — the incumbent's free tier has both
> - **Windows-first in a Mac-first category** — the largest developer OS; the category leader is Mac-only
>
> Free users come for the Git client. Teams pay for trustworthy merges.

*The wedge is deliberately not the AI story* — it's a real product whose value doesn't depend on believing
a roadmap. That ordering is priced-in failure analysis. Honesty note if probed on distribution: state
plainly whether the client is a packaged download or a build-from-source preview today.

**Slide 5 — The product: the verification pipeline** *(roadmap — in development)*
> **plan approval → sandboxed execution → your tests pass in the agent's sandbox → risk-ranked,
> provenance-annotated review → a merge queue that re-verifies whatever goes stale → human-gated merge**
>
> *Validated-then-stale is unvalidated.*

*Walk it left to right; three stops deserve ten seconds each.* **Test gates, not opinions** — the verdict
is your own suite passing in the agent's sandbox, a fact rather than an LLM comment. **The re-verifying
queue** — no product on the market re-runs verification on the post-rebase state. **Provenance per hunk** —
the Agent Trace standard has emitters coming and no renderer; we intend to ship the first. **Everything on
this slide is [Horizon] — say so unprompted.** Under-claiming here *is* the credibility play; this audience
has been vaporware-pitched all year.

**Slide 6 — The moat.**
> Any single feature has a ~2-quarter exclusivity window. The combination doesn't.
> 1. **A real Git engine** — the prerequisite every wrapper lacks; shipped, 1,042 tests
> 2. **The compound pipeline** — sandbox + test gates + re-verifying queue + provenance + audit
> 3. **Vendor-neutral** — every first-party GUI manages only its own agents. Be Switzerland.
> 4. **Windows/WSL2-native** — the largest developer OS, structurally unserved

*Architectural, not feature-list.* Their agents are the PR firehose our intake drinks from — Jules and
Codex are supply, not competition. Bring [`BUSINESS.md`](BUSINESS.md) §8 to diligence.

**Slide 7 — Competition, on one slide.** The [`BUSINESS.md`](BUSINESS.md) §4.2 table collapsed to four
rows, closing on: **we own the intersection — local verification × merge governance × vendor-neutral ×
Windows-native.** Hand them the Conductor analogy yourself. Name **Cursor Origin** as the tripwire
proactively — *naming your own tripwire is worth more than a competition slide.*

**Slide 8 — Traction** *(template — populate at raise time)*
> - **Shipped:** the full native Git client — 1,042 tests
> - **Weekly active repos:** `[n]` · retention curve `[chart]`
> - **Agent runs verified per week:** `[n]` · **% of merges against non-stale verification:** `[n]`
> - **Design partners:** `[3–5 named teams]` on the verification pipeline
> - **Free→Pro conversion:** `[n]%` · MRR `[$n]`

**Slide 9 — Market.** $7–9B → $20–30B by 2030–31 · ~47M developers · the wedge OS · and the row that
matters more than the CAGR: **willingness-to-pay already proven adjacent** (CodeRabbit $24–48/dev/mo,
Mergify $8+, Graphite ~$40). For Dutch/EU investors add the NL sub-slide with the honest caveat: **NL is a
beachhead, not a market.**

**Slide 10 — Business model.** The [`BUSINESS.md`](BUSINESS.md) §7.1 tier table, closing on **BYOK = no
inference-margin death. We charge for trust, not tokens.** Raise both cloud caveats *yourself* before a
diligence associate does: the numbers are placeholders pending beta telemetry, and the cloud revenue line
is mostly pass-through — **quote gross-margin dollars, never the revenue column as ARR.**

**Slide 11 — Team & home base.** Shipped a real Git engine — the hard prerequisite every wrapper lacks ·
Windows/.NET depth in a Mac-first field · Enschede: UT/Saxion pipeline, salaries €5–10k below Randstad, and
a subsidy stack funding 30–50% of R&D wages before dilution. *"Why us" is the engine, not the résumé.* For
non-EU investors compress the base to one line: "our burn multiple benefits from a Dutch subsidy stack and
Twente engineering costs."

**Slide 12 — The ask.**
> **€750k–1.5M pre-seed** *(or, on launch traction: a $2–4M seed)*
> Buys, in order: (1) the verification pipeline in design partners' hands; (2) the first 2–3 engineering
> hires (WBSO-subsidized; grants extend the round ~40%); (3) the governance layer that unlocks $50+/seat.

*The two-track anchor is deliberate — decide after launch data, and saying so signals discipline rather
than indecision.* Use-of-funds ordering mirrors the moat. End on the slide-1 sentence and stop talking.

**Appendix slides (hold ready, don't present):** A — the failure ledger ("why won't you die like the
others"). B — the cloud cost model, with the illustrative flag printed on the slide. C — the empty squares
("isn't this crowded"). D — the EU/sovereignty angle. E — the demo (§4).

### 3.1 Deck variants

- **Technical deep-dive** — the safe-to-merge guarantee *as* the moat, layer by layer with task-ID anchors:
  the claim stated as an invariant → Layer 0 (shipped) a real Git engine and why it's the prerequisite →
  containment (the quarantine remote) → the verdict as fact not opinion → the re-verifying queue (the empty
  square) → provenance and risk-ranked review → the audit record → the same guarantee in the cloud on the
  same binary → **what we deliberately do not build.**
- **Advisor / board update** — where things stand on one screen → since last time → the numbers (small,
  honest, trending) → **the 2–3 decisions I want your judgment on** → the honest three risks → **exactly
  one ask.** This is the recurring report-back template; its value is the cadence, not the slides.
- **Swarm vision** — the ten-year story told honestly, in three acts: the role is changing, not the
  developer → Act I, shipped: the instrument → Act II, in development: several agents, perfectly governed →
  the ceiling is a business model, not a wall → Act III **[Horizon]** → why this is ours to build. Keep the
  vocabulary discipline: this deck is the one place the long-range story is told, and it still never says
  "swarm of fifty."

---

## 4. The demo script (10 minutes)

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) §15.2; [Video Scripts](business/go-to-market/marketing/VideoScripts.md) for the filmed version.*

1. **(0–2 min)** Open a big real repo. The graph renders instantly; flip themes; partial-stage a hunk by
   dragging lines. *Message: this is a real Git client, not a wrapper.*
2. **(2–4 min)** The pain: spawn two agent tasks; show worktree isolation — no `.git/index.lock` roulette,
   nothing touches your working directory.
3. **(4–7 min)** The wedge: an agent finishes → verification runs the test suite → one branch passes, one
   fails → **the failing one never reaches review.** Open the passing diff: risk-ranked hunks, provenance
   gutter ("Agent A, plan #12").
4. **(7–9 min)** Merge the first branch → the second's verification goes stale → the queue auto re-verifies
   before offering the merge. *"No other product on the market does this step."*
5. **(9–10 min)** Close on the audit view: every spawn, plan approval and merge, attributable. *"This is
   what your compliance team asks for in 2027."*

**Anything not yet built runs scripted or mocked and is labeled "landing in vN" out loud.** Do not fake it
silently for an experienced founder or engineer; they will probe. **Failure modes to avoid:** too deep on
architecture with no pain narrative; no specific customer; burying the demo behind slides; scale claims the
hardware can't cash; pitching the tech instead of the switching costs and workflow depth.

---

## 5. Outreach sequences

*Detail: [Beta Outreach Sequences](business/go-to-market/sales/Beta_Outreach_Sequences_2026-08.md) (all four openers, EN + NL, email + LinkedIn, reply handling).*

**Stage rule:** the ask is **"tell me how you handle this today"** — never "buy," never "try our product,"
and in the first message usually not even "look at this." Ten well-researched sends a week beats a hundred
sprayed. Never send any of these unedited: name their specific artefact in the first sentence or don't
write to them.

### 5.1 The peer opener (highest reply rate — where two openers apply, this one wins)

For people who have publicly built or evaluated something adjacent. You are writing engineer-to-engineer
about a shared problem, and you are the one asking for help.

> **Subject:** your worktree-per-agent evaluation
>
> Hi {name},
>
> I found {specific artefact — e.g. "your poc-claude-code repo, and the Multi Agents Workflow Support
> section comparing Cursor worktrees, Worktrunk and Workmux"}. I've been deep in the same problem for a
> while and I've never seen anyone lay the comparison out that plainly.
>
> I'm building a native Git client in the Netherlands ({url}) that's growing into the layer *after* the
> agents finish — sandboxed runs, a merge queue that re-verifies branches when main moves, per-hunk
> provenance in review. The Git client works today. The agent side is still in integration, so I'm not
> pitching you anything.
>
> What I'd like is 30 minutes to hear how you actually landed on your setup: what broke, what you rejected,
> and what you'd still fix. I'll send you what I've built and you can tell me where it's wrong — that trade
> seems fair to me.
>
> Worth a call in the next couple of weeks?

**Nederlands:**

> **Onderwerp:** jullie evaluatie van worktree-per-agent
>
> Hoi {naam},
>
> Ik kwam {artefact} tegen. Ik zit al een tijd diep in hetzelfde probleem en ik heb die vergelijking nog
> nergens zo helder opgeschreven gezien.
>
> Ik bouw vanuit Enschede een native Git-client ({url}) die doorgroeit naar de laag *ná* het werk van de
> agents: runs in een sandbox, een merge queue die opnieuw verifieert zodra main verschuift, en herkomst
> per hunk in de review. De Git-client werkt vandaag. De agent-kant zit nog in integratie — ik verkoop je
> dus niks.
>
> Wat ik wél zoek: 30 minuten om te horen hoe jullie tot je huidige opzet zijn gekomen. Wat ging er stuk,
> wat hebben jullie afgeschoten, en wat zou je nu nog anders willen. Ik stuur je in ruil wat ik gebouwd
> heb, en dan mag jij zeggen waar het niet klopt.
>
> Schikt een gesprek in de komende twee weken?

### 5.2 The other three openers

- **Quote-back** — for companies that published your argument in their own words. Open by quoting them
  back, attributed precisely (getting the attribution wrong is the whole first impression), then: "you've
  written the thing I'm building — I'd like to hear how you actually handle it today."
- **Greenfield** — regulated, no agent tooling yet. There is no incumbent to dislodge, so the frame is
  education, not competition: "you'll face this as soon as your teams adopt agents, and the only version
  you can adopt is one where the code never leaves the machine."
- **Neighbour** — within an hour's drive. A Kennispark or Zwolle neighbour asking for twenty minutes is a
  different email from a cold vendor. Say where you are in the first line.

### 5.3 Follow-ups, and the four replies you'll actually get

**Two follow-ups, then stop.** The four replies: *"interested, but later"* (ask for a date, not a maybe) ·
*"what does it cost?"* (the deferral in §6.4 — the refusal is more persuasive than any price) · *"we
already use X"* (that's the interview you wanted — ask what X doesn't do) · *silence* (two follow-ups, then
remove them from the list and note why).

### 5.4 The founder-to-champion note (design-partner recruitment)

> I'm building Mainguard in Enschede — a native Git client that runs coding agents in sandboxes and makes
> them pass your test suite before anyone reviews the diff. You're one of the few teams in NL running
> agents seriously. Could I get 30 minutes for a demo in exchange for brutal feedback? Not selling anything
> — looking for a handful of design-partner teams before we charge anyone.

---

## 6. Site & email copy

*Detail: [Email Sequences](business/go-to-market/marketing/EmailSequences.md) (every send in full); [Founding User Program](business/go-to-market/Founding_User_Program_2026-08.md) §4.*

### 6.1 Waitlist page — replacing the current lede

The current line makes a dated promise ("this fall") and an undefined one ("founding-member terms"). Both
need fixing. **Heading:** `Get the beta`

> The Git client is finished and free — no account, ever. The agent control center is still being wired
> together, and I'd rather show you an honest half-built thing than a launch video.
>
> Join and you get every build before it's public. Send back one piece of real feedback and you're a
> **Founding User**: **50% off Pro for its first two years**, at whatever it costs then, locked to you.

**Below the form, in small text:**

> No newsletter. No drip campaign. You'll hear from me when there's a build, and when something changes
> that you'd want to know about. That's it.
>
> Worth knowing before you install: nothing is code-signed yet, so Windows will show a "Windows protected
> your PC" warning the first time. That's on the roadmap, and I'd rather tell you now than have you
> discover it.

*That last paragraph is not a liability. To the audience that converts on verifiable claims, a vendor who
warns you about their own SmartScreen prompt is a vendor who tells you things.*

### 6.2 Success state

> **You're on the list.**
>
> You'll hear from me when there's a build — and nothing else in between.
>
> One thing, if you have thirty seconds: **reply to the confirmation email and tell me what you're running
> today.** Which agents, how many at once, what breaks. That reply is worth more to me than the signup, and
> it's what makes you a Founding User rather than an address on a list.

### 6.3 Email sequences

**The structural constraint, stated first.** The free tier requires no account — so there is no "user
email" to drip onto. Email reaches exactly two self-selected lists: the waitlist and the newsletter opt-in
offered at download. **All sequences are time-keyed, never telemetry-keyed** — we don't know what a
recipient did in the app, and the copy never pretends we do ("if you've already tried X" — never "we
noticed you haven't"). This constraint is the trust posture working as designed; say so when relevant.

**Voice:** subject lines are plain statements, sentence case, no exclamation marks, no rocket emoji. One
idea per email; every email earns its send with something concrete — an essay, a build, a date.
Contractions yes, "please"-padding no. Every email ends with the same footer: *You're getting this because
you joined the Mainguard waitlist. One click to leave: [unsubscribe].* Unsubscribes are honored silently.

**Sequence A — waitlist nurture.** A1 immediately; A2–A4 ride the essay publishes; A5 three days before
launch. **If the calendar slips, the emails slip — never send filler to hold cadence.**

*A1 — Welcome (instant).* Subject: `You're on the Mainguard waitlist — here's what that means`
> You signed up for Mainguard — thanks. Here's the deal, plainly.
>
> Mainguard is a native Git client for Windows — Avalonia + Skia on .NET 10, not an Electron shell. Free,
> no account, nothing leaves your machine. Waitlist cohorts get it first, in small batches, so we can fix
> what breaks before the next batch.
>
> Two honest notes while you wait. First: the multi-agent features you may have read about on the site are
> the roadmap, not the product — we mark the difference everywhere. Second: because the app has no login,
> this list is the only way I can reach you. I'll use it rarely: essays worth your time, your cohort
> invite, and the launch days. That's it.

*A2 — the index.lock essay.* Subject: `Why a crashed tool can corrupt your repo — and the rule that
prevents it`. The short version to close on: *a tool that guards your work doesn't guess on your behalf.*

*A3 — the commit-graph essay.* Subject: `How Mainguard draws a 100,000-commit graph at 60fps`. Close by
asking for the gnarliest repository they have — that's the feedback that improves the router.

*A4 — the trust posture.* Subject: `No login, no telemetry by default — the boring documents that prove
it`. Links to the security architecture doc, the telemetry policy with its published schema, and the
licensing statement. Close: *a Git client sits between you and your code. I don't think it can ask for
trust — it has to be structured so it doesn't need much.*

*A5 — the cohort invite / launch heads-up.*

**Sequence B — launch sends.** One per act, gated on the act actually shipping. The act-two send is gated
twice: the feature must work, and each sentence must be true.

**Sequence C — onboarding drip** (download-time opt-ins, time-keyed).

**The beta welcome email — does not exist yet and is needed.** First line after hello:
> Before anything else: this is unsigned, so Windows will warn you, and the agent side isn't in this build.
> Here's what *is* in it, and here's the one thing I'd love you to try.

### 6.4 The founding-user offer, per surface

**Outreach email, deferring a price question:**
> Nothing, and not for a while. The Git client is free permanently. The agent layer will be paid
> eventually, but it isn't finished and I'm not taking money for something in integration. What I'd rather
> have from you is the feedback — and if you give it, you're a Founding User: half price on Pro for its
> first two years, whatever it costs by then.

**End of a discovery call:**
> Two things I'd ask. Install the client and tell me one thing that annoys you — that alone makes you a
> Founding User, which is half price on Pro for two years once there's a Pro to pay for. And if your team
> ends up wanting to run this against a real repo, I'm taking eight design partners: free for the team for
> a year, and a standing monthly half-hour with me.

**LinkedIn / X, occasionally — not every post:**
> The Git client is free and finished. The agent control center isn't, and I'm not pretending otherwise.
> Early builds go to the list first; anyone who sends back real feedback gets Founding User terms — half
> price on Pro for its first two years. {link}

---

## 7. The target company list

*Detail: [Target Company List](business/go-to-market/sales/Target_Company_List_2026-08.md) — the full entries with every quote, source URL and verification warning.*

**Dated 2026-08-20. Re-verify before every send** — three companies merged or rebranded while this research
ran. **Never assert an `[inferred]` or ⚠️ item to the company; ask about it instead.**

**Scoring:** A agent adoption · B Windows/.NET · C compliance pressure · R reachability · S size fit, each
0–2. S=2 is the 10–100 developer ICP band.

### 7.1 Tier A — open with these

| | Company | Score | Why | Contact / route |
|---|---|---|---|---|
| **A1** | **XPRTZ** (Utrecht) | 9 (A2 B2 C1 R2 S2) | The cleanest verified band fit anywhere: "30+", almost entirely engineers, and — their words — *"Wij hebben **geen sales afdeling**"*, which tells you exactly how to approach them. Technology Radar puts **Cursor in "Adopt"**; their practitioner post names your failure modes unprompted (*"introduceerde bugs in bestaande functionaliteit"*, *"tunnelvisie"*). Client logos include DNB, CIBG, DHD | **Jasper Jak** — jasper@xprtz.nl (published by them). **The hook:** `github.com/xprtz` has an **`advanced-git`** repo — "Deck for the Advanced Git Techniques talk." Someone there gives Git talks. ⚠️ Roelant Dieben is listed as Partner elsewhere but appears nowhere on xprtz.net |
| **A2** | **Blis Digital** (Barendrecht) | 9 (A2 B2 C2 R1 S2) | The deepest agent evidence found. ~50 professionals; their **AI Native Lead Engineer** vacancy requires *"je werkt dagelijks met een agentic harness als Claude Code."* **They have already written your thesis:** *"Wie de code samenvoegt, is er verantwoordelijk voor, ook als een agent hem schreef."* C#/ASP.NET Core/Azure DevOps; ISO 27001 | **Christian Boer** — co-founder, "AI engineer & partner" (**Richard Schot is the CEO** — don't confuse them). ⚠️ **Attribute the quote to the vacancy, not the manifest** — a Blis engineer would catch it instantly. ⚠️ A company this far ahead may have built its own answer: go in wanting to learn what |
| **A3** | **Exsertus** (Kontich, BE) | 9 (A1 B2 C2 R2 S2) | *"Met onze 30 teamleden"*, flat, VISUG partner. The most explicit stack of anyone: C#, .NET 8+, ASP.NET Core, EF Core, gRPC, Azure, **Azure DevOps, CI/CD, Git**. Clients: RIZIV, ZAS hospitals, SD Worx, NIPRO | **Tom Borloo** publishes his own address in the job ad — tom.borloo@exsertus.be. **Lowest-friction first conversation on the list.** Context: part of the **Cronos Groep**, which changes who decides on tooling. Agents are Copilot-grade, not agentic-CLI — treat maturity as an open question |
| **A4** | **Arcady** (Zwolle) | 8 (A1 B2 C1 R2 S2) | **"84% van onze Arcadians is developer"** → roughly 42–50 developers. One hour from Enschede. Microsoft stack; they blog about API-versioning in **.NET 10**. Their MD published *"Iedereen kan straks code maken. Maar wie bepaalt of het de juiste code is?"* — your positioning, in Dutch, written by them | **Sander ten Brinke** — Lead Developer / Microsoft MVP, s.tenbrinke@arcady.nl. **Best reachability on the list:** they host **DevSessions Zwolle** and opened their office for **Hacktoberfest on 8 + 15 Oct 2026**. ⚠️ Lead with the 84% figure; "meer dan 60" appears only in a meta tag |
| **A5** | **Visma Circle** (Eindhoven/Sittard) | 8 (A2 B1 C2 R1 S2) | **The highest purchase intent found anywhere, with a named budget owner.** Exactly **14 engineers**. A live ad: *"de rol van **AI Agent Engineer**"* using *"OpenCode en Claude Code"*, plus the review half — *"het kritisch beoordelen en verifiëren van AI-output"*. The Manager role owns *"de budgetten voor de benodigde engineering tooling"* for all 14. ISO 27001; builds Djuma for municipalities | ⚠️ The vacancy is titled "Product Engineer" — don't call it "your AI Agent Engineer ad." ⚠️ **No literal .NET/C# found** — treat the stack as an assumption to test. ⚠️ Don't cite their team page; it still ships lorem-ipsum placeholder people |
| **A6** | **Peter Cosemans / Euricom** (BE — the site is **euri.com**) | — | **A person, not a company target.** `Euricom/poc-claude-code` carries `CLAUDE.md`, `conductor.json`, and a README section comparing **three git-worktree-per-agent tools side by side**. But: 22 commits, 0 stars, last updated March 2026, and **every commit is by one author.** "Euricom runs agent-per-worktree workflows" is **not supported**; "someone at Euricom has been experimenting" is | Treat as a peer conversation with one engineer who has personally compared the tools in your category. ⚠️ Euricom's headcount and any named engineering leader are unverifiable (JS-rendered SPA) — **a manual LinkedIn pass is required before treating it as a company target** |
| **A7** | **Wigo4it** (Den Haag) | 8 (A0 B2 C2 R2 S1–2) | The strongest compliance narrative, and **genuinely greenfield** — zero incumbent agent tooling, so no rip-and-replace and no vendor to dislodge. Non-profit ICT cooperative of the G4 municipalities building **Socrates**, the bijstand system for 110,000+ citizens. 140 staff; **[inferred]** 60–100 engineers. Published stack: "GIT • C# • AKS • .NET Core • Azure • Blazor • Azure DevOps" | **Jean-Paul van der Ham — Tech Lead**, owns their public monthly **Tech Radar** and is the designated route for technology suggestions. That is an invitation. Squads own compliance directly — if provenance lands anywhere, it lands here first |
| **A8** | **Demcon** (Enschede) | 7 (A0 B2 C2 R2 S1) · **local** | The tightest *stack* match on the list, fifteen minutes away. Multiple live **C#/WPF** roles; GitHub corroborates (`libstored.net`, `ros2_dotnet`). ISO 9001, **ISO 13485** (medical devices), ISO 27001, plus a **defence & security division** — auditable change history is a regulatory obligation, not a preference. They maintain **`cmake-sbom`** (64 stars) — they already spend engineering time on supply-chain traceability | **Skip the company.** The `libstored` and `cmake-sbom` maintainers are named, active, reachable engineers doing exactly your kind of work. Open an issue, or write as a peer. Agents A=0 — expect to be educating, not competing |
| **A9** | **Nedap — Security Management group** (Groenlo) | 7 (A0 B2 C2 R2 S1) | **Target the market group, never the company** — 1,063 people in semi-autonomous groups, and the .NET one is strong (C#/.NET 5+ years, Azure DevOps, Duende IdentityServer, .NET MAUI). Healthcare is Ruby/Vue and embedded is Yocto/Rust — don't pitch those. Extensive certifications | They run a public **"Talk to a Developer"** programme, open to outsiders. ⚠️ **But it is employer branding with a no-recruiters disclaimer** — if you book it, **say why in the booking note.** Turning up under false pretences to a company offering open access is the wrong first impression in a country this small. Their 219-repo GitHub org is a lower-risk route. ⚠️ A circulated claim that their ads mention Claude Code **could not be reproduced** — treat adoption as unknown |
| **A10** | **Sigmax** (Enschede, Kennispark) | 6 (A0 B2 C2 R0 S2) · **local, timing-sensitive** | *"meer dan 120 medewerkers"* on Kennispark. Their .NET ad covers *"migration to Azure and converting Legacy .NET framework projects to .NET 6"* — exactly the high-diff-volume work an agent-assisted Git workflow sells into. A public Compliancy Center: ISO 27001, ISO 9001, ISAE 3000, SOC 2, **BIO BBN 1**, demonstrable **Wpg** (police data) and DigiD. 240–290+ municipalities plus NS | **Timing signal: Sigmax is recruiting a CTO** with an explicit modernisation-and-budget mandate. A vacant CTO seat with that brief is a buying window — **track who fills it and write in their first month.** R=0 (no blog, no GitHub, no named speakers) — but a Kennispark neighbour asking for twenty minutes is a different email |

### 7.2 Tier B — the second batch

| | Company | Score | Why | Route |
|---|---|---|---|---|
| B1 | **Akkuro** (Deventer, "by Topicus") | 7 | The most on-message open role found anywhere: *"AI-Native Software Engineer"* requiring *"practical standards for verification, traceability and responsible AI-assisted development"* — in regulated finance | Apply the role's own language back at them; 45 min from Enschede |
| B2 | **4DotNet** (Meppel/Nieuwegein) | 7 | Sells *"Coding agent adoptie traject"* and an *"AI development governance framework"* as productised services. Two Microsoft MVPs. **A design partner *and* a distribution channel** | **Edgar Tichelaar**, CEO/owner. Techorama NL Platinum |
| B3 | **Cloud Republic** (Utrecht) | ~7 | *"ontwerp agent-rollen alsof het echte teamleden zijn"*, *"behandel instructies als code. Version ze, review ze"*, and they sell a check built on *"waar ontbreekt logging, ownership of change-traceability?"* — your vocabulary, verbatim | ⚠️ Part of de Caesar Groep; standalone headcount not found |
| B4 | **Blinqx / Scan Sys** (Barendrecht) | 6 | *"een hecht team met 45 collega's"* on .NET 8, C#, Angular, Azure DevOps, with a **"Senior .NET Developer (AI first)"** role open | Cold; via the job ad |
| B5 | **Innvolve** (Vught) | 6 | *"Ben jij een .NET-developer die AI-tools en agents net zo vanzelfsprekend inzet als je IDE?"* Built their own agent, gated by an AVG/AI-Act review. ISO 27001; Radboudumc, CZ, VECOZO, CBR | Cold. **[inferred]** dev group under 20 — weak band fit |
| B6 | **Intercept** (Zwolle) | 7 | Deepest compliance set found (ISO 27001:2022, ISO 9001, SOC 2 Type 1, CSA STAR L1, all downloadable). One of three Benelux firms with both Azure Expert MSP and **Agentic DevOps** accreditations | **Gregor Suttie** — Azure Architect & MVP, publishes on agentic workflows |
| B7 | **Yellowtail Conclusion** (Naarden) | 5 | Financial software for Rabobank, Aegon, ABN AMRO, APG. **Back-end team split across NL, South Africa and India** — which makes provenance a live operational problem, not a hypothetical | Hiring a CISO — write once seated |
| B8 | **Ecare / PUUR.** (Enschede) | ~6 | Local. Azure + SQL Server, NEN 7510, ISO 27001, ISAE 3402 II | **Dennis Snippert** — directeur product & technologie. Visma-owned |
| B9 | **xxllnc** (Hengelo) | ~6 | Local. 380 group-wide, but the Veenendaal Belastingen team is *"een klein, specialistisch development team"* on **C# (.NET 10)** | Cold; target the team, not the group |
| B10 | **Qurentis** (Valkenswaard) | ~6 | Holds an **ISAE 3402 Type 2 scoped specifically to "het kritische proces softwareontwikkeling"** — the best "compliance constrains the development process" artefact found. Worth a call purely to learn how they think about it | Cold |
| B11 | **JCC Software** (Oldenzaal) | ~5 | Only ~9 engineers — below band — but local, and **CTO Robin van Ommeren** is named with a phone number. A cheap, friendly first practice call | Direct |

### 7.3 Lighthouse & channel accounts — not band-fit, high leverage

- **Mews (~1,500) — the best individual contact anywhere in this document.** Three engineers published in
  2026 about running Claude Code on a **PCI-scoped financial backend** and a **20,000-file C# monolith**:
  *"we let it write almost 100% of the code, while human review and oversight remained critical"* and — the
  line to open with — *"On other occasions, **Claude Code straight up ignored rules clearly defined in
  CLAUDE.md**."* **Fredrik Claesson** built and open-sourced **ContextKing**, a semantic navigation layer
  for agents in large C# repos measuring 10.5× token reduction. He has built a tool in your adjacent space,
  in your language, on your problem. ⚠️ Whether Mews developers are on Windows or Mac is unverified and
  load-bearing.
- **Info Support (500+) — the channel play.** They sell **Claude Code training by name** (€895/day) and
  ship `promptyard` ("installs agents, prompts, and skills from Git repositories"). **They sell training,
  not a competing platform** — a channel rather than a rival. **Willem Meints — Chief AI Architect,
  Microsoft AI MVP**, promptyard co-author: the highest-value single introduction available. **The event:**
  **Bitbash**, free, Veenendaal HQ, 22–23 Jan 2027 — submit a talk.
- **Topicus (1,600) — team-level entry, and the objection stated in advance.** Nearly all developers use AI;
  they hire "AI-first Fullstack Developer" roles and run an **organisation-approved tooling allowlist** — a
  procurement gate you must clear. Their AI-lead **Egbert van der Veen** is on record: *"We werken met
  modellen die binnen Europa worden ontwikkeld en gehost. **Data mag niet naar Big Tech**."* That is
  simultaneously the biggest objection you will face and the sharpest wedge you have. He is the entry point.
- **ilionx (~1,500) — the governance mirror.** Established an **AI-Office** under **Arno Titawano**,
  building a central review point for all AI applications, targeting **ISO/IEC 42001**, explicitly to
  prevent *"shadow AI."* Competitor-adjacent, so pitch nothing — but this is the single best free education
  available on how a large Dutch buyer will evaluate you in 2027.

### 7.4 Deprioritised, with reasons (so nobody re-adds them)

| Company | Why not |
|---|---|
| **Thales Nederland** | Java/Scala/C++ on **GitLab**, group-standardised. **Zero .NET.** Clearance-gated. Long-cycle at best |
| **Betabit** | ⚠️ **Competitive** — the Fenêtre merger launched **SiliconCode**, an AI code-generation platform. Keep for market intelligence |
| **Yuma (ex-Luminis)** | ⚠️ **Competitive** — sells AI-assisted-development coaching and ships its own agent, **Akgents**. JVM/AWS-first, zero .NET. But Jettro Coenradie publishes well — engage online, don't sell |
| **Q42** | ~104 people and a real agent story in the open — but they publish `macos-image-templates`, i.e. **[inferred]** the dev machines are Macs. Wrong OS. Great people to engage online |
| **De Voorhoede** | 19 people, pure JS/Vue, no C#, no .NET, no Windows tooling. Their lead front-end dev wrote the best public Claude Code statement found in NL — comment on it, don't pitch. ⚠️ Their ISO 27001 is inferred — do not assert it |
| **Adyen, Mollie, bol, Coolblue, Picnic, Weaviate, Channable, Framer** | Disqualified on size. ⚠️ The **bol .NET-heritage premise did not survive verification** — they are GCP/Java/Kotlin/Go now. Coolblue is genuine C# but on AWS |
| **Cloudwise, SciSports, LioniX, Decos, Aikido, Cormel, Impulse, Elmos, Shift2** | Wrong company type, below band, or developers offshore |

**Picnic is not a target — Picnic is a quote.** CTO Daniel Gebler and Gianluca Bandini have publicly framed
your exact thesis: *"governance is embedded in the workflow, not imposed as a months-long gate"*, and *"Peer
review, automated testing, staged rollouts, and access controls remain non-negotiable."* **Cite it.** A
Dutch CTO saying it in 2026 is worth more in a Dutch sales conversation than anything you can say about
yourself.

### 7.5 Open gaps worth a manual pass, in priority order

Euricom headcount + a named person (unlocks a top-three target) · Cloud Republic standalone headcount ·
Yellowtail headcount · whether Visma Circle is actually .NET · whether Mews developers are on Windows or
Mac.

---

## 8. Grant application drafts

*Detail: [Grant Applications NL](business/go-to-market/Mainguard_Grant_Applications_NL.md) (all three WBSO projects, the VFF plan, MIT, and the advisor notes).*

**Ready for a subsidy advisor to hold the pen.** Scheme parameters are the 2026 figures —
**re-verify against the current regeling before filing.** Final submissions may need to be in Dutch; these
are English masters. Free advisory access via Novel-T's partner subsidy advisors.

**Three honesty rules.** Technical-novelty claims follow the WBSO standard — *technically new to the
applicant, with named technical bottlenecks* — and every bottleneck below is a real, specified engineering
problem with a task ID. Nothing is dressed up. Product-state honesty is unchanged: the Git client is
shipped, the agent platform is in development — **and a subsidy application is precisely the place where
"in development" is the correct and advantageous tense, because WBSO funds R&D, not shipped software.**

**Prerequisites before any filing:** Holding BV → Werk-BV incorporated with IP assigned · **eHerkenning
eH3** (or DigiD while zzp) · **an hours ledger from day one** (RVO audits; realized hours due by 31 March
of the following year) · registration with Novel-T · **timing discipline** — WBSO must be filed *before the
month in which R&D starts*, and MIT Overijssel opens on one day in April and is oversubscribed with a
notarial lottery, so the application must be ready on day one.

### 8.1 WBSO — three project descriptions

Keep them as **separate projects** for clean hour attribution.

**Project 1 — "Deterministic multi-process Git coordination engine."**
> **Objective.** Develop a coordination engine allowing multiple autonomous processes (interactive coding
> agents and a developer's own tooling) to operate concurrently against a single Git repository without
> index corruption, lock leakage, or silent loss of work.
>
> **Technical bottlenecks.** (1) *Cross-process handle determinism* — native libgit2 handles held by a
> crashing process leave `.git/index.lock` artifacts that block every other process; no available library
> guarantees deterministic acquisition/release across N concurrent writers, so we must design a
> single-owner handle discipline and extend it to a daemon serving multiple clients. (2) *Merge
> linearization under concurrency* — when several agent branches are verified in parallel, merges must
> serialize through one linearization point while verification results stay bound to the exact base commit;
> designing a lock+CAS scheme whose records stay consistent under rebase is an open engineering problem for
> us. (3) *Stale-verification invalidation* — after any merge, previously verified branches are verified
> against a base that no longer exists; efficient detection and automatic re-queue without O(N²)
> re-verification storms requires new scheduling logic, and no available merge-queue product re-runs
> verification on post-rebase state.
>
> **Planned S&O work.** The daemon's repository-access layer, the verification-record store, the staleness
> detector, the re-queue scheduler, and test harnesses simulating concurrent-writer crash scenarios.

**Project 2 — "Hardened execution sandbox for autonomous coding agents on Windows/WSL2."**
> **Objective.** Develop an execution environment in which untrusted, AI-generated code runs on a
> developer's Windows machine with (a) no network path except an allowlist, (b) no credential or route to
> the user's real Git remote, and (c) survivable failure semantics.
>
> **Technical bottlenecks.** (1) *Default-deny egress on WSL2* — existing container tooling provides no
> off-the-shelf default-deny egress proxy with per-agent allowlists; we must build the proxy configuration
> layer and policy enforcement. (2) *Structural remote quarantine* — preventing a prompt-injected agent
> from pushing to a real remote **by construction** (a worktree whose only configured `origin` is a
> daemon-owned bare mirror, with no real credential present in the sandbox) rather than by firewall rule,
> and keeping that invariant across provisioning, sync and cleanup. (3) *Windows↔Linux boundary
> performance* — avoiding 9P filesystem latency for repository I/O while the UI stays native Windows
> requires a client–daemon protocol whose every timeout derives from a measured round-trip budget rather
> than hardcoded local assumptions.
>
> **Planned S&O work.** The sandbox provisioning pipeline, egress proxy and policy engine, quarantine-remote
> provisioner, crash/reattach semantics, and latency-budget instrumentation.

**Project 3 — "Tamper-evident attribution and audit chain for AI-generated code changes."**
> **Objective.** Develop an append-only, hash-chained audit structure recording, per code change: the
> generating agent/model/session, the approving human identity, the verification verdict and its base
> commit, and the merge decision — verifiable offline by a third party.
>
> **Technical bottlenecks.** (1) *Per-hunk attribution* — mapping agent output through rebases, squashes and
> partial staging down to hunk granularity, and rendering it in blame/diff structures, has no existing
> implementation; the emerging Agent Trace standard specifies emission only, and no consumer exists.
> (2) *Chain integrity under a local-first model* — hash-chaining and externally anchoring an audit log
> that lives on the user's machine rather than a trusted server, such that tampering is detectable without
> a cloud dependency. (3) *Identity binding without a server* — binding OS-level user identity to approval
> events in a way that survives export to enterprise SIEM systems.
>
> **Planned S&O work.** The chain format and verifier, the provenance mapper across Git operations,
> blame/diff-gutter rendering, and SIEM export.

*Advisor note: these bottlenecks deliberately map to real specified tasks — if an RVO reviewer probes, the
phase-2 specs are the evidence.*

### 8.2 VFF — the early-phase plan

**Trigger:** an angel/VC intent letter. **Apply via Oost NL, not RVO.**

> **Company.** Mainguard B.V. (Enschede) — a native Git client for Windows (shipped; 1,042 automated tests)
> evolving into a verification and merge-governance layer for AI-coding-agent output (in development).
>
> **The proposition to validate.** Development teams adopting AI coding agents face a verification
> bottleneck: AI-assisted teams merge ~2× more pull requests while review time has risen 91%, and 87% of
> developers report distrust of agent output. We build the layer that makes agent output safe to merge:
> sandboxed execution, deterministic test-verification before human review, a merge queue that re-verifies
> stale branches, per-line attribution, and an audit trail. No shipped product combines these capabilities
> (verified capability-gap analysis, July 2026).
>
> **Early-phase milestones** (= the plan the loan finances, ≤ 2 years): the validation milestones are the
> launch plan's own gates — client GA, the verification pipeline in design partners' hands, paid pilots
> converted, and the governance tier.

### 8.3 MIT Haalbaarheidsproject — April 2027

35% of feasibility-study costs, max €20,000. **Ready-on-day-one**; set the reminder for February 2027.
Proposed subject: *"feasibility of cloud-hosted verification worktrees for EU enterprises."*

---

## 9. Launch-day copy

*Detail: [Social Launch Reserve](business/go-to-market/marketing/SocialLaunchReserve.md) (both Show HN bodies, the 12-reply FAQ, thread series); [Narrative.md](creative/Narrative.md) §5 is copy of record.*

The launch runs in two acts: **act one** is the free Git client, **act two** is the agent control plane
four to eight weeks later ([`GTM.md`](GTM.md) §2). **Both Show HN bodies are held in reserve** until
their act's gate opens — Show HN is one shot, and spending it early converts the best distribution event
available into a mediocre one.

### 9.1 Show HN — act one

**Title:** `Show HN: Mainguard – a fast, native Git GUI for Windows (free, no login)`

> Mainguard is a Git client I've been building for about a year. It's a native desktop app — Avalonia +
> Skia on .NET 10, LibGit2Sharp underneath — not an Electron shell around a web view. It's free, there's no
> account, and nothing leaves your machine. ††(Today it's a build-from-source dev preview.)
>
> What it does:
>
> - A commit graph that stays smooth on large histories — a virtualized, vector-drawn DAG lane router
>   rendered directly at 60fps, not a chart library.
> - Staging down to the line. Stage, unstage, or discard by hunk; drag-select individual lines in the
>   unified view; accept or reject blocks side-by-side. The patch engine is validated against `git apply`,
>   so what you stage is exactly what Git stages.
> - A synchronized 3-pane conflict resolver (Ours | Result | Theirs) with per-side accept/reject/undo.
>   Merge, rebase, cherry-pick and pull all route conflicts through it.
> - An operation-history journal so ref moves are undoable, and a reflog viewer for the ones that aren't.
>   Force-push is `--force-with-lease`, never a bare `--force`.
> - Branch, tag and worktree porcelain; interactive rebase; five switchable themes on one design system.
>
> Why it exists: I got tired of `.git/index.lock` roulette — two tools touch the index, one exits early,
> and the next operation fails with a message that blames nothing and suggests nothing. Mainguard's one
> non-negotiable architectural rule is that every repository handle opens and closes through a single
> deterministic path, so the app itself can never leave that lock behind. When it finds a stale lock some
> other process left, it says so plainly and tells you how to check whether it's safe to remove — it won't
> silently delete a file another process might hold.
>
> Where it's going, stated honestly: the roadmap is a control plane for coding agents — a merge queue that
> re-verifies branches that go stale when main moves, risk-ranked review with per-hunk provenance, hardened
> local sandboxes. None of that is built. Today it's a fast, precise Git client for one developer, and I'd
> rather you hold me to the roadmap than believe it already exists.
>
> Feedback I'd most value: does the graph stay smooth on your gnarliest repo, and does line-level staging
> behave exactly like `git apply` for you?

†† Substitute the packaging sentence once packaged builds ship.

**Mechanics:** Tue–Thu, 9am–12pm ET. Direct download, no signup wall. Founder in the thread within the hour
and available all day. Same-week satellites fire per [`GTM.md`](GTM.md) §2.2.

### 9.2 The first-hour FAQ — pre-drafted replies

*Register: concede what's true, state the fact, point at the object, never bristle. Adapt to the actual
comment; never paste an answer to a question that wasn't asked.*

**"Why another Git client? This market is done."** — Fair; the market is mature and Fork set a high bar.
Near-term: headroom on Windows specifically — native rendering, line-level staging validated against `git
apply`, an undo journal for ref moves, a free tier with no account. Long-term: the client is the
foundation, not the pitch, and the verification layer is only buildable on a real Git engine. **The client
has to be excellent anyway, because you'll live in it.**

**"Why .NET/Avalonia and not Electron or Tauri?"** — Deterministic native rendering and real native
controls. The graph is a virtualized vector canvas; each visible row is a tiny control drawing only its own
lines, so scrolling a huge history costs what scrolling a virtualized list costs. Tauri solves the memory
half but you're still in a web view — I wanted the graph *drawn*, not laid out.

**"Why FSL and not MIT? 'Source-available' is not open source."** — You're right that it isn't, and I won't
pretend otherwise. The daemon is source-available so the security boundary is auditable — that's the part
where trust matters most. Why not MIT for everything: this category buried two free-and-thin orchestration
tools in 2026 alone; FSL keeps the code inspectable and the company alive to maintain it. **The free client
stays free with no account, forever — that part isn't a trial.**

**"GitKraken / Fork / Sublime Merge already exist."** — They do, and they're good — Fork especially, and
it's native too, so I won't claim that edge against it. Differences today: line-level staging validated
against `git apply`, the operation journal, no account wall, native rendering vs Electron. Difference in
direction: none of them is building verification and merge governance for agent output. **And to be equally
honest, my half of that comparison is unbuilt today.**

**"Won't GitHub/Anthropic/OpenAI/Cursor just ship this?"** — They're shipping the generation side,
single-vendor each. The structural bet: none is incentivized to make its GUI a better home for a rival's
agents, and teams already mix vendors per task. The one I watch most closely is Cursor's Origin. It's a
cloud forge; I'm local-first. **If Origin ships local execution plus provenance, that's my tripwire and
I've said so publicly.**

**"What do you collect? What phones home?"** — Nothing without opt-in. No login, no account, telemetry
opt-in with a published schema, keys in the OS keyring, and the sandbox roadmap is default-deny egress. The
security architecture doc is linked from the repo. **The no-login free tier isn't generosity — a Git client
sits between you and your code; I think it has to earn trust structurally, not ask for it.**

**"Is the agent stuff vaporware?"** — It's a roadmap, and the post labels it as one. **I'd rather you hold
me to shipping it than believe it exists.** What's real today is the client, exercised by 1,042 tests. The
merge-queue design is published in the same spirit: written down in advance so it can be checked against
what ships.

**"Does it run on Linux/macOS?"** — Avalonia is cross-platform and it builds and runs there, but I'm
Windows-first deliberately — the largest developer OS and the least served by the current wave, which
shipped Mac-first. Linux/macOS polish comes after Windows earns it. **If you hit something broken on your
platform, I want the issue anyway.**

**"Binaries? I'm not building from source."** — Completely fair. *(Swap the answer to match the actual
distribution state on the day.)*

**"I use lazygit/magit/the CLI. Why would I switch?"** — You might not, and I won't pretend a GUI beats a
workflow already in your fingers. Where a GUI earns its place for me: drag-selecting individual lines
across a big refactor, a 3-pane resolver with per-side undo, and *seeing* a tangled DAG instead of
remembering it. **If the CLI serves you, the CLI serves you.**

**"60fps is a marketing number. Prove it."** — The claim is specific: a virtualized vector canvas with
per-row controls and chunked lane routing, so cost tracks the viewport, not the repository. The router essay
explains the design with type names and the benchmark methodology is in the repo. **What I actually want is
falsification: if you have a repository that makes it stutter, that's the most useful bug report I'll get
this week.**

**"What's the business model? How do you not die like the free orchestrators?"** — The client is free
forever, no login — that's the funnel, and it has standalone value, **which is exactly what the dead
free-orchestration tools lacked.** Revenue is the verification pipeline: Pro at $20/mo (BYOK — your keys,
no inference markup), teams later at $50+/seat for governance — **and that tier doesn't get sold before
those features exist.** I wrote up the failure ledger of this category; the pricing is designed against it.

### 9.3 Show HN — act two

**Title direction:** `Show HN: Run coding agents in sandboxes that must pass your tests before you review
their code`. **Gated: ships only when true.** The body follows the act-one structure — what it does, why it
exists, what is honestly not built — with the pipeline as the spine and "validated-then-stale is
unvalidated" as the named failure mode.

### 9.4 Thread series & long-forms

Per act, adapted per channel: **X/Bluesky** (the mechanism thread — one claim per post, each pointing at an
object), **LinkedIn** (the long-form version for the Dutch B2B audience; the governance framing leads),
**the engineering thread** (the 60fps router, purely technical, zero pitch — the .NET community carries it),
and **the founder story** (also the About-page copy).

---

## 10. Press kit & video

*Detail: [Press Kit](business/go-to-market/marketing/PressKit.md); [Video Scripts](business/go-to-market/marketing/VideoScripts.md).*

**Press one-pager — two variants.** Variant 1 (act one): the client, the trust posture, the local-founder
angle. Variant 2 (act two addendum): the verification pipeline. **A fact sheet for fact-checkers, kept
current.** Angles by outlet type, including the Dutch list ([`GTM.md`](GTM.md) §4.4). Pre-cleared quotable
lines used verbatim. **Asset rules:** real captures only, and anything prototype carries a persistent
`Design prototype — mock data` label.

**The press FAQ — the five questions every journalist asks:** what is it · who is it for · how is it
different · what does it cost · who is behind it and where are they.

**Video scripts.** (1) **The 90-second explainer** — the launch video, and the source clip for creator
outreach. (2) **The founder-story film** (~2:30) — "Why I'm building Mainguard." (3) **The act-two
live-demo storyboard** (~4:30), narrated in the product's own state words, with the persistent
prototype label and a 30-second external-PR-intake wedge insert.

---

## 11. Content backlog

*Detail: [Manifesto](business/go-to-market/marketing/Manifesto.md); [Content Calendar](business/go-to-market/marketing/ContentCalendar.md) (the drafted pieces in full).*

**The manifesto — "Ungoverned AI merges are a time bomb."** The standalone thesis essay: the blind-merge
enemy framing as prose, five tool-agnostic defusing properties, a self-discounting disclosure, and a full
source ledger. **Currently the anchor piece** — it's finished, it's pure argument, and it needs no demo.

**Drafted and ready:**
- Honest comparison pages vs GitKraken / Fork / Conductor — each opening with the concession and closing
  with the positioning sentence (the Conductor page gated present-tense-only).
- *"A 60fps commit graph without a chart library"* — the shipped-code engineering essay, with real type
  names.
- *"The merge queue that re-verifies"* — the design essay, in **[Horizon]** tense throughout.

**Outlined, not written:** the `.git/index.lock` essay (also email A2) · the WSL2 sandbox essay ·
AI-review fatigue · the pricing-logic essay.

**Release notes** follow the Voice Bible's release-notes register: what changed, what it fixes, what it
breaks — no adjectives.
