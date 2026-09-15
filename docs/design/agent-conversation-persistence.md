# Persisting an agent's conversation across its jail

**Status:** implemented (unit-verified; the RequiresDocker leg is CI-verified — see §8).
**Related:** [`resume-stranded-queue-entry.md`](resume-stranded-queue-entry.md) (the feature this
completes), [`agent-cli-settings-persistence.md`](agent-cli-settings-persistence.md) (the sibling
declaration this deliberately does **not** copy), [`agent-branch-confinement.md`](agent-branch-confinement.md)
(MG-3, whose writable-mount discipline this inherits).

## 1. The state this exists for

From the owner, during live testing:

> *"i think i managed to resume an agent's session, but i cant access the previous claude code
> conversation"*

Both halves are true. [Resume](resume-stranded-queue-entry.md) worked exactly as designed: the stranded
entry got a live jail, standing on its own `agent/<id>` branch, with its commits intact. And the CLI in
that jail came up with no memory of any of it.

The mechanism is one line of the sandbox spec. The jail's `$HOME` is `/home/agent`
(`ContainerSpecBuilder.AgentHome`), mounted as a **256 MiB tmpfs**. Claude Code keeps its conversation
transcripts under `$HOME/.claude/`. So the transcripts live in RAM inside the container and are destroyed
with it. `AgentResumeService` adopts the branch and the agent id but spawns a **new** container — the
continuity a resume exists to provide is precisely what was being lost.

## 2. The decision that matters: a bind mount, not a harvest

This repository already has a mechanism for carrying CLI state across a teardown. `credentialPaths` (and
its sibling `settingsPaths`) are **harvested on stop** by `SandboxAgentLauncher.HarvestCliCredentialsAsync`
and **restored on spawn**. It is the obvious pattern to reach for here, and it is the wrong one — not
heavier, not slower: it cannot work for this problem at all.

The event that makes an operator need the conversation back is the jail dying **without a clean stop**: a
VM crash, a `docker rm`, a WSL restart. That is the literal definition of a *stranded queue entry*, which
is what resume exists to recover. Harvest runs inside `AgentSpawnService.StopAsync`. **In the crash case
`StopAsync` never runs.**

So a harvest-based conversation store would:

- pass every test anyone wrote for it — because every such test stops the agent, and
- fail in every situation a user actually hits.

That is this codebase's signature defect (a control that looks applied and is not), and here it was
visible in advance. It is refused on purpose, and the refusal is written into the code at the mount site
(`ContainerSpecBuilder.BuildMounts`) rather than only here, because that is where somebody would later be
tempted to "simplify" it into a copy.

**The design is therefore a bind mount.** A per-`(repoHash, agentId)` directory on daemon-owned ext4 is
mounted read-write into the jail *at the CLI's own state path*, so the CLI writes its transcripts straight
onto disk that outlives the container. Nothing runs at teardown because nothing has to be copied. A crash
loses nothing.

### Why the mount lines up across jails

The transcript directory is keyed by the CLI's working directory — Claude Code writes
`$HOME/.claude/projects/<escaped-cwd>/<session-uuid>.jsonl`, where `<escaped-cwd>` is the absolute path
with `/` and `.` replaced by `-`. Every Mainguard jail's `WorkingDir` is the fixed `/workspace`
(`ContainerSpecBuilder.WorkspaceTarget`), so that directory is `-workspace` in **every** jail for **every**
agent. A re-spawned CLI looks in exactly the directory the dead jail wrote, which is what makes a remount
sufficient where a copy would otherwise be required.

## 3. The declaration

A third field in the adapter manifest, alongside `credentialPaths` and `settingsPaths`:

```jsonc
"conversationPaths": [".claude/projects"],   // $HOME-relative DIRECTORIES holding conversation state
"resumeArgs": ["--continue"]                 // argv appended to `launch` to re-enter the conversation
```

Both are carried across the host/VM boundary on the install marker (`InstalledAdapterMarker`), because the
daemon spawns from the marker, not from the manifest.

**What is declared, and what is deliberately not.** Verified 2026-08-14 against a live Claude Code install
(2.1.228) rather than against documentation:

| path | decision | why |
|---|---|---|
| `.claude/projects` | **declared** | the transcripts — `<escaped-cwd>/<session-uuid>.jsonl`, confirmed on disk |
| `.claude/todos` | not declared | **does not exist in this build** (it writes `tasks/`, `plans/`, `sessions/`). It may have existed in older releases; declaring it would mount a directory the CLI never writes — persisting nothing while looking configured |
| `.claude` | **refused** (§4) | contains `.credentials.json` |
| `.claude/history.jsonl` | not declared | cross-project prompt history, mode 600; out of scope and credential-adjacent |
| codex / gemini-cli / qwen-code / opencode | **nothing declared** | nobody has verified where they keep session state on the pinned versions |

The four empty declarations are the honest answer, not an incomplete one. An empty declaration means "this
CLI gets no persistence yet"; a *guessed* path would mean "this CLI silently persists nothing", which is
strictly worse because it looks done. Adding one is a small, reviewable change once somebody verifies the
layout against the pinned version — the same discipline `baseUrlEnvVar` already follows.

## 4. The invariant: a conversation path may never reach a credential

This is the one rule that makes the feature safe to ship, and it is a **typed refusal**, not a comment and
not a doc note.

The owner's standing rule is that logins and tokens live only in the host OS keychain and are transferred
into the jail at boot — nothing is stored agent-side. A conversation store is daemon-owned ext4 that
deliberately **outlives** the jail, so a credential landing in it would be a token written to plain disk,
in a tree whose entire purpose is to survive teardown, remounted into every later jail for that agent id.

The specific accident is not hypothetical: `.claude` is where the transcripts live, **and** it contains
`.claude/.credentials.json` (mode 600). A maintainer debugging a missing transcript would reach for exactly
that directory.

So the rule is **prefix containment, in both directions**, whole-segment:

- a conversation path may not equal a credential path,
- may not *contain* one (`.claude` ⊃ `.claude/.credentials.json`),
- may not be *contained by* one,
- while a sibling sharing a prefix is fine (`.claude-backup` is not inside `.claude`).

It is enforced at **two** points, by one implementation (`ConversationStorePolicy.AssertNoCredentialOverlap`):

1. `AdapterManifest.Parse` — the manifest does not parse, so a bad edit fails CI rather than a user's install.
2. `ConversationStoreManager.Prepare`, on the spawn path — a typed `ConversationStoreOverlapException`
   before any directory is created and before any container exists.

The second is not redundant. The manifest is reviewed product source, but the daemon spawns from an
**install marker**: an ordinary JSON file in a VM path, possibly written by an older build. A gate that
only reads the manifest is a check on a file the decision does not depend on.

**Refused, not filtered.** Dropping the offending path and continuing would leave the feature looking
configured while persisting a subset nobody chose, and would leave the wrong declaration in place.

## 5. Where the store lives, and the guards on it

```
<vmRoot>/conversations/<repoHash>/<agentId>/<declared path>/…
```

A sibling of `repos/`, `worktrees/`, `agents/` and `caches/`, provisioned by
`UsernsRemapPolicy.MountOwnershipScript` with the same MG-17 group share, and following the
`PackageCachePolicy` precedent closely — including its `PackageCacheGrant` decision, which is reused
rather than copied because the grant is a property of the *machine* (did the boot step create the jail
group?), not of the tree.

Two facts are re-asserted **fail-closed** in `ContainerSpecBuilder.AssertConversationStores`, on the
finished create request:

- **the store's SOURCE is not inside `$HOME`.** The *target* is under `$HOME` by necessity — that is where
  the CLI reads it, and unlike a package cache (which is *told* where it is through the environment) a
  CLI's transcript directory is not configurable. So the only thing that makes the feature real is that
  the source is not: a source under the tmpfs would be the very thing whose destruction this exists to
  survive, and nothing would look wrong until somebody came back for the history.
- **the store's SOURCE is not inside the worktree** (neither `/workspace` nor this request's own ext4
  worktree path). A store in there puts the operator's entire session in the tree the agent commits from —
  one `git add -A` away from being merged to main. The MG-43 package cache was moved out of the worktree
  for exactly this reason.

Plus the MG-3 structural guard the package cache already carries: a read-write mount of daemon-side state
may only ever name a path inside a `conversations/` tree, so this mount can never be edited into a second
writable path at the mirror or the per-agent git dir.

**And it is proven inside the container.** `DockerSandboxEngine.AssertConversationStoresUsableAsync` runs a
sentinel-framed probe in the started jail and fails the spawn if any store is missing or unwritable. This
is fail-closed for a specific reason: because the target sits under the tmpfs `$HOME`, a mount that did not
take produces **no error at all** — the CLI writes to the tmpfs, the whole session works, and the loss is
discovered later by whoever came back for the conversation. The probe is the one moment the difference is
observable.

## 6. Putting the operator back in the conversation

A persisted transcript nobody opens does not answer the report. On a **resume**, the launcher appends the
adapter's declared `resumeArgs` to the CLI's launch argv. Two conditions, neither sufficient alone:

- **the adopt path only.** An ordinary spawn is new work on a new branch and must start clean, or a fresh
  agent is dropped into a stranger's session.
- **a transcript must actually exist** in this agent's store. Asked by looking for **files**, not for the
  directory — `Prepare` creates the directory on every spawn including the first, so a directory-existence
  guard would be permanently true, i.e. no guard at all.

The second condition is deliberately conservative. Passing a resume flag with no prior session is a worse
failure than not passing it. **Measured:** `claude --continue -p …` in a directory with no prior transcript
exits 0 and starts a fresh session (2.1.228, on the maintainer's machine). But that was *print* mode, and
the jail runs the CLI **interactively under a PTY** (`AgentCliBinder`), which was **not** measured. The
guard therefore stands on the evidence available rather than on the mode that could not be tested. If the
interactive behaviour is later measured to be equally benign, the guard can be relaxed — the reverse
mistake (a CLI that exits at spawn, leaving a dead terminal with nothing saying why) is not recoverable by
the operator.

An adapter with no `resumeArgs` still gets its transcripts mounted; nothing is invented to "use" them.
Absent is a statement, exactly as with `baseUrlEnvVar`.

## 7. Lifecycle: when the store goes

**A clean `StopAgent` takes the conversation with the branch.** `WorktreeManager.RemoveAgentWorktree` — the
teardown path that ends in `branch -D` — releases the store, immediately after it releases the package
cache.

The argument is the `branch -D` itself. A clean stop deletes the branch, the worktree and the agent's own
repository, so the work the conversation is *about* no longer exists. Keeping the transcript would not
preserve continuity; it would build a trap. **Agent ids are unique per repo, not globally**, and the
external-PR intake's `pr-<n>` ids *recur* — a later `pr-7` for a different pull request would mount, and
resume into, the previous author's session. That is both a wrong answer and a disclosure of one PR
author's session to the next. (The `(repo, agent)` scoping trap has been fixed four separate times in this
codebase; the store is keyed on the pair everywhere, including here.)

**`RemoveAgentWorktreeKeepingBranch` deliberately does NOT release it.** That is the resume rollback: it
exists to preserve `agent/<id>` across a failed or repeated resume, and the conversation is the other half
of what a resume restores. Releasing there would delete the one copy of the session on exactly the path
invoked to keep the work.

This mirrors the precedent in `AgentSpawnService.StopAsync`, where id-keyed state (the leader registry,
the IPC endpoint, the terminal lock, the gateway credential) is released only once no session anywhere
still answers to that id. The store's key is the **pair**, so it is released with that pair's branch.

### Untrusted jails

Unlike `settingsPaths`, the store is **not** gated by `withoutHostCredentials`, and it does not need to
be. That flag exists because settings and logins flow *between* agents — an external pull request's jail
must not inherit the user's approvals or tokens. A conversation store is per `(repo, agentId)` and mounted
into exactly one jail, so an untrusted worker can only ever see the transcripts it wrote itself, and they
are released with its branch on the clean stop that ends the intake. There is nothing to inherit, so there
is nothing to gate.

### Stated gap: no budget, no eviction

Unlike `PackageCacheManager`, there is no size ceiling and no LRU eviction, and that is a decision rather
than an omission: a package cache is derived content in gigabytes that a re-download rebuilds, while a
transcript is a few hundred kilobytes of JSONL that nothing can rebuild. Evicting one to reclaim space
would delete the only copy of the thing the feature exists to keep. The consequence is that a store
orphaned by a crashed daemon is residue until that agent is torn down cleanly. If this ever matters in the
field, the answer is an age-based sweep of stores with no live session and no queue entry — not a byte
budget.

## 8. What was verified, and what was not

**Verified locally (unit, no Docker):**

- the overlap refusal, including the `.claude` containment case in both directions
  (`AdapterConversationPathsTests`, 16 tests);
- the mounts on the **real built container spec** — source not under `$HOME`, not under `/workspace`, not
  under the worktree; target at the CLI's own path; read-write; refusals for a non-`conversations/` source
  and a drvfs source (`ContainerSpecConversationStoreTests`, 13 tests);
- the store's layout, `(repo, agent)` scoping, idempotence across respawn, the files-not-directories
  resume guard, and the release semantics (`ConversationStoreManagerTests`, 15 tests);
- the probe parser, including "no frame is a failure, never a pass" (`ConversationStoreProbeTests`, 10 tests);
- the resume decision and the mounts against the **daemon's own** `AgentSpawnService`
  (`ConversationResumeWiringTests`, 8 tests);
- the shipped Claude Code layout, read off a live 2.1.228 install.

**NOT verified locally:** the RequiresDocker leg. Docker is not reachable from the maintainer's WSL distro,
so `ConversationPersistenceDockerTests` **skipped** (3 tests) rather than running. It is written and
compiles, and it is PR-blocking in Linux CI. It models the crash, not the stop: the container is removed
through the engine and the session record dropped, with no `StopAgent` anywhere, then the transcript is
asserted on the daemon's disk and read back inside the resumed jail.

**One assumption it is there to check.** The store's mount target is a *deeper path underneath the tmpfs
`$HOME`*. Container engines apply mounts parent-first, so the bind lands on top of the tmpfs and the deeper
path is real ext4 — but that interaction was reasoned about, not measured here. If it turns out not to hold
on the shipping engine, the in-jail probe turns it into a loud, named spawn failure rather than a silent
loss of history, and the fallback is to mount the store at a fixed path outside `$HOME` and symlink the
CLI's directory to it.
