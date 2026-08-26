# Mainguard — Target Company List (built, scored, contactable)

**Date:** 2026-08-20 · **Register:** brand (sales execution) · **Status:** working list, refresh quarterly
**Method:** [`Mainguard_Company_Sourcing_Playbook.md`](Mainguard_Company_Sourcing_Playbook.md) — this file is
the *output* the playbook always described but never contained. Scoring is the playbook's A/B/C/R/S model.
**Outreach against this list:** [`Beta_Outreach_Sequences_2026-08.md`](Beta_Outreach_Sequences_2026-08.md).
**Stage discipline:** this is **early-beta and feedback recruitment**, not selling. See
[`../Founding_User_Program_2026-08.md`](../Founding_User_Program_2026-08.md) for what we actually offer.

---

## 0. How to read this, and what changed

Every fact below is sourced from public material as of 2026-08-20. Facts marked **[inferred]** are
reasoning, not evidence — never repeat them to the company as if they were established. Facts marked
**⚠️** failed verification and must not be used at all.

**The single most important finding: the playbook's tier list was aiming one layer too high.** Topicus,
Thales, Nedap, Info Support, ilionx and Sopra Steria are all 500–2,250 people — far outside the 10–100
developer band. The companies that actually match the ICP were found via **Microsoft conference sponsor
lists** (Techorama NL/BE, VISUG, Azure Lowlands): 30–60-person Dutch and Belgian .NET consultancies and
govtech ISVs. That seam produced better targets than the three original tiers combined, and it is the
seam to keep mining.

**Five corrections to the playbook that would break outreach if repeated:**

| Playbook says | Reality | Source |
|---|---|---|
| Heutink ICT (Rijssen) | Merged into **Cloudwise** 1 Jan 2025, Hengelo. Also: zero software-engineering roles — not a target. | [cloudwise.nl](https://www.cloudwise.nl/nieuws/berichten/heutink-ict-en-cloudwise-gaan-samen-verder-onder-een-naam-cloudwise) |
| Movella/Xsens | Rebranded back to **Xsens**, March 2026 | [xsens.com](https://www.xsens.com/movella) |
| Luminis | Now **Yuma** — luminis.eu redirects to weareyuma.com | [weareyuma.com](https://www.weareyuma.com/en/about/about-us/background) |
| Betabit ⊂ Sopra Steria | **False.** Betabit is independent and is itself the acquirer (merged with Fenêtre, June 2026) | [betabit.nl](https://www.betabit.nl/over-ons/) |
| SciSports (Enschede) | **Zeist**, ~24 people, one open role and it's a graphic designer. Disqualified. | [scisports.com](https://www.scisports.com/contact/) |

**One scoring clarification.** The **B axis is about the operating system, not the language.** Mainguard's
Git client serves a C++/Qt shop on Windows perfectly well; what it does not serve is a Mac-only shop. Score
B on "are these developers on Windows," using Microsoft stack as the proxy — and downgrade explicitly
Mac-issuing companies (Q42, De Voorhoede, Framer, Weaviate) regardless of how good their agent story is.

### Scoring model (from the playbook §1)

| Axis | 0 | 1 | 2 |
|---|---|---|---|
| **A — agent adoption** | no evidence | AI-assisted coding mentioned (Copilot completions) | agentic CLIs / parallel sessions evidenced |
| **B — Windows/.NET** | Mac/Linux-only shop | mixed-OS | Microsoft-stack core |
| **C — compliance pressure** | none visible | client-contract accountability | regulated vertical / certified / public sector |
| **R — reachability** | cold, no path | 2nd-degree or community overlap | warm path, published contact, or a public programme |
| **S — size fit** | <10 or >250 devs | 100–250 devs | 10–100 devs (the ICP band) |

---

## 1. Tier A — open with these ten

Ranked by score, then by how fast the conversation can start. Every one of these has either a published
contact address or a public programme you can walk into.

### A1 · XPRTZ (Utrecht) — **score 9/10** (A2 B2 C1 R2 S2)

The cleanest verified band fit anywhere: *"Zakelijke vrienden: 30+"*, a roster that is almost entirely
engineers, and — their words — *"Wij hebben **geen sales afdeling**"*
([werken-met](https://www.xprtz.net/werken-met)), which tells you exactly how to approach them: as a peer,
never as a vendor.

- **Agents (A=2):** their public Technology Radar puts **Cursor in "Adopt"** (Tools → Adopt, item 9) —
  *"Een must-try naast Claude en Github Copilot!"* **Link [/expertise](https://www.xprtz.net/expertise),
  where the radar is embedded — `/radar` returns a blank page.** Their practitioner post names your failure
  modes unprompted: *"introduceerde bugs in bestaande functionaliteit"*, *"tunnelvisie… de agent gaat nieuwe
  code schrijven in plaats van bestaande code te hergebruiken"*, *"Veiligheidsmaatregelen — Beperk toegang
  en controleer de output."*
- **Compliance (C=1):** no certificates of their own found, but client logos include **DNB (De Nederlandsche
  Bank)**, **CIBG** and **DHD (Dutch Hospital Data)** — accountability arrives through the contract.
- **The hook:** [github.com/xprtz](https://github.com/xprtz) contains an **`advanced-git`** repo — "Deck for
  the Advanced Git Techniques talk." Someone there gives Git talks. That is your opening line.
- **Contact:** **Jasper Jak** — jasper@xprtz.nl, 06-10394044 (both published by them).
  ⚠️ **Roelant Dieben** is listed as "Partner & Directeur XPRTZ" on
  [azurelowlands.com](https://www.azurelowlands.com/) but appears nowhere on xprtz.net — verify before use.

### A2 · Blis Digital (Barendrecht) — **score 9/10** (A2 B2 C2 R1 S2)

The deepest agent evidence of any company found, Mews included. ~50 professionals; their own engineering
manifest argues for *"eigenaarsteams: vaak drie tot vijf engineers, aangevuld met AI-agents."*

- **Agents (A=2):** their **AI Native Lead Engineer** vacancy (posted 13 July 2026, live, €4,800–8,100,
  Barendrecht, 10+ yrs) states as a requirement *"je werkt dagelijks met een agentic harness als **Claude
  Code** of GitHub Copilot."* **Precision matters here:** *"Ervaring met het configureren van
  agent-omgevingen (guardrails, CLAUDE.md, MCP-servers, tool-permissies)"* sits under **"Nice to haves,"
  not "Must haves"** — do not say the ad "requires" it. Their GitHub repo `figma2code-mapping` is a skill
  for Claude Code.
- **They have already written your thesis:** *"**Wie de code samenvoegt, is er verantwoordelijk voor, ook als
  een agent hem schreef.**"* **⚠️ Attribution correction:** the line is verbatim and real, but it appears in
  the [**AI Native Lead Engineer vacancy**](https://www.blisdigital.com/vacatures/ai-native-lead-engineer),
  under "Hoe wij werken: AI-Native Engineering" — **not** in their
  [AI-native engineering manifest](https://www.blisdigital.com/ai-native-engineering), which does not
  contain it (its closest line is *"De mens stuurt, controleert en neemt verantwoordelijkheid"*). Attribute
  it to the vacancy. A Blis engineer would catch the mistake instantly, and that's the whole first impression.
- **Size:** *"uitgroeide tot een team van ruim 50 professionals"* (verified on their About page).
- **Stack (B=2):** C#, TypeScript, ASP.NET Core, Docker, MSSQL, Azure DevOps. **Compliance (C=2):** ISO 27001
  — DNV badge in the site footer; not cross-checked against DNV's registry.
- **⚠️ Risk flag:** a company this far ahead may have built its own answer already. That makes them a
  *better* feedback conversation and a *worse* conversion prospect. Go in wanting to learn what they built.
- **Contact:** **Christian Boer** — co-founder (2005, with Dirk-Jan Los and Richard Schot), now billed as
  *"AI engineer & partner."* **Note: Richard Schot is the CEO.** Boer is the right technical contact; don't
  mistake him for the chief executive.

### A3 · Exsertus (Kontich, BE) — **score 9/10** (A1 B2 C2 R2 S2)

*"Met onze 30 teamleden en onze vlakke hiërarchische structuur"* — in the band, flat, and part of the
Cronos Group. VISUG partner.

- **Stack (B=2), the most explicit of anyone:** "C#, .NET 8+, ASP.NET Core, Minimal APIs, Entity Framework
  Core, REST API, gRPC… Blazor, Angular, React, Vue.js… Azure App Services, Functions, Key Vault… **Azure
  DevOps, CI/CD, Git**."
- **Agents (A=1):** the same live ad lists "GitHub Copilot, Azure AI Foundry, Generatieve AI & LLM's" as
  required backend skills. Copilot-grade, not agentic-CLI-grade — treat their agent maturity as an open
  question, not an assumption.
- **Compliance (C=2):** clients are **RIZIV** (Belgian national health insurance), the **ZAS** hospital
  group, **SD Worx**, and **NIPRO** (medical devices).
- **Lowest-friction first conversation on the entire list:** **Tom Borloo** publishes his own address in the
  job ad — *"Hey, ik ben Tom Borloo, Technical Lead bij Exsertus… Contacteer me gerust via
  tom.borloo@exsertus.be."* (His title circulates as both "Technical Lead" and "Technisch Architect" on
  their own pages — use whichever appears on the page you're citing.)
- **Context to know before writing:** Exsertus is part of the **Cronos Groep**, stated in the same vacancy.
  Thirty people, but with a large parent behind them — which changes both the procurement path and who
  actually decides on tooling.

### A4 · Arcady (Zwolle) — **score 8/10** (A1 B2 C1 R2 S2)

**"84% van onze Arcadians is developer"**, published in the "Feiten" block on
[over-ons](https://arcady.nl/over-ons) — so with a roster in the low sixties, roughly **42–50 developers**.
One hour from Enschede. **⚠️ Lead with the 84% figure:** *"meer dan 60 Arcadians"* appears only in the page's
`og:description` meta tag, not in visible body copy, so quoting it as a headline stat is a small unforced
error.

- **Stack (B=2):** "Microsoft Stacks (denk aan **C#, ASP.NET Core, Azure, MS SQL**)"; they blog about
  [API-versioning in **.NET 10**](https://arcady.nl/updates/api-versioning-in-net-10-slimme-keuzes-voor-groeiende-api-landschappen).
- **Agents (A=1):** company-issued Copilot as a stated perk, a staffed **AI Officer**, monthly "AI AI AI"
  sessions. No agentic-CLI evidence — but their MD published a piece titled *"Iedereen kan straks code maken.
  Maar wie bepaalt of het de juiste code is?"* That is Mainguard's positioning in Dutch, written by them.
- **Reachability (R=2) — the best on the list.** They **run and host
  [DevSessions Zwolle](https://www.meetup.com/devsessionszwolle/) events** (say "host," not "founded" — the
  organiser of record isn't confirmed), and their own post dated 12 Aug 2026 says *"Op **donderdag 8 & 15
  oktober 2026** staan de deuren van ons kantoor open"* for
  [Hacktoberfest](https://arcady.nl/updates/arcady-doet-mee-aan-hacktoberfest-2026). You can meet these
  people in person twice this autumn without an introduction.
- **Contact:** **Sander ten Brinke** — Lead Developer / Microsoft MVP, s.tenbrinke@arcady.nl (byline and
  signature block on their own Hacktoberfest post).

### A5 · Visma Circle (Eindhoven / Sittard) — **score 8/10** (A2 B1 C2 R1 S2)

The highest purchase intent found anywhere, and a **named budget owner for exactly this category**.

- **Size:** exactly **14 engineers** in a 20-FTE engineering org, 90+ total
  ([Manager Product Engineering ad](https://werkenbij.vismacircle.nl/jobs/7938875-manager-product-engineering)).
  Bottom edge of the band — small enough that one conversation reaches everyone.
- **Agents (A=2), from a live ad** published 18 June 2026, last modified 10 Aug 2026 — **note the vacancy is
  titled "Product Engineer"; "AI Agent Engineer" is the role framing inside it, so don't call it "your AI
  Agent Engineer ad"**: *"wil jij de stap zetten van traditionele Software Engineer naar de rol van **AI
  Agent Engineer**"* and **"AI-agents (zoals Copilot en Claude), waarbij je gebruikmaakt van bijbehorende
  tools zoals OpenCode en Claude Code."** Plus the review half: *"Het **kritisch beoordelen en verifiëren van
  AI-output via code reviews om fouten of hallucinaties te voorkomen**."*
- **The budget line:** the Manager role sets a six-month objective of AI-native workflows as standard *"voor
  alle 14 engineers"* and explicitly owns *"de **budgetten voor de benodigde engineering tooling**."*
- **Compliance (C=2):** ISO 27001; they build **Djuma**, zaakgericht werken for gemeenten, waterschappen and
  provincies, 60,000+ users.
- **⚠️ Open question:** a literal ".NET"/"C#" keyword was **not found** — Microsoft/SharePoint yes. Treat the
  stack as an assumption to test in the first call, not a fact to assert.
- **⚠️ Don't cite their team page** — `/over-visma-circle` still ships lorem-ipsum placeholder cards
  ("Eric Brighton", "Something Cool"). Those are not people.

### A6 · Peter Cosemans / Euricom (Belgium — the site is **euri.com**, not euricom.com) — **a person, not a company**

**This entry was downgraded during fact-checking, and the correction changes who you write to.**

The signal is real: [`Euricom/poc-claude-code`](https://github.com/Euricom/poc-claude-code) carries
`CLAUDE.md`, `.mcp.sample.json`, `conductor.json`, `.worktreeinclude`, and a README section titled
**"Multi Agents Workflow Support"** comparing **three git-worktree-per-agent tools side by side — Cursor
worktrees (`.cursor/worktrees.json`), Worktrunk, and Workmux.** Someone has sat down and evaluated exactly
your category.

**But it is not an organisational practice.** The repo is 22 commits, **0 stars, 0 forks**, last updated
**19 March 2026** (five months stale), and via the GitHub API **every commit is by one author: Peter
Cosemans**. Euricom's 82-repo org also ships [`agent-skills`](https://github.com/Euricom/agent-skills) and
`ai-template-cc-typescript`, and they're a Techorama BE Gold partner — but "Euricom runs agent-per-worktree
workflows" is **not supported**. "Someone at Euricom has been experimenting with them" is.

**So treat this as a peer-to-peer conversation with one engineer, not a company target.** Peter Cosemans has
personally compared the tools in your category and written it down. That makes him a genuinely valuable
half-hour — he'll have opinions about all three, and he's exactly the kind of person the peer opener
(§2 of the outreach doc) exists for.

- **⚠️ Unresolved:** Euricom's headcount and any named engineering leader are unverifiable — euri.com is a
  JS-rendered SPA that returns empty content to fetchers. Cosemans' title there is unconfirmed. **A manual
  LinkedIn pass is required before treating Euricom as a company target at all.**

### A7 · Wigo4it (Den Haag) — **score 8/10** (A0 B2 C2 R2 S1–2)

The strongest compliance narrative on the list, and **genuinely greenfield** — zero incumbent agent tooling
found, which means no rip-and-replace and no existing vendor relationship to dislodge.

Non-profit ICT cooperative of the G4 municipalities (Amsterdam, Rotterdam, Den Haag, Utrecht) building
**Socrates**, the bijstand system — *"social welfare benefits for more than 110.000 citizens"*
([GitHub](https://github.com/wigo4it)). 140 total across 5 tribes / 9 squads / 4 chapters; **[inferred]**
60–100 engineers.

- **Stack (B=2), published by them:** "GIT • C# • AKS • .Net Core • Azure • Blazor • Oracle • Azure DevOps
  • Terraform."
- **Reachability (R=2):** a public [Tech Radar](https://techradar.wigo4it.nl/) updated monthly, and
  **Jean-Paul van der Ham — Tech Lead** owns it and is the publicly designated route for technology
  suggestions. That is an invitation. Techorama NL Gold partner.
- **Why they matter beyond the seat count:** squads own compliance directly. If provenance and verification
  land anywhere, it lands here first.

### A8 · Demcon (Enschede) — **score 7/10** (A0 B2 C2 R2 S1) · **local**

The tightest *stack* match on the list and a fifteen-minute bike ride away.

- **Stack (B=2):** multiple live **C#/WPF** roles — *"At least 3 years of experience with software
  development in **C# WPF**… (CI/CD, SCRUM, Agile, **git**, python)"*. GitHub corroborates: `libstored.net`,
  `ros2_dotnet`, `dotnet_cmake_module`, `DicomTestTools`. Desktop C#/WPF + Git + on-prem industrial is the
  closest thing to Mainguard's own shape found in the Netherlands.
- **Compliance (C=2):** ISO 9001, **ISO 13485** (medical devices), ISO 45001, ISO 27001 for management &
  support, plus a dedicated **defence & security division**. ISO 13485 + defence means auditable change
  history is a *regulatory obligation*, not a preference.
- **The corroborating tell:** they maintain [`cmake-sbom`](https://github.com/DEMCON) — "Guided SBOM
  generation from CMake", 64 stars. They already spend engineering time on supply-chain traceability.
- **Best route (R=2):** skip the company. **The `libstored` and `cmake-sbom` maintainers are named, active,
  reachable engineers doing exactly your kind of work.** `libstored` was pushed 11 Aug 2026, `cmake-sbom`
  1 Aug 2026. Open an issue, or write to a maintainer as a peer.
- **Agents (A=0):** not found. Expect to be educating, not competing.

### A9 · Nedap — Security Management group (Groenlo) — **score 7/10** (A0 B2 C2 R2 S1)

Target the market group, never the company — Nedap is 1,063 people organised into semi-autonomous market
groups, and the .NET one is strong: *"at least 5 years demonstrable experience with C# and .NET"*, "Azure
and Azure DevOps", **Duende IdentityServer**, **.NET MAUI**, Bicep. (Healthcare is Ruby/Vue; embedded is
Yocto/Rust. Don't pitch those.)

- **Reachability (R=2), with a caveat you should take seriously.** They run a public
  **["Talk to a Developer"](https://nedap.com/talk-to-a-developer/)** programme — *"Book an online
  appointment to phone one of our devs below. You can ask them anything during this informal chat… Fire
  away!"* It is open to outsiders. **But it is employer branding aimed at candidates, and the page carries a
  no-recruiters disclaimer** — so using it as a sales channel stretches its intent even though nothing
  forbids it. **If you book it, say why in the booking note**: that you're building a Git tool, not job
  hunting, and you want twenty minutes of a developer's view. Turning up under false pretences to a company
  that offers open access is precisely the wrong first impression in a country this small.
  Their [GitHub org](https://github.com/nedap) has 219 public repos with pushes within a day of research —
  a lower-risk route if you'd rather not use the programme at all.
- **Compliance (C=2):** Nedap Healthcare holds ISO 27001, NEN 7510, ISO 27701, ISO 27018, ISO 9001,
  ISAE 3402 type II.
- **⚠️ Do not repeat:** a claim circulated that Nedap job ads mention "Copilot, Cursor and Claude Code."
  **It could not be reproduced from any source.** Treat Nedap's agent adoption as unknown.

### A10 · Sigmax (Enschede, Kennispark) — **score 6/10** (A0 B2 C2 R0 S2) · **local, timing-sensitive**

Weak on reachability, strong on everything else, and it is on your doorstep.

- **Size (S=2):** *"meer dan 120 medewerkers… op het Kennispark in Enschede"* — the only original Tier 1
  company plausibly inside the band.
- **Stack (B=2), and the work is on-message:** their ".NET Software Engineer" ad covers CityControl including
  *"**migration to Azure cloud and converting Legacy .NET framework projects to .NET 6**."* A legacy-to-modern
  migration is precisely the high-diff-volume work an agent-assisted Git workflow sells into.
- **Compliance (C=2), unusually well documented:** a public
  [Compliancy Center](https://www.sigmax.nl/compliancy-center/) — ISO 27001 since 2018, ISO 9001, ISAE 3000,
  SOC 2, and a Statement of Applicability covering **BIO BBN 1**. Demonstrable **Wpg** (police data), DigiD
  and AVG compliance, 240–290+ municipalities plus NS. Wpg + BIO is about as regulated as Dutch civil
  software gets — and it means "the code never leaves the machine" is the *only* agent story they can adopt.
- **Timing signal:** **Sigmax is actively recruiting a CTO** with an explicit modernisation-and-budget
  mandate — *"multi-year plans and budgets for technology investments."* A vacant CTO seat with a
  modernisation brief is a buying window. **Track who fills it and write to them in their first month.**
- **Reachability (R=0):** no blog, no GitHub, no named speakers. Cold, but local — a Kennispark neighbour
  asking for twenty minutes is a different email than a cold vendor.

---

## 2. Tier B — worth the second batch

| # | Company | Score | Why | Contact / route |
|---|---|---|---|---|
| B1 | **Akkuro** (Deventer, "by Topicus") | 7 | The most on-message open role found anywhere: *"AI-Native Software Engineer"* requiring *"practical standards for **verification, traceability and responsible AI-assisted development**"* — in regulated finance ([job](https://akkuro.com/company/careers/job-listing-ai-native-software-engineer)) | Apply the role's own language back at them; 45 min from Enschede |
| B2 | **4DotNet** (Meppel + Nieuwegein) | 7 | Sells *"**Coding agent adoptie traject**"* and an *"AI development governance framework"* as productised services. Two Microsoft MVPs. **A design partner *and* a distribution channel** | **Edgar Tichelaar**, CEO/owner. Techorama NL Platinum |
| B3 | **Cloud Republic** (Utrecht) | ~7 | Their 19 Aug 2026 post says *"**ontwerp agent-rollen alsof het echte teamleden zijn**"*, *"behandel instructies als code. **Version ze, review ze**"*, and they sell a check built on *"waar ontbreekt **logging, ownership of change-traceability**?"* — your vocabulary, verbatim | ⚠️ Part of de Caesar Groep; standalone headcount not found |
| B4 | **Blinqx / Scan Sys** (Barendrecht) | 6 | Scan Sys unit is *"een hecht team met 45 collega's"* on .NET 8, C#, ASP.NET MVC, Angular, Azure DevOps, with a **"Senior .NET Developer (AI first)"** role open | Cold; via the job ad |
| B5 | **Innvolve** (Vught) | 6 | Open role: *"Ben jij een **.NET-developer die AI-tools en agents net zo vanzelfsprekend inzet als je IDE**?"* Built their own agent, gated by an AVG/AI-Act review. ISO 27001; Radboudumc, CZ, VECOZO, CBR | Cold. **[inferred]** dev group under 20 — weak band fit |
| B6 | **Intercept** (Zwolle) | 7 | Deepest compliance set found (ISO 27001:2022, ISO 9001, SOC 2 Type 1, CSA STAR L1, all downloadable). One of three Benelux firms with both Azure Expert MSP and **Agentic DevOps** accreditations | **Gregor Suttie** — Azure Architect & MVP, published on agentic workflows 19 Aug 2026 |
| B7 | **Yellowtail Conclusion** (Naarden) | 5 | Financial software (Hypact, PensioenSuite, Key Control Dashboard) for Rabobank, Aegon, ABN AMRO, APG. **Back-end team split across NL, South Africa and India** — which makes provenance a live operational problem, not a hypothetical | Currently hiring a CISO — write to them once seated |
| B8 | **Ecare / PUUR.** (Enschede) | ~6 | Local. Azure + SQL Server, NEN 7510, ISO 27001 since 2017, ISAE 3402 II | **Dennis Snippert** — directeur product & technologie. Visma-owned |
| B9 | **xxllnc** (Hengelo) | ~6 | Local. 380 group-wide, but the Veenendaal Belastingen team is *"een klein, specialistisch development team"* on **C# (.NET 10)** | Cold; target the team, not the group |
| B10 | **Qurentis** (Valkenswaard) | ~6 | Holds an **ISAE 3402 Type 2 scoped specifically to "het kritische proces softwareontwikkeling."** The single best "compliance constrains the development process" artefact found — worth a call purely to learn how they think about it | Cold |
| B11 | **JCC Software** (Oldenzaal) | ~5 | Only ~9 engineers — below band — but local, and **CTO Robin van Ommeren** is named with a phone number. A cheap, friendly first practice call | Direct |

---

## 3. Lighthouse and channel accounts — not band-fit, high leverage

These will not be your first paying teams. Two of them are the best *individual* contacts you have, and one
is a distribution channel.

**Mews (~1,500) — the best individual contact on this entire list.** Three engineers published in 2026 about
running Claude Code on a **PCI-scoped financial backend** and a **20,000-file C# monolith**: *"During
execution we let it write almost 100% of the code, while human review and oversight remained critical"*;
*"we created a concise **CLAUDE.md**… up to 200 lines"*; *"**GitHub Copilot's CI/CD review caught gaps** the
local one missed"*; and — the line to open with — *"On other occasions, **Claude Code straight up ignored
rules clearly defined in CLAUDE.md**"*
([post](https://developers.mews.com/ai-driven-development-without-losing-quality-and-control/)).
**Fredrik Claesson** built and open-sourced [**ContextKing**](https://github.com/Fredrik-C/ContextKing), a
semantic navigation layer for agents in large C# repos, measuring 10.5× token reduction. **He is the obvious
first contact anywhere in this document** — he has built a tool in your adjacent space, in your language, on
your problem. ⚠️ Whether Mews developers are on Windows or Mac is unverified and load-bearing.

**Info Support (500+) — the channel play, and the best single event.** They sell **Claude Code training by
name**: *"Claude Code: An agentic software experience"*, €895, next running 17 Sep 2026, covering *"Agentic
patterns: hooks, MCP plugins, multi-agent coordination"* — with prerequisites telling students to budget
*"tussen de 5 en 20 miljoen tokens"* for the day. They ship [`promptyard`](https://github.com/infosupport/promptyard)
("installs agents, prompts, and skills from Git repositories… supports Claude, GitHub Copilot, and OpenCode").
**Crucially they sell training, not a competing platform** — which makes them a channel rather than a rival.
- **Willem Meints — Chief AI Architect, Microsoft AI MVP**, *"helping engineering teams adopt agentic AI"*,
  promptyard co-author, GitHub [@wmeints](https://github.com/wmeints). **Highest-value single introduction
  available to you.**
- **The event:** **[Bitbash](https://www.bitbash.nl/)** — free, their Veenendaal HQ, 22–23 Jan 2027,
  ".NET, Azure, dataplatformen en AI." Submit a talk.

**Topicus (1,600) — team-level entry, and the objection stated in advance.** Nearly all their developers use
AI ( *"Tegenwoordig gebruiken bijna al onze developers AI"* ), they hire **"AI-first Fullstack Developer"**
roles, and they run an **organisation-approved tooling allowlist** — a procurement gate you must clear. Their
AI-lead **Egbert van der Veen** is on record: *"We werken met modellen die binnen Europa worden ontwikkeld en
gehost. **Data mag niet naar Big Tech**."* That is simultaneously the biggest objection you will face and the
sharpest wedge you have — Mainguard's whole architecture is the answer to it. **He is the entry point.**

**ilionx (~1,500) — the governance mirror.** Established an **AI-Office on 24 July 2026** under
**Arno Titawano**, building *"een centraal meld-, advies- en toetsingspunt voor alle AI-toepassingen"*,
targeting **ISO/IEC 42001**, explicitly to prevent *"shadow AI, losse tools die zonder centraal overzicht
naast elkaar ontstaan."* Competitor-adjacent (they sell "Hyper Developer"), so pitch nothing — but this is
the single best free education available on how a large Dutch buyer will evaluate you in 2027.

---

## 4. Deprioritised, with reasons (so nobody re-adds them)

| Company | Why not |
|---|---|
| **Thales Nederland** | Java/Scala/C++ on **GitLab**, group-standardised. **Zero .NET.** Clearance-gated (Dutch nationality required on R&I roles); **[inferred]** much code sits on accredited networks. Long-cycle at best. ⚠️ CTO Martin Koning ter Heege's appointment date is unverified |
| **Betabit** | ⚠️ **Competitive.** The Fenêtre merger launched **SiliconCode** (siliconcode.ai), an AI code-generation platform. Purest Microsoft shop otherwise; keep for market intelligence, and note **Frits Vink — IT Manager & CISO** (there is no CTO title) |
| **Yuma (ex-Luminis)** | ⚠️ **Competitive** — sells "AI-Assisted Software Development" coaching and ships its own agent product, **Akgents**. JVM/AWS-first; zero .NET repos; no .NET vacancy. **But Jettro Coenradie publishes well** on spec-driven development — engage him online, don't sell |
| **Q42** | ~104 people and a real agent story in the open ([`superpowers-agent-teams`](https://github.com/Q42/superpowers-agent-teams), [`sqill`](https://github.com/Q42/sqill)) — but they publish `macos-image-templates`, i.e. **[inferred]** the dev machines are Macs. Wrong OS. Great people to engage on the online track |
| **De Voorhoede** | 19 people, pure JS/Vue, **no C#, no .NET, no Windows tooling anywhere**. Say it plainly and move on. Their lead front-end dev **Sjoerd** wrote the best public Claude Code statement found in the Netherlands — comment on his post, don't pitch him. ⚠️ Their ISO 27001 is inferred from a DNV badge and a filename — do not assert it |
| **Adyen, Mollie, bol, Coolblue, Picnic, Weaviate, Channable, Framer** | All disqualified on size. Note that the **bol .NET-heritage premise did not survive verification** — they are GCP/Java/Kotlin/Go now; retire it. Coolblue is genuine C# but **on AWS, not Azure** |
| **Cloudwise, SciSports, LioniX, Decos, Aikido, Cormel, Impulse, Elmos, Shift2** | Wrong company type, below band, or developers are offshore (Decos: Pune) |

**Picnic is not a target — Picnic is a quote.** CTO Daniel Gebler and Gianluca Bandini have publicly framed
your exact thesis: *"**governance is embedded in the workflow, not imposed as a months-long gate**"*, and
*"Peer review, automated testing, staged rollouts, and access controls remain non-negotiable"*
([post](https://jobs.picnic.app/en/blogs/the-architecture-behind-ai-impact)). Cite it. A Dutch CTO saying it
in 2026 is worth more in a Dutch sales conversation than anything you can say about yourself.

---

## 5. Where the next forty come from

The seams, ranked by what actually worked:

1. **Microsoft conference sponsor lists — confirmed, by a wide margin.** Techorama NL/BE and VISUG partner
   pages produced ten of the twelve best targets in two fetches. **Still unmined: Azure Lowlands (organised
   out of XPRTZ), devNetNoord (Groningen, 649 members), dotNed Saturday, Update Conference Prague.** Work
   these next.
2. **Dutch/Belgian .NET consultancies, 20–80 people.** Highest yield per hour. Nearly every Tier A entry.
3. **Govtech ISVs.** Visma Circle and Wigo4it both came from here. DigiD/BIO obligations make the
   provenance conversation trivial to start.
4. **Dutch healthcare ISVs — largely failed, don't repeat it.** The compliance half holds (Gerimedica,
   Adapcare, Ecare, Qurentis, SDB, ZorgNed all carry NEN 7510 or ISO 27001) but **the .NET half does not**
   (Gerimedica is Java/Spring; Adapcare is TypeScript/React on AWS), and **not one healthcare ISV showed any
   AI-coding-agent evidence** — product AI everywhere, dev-tooling AI nowhere.
5. **Fintech — mixed.** Ohpen is AWS, deprioritise. Akkuro and Blinqx came out of it.

**Two in-person touchpoints worth planning around, both verified:**

- **Techorama Netherlands** — Utrecht, **26–28 October 2026** ("Medieval Edition"). Precisely: **26 Oct is
  the workshop day** at Van der Valk hotel Utrecht; **the conference days are 27–28 Oct** at
  Kinepolis/Jaarbeurs. Betabit, Wigo4it, 4DotNet, Intercept, Arcady and Blis are all partners. Six targets
  in one building.
- **Hacktoberfest at Arcady's Zwolle office — 8 and 15 October 2026.** An hour away, no introduction needed.
- (**Bitbash**, Info Support HQ, 22–23 Jan 2027 — free, and a talk slot to aim at.)

---

## 6. Cross-cutting: the one objection that decides everything

**EU hosting and data residency is the qualifying criterion, not a nice-to-have.** It appeared independently
at every serious target:

- Topicus: *"Data mag niet naar Big Tech."*
- Adyen: runs **on-prem LLMs** to avoid *"information leakage through the prompt."*
- Blis Digital's own FAQ: *"Onder de AVG mag je persoonsgegevens niet zonder grondslag bij een externe partij
  laten verwerken, **en een AI-codetool is zo'n partij**."*
- And it is structurally unavoidable at Sigmax (Wpg/BIO), Nedap Healthcare (NEN 7510), Demcon (ISO 13485 +
  defence) and Thales.

Mainguard's local-first architecture, BYOK-in-the-OS-keyring, and default-deny egress are the direct answer
to all four. **Lead with it in the Netherlands.** Being Dutch, local-first, and source-available is the
leverage — and it is the one axis where no Mac-only, cloud-first, US-funded competitor can follow.

---

## 7. Working list hygiene

- **Re-verify before every send.** Job ads expire, people move, companies merge (three did while this
  research ran). The quotes above are dated 2026-08-20.
- **Never assert an [inferred] or ⚠️ item to the company.** Ask about it instead — it makes a better
  question than a claim.
- **Open gaps worth a manual LinkedIn pass, in priority order:** Euricom headcount + a named person (unlocks
  a top-three target); Cloud Republic standalone headcount; Yellowtail headcount; whether Visma Circle is
  actually .NET; whether Mews developers are on Windows or Mac.
- **Track outcomes in one place** — company, score, contact, date sent, reply, call held, what they said.
  The interview notes are the real asset here; the list is just the index.
