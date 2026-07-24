# MG-3 / MG-17 — Confining agent writes to the bare mirror

**Status:** design proposal, no implementation. Written for review before any code.
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

### The real cost: `git gc`

Not disk — object lifetime. If the mirror repacks or prunes objects an agent's repo depends on
through the alternate, that agent's repo breaks; git does not track borrowers. Either gc is disabled
while agents are attached, or it needs an explicit lock. **This is the genuine engineering cost of
Option B** (see §7).

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

## 6. Recommendation

1. **Option B** as the real fix, staged:
   - *Stage 1* — introduce the per-agent repo + alternates; worktrees move to it. Mirror still RW
     (no security gain yet, but the data path is proven and reversible).
   - *Stage 2* — add the daemon-side mediated ref-update API with the four rules in §4.
   - *Stage 3* — flip the mirror mount to `ReadOnly = true`. **This is the commit that closes MG-3**;
     everything before it is refactoring that can land independently.
2. **Option A (userns-remap)** in parallel, tracked as MG-17 on its own merits — and the
   "user-namespaced" claim stays out of the docs until it is actually enabled.
3. Option C only if a stopgap is wanted before Stage 3 lands.

## 7. Open questions for review

- **`git gc` vs alternates.** Does repack/prune on the mirror while agents hold alternates need an
  explicit lock, or is gc simply disabled while any agent is attached? This is the main unresolved
  design cost of Option B.
- **Per-agent disk.** Alternates make object storage near-zero, but each repo still carries its own
  refs/config/index. Confirm that is acceptable at the expected agent count.
- **Fetch trigger.** The daemon watching the agent ref move is the proposed signal; verification time
  is the natural alternative. Which is preferred?
- ~~Push-to-daemon vs daemon-fetch~~ — **resolved**: the agent pushes to its own repo and the daemon
  fetches, which keeps `git push` working while leaving ref naming entirely daemon-side (§4).
