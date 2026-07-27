# mainguard-egress-proxy

The sole route out of the internal `mainguard-agents` network (P2-07). Default-deny.

**Built in CI / the release pipeline — never at runtime** (G-16).

Three layers, all driven by the daemon-rendered allowlist:

1. **tinyproxy** — an HTTP(S) CONNECT proxy with `FilterDefaultDeny Yes` + the rendered host allow-filter.
2. **dnsmasq** — pinned DNS that answers allowlisted names only; everything else is NXDOMAIN (kills DNS
   exfiltration).
3. **iptables backstop** — DROPs any non-proxy egress, so an agent that ignores `HTTP_PROXY` and dials a
   raw IP is still dropped. Enforcing egress by proxy env alone (without this backstop) is a named
   rejection trigger.

The daemon's `EgressProxyConfigurator` renders the allowlist to `/run/mainguard/tinyproxy-filter`,
`/run/mainguard/tinyproxy-upstreams`, `/run/mainguard/dnsmasq.conf`, and `/run/mainguard/backstop.sh`
(see `EgressProxyConfig` for the exact rendering) and calls `reload.sh`.

**A reload is an outage, so it only happens when it changes something (MG-41).** Restarting the two
daemons takes their listeners down — ~80 ms for tinyproxy, ~20 ms for dnsmasq, measured by sampling
`/proc/net` every 10 ms — and a config push runs on every agent spawn, where the rendered policy is
almost always identical to the one already loaded. So `reload.sh` restarts the daemons only when the
rendered config differs from what the live ones were started from (`applied.digest`, recorded only
after both were confirmed listening) or one of them is no longer listening; and the entrypoint's boot
reload (`reload.sh --boot`) yields to any reload that has already run or is running, so it can never
restart the proxy after the daemon's `EnsureReadyAsync` has told a caller it is ready. Both gates fail
towards restarting, and a policy *change* always restarts. The backstop is re-applied on every reload
either way — it is one `iptables-restore` transaction and drops nothing.

SIGHUP is not a substitute for the restart, on measurement rather than principle: tinyproxy 1.11.1 does
re-read its filter on SIGHUP, but **dnsmasq 2.90 does not re-read its config file** — a name removed
from the allowlist and SIGHUP'd still answered its old address, while a restart with the same config
answered `0.0.0.0`. Using it would reintroduce the exact bug `CAP_KILL` was granted to fix: a host
removed from the allowlist that stays reachable.

`tinyproxy-upstreams` carries the P2-08 gateway fronting: `upstream` directives routing every model-API
host through the AI gateway. `reload.sh` **appends** it into the `tinyproxy.conf` it generates —
tinyproxy 1.11 has no `Include` directive and refuses to start on one — and that append is the load
step. Without it the file is rendered on every push and read by nothing, so fronting is silently inert
and model traffic goes straight to the provider. `SandboxEgressDockerTests`
`RenderedGatewayUpstreams_ShouldBeInEffectOnTheRunningProxy_NotMerelyWritten` asserts the *running*
proxy routes a model host to the gateway, never that the file exists.

The repo's git host is **not** on the agent allowlist (A6). Git-dependency fetches go through the
daemon read-only git proxy (`DaemonGitProxy`), which is fetch-only and refuses push structurally.
