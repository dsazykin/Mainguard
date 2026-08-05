# Budget enforcement: BYOK is metered, OAuth is not

Status: **BYOK metering implemented, wired, on by default, and proven end to end against real
containers. OAuth budgeting is an open problem with no committed direction.**

> **Which CLIs this actually covers.** Confinement needs the vendor's CLI to honour a base-URL
> environment variable. Verified 2026-08-05 against the pinned tarballs/binaries in
> `adapters.starter.json` (each re-downloaded and sha256-checked against its pin before inspection):
>
> | CLI | base-URL variable | upstream | confinable |
> |---|---|---|---|
> | claude-code | `ANTHROPIC_BASE_URL` | `api.anthropic.com` | **yes** |
> | gemini-cli | `GOOGLE_GEMINI_BASE_URL` | `generativelanguage.googleapis.com` | **yes** |
> | codex | *none exists* | — | **no** |
> | qwen-code | *not sufficient alone* | — | **no** |
> | opencode | *none exists* | — | **no** |
>
> Codex's Linux binary contains `OPENAI_API_KEY` and `CODEX_API_KEY` and **zero** occurrences of
> `OPENAI_BASE_URL`; it takes its endpoint from `config.toml` (`openai_base_url`,
> `model_providers.<id>.base_url`) only. qwen-code does read `OPENAI_BASE_URL`, but its
> `getAuthTypeFromEnv()` selects the OpenAI provider only when `OPENAI_API_KEY` **and**
> `OPENAI_MODEL`/`QWEN_MODEL` **and** `OPENAI_BASE_URL` are all set — and the adapter declares no
> `apiKeyEnvVar` at all, so there is no BYOK key to confine. opencode is multi-provider with per-provider
> base URLs in its own config.
>
> **So a BYOK codex / qwen / opencode jail still receives the raw provider key.** That is the honest
> residual scope of MG-4, and it is deliberately not papered over with a plausible-looking variable name:
> a wrong name yields a confinement that silently does nothing, which is worse than declaring none.
> Closing codex needs a config-file writer on the spawn path, which the manifest format does not express.

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

- The gateway's own address is now allowlisted too (see below), which is what makes confinement reachable.
  It is a token-authenticated endpoint: an agent presenting no `mg_sess_` token gets a 401, and an agent
  cannot influence its own upstream because it never supplies one.

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

## The proxy has to be *willing* to carry it — the step that was missing

Choosing that route is not the same as having it. The jail's `HTTP_PROXY`/`HTTPS_PROXY` name tinyproxy and
`NO_PROXY` covers only loopback plus the internal git proxy, so a confined request arrives at tinyproxy
naming **the gateway** as its destination host — and tinyproxy runs `FilterDefaultDeny` against an
anchored allowlist of provider and CLI-service hosts which never contained the daemon's own address.

Mainguard's own proxy refused it. Measured from inside a real jail: `403`, with tinyproxy's error page.
So the confinement built in #298 was not merely inert — switching the gateway on would have **broken every
BYOK agent it touched**, and the refusal would have looked like a provider outage from inside the jail.

The fix is one allowlist entry: the gateway's own host, rendered as a **direct-route** `AgentService`
entry (`EgressProxyConfigurator.CombineGatewayHost`). Deliberately not a `ModelApi` entry, because those
also emit the `upstream` directive ruled out above. It moves no existing host's route, so an OAuth agent
sees no change at all.

## Enabled by default, and why that is safe

`MAINGUARD_GATEWAY_BIND` was the only way to turn the gateway on, and nothing in the repo ever set it —
so in every supported deployment a BYOK jail received the raw provider key, which is precisely what MG-4
exists to prevent, and it mattered most for agents spawned from **untrusted external-PR content**.

The default is now `auto`: `GatewayBindPolicy.TryResolvePrivateHostAddress()` picks a private, non-loopback
IPv4 (loopback is excluded because inside a container `127.0.0.1` is the container — a gateway there binds
cleanly and confines nothing). `MAINGUARD_GATEWAY_BIND=off` / `--gateway-bind off` restores the old
posture; an explicit address still pins it and is still policed by `GatewayBindPolicy`.

**Turning it on cannot break a working agent, and that is enforced rather than hoped.** Confinement
engages only when *all* of these hold, and each failure falls back to exactly the pre-gateway behaviour:

1. a provider key was supplied (an OAuth agent has none, so it is never confined);
2. the CLI declares both a base-URL variable and a model host (the table at the top);
3. **the jail's own egress proxy has been measured able to dial the bound address**
   (`IEgressPolicy.CanProxyReachAsync` — a real TCP connect from inside the proxy container, per spawn,
   cached on success only).

Step 3 is what converts "the auto-resolved address is reachable" from an assumption into a checked
precondition. A wrong guess costs a skipped confinement, never a broken agent — asserted in
`GatewayConfinementDockerTests.GatewayUnreachableFromTheProxy_SkipsConfinement_SoTheAgentKeepsWorking`,
which spawns a real jail against an unroutable private address and proves it keeps the raw key and its
direct route.

## How this is proven

`Mainguard.Server.Tests/Agents/GatewayConfinementDockerTests.cs` (RequiresDocker) drives the real spawn
chain against real containers and issues the model request **from inside the jail**, sourced from the same
`/run/secrets/agent.env` the CLI sources and routed through the container's own proxy env — nothing about
the request is constructed by the test. It asserts the traffic transits the gateway, that `BudgetLedger`
is charged 41 tokens, that a second call over a 1-token cap is refused with a soft `402` with nothing
reaching the provider, that the key is absent from the container spec, the credential tmpfs and the
agent's effective environment, and that an OAuth agent is unconfined with its login restored and its
provider host still carried by the proxy.

The only faked leg is the provider itself: `ModelProxyMiddleware` always dials `https://<bound upstream>`,
and a test cannot bill a real key.
