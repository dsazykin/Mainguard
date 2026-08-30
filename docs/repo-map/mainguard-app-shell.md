<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### `Mainguard.App.Shell/` (Avalonia UI shell — assembly `Mainguard.App.Shell`) + `Mainguard.Client.App/` / `Mainguard.Pro.App/` (exe heads)

**Step 2f** split the old pre-split app exe into the edition-agnostic + Client **shell library** `Mainguard.App.Shell/` (assembly name is now `Mainguard.App.Shell` (step 2g), matching the paths below and the `avares://Mainguard.App.Shell/…` URIs) plus two thin WinExe heads. The shell references ONLY `Mainguard.UI` + `Mainguard.Git`; the file bullets below live in `Mainguard.App.Shell/`.

- **`ShellEntryPoint.cs`** (shell) — the shared entry-point plumbing both heads call: the
  interactive-rebase editor argv shims (`--rebase-editor` writes the todo list, `--rebase-msg`
  supplies the reword/squash message keyed by original SHA — `TryHandleShim` runs + returns *before*
  Avalonia starts, don't reorder), the single-instance guard, `CrashGuard.Install` (via `AfterSetup`),
  and `BuildAvaloniaApp`.
  `TryHandleShim` hands back an **exit code** the head must assign to `Environment.ExitCode`.
- **`CrashGuard.cs`** (shell) — the app-wide unhandled-exception net, installed by `RunDesktop` (via
  `AppBuilder.AfterSetup`, so the Dispatcher exists) for BOTH heads. `Dispatcher.UIThread.UnhandledException`
  is the one that matters: an exception escaping a dispatcher job — a `Dispatcher.UIThread.Post` callback,
  or the `async void` rethrow `[RelayCommand]`'s `AsyncRelayCommand` performs on a faulted command body —
  has no frame below it to catch it, so .NET calls `abort()` and the whole GUI dies (ISSUES-LOG #12: clicking
  Coordinator → Restart took the client down with SIGABRT mid-`SpawnAgent`). It marks such exceptions handled,
  appends the full exception to **`<data root>/logs/client-crash.log`** (the client had NO file log before —
  the only evidence a crash left was an unsymbolicated native stack) and raises a shell toast. Also records
  `AppDomain.UnhandledException` (can't be handled, but can be diagnosed) and observes
  `TaskScheduler.UnobservedTaskException`. A net, not a licence: commands that can fail still catch their own
  failures and render them in context.
- **`SingleInstanceGuard.cs`** (shell) — the cross-platform single-instance guard behind
  `RunDesktop`: the named mutex on Windows, an exclusive flock on `<data root>/app.lock` on Unix
  (a named mutex does not exclude across processes on macOS — two instances both "won"
  on-device). Either form is released by the kernel when the holder dies, so a crash never
  wedges the next launch.
- **`RebaseEditorShim.cs`** (shell) — the shim's actual work, split out so it is testable.
  **The exit code is the contract with git**: git reads 0 from `GIT_SEQUENCE_EDITOR` as "the editor
  wrote your todo" and otherwise proceeds with its own default todo — a plain `pick` of every commit —
  silently discarding every reorder/squash/drop/fixup and then reporting the rebase as a success. So
  every failure to place the todo/message returns non-zero (git aborts, branch untouched) and writes
  the reason to stderr, which git relays. Never swallow an exception in this path. "No staged message
  for this step" is a legitimate exit 0; a staged message that could not be placed is not.
  - **`Mainguard.Client.App/Program.cs`** (`App.Edition = new ClientManifest()`) and
    **`Mainguard.Pro.App/Program.cs`** (`App.Edition = new ProManifest()` + wires the Pro-launch seams +
    `WireProComposition`) are the actual `Main`s. **`App.axaml` / `App.axaml.cs`** (shell) — app
    bootstrap, DB migrate-on-startup, static `Settings`, theme init, tray, the guarded full-exit, and
    the null-until-wired Pro-launch seams
    (`ProDesktopStarter`/`VisualizedShutdownAsync`/`AfterInitialize`); the Pro launch/OOBE/shutdown
    machinery itself lives in `Mainguard.Agents.UI/Editions/ProDesktopHost`.
  - `App.axaml` includes the `avares://Mainguard.UI/…` design system (the Pro-only Dock theme is
    runtime-injected by `ProDesktopHost.InjectProChrome`, not here).
- **`Themes/`, `Theming/ThemeManager.cs`** — moved to `Mainguard.UI/` (step 2c); see that section above.
- **`ViewLocator.cs`** — maps a `FooViewModel` to its `FooView` by naming convention (a whole-string
  `ViewModel`→`View` replace, which also turns the `…ViewModels.*` namespace segment into `…Views.*`).
  New VM/View pairs are wired automatically as long as they follow the name pattern. **Multi-assembly
  (1e, ADR-0001):** resolution now scans a registered set — the static `ViewLocator.ViewAssemblies`
  (defaults to just the shell so a bare `new ViewLocator()` in tests/harnesses still resolves;
  `App.Initialize` seeds it from `App.Edition.ViewAssemblies` via `App.ComposeViewAssemblies`, shell
  always included + deduped) — probing each with `asm.GetType(name)`, so a Phase-2 Pro-UI assembly's
  Views resolve without assembly-qualified names. It stays a manifest-contributed list, NOT an
  `AppDomain` scan (trim-honest: the Client head never lists the Pro assembly). Pro VMs must keep the
  parallel `…ViewModels.* → …Views.*` shape or the locator would need a namespace map (deliberately
  not built). Cross-assembly resolution is pinned single-project by `Mainguard.Tests`'
  `ViewLocatorCrossAssemblyTests` (a probe VM/View pair in the test assembly).
- **`Views/`** — one `.axaml` (+ `.axaml.cs`) per screen/dialog. Paired 1:1 with `ViewModels/`. Secondary dialogs/panels derive from `Mainguard.UI`'s `ChromedWindow` and place its `CustomTitleBar` in row 0 for one consistent hand-drawn title bar (both moved to `Mainguard.UI` in step 2c; the derived windows stay here in the same `Mainguard.App.Shell.Views` namespace, resolving cross-assembly).
  - Shell: `MainWindow` (top nav, sidebar, overlays: command palette / delete-confirm / invalid-repo
    / the bottom-right shell-toast stack bound to `MainWindowViewModel.Toasts` — window-wide,
    auto-dismissing, mirrors RepoDashboard's #85 toast styles; its `Grid.Row="1"` is load-bearing —
    without it the host falls into the Auto-sized title-bar row and `VerticalAlignment="Bottom"`
    renders the stack in the TOP-right corner, the 2026-08 regression pinned by
    `Headless/MainWindowShellRenderHarness.ShellToasts_*`). The command-palette overlay hosts
    `CommandPaletteView` (T-18: a reusable palette card — query box + ranked/highlighted result rows
    with category headers, category chips, and gesture chips) bound to
    `MainWindowViewModel.CommandPalette`;
    - `MainWindow.axaml.cs` builds the window's global `KeyBinding`s at runtime from the effective
      `ShortcutMap` (routing each gesture to `InvokeActionByIdCommand`) and rebuilds them when rebinds are
      saved.
  - `CommandPaletteView` (T-18 palette overlay, above) and `ShortcutSettingsView` (T-18
    keyboard-shortcut rebind screen: one editable gesture per registered action, live conflict flagging
    via `ShortcutMap`, reset-to-defaults; a `UserControl` wrapping the unchanged
    `ShortcutSettingsViewModel` — it used to be its own `ShortcutSettingsWindow` opened from File →
    Keyboard Shortcuts…, now it is the Settings window's **Keyboard Shortcuts** page, built by
    `MainWindowViewModel`'s private `BuildShortcutSettingsPage()` factory and dropped into
    `SettingsViewModel.Pages`).
  - Repo workspace: `RepoDashboardView` (layout host) → `StagingPanelView`, `DiffViewerView`,
    `CommitTimelineView`.
    - `StagingPanelView` embeds `PreCommitFindingsView` (T-30) and, in its commit composer,
      `CommitComposerView` (T-31 conventional-commit composer: a plain ⇄ structured segment toggle, then
      the structured form — Type dropdown, Scope, Description with a live char counter (amber past 72),
      Body, Breaking toggle + description, add/removable Co-author "Name <email>" and Closes issue-ref
      chips, a live read-only `SurfaceDeep` preview of the assembled message, and inline Danger/Warning
      validation chips — all tokens, no raw color; the mode toggle persists to
      `UserPreferences.UseStructuredCommitComposer` and the commit uses the assembled message through the
      existing path + T-30 scan). The pre-commit scanner findings panel: grouped by severity
      (Blocker/Warning/Info dot from Danger/Warning/`InfoBrush` tokens), each row a kind icon +
      `path:line` + redacted message + a reveal-in-diff eye, an all-clear state, a "Scan before commit"
      toggle, and the blocker banner with **Commit anyway** / Cancel.
  - Feature screens: `CloneDashboardView`, `AnalyticsView` (T-22 analytics: four LiveChartsCore
    charts in themed cards — language-breakdown donut, weekly-churn time series, commit punch-card
    heatmap, top-contributor bars — with a ghost/skeleton loader while the async analysis runs; all
    series/axis paints come from `Charts/ChartTheme` tokens), `BlameView` (T-11 blame: an `AvaloniaEdit`
    viewer with a `BlameGutterMargin` — age-heat bar + `author · shortSha · relative-date`, boundary
    shading, click-to-select-commit — defined in `BlameView.axaml.cs`; age-heat colors resolve from
    theme tokens at render time; **T-32** adds a right-click **"Why this line"** context popover — the
    PR(s) that introduced the commit + its linked issues, routing into the PR/Issues panel or browser —
    bound to `BlameViewModel.LineContext`. **T-33** hosts this `UserControl` in the `BlameWindow`
    dialog, reachable from the **"Blame this file"** context-menu entries).
  - Dialogs/windows: `AgentCliSettingsView` (P2-22 §J-5 **Agent CLIs** — the add-more-later surface:
    per-CLI row with the pinned-version chip, an installed/not-installed line (icon + text, never colour
    alone), per-row Install, an inline actionable failure cause, Cancel install + Refresh; root is now a
    `UserControl` — it used to be its own window opened from the repo actions menu, now it is the
    Settings **Agent CLIs** page, and `AgentCliSettingsViewModel` implements `ISettingsPage` so
    `OnActivated` kicks the catalog refresh, replacing the old window's `OnDataContextChanged` hook),
    `ToolchainSettingsView` (**Toolchains** — the same row shape over the user-managed *language*
    toolchain channel: per-toolchain pinned-version chip, the summary, an installed/not-installed line
    that carries what the PROBE reported (so "a different version is present — expected 3.12.13, the
    probe reported: go1.22.6" reads as words rather than a bare "no"), per-row Install/Remove, an
    inline actionable failure cause, Cancel + Refresh; a `UserControl` page whose
    `ToolchainSettingsViewModel` implements `ISettingsPage`),
    `StartupWindow`/`StartupWindowViewModel` (owner design 2026-07-17 — the control-center BLOCKING
    startup loading screen driving Core's `AppStartupSequence`: a `BootstrapStageViewModel` glyph
    checklist + one changing status line, and the CONSENTED tier-2 OS upgrade offer hosted INLINE —
    installed→new version chips, the honest consent copy, Upgrade now / Later, and the `VmUpgradePlan`
    step checklist reusing `VmUpgradeOfferViewModel`, which replaced the deleted `VmUpgradeOfferWindow`;
    a degraded/failed essential step shows honest status and hands `MainWindowViewModel` a
    `StartupResult` banner), `ShutdownWindow`/`ShutdownWindowViewModel` (the small full-exit teardown
    window driving Core's `AppShutdownSequence` — a changing status line for releasing the `VmKeepAlive`
    holder and, when StopVmOnExit is on, "Stopping Mainguard OS…", with completion before process exit),
    `MainguardOsPageView`/`MainguardOsPageViewModel` (Pro-only Settings **Mainguard OS** page — replaces
    the deleted `AddReposToOsView` window: wraps the unchanged `AddReposToOsViewModel`, itself still the
    post-setup host of the shared `RepoOnboardingViewModel` engine (the OOBE step's two entry choices +
    row list + failure-isolated sequential copy), and additionally exposes a
    `RebuildSandboxImagesCommand` folding in the old separate Tools → "Rebuild sandbox images" action,
    which had no dialog of its own; `MainguardOsPageViewModel` implements `ISettingsPage.OnDeactivated`
    to cancel an in-flight repo copy if the user navigates away mid-copy, replacing the old window's
    `OnClosing` guard; composed via `IProToolsSurface.CreateMainguardOsPage(Window owner)`, which still
    constructs `AddReposToOsViewModel` through `App.CreateAddReposToOsViewModel`/`ProDesktopHost`),
    `CreateBranchDialog`, `CreateTagDialog`, `SettingsWindow`/`SettingsViewModel` (#78, restructured —
    File → Settings… now opens an 880×620 window with a left sidebar (`Pages`:
    `ObservableCollection<SettingsPageRowViewModel>`, an `ItemsControl` styled with the same
    `Button.railItem`/`Classes.active` the main window's section rail uses) and a right `ContentControl`
    resolving `ActivePageContent` through the app-wide `ViewLocator`, mirroring
    `MainWindowViewModel.RailSections`/`ActivateSection`.
    `SettingsViewModel.ActivatePage(pageId, focusHost)` builds and activates one of ~10 pages —
    **General**, **Keyboard Shortcuts**, **Accounts**, **SSH Keys**, **Git Profiles**, **AI
    Providers**\[Pro], **Agent CLIs**\[Pro], **Toolchains**\[Pro], **PR Intake**\[Pro], **Mainguard
    OS**\[Pro], **Daemon Logs**\[Pro], **About** —
    lazily and caches each row's Content; any page whose ViewModel is also `IDisposable` (currently only
    Daemon Logs) has its row's cache discarded on leaving so the next visit rebuilds fresh instead of
    reusing a disposed instance. The old small 440×560 single-screen dialog (pinned-top-menu-icon
    checkboxes — note the pinned strip itself is **not rendered** in the phase-2 shell, so pinning has
    no visible effect until a strip returns — plus a read-only About/versions footer bound to
    `VersionsViewModel`) is gone: the pin-checkbox rows moved onto the new **General** page
    (`SettingsPinRowViewModel` now lives on `GeneralSettingsViewModel`, not `SettingsViewModel`) and the
    About footer was promoted to its own **About** page (`VersionsView`, below).
    `MainWindowViewModel.OpenSettingsAsync(pageId = "General", focusHost = null)` reuses a singleton
    `_settingsWindow` (same pattern as the existing `_repoPicker` singleton) and constructs
    `SettingsViewModel` with all its page-factory dependencies; the old
    `OpenShortcutSettingsCommand`/`OpenShortcutSettingsAsync` (which opened `ShortcutSettingsWindow`
    directly) is gone, replaced by a private `BuildShortcutSettingsPage()` factory `SettingsViewModel`
    calls), `ConfirmationDialog`, `CheckoutConflictDialog`, `MergeCommitDialog`,
    `ConflictedFilesWindow`, `ConflictResolverWindow`, `DeviceFlowAuthDialog`,
    `InteractiveRebaseWindow`, `RemotesWindow` (T-10 remotes manager: add / rename / edit-URL / remove),
    `SubmodulesWindow` (T-16 submodules panel: per-row status chip + short SHA + URL, per-row "Update to
    remote"/"Open as repo", and Update all (init) / Sync URLs / Refresh actions; async commands with
    typed errors surfaced inline), `LfsWindow` (T-17 Git LFS panel: per-repo enable toggle,
    tracked-pattern list with track/untrack, LFS-object list with Downloaded/Pointer chips + short OID,
    Pull objects, and Prune with dry-run preview + confirm; shows a not-installed notice when git-lfs is
    absent), `AccountsWindow` (T-14 Accounts preferences — now a thin wrapper embedding the actual form
    content `AccountsView` (`UserControl`, `x:DataType="AccountsViewModel"`, extracted so it can be
    shared): per-host rows with token status, device-flow / PAT sign-in, sign-out, add-custom-host;
    still used by the Client edition's first-run flow in `App.axaml.cs`, while Settings' **Accounts**
    page hosts the same `AccountsView` directly — `RepoDashboardViewModel.ManageAccountsCommand` now
    redirects to `MainWindowViewModel.OpenSettingsAsync("Accounts", focusHost)` instead of constructing
    `AccountsWindow` directly, and `HandleGitActionException`'s git-auth-failure deep link still works,
    now landing on the Settings Accounts page with the failing host pre-filled), `SshKeysWindow` (T-14
    SSH keys, same thin-wrapper-over-extracted-`SshKeysView` pattern: list `~/.ssh` keys, generate
    ed25519 with optional passphrase, copy public key — `ManageSshKeysCommand` likewise redirects to
    `MainWindowViewModel.OpenSettingsAsync("SshKeys", focusHost)`), `FileHistoryView` (T-12 file-history
    dialog: rename-following revision list + the selected-vs-predecessor diff, rendered read-only from
    `PatchParser`, plus the v1 line-history filter; a `Window` opened from the staging-panel and
    diff-viewer "History of this file" context menus; loads on open), `BlameWindow` (T-33 blame dialog:
    a thin `Window` hosting `BlameView`, opened from the staging-panel and diff-viewer **"Blame this
    file"** context menus; its `OnOpened` turns the gutter on and loads blame for the pre-set file,
    making the T-11 gutter + T-32 "Why this line" popover reachable), `OperationHistoryWindow` (T-19
    operation-history panel: journaled operations newest-first with per-row Kind/description/time, a
    status chip (Applied/Undone/Superseded/Not undoable), and a per-entry Undo or Redo button; opened
    from the repo actions menu), `ReflogWindow` (T-20 reflog viewer &amp; recovery: a ref picker (HEAD +
    local branches) over a newest-first reflog list — each row's from→to short SHAs + first-line message
    + time — with a per-row destructive **Restore** (confirmed hard reset) and an inline **Create branch
    here** (name box → orphan-tip recovery); opened from the repo actions menu).
    - `ProfilesPageView`/`ProfilesPageViewModel` (T-21 Git profiles, now the Settings **Git Profiles**
      page — replaces the deleted `ProfilesWindow`: a profile list with per-row Apply/Edit/Delete, an
      inline editor (name / user.name / user.email / sign-commits + signing key), and a **cancel-safe
      delete** toast with Undo/Dismiss over the unchanged `ProfilesViewModel`; `ProfilesPageViewModel`
      implements `ISettingsPage.OnDeactivated` to call
      `RepoDashboardViewModel.RefreshAfterHostSurfaceAsync()` when navigating away, replacing the old
      dialog's post-close refresh) and `WorktreeWindow` (T-21 worktree manager: worktree list with
      main/locked/detached chips + short SHA and per-row Open/Remove, plus an Add form — path +
      existing-branch picker or new-branch name — with the checked-out-branch validation hint, and Prune;
      opened from the repo actions menu).
    - `PullRequestsWindow` (T-23 Pull Requests panel: an open-PR list — number/title/author/source→target
      with a draft badge, per-PR merge-method picker + Merge/Close/Open-in-browser — plus a Create-PR form
      (prefilled source=current branch, target=default branch, title=last commit subject) and a graceful
      **unsupported/not-connected** affordance when the origin host has no provider or no token; opened
      from the repo actions menu, the branch context menu's "Create pull request", and the command
      palette. **T-25** adds a per-PR **Review** button that opens an in-panel review section: submitted
      **reviews** (author + token-styled verdict badge — Approved/Changes-requested/neutral — + body),
      **inline comment threads** grouped by file path (path : `line N`/`outdated` chip + diff-hunk context
      + body), and a **submit-review** affordance (Comment/Approve/Request-changes verdict picker + body →
      Submit)).
    - `IssuesWindow` (T-24 Issues panel, sibling of `PullRequestsWindow`: an issue list —
      number/title/author, host-colored **label chips**, assignees, comment count, updated-at — with an
      Open/Closed segment filter, a New-issue form (title + body + optional comma-separated
      labels/assignees), per-issue Comment (inline box) / Close·Reopen / Open-in-browser, and the graceful
      **unsupported/not-connected** affordance; opened from the repo actions menu and the command
      palette).
    - `ChecksWindow` (T-26 CI Checks panel, sibling of `IssuesWindow`: for one commit, a compact **overall
      status badge** (✓/✕/• glyph in Success/Danger/Warning tokens + "n failing/passed" + a
      `✓ n · ✕ n · • n` count line, hidden when the commit has no checks) over a **run list** — per-run
      state icon, name, "View logs" (opens `DetailsUrl`), and **Re-run** for a re-requestable check-run —
      plus the graceful **unsupported/not-connected** affordance; opened from the commit context menu's
      "View CI checks…" and the commit-detail "Checks" button. The compact badge is also shown inline on
      the selected commit's detail card in `CommitTimelineView` (bound to
      `CommitTimelineViewModel.SelectedCommitChecks`, loaded best-effort off-thread when the origin host
      is supported/signed in). Live fetch/re-run validation deferred to the T-26 manual matrix).
    - `NotificationsWindow` (T-27 Notifications inbox, sibling of `IssuesWindow`: the authenticated user's
      notifications for the origin host, **grouped by repository** (owner/repo header + count) with each
      thread's **reason chip** + **subject-kind icon** (PR/issue/commit/release/discussion) + title +
      updated-at and **unread styling** (accent dot + bold title); per-item **Mark read**, toolbar **Mark
      all read** + an **Unread only / All** segment toggle + Open-in-browser, and the graceful
      **unsupported/not-connected** affordance; chips/icons from tokens; opened from the repo actions menu
      and the command palette. Live fetch/mark-read deferred to the T-27 manual matrix).
    - `ReleasesWindow` (T-28 Releases panel, sibling of `IssuesWindow`: an existing-release list — name,
      **Draft/Pre-release badges** (token-styled chips), tag, author, published date, open-in-browser —
      plus a **New-release composer** — a new-tag box or an existing-tag picker + a target-branch box
      (prefilled from the current branch), a title, a notes editor with an **Auto-generate notes** button
      (fills from the fully-local `GenerateNotes`), Draft/Pre-release toggles, and **Publish** — with the
      graceful **unsupported/not-connected** affordance; opened from the repo actions menu and the command
      palette. Live publish deferred to the T-28 manual matrix).
    - `ConflictResolverWindow` is a synchronized 3-pane merge editor (Ours | Result | Theirs) on three
      `AvaloniaEdit` editors with filler-line alignment, lock-step scrolling, and `MergeBandRenderer`
      (region tints + gutter accept-chevrons), all in its code-behind; resolution logic stays in the
      ViewModel/engine.
    - `ApiKeySettingsView` (P2-01 the Settings **AI Providers** page: provider dropdown + masked key entry
      + validate-then-save, per-provider stored-key rows with Remove, the health/status line, and the
      CLI-OAuth entry — root is now a `UserControl` (it used to be its own window reached via
      `ManageApiKeysCommand`, which is gone along with the rest of the toolbar's Tools-menu deep links),
      and `ApiKeySettingsViewModel` implements `ISettingsPage`), `CliOAuthTosDialog` (the modal Anthropic
      subscription-OAuth ToS notice — "I understand" records the acknowledgment before the option
      activates), `BootstrapProgressView` (P2-05 staged-checklist bootstrap window: a card per step with a
      distinct icon-per-state encoding — pending/running/done/failed, not colour-only — a per-step log
      tail, an error banner, and Start/Cancel; design tokens + component classes only, works in the light
      Daylight Loom theme).
    - `EgressAllowlistView` (P2-07 agent egress allowlist editor `Window`: the user-visible default-deny
      host list with per-row Remove + a git-host `defeats A6` marker, an A6 warning banner when any entry
      re-opens a git route, and an add-host form; design tokens only — no Docker/engine reference, reaches
      the daemon via the gateway seam).
    - `PrIntakeSettingsView` (P2-12 Settings **PR Intake** page: the intake on/off switch, poll interval,
      bot-author list, a subscribe-a-repository form and the daemon's subscribed-sources list, plus a
      daemon-refusal banner; design tokens + component classes only, five-theme clean — paired with
      `PrIntakeSettingsViewModel`). **It is a `UserControl`, and that is the fix, not a detail:** it
      shipped as a top-level `Window` with zero references anywhere in the repo — no menu, no button, no
      test, no harness — so external PR intake had a complete settings dialog nothing could open, and a
      `Window` cannot be hosted as `ContentControl.Content` either, so it could not have been dropped into
      the Settings rail as-is.
  - Control-center integration (Lane E Part 3, revised 2026-07-11 — mock-backed,
    docs/design/ControlCenterDesign.md + VibeModeDesign.md): the coordinator surfaces live **inside
    MainWindow** behind its **section rail** (leftmost column: expandable/collapsible like the repo
    sidebar, collapsed = icons + tooltips; top third = Repo viewer / Coordinator-with-attention-badge /
    Resources / the relocated host icons PRs·Issues·Notifications·Releases, bottom two-thirds = the live
    agent list whose tooltips carry name — state · task, the kill switch at the rail's foot). **Step
    2d** extracted that agent rail (the worker list + the kill switch) into its own
    `AgentRailView`/`AgentRailViewModel` — the shell hosts it as opaque `object` content
    (`MainWindowViewModel.AgentRailContent` → `IAgentPlatformSurface.AgentRailContent`, both `object?`)
    through a `ContentControl`+ViewLocator gated by `ShowsAgentRail`, so the shell names no Pro rail
    type;
    - `AgentRailViewModel` is a thin view over `ControlCenterViewModel` (the single owner of the agent
      projection + kill-switch state), and the rail-exclusive `railBadge`/`railKill` styles moved into
      `AgentRailView` with the markup.
    - `ControlCenterView` (renamed from `CoordinatorSurfaceView` in 2d so the
      `ControlCenterViewModel`→`ControlCenterView` name transform resolves it; MainWindow now hosts it via
      a `ContentControl`+ViewLocator (`{Binding ControlCenter}`) rather than a hard-named element — the
      preset-aware surface MainWindow swaps in: freeze banner + center content (the coordinator's inline
      terminal / the focused agent's **dock workspace**) + the queue rail; **P2-47 #6 mounts the reused
      `AgentWorkspaceViewModel` at the agent-focus mount point** (replacing the bare `AgentDocumentView`)
      with `ControlCenterViewModel.SelectAgent` calling `ShowAgent(...)`, and **#5 makes its primary pane
      a live terminal** — a fresh `TerminalViewModel` + `DaemonTerminalGateway` (from
      `DaemonBackedOrchestrator.CreateTerminalGateway`) attached to the agent's PTY over
      `TerminalService.Attach`, torn down/rebuilt per agent (mock harness → placeholder, daemon-down
      attach swallowed); **#7 adds the review-cockpit overlay** (`ReviewCockpit` shown when the merge
      rail's review action resolves a live `GetMergeDiff`); **PR3 adds the coordinator-as-CLI cards** at
      the coordinator focus's head — "Start a coordinator" (installed-CLI picker over
      `ControlCenterViewModel.InstalledClis` + `StartCoordinatorCommand`, shown only when the backing
      services are a real CLI host and no coordinator is live; a start spawns the CLI and
      `RefreshCoordinatorCli` binds its interactive terminal **inline on this surface** — never routing
      through `SelectAgent`; that terminal is where you talk to it AND where CLI login happens; while
      spawning or before the CLI draws its first PTY frame the terminal area shows a **loading animation**
      (`IsCoordinatorConnecting`, cleared on `TerminalViewModel.HasReceivedOutput`) — with **Stop /
      Restart** while live (`RestartCoordinatorCommand`; `StopCoordinatorCommand`). **Stop is the escape
      hatch from a wedged launch:** it is reachable mid-spawn (`ShowStopCoordinator` covers
      starting/connecting, not just live), it **confirms first** via the `CoordinatorStopPromptViewModel`
      overlay (wording adapts: "Stop the coordinator?" live vs "Cancel startup?" mid-launch), and
      confirming **cancels the in-flight spawn** (`RunStartupAsync` threads a `_startupCts` into
      `StartCoordinatorAsync` — the daemon tears the partial spawn down on the cancelled RPC) **and** ends
      the live session (full CLI + sandbox teardown). A **connect watchdog** (`CoordinatorConnectTimeout`,
      45 s, test-shortenable) flips `CoordinatorConnectTimedOut` when connecting overstays with no first
      frame and no death — the loader stops pretending and points at Stop instead of spinning forever (it
      never auto-kills — a real first-launch sandbox build can be slow). **ISSUES-LOG #23** splits that
      stalled card in two on `CoordinatorHasStarted`, which is read off the coordinator's CURRENT
      lifecycle state rather than off a first-frame EVENT: a coordinator adopted across a daemon restart
      is already `Working` the first time the projection sees it, has no transition left to fire, and its
      terminal can never draw again (its PTY died with the daemon that owned it) — so it used to sit on
      "Still starting the coordinator" forever. A started coordinator now gets the honest
      "running — its terminal isn't attached" card with **Restart** on it, and a running session's
      `Detail` (e.g. the reconciler's adoption line) is no longer rendered as launch progress nor allowed
      to buy the watchdog's 20-minute working budget. Restart itself no longer degrades into a silent
      Stop when nothing is picked in the (hidden-while-live) CLI picker: it falls back to the installed
      CLI, and says why when there is none. And the honest dead-coordinator
      card (`IsCoordinatorDead` — the newest coordinator-role session reached a terminal state: says it
      ended, keeps its terminal open for the replay — `ShowCoordinatorTerminal` stays true (the daemon
      retains the bound session's replay — the CLI's final output is the why), and un-gates the start card
      so a new coordinator is always startable over a corpse); **the coordinator is its own entity owned
      by these cards — coordinator-role sessions NEVER appear in the workers rail** (`RefreshAgents`
      filters them; the exit guard's `LiveAgentCount` still counts a live coordinator, never a dead one),
      and the rail's worker rows carry a quiet role word (subagent via `AgentRowViewModel.RoleLabel`);
      layout preset = the Settings **General** page's Layout picker (formerly a File-menu → Layout
      submenu), Flight Deck default / Conversation Deck, persisted like Theme via
      `UserPreferences.WorkspaceLayout`; The Loom retired). **Panel sizing (2026-08-06):** the surface
      grid was `ColumnDefinitions="Auto,*,8,300"` — a LITERAL 8px gap where a splitter belonged and a
      hard-coded 300px queue — so a wider window only fed the terminal and the boundary had nothing to
      grab. The queue column is proportional now (`3*` / `*`, `MinWidth` 320 / 280, `MaxWidth` 640) with
      a real `GridSplitter` between them, and the telemetry card's fixed `Height="240"` became a second
      (row) splitter. Both carry the local `GridSplitter.PanelSeam` style: `BorderHairline` at rest,
      `AccentBrush` on pointer-over / pressed / focus, `Focusable` for arrow-key resizing. The row
      splitter rewrites its `Auto` row to a pixel length on first drag, which would leave a hole when
      Conversation Deck hides telemetry — `ControlCenterView.axaml.cs` parks that height on hide and
      restores it on show (its only job). No width persistence: none of the shell's other splitters
      persist either. `QueueRailView` (the mock merge-queue rail; its rows now put the state word in an
      `Auto` column so a 32-hex agent id can no longer push it off the edge, trim the name with a
      full-value tooltip, and **wrap** the branch / SHA identifiers — horizontal scrolling is explicitly
      `Disabled` there because enabling it measures at infinite width and silently defeats that wrap.
      It also carries the entry-**lifecycle** actions, which is the rail the shipped Control Center
      actually hosts: a `Button.DangerQuiet` **Discard** on every non-terminal row — the reported defect
      was an entry left by a stopped agent with no control on it at all, so this is per-row rather than
      on the front one — behind a two-step confirm whose question states that the entry will not be
      merged and the branch is left alone, plus a `Button.Secondary` "Clear stalled run" shown only when
      the daemon reports no run behind a `Verifying` state. The rail's ONE accent stays the Review CTA:
      nothing added here is `Button.Accent`, and the destructive action reads destructive by hue. Each row
      also hosts `VerificationPanelView` — see below), **`VerificationPanelView.axaml`** (H4 — the verdict
      line plus the recorded test output, expandable in place. It lives ON THE ROW because the row is the
      only surface every entry has: an entry whose agent is gone has no worker document, and one that
      never verified cannot reach the review cockpit, so a log reachable only from either would be missing
      for exactly the entries that failed. Collapsed by default and fetched on first expand — the output is
      a daemon-side file read per entry and the rail re-projects on every queue event. The reader is
      BOUNDED rather than the log being shortened: a `MaxHeight` cap with its own scroll, so a 256 KiB tail
      cannot push the row's actions off the surface, and horizontal scrolling is ON here (unlike the rail's
      wrapping identifiers) for the reason `ReviewCockpitView` scrolls its diff sideways — wrapping a stack
      trace destroys the alignment that makes it readable. `SelectableTextBlock` in `FontMono`, because the
      reason a person opens a failing log is usually to copy the assertion out of it. The same control is
      hosted by `AgentDocumentView`'s review section),
      `MergeQueueView` (P2-10: the merge-queue rail bound to the real `MergeQueueViewModel` — per-row
      Merge/Override, gate reason line; **harness-only** — constructed solely by
      `MergeQueueRenderHarness`, never by the app), `CoordinatorPanelView` (the coordinator
      conversation — **retained for a possible future surface but no longer rendered**; since 2026-07-22
      the coordinator is driven from its inline terminal, not this bespoke GUI. It no longer *defines* the
      plan cards: it hosts `PlanGateView` like everyone else), `PlanGateView` (**the phase-2 plan gate,
      the one definition of the approval card** — the daemon's backpressure sentence, the escalated
      workers, and one Approve/Reject card per blocked worker with Scope + Approach + the feedback box.
      Split out of `CoordinatorPanelView` because the coordinator conversation moved into a real PTY and a
      PTY cannot render a button: the gate is a decision about a *worker*, taken out of band from whatever
      the coordinator is saying, so it has to be hostable without the chat. **Mounted by
      `ControlCenterView` above the coordinator's terminal** — the silence it explains is the silence in
      that pane — collapsed entirely by `HasGateContent` when nothing is waiting. **The plan-mode toggle
      (2026-08-30) lives at the top of this view**: a checkbox bound `OneWay` plus a `Command` (a two-way
      binding would render a setting the daemon rejected or never received) over the daemon's own summary
      sentence, in `WarningBrush` when approvals are OFF. `HasGateContent` now also includes
      `!PlanModeEnabled`, because with approvals off nothing is ever pending and a gate that only appeared
      for pending cards would go permanently dark — which is exactly what an IDLE orchestration looks
      like), `ReviewCockpitView` (P2-11: the review
      cockpit — risk-ranked file/hunk list (ordering only, nothing hidden), per-hunk provenance chips, the
      pinned item-by-item flagged gate panel, the test-delta strip, footer Bring-local/Merge; bound to the
      real `ReviewCockpitViewModel`, **mounted in `ControlCenterView` as a dismissable overlay (P2-47
      #7)** built from the live `GetMergeDiff` RPC; **no rule logic in the axaml/code-behind** — invariant
      1; its diff readout is the one pane on this surface that scrolls HORIZONTALLY — diff lines are
      source, so wrapping them would break column alignment and the old clip simply hid the rest),
      `AgentDocumentView` (terminal tail + plan tree + health strip + flagged-gate review section +
      composer/prompt queue), `TelemetryPanelView` (sandbox-health fact table; its trimmed Detail column
      carries a full-value tooltip — a blocked host you cannot read is a blocked host you cannot act on),
      `QueueSeedingPanelView` (the DEV-ONLY seeding card under the telemetry card in the right rail —
      warning-hued "dev" pill, state/flavor/count pickers, hold + verify-fails, the preset buttons
      (incl. "Plan-gated": one in-scope + one out-of-scope entry through the REAL plan pipeline) and
      the DangerQuiet Clear; the hosting `Border` in `ControlCenterView` is `IsVisible`-bound to
      `QueueSeeding` not-null, so a daemon without the seeding boot flag never shows a trace of it),
      `ResourceMonitorView` (the
      Resources **tab** — task-manager style: totals header + CPU history decomposing into one live row
      per agent (CPU/RAM/spend/state/task, stable order so an open context menu never gets yanked),
      right-click Pause/Resume + End task with a C-pattern confirmation; **P2-47 #4 adds the editable
      per-day spend cap** (USD/day + tokens/day round-tripping through
      `ITelemetryService.Get/SetSpendBudgetAsync` → the `SetBudgets` RPC, preserving the per-agent caps);
      `MainWindowViewModel.ResourceMonitor` holds the lazily-created monitor as `object?` (2d) and drops
      it into a `ContentControl` that ViewLocator resolves to `ResourceMonitorView`), `RepoPickerWindow`
      (the repositories tree, moved out of MainWindow's docked sidebar 2026-07-11 so the workspace runs
      full-width: shares `MainWindowViewModel` as DataContext, carries the Repository/WorkspaceCategory
      templates, drag-to-categorize, rename/delete keys, and the delete-confirmation overlay; opened via
      `OpenRepoPickerCommand`, single instance, double-click opens the repo and closes the picker. Each
      row's surface is `Controls/RepoRow`, so Enter/Space and UI Automation `Invoke` open it too (W2 —
      the rows previously exposed no activation path to the keyboard or to assistive tech at all); the
      row is `AutomationProperties.Name`d from `DisplayName` and shows the hover wash on `:focus-within`
      so a Tab-focused row is visible. The
      title-bar hamburger used to open this directly, but it is now a pure compact/expand toggle
      (`ToggleToolbarCommand`/`IsToolbarExpanded`, persisted as `UserPreferences.ToolbarExpanded`) and
      **Select Repo** is one of the four items — Select Repo / Close Repository / Settings / Exit — the
      JetBrains-style expanded row swaps in for the compact Branch/Sync/Repository row), `VibeModeView`
      (chat cards + triage + live-preview placeholder + publish flow — headed for its own app; not
      reachable from MainWindow, kept alive by the render harness). Rail refinements (2026-07-11
      feedback): uniform 32px item height in both states (icons never shift), explicit token-driven hover
      (no Fluent white flash) with dimmed-not-filled disabled items, `AccentSelection` active-section
      tint, the kill switch red at rest, the agent list scrollable both axes. **Everything is a tab**
      (second-pass feedback): the host surfaces (PRs/Issues/Notifications/Releases) open as MainWindow
      sections — their window content was extracted verbatim into
      `PullRequestsView`/`IssuesView`/`NotificationsView`/`ReleasesView` UserControls (the windows remain
      as thin wrappers for the legacy dialog entry points: palette, branch menu, blame links), built from
      shared factories on `RepoDashboardViewModel` (`Create*ViewModel()`), hosted via ViewLocator with
      `CloseAction` returning to the Repo viewer and the ahead/behind refresh firing on tab leave. **Agent
      prompting mode** (the Settings **General** page's Agent-prompting toggle, formerly a File → Agent
      prompting submenu, persisted as `UserPreferences.DirectAgentPrompting`): "Direct to agents" enables
      the agent document's composer; "Through the Coordinator" disables it with an explanatory watermark —
      `ControlCenterViewModel.SetDirectPrompting` propagates to every open document.
      `TerminalView.axaml(.cs)` (P2-03) — the interim terminal surface hosting a
      `controls:TerminalControl`; its code-behind only binds the concrete control to `TerminalViewModel`
      (hands it the control as `ITerminalView`, routes the layout-resize back) — no VT/terminal logic in
      code-behind (rejection trigger).
- **`ViewModels/`** — one per view above, plus row/item VMs with no view of their own:
  `PreCommitFindingsViewModel`/`PreCommitFindingGroupViewModel`/`PreCommitFindingRowViewModel` (T-30
  pre-commit scanner panel + one severity group + one finding row: runs `IPreCommitScanner.ScanStaged`
  off the UI thread, groups findings by severity, exposes `HasBlockers`/`IsAllClear`/counts, the
  `AutoScanEnabled` toggle persisted via `ISettingsService`, and the **Commit anyway** override —
  which raises `CommitConfirmed` so `StagingPanelViewModel` resumes the exact commit; the row exposes
  severity/kind as mutually-exclusive booleans so the View picks a token — no color in the VM.
  `StagingPanelViewModel` owns one, gates both commit commands on it (scan → any blocker pauses for an
  explicit override; warnings are advisory; disabled toggle skips the scan), and wires reveal-in-diff
  to select the finding's file), `CommitComposerViewModel` (T-31 conventional-commit composer: holds
  the structured fields, assembles a live `Preview` + commitlint `Issues` through the pure
  `Commits/ConventionalCommitBuilder`, exposes `HasErrors`/`DescriptionOverLimit` and add/remove
  co-author + issue-ref commands, raises `Changed` so the owner re-gates; UI-free, no color — the View
  maps `IsError` to a Danger/Warning token. `StagingPanelViewModel` owns one, exposes the
  `UseStructuredComposer` toggle persisted to `UserPreferences`, and in structured mode commits its
  assembled message through the unchanged commit path + T-30 scan (errors block the default Commit;
  the plain box is the escape hatch)), `CommandPaletteViewModel` (T-18: the Ctrl+P palette — snapshots
  the candidate set on open via a provider, fuzzy-filters through Core's `FuzzyMatcher`, groups by
  category with header rows in browse mode, and exposes header-skipping selection + activate;
  `PaletteEntry`/`PaletteSegment`/`PaletteRowViewModel` are its row types; `MainWindowViewModel` owns
  the `ActionRegistry`, builds the entries (enabled actions + local branches + bookmarked repos), and
  routes global gestures through `InvokeActionByIdCommand`),
  `ShortcutSettingsViewModel`/`ShortcutRowViewModel` (T-18 rebind screen: live conflict recompute +
  persist only the diffs-from-default), `CommitRowViewModel` (carries the T-15 signature badge state —
  `SignatureStatus`/signer → verified/untrusted/bad derived flags + tooltip; badge holder collapses
  when unsigned — and the T-09b `RefLabels`: the branch/tag chips whose tip lands on this commit,
  drawn inline in the row and used as the drag source/target for drag-to-rebase/merge;
  `RefLabelViewModel` is the per-chip data (RefName/DisplayName/Sha/IsTag/IsCurrentHead), populated by
  `CommitTimelineViewModel.BuildRefDecorations`), `MenuItemViewModel`, `BranchBrowserViewModel`,
  `ToastViewModel` (#85 — one floating notification: message + severity, a manual close, and an expand
  toggle for long text, owning its own auto-dismiss timer so stacked toasts age independently;
  `RepoDashboardViewModel.ShowNotification` adds them to a `Toasts` collection capped at 3, newest at
  the bottom, and disposes each on teardown; `MainWindowViewModel` hosts the window-level sibling
  stack — `MainWindowViewModel.Toasts`/`ShowToast`, same cap/dispose rules, rendered by `MainWindow`'s
  bottom-right overlay for shell-wide events like the daemon auto-update outcome),
  `PinnableMenus`/`SettingsViewModel` (#78, completely restructured — from a small single-screen
  dialog with pinned-icon checkboxes + an About footer into the **page-rail host** for the whole
  Settings window: owns `Pages` (`ObservableCollection<SettingsPageRowViewModel>`) +
  `ActivePageContent` (`object?`) + `ActivatePage(pageId, focusHost)`, mirroring
  `MainWindowViewModel.RailSections`/`ActivateSection`; builds all ~10 pages (General / Keyboard
  Shortcuts / Accounts / SSH Keys / Git Profiles / AI Providers\[Pro] / Agent CLIs\[Pro] /
  Toolchains\[Pro] / Mainguard OS\[Pro] / Daemon Logs\[Pro] / About)), `SettingsPageRowViewModel` (a
  `RailSectionViewModel`-shrunk-down analog for one Settings-sidebar row:
  id/label/icon/`IsActive`/`ActivateCommand` + a lazily-built, cached Content — the cache is dropped
  by `SettingsViewModel` whenever the page's ViewModel is also `IDisposable`, so a disposed
  `DaemonLogsViewModel` is never reused), `SettingsPinRowViewModel` (#78 — the pinned-sidebar-icon
  checkbox row; **moved off `SettingsViewModel` onto `GeneralSettingsViewModel`**, one row per
  pinnable rail destination, persisted to `UserPreferences.PinnedMenuIds` via `ISettingsService`;
  `MainWindowViewModel.RebuildRailSections` filters the section rail by this set live on every toggle,
  so unpinning a host destination removes it from the sidebar immediately), `GeneralSettingsViewModel`
  (new Settings **General** page — Theme / Layout / Agent-prompting / Close-to-tray / Stop-VM-on-exit
  / the pinned-sidebar-icon rows above, all absorbed from the old File-menu dropdown submenus into one
  page; the theme picker is data-driven off `ThemeManager.Themes` + the System pseudo-row via
  `SettingsThemeRowViewModel` — key/display-name/`IsSelected`, refreshed against the persisted choice
  on every pick, so the lineup lives only in `ThemeManager` and the active choice is marked), `ISettingsPage` (`Mainguard.UI.ViewModels` — the minimal `OnActivated()`/`OnDeactivated()`
  interface, same weight class as `IShellRailHost`, that every Settings page's ViewModel implements so
  `SettingsViewModel`'s page-switch logic can notify it), `VersionsViewModel` (promoted from the old
  small Settings dialog's read-only About/versions footer card into its own Settings **About** page
  hosted by `VersionsView`: the app's own informational version plus the daemon + MainguardOS payload
  versions from one `GetDaemonInfo` probe over an injectable query seam (same
  null-means-`Unimplemented`, throw-means-unreachable contract as `DaemonAutoRefresh`); honest
  degraded states — "unreachable", "pre-0.2.0", "not stamped" — never a crash or a blank, concurrent
  refreshes coalesce, and it now implements `ISettingsPage` — `OnActivated` triggers the version
  fetch, replacing the old window's `Opened` hook), `InteractiveRebaseViewModel`,
  `MergeChunkViewModel` (one per merge chunk in the resolver), `ConflictedFileItem`,
  `DiffHunkRowViewModel`/`DiffLineRowViewModel` (partial-staging hunk/line rows in the diff viewer;
  carry T-13 intra-line `HighlightSpans`/`TrailingWhitespaceSpan`), `ImageDiffViewModel` (T-13
  image-diff state: before/after `Bitmap`s + sizes + `SwipePosition` + `IsOnionSkin` mode toggle;
  `HasBothImages` gates the overlay — the T-13b reveal interaction lives in `ImageDiffControl`),
  `RemotesViewModel`/`RemoteRowViewModel` (T-10 remotes-manager dialog + one editable row each),
  `SubmodulesViewModel`/`SubmoduleRowViewModel` (T-16 submodules panel + one row each: loads
  `GetSubmodules`, runs init/update/update-remote/sync off the UI thread with an `IsBusy` guard and
  typed errors → `ErrorMessage`; "open as its own repo" routes back through
  `MainWindowViewModel.OpenRepository` via a callback),
  `LfsViewModel`/`LfsPatternRowViewModel`/`LfsFileRowViewModel` (T-17 Git LFS panel + tracked-pattern
  and LFS-object rows: loads availability/enable-state/patterns/objects, runs install-uninstall (the
  enable toggle)/track/untrack/pull off the UI thread with an `IsBusy` guard and typed errors; prune
  previews with `--dry-run` then confirms through `IConfirmationService` before the real prune),
  `AccountsViewModel`/`AccountRowViewModel` (T-14 Accounts page: per-host provider metadata + token
  status keyed `token_<host>`, PAT store/remove offline, device-flow sign-in wiring),
  `SshKeysViewModel`/`SshKeyRowViewModel` (T-14 SSH keys page: list/generate/copy over
  `SshKeyService`), `BlameViewModel` (T-11 blame: loads `GetBlame` off the UI thread on `Task.Run`
  with a `CancellationToken` cancelled on file switch — never a stale gutter — through `BlameCache`;
  click-a-line selects that commit via `WeakReferenceMessenger`; **T-32** right-click resolves the
  commit's context via `ICommitContextService` off-thread (`IsContextBusy`-gated) into a
  `BlameCommitContextViewModel` (`LineContext`) — the PR chooser + linked-issue rows — routing a jump
  through injected sinks (PR/Issues panel or browser);
  `BlameCommitContextViewModel`/`CommitContextPrRowViewModel`/`CommitContextIssueRowViewModel` are its
  row types. **T-33** wires the live entry point: `RepoDashboardViewModel.OpenBlameAsync` constructs
  this VM with a `CommitContextService` on the shared PR/Issues `HttpClient` + the panel-routing sinks
  and hosts it in `BlameWindow`), `FileHistoryViewModel` (T-12 file-history dialog: loads
  `GetFileHistory` off the UI thread and auto-selects the newest revision; the selection→predecessor
  diff recomputes off-thread with cancellation so rapid paging never renders a stale diff; the
  introducing revision renders as all-additions and binary blobs show a placeholder; the line-history
  filter narrows the list via `LineHistoryFilter`),
  `OperationHistoryViewModel`/`OperationHistoryRowViewModel` (T-19 operation-history panel + one row
  each: loads `IOperationJournal.GetHistory`, drives per-entry `Undo`/`Redo` with typed errors →
  `ErrorMessage`, reloads after each action; a row's `CanUndo`/`CanRedo`/`StatusText` derive from the
  entry's undoable/undone/truncated flags, and it refreshes the workspace via an `onChanged`
  callback), `ReflogViewModel`/`ReflogRowViewModel` (T-20 reflog viewer + one entry row each: loads
  `GetReflog` for the picked ref (HEAD + local branches), and drives the two recovery actions off the
  UI thread with an `IsBusy` guard and typed errors — **Restore** gates on `IConfirmationService` then
  calls the journaled `ResetToCommit(Hard)`, **Create branch here** validates an inline name then
  calls the journaled `CreateBranchAt`; both undoable via T-19, workspace refreshed via an `onChanged`
  callback), `PullRequestsViewModel`/`PullRequestRowViewModel` (T-23 Pull Requests panel + one PR row
  each: gates on `IPullRequestService.IsSupported` (graceful unsupported/no-token affordance), loads
  the open-PR list off the UI thread under an `IsBusy` guard with results marshalled to
  `Dispatcher.UIThread` and typed errors → `ErrorMessage`; the create form is prefilled from
  HEAD/default-branch/last-commit and **disabled with a hint on a detached/unborn HEAD**; each row
  drives Merge(method)/Close/Open-in-browser through the service; opened from the repo actions menu, a
  branch-context "Create pull request" entry, and the T-18 palette (`ActionIds.ViewPullRequests`).
  **T-25** adds the review flow: a row's **Review** opens the selected PR's reviews + inline comment
  threads (loaded off the UI thread, results marshalled to `Dispatcher.UIThread`, grouped by path) and
  a submit-review command gated by `IsBusy`/`IsSupported` (a body is required unless the verdict is
  Approve); `ReviewRowViewModel` exposes the verdict as mutually-exclusive booleans so the View picks
  a design-token badge (no color in the VM), `ReviewThreadViewModel`/`ReviewCommentRowViewModel` carry
  the per-path thread + one inline comment (`IsOutdated` when `line==null`). **T-29** adds a per-row
  **Check out locally** that folder-picks (default `../<repo>-pr-<n>`) then runs
  `CheckoutPullRequestWorktree` off the UI thread under the `IsBusy` guard and, on success, offers an
  **Open worktree** button (routes through the T-16 open-as-repo path) — both the folder pick and the
  open-route are injected callbacks so the flow is VM-testable),
  `IssuesViewModel`/`IssueRowViewModel`/`IssueLabelChipViewModel` (T-24 Issues panel + one issue row +
  one label chip each: gates on `IIssueService.IsSupported` (graceful unsupported affordance), loads
  the list off the UI thread under an `IsBusy` guard with results marshalled to `Dispatcher.UIThread`
  and typed errors → `ErrorMessage`; the Open/Closed filter reloads; New-issue create + per-row
  Close/Reopen, inline Comment, and Open-in-browser route through the service; the label chip paints
  the host's hex as its background with an **auto-contrast** (luminance-based black/white) foreground
  — the one allowed data-driven color, everything else tokens; opened from the repo actions menu and
  the palette (`ActionIds.ViewIssues`)),
  `NotificationsViewModel`/`NotificationGroupViewModel`/`NotificationRowViewModel` (T-27 Notifications
  inbox + one repo-group + one thread row each: gates on `INotificationService.IsSupported` (graceful
  unsupported affordance), loads the list off the UI thread under an `IsBusy` guard with results
  marshalled to `Dispatcher.UIThread` and **grouped by `RepoFullName`** (newest thread first inside
  each group) with typed errors → `ErrorMessage`; the **Unread only** toggle reloads, per-row **Mark
  read** (gated to unread rows) / **Mark all read** route through the service then reload to reflect
  the host result, Open jumps to the thread URL; the row exposes the reason as a display string + the
  subject kind as mutually-exclusive booleans so the View picks a design-token chip/icon — no color in
  the VM; opened from the repo actions menu and the palette (`ActionIds.ViewNotifications`)),
  `ReleasesViewModel`/`ReleaseRowViewModel` (T-28 Releases panel + one release row each: gates on
  `IReleaseService.IsSupported` (graceful unsupported affordance), loads the list off the UI thread
  under an `IsBusy` guard with results marshalled to `Dispatcher.UIThread` and typed errors →
  `ErrorMessage`; the composer prefills the target from the current branch and loads existing tags for
  the picker; **Auto-generate notes** runs the local `GenerateNotes` on a background `Task` and fills
  the body; Publish routes a `CreateRelease` through the service then reloads; the row exposes
  Draft/Prerelease flags so the View picks token-styled badges — no color in the VM; opened from the
  repo actions menu and the palette (`ActionIds.ViewReleases`)),
  `ProfilesViewModel`/`ProfileRowViewModel` (T-21 Git profiles manager + one row each: CRUD over
  `IProfileService`, the duplicate-name error surfaced inline, **cancel-safe delete** — a deleted row
  keeps an Undo that `Restore`s it — and per-row Apply-to-this-repo; `HasRepo` gates Apply),
  `WorktreePanelViewModel`/`WorktreeRowViewModel` (T-21 worktree panel over the T-07 backend + one row
  each: list/add/open/remove-force/prune off the UI thread with an `IsBusy` guard and typed errors;
  `CanCreate` disables the button when the picked existing branch is already checked out in another
  worktree — `SelectedBranchIsCheckedOut` — since git forbids the double checkout).
  - `CloneDashboardViewModel` (**P2-48: multi-provider**) lists the signed-in account's repos via
    `IHostRepositoryService` as host-agnostic `RemoteRepository` and clones the chosen one; a
    `Providers`/`SelectedProvider` segmented selector (row VM `CloneProviderOption`) is driven by which
    `KnownHosts` have a stored token (GitHub + GitLab today — GitLab appears once its token is stored
    via the Accounts screen; only shown when >1), each provider's repos loaded per-host with the clone
    credential resolved per-host by `CredentialResolver` (`token_<host>`, no more GitHub-only
    `github_token`); GitHub's own in-screen device-flow sign-in (`GitHubAuthClient`) is preserved, and
    it still carries the T-21 clone-progress state
    (`IsCloning`/`CloneProgressPercent`/`CloneStatusText`/`CloneErrorText` +
    `CancelCloneOperationCommand`), driving a clone through `ICloneService` via `RunCloneAsync`.
  - `AnalyticsViewModel` (T-22) runs the two `RepositoryAnalyzer` walks off the UI thread under a
    `CancellationTokenSource` cancelled on `Dispose` (the workspace-swap disposes it), folds the commit
    stats through the pure aggregators, and builds the four LiveCharts series + their observable axis
    arrays — every paint from `Charts/ChartTheme`;
  - `HasCommitData`/`HasLanguageData` drive the empty states. All derive from `ViewModelBase.cs`.
  - `RepoDashboardViewModel` owns the T-12 `OpenFileHistoryAsync` and T-33 `OpenBlameAsync` dialog entry
    points (wired from the diff-viewer/staging-panel "History of this file"/"Blame this file" menus),
    the T-10 push-option commands (force-with-lease / set-upstream / push-tags), the `AutoFetchService`
    (Watch/Unwatch; surfaces the "last fetched N min ago" label with >15-min dimming), and is
    `IDisposable` (the Settings rework deleted its
    `ManageProfilesCommand`/`ManageApiKeysCommand`/`ManageAgentClisCommand`/`ViewDaemonLogsCommand`/`AddReposToOsCommand`/`RebuildSandboxImagesCommand`
    entirely — their toolbar buttons are gone, moved to Settings, and none had any other deep-link call
    site; `ManageAccountsCommand`/`ManageSshKeysCommand` still exist but now redirect to
    `MainWindowViewModel.OpenSettingsAsync` instead of constructing `AccountsWindow`/`SshKeysWindow`
    directly) — `MainWindowViewModel` disposes the outgoing workspace so the fetch loop + watcher don't
    leak. The conflict resolver (`ConflictResolverWindowViewModel` + `ConflictedFilesViewModel`) is
    engine-driven off `IMergeDiffService` + the conflict-index service methods; it never parses
    working-tree markers. **Lane E control-center prototype VMs** (all on `Core/Agents` interfaces,
    mock-backed, event-driven requery per OPS §3.4): `ControlCenterViewModel` (owns the
    `MockOrchestrator`, the LIFO agent rows, kill-switch state, the two workspace layouts (Flight Deck
    default / Conversation Deck; unknown keys fall back), `IsCoordinatorFocus` (coordinator inline
    terminal vs agent-document center content, driven by the section rail;
    **`CoordinatorTerminal`/`ShowCoordinatorTerminal` + `Start`/`Stop`/`RestartCoordinatorCommand`** own
    the coordinator surface now), and the CPU-sparkline/spend readouts; app-lifetime instance owned by
    `MainWindowViewModel.ControlCenter`, which also carries `IsRepoSectionActive`/`IsRailExpanded` + the
    Show*/SetLayout/ToggleRail/OpenResourceMonitor commands and restores
    `WorkspaceLayout`/`SectionRailExpanded` from settings), `AgentRowViewModel` (lifecycle →
    badge-geometry key + a single `AgentStatus` the View colours through the one
    `AgentStatusBrushConverter`; `NeedsAttention` from `AttentionPolicy`; no color and no second
    status→brush map in the VM — P2-13; `RefreshBadgeBrush()` re-runs the converter on live theme
    switch), `AgentRailViewModel` (2d — the Pro agent rail as its own surface: a thin view over
    `ControlCenterViewModel` exposing its `Agents` list + the kill-switch
    `IsFrozen`/`KillSwitchLabel`/`ToggleKillSwitchCommand`, re-raising the two derived readouts when the
    control center flips them; the shell hosts it as opaque `AgentRailContent` → `AgentRailView` via
    ViewLocator, so the shell names no Pro rail type), `QueueRailViewModel`/`QueueEntryViewModel` (the
    rail projection over `IMergeQueueService` — **the merge-queue surface the shipped Control Center
    actually hosts**: state words, `CanMerge` gate line, the header `CountText` ("N in play · N in
    history") that keeps a row scrolled below the fold from reading as a row that vanished (ISSUES-LOG
    #4 and #13 were both filed against rows that were rendering), the verified-against stamp (from the wire's
    `VerifiedMainSha`), the one Review accent on the front-most fresh Verified entry PLUS a
    non-accent `ShowSecondaryReview` button on every other reviewable row (the cockpit is the only
    home of the Merge button, so a verified branch without a Review path is unmergeable), a per-row
    **`Verification`** child (`VerificationPanelViewModel` — the verdict and its output, see below), and the
    per-row **`VerifyCommand`** — the human verification trigger. The command is
    deliberately thin: one call to `IMergeQueueService.RunVerificationAsync`, then it renders the answer
    (`VerifyMessage`) — it transitions nothing and judges no pass/fail, because all of that is the
    daemon's `MergeQueue.RunVerificationAsync` and the new state arrives back on the queue stream.
    `CanVerify` withholds the button while a run is in flight and on the terminal states. Beside it, the
    **entry-lifecycle** commands `BeginDiscard`/`CancelDiscard`/`ConfirmDiscard` (two-step) +
    `ClearStalledVerification` + **`Resume`** (the way out for a STRANDED entry: the daemon spawns a jail
    onto this same entry's agent id and branch — `CanResume` is true only when the daemon POSITIVELY
    reports no sandbox (`QueueEntry.HasLiveSandbox == false`; the three-valued `null` offers nothing), and
    the same fact now withholds `CanVerify` instead of leaving an enabled button whose only behaviour is
    "has no live sandbox". Not two-step, unlike Discard: it adds a sandbox and destroys nothing. The CLI
    to run comes from the injected `resumeAgentKind` callback, read at press time, because the picker
    lives on `ControlCenterViewModel`), each an equally thin drive of a daemon RPC through
    `MergeActionRunner`.
    The class can neither remove a row nor invent an outcome — a local "remove from list" would clear
    the rail until the next `StreamQueue` snapshot silently refilled it. `IsVerificationStalled` comes
    from the daemon's `QueueEntry.VerificationInFlight`, never inferred from `Verifying`, which is wrong
    for exactly the frozen entries the action exists for; `CanDiscard` hides the action on terminal
    entries (the daemon refuses them anyway). The `IMergeQueueService` argument is **required** for both
    reasons at once: an optional seam lets a caller build a row whose buttons silently do nothing),
    `MergeQueueViewModel`/`MergeQueueRowViewModel` (P2-10: the rail bound to the **real** `MergeQueue`
    state machine — subscribes to its `Changed` event, per-row state word + `main@sha` label +
    `CanMerge`-gated Merge button with the reason as tooltip + the loud stale-override behind a confirm;
    design tokens/registered themes; render-harness-driven, not yet mounted on MainWindow),
    `ReviewCockpitViewModel` +
    `ReviewFileRowViewModel`/`ReviewHunkRowViewModel`/`FlaggedChangesPanelViewModel`/`FlaggedItemRowViewModel`
    (P2-11: composes the pure-Core rules into the cockpit — `ReviewCockpitContext` inputs → risk-ordered
    file/hunk rows (`TotalHunkCount`==`RenderedHunkCount`, invariant 3), provenance chips (Agent-Trace
    join then trailer fallback then honest absence), the item-by-item flagged panel driven by
    `FlaggedChangeGate`'s `AcknowledgmentStore` + the RT-D2 `ChangedTestCommandGate` item, the
    test-delta strip, `CanMerge`-gated Merge + T-29 `BringBranchLocal`, and review-sprint mode
    (j/k/a/space + risk budget → `ViewedStateEvent`s, unviewed for deferred hunks for P2-38); the
    `SeverityVocabulary` glyph map is a rendering-only projection — no rule logic. Two notes on the
    LIVE panel: `KindOf` reads the `FlaggedKind` out of the item id (`kind|path|hash`, which is where
    the daemon's own kind travels) — it used to parse the `Category` string, a `RiskCategory` name, so
    every daemon-flagged row but one arrived mislabelled `RiskCategory`; and
    `ReviewCockpitContext.LockfileFlags` is **local-composition only** — production always supplies
    `live:`, so the §3.6 lockfile rows are armed daemon-side by
    `MergeQueueProvisioner.ReviewLockfiles` and arrive through the ordinary projection),
    `CoordinatorPanelViewModel`/`ChatLineViewModel`/`PlanCardViewModel`/`EscalatedPlanViewModel`
    (the coordinator conversation + the **worker-authored** plan approval card; Approve is the panel's
    accent. The conversation half is no longer mounted (the coordinator surface is the inline terminal
    now), but the **plan-gate half is** — `ControlCenterView` binds this same VM into `PlanGateView`
    above the coordinator's terminal, so `PendingPlans`/`EscalatedPlans`/`BackpressureText` are live
    shipped state rather than harness-only state. `PendingPlans` is a **collection, one card per blocked
    worker** (reconciled in place on `(PlanId, Revision)` so a refresh never eats half-typed feedback or a
    just-raised error), with `PendingPlan` kept as its head; `HasGateContent` is what lets a host collapse
    the whole region when nothing is waiting. **Phase 2** renders the three states the plan gate otherwise makes invisible: the card names
    the worker that wrote the plan and says it is *blocked until you decide*; Reject carries a feedback
    box, because that text is delivered back to the worker to revise against, plus the revision counter
    against the daemon's budget and a warning when the next rejection would stop the worker rather than
    produce another plan; an `EscalatedPlanViewModel` card has **deliberately no buttons** — the loop is
    over and the next move is the human's; and `BackpressureText`/`IsCapSaturatedByBlockedWorkers` render
    the daemon's stall line, since a coordinator that has quietly stopped spawning is indistinguishable
    from a hang. Both decisions run through one `PlanCardViewModel.DecideAsync` that resets `IsDeciding` in
    a `finally` and reports a failure in `DecisionErrorText`/`HasDecisionError`: this gate is *blocking*, so
    a card that latched its buttons disabled — on a throw, or on a decision that returned while the plan
    stayed pending with the same id/revision and `Refresh` therefore kept the same instance mounted — took
    away the operator's only means of clearing backpressure. Built entirely from existing theme tokens — a
    warning, a stop and a failure are things the design system already has words for),
    **`VerificationPanelViewModel.cs`** (H4 — one entry's verification VERDICT and its recorded output on
    demand, composed identically into `QueueEntryViewModel` and `AgentDocumentViewModel` so the two
    surfaces cannot drift into saying different things about one record. Three outcomes and never two:
    green, red, and *no record at all* — the collapse of red into never-run is the defect it exists to end.
    `ToggleCommand` fetches the log through `IMergeQueueService.GetVerificationLogAsync` on FIRST expand
    only, caches it against the verdict it belongs to and drops it when a new verdict arrives; it never
    calls `RunVerificationAsync`, because charging a human a minutes-long jail run to find out why a run
    failed is the problem, not the fix. `LogNotice` keeps the daemon's distinct answers distinct —
    truncated tail / artifact gone / run printed nothing / daemon unreachable — so none of them renders as
    an empty box), `AgentDocumentViewModel` +
    `PlanStepViewModel`/`QueuedPromptViewModel`/`FlaggedItemViewModel` (terminal tail, plan tree, health
    strip, composer + visible prompt queue, and the review section: item-by-item flagged acks gating the
    Merge button, plus the shared `Verification` panel and a `VerifiedAgainstText` main@sha stamp — these
    replaced a single `ReviewFactsText` line that printed either an invented `{TestsPassed}/{TestsTotal}
    tests green` or, for everything else including a branch whose tests had just failed, "no verification
    record yet"), `TelemetryPanelViewModel`/`SandboxEventRowViewModel` (P2-44 fact table, no accent),
    `QueueSeedingPanelViewModel` (the DEV-ONLY seeding card, docs/design/queue-seeding.md §6-7 — a thin
    driver over `IQueueSeedingGateway` whose scenario presets are CLIENT-side compositions of the RPC
    primitives ("Stale pair" is literally two specs in one ordered batch; "Merge during verify" holds
    one entry mid-run while a sibling's real merge fires the real cascade; "Plan-gated" seeds both arms
    of the phase-2 plan dimension — an approved scope that covers the seed's own commit, and one that
    does not, blocking on the real out-of-approved-scope item); refusals render the
    daemon's words verbatim; `ControlCenterViewModel.QueueSeeding` stays null — card absent, not
    disabled — unless the daemon's one-shot `ProbeQueueSeedingAsync` availability probe answered yes,
    which a daemon without the boot flag never does),
    `VibeModeViewModel`/`VibeCardViewModel` (P3-02/03/04: the event→friendly-card translation, the
    three-action triage with the honest disabled state, publish → live-URL card).
  - `ApiKeySettingsViewModel`/`ApiKeyProviderRowViewModel` (P2-01: validate-then-store off the UI thread
    with cancellation on page close, keyed `llm_<provider>`, candidate key nulled after every check,
    per-provider delete; injectable keystore/health-check/db seams; now the Settings **AI Providers**
    page — `ApiKeySettingsViewModel` implements `ISettingsPage`. Its **Custom key** section stores any
    env-var name under `llm_env_<NAME>` with no health check, and its confirmation names the ONE
    exception to "injected into every agent you start" — a jail running an external pull request's code
    is spawned `withoutHostCredentials` and inherits none of them, ISSUES-LOG #37) and
    `CliOAuthTosDialogViewModel`
    (writes the persisted `TosAcknowledgment` on acknowledge).
  - `TerminalViewModel` (P2-03) wires the engine (`ITerminalView`) to the daemon stream
    (`ITerminalGateway`): forwards engine keystrokes (incl. Ctrl+C→0x03) to the daemon, feeds daemon
    `raw` output into the engine, and debounces (~50 ms) layout resizes before propagating them
    (SIGWINCH) — it touches only the interface, so P2-18 swaps the engine with no VM change. The
    keystroke forward (`ForwardInputAsync`) is necessarily fire-and-forget — a key event cannot block
    the UI thread on a round-trip — so it OBSERVES every outcome: a genuine delivery failure sets
    `InputDeliveryError`, which `TerminalView` renders as a banner over the pane, and the next input
    that lands clears it. Teardown exceptions (cancel/dispose, including the transport's
    `RpcException(Cancelled)` and anything the gateway wrapped one in) are classified out. It used to
    catch only `OperationCanceledException`, which is how three characters typed at a jailed CLI
    vanished with nothing said (stress S1 / G5). A dropped SIGWINCH is deliberately NOT surfaced —
    self-correcting, and not something the operator typed — but it is no longer left unobserved. Derives
    from `ViewModelBase` (not bare `ObservableObject`) so `ViewLocator` resolves it to `TerminalView`
    inside a `ContentControl` — the coordinator surface and the agent dock both rely on that resolution.
  - `BootstrapProgressViewModel` (P2-05: drives the `MainguardOsBootstrapper` off the UI thread,
    marshals each stage transition back via `Progress<T>`, cancellable between steps; plus the
    `BootstrapStageViewModel` row VM — name + `BootstrapStageState` + log tail with
    `IsPending`/`IsRunning`/`IsDone`/`IsFailed` projections for the icon encoding).
  - `EgressAllowlistViewModel`/`EgressAllowlistRowViewModel` (P2-07: the user-visible egress allowlist
    over the `IEgressAllowlistGateway` daemon seam — add/remove through the gateway (change-logged
    daemon-side), `HasGitHostWarning` drives the A6 banner, each row carries the `DefeatsA6` marker; no
    color/engine type in the VM).
  - `AgentCliRowViewModel` (P2-22 §J-5: ONE agent CLI as presented to the user — id/display name/pinned
    `VersionLabel` + the install lifecycle as mutually-exclusive booleans
    (`IsSelected`/`IsInstalling`/`IsInstalled`/`IsFailed`/`CanSelect`/`CanInstall`/`IsIdle`) so the View
    encodes state by icon AND text, never colour alone; shared by BOTH CLI surfaces so they cannot
    drift), `AgentCliSettingsViewModel` (the **"add more later"** surface over `AgentCliInstaller`:
    `RefreshAsync` re-reads the channel + re-probes each CLI in the VM, `InstallAsync(row)` installs ONE
    at its pinned version — installs are **serialized** (`IsBusy`; the shared npm prefix must never see
    two at once) and failure-isolated (the row carries its own actionable cause and stays retryable),
    Cancel works, and a catalog-read failure explains itself instead of throwing; now the Settings
    **Agent CLIs** page — the old repo actions menu → **Agent CLIs…** entry and
    `RepoDashboardViewModel.ManageAgentClisAsync` are gone, and `AgentCliSettingsViewModel` implements
    `ISettingsPage` (`OnActivated` kicks `RefreshAsync`); Install/Update/Revert are parent commands
    whose `CanExecute` reads ROW state, so the VM watches `Clis` + each row's `PropertyChanged` and
    re-publishes `NotifyCanExecuteChanged()` — `[NotifyCanExecuteChangedFor]` on `IsBusy` alone left
    the buttons visible-but-dead (`Headless/RowCommandEnablementTests`); add a row property to
    `RowCommandInputs` whenever a new row-reading `CanExecute` lands),
    `ToolchainRowViewModel`/`ToolchainSettingsViewModel` (Settings **Toolchains** — the human half of
    the user-managed toolchain channel (`Mainguard.Agents.Agents.Toolchains.ToolchainChannel`): a
    repository may name a toolchain `id` and nothing else, and whether that id is actually on this
    machine is decided here. `RefreshAsync` re-reads the curated manifest and RE-PROBES each toolchain
    inside the VM (a toolchain that runs at the WRONG version is reported as not installed, with the
    mismatch spelled out on the row), `InstallAsync(row)` installs ONE at its pinned, checksum-verified
    version and `RemoveAsync(row)` removes it — both serialized (`IsBusy`) and failure-isolated to the
    row, which carries the typed `ToolchainChannelException` message as its actionable cause; Remove
    asks for no confirmation but ALWAYS re-probes afterwards (re-attaching a failure's cause to the
    rebuilt row) so the list can never misreport a half-removal. Same row→command
    `CanExecuteChanged` bridge as the Agent CLIs page (`_watched` + `RowCommandInputs` =
    `CanInstall`/`CanRemove`), and the same two-constructor (live channel / design rows) shape),
    `ToolchainDeclarationViewModel` (Settings **Toolchains** → the per-repository half: declaring
    `.mainguard/toolchain` as **four discrete buttons the user presses one at a time** — Write file →
    Stage & commit → Push → Install. **No step ever does another's work**: writing touches the working
    tree and stages nothing, committing stages that ONE path and never pushes. That is not a style
    preference — an action that quietly does more than its label says is the failure this shape exists
    to prevent, and the first implementation staged inside step 1 *while its own status message said
    nothing had been staged* (`WriteFile_ShouldWriteTheWorkingTreeOnly_AndStageNothing`). Each command
    is enabled **iff** its `…DisabledReason` is empty (`CanWriteFile`/`CanCommit`/`CanPush`/`CanInstall`),
    so a button cannot be disabled without a stated reason — the #302 pattern, made structurally
    impossible rather than remembered. Never stashes, never checks out, never pushes as a side effect:
    a dirty tree or a non-default branch is REFUSED with the reason, naming both branches. The default
    branch is resolved dynamically via `RepoToolchainConfig.DefaultBranch` (`symbolic-ref`, `main`
    fallback) — neither name is hardcoded anywhere, because the owner's repo is `master`. `RefreshAsync`
    re-MEASURES the repository through one `IGitService.ExecuteWithRepo` after every step, so a change
    made outside the app is visible on the next pass and no precondition is ever inferred),
    `VmUpgradeOfferViewModel` (the tier-2 upgrade
    offer/progress VM: starts in the consent state (`IsOffering`), `UpgradeCommand` runs the injected
    `IVmUpgradeOrchestrator` off the UI thread and advances the `VmUpgradePlan`-seeded
    `BootstrapStageViewModel` checklist from the orchestrator's `IProgress<string>` lines (a line
    matching a step description advances; others become the running step's log tail; the optional
    `LogSink` — the App passes its oobe.log writer — receives every progress line plus one final-result
    line with the failure kind, promote strategy, and stranded path), `LaterCommand` fires the
    `Declined` callback (the App's session don't-nag flag) and closes, failure surfaces the typed
    message + `StrandedVhdxPath` and Close is disabled while running — never a fake success),
    `RepoOnboardingViewModel` (the ONE copy-host-repos-into-Mainguard-OS engine, extracted from the OOBE
    wizard's repo step: discovery/pick choices, the default-checked `OnboardRepoRowViewModel` list, the
    sequential per-row failure-isolated copy run with cancellation, the state-derived footer matrix, and
    the friendly per-row error copy incl. the named daemon-unreachable cause; every seam injected, null
    seams tolerated as no-ops — `OobeWizardViewModel` composes it and forwards its surface 1:1 so both
    hosts share one behaviour) and `AddReposToOsViewModel` (the post-setup host of that engine: the live
    seams + a `CloseCommand`; re-copying an already-provisioned repo succeeds quietly because the whole
    pipeline is idempotent — it used to be hosted directly by the standalone `AddReposToOsView` window,
    now it is wrapped by `MainguardOsPageViewModel`, the Settings **Mainguard OS** page, which
    additionally cancels an in-flight copy on `ISettingsPage.OnDeactivated` and adds
    `RebuildSandboxImagesCommand`), `MacOobeViewModel` + `Views/MacOobeWindow` (the macos-host
    first-run: sequential retryable checks — Docker engine via `DockerEndpointResolver` + a live
    version ping, the ~/mainguard file-sharing canary run in a throwaway container, jail-image
    probe/build through the shared installer in the background, daemon start + pinned-mTLS
    answer — then the agent-CLI picker over the container install host and the start-at-login
    launchd toggle; Continue gates only on engine + daemon, marks `MacOobeState` completed and
    hands off to the SAME startup-window path the control-center route takes.
    `ProDesktopHost.DecideLaunchRoute` routes macOS by the completed marker, and the keep-alive /
    resume-task-sweep Windows machinery no-ops there), `PrIntakeSettingsViewModel`/`PrIntakeSourceRowViewModel` (P2-12: the
    Settings **PR Intake** page — the on/off switch, poll cadence, shared bot-author list and the
    subscribed `(host, owner, repo, author-filter)` sources. **All of it is DAEMON state, edited over
    gRPC through `IPrIntakeGateway`** — `Load`/`Save`/`AddSource` are round trips, `Save` re-renders from
    what the daemon PERSISTED (it clamps the cadence and defaults an empty bot list) and a refusal
    populates `ErrorMessage` instead of claiming success. There is deliberately **no gateway-less
    constructor**: it used to take the daemon's own `IPrIntakeStore` and default to an in-process one, so
    the page could only ever have saved into something the daemon never reads. Reached from
    `SettingsViewModel`'s `"PrIntake"` row via `IProToolsSurface.CreatePrIntakePage`, built over
    `ProComposition.PrIntakeGatewayFactory`).
- **`Controls/`** — custom-drawn controls.
  - `CommitGraphCanvas.cs` renders the commit graph (uses `Core/Graph`) and hosts right-click
    hit-testing;
  - `GraphHitTester.cs` is the pure, unit-testable row/node/label hit-tester it delegates to (T-09; no
    Avalonia deps beyond `Point`/`Rect`) — also reused per-move to resolve the drop-target chip during
    the drag gesture.
  - `LabelDragGesture.cs` (T-09b) is the pure state machine behind drag-to-rebase/merge: it arms on a
    ref-chip press and promotes to a drag only once the pointer passes the ~5px threshold (so a plain
    click still selects and a right-click still opens the menu);
  - `CommitTimelineView.axaml.cs` drives it (ghost + drop-target highlight) and calls
    `CommitTimelineViewModel.ResolveLabelDrop` on drop over a different chip to open the two-action
    flyout.
  - `IntraLineDiffTextBlock.cs` (T-13) is a `TextBlock` that splits a diff line into styled `Run`s from
    precomputed spans (`SpansSource` word emphasis + `TrailingWhitespaceSpan` marker) — contains no diff
    algorithm; emphasis/marker brushes resolve from theme tokens and re-resolve on
    `ThemeManager.ThemeChanged`. `ImageDiffControl.axaml(.cs)` (T-13 / T-13b) renders a detected image
    blob pair with a size summary and a **drag-to-reveal overlay**: both revisions are stacked in one
    box (`Stretch=Uniform`). The code-behind computes each revision's letterboxed **rendered image
    rect** (the pure, testable `RenderedImageRect(image, box)` — `scale = min(boxW/imgW, boxH/imgH)`,
    centered) so the geometry lands on the image edge even when the two revisions differ in size: a
    vertical **wipe** divider clips the after to its left slice (image-local space) and the before to
    the complementary right slice of the shared after-rect, with an accent divider line+handle at the
    boundary (pointer-X, mapped across the rendered rect, → `SwipePosition`), so the old image never
    bleeds past the divider; or an **onion-skin** true **crossfade** (before opacity `1-SwipePosition`,
    after `SwipePosition`) so the old image isn't left fully opaque underneath (mode toggle). A
    one-sided change (added/deleted image) shows just the present revision with a label. Overlay
    geometry is in code-behind because it needs the measured stage bounds; only divider feel-tuning is
    left as a note.
  - `CheckerboardBackdrop.cs` — the token-drawn transparency checkerboard behind both image stages
    (alternating `SurfaceDeep`/`SurfaceCard`, resolved at render time + re-resolved on
    `ThemeManager.ThemeChanged`), so transparent pixels are distinguishable from surface-coloured ones
    in every theme.
  - `RepoRow.cs` (W2) — the repo picker's repository-row surface: a `Grid` subclass that adds the
    activation path the raw-pointer-handler row never had. `Activate()` raises the `Activated` routed
    event, reached from Enter/Space (the row is `Focusable`, so it is also a tab stop) and from
    `RepoRowAutomationPeer` — a `ControlAutomationPeer` reporting `AutomationControlType.ListItem` and
    implementing `IInvokeProvider`, so UI Automation and screen readers can open a repository.
    Deliberately NOT a `Button` wrapper: a Button would swallow the pointer press that
    `RepoPickerWindow`'s select-then-drag gesture depends on. `RepoPickerAccessibilityTests` pins both
    the Invoke and the Enter paths.
  - `TerminalControl.cs` + `VtScreen.cs` (P2-03) — the interim terminal engine behind `ITerminalView`:
    `VtScreen` is a pure, Avalonia-free VT parser + cell grid (SGR colour, cursor motion, erase,
    10k-line circular scrollback, OSC 52 clipboard-copy decode → `ClipboardCopyRequested` — queries
    never answered — + bracketed-paste DECSET 2004 tracking) and `TerminalControl` is the themed
    monospace cell renderer over it (dirty-flag invalidation, key→VT byte mapping incl. Ctrl+C→0x03,
    host-clipboard bridge: OSC 52 → clipboard, Ctrl(+Shift)+V / Shift+Insert paste → CR-normalized,
    bracket-wrapped bytes toward the PTY, terminal palette from
    `TerminalBackground`/`TerminalForeground`/`TerminalCursor`/`TerminalAnsi0-15` theme tokens, and
    **wheel-scroll through the scrollback ring** — the 10k-line buffer always existed but the control
    never rendered it, so the terminal looked unscrollable (user-reported in the live cycle test):
    3 lines/notch via `VtScreen.ScrollbackLine` cells, cursor hidden while scrolled (the honest
    "you are viewing history" signal), any keystroke snaps back to live). **Geometry deferral
    (ISSUES-LOG #22):** the control constructs its `VtScreen` with `awaitGeometry: true`, so the
    engine HOLDS fed bytes (bounded 2 MB, past which it parses rather than drops) until the first
    layout pass establishes the real (cols, rows) — `ArrangeOverride` now sizes the engine itself
    instead of waiting for the ViewModel's ~50 ms debounced round trip. Without it a rehydrated
    agent's replayed scrollback (which the daemon sends within milliseconds of attach, long before
    arrange) is parsed at the 80×24 placeholder and, since this engine has no reflow, stays wrapped
    at 80 columns forever — the garbled restart-resume terminal. The
    renderer is the fallback for the planned vendored `Iciclecreek.Avalonia.Terminal` (see note below).
    **Known field gaps (2026-07-22), deferred to P2-18 by decision — do NOT grow `VtScreen` toward
    conformance:** Ink/Yoga TUIs (claude-code) mis-render — no scroll regions (DECSTBM), insert/delete
    line/char, alt screen, save/restore cursor, origin mode, or deferred-wrap — and there is no
    mouse-selection copy (only the OSC 52 application-driven copy + paste chords). P2-18's
    field-findings block in the master doc binds the fixes, incl. required drag-selection copy and
    `TerminalClipboardTests` surviving the engine swap; it carries a test-only grid-readback hook
    (`ReadGrid`/`FeedSync`, `InternalsVisibleTo("Mainguard.Tests")`) the P2-04 harness drives.
- **`Converters/`** — the git-model `IValueConverter`s that stayed with the git surfaces (the
  edition-agnostic `BoolToOpacityConverter` + `ResourceKeyToGeometryConverter` moved to
  `Mainguard.UI/Converters/`, step 2c): `FileExtensionToIconConverter`, `DiffLineKindToClassConverter`
  (P2-11: `DiffLineKind`→bool for a single add/delete style-class toggle, static `Add`/`Delete`
  instances — lets the review cockpit tint diff lines with shipped tokens without rule logic in XAML),
  `AgentStatusBrushConverter` (P2-13: **the one** `AgentStatus`→brush mapping — resolves an
  `AgentStatus*Brush` design token by key from the active theme, never a literal brush; `TokenKeyFor`
  is the pure status→token contract the theme-completeness test enumerates. A second status→brush site
  is a rejection).
- **`ViewModels/Agents/`** — the P2-13 activity-bar/docking primitives (App-only): `AgentStatus.cs`
  (the nine-value UI badge status + pure total `AgentStatusMap` from
  `AgentLifecycleState`/`WorkerMergeState` — "total" is a claim its `_ =>` fallback cannot falsify, so
  `WorkerMergeState.VerificationFailed` maps to `Conflict` EXPLICITLY (the badge this enum reserves for
  "needs human intervention"; not `Dead` — the branch is still the agent's to fix) rather than sliding
  into the `Working` default and badging a failed branch as ordinary live work, and
  `EveryMergeState_HasADeliberateBadge_NotTheWorkingFallback` pins that exactly one state badges
  `Working`), `AttentionPolicy.cs` (pure attention derivation —
  AwaitingReview/Conflict/plan-pending, + the waiting/blocked transition test the notifier uses),
  `AgentListProjection.cs` (pure LIFO ordering the rail and its test share),
  `AgentWorkspaceViewModel.cs` (per-agent **Dock.Avalonia** workspace: Terminal + agent-diff + staging
  as docked panes in the persisted `WorkspaceLayoutKind` (FlightDeck default / ConversationDeck);
  `IDisposable` — closes floating dock windows on teardown (the documented Dock.Avalonia leak) and
  clears the factory registries; `ShowAgent(...)` is the **lightweight** switching path that swaps the
  three panes' content through ONE reused dock host so opening/closing agents keeps the heap flat —
  never rebuilds the layout; contains `WorkspaceTool` + the internal `WorkspaceDockFactory`).
- **`Views/Agents/`** — `AgentWorkspaceView.axaml(.cs)` (P2-13: hosts the `DockControl` bound to `AgentWorkspaceViewModel.Layout`, tool bodies via a `WorkspaceTool` data template; nulls the DockControl's `Layout` on detach to release the realized visual graph).
- **`Services/`** — UI-facing service abstractions kept out of the ViewModels for testability.
  - `IConfirmationService` / `DialogConfirmationService` gate destructive actions (e.g. the T-09 graph
    hard-reset) behind a confirmation dialog; a fake records the ask in tests.
  - `DaemonUpdateToastPublisher.cs` — the App's subscriber to `DaemonAutoRefresh`'s typed `onOutcome`
    seam: composes via Core's pure `DaemonRefreshToast.TryCompose` (Refreshed/RefreshFailed only) and
    posts the shell toast onto the UI thread into `MainWindowViewModel.Toasts`; no main window or no
    toast-worthy outcome means nothing happens.
  - `JailText.cs` — renders sandbox-produced text safe to DISPLAY, the client-side twin of the daemon's
    `AgentIpcServer.Echo` (which does the same before anything is logged). `GetVerificationLog` is the one
    path that hands jail bytes straight to a human surface — an artifact written by a test runner inside
    the worker's own jail — so `DaemonBackedOrchestrator` sanitizes at the projection boundary and no
    consumer can forget: newlines and tabs kept (structure in a test log), other control characters made
    visible as `.` rather than silently dropped, CR/CRLF collapsed to one break, and ANSI escape sequences
    consumed WHOLE (CSI/OSC/two-character, including one the daemon's tail cut in half) so a coloured
    reporter's output arrives as the plain text underneath it instead of a smear of `.[31m`.
  - `BrowserLauncher.cs` — the single open-a-URL-in-the-browser path (validates via Core's `SafeWebUrl`,
    then platform-dispatches; best-effort, never throws); ViewModels take it as their default `_openUrl`
    delegate instead of private copies.
  - `FileExplorerLauncher.cs` — the single reveal-a-folder-in-the-OS-file-explorer path (mirrors
    `BrowserLauncher`: refuses anything that isn't an existing directory, then platform-dispatches
    `explorer.exe`/`open`/`xdg-open`; best-effort, never throws);
  - `PullRequestsViewModel` takes it as its default `_revealInFileExplorer` delegate.
  - `TerminalLauncher.cs` — the single open-a-folder-in-the-OS-terminal path (same hygiene as the
    reveal path): macOS `open -a Terminal`, Windows `wt.exe` falling back to `cmd`, Linux
    `x-terminal-emulator`. Surfaced from the macOS menu bar's Repository menu.
  - `MacMenuBar.cs` — the macOS top-of-screen menu bar (no-op elsewhere): File / Repository /
    View / Help built over EXISTING seams — repo actions dispatch through
    `MainWindowViewModel.InvokeActionByIdCommand` (the same registry the shortcuts and palette
    use, so availability rules hold), themes through `ThemeManager` keys ("System" included),
    reveal/terminal through the launchers above. The menu bar follows the KEY window on macOS,
    so `Attach` is called from MainWindow's ctor and from every `ChromedWindow` via the
    `ChromedWindow.MenuInstaller` seam; `App.axaml` names the application ("Mainguard") because
    Avalonia titles the app menu from `Application.Name`, not the bundle.
  - `BrowserOpener.cs` (P2-22 — the App's `IBrowserOpener`, a thin adapter over `BrowserLauncher` so
    `LoopbackOAuthListener` opens the authorize URL through the ONE launcher, no second path).
  - `DeepLinkHandler.cs` (P2-22 — the `mainguard://` entry point: `RegisterProtocolAsync` (per-user
    protocol via `WindowsIntegration`), `Handle` (parse via the pure Core `DeepLinkParser` → dispatch
    only a valid non-secret command), and single-instance forwarding over a named pipe
    (`TryForwardToRunningInstanceAsync`/`ListenForForwardedLinksAsync`) — a second launch carrying a URI
    hands it to the running instance; the OAuth loopback flow deliberately avoids the protocol handler,
    so this path only ever carries non-secret navigation links).
  - `DaemonClient.cs` is the sole daemon touch-point (G-18: loopback gRPC, bearer token,
    reconnect/backoff, `ConnectionState`) — **MG-19: `ForLoopback` now dials `https://127.0.0.1:<port>`
    over pinned mutual TLS via `DaemonTransportCredentials`, NOT h2c;
  - `CreateChannel` resolves the session directory once so the token and the certificates always come
    from the same daemon, and it throws rather than falling back to plaintext (a fallback would hand the
    bearer token to a port squatter)** — P2-03 adds `AttachTerminal` (the long-lived
    `TerminalService.Attach` bidi call); P2-06 adds `ProvisionRepoAsync` (→
    `ProvisionedRepo(RepoHandle, SyncRemoteName, SyncRemoteUrl)`); PR3 adds `ListInstalledAdaptersAsync`
    and a `role` parameter on `SpawnAgentAsync`; H4 adds `GetVerificationLogAsync` (the CONTENT of an
    entry's last verification artifact — never its daemon path, G-14).
  - `ITerminalGateway.cs` (P2-03) — the ViewModel-facing seam onto that stream: `DaemonTerminalGateway`
    writes the first `agent_id` frame then forwards input/resize and raises `OutputReceived` for each
    `raw` frame; a fake backs the ViewModel tests. **Every frame it sends — selector, input, resize —
    goes through the one `TerminalWriteQueue` below** (stress S1 / G5: gRPC permits a single in-flight
    `WriteAsync` per request stream, and this class has three concurrent writers, so a keystroke
    landing inside another frame's round-trip threw `Can't write the message because the previous
    write is in progress` into a fire-and-forget task and the character was silently gone). Two
    consequences of that: `SendInputAsync`/`SendResizeAsync` return a task that completes only once
    the frame is actually written and FAULTS when it is not, and input sent before an attach (or
    after a dispose) now says so instead of returning `Task.CompletedTask` for bytes that went
    nowhere. `AttachOverride`/`WriteQueueCapacity` are the internal test seams
    (`TerminalInputSerializationTests`).
  - `TerminalWriteQueue.cs` — the serializing writer behind `DaemonTerminalGateway`: a bounded
    (4096-frame) channel plus one pump task, so a concurrent caller QUEUES instead of throwing.
    Ordering is the order `EnqueueAsync` was called (a synchronous `TryWrite` under the lock — the
    caller's position is fixed before it ever awaits), because out-of-order keystrokes would turn
    character loss into character transposition. Backpressure is the bound: a stream slow enough to
    bank the whole queue is refused loudly rather than growing without limit. Teardown (`Close`)
    fails everything queued and every later enqueue immediately — a write posted after detach must
    not hang on a stream nobody will read — and a write that genuinely fails poisons the queue, so
    every frame after the break reports failure rather than reporting success for bytes that go
    nowhere.
  - `AutoDetectScan.cs` — the pure directory walk behind the sidebar's "auto-detect repositories"
    folder browse, split out of `MainWindowViewModel.ScanAutoDetectFolderAsync` so the walk is
    unit-pinned (`AutoDetectScanTests`) while the ViewModel keeps only the persistence around it.
    `Scan(rootPath, isGitRepository)` → `AutoDetectedRepo(Path, DisplayName, CategoryName?)`: the
    chosen root when the root is ITSELF a repository, otherwise its immediate subdirectories plus one
    grouping level down (the grouping folder's name becomes the workspace category); unreadable
    directories are skipped, never thrown. The root-is-a-repository case is a correctness requirement,
    not a convenience — walking a repository's own children used to add its `.git` directory as a
    repository literally named ".git" (walkthrough bug W3), which `IGitService.IsGitRepository`
    now independently refuses as well.
  - `SyncRemoteRegistrar.cs` (P2-06) — the small, testable idempotent sync-remote registration helper:
    takes the remote name/URL **verbatim from the daemon's `ProvisionRepo` response** (never a hardcoded
    literal) and, via `IGitService`, adds it / updates a changed URL / no-ops when unchanged.
  - `MainWindowViewModel.OpenRepository` calls it best-effort on project open (a missing/unreachable
    daemon is a silent no-op).
  - `IEgressAllowlistGateway.cs` (P2-07: the App's seam to the daemon-owned egress allowlist —
    `ListAsync`/`AddAsync`/`RemoveAsync` over `EgressAllowlistItem`; `InMemoryEgressAllowlistGateway`
    seeds the defaults for the render harness/preview, the production impl forwards to the daemon over
    gRPC so the App never references `Docker.DotNet`/the sandbox engine seams — ESC-I2/G-18). ESC-I2:
    the App never references the daemon substrate facade — it speaks only gRPC + `IGitService`. Async
    because the shipped implementation is a round trip: the seam was declared synchronous, which only
    the in-memory seed could satisfy, so the editor had no live gateway to be shown with.
  - `DaemonEgressAllowlistGateway.cs` (the SHIPPED `IEgressAllowlistGateway`, over `EgressService`'s
    `ListAllowlist`/`AddAllowlistHost`/`RemoveAllowlistHost`. Stateless — every read is a fresh
    `ListAllowlist`, so the editor reflects the daemon's authoritative list including entries the
    unblock prompt added. Built via `DaemonBackedOrchestrator.CreateEgressAllowlistGateway()`, the same
    factory shape as `CreateTerminalGateway`; reached from `ControlCenterViewModel`'s
    `OpenEgressAllowlistCommand` — the coordinator toolbar's "Network…" button and the egress block
    prompt's "Manage allowlist…").
  - `IQueueSeedingGateway.cs` / `DaemonQueueSeedingGateway.cs` (the App's seam to the DEV-ONLY daemon
    queue seeder — `IsAvailableAsync` (false, never a throw, for UNIMPLEMENTED/PermissionDenied: the
    unmapped service IS the visibility contract, no capability flag travels) + `SeedAsync`/
    `PushCommitsAsync`/`ClearAsync` over `SeedEntryRequestItem`/`SeedResultItem`/`SeedBatchResult`,
    wire vocabulary verbatim (incl. the plan dimension's `WithPlan`/`Scope`). Built via `DaemonBackedOrchestrator.CreateQueueSeedingGateway()` — the
    same factory shape as the intake/egress gateways — with the repo handle read LIVE so the panel
    always seeds the repo the rail is showing.)
  - `IPrIntakeGateway.cs` (P2-12: the App's seam to the **daemon-owned** external-PR-intake
    configuration — `LoadAsync`/`SaveAsync`/`SubscribeAsync` over `PrIntakeConfiguration` +
    `PrIntakeSourceItem`. `InMemoryPrIntakeGateway` (which calls `PrIntakeSettings.Normalized` rather
    than re-typing the clamp) serves the render harness/preview only — deliberately not a default
    anywhere, because a settings surface silently defaulting to storage the daemon never reads IS the
    defect this seam exists to remove.)
  - `DaemonPrIntakeGateway.cs` (the SHIPPED `IPrIntakeGateway`, over `PrIntakeService`'s
    `GetPrIntakeSettings`/`UpdatePrIntakeSettings`/`SubscribePrIntakeSource`. Stateless — every load is a
    fresh read, and a save returns the daemon's PERSISTED values (clamped cadence, defaulted bot list),
    never an echo of the request. Built via `DaemonBackedOrchestrator.CreatePrIntakeGateway()` (the same
    factory shape as `CreateEgressAllowlistGateway`) or, for the Settings page, via
    `ProComposition.PrIntakeGatewayFactory` over one lazy process-lifetime loopback `DaemonClient` —
    Settings pages are cached and never disposed, so a client per page would leak an mTLS channel per
    window open.)
  - `DockLayoutPersistence.cs` (P2-13: saves/restores a per-agent-kind `DockLayoutState` as versioned
    JSON under `%AppData%/Mainguard/workspace-layouts`; restore is total —
    absence/parse-failure/schema-drift falls back to the default layout, never throws).
    Driven by `AgentWorkspaceViewModel` (`persistence`/`layoutKey` ctor args, supplied from
    `ControlCenterViewModel.SelectAgent` keyed on the agent KIND): loaded before `CreateLayout`, saved on
    Dock's `DockableMoved`/`Swapped`/`Added`/`Removed` and once more on `Dispose` before teardown clears
    the graph. Only `ToolOrder` is restored — the layout KIND is the live Flight/Conversation preference
    and wins over the file. `WorkspaceDockFactory.OrderedTools()` is the consumer that makes restore
    real (`CreateLayout` previously ignored `ToolOrder` entirely) and is total: unknown/duplicate/missing
    ids can never lose a pane.
  - `AgentNotificationService.cs` (P2-13: OS/in-window toast on an agent transition INTO waiting/blocked
    (AwaitingReview/Conflict), suppressed when the app is foregrounded on that agent; first observation
    baselines silently; `IAgentNotifier` seam with the `WindowNotificationManager`-backed default + a
    fake for tests. NOW WIRED: `ControlCenterViewModel.RefreshAgents` feeds it per reconciled row and
    `Forget`s removed agents; the shipped notifier is `OsAgentNotifier.cs` — a Notification Center
    banner via `MacNative` on macOS, the shell toast elsewhere/on failure. `RefreshAttention` also
    mirrors the attention count onto the macOS Dock badge.)
  - `SpawnProgressWatchdog.cs` (the spawn RPC's deadline, measured from the daemon's **last sign of
    life** rather than from the start of the call. `SpawnAgent` carried a flat 5-minute gRPC deadline; a
    first run builds the repo's ~2.9 GB toolchain image inside that call, so the client hung up on a
    working launch — and destructively, since the cancelled server call makes the daemon tear the session
    and worktree down. A bigger constant only moves the cliff, so the budget bounds SILENCE: each
    launch-progress delta re-arms it, and a build that keeps reporting runs as long as it needs.
    `DaemonBackedOrchestrator` still passes a 60-minute hard cap as the gRPC deadline, so a daemon that
    chatters without progressing is bounded too — the false timeout was traded for a bounded one, never
    for an infinite wait. A user's Stop stays an `OperationCanceledException`, never a timeout.)
  - `DaemonBackedOrchestrator.cs` (**P2-47**: the real, DaemonClient-backed implementation of every
    control-center seam —
    `IAgentService`/`IMergeQueueService`/`ICoordinatorService`/`IKillSwitchService`/`ITelemetryService`/`IVibeService`
    — that **replaces `MockOrchestrator` in the shipped app**. Runs the P2-02 `StreamAgentEvents`
    snapshot-then-deltas stream on a background pump, keeps a live agent projection served from
    `ListAgents()`, and routes `EndAgentAsync`→`StopAgent`; construction never blocks on the daemon. It
    is NOT a mock — surfaces whose gRPC contract does not exist yet (merge-queue projection, coordinator
    conversation, kill switch, telemetry, Vibe) return empty/neutral state with a marked
    `P2-47 residual` and light up as their RPCs are added. **Phase 2:** `ApplyPlanUpdate` also keeps
    **escalated** plans in the projection (a worker that stopped after spending its revision budget is
    the state that most needs a human and would otherwise vanish from the surface), exposes
    `GetWorkerPlans()`/`GetBackpressure()`, and carries the daemon's backpressure numbers verbatim rather
    than re-deriving them — a client-side count can disagree with the number that actually refuses the
    coordinator. `SubmitPlanDecisionAsync`'s rejection reason is now the worker's feedback; an empty one
    is sent as an explicit "rejected without written feedback" rather than a silent blank, because it
    costs a round of the revision budget either way — and it **propagates a failure** instead of the
    blanket `catch (Exception) {}` it used to end in: that swallow made a decision which never reached the
    daemon indistinguishable from one that landed, so the panel could not tell the human their approval was
    lost while the worker stayed blocked. `CreateBundle()` is the shipped-app factory
    (`OrchestratorServices` over a loopback `DaemonClient`). **PR3:** it also implements `ICliAgentHost`
    — `ListInstalledClisAsync` (the `ListInstalledAdapters` RPC), `StartCoordinatorAsync` (keystore key
    resolved via `ApiKeyProviderMap` from the adapter's declared env-var name — no provider mapping
    means interactive login, no key travels — then `SpawnAgent` with the coordinator role against the
    active repo handle; keystore lookup is an injectable seam), and `CoordinatorAgentId` tracked from
    the snapshot's role field; a state delta for an agent the projection has never seen places a
    placeholder and triggers a `ListAgents` resync, so the snapshot/delta race can never strip a freshly
    spawned coordinator's role or kind (field bug, 2026-07-17). `ICliAgentHost.cs` — that seam +
    `InstalledCliOption` + the pure `ApiKeyProviderMap` (env-var name → `llm_<provider>` keystore key).
    `CliLoginVault.cs` — the host-side persistence format of the **CLI login round-trip** (a CLI's
    interactive login used to die with the jail's tmpfs `$HOME`, forcing a sign-in on every launch): one
    OS-keyring entry per adapter kind (`cli_login_<id>`, JSON path→base64), `Parse` total (corrupt vault
    ⇒ empty ⇒ a fresh login, never a crash) and `MergeAndSerialize` folding a stop's harvest into the
    stored vault without erasing files the harvest didn't return; `DaemonBackedOrchestrator` restores
    the vault on `StartCoordinatorAsync` (`SpawnAgent.cli_credentials`) and persists `StopAgent`'s
    harvested files back through its injectable `keystoreSave` seam — secrets live ONLY in the host OS
    keychain, never agent-side. **The harvest half is now DRIVEN**: `Start()` runs a `LoginHarvestPump`
    that calls `PersistLiveAgentLoginsAsync` every `loginHarvestInterval` (default 1 min, injectable for
    tests), and `Dispose()` runs one FINAL sweep on its own bounded token before `_cts` is cancelled.
    Before this, `PersistLiveAgentLoginsAsync` had no callers anywhere in the repo, so only an explicit
    in-app Stop ever wrote a `cli_login_*` entry — app close, a daemon/VM restart or a crash lost the
    login and the user re-authenticated inside the jail every session. Pinned by
    `CliLoginHarvestWiringTests` (the caller) and `CliLoginRoundTripDockerTests` (the round-trip).
    `CliSettingsStore.cs` — the same round-trip for a CLI's **settings**, and deliberately a DIFFERENT
    store: one plain JSON file per `(repo handle, adapter id)` under
    `<data root>/cli-settings/`, not a keyring entry. Logins stay keychain-only because they are
    credentials; settings are configuration the owner should be able to read, audit and delete, and the
    scope is **per repository** because a permission allowlist is a standing grant of execution —
    approving a command in one repo must not pre-approve it in another. `Load`/`Save` are total (a
    corrupt or missing file ⇒ empty ⇒ the CLI asks again, never a failed spawn), `Save` merges rather
    than replaces (a file one session did not rewrite must not erase a working allowlist), a blank scope
    is never a wildcard, and scope segments that are not filename-safe are HASHED rather than sanitised
    so two repos can never collapse onto one directory. `DaemonBackedOrchestrator` loads it on
    `StartCoordinatorAsync` (`SpawnAgent.cli_settings`) and `PersistHarvestedSettings` files every
    harvest under the outcome's OWN `RepoHandle` — the sweep walks every agent on the daemon, so filing
    by "whichever repo is open" is exactly how one repo's allowlist would land under another's name.
    See [`docs/design/agent-cli-settings-persistence.md`](../design/agent-cli-settings-persistence.md).
    `VmExitGuard.cs` — the pure full-exit-warning decision
    (`ShouldConfirm(stopVmOnExit, liveAgents)`) + dialog copy: a VM-stopping full exit under live agents
    confirms first; `App.RequestFullExitGuardedAsync` is the guarded path every user-facing exit takes
    (tray Exit, File → Exit, the X with close-to-tray off — `MainWindow.OnClosing` reroutes that X
    through it), with `App.LiveAgentCountProvider` wired to `ControlCenterViewModel.LiveAgentCount` and
    the confirmation service overridable for tests; the guard can never trap the user (failures proceed
    to exit). `MergeActionRunner.cs` — the ONE place a surface drives the human "Merge to Main" action
    (both the review cockpit and the agent document invoke the merge fire-and-forget, so every refusal
    used to vanish into an unobserved task and the button read as "nothing happened"): awaits
    `ConfirmMergeAsync`, and turns each outcome into one visible line — the daemon's reason verbatim
    (§3.4) as a warning toast, or `Merged agent/<id> into main.` — never throwing at its caller. It is
    now the one place for the entry-lifecycle actions too (`DiscardAsync`, `RejectAsync` — the
    review verdict "no", driven from the cockpit's two-step DangerQuiet Reject with an optional
    reason box — `ClearStalledVerificationAsync`, `ResumeAsync`), which need the same contract for a sharper reason: the
    daemon answers a REFUSED discard (or resume) with an ordinary successful RPC carrying
    `discarded=false` / `resumed=false`, so "no
    exception" is not evidence anything was removed. `DaemonBackedOrchestrator.DiscardEntryAsync` turns
    that into a throw and this turns the throw into a warning; the success line says *dropped from the
    merge queue* and that the branch and its commits are untouched, because a queue entry vanishing is
    otherwise ambiguous with one that merged. `ResumeConfirmation` says the entry keeps its id and its
    commits and states a cleared stalled verification in the same breath — a state change the human did
    not directly ask for must not be silent. `DaemonBackedOrchestrator.ResumeEntryAsync` resolves the
    picked CLI's BYOK key + saved login exactly as `StartCoordinatorAsync` does and asserts nothing else:
    every question a resume has to answer is the daemon's.
    `DaemonBackedOrchestrator.ConfirmMergeAsync` is the RT-D1 conversation itself (P2-10 §3.7):
    `BeginMerge` (the daemon's lease + `CanMerge` under it) → **the real Windows-side
    `git merge --ff-only` on the user's own checkout, via `IJournaledMergeExecutor`** → `ConfirmMerge`
    with the sha main ACTUALLY moved to, or the new `AbandonMerge` when nothing landed; the middle leg
    was simply absent, so pressing Merge recorded a merge (and the cached PRE-merge sha) that git had
    never performed. `SetActiveRepo(handle, localRepoPath, syncRemoteName)` carries the rest of the
    `ProvisionRepo` answer, because a handle alone can observe the queue but cannot merge.
    `DaemonBackedOrchestrator.RunVerificationAsync` is the **verification trigger's production caller** —
    the rung that did not exist, which is why `DaemonClient.RunVerificationAsync` was defined and never
    invoked and every queue entry stayed at `not verified yet`. It resolves the active repo handle and
    makes ONE RPC: no policy, no state machine, no local projection mutation. All of that belongs to the
    daemon's `MergeQueue.RunVerificationAsync`, which runs the test command in the *agent's own jail* and
    republishes the new state on the queue stream. A gRPC `FailedPrecondition` (no live jail / no
    configured test command / missing toolchain) is returned as `VerificationOutcome(Ran: false)` with the
    daemon's reason verbatim, kept distinct from a suite that genuinely failed (`Ran: true, Passed: false`);
    the call carries a 30-minute deadline because a first run may build the toolchain image.
    `FlaggedChangeSource.cs` — `IFlaggedChangeSource` + `DaemonFlaggedChangeSource` +
    `FlaggedAckOutcome`: the seam a review surface reads its must-acknowledge items from (the daemon's
    `QueueEntry.FlaggedItems` projection) and routes per-item acknowledgments through (the daemon's
    `AcknowledgeFlaggedChange` RPC, which is where the gate that blocks the merge actually lives). The
    **review cockpit overlay** was built with `changedGate: null`, `queue: null` and a private
    in-process `AcknowledgmentStore`, so it surfaced no daemon-flagged item at all — and a checkmark it
    did draw would have cleared a store no merge consults, telling a human they had unblocked a merge
    that was still blocked. The outcome is the daemon's OWN answer (`Acknowledged`/`CanMerge`/`Reason`
    via `DaemonBackedOrchestrator.AcknowledgeFlaggedChangeReportedAsync`), never the local optimism of
    having called it, and `CanAcknowledgeFlaggedChange` lets the panel disable + explain rather than
    appear to work when no repo is active. MG-11 still applies: acknowledging is human-only, on the
    coordinator denial list.
- **`ViewModels/DaemonLogsViewModel.cs`** + **`Views/DaemonLogsView.axaml(.cs)`** — the read-only
  "Daemon logs" surface, now the Settings **Daemon Logs** page (root is a `UserControl`; it used to be
  its own window opened via `RepoDashboardViewModel.ViewDaemonLogsAsync`, which is gone): a source
  dropdown (the unified journal + each per-subsystem file, from `DaemonLogSubsystems.All`), a
  scrollable monospace log pane on `SurfaceDeep` with the loading spinner centered inside it, and a
  Copy button — over Core's `DaemonLogReader` (constructed directly, no DI: the Agent-CLIs settings
  pattern). The clipboard write is a settable `CopyAction` the View wires; a design/render ctor backs
  the harness;
  - `DaemonLogsViewModel` implements `ISettingsPage` alongside its existing `IDisposable` —
    `OnActivated` refreshes, `OnDeactivated` disposes, and `SettingsViewModel`'s page-switch logic
    specifically discards this row's cache on leaving so the next visit rebuilds fresh rather than
    reusing a disposed instance.

## Role in the solution

- **`Mainguard.App.Shell`** (step 2f/2g — an Avalonia **library**, `OutputType` Library; step 2g
  normalized the assembly name to `Mainguard.App.Shell`, with matching
  `avares://Mainguard.App.Shell/…` resource URIs, `Mainguard.App.Shell.*` CLR namespaces, `x:Class`
  values and `InternalsVisibleTo` targets) — the **edition-agnostic + Client shell**, physically split
  from the two exe heads. **THE PAYOFF of the whole split:** it references **ONLY `Mainguard.UI` +
  `Mainguard.Git`** — never `Mainguard.Agents` / `Mainguard.Agents.UI` / `Mainguard.Protos` /
  `Docker.DotNet` / `Porta.Pty` / Grpc / `Dock.Avalonia` (pinned by
  `EditionReferenceGraphTests.Shell_IsReferenceClean_OfTheAgentPlatform`, keyed on assembly identity)
  — which is exactly what lets the Client head's published `.deps.json` exclude the whole agent
  platform. Holds: the composition-root `App.axaml.cs` (edition-agnostic only — DB migrate,
  `ViewLocator` seed, theme, tray icon, the guarded full-exit
  `RequestFullExitGuardedAsync`/`RequestFullExit`; the Pro launch/shutdown are reached ONLY through
  the null-until-wired seams `App.ProDesktopStarter` / `App.VisualizedShutdownAsync` /
  `App.AfterInitialize` the Pro head fills — under Client they stay null and the launch is the
  deliberately MainguardOS-free `StartClientDesktop` + dedicated Clone first-run); `App.axaml` (Fluent
  + AvaloniaEdit + `avares://Mainguard.UI/…` design system — the Pro-only Dock theme is NOT included
  here, it is injected at runtime by `ProDesktopHost.InjectProChrome`); `ShellEntryPoint` (the shared
  arg-shim + single-instance guard + `BuildAvaloniaApp` both heads call — the git-editor
  `--rebase-editor` / `--rebase-msg` self-invocation shims); `MainWindow`+`MainWindowViewModel`
  (repo-provisioning on open reached via the `IAgentPlatformSurface.ProvisionRepoAsync` seam so
  the shell never names `DaemonClient` — the seam now returns a `RepoProvisionOutcome` and a FAILED
  provision is surfaced with an error toast + a bottom-left retry card
  (`IsAgentProvisionRetryVisible`/`RetryAgentProvisioningCommand`) instead of being swallowed; the
  Pro implementation clears the previously active repo before provisioning, so a failed/slow
  provision leaves the merge rail empty rather than pointed at the previously opened repo — pinned
  by `Mainguard.Tests/RepoProvisioningHonestyTests`; the ctor takes only the degraded-banner STRING,
  never the Pro `StartupResult`); every git + host-collab `Views/`↔`ViewModels/`, `RepoDashboardViewModel`,
  `Controls/`, `Converters/`; `Editions/ClientManifest`; the Client
  `ClientFirstRunWindow`/`ClientFirstRunViewModel`; `VersionsViewModel` (the daemon/OS-version rows
  come from the `Editions/ShellVersionProbe` Mainguard.UI seam — `null` under Client → honest
  "unreachable", the app-version row always shows); and the shell `Services/` (`RepoCatalog`,
  `SyncRemoteRegistrar`, `VmExitGuard`, `DialogConfirmationService`, browser/file-explorer/launch/exit
  helpers). `App.Edition` DEFAULTS to `new ClientManifest()` (the shell cannot name `ProManifest`);
  each head sets the real edition in `Main`. Grants `Mainguard.Tests` `InternalsVisibleTo`.
  `ViewModels/` ↔ `Views/` paired by convention through `ViewLocator` (now in `Mainguard.UI`).
- **`Mainguard.Agents.UI`** (step 2e) — the **Pro agent-platform UI**, physically split out of
  `Mainguard.App.Shell`. Holds every Pro-only `Views/` + `ViewModels/` (Control Center / Coordinator /
  Resources / agent rail / telemetry / queue rail / merge queue / review cockpit / agent workspace +
  document / terminal / OOBE wizard / bootstrap / vibe mode / startup + shutdown windows / CLI-OAuth
  ToS + VM-upgrade offer), the five Pro-only Settings pages (`AgentCliSettingsView`,
  `ApiKeySettingsView`, `DaemonLogsView`, `ToolchainSettingsView`, `ToolchainDeclarationView` — all `UserControl`s
  implementing `Mainguard.UI`'s `ISettingsPage`, embedded by the shell's `SettingsViewModel` — and
  `MainguardOsPageView`/`MainguardOsPageViewModel`, the settings-page host of the old
  `AddReposToOsView`/`AddReposToOsViewModel` add-more-repos engine that also folds in the standalone
  "Rebuild sandbox images" Tools action as `RebuildSandboxImagesCommand`), the Pro-only terminal
  controls — the interim `Controls/TerminalControl`+`VtScreen` and the **P2-18 grid engine**:
  `Controls/TerminalGridControl` (first-party Skia cell grid over `Controls/GridModel` — the pure
  client mirror of the daemon's vterm screen, applying `TerminalOutput` envelopes; damage-driven
  invalidation, bounded glyph/typeface cache with CJK fallback, wide-glyph rendering, the REQUIRED v1
  mouse-selection copy via `Controls/GridSelection` (absolute-row selection, Ctrl+Shift+C + context
  menu, single-space run collapse/newlines/trailing-trim; Shift overrides when the app tracks the
  mouse; Ctrl+C stays SIGINT), OSC 52 → host clipboard from daemon-decoded frames, the three paste
  chords via `Controls/GridInputEncoder` (paste reuses the pinned `BuildPasteBytes`; DECCKM-aware
  keys; SGR/X10 mouse encoders), wheel scrollback over the local ring, and a minimal IME preedit
  overlay at the cursor), `Controls/ITerminalEngineControl` (what `TerminalView` hosts — either engine
  behind the `TerminalEngine` flag via `Services/TerminalEngineSelection`, zero ViewModel change),
  `Converters/AgentStatusBrushConverter`+`DiffLineKindToClassConverter`, the daemon-facing `Services/`
  (`DaemonClient` — the sole gRPC touch-point, G-18 — `DaemonBackedOrchestrator`, `ITerminalGateway`
  (P2-18: with the grid engine selected the attach handshake is `AttachOptions(grid:true)` and
  grid/clipboard frames forward as serialized `TerminalOutput` envelopes through the same byte event —
  the VM shuttles opaque bytes either way), `ICliAgentHost`, `IEgressAllowlistGateway`), the Pro
  manifest (`Editions/ProManifest`+`ProToolsSurface`), and `Editions/ProComposition` (the
  down-injected shell-capability seam). References `Mainguard.UI` / `Mainguard.Agents` /
  `Mainguard.Git` / `Mainguard.Protos` (+ Grpc.Net.Client, Dock.Avalonia, LiveCharts) and **NEVER
  `Mainguard.App.Shell`** (the one-way boundary — pinned by
  `EditionReferenceGraphTests.ProUi_DoesNotReference_App`, keyed on assembly identity since the
  shell/Pro-UI/base-UI types were normalized to distinct `Mainguard.*` namespaces in 2g, but the check
  stays keyed on assembly identity). The shell reaches these Views ONLY via
  `App.Edition`/`ViewLocator` (`ProManifest.ViewAssemblies` = this assembly;
  `App.ComposeViewAssemblies` always prepends the shell so host/git Views resolve too), and the App
  capabilities the Pro UI needs (settings accessor, oobe.log sink, shell-toast, sandbox-image rebuild
  engine, Add-Repos VM factory, shell-window factory, host rail descriptors, and the
  orchestrator-services factory) are injected DOWN through `ProComposition`, wired by the **Pro exe
  head** (`Mainguard.Pro.App.Program.WireProComposition`, mirrors `ThemeManager.PersistKey`). **Step
  2f moved the whole Pro launch/OOBE/startup/shutdown machinery OUT of the shell's `App` and INTO
  `Editions/ProDesktopHost` here** (VM keep-alive, `Start`/`RunVisualizedShutdownThenExitAsync`, the
  OOBE wizard + startup/shutdown sequences, `DecideLaunchRoute`, `CreateAddReposToOsViewModel` (still
  the factory behind the Settings **Mainguard OS** page, now called from
  `ProToolsSurface.CreateMainguardOsPage(Window owner)` instead of a Tools-menu dialog), the
  daemon-version probe, and `InjectProChrome` which adds the Dock theme the shell's `App.axaml` omits)
  — the shell reaches it only through `App.ProDesktopStarter`/`App.VisualizedShutdownAsync`; the
  App-side Pro `Services/` moved here too
  (`ProductionStartupEnvironment`/`ProductionShutdownEnvironment`, `LaunchRouter`,
  `EndToEndDaemonHealthProbe`, `SandboxImageInstaller`, `DaemonUpdateToastPublisher`,
  `AgentNotificationService`, `DockLayoutPersistence`, `DeepLinkHandler`, and
  `MacStartupEnvironment` — the macos-host `IAppStartupEnvironment`: "wake the VM" becomes
  "ensure the local mainguardd runs from the payload" (`MacDaemonController`), tier-1 refresh
  restarts that process (`MacDaemonUpdater`), tier-2 never offers (no OS to upgrade), and the
  image probe/build runs against the host engine through `HostCommandRunner`;
  `ProDesktopHost.CreateStartupWindow` selects it by platform, `DecideLaunchRoute` goes straight
  to the control center on macOS — the OOBE wizard is the WSL2 provisioning flow — and the VM
  stop / agent-CLI update-check paths no-op there), reseamed to reach the shell
  only via `ProComposition` (toasts via `ShowShellToast`, settings via `ProComposition.Settings`,
  version via entry assembly). Grants `Mainguard.Tests` `InternalsVisibleTo` (the moved
  `VtScreen`/terminal grid-readback hooks). Referenced by `Mainguard.Pro.App` (the Pro head — the ONLY
  project that references both this and the shell).
- **`Mainguard.Client.App`** (step 2f, WinExe) — the plain **Git-client exe head**. Thin
  `Program.Main`: `ShellEntryPoint.TryHandleShim(args, out var exit)` (git-editor shims run + return
  first; `Environment.ExitCode = exit` — a failed shim MUST report non-zero),
  `App.Edition = new ClientManifest()`, `ShellEntryPoint.RunDesktop(args)`. **References
  `Mainguard.App.Shell` ONLY** — so its published closure carries only `Mainguard.App.Shell` (the
  shell) + `Mainguard.UI` + `Mainguard.Git`, with ZERO `Mainguard.Agents(.UI)` / `Mainguard.Protos` /
  `Docker.DotNet` / `Porta.Pty` / Grpc (the payoff proof: parse `Mainguard.Client.App.deps.json` —
  CI-enforced by the `client-closure` gate, `build/ci/verify-client-closure.sh`, which also runs
  locally; a Pro-head positive control keeps it non-vacuous).
- **`Mainguard.Pro.App`** (step 2f, WinExe) — the **Pro exe head** and the packaging head. Thin
  `Program.Main`: `App.Edition = new ProManifest()`, point
  `App.ProDesktopStarter`/`App.VisualizedShutdownAsync` at `ProDesktopHost`, and
  `App.AfterInitialize = WireProComposition` (invoked once `App.Settings` exists — bridges shell
  capabilities DOWN into `ProComposition`: settings, oobe.log, `ShowShellToast`, `CreateShellWindow`,
  `AddReposToOsFactory`, `PersistRepo` + `ProvisionRepoIntoOs`, `HostRailSections` (naming the shell's
  host ViewModels), `ShellVersionProbe.Query`; the ONE place that names BOTH the shell and the Pro UI
  — neither may name the other across the one-way boundary). **References `Mainguard.App.Shell` +
  `Mainguard.Agents.UI`** (+ the `Mainguard.Installer.Elevated` helper,
  `ReferenceOutputAssembly="false"`), and OWNS the MainguardOS payload-bundling MSBuild targets
  (`MainguardOS.tar.gz` / daemon fast-path / jail images / release stamp / elevated-helper
  co-location) moved here from the old pre-split app project (`Mainguard.Pro.App.csproj` now) — only
  the Pro edition ships the VM.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
