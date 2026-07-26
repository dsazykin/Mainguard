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

# Serialize concurrent reloads. The entrypoint fires one when the first config lands and the daemon
# fires one explicitly after every push, so two can overlap — and two overlapping runs of the
# stop-then-start sequence below can leave a daemon stopped by the other run's pkill and never
# restarted. `mkdir` is the portable atomic test-and-set. The wait is bounded so a crashed holder
# degrades to the old racy behaviour rather than wedging the proxy forever.
i=0
while ! mkdir "$CONF_DIR/.reload.lock" 2>/dev/null && [ "$i" -lt 100 ]; do
    i=$((i + 1))
    sleep 0.1
done
trap 'rmdir "$CONF_DIR/.reload.lock" 2>/dev/null || true' EXIT INT TERM

# Is a process with this exact name alive? Walks /proc directly instead of using pgrep, so the health
# verdict below can never be a false "failed" just because a tool is missing from the image — the exact
# class of silent breakage this script already suffered with pkill.
is_running() {
    for proc in /proc/[0-9]*; do
        [ -r "$proc/comm" ] || continue
        [ "$(cat "$proc/comm" 2>/dev/null)" = "$1" ] || continue
        # Skip the dead. Both daemons are backgrounded from a docker-exec shell that then exits, so
        # they are orphaned and reparented to pid 1. A dead-but-unreaped process keeps its name in
        # /proc indefinitely, and counting it as alive makes a successful stop look like a failed one
        # (and, worse, would make a CRASHED dnsmasq report healthy). The container is created with
        # HostConfig.Init so pid 1 actually reaps; this covers the window before it does.
        state=$(sed -n 's/^State:[[:space:]]*\([A-Z]\).*/\1/p' "$proc/status" 2>/dev/null)
        case "$state" in
            Z | X) continue ;;
        esac
        return 0
    done
    return 1
}

# Stop a daemon and WAIT for it to actually be gone, reporting whether it went. Restarting without
# waiting means the replacement loses the bind race and dies; not checking at all means a stop that
# silently failed leaves the OLD policy serving while everything downstream looks healthy — which is
# exactly the shape of the pkill/CAP_KILL bug this guards. Returns non-zero if it is still alive.
stop_daemon() {
    pkill -x "$1" 2>/dev/null || true
    i=0
    while [ "$i" -lt 50 ] && is_running "$1"; do
        i=$((i + 1))
        sleep 0.1
    done
    ! is_running "$1"
}

# Is something LISTENING on this port? $1 = "tcp" or "udp", $2 = port in uppercase hex.
#
# "The process exists" and "the socket is accepting connections" are different facts, and the gap
# between them is a real window: both daemons fork and bind afterwards, so a readiness check that only
# looks for the process reports ready while connections are still refused. Parsed straight out of
# /proc/net/{tcp,udp} so this needs no ss/netstat/lsof in the image. TCP state 0A is LISTEN; a UDP
# socket appears in the table once bound.
is_listening() {
    awk -v want="$2" -v proto="$1" '
        NR > 1 {
            split($2, a, ":")
            if (a[2] == want && (proto == "udp" || $4 == "0A")) { found = 1 }
        }
        END { exit(found ? 0 : 1) }
    ' "/proc/net/$1"
}

# Wait for a freshly-started daemon to be RUNNING and LISTENING. $1 = process name, $2 = proto,
# $3 = port (hex). Reporting "ok" before the socket is up is what makes a caller's "the proxy is
# ready" a lie, and the caller then hands that proxy to an agent whose very first request is refused.
wait_started() {
    i=0
    while [ "$i" -lt 50 ]; do
        if is_running "$1" && is_listening "$2" "$3"; then
            return 0
        fi
        i=$((i + 1))
        sleep 0.1
    done
    return 1
}

# 1. Pinned DNS: dnsmasq answers allowlisted names only; everything else NXDOMAIN (kills DNS exfil).
#    MG-7: this is now the agents' ONLY resolver (the daemon pins HostConfig.Dns at this container), so
#    the rendered config finally sits in the resolution path instead of beside it.
if [ -f "$CONF_DIR/dnsmasq.conf" ]; then
    # dnsmasq is the jails' ONLY resolver now (MG-7), so a dead — or stale — dnsmasq is a whole-fleet
    # egress failure rather than a silently-bypassed control. The verdict is recorded where an operator
    # and the RequiresDocker suite can both read it.
    if ! stop_daemon dnsmasq; then
        # Still alive after SIGTERM: it is serving the PREVIOUS policy and the new one will never load.
        echo stale > "$CONF_DIR/dnsmasq.status"
    else
        # --log-facility to a FILE, not the default syslog (there is no syslogd here) and not the exec's
        # stderr. dnsmasq's startup failures are otherwise completely silent, which is precisely how
        # "dnsmasq never actually started" survived undetected for so long: it exits 5 and the only
        # trace is a syslog line nothing collects. Now the reason is always on disk.
        #
        # Detach the daemon's stdin/stdout/stderr from the caller's pipe. When reload.sh is invoked
        # over a Docker exec, a backgrounded child that inherits the exec's stdout keeps the attach
        # stream open forever, so ReadOutputToEnd on the daemon side never sees EOF (the setup hangs).
        dnsmasq --conf-file="$CONF_DIR/dnsmasq.conf" --keep-in-foreground \
            --pid-file="$CONF_DIR/dnsmasq.pid" --log-facility="$CONF_DIR/dnsmasq.log" \
            </dev/null >/dev/null 2>&1 &

        if wait_started dnsmasq udp 0035; then
            echo ok > "$CONF_DIR/dnsmasq.status"
        else
            echo failed > "$CONF_DIR/dnsmasq.status"
            # The log is the only record of WHY: dnsmasq exits 5 silently on a capability problem.
            echo "[mainguard-egress-proxy] dnsmasq FAILED to start; its log follows"
            [ -f "$CONF_DIR/dnsmasq.log" ] && tail -20 "$CONF_DIR/dnsmasq.log" || true
        fi
    fi
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
    if ! stop_daemon tinyproxy; then
        # Same failure mode as dnsmasq: an unkillable predecessor keeps the OLD allow-filter in force,
        # so a host the user just REMOVED from the allowlist stays reachable.
        echo stale > "$CONF_DIR/tinyproxy.status"
    else
        # tinyproxy daemonizes (forks + parent exits); redirect its fds too so the daemon child never
        # holds the exec's attach pipe open.
        tinyproxy -c "$CONF_DIR/tinyproxy.conf" </dev/null >/dev/null 2>&1
        if wait_started tinyproxy tcp 22B8; then
            echo ok > "$CONF_DIR/tinyproxy.status"
        else
            echo failed > "$CONF_DIR/tinyproxy.status"
            echo "[mainguard-egress-proxy] tinyproxy FAILED to start"
        fi
    fi
fi

echo "[mainguard-egress-proxy] reload: dnsmasq=$(cat "$CONF_DIR/dnsmasq.status" 2>/dev/null || echo none) tinyproxy=$(cat "$CONF_DIR/tinyproxy.status" 2>/dev/null || echo none)"

# 3. iptables backstop: a default-deny INPUT chain in THIS namespace (MG-18 — agent containment is the
#    Internal network, which the daemon now asserts on every reuse; what this chain bounds is which of
#    the proxy's own listeners an agent may reach, and at which address).
if [ -f "$CONF_DIR/backstop.sh" ]; then
    sh "$CONF_DIR/backstop.sh"
fi
