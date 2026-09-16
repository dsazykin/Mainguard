# T-02 — 3-Way Merge Chunker (pure engine) — Implementation Plan

**Task ID:** T-02
**Milestone:** M1 remainder (audit fix 1.1, part 1)
**Priority:** CRITICAL
**Depends on:** nothing (can start immediately; runs in parallel with T-03)
**Consumed by:** T-04 (conflict-resolution UI). T-04 is blocked on this + T-03.
**Branch:** `plan/T-02-merge-chunker` (this doc) → implement on a fresh `feat/T-02-merge-chunker` off `main`.

> **Source of truth:** §T-02 of `docs/planning/Mainguard_Master_Implementation_Document.md` and §TI-02 of
> `docs/testing/Mainguard_Test_Implementation_Strategy.md`. This document expands both into a single
> implement-in-one-pass spec. Where this document and the Master Doc's *sample code* differ, **this
> document wins** — the Master Doc sample is explicitly labelled "one valid implementation, not the
> spec," and its sketch has a real defect (see §11). The Master Doc's **Contract, Invariants, and
> Edge-case matrix are binding** and are reproduced verbatim below.

---

## 0. Context — why this task exists and what is already on `main`

**A working conflict resolver already exists — this task does not add "the first" engine; it replaces the
resolver's *data source*.** Be accurate about the starting state:

- `Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs` (338 lines) is a **functional 3-way
  resolver**: a 3-column ours / common / theirs view with accept-ours / accept-theirs / take-both / discard
  per block, live "fully resolved" gating, and it writes the merged result back to the file. It is real and
  it works today.
- `Mainguard.App.Shell/ViewModels/ConflictedFilesViewModel.cs` is the per-file list that opens it, plus
  whole-file `git checkout --ours/--theirs` shortcuts.
- Audit fix 1.1 deleted a *separate*, **dead** `MergeDiffService.GenerateMergeChunks` stub that returned an
  empty list. The resolver above never called it, so its removal changed nothing.

The one thing the current resolver does **not** do: it builds its blocks by **parsing Git's
`<<<<<<< / ======= / >>>>>>>` markers out of the working-tree file** (`ParseFile` → `File.ReadAllLines`).
Marker parsing only exposes *ours* and *theirs* (no common ancestor unless the user has set
`merge.conflictStyle=diff3`), so it cannot do true 3-way classification or auto-merge non-conflicting
regions — it treats an entire marked span as one conflict.

**What T-02/T-03/T-04 change:** move the resolver's input from *working-tree marker text* to the *three
index blob stages* (`repo.Index.Conflicts`: ancestor/ours/theirs), so the engine always has the real base
and can classify each region as `Unchanged` / `LeftOnly` / `RightOnly` / `Conflict`. T-03 supplies those
blob texts; **this task (T-02)** builds the pure chunker that turns them into ordered chunks; T-04 rebuilds
the resolver UI on top of the chunker (reusing the existing 3-column layout and accept/discard interactions
as its visual base — see §T-04).

This task builds the **pure text engine only**: strings in → ordered chunks out, with **zero** repository
access, **zero** file I/O, and **zero** mutable static state. Keeping it pure is what makes it unit-testable
without Git and reviewable by reading the tests alone. **Do not touch the App layer in this task** — the
existing resolver keeps working until T-04 rewires it.

### What you can rely on already existing

| Fact | Detail |
|---|---|
| DiffPlex is referenced | `Mainguard.Agents/Mainguard.Agents.csproj` → `<PackageReference Include="DiffPlex" Version="1.9.0" />`. **Not currently used by any code** — this task is its first consumer. |
| Target framework | `.NET 10` (`global.json` pins SDK `10.0.100`, `rollForward: latestFeature`). |
| Model namespace convention | Models live in `Mainguard.Agents/Models/` under `namespace Mainguard.Agents.Models;` (see `GitDiffLine.cs`). |
| Service namespace convention | Services live in `Mainguard.Agents/Services/` under `namespace Mainguard.Agents.Services;`, interface-first (`IFoo` + `Foo`). |
| No DI container | Services are `new`-ed directly by callers. Do **not** introduce a container. T-04 will `new MergeDiffService()`. |
| Test project | `Mainguard.Tests` references **Core only**. This engine is pure, so its tests need no fixture, no repo, and no headless-UI infra. |

---

## 1. Files to create / modify

| Action | Path | Purpose |
|---|---|---|
| **Create** | `Mainguard.Agents/Models/MergeChunk.cs` | `ChunkKind`, `ChunkResolution` enums + `MergeChunk` class. |
| **Create** | `Mainguard.Agents/Services/IMergeDiffService.cs` | The two-method interface. |
| **Create** | `Mainguard.Agents/Services/MergeDiffService.cs` | The engine (`: IMergeDiffService`). |
| **Create** | `Mainguard.Tests/MergeDiffServiceTests.cs` | The 14+ unit cases from §9 (TI-02). |

No existing file is edited by this task. `IGitService` is **not** touched here (that's T-03).

---

## 2. Contract (must exist exactly — copied verbatim from the Master Doc)

### 2.1 `Mainguard.Agents/Models/MergeChunk.cs`

```csharp
namespace Mainguard.Agents.Models;

public enum ChunkKind { Unchanged, LeftOnly, RightOnly, Conflict }
public enum ChunkResolution { Unresolved, TakeLeft, TakeRight, TakeBoth, Custom }

public sealed class MergeChunk
{
    public ChunkKind Kind { get; init; }
    public ChunkResolution Resolution { get; set; } = ChunkResolution.Unresolved;
    public string BaseText  { get; init; } = "";   // this chunk's slice of the base
    public string LeftText  { get; init; } = "";   // this chunk's slice of "ours"
    public string RightText { get; init; } = "";   // this chunk's slice of "theirs"
    public string? CustomText { get; set; }        // used when Resolution == Custom
}
```

**Do not rename or drop any member, and do not change `init` vs `set` accessors** — T-04's ViewModel binds
`Resolution` and `CustomText` as mutable and reads the three text properties. Additive members are allowed
(see §8), e.g. line-number metadata for the UI gutter.

### 2.2 `Mainguard.Agents/Services/IMergeDiffService.cs`

```csharp
namespace Mainguard.Agents.Services;

using Mainguard.Agents.Models;

public interface IMergeDiffService
{
    /// <summary>Splits a 3-way merge into ordered chunks covering the whole document.</summary>
    IReadOnlyList<MergeChunk> GenerateMergeChunks(string? baseText, string? leftText, string? rightText);

    /// <summary>Concatenates chunks per their Kind/Resolution into the merged document.
    /// Throws InvalidOperationException if any Conflict chunk is Unresolved.</summary>
    string AssembleMerged(IEnumerable<MergeChunk> chunks);
}
```

`Mainguard.Agents/Services/MergeDiffService.cs` is `public sealed class MergeDiffService : IMergeDiffService`.

**Parameter names, order, nullability (`string?`), and return types are binding.** `left` == "ours",
`right` == "theirs" everywhere.

---

## 3. DiffPlex 1.9.0 API reference (exactly what you will call)

You only need the low-level differ. All types are in namespace `DiffPlex`; the line chunker is in
`DiffPlex.Chunkers`.

```csharp
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;   // DiffResult, DiffBlock

// Singleton differ:
DiffResult result = Differ.Instance.CreateDiffs(
    oldText:          baseJoined,     // string
    newText:          sideJoined,     // string
    ignoreWhitespace: false,
    ignoreCase:       false,
    chunker:          new LineChunker());   // or LineChunker.Instance
```

`DiffResult` members you use:

| Member | Type | Meaning |
|---|---|---|
| `result.PiecesOld` | `string[]` | `oldText` split into lines by the chunker. With `LineChunker` and LF-normalized input, this equals your own `baseLines` split. |
| `result.PiecesNew` | `string[]` | `newText` split into lines — equals your `sideLines`. |
| `result.DiffBlocks` | `IList<DiffBlock>` | The edit script, **in ascending base order**, non-overlapping. |

`DiffBlock` members (all `int`):

| Member | Meaning |
|---|---|
| `DeleteStartA` | First **base** (old) line index removed/replaced by this block. |
| `DeleteCountA` | How many base lines this block removes (0 ⇒ pure insertion). |
| `InsertStartB` | First **side** (new) line index inserted by this block. |
| `InsertCountB` | How many side lines this block inserts (0 ⇒ pure deletion). |

So each `DiffBlock` means: *base lines `[DeleteStartA, DeleteStartA+DeleteCountA)` are replaced by side
lines `[InsertStartB, InsertStartB+InsertCountB)`.*

> ⚠️ **Do not** use `result.PiecesNew` to reconstruct region content by re-splicing insertions keyed
> only on `DeleteCountA == 0` blocks (the Master Doc sketch does this). Replacement blocks
> (`DeleteCountA > 0 && InsertCountB > 0`) carry new content that such a scheme drops. Use the
> **offset-mapping** reconstruction in §4.4, which handles deletes, inserts, and replacements uniformly.

`Differ.Instance.CreateLineDiffs(old, new, ignoreWhitespace)` is a convenience wrapper around the call
above and is an acceptable substitute for the `CreateDiffs(...)` line — but you still control splitting/joining
yourself for CRLF and trailing-newline handling, so pass **already-normalized** joined text either way.

---

## 4. The engine algorithm (reference implementation)

Implement `GenerateMergeChunks` as the composition of five phases. Each phase is small and independently
testable. A structurally different implementation is allowed **iff** every invariant in §7 and every row of
§6 holds.

### 4.0 Vocabulary

- **base / left / right**: the three input documents (`baseText` = common ancestor, `leftText` = ours,
  `rightText` = theirs).
- **line array**: a document split into lines, LF-normalized, **without** a trailing empty element.
- **anchor / gap `a`**: the position *between* base line `a-1` and base line `a`. There are `N+1` gaps for
  `N` base lines (`0 … N`). Gap `N` is EOF. An insertion "at anchor `a`" is placed in that gap.
- **region `[s, e)`**: a maximal run of base lines that at least one side changed, plus any insertions bound
  to gaps inside it. `s == e` is a legal **zero-length** region (an isolated insertion between two unchanged
  base lines).

### 4.1 Phase 1 — normalize + split (handles CRLF and trailing newline)

```csharp
// Returns the line array; sets hadTrailingNewline. "" -> zero lines (NOT one empty line).
private static string[] SplitLines(string? text, out bool hadTrailingNewline)
{
    text ??= "";
    text = text.Replace("\r\n", "\n").Replace("\r", "\n");   // CRLF and lone-CR -> LF
    if (text.Length == 0) { hadTrailingNewline = false; return Array.Empty<string>(); }
    hadTrailingNewline = text[^1] == '\n';
    var parts = text.Split('\n');
    // A trailing '\n' produces a final "" element that is NOT a real line -> drop exactly one.
    return hadTrailingNewline ? parts[..^1] : parts;
}
```

Call it for all three inputs. Record `leftHadTrailingNewline` and `rightHadTrailingNewline`
(they are **not** used by `GenerateMergeChunks`; assembly uses the "non-empty ⇒ append `\n`" rule in §5, so
you may drop these here — see the note in §5). `baseText`'s trailing-newline flag is unused.

**Boundary cases this must produce:**

| Input | Line array |
|---|---|
| `""` | `[]` (empty) |
| `"a"` | `["a"]` |
| `"a\n"` | `["a"]` |
| `"a\nb"` | `["a","b"]` |
| `"a\nb\n"` | `["a","b"]` |
| `"\n"` | `[""]` (one empty line) |
| `"a\r\nb\r\n"` | `["a","b"]` |

### 4.2 Phase 2 — diff each side against base, extract blocks

Run DiffPlex twice: base→left and base→right, on the **joined normalized** text
(`string.Join("\n", baseLines)` etc. — joining your own arrays guarantees `PiecesOld` aligns with your
indices). Convert each `DiffResult` to a list of blocks:

```csharp
private readonly record struct Block(int BaseStart, int BaseEnd, int InsCount, int DelCount, int InsStart);

private static List<Block> ToBlocks(DiffResult r) =>
    r.DiffBlocks.Select(b => new Block(
        BaseStart: b.DeleteStartA,
        BaseEnd:   b.DeleteStartA + b.DeleteCountA,
        InsCount:  b.InsertCountB,
        DelCount:  b.DeleteCountA,
        InsStart:  b.InsertStartB)).ToList();
```

Keep, per side, the `sideLines` array (`PiecesNew`, which equals your left/right line array) alongside its
blocks — you need it in §4.4.

### 4.3 Phase 3 — mark hotness

Build, over base coordinates:

- `bool[] leftChanged` / `bool[] rightChanged`, length `N`: for every block with `DelCount > 0`, mark
  indices `[BaseStart, BaseEnd)` true. (Line `i` was deleted or replaced by that side.)
- `bool[] leftInsAnchor` / `bool[] rightInsAnchor`, length `N+1`: for every **pure-insertion** block
  (`DelCount == 0`), mark `leftInsAnchor[BaseStart] = true`. (Insertion bound to that gap.)

A gap/line position is **hot** when a region must cover it:
- line `i` is hot ⇔ `leftChanged[i] || rightChanged[i]`;
- gap `a` is hot ⇔ `leftInsAnchor[a] || rightInsAnchor[a]`.

### 4.4 Phase 4 — offset-mapping reconstruction (the core trick)

For a region `[s, e)` you must recover **each side's exact slice** — the side lines that correspond to base
lines `[s, e)`, including replacements and insertions. Because region boundaries always fall on block
boundaries (never strictly inside a block), the base→side offset is constant across each boundary, so:

```
sideStart = SideIndex(blocks, s, inclusiveAnchor: false)
sideEnd   = SideIndex(blocks, e, inclusiveAnchor: true)
slice     = sideLines[sideStart .. sideEnd)
```

`SideIndex` maps a base index to a side index by accumulating each fully-passed block's
`(InsCount - DelCount)`:

```csharp
// inclusiveAnchor controls whether a zero-length insertion block sitting exactly at baseIdx
// counts as "before" the index. It must count for the region END (so the region owns its own
// trailing insertion) but NOT for the region START (so the insertion lands inside the region).
private static int SideIndex(List<Block> blocks, int baseIdx, bool inclusiveAnchor)
{
    int idx = baseIdx;
    foreach (var b in blocks)               // ascending base order
    {
        if (b.BaseStart >= baseIdx && !(inclusiveAnchor && b.BaseStart == baseIdx && b.BaseEnd == baseIdx))
            break;                            // this and later blocks start at/after the index
        bool countIt = inclusiveAnchor
            ? b.BaseEnd <= baseIdx                          // include zero-length block at baseIdx
            : b.BaseEnd <= baseIdx && b.BaseStart < baseIdx; // exclude zero-length block at baseIdx
        if (countIt) idx += b.InsCount - b.DelCount;
    }
    return idx;
}
```

> **Why two predicates?** A normal block ending exactly at the boundary (`BaseEnd == idx, BaseStart < idx`)
> is *before* the region and must be counted at both start and end. A **zero-length insertion** at the
> boundary (`BaseStart == BaseEnd == idx`) belongs *inside* the region: it must be excluded at the start
> (so its inserted lines fall within `[sideStart, sideEnd)`) and included at the end.

`Slice(side, s, e)` returns `sideLines[sideStart .. sideEnd]` as a `string[]`. This one function handles
delete-only (`sideEnd == sideStart`), insert-only (`e == s`, `sideEnd > sideStart`), and replacement
regions uniformly.

### 4.5 Phase 5 — scan, emit, classify

Walk gap positions `0 … N`, accumulating unchanged base lines and flushing them as **one** `Unchanged`
chunk immediately before each region and once at the end.

```
chunks         = []
pendingBase    = []   // accumulated Unchanged base lines
i = 0
while i <= N:
    anchorHot = leftInsAnchor[i] || rightInsAnchor[i]
    lineHot   = i < N && (leftChanged[i] || rightChanged[i])

    if not (anchorHot or lineHot):
        if i < N: pendingBase.Add(baseLines[i]); i++          // ordinary unchanged line
        else:     i++                                          // reached EOF gap, stop
        continue

    // ---- a region starts at s = i ----
    FlushPending(chunks, pendingBase)                          // emit one Unchanged chunk (if any)
    s = i
    e = i
    while e < N and (leftChanged[e] || rightChanged[e]): e++   // consume contiguous hot lines

    EmitRegion(chunks, s, e)                                   // classify + add one chunk (see below)

    if e > s:
        // consumed real lines; clear any insertion anchors inside [s, e] so they don't retrigger
        for a in s..e: leftInsAnchor[a] = rightInsAnchor[a] = false
        i = e
    else:
        // zero-length region: base line s is still unchanged and must be emitted next loop.
        leftInsAnchor[s] = rightInsAnchor[s] = false           // consume the anchor; DON'T advance i
        // i unchanged -> next iteration sees line s as cold and pushes it to pendingBase

FlushPending(chunks, pendingBase)                              // trailing Unchanged, if any
return chunks
```

`FlushPending`: if `pendingBase` is non-empty, add one `MergeChunk { Kind = Unchanged, BaseText =
Join(pendingBase), LeftText = same, RightText = same }` and clear the list. (`Unchanged` chunks carry the
identical base slice in all three text fields — the UI renders them as context.)

`EmitRegion(s, e)`:

```
baseSlice  = baseLines[s .. e]                 // string[]
leftSlice  = Slice(left,  s, e)
rightSlice = Slice(right, s, e)

lDiff = !SequenceEqual(leftSlice,  baseSlice)
rDiff = !SequenceEqual(rightSlice, baseSlice)

kind =
    !lDiff && !rDiff                       -> Unchanged   // defensive; a hot region normally differs
    lDiff  && !rDiff                       -> LeftOnly
    !lDiff && rDiff                        -> RightOnly
    lDiff  && rDiff && SequenceEqual(L,R)  -> LeftOnly    // identical independent edits merge cleanly
    else                                   -> Conflict

chunks.Add(new MergeChunk {
    Kind      = kind,
    BaseText  = Join(baseSlice),
    LeftText  = Join(leftSlice),
    RightText = Join(rightSlice),
});
```

`Join(x)` = `string.Join("\n", x)` — chunks **never** carry a trailing `"\n"` (§5 re-adds separators).
`Join([])` = `""`.

### 4.6 Degenerate cases (must be explicitly correct — pinned by tests)

| Inputs | Required output |
|---|---|
| base == left == right, all non-empty | a **single** `Unchanged` chunk covering the whole document |
| all three empty (`""`) | **empty list** `[]` (this is the pinned choice — do **not** emit an empty `Unchanged`) |
| base empty, left and right each add different content | a single `Conflict` chunk (`BaseText == ""`) |
| left deletes the whole file, right edits it | a single `Conflict` chunk with `LeftText == ""` |

The empty-list case falls out naturally: with `N == 0`, the loop runs once at `i == 0 == N`, both hotness
flags are false, `i++`, loop ends, `pendingBase` empty → nothing flushed → `[]`. Verify your loop does not
emit a spurious chunk here.

---

## 5. `AssembleMerged`

```csharp
public string AssembleMerged(IEnumerable<MergeChunk> chunks)
{
    var lines = new List<string>();
    foreach (var c in chunks)
    {
        string chosen = c.Kind switch
        {
            ChunkKind.Unchanged => c.BaseText,
            ChunkKind.LeftOnly  => c.LeftText,
            ChunkKind.RightOnly => c.RightText,
            ChunkKind.Conflict  => c.Resolution switch
            {
                ChunkResolution.TakeLeft  => c.LeftText,
                ChunkResolution.TakeRight => c.RightText,
                ChunkResolution.TakeBoth  => Combine(c.LeftText, c.RightText),   // left lines THEN right lines
                ChunkResolution.Custom    => c.CustomText ?? "",
                _ /* Unresolved */        => throw new InvalidOperationException(
                                                 "Cannot assemble: unresolved conflict chunk."),
            },
            _ => "",
        };
        if (chosen.Length == 0) continue;        // empty slice contributes no lines (e.g. take-ours of a deletion)
        lines.AddRange(chosen.Split('\n'));
    }
    if (lines.Count == 0) return "";
    // POLICY (pinned by tests): a non-empty merged document ends with exactly one trailing newline.
    // AssembleMerged only receives chunks, not the original trailing-newline flags, so we cannot
    // faithfully reproduce a missing final newline; adding one is the accepted v1 behavior.
    return string.Join("\n", lines) + "\n";
}

private static string Combine(string left, string right)   // TakeBoth: left block then right block
    => left.Length == 0 ? right : right.Length == 0 ? left : left + "\n" + right;
```

Notes:
- **Non-conflict chunks need no resolution.** With zero `Conflict` chunks, `AssembleMerged` runs without
  any `Resolution` being set and yields the clean 3-way merge (invariant 3).
- The **"non-empty ⇒ append `\n`"** policy is why Phase 1's trailing-newline flags are not threaded into
  the model. Document this policy in a code comment (as above). TI-02 case 7's expected value is computed
  with this rule in mind.
- `TakeBoth` is **left lines then right lines**, in that order (edge-case matrix + TI-02 case 13).

---

## 6. Edge-case matrix (binding — every row needs a test)

| Input | Required behavior |
|---|---|
| identical `base == left == right` | single `Unchanged` chunk (empty list only when all three are `""`) |
| left edits line 5, right untouched | `Unchanged` + `LeftOnly` + `Unchanged` |
| both edit the same line differently | that region is **one** `Conflict` chunk |
| both make the *identical* edit | **no** `Conflict` — clean merge (emitted as `LeftOnly`) |
| non-overlapping edits (left line 2, right line 9) | `LeftOnly` and `RightOnly` separated by `Unchanged`; `AssembleMerged` (no resolutions) equals the true merge |
| both insert different text at the same anchor (mid-file **and** EOF) | `Conflict` |
| base empty, both sides add different content (add/add) | one `Conflict` chunk |
| left deletes whole file, right edits it | `Conflict` with `LeftText == ""` |
| CRLF input | handled identically to LF (normalized in Phase 1) |
| `AssembleMerged` with an `Unresolved` conflict | `InvalidOperationException` |
| `TakeBoth` | left lines then right lines |

---

## 7. Invariants (MUST — reviewers verify each)

1. The service is **pure**: no `Repository`, no file I/O, no mutable statics. (`grep` check in §10.)
2. Chunks are ordered and **cover the base document exactly**: concatenating every chunk's `BaseText` with
   `"\n"` separators, skipping empty ones, reproduces the base document.
3. With zero `Conflict` chunks, `AssembleMerged` needs no resolutions and equals the clean 3-way merge.
4. Identical edits on both sides never produce a `Conflict`.
5. `AssembleMerged` on any unresolved `Conflict` throws `InvalidOperationException`.
6. No `ChunkKind.Conflict` chunk has `LeftText == RightText`.
7. **No two adjacent chunks share the same `Kind` unless separated by a `Conflict`** — i.e. `Unchanged`
   runs are coalesced into one chunk, and the scan never emits two back-to-back same-kind chunks. (The
   reference scan guarantees this because `pendingBase` is flushed as a single chunk and regions are always
   separated by ≥1 unchanged line or a consumed anchor.)

---

## 8. Acceptable variations (MAY — do **not** request changes for these)

- Any diff back-end: DiffPlex `DiffBlocks` (this plan), DiffPlex side-by-side models, or a hand-rolled Myers.
- Any internal region representation: interval list, bool arrays (this plan), or a state machine.
- Extra **additive** members on `MergeChunk` (e.g. base/side line numbers for the UI gutter).
- LINQ vs. explicit loops; different private-helper decomposition.

## 8.1 Rejection triggers (any single hit → request changes)

- Any `Repository`, `LibGit2Sharp`, or `System.IO.File`/`Directory` access inside the service.
- Trailing-newline crash, or mangling/duplication of CRLF inputs.
- Conflict detection by scanning for textual `<<<<<<<` / `=======` / `>>>>>>>` markers.
- `AssembleMerged` silently emitting placeholder/marker text for an unresolved chunk instead of throwing.
- Emitting adjacent same-`Kind` chunks that should have been coalesced (violates invariant 7).
- Reconstructing region content by an insertions-only splice that drops replacement-block text (see §11).

---

## 9. Test contract — `Mainguard.Tests/MergeDiffServiceTests.cs` (TI-02)

Pure unit tests, no fixture. **Minimum 14 cases** (the reviewer script asserts count ≥ 12; ship ≥ 14).
Instantiate `IMergeDiffService svc = new MergeDiffService();` per test. Use the framework already in
`Mainguard.Tests` (xUnit — match the existing test files' `[Fact]`/`[Theory]` style).

| # | Test name | Assertion |
|---|---|---|
| 1 | `GenerateMergeChunks_Identical_ShouldYieldSingleUnchanged` | `base==left==right=="a\nb\nc"` ⇒ exactly one chunk, `Kind==Unchanged`, `BaseText=="a\nb\nc"`. |
| 2 | `GenerateMergeChunks_AllEmpty_ShouldYieldEmptyList` | all three `""` ⇒ `Count==0`. Pins the degenerate choice. |
| 3 | `GenerateMergeChunks_LeftOnlyEdit_ShouldYieldLeftOnlyChunk` | base `a\nb\nc`, left `a\nX\nc`, right `a\nb\nc` ⇒ `[Unchanged("a"), LeftOnly(base "b"/left "X"), Unchanged("c")]`. |
| 4 | `GenerateMergeChunks_RightOnlyEdit_ShouldYieldRightOnlyChunk` | mirror of #3 on the right. |
| 5 | `GenerateMergeChunks_SameLineEditedBothSides_ShouldYieldConflict` | base `a\nb\nc`, left `a\nX\nc`, right `a\nY\nc` ⇒ middle chunk `Kind==Conflict`, `LeftText=="X"`, `RightText=="Y"`. |
| 6 | `GenerateMergeChunks_IdenticalEditBothSides_ShouldNotConflict` | left==right==`a\nX\nc` ⇒ **no** `Conflict` chunk; middle is `LeftOnly`. |
| 7 | `GenerateMergeChunks_NonOverlappingEdits_ShouldYieldBothKinds_AndAssembleToTrueMerge` | base 5 lines, left edits line 2, right edits line 4 ⇒ assert the full sequence `Unchanged,LeftOnly,Unchanged,RightOnly,Unchanged` **and** `AssembleMerged(result)` (no resolutions set) equals the manually computed merged string (including the single trailing `\n` per §5). |
| 8 | `GenerateMergeChunks_BothInsertAtSameAnchor_ShouldConflict` | `[Theory]` two rows: (a) both insert a different line mid-file at the same gap; (b) both append a different line at EOF. Each ⇒ a `Conflict` chunk. |
| 9 | `GenerateMergeChunks_AddAdd_EmptyBase_ShouldConflict` | base `""`, left `"L"`, right `"R"` ⇒ one `Conflict`, `BaseText==""`. |
| 10 | `GenerateMergeChunks_WholeFileDeleteVsEdit_ShouldConflict_WithEmptyLeftText` | base `a\nb\nc`, left `""`, right `a\nZ\nc` ⇒ one `Conflict`, `LeftText==""`. |
| 11 | `GenerateMergeChunks_CrlfInput_ShouldBehaveAsLf` | feed the #5 inputs with `\r\n` line endings ⇒ identical chunk shape and no `\r` in any chunk text. |
| 12 | `AssembleMerged_Unresolved_ShouldThrowInvalidOperation` | build a `Conflict` chunk left `Unresolved` ⇒ `Assert.Throws<InvalidOperationException>`. |
| 13 | `AssembleMerged_Resolutions_ShouldEmitChosenText` | `[Theory]`: for a single `Conflict{Left="X",Right="Y"}`, `TakeLeft`→`"X\n"`, `TakeRight`→`"Y\n"`, `TakeBoth`→`"X\nY\n"`, `Custom(CustomText="Z")`→`"Z\n"`. |
| 14 | `Chunks_ShouldCoverBaseExactly` | property-style over a table of ~10 diverse `(base,left,right)` triples: (a) concatenating chunk `BaseText`s with `\n`, skipping empty, reproduces `base` (invariant 2); (b) no two adjacent chunks share a `Kind` (invariant 7); (c) no `Conflict` chunk has `LeftText==RightText` (invariant 6). |

Add any extra cases you find while implementing (e.g. adjacent-hunks, insertion-immediately-before-a-change).
Never delete or weaken a case to go green.

---

## 10. Reviewer verification script (must pass, < 1 min)

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~MergeDiffService"      # all green, count >= 12 (ship >= 14)

# purity: no repo/file IO anywhere in the service
grep -nE "Repository|LibGit2Sharp|System\.IO|File\.|Directory\." \
    Mainguard.Agents/Services/MergeDiffService.cs                    # -> 0 hits

# global engineering invariants (G-1)
grep -rnE "throw new Exception\(|throw new System\.Exception" \
    Mainguard.Agents/Services/MergeDiffService.cs                    # -> 0 hits (only InvalidOperationException is thrown)

# no marker-based conflict detection
grep -n "<<<<<<<" Mainguard.Agents/Services/MergeDiffService.cs      # -> 0 hits
```

---

## 11. Known defect in the Master Doc sample (do not copy it)

The Master Doc §T-02 "Implementation steps" build a `Dictionary<int, List<string>> insertions` keyed **only**
on blocks with `DeleteCountA == 0`, then reconstruct each side's slice as "base lines `[s,e)` minus deleted
lines, with insertions spliced." DiffPlex frequently emits **replacement** blocks (`DeleteCountA > 0 &&
InsertCountB > 0`) — e.g. "line 2 changed from `b` to `X`" is one block with `DeleteCountA==1,
InsertCountB==1`. Such a block's new line (`X`) is **not** in the insertions dictionary and is **not** among
the base lines, so the sketch would drop it, producing wrong `LeftText`/`RightText` and misclassifying
conflicts. The §4.4 offset-mapping reconstruction avoids this by mapping base ranges to side ranges directly
and slicing `sideLines`, which is correct for deletes, inserts, and replacements alike. The Master Doc
explicitly permits this ("Acceptable variations: any diff back-end … different internal region
representation") and the invariants/tests remain the contract.

---

## 12. Definition of done

- [ ] `MergeChunk.cs`, `IMergeDiffService.cs`, `MergeDiffService.cs` created with the exact contract (§2).
- [ ] `GenerateMergeChunks` implements Phases 1–5; `AssembleMerged` implements §5's policy.
- [ ] Every row of §6 handled; every invariant in §7 holds.
- [ ] `MergeDiffServiceTests.cs` contains all 14 cases from §9, all green.
- [ ] Reviewer script (§10) passes with zero hits on every `grep`.
- [ ] No App-layer, `IGitService`, or repository code touched (that's T-03/T-04).
- [ ] PR description links task **T-02**, lists the tests added, and notes the offset-mapping deviation
      from the Master Doc sample (§11). One task = one PR.
```
