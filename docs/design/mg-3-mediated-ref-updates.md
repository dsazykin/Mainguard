# MG-3 / MG-17 — Confining agent writes to the bare mirror

**Status:** IMPLEMENTED 2026-07-27 — all three stages of Option B landed, one commit each.
§6 records the decision; §7 the resolved questions. Both remain accurate to the code.
Implementation followed MG-17 (userns-remap), which landed first.

| stage | what landed | closes |
|---|---|---|
| 1 | per-agent repo + `objects/info/alternates`; worktrees moved to it; the gc policy of §4 | nothing yet (data path) |
| 2 | `AgentRefMediator` — the four rules in code, quarantine-then-decide, CAS; `AgentRefWatcher` | nothing yet (route) |
| 3 | `ContainerSpecBuilder.MirrorMountReadOnly = true` | **MG-3** |

Measured on this repository (a 25 MB mirror): a per-agent repo is **39 KB** at creation with **zero
objects of its own**, six are 238 KB, and one carrying 20 real commits is ~4 MB — the "kilobytes to a
few MB" §4 predicted. The stage-3 flip was verified by performing the attack: with the mirror mounted
read-write, an in-jail overwrite of `<bare>/refs/heads/main` succeeds; read-only, it is refused, while
the agent still commits, pushes to its own repo, and reaches the merge queue through the daemon.
**Findings covered:** MG-3 (quarantine config does not cover direct writes), MG-17 ("user-namespaced" claimed but no userns remap).
**Related, already fixed:** MG-1 (daemon-side git no longer executes agent-planted hooks/config).

---

## 1. What is actually broken

The merge-safety quarantine rests on two settings applied to the bare mirror at provision time
(`RepoProvisioner.cs:117-118`):

```
receive.denyNonFastForwards = true
receive.denyDeletes         = true
```

Both are enforced **inside `git receive-pack`** — the code path a *push* takes. They are not
filesystem permissions and not repository-wide invariants.

The mirror is bind-mounted into the jail **read-write, at its real VM path**
(`ContainerSpecBuilder.cs:137-139`, `Source == Target == BareRepoPath`, `ReadOnly = false`), and with
no userns remap (MG-17) container uid 1000 **is** the daemon's `mainguard` uid 1000 that owns the
mirror. So the agent can simply write the files:

- overwrite `<bare>/refs/heads/main` (a 41-byte file holding a SHA),
- rewrite `<bare>/packed-refs`,
- add loose objects under `<bare>/objects`,

…none of which invokes `receive-pack`. The deny rules never execute. **The lock is on the front
door; the agent walks through the wall.**

### Severity in practice

`AGENT` reach, High. An agent that wants to land unreviewed code can point `refs/heads/main` at any
commit it likes. The merge queue's guarantees are computed over refs it does not exclusively own.

---

## 2. Why the obvious fix does not work

> "Mount the mirror read-only."

This breaks the product. An agent works in a **linked worktree**
(`<vmRoot>/worktrees/<hash>/<agentId>`) created by `git worktree add` off the mirror. A linked
worktree is not self-contained:

- its `.git` is a *file* pointing back into the bare repo,
- it **shares the bare repo's object database** (`<bare>/objects`),
- its branch ref lives in the bare repo (`refs/heads/agent/<id>`).

So a plain `git commit` inside the worktree **writes objects and a ref into the bare mirror**. Write
access to the bare repo is not only needed to push — it is needed for the agent to commit at all.

A read-only mount therefore has to be paired with somewhere else for agent writes to go. That is the
whole design problem.

---

## 3. Option A — userns-remap alone (MG-17 only). **Insufficient for MG-3.**

Enable `userns-remap` in `daemon.json` and chown the mount sources into the remapped subuid range.

- **Fixes:** the MG-17 claim — container uid 1000 no longer equals the daemon's uid 1000, so a jail
  escape does not immediately hold daemon-owned files.
- **Does not fix MG-3:** for the agent to keep committing, the mirror must still be *writable by the
  remapped uid*. Once chowned into that range the agent can rewrite `refs/heads/main` exactly as
  before — only the numeric owner changed.

**Conclusion:** worth doing for its own sake, but it must not be mistaken for closing MG-3.

---

## 4. Option B — per-agent repo sharing objects via `alternates`. **Recommended.**

Give each agent a repository it fully owns, and make the shared mirror something it can only *read*.

```
<vmRoot>/repos/<hash>.git            <- shared mirror. Mounted READ-ONLY into every jail.
<vmRoot>/agents/<hash>/<agentId>.git <- per-agent repo. Mounted read-write into ONE jail.
```

### This is NOT a clone — storage is not the cost

The obvious objection is "a repo per agent will use an insane amount of disk." It won't, because the
per-agent repo does not copy history. It carries a one-line `objects/info/alternates` pointing at the
mirror's object store, so object lookups resolve straight through to the mirror. The agent's repo
owns only:

- its own `refs/`, `HEAD`, `config`, `index`
- **the new objects it creates** — its own commits, trees, blobs

That is kilobytes to a few MB. The checked-out working files are unchanged from today, since linked
worktrees check files out too.

### Why not just keep worktrees — isn't that what they're for?

Worktrees do share the object store, and that is exactly their purpose. The problem is the *direction*
of the sharing:

| | shares objects | direction |
|---|---|---|
| **linked worktree** (today) | yes | the agent **writes into** the mirror |
| **alternates** | yes | the agent **reads from** the mirror, writes locally |

A linked worktree keeps its branch ref in the mirror and writes new objects into the mirror's
`objects/`. So the sharing mechanism *requires* the jail to hold write access to the mirror — and once
it has that, it can also rewrite `refs/heads/main`. That is MG-3 in one sentence.

Alternates give the identical storage benefit with the write direction reversed: same disk, different
trust boundary. **The storage objection is therefore an argument FOR alternates, not against them.**

### The real cost: `git gc` — **resolved 2026-07-27**

Not disk — object lifetime. If the mirror prunes objects an agent's repo depends on through the
alternate, that agent's repo breaks; git does not track borrowers.

The resolving distinction is that **pruning breaks borrowers; repacking does not.** Repacking
consolidates loose objects into packs and re-deltas them, but every object still exists and still
resolves by SHA, so a borrower is unaffected. Deleting unreachable objects is the only operation that
can pull the floor out from under an alternate. That splits gc into a safe half and an unsafe half:

| operation | safe with agents attached? | reclaims |
|---|---|---|
| repack / consolidate loose objects | **yes** — no object is deleted | most of it: loose-object consolidation + delta compression |
| prune unreachable | **no** | the tail |

So the policy is:

1. **`gc.auto=0` on the mirror.** Git runs `gc --auto` implicitly after many ordinary commands; with
   agents borrowing objects, an implicit prune firing mid-session is exactly the failure mode. Nothing
   in the codebase runs `gc`/`repack` explicitly today (only `worktree prune`), so this is purely
   about suppressing the automatic path.
2. **Repack-without-prune may run at any time**, including with agents attached. This is what keeps
   the mirror from growing without bound, and it is the answer to "what if agents are never all
   stopped at once" — space is reclaimed continuously without ever waiting for an idle window.
3. **Full prune only at a genuine idle point** — no agent attached to that repo. That occurs naturally
   when the last agent tears down, so it needs no new scheduler.
4. **A size guard** so this cannot rot silently: track pack/loose growth per mirror and surface it.
   Unbounded growth should be visible, not discovered at 40 GB.

No locking primitive is required, and there is no stale-lease failure mode.

### Publishing: the agent pushes to its OWN repo, the daemon fetches

Once the mirror is read-only, the agent's work has to reach it somehow. Two shapes were considered —
the agent pushing to the daemon, or the daemon fetching — and the answer is to combine them.

Point the agent's `origin` at **its own repo**, not at the daemon:

- `git push origin` **succeeds**, entirely inside the agent's own writable space. No daemon involved,
  no `receive-pack` on the daemon side, and nothing to validate — it cannot reach the mirror because
  it is not talking to it. LLM CLIs push reflexively; this keeps that working.
- The **daemon fetches** from that repo, naming the ref and destination itself.
- The push doubles as the ready signal: the daemon watches the ref move.

Why this matters: with a push-to-daemon model the agent *proposes* ref updates
(`old-sha new-sha refname`) and the daemon must validate every one — right namespace, ff-only, not a
delete — forever, correctly. With the daemon fetching, **the agent cannot name a ref at all**; the
authorization question is structurally absent rather than enforced by checks. Given MG-3 exists
precisely because a *config-enforced* quarantine turned out not to cover direct writes, preferring
structure over checks is the lesson applied.

Honest caveat: both shapes still parse agent-produced pack data on the daemon side (`index-pack` runs
either way). The difference is authority over ref naming and placement, not pack parsing.

The daemon still enforces, in code rather than config, when it updates the mirror:

- target ref must be `refs/heads/agent/<thatAgentId>` — an agent cannot name another's branch,
- fast-forward only,
- no deletes,
- `main` is never a valid target from an agent.

**Cost:** the mirror mount flips to read-only; worktree creation moves to the per-agent repo; teardown
must remove it; `git gc` on the mirror must respect alternates.

**Payoff:** MG-3 becomes structurally impossible rather than config-enforced, and the
deny-non-ff/deny-delete settings stop being load-bearing.

## 5. Option C — keep the RW mount, watch for tampering

A daemon-side watcher verifies `refs/heads/main` and `packed-refs` against expected values and
alarms/reverts on drift.

- **Cheap**, no architectural change.
- **Detective, not preventive** — there is a window between write and detection, and an agent that
  understands the watcher can race it. Does not satisfy "the jail cannot rewrite main".

Useful only as a stopgap or as defense-in-depth alongside Option B.

---

## 6. Decision — **taken 2026-07-27 (owner)**

This section was a recommendation; it is now the plan of record.

1. **Option B, all three stages.** Stage 3 is the point of the exercise — everything before it is
   refactoring that changes no security posture, so stopping at Stage 2 would leave MG-3 open.
   - *Stage 1* — per-agent repo + alternates; worktrees move to it. Mirror still RW (data path proven,
     reversible).
   - *Stage 2* — daemon-side mediated ref updates with the four rules in §4.
   - *Stage 3* — flip the mirror mount to `ReadOnly = true`. **This is the commit that closes MG-3.**
2. **Option A (userns-remap) is being enabled**, tracked as MG-17, and it lands **before** this work.
   Ordering is deliberate: MG-3 creates new per-agent directories that need correct ownership, and
   with the remap already in place they are created correctly by construction instead of retrofitted.
   Both changes also touch `ContainerSpecBuilder`, so they must not run concurrently.
   Until the remap is actually enabled, the "user-namespaced" claim stays out of the docs.
3. **Option C is not being taken.** It is detective rather than preventive, and Stage 3 supersedes it.

## 7. Resolved questions

- ~~`git gc` vs alternates~~ — **resolved**: prune breaks borrowers, repack does not. `gc.auto=0`,
  repack-without-prune allowed at any time (this is what bounds growth without waiting for an idle
  window), full prune only when no agent is attached, plus a size guard so growth stays visible. No
  locking primitive, no stale-lease failure mode. See §4.
- ~~Per-agent disk~~ — **accepted**. `CoordinatorLimits.MaxActiveWorkers` is 6, and the per-agent repo
  carries only refs/config/index plus its own new objects, so the cost is bounded and small.
- ~~Fetch trigger~~ — **resolved: both.** The daemon watches the agent ref move (responsiveness, and it
  keeps the agent's own `git push` meaningful) **and** re-fetches immediately before verification, so
  the bytes that get verified are definitely current rather than whatever the watcher last saw.
- ~~Push-to-daemon vs daemon-fetch~~ — **resolved**: the agent pushes to its own repo and the daemon
  fetches, which keeps `git push` working while leaving ref naming entirely daemon-side (§4).
