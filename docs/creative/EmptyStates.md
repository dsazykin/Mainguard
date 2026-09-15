# Mainguard Empty States

> **Strings:** the final headline/body copy for every state below now lives in [`Microcopy.md`](Microcopy.md) §6 — where a string here differs, Microcopy.md wins. This doc remains the source of truth for the layout, icon, kind, and motion of each state (and the shared ES card, §9).

**Wave-2 creative doc.** The empty / zero / first-run state for every surface in the app. This conforms to [`DESIGN.md`](../../DESIGN.md) (tokens, shape, type, motion), [`PRODUCT.md`](../../PRODUCT.md) (register, principles), and cites the numbered rules in [`Mainguard_Voice_And_Delight_Bible.md`](Mainguard_Voice_And_Delight_Bible.md). Every surface below is a real view/window in the [`AGENTS.md`](../../AGENTS.md) Repository Map.

## How to read this

Every empty state is one of four kinds — and the difference is load-bearing, so each row names which it is:

- **`empty-yet`** — the thing genuinely doesn't exist yet (no commits, no remotes, no profiles). Headline states the plain absence (**ES-1**); one primary next step through the single `Button.Accent` (**ES-2**).
- **`not-connected`** — a capability that needs a host token/tool that isn't present. **This is an empty state, not an error** — no `DangerBrush`, no alarm (**ES-3**, **V-2**). It explains the capability and how to enable it.
- **`all-clear`** — a *healthy* emptiness worth a quiet affirmation: a clean working tree, inbox-zero, every check green (**ES-4**). Delight here is a single settle, never celebration.
- **`loading`** — data is on its way; a ghost/skeleton or an honest, monotonic indicator (**M-6**), never a fabricated number.

**Voice rails applied to every row.** Headlines use the Hero scale (24px / 600), the one place it appears (**DESIGN.md §3**, **ES pattern**). Body is one `TextMuted` line at 12–13px. Copy is precise over friendly (**V-1**), calm not alarmist (**V-2**), engineered not cute — no emoji, no "oops"/"we" (**V-3**). Git operations are lowercase-hyphenated; refs, SHAs and paths render in `TextBlock.Mono` (**N-6**). At most one `Button.Accent` per state (**The One Accent Rule**); secondary paths are `Button.Secondary` or plain links (**ES-2**). No cell references a raw hue — only named tokens.

**Motion rail.** Empty states fade in over 130–150ms, opacity only, no layout shift (**M-3**). The loom/weave delight is reserved for `all-clear` of a healthy repo (**ES-4**, **M-1**): a single thread drawn taut and *settling without overshoot* — no bounce, no confetti (**M-5**). `not-connected` and `loading` art stays **silent** (**M-2**); `loading` uses the ghost skeleton (**M-6**). A reduced-motion preference collapses every one of these to an instant state change (**M-7**).

---

## 1 · Shell & workspace core

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **MainWindow** (shell) | No repo open · `empty-yet` | `No repository open` | `Open a folder that's a Git repo, or clone one from a remote.` | `Button.Accent` **"Open repository"** (folder picker); `Button.Secondary` "Clone…" → `CloneDashboardView` | 56px repo/folder outline icon on `SurfacePanel` card; loom motif at rest, **silent** — first-run is orientation, not celebration (ES-1, ES-2, V-1) |
| **CloneDashboardView** | Idle, no URL entered · `empty-yet` | `Clone a repository` | `Paste an HTTPS or SSH URL to clone it into a local folder.` | `Button.Accent` **"Clone"** (disabled until a valid URL); `Button.Secondary` "Open existing folder" | Thread-spine motif down the card gutter, still; the *only* motion here is the honest monotonic clone bar once a clone starts (M-6, M-1) |
| **RepoDashboardView** (layout host) | Hosts a workspace with no repo bound · `empty-yet` | `No repository open` | `Open or clone a repository to see its staging, diff, and history.` | `Button.Accent` **"Open repository"** | Reuses the shell empty card (one shared component, §9); **silent** (ES-1) |

---

## 2 · Staging, commit & pre-commit

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **StagingPanelView** | Clean working tree, nothing to stage · `all-clear` | `Working tree clean` | `No changes to stage. Every edit is committed.` | None — this is the reward state, not a call to act | 48px settled-weave ring (a completed thread, `SuccessBrush`-toned via token, never raw green); a **single ~140ms fade** as the tree reaches clean — the whole celebration (ES-4, M-1, M-5) |
| **DiffViewerView** | No file selected · `empty-yet` | `Select a file to see its diff` | `Pick a changed file from the staging panel to view its hunks here.` | None — the action lives in the adjacent panel | 48px diff/hunk glyph in `TextMuted`, **silent** — the diff surface is an instrument readout (M-2, ES-1) |
| **PreCommitFindingsView** | Scan ran, zero findings · `all-clear` | `Nothing risky staged` | `No secrets, merge markers, or oversized files in this commit.` | None — proceed via the panel's existing **Commit** button | Quiet check-weave in `SuccessBrush` token; single fade, no fanfare. Distinct from the blocker banner, which is a plain statement carried by role, not louder words (ES-4, V-2, T-30) |
| **CommitComposerView** (structured mode) | Empty structured form, nothing typed · `empty-yet` | `Compose a commit` | `Pick a type and describe the change — the message assembles as you type.` | None — the Type dropdown is the first field; `Button.Accent` "Commit" stays disabled until valid (T-31) | Live read-only preview card on `SurfaceDeep` shows a dimmed placeholder line; validation chips absent until input. **Silent** (ES-1, V-1) |

---

## 3 · History & inspection

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **CommitTimelineView** | Unborn HEAD, no commits · `empty-yet` | `No commits yet` | `Make your first commit from the staging panel to start the history.` | None — routes attention to staging; no `Button.Accent` here (mirrors AnalyticsView no-action pattern) | 56px single-node thread stub; **silent** — the commit graph never animates its rows in (M-2, ES-1) |
| **BlameView** | File untracked / new, no committed lines · `empty-yet` | `Not tracked yet` | `Blame appears once this file has at least one commit.` | None — read-only fact | 48px gutter-heat glyph flat in `TextMuted`; **silent** (ES-1) |
| **BlameWindow** | Hosts `BlameView` for a file with no history · `empty-yet` | `Nothing to blame yet` | `This file has no committed lines — its blame is empty.` | `Button.Secondary` "Close" | Same card as `BlameView`; **silent** (ES-1) |
| **"Why this line" popover** (T-32, in `BlameView`) | Commit found but not merged via a PR · `empty-yet` | `No pull request for this line` | `This commit wasn't merged through a pull request on the connected host.` | `Button.Secondary` "Open commit on host" (via `BrowserLauncher` / `SafeWebUrl`) | Compact popover, Title-scale headline (Hero would overpower a flyout — noted deviation); **silent** (V-1, V-6) |
| **"Why this line" popover** (T-32) | Host not connected · `not-connected` | `Connect a host to trace this line` | `Sign in to GitHub to see the PR and issues behind a commit.` | `Button.Accent` **"Open Accounts"** → `AccountsWindow` | Neutral outline icon, **no `DangerBrush`**, silent (ES-3, V-2) |
| **FileHistoryView** | File has a single revision, no predecessor · `empty-yet` | `No earlier versions` | `This file has only its current revision — nothing to compare yet.` | None — read-only fact | 48px stacked-revisions glyph flat; **silent** (ES-1) |
| **AnalyticsView** | Analysis running · `loading` | *(skeleton — no headline)* | *(none — ghost cards stand in for the four charts)* | None | **Ghost/skeleton loader**: four dim `SurfaceCard` placeholders where the donut, churn series, punch-card, and contributor bars will land. Honest wait, not a fake bar (M-6). **Silent** apart from the skeleton pulse |
| **AnalyticsView** | `HasCommitData` false · `empty-yet` | `Not enough history to chart yet` | `Analytics appears once this repository has a few commits.` | None (ES-1 canonical example) | 56px chart-outline glyph flat; **silent** (ES-1) |
| **AnalyticsView** | Commits exist but `HasLanguageData` false (all-ignored/binary tree) · `empty-yet` | `No languages detected` | `Nothing in the working tree maps to a known language — the churn and contributor charts still apply.` | None | Language-donut card shows a hairline ring placeholder; other three charts render normally; **silent** (ES-1) |

---

## 4 · Connected-host panels

These five panels share the `not-connected` affordance (**ES-3**) — the host lacks an implemented provider *or* a token. That state is never an error: no `DangerBrush`, calm capability copy, and the one `Button.Accent` routes to **Accounts**. Each panel also has its own `empty-yet` (connected, but nothing exists) and, where meaningful, an `all-clear`.

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **PullRequestsWindow** | Origin host has no provider or no token · `not-connected` | `Pull requests need a connected host` | `Sign in to GitHub to see and open PRs for this repository.` | `Button.Accent` **"Open Accounts"** (ES-3 canonical example) | Neutral PR-branch outline; **silent** (ES-3, V-2) |
| **PullRequestsWindow** | Connected, zero open PRs · `empty-yet` | `No open pull requests` | `Push a branch, then open a pull request from here or the branch menu.` | `Button.Accent` **"Create pull request"** (disabled on detached/unborn HEAD, with the T-23 hint) | 48px PR glyph flat; **silent** (ES-1, ES-2) |
| **PR Review section** (T-25, in `PullRequestsWindow`) | PR has no reviews or inline comments · `empty-yet` | `No reviews yet` | `Add the first review — Comment, Approve, or Request changes.` | Verdict picker + body → `Button.Accent` **"Submit review"** (body required unless verdict is Approve) | Inline within the PR card; **silent** (ES-1, V-1) |
| **IssuesWindow** | Not connected · `not-connected` | `Issues need a connected host` | `Sign in to GitHub to see and open issues for this repository.` | `Button.Accent` **"Open Accounts"** | Neutral issue-dot outline; **silent** (ES-3) |
| **IssuesWindow** | Connected, Open filter empty · `all-clear` | `No open issues` | `Everything's triaged. Switch to Closed to see resolved issues.` | `Button.Accent` **"New issue"**; `Button.Secondary` "Closed" (segment) | Quiet settle-weave; single fade (ES-4, M-1) |
| **IssuesWindow** | Connected, repo has never had issues · `empty-yet` | `No issues tracked` | `Open the first issue to start tracking work for this repository.` | `Button.Accent` **"New issue"** | 48px issue glyph flat; **silent** (ES-1) |
| **ChecksWindow** | Not connected · `not-connected` | `CI checks need a connected host` | `Sign in to GitHub to see check runs for this commit.` | `Button.Accent` **"Open Accounts"** | Neutral check glyph; **silent** (ES-3) |
| **ChecksWindow** | `HasAny` false — no CI configured · `empty-yet` | `No checks ran for this commit` | `No CI is configured for this commit's branch, or it hasn't reported yet.` | None — absence of CI is never a failure (per `CheckStateMapper`: empty ⇒ `HasAny=false`, not a fail) | 48px check-outline flat in `TextMuted`; **silent** — must not read as a `DangerBrush` failure (ES-1, V-2) |
| **ChecksWindow** | All runs succeeded · `all-clear` | `All checks passed` | `Every check for this commit reported green.` | None — `Button.Secondary` "View logs" per run remains available | Overall badge ✓ in `SuccessBrush` token; single settle fade (ES-4, M-1, V-2 — verdict word carries meaning, not the color alone) |
| **NotificationsWindow** | Not connected · `not-connected` | `Notifications need a connected host` | `Sign in to GitHub to see your notification inbox.` | `Button.Accent` **"Open Accounts"** | Neutral inbox outline; **silent** (ES-3) |
| **NotificationsWindow** | Unread-only filter, everything read · `all-clear` | `You're all caught up` | `No unread notifications. Switch to All to see everything.` | `Button.Secondary` "All" (segment toggle) — no `Button.Accent` on a reward state | Quiet inbox-zero settle-weave; single fade (ES-4, M-1) |
| **NotificationsWindow** | Connected, no threads at all · `empty-yet` | `No notifications` | `Mentions, review requests, and CI activity for this host land here.` | None — read-only inbox | 48px inbox glyph flat; **silent** (ES-1) |
| **ReleasesWindow** | Not connected · `not-connected` | `Releases need a connected host` | `Sign in to GitHub to see and publish releases.` | `Button.Accent` **"Open Accounts"** | Neutral tag outline; **silent** (ES-3) |
| **ReleasesWindow** | Connected, no releases · `empty-yet` | `No releases yet` | `Publish your first release to tag a version and share notes.` | `Button.Accent` **"New release"** (the composer can Auto-generate notes locally) | 48px tag glyph flat; **silent** (ES-1, ES-2) |

---

## 5 · Repository management windows

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **RemotesWindow** | No remotes configured (local-only repo) · `empty-yet` | `No remotes configured` | `Add a remote to fetch, push, and open pull requests.` | `Button.Accent` **"Add remote"** | 48px remote/cloud outline; **silent** (ES-1, ES-2) |
| **SubmodulesWindow** | Repo references no submodules · `empty-yet` | `No submodules` | `This repository doesn't reference any submodules.` | None — read-only fact; `Button.Secondary` "Refresh" | 48px nested-repo glyph flat; **silent** (ES-1) |
| **LfsWindow** | git-lfs not on PATH · `not-connected` | `Git LFS isn't installed` | `Install Git LFS to track large files here — Mainguard detects it once it's on your PATH.` | `Button.Secondary` "Recheck" — Mainguard can't install it, and won't pretend to (V-6) | Neutral LFS glyph, **no `DangerBrush`** — a missing tool is a capability gap, not an error (ES-3, V-2, V-6) |
| **LfsWindow** | LFS present, no tracked patterns · `empty-yet` | `No LFS patterns tracked` | `Track a pattern like *.psd to store matching files with Git LFS.` | `Button.Accent` **"Track pattern"** | 48px pattern glyph flat; **silent** (ES-1, ES-2) |
| **AccountsWindow** | No hosts connected · `empty-yet` | `No accounts connected` | `Connect GitHub or another host to work with pull requests, issues, and CI.` | `Button.Accent` **"Add account"** (device-flow / PAT) | 48px host/key outline; **silent** — this is the hub every `not-connected` state points to (ES-1, ES-2) |
| **SshKeysWindow** | No keys in `~/.ssh` · `empty-yet` | `No SSH keys yet` | `Generate an ed25519 key to authenticate with your host over SSH.` | `Button.Accent` **"Generate key"** (optional passphrase) | 48px key glyph flat; **silent** (ES-1, ES-2) |
| **OperationHistoryWindow** | Journal empty · `empty-yet` | `No operations recorded yet` | `Mainguard journals every commit, merge, and reset here so you can undo them.` | None — the panel fills itself as you work | 48px undo-arc glyph flat; **silent**. Copy states the recovery promise up front (ES-1, V-5) |
| **ReflogWindow** | Picked ref has no reflog (e.g. `core.logAllRefUpdates` off) · `empty-yet` | `This ref has no reflog` | `Pick another ref, or turn on core.logAllRefUpdates to start recording its moves.` | `Button.Secondary` — the ref picker is the way forward | 48px timeline glyph flat; **silent**. The picker never disappears, so the fix is one control away (ES-1, V-5, N-6) |
| **ProfilesWindow** | No identity profiles · `empty-yet` | `No Git identities yet` | `Create a profile to switch user.name and email per repository.` | `Button.Accent` **"New profile"** | 48px identity-card glyph flat; **silent** (ES-1, ES-2, N-6) |
| **WorktreeWindow** | Only the main worktree exists · `empty-yet` | `Just the main worktree` | `Add a worktree to check out another branch in its own folder — no stashing.` | `Button.Accent` **"Add worktree"** | 48px branching-folder glyph flat; **silent**. One term everywhere — *worktree*, never "checkout copy" (ES-1, N-6) |
| **ShortcutSettingsWindow** (T-18) | *Never empty* — always lists every registered action | *(n/a)* | *(n/a)* | *(n/a)* | Listed for completeness: this surface has no empty state; the action registry is always populated (M-2) |
| **InteractiveRebaseWindow** | *Never empty* — always opened over a non-empty todo range | *(n/a)* | *(n/a)* | *(n/a)* | Listed for completeness: an empty rebase range is never presented; the window opens only with picks to arrange |

---

## 6 · Conflict & merge

| View | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **ConflictedFilesWindow** | Merge/rebase produced no conflicts, or all resolved · `all-clear` | `No conflicts to resolve` | `Every file merged cleanly.` | `Button.Accent` **"Commit merge"** when a merge is in progress; else `Button.Secondary` "Close" | Quiet settle-weave in `SuccessBrush` token; single fade (ES-4, M-1) |
| **ConflictResolverWindow** | Opened, but every chunk already accepted · `all-clear` | `All conflicts resolved` | `Both sides are reconciled — commit the merge when you're ready.` | `Button.Accent` **"Commit merge"** | 3-pane editor rests with no `MergeBandRenderer` tints remaining; the resolution weave settles once, no bounce (ES-4, M-1, M-5) |

---

## 7 · Command palette

| View | Trigger · kind | Headline | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **CommandPaletteView** (T-18) | Query matches no action · `empty-yet` | `No actions match "<query>"` (Title scale — a compact overlay, Hero would overpower it; **noted deviation** from ES full-Hero) | `Try a shorter query, or press Esc to close.` | None — Esc dismisses; the query box stays focused | Inline within the palette card over the scrim; **silent** — the overlay already faded in per M-3 (ES-1, V-1) |

---

## 8 · Agent control center — **[Horizon]**

No agent surfaces are built (**PRODUCT.md** Principle 5; **DESIGN.md §6** — don't design speculative UI). These rows are **voice-and-naming placeholders only** — they specify how a *future* empty state will speak so it inherits the system by construction (Bible "Scope of tense": a shipped-surface doc must not cite a **[Horizon]** rule as license to build). They exist so the empty-state grammar is ready when the control center lands.

| View **[Horizon]** | Trigger · kind | Headline (Hero) | Body (TextMuted) | Primary action | Illustration / motion |
|---|---|---|---|---|---|
| **Agent roster / FleetView** [Horizon] | No agents assigned · `empty-yet` | `No agents running` | `Assign a task to an agent to start work in an isolated sandbox.` | `Button.Accent` **"Assign agent"** | Loom motif at rest; agent names would follow `Loom-1…Loom-n`, never a mascot (N-4). **Silent** (ES-1, N-4) |
| **Verification / pipeline view** [Horizon] | No runs yet · `empty-yet` | `Nothing to verify yet` | `Verification results appear once an agent finishes a run in its sandbox.` | None | State words stay factual — `Verifying` · `Verified` · `Blocked` · `Quarantined`, never mood words (N-3). Passing verification is the emotional peak but stays a **fade, not a fanfare** (T-4, M-1) |
| **Audit trail** [Horizon] | No attributable actions yet · `empty-yet` | `No agent activity recorded` | `Every agent action is logged here, labelled by which agent produced it and whether you've reviewed it.` | None — read-only ledger | Copy is honest about the machine: worker output is always attributed and its review state named (V-6). **Silent** (ES-1, V-6) |

---

## 9 · Reusable empty-state component (the ES card)

One card pattern renders every row above. It is the concrete expression of the **ES** template — build it once in `App.axaml` as a component class, feed it per-surface strings, and every empty state stays in-system by construction (**N-5**).

### Structure (top → bottom, centered)

```
┌───────────────────────────────────────────┐   ← Card
│                                             │
│                 [ 48–64px icon ]            │   ← shared icon, TextMuted (or
│                                             │     SuccessBrush-toned for all-clear)
│              Headline — Hero 24/600         │   ← TextPrimary (ES-1 plain fact)
│                                             │
│         Body — 12–13px, one line            │   ← TextMuted (how to fill it)
│                                             │
│      [ Button.Accent ]   ⟨link/Secondary⟩   │   ← at most one Accent (ES-2)
│                                             │
└───────────────────────────────────────────┘
```

### Token & shape contract (all `{DynamicResource}`, no raw hue)

| Slot | Token / class | Rule |
|---|---|---|
| Card surface | `SurfacePanel` (or `Transparent` when it already sits inside a card) | DESIGN.md §5 Cards |
| Card corners / border | radius `12`, 1px `BorderHairline` | fixed scale (6/8/12/999) |
| Card padding & vertical rhythm | `20` outer, `10`–`15` between icon → headline → body → action | spacing scale 4/5/8/10/15/20 |
| Icon | 48–64px shared PathIcon, `TextMuted`; `SuccessBrush` **token** tone only on `all-clear` | ES art scale; Semantic-Not-Literal (never raw green) |
| Headline | `FontUi`, Hero 24 / 600, `TextPrimary` | ES-1, the one Hero moment (DESIGN.md §3) |
| Body | `FontUi`, 12–13, `TextMuted`, single line; refs/SHAs/paths in `TextBlock.Mono` | ES shape, N-6 |
| Primary action | one `Button.Accent`, verb-first label | ES-2, One Accent Rule |
| Secondary path | `Button.Secondary` or plain accent link | ES-2 |

### Four modes (one prop switches them)

- **`empty-yet`** — icon `TextMuted`; `Button.Accent` present. Headline = plain absence (ES-1).
- **`not-connected`** — icon `TextMuted`; **never** `DangerBrush`; `Button.Accent` = "Open Accounts". Capability + how to enable (ES-3, V-2). Distinct from an error `E`-pattern panel, which stays in a panel and names a recovery path (E-3).
- **`all-clear`** — icon `SuccessBrush`-toned; usually no `Button.Accent`. A quiet affirmation, earned only by genuine healthy emptiness (ES-4).
- **`loading`** — no headline/body; renders dim `SurfaceCard` skeleton blocks or an indeterminate `PathIcon.spinning`. Honest or absent, never a fake number (M-6).

### Motion (identical for every instance)

- Fade in over **130–150ms**, opacity only, **no layout shift** (M-3).
- `all-clear` may play the loom settle — one thread drawn taut, arriving and stopping, **no overshoot / no bounce** (M-1, M-5).
- `not-connected` and `loading` art is **silent** (M-2); `loading` shows only the skeleton pulse.
- A reduced-motion preference collapses all of the above to an instant state change — meaning survives with zero animation (M-7).

### Anti-checklist (fail the card if any is true)

- A raw hex or `StaticResource` color anywhere (DESIGN.md Don't).
- More than one `Button.Accent`, or a destructive `Button.Danger` in an empty state (One Accent Rule; empty states aren't destructive surfaces).
- `DangerBrush` / alarm styling on a `not-connected` state (ES-3, V-2).
- An emoji, a joke, an "oops"/"we", or an exclamation mark in headline or body (V-2, V-3).
- A celebratory motion (bounce, confetti, scale-pop, staggered draw-in) on any mode (M-5).
- A fabricated progress number in `loading` (M-6).
- A new corner radius or spacing value outside the fixed scales (DESIGN.md Don't).
</content>
</invoke>
