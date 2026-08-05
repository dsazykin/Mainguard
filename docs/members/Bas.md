# GitLoom: Team Intake Form

> **How to use:** Copy this file to `intake/<your-first-name>.md`, fill it out, and open a PR (or drop it in the shared folder). Be honest about both skill *and* interest — we assign to `Team_Structure.md` pods by matching these against each pod's "Needs." There are no wrong answers; "I've never done X but want to learn it" is useful signal.
>
> **Scale:** 1 = never touched it · 2 = can follow along · 3 = productive with docs · 4 = strong, could lead it · 5 = deep expert.

---

## 0. About You

- **Name:** Bas
- **Time zone / working hours:** Amsterdam
- **Years of professional experience:** 1
- **Current strongest language(s):** Java, Python
- **Anything about how you work best (async, pairing, deep-focus blocks, etc.):** No preference

---

## 1. Skill Self-Rating (1–5)

Rate each. Leave a short note where useful.

| Area | Rating | Note (optional) |
|---|---|---|
| C# / .NET (modern, .NET 10) |2|Never touched C# but as its very similair to Java im exited to try |
| Avalonia / WPF / desktop UI |2|Also never really used avalonia but also seems similair to HTML so I should be able to follow with some research |
| MVVM (`CommunityToolkit.Mvvm`) |1|Never used |
| Async / concurrency / threading |2|Did some Java concurrency |
| gRPC / protobuf / RPC design |1|Never used |
| Git internals (plumbing, worktrees, rebase, merge) |2|I can follow along after some refreshing and research |
| LibGit2Sharp / driving git via CLI |1| |
| Docker / container hardening (userns, seccomp) |1| |
| WSL2 / Linux systems administration |1| |
| Networking / firewalls / egress control |2|Did networking in module 3, am definitely down to learn more |
| C / P/Invoke / native interop |2|Did some C in module 5 with operating systems |
| Terminal / PTY / VT100 / ANSI emulation |1| |
| Rendering / Skia / custom drawing / graphics |1|Rather interesting though |
| SQLite / EF Core / data modeling |3|Used in module 4 for the website backend |
| Security engineering (threat modeling, secrets, sandboxing) |2|Same as above, I am not very good at it but I do find it very interesting |
| LLM / agent tooling (Claude Code, agent CLIs, prompt design) |2|I never really use AI unless I need to, and I do not really like to use it if I (feel like) can do it myself |
| Build / release / packaging (Velopack, installers, CI) |1| |
| Testing (unit, integration, conformance/golden-transcript) |2|I did unit testing in Java |

---

## 2. Pod Interest Ranking

Rank the pods from `Team_Structure.md` by how much you'd *want* to work on them (1 = most, 4 = least). Interest matters as much as skill.

| Rank (1–4) | Pod | Core work |
|---|---|---|
|2| **Engine** | WSL2/Docker sandbox, Repo Provisioner, gRPC server, AI Gateway, egress firewall, credential isolation |
|4| **Swarm (2A)** | Coordinator/Worker orchestration, merge queue + re-verification, plan approval, session durability |
|3| **UI (2B)** | Avalonia, Dock.Avalonia, activity bar, 3-way merge UI, diff/staging, analytics |
|1| **Terminal** | Porta.Pty, libvterm P/Invoke, VT conformance harness, Skia grid renderer |

- **Top pick — why:** Seems most relevant to my future plans on what I want to go into.
- **Anything you'd actively prefer NOT to work on (and why):** N/A

---

## 3. Ownership & Growth

- **A subsystem I'd confidently *own* (be the go-to person, bus-factor-critical) today:** I don't think i would own any
- **A subsystem I'd want to *grow into* over the next 6 months:** I am not sure yet, would have to dig deeper into the subjects within gitloom first
- **Would you be comfortable as a pod lead / owning a bus-factor-critical component?** (Yes / With support / Not yet) with support
- **Solo deep-focus vs. tight pairing — where are you more effective?** tight pairing

---

## 4. Context & Logistics

- **Have you worked on: desktop apps? / developer tools? / anything agent- or LLM-related?** No
- **Familiarity with this codebase so far (1–5):** 2
- **Have you read `GitLoom_Roadmap.md` and `Implementation_Plan.md`?** (Yes / Skimmed / Not yet) Yes
- **Any part of the plan you have questions or disagreements about?** Not as of now
- **Anything else we should know to place you well:** No
