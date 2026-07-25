#!/bin/sh
# Applies the daemon-rendered egress policy (P2-07). Called by the entrypoint and on every config push.
# The three artefacts are rendered by EgressProxyConfigurator/EgressProxyConfig from the allowlist.
#
# MG-25: this container now runs with a READ-ONLY rootfs, so every path written at runtime has to live
# on a tmpfs. CONF_DIR moved from /etc/mainguard (image layer, now immutable) to /run/mainguard, and the
# generated tinyproxy config moved out of /etc/tinyproxy for the same reason. THIS script stays on the
# read-only layer on purpose — the policy that contains the agents must not be rewritable from inside.
set -eu

CONF_DIR=/run/mainguard
PROXY_PORT=8888

mkdir -p "$CONF_DIR"

# 1. Pinned DNS: dnsmasq answers allowlisted names only; everything else NXDOMAIN (kills DNS exfil).
#    MG-7: this is now the agents' ONLY resolver (the daemon pins HostConfig.Dns at this container), so
#    the rendered config finally sits in the resolution path instead of beside it.
if [ -f "$CONF_DIR/dnsmasq.conf" ]; then
    pkill dnsmasq 2>/dev/null || true
    # Detach the daemon's stdin/stdout/stderr from the caller's pipe. When reload.sh is invoked
    # over a Docker exec, a backgrounded child that inherits the exec's stdout keeps the attach
    # stream open forever, so ReadOutputToEnd on the daemon side never sees EOF (the setup hangs).
    dnsmasq --conf-file="$CONF_DIR/dnsmasq.conf" --keep-in-foreground \
        --pid-file="$CONF_DIR/dnsmasq.pid" </dev/null >/dev/null 2>&1 &
fi

# 2. HTTP(S) CONNECT allowlist: tinyproxy with FilterDefaultDeny + the rendered host filter.
if [ -f "$CONF_DIR/tinyproxy-filter" ]; then
    cat > "$CONF_DIR/tinyproxy.conf" <<EOF
Port $PROXY_PORT
Listen 0.0.0.0
Timeout 600
PidFile "$CONF_DIR/tinyproxy.pid"
Filter "$CONF_DIR/tinyproxy-filter"
FilterDefaultDeny Yes
FilterExtended On
ConnectPort 443
ConnectPort 80
EOF
    pkill tinyproxy 2>/dev/null || true
    # tinyproxy daemonizes (forks + parent exits); redirect its fds too so the daemon child never
    # holds the exec's attach pipe open.
    tinyproxy -c "$CONF_DIR/tinyproxy.conf" </dev/null >/dev/null 2>&1
fi

# 3. iptables backstop: a default-deny INPUT chain in THIS namespace (MG-18 — agent containment is the
#    Internal network, which the daemon now asserts on every reuse; what this chain bounds is which of
#    the proxy's own listeners an agent may reach, and at which address).
if [ -f "$CONF_DIR/backstop.sh" ]; then
    sh "$CONF_DIR/backstop.sh"
fi
