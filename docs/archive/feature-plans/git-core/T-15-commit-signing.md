# T-15 — Commit & Tag Signing (GPG / SSH) — Implementation Plan

**Task ID:** T-15 · **Milestone:** M4 (audit 2.7) · **Priority:** P1 · **Depends on:** T-05 (tag creation path).
**Branch:** `plan/T-15-commit-signing` → implement on `feat/T-15-commit-signing` off `main`.

> **Source of truth:** §T-15 of the Master Doc (contract summary), §TI-15 of the Test Strategy.

---

## 0. Context

No signing today (`grep SignCommits` → 0 hits). Commits go through LibGit2Sharp `repo.Commit`. Signing is a
git-orchestration concern (gpg/ssh, agents, pinentry), so when signing is enabled the commit/tag path
**switches to the git CLI** and lets git drive signing from repo config. Verification badges come from
`git log --format=%G?`.

### What you can rely on

| Fact | Where |
|---|---|
| `RunGitChecked` inherits `GIT_TERMINAL_PROMPT=0` (a bad key can't hang on pinentry — it fails fast) | `GitServices.cs:741`, `:626` |
| Unsigned commit path = LibGit2Sharp `repo.Commit(msg, sig, sig)` via `GetSignature` | `GitServices.cs:320` |
| `CreateTag` (T-05) — annotated tags; signed variant is `tag -s` | `feat/T-05` |
| `UserPreferences` + `SettingsService` for the new signing prefs | `Models/UserPreferences.cs` |
| Commit-row VM (`CommitRowViewModel` / timeline) for verification badges | `CommitTimelineViewModel` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Edit** | `Models/UserPreferences.cs` — `bool SignCommits`, `string GpgFormat` (`openpgp`/`ssh`), `string SigningKey`, `string? GpgProgram` |
| **Edit** | `GitServices.cs` — signed branch in `Commit` and in `CreateTag`; batch verification read |
| **Create** | `Mainguard.Agents/Services/SignatureStatusParser.cs` (pure `%G?` mapping) |
| **Edit** | commit-row VM — verification badge + tooltip |
| **Create/Edit** | Preferences → signing page (on/off, format, key picker, gpg program) |
| **Create** | `Mainguard.Tests/GitServiceSigningTests.cs` (`RequiresGpg`), `SignatureStatusParserTests.cs` (pure) |

---

## 2. Implementation

### 2.1 Signed commit/tag path

```csharp
// In Commit(...): when preferences.SignCommits is on, let git orchestrate signing from repo config
// (commit.gpgsign, user.signingkey, gpg.format). Otherwise keep the LibGit2Sharp path.
if (_prefs().SignCommits)
    RunGitChecked(repoPath, "commit", "-m", message);   // git reads config; pinentry gated by GIT_TERMINAL_PROMPT=0
else
    ExecuteWithRepo(repoPath, repo => repo.Commit(message, GetSignature(repo), GetSignature(repo)));
```

Same branch for `CreateTag`: signed annotated tag → `RunGitChecked(repoPath, "tag", "-s", "-m", message,
name, targetSha)` when signing is on. Signing config (`user.signingkey`, `gpg.format`, `commit.gpgsign`) is
written to **local** repo config from the preferences when the user sets a key (never global).

### 2.2 Verification badges

Batch-read once for the visible commit range:

```csharp
var (code, outp, _) = RunGit(repoPath, "log", "--format=%H %G? %GS", range);
// %G? codes: G good, B bad, U good-unknown-validity, X expired, Y expired-key, R revoked-key, E cannot-check, N none
```

Map with the pure `SignatureStatusParser` → a badge + tooltip on `CommitRowViewModel`. **Only fetch `%G?`
when the signing/verification column is enabled** (no `%G?` cost when off).

### 2.3 Preferences

Signing on/off; format (`openpgp`/`ssh`); key picker (`gpg --list-secret-keys --keyid-format long` for GPG,
`~/.ssh/*.pub` for SSH); optional `gpg.program` override.

---

## 3. Invariants / Test contract — TI-15

**MUST:** signing failures surface **typed** and never hang (`GIT_TERMINAL_PROMPT=0` inherited); unsigned
repos show no badges and pay no `%G?` cost when the column is off.

`GitServiceSigningTests.cs` (`RequiresGpg`, skip when gpg absent):
1. `Commit_WithSigningOn_ShouldProduceVerifiableSignature` — fixture generates a throwaway key
   (`gpg --batch --gen-key`), sets repo config, commits → `git verify-commit HEAD` exit 0; `%G?` == `G`.
2. `Commit_WithSigningOff_ShouldShowStatusN`.
3. `SignatureStatusParser_ShouldMapAllCodes` (pure: G/B/U/N/E rows from canned `log --format` output).
4. `SigningFailure_ShouldThrowTyped_NotHang` — bogus `user.signingkey`; command returns within timeout with
   `GitOperationException`.

---

## 4. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Signing|FullyQualifiedName~SignatureStatus"
```

- [x] Signing prefs; signed `Commit`/`CreateTag` via git CLI; unsigned path unchanged.
- [x] Pure `SignatureStatusParser`; batched `%G?` badges only when column on.
- [x] Signing config written to **local** repo config only.
- [x] TI-15 gpg-gated tests + pure parser green; failure path proven non-hanging. One PR linking **T-15**.
```
