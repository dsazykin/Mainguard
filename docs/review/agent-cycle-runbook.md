# Agent runbook — reproduce the phase-1 cycle end-to-end

**Audience: an autonomous coding agent (Claude Code) running on the target machine.** This reproduces
the on-device phase-1 cycle test — spawn → work → verify → review → merge — that was run on macOS, so
it can be re-run on the Windows/WSL2 machine (a different substrate) to catch substrate-specific
issues. Follow it top to bottom; every step has an assertion you must check, not just an action.

The **companion** `phase-1-hands-on.md` is the human sit-at-the-app version of the same flow, and
`phase-1-review-guide.md` maps each claim to the automated test that proves it — read §2 of the
review guide first for the one-screen summary of the cycle.

---

## 0. Substrate — read this, it changes the paths

The agent platform runs behind `IAgentEnvironment`. Two realizations exist:

| | **Windows** (this run) | macOS (the original run) |
|---|---|---|
| Substrate | `Wsl2AgentEnvironment` — daemon runs **inside the `MainguardEnv` WSL VM** | `MacHostAgentEnvironment` — daemon runs **natively on the host** |
| Docker | inside the VM (`wsl -d MainguardEnv -- docker …`) | host engine (Docker Desktop / OrbStack / Colima) |
| Substrate root (repos/worktrees/agents/adapters/toolchains) | VM path `/home/mainguard/mainguard/…` | host `~/mainguard/…` |
| Sync remote | `mainguard-vm` (UNC `\\wsl$\…` handle) | `mainguard-local` (local bare path) |
| Data root (token, certs, keyring, logs) | `%LOCALAPPDATA%\Mainguard\` | `~/.mainguard/` |

**The flow is identical; the commands and paths differ.** Whenever this doc says "the substrate root"
or "the mirror," resolve the real path for your substrate first (grep
`docs/phase-2/Mainguard_Substrate_WSL2.md` for the Windows paths). Do NOT assume the macOS paths.

**The client still reaches the daemon over `127.0.0.1:5250` on both** — the WSL daemon's loopback is
forwarded to the Windows host, and `DaemonClient.ForLoopback()` reads the token+certs from the
data-root path above. This is why the **RPC driving mode below is the primary, substrate-agnostic
method** — prefer it over GUI automation, which is OS-specific and brittle.

---

## 1. Prerequisites — establish the baseline before touching the cycle

```
# from the repo root, on the branch under test (port/macos or its merge target)
dotnet build Mainguard.slnx -c Release        # must succeed; close any running head first (apphost lock)
```

Confirm the platform is healthy:
- **Docker reachable.** Windows: `wsl -d MainguardEnv -- docker info` succeeds. Record the engine + version.
- **Jail images present.** `docker images | grep mainguard` shows `mainguard-agent-base` and
  `mainguard-agent-toolchain` (build them via the app's OOBE / Tools → Rebuild sandbox images if absent).
- **Daemon running.** Launch the Pro head once to start it (`dotnet run --project Mainguard.Pro.App`),
  complete the OOBE, then confirm `GetDaemonInfo` answers (the RPC harness in §3 prints the version).
- **Clean slate.** Remove leftover jails from prior runs — they cause `RequiresDocker` flakes under
  load: `docker ps -q --filter label=mainguard.role=agent | xargs -r docker rm -f`. Discard any stale
  `Working` merge-queue rows through the app's rail **Discard** (or restart the daemon to rebuild the
  queue from persisted state).

---

## 2. Build the fixture repo and the scripted agent

### 2a. Fixture repo — a `main` branch with a real verify command

```
mkdir <work>/e2e-fixture && cd <work>/e2e-fixture
git -c init.defaultBranch=main init
git config user.name "<you>"; git config user.email "<you>"; git config commit.gpgsign false
mkdir -p .mainguard src
printf 'function add(a,b){return a+b;}\nmodule.exports={add};\n' > src/calc.js
printf "const {add}=require('./src/calc.js');\nif(add(2,3)!==5){process.exit(1);}\nconsole.log('all tests green');\n" > test.js
printf 'node test.js\n' > .mainguard/verify      # argv line, read out of git — NOT a shell script
git add -A && git commit -m "chore: seed the e2e fixture"
```

`.mainguard/verify` is a **command line** the daemon splits and runs *inside the agent's jail*; the
node image carries node 22 (no `.mainguard/toolchain` needed for node). The **exit code is the only
signal** — a passing test must `exit 0`, a failing one non-zero.

### 2b. Scripted agent — a no-LLM stand-in for a coding CLI

The adapters tree is mounted **read-only at `/opt/mainguard/adapters`** in every jail. Drop a launch
script + a registry marker into the substrate's adapters dir (resolve the real path per §0):

`<adapters>/bin/scripted-agent` (chmod +x):
```
#!/bin/sh
cd /workspace || exit 1
ID=$(git branch --show-current | sed 's|agent/||' | cut -c1-8)
mkdir -p src
printf "module.exports={note:'work by %s'};\n" "$ID" > "src/note-$ID.js"
git -c user.name="Scripted Agent" -c user.email="agent@mainguard.local" add -A
git -c user.name="Scripted Agent" -c user.email="agent@mainguard.local" commit -m "feat: add note module ($ID)"
exec sh -i      # idle so the jail stays alive for verify/pause/prompt legs
```

`<adapters>/registry/scripted.json`:
```
{ "id": "scripted", "version": "1.0.0",
  "launch": ["/opt/mainguard/adapters/bin/scripted-agent"],
  "apiKeyEnvVar": "", "egressHosts": [], "credentialPaths": [] }
```

Also make a `scripted-evil` variant whose script does `echo "true" > .mainguard/verify` (rewrites the
repo's own verify command to a trivial pass) before committing — this exercises the
**changed-test-command gate**. An unknown-kind spawn is NOT refused (bare jail + PTY), so a typo is
safe but won't do the work.

> On Windows the adapters tree is inside the VM — write these files via `wsl -d MainguardEnv -u mainguard …`
> or a heredoc through `wsl`. Confirm the marker is visible to the daemon (it reads
> `InstalledAdapterCatalog` from that dir).

---

## 3. The RPC driver — the primary, substrate-agnostic way to drive the cycle

Write a `dotnet fsi` harness that references the built assemblies and drives `DaemonBackedOrchestrator`
— **the exact adapter every UI button calls.** Reference these from `Mainguard.Server.Tests/bin/Release/net10.0/`:
`Mainguard.Agents.dll`, `Mainguard.Agents.UI.dll`, `Mainguard.Protos.dll`, `Mainguard.Git.dll`,
`Grpc.Net.Client.dll`, `Grpc.Net.Common.dll`, `Grpc.Core.Api.dll`, `Google.Protobuf.dll`, and
`Microsoft.Extensions.Logging.Abstractions.dll` (the net8.0 9.0.0 copy from
`~/.nuget/packages/…`, or `%USERPROFILE%\.nuget\…` on Windows — the one in the test bin folder may be
absent).

The methods you need (all on `DaemonBackedOrchestrator` unless noted; `DaemonClient.ForLoopback()`
builds the mTLS channel):

- `client.GetDaemonInfoAsync(ct)` → version (sanity that you're on the daemon you built).
- `client.ProvisionRepoAsync(repoPath, ct)` → `ProvisionedRepo(RepoHandle, SyncRemoteName, SyncRemoteUrl)`.
  Register the sync remote on the checkout yourself (`git remote add <name> <url>`), exactly as the
  shell's `SyncRemoteRegistrar` does.
- `client.SpawnAgentAsync(handle, "", "scripted", "", ct, role="coordinator")` → the agent id.
- `orch.SetActiveRepo(handle, repoPath, syncRemoteName)` — binds the merge projection (this is what
  makes merge able to touch YOUR checkout).
- `orch.RunVerificationAsync(agentId)` → `VerificationOutcome(Ran, Passed, Reason)`.
- `orch.GetQueue()` → the projected entries (State, VerifiedMainSha, CanMerge via `orch.CanMerge`,
  FlaggedItems).
- `orch.BringBranchLocalAsync(agentId, ct)` → `BringLocalResult(Done, LocalBranch, Reason)`.
- `orch.ConfirmMergeAsync(agentId)` → `MergeOutcome(AgentId, NewMainSha)` (throws with the daemon's
  reason on refusal — catch and print it).
- `orch.RejectEntryAsync(agentId, reason)` → `QueueEntryRejectOutcome`.
- `orch.PauseAgentAsync(agentId)` / `orch.ResumeAgentAsync(agentId)`.
- `orch.SendPromptAsync(agentId, text)` — writes into the live PTY (used for the real-agent login/task).

Run scripts with `dotnet fsi <file>.fsx`. `Date.now`/randomness are fine in fsx (unlike the Workflow
runtime). Keep each chunk small so a failure is localized.

---

## 4. The cycle — run it and assert each edge

Do these against the fixture. Every "**Assert**" is a fact you read off git/docker/the daemon, never
"the screen looked right."

1. **Provision + spawn a scripted coordinator.**
   `ProvisionRepoAsync` → register remote → `SpawnAgentAsync(role="coordinator")`.
   **Assert:** `docker ps --filter label=mainguard.role=agent` shows a jail for the id; within ~20 s
   `git --git-dir <mirror> for-each-ref` shows `refs/heads/agent/<id>` (the mediator published the
   agent's commit). If the branch never appears, the ref mediator or the scripted commit failed —
   inspect `docker logs <jail>` and the daemon's `spawn.log`.

2. **Verify — in the jail, not on the host.**
   `SetActiveRepo(handle, fixture, remoteName)`, then `RunVerificationAsync(id)`.
   **Assert:** `Ran=true, Passed=true`, reason names the sha; `GetQueue()` shows the entry `Verified`
   with `VerifiedMainSha` set and `CanMerge`=true. Cross-check the run happened in the container:
   `docker events` during the call, or that the verification store under the daemon data root gained
   a record. **Host execution is a rejection trigger — if you ever see the verify command run on the
   host, that is a defect.**

3. **Bring local.**
   `BringBranchLocalAsync(id)`.
   **Assert:** `Done=true`; `git branch --list 'agent/*'` in the checkout now lists the branch; HEAD
   did NOT move (still on main). Re-run it — a second call must fast-forward or no-op, never error.

4. **Merge — the only thing that counts is main moving in YOUR checkout.**
   Record `git rev-parse main` before; `ConfirmMergeAsync(id)`; record after.
   **Assert:** main advanced to the agent branch's tip; the agent's file (`src/note-*.js`) exists in
   the working tree; `node test.js` still passes. **Then assert the mirror caught up:**
   `git --git-dir <mirror> rev-parse main` equals the new sha within a few seconds (this is the
   post-merge mirror-refresh — without it, a later spawn walks the queue's main backwards; see the
   `fix(merge-queue)` commit). Re-opening the entry must report "already merged" and refuse a second
   merge.

5. **The changed-test-command gate.**
   Spawn a `scripted-evil` agent, `RunVerificationAsync` it.
   **Assert:** it reaches `Verified` but `CanMerge`=false with a reason naming the changed verify
   command, and `FlaggedItems` contains a `changed-test-command` item. A `ConfirmMergeAsync` on it
   throws (refused). Acknowledging the flagged item (`AcknowledgeFlaggedChangeAsync`) then lets it
   merge — but for this run, **Reject it instead**: `RejectEntryAsync(id, "self-green attempt")`.
   **Assert:** the entry is terminal `Rejected` (stays on the stream as Rejected, unlike a discard),
   and the audit log gained a `queue_entry_rejected` event with your reason.

6. **Stale cascade.**
   Spawn TWO more honest scripted agents, verify both to `Verified` (both `CanMerge`). Merge one.
   **Assert:** the OTHER goes `StaleVerified` → auto re-verifies (keep-alive rebase) → returns to
   `Verified` **against the NEW main sha** (`VerifiedMainSha` updates to the post-merge sha). This is
   the cascade + mirror-refresh working together. If entries get stuck at "verification is stale —
   re-verifying" forever, the mirror didn't advance — re-check step 4's mirror assertion.

7. **Pause / Resume.**
   `PauseAgentAsync(id)` on a live jail.
   **Assert:** `docker inspect -f '{{.State.Paused}}' <jail>` = true; the daemon marks the session
   `Paused`. `ResumeAgentAsync(id)` → `Paused` = false. Then the arbitration edge: while a keep-alive
   rebase holds the jail (hard to time by hand — the unit tests `AgentPauseTests`/`YieldProtocolTests`
   cover it), a human unpause is refused with "try again in a moment"; a human pause is sticky. Verify
   the RPCs are also **coordinator-denied** (a coordinator-token client gets `PermissionDenied`).

8. **Kill switch — NOT covered in the macOS run; cover it here.**
   With ≥2 live jails, engage the kill switch (the app's "Stop all", or the `KillSwitchService` RPC).
   **Assert:** EVERY jail freezes (`docker inspect` Paused=true on all of them, not just the first),
   the merge queue is frozen (a `BeginMerge`/`DiscardEntry` is refused while frozen), and Resume
   restores them. This is safety-critical and was untested live — pay attention.

---

## 5. The real-agent leg (claude-code) — needs a human login

1. In the app, **Start coordinator** with `claude-code` selected. It launches the real CLI in its
   jail under a PTY and streams its welcome banner.
2. Drive its onboarding via `SendPromptAsync(agentId, "…")` (synthetic keystrokes do NOT reach a
   jailed PTY — use the RPC): accept the theme, then select the subscription login option.
3. The CLI prints an OAuth URL, usually **truncated on screen**. Pull the full URL from the PTY: a
   raw `AttachTerminal` read replays scrollback on attach — strip ANSI, regex
   `https://claude.com/…state=…`. Hand the URL to the human (you cannot log in yourself). Wait for
   them to confirm, then `SendPromptAsync` any pasted code if prompted.
4. Give it a **verifiable task**: e.g. "Add subtract(a,b) to src/calc.js, extend test.js to cover it,
   run node test.js, then git add and commit with a conventional message. Do not push." Send a bare
   `\r` afterward if the CLI treats the prompt as a paste that needs a separate Enter.
5. **Assert:** the agent commits on `agent/<id>`, the mediator publishes it, then run steps 4 (verify)
   and 4-merge of §4 through the **actual UI buttons** (Verify → Review → Merge) and confirm
   `git rev-parse main` moves and `node test.js` passes. The review cockpit header must read
   **"Coordinator (claude-code)"** (not a raw GUID).
6. **Assert login persistence:** after the agent stops, `<data-root>/Keyring/cli_login_claude-code.*`
   exists; a subsequent spawn reuses it (no second login prompt).

---

## 6. Restart resilience

Quit the app (NOT "Stop all" — a plain quit; the daemon and jails outlive the UI). Relaunch the Pro
head. **Assert:** the Coordinator surface re-attaches to the still-running agent (shows "Stop
coordinator", same id); an interrupted `Verifying` entry resumes; a queue whose main advanced while
no queue was alive re-fires the stale cascade on boot (the `restart-resume` fix).

---

## 7. What to report back

For each numbered step: PASS/FAIL with the concrete evidence (the sha, the docker state, the reason
string). Call out anything **substrate-specific** that behaved differently from the macOS run —
especially around the sync remote name (`mainguard-vm` vs `mainguard-local`), the merge path (WSL UNC
vs host path), verification inside the VM's docker, and the kill switch. Note any `RequiresDocker`
flake and whether it cleared after removing orphan jails. Finish by removing the fixture jails and the
scripted adapter markers.
