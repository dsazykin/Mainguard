# P2-06 — Repo Provisioner: the Git-Native Sync Boundary — Implementation Plan

**Task ID:** P2-06 · **Milestone:** M6 · **Priority:** P0 — the data path every agent depends on.
**Depends on:** P2-02 (daemon + `RepoSyncService` stubs), P2-05 (`MainguardEnv` running).
**Branch:** implement on `feature/P2-06-repo-provisioner` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated on the Linux CI leg (integration on `DualRepoFixture`); no human step.
> Provisioning, worktrees, quarantine remotes, and the byte-identical round-trip are all real-git integration tests; the Windows-VM boundary is simulated with a second directory on CI exactly as TI-P2-00 specifies.
>
> **Source of truth:** §P2-06 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`, including the
> 2026-07-07 **quarantine-remotes extension**. Global invariant **G-11: no Windows-path bind
> mounts into containers, ever** — Git objects (fetch/push) are the only cross-boundary repo data
> path.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-06 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-06** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-06 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

The daemon (P2-02) exposes `RepoSyncService` RPCs as typed `UNIMPLEMENTED` stubs. This task bodies
them: a bare ext4 mirror of the user's Windows repo inside `MainguardEnv`, per-agent worktrees off
that mirror, and the Windows-side `mainguard-vm` remote so a human can fetch agent branches. The
whole agent platform moves repo data **only** as Git objects across this boundary.

### What you can rely on

| Fact | Where |
|---|---|
| `RepoSyncService`: `ProvisionRepo`, `CreateWorktree`, `ListWorktrees`, `RemoveWorktree` stubs | `Mainguard.Server/Services/RepoSyncGrpcService.cs` (P2-02) |
| Hardened git CLI runner (`RunGit` family: arg lists, env-token auth, redaction, typed failures) — **compile the same family into the daemon**; do not write a new runner | `Mainguard.Agents/Services/GitServices.cs` |
| Windows-side remote management `AddRemote` (idempotent registration path exists) | `Mainguard.Agents/Services/GitServices.cs` / `IGitService` |
| `ExecuteWithRepo` discipline for LibGit2Sharp on the Windows side | `Mainguard.Agents/Services/GitServices.cs` |
| Worktree porcelain parsing (T-07) for `git worktree list --porcelain` | `Mainguard.Agents/Services/WorktreePorcelainParser.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/RepoProvisioner.cs` (`IRepoProvisioner` + impl; daemon-side) |
| **Create** | `Mainguard.Agents/Agents/WorktreeManager.cs` (`IAgentWorktreeManager` + impl; daemon-side) |
| **Create** | `Mainguard.Agents/Agents/RepoPathHasher.cs` (pure: normalized Windows path → SHA-256 hex) |
| **Edit** | `Mainguard.Server/Services/RepoSyncGrpcService.cs` (stubs → real bodies; validation/dispatch only) |
| **Edit** | `Mainguard.App.Shell` project-open path — register the `mainguard-vm` remote idempotently (via existing `AddRemote`) |
| **Create** | `Mainguard.Tests/RepoProvisionerTests.cs`, `AgentWorktreeManagerTests.cs`, `RepoPathHasherTests.cs`, `VmRoundTripTests.cs` (Linux CI tagged) |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// daemon-side Mainguard.Agents/Agents/RepoProvisioner.cs
public sealed record ProvisionResult(string RepoHash, string BareRepoPath, string VmRemoteUrl);
public interface IRepoProvisioner
{
    ProvisionResult Provision(string windowsRepoPathNormalized);   // clone-or-fetch the ext4 bare mirror
}
// daemon-side WorktreeManager.cs
public interface IAgentWorktreeManager
{
    string CreateAgentWorktree(string repoHash, string agentId);   // branch agent/<id> from main + worktree
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);
    void Prune(string repoHash);
}
```

Windows side: on project open, register the daemon-owned quarantine **sync remote** — its *role*
is fixed (the one host-side remote that fetches agent branches) but its *name* is the resolved
`SyncRemote.Name` from **`IAgentEnvironment.ResolveSyncRemote(repoHash)`** (ESC B1 decision SC-2,
`docs/phase-2/Mainguard_Environment_Substrate_Contract.md`), **defaulting to `mainguard-vm` on the
WSL2 substrate** (`mainguard-cloud` on cloud — P2-25) →
`\\wsl.localhost\MainguardEnv\home\<user>\mainguard\repos\<hash>.git` (idempotent; via existing
`AddRemote`, registering whatever `SyncRemote.Name` resolves to — **never a hardcoded literal**,
so P2-10's foreground merge and the P2-25 cloud path stay substrate-agnostic).

---

## 3. Implementation steps

### 3.1 Path hashing + layout (step 1)

- Normalization before hashing: absolute path, backslashes → single form, trailing separator
  stripped, **case-folded** (NTFS is case-insensitive — `C:\Repo` and `c:\repo` must map to one
  mirror). `RepoPathHasher.Hash(path)` = lowercase hex SHA-256 of the normalized UTF-8 string.
- Layout inside the VM: bare mirrors `~/mainguard/repos/<hash>.git`; worktrees
  `~/mainguard/worktrees/<hash>/<agentId>` on branch `agent/<agentId>`.
- First provision: `git clone --bare /mnt/c/<...> ~/mainguard/repos/<hash>.git` — **9P is acceptable
  for object transfer only**; file *watching* over 9P is what's forbidden. Subsequent calls:
  `git fetch` in the bare repo (edge row 1: measurably incremental, no re-clone).
- `core.untrackedCache=true` set in the bare template/config.

### 3.2 Git execution (step 3)

All git inside the daemon goes through the F2 runner compiled into the daemon — the same `RunGit`
family (arg-list spawning, typed nonzero-exit failures, output redaction). Move/share the runner
into a Core namespace both heads can reference if it currently sits app-side; **a second runner
implementation is a rejection trigger** (same rule as the existing "one audited transport per
host").

### 3.3 Worktrees (step 2)

- `CreateAgentWorktree`: refuse (typed) if `agent/<agentId>` or the worktree path already exists
  (edge row 3); else `git worktree add -b agent/<id> <path> main` in the bare mirror.
- `RemoveAgentWorktree(force:false)` on a dirty worktree → typed failure; `force:true` →
  `git worktree remove --force` + `git branch -D agent/<id>` (branch deletion stays with P2-09
  teardown if that ordering is cleaner — but `RemoveAgentWorktree(force:true)` must leave no
  worktree residue either way).
- `Prune` → `git worktree prune` + report.
- **pnpm:** post-worktree, when `pnpm-lock.yaml` exists at the worktree root, run `pnpm install`
  (content-addressable store → N agents ≈ 1× disk). Non-fatal on failure: surface a warning
  in the provision result path — agents can still run; do **not** fail the worktree create.

### 3.4 Quarantine remotes (extension — binding)

Each agent worktree's `origin` is the daemon-owned bare repo and **only** that:

- After `worktree add`: `git remote remove origin` if inherited, then
  `git remote add origin <bareRepoPath>` (local path). `git push origin` from inside the sandbox
  always works — agent UX intact.
- **No agent container ever holds credentials for, or a route to, the user's real remote.**
  Promotion to the real remote happens exclusively on the Windows side via the verified pipeline
  (P2-10/P2-12). A prompt-injected `git push --force origin main` is structurally impossible, not
  merely firewalled. (Also protect the mirror: set `receive.denyNonFastForwards=true` +
  `receive.denyDeletes=true` on the bare repo so a hostile agent can't rewrite `main` in the
  mirror either — agent pushes target `agent/*` refs.)
- Test asserts the sandbox worktree's configured remotes == exactly `{origin → bare path}`.

### 3.5 gRPC + Windows side (step 5)

- `RepoSyncGrpcService` maps RPCs → `IRepoProvisioner`/`IAgentWorktreeManager`; returns opaque
  handles (`RepoHash`, agent ids) — daemon filesystem paths never cross to the client except the
  UNC remote URL, which is Windows-facing data, not a daemon path (G-14).
- App project-open hook: if the repo has been provisioned (daemon reachable + `ProvisionRepo`
  returns), register the **SC-2-resolved sync remote** (`IAgentEnvironment.ResolveSyncRemote(repoHash)`
  → `mainguard-vm` on WSL2) → the UNC bare path via existing `AddRemote`; idempotent (skip when
  present with the same URL, update when hash changed). The literal `mainguard-vm` appears only in
  the WSL2 substrate's `ResolveSyncRemote` implementation — nowhere else (rejection trigger).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| second provision of the same repo | incremental fetch, no re-clone (test measures: objects dir mtime/clone marker, or timing) |
| Windows repo path with spaces/Unicode | hash + UNC registration correct (fixture `C:\repos\Ünï cödé repo`) |
| worktree add on an already-used agent id | typed failure |
| bare repo manually deleted | next provision re-clones cleanly |
| `RemoveAgentWorktree(force:false)` on a dirty worktree | typed failure; `force:true` succeeds |

---

## 5. Invariants (MUST)

1. **G-11:** no container ever mounts a Windows path — the ext4 worktree is the only mount source
   (asserted in P2-07's `docker inspect` test; the plumbing that makes it possible lands here).
2. An agent commit in the VM worktree reaches the Windows repo **byte-identically** via
   `git fetch <SyncRemote.Name> && git merge agent/<id>` (the resolved name — `mainguard-vm` on
   WSL2, per SC-2; round-trip test: commit SHA equality).
3. Provisioner and worktree manager are daemon services with **no UI dependencies**.
4. Agent worktree remotes: exactly the quarantine `origin`; never the user's real remote or its
   credentials.

---

## 6. Test contract (Linux CI; fixture repos both sides)

| # | Test | Assertion |
|---|---|---|
| 1 | `Provision_CreatesBareMirror` | fresh path → `<hash>.git` exists, is bare, `untrackedCache` set |
| 2 | `Provision_SecondRun_IncrementalFetch` | marker file in objects dir survives; new commit on source arrives via fetch |
| 3 | `Hasher_NormalizesCaseSlashesTrailing` | `C:\Repo\`, `c:/repo` → same hash; Unicode path stable |
| 4 | `Worktree_AddRemovePrune_RoundTrip` | create → listed (porcelain parser) → remove → pruned; branch `agent/<id>` lifecycle correct |
| 5 | `Worktree_DuplicateAgentId_Throws` | typed failure, no residue |
| 6 | `Worktree_DirtyRemove_ForceSemantics` | force:false throws typed; force:true cleans |
| 7 | `QuarantineRemote_IsOnlyRemote` | worktree remotes == `{origin: <barePath>}`; bare repo denies non-FF/deletes |
| 8 | `WindowsVm_CommitRoundTrip` | commit in worktree → `fetch` from a stand-in "Windows" clone → identical SHA + tree |
| 9 | `Provision_PathWithSpaces` | end-to-end with spaces/Unicode in the source path |
| 10 | `Pnpm_InstallFailure_NonFatal` | poisoned lockfile → worktree still created, warning surfaced |
| 11 | `SyncRemote_NameIsResolvedNotHardcoded` (**SC-2**) | registration + round-trip use `ResolveSyncRemote(repoHash)`; a fake substrate returning `mainguard-cloud` registers that name — no `mainguard-vm` literal on the call path |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** any bind mount of `/mnt/c` into agent-visible paths; worktrees on the Windows
filesystem "temporarily"; a second git-runner implementation; real-remote URLs or credentials
reaching worktree config; **a hardcoded `mainguard-vm` literal outside the WSL2
`ResolveSyncRemote` implementation** (SC-2 — the P2-25 cloud substrate resolves
`mainguard-cloud` through the same seam).

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~RepoProvisioner|FullyQualifiedName~AgentWorktree|FullyQualifiedName~RepoPathHasher"
grep -rn "/mnt/c" Mainguard.Agents/Agents/ | grep -v "clone --bare\|Provision"   # object-transfer path only
grep -rn "token_\|Authorization" Mainguard.Agents/Agents/RepoProvisioner.cs Mainguard.Agents/Agents/WorktreeManager.cs  # 0 hits
```

---

## 8. Definition of done

- [ ] `IRepoProvisioner`/`IAgentWorktreeManager` exactly per contract; hasher normalization; pnpm hook.
- [ ] Quarantine remotes with non-FF/delete denial on the mirror; remotes test green.
- [ ] `RepoSyncService` bodies replace stubs; Windows sync-remote registration idempotent and **SC-2-resolved** (never a hardcoded literal).
- [ ] Round-trip invariant proven (SHA-identical); all edge rows tested on Linux CI.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-06**, base `phase2`.
