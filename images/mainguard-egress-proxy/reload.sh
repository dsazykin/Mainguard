#!/bin/sh
# Applies the daemon-rendered egress policy (P2-07). Called by the entrypoint (once, as `--boot`) and by
# the daemon on every config push. The four artefacts (tinyproxy-filter, tinyproxy-upstreams,
# dnsmasq.conf, backstop.sh) are rendered by EgressProxyConfigurator/EgressProxyConfig from the
# allowlist. Every one of them has to be LOADED here, not merely present: an artefact this script does
# not consume is policy that silently does nothing.
#
# MG-25: this container now runs with a READ-ONLY rootfs, so every path written at runtime has to live
# on a tmpfs. CONF_DIR moved from /etc/mainguard (image layer, now immutable) to /run/mainguard, and the
# generated tinyproxy config moved out of /etc/tinyproxy for the same reason. THIS script stays on the
# read-only layer on purpose — the policy that contains the agents must not be rewritable from inside.
#
# MG-41 — A RELOAD IS AN OUTAGE, SO IT HAPPENS ONLY WHEN IT CHANGES SOMETHING.
# Stopping and restarting the two daemons takes the listeners down: sampling /proc/net every 10ms across
# a reload measured tinyproxy's listener down ~80ms and dnsmasq's ~20ms. A config push runs on EVERY
# agent spawn while the rendered policy is almost always byte-identical to the one already loaded, so
# the overwhelmingly common reload was pure downtime for every other agent's egress. Two gates now stand
# in front of the restart, and BOTH fail towards restarting:
#
#   1. `--boot` (the entrypoint's one reload) yields the moment any other reload has run or is running.
#      The entrypoint used to reload unconditionally, unsynchronised with the daemon's push — measured,
#      the live tinyproxy's start time matched the entrypoint's reload, not the push's, and locally it
#      landed ~1.5s BEFORE the daemon's setup returned (on a slower runner, after). A caller told "the
#      proxy is ready" could therefore still have the proxy restart underneath it.
#   2. A push skips the restart only when the rendered daemon config is byte-identical to the config the
#      LIVE daemons were started from (applied.digest, recorded only after both were confirmed
#      listening) AND both are still listening right now. Anything else — a changed byte, a missing
#      record, a dead daemon — restarts both, which is the pre-existing, proven path.
#
# What is deliberately NOT done: replacing the restart with SIGHUP. Measured in this exact image —
# tinyproxy 1.11.1 does re-read its filter on SIGHUP, but dnsmasq 2.90 does NOT re-read its config file.
# A name removed from the allowlist and SIGHUP'd still answered its old address (10.0.0.2), while the
# same config applied by a restart answered 0.0.0.0. SIGHUP would therefore reintroduce exactly the bug
# CAP_KILL was granted to fix — a removed host that stays reachable — and more quietly. The backstop is
# unaffected either way: it is a complete-table `iptables-restore` (one netlink transaction, ~31ms,
# idempotent, no observable intermediate state), so it is re-applied on EVERY reload, skip or not.
set -eu

CONF_DIR=/run/mainguard
PROXY_PORT=8888

# Everything this script tracks lives on the /run tmpfs beside the policy, which is what makes the
# bookkeeping correct across a `docker start`: the tmpfs comes back EMPTY, so a revived container has
# no applied digest and no "a reload has run" mark, and its boot reload arms itself exactly as a freshly
# created container's does.
LOCK_DIR="$CONF_DIR/.reload.lock"
RAN_MARK="$CONF_DIR/reload.ran"
APPLIED_DIGEST="$CONF_DIR/applied.digest"

# The entrypoint's single boot reload passes --boot; the daemon's config push passes nothing.
BOOT=no
if [ "${1:-}" = "--boot" ]; then
    BOOT=yes
fi

log() { echo "[mainguard-egress-proxy] $*"; }

mkdir -p "$CONF_DIR"

# Serialize concurrent reloads. `mkdir` is the portable atomic test-and-set. Two overlapping runs of the
# stop-then-start sequence below can leave a daemon stopped by the other run's pkill and never
# restarted, so the lock is load-bearing — but the two callers want opposite things when they cannot
# have it immediately, and conflating them is what left the boot reload racing the push.
have_lock=no
if mkdir "$LOCK_DIR" 2>/dev/null; then
    have_lock=yes
elif [ "$BOOT" = yes ]; then
    # The boot reload exists only to cover "config landed and nothing loaded it". Somebody is loading it
    # right now, so that job is done — and the one thing this reload must never do is restart the
    # daemons after a push has already told a caller the proxy is ready. Yielding is the correct
    # outcome here, not a degraded one.
    log "boot: another reload holds the lock — leaving the policy to it"
    exit 0
else
    # A push MUST land: its whole purpose is to make a policy edit — including a REMOVED host — reach
    # the running daemons, so it waits rather than yielding. The bound comfortably exceeds the longest a
    # reload can hold the lock (two stops at 5s + two starts at 5s = ~20s), so it cannot expire while a
    # boot reload is legitimately working; it exists so a CRASHED holder degrades to the old racy
    # behaviour instead of wedging the proxy forever.
    i=0
    while [ "$i" -lt 300 ]; do
        i=$((i + 1))
        sleep 0.1
        if mkdir "$LOCK_DIR" 2>/dev/null; then
            have_lock=yes
            break
        fi
    done
fi

# Release only a lock this run actually took. The old trap released it unconditionally, so a run that
# gave up waiting went on to rmdir the HOLDER's lock — turning a bounded wait into a silent unlock of
# somebody else's critical section.
trap 'if [ "$have_lock" = yes ]; then rmdir "$LOCK_DIR" 2>/dev/null || true; fi' EXIT INT TERM

# MG-41 gate 1: the boot reload never runs after a push has reloaded. The mark is set by every reload
# that completes, so this is "has any reload run in this container's lifetime?" — deliberately NOT "did
# it succeed". Re-running an identical reload that just failed has no new information to act on, while
# restarting the daemons underneath a caller that was told "ready" is a real outage; the next config
# push is the thing that retries, and it does so through gate 2 below.
# PROBE M24 (DO NOT MERGE): gate 1 removed — a --boot reload no longer yields to a config push that
# already ran, so the boot path restarts tinyproxy/dnsmasq underneath a caller already told "ready".
if [ "$BOOT" = yes ] && [ -f "$RAN_MARK" ] && false; then
    log "boot: a config push has already reloaded this policy — leaving the daemons running"
    exit 0
fi

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

# A fingerprint of the config the two daemons parse — and ONLY that. backstop.sh is deliberately absent:
# it is re-applied on every reload regardless (atomic and idempotent), and it changes on every agent
# spawn (it names the proxy's address on each per-agent segment), so including it would make the digest
# differ every single push and the gate would never fire. Per-file hashes rather than a concatenation so
# content cannot slide between files unnoticed; "absent" is itself a distinct state.
config_digest() {
    for artefact in dnsmasq.conf tinyproxy-filter tinyproxy-upstreams; do
        if [ -f "$CONF_DIR/$artefact" ]; then
            echo "$artefact $(sha256sum < "$CONF_DIR/$artefact" | cut -d' ' -f1)"
        else
            echo "$artefact absent"
        fi
    done | sha256sum | cut -d' ' -f1
}

# Both daemons alive AND accepting. This is the other half of the skip gate: a matching digest proves
# the policy did not change, and this proves the processes that loaded it are still the ones serving.
daemons_listening() {
    is_running dnsmasq && is_listening udp 0035 && is_running tinyproxy && is_listening tcp 22B8
}

# MG-41 gate 2. Read the fingerprint BEFORE anything is stopped: recording what was on disk when the
# daemons were started can only ever be older than what they actually parsed, and an older digest fails
# towards an unnecessary restart. (The reverse — recording something NEWER than what was loaded — is the
# dangerous direction, and the re-check after the restart below closes it.)
desired_digest=$(config_digest)
restart_daemons=yes
if [ -f "$CONF_DIR/dnsmasq.conf" ] && [ -f "$CONF_DIR/tinyproxy-filter" ] \
    && [ -f "$APPLIED_DIGEST" ] && [ "$(cat "$APPLIED_DIGEST")" = "$desired_digest" ] \
    && daemons_listening; then
    restart_daemons=no
fi

if [ "$restart_daemons" = no ]; then
    log "reload: policy unchanged (digest $desired_digest) and both daemons listening — not restarting them"
else
    # Invalidate FIRST. From here until the restart is confirmed there is no honest answer to "which
    # bytes are the live daemons serving", and a reload that dies in the middle (exec timeout, the
    # container killed) must not leave a record that lets the NEXT push skip. Absent = restart.
    rm -f "$APPLIED_DIGEST"

    # 1. Pinned DNS: dnsmasq answers allowlisted names only; everything else NXDOMAIN (kills DNS exfil).
    #    MG-7: this is now the agents' ONLY resolver (the daemon pins HostConfig.Dns at this container),
    #    so the rendered config finally sits in the resolution path instead of beside it.
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
        # P2-08 gateway fronting. The daemon renders the model-API `upstream` lines to their own artefact
        # (EgressProxyConfig.RenderTinyproxyUpstreams) and NOTHING used to load them: the file was written
        # on every push and read by no one, so fronting was inert — every model-API request went straight to
        # the provider, with none of the gateway's token bucket / budget / 429 handling, and no error
        # anywhere because the file existed and looked correct.
        #
        # This APPENDS the rendered lines into the config tinyproxy actually parses rather than adding an
        # `Include` for them: tinyproxy 1.11 (this image's version) has no Include directive at all and
        # refuses to start on one — "ERROR: Syntax error on line N / Unable to parse config file. Not
        # starting." Verified against the image. Appending is the only way to get them loaded, and because
        # the config above is regenerated from scratch on every reload, the appended set is replaced (never
        # accumulated) each time. The daemon writes this artefact on every push — header-only when no
        # gateway is configured — so an absent file means a pre-P2-08 proxy, not "no policy".
        if [ -f "$CONF_DIR/tinyproxy-upstreams" ]; then
            cat "$CONF_DIR/tinyproxy-upstreams" >> "$CONF_DIR/tinyproxy.conf"
        fi
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

    log "reload: dnsmasq=$(cat "$CONF_DIR/dnsmasq.status" 2>/dev/null || echo none) tinyproxy=$(cat "$CONF_DIR/tinyproxy.status" 2>/dev/null || echo none)"

    # Record what the LIVE daemons are serving — the only claim a later push is allowed to skip on. Three
    # conditions, all necessary: both daemons came up (so there are processes to attribute the bytes to),
    # and the artefacts are STILL the ones fingerprinted above. That last re-read is what makes a
    # concurrent push safe: writes are separate execs and do not take this lock, so if the policy moved
    # underneath this run we cannot say which bytes were parsed — and then we say nothing at all, leaving
    # the next push to restart.
    if [ "$(cat "$CONF_DIR/dnsmasq.status" 2>/dev/null || echo none)" = ok ] \
        && [ "$(cat "$CONF_DIR/tinyproxy.status" 2>/dev/null || echo none)" = ok ] \
        && [ "$(config_digest)" = "$desired_digest" ]; then
        echo "$desired_digest" > "$APPLIED_DIGEST"
    fi
fi

# 3. iptables backstop: a default-deny INPUT chain in THIS namespace (MG-18 — agent containment is the
#    Internal network, which the daemon now asserts on every reuse; what this chain bounds is which of
#    the proxy's own listeners an agent may reach, and at which address). Applied on EVERY reload,
#    including a skipped one: it is a complete-table iptables-restore, i.e. one netlink transaction with
#    no observable intermediate state, so re-applying it costs ~31ms and drops nothing — and the
#    address set it admits DOES change on every spawn, which is precisely what a new segment needs.
if [ -f "$CONF_DIR/backstop.sh" ]; then
    sh "$CONF_DIR/backstop.sh"
fi

# "A reload has run in this container's lifetime" — the boot reload's gate (see MG-41 gate 1 above).
# Written last so it means the whole sequence completed, and on the tmpfs so a `docker start` re-arms it.
: > "$RAN_MARK"
