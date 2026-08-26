# Mainguard — The Founding User Program

**Date:** 2026-08-20 · **Register:** brand · **Status:** proposed — needs one decision from Daniel (§3)
**Why this exists:** the site already promises *"founding-member terms for Pro and Cloud"* on the waitlist
page and nowhere defines them. An undefined promise captures nobody and, worse, becomes a thing you have to
either honour generously or walk back publicly. This document defines it before the campaign starts.
**Reads under:** pricing in `../market-analysis/Mainguard_Market_Research_v2.md` §5.3 and
[`Mainguard_Business_Plan.md`](Mainguard_Business_Plan.md). **Binding:** the honesty contract,
[`../creative/Narrative.md`](../creative/Narrative.md) §0.

---

## 1. The problem this solves

The plan is to give the product away now, gather feedback, and convert those people to paid later with
"a discount or something similar." That plan has a failure mode that is easy to walk into and hard to walk
back out of:

**A vague promise does no work now and creates an obligation later.** "Founding-member terms" invites every
reader to imagine their own number. Some imagine 20% off; some imagine free forever. When the price list
arrives, whatever you choose disappoints one of those groups — and they'll be your loudest early users, in a
small country, talking to each other.

A specific promise does the opposite. It gives the outreach email a concrete thing to trade for a
conversation, it makes the waitlist worth joining rather than a notification list, and it costs nothing
today because there is nothing to charge for yet.

**The design constraints:**

- It must be **honourable at any future price.** Define it as a relationship to the eventual price, not an
  absolute number, since the price isn't locked.
- It must **reward the behaviour you actually want** — feedback and honest criticism — not just an email
  address.
- It must **not compromise the free client.** The Git client is free permanently, with no account. That is a
  positioning commitment, not a tier, and nothing here touches it.
- It must be **small enough to keep.** If 500 people take it, you must still be glad you offered it.

---

## 2. The program

Two tiers, because the two groups behave differently and cost you different amounts.

### Tier 1 — **Founding User** (the open offer)

**Who:** anyone who joins the waitlist and, when the beta lands, actually installs it and sends back one
piece of substantive feedback — a bug, an annoyance, a "this doesn't fit how we work." One paragraph is
enough. The bar is *engagement*, not volume.

**What they get:**

1. **50% off Pro for the first two years**, from whenever Pro starts charging, at whatever Pro costs then.
2. **Locked in per person, not per plan** — if the price rises, their discount rides on the new price; the
   50% doesn't erode.
3. **First access to every build**, before public release.
4. **A named credit** in the release notes for anyone whose report changes the product, if they want it
   (many will; it's free and it's the reward engineers actually value).

**Why 50%/two years and not "lifetime free":** lifetime free costs you the entire cohort's revenue exactly
when early revenue is proof of a business, and it selects for people who want free software rather than
people who want *this* software. Two years at half price is generous enough to be worth having, cheap
enough to keep, and it converts to full price at a natural moment.

### Tier 2 — **Design Partner** (invitation only, cap at 8 teams)

**Who:** a team that runs Mainguard against a real repository and commits to a recurring conversation — say
30 minutes a month for six months. Invited from the Tier A companies in
[`sales/Target_Company_List_2026-08.md`](sales/Target_Company_List_2026-08.md), or from anyone who turns out
to be unusually useful.

**What they get:**

1. **Pro free for the whole team for one year** from general availability.
2. **50% off the following year**, then standard pricing.
3. **A direct line** — your address, not a support queue.
4. **Real influence on the roadmap**, stated honestly: their problems get worked on first. Not a veto, not a
   guarantee, and say so.
5. **Optional public reference** — a logo, a quote, a joint post. **Optional, and never a condition.** A
   design partner who can't be named because their procurement forbids it is still a design partner.

**What you get:** the recurring conversation, permission to watch them work, and — for the ones who can — a
reference that unlocks the next ten companies. In the Netherlands one named Dutch team is worth more than
any amount of copy.

**Why cap it at 8:** eight monthly conversations is four hours a month, which is the honest ceiling
alongside development. A design-partner program you cannot service is worse than none, because the failure
is visible to exactly the people whose opinion matters most.

### What both tiers do *not* get

Be explicit, in writing, at sign-up:

- **No SLA, no support commitment, no uptime promise.** It's a beta.
- **No guarantee any specific feature ships.** Roadmap influence is real; roadmap authority isn't.
- **No promise of a date.** The pipeline is finished when it's finished.

Saying this up front costs you almost no one and saves every awkward conversation later.

---

## 3. The one decision needed

The offer above is written at **50% for two years**. The alternatives, honestly weighed:

| Option | For | Against |
|---|---|---|
| **50% for 2 years** *(recommended)* | Generous, memorable, finite. Converts at a natural moment. Affordable at any cohort size | Slightly complicated to explain in one line |
| **Lifetime 30%** | Simplest possible sentence; permanent goodwill | A permanent 30% haircut on your best-disposed cohort, forever |
| **First year free** | Easiest to say, strongest hook | Zero revenue from early believers precisely when early revenue is the proof point; and "free year" attracts free-year people |
| **Perpetual founding price** (they keep today's price forever as prices rise) | The most loved option among developers; costs nothing today | Unbounded over a long horizon; hard to model |

**Recommendation: 50% for two years.** It is the only one of the four that is generous, bounded, and
honourable at any future price. Decide it now and write it down — the number appears in the outreach emails,
the waitlist page and the beta welcome mail, and it has to be the same number in all three.

---

## 4. Site copy

### 4.1 Waitlist page — replacing the current lede

The current line — *"First access to the free client this fall, and founding-member terms for Pro and Cloud"*
— makes a dated promise ("this fall") and an undefined one ("founding-member terms"). Both need fixing.

**Heading:** `Get the beta`

**Lede:**

> The Git client is finished and free — no account, ever. The agent control center is still being wired
> together, and I'd rather show you an honest half-built thing than a launch video.
>
> Join and you get every build before it's public. Send back one piece of real feedback and you're a
> **Founding User**: **50% off Pro for its first two years**, at whatever it costs then, locked to you.

**Below the form, in small text:**

> No newsletter. No drip campaign. You'll hear from me when there's a build, and when something changes that
> you'd want to know about. That's it.
>
> Worth knowing before you install: nothing is code-signed yet, so Windows will show a "Windows protected
> your PC" warning the first time. That's on the roadmap, and I'd rather tell you now than have you discover
> it.

That last paragraph is not a liability. To the audience that converts on verifiable claims, a vendor who
warns you about their own SmartScreen prompt is a vendor who tells you things.

### 4.2 Success state — replacing the current one

Current: *"We'll email you the moment there's something to download — and nothing else."* Good, but it asks
for nothing while the person is at their most willing.

> **You're on the list.**
>
> You'll hear from me when there's a build — and nothing else in between.
>
> One thing, if you have thirty seconds: **reply to the confirmation email and tell me what you're running
> today.** Which agents, how many at once, what breaks. That reply is worth more to me than the signup, and
> it's what makes you a Founding User rather than an address on a list.

### 4.3 One schema change worth making first

`site/worker/schema.sql` captures `email` and `interests`. To convert this cohort later — and to know which
signups are companies rather than curious individuals — you need two more things:

- an optional **"what are you running today"** free-text field on the waitlist form (this is your richest
  qualitative data source and people fill it in when asked at the right moment), and
- a **`founding_status`** column, so you can mark who actually sent feedback and therefore who earned the
  discount. Without it, in a year you'll be guessing, and guessing generously.

The existing `message` column can carry the free-text answer for `kind = 'waitlist'` rows with no migration.
`founding_status` is a one-line `ALTER TABLE`. Do both before the campaign starts, not after — retrofitting
provenance onto a list is the exact problem this whole product is about.

---

## 5. How the offer appears in each place

**Outreach email (deferring a price question):**
> Nothing, and not for a while. The Git client is free permanently. The agent layer will be paid eventually,
> but it isn't finished and I'm not taking money for something in integration. What I'd rather have from you
> is the feedback — and if you give it, you're a Founding User: half price on Pro for its first two years,
> whatever it costs by then.

**End of a discovery call:**
> Two things I'd ask. Install the client and tell me one thing that annoys you — that alone makes you a
> Founding User, which is half price on Pro for two years once there's a Pro to pay for. And if your team
> ends up wanting to run this against a real repo, I'm taking eight design partners: free for the team for a
> year, and a standing monthly half-hour with me.

**LinkedIn / X, occasionally — not every post:**
> The Git client is free and finished. The agent control center isn't, and I'm not pretending otherwise.
> Early builds go to the list first; anyone who sends back real feedback gets Founding User terms — half
> price on Pro for its first two years. {link}

**Beta welcome email, first line after hello:**
> Before anything else: this is unsigned, so Windows will warn you, and the agent side isn't in this build.
> Here's what *is* in it, and here's the one thing I'd love you to try.

---

## 6. What this program is not

**It is not a pilot.** Nobody is being asked to depend on this. Say "beta" and mean it.

**It is not a way to sell early.** If someone offers to pay now, the answer is no, with a reason: *"I'm not
taking money for something in integration."* That refusal is worth more than the money — it is the single
most persuasive thing you can say to an engineering audience, and it will be repeated for you.

**It is not a substitute for the free client being genuinely excellent.** The client is the top of the
funnel and the trust wedge. Every hour spent on the founding-user mechanics is worth less than an hour spent
on the client being fast, correct and pleasant — which, per the walkthrough log, still has a handful of
clipped toasts and stale banners to fix.

---

## 7. Open items

- [ ] **Decide the discount** (§3). Recommended: 50% for two years. Everything downstream depends on it.
- [ ] Update `site/src/pages/Waitlist.tsx` with §4.1 and §4.2 copy.
- [ ] Add the optional "what are you running today" field and the `founding_status` column (§4.3).
- [ ] Write the beta welcome email — it does not exist yet; `marketing/EmailSequences.md` covers waitlist
      nurture and launch, but not "here is your unsigned beta and here is what's broken."
- [ ] Decide whether the download is public or reply-gated. **Recommendation: reply-gated while unsigned** —
      it gets you the conversation, it lets you deliver the SmartScreen warning personally, and it keeps an
      unsigned binary off a public button.
- [ ] Keep a single list of who was promised what, with dates. In a year this is the only record that
      matters, and it is the one thing nobody ever writes down at the time.
