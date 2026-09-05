# Phase 2 — hands-on test procedure

**What phase 2 changes.** The coordinator no longer writes the plan. It spawns workers freely
within the caps; **each worker inspects the repo, writes its own plan, presents it, and stops** until
you approve. Rejection is feedback, not death — the worker revises and re-presents, up to 3 times,
then escalates.

**Not merged.** This is PR **#290**, branch `feat/coordinator-phase-2-worker-authored-plans`.

---

## Setup

```powershell
cd C:\Users\yikes\Code\Mainguard
git fetch origin
git checkout feat/coordinator-phase-2-worker-authored-plans
dotnet build
$env:MAINGUARD_DATA_ROOT = "$env:TEMP\mg-test-p2"
dotnet run --project Mainguard.Pro.App
```

Use the same scratch repo as phase 1, and read that guide's **"Where the agent's work actually
lives"** section first — the VM paths and the `$H` repo-hash variable used below are defined there.
Do phase-1 steps 1–2 (app starts, coordinator starts) before anything here; if those don't work,
nothing below will, and it won't be phase 2's fault.

**Testing note:** read *"Running the test suite while an agent is up"* in the phase-1 guide before
running any Docker-backed test — it covers the unfiltered-run trap, the two Docker daemons, and how a
severed jail masquerades as a provider outage.

**Shell note:** Windows PowerShell 5.1 has **no `&&`** — one command per line. Separately, and for a
different reason, `.mainguard/verify` has no shell either: it is argv, so a chained command needs the
`sh -c "…"` wrapper phase 1's setup uses.

**Toolchain:** the scratch repo declares `.mainguard/toolchain` = `python-3`. Confirm **Settings →
Toolchains** shows Python 3 installed before starting anything — a worker whose toolchain is missing
refuses to spawn, and that refusal will look like a phase-2 gate misfiring when it isn't one. The
install lives in the VM at `/home/mainguard/mainguard/toolchains`, so it survives changing
`$env:MAINGUARD_DATA_ROOT` and you only do it once across all three phases.

**Where everything below happens:** left rail → **Coordinator**. Its tooltip reads *"Coordinator —
plan approvals, chat, and the merge queue"*.

You talk to the coordinator in its **terminal** — the big pane in the middle, the real CLI in its own
sandbox. The **plan gate sits directly above that terminal**, between the Start/Stop toolbar and the
terminal itself, and it is **not there when nothing is waiting**. It appears when a worker presents a
plan, when a worker escalates, or when the daemon reports backpressure — and it collapses again once
you have cleared everything, so if you see no gate, nothing is waiting on you.

It is above the terminal on purpose: the thing the gate exists to explain is a *silence in that
terminal*. When blocked workers fill the worker cap the coordinator stops spawning, and the pane you
are staring at goes quiet with no explanation. The decision and the reason for the silence belong in
the same field of view. (The merge queue on the right is the wrong home for it — that rail is work
already finished, and approval is the gate *before* work starts.)

---

## 1. The worker writes the plan, not the coordinator

**Do:** ask the coordinator for something that needs looking at the code first:

> "Improve the error handling in calc.py."

**Expect:** the coordinator spawns a worker **without asking you first** (spawning is free within
the caps). Then a **plan card** appears with **Scope** and **Approach** filled in.

**The thing to actually judge:** does the plan describe *this* repo? It should name real files and
real functions, because the worker read them. A plan that could have been written without opening
the repo means authorship didn't really move — the whole point of phase 2 is that the coordinator has
no worktree and no view of the code, so its plan would have been a guess.

---

## 2. It genuinely blocks

**Do:** leave the plan un-approved. Wait a couple of minutes.

**Expect:** **no work happens.** No commits, no branch, no file changes.

**Check — and this has to be done in the VM, not your checkout.** Looking at your own working tree
proves nothing here: the agent's work never lands there before a merge, so it would look identical
whether the gate held or failed completely. That is the trap; look where the work would actually be:

```powershell
# the per-agent repo: does it exist, and does it have any agent branch with commits?
wsl -d MainguardEnv -u root -- ls /home/mainguard/mainguard/agents/$H
wsl -d MainguardEnv -u root -- git --git-dir=/home/mainguard/mainguard/repos/$H.git for-each-ref refs/heads/agent/
```

**Expect:** no new `agent/*` ref for this worker, and no commits in its per-agent repo. A jail may
exist (the worker was spawned, and it read the repo to write its plan) — that is fine. What must
*not* exist is committed work.

**This is the load-bearing test of phase 2.** The design point is that a blocking *call* an agent
can decline to make is a convention, not a boundary — so the daemon withholds the task entirely
until the plan is approved. If the worker starts anyway, the gate is decorative.

---

## 3. Reject with feedback → it revises

**Do:** click **Reject** and give a specific reason:

> "Don't add a dependency. Use the standard library only."

**Expect:** the worker does **not** die. It comes back with a **revised plan** that reflects your
feedback — and you should be able to see it did, not just that a new card appeared.

**Then approve** the revision and let it work.

**This is the interaction most worth judging.** Rejection is meant to *improve* the plan rather than
discard the attempt. If a rejection kills the worker, or the revision ignores what you said, that's
the finding.

---

## 4. The revision limit

**Do:** on a fresh request, reject **four times**.

**Expect:** rejections 1–3 produce revisions. The **fourth** stops and escalates to you instead of
looping forever.

**Why it exists:** an unbounded reject→revise loop burns budget and wall-clock indefinitely, and a
worker that keeps producing plans you dislike will do so forever. The limit lives in
`CoordinatorLimits`, **not in a prompt** — a limit an agent is merely told about is a suggestion.

---

## 5. Blocked workers hold their slot — backpressure

**Do:** ask for several independent pieces of work so multiple workers spawn. Approve none.

**Expect:** once the cap (`MaxActiveWorkers`, default 6) is reached by blocked workers, the
**coordinator stops spawning** and says so. Not a hang — backpressure.

**Judge this one, because it reverses the intuitive answer.** "Blocked workers are idle, exempt
them" is wrong: the cap is a *resource* cap and a blocked worker still holds its jail, tmpfs,
network segment and worktree. Exempting them lets the coordinator spawn unboundedly many
resource-consuming workers exactly when you're too busy to approve.

**Specifically check the stall is legible.** A silent stall is indistinguishable from a hang. At the
top of the gate you should get a bordered banner reading *"The coordinator has stopped spawning — it
is waiting on you"* over the daemon's own sentence, e.g. *"6 workers are waiting on your approval.
The worker cap (6/6) is full — the coordinator has stopped spawning until you clear plans."* That
sentence is **carried from the daemon, not recomputed in the UI**, deliberately: the number that
refuses the coordinator its next spawn and the number you read have to be the same number.

Below it there is a second, quieter line — *"N plans pending — the oldest has waited M min"* — which
only appears at **3 or more**. That one is the ageing line, not the stall line; it is fine for it to
stay quiet at 1–2 because the cards themselves are right there. Say if you disagree.

**And check you can actually clear it.** With six workers blocked you should get **six cards**, each
with its own Scope, its own worker name and its own Approve/Reject — not one card for the head of the
queue. Five unreachable decisions holding the cap shut would be the same stall with extra steps.

---

## 6. Approved work still goes through the queue

**Do:** approve a plan, let the worker finish.

**Expect:** everything from phase 1 still applies — verification in its own jail, then the merge
queue, then **you** merge. Phase 2 added a gate; it didn't remove one.

**Check `main` moved by sha**, same as phase 1 step 5 — `git fetch` first, then `git rev-parse
main` in your checkout. That merge is the only moment the work crosses from the VM to your machine.

**Also worth confirming:** an unapproved worker cannot merge **even if it somehow verified green** —
the plan gate is ANDed into the merge queue alongside the changed-test-command gate and the
flagged-change gate. Three independent gates; all must pass.

---

## 7. Restart mid-flight

**Do:** with a plan presented and un-approved, close the app and reopen it.

**Expect:** the pending plan is still there, still blocking. Approve it and the worker picks up.

**Why:** the plan store is daemon-side, not UI state. If a restart loses a pending plan, or worse
releases the task, that's a real defect.

---

## What to write down

- **Did the plan describe the real repo?** (step 1) — the single best signal that authorship moved.
- **Did rejection actually improve the next plan?** (step 3)
- **Was the stall legible enough that you'd know to go look?** (step 5)
- Anything where you couldn't tell whether the system was working or stuck.

---

## Cleanup

```powershell
Remove-Item -Recurse -Force $env:TEMP\mg-test-p2
wsl -d MainguardEnv -u root -- docker ps -aq | ForEach-Object { wsl -d MainguardEnv -u root -- docker rm -f $_ }
```
