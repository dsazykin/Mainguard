# Persisting agent CLI settings — and why the scope is per repository

## The report

> "every time a new agent is launched it has no claude settings, meaning that i have to approve all
> commands again which i have already approved. this is bad ui, there should be either a global
> mainguard .claude or per repo .claude, this would allow all settings to be saved for all agents"

## What was actually happening

`adapters.starter.json` declared, for `claude-code`:

```json
"credentialPaths": [".claude/.credentials.json", ".claude.json"]
```

Credentials, and nothing else. There was no settings field anywhere, and no code path referenced
`settings.json` or `permissions`. So every jail started with a `$HOME/.claude` holding restored OAuth
tokens and an otherwise empty directory, and a `/workspace` freshly created from the repo mirror —
both of which are wiped on every spawn:

* `$HOME` (`/home/agent`) is a 256 MiB **tmpfs** mounted over the image's home. It dies with the
  container.
* `/workspace` is a bind mount of the **per-agent worktree**, created at spawn and deleted at
  teardown.

Every permission grant lived in one of those two trees, so every grant was lost every spawn.

## What is persisted

A new adapter declaration, `settingsPaths`, the non-credential twin of `credentialPaths`. Each entry
names a **root** and a relative path:

```json
"settingsPaths": [
  { "root": "workspace", "path": ".claude/settings.local.json" },
  { "root": "home",      "path": ".claude/settings.json" }
]
```

Two roots exist because a CLI keeps user-level and project-level configuration in different trees, and
**the project one is where the grants are**: claude-code writes "yes, and don't ask again" to
`.claude/settings.local.json` in the project directory, not to the home file. A home-only design would
have persisted a file the CLI never writes — a fix that looks applied and changes nothing.

`claude-code` is the only bundled adapter that declares `settingsPaths`. `gemini-cli` and `qwen-code`
already carry `.gemini/settings.json` / `.qwen/settings.json` under `credentialPaths`; that predates
this field and was deliberately left alone, because moving them would migrate live data out of the OS
keychain. Re-open that separately.

## Scope decision: per repository

**Grants are stored per (repository, adapter kind). There is no global tier.**

A permission allowlist is not a preference. It is a standing grant of execution, and the question
"which command may run without asking" only has an answer relative to a codebase — `make deploy`,
`npm run release`, `./scripts/reset.sh` mean entirely different things in different repositories.
Approving one while working on repo A must not silently pre-approve it in repo B.

The owner offered "global or per repo". Per repo is the narrower of the two, and where the scope is
uncertain the narrower one wins.

**The cost is real and accepted.** Settings that genuinely are global — a theme, a model choice — are
inside the same opaque vendor blob as `permissions`, and Mainguard does not parse vendor settings
schemas. So they are re-set once per repository rather than once. Splitting a settings file into
"global-safe" and "repo-only" halves would mean Mainguard understanding each CLI's schema and staying
correct as it changes; getting that wrong in the permissive direction is exactly the failure this
scope decision exists to avoid. If a vendor ever separates the two files, the manifest can declare
them separately and a global tier becomes a small, honest change.

### Where it is stored, and why not the keychain

```
%LocalAppData%\Mainguard\cli-settings\<repo handle>\<adapter id>.json     (Windows)
~/.mainguard/cli-settings/<repo handle>/<adapter id>.json                 (elsewhere)
```

The standing rule is that **logins live only in the host OS keychain**. That rule is about
*credentials*. Settings are not credentials: they are configuration the owner has every reason to
want to read, audit and throw away. A keyring entry is none of those things. So settings go to an
ordinary JSON file under the same data root the daemon token, the adapter pins and the app database
already use — and "forget everything this repository approved" is a file the owner can delete.

The two stores are kept apart by construction: `AdapterManifest.Parse` **refuses** a path that appears
in both `credentialPaths` and `settingsPaths`, so a manifest edit cannot divert a credential into the
plaintext store. `CliSettingsStoreTests` and `AdapterSettingsPathTests` pin both halves.

## The two trust gates

### IN — untrusted jails inherit nothing

External-PR intake spawns a worker from a **bot pull request**: code chosen by someone outside the
user's machine. `ExternalPrWorkerHost` already passes `withoutHostCredentials: true` so that jail
inherits no cached CLI login. That flag now gates settings too, and the reasoning is *stronger* than
for a login: an inherited allowlist is inherited **execution**. A jail holding somebody else's branch
that boots pre-approved to run whatever the user had ever allowed in that repository would be a
genuine security regression introduced by a convenience fix.

The decisive case is not "the caller passed no settings" — it is "the caller passed none **and** the
daemon's per-(repo, kind) fallback cache is warm", which is exactly the state a pull request arrives
into on a repository the user has been working in all day.
`CliSettingsBoundaryTests.AnUntrustedSpawn_InheritsNoGrants_EvenWhenTheRepositorysCacheIsWarm` drives
that case through the shipped `AgentSpawnService`; deleting the gate makes it fail with
`it got 1 of them, which is pre-approved execution on code an outside author chose`.

### OUT — only a human-attended jail's approvals flow back

Should a grant approved *inside* a jail flow back out and apply to future agents?

It has to, or the feature does not exist: the approvals the owner is complaining about are made in the
CLI's own terminal, inside the jail. Requiring them to be hand-written into a host file instead is not
the fix that was asked for.

But this is the direction an agent can escalate itself. The settings file is **agent-writable by
construction** — the CLI must be able to record a new approval — so a harvest cannot distinguish "the
human answered yes" from "the agent wrote the file". What Mainguard *can* determine is whether a human
was in a position to answer at all:

* A `Managed` worker's terminal is **daemon-locked read-only** (P2-14). Nobody typed an approval into
  it, so anything in its settings file was written by the agent. Its settings are never persisted.
  External-PR workers are `Managed`, so this gate covers them a second time.
* Manual and coordinator sessions are terminals the user drives. Those are harvested.

That is `CliSettingsHarvestPolicy`, and
`CliSettingsBoundaryTests.StoppingAnUnattendedWorker_PersistsNothing_EvenThoughTheFileIsRightThere`
proves it is doing the work — the fake jail *has* the file, so an empty result can only be the gate.

**Restore stays deliberately wider than harvest.** A `Managed` worker still *receives* the
repository's approvals (otherwise it stalls forever on prompts nobody can answer); it just cannot
write to them. Grants flow in from a human-managed source and never back out of an unattended one.

### Residual risk, stated plainly

An agent running in a **trusted, human-attended** jail can write its own entries into that jail's
settings file, and they will be harvested — Mainguard cannot tell them from the user's. What bounds it:

* the blast radius is **one repository**, never the machine;
* untrusted and unattended jails are excluded entirely;
* harvested files are capped at `AdapterSettingsPolicy.MaxFileBytes` (256 KiB), so a jail's occupant
  cannot push volume into a store that later jails read;
* only paths the adapter **declares** are ever read or written, so this cannot reach `.ssh` or
  anything else in the home or the checkout;
* the store is a readable JSON file the owner can inspect and delete.

This is not eliminated, and no comment in the code pretends otherwise. Eliminating it would mean
Mainguard mediating the CLI's own approval prompt, which it does not do today.

## The mechanism

Identical in shape to the credential round trip, which is why it is an extension of it rather than a
second system:

1. **Spawn.** The client loads `cli-settings/<repo>/<kind>.json`, sends it on `SpawnAgentRequest`
   (`cli_settings`). The daemon drops entries whose root it does not recognise, then
   `SandboxAgentLauncher.FilterCliSettings` keeps only (root, path) pairs the installed adapter's
   marker declares — the client names paths on the wire, so this is a real filter, not a formality.
2. **Restore.** `DockerSandboxEngine.RestoreCliSettingsAsync` writes each file as the **agent uid**,
   over exec **stdin**, **write-if-absent**. Stdin rather than `docker cp` because `docker cp` writes
   into the image layer *underneath* the tmpfs and reports success — the file is then invisible to
   everything in the container while every daemon-side signal says it landed. Write-if-absent so a
   relaunch never overwrites approvals the user just made in a live jail.
3. **Harvest.** `SandboxAgentLauncher.HarvestCliSettingsAsync` reads the declared files back out over
   base64, size-checked in the shell so an oversized file never enters daemon memory. Called from
   `AgentSpawnService` only behind the attendance gate, on stop and on the periodic live sweep.
4. **Keep it out of the user's history.** `/workspace` *is* the agent's git worktree, and the
   keep-alive rebase cycle's dirty-tree path is `git add -A && git commit`. A restored workspace
   settings file is an untracked file in the tree the agent commits, so without a guard this feature
   would start committing the user's permission allowlist into their repository and merging it to
   main. The restore therefore appends the path to `$GIT_DIR/info/exclude` — the repository's *local*
   ignore file, which lives in the per-agent repo the daemon deletes at teardown. Nothing tracked is
   touched, the user's own `.gitignore` is not edited, and no state outlives the agent. (MG-43 moved
   the package cache out of the worktree for the same reason; this file cannot move, because it is
   where the CLI reads it from.)
   `ARestoredWorkspaceSettingsFile_IsNeverCommittedIntoTheUsersRepository` makes `/workspace` a real
   repository and asserts `git status --porcelain` is empty *while the file is present*.
5. **Persist.** The response carries the session's `repo_handle`, and the client files the result
   under **that** repository — not "whichever repo is open". The harvest sweep walks every agent on
   the daemon, so filing by the open repo is precisely how one repository's allowlist would end up
   under another's name.

## Verification

| Instrument | Proves | Reads |
| --- | --- | --- |
| `CliSettingsRoundTripDockerTests.AGrantMadeInOneJail_IsPresentInsideAFreshJail` | the whole round trip, through real jails and the real host store | **inside the second container** |
| `…AJailGivenNoSettings_HasNoApprovedCommandsAtAll` | an untrusted jail really has no allowlist | inside the container |
| `…ALiveJailsOwnSettings_AreNeverClobberedByTheStoredCopy` | write-if-absent on the reuse path | inside the container |
| `CliSettingsBoundaryTests` (8) | both trust gates + the declared-path filter, through the shipped `AgentSpawnService` | the spawn request the engine received |
| `AdapterSettingsPathTests` (9) | the manifest schema, the credential/settings disjointness, and that the bundled adapter really declares both roots | the shipped manifest |
| `CliSettingsStoreTests` (9) | per-repo isolation, merge semantics, corrupt-file tolerance | the file on disk |

Every one was run green, then broken and observed red: the restore (fresh jail read back empty), the
harvest (0 files instead of 2), the untrusted gate (1 inherited grant), the attendance gate (a Managed
worker's file persisted), the path filter (3 files through instead of 1), the manifest disjointness
check, the per-repo scope segment, and the bundled `settingsPaths` declaration.
