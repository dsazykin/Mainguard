# Budget enforcement: BYOK is metered, OAuth is not

Status: **BYOK metering implemented. OAuth budgeting is an open problem with no committed direction.**

This note exists so nobody reads a budget cap as covering traffic it does not cover. The repo has a
recurring failure mode of documenting controls that are not actually in the path; this is the honest
statement of where the line falls.

## What is enforced

A worker whose model credential Mainguard holds (**BYOK** — the user supplied an API key) is metered:

1. At spawn, the daemon issues that agent an opaque `mg_sess_…` token, keeps the real provider key
   daemon-side, and records the agent's **upstream binding** (which provider host its traffic goes to).
2. The jail receives the token in place of the key, plus a base-URL variable pointing at the daemon's
   model gateway. The provider key never enters the container.
3. Every model request transits `ModelProxyMiddleware`, which resolves the agent from its token, charges
   `BudgetLedger`, substitutes the real key, and forwards to the bound upstream. Over-cap agents are
   refused with a soft `402` and paused rather than killed.

### Why routing is bound per-agent rather than read off the request

Once a CLI's base URL points at the gateway, the request's `Host` header names **the gateway**, not the
provider. The original middleware decided "is this model traffic?" by matching that header against a
model-host list — which therefore never matched in production, so every real request fell through
unfronted and `BudgetLedger` was never written outside tests. Nothing else in the daemon recorded which
provider an agent's traffic belonged to.

So the agent's upstream is captured at spawn, from the adapter being launched, and one lookup on the
presented token answers **both** "who is calling" (attribution) and "where does this go" (routing).
Keeping those two answers in a single record is deliberate: two parallel mechanisms would eventually
disagree, and the disagreement would be invisible. It also removes any way for an agent to influence its
own upstream, since it never supplies one.

## What is not enforced, and why

**An OAuth worker's traffic is not metered at all.** The user signs in inside the terminal; the CLI holds
its own provider session; requests go to the provider directly.

The obstacle is structural. Metering happens at a proxy that substitutes a credential it controls. An
OAuth CLI authenticates *past* such a proxy with a session Mainguard never issued, never sees, and cannot
price. There is no key to withhold, so there is no leverage to route the traffic through anything.
Forcing it through a proxy would mean intercepting a session the CLI negotiated itself — which breaks the
login rather than metering it.

**This is recorded as an open problem. No direction is recommended here.**

## Confinement is accounting, not blocking

BYOK metering is a *soft* limit, and the softness is deliberate:

- `api.anthropic.com` / `api.openai.com` stay on the default egress allowlist, because **OAuth CLIs have
  no other route** — they hold no API key, so they cannot be gateway-confined, and removing those entries
  would break interactive login outright.
- A BYOK jail therefore still has a network path to the provider. What it does **not** have is a
  credential that works against it: the jail holds only a `mg_sess_` token, which the provider rejects.

**Residual bypass, stated plainly:** a BYOK CLI that ignored its configured base URL could still reach the
provider host directly. It would get nowhere without a valid key — but if an agent obtained a provider key
by some other means, the budget would not stop it, because the budget is enforced at the credential
substitution point rather than at the network boundary.

Per-agent network confinement (a segment that omits the model hosts for BYOK agents only) was considered
and **not implemented**, because the priority is that nothing which works today stops working, and the
OAuth path must be untouched.

## Measured: why the gateway is reached through the egress proxy

The gateway's bind design assumed a jail could dial a Docker bridge address directly. That assumption
predates per-agent network segments (MG-36) and does not hold. Measured on Docker 29.4.3, from a
container on an `Internal=true` network:

| from | to | result |
|---|---|---|
| internal-net container | **its own** bridge's host-side address | **reachable** |
| internal-net container | a *different* bridge's host-side address | refused |
| internal-net container | the Docker host's own (eth0) address | refused |
| egress-capable container (the proxy's posture) | the Docker host's own address | **reachable** |

Every agent segment gets its **own** bridge, created at spawn, while Kestrel binds once at startup — so no
single fixed gateway bind address is directly reachable by every jail. The viable route is
**jail → its own segment's egress proxy → the daemon's gateway**, which is why the gateway binds one fixed
private address and the jail reaches it through the proxy it already routes through.

This also rules out the tinyproxy `upstream` mechanism for this purpose: `upstream` is configured
per-destination-host on a proxy shared by every agent, so routing model hosts through the gateway would
capture **OAuth** agents' traffic too, and the gateway would reject their (unrecognised) session tokens —
breaking exactly the path that must not change.
