# Mainguard Video & Demo Scripts

**Lane G Part 5c · Register: brand (VO is founder first-person) · Status: shootable scripts + storyboards.**

Three scripts: the 90-second explainer (Act One), the founder-story film, and the live-demo storyboard that pairs with Lane E's control-center prototype. Production rules for all three: **every frame is the real app or the real prototype** — no motion-graphics recreations of UI, no sped-up fakery (a real 140 ms fade is the brand; see MotionPlaybook); captures on Midnight Loom unless noted, with one deliberate Daylight Loom beat per video so "premium ≠ dark" is shown, not claimed. Music sparse and low; the app's silence is the aesthetic. No exclamation marks in on-screen text; VO reads at a calm, unhurried pace — the register is a person showing you an instrument, not a trailer.

**The honesty rule for demos** (GTM Plan §10.2, binding): anything not yet shipped runs as the Lane E prototype on mock services and is **labeled on screen** — a persistent lower-third: `Design prototype — mock data. Not shipped.` Never silently faked. At L2, control-plane scenes are re-shot against the live pipeline and the label comes off scene by scene, only as each scene becomes true.

---

## 1. The 90-second explainer — Act One (the launch video, also the creator-outreach clip source)

*Purpose: the landing page hero and the press-kit attachment. Everything shown is shipped. ~230 words VO ≈ 90 seconds.*

| # | Time | Picture | VO |
|---|---|---|---|
| 1 | 0:00–0:08 | Black. A terminal line types: `fatal: Unable to create '.git/index.lock': File exists.` Holds a beat. | "If you've used Git long enough, you've met this message. A tool crashed, left a lock behind, and now nothing works — and nothing tells you why." |
| 2 | 0:08–0:16 | Cut to Mainguard opening a large repository. The workspace paints composed; the graph is immediately present. | "Mainguard started as the answer. A native Git client — built so it structurally cannot leave that lock behind." |
| 3 | 0:16–0:30 | The commit graph, scrolled hard through a huge tangled history. Cursor flicks; no stutter. Slow-push zoom on the round-capped linework. | "This graph isn't a chart library. Every row draws itself, history streams in chunks, and it stays smooth at sixty frames a second — on the histories that make other clients give up." |
| 4 | 0:30–0:44 | Unified diff. Drag-select three individual lines → Stage. The staged hunk appears; a beat on the composed result. | "Staging goes down to the individual line. Drag exactly what you mean into the commit — and the patch engine is validated against `git apply`, so what you stage is exactly what Git stages." |
| 5 | 0:44–0:56 | The 3-pane conflict resolver: accept a block from Theirs, undo it, accept from Ours. The panes scroll in sync. | "Conflicts get a synchronized three-pane resolver — ours, result, theirs — with per-side accept, reject, and undo. Merge, rebase, cherry-pick all route here." |
| 6 | 0:56–1:08 | The force-push confirmation dialog, held long enough to read. Then Operation history, an entry marked `Undone`. | "And the app assumes the stakes are real. Force-push is always with-lease — it refuses to overwrite work it can't see. Ref moves are journaled, so undo means undo." |
| 7 | 1:08–1:18 | File → Theme. Midnight Loom → Daylight Loom → Command Deck. The graph re-inks live; shape and spacing never move. | "One design system. Five themes. Same instrument, different light." |
| 8 | 1:18–1:30 | Settle on the full workspace. On-screen text, small and unaccompanied: `Mainguard. Free. No login. mainguard.dev` | "Mainguard is free, there's no account, and nothing leaves your machine. The agent era is coming to this instrument — but that's a story we'll show you when it's true. Today: the best Git client Windows never had." |

*Creator cut-down (60s):* scenes 3–5 + 7–8, VO trimmed to the mechanism lines. This is the clip attached to ThePrimeagen/Theo/Fireship outreach at Act Two — paired there with the queue clip from §3 scene 8.

---

## 2. The founder story film — "Why I'm building Mainguard" (~2:30)

*Purpose: the About page and the act-two press kit. Script is the Narrative §5.5 essay (copy of record) compressed to voice; the film adds only pictures. Talking-head A-roll (desk, natural light, no startup-office theater) intercut with screen B-roll.*

**Beat 1 — The lock file (0:00–0:45).** A-roll open, mid-sentence energy: "Every developer who's run more than one Git tool has met `.git/index.lock`. Something crashes, the lock stays, and the next thing you try fails — with a message that blames nothing and suggests nothing. You delete a file you're not sure is safe to delete. And you hope." B-roll: the terminal error; then Mainguard's stale-lock notice, held: *"Mainguard didn't create this lock, so it won't be removed automatically."* VO over it: "Mainguard began as an answer to that one footgun — a rule that the app can never leak the state that leaves locks behind. And when it finds someone else's lock, it says so plainly and refuses to guess on your behalf. That rule turned out to be the whole thesis in miniature."

**Beat 2 — The instrument (0:45–1:30).** B-roll-led: the graph's linework in close-up, the theme switch, the force-push dialog. VO: "The second belief is that a tool for high-stakes work should feel like an instrument, not a web page in a frame. So it's rendered natively — the graph is drawn, not charted. And the care concentrates where the stakes do: destructive operations tell you what changes, what stays recoverable, and which safer path exists — before you click, not after."

**Beat 3 — The trust problem (1:30–2:30).** Back to A-roll, slower: "While I was building the client, the ground moved. Coding agents made it trivial to produce ten branches an hour — and review time went up ninety-one percent against a fixed human ceiling. Every vendor is selling faster generation. Almost nobody is working on the part that gates shipping: how you verify, attribute, and safely merge work you didn't write." Beat. "That's where Mainguard is going, and I'll be precise about tense: none of it is built yet. A merge queue that re-verifies whatever goes stale. Review ranked by blast radius, with provenance per line. Sandboxes where an agent can be wrong without being dangerous." Final line, direct to lens: "The client ships first, because the client is the proof. I'd rather be held to the rest than believed in advance."

*(End card: `Mainguard — free, native, no login. The roadmap is public. Hold us to it.`)*

---

## 3. The live-demo storyboard — Act Two (~4:30, pairs with the Lane E prototype)

*Purpose: the act-two launch video, the creator demo, and the design-partner pitch. Every scene names its real surface in [ControlCenterDesign.md](../../../design/ControlCenterDesign.md) / [VibeModeDesign.md](../../../design/VibeModeDesign.md); the narration uses the specs' own state words (`Verified`, `Stale`, `CanMerge`) so the film and the product never drift. Until each capability ships, its scene carries the prototype lower-third; the storyboard is written so the same shots re-stage against the live product at L2. Arc follows GTM Plan §10.2.*

| # | Time | Picture (surface, per spec) | Narration |
|---|---|---|---|
| 1 | 0:00–0:20 | The v1 client on a real repo — graph, staging. Then the **section rail** (ControlCenter §0): cursor moves from *Repo viewer* to *Coordinator*. | "This is Mainguard — the same free Git client from launch. Today it grows a second section. Same window, same instrument: the repo viewer never changes. This is the control center." |
| 2 | 0:20–0:50 | **Prompt-first dispatch** (§4.3): command palette, `New session:` typed with the task; repo → agent CLI → base branch picked as chips; Enter. Toast: `Plan requested — the Coordinator will draft it for your approval.` | "You don't configure an agent — you describe a task. Mainguard routes it through the Coordinator, and here's the first rule of the whole system: no code gets written until a human approves a plan." |
| 3 | 0:50–1:20 | The **TaskPlan approval card** (§5.2): Scope paths in mono, Approach, Test, the facts row (`Budget $1.50 · admission 3/4`). Cursor hovers `[Approve plan]`, clicks. | "The plan is three fields, and Scope is the load-bearing one — these files, this approach, this test. Approving a plan approves a scope; anything the agent touches outside it gets flagged later, automatically. The budget and headroom are stated before you decide, not after." |
| 4 | 1:20–1:50 | The **agent document** (§4.1): terminal streaming on `SurfaceDeep`, the plan tree ticking, the health strip — `egress 0 · procs ok`. A second and third agent spawn in the rail's agent list. | "Each agent works in its own isolated sandbox and worktree — it can't touch your working directory, and the health strip is live: zero blocked egress attempts, processes normal. Run a few in parallel; four to six is the honest ceiling on a sixteen-gig laptop, and we won't pretend otherwise." |
| 5 | 1:50–2:20 | The **merge-queue rail** (§3.1): main's thread in Lane1; entries `Loom-3 — Verified · main@d4e1f`, `Loom-1 — Verifying · tests 41/58` ticking, `Loom-4 — Working`. | "When an agent finishes, its branch doesn't come to you — it goes to verification: your test suite, inside its sandbox, against the exact main it would merge into. That SHA on the rail isn't decoration; it's the claim, auditable. Only `Verified` earns the review button." |
| 6 | 2:20–3:00 | The **review cockpit** (§6.1): header facts (`verified @ d4e1f · fresh · 58 tests green`), the flagged gate — one item acked, one jumping the diff (`outside approved scope: docs/notes.md`), provenance chip `⑂ Loom-3 · task #7 · a1b2c3d`. The last ack lands; `[Merge]` lights. | "Review is ranked by blast radius, not alphabetically. Flagged items — a touched scripts block, anything outside the approved scope — must be acknowledged one by one; there is no select-all for risk. Every hunk carries its provenance: which agent, which task, which commit. And the merge button isn't enabled by mood — it's enabled by `CanMerge`, and when it's disabled it tells you the exact reason." |
| 7 | 3:00–3:40 | **The signature moment.** Merge clicks. Toast: `Merged fix/auth-refresh into main.` Then, on the rail, the **stale cascade** (§3.3): main's dot advances; the other `Verified` chips flip, one by one, to `Stale ↻ re-verifying against d4e1f…`. Hold the silence; let the ripple read. | "Watch the rail. One merge landed — and every other verified branch just went stale, because main moved and their evidence is now about a world that no longer exists. Mainguard re-verifies them automatically before they can merge. Validated-then-stale is unvalidated. No other product on the market does this step — this ripple is the reason Mainguard exists." |
| 8 | 3:40–4:00 | The **kill switch** (§5.4/§0): quiet octagon at the rail's foot; hover shifts it to Danger; click. The freeze banner: `All agents paused. The merge queue is frozen. Nothing was lost — resume when ready.` Then resume. | "And the brake is always one click, no confirmation dialog — because it's an emergency stop that destroys nothing. Everything pauses, everything is kept, resume when ready. A control room earns trust by how it stops." |
| 9 | 4:00–4:20 | **Telemetry** (§8.1): the egress fact table; then its all-clear empty state: `No blocked egress, no secret access attempts, no anomalous processes — 4 sandboxes healthy.` A beat on the audit line for the merge from scene 7. | "Underneath, everything you just watched is a record: plan approvals, verdicts, the merge and who clicked it — audit-grade, tamper-evident, exportable. Not because a regulation demands it today, but because 'trust me' is not an audit trail." |
| 10 | 4:20–4:35 | Return to the Repo viewer — the ordinary graph, the new merge commit at the top. End card: `Your agents, wherever you run them. Verified here. mainguard.dev` | "And then it's just Git again — a clean commit on main that you can actually vouch for. Your agents, wherever you run them. Verified here." |

**Optional coda — Vibe (0:20, only where the audience includes non-technical founders; clearly labeled `Future product — design prototype`).** The Vibe surface (VibeModeDesign §1–2): the chat card `Progress saved — you can always come back to this point.`, then `[Publish to Web]` and the live-URL card `Your app is live`. Narration: "The same safety machinery, one more translation layer: for the founder who's never heard of a branch — everything above, in plain words. That's further out, and it's labeled that way."

**Wedge insert for creator/press cuts (30s, drops in after scene 5):** the external-PR intake — the PR list showing bot-authored PRs (Codex/Jules), one subscribed, its entry materializing on the queue rail as `agent/pr-142 — Verifying`. Narration: "One more thing, and it needs zero adoption: if your agents already open pull requests, point Mainguard at them. Same verification, same review, same staleness rules — merged back through your host's own API. Nothing about how you run agents changes. What changes is what you can trust when they finish."

---

## Self-gate (Part 5c)

- Every scene names a real, specified surface and quotes spec strings verbatim (TaskPlan card fields §5.2; rail state words + `main@sha` §3.1–3.2; cockpit gate copy §6; freeze banner §5.4; telemetry all-clear §8.1; Vibe cards VibeModeDesign §2/§4.2) — the film cannot drift from the design because it *is* the design, read aloud.
- Honesty machinery is structural: the prototype lower-third rule, per-scene label removal only as capabilities ship, capacity honesty (4–6) spoken in the demo itself, audit framed as "not because a regulation demands it today."
- The narration carries the named lines verbatim where they're load-bearing (scene 7: "validated-then-stale"; wedge insert: the §2.2 campaign wording) and never uses banned vocabulary; the emotional peak (the stale cascade) is played with silence and a ripple, not music — restraint is the brand (MotionPlaybook discipline extended to film).
