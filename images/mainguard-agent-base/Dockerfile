# Mainguard hardened agent base image (P2-07).
#
# Built in CI / the release pipeline ONLY — NEVER at runtime (G-16: a runtime `docker build`
# severs the agent PTY). The toolchain-sideload mechanism (`devbox add <tool>`) needs Nix, so Nix +
# the real Devbox binary + a PINNED nixpkgs are baked at BUILD time (root, with network) so nothing
# installs at runtime — a runtime Nix install needs root+sudo, which the hardened, non-root,
# no-new-privileges jail forbids by design.
#
# The container is spawned with the hardened spec from ContainerSpecBuilder:
#   - no-new-privileges, seccomp denylist (process_vm_readv/process_vm_writev/ptrace), CapDrop ALL
#   - userns remap, memory/pids limits, read-only rootfs (writable surfaces are tmpfs)
#   - the ONLY host bind mount is the ext4 agent worktree at /workspace (G-11); /nix is an EPHEMERAL
#     per-container writable volume pre-populated from this image's baked store (a tmpfs would hide it)
# and reaches the network only through the mainguard-egress-proxy (default-deny: model APIs + package
# registries incl. cache.nixos.org; A6 keeps the git host off the allowlist).

# MG-27: the base image is pinned by DIGEST, not by the floating `bookworm-slim` tag. A tag is a
# mutable pointer — the same Dockerfile built a week apart silently produced different bytes, and the
# `mainguard.image.version` source hash (which hashes THIS FILE) could not see the difference. The
# digest names the exact manifest. Refreshing it is a deliberate edit: `docker pull debian:bookworm-slim`
# then `docker image inspect --format '{{index .RepoDigests 0}}' debian:bookworm-slim`, and because the
# digest lives in this file, updating it moves the image version constant too (the guard test says so).
FROM debian:bookworm-slim@sha256:7b140f374b289a7c2befc338f42ebe6441b7ea838a042bbd5acbfca6ec875818

# Base tooling only. git + curl for package/dependency fetches through the proxy; xz for nix.
# (Debian's archive keeps only the current point release of each package, so pinning apt versions here
# would break the build within weeks rather than harden it; the base-image digest is what pins the
# starting package set, and every artefact fetched from OUTSIDE the archive below is checksummed.)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl git xz-utils \
    && rm -rf /var/lib/apt/lists/*

# Two DISTINCT non-root users (G2 control 1): the agent CLI runs as `agent` (uid 1000); the OOB
# supervisor runs as `supervisor` (uid 1001) and solely owns /run/secrets/oob.key (mode 0400) so the
# agent uid can never read the OOB session key K.
RUN groupadd -g 1000 agent      && useradd -u 1000 -g 1000 -m -s /bin/bash agent \
    && groupadd -g 1001 supervisor && useradd -u 1001 -g 1001 -m -s /bin/bash supervisor

# --- Nix (build-time, single-user, no daemon) ---------------------------------------------------
# The Determinate installer with `--init none` installs Nix WITHOUT systemd (correct for a container)
# into /nix, owned root. Flakes on. At runtime /nix is remounted as a writable volume seeded from here.
#
# MG-27: this was `curl https://install.determinate.systems/nix | sh`. That URL serves a bootstrap
# script whose CONTENT changes with every upstream release, and piping it straight into a shell means
# the image executes whatever bytes arrive, unverified, with root and network — the single largest
# supply-chain surface in this file. The bootstrap script's only job is to detect the platform and
# download the versioned `nix-installer` binary, so it is skipped entirely: the pinned binary is
# fetched from the version-addressed URL the script itself would have used, its SHA-256 is checked
# before it is made executable, and only then is it run with exactly the same arguments.
# Refresh: bump NIX_INSTALLER_VERSION, download BOTH arch artefacts, and paste their `sha256sum`s.
#
# TARGETARCH (BuildKit-provided: amd64 in CI/the WSL2 VM, arm64 on the macos-host substrate)
# selects the artefact; each arch carries its OWN pinned SHA-256, verified against the publisher's
# released checksums. An arch this file has no pin for fails the build loudly — never an
# unverified fetch.
ARG TARGETARCH
ARG NIX_INSTALLER_VERSION=v3.21.8
ARG NIX_INSTALLER_SHA256_AMD64=eecf66f62b044f40b0632d8e9ea72ffc3bd3214357d09317117febff748e71b3
ARG NIX_INSTALLER_SHA256_ARM64=fa5b3a13dffd70ea8f9575c3d0fe3af4cb2f881dfcb03983634dbe6756d4e39e
RUN case "${TARGETARCH}" in \
      amd64) NIX_PLATFORM=x86_64-linux;  NIX_SHA256="${NIX_INSTALLER_SHA256_AMD64}" ;; \
      arm64) NIX_PLATFORM=aarch64-linux; NIX_SHA256="${NIX_INSTALLER_SHA256_ARM64}" ;; \
      *) echo "no pinned nix-installer for TARGETARCH '${TARGETARCH}'" >&2; exit 1 ;; \
    esac \
    && curl --proto '=https' --tlsv1.2 -sSf -L -o /tmp/nix-installer \
      "https://install.determinate.systems/nix/tag/${NIX_INSTALLER_VERSION}/nix-installer-${NIX_PLATFORM}" \
    && echo "${NIX_SHA256}  /tmp/nix-installer" | sha256sum -c - \
    && chmod 0755 /tmp/nix-installer \
    && /tmp/nix-installer install linux --no-confirm --init none \
        --extra-conf "experimental-features = nix-command flakes" \
        --extra-conf "build-users-group =" \
    && rm -f /tmp/nix-installer
ENV PATH=/nix/var/nix/profiles/default/bin:/usr/local/bin:/usr/bin:/bin

# --- Devbox: the REAL binary (not the self-downloading launcher) ---------------------------------
# get.jetify.com installs a LAUNCHER that self-downloads the real binary on first run — which the
# default-deny jail blocks — so this used to run the launcher here, execute it once to populate its
# cache, then fish the real binary out of that cache with `find`. Two unpinned, unverified downloads
# (the install script and whatever the launcher decided to fetch) to obtain a binary whose identity was
# never checked.
#
# MG-27: the same binary is published as a versioned GitHub release asset. Fetch THAT, verify its
# SHA-256, and unpack it — one pinned, checksummed artefact and no launcher. Verified byte-identical to
# what the old recipe produced: the extracted binary's sha256 matches the `/usr/local/bin/devbox` in the
# image built from the previous Dockerfile, so this is a pure provenance change, not a version bump.
# Refresh: bump DEVBOX_VERSION and paste both arch tarballs' `sha256sum`s (the release's
# checksums.txt carries them). Same TARGETARCH selection discipline as the nix-installer above.
ARG DEVBOX_VERSION=0.17.5
ARG DEVBOX_SHA256_AMD64=eb2d8fb34266ba3befc294d7d6f56e2cd4da2cacb7a0cf52db5b8092575544f8
ARG DEVBOX_SHA256_ARM64=880901fff1ce7bf48086c12d84535bc14c257b56cb0d05e93e037f2cb1b1d529
RUN case "${TARGETARCH}" in \
      amd64) DEVBOX_ARCH=amd64; DEVBOX_SHA256="${DEVBOX_SHA256_AMD64}" ;; \
      arm64) DEVBOX_ARCH=arm64; DEVBOX_SHA256="${DEVBOX_SHA256_ARM64}" ;; \
      *) echo "no pinned devbox for TARGETARCH '${TARGETARCH}'" >&2; exit 1 ;; \
    esac \
    && curl --proto '=https' --tlsv1.2 -fsSL -o /tmp/devbox.tar.gz \
      "https://github.com/jetify-com/devbox/releases/download/${DEVBOX_VERSION}/devbox_${DEVBOX_VERSION}_linux_${DEVBOX_ARCH}.tar.gz" \
    && echo "${DEVBOX_SHA256}  /tmp/devbox.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/devbox.tar.gz -C /usr/local/bin devbox \
    && chmod 0755 /usr/local/bin/devbox \
    && rm -f /tmp/devbox.tar.gz \
    && HOME=/root /usr/local/bin/devbox version

# --- Pre-baked A6-clean toolchain ---------------------------------------------------------------
# devbox's `add` resolves packages via nixhub → fetches nixpkgs from GITHUB at runtime, which the
# default-deny jail (A6) forbids — and every strict-A6 workaround fights devbox's design. So the
# curated toolchain is baked HERE, at build time (github reachable), into a PERSISTENT nix profile at
# /opt/toolchain (never the agent's tmpfs $HOME, which is wiped at runtime). At runtime the tools are
# already on PATH from the read-only image; nothing resolves or fetches from a git host (A6 intact),
# and a fixed/audited toolchain also shrinks the supply-chain surface the F5 caveat warns about.
# Adding an ARBITRARY new tool at runtime is a documented v1.x item (a daemon-mediated/self-hosted nix
# resolver that keeps the git host off the allowlist). NIXPKGS_REV pins the toolchain for reproducibility.
ARG NIXPKGS_REV=50ab793786d9de88ee30ec4e4c24fb4236fc2674
RUN nix profile install --profile /opt/toolchain \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#jq" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#ripgrep" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#fd" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#tree" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#gnumake" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#nodejs_22" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#python3" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#go" \
    && chown -R agent:agent /nix /opt/toolchain
# /opt/mainguard/adapters/bin is the READ-ONLY mount of the VM-side dynamically-installed agent CLIs
# (AdapterPaths.SandboxMount; ContainerSpecBuilder attaches it when any CLI is installed). It is on
# PATH permanently so a CLI installed AFTER this image was built is runnable BY NAME inside the jail
# with no image rebuild — that is what makes the CLIs dynamic. The daemon launches an agent by its
# ABSOLUTE marker argv, so this entry is for the agent's own shell (e.g. an agent invoking `claude`).
# A missing mount (no CLIs installed) simply makes it an empty PATH entry — never an error.
ENV PATH=/opt/mainguard/adapters/bin:/opt/toolchain/bin:/nix/var/nix/profiles/default/bin:/usr/local/bin:/usr/bin:/bin

# /workspace is the ext4 worktree bind target; /run/secrets is a tmpfs the daemon populates post-start.
WORKDIR /workspace
USER agent

# The seccomp profile applied to this container is passed by the daemon at create time
# (HostConfig.SecurityOpt: seccomp=<json>). ./seccomp.json IS that profile and the single source of
# truth: it is embedded into Mainguard.Core and returned verbatim by SeccompProfile.Json. It is the
# canonical moby default-deny profile (defaultAction SCMP_ACT_ERRNO) with ptrace/process_vm_readv/
# process_vm_writev removed from every allow rule and explicitly denied.
CMD ["sleep", "infinity"]
