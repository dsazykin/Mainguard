# Mainguard — The Swarm Vision Deck *(the ten-year story, told honestly)*

**Date:** 2026-07-11 · **Register:** brand-vision (Voice Bible rules still bind; a vision deck earns the right to be expansive only by staying exact)
**Audience & occasion:** the second meeting with a lead investor who already believes the wedge; a keynote-length closing section after the seed deck; the "where does this go" conversation with a strategic acquirer or senior hire. **Never the opener** — vision before proof is the register of the dead companies.
**Vocabulary discipline (binding):** the market docs forbid *leading* with "swarm" — it is commoditized, hardware-dishonest as a today-claim, and the vocabulary of the corpse pile (GTM Plan §2.4; Narrative §3.4). This deck may use the word because it is *explicitly a vision deck*: every capacity claim is tensed, the honest local ceiling (**4–6 agents on 16 GB**) is stated on the slide that scales past it, and the scale answer is the cloud tier's economics, not adjectives.
**Honesty contract (binding, Narrative §0):** shipped vs **[Horizon]** marked throughout; illustrative cloud numbers flagged; no figure appears here that does not trace to the corpus.

---

## Slide 1 — The role is changing, not the developer

**On the slide:**

> In 2020 you wrote the code.
> In 2026 agents write ten branches an hour — and you're asked to pretend-review a firehose.
>
> **The next durable role is the engineering manager of machines: deciding what gets built, and what gets merged.**

**Speaker notes.**
Open with the human, not the technology. The role shift is already documented behavior, not prediction: teams run 4–8 concurrent worktrees per developer with nothing but a CLI (Viability §1.1); "agentmaxxing" across vendors is a named manual practice (GTM Plan §5.3). What's missing is the *management layer* that makes the role survivable — today it's tmux duct tape and a heavy manual merge tax (Master Market Document §2.3).

The framing to plant, from PRODUCT.md itself: success for the roadmap is *"a human comfortably supervising a swarm of agents from one screen without losing control of their working directory."* The operative words are **comfortably** and **without losing control** — this deck is about how both scale.

---

## Slide 2 — Act I, shipped: the instrument

**On the slide:**

> You don't get to manage machines with a tool you don't trust.
>
> **Shipped today:** a native Git client — 60fps commit graph, staging to the individual line validated against `git apply`, a 3-pane conflict resolver, an undo journal. 1,042 tests. No login. Nothing leaves your machine.

**Speaker notes.**
Thirty seconds, present tense, then move on — the seed deck carries this in full. Its job here is to timestamp the vision: Act I is *done*, which is what separates this vision deck from every orchestration pitch of 2025. The founder-story beat (Narrative §5.5): anyone can promise a control plane; you earn the right to build one by shipping the instrument underneath it and letting people hold your work to the same standard the tool will hold the agents to.

---

## Slide 3 — Act II, in development: several agents, perfectly governed

**On the slide:**

> *(Roadmap — in development, not shipped.)*
>
> A developer supervising **4–6 agents** on their own laptop:
>
> - Each in a sandboxed worktree whose only push target is a quarantine mirror
> - Plans approved before code is written; tests passed before diffs are seen
> - A merge queue that re-verifies whatever goes stale — *validated-then-stale is unvalidated*
> - Every line attributable: which agent, which model, which approved plan

**Speaker notes.**
The number on the slide is deliberately small. 4–6 agents on a 16 GB laptop is the *defensible* claim (WSL2 takes half the RAM by default; API rate limits bind earlier — Part XX §8), and stating the ceiling is a feature of the pitch: it signals the numbers on the later slides are real too. "A few agents, perfectly managed, beats 50 agents that OOM a laptop" (Master Market Document Part I, takeaway 5).

Everything on this slide is specified at task-ID depth (P2-06/07 sandboxes and quarantine, P2-14 plan approval, P2-10 queue, P2-11 provenance) and in development on `phase2`. The mechanism detail lives in the technical deck; here it is one breath per line.

---

## Slide 4 — The ceiling is a business model, not a wall

**On the slide:**

> The laptop caps the swarm at 4–6. **The cloud removes the cap — on the same binary.**
>
> - Same daemon, new home: per-tenant pods, per-session sandboxes, the same quarantine guarantee (invariant `G-14` — no cloud-only fork)
> - Locally, BYOK means we earn nothing from usage — deliberately. In the cloud, usage finally becomes revenue
> - *Illustrative model (§5, placeholders):* ~$1.43 per typical session · ~$3.11 gross margin per active user-month · break-even ≈ 3,200 active cloud users

**Speaker notes.**
This is the slide where vision meets spreadsheet, and the honesty flags do the persuading. The cost model (Cloud Vibe Companion §5) is arithmetic-shown, assumption-numbered, and its own §5.6 warns that the revenue column is mostly pass-through model spend — *gross-margin dollars are the number the tier runs on*. Say that before anyone asks. The capital is gated, not gambled: spike → walking skeleton → full build → GA, each stage behind a written go/no-go gate, and GA only when beta telemetry matches the model within tolerance (§3).

The strategic point for a vision audience: the scale story requires **zero architectural pivots**. The protocol already runs under injected 80 ms WAN latency in CI (P2-25); every timeout is RTT-relative; the sandbox→real-remote absence holds identically in the pod. The swarm grows by renting more homes for the same binary.

---

## Slide 5 — Act III [Horizon]: the person who never sees a terminal

**On the slide:**

> *(Horizon — specified, not built.)*
>
> The Vibe founder: describes what they want, watches a live preview, ships.
>
> - An agent does the Git work; auto-checkpoints guard every step; escalations arrive in plain language with three honest options
> - Cloud-first by necessity: this person has no terminal, no WSL2, no API console — the local install cannot win time-to-first-magic
> - Same governed engine underneath — the checkpoints, the quarantine, the audit trail are identical

**Speaker notes.**
Act III widens the market from "developers managing agents" to "people who could never employ a developer" — and it is sequenced *last* on purpose: the v1 dual-target strategy was withdrawn because a local Vibe Mode cannot beat browser-native rivals on time-to-first-magic (Master Market Document §8.4); Vibe ships as hosted Mainguard Web (P3-05) on cloud sessions (P3-06), with P3-01 checkpoints and P3-02 plain-language escalation. The discipline to emphasize: the simple product is not a *simpler* engine — it is the same governed engine wearing calmer clothes. The trust promises are load-bearing precisely for the person who cannot audit them.

For investors this is the second S-curve: the developer wedge proves the governance engine; the Vibe segment is where managed-key cloud usage — the §5 model's healthiest margin mode — becomes the default rather than the option.

---

## Slide 6 — What the world looks like if we're right

**On the slide:**

> A team of eight ships like a team of forty — and can prove, line by line, who wrote what, what was verified, and who approved it.
>
> - The merge stops being an act of hope. **"Hope is not a merge strategy"** stops being a slogan because it stops being the practice.
> - The audit trail becomes what procurement asks for — and we're the ones who shipped it before they asked
> - Any vendor's agents, one neutral control plane. Switzerland, at scale.

**Speaker notes.**
Paint the end state in the enemy's terms: the blind merge, extinct as a practice — every differentiator in the stack was an answer to one specific way it happens (Narrative §3.1). The compliance line keeps its discipline even at vision altitude: "what procurement is asking for," never "what the law requires" (honesty contract §0.2).

The neutrality point is the durable one: in a world where every platform vendor runs its own agents, the *governance* layer has the same structural reason to be independent that CI and code review did. None of them will make their GUI a better home for a rival's agents; the neutral chokepoint compounds with every agent seat sold — by anyone (Master Market Document Part I).

---

## Slide 7 — Why this is ours to build

**On the slide:**

> 1. The instrument is **shipped** — the Git engine everyone else in the category still lacks
> 2. The empty squares are **verified** — five capabilities, checked across the whole field, nobody combines them
> 3. The economics are **modeled** — BYOK now, metered cloud later, each stage gated by written acceptance tests
> 4. The claims are **sized to be kept** — 4–6 agents today, the cloud for the swarm, [Horizon] stamped where it belongs
>
> **The vision is big. The tense discipline is what makes it credible.**

**Speaker notes.**
Close the loop: a vision deck's last slide should be indistinguishable in *rigor* from a diligence memo, and this one is — each line points at an artifact (the test suite; GTM Plan §5.2's probes; Cloud Companion §5 and §3's gates; the honesty contract itself). The final line is the meta-argument and the true differentiator in a category that died of over-promising: we'd rather be held to the roadmap than believed in advance (Show HN, Narrative §5.2).

Then stop. The follow-ups this deck should generate are the technical deep-dive and the business plan — have both on hand.

---

## Self-gate (five-question gate, applied)

1. **Point at the object** — every act names its task IDs (T-01…T-33; P2-xx; P3-01/02/05/06) and every number its source (§5 model, Part XX §8, GTM Plan §5.2).
2. **Way back** — the vision's guardrails (gates, tripwires, the withdrawn dual-target strategy) are presented as part of the vision, not hidden.
3. **Audit-log test** — three acts, three tenses: shipped / in development / [Horizon]; the word "swarm" appears only under the stated vocabulary discipline; the cloud numbers carry their placeholder flag on the slide.
4. **Delete a word** — the biggest claims are carried by nouns and numbers ("a team of eight ships like a team of forty" is the sole rhetorical flourish, and it is framed as conditional: *if we're right*).
5. **Severity rides the role** — no competitor appears at all; the enemy remains the practice.
