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

`tinyproxy-upstreams` carries the P2-08 gateway fronting: `upstream` directives routing every model-API
host through the AI gateway. `reload.sh` **appends** it into the `tinyproxy.conf` it generates —
tinyproxy 1.11 has no `Include` directive and refuses to start on one — and that append is the load
step. Without it the file is rendered on every push and read by nothing, so fronting is silently inert
and model traffic goes straight to the provider. `SandboxEgressDockerTests`
`RenderedGatewayUpstreams_ShouldBeInEffectOnTheRunningProxy_NotMerelyWritten` asserts the *running*
proxy routes a model host to the gateway, never that the file exists.

The repo's git host is **not** on the agent allowlist (A6). Git-dependency fetches go through the
daemon read-only git proxy (`DaemonGitProxy`), which is fetch-only and refuses push structurally.
