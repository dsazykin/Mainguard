# Mainguard — Outreach Sequences for the Beta-Feedback Motion

**Date:** 2026-08-20 · **Register:** brand (sales execution)
**Supersedes, for this stage only:** the sequences in
[`Mainguard_Outreach_And_Pitch_Kit.md`](Mainguard_Outreach_And_Pitch_Kit.md) §3. That kit is written for a
discovery → demo → **pilot → close** motion with pricing conversations at the end. We are not doing that
yet. Its §2 persona pitches, §4.1 discovery script and §6 objection table remain in force; its §3 email
bodies and §4.5 close do not.
**Targets:** [`Target_Company_List_2026-08.md`](Target_Company_List_2026-08.md)
**What we offer:** [`../Founding_User_Program_2026-08.md`](../Founding_User_Program_2026-08.md)

---

## 0. The stage rule, in one paragraph

**We are not selling anything. We are recruiting people who will tell us the truth, and paying them in early
access and founding-user terms.** The Git client works and is worth installing today; the agent control
center is in integration and is not. Every message below says both of those things out loud, because the
people we are writing to can tell the difference in about ten seconds and the ones who can are exactly the
ones we want. The ask is never "buy," never "try our product," and — in the first message — usually not even
"look at this." The ask is **"tell me how you handle this today."**

**Three rules that outrank the templates:**

1. **Never claim the agent platform is shipped.** The honesty contract ([`../../creative/Narrative.md`](../../creative/Narrative.md) §0)
   binds outreach exactly as it binds marketing. Shipped: the Git client. Everything else is marked, in the
   email, as not-yet.
2. **Never send a template unedited.** Every target on the list has a specific public artefact — a repo, a
   blog post, a job ad, a tech radar — quoted in their entry. If you cannot name theirs in the first
   sentence, you are not ready to write to them.
3. **Volume is ten a week, not a hundred.** At a few hours a week the constraint is follow-through, not
   send volume. Ten well-researched sends with real follow-up beats a hundred sprayed.

**Honest framing of what a "yes" gets us:** a 30-minute call, notes, and possibly a person who installs the
client. That is the whole win at this stage. Anyone who wants to talk about price is politely deferred —
see §6.

---

## 1. Which opener to use

| Their situation | Opener | Targets |
|---|---|---|
| They publish code or artefacts about agents | **§2 · Peer** | Peter Cosemans (Euricom), Demcon maintainers, Fredrik Claesson (Mews), Q42, XPRTZ |
| They published your argument in their own words | **§3 · Quote-back** | Blis Digital, Arcady, Cloud Republic, Visma Circle, Akkuro, Topicus |
| Regulated, no agent tooling yet | **§4 · Greenfield** | Wigo4it, Sigmax, Demcon, Nedap, Qurentis, Yellowtail |
| Within an hour's drive | **§5 · Neighbour** | Sigmax, Demcon, Ecare, xxllnc, JCC, Arcady, Akkuro |

Where two apply, the **peer** opener wins — it is the least like sales and gets the highest reply rate from
exactly the person you want.

---

## 2. The peer opener

For people who have publicly built or evaluated something adjacent. You are writing engineer-to-engineer
about a shared problem, and you are the one asking for help.

### 2.1 Email — English

> **Subject:** your worktree-per-agent evaluation
>
> Hi {name},
>
> I found {specific artefact — e.g. "your poc-claude-code repo, and the Multi Agents Workflow Support
> section comparing Cursor worktrees, Worktrunk and Workmux"}. I've been deep in the same problem for a
> while and I've never seen anyone lay the comparison out that plainly.
>
> I'm building a native Git client in the Netherlands ({url}) that's growing into the layer *after* the
> agents finish — sandboxed runs, a merge queue that re-verifies branches when main moves, per-hunk
> provenance in review. The Git client works today. The agent side is still in integration, so I'm not
> pitching you anything.
>
> What I'd like is 30 minutes to hear how you actually landed on your setup: what broke, what you rejected,
> and what you'd still fix. I'll send you what I've built and you can tell me where it's wrong — that trade
> seems fair to me.
>
> Worth a call in the next couple of weeks?
>
> {signature}

### 2.2 Email — Nederlands

> **Onderwerp:** jullie evaluatie van worktree-per-agent
>
> Hoi {naam},
>
> Ik kwam {artefact} tegen. Ik zit al een tijd diep in hetzelfde probleem en ik heb die vergelijking nog
> nergens zo helder opgeschreven gezien.
>
> Ik bouw vanuit Enschede een native Git-client ({url}) die doorgroeit naar de laag *ná* het werk van de
> agents: runs in een sandbox, een merge queue die opnieuw verifieert zodra main verschuift, en herkomst per
> hunk in de review. De Git-client werkt vandaag. De agent-kant zit nog in integratie — ik verkoop je dus
> niks.
>
> Wat ik wél zoek: 30 minuten om te horen hoe jullie tot je huidige opzet zijn gekomen. Wat ging er stuk,
> wat hebben jullie afgeschoten, en wat zou je nu nog anders willen. Ik stuur je in ruil wat ik gebouwd heb,
> en dan mag jij zeggen waar het niet klopt.
>
> Schikt een gesprek in de komende twee weken?
>
> {ondertekening}

### 2.3 The variant that works even better: don't email at all

For **Demcon** (`libstored`, `cmake-sbom`) and **Peter Cosemans at Euricom** (`poc-claude-code`), open a
GitHub issue or a thoughtful reply on the repo instead. A substantive technical comment from a stranger who
clearly read the code converts better than any email, and it happens in public where their colleagues see
it. Email second, referencing the thread.

**One correction to carry into the Euricom message:** `poc-claude-code` is one person's proof of concept —
22 commits, single author, last touched March 2026 — **not an organisational practice**. Write to Cosemans
about *his* evaluation. Writing to "Euricom" about "your worktree-per-agent workflow" would be visibly wrong
to the one person who'd read it.

For **Nedap**, their ["Talk to a Developer"](https://nedap.com/talk-to-a-developer/) programme lets outsiders
book a call with one of their developers. Use it if you like — **but say in the booking note that you're
building a Git tool and want a developer's view, not that you're a candidate.** The programme is candidate-
facing employer branding and carries a no-recruiters disclaimer; turning up under an implied false pretext
at a company that offers open access is a bad trade for twenty minutes.

---

## 3. The quote-back opener

For companies that have already published your argument. The entire trick: let them be right, and ask them
to go further.

### 3.1 Email — English

> **Subject:** "{their exact line}"
>
> Hi {name},
>
> {Their line, quoted, with attribution — e.g. "Wie de code samenvoegt, is er verantwoordelijk voor, ook als
> een agent hem schreef."} That sentence is the reason I'm building what I'm building, and you got to it
> before I did.
>
> The question I keep getting stuck on is the next one down: *how* does the person merging discharge that
> responsibility, mechanically, when the diff is long and the tests passed an hour ago against a main that
> has since moved? I'm building a native Git client ({url}) whose roadmap is exactly that — deterministic
> verification at the gate, re-verification when main moves, per-hunk provenance. The client is real and
> works; the agent layer is in integration and I'd rather show you an honest half-built thing than a demo
> video.
>
> Could I have 30 minutes to hear how {company} handles it today? I'm looking for people who'll tell me
> where I'm wrong, not for customers — there's nothing to buy yet.
>
> {signature}

### 3.2 Email — Nederlands

> **Onderwerp:** "{hun eigen zin}"
>
> Hoi {naam},
>
> {Hun zin, letterlijk geciteerd.} Die zin is precies de reden dat ik bouw wat ik bouw — en jullie waren me
> voor.
>
> Waar ik op blijf hangen is de vraag daaronder: *hoe* maakt degene die merget die verantwoordelijkheid
> concreet waar, als de diff lang is en de tests een uur geleden groen waren tegen een main die intussen is
> opgeschoven? Ik bouw een native Git-client ({url}) waarvan de roadmap precies dat is: deterministische
> verificatie bij de poort, opnieuw verifiëren zodra main verschuift, herkomst per hunk. De client werkt
> echt. De agent-laag zit in integratie — ik laat je liever een eerlijk half-af ding zien dan een
> demofilmpje.
>
> Heb je 30 minuten om te vertellen hoe jullie dit vandaag aanpakken? Ik zoek mensen die me vertellen waar
> ik het mis heb, geen klanten — er valt nog niks te kopen.
>
> {ondertekening}

**Per-target first lines, ready to use** (full sourcing in the target list):

| Target | Open with |
|---|---|
| Blis Digital | *"Wie de code samenvoegt, is er verantwoordelijk voor, ook als een agent hem schreef."* — **attribute to their AI Native Lead Engineer vacancy, not to their manifest.** The manifest does not contain the line, and Christian Boer is a co-founder but **Richard Schot is CEO** |
| Arcady | *"Iedereen kan straks code maken. Maar wie bepaalt of het de juiste code is?"* |
| Cloud Republic | *"behandel instructies als code. Version ze, review ze"* |
| Visma Circle | *"het kritisch beoordelen en verifiëren van AI-output via code reviews om fouten of hallucinaties te voorkomen"* — **the vacancy is titled "Product Engineer"**; "AI Agent Engineer" is the framing inside it |
| Akkuro | *"practical standards for verification, traceability and responsible AI-assisted development"* — from their AI-Native Software Engineer role |
| Topicus | *"We werken met modellen die binnen Europa worden ontwikkeld en gehost. Data mag niet naar Big Tech."* — and lead with local-first as the answer |
| Mews / Fredrik Claesson | *"On other occasions, Claude Code straight up ignored rules clearly defined in CLAUDE.md"* — plus a real question about ContextKing |

---

## 4. The greenfield / compliance opener

For regulated shops with no agent tooling yet. Do **not** open with agents — they may not have the problem
yet, and arriving as the person warning them about a future risk is the wrong entrance. Open with the Git
client, which is real, free, and immediately useful, and let the second conversation be about agents.

### 4.1 Email — Nederlands (this segment is Dutch-first)

> **Onderwerp:** native Git-client, geen account, alles blijft lokaal
>
> Hoi {naam},
>
> Ik bouw vanuit Enschede **Mainguard**: een native Git-client voor Windows en macOS. Geen Electron, geen
> account, geen telemetrie standaard aan, en de code verlaat de machine niet. Gratis, en dat blijft zo.
>
> Ik zoek {sector — bijv. "teams die onder BIO en Wpg werken"} die er kritisch naar willen kijken, juist
> omdat jullie eisen strenger zijn dan gemiddeld. {Specifieke haak — bijv. "Jullie Compliancy Center is
> uitgebreider dan wat ik bij de meeste softwarebedrijven zie" / "Jullie Tech Radar staat publiek en wordt
> maandelijks bijgewerkt — dat maakt het makkelijk om te zien waar dit wel of niet in past."}
>
> Waar het naartoe groeit: de laag die het werk van AI-coding-agents controleerbaar maakt vóórdat het op
> main landt — geïsoleerd draaien, verificatie tegen de *huidige* main, en per regel vastleggen welk proces
> het geschreven heeft. Dat deel is nog in aanbouw; ik claim niet dat het af is.
>
> Heb je 30 minuten? Ik wil vooral horen hoe jullie vandaag omgaan met {"wie heeft dit geschreven en is het
> getest"} en waar dat schuurt. Geen verkoopgesprek — er is nog niets te koop.
>
> {ondertekening}

### 4.2 Email — English

Same body, translated; use for Belgian targets who work in English and for any international target.
Keep *"there is nothing to buy yet"* — it is the sentence that gets replies from people who ignore vendors.

---

## 5. The neighbour opener

Under an hour's drive. Shorter, warmer, and the ask is coffee rather than a call. This is the single highest
conversion rate available to you and it does not scale — which is exactly why it goes first.

> **Onderwerp:** buurman op Kennispark — 20 minuten?
>
> Hoi {naam},
>
> Ik bouw hier in Enschede aan Mainguard, een native Git-client die doorgroeit naar een controlelaag voor
> AI-coding-agents ({url}). {Concrete haak over hun werk.}
>
> Ik ben op zoek naar een handvol teams in de regio die er eerlijk naar willen kijken voordat er iets
> definitiefs staat. Zou je een keer 20 minuten hebben — bij jullie op kantoor, of koffie op het park? Ik
> wil vooral horen hoe jullie het nu doen.
>
> {ondertekening}

**Use for:** Sigmax (Kennispark, same park), Demcon, Ecare/PUUR (Enschede), xxllnc (Hengelo), JCC Software
(Oldenzaal), Arcady (Zwolle, 1 hr), Akkuro (Deventer, 45 min).

**And the two dated ones — put them in the calendar now:**

- **Hacktoberfest at Arcady's office, Zwolle — Thursday 8 and 15 October 2026.** Their own post: *"staan de
  deuren van ons kantoor open."* Turn up. No email required.
- **Techorama Netherlands, Utrecht — 26–28 October 2026.** The 26th is the workshop day (Van der Valk);
  **the conference is 27–28 Oct at Kinepolis/Jaarbeurs** — that's the part to attend. Betabit, Wigo4it,
  4DotNet, Intercept, Arcady and Blis are all partners: six targets in one building. Two days as an
  attendee is worth more than a month of cold email.

---

## 6. LinkedIn DMs

Shorter, no links in the first message (LinkedIn suppresses them), and never a connection request with a
pitch attached. Connect first with a plain note, message two days later.

**Connection note (Dutch):**
> Hoi {naam} — ik bouw in Enschede aan een Git-client voor teams die met AI-agents werken. Jouw stuk over
> {onderwerp} raakte precies waar ik mee bezig ben. Leek me goed om te connecten.

**Follow-up message, two days later:**
> Dank voor het connecten. Concrete vraag, geen pitch: hoe gaan jullie op dit moment om met agent-branches
> die getest zijn tegen een main die intussen is doorgeschoven? Ik bouw daar een oplossing voor en ik zoek
> vooral mensen die me vertellen waar mijn aannames niet kloppen. 30 minuten zou me enorm helpen — en ik
> stuur je in ruil wat ik tot nu toe heb.

**English variant** for international targets and the online track — same two-step, same "no pitch" framing.

---

## 7. Follow-ups: two, then stop

**Follow-up 1 — five working days later.** Reply in-thread, add something, never "just bumping."

> Hoi {naam} — korte aanvulling: {een nieuw, concreet ding — een screenshot van de review-cockpit, een link
> naar de security-architectuur, een essay}. Als een gesprek nu niet uitkomt: ook prima, dan laat ik het
> hierbij. En als iemand anders bij {bedrijf} hier meer mee heeft, hoor ik dat graag.

**Follow-up 2 — twelve working days after that.** Close the loop and give them an easy exit.

> Hoi {naam} — ik laat het hierbij, wil je niet blijven achtervolgen. Als het later wel relevant wordt: ik
> ben makkelijk te vinden, en {url} houdt bij wat er verandert. Succes met {iets specifieks van hen}.

**Then stop.** Two follow-ups, then the company goes to a "revisit in six months" list. Anything more costs
you the relationship you're actually trying to build in a country where the developer community is small
enough that people talk.

---

## 8. Handling the four replies you'll actually get

**"Sure, send me the tool."**
Send it, with the warning first, not buried. *"Two things before you click: it isn't code-signed yet, so
Windows will show a 'Windows protected your PC' screen — you'll need More info → Run anyway. And the agent
side isn't in this build; this is the Git client. If either of those is a dealbreaker for you, say so and
I'll come back when they're fixed."* Being the vendor who warns you about their own SmartScreen prompt buys
more credibility than a signature would.

**"Interesting — what does it cost?"**
Defer cleanly, then convert to the founding-user ask.
*"Nothing, and not for a while. The Git client is free permanently. The agent layer will be paid eventually,
but it isn't finished and I'm not taking money for something in integration. What I'd rather have from you
is the feedback — and if you give it, I'll put you on founding-user terms when there is something to charge
for."* Full terms in [`../Founding_User_Program_2026-08.md`](../Founding_User_Program_2026-08.md).

**"We built our own."**
This is the best possible reply and should be treated as a win, not a loss. *"That's exactly who I wanted to
talk to. What did you build, what did you decide not to build, and what still annoys you about it?"* Blis
Digital, Euricom and Mews will all say some version of this. The interview is worth more than the seat.

**"Not now / no reply."**
Fine. Two follow-ups, then the six-month list. Do not take a non-reply as a signal about the product; at ten
sends a week from an unknown sender, most silence is just inbox volume.

---

## 9. The call itself (30 minutes, no demo)

Adapted from the pitch kit §4.1. **Do not demo in the first call.** You have screenshots and an architecture
story, not a running end-to-end pipeline, and a demo that stalls is worse than no demo. Sell the interview.

| Minutes | What |
|---|---|
| 0–3 | Frame it honestly: "I'm building this, it's early, I want to know how you work — I'll talk for five minutes at the end, not now." |
| 3–20 | **Their reality.** How many people run agents, which ones, how the branches come back, who reviews them, what has gone wrong, what happens when main moves under a verified branch, who asks them "who wrote this." Ask for a *story*, not an opinion — "walk me through the last time an agent branch caused a problem." |
| 20–25 | **Your five minutes.** The thesis, the client (real, free, installable today), the agent layer (in integration, here's the architecture and here's what it looks like). Say plainly what does not work yet. |
| 25–30 | **Two asks:** (1) install the client and tell me one thing that annoys you; (2) can I come back in six weeks when the pipeline runs end-to-end and show you the real thing. Then the founding-user offer, in one sentence. |

**Write the notes the same day**, into a per-company file. The pattern across twenty of these is the most
valuable thing this whole exercise produces — more valuable than any individual seat, and it is what makes
the eventual launch copy true rather than assumed.

**Questions worth asking every single time**, because the answers compound:

1. When an agent branch is verified and then main moves — what happens today?
2. Who reviews agent-written diffs, and how long do they actually spend?
3. Has anyone outside engineering asked you "which of this was written by an AI"? Who, and why?
4. What would have to be true for you to let an agent's branch merge without a human reading every line?
5. Are your developers on Windows, Mac, or both? (Load-bearing for us and nobody volunteers it.)

---

## 10. Cadence for a few hours a week

| Day | Work | Time |
|---|---|---|
| Mon | Pick 10 targets, verify each one's artefact is still live, write 10 personalised sends | 90 min |
| Tue | Send. LinkedIn connects go out the same morning; emails mid-morning | 20 min |
| Thu | Follow-up 1 for the batch from two weeks ago; reply to anything live | 30 min |
| Any | Calls as they land — cap at three a week so the notes actually get written | 90 min |

**Target for the first eight weeks:** 60–80 sends, 12–20 conversations, 5–8 people who install the client,
and one paragraph of written notes per call. That is a realistic return at this volume, and it is enough to
know whether the thesis survives contact with Dutch engineering teams.

**Stop rule:** if after 40 sends the reply rate is under 10%, the problem is the message, not the list —
rewrite the opener before sending the next 40.
