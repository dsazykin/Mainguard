# Mainguard — Defensibility Memo (why this doesn't get taken from us)

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../BUSINESS.md), [`GTM.md`](../../GTM.md) and [`GTM_Assets.md`](../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Date:** 2026-07-11 · **Status:** Lane-F stakeholder document — the diligence-grade moat argument · **Register:** brand (Voice Bible rules; concessions stated before claims)
**Audience:** an investor's partner meeting, a diligence engineer, our own roadmap prioritization when a competitor ships something.
**Method:** every moat claim is paired with (a) the evidence it rests on, cited to `docs/business/market-analysis/` / `docs/business/go-to-market/`, and (b) the **erosion scenario** — what would have to happen for the claim to stop being true, and the tripwire already set for it. A moat memo that can't name its own erosion isn't a memo; it's a wish.
**Companion:** `Mainguard_Objection_Handling.md` — the same material, cut per objection for live use.
**Honesty contract (Narrative §0):** the pipeline layers are **[Horizon]** — the moat argument is explicitly about *architecture and position*, not shipped features; where a claim depends on shipping, that dependency is stated.

---

## 1. The thesis

Mainguard's defensibility is not any single feature — the market docs are blunt that **any single feature has a ~2-quarter exclusivity window** (GTM Plan §12.1). The moat is the *stack*: five layers that individually range from hard to merely annoying to copy, but that compound because each requires the one below it, and the bottom layer — a real Git engine — is the one thing no competitor class in this category possesses or can acquire cheaply. On top of the stack sit two positional assets (vendor-neutrality and the Windows/WSL2 flank) and one cultural asset (the trust posture) that are cheap for us and structurally expensive for the specific incumbents best placed to attack us.

One sentence for the partner meeting: **everyone else must either build a Git client, betray their platform incentive, or re-architect a shipped product to follow us — and most must do two of the three.**

---

## 2. The five layers, with retrofit-cost analysis

### Layer 1 — The Git engine (shipped; the prerequisite)

**The asset.** A working native client: deterministic repository-handle management, a pure patch engine validated against `git apply`, a 3-way merge chunker, interactive rebase, an operation journal, a reflog surface — T-01…T-33, 1,042 tests.

**Why it's a moat and not just a product.** Every downstream differentiator is a Git-engine problem: stale re-verification *is* rebase machinery; curating agent WIP *is* interactive rebase + partial staging; "undo what the agent did" *is* the operation journal; per-hunk provenance *is* a diff stack + blame (Viability §3, D-5). The orchestrator field (Conductor, Superset, Nimbalyst, Sculptor) has none of this — their isolation is worktrees, their review is a diff view, their merge is a button that shells out. To follow us downstream they must first build what took us a year and 1,042 tests to trust.

**Retrofit cost per class:** orchestrators — build a client from scratch (a year+, off-thesis for a 6-person team shipping weekly). First-party vendors — they have engineers, not incentive (Layer 5). GitKraken — has a client (Electron), but its agent execution is host-level and unsandboxed, and queue semantics are the re-architecture case (Competitor Research §2). MergeLoom — has no client at all and every human touchpoint is rented from the tracker and code host (Deep Dive §6).

**Erosion scenario.** An incumbent *acquires* a client instead of building one (the Cursor–Graphite pattern). Tripwire: any orchestrator/review-layer acquisition of a Git client → reassess within the quarter.

### Layer 2 — Containment by construction (P2-06/07, ESC-I3) [Horizon]

**The asset.** Sandboxes whose *only* push target is a daemon-owned quarantine mirror, with no real-remote credential present and default-deny egress — escape structurally impossible rather than firewall-blocked.

**Why it holds.** The field's isolation stories stop at worktrees (Conductor: same host, same credentials, same network — Competitor Research §3); Sculptor is the closest thesis (real containers) but publishes no egress posture at all (§10); MergeLoom — a *governance vendor* — publishes no sandbox or egress hardening for the thing that executes AI-written code [V absent — Deep Dive §1.9]. Publishing our security architecture with a source-available (FSL) daemon converts the design into a checkable claim — "a boundary you can audit" — which a closed competitor cannot match by assertion (GTM Plan §2.5).

**Erosion scenario.** Docker-class infrastructure makes hardened agent sandboxes a commodity primitive. Partially priced in: the primitive can commoditize; the *integration* (quarantine remotes wired into provisioning, sync, verification, and audit) is the product. Watch Docker Sandboxes and Claude Code sandbox-environments (Appendix A sources).

### Layer 3 — Deterministic verification + the re-verifying queue (P2-10) [Horizon] — the keystone

**The asset.** Test verdicts recorded as `main@<sha>` + pass/fail + artifact, and a merge queue in which any merge marks other verified branches stale and re-verifies them post-rebase. *Validated-then-stale is unvalidated.*

**Why it's the hardest to copy.** Probe-verified across the field: every queue on the market re-runs CI; **none re-runs verification on the post-rebase state of agent branches** (GTM Plan §5.2 #4). For GitHub, retrofitting means owning local execution — inverting a cloud product. For MergeLoom, staleness is *intrinsic to the architecture*: they stop at "PR opened," and their epic slices meet only at PR time (Deep Dive §1.6, §5.3). For the review layer (CodeRabbit et al.), verdicts are LLM opinions by construction — deterministic local gates are a different product with a different cost structure. This is also the layer where the failure mode has a *name we own* — the framing shortcut is deployed in every asset until the market repeats it back (Narrative §3.3).

**Dependency stated:** this layer's moat value requires shipping it credibly at act two. It is the spine of the build order for exactly that reason.

**Erosion scenario.** Cursor Origin is the only announced product aiming at agent-scale review + merge queue (fall 2026). It is a cloud forge — the local-first counter-position is clean. **Tripwire (already in writing):** if Origin ships local execution + provenance, re-plan within a quarter (GTM Plan §12.1).

### Layer 4 — Provenance + risk-ranked review (P2-11, Agent Trace) [Horizon]

**The asset.** Per-hunk "agent X, model Y, session Z, under approved plan P" in the diff and blame gutters, with hunks ordered by blast radius and a flagged-changes gate on supply-chain-sensitive paths.

**Why it holds.** Hunk-level risk ranking exists in production only inside Meta (RADAR — GTM Plan §5.2 #3). The Agent Trace standard has emitters coming from vendors and **no consumer/renderer exists** (probe (b)); being the standard's first renderer means the vendors do the emission work and the value accrues to whoever owns the review surface — which requires Layer 1. A standards position is cheap to take early and expensive to take late.

**Erosion scenario.** A vendor ships its own renderer — but single-vendor, per Layer 5, which is precisely the gap. The cross-vendor renderer position erodes only if the *standard* fragments; watch the Agent Trace repo.

### Layer 5 — The audit chain (P2-15/16) [Horizon] + the structural incentives above it

**The asset.** Hash-chained, append-only, identity-bound, SIEM-exportable, offline-verifiable (`audit verify`) — vs the field's nothing (no orchestrator ships any of it; MergeLoom ships traceability with no integrity [V absent]).

**And the two positional assets that protect all five layers:**

- **Vendor-neutrality (Switzerland).** Every first-party GUI manages only its own agents; each is *incentivized* to lock in, and none will make its GUI a better home for a rival's agents (GTM Plan §5.3). This is the rare moat that is enforced by the *competitors'* economics rather than ours: absorbing our position requires betraying their own. Jules/Codex/Copilot PR output is our intake supply, not our competition.
- **Windows/WSL2-first.** The largest developer OS (59.2% personal / ~48% professional), shipped Mac-first by the entire polished-devtool wave; the funded category leader has no Windows signals in its changelog (Competitor Research §3). Real WSL2 depth (egress, filesystem topology, provisioning — P2-05/21/22) is unglamorous integration work that a Mac-first team funds last. Erosion: $22M funds a Windows port any quarter — which is why the defense is *pairing* Windows with queue semantics they'd have to re-architect to follow, not Windows alone.

---

## 3. The trust posture as a moat (cheap for us, expensive for the attackers)

No login, no private-repo wall, local-first, BYOK in the OS keyring, source-available daemon, published telemetry schema, published security architecture. Each is nearly free for us and structurally costly for a specific attacker: GitKraken monetizes the account wall it would have to demolish; the first-party vendors monetize the lock-in; Warp already paid the multi-year trust tax that proves the asymmetry (GTM Plan §2.5, §5.4). In a product whose entire thesis is "refuse blind trust," the posture is also coherence — the marketing *is* the architecture. Erosion: none structural; this moat is lost only by our own drift, which is why the posture is locked in writing.

---

## 4. What is *not* a moat (kept honest, so the rest is believed)

- **Native rendering vs Fork.** Fork is native too, fast, and loved; against Fork the edge is the agent thesis, not the renderer (Narrative §2.1). Native-vs-Electron is an edge against GitKraken specifically.
- **Orchestration.** Commoditized, free from every vendor, and the vocabulary of the dead companies. Parity, conceded quickly (GTM Plan §5.3).
- **"Agents in worktrees."** A checkbox since Claude Code v2.1.49 (Viability §1.1).
- **The name.** MergeLoom's collision makes "Mainguard" a risk item, not an asset; every positioning line survives a rename by design (Narrative §2.2).
- **Speed alone.** Sublime Merge is the fastest client in the market and dormant — necessary, not sufficient (Narrative §2.1).
- **Any single feature for more than ~2 quarters.** Stated three times in the corpus; believed here.

---

## 5. The failure ledger as the moat map

The corpse pile defines the boundaries inside which the moat exists (GTM Plan §5.4): Bloop/Vibe Kanban (free orchestration ≠ business → the free tier is a *Git client* with independent value); Terragon (cloud agent-running gets absorbed → local-first, their subscriptions); Kite (individuals don't pay → teams are the revenue plan); Warp (trust tax → the §3 posture); Omnara (wrapping vendor UIs → integrate at the CLI/process boundary only). A competitor attacking us must either re-learn these lessons at their own expense or adopt our exact shape — and the second is the moat working.

---

## 6. Erosion dashboard (the standing watch)

| Watch item | Signal | Response |
|---|---|---|
| **Cursor Origin** (fall 2026) | Ships local execution + provenance | Re-plan within a quarter (the named tripwire) |
| **GitKraken Kepler** | Ships sandbox / queue / provenance semantics | Accelerate the compliance-grade pieces hardest to retrofit (queue semantics, audit integrity — Competitor Research §2) |
| **Conductor** | Windows port announced | Press verification + queue, where they must re-architect; Windows alone was never the whole moat |
| **Agent Trace** | Standard fragments, or a first-party renderer ships | Reassess the Layer-4 position; the cross-vendor surface remains ours if Layer 1 holds |
| **Design partners** | Won't pre-commit to paid pilots 2 months post-act-two | The moat protects a business that must exist: revisit packaging (GTM Plan §12.2) |
| **Anthropic/OpenAI ToS** | Further harness constraints (the 2026 OAuth precedent) | API-key path primary; local-model pressure valve; adapters vendor-neutral (Part XX §4) |

---

## 7. Summary for the partner meeting

1. The bottom of the stack is **shipped** (the Git engine — the one asset no competitor class has).
2. The keystone (**the re-verifying queue**) is a verified empty square, architecture-hostile to every incumbent's retrofit path, and named in language we control.
3. The two positions (**vendor-neutral, Windows-first**) are protected by the *attackers'* incentives and priorities, not only by our speed.
4. The posture (**no login, local-first, source-available**) makes the trust claim checkable — and is the one moat that can only be lost voluntarily.
5. Every erosion path has a **named tripwire already in writing** — this memo's confidence is conditional, monitored, and therefore worth something.

*Self-gate: every claim cites its market-analysis or GTM source; every [Horizon] layer states its shipping dependency; §4 concedes the non-moats at the same volume as §2 claims the moats; every erosion scenario has a response owner in the roadmap. Nothing here requires believing a forecast — only reading the field as verified on 2026-07-06/07.*
