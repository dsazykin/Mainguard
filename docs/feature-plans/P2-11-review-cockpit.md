# P2-11 — Review Cockpit: Risk-Ranked Diffs, Per-Hunk Provenance, Flagged-Changes Gate — Implementation Plan

**Task ID:** P2-11 · **Milestone:** M7 · **Priority:** P0 — the daily-driver reason to open
Mainguard (review time +91% is the buying trigger).
**Depends on:** P2-10; reuses T-06 `PatchParser`, T-11 blame, T-13 diff stack, T-29 PR→worktree
plumbing.
**Branch:** implement on `feature/P2-11-review-cockpit` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated rule corpora + ViewModel tests + **screenshot testing and human visual approval** on the cockpit.
> Classifier/provenance/flag/ack logic is pure and fixture-tested; the end-to-end poisoned-branch flow is scripted. The cockpit is a P0 daily-driver surface: render-harness PNGs in all five themes + a human review-ergonomics pass (risk ordering readability, chip density, flagged-panel flow per ControlCenterDesign §6) before merge.
>
> **Source of truth:** §P2-11 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`, including
> the two 2026-07-07 extensions (semantic lockfile diff, review-sprint mode) — both are in scope
> for this task unless split out with the owner's agreement; if split, this plan's §3.6/§3.7
> become the follow-up's spec.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-11 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-11** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-11 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Design decisions (binding)** | [`ControlCenterDesign.md`](../design/ControlCenterDesign.md) §6 ("where trust is manufactured") -- risk-ranked hunks, per-hunk provenance chips, the flagged-changes acknowledgment gate, and the test-delta strip are one cockpit surface; state vocabulary + badge family per §9 |
| **Launch-blocker / hardening gates** | **RT-D2 changed-test-command flag + OPS SA-1/F6 out-of-approved-Scope flag** land in this task's `FlaggedChangeDetector` as dedicated must-acknowledge items -- see the 2026-07-12 additions section below |

---

## 0. Context — what exists today

P2-10 tells you *whether* a branch is verified; nothing helps a human review N agent branches
fast. This task is the review surface: order hunks by risk (not alphabetically), attribute every
hunk to an agent/task/plan (Agent Trace first, trailers fallback — Mainguard is the first review UI
to render Agent Trace), and hard-gate merges behind item-by-item acknowledgment of flagged
changes. External PR branches (P2-12) flow through the same cockpit.

### What you can rely on

| Fact | Where |
|---|---|
| `PatchParser` / `FilePatch` / `DiffHunk` models (T-06) | `Mainguard.Agents/Services/PatchParser.cs` |
| Diff rendering stack incl. intra-line + image diffs (T-13) | `Mainguard.Agents/Services/IntraLineDiff.cs`, `Mainguard.App.Shell/ViewModels/DiffViewerViewModel.cs` |
| Blame (T-11) for context jumps | `Mainguard.Agents/Services/BlameCache.cs`, `BlameViewModel` |
| PR→local-worktree checkout plumbing (T-29) | existing checkout service surface |
| `IMergeQueue.CanMerge` composable gate hook (`IMergeGate`) | P2-10 |
| Orchestrator emits worker sessions (will emit Agent Trace + trailers per this task's step 2) | P2-09/P2-14 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Review/RiskClassifier.cs` (pure) |
| **Create** | `Mainguard.Agents/Review/ProvenanceReader.cs` (pure) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/FlaggedChangeDetector.cs` (pure) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/AgentTraceEmitter.cs` (orchestrator-side: session → Agent Trace JSON + `Agent:/Task:/Plan:` trailers) |
| **Create** | `Mainguard.Agents/Review/AcknowledgmentStore.cs` (ack ↔ flagged-hunk-set content hash) |
| **Create** | `Mainguard.Agents/Review/LockfileSemanticDiff.cs` (extension a: parse lockfile deltas → dependency delta + OSV check) |
| **Create** | `Mainguard.App.Shell/ViewModels/ReviewCockpitViewModel.cs` + `Mainguard.App.Shell/Views/ReviewCockpitView.axaml(.cs)` |
| **Create** | `Mainguard.App.Shell/ViewModels/FlaggedChangesPanelViewModel.cs` (+ panel section in the cockpit view) |
| **Create** | `Mainguard.Tests/RiskClassifierTests.cs`, `ProvenanceReaderTests.cs`, `FlaggedChangeDetectorTests.cs`, `AcknowledgmentTests.cs`, `LockfileSemanticDiffTests.cs`, `Mainguard.Tests/Integration/PoisonedBranchGateTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Review/RiskClassifier.cs (pure)
public enum RiskCategory { ExecutableConfig, Lockfile, CiWorkflow, GitHooks, EditorConfig, SecuritySensitivePath, Source, Docs }
public sealed record HunkRisk(RiskCategory Category, int Rank);   // lower rank = review first
public static class RiskClassifier
{
    public static HunkRisk Classify(string filePath, DiffHunk hunk);   // path + content rules
}

// Mainguard.Agents/Review/ProvenanceReader.cs (pure)
public sealed record HunkProvenance(string? Agent, string? Task, string? Plan, string Sha, string Source); // Source: "agent-trace" | "trailer"
public static class ProvenanceReader
{
    public static IReadOnlyList<HunkProvenance> FromAgentTrace(string traceJson);
    public static HunkProvenance? FromTrailers(string commitMessage, string sha);
}

// Mainguard.Agents/Agents/Orchestrator/FlaggedChangeDetector.cs (pure)
public static class FlaggedChangeDetector
{
    public static IReadOnlyList<(string Path, RiskCategory Category)> Detect(IReadOnlyList<FilePatch> mergeDiff);
}
```

UI (binding): risk-ordered files/hunks; provenance gutter chip per hunk (agent · task · plan);
**"bring this branch local"** action (fetch agent branch → local worktree via T-29 — Sculptor
Pairing-style hand-back); flagged-changes panel acknowledged **item-by-item** before `CanMerge`
consults it; a test-delta strip (what P2-10's verification newly covers/failed vs main).

---

## 3. Implementation steps

### 3.1 `RiskClassifier` rules (step 1)

Path rules: lockfiles (`package-lock.json`, `pnpm-lock.yaml`, `*.lock`), `.github/workflows/` →
`CiWorkflow`, `.git/hooks`-installing paths + `husky` config → `GitHooks`, `.vscode/`/editor
config → `EditorConfig`, security-sensitive heuristics (`auth/`, `crypto/`, `*Security*`,
`*Credential*`) → `SecuritySensitivePath`, docs extensions → `Docs`, default `Source`.
**Content rule:** `package.json` — hunk touching the `"scripts"` block → `ExecutableConfig`; a
dependency-version-only hunk → `Lockfile` (edge rows 1–2 hinge on this distinction). Renamed
files classify by **new path + content** (edge row 4). Rank table lives beside the enum
(ExecutableConfig=0 … Docs=7).

### 3.2 Provenance: emit + read (step 2)

- `AgentTraceEmitter` (orchestrator side): every worker session emits **Agent Trace** records —
  the Cognition/Cursor interchange JSON mapping file/line ranges to contributors (agent, task,
  plan, session) — stored per branch in the daemon artifact dir; **and** writes
  `Agent:`/`Task:`/`Plan:` commit trailers via the adapter's commit path as the durable
  in-history fallback.
- `ProvenanceReader.FromAgentTrace` parses trace JSON → per-range `HunkProvenance`
  (`Source="agent-trace"`); `FromTrailers` parses the three trailers from a commit message
  (`Source="trailer"`); tolerant of missing/unknown fields (human commits → null, no crash —
  edge row 3).
- Hunk mapping: join trace line ranges to `DiffHunk` new-file ranges; trailers attribute
  whole-commit hunks (blame the hunk's commits via T-11 to map hunk → commit).

### 3.3 `FlaggedChangeDetector` + acknowledgments (steps 3–4)

- Detect = classify the full merge diff; categories `{ExecutableConfig, CiWorkflow, GitHooks,
  SecuritySensitivePath}` (+ Lockfile when the semantic diff flags it — §3.6) are flag-worthy.
- `AcknowledgmentStore`: an ack binds to **SHA-256 of the canonical flagged-hunk set** (paths +
  hunk content hashes). New push → new hash → all acks invalid (edge row 5, invariant 2).
  Each item acknowledged individually (a single global checkbox is a rejection trigger);
  acknowledgment events recorded (P2-15 chains them later: `acknowledged_flagged_change`).
- Wire into P2-10: an `IMergeGate` implementation — `Allows == all flagged items acked for the
  current hash`.

### 3.3a Out-of-approved-Scope detection (OPS SA-1 / F6 — 2026-07-12 addition)

For a **managed** worker, `FlaggedChangeDetector` also compares the produced diff's touched
paths against the worker's approved `TaskPlan.Scope` (P2-14): any file **outside** the approved
Scope is a **dedicated must-acknowledge flagged item** (`out-of-approved-scope`, same mechanism
as the RT-D2 changed-test-command flag) that blocks `CanMerge` until acknowledged. This closes
the gap where a Coordinator `send_worker_prompt` re-steers a running worker off its approved
plan and the review surface showed only a diff with no scope-violation signal — plan approval
binds to a Scope, so a silent merge of off-Scope work is not allowed. (Plan-less manual-mode
runs skip the scope comparison — mirror P2-35's Diff Guard rule.)

### 3.3b RT-D2 changed-test-command flag (M7 exit — surfaced here, recorded by P2-10)

P2-10's `VerificationRecord` carries `ResolvedCommand` + `ConfigHash`; when either differs from
the `main`-side baseline, the cockpit renders a **dedicated must-acknowledge flagged item**
(`changed-test-command`) in the flagged panel — before it is acknowledged, `CanMerge` is false.
A branch that rewrites its test to `exit 0` cannot ride a silent merge
(`GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge` — the queue-side half lives in P2-10's
suite; the panel/gate half is asserted here).

### 3.4 Cockpit view (step 3)

Compose existing controls: T-13 diff rendering inside a file/hunk list **ordered by risk rank**
(ordering only — nothing hidden, invariant 3); provenance gutter chips; the flagged panel;
test-delta strip fed from the two latest `VerificationRecord`s (new failures/new passes vs main);
"bring this branch local" button → T-29 fetch-into-worktree flow. Async loads off the UI thread;
design tokens/component classes; all five themes.

### 3.5 Test-delta strip

Parse the verification log artifacts (P2-10) for test results (start with the .NET TRX/xUnit
output the verification command produces when available; fall back to pass/fail-only). Delta =
current branch run vs latest main-baseline run. Keep the parser pure + fixture-tested.

### 3.6 Extension (a) — semantic manifest/lockfile diff

`LockfileSemanticDiff.Parse(oldText, newText, kind)` for `package-lock.json`, `pnpm-lock.yaml`,
`*.csproj` `PackageReference`, `poetry.lock` → `DependencyDelta` rows (added/updated/removed,
version jump, install-scripts present, maintainer/registry change where the format carries it) +
a **local OSV database** CVE lookup (offline snapshot shipped/updated; no network at review
time). Renders instead of the raw 9,000-line lockfile hunk; script-bearing or CVE-hit rows feed
the flagged gate.

### 3.7 Extension (b) — review-sprint mode

Timed keyboard-only pass over ranked hunks (j/k next/prev, a acknowledge, space mark-viewed)
with a per-session risk budget; deferred hunks recorded as **unviewed** for the P2-38 coverage
map (emit the viewed-state events now; P2-38 consumes them).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| dependency bump only | flags `Lockfile`, not `ExecutableConfig` |
| script added to `package.json` | flags `ExecutableConfig`; acknowledgment required |
| commit without trailers (human commit) | provenance chip absent, no crash, rank still applies |
| renamed file with risky content | classified by new path + content |
| acknowledgment then diff changes (new push) | acknowledgments reset (they bind to a diff hash) |
| **F6:** managed worker's diff touches a file outside the approved `TaskPlan.Scope` | dedicated `out-of-approved-scope` must-acknowledge item; `CanMerge` false until acked |
| **F6:** plan-less manual-mode run | scope comparison skipped; other rules apply |
| **RT-D2:** branch changed the verification command/config vs the main baseline | dedicated `changed-test-command` must-acknowledge item; `CanMerge` false until acked |

---

## 5. Invariants (MUST)

1. Classifier/detector/reader are pure and fixture-tested; **UI contains no rule logic**.
2. Acknowledgments bind to the content hash of the flagged hunk set — any change invalidates them.
3. Risk ordering never hides hunks (ordering only; everything remains reachable).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Classifier_FixtureCorpus` | every category + the scripts-vs-bump distinction + rename-by-new-path |
| 2 | `Trailer_ParseMatrix` | all three trailers / partial / none / malformed → correct nullable fields |
| 3 | `AgentTrace_ParseAndRangeJoin` | fixture trace JSON → hunk provenance mapping; unknown fields tolerated |
| 4 | `Ack_InvalidationOnHashChange` | ack set → new hunk content → all invalid; unrelated-file change → hash unchanged case documented |
| 5 | `Ack_ItemByItem_GateComposition` | gate false until every item acked; events emitted |
| 6 | `LockfileSemanticDiff_Fixtures` | per-format delta extraction; scripts-present + OSV-hit flags |
| 7 | `TestDelta_ParserFixtures` | TRX/xUnit log → new-fail/new-pass delta |
| 8 | `PoisonedBranch_EndToEnd` | poisoned `postinstall` branch → flagged panel appears → merge blocked until acknowledged (extends the P2-10 canary) |
| 9 | `OutOfScopeDiff_ShouldBeDedicatedFlaggedItem` (**OPS SA-1/F6**) | fixture managed worker with approved Scope `[src/a/**]` producing a diff touching `src/b/x.cs` → `out-of-approved-scope` item; gate false until acked; plan-less run → no scope item |
| 10 | `ChangedTestCommand_ShouldBlockCanMergeUntilAcked` (**RT-D2 panel half**) | fixture `VerificationRecord` with `ResolvedCommand`/`ConfigHash` differing from baseline → `changed-test-command` item rendered; gate false until acked |
| 11 | `ReviewSprint_DeferredHunksRecordedUnviewed` | sprint session defers hunks → unviewed events emitted for the P2-38 coverage map (TI-P2-11.12) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** rules implemented in XAML/code-behind; acknowledgment as a single global checkbox;
provenance reader network calls; hidden hunks.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~RiskClassifier|FullyQualifiedName~Provenance|FullyQualifiedName~FlaggedChange|FullyQualifiedName~Acknowledgment|FullyQualifiedName~LockfileSemanticDiff|FullyQualifiedName~PoisonedBranch"
grep -rn "RiskCategory" Mainguard.App.Shell/Views/            # rendering only — no rule logic in XAML/code-behind
```

---

## 8. Definition of done

- [ ] Pure classifier/reader/detector per contract; Agent Trace emitted by the orchestrator + trailers fallback.
- [ ] Cockpit: risk ordering, provenance chips, flagged panel with hash-bound item acks wired into `CanMerge`, test-delta strip, bring-branch-local — one surface per ControlCenterDesign §6, badges/states per §9.
- [ ] F6 out-of-approved-Scope items + RT-D2 changed-test-command items flow through the same must-acknowledge mechanism (guard tests 9–10 green — RT-D2 is an M7 exit criterion with P2-10).
- [ ] Semantic lockfile diff + OSV flags; review-sprint mode emitting viewed-state events.
- [ ] All edge rows + poisoned-branch end-to-end green; test contract = union of §6 and TI-P2-11.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-11**, base `phase2`.
