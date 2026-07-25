# Mainguard egress proxy image (P2-07).
#
# Built in CI / the release pipeline ONLY — never at runtime (G-16). This is the sole route out of the
# internal `mainguard-agents` network: an allowlist-driven HTTP(S) CONNECT proxy (tinyproxy) + pinned DNS
# (dnsmasq answering allowlisted names only, NXDOMAIN otherwise — kills DNS exfiltration) + an iptables
# backstop that DROPs non-proxy egress (proxy-env-only enforcement is a rejection trigger).
#
# The daemon (EgressProxyConfigurator) renders the allowlist to the three config artefacts at runtime
# and calls /etc/mainguard/reload.sh. MG-25: the container runs with a READ-ONLY rootfs, so the rendered
# artefacts live on the /run tmpfs while the scripts stay on the immutable image layer:
#   /run/mainguard/tinyproxy-filter  — the CONNECT allow-filter (default-deny)
#   /run/mainguard/dnsmasq.conf      — pinned DNS (allowlisted names only; MG-7: the agents' ONLY resolver)
#   /run/mainguard/backstop.sh       — the proxy-namespace INPUT backstop

FROM debian:bookworm-slim

# procps is NOT optional: reload.sh has always ended each daemon with `pkill` before restarting it, and
# `pkill` lives in procps, which was never installed. Every one of those calls was a silent no-op
# (`|| true` swallowed the "not found"), so on the SECOND and later reloads the fresh dnsmasq/tinyproxy
# could not bind their already-taken ports and died, leaving the ORIGINAL processes serving the ORIGINAL
# policy. In other words an allowlist edit — including removing a host — never reached a running proxy.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        tinyproxy dnsmasq iptables ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/mainguard
COPY reload.sh /etc/mainguard/reload.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /etc/mainguard/reload.sh /entrypoint.sh

# Capabilities the daemon adds at create time, and why (MG-25):
#   NET_ADMIN        — the iptables backstop. Debian bookworm's `iptables` is the nft variant, which
#                      drives nftables over netlink, so NET_RAW (needed only by iptables-legacy's
#                      AF_INET/SOCK_RAW libiptc socket) is NOT required and is no longer granted.
#   NET_BIND_SERVICE — dnsmasq's port 53. Under CapDrop ALL even root cannot bind below 1024.
#   SETGID/SETUID    — dnsmasq's own privilege drop: it calls setgroups()/setgid()/setuid() at startup
#                      unconditionally and exits 5 without them. Granting these is what lets dnsmasq
#                      run UNPRIVILEGED; without them it did not run at all.
ENTRYPOINT ["/entrypoint.sh"]
