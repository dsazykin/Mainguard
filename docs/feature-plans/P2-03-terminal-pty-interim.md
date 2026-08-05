# P2-03 — Terminal Engine (Interim): PTY Shim + Vendored Renderer — Implementation Plan

**Task ID:** P2-03 · **Milestone:** M6 · **Priority:** P0
**Depends on:** P2-02 (daemon + `TerminalService` bidi stream). **Gated by:** P2-04 from day one.
**Branch:** implement on `feature/P2-03-terminal-pty-interim` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated core + render-harness screenshots; **human terminal-feel approval required**.
> The detector/streamer/PTY layers are 100% unit/integration-testable (split-at-every-offset corpus, cat echo, isatty probe). But interactive terminal *feel* (latency, reflow, scroll behavior under vim/htop) is explicitly a manual matrix per v1 A.6 — a human must drive real TUIs and sign off in the PR.
>
> **Source of truth:** §P2-03 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`. Contract,
> invariants, and edge-case matrix below are binding. Any PR touching terminal code must run the
> P2-04 harness (global PR rule 3).

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-03 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-03** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-03 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

No terminal exists anywhere in the product. Agents are interactive CLI processes (Claude Code,
OpenCode, vim under them, etc.) that demand a real PTY — `isatty()` must be true, resize must work,
Ctrl+C must interrupt. This task ships the **interim** engine: a PTY shim (daemon side), a
byte-safe streaming layer over the P2-02 `TerminalService`, and a vendored Avalonia renderer behind
an interface so P2-18 (libvterm server-side engine) can swap in **without ViewModel changes** —
that seam is the whole point of the design.

### What you can rely on

| Fact | Where |
|---|---|
| `TerminalService.Attach(agentId)` bidi stream; output frame `oneof { bytes raw; GridUpdate grid; }` | P2-02, `Mainguard.Protos/protos/mainguard/v1/terminal.proto` |
| Daemon host + auth + logging mask | `Mainguard.Server/` (P2-02) |
| ViewModels/Views paired via `ViewLocator`; CommunityToolkit.Mvvm | `Mainguard.App.Shell/` |
| Design tokens + component classes; five themes | `Mainguard.App.Shell/Themes/*.axaml`, `App.axaml` |
| xUnit + headless Avalonia harness | `Mainguard.Tests/` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/PtyProcessShim.cs` (`PtySession` + `PtyProcessShim`) |
| **Create** | `Mainguard.Agents/Terminal/ITerminalView.cs` |
| **Create** | `Mainguard.Agents/Terminal/VtBoundaryDetector.cs` (pure) |
| **Create** | `Mainguard.Server/Terminal/TerminalStreamer.cs` |
| **Edit** | `Mainguard.Server/Services/TerminalGrpcService.cs` (wire `Attach` to PTY sessions via the streamer) |
| **Create** | `external/Iciclecreek.Avalonia.Terminal/` (vendored, license file retained) |
| **Create** | `Mainguard.App.Shell/Controls/TerminalControl.cs` (adapter: vendored renderer behind `ITerminalView`, + test-only grid-readback hook for P2-04) |
| **Create** | `Mainguard.App.Shell/ViewModels/TerminalViewModel.cs` + `Mainguard.App.Shell/Views/TerminalView.axaml(.cs)` |
| **Edit** | `Mainguard.slnx` if the vendored code is a separate project |
| **Create** | `Mainguard.Tests/VtBoundaryDetectorTests.cs`, `PtySessionTests.cs`, `TerminalStreamerTests.cs`, `TerminalScrollbackTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Agents/PtyProcessShim.cs
public sealed class PtySession : IDisposable
{
    public Stream IO { get; }
    public void Resize(int cols, int rows);
    public void Kill();
    public Task<int> ExitCode { get; }
}
public static class PtyProcessShim
{
    public static PtySession Spawn(string command, IReadOnlyList<string> args, string cwd,
        IReadOnlyDictionary<string, string> env, int cols, int rows);
}

// Mainguard.Agents/Terminal/ITerminalView.cs
public interface ITerminalView
{
    void FeedOutput(ReadOnlyMemory<byte> data);
    event Action<byte[]>? InputAvailable;
    void Resize(int cols, int rows);
    object GetStateSnapshot();
    void RestoreState(object snapshot);
}

// Mainguard.Agents/Terminal/VtBoundaryDetector.cs (pure)
public sealed class VtBoundaryDetector
{
    /// <summary>Returns the largest prefix length of <paramref name="buffer"/> that ends on a
    /// VT-sequence and UTF-8 codepoint boundary; bytes beyond it are held for the next frame.</summary>
    public int SafeFlushLength(ReadOnlySpan<byte> buffer);
}
```

Daemon side: `TerminalStreamer` — PTY bytes pooled, flushed every **16 ms** as one gRPC `raw`
frame, never splitting a VT sequence or UTF-8 codepoint (holdback cap **4 KB**, then flush
regardless).

---

## 3. Implementation steps (ordered; build after each)

### 3.1 PTY shim over `Porta.Pty` (step 1)

- ConPTY on Windows (the `--local-dev` loop), forkpty on Linux (daemon). One `Spawn` API; the
  platform split lives inside the shim.
- `cwd` is **locked to the agent worktree** — the shim takes what it is given; callers (P2-09)
  guarantee it is a worktree path. No shell wrapping (`command` + `args` exec'd directly — never
  `cmd.exe`/`sh -c`; a `cmd.exe` shell is a global rejection trigger).
- `env` is the complete environment (caller composes it; secrets arrive via the P2-07 tmpfs
  mechanism, never argv).
- `ExitCode` task completes on process exit; `Kill()` → SIGKILL/`ClosePseudoConsole`; `Dispose`
  is idempotent and reaps the child.

### 3.2 `VtBoundaryDetector` (step 2 — the correctness heart)

State machine over the byte stream: `Ground / Esc / Csi / Osc / Dcs / Ss3` **plus UTF-8
continuation counting** (expected-continuation-bytes counter). Rules:

- In `Ground` with no pending UTF-8 continuations → everything so far is flushable.
- `ESC` enters `Esc`; `[` → `Csi` (terminated by a final byte `0x40–0x7E`); `]` → `Osc`
  (terminated by `BEL` **or** `ESC \` — both terminators required by the P2-04 corpus); `P` → `Dcs`
  (terminated by `ST`); `O` → `Ss3` (one following byte).
- A UTF-8 lead byte sets the continuation counter (1–3); each continuation byte decrements; the
  codepoint is flushable only at zero.
- The class is **pure** (no allocation beyond locals, no state retained between calls beyond what
  the caller passes — hold the carry in the streamer, or make the detector instance stateful but
  deterministic; either way `SafeFlushLength` returns a prefix length and never mutates the input).

### 3.3 `TerminalStreamer` (step 3)

- Reads `PtySession.IO` into `ArrayPool<byte>` buffers.
- A 16 ms ticker flushes: run `SafeFlushLength` over `carry + new bytes`; send the safe prefix as
  **one** gRPC `raw` frame; retain the tail as carry.
- Carry cap 4 KB: a malformed endless escape must not buffer unboundedly — at the cap, flush
  regardless (edge row 2).
- Pooled buffers returned on every path (fault included); memory stays flat under
  `yes | head -c 100M` (edge row 3).

### 3.4 Vendored renderer (step 4)

Vendor `Iciclecreek.Avalonia.Terminal` into `external/` with its license file retained. Adapt it
behind `ITerminalView` in `TerminalControl`:

- `FeedOutput` marshals to the UI thread (`Dispatcher.UIThread.Post`) and feeds the vendored
  parser.
- `GetStateSnapshot`/`RestoreState` wrap whatever screen+scrollback state the vendored control
  exposes (opaque `object` — the interface must not leak vendored types, or the P2-18 swap breaks).
- Add the **test-only grid-readback hook** (`internal` + `InternalsVisibleTo("Mainguard.Tests")`)
  that P2-04 needs: "feed bytes → read grid" — cell text, fg/bg, width attributes.

### 3.5 `TerminalViewModel` + `TerminalView` (step 5)

- Keystrokes → `InputAvailable` bytes (UTF-8 encode; **Ctrl+C sends `0x03`**, Ctrl+D `0x04`;
  arrows/function keys send their VT sequences — delegate to the vendored control's key mapping if
  sound).
- Resize: control measure changes → `ITerminalView.Resize` → VM → daemon `Resize` message on the
  `Attach` stream → `PtySession.Resize` (SIGWINCH). Debounce ~50 ms.
- Rendering: 60 FPS dirty-flag invalidation (only invalidate on new frames; no timer-driven
  redraws when idle).
- Scrollback: **10k-line circular buffer** — memory-capped, oldest lines dropped.
- No terminal logic in code-behind; the VM talks only to `ITerminalView` + the daemon stream
  (renderer APIs leaking into ViewModels is a rejection trigger).
- Colors/fonts through design tokens where the control chrome is concerned (the terminal cell
  palette itself maps ANSI colors; expose it as theme resources so Daylight Loom stays readable).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| frame boundary lands mid-CSI / mid-OSC / mid-emoji | detector holds the tail; reassembly byte-identical |
| malformed endless escape | 4 KB holdback cap flushes anyway |
| `yes \| head -c 100M` | memory flat (pooled buffers + scrollback cap) |
| Ctrl+C | 0x03 reaches the PTY; foreground process interrupts |
| resize while streaming | no torn frames, TUI reflows |

---

## 5. Invariants (MUST)

1. `isatty()` is true inside the PTY (probe test — spawn `test -t 0`/Python `sys.stdin.isatty()`).
2. Detector is pure and exhaustively tested: **every fixture sequence split at every byte offset**.
3. No terminal logic in code-behind; the renderer sits behind `ITerminalView` so P2-18 swaps
   engines without ViewModel changes.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Detector_SplitAtEveryOffset_Corpus` | corpus: CSI SGR (`ESC[1;31m`), OSC 8 hyperlink with **both** terminators, DCS, 2/3/4-byte UTF-8, ZWJ emoji (👩‍👩‍👧‍👦) — for each sequence, for **every** split offset, `flush(prefix)+flush(rest)` reassembles byte-identically and never emits a partial sequence |
| 2 | `Detector_HoldbackCap_Flushes` | 5 KB of `ESC]` garbage with no terminator → flush occurs at the 4 KB cap |
| 3 | `Pty_EchoRoundTrip` | spawn `/bin/cat` (Linux) / `cmd-less` echo equivalent via ConPTY (Windows: `findstr` or a tiny helper) → write bytes, read identical bytes |
| 4 | `Pty_IsattyProbe` | spawned probe reports a TTY on stdin/stdout |
| 5 | `Pty_CtrlC_Interrupts` | long-running child; write `0x03`; `ExitCode` completes with the interrupt code |
| 6 | `Pty_Resize_Propagates` | TUI probe (e.g. `tput cols` loop or `stty size`) reflects the new size after `Resize` |
| 7 | `Streamer_16msBatching_OneFrame` | burst writes within a tick → exactly one gRPC frame |
| 8 | `Scrollback_Capped_At_10k` | feed 20k lines → buffer holds the last 10k; memory bounded |
| 9 | `Streamer_MemoryFlat_Under_Firehose` | 100 MB through the streamer → pooled buffer high-water mark bounded (tagged `RequiresPty`/slow) |

Linux-only PTY tests carry a platform skip attribute; CI runs them in the Docker test wrapper.

---

## 7. Rejection triggers / Reviewer script

**Rejection:** raw `Process` with redirected pipes standing in for a PTY; renderer APIs leaking
into ViewModels; a `cmd.exe`/`sh -c` wrapper around the agent command; unbounded buffering.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~VtBoundary|FullyQualifiedName~Pty|FullyQualifiedName~TerminalStreamer|FullyQualifiedName~Scrollback"
grep -rn "Iciclecreek" Mainguard.App.Shell/ViewModels/        # 0 hits — renderer stays behind ITerminalView
grep -rn "UseShellExecute\|cmd.exe" Mainguard.Agents/Agents/ Mainguard.Server/   # 0 hits
ls external/Iciclecreek.Avalonia.Terminal/LICENSE*    # license retained
```

Then run the P2-04 harness (`dotnet test --filter "FullyQualifiedName~VtConformance"`) — the
allowlist may have entries, but it must exist and be green.

---

## 8. Definition of done

- [ ] `PtySession`/`PtyProcessShim` (ConPTY + forkpty), `ITerminalView`, `VtBoundaryDetector` exactly per contract.
- [ ] `TerminalStreamer` with 16 ms pooled batching + 4 KB holdback cap, wired into `TerminalService.Attach`.
- [ ] Vendored renderer behind `TerminalControl` + grid-readback hook; `TerminalViewModel`/`TerminalView` with keys, resize, 10k scrollback.
- [ ] All edge-matrix rows + invariants tested; split-at-every-offset corpus green; P2-04 harness runs.
- [ ] `docs/repo-map/` index updated (incl. `external/`). One task = one PR linking **P2-03**, base `phase2`.

---

## 9. Known field limitations (2026-07-22) — accepted here; P2-18 closes them

The first live coordinator sessions (claude-code in the hardened jail, driven from the inline
coordinator terminal) surfaced interim-engine limits that are **accepted for P2-03** and bound to
**P2-18** rather than patched here:

- **Ink/Yoga TUIs mis-render.** `VtScreen` lacks scroll regions (DECSTBM), insert/delete
  line/char, the alternate screen buffer, save/restore cursor, origin mode, and deferred-wrap
  semantics — full-screen redraws leave stale rows and dead fragments, and layouts don't track the
  pane size. This is the biggest operator-facing defect of the interim engine.
- **No mouse-selection copy** out of the terminal. What exists (added 2026-07-22): the
  application-driven copy path — OSC 52 → host clipboard (queries never answered) — and the three
  paste chords (Ctrl+V / Ctrl+Shift+V / Shift+Insert, CR-normalized, bracketed-paste-wrapped).
  That clipboard bridge is contract for P2-18's `TerminalGridControl`; `TerminalClipboardTests`
  must survive the engine swap unchanged. Drag-selection + Ctrl+Shift+C copy is a **required**
  P2-18 v1 feature (see the master doc's P2-18 field-findings block).

**Do NOT grow `VtScreen` toward conformance** — hand-building the missing repertoire is exactly
the work P2-18 (libvterm) replaces, and the P2-04 harness is the gate that proves the swap.
