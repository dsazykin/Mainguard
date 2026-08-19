# Collaborative agent sessions — the concrete design for P3-10

Status: **design / planning** (no code) · Edition: **Cloud** · Supersedes the P3-10 sketch with a
concrete design · Depends on P3-06 (cloud worktrees), P3-05 (Mainguard Web), and pulls in P2-41
(remote dashboard), P2-45 (flight recorder), P2-15 (tamper-evident audit).

## 1. Thesis — why this fits Mainguard specifically

"All LLMs have share-chat; none is collaborative." The insight is that the thing worth sharing in
Mainguard is **not a transcript — it is a live run**: a jailed agent with hours of state, a terminal,
a branch evolving under mediation, a verification history, flagged changes awaiting acknowledgment,
and a merge decision. That bundle already lives in one place — **the daemon** — and every surface a
second person would want is already a daemon-owned stream (`StreamAgentEvents`, `StreamQueue`,
`TerminalService.Attach`, the flagged-change gate, the merge lease). The daemon is already a
multi-client server in miniature; today it serves one client on loopback with mTLS.

Crucially, almost nothing here is concurrently-editable text, so **the hard CRDT problem Google Docs
needed does not exist**: the terminal has one input stream, the merge has one lease, acknowledgments
are idempotent per item. The real work is **reach** (getting guests to a run that lives behind cloud
infra), **identity** (who did what, provably), and **control semantics** (who may act, and how that
is visible). Agent prompts can run for hours on large production tasks involving many people; letting
those people share the live run — watch, and later steer — is a genuine product wedge.

## 2. Locked decisions

| Axis | Decision |
|---|---|
| **Topology** | Cloud edition (P3-06). The cloud is the session host; guests fan out from it. Not a host-based P2P v1. |
| **Guest depth (v1)** | **Viewer-only.** Live terminal read, queue/review/telemetry visibility, presence. The Reviewer → Operator → Owner ladder is *designed into the protocol* but not exposed. |
| **Identity** | **Mainguard accounts**, with **both** personal accounts and org/team **workspaces**. |
| **Visibility** | **Full, trust-by-invite.** Roles gate ACTIONS, not sight; inviting = trusting with the session (like a screen-share). Secret-masking is later hardening, not a v1 gate. |
| **Shared unit** | **Phased:** v1 = one agent session; then the repo's full control center; then the whole workspace. |
| **Invite & reach** | The session **owner** invites; a shared session is **visible to the workspace** by default (appears in a shared list), and the owner can additionally invite external guests by link. |
| **Presence** | **Phased:** v1 = minimal (viewer count / avatars-lite); the rich version (per-surface location, follow-mode, terminal driver indicator) is added incrementally. |
| **Session lifecycle** | Becomes **read-only history** when the run ends (v1); full time-scrubbing **replay** (P2-45 tie-in) later. |
| **Protocol future-proofing** | **Pre-design the seams now** — the wire carries roles, a request/grant **driver-token** model, and per-actor **attributed actions** from v1, exposed in UI only when depth lands. No v1 protocol migration later. |
| **Cloud authority (dangerous actions)** | **Org-role-grantable, designed now; owner-only in v1.** Merge and kill-switch can be granted to org roles in the mature model, but in viewer-only v1 they stay strictly the owner's. |
| **Guest client** | **Browser via Mainguard Web (P3-05)** is the primary, no-install guest surface; native app users can also join. |

## 3. The phased roadmap

The build order follows the two "phased" decisions, so each step ships value and the protocol never
breaks:

- **Phase A (v1) — Watch one run.** Share a single agent session (its terminal + queue entry + review)
  read-only to workspace members and invited guests, in the browser. Minimal presence (who's here).
  Session persists as read-only history after the run. Owner-only for every action.
- **Phase B — Watch the fleet.** Widen the shared unit to the repo's whole control center (all agents,
  the merge queue, telemetry). Add richer presence (per-surface location).
- **Phase C — Participate.** Expose the Reviewer depth already in the protocol: guests comment,
  acknowledge flagged changes, approve plans. Add follow-mode + the driver indicator UI.
- **Phase D — Steer.** Operator depth: attributed prompts into the agent, request/grant terminal
  control (the driver token), pause/resume. Org-role grants for merge/kill land here.
- **Phase E — Whole workspace + replay.** Share the workspace; full P2-45 flight-recorder replay of
  ended sessions.

## 4. Architecture

### 4.1 Topology & the session host (P3-06)
The cloud runs the agent's jail (the P3-06 cloud worktree/substrate). The **session host** is a cloud
service in front of that substrate that fans the daemon's existing streams out to N connected clients
over **gRPC-web** (browser-reachable), instead of the single loopback client the on-prem daemon
serves. It is the same wire contract the local daemon already speaks, multiplexed:
`StreamAgentEvents` / `StreamQueue` / `TerminalService.Attach` become 1-to-many, presence and
attributed-action frames are added, and **P2-41's remote-dashboard protocol is the single-viewer
precursor** — QR/short-code device pairing, scoped tokens, TLS — that this generalizes to
multi-viewer fan-out. Where P2-41 lets *you* reach *your* run from your phone, this lets *others*
reach it, gated by identity.

### 4.2 Identity & accounts
Mainguard accounts, with two container types: a **personal account** (solo work) and **org/team
workspaces** (shared work). A run is owned by whoever started it; when started inside a workspace, the
workspace is the audience. Workspace membership + roles (member / admin, and later per-repo
maintainer) are the substrate for the authority model in §4.7. Guest identity is the account, always —
there is no anonymous mode; a share link is a *locator*, and joining still requires signing in (or
accepting an invite that binds a guest identity). **A link never carries a secret** — auth is a token
exchange after the locator resolves, the same discipline the `mainguard://` deep-link guard enforces.

### 4.3 The session model
A **shared session** is (owner, unit, audience, roles, lifecycle-state). The **unit** grows by phase
(one agent → control center → workspace) but the object is the same. **Audience** = the workspace
(default-visible) ∪ explicitly-invited external guests. **Roles** are per-participant and default to
Viewer. **Lifecycle-state** is `live` → `ended` (read-only history) → (later) `replayable`.

### 4.4 The wire protocol seams (pre-designed, mostly dormant in v1)
Even though v1 is viewer-only, the protocol carries from day one:
- **A `role` per connected participant** (Viewer / Reviewer / Operator / Owner) — v1 issues only
  Viewer + Owner; the enum and the server-side gate exist so higher roles are a gating change, not a
  wire change.
- **A driver token** for terminal input: exactly one participant may hold it; others `request` and the
  holder (or owner) `grant`s; the holder is always visible. In v1 only the owner ever holds it (guests
  can't request), but the frames exist. This models terminal control as an explicit, visible handoff —
  never simultaneous typing (there is one PTY input stream; that constraint is a feature).
- **Attributed actions.** Every act that changes state — an acknowledgment, a plan approval, a prompt,
  a merge — carries the **daemon-derived** account identity (extending RT-D2's "approver identity is
  daemon-derived, never a client field" to per-guest). This is what makes the audit real and is the
  hard dependency on **P2-15** (a tamper-evident log that records *which human* did *what* in a shared
  room). Prompts sent to an agent render their attribution **in the conversation** ("Bob: try the
  narrower fix") so the agent's context and every human's mental model stay aligned.

### 4.5 Presence (phased)
v1: a viewer list (avatars + count) on the shared surface. Later: per-surface location ("Alice is on
the diff for `TokenStore.cs`"), a follow-mode (jump to what someone's viewing), a terminal read-cursor
/ driver indicator. All of it is UI over presence frames the session host already multiplexes — no new
distributed-systems problem.

### 4.6 Lifecycle & the artifact
While `live`, guests attach to the streams. When the run ends, the session becomes **read-only
history** — the final terminal state, the branch, the queue outcome, and the attributed action log,
still openable by the audience so an hours-long collaborative run leaves a shared record, not just
something on the owner's disk. **Replay** (scrub the PTY + commits + queue transitions from the start)
is the P2-45 **flight recorder** surfaced to the audience — a later phase, not v1. Retention of ended
sessions is a workspace setting (proposed default below).

### 4.7 Authority for the dangerous actions
Merge and kill-switch mutate real state (merge lands on a checkout; kill freezes every jail). The
permission model is **org-role-grantable by design** — a workspace can grant merge/kill to admins or a
repo's maintainers — but **v1 keeps them strictly owner-only** because v1 is viewer-only anyway. The
merge three-step (RT-D1) and its lease are unchanged; what changes later is *which identities* the
server will accept a `BeginMerge` from, checked against the workspace role, daemon-side.

### 4.8 Full visibility (trust-by-invite) and its one sharp edge
Inviting someone grants them the session's *sight*; roles gate only *actions*. This is deliberate and
matches a screen-share. The sharp edge: **the jail terminal can display anything the agent prints —
including a secret it `cat`s or the owner's private code.** v1 accepts this (inviting = trusting).
Later hardening can run P2-45-style mask-before-persist redaction over the *guest* stream, but it can
never catch everything, so it is framed as defense-in-depth, not a visibility gate — the trust
boundary is the invite.

## 5. Security & audit summary
- Per-account, daemon-derived attribution on every state-changing action → **hard dependency on
  P2-15**; the shared room is exactly why the audit log must be tamper-evident.
- Share links are locators, never bearers of secrets; join = sign-in / bound-invite.
- The terminal shows what the agent shows (trust-by-invite); masking is later, additive.
- gRPC-web fan-out inherits the mTLS/scoped-token discipline of P2-41; the cloud front door is the
  P3-06 boundary.

## 6. Dependencies
- **P3-06 cloud worktrees** — the substrate the run lives in; the hard blocker (this is a cloud feature).
- **P3-05 Mainguard Web** — the browser guest client.
- **P2-41 remote dashboard** — the single-viewer protocol precursor (pairing, scoped tokens, gRPC-web).
- **P2-45 flight recorder** — the replay artifact (Phase E).
- **P2-15 tamper-evident audit log** — per-guest attribution (needed the moment two humans can act).

## 7. Defaulted decisions (proposed — override if you disagree)
These are the smaller open points; sensible defaults so nothing is silently decided:
- **Notifications.** An invitee learns of a share via an in-product notification + the existing OS
  path (Notification Center / Dock badge already ship); email invites for external guests. Default:
  in-product + OS now, email when the external-guest link lands.
- **Spend visibility & whose budget.** Model traffic in the cloud routes through the workspace's
  gateway; spend attributes to the **workspace** and is visible in the resource monitor under its
  existing honesty rules (measured vs unmeasured). A guest-issued prompt (Operator depth) bills the
  workspace, attributed to the guest — surfaced, never hidden.
- **Tiering.** **Hosting** a shared session is a **Pro/Cloud** capability; **joining as a viewer** is
  available to any signed-in Mainguard account (including free, via the browser), to maximize reach.
  This is a real business decision — flagged for override.
- **Concurrent-run scale.** The session host fan-out is bounded per session (a soft cap on live
  viewers), logged when hit rather than silently dropping — same "no silent caps" discipline as the
  rest of the platform.

## 8. Relationship to P3-10
This **is** the concrete design for P3-10 (team collaboration), which the roadmap left chained to
P3-06 as a sketch. When P3-10 is scheduled, it starts from Phase A here rather than a blank page.

## 9. Risks & open questions
- **P3-06 is the long pole.** None of this ships before cloud worktrees; the design is deliberately
  written so Phase A is the minimum viable slice on top of them.
- **gRPC-web fan-out at scale** — the session host is a new stateful service (presence + stream
  multiplex); its failure modes (a viewer wedging a stream, reconnect storms) need their own design
  when built.
- **The trust-by-invite terminal** is a real data-exposure surface for private repos; if customers
  push back, the masking hardening moves earlier — but it is never a substitute for the invite
  boundary.
- **Attribution forgeability** — the whole audit value collapses if a client can assert an identity;
  the daemon-derived rule (RT-D2, extended per-guest) and P2-15 must land together, not separately.
