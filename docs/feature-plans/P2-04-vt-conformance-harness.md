# P2-04 — VT Conformance & Replay Harness — Implementation Plan

**Task ID:** P2-04 · **Milestone:** M6 · **Priority:** P0 — starts alongside P2-03 and **gates**
P2-03 and P2-18.
**Depends on:** P2-03 interfaces (`ITerminalView`, the grid-readback hook).
**Branch:** implement on `feature/P2-04-vt-conformance-harness` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — the harness IS the deliverable; deterministic replays, no human step.
> Golden transcripts are byte-order-only replays compared cell-by-cell; regeneration must be byte-identical locally. Any timing dependence is a rejection trigger. The allowlist-only-shrinks rule is a CI diff check.
>
> **Source of truth:** §P2-04 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`, which binds
> the v1-strategy §G-7.1c contract verbatim. The harness **is** the deliverable — it must run
> red/green on the interim engine with the allowlist checked in.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-04 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-04** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-04 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-03 lands an interim vendored renderer; P2-18 replaces it with a server-side libvterm grid
engine. Both must be provably correct against real-world TUI byte streams, and the swap must be
provably non-regressing. This harness is the proof: scripted conformance suites + recorded
golden transcripts replayed deterministically, driving **both** engines through one abstraction.

### What you can rely on

| Fact | Where |
|---|---|
| `ITerminalView` + test-only grid-readback hook ("feed bytes → read grid") | P2-03, `Mainguard.App.Shell/Controls/TerminalControl.cs` (`InternalsVisibleTo("Mainguard.Tests")`) |
| `VtBoundaryDetector` (byte-safe chunking already tested separately) | `Mainguard.Agents/Terminal/VtBoundaryDetector.cs` |
| Headless Avalonia test harness (TI-00) for control-level tests | `Mainguard.Tests/` |
| Docker test wrapper for a reproducible Linux environment (where `vttest`/`esctest` binaries run) | `docker-compose.yml` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Tests/Terminal/ITerminalEngineHarness.cs` (feed bytes → read grid abstraction) |
| **Create** | `Mainguard.Tests/Terminal/InterimEngineHarness.cs` (adapts P2-03's control via the readback hook) |
| **Create** | `Mainguard.Tests/Terminal/VtConformanceTests.cs` (vttest/esctest scripted runs) |
| **Create** | `Mainguard.Tests/Terminal/TranscriptReplayTests.cs` |
| **Create** | `Mainguard.Tests/Terminal/known-failures.txt` (allowlist, checked in) |
| **Create** | `Mainguard.Tests/Transcripts/` — recorded byte streams + committed goldens: `claude-code.bytes/.golden`, `opencode.bytes/.golden`, `vim.bytes/.golden`, `htop-60s.bytes/.golden`, `tmux.bytes/.golden` |
| **Create** | `Mainguard.Tests/Terminal/TranscriptRecorder.cs` + a small recording entry point (dev tool to (re)capture transcripts through a PTY) |
| **Create** | CI guard: `.github/workflows/ci.yml` step — allowlist may only shrink (diff check vs base) |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (binding)

1. **Conformance:** `vttest`/`esctest` scripted headless against the engine with a checked-in
   **known-failures allowlist**; progress is monotonic — the allowlist only ever shrinks.
2. **Golden transcripts** under `Mainguard.Tests/Transcripts/` (Claude Code, OpenCode, vim,
   htop 60 s, tmux), replayed **byte-order-only** (no timing dependence) and compared
   **cell-by-cell** against committed goldens.
3. **Required coverage matrix** — alternate screen, DEC 2026 synchronized output, truecolor,
   CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks.
4. The harness drives **both** engines through the "feed bytes → read grid" abstraction; P2-03's
   control gains a test-only grid-readback hook (already specified there); P2-18 registers a second
   `ITerminalEngineHarness` implementation and must pass the same suites.

---

## 3. Implementation steps

### 3.1 The engine abstraction

```csharp
// Mainguard.Tests/Terminal/ITerminalEngineHarness.cs
public interface ITerminalEngineHarness
{
    void Reset(int cols, int rows);
    void Feed(ReadOnlySpan<byte> bytes);
    GridSnapshot ReadGrid();     // cells: text (grapheme), fg, bg, attrs, width; cursor pos; alt-screen flag
}
```

`GridSnapshot` is the comparison currency: implement `Equals`/diff-formatting that prints a
readable cell-level diff (row, col, expected vs actual) — golden failures must be diagnosable from
CI output alone.

### 3.2 Conformance runs (vttest/esctest)

- Ship the test scripts' **byte scripts**, not interactive runs: pre-record vttest menu-driven
  sequences (the standard approach: drive vttest in a PTY once, capture its output per test page,
  store as fixtures) and esctest's expected-response assertions where they are pure-output.
- Each conformance case = feed fixture bytes → compare grid against the case's expected grid (or
  known invariant, e.g. cursor position).
- `known-failures.txt`: one case-id per line + a comment. The test runner treats listed cases as
  expected-fail (they assert *failure* — an allowlisted case that starts passing fails the suite
  with "remove it from the allowlist", keeping the list honest and shrink-only).
- CI diff check: on PRs, `git diff origin/phase2 -- Mainguard.Tests/Terminal/known-failures.txt`
  must show no added lines (removals fine). Wholesale golden regeneration without justification is
  a rejection trigger.

### 3.3 Transcript recording + replay

- `TranscriptRecorder`: runs a command in a PTY (P2-03 shim), captures the raw output byte stream
  to `<name>.bytes` with **no timestamps in the comparison path** (a sidecar timing file is
  permitted for humans, never read by tests).
- Recording session (done once by the implementer, files committed): Claude Code (a short scripted
  interaction), OpenCode, `vim` (open file, edit, quit), `htop` for 60 s, `tmux` (split, switch,
  exit). Trim to keep each file reasonable; goldens are the final grid state (plus, for htop,
  intermediate checkpoints every N bytes to catch mid-stream corruption).
- Replay test: `Reset(cols,rows)` → `Feed(all bytes)` (optionally chunked through
  `VtBoundaryDetector` at randomized-but-seeded offsets to double as an integration check) →
  `ReadGrid()` == committed golden, cell-by-cell.
- **Determinism invariant:** regenerating any golden locally is byte-identical — the golden
  writer normalizes anything non-deterministic (no timestamps, fixed cols×rows per transcript,
  fixed TERM). A `--regen` mode (env var `MAINGUARD_REGEN_GOLDENS=1`) rewrites goldens; the test
  fails if regen output differs from committed while regen mode is off.

### 3.4 Coverage matrix tests

Hand-written focused fixtures (byte sequences + expected grids) for each required row:

| Area | Fixture asserts |
|---|---|
| alternate screen | `smcup`/`rmcup` switches; primary screen content restored on exit |
| DEC 2026 sync output | `CSI ?2026h/l` — no partial frame rendered between markers |
| truecolor | `38;2;r;g;b` / `48;2;...` land in cell fg/bg exactly |
| CJK/emoji width | `你`, wide emoji, ZWJ family occupy 2 cells; narrow fallback correct |
| bracketed paste | mode toggles tracked; paste wrapped `200~`/`201~` on input side |
| mouse reporting | `?1000h`/`?1006h` (SGR) → input encoder emits correct sequences for clicks |
| OSC 8 hyperlinks | link id/uri attached to cells; both `BEL` and `ST` terminators |

Input-side rows (paste, mouse) drive the engine's input encoder (the P2-03 control's key/mouse
mapping) rather than `Feed`.

---

## 4. Invariants (MUST)

1. Regenerating any golden locally is **byte-identical** (determinism).
2. The allowlist only ever **shrinks** (CI diff check enforces).
3. No test reads timing data — replays are byte-order-only.
4. Both engines run the same suites through `ITerminalEngineHarness` (P2-18 adds its adapter and
   changes nothing else).

---

## 5. Rejection triggers

- Timing-dependent replays (sleeps, wall-clock assertions).
- Goldens regenerated wholesale in a PR without justification.
- Allowlist additions.
- Harness coupled to vendored-renderer types (breaks the P2-18 reuse).

---

## 6. Test contract

The harness **is** the deliverable:

| # | Deliverable test | Green criterion |
|---|---|---|
| 1 | `VtConformance_Vttest_*` | every non-allowlisted case passes on the interim engine |
| 2 | `VtConformance_AllowlistedStillFail` | allowlisted cases still fail (shrink-only honesty) |
| 3 | `TranscriptReplay_{ClaudeCode,OpenCode,Vim,Htop,Tmux}` | final grid == golden, cell-by-cell |
| 4 | `TranscriptReplay_ChunkedFeeds_Identical` | seeded random chunking → same grid as one-shot feed |
| 5 | `CoverageMatrix_*` (7 areas) | per-area fixture assertions |
| 6 | `Goldens_RegenIsByteIdentical` | regen in a temp dir == committed files |

CI: Linux job runs everything; the allowlist-shrink diff guard runs on every PR touching
`Mainguard.Tests/Terminal/`.

---

## 7. Reviewer script

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~VtConformance|FullyQualifiedName~TranscriptReplay|FullyQualifiedName~CoverageMatrix"
git diff origin/phase2 -- Mainguard.Tests/Terminal/known-failures.txt | grep '^+' | grep -v '^+++' # empty
grep -rn "Thread.Sleep\|Task.Delay" Mainguard.Tests/Terminal/   # 0 hits in comparison paths
```

---

## 8. Definition of done

- [ ] `ITerminalEngineHarness` + interim-engine adapter; readable cell-diff output.
- [ ] vttest/esctest scripted suites + checked-in shrink-only allowlist + CI guard.
- [ ] Five recorded transcripts + goldens; byte-order-only replay; regen determinism test.
- [ ] Full coverage matrix (7 areas) green.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-04**, base `phase2`.
