#!/bin/sh
# mainguard-egress-proxy entrypoint (P2-07). Waits for the daemon to render the allowlist config, then
# starts dnsmasq (pinned DNS) + tinyproxy (CONNECT allowlist) and applies the iptables backstop.
#
# MG-25: the rendered policy lives on the /run tmpfs, not /etc — the rootfs is read-only now.
set -eu

CONF_DIR=/run/mainguard

# Wait for the daemon's first config push (EgressProxyConfigurator.PushConfigAsync).
i=0
while [ ! -f "$CONF_DIR/tinyproxy-filter" ] && [ "$i" -lt 60 ]; do
    sleep 1
    i=$((i + 1))
done

/etc/mainguard/reload.sh || true

# Keep the container alive; reloads re-run reload.sh on each config push.
exec sleep infinity
