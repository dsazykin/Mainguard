#!/bin/sh
# mainguard-egress-proxy entrypoint (P2-07). Waits for the daemon to render the allowlist config, then
# starts dnsmasq (pinned DNS) + tinyproxy (CONNECT allowlist) and applies the iptables backstop.
#
# MG-25: the rendered policy lives on the /run tmpfs, not /etc — the rootfs is read-only now.
set -eu

CONF_DIR=/run/mainguard

# Wait for the daemon's first config push (EgressProxyConfigurator.PushConfigAsync) to COMPLETE.
# backstop.sh is the LAST artefact the daemon writes, so waiting on it (rather than on the first file,
# tinyproxy-filter) is what makes this a wait for a whole config instead of a race against a partial
# one: `> file` creates the path the instant the write begins, so the old condition fired while the
# allowlist was still arriving and started tinyproxy against an empty — i.e. deny-everything — filter.
i=0
while [ ! -f "$CONF_DIR/backstop.sh" ] && [ "$i" -lt 60 ]; do
    sleep 1
    i=$((i + 1))
done

/etc/mainguard/reload.sh || true

# Keep the container alive; reloads re-run reload.sh on each config push.
exec sleep infinity
