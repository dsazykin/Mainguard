using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 4 (first boot): provision the VM-wide sysctls the sandbox depends on and wait for Docker.
/// <para>
/// <b>G2 control (2) — boot-provisioned here.</b> <c>kernel.yama.ptrace_scope=2</c> is a
/// non-namespaced kernel sysctl: Docker permits only namespaced sysctls, so it CANNOT be set per
/// container from <c>CreateContainerAsync</c>. It must be set VM-wide at first boot (alongside
/// <c>fs.inotify.max_user_watches</c>) — this step both applies it live and persists it to
/// <c>/etc/sysctl.d/</c>, and its <b>check</b> phase asserts the current value is ≥ 2 so a regressed
/// VM re-provisions. P2-07's key-custody guarantee names this check as its dependency.
/// </para>
/// <para>
/// <b>Known machine-wide side effect (audit MG-33) — deliberate, documented, not a defect.</b> WSL2
/// runs ALL distros on ONE shared kernel, and neither of these sysctls is namespaced, so "VM-wide"
/// literally means <i>every WSL2 distro on the machine</i>, not just <c>MainguardEnv</c>: while the WSL
/// VM is up, the user's Ubuntu (etc.) also sees <c>ptrace_scope=2</c> and the raised inotify limit.
/// Both directions are HARDENING or a raised ceiling — ptrace_scope=2 restricts ptrace to admin-capable
/// processes, so nothing another distro relied on becomes less safe — but a debugger/profiler run in
/// another distro (gdb attaching to an already-running pid, perf, some sanitizers) can start needing
/// sudo. The blast radius is bounded and reversible: the value lives in the shared kernel's memory
/// only, so it resets the next time the whole WSL VM is shut down (the VM-wide <c>wsl</c> shutdown
/// verb — which Mainguard itself never emits, G-12), and the persisted drop-in
/// (<see cref="SysctlDropInPath"/>) is written INSIDE <c>MainguardEnv</c> only — it disappears with the
/// distro at uninstall and never re-applies afterwards. Scoping this per-distro is not possible
/// (non-namespaced sysctl), and weakening it would break the G2 key-custody chain, so the fix is to
/// state the side effect here and in the OOBE log line rather than to change the security posture.
/// </para>
/// <para>
/// <b>MG-17 — the user-namespace remap is provisioned here too, and asserted here too.</b> The product
/// claimed the jails were user-namespaced while <c>daemon.json</c> set no <c>userns-remap</c> at all.
/// This step now (a) pins <c>/etc/subuid</c>+<c>/etc/subgid</c> <i>before</i> writing the daemon.json
/// that enables the remap — dockerd refuses to start when the named user has no subordinate range, so
/// the reverse order takes the whole VM down rather than merely leaving the remap off; (b) drains
/// mainguard's containers and networks with the OLD daemon on the one boot that flips the storage root
/// (see <see cref="UsernsRemapPolicy.PreFlipDrainScript"/>); (c) provisions the shared
/// <c>mainguard-jail</c> group and the two bind-mount sources' ownership; and (d) asserts in its
/// <b>check</b> phase that the remap is genuinely in effect, exactly as it asserts <c>ptrace_scope</c>
/// ≥ 2 — a control that only exists in a config file is a control nobody has confirmed.
/// </para>
/// </summary>
public sealed class FirstBootStep : IBootstrapStep
{
    /// <summary>Watches raised so large agent worktrees don't exhaust inotify.</summary>
    public const string InotifyWatches = "fs.inotify.max_user_watches=524288";

    /// <summary>G2 control (2): yama ptrace scope hardened VM-wide.</summary>
    public const string PtraceScope = "kernel.yama.ptrace_scope=2";

    // The sysctl KEYS (dotted). We read/write them via /proc/sys directly (cat/tee) rather than the
    // `sysctl` binary — the payload now ships procps (agents/diagnostics expect ps/pgrep), but the
    // /proc/sys path keeps this step independent of that package set.
    private const string InotifyKey = "fs.inotify.max_user_watches";
    private const string PtraceKey = "kernel.yama.ptrace_scope";

    /// <summary>Where both sysctls are persisted so they survive a VM restart.</summary>
    public const string SysctlDropInPath = "/etc/sysctl.d/99-mainguard-sandbox.conf";

    private const int RequiredWatches = 524288;
    private const int RequiredPtraceScope = 2;

    private readonly IWslRunner _wsl;
    private readonly int _dockerPollAttempts;
    private readonly TimeSpan _dockerPollDelay;

    public FirstBootStep(IWslRunner wsl, int dockerPollAttempts = 90, TimeSpan? dockerPollDelay = null)
    {
        _wsl = wsl;
        _dockerPollAttempts = dockerPollAttempts;
        _dockerPollDelay = dockerPollDelay ?? TimeSpan.FromSeconds(1);
    }

    public string Name => "First boot (sysctls + Docker)";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct) =>
        await UnsatisfiedReasonAsync(ct).ConfigureAwait(false) is null;

    /// <summary>Why the first-boot invariants aren't met yet — null when all are — so a failure names the
    /// exact check and value instead of the bootstrapper's opaque "state check still failed".</summary>
    private async Task<string?> UnsatisfiedReasonAsync(CancellationToken ct)
    {
        // G2 control (2): ptrace_scope=2 is defense-in-depth and needs the Yama LSM. Stock WSL2 kernels
        // frequently ship WITHOUT Yama, so /proc/sys/kernel/yama/ptrace_scope doesn't exist — there we
        // can't enforce it VM-wide and must NOT block provisioning on it (the P2-07 container hardening —
        // seccomp, dropped caps, non-root, read-only rootfs, egress-deny — is the primary isolation and
        // is unaffected). When Yama IS present we still require the hardened value (a regression to <2 is
        // a real, fixable failure).
        var ptrace = await ReadSysctlIntAsync("kernel.yama.ptrace_scope", ct).ConfigureAwait(false);
        if (ptrace is not null && ptrace < RequiredPtraceScope)
            return $"kernel.yama.ptrace_scope is {ptrace} (need >= {RequiredPtraceScope})";

        var watches = await ReadSysctlIntAsync("fs.inotify.max_user_watches", ct).ConfigureAwait(false);
        if (watches is null || watches < RequiredWatches)
            return $"fs.inotify.max_user_watches is {(watches?.ToString() ?? "unavailable")} (need >= {RequiredWatches})";

        if (!await DockerIsGreenAsync(ct).ConfigureAwait(false))
            return "Docker is not responding to `docker info`";

        // MG-17: the product claims the jails are user-namespaced. Assert that the DAEMON is actually
        // remapping — in the same spirit as the ptrace_scope check above, and for the same reason: a
        // control that is only written to a config file is a control nobody has confirmed. Ordered after
        // the Docker readiness check so a dead dockerd reports "Docker is not responding", never "no
        // userns remap".
        var userns = await UsernsUnsatisfiedReasonAsync(ct).ConfigureAwait(false);
        if (userns is not null)
            return "MG-17: " + userns;

        return null;
    }

    /// <summary>
    /// Runs the MG-17 probe in-VM and hands its stdout to the pure
    /// <see cref="UsernsRemapPolicy.DescribeUnsatisfied"/>. The parsing — including telling "the probe
    /// did not run" apart from "the remap is off" — lives in that pure function so every branch is
    /// unit-assertable without a VM.
    /// </summary>
    private async Task<string?> UsernsUnsatisfiedReasonAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(
            WslCommands.InDistro("bash", "-c", UsernsRemapPolicy.ProbeScript), stdin: null, ct).ConfigureAwait(false);

        // Deliberately NOT gated on result.Succeeded: the script always exits 0 and always prints its
        // frames, so the frames themselves — not an exit code — are the evidence that it ran.
        return UsernsRemapPolicy.DescribeUnsatisfied(result.StdOut);
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        // Apply live by writing /proc/sys directly (no `sysctl` binary in the payload). ptrace_scope's
        // write is best-effort: a kernel without Yama has no such file and the tee simply no-ops.
        //
        // MG-33: BOTH writes land in the SHARED WSL2 kernel (one kernel for every distro; neither sysctl
        // is namespaced), so both are machine-wide for as long as the WSL VM is up and reset on the next
        // WSL VM shutdown — see the side-effect paragraph on the class. The log lines below say so
        // out loud, because the OOBE progress log is the only place the user sees this happen.
        log.Report("Raising fs.inotify.max_user_watches (shared WSL2 kernel — affects all distros while WSL runs)…");
        await WriteProcSysctlAsync(InotifyKey, RequiredWatches.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        log.Report("Hardening kernel.yama.ptrace_scope=2 (G2; shared WSL2 kernel — applies to all WSL2 distros until the WSL VM is fully shut down)…");
        await WriteProcSysctlAsync(PtraceKey, RequiredPtraceScope.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        // Persist BOTH to /etc/sysctl.d/ so they survive a VM restart — applied on boot by systemd's
        // systemd-sysctl.service (part of systemd, independent of the missing `sysctl` binary). The
        // drop-in lives INSIDE MainguardEnv, so the persistence is scoped to our distro even though the
        // effect of applying it is not: uninstalling (unregistering the distro) removes the file with it,
        // and nothing re-applies the sysctls to the shared kernel afterwards.
        log.Report($"Persisting sysctls to {SysctlDropInPath}…");
        var dropIn = $"{InotifyWatches}\n{PtraceScope}\n";
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", SysctlDropInPath), stdin: dropIn, ct).ConfigureAwait(false);

        // MG-17: the subordinate id ranges dockerd's userns-remap maps into. These MUST exist before
        // dockerd is (re)started with `"userns-remap": "mainguard"` in daemon.json — dockerd refuses to
        // start if the named user has no subordinate range, which would take the whole VM down rather
        // than merely leaving the remap off. Written whole (same convention as /etc/wsl.conf and
        // daemon.json) so the files are a deterministic function of UsernsRemapPolicy.
        log.Report($"Pinning the container userns range ({UsernsRemapPolicy.RemapUser}:{UsernsRemapPolicy.SubordinateBase})…");
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", UsernsRemapPolicy.SubuidPath),
            stdin: UsernsRemapPolicy.SubidFileContent, ct).ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", UsernsRemapPolicy.SubgidPath),
            stdin: UsernsRemapPolicy.SubidFileContent, ct).ConfigureAwait(false);

        // Make sure dockerd is actually up (repairs wsl.conf + clears a stale pidfile, then starts it).
        await EnsureDockerRunningAsync(log, ct).ConfigureAwait(false);

        // Wait for the Docker socket to come up — in ONE in-VM call, not a host-side poll.
        //
        // This used to spawn a fresh `wsl.exe -d MainguardEnv -- docker info` PER ATTEMPT (up to 90, once a
        // second) plus extra spawns for each nudge: ~126 WSL process launches in 90s, each doing a full
        // session setup into the distro. That hammering is what drove the WSL service into
        // `Wsl/Service/E_UNEXPECTED` ("catastrophic failure") — our bug, not WSL's. Pushing the whole
        // retry loop into a single `bash -c` inside the distro means ONE spawn for the entire wait.
        //
        // The loop keeps the recovery behaviour: first boot can race (dockerd's bolt volume-metadata DB
        // open times out under fresh-VM I/O contention and dockerd exits; systemd then backs off with
        // "start request repeated too quickly" and stops retrying), so every ~10s we clear a
        // failed/inactive unit's stale pidfile + lockout and start it again — never touching a unit that
        // is legitimately "activating". Cancellation still works: the runner kills the wsl process on ct.
        log.Report("Waiting for Docker to become ready…");
        var dockerReady = await WaitForDockerInVmAsync(ct).ConfigureAwait(false);
        if (dockerReady)
            log.Report("Docker is ready.");

        if (!dockerReady)
            throw new BootstrapException(Name,
                $"Docker did not become ready inside {WslCommands.DistroName}. {await DescribeDockerFailureAsync(ct).ConfigureAwait(false)}".Trim());

        // MG-17: with the jails now remapped, the identity that writes through every read-write bind
        // mount is host uid/gid 101000, not the daemon's own 1000. Provision the shared group and the
        // mount ownership so the jail can still read and write exactly what it legitimately needs — and
        // nothing else under /home/mainguard. See UsernsRemapPolicy for why this is a group grant rather
        // than an owner chown.
        await EnsureJailOwnershipAsync(log, ct).ConfigureAwait(false);

        // Docker is up — confirm the remaining invariants and, if one is unmet, name it precisely rather
        // than letting the bootstrapper's opaque post-check swallow the reason.
        var reason = await UnsatisfiedReasonAsync(ct).ConfigureAwait(false);
        if (reason is not null)
            throw new BootstrapException(Name, $"First boot completed but {reason}.");
    }

    /// <summary>
    /// Waits (inside the VM, in a single <c>wsl</c> invocation) for <c>docker info</c> to succeed,
    /// nudging a failed/inactive unit past systemd's rapid-restart lockout every ~10s. Returns true when
    /// Docker came up within <see cref="_dockerPollAttempts"/> seconds.
    /// <para>One spawn instead of ~126: the host-side poll it replaces was launching a wsl.exe per
    /// attempt, and that burst is what tipped the WSL service into E_UNEXPECTED.</para>
    /// </summary>
    private async Task<bool> WaitForDockerInVmAsync(CancellationToken ct)
    {
        // All values below are our own constants — no user input reaches this script.
        var script =
            $"for i in $(seq 1 {_dockerPollAttempts}); do " +
            "if docker info >/dev/null 2>&1; then exit 0; fi; " +
            "if [ $((i % 10)) -eq 0 ]; then " +
            "  s=$(systemctl is-active docker 2>/dev/null || true); " +
            "  if [ \"$s\" = \"failed\" ] || [ \"$s\" = \"inactive\" ]; then " +
            "    rm -f /var/run/docker.pid 2>/dev/null || true; " +
            "    systemctl reset-failed docker >/dev/null 2>&1 || true; " +
            "    systemctl start docker >/dev/null 2>&1 || true; " +
            "  fi; " +
            "fi; " +
            "sleep 1; " +
            "done; exit 1";

        var result = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("bash", "-c", script), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>Gathers the real reason dockerd didn't come up — its own journal failure line(s) plus
    /// `docker info`'s error — so the OOBE error card shows something actionable, not just a bare
    /// "did not become ready".</summary>
    private async Task<string> DescribeDockerFailureAsync(CancellationToken ct)
    {
        var journal = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("journalctl", "-u", "docker", "--no-pager", "-n", "25"),
            stdin: null, ct).ConfigureAwait(false);

        // Prefer dockerd's explicit failure line (e.g. the bolt volume-DB timeout); fall back to `docker
        // info`'s stderr, then a trimmed journal tail.
        var failure = journal.StdOut
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Contains("failed to start daemon", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("level=fatal", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(failure))
            return failure;

        var info = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        var detail = new[] { info.StdErr, info.StdOut }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        return string.IsNullOrEmpty(detail) ? "Check `journalctl -u docker` inside the VM for details." : detail;
    }

    /// <summary>
    /// Brings dockerd up reliably and idempotently. The tarball enables <c>docker.service</c> under
    /// systemd, so dockerd starts on boot on its own; a leftover <c>[boot] command = service docker
    /// start</c> in <c>/etc/wsl.conf</c> would ALSO start it, double-starting dockerd → a stale
    /// <c>/var/run/docker.pid</c> → <c>"pid file found"</c> and systemd's <c>"start request repeated too
    /// quickly"</c>. So we rewrite wsl.conf deterministically with NO boot command (writing the WHOLE
    /// file is idempotent — the previous logic <b>appended</b> a <c>[boot] command</c> whenever it
    /// didn't spot the literal <c>"dockerd"</c>, which it never did, so every retry duplicated
    /// <c>boot.command</c> until WSL rejected the file), then clear any stale pidfile / failed-unit
    /// lockout and (re)start docker via systemd in the current session. The poll loop that follows is
    /// the source of truth for readiness, so transient start hiccups here are non-fatal.
    /// </summary>
    private async Task EnsureDockerRunningAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Repairing /etc/wsl.conf (systemd, no duplicate boot command)…");
        const string wslConf = "[boot]\nsystemd=true\n\n[user]\ndefault=mainguard\n";
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", "/etc/wsl.conf"), stdin: wslConf, ct).ConfigureAwait(false);

        // Pin dockerd to a dedicated bridge subnet + address pool. All WSL2 distros share ONE network
        // stack, so MainguardEnv's dockerd defaulting docker0 to 172.17.0.0/16 collides with Docker
        // Desktop's docker0 — which drops the user's Docker Desktop AND wedges this daemon (the loser of
        // the race restart-loops and a lingering instance then holds the volume-DB lock → the boltdb
        // "timeout"). A distinct 10.202/10.203 range never collides with Docker Desktop (172.x) or a
        // typical LAN. Written idempotently; the (re)start below picks it up.
        var alreadyConfigured = await DaemonJsonMatchesAsync(ct).ConfigureAwait(false);
        if (!alreadyConfigured)
        {
            // MG-17 migration, and the ONE moment it can be done: enabling userns-remap relocates
            // dockerd's whole storage root, so everything in the current root becomes invisible rather
            // than removed. Drain mainguard's containers and networks with the OLD daemon while it can
            // still see them — otherwise the jails and the shared egress proxy survive as unmanaged
            // containers and their bridges keep holding subnets out of the pool the new root allocates
            // from. Best effort: a failure here must never block provisioning. This is also where the
            // proxy is recreated exactly ONCE — after the flip its container simply does not exist in the
            // new root, so EnsureReadyAsync's ordinary create path runs, and no jail is left stranded
            // because every jail was removed here too.
            log.Report("Migrating Docker to a user-namespaced storage root (removing mainguard containers/networks first)…");
            await _wsl.RunAsync(
                WslCommands.InDistroAsRoot("bash", "-c", UsernsRemapPolicy.PreFlipDrainScript), stdin: null, ct)
                .ConfigureAwait(false);

            await _wsl.RunAsync(WslCommands.InDistroAsRoot("mkdir", "-p", "/etc/docker"), stdin: null, ct).ConfigureAwait(false);
            await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", "/etc/docker/daemon.json"), stdin: DockerDaemonJson, ct).ConfigureAwait(false);
        }

        // Skip the restart only when the config is already ours AND the daemon is healthy — otherwise a
        // daemon left running on the colliding default subnet would never be moved onto the safe one.
        if (alreadyConfigured && await DockerIsGreenAsync(ct).ConfigureAwait(false))
        {
            log.Report("Docker is already running.");
            return;
        }

        log.Report("Starting Docker…");
        // Stop first so systemd SIGTERMs the whole unit cgroup — this kills any lingering/half-dead
        // dockerd holding the volume-DB lock. Then clear the stale pidfile + rapid-restart lockout and
        // start clean, now with the dedicated-subnet daemon.json in effect.
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "stop", "docker"), stdin: null, ct).ConfigureAwait(false);
        await RunRootAsync(ct, "rm", "-f", "/var/run/docker.pid").ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "reset-failed", "docker"), stdin: null, ct).ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "start", "docker"), stdin: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The dockerd config baked into MainguardEnv: a dedicated bridge subnet + address pool so its
    /// dockerd never collides with a concurrently-running Docker Desktop in the shared WSL2 network
    /// stack, and (MG-17) the <c>userns-remap</c> that makes "user-namespaced jail" true rather than
    /// merely claimed. Kept in one constant so the daemon.json this step writes and the one the
    /// MainguardOS Dockerfile bakes cannot drift apart.
    /// </summary>
    public static readonly string DockerDaemonJson =
        "{\n  \"bip\": \"10.202.0.1/24\",\n"
        + "  \"default-address-pools\": [ { \"base\": \"10.203.0.0/16\", \"size\": 24 } ],\n"
        + $"  \"userns-remap\": \"{UsernsRemapPolicy.RemapUser}\"\n}}\n";

    /// <summary>
    /// True when the VM's daemon.json is already the one this step writes. Both facts are required:
    /// the subnet pin AND the MG-17 remap. An existing install carries a daemon.json with the subnet
    /// only, so requiring the remap key here is precisely what makes the upgrade path fire — the file
    /// is rewritten and dockerd restarted onto the remapped storage root.
    /// </summary>
    private async Task<bool> DaemonJsonMatchesAsync(CancellationToken ct)
    {
        var current = await _wsl.RunAsync(WslCommands.InDistro("cat", "/etc/docker/daemon.json"), stdin: null, ct).ConfigureAwait(false);
        if (!current.Succeeded)
            return false;

        return current.StdOut.Contains("10.202.0.1/24", StringComparison.Ordinal)
            && current.StdOut.Contains($"\"userns-remap\": \"{UsernsRemapPolicy.RemapUser}\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// MG-17 — provisions the shared identity between the unprivileged daemon and the remapped jails,
    /// then brings the two read-write bind-mount sources to the shared-ownership invariant. Both scripts
    /// are idempotent and both live in <see cref="UsernsRemapPolicy"/> so their content is unit-testable.
    ///
    /// <para>The <c>mainguardd</c> restart is conditional on the group having actually changed:
    /// supplementary groups are captured at process start, so a daemon that was already running when
    /// <c>mainguard-jail</c> appeared holds an identity that cannot touch the shared trees — and nothing
    /// would say so, it would simply fail to fetch or to remove a worktree. Restarting unconditionally
    /// would instead bounce a healthy daemon on every provisioning re-run.</para>
    /// </summary>
    private async Task EnsureJailOwnershipAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report($"Sharing the agent worktrees with the remapped jail identity (gid {UsernsRemapPolicy.AgentHostGid})…");

        var group = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("bash", "-c", UsernsRemapPolicy.GroupProvisionScript), stdin: null, ct)
            .ConfigureAwait(false);

        await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("bash", "-c", UsernsRemapPolicy.MountOwnershipScript()), stdin: null, ct)
            .ConfigureAwait(false);

        if (group.StdOut.Contains(UsernsRemapPolicy.GroupChangedSentinel, StringComparison.Ordinal))
        {
            log.Report("Restarting mainguardd so it picks up the shared jail group…");
            // try-restart, not restart: a daemon that is not running must not be started here — that is
            // StartDaemonStep's job, and starting it early would hide a genuine start failure there.
            await _wsl.RunAsync(
                WslCommands.InDistroAsRoot("systemctl", "try-restart", "mainguardd"), stdin: null, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The <c>/proc/sys</c> path for a dotted sysctl key (e.g. <c>kernel.yama.ptrace_scope</c>
    /// → <c>/proc/sys/kernel/yama/ptrace_scope</c>).</summary>
    private static string ProcPath(string sysctlKey) => "/proc/sys/" + sysctlKey.Replace('.', '/');

    /// <summary>Writes a sysctl value straight to its <c>/proc/sys</c> node (as root, via tee). A key the
    /// kernel doesn't expose (e.g. Yama-less ptrace_scope) has no file, so the write simply no-ops.</summary>
    private Task<WslRunResult> WriteProcSysctlAsync(string key, string value, CancellationToken ct) =>
        _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", ProcPath(key)), stdin: value, ct);

    private async Task<int?> ReadSysctlIntAsync(string key, CancellationToken ct)
    {
        // cat the /proc/sys node (world-readable) rather than `sysctl -n` — no procps in the payload.
        var result = await _wsl.RunAsync(WslCommands.InDistro("cat", ProcPath(key)), stdin: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;
        return int.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private async Task<bool> DockerIsGreenAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    private Task<WslRunResult> RunRootAsync(CancellationToken ct, params string[] command) =>
        _wsl.RunAsync(WslCommands.InDistroAsRoot(command), stdin: null, ct);
}
