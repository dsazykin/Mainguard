<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
### Tests & tooling

- **`Mainguard.Tests/MainguardPathsGuardTests.cs`** — the structural guard for the mainguardd
  crash-loop bug class: scans the shipping source and fails on any `Environment.GetFolderPath` outside
  `MainguardPaths.cs` (see that entry).
  - **`Mainguard.Tests/MainguardPathsMigrationTests.cs`** — the Phase-4 Windows data-root migration
    policy over `MainguardPaths.TryMigrateDataRoot`: moves legacy→current only when current is absent
    (preserving contents), no-ops when current already exists or on a fresh install, and is idempotent.
  - **`Mainguard.Tests/AgentCliUiTests.cs`** — the P2-22 §J-5 CLI-picker surfaces (OOBE step + settings
    window) driven over fake channel/host seams with the REAL `AgentCliInstaller`/`AdapterChannel`: an
    install failure never blocks finishing setup, failures name an actionable cause (hash-mismatch →
    "checksum"), one CLI failing doesn't stop the others, skip finishes with zero CLIs, an
    already-installed CLI isn't re-offered, Cancel leaves no row spinning, and a catalog-read failure
    still lets the user through.
  - **`Mainguard.Tests/AddReposToOsViewModelTests.cs`** — the post-setup Add-Repos-to-Mainguard-OS
    window over the same fake seams as `OobeRepoOnboardingTests`: honest empty scan, per-row failure
    isolation with a live retry, the named daemon-unreachable cause (never a crash), quiet idempotent
    success for an already-provisioned repo, mid-run cancel, Close wiring; because the window IS the
    OOBE step's engine, it pins the two surfaces to one behaviour.
  - **`Mainguard.Tests/Headless/AgentCliSettingsRenderHarness.cs`** — the Agent CLIs window in all five
    themes × list/installing/failure/loading/load-error →
    `artifacts_headless/agent_cli_settings_<Theme>_<state>.png` (`OobeWizardRenderHarness` gained the
    picker's `clis_pick`/`clis_installing`/`clis_results` states).
  - **`Mainguard.Tests/Headless/RowCommandEnablementTests.cs`** — the "visible but permanently
    disabled button" regression (owner: *"the update cli button isn't clickable"*). Renders the real
    `AgentCliSettingsView`, flips a row's `UpdateAvailableVersion` / `PreviousVersion` / `IsInstalled`,
    and asserts the rendered `Button.IsEffectivelyEnabled` — deliberately NOT visibility, which is the
    half that always worked. Pins the whole class: the per-row Install/Update/Revert commands live on
    `AgentCliSettingsViewModel` but their `CanExecute` reads ROW state, so the parent must bridge row
    `PropertyChanged` → `…Command.NotifyCanExecuteChanged()` (`[NotifyCanExecuteChangedFor]` on the
    parent's `IsBusy` covers only half of it). The ViewModel-level twins that pin the notification
    itself live in `AgentCliUiTests.Settings_Row*`.
  - **`Mainguard.Tests/Headless/MainWindowShellRenderHarness.cs`** — the real `MainWindow` shell:
    top-nav/toolbar + opening overlay (`mainwindow_shell.png`), the Settings window's pinned-menu
    picker (`settings_window.png`), and BOTH toast hosts pinned to the **bottom-right** corner with
    measured geometry (not eyeballed) plus five-theme PNGs — the dashboard stack
    (`toasts_stacked_<Theme>.png`) and the shell-level stack
    (`shell_toasts_bottom_right_<Theme>.png`), the latter also covering upward stacking order. The
    position tests exist because the shell host lost its `Grid.Row="1"` and rendered top-right out of
    the title-bar row; they fail with the measured y-offset rather than a bare boolean.
  - `StartupShutdownRenderHarness` (owner design 2026-07-17) renders `StartupWindow` + `ShutdownWindow`
    in all five themes across the key states (`loading_early`, `upgrade_consent`, `upgrade_running`,
    `degraded`, and the shutdown `stopping_vm`/`releasing`), plus the `MainWindow` degraded banner —
    PNGs `startup_<Theme>_<state>.png` / `shutdown_<Theme>_<state>.png` /
    `mainwindow_banner_<Theme>.png`;
  - `StartupShutdownViewModelTests` covers the checklist/status mapping, the tier-2 host toggle, and the
    `MainWindowViewModel` banner.
  - **`Mainguard.Server.Tests/SubsystemFileLoggerProviderTests.cs`** +
    **`DaemonStartupLoggingTests.cs`** (in-depth daemon logging) — category→file routing, rolling at the
    cap, the line format, mask fidelity, swallowed IO, and the
    `DaemonLogCategories`↔`DaemonLogSubsystems` lockstep; plus the Lifecycle "bound" + Migration
    ("preparing db / stale migration lock cleared / migrate ok / watchdog fired") milestones (the
    watchdog fallback driven through `TryPrepareDatabase` directly).
  - `LoggingMaskTests` gains a non-`RpcException`-under-`Rpc` handler-fault test;
  - `SpawnImagePreflightTests` asserts the `Spawn` step sequence + the image-missing failure.
  - **`Mainguard.Tests/Headless/DaemonLogsRenderHarness.cs`** — the Daemon logs window in all five
    themes × populated/spawn/loading/empty → `artifacts_headless/daemon_logs_<Theme>_<state>.png`.
- **`Mainguard.Tests/`** — xUnit tests for Core (`MockOrchestratorTests` — the Lane E mock daemon's
  invariants: stale cascade on merge, gate reasons, freeze-first kill switch, plan-approval spawn,
  prompt queue, deploy phases; `Headless/ControlCenterRenderHarness` — the P2-13 pattern: the
  coordinator surface rendered in all five themes + both layouts + Vibe/triage + post-cascade/frozen
  states, PNGs to `artifacts_headless/`;
  **`Headless/ControlCenterPanelSizingRenderHarness.cs`** — the Control Center's panel *sizing*, which
  the harness above cannot see because `MockOrchestrator`'s fixtures are short friendly strings
  ("Loom-3", "fix/auth-refresh") while `DaemonBackedOrchestrator` projects `Name = AgentId` (32 hex)
  and `Branch = agent/<id>`. Rewrites the rail's entries to production id lengths, then asserts the
  merge-queue seam is a real draggable/keyboard-resizable `GridSplitter`, that widening the window
  widens the queue (bounded at 640px), that no queue text is arranged past the rail's right edge
  (**geometric** overflow — `TextLayout.HasCollapsed` alone is vacuous here, a horizontal `StackPanel`
  measures at infinite width and never trims), that the telemetry row resizes and still collapses when
  Conversation Deck hides it, and that the seam's hover accent is a different colour from its rest
  state in every theme. PNGs: `control_center_sizing_<narrow|default|wide>_<Theme>.png`,
  `control_center_seam_hover_<Theme>.png`; the **P2-10 suite** — `MergeQueueStateMachineTests`
  (exhaustive legal + typed-illegal transitions, property test, stale-cascade FIFO, loud override
  audited/`CanMerge`-still-false, no-test-command typed, immutable records, restart-resume,
  `NoAutoMergePathExists`, RT-D2 gamed-command flagged + `VerificationCommandResolver`),
  `VerificationRunnerTests` (daemon-observed exit is pass/fail,
  `ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit`, runs-in-sandbox-never-host, artifact
  provenance), `ForegroundMergeServiceTests` (real-git: journaled/undoable A5 ff-only merge,
  CAS-lost-when-main-moved, the always-`--ignore-scripts` poisoned-postinstall canary with an injected
  EBUSY retry, and the RT-D1 `DaemonCrashMidMerge` committed-but-unconfirmed exactly-once +
  never-committed release), `Integration/StaleCascadeTests` (two/three-worker cascade → re-verify →
  merge blocked until fresh; fail-after-rebase → Working), `Headless/MergeQueueRenderHarness` (the
  real-`MergeQueue` rail in all five themes → `merge_queue_<Theme>.png`); the **P2-11 review-cockpit
  suite** — `RiskClassifierTests` (fixture corpus: every category + the scripts-vs-dependency-bump
  distinction + rename-by-new-path), `ProvenanceReaderTests` (trailer matrix
  present/partial/absent/malformed→nullable, Agent-Trace ours + external-vendor parse + range-join,
  `AgentTraceEmitter` serialize/trailer round-trip), `FlaggedChangeDetectorTests` (the four
  flag-worthy categories, `OutOfScopeDiff_ShouldBeDedicatedFlaggedItem` F6 + plan-less skip,
  `ChangedTestCommand_ShouldBlockCanMergeUntilAcked` RT-D2 panel half, `ScopeMatcher` globs),
  `AcknowledgmentTests` (hash-change invalidation, unrelated-file hash-unchanged, item-by-item gate
  composition + events, global-ack-impossible-by-construction), `LockfileSemanticDiffTests`
  (per-ecosystem delta + install-scripts + offline OSV CVE), `TestDeltaParserTests` (TRX/pass-fail →
  new-fail/new-pass), `ReviewCockpitViewModelTests` (risk-ordering-reorders-never-hides, provenance
  present/absent, `BringBranchLocal` T-29 round-trip, review-sprint deferred→unviewed, flagged-gate
  blocks merge), `Integration/PoisonedBranchGateTests` (`PoisonedBranch_EndToEnd` — poisoned
  postinstall → Verified-but-blocked → item-by-item ack → CanMerge, + new-push re-arm),
  `Headless/ReviewCockpitRenderHarness` (the real cockpit in all five themes →
  `review_cockpit_<Theme>.png`); `Headless/MainWindowRailRenderHarness` — the integrated MainWindow
  with the section rail (repo section, coordinator section, collapsed rail) + the task-manager
  resource monitor (rows + end-confirm) + the repo picker; `GitServicesTests`, `GitServiceTagTests`,
  `GitServiceWorktreeTests`, `WorktreePorcelainParserTests`, `GitServiceDiffAgainstCommitTests`,
  `InteractiveRebaseServiceTests`, `PatchParserTests`, `PatchBuilderTests`,
  `GitServicePartialStagingTests`, `CommitGraphRouterTests`, `CommitGraphRouterWideDagTests` (the H2
  net: pathological 50k×64-lane structural bench with printed same-run before/after timing — never a
  timing assert, ADR-007 — plus chunked-equals-whole through the fringe, seeded random-DAG invariants,
  and output-equivalence against an embedded verbatim copy of the pre-optimization router),
  `PureEnginePropertyTests` (ADR-005: seeded-random property laws — PatchParser Serialize∘Parse
  byte-identical round-trip, MergeChunker conservation/no-spurious-conflict/base-coverage laws,
  ChangelogGenerator nothing-ever-dropped), `GitServiceIndexLockTests` (Part-5 reliability: a held
  `.git/index.lock` clears mid-backoff → silent retry success; wedged → typed actionable
  `GitOperationException`, repo stays usable; reads unaffected), `GraphHitTesterTests` (pure graph
  hit-testing, T-09), `CommitTimelineMenuTests` (graph context-menu construction, hard-reset/delete
  confirmation routing, drag merge/rebase flyout, T-09), `LabelDragGestureTests` (T-09b pure: the
  drag-gesture threshold state machine — a sub-threshold move never begins a drag (click/right-click
  preserved), a past-threshold move begins it once, Cancel clears state), `PinnedRefsTests`
  (pinned-ref persistence + router left-most ordering + migration apply, T-09),
  `GitServiceCurrentBranchFilterTests` (current-branch-only walk, T-09), `GitServiceRemoteTests` (T-10
  remotes CRUD + push options: tracked-remote resolution, zero-remote typed throw, the
  force-with-lease succeed/stale-fail safety pair, set-upstream config, push-tags — the CLI push paths
  carry `RequiresGitCli`), `AutoFetchServiceTests` (T-10 auto-fetch:
  cadence/enable/skip-in-op/no-self-overlap/failure-count via a fake `IGitService` + the
  `RunCycleAsync` seam — no real waiting), `GitServiceBlameTests` (T-11 blame: per-line→SHA mapping
  over disjoint-edit commits, starting-at-prior-commit, typed throw on missing path, cache
  invalidation on HEAD change), `BlameCacheTests` (T-11 bounded-LRU eviction + per-repo invalidation),
  `BlameViewModelTests` (T-11 cancel-stale-load on rapid file switch), `GitServiceFileHistoryTests`
  (T-12 file history: touching-commits-newest-first, rename following with historical paths,
  blob-at-commit + typed binary/missing throw, adjacent-diff equals the tree diff,
  introduce/delete-then-gone/path-with-spaces edge cases, plus the pure line-range filter),
  `FileHistoryViewModelTests` (T-12 VM: newest-auto-select, selection→predecessor diff, introduction
  all-additions render, binary placeholder, line-filter narrowing via `FakeGitService`),
  `LineHistoryFilterTests` (T-12 pure hunk-intersection geometry — old/new-side, boundaries, omitted
  counts, multi-hunk), `IntraLineDiffTests` (T-13 pure intra-line word spans —
  single-word/full-rewrite/empty/whitespace-only/CRLF pinned ranges + surrogate-pair safety theory),
  `WhitespaceMarkersTests` (T-13 trailing-whitespace ranges), `ImageDiffDetectionTests` (T-13
  image-candidate table + binary sniff + size summary), `GitServiceWhitespaceDiffTests` (T-13
  `git diff -w` zero-hunks/real-hunks/staged/no-eol; `RequiresGitCli`),
  `DiffViewerViewModelDiffQualityTests` (T-13 VM: partial staging hidden in `-w` mode, syntax-toggle
  persistence, intra-line spans + trailing-whitespace + image-mode detection), `SettingsServiceTests`,
  `AppDbContextTests`, `GitHostDetectorTests`, `HostProviderRegistryTests` (T-14 provider resolution
  by host+kind + single-source `TokenUsername` + PAT-prompt acquire/throw-with-host),
  `SshKeyServiceTests` (T-14 ArgumentList argv construction + a REAL local ssh-keygen round trip —
  generate → files exist → `ListKeys` finds it → passphrase round-trips through the keyring,
  `RequiresGitCli`), `SecureKeyringTests` (T-14 round-trip + null-on-corrupt via the path override +
  encrypted-at-rest), `CredentialResolverTests` (T-14 SSH-vs-token credential selection),
  `AccountsViewModelTests` (T-14 known-host catalog + PAT store/remove + add-custom-host),
  `SignatureStatusParserTests` (T-15 pure: the `%G?` code table + batched-log parse incl.
  separator-in-signer/CRLF/empty), `GitServiceSigningTests` (T-15 integration, `RequiresGpg`: signed
  commit verifies + `%G?`==Good, signing-off→None, signed annotated tag verifies,
  signed-then-read-without-key→not-Good, bogus-key→typed throw without hang — an ephemeral throwaway
  GNUPGHOME + passphrase-less ed25519 key generated with the same gpg git invokes; skips cleanly when
  gpg is absent)), `SubmoduleStatusMapperTests` (T-16 pure: every representative `SubmoduleStatus`
  flag combination pinned to its `SubmoduleState`, the Modified-over-Dirty precedence, and an
  exhaustive total/deterministic sweep over all flag bits), `GitServiceSubmoduleTests` (T-16
  integration, `RequiresGitCli`: fresh-clone uninitialized→up-to-date after init via deinit + cached
  gitdir, inner-commit flags the superproject Modified, uncommitted inner change flags Dirty, multiple
  entries listed path-sorted, path-with-spaces round-trip, missing-.gitmodules empties, sync after URL
  edit — superproject + file-based submodule built with `protocol.file.allow` in test arrangement
  only) plus ViewModel/render tests,
  `LfsPointerTests`/`LfsLsFilesParserTests`/`LfsAttributesParserTests` (T-17 pure: pointer detection
  incl. malformed variants, ls-files split with space-bearing paths, `.gitattributes` filter=lfs
  extraction — all pinned to real git-lfs 3.5.1 output), `GitServiceLfsTests` (T-17 integration,
  `RequiresGitLfs`: install/track writes .gitattributes + commit-through-CLI yields a pointer,
  ls-files lists the object, untrack round-trip, no-match-empty, existing-.gitattributes-append,
  path-with-spaces, install/uninstall enable-state, prune dry-run summary, pull-with-no-remote typed
  throw, and an un-gated graceful-degrade case forcing availability false via the override so every
  method throws "not installed" without touching git), `DiffViewerViewModelLfsTests` (T-17 VM: a
  pointer-side diff renders "LFS object (size)" and skips hunks; ordinary text diff does not),
  `FuzzyMatcherTests` (T-18 pure: the `"chb"` ranking table + exact pinned scores, word-boundary and
  consecutive-run bonuses, non-subsequence exclusion, case-insensitivity, empty-query-matches-all,
  matched-position highlighting, non-negative-real-match, deterministic tie-break),
  `ActionRegistryTests` (T-18 pure: `CanExecute` filtering incl. live re-evaluation and throwing
  predicate, duplicate/empty-id throw, registration order, `Find`), `ShortcutMapTests` (T-18 pure: the
  five defaults, duplicate-gesture conflict detection, case/modifier-order normalization, immutable
  rebind, gesture→action resolution, defaults overlay, and the UserPreferences JSON round-trip),
  `CommandPaletteViewModelTests` (T-18 VM: browse-mode grouping/headers, ranked filtering with
  highlighted segments, no-match empty state, header-skipping/wrapping navigation,
  activate-runs-and-closes, and re-snapshot-on-Reset for availability changes),
  `OperationJournalTests` (T-19 the heart of undo/redo: a `[Theory]` round-trip over **every**
  LibGit2Sharp-driven op kind — Commit, Amend, fast-forward Merge, Rebase, Reset(soft/mixed/hard),
  Revert, CherryPick, CreateBranch, CreateBranchAt, RenameBranch, DeleteBranch, StashPush, annotated
  TagCreate, TagDelete, Checkout — asserting Undo restores all ref SHAs + HEAD target byte-exactly and
  Redo restores the post-state via a `CaptureRefState` dictionary oracle, plus the five named tests:
  branch-delete-restores-upstream, dirty-tree-refuses-typed-and-changes-nothing,
  new-op-after-undo-truncates-redo, non-undoable-flagged-with-reason (StashDrop), and
  persist-across-context-reopen), `OperationJournalCliTests` (T-19 `RequiresGitCli`: the
  interactive-rebase round-trip driven through Mainguard.Client.App's rebase-editor argv shim, and a
  live push flagged non-undoable + undo-refused), `GitServiceReflogTests` (TI-20 reflog reads +
  recovery: commit→hard-reset shows both moves with correct from/to, newest-first ordering + zero-from
  on the creation entry, create-branch-here at the pre-reset entry restores the orphaned commit,
  deleted-branch recovery finds the orphaned tip via HEAD's reflog, fresh/empty reflog, detached-HEAD
  move, multi-line commit message collapsed to one line, a branch with no reflog (logallrefupdates
  off) → empty, friendly-name resolution, missing-ref typed throw, take-cap keeps newest),
  `ReflogViewModelTests` (TI-20 VM against a real journal-backed `GitService`: confirmed Restore
  hard-resets + lands a journaled undoable `ResetToCommit` (undo verified), declined Restore mutates
  nothing, create-branch-here recovers an orphaned tip + journals `CreateBranchAt`, empty name
  rejected, ref-switch reloads that ref's reflog), `GitServiceCheckoutPrWorktreeTests` (TI-29 check
  out a PR/branch into a worktree: the pure `PullRequestHeadRef` per-host `[Theory]` (GitHub/GitLab
  mapped, Bitbucket/AzDO/Unknown + non-positive number throw typed), plus `RequiresGitCli` integration
  over a **local file:// fixture remote carrying a synthetic `refs/pull/1/head`** — a github.com
  origin URL rewritten to the local bare via `url.insteadOf` so the host resolver sees GitHub while
  the fetch stays offline — proving `CheckoutPullRequestWorktree` creates branch `pr/1` + a worktree
  whose HEAD **and** files match the PR commit, the non-empty-target typed refusal that creates
  nothing, the re-checkout-while-branch-in-use typed throw + cleanup (no half-made worktree), and
  `CheckoutBranchWorktree` over a local branch and a remote-tracking branch (local tracking branch
  created, worktree matches)), `PullRequestProviderTests` (TI-23 provider parsing against checked-in
  JSON fixtures through an injected `HttpMessageHandler` — no live network:
  list/get/create/merge/close map to the models, `merged_at`/draft state derivation, merged-filter
  selection, error bodies → typed exceptions (401→`AuthenticationRequiredException` with host, 403
  rate-limit + 405/merged:false → typed `GitOperationException`, 422 already-exists → "a pull request
  already exists"), network-down → typed not raw, the create/merge request bodies + PATCH state:closed
  asserted, and the **token-security** invariants: the token appears only in the
  `Authorization: Bearer` header — never the URL, request body, a produced model string, or an
  exception — and error text echoing the token is redacted; **TI-25** review parsing also lives here —
  reviews list (each state, dropping the blank PENDING), inline comments (incl. the outdated
  `line==null` one), the verdict→event `[Theory]` (Approve/RequestChanges/Comment → the exact GitHub
  event + body asserted), a 422 "can't approve your own PR" → typed, and the review
  token-never-leaks/redaction sweep), `PullRequestServiceTests` (TI-23 the `IsSupported` host/token
  matrix over a real repo + temp `SecureKeyring` — GitHub+token→true,
  no-token/GitLab-stub/unknown-host/no-remote→false, ssh remote resolves; dispatch routes to the
  GitHub provider at `/repos/owner/repo/pulls` over the injected client with the Bearer header,
  no-token/unsupported operations throw typed; plus the `GitHostDetector.ParseOwnerRepo` slug matrix),
  `PullRequestsViewModelTests` (TI-23 VM gating over fakes: detached/unborn HEAD disables Create with
  a hint, attached prefills source/target/title, `IsBusy` gates every command, unsupported host shows
  the affordance + disables actions, blank title disables submit, and the list marshals into the
  collection; **TI-25** review gating — opening Review loads reviews + groups comment threads by path
  (outdated comment maps), the verdict-flag → badge mapping, submit-review requires a body unless
  Approve, `IsBusy`/unsupported disable submit, and submit routes the verdict+body through the service
  then clears the body; **TI-29** check-out-locally gating — a successful checkout offers
  Open-worktree + routes the open callback, a cancelled folder pick is a no-op, a typed error surfaces
  + keeps the worktree closed, and it no-ops while `IsBusy`), `IssueProviderTests` (TI-24 provider
  parsing against checked-in JSON fixtures through an injected `HttpMessageHandler`: the **mixed
  PR+issue list proving items with a `pull_request` object are filtered OUT**, get+comments (unicode
  body preserved), create (labels/assignees body asserted), comment, close/reopen PATCH state, error
  bodies → typed (401→auth-required+host, 403 rate-limit, 422 bad-label host message, network-down →
  typed not raw), and the token-security invariants — token only in the `Authorization: Bearer`
  header, never a URL/body/model/exception, echoing errors redacted), `IssueServiceTests` (TI-24 the
  `IsSupported` host/token matrix over a real repo + temp `SecureKeyring` — GitHub+token→true,
  no-token/GitLab-stub/unknown-host/no-remote→false, ssh resolves; dispatch routes to the GitHub
  provider at `/repos/owner/repo/issues` with the Bearer header; no-token/unsupported throw typed),
  `IssuesViewModelTests` (TI-24 VM gating over fakes: unsupported host shows the affordance + disables
  actions, `IsBusy` gates every command, blank title disables submit, the Open/Closed filter reloads
  with the matching state, the list marshals into the collection, label chips built with a
  host-colored background + auto-contrast foreground (dark text on a light label),
  create/close/comment route through the service), `NotificationMapperTests` (TI-27 pure: every
  `reason` + `subject.type` → its enum, unknown/blank → `Other`, case/whitespace-insensitive),
  `NotificationProviderTests` (TI-27 provider parsing against checked-in JSON fixtures through an
  injected `HttpMessageHandler`: the mixed read/unread list maps reasons/subject-kinds + best-effort
  api→html URLs (incl. a null subject URL → empty), unread-only sets `all=false` (all → `all=true`),
  mark-read PATCH `threads/{id}` + mark-all PUT `/notifications` `{"read":true}` request shapes, error
  bodies → typed (401→auth-required+host, 403 rate-limit, network-down → typed not raw), and the
  token-security invariants — token only in the `Authorization: Bearer` header, never a
  URL/body/model/exception, echoing errors redacted), `NotificationServiceTests` (TI-27 the
  `IsSupported` host/token matrix over a real repo + temp `SecureKeyring` — GitHub+token→true,
  no-token/GitLab-stub/unknown-host/no-remote→false, ssh resolves; dispatch routes to the GitHub
  provider at `/notifications` with the Bearer header; no-token/unsupported throw typed),
  `NotificationsViewModelTests` (TI-27 VM gating over fakes: unsupported host shows the affordance +
  disables actions, `IsBusy` gates every command, the list **groups by repository** newest-first, the
  Unread-only toggle reloads with the matching flag, mark-read routes the thread id / no-ops on an
  already-read row, mark-all routes through the service, the reason chip + subject-kind flags map),
  `ChangelogGeneratorTests` (TI-28 pure: `ParseSubject` across
  feat/fix/scope/`!`/`BREAKING CHANGE`/type-lowercasing/plain→other/short-sha, and `BuildNotes` pinned
  **byte-exact** — grouped Breaking/Features/Fixes/Other, the scope-prefixed `- desc (sha7)` lines,
  the Full-changelog range vs no-prev footer, empty→empty, and breaking-not-double-listed),
  `ReleaseServiceGenerateNotesTests` (TI-28 local notes over a real fixture repo: since-previous-tag
  excludes the tagged+older commits, highest-semver tag chosen as previous when several exist,
  no-prev-tag covers whole history with no range, empty repo → empty string, non-conventional subjects
  grouped under Other), `ReleaseProviderTests` (TI-28 provider parsing against checked-in JSON
  fixtures through an injected `HttpMessageHandler`: list maps
  draft/prerelease/name-fallback/null-published, create request-body shape
  (tag/target/name/body/draft/prerelease, target omitted when blank),
  422-already-exists/401-auth/network-down → typed, and the token-security invariants — token only in
  the `Authorization: Bearer` header, never a URL/body/model/exception, echoing errors redacted),
  `ReleaseServiceTests` (TI-28 the `IsSupported` host/token matrix over a real repo + temp
  `SecureKeyring` — GitHub+token→true, no-token/GitLab-stub/unknown-host/no-remote→false, ssh
  resolves; dispatch routes to the GitHub provider at `/repos/owner/repo/releases` with the Bearer
  header; no-token/unsupported throw typed; and `GenerateNotes` works with no token/remote since it's
  local), `ReleasesViewModelTests` (TI-28 VM gating over fakes: unsupported host shows the affordance
  + disables actions, `IsBusy` gates every command, Publish gated on a non-blank tag, target prefilled
  from the current branch, picking an existing tag fills the tag box, Auto-generate fills the body
  from the local service, Publish routes the draft/prerelease `CreateRelease` then reloads, the list
  marshals with Draft/Prerelease badges), `ProfileServiceTests` (TI-21 profiles: CRUD, the
  case-insensitive duplicate-name typed throw on create + update, the cancel-safe delete/restore
  round-trip preserving the id, and the invariant — `Apply` writes identity/signing to **local**
  config only with the real global `.gitconfig` byte-identical/absent before-and-after),
  `CloneServiceTests` (TI-21 clone, zero-network `file://`/local fixtures: monotonic
  `ReceivedObjects`/`Percent` to completion, bare-repo clone completes, **cancel-via-transfer-callback
  throws and deletes the partial dir**, pre-cancelled token, clone-into-non-empty-dir typed throw,
  fresh-empty-dir succeeds), `HostRepositoryServiceTests` (P2-48 "list my repositories": GitHub
  `/user/repos` + GitLab `/api/v4/projects` map to `RemoteRepository` through an injected
  `HttpMessageHandler` — no live network — asserting the per-host endpoint/query, self-hosted GitLab
  origin, private/description mapping, `token_<host>` resolution + the `github_token` fallback,
  Bitbucket/AzDO unsupported, and G-4 token-only-in-Authorization), `ProfilesViewModelTests` (TI-21
  profiles VM against a fake `IProfileService`: new/save, blank + duplicate-name inline errors, the
  cancel-safe delete→undo/dismiss, apply-with/without-repo), `WorktreePanelViewModelTests` (TI-21
  worktree VM against a canned `FakeGitService`: the branch-already-checked-out → `CanCreate` false
  rule, free/new-branch valid, and the `AddWorktree` create-flag routing), `RepositoryAnalyzerTests`
  (TI-22 analytics: the gitignore-aware language walk counting exactly the non-ignored bytes with a
  `!keep.js` negation honored and `.git/` skipped, cancellation honored on a large synthetic tree +
  the history walk, the pure punch-card/churn/contributor aggregators pinned exact on fixed
  `DateTimeOffset`s (offset wall-clock bucketing, weekly zero-fill, merge-exclusion, case-insensitive
  email merge), plus integration walks proving churn/timestamps, binary-excluded churn,
  merge-flagged-with-zero-churn, and the empty-repo/single-commit/commit-cap edge cases).
  - `SecretPatternsTests` (TI-30 pure: each named rule matches its planted sample, the rule surface is
    bool-only so a value can never be returned, no false positive on innocuous text, and the entropy
    helper), `PreCommitScanEngineTests` (TI-30 pure: the **pinned** mixed-input finding set — secret +
    three merge markers + oversized binary — in deterministic order, the **no-secret-in-any-message**
    invariant, binary-never-text-scanned, LargeFile-by-threshold, ManyFiles-over-threshold,
    merge-marker-not-mid-line, clean-tree-empty), `PreCommitScannerTests` (TI-30 `RequiresGitCli`: over
    a fixture repo, a staged AWS key + merge-marker file + >5 MB blob yields the matching findings, a
    clean stage yields none, only staged changes are scanned (not the working tree), a custom size cap
    flags a mid-size blob, and the AWS key value leaks into no message),
    `PreCommitScannerViewModelTests` (TI-30 `RequiresGitCli`: `PreCommitFindingsViewModel` groups by
    severity + reports blockers/all-clear, `AutoScanEnabled` persists to a real `SettingsService`,
    `CommitAnyway` raises `CommitConfirmed` + resets; and `StagingPanelViewModel` gating over a real
    journal-free GitService — a secret blocks the commit then Commit-anyway overrides it, a disabled
    toggle commits without scanning, a clean stage commits and shows all-clear),
    `ConventionalCommitBuilderTests` (TI-31 pure: `Build` pinned byte-exact for
    feat+scope+body+co-author+closes / fix-breaking / minimal / empty-scope /
    multi-co-author-with-malformed-dropped / breaking-without-description / bare-number→`#`, `Validate`
    errors vs warnings (unknown/known type, empty description, subject > 72, malformed/well-formed
    co-author, breaking-without-description, clean-imperative-none), and `Parse` + the `Parse(Build(d))`
    round-trip recovering the stable fields incl. a CRLF message), `CommitComposerViewModelTests` (TI-31
    VM: preview updates live from the fields, validation issues + `HasErrors` gate, add/remove co-author
    and issue chips flow into the preview, the over-72 counter toggle, `Changed` fires, and `Clear`
    resets) + `StagingPanelComposerTests` (TI-31 `RequiresGitCli` over a fixture repo: the
    plain⇄structured toggle persists to a real `SettingsService`, a structured commit uses the assembled
    message through the commit path, and a structured commit is still gated by the T-30 pre-commit scan
    then committed on override). Shared test doubles live in `Fakes/` (`FakeGitService.cs` — a no-op
    `IGitService` fake for VM tests; `FakeLfsService.cs` — a delegate-backed `ILfsService` fake for the
    T-17 LFS panel render/VM tests; `FakePullRequestService.cs` — a delegate-backed
    `IPullRequestService` fake for the T-23 PR panel render/VM tests (T-25 added
    review/comment/submit-review delegates); `FakeIssueService.cs` — a delegate-backed `IIssueService`
    fake (captures the last list filter / set-state / comment) for the T-24 issues panel render/VM
    tests; `FakeCheckStatusService.cs` — a delegate-backed `ICheckStatusService` fake for the T-26
    checks panel render/VM tests; `FakeNotificationService.cs` — a delegate-backed
    `INotificationService` fake (captures the last `onlyUnread` / mark-read id / mark-all count) for the
    T-27 notifications inbox render/VM tests; `FakeReleaseService.cs` — a delegate-backed
    `IReleaseService` fake (captures the last create request / generate args) for the T-28 releases
    panel render/VM tests). T-23 provider fixtures (checked-in GitHub JSON:
    list/create/detail/merged/not-mergeable + 401/403/422 error bodies, plus the T-25 review fixtures —
    `github_reviews.json` (each state incl. a blank PENDING to drop), `github_review_comments.json`
    (incl. an outdated `line:null` comment), `github_review_submitted.json`,
    `github_error_422_review.json`) live in `Fixtures/PullRequests/`; T-24 issue fixtures (a **mixed
    PR+issue** list, detail, comments, created, comment-created, closed + 401/403/422-bad-label error
    bodies) live in `Fixtures/Issues/`; T-27 notification fixtures (a mixed read/unread list spanning
    PR/issue/commit/release/discussion subjects across two repos incl. a null subject URL, and an
    unread-only list) live in `Fixtures/Notifications/`; T-28 release fixtures (a releases list spanning
    stable/prerelease/draft + a created-release body + a 422 already-exists error) live in
    `Fixtures/Releases/`; T-32 commit-context fixtures (commit→pulls with one / several / none, PR
    bodies carrying `#n` / cross-repo / closing-keyword refs) live in `Fixtures/CommitContext/`.
  - `TestData/patches/` holds the real-git patch corpus (LF-locked) for the parser round-trip. The
    project references **both** `Mainguard.Agents` and `Mainguard.App.Shell`.
  - `Headless/TestAppBuilder.cs` (`[AvaloniaTestApplication]`) sets up headless Avalonia with Skia
    (`UseHeadlessDrawing=false`) so `[AvaloniaFact]` tests drive real Views and can capture rendered
    frames;
  - `Headless/ResolverRenderHarness.cs` (conflict resolver), `Headless/TagUiRenderHarness.cs` (tag UI),
    `Headless/PartialStagingRenderHarness.cs` (partial-staging diff viewer),
    `Headless/InteractiveRebaseRenderHarness.cs` (interactive-rebase plan + fold rail),
    `Headless/GraphInteractionsRenderHarness.cs` (commit graph with a context menu open, driving
    right-click hit-testing, T-09), `Headless/LabelDragRenderHarness.cs` (T-09b drag-to-rebase/merge
    pointer gesture: **injects** press→move-past-threshold→move-onto-another-chip→release and asserts
    the gesture resolves the right source+target and produces the two-action flyout, captures a mid-drag
    frame with the ghost + drop-target highlight, and asserts release-on-self opens no flyout),
    `Headless/RemotesUiRenderHarness.cs` (T-10 remotes-manager window, populated + empty states),
    `Headless/SubmodulesUiRenderHarness.cs` (T-16 submodules window — populated with all four status
    chips via a canned `FakeGitService` list, plus the empty state), `Headless/LfsUiRenderHarness.cs`
    (T-17 Git LFS window — populated tracked-patterns + LFS-objects with Downloaded/Pointer chips via a
    canned `FakeLfsService`, plus the not-installed state), `Headless/BlameRenderHarness.cs` (T-11 blame
    gutter — age-heat bar + author/sha/date against a fixture repo),
    `Headless/BlameCommitContextRenderHarness.cs` (T-32 — the blame → PR/issue "Why this line" popover
    open over the gutter: PR chooser + linked-issue rows), `IssueReferenceParserTests` (TI-32 — the pure
    parser pinned: bare `#12`, cross-repo `owner/repo#7`, closing keywords, multi-ref, no-match,
    closing-keyword-vs-plain dedup), `CommitContextProviderTests` (TI-32 — `GitHubCommitContextProvider`
    against `Fixtures/CommitContext/` through an injected `HttpMessageHandler`: commit→pulls
    one/several/none, linked issues parsed+deduped from PR bodies, error→typed,
    token-only-in-Authorization + redaction, stubs unsupported), `CommitContextServiceTests` (TI-32 —
    the `IsSupported` host/token matrix + GitHub dispatch/auth-header/typed-degradation over a fake
    keyring), `BlameCommitContextViewModelTests` (TI-32 VM gating — single PR routes, several reveal the
    chooser, none disables, PR/issue rows route the model, `BlameViewModel.ShowCommitContextAsync`
    unsupported→inert / supported→popover), `Headless/FileHistoryRenderHarness.cs` (T-12 file-history
    dialog — revision list + selected-vs-predecessor diff and the introducing-revision all-additions
    render against a fixture repo), `Headless/DiffQualityRenderHarness.cs` (T-13 intra-line emphasis +
    trailing-whitespace markers in the unified & side-by-side diff, and the ignore-whitespace mode
    hiding partial-staging actions), `Headless/AccountsSshRenderHarness.cs` (T-14 Accounts + SSH-keys
    preferences pages, asserting a non-empty rendered frame), `Headless/SigningBadgeRenderHarness.cs`
    (T-15 commit-timeline with the verified/untrusted/bad signature badges — statuses assigned directly
    for a deterministic frame), `Headless/CommandPaletteRenderHarness.cs` (T-18 command palette: the
    filtered state with highlighted match spans + category/gesture chips, the empty-query grouped browse
    state, and a key-input-driven typing capture — canned `PaletteEntry` set, no repo needed),
    `Headless/OperationHistoryRenderHarness.cs` (T-19 operation-history window: a realistic journal —
    undoable rows, an undone row with a Redo button, and a non-undoable flagged row — built through a
    real GitService+OperationJournal on a fixture repo), `Headless/PreCommitScannerRenderHarness.cs`
    (T-30 pre-commit findings panel: the blocker+warning+info grouped state with the "Commit anyway"
    banner, and the all-clear state — findings set directly via a canned VM, hosted in a themed window),
    `Headless/CommitComposerRenderHarness.cs` (T-31 conventional-commit composer: the filled structured
    composer — type/scope/description with the amber over-limit counter, body, breaking, co-author +
    issue chips, live preview, and a validation warning — via a canned `CommitComposerViewModel`, and
    the plain-mode staging composer with the mode toggle via a canned `StagingPanelViewModel`), and
    `Headless/ReflogRenderHarness.cs` (T-20 reflog viewer: a realistic HEAD reflog — commits, a branch
    checkout, and a hard reset — with the per-row Restore / Create-branch-here affordances, built
    against a fixture repo), `Headless/PullRequestsRenderHarness.cs` (T-23 Pull Requests panel: the
    populated open-PR list — incl. a draft badge and the per-PR merge-method picker + Merge/Close/Open
    affordances — served by a canned `FakePullRequestService`, plus the unsupported/not-connected empty
    state, the **T-25** review panel — verdict badges + inline comment threads (incl. an outdated
    comment) + the submit-review affordance, and the **T-29** check-out-locally state — the per-row
    "Check out locally" action + the "Checked out into …" banner with its Open-worktree button, via a
    canned checkout), `Headless/IssuesRenderHarness.cs` (T-24 Issues panel: the populated issue list —
    host-colored **label chips** with auto-contrast text, assignees, comment counts, per-issue
    Comment/Close/Open — served by a canned `FakeIssueService`, plus the unsupported/not-connected empty
    state), `Headless/ChecksRenderHarness.cs` (T-26 CI Checks panel: a mixed
    success/failure/pending/neutral run list with the failing overall badge, plus the unsupported
    state), `Headless/NotificationsRenderHarness.cs` (T-27 Notifications inbox: notifications across two
    repos with mixed reasons/subjects and read+unread rows — grouped by repo with reason chips,
    subject-kind icons, and unread dot+bold styling — via a canned `FakeNotificationService`, plus the
    unsupported/not-connected empty state), `Headless/ReleasesRenderHarness.cs` (T-28 Releases panel:
    the existing-release list (stable/prerelease/draft with Draft/Pre-release badges) with the
    New-release composer open and its body pre-filled from a generated changelog, via a canned
    `FakeReleaseService`, plus the unsupported/not-connected empty state),
    `Headless/ProfilesCloneRenderHarness.cs` (T-21: the Git Profiles window — rows with the signs chip +
    Apply/Edit/Delete — and the Clone Dashboard's clone-progress overlay at a mid-fill percentage, via
    canned services), `CloneDashboardRenderHarness.cs` (P2-48: the multi-provider Clone Dashboard — the
    GitHub/GitLab segmented selector + a populated repo grid — rendered with GitHub selected and GitLab
    selected across all five themes, over a canned `IHostRepositoryService`; PNGs to
    `artifacts_headless/`), and `Headless/AnalyticsRenderHarness.cs` (T-22: the full analytics view —
    language donut, weekly-churn time series, punch-card heatmap, contributor bars — against a
    multi-author/multi-week/multi-language fixture, captured in **both** MidnightLoom and DaylightLoom
    to prove light/dark legibility; pumps the dispatcher until `IsLoading` clears so LiveCharts has laid
    out before capture), and `Headless/DiffViewerFileRemovedRenderHarness.cs` (#82 regression: loads a
    file into the Code-Editor `TextEditor` (line numbers on), renders a frame, then simulates the file
    being renamed/removed on disk + the watcher-driven refresh clearing the selection
    (`UpdateDiff(null)`) — pumping + forcing a render and asserting the editor safely clears with no
    exception; headless can't reproduce the Win32 compositor race, so it locks in the safe-clear path)
    render against real fixture repos, saving PNGs to `artifacts_headless/` (gitignored) for visual
    review. `Headless/ApiKeySettingsRenderHarness` (P2-01: the AI Providers page — empty / valid
    "supports ~N agents" / invalid-error states — and the CLI-OAuth ToS dialog, rendered in all five
    themes with a fake health-check + temp-dir keyring, PNGs to `artifacts_headless/`). P2-01
    unit/VM/fixture tests: `ApiKeyHealthServiceTests` (offline health check via recorded HTTP-response
    fixtures under `Fixtures/ApiKeyHealth/` — per-provider request shape, rate-limit-header parsing, the
    monotonic ceiling table, 401 key-scrub, missing-headers floor,
    unreachable/unknown-provider/cancellation typed throws), `CredentialInjectorTests` (purity + newline
    rejection), `SecureKeyStoreTests` (`ISecureKeyStore` round-trip + backing-file removal),
    `ApiKeySettingsViewModelTests` (invalid-not-stored, valid-stored-and-nulled + re-check on re-save,
    delete, and the `TosAcknowledgment` cross-context persistence via in-memory SQLite). **P2-03
    terminal tests:**
  - `VtBoundaryDetectorTests` (the correctness heart — the split-at-every-offset corpus over CSI SGR /
    OSC 8 both terminators / DCS / SS3 / 2·3·4-byte UTF-8 / ZWJ emoji, reassembling byte-identically,
    plus incomplete-CSI/UTF-8/endless-escape holds), `PtySessionTests` (`LinuxOnly` forkpty probes: cat
    echo round-trip, isatty-true, Ctrl+C interrupt, kill, resize→winsize; a `WindowsOnly` ConPTY smoke),
    `TerminalScrollbackTests` (the pure `VtScreen`: 10k circular scrollback cap, grid readback of
    text/SGR colour/cursor/UTF-8), `TerminalViewModelTests` (VM forwarding of Ctrl+C→0x03,
    output→engine, debounced resize, plus the `TerminalControl.MapKey` VT-byte table), and
    `Headless/TerminalRenderHarness` (a coloured TUI frame through the interim engine captured in
    MidnightLoom + DaylightLoom, `terminal_frame_*.png`), and `Headless/BootstrapProgressRenderHarness`
    (P2-05 staged checklist — a running mix + a failed run — in all five themes,
    `bootstrap_progress_*.png`). P2-05 units: `WslConfigMergerTests` (the six `.wslconfig` fixtures
    under `Fixtures/WslConfig/` — empty / no-`[wsl2]` / existing-`[wsl2]` / user-keys-preserved /
    comments+unknown-sections / CRLF — plus idempotency, purity, and the `memory=` min(50% RAM,8GB)
    table) and `BootstrapStateMachineTests` (skip-satisfied→zero-acts, healthy-machine full no-op,
    resume-after-failure, typed failure names the step, WSL-not-installed-before-any-act, the G-12
    `Lifecycle_ShouldNeverEmitShutdown` + a `NoShutdownAnywhere_InCoreOrServer` source grep, UTF-16LE
    `--list` parsing, and `FirstBootStep_ShouldProvisionPtraceScope2` for G2 control (2); all seams
    mocked — no real `wsl.exe`).
  - `DaemonUpdaterTests` (the tier-1 daemon fast-path: the pure skew decision incl.
    Unimplemented-as-skew + build-metadata stripping, the `/mnt` payload-dir translation, the exact
    ordered in-distro refresh argument lists over a fake `IWslRunner` incl. the `/opt/mainguard.old`
    rollback swap and the already-renamed-apphost probe, failure recovery — copy-failure never touches
    the install dir, promote-failure restores the rollback, the unit is always restarted — the G-12
    no-`--shutdown`/distro-scoped builder proof, the `DaemonAutoRefresh` orchestration:
    unreachable-skip, boot-wait retry, missing/empty-payload skip, failed-refresh logged never thrown —
    and the typed `onOutcome` seam: each outcome kind reported with old/new versions,
    `DaemonRefreshToast.TryCompose` composes a toast for Refreshed/RefreshFailed only (everything
    quieter is toast-silent), `SandboxImageProvisionerTests` (v1 sandbox-image provisioning over a fake
    `IWslRunner`: probe parsing, exact distro-scoped `/mnt`-translated build argv + G-12 proof,
    serialized builds, per-image failure isolation, missing-sources skips, the auto-provision
    outcome/toast policy), `SandboxImageProvisioningTrackerTests` (the 2026-08-05 auto-provisioning fix:
    the tracker's in-flight state, a second request JOINING the run already going instead of starting a
    rival `docker build` of the same tag, a faulted run not wedging the gate — plus
    `SandboxImageMissingMessageTests`, which pins the spawn-preflight banner to only what was actually
    checked: no manual `docker build` fallback (the old one named an in-distro path that does not exist
    AND omitted `--label mainguard.image.version`, so an image built by it was re-rejected as stale
    immediately), no "restart Mainguard" advice (restarting CANCELS the in-flight build — the reason the
    images stayed stale through every attempt), and no promise of an "installed" notice a stale image
    never emits), `VmUpgradeOrchestratorTests` (the tier-2 in-place VM upgrade:
    `VmUpgradePolicy` older/equal/newer/garbage — installed-newer is NEVER offered a downgrade — the
    `MainguardOsReleaseStamp` parser, the new staging-scoped/tar-transport builders' exact argv,
    `VmUpgradeCheck` (daemon answer → in-distro `/etc/mainguardos-release` fallback → unknown = no
    offer), and `VmUpgradeOrchestrator` over a fake `IWslRunner` + fake host filesystem: the exact
    happy-path sequence incl. the VHDX move-before-unregister-staging, the invariant-3 ordering proof
    (migrate + validate strictly precede any `--terminate`/`--unregister MainguardEnv`),
    validation-miss/import failures returning `OldDistroIntact` with staging cleanup + daemon restart,
    the resilient promote (a bounded move retry that succeeds without the fallback, move-exhausted → the
    copy-then-cleanup path with the copy-strictly-before-unregister-staging ordering asserted,
    copy-verify-fails → stranded naming BOTH failures, copy-ok-but-import-fails → stranded pointing at
    the CANONICAL VHDX), promote failures after the retire returning the typed `StrandedAfterRetire`
    naming the surviving VHDX, the no-user-data skip, and per-plan-step progress reporting).
    `VmUpgradeOfferViewModelTests` (the consented tier-2 offer surface: seeded pending plan steps, Later
    → `Declined` + close without touching the orchestrator, Upgrade drives the checklist from progress
    lines, failure/stranded/throw all surfaced honestly, and the `LogSink` receives every progress line
    + the final typed result incl. the stranded VHDX path and promote strategy), and a throwing outcome
    callback is swallowed without corrupting the log).
  - `VersionsViewModelTests` (the File → Settings… About/versions surface over a fake `GetDaemonInfo`
    query seam: reachable daemon shows daemon+payload versions, unreachable → honest "unreachable" text
    without throwing, `Unimplemented`/null → "pre-0.2.0", empty payload stamp → "not stamped", Refresh
    re-queries so a restarted daemon shows its new version, and a concurrent refresh coalesces to one
    query). **`Fixtures/WslConfig/**` is pinned `-text` in `.gitattributes`** so the intentionally-CRLF
    fixture is never EOL-normalized. **P2-06 pure/VM units:**
  - `RepoPathHasherTests` (case/slash/trailing normalization → one hash, lowercase-hex SHA-256 shape,
    Unicode + Unix-temp-path stability) and `SyncRemoteRegistrarTests` (the App-side idempotent
    sync-remote registration over a real `GitService` + temp repo — run-twice→one-remote,
    changed-URL→updated, resolved-name-not-hardcoded). **P2-07 sandbox pure tests:**
  - `ContainerSpecBuilderTests` (every hardening flag on the create request + the G2 quartet — seccomp
    denies `process_vm_readv`/`process_vm_writev`/`ptrace`, `CapDrop ALL`/no `CAP_SYS_PTRACE`,
    supervisor-uid ≠ agent-uid 0400 tmpfs — the `/mnt/c`/`C:\`/UNC typed-rejection theory,
    no-secret-in-Env, and `ptrace_scope`-absent-from-request), `EgressSegmentationTests` (**MG-36,
    pure**: per-agent segment naming is distinct/stable/docker-safe; `IsDefaultDenyAgentNetwork` covers
    the shared network AND every segment but never the egress leg; **a jail on a segment with no
    resolver pin is still refused** — the MG-7 gate would have silently stopped applying when the
    topology gained a second network; the multi-address MG-18 backstop admits every proxy address, keeps
    every ACCEPT destination-constrained, and still terminates in DROP), `SandboxImageDigestTests`
    (**MG-27, pure** — see the jail-image note above), `EgressAllowlistTests` (defaults carry **no
    git-host entry**, add/remove round-trips + `allowlist_changed` audit events, a git-host entry
    flagged `DefeatsA6`, JSON persistence round-trip), `GatewayReachabilityPolicyTests` (**MG-4, pure:
    the rendered egress policy that lets a CONFINED jail reach the model gateway, and the two things it
    must not disturb. The gateway's own host must appear in the tinyproxy filter as a bare host (a
    `host:port` pattern can never match, the same silent-no-op class as a wrong base-URL variable);
    enabling confinement must add NO `upstream` directive, because an `upstream` is keyed on the
    destination host on the one proxy every agent shares and would drag OAuth traffic through the gateway
    to be 401'd — the highest-value assertion in the file; the provider hosts stay allowlisted; the
    no-gateway default renders byte-identical policy; and the gateway entry does not trip the A6
    warning**), `DeclaredDependencyResolverTests` (F5:
    `go.mod`/`package.json`/`package-lock.json` → the exact module set, subpath allow, out-of-scope
    typed denial), `DaemonGitProxyTests` (A6: allowlisted fetch succeeds + transparency line,
    non-allowlisted refused + audited, `git-receive-pack` push refused structurally + audited, and a
    reflection proof that no push/receive method exists), and `Headless/EgressAllowlistRenderHarness`
    (the egress allowlist `Window` in all five themes — default + the A6-warning state — PNGs to
    `artifacts_headless/`).
  - `TestTools/PlatformFacts.cs` supplies the `LinuxOnlyFact`/`WindowsOnlyFact` skip-with-reason
    attributes.
  - `NpmProvenanceTests.cs` + `BuildProvenanceTests.cs` (MG-9 build provenance — the npm signature
    scheme against a locally generated P-256 pair, the integrity→bytes binding, every fail-closed policy
    branch, the manifest's refusal of an adapter with no declared rung, the shipped-manifest rung guard,
    the `gh attestation verify --format json` parser against captured output, and the refusals at
    `ApplyUpdateAsync` / `ImportDistroStep` / `VmUpgradeOrchestrator` / `DaemonUpdater`;
    `RequiresNpmRegistryFactAttribute.cs` gates the ONE test that hits the real registry — it verifies
    npm's actual signature under the compiled-in key for all five shipped adapters and skips VISIBLY
    when offline, because an early `return` would report a green "Passed" while asserting nothing).
- **`Mainguard.Tests/Terminal/` + `Mainguard.Tests/Transcripts/`** — the **P2-04 VT conformance &
  replay harness**, since P2-18 parametrized over BOTH engines through `EngineCatalog.cs` (engine
  roster + per-engine allowlist/golden/input-encoder resolution; `MAINGUARD_REQUIRE_LIBVTERM=1` in CI
  turns a missing native library into a hard failure — the merge gate can never silently skip).
  - `RequiresLibvtermFact.cs` supplies `[RequiresLibvtermFact]` — the visible-skip attribute the
    libvterm-only tests carry instead of a silent early return.
  - `ITerminalEngineHarness.cs` (the "feed bytes → read grid" seam + `GridSnapshot` golden currency),
    `InterimEngineHarness.cs` (VtScreen adapter) and `LibvtermEngineHarness.cs` (the P2-18 twin over
    `VtermSession`), `VtConformanceTests.cs` (vttest-style cases ×2 engines + the libvterm-only
    Ink-repertoire cases — DECSTBM/DECOM/IL-DL/ICH-DCH/deferred-wrap/ED 2-3 — plus the allowlist-subset
    ≥-parity gate), `CoverageMatrixTests.cs` (7 areas ×2 engines; the grid client's `GridInputEncoder`
    closes the bracketed-paste + mouse rows), `TranscriptReplayTests.cs` (per-engine goldens:
    `name.golden` interim — never rewritten by P2-18 — beside `name.libvterm.golden`), the shrink-only
    allowlists `known-failures.txt` (interim) + `known-failures.libvterm.txt` (libvterm; osc8 only —
    must stay a subset of the interim list), `TerminalHarnessPaths.cs`, `GridBuilder.cs`,
    `TranscriptRecorder.cs`(+`RecordingEntryPoint`), `InterimInputEncoder.cs`. P2-18 client-model tests
    beside them: `GridModelTests.cs` (proto → cells, scroll/push/pop ops, packed runs, clipboard
    frames), `GridSelectionTests.cs` (the REQUIRED selection-copy contract: Ink run collapse,
    written-space preservation, wide-spacer skip, absolute-row survival), `GridInputEncoderTests.cs`,
    `VtermSessionTests.cs` (+`TerminalModeTrackerTests` — OSC 52 query-never rule daemon-side).
- **`Mainguard.Tests/BootStaleCascadeTests.cs`** (MG-29) — a merge replayed by the boot reconcile
  must fire the stale cascade on the queue that OWNS the agent. The daemon wired `onMerged` as
  `foreach (var handle in Array.Empty<string>())` — a hardcoded no-op, so a recovered merge never
  reached any queue and a co-tenant branch stayed `Verified` against a main that had already moved.
  - `MergeReconcileTask.onMerged` now carries the lease's repo hash so the owning queue is a direct
    lookup; the tests drive the REAL daemon callback body and assert the co-tenant is re-staled +
    un-mergeable, the merged agent lands `Merged`, another repo's queue is untouched, and an
    unregistered repo is a safe no-op.
- **`Mainguard.Server.Tests/GridPipelineTests.cs`** (P2-18) — the deterministic
  engine→wire→client-mirror proof: htop-transcript snapshot/attach identity (cell-by-cell),
  chunked-delta streaming mirror, the 1000-line steady-scroll traffic invariant (scroll ops, bounded
  damage, measured byte budget printed for PR evidence), and resize ring-consistency.
  - **`BoundGridSessionTests.cs`** — the same through the REAL `BoundTerminalSession` pump: atomic
    snapshot+deltas mirror a scripted CLI, OSC 52 clipboard frames (queries never), raw subscribers +
    `TailText` intact alongside the engine, resize → PTY+vterm+snapshot, `GetScrollback`.
- **`Mainguard.Server.Tests/CliLoginHarvestWiringTests.cs`** — the **caller** of the CLI-login
  harvest, which is what was missing: `DaemonBackedOrchestrator.PersistLiveAgentLoginsAsync` had no
  callers anywhere in the repo, so only an explicit in-app Stop ever wrote a `cli_login_*` keychain
  entry and every other teardown (app close, daemon/VM restart, crash) lost the login. Both new legs
  are asserted through the SHIPPED orchestrator against the real in-proc daemon and the real
  `HarvestAgentCredentials` RPC, and **neither test calls `PersistLiveAgentLoginsAsync` itself** — that
  would recreate the exact blind spot. `StartedOrchestrator_SweepsALiveAgentsLogin_IntoTheHostKeychain`
  drives the periodic pump (200 ms interval) and asserts the harvested bytes land in the injected
  keychain; `Dispose_HarvestsOneLastTime_SoClosingTheAppKeepsTheLogin` sets a 30-minute interval so the
  vault can only be written by the shutdown sweep. The rig is Docker-free: a fake substrate whose
  sandbox engine answers the daemon's OWN harvest exec (`sh -c '[ -f "$1" ] && base64 "$1"'`) with the
  login bytes, and only for the path the temp install marker declares. The real-jail leg is
  `Agents/CliLoginRoundTripDockerTests.cs`.
- **`Mainguard.Server.Tests/MergeExecutionPathTests.cs`** — the GUI Merge button actually merges,
  end to end through the real composition (in-proc daemon + shipped `DaemonClient` + shipped
  `DaemonBackedOrchestrator` + a real git repo on disk). **Asserts repository state, never RPC
  success** — "ConfirmMerge returned Confirmed" is exactly what the broken path already produced, so
  every case is a `rev-parse main`: the merge moves main to the agent tip and records THAT sha; and
  each refusal (main moved underneath, not a fast-forward, dirty working tree, lease held elsewhere,
  gate refuses, no local checkout bound) leaves main, the branch's queue state and the co-tenant's
  `Verified` entry untouched with the lease handed back — the stale cascade cannot fire on a merge
  that did not happen.
- **`Mainguard.Server.Tests/ExternalPrMergePathTests.cs`** — the same composition for a P2-12
  `External` entry, plus a **real bare "upstream" repository** the fake `IHostPullRequestGateway`
  performs real git merges in (no live GitHub — that stays in the manual matrix). The guard test is
  `HostReportedAMergeThatIsNotOnTheBaseBranch_RecordsNothing`: the host's merge call returns success
  naming a plausible real commit (the PR's own head) while nothing was merged upstream, and the entry
  must **not** reach terminal `Merged` — "the API returned success" is exactly the vacuous-assertion
  shape. Also: the happy path (upstream merges, local main converges onto that commit and contains the
  PR, the daemon records THAT sha); each upstream refusal (already merged / closed unmerged / conflict
  / blocked by required checks / head moved since verification) with the merge never attempted and the
  host's main untouched; each transport failure (permission, unreachable, 409 head-CAS, 405
  not-mergeable); the MG-11 gate and MG-23 lease refusing before the host is touched at all; no
  double-merge after a confirmed one; and local preconditions (main moved, dirty tree) refusing
  **before** the irreversible upstream merge.
- **`Mainguard.Server.Tests/Agents/MergeQueueEndToEndDockerTests.cs`** (`RequiresDocker`) — **the
  merge queue driven as ONE loop instead of as a pile of parts.** Every other merge-queue test
  substitutes something load-bearing (a `runVerification` lambda that returns `Passed:true`, a
  `main@sha` of `"sha0"`, a hand-registered queue), and none of them can answer the question the
  product rests on: *does an agent's branch get verified in that agent's own jail and then land on the
  user's real checkout with `main` at the commit it should be?* Nothing is substituted here: the
  daemon is the real composition root over an isolated VM root, the queue is the one
  `MergeQueueProvisioner` builds on `ProvisionRepo`, the jail comes from the shipped
  `SandboxAgentLauncher` chain via the `SpawnAgent` RPC, the verification command is read out of git
  from `.mainguard/verify` and run by a real `docker exec`, and the merge is driven by the shipped
  `DaemonBackedOrchestrator` — the exact adapter the Merge button runs on — against a real git
  repository on disk. **Every merge assertion is a ref assertion** (`refs/heads/main` is at the branch
  tip, or is exactly where it started): "the RPC returned success" is precisely what the pre-#261 code
  did while running no git at all. The fixture repo is a tiny **Node** project (`.mainguard/verify` =
  `node .mainguard/verify.js`, a single argv line — no shell, no `&&`) because the jail carries
  `nodejs_22` and the suite can therefore genuinely FAIL, which is what makes the pass assertions
  non-vacuous; the matching negative control merges nothing and leaves `main` untouched. Covers:
  branch → verify → queue → merge; the RT-D2 flagged-change gate (a branch that rewrites its own
  verify command verifies GREEN and is still unmergeable until the item is acknowledged **per item** —
  acknowledging a different id does not clear it); the stale cascade (a co-tenant is BLOCKED —
  `CanMerge` false, `BeginMerge` refused, the Merge button refused, `main` unmoved — through a window
  made deterministic by an *untracked* dwell file, so the verify command stays byte-identical to
  main's and the RT-D2 gate stays silent); an `External` entry merging on its host with the checkout
  reconciled onto that commit; and the daemon-restart resume. **The external legs are now driven by
  the REAL `ExternalPrIntake` over the daemon's own `IPrWorkerHost`** (one `PollOnceAsync`; only the
  PR *list* and the fetch *URL* are fixture-local, and the source→repo resolution has its own tier —
  see `ExternalPrIntakeSpawnWiringTests`), which replaced
  `ExternalPullRequestEntry_WithNoJail_CannotVerify_BecauseTheIntakeSpawnsNone` — a test that asserted
  the defect positively and whose own doc said to invert it the day the intake was wired. In its
  place: the intake spawns the `pr-<n>` jail, the entry verifies IN it and merges (asserting the
  session id/role/kind, the same jail image as a co-tenant local agent — hence the same MG-42
  toolchain layer — and a SEPARATE MG-43 package cache keyed by `pr-<n>`); the failing-suite control
  that makes "it verified" mean something; and the MG-2 cap-refusal control, where a full
  managed-worker pool leaves the PR with no jail, no worktree and no queue entry, and the same PR
  materializes normally once a slot frees.
- **`build/libvterm/`** — `build.sh` (the pinned libvterm 0.3.3 source build — URL + sha256 constants, direct `cc` compile, output `out/libvterm.so` consumed by CI tests and bundled into the daemon publish by `Mainguard.Server.csproj`; daemon-side only, never the client) + `README.md`.
- **`.github/workflows/ci.yml`** — CI. The `build-and-test` job builds the pinned libvterm before
  testing and exports `MAINGUARD_LIBVTERM`/`MAINGUARD_REQUIRE_LIBVTERM` so the P2-04 suites gate the
  P2-18 engine; the allowlist shrink-guard covers both per-engine known-failures files. Its
  `payload-reproducible` job now also runs a **daemon-startup smoke in the real payload image as the
  real service identity** (uid 1000, `HOME=/home/mainguard`, CWD `/`): mainguardd must stay alive,
  actually listen on 127.0.0.1:5250, and have created an ABSOLUTE data root under `$HOME`. Both
  shipped crash-loop bugs (missing libicu; `GetFolderPath`→relative path) survived only because
  mainguardd had never once been STARTED in its shipping rootfs before a user did it — this asserts
  behaviour, not an exit code. Its `client-closure` job (ADR-0001 payoff, automated) publishes the
  Client head and fails if the closure names any agent-platform assembly (`Mainguard.Agents(.UI)` /
  `Mainguard.Protos` / `Docker.DotNet` / `Porta.Pty` / `Grpc`), via
  `build/ci/verify-client-closure.sh` (also runnable locally).
  - **`.github/workflows/deploy-site.yml`** — builds `site/` and deploys it to GitHub Pages on pushes to
    `main` touching `site/**` (or manual dispatch). **`Dockerfile` / `docker-compose.yml` /
    `.dockerignore`** — container build.
  - **`global.json`** — SDK pin.
  - **`Directory.Build.props`** — repo-wide MSBuild properties: `RestorePackagesWithLockFile` (MG-35
    lockfile pinning) and **`$(MainguardDotnetHost)` — the dotnet host every `<Exec>` in this repo must
    invoke, never a bare `dotnet`**. An `<Exec>` runs its command in a CHILD process, so a bare `dotnet`
    resolves from that child's PATH and launching the build by absolute path does not help: Pro.App's
    two publish targets died with `MSB3073 … exited with code 127` on any machine whose SDK sits outside
    the default location (`~/.dotnet`, a self-contained CI toolchain). The property takes
    `$(DOTNET_HOST_PATH)` — the absolute path of the host running the current build, so a child publish
    is guaranteed to be the SAME SDK global.json resolved — and falls back to the bare name only if a
    non-SDK host leaves it empty. Quote the expansion at every call site
    (`C:\Program Files\dotnet\dotnet.exe`).
  - **`.config/dotnet-tools.json`** — local tools (`dotnet-ef`).
  - **`.mainguard/`** — **this repository's own merge-queue verification contract** (MG-42), read out of
    git by the daemon for BOTH the main baseline and an agent's branch: **`.mainguard/verify`** =
    `dotnet test Mainguard.slnx --configuration Release` — the P2-10 §3.2 verification command, a single
    **argv-style line tokenized without a shell** (so `&&` chaining does not work and a single command
    has to carry the whole signal). It does: `dotnet test` on the solution builds the solution first,
    and it builds **every** project — verified by planting a syntax error in `Mainguard.Client.App`,
    which no test project references, and watching the command fail — so one command really is
    build+test. **`.mainguard/toolchain`** = `dotnet-10`, because the curated agent base image ships
    jq/rg/fd/tree/make/node/python3/go and **no .NET**, so without a declared toolchain Mainguard could
    not verify its own repository at all.

## Role in the solution

- **`Mainguard.Tests`** — xUnit tests for Core + App + the client-side daemon pieces; hosts the
  `ScriptedAgentHarness` tool project under `TestTools/ScriptedAgent/`, and
  **`TestTools/DaemonTransportMaterial.cs` (MG-19 — mints a valid client-side transport-credential
  pair on disk so `DaemonClient.ForLoopback` tests can get past the credential gate and keep asserting
  what they are really about; it duplicates a few lines of the server's `SessionTransportCertificates`
  deliberately, because this client-side tier must not reference the server assembly — the same reason
  `RequiresLibvtermFact` exists twice)** and **`TestTools/TestPorts.cs` + `DeadPortAllocationTests`
  (the last two racy `FreePort()` copies — `DaemonStreamTests` and `DaemonAuthTests` — retired.
  Deliberately NOT a copy of `Mainguard.Server.Tests`' `TestPorts`: that one exists because its
  callers BIND the port they are handed, so it answers with in-process exclusivity plus a bind retry.
  **Nothing in this assembly binds a leased port** — all four call sites want the opposite property, a
  port where nothing is listening, and two never open a socket at all — so dedup buys them nothing and
  the mechanism is the INVERSE hazard #263 left open: releasing the socket and returning the bare
  number made "nothing is listening here" a hope, and a foreign process taking the port turns a test
  that expects a connection to FAIL into one that gets a connection. `LeaseDeadPort()` VERIFIES the
  port is refusing connections (only `ConnectionRefused` counts as dead; a connect that never answers
  is discarded, since the cost of one more probe beats handing out a live port), and
  `OnDeadPortAsync()` tolerates the residue the check cannot remove by re-running the body on a fresh
  port — but ONLY when the port really did stop being dead, because a retry that swallowed genuine
  failures would trade a rare wrong failure for a permanent blind spot. `DeadPortAllocationTests`
  drives both through seams so the branch is genuinely taken (the trap #263's own concurrency test
  fell into, where the kernel never repeated and the assertion passed with the fix disabled): the
  probe is pinned in BOTH directions, an occupied candidate is injected and must be rejected, the
  search is bounded, a port is stolen mid-body on cue and must be retried on a different one, and the
  control proves a failure on a still-dead port surfaces on the first attempt)**. **P2-13
  activity-bar/docking tests:** `AttentionDerivationTests`, `ActivityBarOrderingTests`,
  `NotificationSuppressionTests`, `DockLayoutPersistenceTests` (pure),
  **`ControlCenterLiveWiringTests` (P2-47 integration proof #2 — the "no mock AND no empty-stub"
  guard: the shipped `App.CreateProductionOrchestratorServices` bundle exposes the real
  `DaemonBackedOrchestrator` behind EVERY seam — agents/queue/coordinator/kill/telemetry — and never a
  `MockOrchestrator`; the behavioral no-empty-stub proof is `AlphaControlCenterProjectionTests` in
  Server.Tests)**, **`CoordinatorConversationTests` (P2-47 #9 — the coordinator bridge is real:
  `SendAsync` drives a real `CoordinatorAgent` and projects its reply turn; no engine → honest system
  turn, never fabricated)**, **`DaemonStateMappingTests` (the daemon wire-state →
  `AgentLifecycleState` vocabulary: "Stopped"→TornDown, "Starting"→Provisioning — guards the
  2026-07-22 ghost-coordinator bug where "Stopped" fell into the Working default and a torn-down
  coordinator projected as alive forever, wedging the startup loader and making Stop look like a
  no-op)**, **`TerminalClipboardTests` (terminal ↔ host clipboard: OSC 52 decode/raise incl.
  query-never-answered + split-feed, DECSET 2004 tracking, paste-byte building + the three paste
  chords)**, plus the headless `Headless/AgentStatusBrushTests` (every `AgentStatus`→token in all five
  themes), `Headless/DockTeardownMemoryTests` (the blocking 50× open/close heap-stability +
  zero-floating-windows harness via the reused-host content-swap path),
  `Headless/ResourceMonitorStreamTests`, `Headless/ResourceMonitorHonestyTests` (**the two honesty
  properties of the Resources tab**: an unmeasured reading renders "—" while a measured zero still
  renders "0%" — the tab previously hard-coded 0 for everything, which is indistinguishable from an
  idle fleet — and the cost UI appears only where spend is actually metered, with the unmetered row
  refusing to draw `$0.00` even on a mixed fleet), `Headless/ResourceMonitorRenderHarness` (the tab in
  all five themes × BYOK / OAuth / failed-sample / no-agents → `resources_*.png` in
  `artifacts_headless/`; the VM truths are asserted beside each capture so a blank surface cannot pass
  as green), and the `Headless/ActivityBarRenderHarness` (the five-theme
  rail PNGs + the Flight/Conversation dock workspace PNGs → `artifacts_headless/`). **`Terminal/`** is
  the P2-04 VT conformance & replay harness: `ITerminalEngineHarness.cs` (the engine-agnostic "feed
  bytes → read grid" seam + `GridSnapshot`/`GridCell`/`CellColor`/`CellAttrs` with a deterministic
  golden serializer and cell-by-cell diff), `InterimEngineHarness.cs` (adapts the P2-03 `VtScreen`
  directly — no Avalonia/renderer coupling — mapping its grid to `GridSnapshot`, filling
  width=1/alt=false/link=null/bold-only), `GridBuilder.cs` (fluent expected-grid builder),
  `CoverageMatrixTests.cs` (the 7 required areas — alt-screen, DEC 2026 sync, truecolor, CJK/emoji
  width, bracketed paste, mouse, OSC 8), `VtConformanceTests.cs` (vttest-style byte-script pages),
  `InterimInputEncoder.cs` (honest reflection of the interim input path's paste/mouse gaps),
  `TranscriptReplayTests.cs` (byte-order-only replay + seeded-random chunked-feed determinism through
  `VtBoundaryDetector` + `MAINGUARD_REGEN_GOLDENS` regen), `TranscriptRecorder.cs` +
  `TranscriptRecordingEntryPoint.cs` (PTY capture dev tool, gated on `MAINGUARD_RECORD_TRANSCRIPTS`),
  `TerminalHarnessPaths.cs` (source-tree fixture paths + allowlist loader), and `known-failures.txt`
  (the shrink-only expected-fail allowlist; CI enforces no additions vs phase2). Committed transcript
  fixtures live under **`Transcripts/`** (`*.bytes` raw output + `*.golden` cell serialization;
  `vim`/`htop-60s`/`tmux` representative, `claude-code`/`opencode` synthetic — see
  `Transcripts/README.md`). **P2-08 gateway tests (Core):** `TokenBucketTests`
  (refill-never-exceeds-capacity / grants-≤-refill / estimate-actual conservation / FIFO fairness on a
  virtual clock), `BackoffTests` (Retry-After-as-floor exponential), `BudgetLedgerTests` (caps +
  typed-pause-not-kill + `budget_exceeded` audit + snapshot + price table), `AdmissionControllerTests`
  (86% → reject with honest reason, cache TTL), `SwarmReconcilerTests` (dead-prune/orphan-adopt/stop +
  Docker-as-truth + RT-D1 ordering), and the shared `GatewayTestDoubles` (`FakeAgentSupervisor`).
  **P2-09 pure tests (Core):** `GitMutationGuardTests` (mid-rebase/detached/merge verdicts + the
  `index.lock` backoff-then-typed-failure + the no-active-token refusal), `YieldProtocolTests`
  (ready-path round-trip with no pause + the timeout `docker pause`-then-resume-unpause path, via
  fakes), `LeaderReattachTests` (the `LeaderRegistry` round-trip + the boot reattach reconcile reaping
  dead-container sessions). **P2-12 pure tests (Core):** `ExternalPrIntakeTests` (fixture-driven
  through the T-23 provider seam + the **worker-host**/fetch seams — materialize only bot PRs,
  idempotent double-subscribe/same-PR, force-push invalidation+re-queue, closed-PR cancel+**release
  the whole worker** (not just the worktree — the jail's MG-36 segment is what actually leaks),
  rate-limit backoff-no-crash-loop, zero-upstream-writes, the configurable author-filter `[Theory]`,
  and the spawn seam: the intake asks for a `pr-<n>` jail before anything else, a **gate refusal
  materializes NOTHING** and is retried on a later poll, a spawn failure likewise, repeat polls never
  spawn a second jail, and a non-rate-limit transport fault no longer escapes the poll) and
  `MergeDispatchTests` (the origin-routed merge step — local→foreground service, external→host merge
  API, both fire `NotifyMainMoved`). **P2-14 governance tests (Core):** `TaskPlanSchemaTests` (the
  schema corpus — valid + every invalid shape → exact error sets, unknown-field rejection, oversized
  guard), `PlanApprovalTests` (reject→no spawn/no worktree residue; approve persists the
  daemon-derived identity + survives restart; the S-8 pending-cap → `ResourceExhausted` +
  `plan_draft_rejected` audit + pressure signal), `CoordinatorToolCapTests` (`SpawnWorker` capped by
  admission/budget/worker-cap → rejected-without-drafting, the two-phase never-spawn-directly path,
  S-8 exhaustion, frozen-refusal, and the manual-mode-bypasses-coordinator-but-not-admission rule),
  `KillSwitchTests` (`FanOutUnder5s`+snapshot+frozen; the **RT-D4 `HardCeiling_IndependentOfRtt`**
  clamp with the A3 spike; the **SA-1/F4 `FreezesQueueBeforeFanOut`** timeline; the **RT-D3
  audit-outage→recovery `killswitch_audit_gap`**), and `Integration/ScriptedCoordinatorEndToEndTests`
  (the scripted-coordinator
  two-tasks→two-plans→approvals→parallel-workers→verify→sequential-merge-with-stale-reverify story
  asserted through the audit trail; the real-container leg is `MergeQueueDockerTests`). **MG-42
  per-repo toolchain (Core):** `ToolchainDeclarationTests` (the `.mainguard/toolchain` format + the
  closed-catalog refusals — a line that is not a bare id (`dotnet-10 && curl … | sh`, a URL,
  `../../etc/passwd`, `$(id)`) is rejected at parse time and an uncatalogued id is a typed
  `UnknownToolchainException`, never a silent skip; the resolution rules mirrored from
  `VerificationCommandResolver` incl. **main-is-what-gets-provisioned**, a hostile branch declaration
  that flags WITHOUT throwing (an exception on the verification path would let one branch wedge the
  whole repo's queue) and specifically does not normalise into equality with main; the generated
  Dockerfile's digest-pinned `FROM`, its absent `COPY`/`ADD`, and its return to `USER agent`; the
  content-addressed tag's sensitivity to base digest and declaration but not to comments; and the
  provisioner's cache-hit/poisoned-tag/failed-build/lying-build paths), `ToolchainProvisionerProgressTests`
  (the user-facing progress line: reported BEFORE the build rather than after — asserted by snapshotting
  the builder's call count at report time, not by statement order — the ready line after, nothing at all
  on a cache hit, and a null sink changing nothing; this is the daemon end of the channel that turns a
  multi-minute first-run image build into visible progress instead of a hang) plus, in
  `MergeQueueProvisionerTests`, the drift proof over a REAL bare mirror and REAL agent branch — one
  claim per test on purpose, since xUnit stops at the first failing assertion and a five-assertion
  test measures only the first: `ABranchsToolchain_IsNeverTheOneProvisioned` (main declares
  `dotnet-10`, the branch demands `rust-stable`, and the observable is which presence probe the daemon
  ran in the jail), the flag/block/reason/acknowledge quartet, comment-edits-are-not-drift,
  both-files-drifted-names-both,
  `AgentThatMovedItsWorkOffItsOwnBranch_IsRefusedWithTheMeasurement_NotVerifiedSilently` (the decisive
  proof for the stranded-branch defect — the agent is moved off `agent/<id>` through LibGit2Sharp, which
  never runs hooks, so the daemon-side backstop cannot pass merely because the in-jail guard rail stopped
  the agent first; asserts the refusal names both branches and the recovery, that NOTHING was executed in
  the jail, and that the queue returns to `Working`. Asserting "the mirror's ref did not move" would have
  proven nothing — that is true with or without the fix), and the typed `ToolchainProvisioningException` when the jail does not
  actually carry what main declared (asserting the verify command was never launched — a provisioning
  failure is not a test result). **P2-11 flagged-change gate wiring** lives in the same class because
  the detector was never broken and the spine was, so only wiring-level tests over the real mirror can
  see it: `BranchThatPoisonsPackageJson_…` (a GREEN, Verified branch blocked by a postinstall until the
  item is acked), `BranchOutsideItsApprovedScope_…` (SA-1/F6 — a *benign* `Source` edit that is
  flag-worthy for one reason only, the approved `TaskPlan.Scope`) with
  `BranchInsideItsApprovedScope_IsNotFlagged` as its paired negative control, `ANewPayloadOnTheBranch_…`
  (a new payload + the stale cascade's re-verify produces a new content hash and drops the ack), and
  `AGreenBranchWhoseReviewCouldNotRun_IsDenied` (fail-closed: an otherwise perfectly mergeable branch
  whose diff could not be classified is denied, and the verification result is untouched). **VM lifetime:** `VmKeepAliveTests` (the MainguardEnv keep-alive
  holder — distro-scoped argv with no lifecycle verbs (G-12), restart-on-exit with capped backoff,
  start failures swallowed and retried, Dispose cancels a live holder session promptly). **Daemon
  fast-path:** `DaemonUpdaterTests` (the tier-1 skew decision + `/mnt` translation + the exact
  in-distro refresh command sequence with rollback/recovery over a fake `IWslRunner`, the G-12 builder
  proof, the `DaemonAutoRefresh` startup orchestration — unreachable/missing-payload skips, failures
  logged never thrown — and the typed-outcome seam + toast policy: only Refreshed/RefreshFailed
  compose a toast, a throwing callback never ripples back), `SandboxImageProvisionerTests` (v1
  sandbox-image provisioning over a fake `IWslRunner` — never real docker: probe parsing, the exact
  `/mnt`-translated distro-scoped build argv + the G-12 builder proof, serialized never-concurrent
  builds, per-image failure isolation carrying the docker error tail, missing-bundled-sources skips
  naming the path, and the `SandboxImageAutoProvision` outcomes + toast policy),
  `VersionsViewModelTests` (the Settings About/versions surface over a fake `GetDaemonInfo` seam:
  reachable, unreachable, pre-RPC, unstamped payload, refresh-again, coalesced concurrent refresh).
  `VmUpgradeOrchestratorTests` (the tier-2 in-place VM upgrade: the pure offer policy — proper version
  compare, equal/newer/garbage never offer — the `mainguardos-release` stamp parser, the new builders'
  exact argv, the `VmUpgradeCheck` detection flow incl. the daemon-down in-distro stamp fallback, and
  the orchestrator over a fake `IWslRunner`: exact happy-path order, migrate+validate strictly precede
  any old-distro mutation, failure-before-retire leaves the old distro untouched with staging cleaned
  up + the daemon restarted, promote/move failure after the retire surfaces the typed stranded error
  naming the VHDX). `VmUpgradeOfferViewModelTests` (the consented offer surface over a fake
  orchestrator: Later declines + closes without running, the run drives the plan-step checklist,
  stranded failures surface the VHDX path — never a fake success). **Edition seam:**
  `EditionReferenceGraphTests` (ADR-0001 Decision 6 reference-graph gate via NetArchTest.Rules — App
  has no ref to Server (G-18), Core none to App, App none to Docker.DotNet/Porta.Pty, with App→Core
  and App→Grpc.Net.Client+Mainguard.Protos positive controls; the interim gate before the Phase-2
  `.deps.json` client-closure check). **1e — multi-assembly ViewLocator:**
  `ViewLocatorCrossAssemblyTests` (a probe `ProbeViewModel`/`ProbeView` pair living in the TEST
  assembly proves the cross-assembly locator single-project — with only the shell registered the probe
  hits the Not-Found placeholder, and registering the probe's own assembly resolves it to `ProbeView`;
  `ViewLocator.ViewAssemblies` is restored in a `finally` so the shared headless session's shell-only
  default is untouched). **1f — edition test/harness safety net:**
  `Headless/MainWindowEditionRenderHarness` (the twin full-shell render guard — renders the whole
  `MainWindow`, top nav + rail + open workspace, under BOTH editions →
  `artifacts_headless/mainwindow_pro.png` + `mainwindow_client.png`; the "did the Client grow a kill
  switch / did Pro lose the Coordinator" screenshot guard, asserting Pro composes a control center and
  Client composes none), `EditionShapeTests` (the structural shape guard — constructs the real shell
  under each manifest: the Client test is the consolidated no-Pro-under-Client guard — rail is exactly
  Repo + the four host tabs, `ShowsAgentRail`/`HasAgentPlatform` false, `ControlCenter` + `ProTools`
  null ⇒ zero `DaemonBackedOrchestrator`/`DaemonClient` composed; the Pro test asserts
  Coordinator/Resources present, both gates true, a control center built behind the injected mock
  seam, `ProTools` present), and `EditionManifestCompletenessTests` (every rail section whose
  `ContentViewModelType` is non-null resolves to a real View through the manifest's `ViewAssemblies` —
  never the Not-Found `TextBlock`; kept non-vacuous by the four host-tab sections now carrying their
  VM type in both manifests, with Repo/Coordinator/Resources deliberately null as direct-panel content
  until Phase 2). **T-17/TI-15 skip hygiene:** `RequiresGitLfsFact.cs` (`RequiresGitLfsFactAttribute`
  sets a genuine `FactAttribute.Skip` — replacing the v3-only `SkipException.ForSkip` dynamic-skip
  marker that xUnit 2.9.3's v2 core reported as a Failed test with the raw sentinel in the message —
  backed by the fixture-free `GitLfsAvailability` static probe, which runs the identical
  `git lfs version` check `LfsService.IsAvailable` uses, cached once per run) and
  `RequiresUnixFileModesFact.cs` (`RequiresUnixFileModesFactAttribute` — same discovery-time `Skip`
  pattern; gates the MG-17 group-share test, which asserts real POSIX modes on a real temp tree and
  would otherwise be an early-`return` silent skip on Windows), `UsernsRemapTests.cs` (**MG-17** — the
  mapping arithmetic, the daemon.json (parsed as JSON, and cross-checked against the MainguardOS
  Dockerfile so a fresh import and an upgraded VM cannot run different isolation postures), the framed
  boot probe incl. the "observed nothing" and "remapped to the WRONG base" cases, the builder's
  refusal of `UsernsMode=host`, and the real-filesystem group/setgid grant) and `RequiresGpgFact.cs`
  (`RequiresGpgFactAttribute` + `GpgAvailability`, which stands up one throwaway `GpgTestEnvironment`
  — the same locate-binary-then-generate-a-key probe the signing tests themselves use — to decide
  availability once, fixture-free) gate every environment-dependent test in `GitServiceLfsTests` /
  `GitServiceSigningTests` respectively, mirroring `Terminal/RequiresLibvtermFact.cs`.
  **`SandboxSecretWriteTimeoutTests.cs`** — the spawn path's secret delivery is TIME-BOUNDED: it
  drives the real `DockerSandboxEngine.WriteSecretFileAsync` against a fake `IDockerClient` whose exec
  create never completes and never observes its cancellation token (the shape of a Docker endpoint
  that accepts an attach and delivers nothing — Docker Desktop's WSL2 socket proxy does this to exec
  stdin), and asserts the typed `SandboxExecTimeoutException`, its what/which-container/how-long
  diagnostic, the in-jail unlink of the half-written secret, and that caller cancellation still reads
  as cancellation. Each test carries its OWN outer wait so an unbounded implementation fails in
  seconds rather than hanging the run. (The wedge is now injected through the `IExecStdinTransport`
  seam rather than a fake `IDockerClient`, because the stdin no longer goes through Docker.DotNet at
  all — see `ExecStdinTransport.cs`; it also asserts G-13 directly: the secret arrives as `Stdin` and
  never appears in argv.) **`DockerStdinRegressionGuardTests.cs`** — the source-level guard that no
  shipped project may set `AttachStdin = true` on Docker.DotNet's `ContainerExecCreateParameters`
  (that library cannot deliver exec stdin against a modern engine). Anchored against every way a
  source scan lies: it fails if the repo root is not found, if it read implausibly few files, if the
  known Docker files are missing from the scan, or if the SAME matcher cannot find the sibling
  `AttachStdout = true` that production really does use; a second test re-runs the matcher over the
  real sources plus one synthetic offender and requires exactly that one hit.
- **`Mainguard.Server.Tests`** (P2-02 / TI-P2-00) — the daemon in-proc test tier
  (`WebApplicationFactory<Program>` + `Grpc.Net.Client`). Home of the shared Phase-2 fixtures under
  `Fixtures/`: `DaemonFixture` (in-proc host + authenticated `GrpcChannel`, session token, wrong-token
  channel factory, log-capture sink, and a **pinned roomy memory sample** so the tier never gates a
  shim spawn on the box's own `/proc/meminfo` — that uncontrolled input was the MG-37
  `ShimList_IsScopedToTheCallersOwnWorkers` flake; a test that wants pressure still overrides it —
  every daemon in-proc test uses it), `FakeModelEndpoint` (scripted model-API responses),
  `DualRepoFixture` (Windows-side repo + ext4-style bare mirror + `CaptureRefState()`), `AuditProbe`
  (`IAuditLog` sequence assertions), **`PinnedDaemonChannel` (MG-19 — builds channels to a REAL
  Kestrel daemon over the mutually-authenticated transport, plus the deliberately-broken variants: no
  client certificate, foreign client certificate, plaintext h2c, pinning-disabled; the in-proc
  `DaemonFixture` tier CANNOT exercise any of this because `WebApplicationFactory` swaps Kestrel for a
  `TestServer`, so the TLS layer never runs there)**, **`TestPorts` + `TestDaemonHost` (the ONE
  loopback-port allocator and the ONE place a real Kestrel daemon is started — they replaced four
  duplicated `FreePort()` helpers whose `Start()`/`Stop()`/return-the-number shape was a TOCTOU: the
  same `phase2` merge commit produced a green run and an `AddressInUseException` one. `TestPorts`
  never re-issues a port this process already handed out (rejected candidates are held open so the
  kernel moves on) and `LeaseBoundListener()` hands back an already-bound socket for the one test that
  wants a port OCCUPIED; `TestDaemonHost.StartAsync` leases the port, retries the bind on a fresh one
  — bounded, and ONLY on address-in-use, so "fails loudly" assertions still surface first try — and
  returns a `RunningDaemon` exposing the port it actually bound, its token, its URLs and its
  `StartAttempts`. Neither mechanism suffices alone: the allocator cannot stop a foreign process
  taking the port, and a retry alone would still let two of our own hosts collide)**,
  `TestPortAllocationTests` (the fixtures' own deterministic proof — both mechanisms driven through
  injectable seams, since a race cannot be failed on demand). Test classes: `DaemonAuthTests` (auth
  coverage incl. the reflect-every-method `[Theory]` + loopback bind),
  **`DaemonTransportSecurityTests` (MG-19 — every test holds a VALID bearer token, so each one proves
  the token is no longer sufficient on its own: plaintext h2c refused, no-client-certificate refused,
  unpinned/stale client certificate refused, the client's server-pin refuses an impostor, credential
  files are 0600, and a missing credential throws instead of downgrading; plus a pinned-mTLS positive
  control that keeps the refusals honest)**, `TerminalStreamRpcTests` (bidi echo — the no-PTY-bound
  fallback), `TerminalStreamerTests` (TI-P2-03: the streamer's batch-as-one-frame, 4 KB holdback-cap
  flush, never-split-across-frames, and the `Slow` 100 MB firehose memory-flat proof, plus the
  `RunAsync` stream pump), `TerminalPtyAttachTests` (TI-P2-03: `Attach` wired to a real PTY through
  the streamer — `/bin/cat` echo round-trip `LinuxOnly`, a ConPTY probe `WindowsOnly` — via a
  `TerminalSessionManager` override), **`AgentCliWiringTests` (PR3: the spawn→PTY→attach walking
  skeleton over a fake substrate + fake `ITerminalSession`s — spawn with an installed-CLI marker binds
  a long-lived session, Attach streams the REAL CLI (replay-then-live, detach never kills it,
  StopAgent does), an unprovisioned handle stays session-only+echo, `ListInstalledAdapters` lists the
  registry markers, roles ride ListAgents + the snapshot stream, the frozen-gate shim refusal; **MG-37
  — `mainguard-agent list` is scoped to the CALLER's own workers (it returned every session on the
  daemon, so a coordinator could enumerate other coordinators' workers and other repos' agents through
  its own jail socket); `AgentSession.ParentAgentId` records the spawning coordinator;** the
  Unix-socket legs — coordinator IPC endpoint + locked managed-worker spawn via the socket, and the
  REAL python3 `mainguard-agent` shim round-trip — are `LinuxOnly`, authoritative in the Linux CI
  leg),** `LoggingMaskTests` (secret-field mask), `DaemonClientReconnectTests` (restart→resume state
  sequence), `FixtureAcceptanceTests` (the TI-P2-00 fixture smokes),
  **`CompositionRootResolutionTests` (P2-47 integration proof #1 — every mapped gRPC service's ctor
  graph resolves via `ActivatorUtilities`, the gateway+governance singletons resolve, and the P2-12
  external-PR intake chain resolves so `PrIntakeHostedService` no longer idles — including, by
  reflection over the registered engine's own delegates, that its target resolver really is
  `PrIntakeTargetResolver.Resolve` and not a hardwired `null`, and that its worker host really is the
  daemon's `ExternalPrWorkerHost`: a hardwired constant is invisible from the outside, since the
  engine resolves and the hosted service starts either way), and that the daemon's
  `MergeQueueProvisioner` really was constructed with `checkAgentBranch` — an optional argument whose
  absence restores the silent stranded-branch behaviour exactly while every other test stays green),
  **`ExternalPrIntakeSpawnWiringTests`**
  (the intake→spawn decisions at the tier that needs no Docker: the explicit-`pr-<n>`-id scheme and
  its duplicate-id refusal; `EnsureWorkerAsync` adopting a live session and a restart-orphaned jail;
  the MG-2 managed-worker cap refusing an intake spawn over the SAME population an ordinary managed
  worker fills, with the under-cap control proving the refusal was the cap; an unprovisioned mirror
  failing and reclaiming its session; and `PrIntakeTargetResolver` over REAL repos, real mirrors and
  the production `GitService.GetRemotes` — right repo among two, one-component-off negatives each
  paired with a matching-source control, case-insensitivity, empty index, unreadable repo),
  **`AgentSessionRepoScopingTests`** (a session's identity is `(repo, agent id)`: two repos each
  running `pr-7` through the production `ExternalPrWorkerHost`/`AgentSpawnService` chain over a fake
  substrate — both spawn, each gets its OWN container, per-repo idempotence, the box-wide worker cap
  counting both, and repo A's release tearing down only repo A's jail while repo B's session,
  container and terminal lock survive; plus the store-level scoping, the duplicate-of-the-FULL-key
  throw with a same-id-different-repo control in the same test, the id-only lookups resolving
  *nothing* when ambiguous rather than guessing, the daemon's own `ResolveVerificationJail` giving
  each repo's queue only its own container, and the shipped
  `ContainerName`/`AgentSegmentName`/`AgentCachePath` already being per-repo for one shared id),
  **`Agents/AgentSessionScopingDockerTests`** (the #270 label fallback against two REAL jails that
  share an agent id: both carry `mainguard.agent=pr-7`, so
  `GatewayServiceRegistration.ResolveRunningJail` must disambiguate on `mainguard.repo` too — each
  repo resolves its own container, a third repo resolves null, and stopping one leaves the other
  resolvable), `AlphaLoopSmokeTests` (P2-47 integration proof #3 — the Alpha loop smoke through the
  REAL composition root: real in-proc daemon + shipped `DaemonClient` + shipped
  `DaemonBackedOrchestrator` — spawn→list→live-stream projection→stop, no mocks; the
  sandboxed-spawn/verify/review/merge legs are the documented manual runbook), `MergeDiffRpcTests`
  (P2-47 #7 — the new `GetMergeDiff` RPC exercised in-proc through the real composition root over a
  real provisioned bare mirror + agent worktree: the daemon runs `git diff main...agent/<id>`, the
  client parses the unified diff to `FilePatch`, and the parsed hunks carry the agent-branch change),
  `DaemonInfoRpcTests` (the tier-1 `GetDaemonInfo` skew probe in-proc through the real composition
  root: the daemon names its own assembly informational version and the stamped `MAINGUARDOS_VERSION`
  from an overridden release file, an absent stamp yields "" — never a throw — plus the pure
  `ParsePayloadVersion` cases; auth coverage for the new method rides the reflect-every-method theory
  automatically), `AlphaControlCenterProjectionTests` (P2-47 — the behavioral "no empty stub remains"
  proof: through the REAL composition root + shipped `DaemonClient`/`DaemonBackedOrchestrator`, a real
  daemon action projects live off each RPC — kill switch Engage/Resume → frozen state, a drafted plan
  → the pending-plan projection then Reject clears it, a real ledger spend → the telemetry projection
  + budgets round-trip, `SendMessage` → the coordinator transcript, and a registered `MergeQueue`
  entry → the merge-queue projection incl. `MainSha`); `ReviewCockpitOverlayAckTests` (the **review
  cockpit overlay** against the same real in-proc daemon: an RT-D2-blocked branch surfaces its flagged
  item on the overlay, the overlay's own per-row acknowledge control is pressed, and the **daemon-side
  gate** then permits the merge it was refusing — asserted on `ChangedTestCommandGate` state and
  `MergeQueue.CanMerge`, never on "the RPC was invoked" — plus the negative that a wrong item id
  clears nothing, and that with no active repo the acknowledgment is refused out loud rather than
  silently dropped)**, `FlaggedChangeMergeGateTests` (**P2-11 — the flagged-change gate refuses the
  merge AT THE DAEMON.** The gate had no production wiring at all, so every assertion here goes
  through the gRPC surface rather than a ViewModel — a ViewModel is precisely where this check used to
  live while the daemon waved the merge through. The decisive case is an out-of-approved-scope change:
  `CanMerge` refuses, the item reaches the human on `StreamQueue` addressed by the id the ack RPC
  accepts, `BeginMerge` is refused *and hands the lease back*, and only after
  `AcknowledgeFlaggedChange` — the act contract §4 denies the coordinator — is the merge granted. Plus
  the poisoned-executable-config arm (live today; it needs no plan), one-item-acked-leaves-the-other-
  blocking, an unknown item id clears nothing, and the fail-open guard: acknowledging for an agent
  whose review never ran must not CREATE its store, since an empty store reads as fully acknowledged
  and would bypass the MG-40 default-DENY)**. **`Gateway/`** (TI-P2-08): `Fake429EndpointTests` (invariant #1 end-to-end —
  `FakeModelEndpoint` returns 429-then-200, the `GatewayForwarder` returns exactly one delayed 200,
  PTY paused then resumed, agent `RateLimited` then cleared, lease settled from the usage body),
  `GatewaySpendRpcTests` (budgets get/set round-trip + `StreamSpend`/snapshot totals reconcile, on a
  `ConfigureTestServices`-isolated in-memory host), `GatewayKeyCustodyTests` (MG-4/MG-20/MG-38 stage 1
  — the gateway credential-custody boundary: `AgentGatewayCredentials` mints an opaque per-agent
  `mg_sess_` token for the jail while the REAL provider key stays daemon-side, tokens are revoked on
  stop and cannot be replayed, `ModelProxyMiddleware` derives the calling agent from that
  authenticated token instead of the spoofable `x-mainguard-agent` header, injects the daemon-held key
  at the network hop so the agent's own credential never survives it, refuses an unauthenticated
  caller with 401, and filters credential/Mainguard/hop-by-hop headers both directions),
  `GatewayUpstreamBindingTests` (**the per-agent upstream binding — every request built the way a
  confined jail actually sends it, i.e. `Host` = the GATEWAY rather than the provider. The custody
  tests above all use `Host = api.anthropic.com`, a shape production cannot produce once a CLI is
  pointed at the gateway, which is why they passed while the real path fell through unfronted and
  charged nothing. Covers: routing to the agent's bound upstream, `BudgetLedger` actually charged,
  an over-budget agent refused 402 with nothing forwarded, an OAuth agent passing through untouched
  and NOT 401'd (the regression that would hurt most), and an unknown token on the legacy model-host
  shape still refused so the pass-through is not an auth bypass**),
  `GatewayPipelineWiringTests` (**that the gateway is SERVING, not merely bound — the bind tests pass
  in a world where the port answers 404 to everything, which is exactly what the daemon did before
  the middleware was wired. Issues a real HTTP request over the real Kestrel listener, because
  ASP.NET activates middleware lazily and a constructor whose arguments cannot be resolved from DI
  would start the daemon happily and only fail on an agent's first call. Also pins that the branch
  does NOT leak onto the mutually-authenticated gRPC control port**),
  `Agents/GatewayConfinementDockerTests.cs` (**MG-4 end to end against REAL containers
  (`[RequiresDockerFact]`) — the layer the in-process gateway tests structurally cannot reach. The model
  request is issued FROM INSIDE a real hardened jail, sourced from the same
  `CredTmpfsSpec.DefaultCredentialPath` the CLI sources (spelled through the constant — a stale copy of
  the path would make `. <path>` yield an empty environment SILENTLY) and routed through the container's
  own `HTTP_PROXY`, so nothing about its shape is
  constructed by the test. That is what exposed the defect the change fixes: a confined request reaches
  tinyproxy naming the GATEWAY as its destination, and the default-deny filter had no entry for the
  daemon's own address, so Mainguard's own proxy answered 403 — switching the gateway on would have
  BROKEN every BYOK agent rather than metering it. Five legs: the traffic transits the gateway and the
  ledger is charged 41 tokens; the provider key is absent from the container spec, the credential tmpfs
  AND the agent's effective environment — the tmpfs sweep runs as BOTH owner uids, because each secret
  now lives in its owner's own `0700` directory and a sweep as the agent alone would silently stop
  covering the supervisor's; a second call over a 1-token cap is refused 402 with nothing
  reaching the provider; an OAuth agent is unconfined with its login restored and its provider host still
  carried by the proxy (paired with a not-allowlisted negative control so an offline runner cannot pass
  it for the wrong reason); and an unreachable gateway SKIPS confinement so the agent keeps its raw key
  and its working direct route — the precondition that makes gateway-on-by-default safe. Only the
  provider itself is faked, via the `DaemonHost` `configureServices` seam**),
  `DailyBudgetCapBootTests` (MG-21 — the persisted PER-DAY caps survive a restart: boot built
  `BudgetCaps(..., 0, 0)`, and 0 means UNLIMITED, so a daily budget silently stopped being enforced
  after every daemon restart while `GetBudgets` still reported it; resolves the ledger from a freshly
  built host whose store already holds a budget and asserts the daily cap is both carried AND
  enforced, with an unset-stays-unlimited control), `DatabaseBootstrapTests` (the daemon-DB bootstrap
  can never block the gRPC bind — a stale `__EFMigrationsLock` row from a daemon killed mid-migration
  is cleared before `Migrate()`, the migration runs under a watchdog, and expiry falls back to the
  in-memory stores). **P2-14 daemon in-proc tests:** `InputLockGrpcTests` (a **raw** `TerminalService`
  client — not `DaemonClient` — attaching to a `TerminalLockRegistry`-locked agent reads the banner
  then gets `PermissionDenied` on an input frame; the unlocked control echoes), `RoleInterceptorTests`
  (MG-12/MG-30 — a coordinator token now genuinely AUTHENTICATES and is then denied by the ROLE layer
  for `BeginMerge`/`ConfirmMerge`/`ApprovePlan` **and `GetScrollback`**, asserted on the role gate's
  own message so a bearer-layer rejection can no longer satisfy it; the operator token passes to
  `NOT_FOUND` and may still read scrollback; an unregistered token is still rejected outright),
  `ConnectionRoleRegistryTests` (MG-12 — `Resolve` fails CLOSED: only a constant-time match on the
  operator token yields Operator, unknown/null yield least privilege),
  `AttachInputLockInterceptorTests` (MG-31 — the interceptor's input-lock layer severs input for BOTH
  attach handshakes: it tracked only the bare `agent_id` oneof, so a P2-18 grid client selecting its
  agent via the `Attach` oneof left the tracked id null and every later `data` frame passed the gate;
  driven directly against `RoleInterceptor.LockedInputReader` because `TerminalGrpcService` re-checks
  the lock and an end-to-end assertion would pass either way), `ApproverIdentityDaemonDerivedTests`
  (SA-1/F2 — reflection proves `ApprovePlanRequest` has no identity field, and a
  `ConfigureTestServices`-overridden `IApproverIdentityResolver` proves the recorded/echoed approver
  is the connection's value regardless of the request). **`Agents/SwarmReconcilerDockerTests.cs`**
  (TI-P2-08 test 7, RequiresDocker via the new Docker-presence-only `[RequiresDockerDaemonFact]`):
  `Reconciler_OutOfBandDockerRm_ShouldConvergeOnBoot` stands up a trivial `busybox` container with the
  real `mainguard.agent`/`mainguard.repo` labels (not the P2-07 agent-base image), points a
  `SwarmReconciler` at the **real `DockerAgentLister`**, then `docker rm -f`s it out of band and
  asserts the next reconcile prunes + marks it `Dead` (Docker-as-truth convergence the simulated tests
  can't prove; cleans up in a finally). **`Agents/ResourceSamplingDockerTests.cs`**
  (`[RequiresDockerDaemonFact]`) proves the Resource Monitor's data source against a REAL engine, which
  is the claim the feature shipped without: a `busybox` spin loop pinning one core must read a real
  non-zero CPU **bounded by `ProcessorCount × 100`** (a mis-scaled formula cannot pass by being huge),
  a sleeping container alongside it must read far lower (so a constant-returning sampler fails), and a
  missing container must come back **UNKNOWN rather than 0** — `0%` and "no data" have to stay
  distinguishable. **`AgentResourceProjectionTests.cs`** carries the same claim across the whole wire
  through the REAL composition root: a scripted `IContainerResourceSampler` (injected via
  `DaemonFixture.ResourceSampler`) feeds 37.5% / 1 GiB in, and the shipped `DaemonClient` +
  `DaemonBackedOrchestrator` must project those exact values out of `GetAgentUsage()` — asserted on the
  VALUES, not the shape, because the old code passed every shape test while returning 0. It also pins
  the metering predicate on a real daemon (an agent `AgentGatewayCredentials.Issue`d a token is
  metered; one without is not, and carries no spend figure at all). **`Agents/`** hosts the TI-P2-06 integration suite on
  `DualRepoFixture`: `AgentTestGit.cs` (test-only git CLI helper + temp-VM-root/cleanup — not a
  production runner), `SessionKeyCacheScopeTests.cs` (MG-6 — the memory-only credential cache is
  scoped to **(repo, kind)**, never the bare agent kind: a model key / harvested CLI OAuth files /
  llm_env_* entries cached for one repo are invisible to another, each repo keeps its own key with no
  last-writer-wins clobber, a blank repo handle never forms a shared bucket, and a miss returns null
  rather than substituting a stranger's credential), `RepoProvisionerTests.cs` (first-run hardened
  bare mirror, incremental fetch advancing the head, manual-delete re-clone, spaces/Unicode path),
  `AgentBranchGuardTests.cs` (**the stranded-branch defect from live phase-1 testing**, over a REAL
  mirror/agent repo/linked worktree, driving "the agent" through a plain `git` CLI via `AgentTestGit`
  because the daemon's own helper pins `core.hooksPath=/dev/null` and would disable the very hook under
  test — a test that could only ever pass. Detection: `checkout -b` + commit is caught, the report names
  both branches and the computed fast-forward recovery; a detached HEAD is caught and not mistaken for a
  branch named `HEAD`; the on-its-own-branch control stays clean; no worktree reports `Unknown` rather
  than collapsing into "fine". Prevention: spawn installs the hook, `checkout -b` is refused with no
  residue and work on `agent/<id>` still succeeds; a 5-case theory pins that stash/tag/pack-refs/gc/
  detach are untouched; `TheHookSurvivesAnUpgradeOverAJailThatIsALREADYStranded` covers the population
  this ships to, where `pack-refs` re-states a pre-existing loose foreign branch as a create; and
  `TheHookDoesNotObstructTheDaemonsOwnGit` pins that spawn/teardown are unaffected),
  `AgentWorktreeManagerTests.cs` (add/remove/prune round-trip, duplicate-id + dirty-remove typed
  failures, **MG-3** quarantine-only remotes pointing at the agent's OWN repo (never the shared
  mirror), `AgentRepo_BorrowsObjectsThroughAlternates_NeverCopiesHistory` (the alternates file names
  the mirror's object store and the agent repo owns literally zero objects of its own),
  `AgentPush_LandsInItsOwnRepo_AndOnlyTheDaemonPublishesToTheMirror` (an agent push moves its own ref
  and leaves the mirror's where it was — only `PublishAgentBranch` carries it across), the
  byte-identical Windows↔VM round-trip, the pnpm hook, and the SC-2 resolved-name path via a
  `FakeAgentEnvironment`), `AgentRepoTests.cs` (**MG-3** — the agent-id gate as a `[Theory]` over
  traversal/ref-injection shapes vs the shapes the daemon actually mints;
  `Mirror_DisablesAutomaticGc_SoAnImplicitPruneCannotBreakBorrowers` (re-asserted on re-provision, so
  an old mirror is repaired by a daemon update); the §4 gc split proven on real git —
  `RepackWithoutPrune_KeepsEveryObjectResolvable_EvenUnreachableOnesAgentsMayBorrow` (the
  discriminator: with `-a` instead of `-A` the object is gone) and
  `Prune_IsRefusedWhileAnAgentIsAttached_AndReclaimsOnceTheLastOneDetaches`;
  `PublishedWork_IsCopiedIntoTheMirror_NotBorrowedFromTheAgentRepository` (destroy the agent repo and
  the mirror still serves the commit — the property that makes teardown safe) and the no-residue
  teardown), `AgentRefMediationTests.cs` (**MG-3 stage 2** on real git — fast-forward publish +
  idempotent re-publish; a rewritten-history publish REFUSED with the mirror's ref unmoved and the
  refusal surfaced to the warning sink; an agent deleting its own branch (or having its whole
  repository wiped) never read as a delete; hostile ids refused before git is touched and the
  integration branch untouched; the rule-4 check reached on its own by pointing the mirror's HEAD at
  the agent branch; one agent's forged copy of another's ref not carried across; no quarantine ref
  left behind on success OR refusal; and the watcher driven through `PollOnce` — publishes on a move,
  silent while still, KEEPS refusing rather than recording the snapshot and going quiet, and
  SELF-EVICTS an agent whose repository is gone (`SwarmReconciler` disposes an orphan by calling
  `RemoveAgentWorktree` directly and never unwatches it, and a vanished repo never publishes
  `Current`, so the entry would otherwise spawn a git process every tick for the life of the daemon) —
  but **only on a CORROBORATED absence**: `Directory.Exists` answers `false` on any error, so a
  momentary I/O failure could permanently unwatch a LIVE agent, and eviction is the one outcome here
  that is not self-correcting (every other one leaves the snapshot unrecorded so the next tick
  retries; an evicted agent has no next tick). `AgentRefWatcher.ProbeRepo` therefore distinguishes
  `Absent` from `Unreadable`, two consecutive absences are required before dropping the watch, and
  both the eviction and an unreadable repository reach the warning sink. Proven by
  `Watcher_WhenTheRepositoryCannotBeRead_KeepsTheWatch_AndCatchesUpOnceItCan` (an injected I/O failure
  — not a deletion — leaves the agent watched and the commit made during the outage publishes on
  recovery), the corroboration half of
  `Watcher_DropsAnAgentWhoseRepositoryIsGone_ButOnlyOnACorroboratedAbsence`, and the two `ProbeRepo_*`
  tests, the second of which pins the collapse itself — `Directory.Exists` is asserted to answer
  `false` for the very directory the probe reports `Unreadable`). **P2-07 RequiresDocker leg**
  (TI-P2-07 §A.5, PR-blocking in Linux CI): `Fixtures/SandboxFixture.cs` (spawns a real hardened agent
  jail through `DockerSandboxEngine` + `EgressProxyConfigurator` on an ext4 temp worktree and cleans
  up — the §A.4 infrastructure contract the egress/inspect/git-proxy/memory-scrape tests stand on;
  agent image ref via `MAINGUARD_AGENT_IMAGE`), `Fixtures/RequiresDockerFact.cs`
  (`[RequiresDockerFact]` skips unless Docker is reachable AND the CI-built agent-base image is
  present; the sibling `[RequiresDockerDaemonFact]` gates on Docker-daemon presence only — for P2-08's
  reconciler test that stands up its own trivial image; class-level
  `[Trait("Category","RequiresDocker")]` carries the CI filter),
  `Fixtures/RequiresAccessDeniedFact.cs` (`[RequiresAccessDeniedFact]` — skips unless this process can
  actually be DENIED access to a directory it owns, which root, Windows and any metadata-less mount
  quietly are not; the probe performs the real deny-then-read rather than trusting `chmod`, because a
  setup that silently leaves the directory readable turns "the I/O error path" into a test of the
  happy path. `AccessDenialSupport.Deny(dir)` hands back the restore as an `IDisposable`; the skip is
  set on `FactAttribute.Skip` from the constructor — xunit 2.9.3 reports `Assert.Skip` as a FAILURE),
  **`Fixtures/DockerSuiteIsolation.cs` (`DockerSuiteLock` + `DockerSuiteFixture` +
  `[CollectionDefinition("RequiresDocker")]` — the daemon lock that makes two concurrent
  RequiresDocker RUNS on ONE Docker daemon take turns instead of destroying each other's state. The
  suite's Docker resources are shared BY CONSTRUCTION: the egress proxy is a singleton per daemon
  (#242 established that `EnsureReadyAsync` does not own it), and
  `mainguard-agents`/`mainguard-egress` are addressed by literal name from the static MG-7/MG-18 gates
  every jail passes — so per-run resource names would mean renaming what those security gates key on,
  and several suites (the MG-18 drift plant, the adoption-under-disturbance tests) are ABOUT the
  singleton at its real name. What is isolated is therefore the RUN, not the resources: every class
  carrying the RequiresDocker trait joins the collection, whose fixture holds a cross-process
  `FileShare.None` file lock — the one primitive the kernel releases when a killed run's process dies
  — for the whole window its Docker tests execute, and sweeps the proxy + both topology networks +
  every `mainguard-agent-` segment at BOTH ends of that window. The startup half is the new one and it
  is not tidiness: teardown only ran on paths that completed, so a run killed mid-test leaked
  segments, and Docker's default bridge pool is ~32 deep (#270), meaning the leak fails some LATER
  run's spawn with an address-pool error. `FixtureAcceptanceTests` asserts the lock really excludes
  and really releases, that the sweep predicate matches mainguard's networks and no neighbour's, and —
  by reflection, with a floor on the count so an empty match cannot pass — that every RequiresDocker
  class is actually in the collection)**, `Fixtures/RequiresLibvtermFact.cs` (`[RequiresLibvtermFact]`
  — **visibly skips** the P2-18 grid legs when native libvterm is not loadable, replacing the old
  `if (!Available) return;` early return that reported a green **"Passed"** while asserting nothing;
  the sibling `LibvtermPresenceTests` is this project's merge gate — under
  `MAINGUARD_REQUIRE_LIBVTERM=1` a missing library is ONE hard failure instead of a silent skip,
  mirroring `EngineCatalogTests` in Mainguard.Tests), `Agents/MirrorReadOnlyDockerTests.cs` (**MG-3
  stage 3 — the finding, PERFORMED.** Launches the real production jail through the real
  `SandboxAgentLauncher` chain and, from inside it, attempts every write the finding names: overwrite
  `<bare>/refs/heads/main`, rewrite `packed-refs`, drop a loose object under `<bare>/objects/`, append
  to `<bare>/config`, and `git --git-dir=<bare> update-ref`. Each must be REFUSED, and
  `refs/heads/main` is re-read host-side afterwards. Asserting `ReadOnly == true` on a spec object
  would not be this test — a mount option that never reached a container proves nothing, and the
  finding is exactly that a control which looked right was not in the path. **Non-vacuity was taken in
  the natural direction AND per probe**, which is what made it worth doing: with
  `MirrorMountReadOnly = false` the `refs/heads/main` overwrite SUCCEEDS on a box with no userns remap
  (container uid 1000 IS the daemon's, and `core.sharedRepository=group` makes it group-writable
  besides) — measured, then flipped. Inverting EVERY attack assertion to `WROTE` against a writable
  mirror then exposed three further frames that were reporting refusals they had never attempted: the
  `update-ref` sha was an inlined nested command substitution whose escaped quotes reached git
  literally; corrected, it named the AGENT's HEAD, an object the mirror does not have, so update-ref
  failed on the lookup rather than on permissions; and the reads sat AFTER the destructive writes, so
  on a writable mirror the earlier probes corrupted `packed-refs` and everything downstream failed for
  that reason instead. A test-level flip alone passes through all three — the first assertion fails
  either way and the frames behind it measure nothing. Every probe is sentinel-FRAMED and prints
  `WROTE` or `REFUSED` and never nothing, so a shell that did not run cannot read as a refusal;
  positive controls in the SAME exec require that the agent can still read the mirror's history
  through the alternate, still write its OWN repo, and still `git commit` + `git push origin` in
  /workspace — because "everything was refused" is also what a broken probe reports),
  `Agents/SandboxSpawnDockerTests.cs` (P2-47 #8, `[RequiresDockerFact]`: the real sandboxed spawn
  behind `AgentService.SpawnAgent` — a provisioned repo drives `SandboxAgentLauncher.TryLaunchAsync`
  into a real hardened jail carrying the `mainguard.repo`/`mainguard.agent`/`mainguard.role` labels,
  then tears it + its worktree down; an unprovisioned handle degrades to a session-only launch with no
  jail), **`SecretDeliveryDockerTests.cs`** (`[RequiresDockerFact]` — secrets really arrive: a full
  production spawn carrying a per-run nonce, then a SENTINEL-FRAMED in-jail probe asserting the
  credential file holds that nonce at mode `400` owned by the agent uid and the OOB key at `400` owned
  by the *supervisor* uid (G2 control 1 asserted as `credential.Uid != oob.Uid`, not as two
  constants). The framing is the point: "mode 0400, uid 1000" is exactly the assertion that passes
  when the probe printed nothing, so a missing frame is its own reported failure, and the nonce means
  a stale or coincidentally-0400 file cannot satisfy it. A third test pins the *reason* the archive
  API is unusable by asserting Docker really does refuse `ExtractArchiveToContainerAsync` into the
  read-only-rootfs jail — if that ever starts passing, the transport can be revisited),
  **`Agents/CliLoginRoundTripDockerTests.cs`** (`[RequiresDockerFact]` — the **CLI-login round-trip**
  end to end against real jails: a per-run nonce is written into jail #1's tmpfs `$HOME` at the one
  path a temp install marker DECLARES, `SandboxAgentLauncher.HarvestCliCredentialsAsync` reads it back
  out, `CliLoginVault.MergeAndSerialize` produces the exact string the OS keychain would hold, jail #1
  is removed (the tmpfs dies with it), and a FRESH jail spawned with those restored files is probed
  with a sentinel-framed `cat`. Docker rather than a fake because all three hops — the tmpfs overlay,
  the harvest exec running as the agent uid against a 0600 file, and the exec-stdin restore that
  `docker cp` cannot do (it writes UNDER the tmpfs and reports success) — are invisible to a fake
  engine. The CALLER that drives this in the shipped app is pinned separately by
  `CliLoginHarvestWiringTests`), `SpawnImagePreflightTests.cs` (the v1 spawn preflight, in-proc — no docker: both images present
  proceeds to the engine; a missing `mainguard-agent-base`/`mainguard-egress-proxy` answers
  `FailedPrecondition` naming exactly that image + the repair BEFORE any worktree/jail work — the
  egress image's absence was previously not actionable), `Agents/SandboxHardeningDockerTests.cs`
  (docker-inspect shows no Windows mounts + live userns/limits, persistent-jail start-not-recreate,
  cred tmpfs 0400/tmpfs per-agent, and the G2 key-custody proof — the agent uid cannot read the
  supervisor-owned `/run/secrets/supervisor/oob.key` — probed as EACH SECRET'S OWNER, since the agent
  cannot see into the supervisor's `0700` directory at all and asking it would conflate "not delivered"
  with "properly hidden"; plus `JailWithTheOldFlatSecretsTmpfs_IsRecreated_NotReused`, whose legacy
  container is byte-identical to a real one **except** its tmpfs — a hand-built stand-in with no mounts
  and no network is recreated by the checks that already existed, and that first version stayed green
  with the new check disabled). **These RequiresDocker legs only ever run against
  a modern engine (Docker Desktop / CI, Engine 29.4.3), so they could not catch the in-jail `chown`
  EPERM that broke every spawn on `MainguardEnv`'s Docker 20.10.24** — on 20.10.24 a non-root `User`
  plus `no-new-privileges` leaves even a uid-0 exec with an empty permitted capability set, and on
  Engine 29 it does not. The guards for that live in the no-Docker leg
  (`ContainerSpecBuilderTests.Build_EachSecretLivesInATmpfsOwnedByItsOwnUid_SoNoChownIsEverNeeded`,
  `SandboxSecretWriteTimeoutTests.SecretWrite_RunsAsTheSecretsOwner_AndNeverChowns`), which is the only
  leg that is engine-independent. `Agents/ToolchainProvisioningDockerTests.cs` (**MG-42** —
  the per-repo toolchain layer built by the SHIPPED `ToolchainProvisioner` against a real runtime and
  then run in a real hardened jail: the premise asserted rather than assumed (`command -v dotnet`
  fails in the base image, so a green suite cannot be hiding that the layer was never needed),
  `dotnet --version` answering `10.0.3x` inside the jail, the layered jail still running as uid 1000
  on a read-only rootfs (a layer ending on `USER root` would undo a control `ContainerSpecBuilder`
  cannot re-check, since it asserts the create SPEC and the user comes from the IMAGE), the
  `mainguard.toolchain.base-digest` label equalling what the base ref resolves to right now, the spawn
  ref being the layer's own digest, the second `EnsureAsync` measured as a cache hit, and
  `RecordTheEngineAndItsImageStoreBehaviour` — an observation, not a gate: it prints the engine
  version/driver and the base image's `RepoDigests` on every run, because when four of these failed on
  CI and passed locally the deciding difference was that store's `RepoDigests` behaviour and **the
  runner's Docker version appears nowhere in the workflow logs**, so the question had to be answered
  by reasoning from an error string instead of by reading a fact),
  `Agents/SandboxEgressDockerTests.cs` (the egress matrix — allowlisted API via proxy, non-allowlisted
  fails **fast**, direct-IP dropped despite proxy-env unset, DNS exfil NXDOMAIN, **in-jail DNS that
  depends on no public resolver** (`AllowlistedName_ShouldStillResolve_WithoutAnyPublicResolver` —
  blocks `1.1.1.1` in the proxy netns AND flushes dnsmasq's cache, because a warm entry answers the
  probe correctly with the fix removed), the **pre-baked toolchain available in a live session**
  (`PrebakedToolchain_ShouldBeAvailableInLiveSession` — jq/rg/fd/make/node/python3/go on PATH, no
  runtime egress; the A6-clean replacement for runtime `devbox add`), and **P2-08 gateway fronting
  actually IN EFFECT** (`RenderedGatewayUpstreams_ShouldBeInEffectOnTheRunningProxy_NotMerelyWritten`
  — points the gateway at a closed loopback port and requires the running tinyproxy to answer a
  model-API host with `502 Unable to connect to upstream proxy`, a verdict only a LOADED `upstream`
  directive produces, with a non-fronted PackageRegistry host as the control; asserting the rendered
  file exists would reproduce the defect it guards), and the A6 direct-git-host-clone-fails-fast),
  `Agents/EgressProxyReloadDockerTests.cs` (**MG-41 — a config push must not take the proxy's
  listeners down, and "ready" must mean the proxy stays up**: an unchanged policy leaves both daemons'
  pids untouched while a changed one restarts them; the entrypoint's boot reload never restarts
  anything after `EnsureReadyAsync` returns, on a genuine cold boot and again when the proxy's own
  applied-digest record is gone; and — the constraint the skip must never trade away — a host REMOVED
  from the allowlist really does become filtered, asserted on tinyproxy's 403 rather than on "the
  request failed" so it holds on a runner with no route out. Every assertion is paired with a control
  that fails if the skip became unconditional), `Agents/SandboxNetworkIsolationDockerTests.cs`
  (**MG-36 east-west isolation, read off real containers**: two jails on two per-agent segments — A
  cannot reach B's listener and cannot pivot via the proxy's address on B's segment, while the paired
  positive controls (B reaches its own listener; A reaches the proxy on its own segment) fail if the
  setup is broken, because a negative claim is otherwise trivially satisfiable by a dead container;
  plus "a jail on its own segment still has no route off the bridge". Uses
  `SandboxFixture.CreateJailOnSegmentAsync`, which builds the production hardened spec but skips the
  stdin-exec secret delivery — a network test must not fail on a Docker endpoint that does not deliver
  exec stdin. Also `ReachabilityProbe_ClassifiesOnTheHandshake_NotOnGettingAPrettyReply` — **the guard
  on the diagnostic itself**, which has now caught two distinct bugs in that one probe. First: the
  leg-1 probe used `sh -c 'echo > /dev/tcp/…'`, and `/dev/tcp` is a **bash** builtin while `sh` is
  dash, so it printed UNREACHABLE *unconditionally on every run* while the report's own guide read
  that as "the jail cannot reach the proxy" — a permanent false negative aimed at the wrong leg.
  Second: its replacement classified on "did I get a valid HTTP status", and CI hit a hop that
  answered `curl: (56) Recv failure: Connection reset by peer` — a **reset proves the handshake
  completed**, so that is REACHABLE, and the probe said otherwise. `SandboxFixture.TcpProbeAsync` (the
  ONE probe, shared with the diagnostic; curl, because the image has no `nc`) now reads the verdict
  off curl's `num_connects`/`time_connect` — the TCP fact itself. **Every peer in that test is one the
  test stands up** (a server, a `SO_LINGER(1,0)` accept-then-RST listener, a closed port, an
  unroutable RFC-5737 address): its positive case used to be the real jail→proxy hop, which made it
  assert "the shared proxy is up right now" — a different proposition that the entrypoint
  double-reload makes intermittently false, and the cause of a third CI failure. The real hop stays as
  a printed diagnostic, never the verdict. `Fixtures/TcpProbeClassifierTests.cs` pins the decision
  table without Docker, covering the branches a jail with `CapDrop ALL` cannot arrange — above all
  **exit 28**, a connect that timed out because the SYN was DROPPED, which needs a packet filter to
  produce. `JailResolution_IsIPv4Only_MatchingTheIPv4OnlyFabric` pins `filter-AAAA` behaviourally
  through node's resolver (`getent` cannot see it, and the proxy's own A-only record cannot either —
  only a FORWARDED name has a real AAAA to suppress, which is why its A-record control is load-bearing
  and says out loud that a forwarding failure there means the proxy's own stub resolver was
  unreachable, not an IPv6 regression)), `Agents/DaemonGitProxyDockerTests.cs` (A6 with real git
  against a local bare "host": allowlisted-prefix fetch succeeds + transparency-logged, push refused +
  audited + no ref moved, non-allowlisted prefix refused, and the F5 declared-dependency scope).
  `PlatformFacts.cs` supplies the `LinuxOnlyFact`/`WindowsOnlyFact` skip-with-reason attributes for
  the platform-split PTY probes. `ResizeClampTests.cs` (MG-22 — resize dimensions are clamped to
  `VtermSession.MaxDimension` BEFORE the native call: the proto carries `uint32` and only `<=0` was
  rejected, so anything in `[1, 2^31-1]` reached `vterm_set_size`, whose upstream `alloc_buffer`
  multiplies `rows*cols` with no overflow or upper-bound check; clamping is applied in
  `BoundTerminalSession.Resize` too so the PTY and the grid are never driven to different sizes).
  **P2-09 lifecycle suite (`Agents/`, real git over `DualRepoFixture`):** `KeepAliveRebaserTests`
  (clean rebase onto advanced main + wip commit + resume; agent mid-its-own-rebase → guard skip then
  next-cycle success; induced conflict → status `Conflict` routed to the T-04 resolver with the rebase
  left in progress and the PTY unresumed; `HumanEdits_ReachWorktreeOnlyViaGit` — an uncommitted
  Windows-side change is absent from the worktree, a committed one arrives via rebase),
  `TeardownResidueTests` (Dispose → no worktree/branch residue, terminal event emitted, clean
  `TeardownReport`; failure-tolerant + idempotent), and the RequiresDocker leg
  `AgentLifecycleDockerTests` (`[RequiresDockerDaemonFact]`, busybox — NOT the agent-base image: the
  yield-timeout `docker pause`/`unpause` proven against a real container, and the leader boot reattach
  converging on Docker truth across a registry reload). **P2-10 RequiresDocker leg
  (`Agents/MergeQueueDockerTests.cs`, `[RequiresDockerDaemonFact]`, busybox):** the launch-blocking
  OPS SA-1 / M7-exit guarantees proven against a **real** container runtime through the real
  `DockerSandboxEngine` + `VerificationRunner` (not the fast `FakeSandboxEngine` unit variant) —
  `ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit` (a real `docker exec` that prints a
  forged `passed` claim but exits non-zero → `Passed:false` → never `Verified` → `CanMerge` false;
  exit-0 control → `Verified`), `Verification_ShouldRunInWorkerSandbox_NeverHost` (a marker written by
  the command is present in the container via a second real exec, absent on the host), and
  `TwoWorkers_StaleCascade_WithRealContainerVerification` (two real jails: A,B verify green, A merges
  → B `StaleVerified`/blocked → re-verifies in its container against the new sha →
  `Verified`/mergeable).
Not in the solution (scratch/experiments, don't rely on them): `Mainguard.StyleConsole`, `Mainguard.StyleTests`, `Mainguard.AvaloniaTests`.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
