#!/bin/sh
# mainguard-egress-proxy entrypoint (P2-07). Waits for the daemon to render the allowlist config, then
# starts dnsmasq (pinned DNS) + tinyproxy (CONNECT allowlist) and applies the iptables backstop.
#
# MG-25: the rendered policy lives on the /run tmpfs, not /etc — the rootfs is read-only now. Note that
# a tmpfs is EMPTY again after `docker start`, so a revived container has no config until the daemon
# re-pushes it; that is expected, and the wait below is what covers the gap.
#
# Everything here logs to stdout on purpose. This container's boot used to be completely silent, so a
# failure to come up produced no evidence at all — and on CI nobody can run `docker logs` after the
# fact because the container dies with the job. The daemon now captures these lines into the failure
# it raises (EgressProxyConfigurator.DescribeContainerFailureAsync), so this output IS the diagnosis.
set -eu

CONF_DIR=/run/mainguard

log() { echo "[mainguard-egress-proxy] $*"; }

log "boot: pid=$$ conf_dir=$CONF_DIR readonly_rootfs=$( [ -w / ] && echo no || echo yes )"

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

if [ -f "$CONF_DIR/backstop.sh" ]; then
    log "boot: config arrived after ${i}s"
else
    # Not fatal: the daemon pushes config and re-runs reload.sh itself, so the container must stay up
    # and wait rather than exit. Exiting here would turn a slow push into an unstartable proxy.
    log "boot: WARNING no config after ${i}s — staying up; the daemon's own push will reload"
fi

# MG-41: `--boot`, and the flag is the whole fix. This reload used to be unconditional and
# unsynchronised with the daemon's push, which runs its OWN reload right after writing the artefacts —
# so the two raced, and the loser restarted tinyproxy/dnsmasq a second time for no reason. Measured: the
# live tinyproxy's start time matched THIS reload, not the push's, and locally it landed ~1.5s before
# the daemon's setup call returned. On a slower runner it lands after, which means a caller that was
# told "the proxy is ready" could still have the proxy restart underneath it (~80ms of tinyproxy
# downtime; CI saw it as connect failures at probes 6-8 with the rc=7/t=0.000000 closed-port signature).
#
# With the flag, reload.sh yields whenever another reload has already run or is running, so exactly one
# of the two ever restarts anything and it is never the one that lands last. There is no timing
# assumption here and nothing sleeps: the decision is made under the reload lock, off a mark on the
# tmpfs, so it is correct in either arrival order.
if /etc/mainguard/reload.sh --boot; then
    log "boot: reload ok"
else
    log "boot: WARNING reload exited $? — staying up so the daemon can retry"
fi

log "boot: idle (pid 1 is the init; reloads re-run reload.sh on each config push)"

# Keep the container alive; reloads re-run reload.sh on each config push.
exec sleep infinity
