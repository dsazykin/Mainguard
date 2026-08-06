# Phase 1 — hands-on test procedure

**What this is.** A sit-at-the-app procedure for phase 1: *the merge queue works, with the
coordinator acting as a normal CLI*. No test suites. You launch the app, do things, and check what
happened.

**Companion:** `phase-1-review-guide.md` covers the judgement calls and automated evidence. This
covers the product.

---

## Setup (once)

```powershell
cd C:\Users\yikes\Code\Mainguard
git checkout phase2
git pull
dotnet build
```

Phase 1 is merged, so `phase2` **is** phase 1.

**Use a scratch data root** so nothing here touches your real settings:

```powershell
$env:MAINGUARD_DATA_ROOT = "$env:TEMP\mg-test-p1"
dotnet run --project Mainguard.Pro.App
```

### Shells — read this first

Two different shells appear below and they are not interchangeable:

- **PowerShell** blocks run on Windows. You are on **Windows PowerShell 5.1**, which has **no `&&`
  operator** — put each command on its own line. (`;` chains but runs the next command even if the
  previous one failed, so it is not a substitute when order matters.)
- **WSL** blocks run inside a Linux distro. Anything inspecting the daemon or a jail is prefixed
  `wsl -d MainguardEnv …` and can be run from PowerShell as written.

**Use a scratch repo**, not Mainguard itself — an agent will commit to it. It must be a **Windows**
path, because that is what the app hashes and provisions:

```powershell
mkdir .\mg-testrepo
cd .\mg-testrepo
git init
"def add(a,b):`n    return a+b" | Set-Content calc.py
"import calc`ndef test_add(): assert calc.add(2,2)==4" | Set-Content test_calc.py
mkdir .mainguard
"python -m pytest -q" | Set-Content .mainguard\verify
git add -A
git commit -m "initial"
```

### Where the agent's work actually lives — important

**You will not see an agent's branch in your own checkout.** The agent works on a copy inside the
MainguardOS VM and never touches your working tree. That is the product's central guarantee, and it
means "nothing appeared locally" is *not* evidence of anything on its own.

```
your checkout      C:\Users\yikes\mg-testrepo          ← only changes when YOU merge
bare mirror        /home/mainguard/mainguard/repos/<repoHash>.git
per-agent repo     /home/mainguard/mainguard/agents/<repoHash>/<agentId>
agent branches     refs/heads/agent/*
```

**Find your repo's hash.** It is the SHA-256 of the normalized Windows path — don't compute it, and
don't try to pick it out of a bare `ls` (you get a multi-column list of 64-char names with nothing to
tell them apart). Ask each mirror which repo it is:

```powershell
wsl -d MainguardEnv -u root -- bash -c 'for g in /home/mainguard/mainguard/repos/*.git; do h=$(basename "$g" .git); printf "%s  %s\n" "$h" "$(git --git-dir="$g" config --get remote.origin.url)"; done'
```

Output looks like:

```
1deb19131adb…  /mnt/c/Users/yikes/Code/mg-testrepo
f0fa0539692f…  /mnt/c/Users/yikes/Code/GitLoom
```

Pick the line matching your scratch repo.

Set it once for the commands below:

```powershell
$H = "<the 64-char hash>"
```

> `dotnet build` also publishes the in-VM daemon; `DaemonUpdater` deploys it when the app launches.
> It **refuses downgrades**, so if you've run a phase-2/3 build, going back to `phase2` may leave the
> newer daemon in place. `wsl -d MainguardEnv -u root -- journalctl -u mainguardd -n 5` shows what's
> actually running.

---

## 1. The app starts and reaches the daemon

**Do:** launch it. Watch the startup window.

**Expect:** a checklist that ends with the main window. First launch after a `wsl --shutdown` takes
~10–30 s while the VM boots.

**If it fails:** the banner should name *which leg* failed — distro not started / daemon not running
/ not yet listening / TLS rejected. A generic "can't connect" is a bug; capture it.

**Known:** if you've been running an older installed build, the daemon may be too old to speak this
build's mTLS. The app now detects and repairs that; it takes ~60 s and says so.

---

## 2. Start a coordinator

**Do:** open a repo (your scratch one). Go to the **Coordinator** section in the left rail.
Start the coordinator.

**Expect:** a terminal draws, with your chosen CLI running in it.

**First run is slow and this is the weak point.** It builds a ~2.9 GB toolchain image. You should
see a progress line naming the build. **Do not press Stop** — that kills the build and the next
attempt starts over.

**Known issue (#53):** `SpawnDeadline` is 5 minutes and this build often takes longer, so a cold
first run can still time out even though it's working. If it does, wait for the build to finish
(`wsl -d MainguardEnv -u root -- docker images | grep toolchain`) and start again — the second
attempt is fast. That's the ticket, not a new bug.

---

## 3. Have the coordinator do a piece of work

**Do:** in the coordinator terminal, ask for something small and verifiable:

> "Add a `subtract(a,b)` function to calc.py with a test, then commit it to a branch."

**Expect:** it works in its own jail on its own branch. Nothing appears in your working tree.

**Check both halves.** Your checkout is untouched **and** the work really exists in the VM — the
first alone proves nothing, since an agent that did nothing at all would also leave it clean:

```powershell
cd $HOME\mg-testrepo
git status                                    # clean — the agent is NOT editing your checkout

# the work exists, over there:
wsl -d MainguardEnv -u root -- ls /home/mainguard/mainguard/agents/$H
wsl -d MainguardEnv -u root -- git --git-dir=/home/mainguard/mainguard/repos/$H.git for-each-ref refs/heads/agent/
```

**Expect:** an agent directory, and an `agent/<something>` ref with a commit on it.

---

## 4. Verification runs in the jail — not on your machine

**Do:** let it finish and propose the work.

**Expect:** verification runs `.mainguard/verify` **inside the agent's container**, not on your host.

**Check:**

```powershell
wsl -d MainguardEnv -u root -- docker ps
wsl -d MainguardEnv -u root -- journalctl -u mainguardd -n 40 | Select-String verif
```

Host execution would be a rejection trigger. If you ever see the verify command run on Windows,
that's a serious finding.

---

## 5. The merge queue — the one that matters most

**Do:** go to the queue. The item should be there, verified. Note `main`'s sha first:

```powershell
cd $HOME\mg-testrepo
git rev-parse main
```

Click **Merge**.

**Expect:**

```powershell
git fetch                 # your checkout learns about the merge
git rev-parse main        # CHANGED
git log --oneline -3      # the agent's commit is now in YOUR checkout
```

This is the **only** point in the whole flow where the agent's work reaches your machine.

**This is the single most important check in phase 1.** For most of this project the Merge button
took the lease, recorded success, fired the stale cascade, and **ran no git at all** — the branch
reached terminal `Merged` with a durable record asserting a merge that never happened. "The UI said
merged" is exactly what the broken version did. **Only the sha counts.**

---

## 6. A branch that rewrites its own verify command is blocked

**Do:** on a second branch in the scratch repo, change `.mainguard/verify` to something trivially
passing (`echo ok`), commit, and have an agent take it through the queue.

**Expect:** it verifies **green** and is **still blocked**, surfaced for review. A branch that
rewrites its own verification is where a green result means least.

**Then:** acknowledge it and merge. Confirm `main` moves only after the acknowledgement.

**Also try:** acknowledging some *other* item id and confirm it clears nothing.

---

## 7. Stale cascade

**Do:** get two agents onto branches from the same base, both verified and queued. Merge one.

**Expect:** the other is **invalidated** — Merge unavailable, with a reason naming why.

**Check:** `git rev-parse main` moved exactly once. A second merge must not be possible without
re-verification.

---

## 8. Kill switch

**Do:** with at least one agent running, use the emergency stop.

**Expect:** every jail stops. Not "the first one".

**Check:** `wsl -d MainguardEnv -u root -- docker ps` — no agent containers left running.

This used to throw when two repos each had a `pr-7`, pausing one jail and leaving the other live.

---

## What to write down

For each step: what you did, what you expected, what happened. Two things specifically —

- **any message that states a cause** ("another instance is holding…", "the launch may have
  failed") — check whether it's true. This codebase has repeatedly shipped confident diagnoses that
  were guesses, and they cost more time than the bugs.
- **anything that looks hung** — how long you waited before deciding, and what the screen showed.

---

## Cleanup

```powershell
Remove-Item -Recurse -Force $env:TEMP\mg-test-p1
Remove-Item -Recurse -Force $HOME\mg-testrepo
wsl -d MainguardEnv -u root -- bash -c 'docker ps -aq | xargs -r docker rm -f'
```

Leaving the VM's mirror and agent repos is harmless — they are keyed by repo hash and a deleted
checkout simply never resolves again.
