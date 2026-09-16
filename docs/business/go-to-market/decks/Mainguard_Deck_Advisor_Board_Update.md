# Mainguard — Advisor / Board-Update Deck

**Date:** 2026-07-11 · **Register:** brand, working-session tone (Voice Bible rules; this is the least "pitch" deck in the set)
**Audience & occasions:** (a) the August advisor working session (Master Market Document Part XVI — advice-not-money; the structure below enforces its 70/30 listening ratio), and (b) the recurring advisor/board update thereafter — the report-back cadence that Part XVI calls "the whole game." The same skeleton serves both; §B marks what changes per occasion.
**Design intent:** a working document, not a performance. Six slides for a 45–60 minute session; the advisor should talk more than the presenter from slide 4 onward. No hero metrics, no adjectives — the numbers are small and honest, and that is the point.
**Honesty contract (binding, Narrative §0):** built vs planned is marked on every status line; overselling product state to an experienced founder is the #1 credibility failure this deck exists to avoid (Part XVI §16.7).

---

## Slide 1 — Where Mainguard stands (one screen, no scrolling)

**On the slide:**

> **Mainguard makes AI-agent code safe to merge.**
>
> | | Status |
> |---|---|
> | Native Git client (graph, line-level staging, 3-pane resolver, undo journal) | **Shipped** — T-01…T-33, 1,042 tests |
> | Agent platform (sandboxes, verification, merge queue, provenance, audit) | **In development** — specified P2-01…P2-26, branch `phase2` |
> | Launch | Act one (free client) ~October · act two (verification) +4–8 weeks |
> | Revenue | **Pre-revenue by design** until design partners validate |
> | Team | 1 → 5–6 forming · Enschede, NL |

**Speaker notes.**
One minute, maximum. The table *is* the honesty posture: two rows, two tenses, no blur. An advisor or board member should be able to reconstruct the company's true state from this slide alone a month later. Update every occurrence of this table from the same source (the Master Market Document Part I "Where we stand") so no two decks disagree.

For the August first session: this replaces any "vision" opening — he received the one-pager pre-read (Part XV §15.3) days ago; re-explaining basics wastes the hour.

---

## Slide 2 — Since last time *(recurring updates)* / How we got here *(first session)*

**On the slide (recurring form):**

> **Did / result / changed my mind:**
>
> - You said `[the advice]` → I did `[the action]` → `[what happened, with a number]`
> - Shipped: `[the 2–3 things that actually landed]`
> - Killed or deferred: `[the thing we stopped doing, and why]`

**Speaker notes.**
This slide is the report-back mechanism — ask → act → report is the loop that converts an advisor into an advocate (Part XVI §16.2), and the "killed" line earns more trust than the "shipped" line: it shows the advice is metabolized, not collected. Keep it to three bullets; a board update that lists twelve accomplishments is hiding the one that matters.

First-session form: compress the origin instead — the index.lock footgun, the year building the client, the pivot of emphasis from "run agents" to "verify what agents produced" when the worktree mechanic commoditized (Viability §2). Two minutes, told as engineering decisions rather than biography.

---

## Slide 3 — The numbers (small, honest, trending)

**On the slide:**

> | Metric | Last | Now | Note |
> |---|---|---|---|
> | Weekly active repos | `[n]` | `[n]` | free-tier health |
> | Agent runs verified / week | `[n]` | `[n]` | the thesis metric *(post act-two)* |
> | % merges against non-stale verification | — | `[n]%` | queue integrity |
> | Design partners (Track B teams) | `[n]` | `[n]` | target 3–5 signed by launch |
> | Discovery interviews done | `[n]` / 25 | `[n]` / 25 | Jul–Sep program |
> | Runway (months, incl. WBSO effect) | `[n]` | `[n]` | |

**Speaker notes.**
Pre-launch, most cells are legitimately zero or blank — show them anyway. A zero with a plan beats a proxy metric dressed up as traction, and this audience pattern-matches inflated dashboards instantly. The two investor-grade metrics (weekly active repos; verified merges/week) are instrumented before any launch precisely so this table has integrity from week one (GTM Plan §9.1).

The tripwires belong to this slide's narration, not a risk appendix: if Track B teams won't pre-commit to paid pilots by two months post-act-two, packaging gets revisited (GTM Plan §12.2); if Cursor Origin ships local execution + provenance, we re-plan within a quarter (§12.1). Naming tripwires unprompted is what separates a board update from a sales update.

---

## Slide 4 — The 2–3 decisions I want your judgment on

**On the slide (August-session example set — replace per occasion):**

> 1. **Sequencing.** Free client in October, verification layer 4–8 weeks later — or lead with the agent story? How did you stage your first launch?
> 2. **First paying teams.** Plan: 3–5 design partners before charging anyone. How did `[his previous company]` land its first paying customers — and what would you change?
> 3. **Bootstrap vs raise.** Funded free competitors on one side, a Dutch subsidy stack on the other. What did raising cost you that you didn't expect?

**Speaker notes.**
This slide is the meeting. Phrase every decision so the advisor's *experience* is the input, not their approval ("how did you…", never "do you like…") — specific asks attract serious engagement; vague ones attract polite nods (Part XVI §16.2). Two or three decisions maximum; write them down before the session and treat them as the agenda.

From here on, the presenter's job is notes: his objections (they preview every investor objection), names he drops (never ask for the intro; let him offer), and — in the August case — anything about how Daemon's own team reviews and merges agent-written code, which is both discovery gold and the natural bridge to slide 6's ask.

---

## Slide 5 — Risks I'm watching (the honest three)

**On the slide:**

> 1. **Platform absorption** — Claude Code Desktop ships worktrees + autoVerify; Cursor Origin lands fall 2026. *Counter: vendor-neutrality + the compound pipeline + a real Git engine; tripwire named.*
> 2. **Monetization ceiling** — orchestration is worth $0 (Bloop, Terragon); we believe verification + governance is worth $20–50. *Counter: design partners prove willingness-to-pay before the enterprise layer is built.*
> 3. **Execution capacity** — one founder, forming team, funded incumbents. *Counter: the Git core — the prerequisite everyone else lacks — is the part that's already done.*

**Speaker notes.**
Three risks, each with its mitigation and none minimized — drawn from Part XX and kept current. For a founder-audience this slide does double duty: it invites the war story ("which of these killed companies you've seen?"), which is exactly the input the session is for. Do not pad the list to look thorough; the long-form register lives in the risk sections of the market docs and can be sent as follow-up reading if asked.

---

## Slide 6 — The ask (exactly one)

**On the slide (August form):**

> **Can I email you in a few weeks with what I did with this advice — and how it went?**
>
> *(If the Daemon overlap surfaced on its own:)* would one or two of your engineers try Mainguard on a real repo for a month? Free — I need brutal feedback from a team that runs agents daily, and they'd shape the roadmap.

**Speaker notes.**
The follow-up-loop ask is always made — it costs nothing, establishes the cadence, and is the actual mechanism by which advisors happen (Part XVI §16.5). The design-partner ask is made **only** if the overlap surfaced naturally in slide 4's discussion; it is concrete, bounded, and useful to him (his team gets a tool plus influence over it). Never stacked with anything else, and explicitly *not* on this slide in any form: the advisor role (raise it on follow-up two or three, via FAST norms — §16.6) and money (never in August; "ask for advice, get money — twice").

Recurring-update form of this slide: one specific, small ask per update — a read on a draft, a reaction to a pricing change, one named intro *if previously offered*. An update with no ask teaches the reader to skim; an update with three teaches them to dread it.

---

## §B — Per-occasion checklist

| | August advisor session | Recurring update (monthly-ish email or call) |
|---|---|---|
| Length | 6 slides, 45–60 min, demo separate (10-min script, Part XV §15.2) | 1-page email in this deck's slide order, or 4 slides |
| Slide 2 | "How we got here" form | "Did / result / changed my mind" form |
| Slide 4 | 2–3 pre-written decision questions | 1 new question, tied to the last report-back |
| Slide 6 | Follow-up loop (+ design partner if natural) | One small ask |
| Never | Pitch at dinner; NDA; unoffered intro requests; money | Metrics theater; three asks; skipped months after bad news |

**The rule the whole deck serves (Part XVI §16.8):** bad-news months are the ones you *don't* skip. An advisor who hears the miss from you, with the tripwire analysis attached, becomes more committed; one who reads it elsewhere becomes polite.

---

## Self-gate (five-question gate, applied)

1. **Point at the object** — every status line carries its task range or test count; every metric names its definition; the asks name a bounded action.
2. **Way back** — each risk pairs with its mitigation and tripwire in the same breath.
3. **Audit-log test** — shipped/in-development split on slide 1 reads identically in a diligence file; no proxy metrics presented as traction.
4. **Delete a word** — the deck contains no adjectives about the product; status words (`Shipped`, `In development`, `Pre-revenue by design`) carry everything.
5. **Severity rides the role** — risks are stated at documented severity, neither inflated for drama nor sanded down for comfort.
