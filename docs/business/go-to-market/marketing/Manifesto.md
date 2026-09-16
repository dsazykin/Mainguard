# Manifesto — *Ungoverned AI merges are a time bomb*

**Lane G Part 4 · Register: brand (founder byline) · Publishes L2 − 3–5 days** (the thesis lands first; the product answers it — [LaunchCampaignPlan §4](LaunchCampaignPlan.md)). The enemy throughout is a practice — the blind merge (Narrative §3.1) — never a company, a product, or the agents themselves. Every figure carries its source; severity rides the facts, not the punctuation. The essay mentions Mainguard once, in a disclosure paragraph, because a manifesto that is secretly an ad persuades no one.

---

## The essay

> # Ungoverned AI merges are a time bomb
>
> A time bomb has one defining property: the damage happens later than the mistake. That is what makes it dangerous — at the moment of the mistake, everything looks fine.
>
> Merging code you didn't verify has exactly this property. The merge itself is painless. The build stays green. The standup stays short. The cost arrives weeks later, detonated by something unrelated: a refactor that touches the module nobody really read, an incident review that needs to know why a query changed, an auditor who asks a question your Git history cannot answer. Software teams have always carried some of this debt. What changed in the last two years is the rate at which we're wiring new charges.
>
> ## The arithmetic has no missing step
>
> Consider what is now simultaneously true. Coding agents made branches cheap: teams using AI assistance merge roughly twice as many pull requests, and a single vendor's coding agent opened over a million PRs in its first five months. Review did not get cheap: PR review time is up 91 percent, against a review capacity that is fixed — there are no more senior-engineer hours this quarter than last. And trust collapsed on schedule: 87 percent of developers say they're concerned about agent accuracy; under high AI adoption, code churn — work that gets rewritten or reverted shortly after landing — is up 861 percent, while delivery-stability metrics degrade.
>
> Cheap branches. Fixed review. Falling trust. There is no way to hold those three lines together without something giving, and what gives, in practice, is diligence. Not because engineers got lazy — because the arithmetic left them no other variable to cut. Only 31 percent of developers run agents today. This is what the *early* innings look like.
>
> ## Name the practice
>
> The failure isn't the agents. Agents are why any of this is worth doing; a tool that generates a working branch in minutes is genuinely miraculous. The failure is a practice old enough to predate them, now scaled beyond what it was ever safe for. Call it what it is:
>
> **The blind merge**: code nobody fully read, verified against a main that has since moved, written by a process nobody can attribute, merged because the diff was too long and the queue was too deep.
>
> Every clause of that sentence is a separate mechanism, and each one deserves to be seen clearly.
>
> *The diff was too long to really read.* Review attention is spent file-by-file, top-to-bottom, alphabetically — as if a config change and a rewritten auth check deserved equal minutes. At two times the PR volume, attention allocated evenly is attention allocated wrongly.
>
> *"It passed tests" — an hour ago, against an older main.* This one is subtle enough that almost every tool on the market gets it wrong. A branch is verified, then a different branch merges, and main moves. The verified branch was verified against a world that no longer exists. Nothing conflicts textually, so it merges — on evidence from the old world. A branch validated an hour ago, against an older main, is not validated. **Validated-then-stale is unvalidated**, and today's pipelines re-run CI without ever re-asking the question.
>
> *Nobody can say which process wrote which line.* When a human writes code, `git blame` is an answer. When four agents and two humans share a repository, "who wrote this and under what instruction" becomes unanswerable precisely when it becomes important — in the incident review, in the audit, in the moment you're deciding how much to trust the next diff from the same source.
>
> *The process could have touched anything while it worked.* An agent that can write code can also fetch a dependency, edit a build script, or read a file it had no business reading. Without containment, reviewing the diff reviews only what the process chose to show you.
>
> *And no record survives of who approved what.* "Trust me" is not an audit trail. Increasingly, procurement and compliance teams are saying so out loud.
>
> ## Why the current answers don't defuse anything
>
> **"We have CI."** CI is necessary and downstream. It re-runs on the branch as submitted; it does not re-verify sibling branches when main moves, and it says nothing about attribution, containment, or approval. Every merge queue on the market re-runs CI. None re-runs *verification* on the post-rebase state of the branches behind it.
>
> **"We added AI review."** An opinion about code is not evidence about code. In one audit, roughly 35 percent of an AI reviewer's comments were genuinely useful — which means teams are training themselves to skim past the other 65, and skimming is the exact habit that produced the blind merge. A deterministic verdict — *your test suite passed, in isolation, against the main it will actually merge into* — is a fact. Review fatigue is what happens when you gate facts behind opinions.
>
> **"The agents will get good enough."** Generation quality is improving and beside the point. A perfect agent still can't answer, from inside a merged blob of history, *who wrote this, was it verified, against what state, and who approved it*. Those are properties of the pipeline, not the author. Meta — operating at the far end of agent adoption — built an internal system to risk-review agent code and catch what gets reverted, because nothing purchasable did it. Its existence is the tell: the teams furthest ahead concluded that trust must be manufactured *between* generation and merge, and built the machinery privately.
>
> ## What defusing actually requires
>
> None of this needs a manifesto's worth of novelty. It needs five properties, stated plainly — and they are properties any team can demand of any tooling, including tooling they build themselves:
>
> 1. **Verification is deterministic and local to the gate.** The branch runs the project's own test suite, in isolation, and the verdict is recorded — a fact, not an opinion.
> 2. **Verification is fresh.** When main moves, every previously verified branch is stale until re-verified. Automatically. No exceptions that don't leave a record.
> 3. **Work is attributable.** Which process, under which instruction, produced which lines — answerable at review time and answerable a year later.
> 4. **Execution is contained.** The process that writes the code cannot silently reach anything beyond its task — and its access is part of the record.
> 5. **The record is tamper-evident.** Who approved what, what was verified against what, what merged when — kept in a form that would read the same in an audit as it did on the day.
>
> Teams that adopt these five properties can run agents aggressively *because* the gate is honest — velocity and governance stop being a trade-off the moment verification is cheap and automatic. Teams that adopt none of them are accumulating charges wired to timers of unknown length. The 861 percent churn figure is what the short-timer detonations already look like; the long-timer ones write incident reviews.
>
> ## Disclosure, and the point
>
> I'm building a product in this space — Mainguard, a Git client whose roadmap is exactly the five properties above, and I've marked plainly elsewhere what is shipped and what isn't. Discount my incentives accordingly; the argument doesn't need my product to be true. Build the five properties into your own pipeline with your own tools and this essay has done its job.
>
> Because the practice, not any vendor, is the enemy. Every one of us has committed a blind merge — the diff was long, the sprint was ending, the tests were green an hour ago, and we hoped. At human speed, hope was a survivable vice. At agent speed, it's a wiring diagram.
>
> **Hope is not a merge strategy.**

---

## Source ledger (kept ready for the comment thread)

| Claim | Figure | Source (via) |
|---|---|---|
| Review-time explosion; ~2× PRs merged | +91% | Viability §1.3 |
| Agent-accuracy concern | 87% (81% security) | Stack Overflow 2025, via GTM Plan §4 |
| Copilot coding-agent PR volume | 1M+ in 5 months | Octoverse 2025, via GTM Plan §4 |
| Code churn under high AI adoption; stability degrading | +861%; DORA 2025 | Faros AI / Octoverse; DORA 2025, via GTM Plan §4 |
| Agent adoption still early | 31% run agents | Stack Overflow 2025, via GTM Plan §4 |
| AI-review comment usefulness | ~35% genuinely useful | audit cited in GTM Plan §5.3 |
| Meta's internal agent-code risk review; revert catch rate | RADAR; 1/3 | GTM Plan §5.1 |
| Queues re-run CI, never re-verify | verified empty | GTM Plan §5.2 probe (a); Narrative §2.4 |

## Self-gate (Part 4)

- Enemy framing: the blind merge (a practice) throughout; agents explicitly defended ("the failure isn't the agents"); no company named except Meta as *positive* proof and Mainguard in a self-discounting disclosure; the five properties are stated tool-agnostically so the essay stands without the product.
- Honesty contract: no EU-deadline scare (compliance appears only as "procurement teams are asking"); no [Horizon] capability claimed as shipped (the disclosure defers to the marked-tense material); the "time bomb" frame is cashed out as a factual property (deferred cost) in the first paragraph, not left as theatrics.
- Voice: zero exclamation marks; severity carried by figures with a full source ledger; the named lines used verbatim ("Validated-then-stale is unvalidated," "Hope is not a merge strategy," "a fact, not an opinion").
