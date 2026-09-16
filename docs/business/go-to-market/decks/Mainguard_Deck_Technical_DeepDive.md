# Mainguard — Technical Deep-Dive Deck: the Safe-to-Merge Guarantee *as* the Moat

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../../BUSINESS.md), [`GTM.md`](../../../GTM.md) and [`GTM_Assets.md`](../../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Date:** 2026-07-11 · **Register:** brand-technical (Voice Bible rules; heavier on mechanism, zero hype)
**Audience:** the technical partner at a dev-tool fund, a diligence engineer, a staff-engineer champion at a design-partner prospect, or a technical advisor. This deck assumes the listener will probe; every slide names the real type, task ID, or invariant so the probe lands on something.
**Companion to:** `Mainguard_Investor_Deck_Seed.md` (the narrative deck). This one replaces slides 5–7 of that deck with the full mechanism and is 80% mechanism by weight.
**Honesty contract (binding, Narrative §0):** the shipped client is present tense (T-01…T-33, 1,042 tests). Everything in the pipeline is **[Horizon]** — specified in `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (P2-xx / P3-xx task IDs), in development on `phase2`, not shipped. This deck says so on every slide where it applies; a technical audience punishes tense-blur harder than any other.

---

## Slide 1 — The claim, stated as an invariant

**On the slide:**

> **Nothing reaches `main` that wasn't verified against the `main` it lands on.**
>
> Not "CI was green when the PR opened." Not "an LLM reviewed it." Verified — your tests, in the agent's sandbox, against the current `main` — at merge time.

**Speaker notes.**
Every other slide is the machinery that makes this one sentence enforceable rather than aspirational. State the contrast precisely, because it is the whole category difference: every merge queue on the market re-runs *CI*; none re-runs *verification* on the post-rebase state of agent branches (probe-verified, GTM Plan §5.2 #4). The named failure mode, verbatim and repeatable: **validated-then-stale is unvalidated.** A branch that passed tests an hour ago, against an older `main`, is a blind merge wearing a green checkmark.

This is a *guarantee* in the engineering sense — an invariant the architecture enforces — and the deck's thesis is that the guarantee itself is the moat: each layer below is individually copyable in ~2 quarters; the stack is not, because the bottom layer is a Git engine nobody else in the category has.

---

## Slide 2 — Layer 0 (shipped): a real Git engine, and why it's the prerequisite

**On the slide:**

> Shipped today, exercised by **1,042 tests** (T-01…T-33):
>
> - Deterministic repository-handle management — every LibGit2Sharp handle opens and closes through one path; the app structurally cannot leak `.git/index.lock`
> - A pure patch engine (`PatchParser`/`PatchBuilder`) validated against `git apply` — line-level staging that is *exactly* what Git stages
> - A pure 3-way merge chunker behind a synchronized 3-pane resolver; merge/rebase/cherry-pick/pull all route conflicts through it
> - An operation journal (undoable ref moves) + reflog viewer; force-push is `--force-with-lease`, never bare `--force`
> - A virtualized, vector-drawn commit-graph lane router at 60fps

**Speaker notes.**
This slide is present tense, and it is the credibility deposit the rest of the deck spends. The technical listener should hear one argument: **verification and merge governance are Git-engine problems.** Stale re-verification requires rebase machinery. Curating agent WIP requires interactive rebase and partial staging. "Undo what the agent did" requires an operation journal. Per-hunk provenance in review requires a diff stack and blame. The orchestrator field (Conductor, Superset, Sculptor) would have to build all of this first to follow us downstream (Viability §3, D-5) — and the incumbent with a client (GitKraken) executes agents unsandboxed on the host with no verification pipeline (Competitor Research §2).

The founding footgun is worth thirty seconds because it *is* the thesis in miniature: `.git/index.lock` roulette — two tools touch the index, one exits early, the next operation fails blaming nothing. Mainguard's oldest architectural rule (every handle through `IGitService.ExecuteWithRepo`) exists to make that structurally impossible. Multi-agent Git is that same problem at N× concurrency; we solved the N=2 case before writing a line of orchestration.

---

## Slide 3 — Layer 1 [Horizon]: containment — the quarantine remote

**On the slide:**

> Every agent works in its own worktree, inside a hardened sandbox, whose **only** configured push destination is a daemon-owned quarantine mirror.
>
> - No credential for the real remote exists inside the sandbox. No route to it exists either — egress is default-deny, allowlisted (model APIs, registries)
> - `git push --force origin main` from a prompt-injected agent is **structurally impossible**, not firewall-blocked
> - Reviewed work leaves one way: the *human* fetches it, and the *human's own* credentials push it

**Speaker notes.**
Task anchors: per-agent worktrees — `IAgentWorktreeManager.CreateAgentWorktree` (P2-06); quarantine remotes — ESC invariant `ESC-I3`; default-deny egress — P2-07 `EgressProxyConfigurator`/`IEgressPolicy`. The design stance to emphasize: **deliberate absence beats enforcement.** The Cloud Companion's topology diagram draws the sandbox→real-git-host edge as `x--x` — a crossing that must not exist — and the same absence holds identically on the desktop and in the cloud pod (§2.2).

Competitive contrast, offered factually: Conductor and the wrapper field isolate with worktrees only — same host, same credentials, same network (Competitor Research §3). Sculptor is the closest sandbox thesis (real containers) but publishes no egress posture — no default-deny claims anywhere (Competitor Research §10). MergeLoom, a governance vendor, publishes no sandbox or egress hardening at all for the thing that executes AI-written code [V absent — Deep Dive §1.9]. The security architecture will be published and the daemon is source-available (FSL) — "a boundary you can audit" is itself a sales asset (GTM Plan §2.5).

---

## Slide 4 — Layer 2 [Horizon]: the verdict is a fact, not an opinion

**On the slide:**

> A branch reaches human review only after **your test suite passes inside the agent's sandbox**.
>
> - Verification runs record `main@<sha>` + pass/fail + log artifact — an immutable record tied to the exact base it was verified against (P2-10)
> - Failing branches never reach your eyes
> - Deterministic verdicts, not LLM review comments — an audit found **~35%** of a leading AI reviewer's comments genuinely useful

**Speaker notes.**
The record structure is the load-bearing detail: verification isn't a boolean, it's a tuple — *what* passed, against *which* `main`, with *what* evidence. That tuple is what makes staleness detectable at all (slide 5) and what makes the audit trail meaningful (slide 7). Anchor: P2-10 verification runs, "configured test command in worker sandbox, immutable records tied to main@sha" (MergeLoom Deep Dive §3 table).

Position against the review layer without naming a villain: CodeRabbit et al. sell probabilistic opinions from a cloud app; the ~35% useful-comment figure (GTM Plan §5.3) is why AI-review fatigue is a documented buying trigger. "Your tests passed in the agent's sandbox" is a fact — the antidote framing (Narrative §3.3: *"a fact, not an opinion"*). Longer-term we'd integrate an LLM reviewer as an optional *signal*, never the gate.

---

## Slide 5 — Layer 3 [Horizon]: the re-verifying merge queue — the empty square

**On the slide:**

> Merge branch A → every other "verified" branch is now verified **against a main that no longer exists.**
>
> - The queue marks them stale, re-queues (rebase → re-verify), and only offers merges whose verification matches current `main`
> - Every queue on the market re-runs CI. **None re-runs verification on the post-rebase state.** Probe-verified across the field.
> - Single linearization point: one daemon owns the queue — lock + CAS, no distributed-consensus theater

**Speaker notes.**
This is the moat's keystone, so give it the most time. The mechanism is pure Git + scheduling: merges serialize through one daemon (the same design that keeps the cloud honest — ADR-1's nested topology keeps *one daemon = one linearization point per tenant*, Cloud Companion §4). The staleness cascade is mechanical, not heuristic: verification records carry `main@sha`, so "stale" is a comparison, not a judgment call.

Why incumbents can't checkbox it: GitHub's merge queue re-runs CI on batched candidates but has no concept of local verification or of *agent* branches' semantic staleness; retrofitting per-branch sandbox re-verification means owning the execution substrate — which is the whole product. MergeLoom's architecture makes it *intrinsic*: they stop at "PR opened," slices meet at PR time, and nothing re-validates when main moves [V absent — Deep Dive §1.6]. The one-liner to leave in the room: *they stop at PR-opened; we govern the last mile.*

If asked about throughput: honest answer — the local ceiling is 4–6 agents on a 16 GB laptop, so queue depth is single digits locally; the cloud tier (P3-06) is where the queue earns its scaling story, on the same binary (`G-14`).

---

## Slide 6 — Layer 4 [Horizon]: provenance and risk-ranked review

**On the slide:**

> The review is ordered by blast radius and every hunk is attributable:
>
> - **Risk ranking** — dependency/config/CI/hook-touching hunks first, not file-alphabetical (P2-11 `RiskClassifier`)
> - **Per-hunk provenance** — "agent X, model Y, session Z, under approved plan P wrote these lines," in the diff and blame gutters
> - Built on the **Agent Trace** standard — emitters are coming from the vendors; **no consumer/renderer exists**. We ship the first.
> - A flagged-changes gate: lockfiles, CI workflows, git hooks require explicit item-by-item acknowledgment

**Speaker notes.**
Two market facts carry this slide. First, hunk-level risk ranking exists in production only inside Meta (the RADAR/Diff Risk Score pattern, GTM Plan §5.2 #3) — the make-vs-buy proof. Second, the Agent Trace probe result (Competitor Research probe (b), via Narrative §2.4): the ecosystem is standardizing *emission* of provenance and nobody renders it. Being the first consumer of a standard others emit is the cheap side of a network effect — the vendors do the writing; we do the reading, in the one place reviewers actually look.

The flagged-changes gate is the supply-chain answer (prompt-injected agents editing `package.json` scripts or CI workflows): those categories can't scroll past; post-merge installs run `--ignore-scripts` (Viability D-1). The buying trigger this slide serves, verbatim from the market docs: *"review five agent branches in twenty minutes, safely"* — review time +91% is the number that makes an EM sign.

---

## Slide 7 — Layer 5 [Horizon]: the record — audit-grade, tamper-evident

**On the slide:**

> Every spawn, plan approval, inference, verification verdict, and merge decision lands in a **hash-chained, append-only audit trail** with the authorizing identity attached.
>
> - `audit verify` — anyone can check the chain's integrity; SIEM export for the compliance team (P2-15/P2-16)
> - Individual attribution: which *human* approved which plan — service-account blindness is the #1 called-out compliance gap
> - Framing discipline: **audit-grade evidence, where EU procurement is heading** — not "legally required cryptography"

**Speaker notes.**
State the legal position exactly as the honesty contract requires (Narrative §0.2), because a technical diligence will check: EU AI Act Article 12 mandates event logging and traceability, **not** cryptographic immutability, and the May 2026 Digital Omnibus provisionally postponed high-risk obligations to Dec 2027. The pitch is *enterprise trust and procurement direction* (auditors are already asking — the AI-BOM movement, Codacy; converging frameworks: Art. 12, NIST AI RMF, OWASP LLM Top 10) — never a deadline scare. Over-claiming here would cost more credibility than the feature earns.

Competitive fact: MergeLoom ships traceability with **no integrity** — no hash chain, no signatures, no SIEM, no SOC 2/ISO [V absent — Deep Dive §1.7]; the published comparison writes itself: "audit-grade vs audit-flavored." No orchestrator GUI ships anything here at all (Viability §1.4).

---

## Slide 8 — The same guarantee, in the cloud, on the same binary

**On the slide:**

> The cloud tier is not a second product. **Same daemon binary, new home** (invariant `G-14` — no cloud-only fork):
>
> - Per-tenant pods; per-tenant encryption keys in a KMS; crypto-shred on account deletion (P3-06)
> - The quarantine-remote absence holds identically — the pod holds no credential for the user's real remote
> - Every timeout is `RttBudget`-relative; the desktop test suite runs in CI under injected 80 ms WAN latency (P2-25)
> - Capital is gated: spike → walking skeleton → full build → GA, each behind a written go/no-go gate

**Speaker notes.**
For a technical investor this slide answers "does the architecture scale into the business model" — and the answer is that the scaling story was designed in, not bolted on. The de-risking sequence (Cloud Companion §3) is engineering discipline as capital discipline: the expensive stage is funded only after the protocol is proven WAN-safe, the pod-topology ADR is decided by a 2-week spike, and a browser-driven walking skeleton works. The GA gate is *"beta unit economics match the §5 cost model within tolerance"* — the spreadsheet is the acceptance test, not the pitch.

Unit-economics headline, flagged as illustrative placeholders: platform COGS is cents per session; model spend dominates (87–95%) and passes through; margin = platform markup + 10% handling; break-even ≈ 3,200 active cloud users (§5.3–5.6). The topology decision *is* a pricing decision — flat one-pod-per-agent would roughly triple platform price for identical value (§5.7) — which is why the nested topology is the default and per-agent pods are a paid isolation tier.

---

## Slide 9 — What we deliberately do not build

**On the slide:**

> - **No auto-merge, ever** — P2-10 lists "auto-merge of any kind" as a rejection trigger
> - No "50 agents" claims — the honest local ceiling is **4–6 agents on 16 GB**; scale is the cloud tier's job
> - No reselling inference — BYOK locally; metered pass-through + thin handling in the cloud
> - No wrapping vendor UIs — integration at the CLI/process boundary (PTY + git), never against a vendor's release pace
> - No LLM-opinion gates — models may advise; only deterministic verdicts gate

**Speaker notes.**
A negative-space slide closes a technical pitch better than a roadmap slide — it shows the design has a spine. Each line is a priced lesson: auto-merge is the enemy blessed (Composio AO merges when CI is green; *we prove it's still green after everyone else merged* — Narrative §2.2); "50 agents" is the vocabulary of the dead companies and indefensible on consumer hardware (honesty contract §0.3); inference resale is margin death (GTM Plan §8); UI-wrapping is Omnara's pivot (GTM Plan §5.4).

Close by returning to slide 1's invariant, and offer the objection-handling doc (`docs/business/go-to-market/Mainguard_Objection_Handling.md`) and the red-team plan (`docs/phase-2/Mainguard_Orchestration_RedTeam_Plan.md`) for the deep-diligence follow-up. A technical audience converts on the willingness to be probed.

---

## Self-gate (five-question gate, applied)

1. **Point at the object** — every layer names its task ID (P2-06/07/10/11/14/15/16, P3-06), invariant (ESC-I3, G-14), or type (`IAgentWorktreeManager`, `RiskClassifier`, `PatchParser`); shipped claims carry the test count.
2. **Way back** — the deck's recovery paths are architectural (undo journal, reflog, quarantine, gates) and named where relevant.
3. **Audit-log test** — [Horizon] is stamped on slides 3–8; the EU AI Act position is stated with the Omnibus caveat; MergeLoom claims carry their [V]/[V absent] tags' substance.
4. **Delete a word** — no decorative adjectives; the strongest words are "structurally impossible" and each is backed by a mechanism.
5. **Severity rides the role** — competitors are described by verified absences, not adjectives.
