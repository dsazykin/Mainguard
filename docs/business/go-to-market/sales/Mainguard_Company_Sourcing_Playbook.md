# Mainguard — Target-Company Sourcing Playbook

**Date:** 2026-07-11 · **Register:** brand (sales/GTM execution layer)
**Reads under:** `docs/business/go-to-market/Mainguard_Master_Market_Document_2026-07.md` — the ICP (§4.5),
personas (§4.6), non-targets (§4.7), the Twente/Enschede base and national target map (Part IX), the
design-partner program (Part XIII), and EU expansion sequencing (Part XVIII). Outreach itself —
sequences, templates, pitch, pilots — lives in the companion
`Mainguard_Outreach_And_Pitch_Kit.md`; this document answers one question: **how do we find them.**

The stage discipline applies throughout: today's "sales" motion is **design-partner recruitment**
(Track A individuals, Track B teams — Part XIII), converting to paid pilots after Act Two. Sourcing
starts now precisely because the 40-candidate list (target: 25 discovery interviews Jul–Sep, 3–5
signed Track B teams by launch — Part IX walkthrough) is the input to everything downstream.

---

## 1. The ICP, sharpened into three findable segments

The locked ICP (§4.5): *a 10–100 developer product company or agency, Windows-heavy or mixed-OS,
already running agentic CLIs, where an EM or staff engineer owns the drowning-review-queue problem,
and where compliance or client contracts make "who wrote this code and was it tested" a real
question.* That is one sentence describing an intersection. For sourcing, split it into the three
axes — each axis is independently findable, and companies scoring on two or three are the bullseye.

### Segment A — Teams already running AI coding agents (the urgency axis)

- **Who:** product companies and agencies where ≥ 2 developers run Claude Code / Codex CLI / OpenCode /
  Copilot agent sessions weekly; the duct-tape signal is worktrees + tmux or Conductor-on-Mac
  (persona "Sam," GTM Plan §3.2).
- **Their pain in their words:** "the review queue is drowning"; "two agents collided"; "I merged
  something I didn't fully read." (Discovery-script spine, GTM Plan §6.1.)
- **What they buy first:** sandbox isolation + the review cockpit + test-gates — the Act Two story.
- **Findability: high** — agent adoption leaks into public artifacts (repos, job posts, talks, posts).
  §3.3–3.5 below are built on those leaks.
- **Disqualifier:** all-in on cloud-only agents with no local dev loop (Jules-only shops) until the
  external-PR intake ships (§4.7).

### Segment B — Windows-first dev shops (the underserved flank)

- **Who:** .NET/Windows product companies, ISVs, and Microsoft-stack consultancies — persona "Dana's"
  employers. Windows is 59.2% personal / ~48% professional developer use while the entire polished
  devtool wave shipped Mac-first, and the category leader is Mac-only (GTM Plan §4; Narrative §1).
- **Their pain:** no premium native client on their OS; agents arriving at work with no safe way to
  run them; locked-down machines where an Electron app with an account wall is a procurement fight.
- **What they buy first:** the free client (speed, polish, no login) — then the WSL2-native agent
  runner makes their company the Pro/Team buyer. Individuals convert at $0; *the company pays*
  (GTM Plan §3.2).
- **Findability: very high** — .NET stacks are advertised in every job post, partner directory, and
  conference sponsor list. The Netherlands over-indexes (§4.5: ".NET shops … the Netherlands is full
  of exactly these").

### Segment C — Regulated / compliance-driven orgs (the audit-trail axis)

- **Who:** fintech, insurance, healthcare ISVs, defense, and government contractors — orgs where an
  auditor or a client contract asks "who wrote this code and was it tested." Buyer is "Priya"
  (EM accountable for AI governance, GTM Plan §3.2); the structural EU tailwind is real (sovereignty,
  local-first, source-available — Part XVIII).
- **The binding rule, restated:** **"Do not sell to Priya before the governance features exist"**
  (GTM Plan §3.2). Segment C is *sourced and nurtured now* (discovery interviews, essay distribution,
  the sovereignty page) and **sold to only when the audit/provenance tier ships** (2027, Master Market
  Document §8.1 Team-tier rule). The honest language until then: "audit-grade, where procurement is
  heading" — never a deadline scare (honesty contract §0.2).
- **What they buy eventually:** Team/Enterprise — provenance, audit/SIEM, RBAC/SSO ($50+/seat).
- **Findability: high** — regulation is public: DNB/AFM registers, ISO 27001 certificate directories,
  government-supplier lists, defense clusters (Thales Hengelo is already Tier 1, Part IX).

### The bullseye and the scoring model

The archetype scoring all three axes is already on the map: **Topicus** (Deventer — Microsoft-stack,
compliance-sensitive verticals, 45 min from Enschede, Part IX Tier 1). Score every candidate 0–2 per
axis, plus reachability:

| Axis | 0 | 1 | 2 |
|---|---|---|---|
| **A — agent adoption** | no evidence | AI-assisted coding mentioned (Copilot completions) | agentic CLIs / parallel sessions evidenced |
| **B — Windows/.NET** | Mac/Linux-only shop | mixed-OS | Microsoft-stack core |
| **C — compliance pressure** | none visible | client-contract accountability (agencies) | regulated vertical / certified / public sector |
| **R — reachability** | cold, no path | 2nd-degree or community overlap | warm intro available (Novel-T, dotNed, advisor, alumni) |
| **S — size fit** | <10 or >250 devs | 100–250 | 10–100 (the ICP band) |

**Priority = A+B+C+R+S.** Work the list top-down; a 7+ goes into active outreach, 4–6 into nurture,
≤3 parked. Segment-C-heavy candidates with low A scores stay in nurture regardless of total (the
Priya rule). One more standing rule from the overlap analysis (§4.8): a company deeply invested in
GitKraken-for-teams or wall-to-wall Copilot has near-zero switching cost pressure — require an A-axis
score of 2 before spending outreach effort there.

---

## 2. Play 1 — The Twente/Enschede map (warm, local, visitable)

The Part IX map is the starting inventory; this play is the *mechanics* of working and extending it.

**The existing tiers (Part IX §9.2, scores added):**

| Tier | Companies | Segment profile | Priority notes |
|---|---|---|---|
| 1 — Twente/East NL | Topicus (Deventer), Thales NL (Hengelo), Nedap (Groenlo), Demcon, Sigmax (Kennispark), Heutink ICT + regional agencies, Xsens/Movella, SciSports, LioniX, UT/Saxion spin-offs via Novel-T | B+C heavy; A varies | Topicus = bullseye archetype; Thales = long-cycle C (air-gap-friendly, local-first is the *only* agent story they can adopt); spin-offs = fastest A-segment loops |
| 2 — National .NET ISVs/consultancies | Info Support (Veenendaal), Betabit, Luminis/Yuma, ilionx, Sopra Steria NL, Sioux, Q42, Voorhoede | B heavy, C via clients | One adopted champion at Info Support seeds hundreds of consultants (Part IX) |
| 3 — AI-forward Dutch product cos | Adyen, Mollie, bol., Coolblue, Picnic, ASML, Weaviate, Framer, Channable, Mews | A heavy, C for fintech | Logo value; founders reachable via the Amsterdam ecosystem |

**Mechanics to run now:**

1. **Register with Novel-T** (the standing action, Part IX §9.1) — the free front door to Twente's
   grant advisors, angels, and corporate network; Incubase events put UT/Saxion spin-offs (Segment A)
   in the same room.
2. **Crawl the regional directory** — `softwarebedrijf-info.nl/enschede` (named in Part IX) lists
   dozens of 10–100-dev shops in Hengelo/Enschede; score each with the §1 model; expect ~15–25
   additions to the 40-candidate list from this alone.
3. **Kennispark tenant list** — the innovation-district company roster is public; filter for software
   organizations, walk to the ones that score (they are literally neighbors).
4. **dotNed + Techorama as a warm map** (Master Market Document §14.7): speaker and sponsor lists are
   self-declared .NET shops (Segment B) with named, reachable engineers — "speakers answer email"
   (Part IX walkthrough). Betabit sponsors Techorama and .NET Zuid; every co-sponsor is a candidate.
5. **The advisor and the August meeting:** the single best Track B outcome is Daemon's team as a
   design partner (GTM Plan §6.2); every name the advisor offers unprompted goes to the top of the
   R-axis.
6. **Local-hero press as inbound sourcing:** IO+/Tubantia/U-Today coverage (§14.6) makes Tier-1
   companies call us; time it to Act One.
7. **DACH is next, not now:** once 2–3 Dutch logos exist, rerun this play on the German equivalents
   (Zühlke, adesso, msg-systems-type consultancies — Part XVIII); Germany is 20 minutes away.

---

## 3. Play 2 — LinkedIn Sales Navigator recipes

One seat, used for list-building and warm-path mapping — not for spray outreach (the outreach rules
live in the pitch kit). Concrete saved-search recipes, one per segment:

**Recipe B — Windows/.NET shops (accounts):**
- Filters: Geography = Netherlands (then DACH at expansion) · Company headcount = 11–200 ·
  Industry = Software Development / IT Services · Technology-keyword search in account pages and
  posts: `.NET`, `C#`, `Azure DevOps`, `Blazor`, `WPF`.
- Enrich: cross-check against the Microsoft Solutions Partner directory (Betabit's designation —
  "Digital App & Innovation, Data & AI" — is the fingerprint of a Dana employer, Part IX Tier 2).

**Recipe A — agent-running teams (leads + content):**
- Lead filters: Title = engineering manager / staff engineer / head of engineering / CTO ·
  Headcount 11–200 · Posted-content keywords: `Claude Code`, `Codex CLI`, `coding agents`,
  `Copilot agent`, `AI pair programming`, `agentic`.
- The content search is the sharpest tool: people *posting* about running agents are Sams
  self-identifying, and their employer joins the list with A=2, R=1 in one move.

**Recipe C — regulated orgs (accounts, nurture-only):**
- Filters: Industry = Financial Services / Insurance / Hospitals & Health Care / Defense & Space ·
  Geography = NL · Headcount 51–500 (regulated orgs run larger) · Keywords: `ISO 27001`, `SOC 2`,
  `DORA`, `NIS2`, `audit`.
- Route the results into nurture (essays, sovereignty page), not outreach — the Priya rule (§1).

**Warm-path discipline:** for every 7+ account, check 2nd-degree connections through UT/Saxion alumni,
dotNed, Novel-T staff, and the advisor before any cold touch — Dutch B2B runs on warm intros and
"LinkedIn is disproportionately effective for B2B in NL" (§14.7) cuts both ways: a cold pitch there is
visible to everyone.

---

## 4. Play 3 — GitHub org signals (agent adoption you can grep for)

Agent adoption leaves public fingerprints. GitHub code search across org repos, in signal order:

1. **Agent-config files in repos** — the strongest signal that agents run *routinely*:
   - `CLAUDE.md` / `AGENTS.md` in repo roots (`filename:CLAUDE.md`, `filename:AGENTS.md`)
   - `.github/copilot-instructions.md` · `.cursor/rules` / `.cursorrules` · `.claude/` directories
2. **Agent-authored history:**
   - PRs by `copilot-swe-agent` / Copilot-app bot identities; `Co-authored-by: Claude` /
     `Co-authored-by: GitHub Copilot` commit trailers in org repos.
   - Jules/Codex GitHub-Action workflow files (`.github/workflows/` referencing agent actions) —
     remembering the non-target rule: cloud-only-agent shops wait for the external-PR intake (§4.7).
3. **Org qualifiers:** profile location NL (then DACH); primary language C# for the B-axis;
   10–100 members ≈ the ICP band; active within 90 days.
4. **Tooling:** GitHub code-search queries saved and re-run monthly; a small script against the GitHub
   search API can emit `org · signal · file · date` rows straight into the candidate list. (Respect
   API terms; this is public-artifact research, not scraping private data.)
5. **What a hit means:** an org with `CLAUDE.md` in three repos scores A=2 and — crucially — gives the
   outreach a *specific, honest opening*: we can name the artifact we saw (pitch-kit rule: every
   personalization is a real, named observation).

**Bonus signal — the Agent Trace RFC:** emitters are coming and no consumer/renderer exists
(Narrative §2.4 empty square #2). Orgs discussing or adopting agent-trace tooling in public issues
are early-adopter Segment A at its most reachable.

---

## 5. Play 4 — Job postings (adoption evidence in the wild)

Job posts are companies describing their own stack and pain, timestamped. Sources: LinkedIn Jobs,
Indeed NL, werkenbij-sites of Tier-1/2 companies, and Dutch boards (Nationale Vacaturebank; UT/Saxion
career portals for who is hiring locally).

**Query set (run monthly, NL first then DACH):**

| Query | Signal | Segment |
|---|---|---|
| `"Claude Code" OR "Codex" OR "coding agents" OR "agentic" + developer` | agents named in a JD = adoption is policy, not hobby | A=2 |
| `"GitHub Copilot" + (".NET" OR "C#") + Nederland` | AI-assisted + Microsoft stack | A=1, B=2 |
| `"AI-assisted development" / "AI tooling" in engineering-lead JDs` | someone owns the AI-enablement problem — that person is the champion | A≥1, names the buyer |
| `(".NET" OR "C#") + ("ISO 27001" OR "medical" OR "fintech" OR "defensie")` | Microsoft stack under compliance | B=2, C=2 |
| `"developer experience" OR "platform engineer" + agents/AI` | a platform team exists = a budget line for dev tooling exists | buyer signal |

**Reading a hit:** a JD mentioning Copilot/agents does double duty — it scores the company *and*
usually names the hiring manager (a warm, specific person for the pitch kit's champion-first motion).
JDs also reveal the OS posture ("Windows development environment", "Azure DevOps") for the B-axis.

---

## 6. Play 5 — Communities (where Sam and Dana already are)

Community sourcing is listening, not posting — the 90/10 founder-disclosed rule governs anything we
say (GTM Plan §7.5).

- **r/ClaudeAI, r/ChatGPTCoding** — people posting multi-agent workflows are DM-able Sams; the
  discovery-interview sourcing list names exactly this (GTM Plan §6.1). Their employers enter the
  company list via LinkedIn cross-reference.
- **dotNed / dotNed Saturday / .NET Zuid** — the Dana community; attend now, propose the pure-tech
  Avalonia talk (§14.7 — "zero pitch, the .NET community will carry it"); every conversation is a
  discovery interview.
- **Techorama (26–28 Oct)** — sponsor/speaker/attendee companies harvested per Play 1; the hallway
  track is 25 discovery interviews in two days if prepared.
- **Avalonia and C# Discords** — Dana-dense; we are a flagship consumer of their stack (and a sponsor,
  per the paid-media plan §2.9); be known there as a builder, not a vendor.
- **Novel-T / Incubase / Kennispark demo days** — Segment A startups plus the angel network in one
  room (Part IX).
- **NL MVP circuit** — demo to 3 Dutch .NET MVPs pre-launch (§14.7); MVPs know every serious .NET
  shop in the country and are the best R-axis multipliers there are.
- **X build-in-public replies** — accounts engaging with the clips are self-selecting; check employer,
  score, list.

---

## 7. Play 6 — Inbound enrichment (the waitlist is a sourcing engine)

From Phase 0 the waitlist, Discord, and (opt-in) telemetry produce inbound signals; treat them as a
sourcing play, not passive data:

1. **Company-domain triage:** every waitlist signup with a company domain gets scored (§1) — a
   `@topicus.com` signup is a Tier-1 warm path that outranks any cold play in this document.
2. **The self-report question** ("where did you hear about Mainguard?" — paid-media plan §1) doubles as
   segment attribution: "r/ClaudeAI" flags Segment A, "dotNed" flags Segment B.
3. **Discord intake:** ask two questions on join (what's your Git client today · do you run coding
   agents) — voluntary, two clicks, and it sorts Track A candidates from Track B champions.
4. **Essay analytics:** a company IP range reading the index.lock essay three times is not
   identifiable — but a reader who *emails about it* is a champion self-identifying; the essays end
   with a plain invitation to do exactly that.

---

## 8. The candidate list (structure, cadence, ownership)

One sheet, one owner (the founder, until hire #1), reviewed weekly. Columns:

> Company · Tier (1/2/3/other) · Segment scores A/B/C/R/S · Priority (sum) · Evidence (the named
> artifact: repo file, JD link, meetup, post) · Champion (name/title) · Warm path · Persona
> (Sam/Dana/Priya) · Track (A/B) · Status (unworked → contacted → interviewed → demo → design partner
> → paid pilot) · Next action + date · Notes

**Cadence and targets (aligned to the master calendar):**

- **Jul–Sep 2026:** build to 40+ scored candidates (Plays 1–5); 25 discovery interviews (Part XIII
  target); Novel-T registered; Techorama harvested in October.
- **By Act One (~Oct):** 3–5 Track B teams signed (Part IX walkthrough target); Track A individuals
  flowing from the waitlist.
- **Post Act Two (Nov–Dec):** the list re-sorts by the paid-pilot tripwire (GTM Plan §12.2) — Track B
  partners are asked to convert; new outreach continues from the top of the priority order.
- **2027:** Segment C moves from nurture to active only when the governance tier ships; DACH rerun
  begins at 2–3 Dutch logos (Part XVIII).

**Hygiene rules:** every row carries *named evidence* — a candidate on the list "because they seem
like a fit" gets deleted; the disqualifying signal from discovery is respected and recorded (if
interviewees consistently say first-party desktop apps are good enough for review, that goes up the
chain as a wedge-sharpening flag, GTM Plan §6.1); GDPR common sense — public/professional data only,
no scraped personal emails, and Dutch professional norms (warm, brief, honest) govern every touch.
