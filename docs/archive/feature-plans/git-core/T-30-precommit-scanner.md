# T-30 — Pre-Commit Safety Scanner — Implementation Plan

**Priority:** P1 · **Depends on:** nothing external (pure/local). **Fully offline-verifiable — no host account.**
> Everything here is machine-verifiable: pure analyzers with pinned findings + a service over local fixtures +
> a staging-area UI. Nothing deferred except a subjective "does the warning read well" glance.

## 0. Why
The most expensive git mistakes are committing a **secret**, a huge binary, or leftover **merge markers**.
Scan the staged change *before* the commit lands and warn (with the option to proceed) — a safety net that is
also the guardrail an autonomous agent needs before it commits.

## 1. Contract
```csharp
// Mainguard.Agents/Safety/PreCommitFinding.cs
namespace Mainguard.Agents.Safety;

public enum FindingSeverity { Info, Warning, Blocker }
public enum FindingKind { Secret, LargeFile, MergeMarker, DebugLeftover, ManyFiles, Other }

public sealed class PreCommitFinding
{
    public FindingKind Kind { get; init; }
    public FindingSeverity Severity { get; init; }
    public string Path { get; init; } = "";
    public int? Line { get; init; }
    public string Message { get; init; } = "";   // human message; MUST NOT echo the secret value
    public string Rule { get; init; } = "";       // e.g. "aws-access-key-id"
}
```
```csharp
// Mainguard.Agents/Safety/SecretPatterns.cs (pure) — named regexes: AWS access key id/secret, GitHub token
// (ghp_/gho_/ghs_/github_pat_), Google API key, Slack token, private key blocks (-----BEGIN ... PRIVATE KEY-----),
// generic "high-entropy assignment" (api_key/secret/password = "…"), JWT. Each returns a match WITHOUT the value.
// Mainguard.Agents/Safety/PreCommitScanEngine.cs (pure): given IEnumerable<(string Path, string Content, bool IsBinary, long SizeBytes)>
//   → IReadOnlyList<PreCommitFinding>. Rules: Secret (regex, redacted message), LargeFile (> threshold, default 5 MB, configurable),
//   MergeMarker (^<<<<<<< / ^======= / ^>>>>>>> lines), DebugLeftover (optional: TODO-FIXME? keep minimal + off by default),
//   ManyFiles (> N files, warning). Deterministic ordering.

// Mainguard.Agents/Services/IPreCommitScanner.cs
public interface IPreCommitScanner
{
    IReadOnlyList<PreCommitFinding> ScanStaged(string repoPath);   // reads the staged tree via ExecuteWithRepo; runs the pure engine
}
```

## 2. Implementation
1. **Pure `SecretPatterns` + `PreCommitScanEngine`** — no IO; the whole detection surface is here so findings
   are unit-pinned. **A finding's `Message` MUST NEVER contain the matched secret** (redact to a rule name +
   `path:line`). Thresholds via `UserPreferences` (e.g. `PreCommitMaxFileMB`, `PreCommitScanEnabled`).
2. **`PreCommitScanner`** (Core): enumerate **staged** entries via `IGitService.ExecuteWithRepo` (index →
   added/modified blobs), read blob text (skip/flag binaries by size only), feed the engine. No network.
3. **UI.** In the staging/commit area: on demand (and optionally auto before commit if enabled), show a
   **findings panel/banner** — grouped by severity, each with kind icon, `path:line`, message, and a
   **"reveal in diff"** jump. A **Blocker** (secret / merge marker) shows a prominent warning with
   **"Commit anyway"** (explicit override) vs **"Cancel"**; Warnings are advisory. A settings toggle enables
   auto-scan-before-commit. Severity colors from Danger/Warning/Info tokens (no raw colors).

## 3. Edge cases / invariants
- **A finding never leaks the secret value** (message is rule + location only) — assert in tests (grep the produced messages for a planted secret → absent).
- Binary files: flagged only by size (LargeFile), never scanned for secrets/markers as text.
- Clean staged tree → no findings, panel shows "all clear".
- Merge markers only at line start (`^<<<<<<<` etc.), not mid-line, to avoid false positives on e.g. diff docs. (A file legitimately documenting markers is an acceptable false positive; keep the override.)
- Deterministic finding order (by severity then path then line).
- All git via `ExecuteWithRepo`; Core UI-free; scanning is fast (cap huge files — size check before reading text).

## 4. Test contract (offline)
- Pure `SecretPatterns`: each named rule matches a planted sample and the match/message **excludes** the value; no false positive on innocuous text.
- Pure `PreCommitScanEngine`: pinned findings for a mixed input (a secret, a merge-marker file, an oversized file, a many-files case, a clean file) — exact kinds/severities/paths/lines; order deterministic; **no secret in any message**.
- `PreCommitScanner` over a **local fixture** (`RequiresGitCli`): stage a file with an AWS key + one with merge markers + a big blob → findings match; a clean stage → none.
- VM gating: scan populates grouped findings; "commit anyway" overrides a blocker; enable/disable toggle.
- Headless render: the findings panel (blocker + warning + info) + the all-clear state.

## 5. Definition of done
- [ ] Pure `SecretPatterns` + `PreCommitScanEngine` (findings pinned, secret never echoed) + `IPreCommitScanner`/impl over the staged tree.
- [ ] Staging-area findings panel (grouped, reveal-in-diff, commit-anyway override) + a settings toggle (auto-scan-before-commit).
- [ ] `UserPreferences` thresholds (max file MB, enabled). Migration only if an entity changes (prefs are JSON → none).
- [ ] Offline tests green (patterns + engine pinned + scanner over a fixture + VM + render), incl. the secret-never-leaks assertion. One PR linking **T-30**.
