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

## 4. Option B — per-agent object/ref namespace, daemon reconciles. **Recommended.**

Give each agent a repository it fully owns, and make the shared mirror something it can only *read*.

```
<vmRoot>/repos/<hash>.git          <- shared mirror. Mounted READ-ONLY into every jail.
<vmRoot>/agents/<hash>/<agentId>.git <- per-agent repo. Mounted read-write into ONE jail.
```

- The per-agent repo is created with the mirror as an **alternate** (`objects/info/alternates`) so
  existing objects are read without copying — cheap, no full clone.
- The agent's worktree is created from the per-agent repo, so `commit` writes objects and refs
  **into the agent's own repo**. Nothing it can write is shared.
- To publish, the agent pushes to the daemon (or the daemon fetches on a trigger). Either way the
  daemon performs the ref update in the mirror, where it can enforce, **in real code rather than
  config**:
  - target ref must match `refs/heads/agent/<thisAgentId>` — an agent cannot name another's branch,
  - fast-forward only,
  - no deletes,
  - `main` is never a valid target from an agent.

**Cost:** the mirror mount flips to read-only; worktree creation moves to the per-agent repo;
teardown must remove the per-agent repo; `git gc` on the mirror must respect alternates (or be
disabled while agents are attached).

**Payoff:** MG-3 becomes structurally impossible rather than config-enforced, and the deny rules stop
being load-bearing. MG-4's "the jail cannot reach shared state" story gets simpler too.

---

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

- Does `git gc`/repack on the mirror while agents hold alternates need an explicit lock, or is
  gc simply disabled while any agent is attached?
- Push-to-daemon vs daemon-fetch for publishing: push keeps the agent's mental model (`git push
  origin`), fetch avoids running `receive-pack` against agent-influenced input at all.
- Is per-agent disk cost acceptable? Alternates make it near-zero for objects, but each repo still
  carries its own refs/config/index.
