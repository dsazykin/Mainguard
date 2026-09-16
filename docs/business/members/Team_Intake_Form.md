# Mainguard: Team Intake Form

> **How to use:** Copy this file to `intake/<your-first-name>.md`, fill it out, and open a PR (or drop it in the shared folder). Be honest about both skill *and* interest — we assign to `Team_Structure.md` pods by matching these against each pod's "Needs." There are no wrong answers; "I've never done X but want to learn it" is useful signal.
>
> **Scale:** 1 = never touched it · 2 = can follow along · 3 = productive with docs · 4 = strong, could lead it · 5 = deep expert.

---

## 0. About You

- **Name:**
- **Time zone / working hours:**
- **Years of professional experience:**
- **Current strongest language(s):**
- **Anything about how you work best (async, pairing, deep-focus blocks, etc.):**

---

## 1. Skill Self-Rating (1–5)

Rate each. Leave a short note where useful.

| Area | Rating | Note (optional) |
|---|---|---|
| C# / .NET (modern, .NET 10) | | |
| Avalonia / WPF / desktop UI | | |
| MVVM (`CommunityToolkit.Mvvm`) | | |
| Async / concurrency / threading | | |
| gRPC / protobuf / RPC design | | |
| Git internals (plumbing, worktrees, rebase, merge) | | |
| LibGit2Sharp / driving git via CLI | | |
| Docker / container hardening (userns, seccomp) | | |
| WSL2 / Linux systems administration | | |
| Networking / firewalls / egress control | | |
| C / P/Invoke / native interop | | |
| Terminal / PTY / VT100 / ANSI emulation | | |
| Rendering / Skia / custom drawing / graphics | | |
| SQLite / EF Core / data modeling | | |
| Security engineering (threat modeling, secrets, sandboxing) | | |
| LLM / agent tooling (Claude Code, agent CLIs, prompt design) | | |
| Build / release / packaging (Velopack, installers, CI) | | |
| Testing (unit, integration, conformance/golden-transcript) | | |

---

## 2. Pod Interest Ranking

Rank the pods from `Team_Structure.md` by how much you'd *want* to work on them (1 = most, 4 = least). Interest matters as much as skill.

| Rank (1–4) | Pod | Core work |
|---|---|---|
| | **Engine** | WSL2/Docker sandbox, Repo Provisioner, gRPC server, AI Gateway, egress firewall, credential isolation |
| | **Swarm (2A)** | Coordinator/Worker orchestration, merge queue + re-verification, plan approval, session durability |
| | **UI (2B)** | Avalonia, Dock.Avalonia, activity bar, 3-way merge UI, diff/staging, analytics |
| | **Terminal** | Porta.Pty, libvterm P/Invoke, VT conformance harness, Skia grid renderer |

- **Top pick — why:**
- **Anything you'd actively prefer NOT to work on (and why):**

---

## 3. Ownership & Growth

- **A subsystem I'd confidently *own* (be the go-to person, bus-factor-critical) today:**
- **A subsystem I'd want to *grow into* over the next 6 months:**
- **Would you be comfortable as a pod lead / owning a bus-factor-critical component?** (Yes / With support / Not yet)
- **Solo deep-focus vs. tight pairing — where are you more effective?**

---

## 4. Context & Logistics

- **Have you worked on: desktop apps? / developer tools? / anything agent- or LLM-related?**
- **Familiarity with this codebase so far (1–5):**
- **Have you read `Mainguard_Roadmap.md` and `Implementation_Plan.md`?** (Yes / Skimmed / Not yet)
- **Any part of the plan you have questions or disagreements about?**
- **Anything else we should know to place you well:**
