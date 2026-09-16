# Mainguard: Team Intake Form

> **How to use:** Copy this file to `intake/<your-first-name>.md`, fill it out, and open a PR (or drop it in the shared folder). Be honest about both skill *and* interest — we assign to `Team_Structure.md` pods by matching these against each pod's "Needs." There are no wrong answers; "I've never done X but want to learn it" is useful signal.
>
> **Scale:** 1 = never touched it · 2 = can follow along · 3 = productive with docs · 4 = strong, could lead it · 5 = deep expert.

---

## 0. About You

- **Name:** Samir Pronto
- **Time zone / working hours:** GMT+2
- **Years of professional experience: 1**
- **Current strongest language(s): Python, Java, HTML, C++, C# **
- **Anything about how you work best (async, pairing, deep-focus blocks, etc.):** No particular likes or dislikes

---

## 1. Skill Self-Rating (1–5)

Rate each. Leave a short note where useful.

| Area | Rating | Note (optional) |
|---|---|---|
| C# / .NET (modern, .NET 10) | 3 | Made a few applications with this in the past, somewhat familiar |
| Avalonia / WPF / desktop UI | 3 | Same as above for WPF |
| MVVM (`CommunityToolkit.Mvvm`) |1 | No particular experience |
| Async / concurrency / threading | 2 | Am used to it in Python, Java and C++|
| gRPC / protobuf / RPC design |1| Seems interesting though, could learn it |
| Git internals (plumbing, worktrees, rebase, merge) | 2 | Not super knowledgable, but with documenetation feel like I could help|
| LibGit2Sharp / driving git via CLI | 1| Never used it, like the description though and would like to learn |
| Docker / container hardening (userns, seccomp) | 2 | Very elementary experience, but again something I would like to be more knowledgable in|
| WSL2 / Linux systems administration | 1 | Similar to above |
| Networking / firewalls / egress control | 2 | Some experience from Mod 3, but nothing much outside of that, would also be nice to learn|
| C / P/Invoke / native interop | 2 | Am somewhat familiar with C, havent used it much directly though, no experience with P/Invoke |
| Terminal / PTY / VT100 / ANSI emulation | 1 | No experience |
| Rendering / Skia / custom drawing / graphics | 1|  No experience, but seems cool to learn|
| SQLite / EF Core / data modeling | 3| Some SQLLite and data modeling experience from our university work |
| Security engineering (threat modeling, secrets, sandboxing) | 2 |Similar to above, however likely not in the exact context meant here  |
| LLM / agent tooling (Claude Code, agent CLIs, prompt design) | 2| Have not really done anything with agent tooling in the past, though have experience with LLMS, and seems cool to learn |
| Build / release / packaging (Velopack, installers, CI) | 2 | Some experience, not with Velopack though |
| Testing (unit, integration, conformance/golden-transcript) | 2 | Some experience, but would prefer not to work on this

---

## 2. Pod Interest Ranking

Rank the pods from `Team_Structure.md` by how much you'd *want* to work on them (1 = most, 4 = least). Interest matters as much as skill.

| Rank (1–4) | Pod | Core work |
|---|---|---|
| 2 | **Engine** | WSL2/Docker sandbox, Repo Provisioner, gRPC server, AI Gateway, egress firewall, credential isolation |
| 3 | **Swarm (2A)** | Coordinator/Worker orchestration, merge queue + re-verification, plan approval, session durability |
| 4| **UI (2B)** | Avalonia, Dock.Avalonia, activity bar, 3-way merge UI, diff/staging, analytics |
| 1| **Terminal** | Porta.Pty, libvterm P/Invoke, VT conformance harness, Skia grid renderer |

- **Top pick — why:** I want to go into Computer and Embedded Engineering in future, and this has the closest overlap to what I want to work towards, so I feel like it would be the best skillset for me to have
- **Anything you'd actively prefer NOT to work on (and why):** The UI, because it is not relevant I feel to anything I would really want to work on in the future professionally, outside of personal projects.

---

## 3. Ownership & Growth

- **A subsystem I'd confidently *own* (be the go-to person, bus-factor-critical) today:** I dont feel like I have the skillset to be fully 100% confident in any today.
- **A subsystem I'd want to *grow into* over the next 6 months:** Any system to do with either low-level libraries, C code or linux operating system systems like the GloomOS linux distro
- **Would you be comfortable as a pod lead / owning a bus-factor-critical component?** (Yes / With support / Not yet) With support
- **Solo deep-focus vs. tight pairing — where are you more effective?** Solo deep-focus

---

## 4. Context & Logistics

- **Have you worked on: desktop apps? / developer tools? / anything agent- or LLM-related?** Have worked on desktop apps for some business, mainly to do with archiving documents on the cloud using various services, often Firebase, displaying information in user friendly ways and handling user credentials
- **Familiarity with this codebase so far (1–5):** 2
- **Have you read `Mainguard_Roadmap.md` and `Implementation_Plan.md`?** (Yes / Skimmed / Not yet) Skimmed
- **Any part of the plan you have questions or disagreements about?** Not particularly 
- **Anything else we should know to place you well:** N
