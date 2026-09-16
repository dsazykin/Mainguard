# T-29 — Check Out a PR / Branch Into a Worktree — Implementation Plan

**Priority:** P1 · **Depends on:** T-07 (worktree porcelain), T-23 (PR list). Reuses `RunGitChecked*` + the
worktree service. **Mostly LOCAL git mechanics → largely offline-verifiable with a fixture remote.**
> **Offline slice:** the fetch-ref + worktree-create mechanics against a **local file:// fixture remote** that
> carries a synthetic `refs/pull/N/head` (fully verifiable), plus branch→worktree. **Only the round-trip
> against a real GitHub remote DEFERS** (same mechanics, real network).

## 0. Why
Reviewing a PR's code means fetching it and switching branches, clobbering your working state. "Check out
locally" fetches a PR head (or any remote/local branch) into a **separate worktree** so you can build/run/
review it without disturbing your current checkout — power-user gold, and the seam an agent uses to test a PR.

## 1. Contract
```csharp
// Add to IGitService (CLI-driven, reuse RunGitChecked + the T-07 worktree add):

/// <summary>The conventional fetch ref for a host PR head. GitHub: pull/{n}/head. Pure/testable.</summary>
static string PullRequestHeadRef(HostKind host, int number);   // or a small PrRefResolver helper class

/// <summary>Fetch a PR head from the remote into a local branch, then create a worktree checked out to it.
/// Returns the created worktree path. Idempotent-ish: a typed error if the target dir is non-empty.</summary>
Task<string> CheckoutPullRequestWorktree(string repoPath, int prNumber, string remoteName, string worktreePath, CancellationToken ct);

/// <summary>Create a worktree checked out to an existing (local or remote-tracking) branch.</summary>
string CheckoutBranchWorktree(string repoPath, string branchOrRef, string worktreePath);
```

## 2. Implementation
1. **Pure ref resolver** — `PullRequestHeadRef(HostKind.GitHub, n)` → `pull/{n}/head`; GitLab →
   `merge-requests/{n}/head`; others → typed "not supported" (or best-effort). Unit-testable, no IO.
2. **`CheckoutPullRequestWorktree`** (CLI via `RunGitChecked`): `git fetch <remote> <headRef>:pr/<n>`
   (creates/updates local branch `pr/<n>`), then reuse the **T-07 `AddWorktree`** to create a worktree at
   `pr/<n>` in `worktreePath`. Non-empty target dir → typed `GitOperationException`. All via the CLI path
   (worktree API is a locked no); network only in the `fetch`.
3. **`CheckoutBranchWorktree`** — reuse `AddWorktree(repoPath, worktreePath, branch)`; for a remote-tracking
   branch, create the local tracking branch first if needed.
4. **UI.**
   - In the **PR panel** (T-23): a per-row **"Check out locally"** action → a folder pick (default a sibling
     `../<repo>-pr-<n>`), runs `CheckoutPullRequestWorktree`, then offers **"Open worktree"** (open that path
     as a repo, reusing the T-16 open-as-repo route).
   - In the **branch browser** / worktree panel (T-21): **"Check out in new worktree"** for a branch.
   - Off-UI-thread, `IsBusy`-gated, typed errors surfaced; journaled via T-19 where it creates refs.

## 3. Edge cases / invariants
- All git via `RunGitChecked`/`ExecuteWithRepo`; no ad-hoc handles.
- Non-empty target dir → typed refusal, nothing created.
- Re-checkout of the same PR (branch `pr/<n>` exists) → update the branch + reuse/refresh, typed-clear behavior (document: refuse if a worktree already there, or fast-forward).
- Detached/odd remote → typed error; never leaves a half-made worktree (best-effort cleanup on failure).
- No secret in argv/URL (auth uses the existing authenticated fetch path where a private remote needs it).

## 4. Test contract (offline, local fixture remote)
- Pure `PullRequestHeadRef` per host.
- Integration over a **local file:// fixture** (`RequiresGitCli`): a bare remote with a synthetic
  `refs/pull/1/head` → `CheckoutPullRequestWorktree` creates branch `pr/1` + a worktree whose HEAD matches the
  PR commit and whose files match; non-empty-target refusal; re-checkout behavior. Plus `CheckoutBranchWorktree`
  over a local branch and a remote-tracking branch.
- VM gating: "check out locally" disabled while IsBusy; typed error surfaces; open-worktree routes.
- Headless render (optional): the PR row action / the worktree-created confirmation.
- **Manual (deferred):** against a real GitHub PR — check out locally, open the worktree, confirm it's the PR's code; private-repo fetch uses the token without leaking it.

## 5. Definition of done
- [ ] Pure `PullRequestHeadRef` + `CheckoutPullRequestWorktree` (CLI fetch + T-07 worktree) + `CheckoutBranchWorktree`.
- [ ] PR-row "Check out locally" + branch "Check out in new worktree" + open-worktree, graceful typed errors.
- [ ] Local-fixture integration tests (fetch synthetic pull ref → worktree matches) + pure ref tests + VM gating green.
- [ ] Live GitHub round-trip deferred with a TODO + manual checklist. One PR linking **T-29**.
