<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
# `Mainguard.Git/` — git engine + persistence base

The all-editions base. Git logic goes here.

- **`MainguardPaths.cs`** — **the single source of truth for Mainguard's per-user data root**
  (`%LocalAppData%\Mainguard` on Windows, `~/.mainguard` elsewhere) + `HomeDirectory()`. **Phase-4:**
  `MigrateLegacyWindowsDataRootOnce()` moves an upgrading install's
  `%LocalAppData%\Mainguard`→`\Mainguard` on first run (one-shot, best-effort, never throws; hooked in
  `ShellEntryPoint.RunDesktop` before the DB opens). The Unix branch is DELIBERATELY still
  `~/.mainguard` — it is the in-VM daemon identity (`/home/mainguard/.mainguard`), which moves only
  with the coordinated `MainguardEnv`/`mainguardd` re-identity (open owner decision). Every component
  that persists per-user state resolves through here instead of calling `Environment.GetFolderPath`
  directly. **Why it exists (the mainguardd crash-loop bug class):** on Unix the default
  `GetFolderPath(...)` VERIFIES the directory exists and returns `""` when it doesn't — a fresh
  service account has no `~/.local/share`, so every
  `Path.Combine(GetFolderPath(LocalApplicationData), "Mainguard")` silently produced the RELATIVE
  `Mainguard/…`, resolved against the daemon's CWD (`/`), and threw `EACCES` → unhandled → systemd
  restart loop (`SecureKeyring` was the last one to bite; setting `HOME` did NOT fix it because the
  subdir still didn't exist). This resolves with `DoNotVerify`, falls back to `$HOME`, and **throws
  with the remedy named** rather than ever returning an empty/relative path. Callers routed through
  it: `SecureKeyring`, `AppDbContext`, `SettingsService`, `DaemonPaths`, `FileAdapterManifestCache`,
  `JsonOobeStateStore`, `RepoProvisioner`/`WorktreeManager` (VM root), `OobeInstanceLock`, and the
  three `DaemonHost` store-path fallbacks. **The rule is now structural, not remembered:**
  - `Mainguard.Tests/MainguardPathsGuardTests.cs` scans the shipping source and FAILS on any
    `Environment.GetFolderPath` call outside this file — so the bug class cannot be reintroduced.
    Migrating the last stragglers routed `BootstrapFileSystem`, `SshKeyService`, `GitServices`'
    signing-key lister, `DockLayoutPersistence`, and the installer/uninstaller data roots through here
    too (`WindowsSystemProbe` uses `Environment.SystemDirectory` instead).
- **`AppDbContext.cs`** — EF Core `DbContext`; the SQLite schema (repositories, categories, user
  preferences, pinned graph refs, the T-19 operation `JournalEntries`, the T-21 `GitProfiles`, and the
  P2-01 `TosAcknowledgments`, and the P2-08 gateway tables (`SpendRecords` spend ledger,
  `ExpectedAgents` reconciler table, single-row `GatewayBudgets` — carries the P2-13/P2-08 per-day cap
  columns `UsdMicrosCapPerDay`/`TokenCapPerDay` alongside the per-agent caps), and the P2-10
  merge-queue tables (`MergeQueueRows` — one persisted state row per (repo, agent), written in the
  same transaction as every state-machine transition so a restart resumes; `VerificationRows` —
  immutable verification records keyed to `main@sha`, insert-only; `MergeLeaseRows` — the RT-D1
  per-repo merge lease + idempotency record), and the P2-12 external-PR-intake tables
  (`PrIntakeSubscriptions` — one row per `(host, owner, repo, filter)` subscription, unique so a
  duplicate subscribe is idempotent; `PrIntakeHeads` — the last-seen head SHA per
  `(source, PR number)`, the "seen PR heads" store + tracked-PR set) — with the
  `HasTosAcknowledgment(provider)` query helper, case-insensitive, that P2-15 chains. `MergeQueueRow`
  carries a P2-12 `Origin` column (`Local`/`External`) and the discard record —
  `DiscardedBy`/`DiscardedAtUtc`/`DiscardReason`, all null except on a `Discarded` row. The record
  lives on the row rather than only in the audit log because the daemon's `IAuditLog` is in-memory
  today, so the row is what actually survives a restart; `DiscardedBy` is daemon-derived at the RPC
  (there is no actor field on the wire) and attributes the host session, not a distinguishable human —
  see `IApproverIdentityResolver`. Migrations live in `Migrations/` (P2-10 adds
  `AddMergeQueue`; P2-12 adds `AddPrIntake` — the two intake tables + the `Origin` column; P2-13 adds
  `AddGatewayPerDayBudget` — the two per-day budget columns; `AddMergeQueueDiscardRecord` adds the
  three discard columns; `AddPrIntakeConfig` adds `PrIntakeConfig`, the intake's **singleton** (`Id = 1`,
  `ValueGeneratedNever`) daemon-wide configuration row — `Enabled`, `PollIntervalSeconds` and a
  comma-separated `BotAuthors`, read and written whole, which is why the author list is one column and
  not a child table; P2-15 adds `AddAuditChain` — the `AuditRecords` table (`Models/AuditRecordRow.cs`:
  `Seq` chain-assigned PK, `TimestampText` stored as the exact hashed ISO-O string, encrypted
  `PayloadCiphertext`, `KeyId`, `PrevHash`/`Hash`, `Redacted`) **plus the two append-only SQLite
  triggers** — any `DELETE` aborts, and the only legal `UPDATE` is the redaction tombstone transition,
  so even raw SQL must first drop a trigger to rewrite history, which the hash chain then catches).
- **`Actions/`** — the UI-free command surface for the command palette + keyboard shortcuts (T-18);
  pure and unit-tested, and the seam that later becomes the agent command surface.
  - `AppAction.cs` (one invokable action: `Id`/`Title`/`Category` + `Func<bool> CanExecute` +
    `Func<Task> Execute`), `ActionRegistry.cs` (register/`All`/`Enabled()` — duplicate id throws on
    registration, `Enabled()` filters by live `CanExecute` so an unavailable action is never listed),
    `ActionIds.cs` (shared id constants), `FuzzyMatcher.cs` (pure subsequence matcher:
    `Score`/`Match`/`Rank<T>` with word-boundary + consecutive-run bonuses and gap penalty; pinned
    scoring, greedy/deterministic; empty query matches all with score 0, non-subsequence → `NoMatch`
    sentinel; `Match` also returns matched-char positions for highlighting), `ShortcutMap.cs` (pure
    id→gesture map: `Default` (Ctrl+P palette, Ctrl+Enter commit, Ctrl+Shift+P push, F5 refresh, Ctrl+B
    new branch), `NormalizeGesture` for case/modifier-order-insensitive equality,
    `Conflicts()`/`HasConflicts` flag a gesture bound to two ids, `With` rebinds immutably,
    `FromPreferences`/`ToPreferences` overlay+diff against defaults for persistence). No Avalonia.
- **`Models/`** — plain data/domain types: `Repository`, `WorkspaceCategory`, `GitCommitItem`,
  `GitBranchItem`, `GitFileStatus`, `GitStashItem`, `GitDiffLine`, `SideBySideDiffRows`,
  `RemoteRepository` (host-agnostic "my repositories" list item — GitHub/GitLab map their JSON onto
  it; P2-48), `CommitSearchFilter`, `UserPreferences`, `PullStrategy`, `HostKind`, `RebaseTodoItem`,
  `MergeChunk` (+ `ChunkKind`/`ChunkResolution` enums), `ConflictedFile`, `ConflictSide`,
  `GitTagItem`, `DiffLine`/`DiffHunk`/`FilePatch` (+ `DiffLineKind`; unified-diff model for partial
  staging, in `DiffHunk.cs`), `WorktreeItem`, `SubmoduleItem` (one submodule: path, URL, recorded
  `HeadSha`, rolled-up `SubmoduleState` — enum `Uninitialized`|`UpToDate`|`Modified`|`Dirty`; T-16),
  `GitHeadState` (HEAD snapshot — attached/detached/unborn + tip SHA — driving the graph context-menu
  rules in T-09), `PinnedRef` (a pinned branch/tag per repo, ordered first into the graph router —
  T-09), `GitRemoteItem` (a configured remote: name + fetch URL + optional distinct push URL — T-10),
  `BlameLine` (per-line blame attribution — 1-based line number, commit SHA, author, date, boundary
  flag — T-11), `FileVersion` (one revision in a file's history — SHA/`ShortSha`, historical
  `PathAtCommit` that follows renames, short message, author, date — T-12), `LfsFile` (one
  `git lfs ls-files` entry — OID, working-tree path, downloaded-vs-pointer flag — T-17),
  `SignatureStatus` (enum: the `%G?` verification states) + `CommitSignatureInfo` (status + signer) +
  `SigningKeyOption` (a pickable signing key — id/label) in `SignatureStatus.cs` (T-15),
  `JournalEntry` (one journaled mutating op for T-19 undo/redo — Id/RepoPath/Kind/Description/WhenUtc,
  pre/post ref-map JSON, `IsUndoable` + `UndoBlockedReason?`, `IsUndone`/`IsTruncated` state;
  persisted via `AppDbContext`), `ReflogItem` (one reflog entry for T-20 — from→to sha, first-line
  message, `When`; plain data), `GitProfile` (a switchable Git identity for T-21 — name +
  `UserName`/`UserEmail` + signing prefs; persisted via `AppDbContext`), `CloneProgress` (+
  `ClonePhase` enum — an immutable clone progress snapshot for T-21: received/total/indexed objects,
  bytes, checkout steps, monotonic `Percent`, status text), `PullRequest.cs` (T-23 host-agnostic PR
  types: `PullRequestState`/`PullRequestMergeMethod` enums + `PullRequestItem`
  (number/title/author/source→target/state/draft/url), `PullRequestDetail`
  (body/mergeable/reviewers/checks), `CreatePullRequest`), `PullRequestReview.cs` (T-25 host-agnostic
  review types: `ReviewVerdict` (Comment/Approve/RequestChanges → GitHub event) + `ReviewState`
  (Pending/Commented/Approved/ChangesRequested/Dismissed) enums, `PullRequestReview`
  (id/author/state/body/submittedAt), `ReviewComment` (one inline file/line comment —
  path/`Line?`/diff-hunk/body; `Line` is null when the comment is on an outdated diff), `SubmitReview`
  (verdict + body)), `Issue.cs` (T-24 host-agnostic issue types: `IssueState` enum + `IssueLabel`
  (name + host hex color for the chip), `IssueItem`
  (number/title/author/state/comment-count/labels/assignees/url/updated-at), `IssueComment`,
  `IssueDetail`, `CreateIssue`), `CheckStatus.cs` (T-26 host-agnostic CI/checks types: `CheckState`
  enum (Pending/Success/Failure/Neutral — the badge-facing roll-up), `CheckRunItem`
  (id/name/state/raw-status/conclusion/details-url/completed-at; `CanRerun` is false for a legacy
  commit status whose id is 0), `CommitChecks` (sha + `Overall` + pass/fail/pending counts + runs,
  with `HasAny` false ⇒ no CI configured; `None(sha)` is the empty result)), `Notification.cs` (T-27
  host-agnostic notifications types: `NotificationReason` enum
  (Mention/ReviewRequested/Assign/Author/Comment/StateChange/Subscribed/TeamMention/CiActivity/Other)
  + `NotificationSubjectKind` enum (PullRequest/Issue/Commit/Release/Discussion/Other) driving the
  reason chip + subject-kind icon, `NotificationItem` (thread
  id/reason/kind/title/`RepoFullName`/best-effort web `Url`/unread/updated-at)), `Release.cs` (T-28
  host-agnostic release types: `ReleaseItem`
  (id/tag/name/body/draft/prerelease/author/published-at/url) and `CreateRelease` (tag +
  `TargetCommitish` for a new tag + name/body/draft/prerelease)), `CommitContext.cs` (T-32 blame →
  PR/issue types: `LinkedIssueRef` (number + `RepoFullName`) and `CommitContextResult` (sha + the
  `PullRequestItem`s that contain the commit + the parsed `LinkedIssueRef`s)).
  - `UserPreferences` carries `AutoFetchMinutes` (T-10 auto-fetch cadence; 0 = off) and
    `SyntaxHighlightDiffs` (T-13 diff syntax-highlight toggle; default true).
  - `UserPreferences` also carries the T-15 signing prefs (`SignCommits`, `GpgFormat`, `SigningKey`,
    `GpgProgram`, `ShowSignatureStatus`), the T-30 pre-commit scanner prefs (`PreCommitScanEnabled`
    default true, `PreCommitMaxFileMB` default 5 — JSON, no migration), the T-31
    `UseStructuredCommitComposer` flag (plain ⇄ structured commit-composer mode; JSON, no migration),
    and the T-18 `ShortcutBindings` (id → gesture overrides layered on the `ShortcutMap` defaults; empty
    string clears a default; JSON-persisted, no migration).
  - `UserPreferences.MacTranslucentChrome` (default false) drives the macOS vibrancy chrome via
    `Mainguard.UI/Theming/VibrancyManager`; it replaced the never-read `EnableGlassmorphism` key
    (deliberately not reused — it defaulted to true, and a stale persisted `true` would silently
    switch existing installs to translucent chrome; unknown JSON keys are ignored on load).
  - `GitDiffLine` carries the T-13 intra-line `HighlightSpans` (changed-word char ranges into `Content`)
    + `TrailingWhitespaceSpan` + `EmphasisKey`.
  - `TosAcknowledgment` (P2-01: a recorded provider-ToS acknowledgment — `Provider` + `AcknowledgedAt` —
    persisted so the CLI-OAuth notice isn't re-shown; backs `AppDbContext.HasTosAcknowledgment`).
- **`Graph/`** — commit-graph layout: `CommitGraphRouter.cs` (lane assignment / edge routing;
  accepts optional pinned-ref tip SHAs to reserve left-most lanes — T-09. **H2-optimized** (ADR-003):
  a SHA→lane dictionary + free-lane `SortedSet` mirror the lane list so every lookup is O(1) — output
  pinned byte-identical to the pre-optimization algorithm by an embedded oracle copy in
  `CommitGraphRouterWideDagTests`; left-most-dominance/left-most-free-slot *is* the
  crossing-minimization policy) + `GraphModels.cs` (nodes/edges/lanes; `GraphLine` is a
  `readonly record struct` — a wide DAG emits ≈ lanes×commits of them). Consumed by the
  `CommitGraphCanvas` control.
- **`Review/`** — the **pure**, fixture-tested P2-11 review-cockpit rule surface (no
  repo/IO/**network** — CVE lookups read only the shipped offline snapshot; the UI renders these,
  never re-derives them — invariant 1).
  - `RiskClassifier.cs` (`RiskCategory` enum ExecutableConfig=0…Docs=7 + `HunkRisk`;
    `Classify(path, hunk)` by path rules + the load-bearing package.json content rule — a
    `"scripts"`-block hunk → ExecutableConfig, a dependency-version-only hunk → Lockfile — renamed files
    by new path + content; plus the `FilePatchPath` new-path resolver).
  - `ProvenanceReader.cs` (`HunkProvenance` + `AgentTraceRange`; `FromAgentTrace`/`ParseTraceRanges` —
    our shape + external-vendor shapes, malformed → empty, never throws — `ForHunk` new-range join, and
    `FromTrailers` — trailer-less human commit → null).
  - `AcknowledgmentStore.cs` (`FlaggedChange`/`FlaggedKind`; the per-branch item-by-item ack ledger
    bound to the **SHA-256 of the canonical flagged set** — a new push resets every ack (invariant 2),
    `LastResetCount` for the "N items reset" copy, `acknowledged_flagged_change` audit events for P2-15;
    **no bulk-ack method** so a global checkbox is impossible by construction).
  - `LockfileSemanticDiff.cs` (`LockfileKind`/`DependencyDelta`; `Parse(old,new,kind,osv,asOf)` for
    package-lock.json/pnpm-lock.yaml/`*.csproj` PackageReference/poetry.lock → per-dep rows with
    major-jump/install-scripts/registry-change + offline OSV CVE ids; script/CVE rows feed the flagged
    gate. `DependencyDelta.AdvisoriesChecked` is the `RttMeasured`-shaped third state — false means the
    empty CVE list is a **silence, not an answer**).
  - `LockfileReview.cs` (the blobs→must-ack policy the daemon's merge path calls:
    `KindFor(path)` — deliberately narrower than `RiskClassifier`'s lockfile *category*, so a format with
    no parser is not handed to one — and `Review(path,kind,base,branch,osv,asOf,unreadable)` →
    `FlaggedChange` rows. **Fail-closed**: unreadable blob, oversize manifest (`MaxManifestBytes` 8 MB) or
    a snapshot that cannot answer all produce ONE `LockfileAdvisoryUnknown` item per lockfile, never an
    omission — an omitted item is an acknowledged item. `ItemsFor` is the single definition
    `FlaggedChangeDetector.FromLockfileDeltas` delegates to).
  - `OsvSnapshot.cs` + `OsvSnapshot.json` (the shipped **offline** OSV database, embedded resource;
    `Default` reads it once, `FromEntries(advisories, capturedOn)` injects a test snapshot,
    `Unavailable(state)` builds the not-loaded value — a review-time network call is a rejection trigger.
    `OsvSnapshotState` Available/Missing/Malformed/**Stale** + `CanAnswerAt` decide whether an *absence*
    of hits is evidence; `MaxAge` is 90 days, argued against the bundled-refresh cadence).
  - `TestDeltaParser.cs` (`TestOutcome`/`TestDelta`; `ParseTrx`/`ParsePassFail` +
    `Compute(current, baseline)` → new-fail/new-pass delta for the §6.5 strip; malformed TRX → empty,
    never throws).
- **`Analytics/`** — repository analytics (T-22), feeds `AnalyticsView`.
  - `RepositoryAnalyzer.cs` runs two `CancellationToken`-honoring background walks through
    `IGitService.ExecuteWithRepo`: a **gitignore-aware working-tree walk** (`repo.Ignore.IsPathIgnored`,
    per-directory cached, `.git/` always skipped, negations like `!keep.js` honored) → bytes per
    language via `LanguageRegistry`, and a single **history walk** (HEAD, capped) → one `CommitStat` per
    commit (churn = diff vs first parent; merges get 0 churn flagged by `ParentCount`; binary files
    report 0 lines and drop out). The history walk is served from `CommitStatsCache.cs` — a bounded LRU
    (default 8) shared across analyzer instances, keyed `(repoPath, headSha, maxCommits)` so a moved
    HEAD misses naturally and cached stats are never stale (H1, ADR-002; the working-tree language walk
    is deliberately uncached — it depends on the dirty worktree, not HEAD). All aggregation lives in
    pure, unit-pinned types so no numbers are computed in the UI: `CommitStat.cs` (the per-commit DTO),
    `PunchCardStats.cs` (commits bucketed by weekday×hour on the commit's **own UTC offset** —
    deterministic, never `ToLocalTime`), `ChurnStats.cs` (added/removed lines bucketed by Monday-started
    week, zero-filled, merges excluded), `ContributorStats.cs` (per-author commits/churn, identities
    merged case-insensitively by email, ranked).
  - `LanguageRegistry.cs`/`LanguageModel.cs` (extension→language + GitHub color metadata),
    `languages.json` (embedded resource).
  - `ChangelogGenerator.cs` (T-28) is the **pure, unit-pinned** changelog builder used by the Releases
    feature (and reusable by a future notes-drafting agent): `ParseSubject(sha, subject)` → a
    `ChangelogEntry` (conventional-commit type/scope/description/`!`-or-`BREAKING CHANGE` breaking flag;
    a non-conventional subject → `Type="other"`, never dropped), and
    `BuildNotes(entries, previousTag, newTag)` → grouped Markdown (Breaking Changes / Features / Fixes /
    Other + a "Full changelog: prev…new" line; empty entries → empty string). No IO — the exact output
    is pinned in tests.
- **`Commits/`** — the T-31 conventional-commit engine (pure, unit-pinned; the *inverse* of T-28's
  `ChangelogGenerator`).
  - `ConventionalCommit.cs` holds the `ConventionalCommitDraft`
    (type/scope/description/body/breaking(+description)/co-authors/closes-issues) +
    `CommitValidationIssue` data types and the static `ConventionalCommitBuilder`: `Build` assembles a
    deterministic `type(scope)!: description` message (no `()` when scope empty, trailing `!` when
    breaking) with a blank-line-separated body and a `BREAKING CHANGE:` / `Closes …` (bare number →
    `#`-prefixed) / `Co-authored-by:` footer block (malformed "Name <email>" co-authors dropped);
  - `Validate` returns commitlint-style errors (missing/unknown type, empty description, malformed
    co-author) vs advisory warnings (subject > 72, breaking-without-description, trailing period,
    non-imperative first word);
  - `Parse` best-effort recovers a draft (reuses `ChangelogGenerator.ParseSubject` for the header +
    lifts the trailer lines), so `Parse(Build(d))` round-trips the stable fields. No IO/host/git types —
    output pinned in tests. **T-32 (blame → PR / issue):**
  - `IssueReferenceParser.cs` — the pure, unit-pinned extractor of issue references from a PR title/body
    (bare `#12` → the PR's own repo; cross-repo `owner/repo#7`; closing keywords
    `closes/fixes/resolves #n` carry no extra signal — the `#n` is captured like any mention; deduped by
    repo + number).
  - `ICommitContextProvider.cs` (internal per-host contract behind `ICommitContextService`,
    `IsImplemented` flag so the dispatch table is complete while unbuilt hosts report unsupported; uses
    the shared `Services.RepoSlug`), `GitHubCommitContextProvider.cs` (v1:
    `GET /repos/{o}/{r}/commits/{sha}/pulls` over the shared `Hosting/GitHubApiClient` transport — token
    in the `Authorization: Bearer` header only — maps the pulls → host-agnostic `PullRequestItem`s, then
    runs `IssueReferenceParser` over each PR's title+body → `LinkedIssues`;
    `// TODO(T-32 human-review): live blame-to-PR`), `StubCommitContextProviders.cs`
    (`UnsupportedCommitContextProvider` base + GitLab/Bitbucket/Azure DevOps stubs that throw a typed
    "not yet supported for <host>" and report `IsImplemented=false`).
- **`Security/`** — `SecureKeyring.cs` (OS keyring / DataProtection secret storage; T-14 added a
  storage-directory-override constructor for testability, `Retrieve` returns null on a corrupt/foreign
  payload; the key ring is DPAPI-wrapped on Windows and Keychain-wrapped on macOS),
  `MacKeychainKeyProtection.cs` (the macOS DPAPI analogue: the DataProtection key XML is
  AES-256-GCM-encrypted with a master key living ONLY in the login Keychain, accessed through
  `/usr/bin/security` so the item's ACL names the Apple-signed CLI and ad-hoc-rebuilt dev
  binaries never trigger per-build prompts; fail-open to the previous plaintext posture when the
  Keychain is unavailable — secrets must survive, hardening is best-effort),
  `GitHostDetector.cs` + `Models/HostKind.cs` (classify a remote as GitHub/GitLab/etc.;
  `UsernameForToken` is the **single source** for the host→token-username convention; `ParseOwnerRepo`
  extracts the `owner/repo` slug from a remote URL — HTTPS/ssh/scp forms, `.git`-stripped, subgroups
  folded into owner — for the T-23 PR API), `SshKeyService.cs` (T-14 SSH key manager: generate ed25519
  via `ProcessStartInfo.ArgumentList` — never a shell string — list `~/.ssh` keys, copy public key,
  passphrase stored `sshpass_<sanitized-keypath>` in the keyring; the keygen `-N` passphrase is an
  argv element only because keygen is a *local* op, never on any network path),
  `CredentialResolver.cs` (T-14 single-source credentials picker: SSH-form remotes →
  `SshUserKeyCredentials` value object with key paths + keyring passphrase; token remotes →
  LibGit2Sharp `UsernamePasswordCredentials`; the pinned libgit2 build has no SSH transport, so SSH
  ops still run through the git CLI), `SafeWebUrl.cs` (guard for URLs handed to the OS shell/browser:
  only absolute http/https pass — host-provided link fields must never launch a
  `file:`/UNC/custom-scheme handler). **P2-01 (BYOK key store):**
  - `ISecureKeyStore.cs` (the minimal `Set`/`Get`/`Delete` secret contract the Phase-2 daemon + P2-24
    backends implement; `SecureKeyring` now also implements it, delegating to
    `Save`/`Retrieve`/`DeleteSecret` — one storage path; LLM keys are keyed `llm_<provider>`),
    `ApiKeyHealthService.cs` (`KeyHealth` + the service: validates an API key at entry through an
    injected `HttpMessageHandler` seam so it is fully offline-testable — Anthropic `POST /v1/messages`
    with the key in `x-api-key`, OpenAI `GET /v1/models` with `Authorization: Bearer`; parses provider
    rate-limit headers into a conservative, monotonic `static readonly` agent-ceiling table; 401/403 →
    `IsValid=false` with the reason scrubbed of the key via `Http/RedactionExtensions`; transport
    failure/unknown provider → typed `GitOperationException`, honoring the `CancellationToken` — the key
    never lands in argv/log/exception), `CredentialInjector.cs` (pure, in-memory `BuildEnvFileContent` →
    newline-terminated `KEY=value` lines, throws `ArgumentException` on a `\n`/`\r` in a value; the
    P2-07 daemon writes it to tmpfs). **P2-22 (loopback OAuth + deep links):**
  - `Pkce.cs` (pure RFC 7636 S256 — `CreatePair` (32-byte CSPRNG verifier, base64url no-pad) +
    `ComputeChallenge`; the Appendix B vector is a test), `LoopbackOAuthListener.cs` (the ONE RFC 8252
    loopback+PKCE token flow every loopback-capable host uses — generates PKCE+`state` → opens the
    authorize URL through the injected `IBrowserOpener` → awaits one callback ≤ 5 min (single-use) via
    the `ILoopbackCallbackChannel` seam → constant-time `state` validation → hands
    `(code,verifier,redirectUri)` to the caller's host-specific `TokenExchange`; typed
    `LoopbackOAuthException`/`LoopbackOAuthError`; the verifier/token never enter any URL we build; a
    second loopback listener anywhere is a rejection trigger), `HttpListenerCallbackChannel.cs` (the
    real channel — ephemeral `127.0.0.1` port, first `/callback` captured + success page, every later
    hit `410 Gone`), `DeepLink.cs` (`mainguard://` **non-secret** deep links — `DeepLinkParser.Parse` →
    typed `DeepLinkCommand` (`OpenRepo`/`OpenPr`/`OpenAgent`) / `Ignored` (unknown verb) / `Rejected`
    (wrong scheme, malformed, OR any query/fragment key matching a secret pattern — invariant 1);
    `DeepLinkBuilder` takes only navigation ids, no token-typed input exists to place a secret in a
    link).
- **`Safety/`** — the T-30 pre-commit scanner's **pure** detection surface (no IO/git/Avalonia, so
  the whole finding set is unit-pinned): `PreCommitFinding.cs` (`FindingSeverity` Info/Warning/Blocker
  + `FindingKind` Secret/LargeFile/MergeMarker/DebugLeftover/ManyFiles/Other enums + the
  `PreCommitFinding` record — **INVARIANT: its `Message` is rule-name + `path:line` only and NEVER
  contains the matched secret value**), `SecretPatterns.cs` (the named-regex catalog — AWS
  access-key-id/secret, GitHub token `gh[psuor]_`/`github_pat_`, Google API key, Slack token, PEM
  `PRIVATE KEY` block, JWT, and a generic high-entropy `secret/api_key/password = "…"` assignment
  guarded by a Shannon-entropy + placeholder filter; each rule's public surface is
  `bool IsMatch(line)` so a matched value can never be returned/echoed), and `PreCommitScanEngine.cs`
  (pure `Scan((path,content,isBinary,sizeBytes)…, PreCommitScanOptions)` → the ordered
  `PreCommitFinding` list: Secret/MergeMarker `^<<<<<<< / ^======= / ^>>>>>>>` at line-start only =
  Blocker, LargeFile over the `MaxFileBytes` cap (default 5 MB) + ManyFiles over the count threshold =
  Warning, optional DebugLeftover = Info; binaries flagged by size only; deterministic order =
  severity › path › line › rule). Consumed by `Services/PreCommitScanner`.
- **`Sync/`** — device-flow + multi-host auth (T-14): `DeviceFlowClient.cs` (reusable RFC-8628 OAuth
  device-flow engine + `DeviceFlowResponse`/`AccessTokenResponse`, injectable `HttpMessageHandler`
  seam), `GitHubAuthClient.cs` (thin GitHub facade over `DeviceFlowClient` preserving the Clone
  Dashboard's in-screen GitHub device-flow sign-in — repo listing moved to the host-agnostic
  `Services/HostRepositoryService` in P2-48), `IHostProvider.cs` (`IHostProvider` contract + the
  `HostAuthMethod` enum {OAuthDeviceFlow, OAuthLoopback, PersonalAccessToken} + `HostAuthContext` UI
  callbacks — `PresentDeviceCode`/`PromptForPat` plus the P2-22-Q1 loopback seams
  `BrowserOpener`/`LoopbackChannelFactory` — + `HostProviderBase` whose `SupportsDeviceFlow` is
  derived from `AuthMethod == OAuthDeviceFlow` and whose `TokenUsername` delegates to
  `GitHostDetector.UsernameForToken`), `GitHubProvider.cs` (device flow — GitHub OAuth apps don't
  support PKCE loopback, so it stays on RFC 8628), `GitLabProvider.cs` (**P2-22 Q1: loopback OAuth +
  PKCE** via `OAuthLoopbackClient`; placeholder public-client
  `DefaultClientId = "mainguard-gitlab-loopback"` — owner registers the real GitLab OAuth app + a
  `127.0.0.1` redirect and passes the id via the `clientId` ctor arg/settings),
  `OAuthLoopbackClient.cs` (**P2-22 Q1** — the reusable authorization-code+PKCE client +
  `OAuthClientConfig` record; **composes** the shared `Security/LoopbackOAuthListener` — no second
  listener — and contributes only the `authorization_code` token-exchange POST behind an injectable
  `HttpMessageHandler` seam, mirroring `DeviceFlowClient`; the verifier travels only in the HTTPS
  body), `PatHostProviders.cs` (`PatHostProviderBase` +
  `BitbucketProvider`/`AzureDevOpsProvider`/`GenericHostProvider` — `PersonalAccessToken` dialog),
  `HostProviderRegistry.cs` (`Resolve(host, HostKind)` → the right provider). Live
  device-flow/loopback-OAuth/PAT/SSH round trips are deferred to the manual matrix
  (`// TODO(T-14/P2-22 Q1 human-review)`).
- **`Hosting/`** — `GitHubApiClient.cs` — the **one audited GitHub REST v3 transport** shared by the
  GitHub PR (T-23), issue (T-24), checks (T-26), notifications (T-27), and release (T-28) providers (a
  second HTTP/token path is a rejection trigger).
  - `SendAsync` puts the token only in the per-request `Authorization: Bearer` header (never
    `DefaultRequestHeaders`, since the `HttpClient` is shared); success bodies returned,
    non-success/host/network → typed `GitOperationException`/`AuthenticationRequiredException`
    (401→auth-required with host, 403 rate-limit special-cased) with any host text scrubbed of the token
    via static `Redact`; host-error phrasing kept generic so the host's own message (e.g. "already
    exists", "could not add label") surfaces. Static `Esc`/`Deserialize` helpers. No Avalonia. The
    token-scrub `Redact` itself now lives in `Http/RedactionExtensions.cs` (P2-01) —
    `GitHubApiClient.Redact` is a thin pass-through so its call sites don't churn.
  - `GitLabApiClient.cs` (P2-48) — the **GitLab REST v4 counterpart**, same audited
    `SendAsync`/`Redact`/`Deserialize` shape (Bearer-only header, typed+scrubbed errors,
    `ApiBaseForHost(host)` → `https://{host}/api/v4` so a self-hosted instance queries its own origin).
  - `IHostRepositoryProvider.cs` + `GitHubRepositoryProvider.cs` / `GitLabRepositoryProvider.cs` /
    `UnsupportedRepositoryProviders.cs` (Bitbucket/AzDO stubs) — the per-host **"list my repositories"**
    adapters behind `HostRepositoryService` (P2-48); each maps its host JSON to the host-agnostic
    `RemoteRepository` (G-10 — the DTO shapes never leave the file).
- **`Http/`** — `RedactionExtensions.cs` — the **single sanctioned** secret-scrub helper
  (`Redact(text, secret)` → replaces the secret with `***`; a second copy of token-scrub logic is a
  rejection trigger). Shared by `GitHubApiClient` (delegating) and the P2-01 `ApiKeyHealthService`.
  `internal static`, no host/Avalonia types. (Distinct from `GitServices.RedactUrlCredentials`, which
  scrubs `user:pass@` out of git-CLI stderr — different logic, not a token-scrub copy.)
- **`PullRequests/`** — the T-23 pull-request provider adapters (internal, behind
  `IPullRequestService`; extended by T-25 review): `IPullRequestProvider.cs` (the `internal` provider
  contract — five PR ops + the three T-25 review ops
  (`GetReviewsAsync`/`GetReviewCommentsAsync`/`SubmitReviewAsync`) + `IsImplemented` flag so the
  dispatch table is complete while unbuilt hosts report unsupported; uses the shared
  `Services.RepoSlug`), `GitHubPullRequestProvider.cs` (v1: REST v3 list/get/create/merge/close plus
  T-25 `pulls/{n}/reviews` (GET list — dropping the empty-bodied PENDING bookkeeping entry — + POST
  submit mapping `ReviewVerdict`→`COMMENT|APPROVE|REQUEST_CHANGES`) and `pulls/{n}/comments` (GET
  inline comments; `line==null` ⇒ outdated) over the shared `Hosting/GitHubApiClient` transport —
  token in the `Authorization: Bearer` header only; maps JSON → the host-agnostic models;
  `// TODO(T-23/T-25 human-review): live PR / review matrix`), `StubPullRequestProviders.cs`
  (`UnsupportedPullRequestProvider` base + GitLab/Bitbucket/Azure DevOps stubs that throw a typed "not
  yet supported for <host>" — incl. the three review ops — and report `IsImplemented=false` — adding a
  real one is additive). No Avalonia.
- **`Issues/`** — the T-24 issue-tracking provider adapters (internal, behind `IIssueService`),
  sibling of `PullRequests/`: `IIssueProvider.cs` (the `internal` five-op contract —
  list/get(+comments)/create/comment/set-state — + `IsImplemented`; uses the shared
  `Services.RepoSlug`), `GitHubIssueProvider.cs` (v1: REST v3 over the shared
  `Hosting/GitHubApiClient`; **CRITICAL** — GitHub's `/issues` endpoint also returns PRs, so every
  item carrying a `pull_request` object is filtered OUT of the issue list; token in the
  `Authorization: Bearer` header only; maps JSON → the host-agnostic models;
  `// TODO(T-24 human-review): live issues matrix`), `StubIssueProviders.cs`
  (`UnsupportedIssueProvider` base + GitLab/Bitbucket/Azure DevOps stubs, `IsImplemented=false`). No
  Avalonia.
- **`Notifications/`** — the T-27 notifications provider adapters (internal, behind
  `INotificationService`), sibling of `Checks/`/`Issues/`/`PullRequests/`: `INotificationProvider.cs`
  (the `internal` three-op contract — list(`onlyUnread`) / mark-read(threadId) / mark-all — +
  `IsImplemented`; user-scoped, so **no** `RepoSlug` is passed), `GitHubNotificationProvider.cs` (v1:
  REST v3 over the shared `Hosting/GitHubApiClient`; `GET /notifications?all={!onlyUnread}` maps
  `reason`+`subject.type` via the pure `NotificationMapper` and best-effort-converts the API
  `subject.url` → web URL (api host swap, `/repos` dropped, `/pulls|/commits` singularized; unlinkable
  subject → empty URL), `PATCH /notifications/threads/{id}`, `PUT /notifications` (`{"read":true}`);
  token in the `Authorization: Bearer` header only;
  `// TODO(T-27 human-review): live notifications matrix`), `StubNotificationProviders.cs`
  (`UnsupportedNotificationProvider` base + GitLab/Bitbucket/Azure DevOps stubs,
  `IsImplemented=false`). No Avalonia.
- **`Checks/`** — the T-26 CI/checks provider adapters (internal, behind `ICheckStatusService`),
  sibling of `Issues/`/`PullRequests/`: `ICheckProvider.cs` (the `internal` two-op contract —
  get-checks / re-request — + `IsImplemented`; uses the shared `Services.RepoSlug`),
  `GitHubCheckProvider.cs` (v1: REST v3 over the shared `Hosting/GitHubApiClient`; merges
  `GET commits/{sha}/check-runs` (Actions/apps, re-runnable id) + `GET commits/{sha}/status` (legacy
  combined status, no id) into one `CommitChecks`, **de-duplicated by name with the check-run
  winning**; `POST check-runs/{id}/rerequest` re-runs; token in the `Authorization: Bearer` header
  only; JSON → host-agnostic models via `CheckStateMapper`;
  `// TODO(T-26 human-review): live checks matrix`), `StubCheckProviders.cs`
  (`UnsupportedCheckProvider` base + GitLab/Bitbucket/Azure DevOps stubs, `IsImplemented=false`). No
  Avalonia.
 - **`Releases/`** — the T-28 releases provider adapters (internal, behind `IReleaseService`),
   sibling of `Issues/`/`Checks/`: `IReleaseProvider.cs` (the `internal` two-op contract — list /
   create — + `IsImplemented`; uses the shared `Services.RepoSlug`), `GitHubReleaseProvider.cs` (v1:
   REST v3 over the shared `Hosting/GitHubApiClient`; `GET /repos/{o}/{r}/releases` (maps
   draft/prerelease/name-fallback/null-published) + `POST /repos/{o}/{r}/releases`
   (`{ tag_name, target_commitish?, name, body, draft, prerelease }` — `target_commitish` omitted when
   blank); token in the `Authorization: Bearer` header only; JSON → host-agnostic models;
   `// TODO(T-28 human-review): live release matrix`), `StubReleaseProviders.cs`
   (`UnsupportedReleaseProvider` base + GitLab/Bitbucket/Azure DevOps stubs, `IsImplemented=false`). No
   Avalonia.
- **`Exceptions/`** — the typed exception hierarchy (`MainguardException` base;
  `AuthenticationRequiredException` — carries an optional `Host` (T-14) so the UI routes an
  unknown-host-no-token failure straight to that host's PAT dialog; `MergeConflictException`,
  `GitOperationException`, `SshAuthenticationException`, `RemoteNotFoundException`,
  `GitIdentityMissingException`, `UndoBlockedException` — T-19 undo/redo refusal: dirty tree,
  non-undoable entry, or truncated redo; `DuplicateProfileNameException` — T-21 profile name already
  in use; `BranchNotMergedException` — carries `BranchName`; the `git branch -d` refusal raised by
  `DeleteBranch(force: false)` on a branch merged into neither HEAD nor its upstream, so the UI can
  offer an explicit "delete anyway" instead of orphaning the commits; `AmendPushedCommitException` —
  carries `BranchName`/`UpstreamName`; refuses an amend of a HEAD the upstream already contains, so
  the amend checkbox can never silently diverge a branch; `BootstrapException` — P2-05 bootstrap-step failure, carries the failing `StepName`;
  `WslNotInstalledException` — WSL2 absent, actionable (points at the P2-21 installer enablement; the
  bootstrapper never runs `wsl --install`); `WslCommandException` — a non-zero `wsl.exe` exit, carries
  exit code + stderr; `RepoProvisioningException` — P2-06 daemon-side git failure
  (clone/fetch/config/worktree/remote) from `AgentGitCommand`, carries the already-redacted stderr;
  `AgentWorktreeConflictException` — P2-06 typed refusal thrown before any mutation: duplicate
  `agent/<id>` branch/path, or a non-forced dirty removal; `AgentBranchMissingException` — its exact
  mirror image, for the RESUME path: `AdoptAgentWorktree` was asked to start a jail on an existing
  `agent/<id>` and the mirror has no such branch, so there is nothing to resume. A refusal and never a
  fallback: creating the branch instead would report success for an operation that recovered no commits
  at all. Carries `(RepoHash, AgentId, Branch)` — the pair, because an id is unique per repo, not
  globally; `AgentBranchRescueFailedException` — same file, the OTHER half of that resume: the rescue
  publish that carries commits out of the dead jail's own repository failed for a transient reason
  (`AgentRefPublishOutcome.Failed` — unreadable repo, races, disk), and the adoption is refused rather
  than allowed to proceed into `ClearWorktreeResidue`, which deletes the only copy. Deliberately NOT
  raised for a *refused* publish (a non-fast-forward is permanent, so refusing there would strand the
  agent forever rather than once). Carries `(RepoHash, AgentId, Reason)`;
  `SandboxSpecException` — P2-07
  hardened-spec violation raised at construction (Windows/UNC mount source, a dropped G2 control, or a
  secret in `Env`); `GitProxyRefusedException` — P2-07 A6 daemon-git-proxy refusal (non-fetch service
  or non-allowlisted prefix); `DeclaredDependencyDeniedException` — P2-07 F5 out-of-scope module
  fetch; `GitMutationLockException` — P2-09 keep-alive worktree `index.lock` stayed held across the
  bounded backoff cap (the agent is yielded/paused, so a persistent lock is a typed failure, not
  retried forever); `SandboxImageMissingException` — v1 spawn-preflight refusal: a required jail image
  (`mainguard-agent-base`/`mainguard-egress-proxy`) is absent from the VM's docker store (fresh import
  / post-tier-2-upgrade), thrown before any worktree/jail work, names exactly the missing image(s) +
  the repair; mapped to `FailedPrecondition` in `AgentGrpcService.SpawnAgent`;
  `ToolchainProvisioningException`/`UnknownToolchainException` — MG-42 per-repo verification-toolchain
  failure, never a degrade (a jail missing its declared toolchain exits 127 and reads like the agent's
  code is broken); `PackageCacheException` + `PackageCacheUnavailableException` /
  `PackageCacheOverBudgetException` — MG-43 daemon-owned package cache: it could not be created, the
  started container cannot write it, or the root is over budget with only live-jail caches left to
  evict. All three stop the spawn rather than falling through to a jail whose restore fills the 256
  MiB tmpfs `$HOME` and dies at `ENOSPC` — which the merge queue would record as an ordinary failed
  verification). Throw these from Core; catch in ViewModels to drive dialogs.
- **`Migrations/`** — generated EF migrations + `AppDbContextModelSnapshot.cs`. Never hand-edit an applied one.
- **`Audit/`** (P2-02, P2-15 in progress) — the G-17 audit seam and the P2-15 tamper-evident chain: `IAuditLog.cs` (append/read + the flat `AuditEvent(Type, Fields)` record; deliberately narrow — P2-15 lands behind this same interface), `InMemoryAuditLog.cs` (thread-safe in-memory journal; the test/no-DB-fallback implementation), `HashChain.cs` (P2-15 pure chain: the `AuditRecord(Seq, Timestamp, Type, PayloadJson, PrevHash, Hash)` record, `ComputeHash` = SHA-256(prevHash ‖ canonicalPayload) lowercase hex, `Verify` walking seq contiguity + linkage + recomputed hashes, first-bad-seq exact; mid-chain slices anchor on their first record, seq 1 pins to `GenesisHash`), `CanonicalJson.cs` (the one serialization the chain hashes: ordinal-sorted keys recursively, invariant culture, equivalent-number collapse, UTF-8 no BOM — hashing non-canonical JSON is a spec rejection trigger), `IChainedAuditLog.cs` (the P2-15 contract surface over the narrow seam: `Append(type, payload, osIdentity)→seq`, `Read(fromSeq, take)`, `VerifyAll`, `Redact`), `ChainedAuditLog.cs` (the tamper-evident implementation: hashes the canonical ENVELOPE `{identity, payload, seq, timestamp, type}` so a flipped timestamp/type/seq column fails verify; AES-GCM payloads at rest; DB commits first, mirror second, with a test-only fault seam between; `VerifyAll` = linkage + recomputed hashes + column↔envelope cross-check + redaction vouching + mirror comparison; `Append` THROWS on store failure — the kill switch's RT-D3 gap path depends on that; `ApplyRetention` = redaction, never deletion, redaction events exempt), `AuditCrypto.cs` (AES-256-GCM, master key `audit-payload-key` in the OS keyring via `ISecureKeyStore`, generated on first use; nonce‖tag‖ciphertext blobs, `KeyId` stamped per row), `AuditFileMirror.cs` (the payload-FREE append-only file mirror — length-prefixed canonical-JSON chain columns, fsync'd; `Recover` repairs only torn/missing TAILS from DB truth and reports a content disagreement as a `ConflictSeq` instead of repairing it — the witness must not be quietly rewritten to match a tampered DB). **Read the remarks on `IAuditLog.Read` before adding an `Append` call site:** there are 28 `Append` calls across 13 production files and ZERO production readers — no RPC, no ViewModel — and the shipped implementation is in-memory, so an audited event is unreachable during and after the incident it describes. P2-15 is the plan and this interface is the seam it lands behind; until then an `Append` is evidence for a later investigation and never the user-visible record of anything, so any path that destroys work pairs it with a log line, a typed refusal or a UI notice.
- **`Services/`** — the service layer every ViewModel talks to. Interface-first:
  - `IGitService.cs` / `GitServices.cs` — the core git engine. **All** LibGit2Sharp access goes
    through `GitServices.ExecuteWithRepo(...)`, which owns the **bounded index.lock retry** (4 attempts,
    25/50/100 ms backoff on `LockedFileException` — raised before anything mutates, so the retry is
    safe; exhausted → typed `GitOperationException` naming `index.lock` and the way out; ADR-001, pinned
    by `GitServiceIndexLockTests`). Commit, stage, branch, tag, merge, rebase, stash, cherry-pick,
    reset, diff, history. **Git-directory resolution:** `ResolveGitDir` (via `Repository.Discover`,
    so it follows the `.git`-file indirection of a linked worktree exactly as git does),
    `ResolveCommonGitDir` (the `commondir` pointer — where `refs/` and `objects/` live) and the
    `GitDirPath(repoPath, …)` helper every per-worktree state read goes through. `Commit` takes an
    `amend` flag (keeps the original author, refuses an unborn branch and an already-published HEAD);
    `DeleteBranch` applies git's `-d` rule and refuses an unmerged branch unless forced.
    Remotes management (T-10): CRUD
    (`GetRemotes`/`AddRemote`/`RemoveRemote`/`RenameRemote`/`SetRemoteUrl`), the
    `ResolveRemoteName`/`GetDefaultRemoteName` resolver that replaced every hardcoded `"origin"`
    (tracked → origin → sole remote → typed `RemoteNotFoundException`), a remote-named `Fetch` overload,
    and the three CLI push options (`PushForceWithLease` — lease only, never bare `--force`; `PushTags`;
    `PushSetUpstream`). Blame (T-11): `GetBlame(repoPath, path, startingSha?)` → per-line `BlameLine`s
    (1-based line numbers, typed `GitOperationException` on a path missing at the revision) computed via
    `ExecuteWithRepo`, plus `InvalidateBlameCache(repoPath)`. File history (T-12): `GetFileHistory`
    (rename-following newest-first log via `Commits.QueryBy(path)`, with a first-parent fallback walk so
    a file deleted at HEAD still shows its past), `GetFileAtCommit` (blob text; typed throw on a missing
    path or a binary blob), `GetFileDiffBetweenCommits` (adjacent-version patch =
    `git diff a b -- path`). Diff quality (T-13): the `GetFileDiff(...,bool ignoreWhitespace)` overload
    (CLI `git diff -w`, `--cached` when staged — whitespace-only changes collapse to zero hunks; partial
    staging is disabled by the caller in that mode) and `GetBlobBytesAtCommit` (raw blob bytes, no
    binary rejection — the "before" image source). Submodules (T-16): `GetSubmodules` (reads
    `repo.Submodules` via `ExecuteWithRepo` → `SubmoduleItem`s, status via the pure
    `SubmoduleStatusMapper`, path-sorted) plus the CLI-driven mutations `UpdateSubmodules`
    (`submodule update --init --recursive`), `UpdateSubmoduleRemote` (`update --remote <path>`),
    `SyncSubmodules` (`sync --recursive`) — production never sets `protocol.file.allow` (rejection
    trigger). Git LFS (T-17) lives in `LfsService`, which composes `GitService` via two internal seams
    (`RunGitCheckedForLfs` for local ops, `RunGitAuthenticatedForLfs` for `lfs pull`) so LFS network
    auth reuses the one audited authenticated CLI path. Commit/tag signing (T-15): an optional
    `Func<UserPreferences>` ctor lets the app feed live signing prefs; when `SignCommits` is on
    `Commit`/`CreateTag` switch to the CLI (`git commit`/`git tag -s`) after writing
    `commit.gpgsign`/`tag.gpgsign`/`gpg.format`/`user.signingkey`/`gpg.program` to **local** repo config
    (`ApplySigningConfig`) — GIT_TERMINAL_PROMPT=0 keeps a bad key from hanging;
    - `GetSignatureStatuses` batch-reads `%G?` via `SignatureStatusParser`;
    - `ListSigningKeys` enumerates gpg secret keys / `~/.ssh/*.pub` for the picker. Reflog (T-20):
      `GetReflog(repoPath, refName = "HEAD", take = 200)` reads a ref's reflog via `repo.Refs.Log(...)`
      (already most-recent-first) → `ReflogItem`s (from→to sha, first-line message, `Committer.When`);
      `refName` accepts `HEAD` / a friendly branch name / a canonical ref (resolved to a `Reference` so
      friendly names work, missing ref → typed `GitOperationException`, a ref with no reflog → empty
      list). The reflog's two recovery actions reuse the already-journaled `ResetToCommit` (restore = hard
      reset) and `CreateBranchAt` (create-branch-here = orphan-tip recovery), so both land in the T-19
      undo history. Check out a PR/branch into a worktree (T-29): the pure static
      `PullRequestHeadRef(HostKind, number)` (GitHub `pull/{n}/head`, GitLab `merge-requests/{n}/head`,
      others typed-unsupported),
      `CheckoutPullRequestWorktree(repoPath, prNumber, remoteName, worktreePath, ct)` (fetches the host's
      PR head into local branch `pr/<n>` via the authenticated CLI path — token via env, never argv — then
      reuses the T-07 `AddWorktree`; non-empty target → typed refusal, best-effort cleanup on failure so
      no half-made worktree; live real-GitHub round-trip deferred behind `// TODO(T-29 human-review)`),
      and `CheckoutBranchWorktree(repoPath, branchOrRef, worktreePath)` (reuses `AddWorktree`; a
      remote-tracking ref gets a local tracking branch created first).
    - `GetRemoteUrl` reads the **raw** `remote.<name>.url` config (not `Remotes[..].Url`) so a
      `url.insteadOf` transport rewrite never hides the user's real host from host/token detection.
  - `BlameCache.cs` — bounded LRU (~32 entries) keyed `(repoPath, path, headSha)` for T-11 blame results; invalidated per-repo on `RepositoryWatcher.RepositoryChanged`. Never unbounded (rejection trigger).
  - `AutoFetchService.cs` — background auto-fetch (T-10). One `PeriodicTimer` loop over the watched
    repo set fetches (prune) off the UI thread on the `UserPreferences.AutoFetchMinutes` cadence (0 =
    off); per-repo overlap guard, skip-while-operating, failures counted (`Fetched`/`FetchFailed`
    events, `GetLastFetched`). Concrete sealed class per the T-10 contract; no `DispatcherTimer` in Core
    (G-5). Cadence/clock are internal test seams (`IntervalOverride`/`Clock`) and `RunCycleAsync` runs
    one deterministic pass.
  - `IMergeDiffService.cs` / `MergeDiffService.cs` — pure 3-way merge chunker (strings in → ordered `MergeChunk`s out; no repo/IO). Consumed by the conflict-resolver UI (T-04).
  - `PatchParser.cs` / `PatchBuilder.cs` — pure unified-diff engine (T-06): parse/serialize (byte-identical round-trip) and build hunk/line subsets that feed the existing `StageHunk`/`UnstageHunk`/`DiscardHunk`. No repo/IO.
  - `WorktreePorcelainParser.cs` — pure parser for `git worktree list --porcelain` (T-07) → `WorktreeItem`s. Worktree ops are CLI-driven (libgit2 worktree API is a locked no).
  - `BranchTreeBuilder.cs` — pure tree-grouping helper (issue #71): turns a flat list of
    slash-delimited branch friendly names into a nested `BranchTreeNode` forest (a shared `prefix/`
    segment becomes a folder node, applied recursively for any deeper shared segments). No repo/IO —
    strings in, tree out. Consumed by `BranchBrowserViewModel.GroupIntoTree` to group the sidebar's
    Local/Remote/Recent branch lists by subfolder.
  - `RecentBranchResolver.cs` — pure helper (issue #70) that derives the sidebar's "Recent" branch
    ordering from HEAD's reflog (already most-recent-first `ReflogItem`s from `GetReflog`) instead of an
    alphabetical slice: parses `checkout: moving from <old> to <new>` messages, keeps the first distinct
    targets that still resolve to an existing local branch (a deleted branch or a detached-HEAD raw-SHA
    checkout is skipped), and fills any remaining slots from a caller-supplied fallback order. No
    repo/IO.
  - `SubmoduleStatusMapper.cs` — pure T-16 mapper from LibGit2Sharp's granular `SubmoduleStatus` flag set → the four-value `SubmoduleState` (precedence: Uninitialized › Modified › Dirty › UpToDate). The single tested place the flag semantics are interpreted; no repo/IO. Submodule mutations are CLI-driven (libgit2 submodule mutation is a locked no, like worktrees).
  - `LineHistoryFilter.cs` — pure T-12 line-history filter: keeps the file revisions whose adjacent-version diff touches a line range (old- or new-side hunk overlap), reusing `PatchParser`. Documented as a `git log -L` approximation; no repo/IO.
  - `IntraLineDiff.cs` — pure T-13 intra-line (word-level) diff engine: given the old/new text of a changed line pair, returns the changed character sub-ranges per side via DiffPlex `WordChunker`. Surrogate-safe (span boundaries snap outward off surrogate-pair midpoints). No repo/IO/Avalonia; feeds `GitDiffLine.HighlightSpans`.
  - `WhitespaceMarkers.cs` — pure T-13 trailing-whitespace detector: `TrailingWhitespace(line)` → the trailing-run `(Start,Length)` (whole line when all-whitespace, null when none). Feeds `GitDiffLine.TrailingWhitespaceSpan`.
  - `SignatureStatusParser.cs` — pure T-15 signing helper: maps git's `%G?` codes (G/B/U/X/Y/R/E/N) to `SignatureStatus` and parses batched `git log --format=%H|%G?|%GS` output into a SHA→`CommitSignatureInfo` map. No repo/IO; feeds the commit-timeline verification badges.
  - `ImageDiffDetection.cs` — pure T-13 image/binary helpers: `IsImageCandidate(path,isBinary)` (extension table {png,jpg,jpeg,gif,bmp,webp,ico}), `DiffIndicatesBinary`/`LooksBinary` (binary sniffing), `FormatBinarySummary(old,new)` (invariant-culture size summary). No repo/IO.
  - `ILfsService.cs` / `LfsService.cs` — Git LFS (T-17), entirely CLI-driven. Cached
    `git lfs version` availability probe (per repo); every method `EnsureAvailable` first and throws
    typed `GitOperationException("Git LFS is not installed.")` rather than attempting the op. Local ops
    via `GitService.RunGit`/an internal checked seam (`install`/`uninstall --local`, `track`/`untrack`,
    `ls-files`, `prune [--dry-run]` returns the summary line, `IsEnabledForRepo` reads
    `filter.lfs.smudge`); the one network op `Pull` (`lfs pull`) goes through the **T-14 authenticated
    CLI path** (`GitService.RunGitAuthenticatedForLfs` → `RunGitCheckedAuthenticated`) so a token never
    lands in argv/URL (G-4).
    - `LfsService` composes the concrete `GitService` so that authenticated path lives in one audited
      place; an internal `AvailabilityOverride` test seam lets the degrade path be tested where git-lfs
      exists. Parsing is delegated to the pure parsers below.
  - `LfsPointer.cs` — pure T-17 LFS pointer helpers: `IsPointer(content)` (true iff the first line is exactly `version https://git-lfs.github.com/spec/v1`; malformed/partial → false), plus `ParseSize`/`ParseOid`. Feeds the diff viewer's "LFS object (size)" summary. No repo/IO.
  - `LfsLsFilesParser.cs` — pure T-17 parser for `git lfs ls-files` (`<oid> <*|-> <path>` → `LfsFile`s; status char = content-present vs pointer-only, path is the remainder verbatim so spaces survive). No repo/IO.
  - `LfsAttributesParser.cs` — pure T-17 parser: the `filter=lfs` patterns in a `.gitattributes` (first token per matching line; decodes `[[:space:]]`). No repo/IO.
  - `IPreCommitScanner.cs` / `PreCommitScanner.cs` — T-30 pre-commit safety scanner.
    `ScanStaged(repoPath[, options])` enumerates the STAGED change set (index vs HEAD's tree via
    `ExecuteWithRepo`; added/modified/renamed only), reads each staged blob (size check BEFORE
    `GetContentText` so a blob over the cap is never slurped; binaries are flagged by size only, never
    text-scanned), and feeds the pure `Safety/PreCommitScanEngine`. No network, no CLI. Thresholds come
    from `UserPreferences` (`PreCommitMaxFileMB`, `PreCommitScanEnabled`).
  - `ISettingsService.cs` / `SettingsService.cs` — user preferences + workspace/category persistence via `AppDbContext`.
  - `RepositoryWatcher.cs` — `FileSystemWatcher` wrapper that raises change events so the UI can
    refresh. Watches the working tree **plus** any git root outside it: in a linked worktree the
    per-worktree gitdir and the shared common dir both sit elsewhere, so watching the tree alone
    saw working-tree edits only and missed every commit/checkout/stage/rebase step. Roots come from
    `GitService.ResolveGitDir` + `ResolveCommonGitDir` and are matched longest-first, so a
    per-worktree `HEAD` is not misread as `worktrees/<name>/HEAD` under the common dir. An ordinary
    repo is unchanged — `.git` is already inside the watched tree, so no extra watcher is created.
    Metadata classification also has a same-namespace textual `.git/` fallback: the resolved roots
    are canonical (libgit2 resolves symlinks), but events arrive in the namespace of the path the
    watcher was given, so a repo reached through a symlink (macOS's `/var` temp dir, any symlinked
    checkout) would otherwise see `index.lock` churn as a working-tree change and refresh mid-op.
  - `FileSystemPaths.cs` — the path-comparison policy: `OrdinalIgnoreCase` on Windows **and**
    macOS (NTFS/APFS are case-insensitive), `Ordinal` on Linux. Purely textual — symlink identity
    still needs one namespace or canonical paths; used by `RepositoryWatcher` prefix checks.
  - `IInteractiveRebaseService.cs` / `InteractiveRebaseService.cs` — interactive rebase sequence
    controller. All rebase-state reads go through `GitService.GitDirPath`, never
    `Path.Combine(repoPath, ".git", …)`.
  - `IPinnedRefService.cs` / `PinnedRefService.cs` — per-repo pinned branches/tags (T-09), persisted via `AppDbContext`; pinned refs order first into the commit-graph router (left-most lanes).
  - `IProfileService.cs` / `ProfileService.cs` — switchable Git identity profiles (T-21). SQLite
    CRUD via `AppDbContext` (case-insensitive unique name → typed `DuplicateProfileNameException` on
    clash) plus **cancel-safe delete** (`Delete` returns the removed snapshot preserving its id;
    `Restore` re-inserts it — the Undo path). `Apply(repoPath, profile)` writes `user.name`/`user.email`
    (and, when the profile signs,
    `commit.gpgsign`/`tag.gpgsign`/`gpg.format`/`user.signingkey`/`gpg.program`) to the repo's **local**
    config only (never global/system) — the one git touch-point goes through `ExecuteWithRepo`.
  - `ICloneService.cs` / `CloneService.cs` — clone with live progress + cancellation (T-21) over
    LibGit2Sharp `Repository.Clone`. `CloneAsync(url, target, IProgress<CloneProgress>?, ct)` reports a
    **monotonic** `ReceivedObjects`/`Percent` (receive weighted 0–90%, checkout 90–100%) via
    `CloneOptions.FetchOptions.OnTransferProgress`/`OnCheckoutProgress`; cancellation is honoured inside
    the transfer callback (return false) after which the **partial destination directory is deleted**
    and `OperationCanceledException` thrown; a non-empty destination throws typed
    `GitOperationException`. Private HTTPS clones resolve credentials through the single-source
    `CredentialResolver` (token never in URL/argv — G-4); the pinned libgit2 has no SSH transport.
  - `IHostRepositoryService.cs` / `HostRepositoryService.cs` — host-agnostic **"list my
    repositories"** service (P2-48), the lister behind the Clone Dashboard. Resolves a host's
    `token_<host>` from the keyring (with the legacy `github_token` GitHub fallback — the Accounts
    scheme), computes `IsSupported(host, kind)` (host has an *implemented* provider **and** a token),
    and dispatches to an internal `Hosting/IHostRepositoryProvider` over a **shared/injected
    `HttpClient`** (never per-call `new`). `ListMyRepositoriesAsync(host, kind, ct)` returns the
    account's repos as the host-agnostic `RemoteRepository` (all memberships, most-recently-updated
    first, capped at 100 — no paging): GitHub via `GitHubApiClient`
    (`/user/repos?sort=updated&per_page=100`), GitLab via `GitLabApiClient`
    (`/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at`, self-host-aware),
    Bitbucket/AzDO as typed `Unsupported*` stubs. Token only in the `Authorization` header (G-4); every
    failure typed; host JSON never leaks past the provider. Mirrors the `PullRequestService`/provider
    structure (but keyed off host, not a local repo — no `HostConnectionResolver`).
  - `IRepoDiscoveryService.cs` / `RepoDiscoveryService.cs` — local git-repository discovery (PR2
    repo onboarding): the service form of the sidebar's auto-detect folder scan
    (`UserPreferences.AutoDetectPath`). A root that is itself a repo returns as the single result;
    otherwise the same two-level walk as `ScanAutoDetectFolderAsync` (top-level dirs + one grouping
    level down), each tested with `IGitService.IsGitRepository`; path-sorted stable output, never throws
    on a missing/unreadable directory (unreadable branches are skipped).
    - `IsGitRepository` is passed through so the OOBE's individual-pick validation needs only this one
      seam.
  - `HostConnectionResolver.cs` — the **shared** origin-host + `token_<host>` + `owner/repo` slug
    resolver used by **both** `PullRequestService` (T-23) and `IssueService` (T-24), so there is exactly
    one host/token/slug path (a duplicate resolver is a rejection trigger).
    - `TryResolveHost` (origin → default → sole remote → `GitHostDetector.Detect`; never throws, so
      `IsSupported` degrades gracefully), `TokenFor` (keyring `token_<host>`), and static `ParseSlug` (→
      the shared internal `RepoSlug(Owner, Name)` record defined here — moved out of `PullRequests/`).
      Per-host provider dispatch stays in each service. No token ever leaves as anything but a header arg
      (G-4).
  - `IPullRequestService.cs` / `PullRequestService.cs` — host-agnostic pull/merge request service
    (T-23, extended by T-25). Resolves origin host + token + `owner/repo` via the shared
    `HostConnectionResolver`, computes `IsSupported` (host has an *implemented* provider **and** a
    token), and dispatches to an internal `PullRequests/IPullRequestProvider` over a **shared/injected
    `HttpClient`** (never per-call `new`). T-25 adds the review surface —
    `GetReviewsAsync`/`GetReviewCommentsAsync`/`SubmitReviewAsync` — through the same resolve+dispatch
    path. The token flows to the provider and lives only in the `Authorization` header — never a
    URL/argv/log/exception (G-4); every failure is typed
    (`GitOperationException`/`AuthenticationRequiredException`). Core stays UI-free; host JSON never
    leaks past the provider.
  - `IIssueService.cs` / `IssueService.cs` — host-agnostic issue-tracking service (T-24), the
    sibling of `PullRequestService`. Same shape via the **same** `HostConnectionResolver` (no duplicate
    host/token/slug resolver) and a **shared/injected `HttpClient`**; computes `IsSupported`, and
    dispatches to an internal `Issues/IIssueProvider` (GitHub v1; GitLab/Bitbucket/AzDO stubs). Token
    only in the `Authorization` header (G-4); every failure typed; host JSON confined to the provider.
  - `ICommitContextService.cs` / `CommitContextService.cs` — host-agnostic **blame → PR / issue**
    service (T-32), the sibling of `PullRequestService`/`IssueService`. Same shape via the **same**
    `HostConnectionResolver` (no duplicate host/token/slug resolver) and a **shared/injected
    `HttpClient`**; computes `IsSupported`, and dispatches to an internal
    `Commits/ICommitContextProvider` (GitHub v1; GitLab/Bitbucket/AzDO stubs).
    `GetForCommitAsync(repoPath, sha, ct)` → a `CommitContextResult` (the PR(s) that contain the commit
    + the issues those PRs link). Token only in the `Authorization` header (G-4); every failure typed;
    host JSON confined to the provider. **Live fetch DEFERRED** (`// TODO(T-32 human-review)`); offline
    slice is fixture-tested.
  - `ICheckStatusService.cs` / `CheckStatusService.cs` — host-agnostic CI / checks-status service
    (T-26), the sibling of `IssueService`/`PullRequestService`. Same shape via the **same**
    `HostConnectionResolver` (no duplicate host/token/slug resolver) and a **shared/injected
    `HttpClient`**; computes `IsSupported`, and dispatches to an internal `Checks/ICheckProvider`
    (GitHub v1; GitLab/Bitbucket/AzDO stubs). `GetChecksAsync(sha)` returns the merged `CommitChecks`;
    `RerequestAsync(checkRunId)` re-runs one check. Token only in the `Authorization` header (G-4);
    every failure typed; host JSON confined to the provider. **Live fetch + re-run against a real host
    account are deferred to the T-26 manual matrix** (`// TODO(T-26 human-review)`).
  - `INotificationService.cs` / `NotificationService.cs` — host-agnostic notifications-inbox service
    (T-27), the sibling of `CheckStatusService`/`IssueService`/`PullRequestService`. Same shape via the
    **same** `HostConnectionResolver` (no duplicate host/token resolver) and a **shared/injected
    `HttpClient`**; computes `IsSupported`, and dispatches to an internal
    `Notifications/INotificationProvider` (GitHub v1; GitLab/Bitbucket/AzDO stubs). Notifications are
    the **authenticated user's** (scoped only by the origin-host token — no `owner/repo` slug):
    `ListAsync(onlyUnread)` returns the threads, `MarkReadAsync(threadId)` / `MarkAllReadAsync()` clear
    unread. Token only in the `Authorization` header (G-4); every failure typed; host JSON confined to
    the provider. **Live fetch + mark-read against a real host account are deferred to the T-27 manual
    matrix** (`// TODO(T-27 human-review)`).
  - `IReleaseService.cs` / `ReleaseService.cs` — host-agnostic releases service (T-28), the sibling
    of `IssueService`/`PullRequestService`. Same shape via the **same** `HostConnectionResolver` (no
    duplicate host/token/slug resolver) and a **shared/injected `HttpClient`**; computes `IsSupported`,
    and dispatches list/create to an internal `Releases/IReleaseProvider` (GitHub v1;
    GitLab/Bitbucket/AzDO stubs). `GenerateNotes(newTag, targetCommitish)` is **local-only** — via
    `IGitService.ExecuteWithRepo` it finds the previous release tag (highest semver-ish tag reachable
    from the target, or none → whole history), walks `prevTag..target`, and runs the pure
    `Analytics/ChangelogGenerator` — **no network**. Token only in the `Authorization` header (G-4);
    every failure typed; host JSON confined to the provider. **Live publish against a real host account
    is deferred to the T-28 manual matrix** (`// TODO(T-28 human-review)`).
  - `NotificationMapper.cs` — the **pure, unit-pinned** T-27 mapper: `MapReason(reason)` + `MapSubjectKind(subject.type)` turn a host notification's dialect into the `NotificationReason`/`NotificationSubjectKind` enums (case/whitespace-insensitive; unrecognized → `Other`). The single tested place those strings are interpreted (mirrors `CheckStateMapper`); no IO/host types.
  - `CheckStateMapper.cs` — the **pure, unit-pinned** T-26 roll-up: maps a check-run
    (`status`+`conclusion`) or a legacy commit `status` (`state`) to `CheckState`, and reduces a run set
    to one `Overall` verdict (**Failure** dominates → else **Pending** → else **Success**;
    Neutral/skipped ignored; `action_required`/`timed_out`/`cancelled`/`stale`/`error` → Failure).
    `Rollup(sha, runs)` builds the `CommitChecks` with pass/fail/pending counts (empty → `HasAny=false`,
    i.e. "no CI", never a fail). No IO/host types.
  - `IOperationJournal.cs` / `OperationJournal.cs` — the T-19 operation journal (unlimited
    undo/redo). `BeginOperation(repoPath, kind, description, undoBlockedReason?)` returns an
    `IDisposable` scope that snapshots every direct ref + HEAD symbolic target (a `RefSnapshot`: refs
    map, head, detached flag, tree-dirty flag, local-branch upstream config) on create and again on
    dispose, persisting a `JournalEntry`. Every mutating `GitService`/`InteractiveRebaseService` method
    wraps itself in `using var op = _journal.BeginOperation(...)`; the pre/post snapshots run in
    short-lived `ExecuteWithRepo` handles that never overlap the mutation's handle (index.lock-safe),
    via an internal journal-free `GitService` accessor so snapshotting never recurses.
    - `Undo` restores the pre-state refs via `Refs.UpdateTarget/Add/Remove` (HEAD repointed with the
      symbolic-`Reference` overload, not a string, so it stays attached), then reset the worktree (mixed
      for a commit/amend undo, hard otherwise) only after a dirty-tree guard that refuses (typed
      `UndoBlockedException`, mutating nothing) when uncommitted work would be clobbered; branch-delete
      undo also recreates upstream config.
    - `Redo` re-applies the post-state; a new op after an undo truncates the redo stack (`IsTruncated`).
      Non-undoable ops (push, pull, stash pop/apply/drop, remote-branch delete) are journaled + flagged
      with a reason, never dropped.
    - `JournalKinds` holds the shared kind constants;
    - `NullOperationJournal` is the zero-cost default so `new GitService()` call sites are
      behavior-preserving.

- Scratch/placeholder (ignore, safe to delete): `Class1.cs`, `Services/Test.cs`.

## Role in the solution

- **`Mainguard.Git`** (step 2a) — the pure **git engine + persistence base** shared by every
  edition. `IGitService`/`GitServices` (all LibGit2Sharp behind `ExecuteWithRepo`), commit-graph
  routing, diff/blame/patch, git `Models/`, EF `AppDbContext` + `Migrations/`, `ISettingsService`,
  host providers, security/keystore, pre-commit scan + review risk-scoring. **A clean leaf: NO
  Docker.DotNet / Porta.Pty / gRPC / agent dependency**; carries LibGit2Sharp / EF Core / DiffPlex /
  DataProtection. Root namespace `Mainguard.Git.*`. Referenced by Mainguard.Agents and every consumer;
  grants `Mainguard.Agents`/`Mainguard.Tests` `InternalsVisibleTo` for low-level git helpers. Prefer
  putting NEW pure git/persistence logic here.
  - `Services/` (interface-first — everything except the human-gated `ForegroundMergeService`), `Models/`, `Analytics/`, `Commits/`, `Graph/`, `Migrations/`, `Security/`, `Sync/`, `Hosting/`, `Http/`, `PullRequests/`, `Issues/`, `Checks/`, `Notifications/`, `Releases/`, `Review/`, `Safety/`, `Audit/`, `Actions/`, `Exceptions/`, plus root `AppDbContext`/`MainguardPaths`.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
