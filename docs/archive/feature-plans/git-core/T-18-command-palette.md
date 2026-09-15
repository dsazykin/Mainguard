# T-18 — Command Palette & Keyboard Shortcuts — Implementation Plan

**Task ID:** T-18 · **Milestone:** M4 (audit 2.15) · **Priority:** P1 · **Depends on:** nothing.
**Branch:** `plan/T-18-command-palette` → implement on `feat/T-18-command-palette` off `main`.

> **Source of truth:** §T-18 of the Master Doc (contract summary), §TI-18 of the Test Strategy.

---

## 0. Context

No palette or configurable shortcuts today (shortcuts live ad-hoc in `MainWindow.axaml`). This task adds a
UI-free **`ActionRegistry`** in Core (which later becomes the agent command surface), a pure **`FuzzyMatcher`**,
a Ctrl+P **command palette**, and a persisted, rebindable **`ShortcutMap`**. The three pure pieces are the
bulk of the test surface.

### What you can rely on

| Fact | Where |
|---|---|
| Avalonia `KeyBindings` / `KeyGesture` for shortcut wiring | Avalonia |
| `UserPreferences` + `SettingsService` for persistence | `Models/UserPreferences.cs` |
| Existing `[RelayCommand]`s across ViewModels to register as actions | throughout `Mainguard.App.Shell` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Actions/AppAction.cs` + `ActionRegistry.cs` (UI-free) |
| **Create** | `Mainguard.Agents/Actions/FuzzyMatcher.cs` (pure, ~80 lines) |
| **Create** | `Mainguard.App.Shell/ViewModels/CommandPaletteViewModel.cs` + overlay view |
| **Edit** | `Models/UserPreferences.cs` — `ShortcutMap` (id → gesture string) |
| **Create** | `Mainguard.Agents/Actions/ShortcutMap.cs` (conflict detection) + rebind UI in Preferences |
| **Create** | `FuzzyMatcherTests.cs`, `ActionRegistryTests.cs`, `ShortcutMapTests.cs` (all pure) |

---

## 2. Contract

```csharp
// Mainguard.Agents/Actions/AppAction.cs
public sealed class AppAction
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public Func<bool> CanExecute { get; init; } = () => true;
    public Func<Task> Execute { get; init; } = () => Task.CompletedTask;
}

public sealed class ActionRegistry            // UI-free
{
    public void Register(AppAction action);   // duplicate Id -> throws
    public IReadOnlyList<AppAction> All { get; }
    public IReadOnlyList<AppAction> Enabled();   // filtered by CanExecute
}

public static class FuzzyMatcher
{
    public static int Score(string query, string candidate);   // <0 or int.MinValue => no match (non-subsequence)
    public static IReadOnlyList<(T Item, int Score)> Rank<T>(string query, IEnumerable<T> items, Func<T,string> text);
}
```

---

## 3. Implementation

- **`FuzzyMatcher` (pure):** subsequence match with **consecutive-run bonus** + **word-boundary bonus**;
  case-insensitive; empty query → all (score 0). Non-subsequence → excluded. Ranking is pinned by the TI-18
  table (`"chb"` ranks "Checkout Branch" above "Cherry-pick branch b").
- **`ActionRegistry`:** register `[RelayCommand]`-backed actions as `AppAction`s; duplicate `Id` on
  registration **throws**; `Enabled()` filters by `CanExecute` (disabled actions are filtered, not shown then
  crashing on invoke).
- **`CommandPaletteViewModel`:** Ctrl+P overlay ranking actions + branch names + bookmarked repos via
  `FuzzyMatcher`; Enter invokes the selected action's `Execute`.
- **`ShortcutMap`:** persisted in `UserPreferences` (id → gesture string); rebind UI with **conflict
  detection** (two ids mapping to the same gesture flagged); survives restart. Defaults: **Ctrl+P** palette,
  **Ctrl+Enter** commit, **Ctrl+Shift+P** push, **F5** refresh, **Ctrl+B** new branch.

---

## 4. Invariants / Test contract — TI-18

**MUST:** matcher is pure and property-tested (ranking table); disabled actions filtered by `CanExecute`
(not hidden post-hoc crash); rebinds survive restart.

Pure tests:
1. `FuzzyMatcherTests` ranking `[Theory]`: `"chb"` → "Checkout Branch" above "Cherry-pick branch b";
   word-boundary bonus; consecutive-run bonus; non-subsequence excluded; case-insensitive; empty → all.
2. `Registry_ShouldFilterByCanExecute`.
3. `Registry_DuplicateIds_ShouldThrowOnRegistration`.
4. `ShortcutMap_ConflictDetection_ShouldFlagDuplicateGesture`; `ShouldRoundTripThroughPreferences`.

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~FuzzyMatcher|FullyQualifiedName~ActionRegistry|FullyQualifiedName~ShortcutMap"
grep -rn "Avalonia" Mainguard.Agents/Actions/          # -> 0 hits (registry/matcher are UI-free)
```

- [ ] UI-free `ActionRegistry` (duplicate-id throw, CanExecute filter) + pure `FuzzyMatcher`.
- [ ] Ctrl+P palette over actions + branches + repos.
- [ ] Persisted `ShortcutMap` with conflict detection + rebind UI + defaults.
- [ ] TI-18 pure tests green. One PR linking **T-18**.
```
