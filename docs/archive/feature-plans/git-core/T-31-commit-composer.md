# T-31 — Conventional-Commit Composer — Implementation Plan

**Priority:** P2 · **Depends on:** nothing external (pure/local). Complements T-28's `ChangelogGenerator`
(this is the *inverse* — it composes conventional subjects that T-28 later parses). **Fully offline-verifiable.**
> Everything is machine-verifiable: a pure builder + validator with pinned output + a composer UI. Nothing
> deferred except a "does this feel good to type in" glance.

## 0. Why
Consistent, conventional commit messages make history, changelogs (T-28), and semver automation work. A
structured composer (type / scope / description / body / breaking / co-authors / issue refs) with a live
preview + commitlint-style validation makes good messages the easy path — and is the format an agent emits.

## 1. Contract
```csharp
// Mainguard.Agents/Commits/ConventionalCommit.cs   (pure)
namespace Mainguard.Agents.Commits;

public sealed class ConventionalCommitDraft
{
    public string Type { get; init; } = "";            // feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert
    public string Scope { get; init; } = "";           // optional
    public string Description { get; init; } = "";      // subject text after the colon
    public string Body { get; init; } = "";
    public bool Breaking { get; init; }
    public string BreakingDescription { get; init; } = "";
    public IReadOnlyList<string> CoAuthors { get; init; } = System.Array.Empty<string>();   // "Name <email>"
    public IReadOnlyList<string> ClosesIssues { get; init; } = System.Array.Empty<string>(); // "#12", "org/repo#7"
}

public sealed class CommitValidationIssue { public string Field { get; init; } = ""; public string Message { get; init; } = ""; public bool IsError { get; init; } }

public static class ConventionalCommitBuilder
{
    public static readonly IReadOnlyList<string> Types;  // the standard set (+ optional emoji map)
    /// <summary>Assemble the full commit message: "type(scope)!: description" + blank + body + blank +
    /// "BREAKING CHANGE: …" + "Closes …" + "Co-authored-by: …" trailers. Pure, deterministic.</summary>
    public static string Build(ConventionalCommitDraft draft);
    /// <summary>commitlint-style checks: known type, non-empty description, subject length &lt;= 72 (warn),
    /// imperative-mood hint (soft), body wrap (soft), breaking has a description, valid co-author "Name <email>".</summary>
    public static IReadOnlyList<CommitValidationIssue> Validate(ConventionalCommitDraft draft);
    /// <summary>Best-effort parse an existing message back into a draft (reuse T-28 ParseSubject for the header).</summary>
    public static ConventionalCommitDraft Parse(string message);
}
```

## 2. Implementation
1. **Pure `ConventionalCommitBuilder`** — `Build` (exact formatting pinned in tests: header `type(scope)!:`,
   `!` when Breaking, blank-line separation, `BREAKING CHANGE:` footer, `Closes #x` line, `Co-authored-by:`
   trailers — GitHub-recognized), `Validate` (errors vs warnings), `Parse` (reuse `ChangelogGenerator.ParseSubject`
   for the header, extract trailers). No IO.
2. **UI — a Commit Composer** in the staging/commit area (a mode toggle: plain box ⇄ structured):
   - **Type** dropdown (with a short label/emoji), **Scope** box, **Description** box (with a live char counter,
     amber past 72), **Body** editor, **Breaking** toggle + description, **Co-authors** (add "Name <email>";
     offer recent authors from history as suggestions — optional), **Closes** issue refs.
   - A **live preview** of the assembled message (read-only) + inline **validation** chips (errors block/​warn).
   - **Commit** uses the assembled message (routes through the existing commit path + the T-30 scan).
   - Persist the user's plain-vs-structured preference (`UserPreferences`). Optionally load a repo `.gitmessage`
     template as the starting body (nice-to-have; keep minimal).
   - Tokens only (type chips / counter / validation from Accent/Warning/Danger); no raw colors.

## 3. Edge cases / invariants
- Empty scope → header has no `()`. Breaking with no description → validation warning, but `!` + `BREAKING CHANGE:` still emitted if Breaking is on.
- Multiple co-authors → one `Co-authored-by:` trailer each; malformed "Name <email>" → validation error, not emitted.
- Description with trailing period / leading type word → soft hints, never blocks.
- `Parse(Build(draft)) ≈ draft` round-trip for the structured fields (pin the stable subset).
- Composer never blocks a determined user — validation is advisory (errors gate the default Commit button but a plain-mode escape hatch remains). Pure builder in Core; UI-free; deterministic.

## 4. Test contract (offline)
- Pure `Build`: pinned output for feat/fix, scope/no-scope, breaking (`!` + footer), co-authors, closes, body — exact strings.
- Pure `Validate`: known/unknown type, empty description (error), >72 subject (warning), malformed co-author (error), breaking-without-description (warning).
- `Parse` + round-trip: `Parse(Build(d))` recovers type/scope/description/breaking/co-authors/closes.
- VM: preview updates live from fields; validation chips reflect issues; commit uses the assembled message; plain⇄structured toggle persists.
- Headless render: the composer (filled) with the live preview + a validation warning; plain mode.

## 5. Definition of done
- [ ] Pure `ConventionalCommitBuilder` (`Build`/`Validate`/`Parse`) with pinned tests + round-trip.
- [ ] Commit Composer UI (structured fields + live preview + validation + plain⇄structured toggle), commit routes through the existing path (+ T-30 scan).
- [ ] `UserPreferences` composer-mode flag (JSON, no migration).
- [ ] Offline tests green (builder pinned + validate + parse round-trip + VM + render). One PR linking **T-31**.
