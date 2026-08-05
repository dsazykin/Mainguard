# mainguard-agent-base

The static hardened base image every agent container runs (P2-07).

**Built in CI / the release pipeline — never at runtime** (G-16: a runtime `docker build` severs the
agent PTY). Toolchains are not baked in; agents sideload them at runtime with `devbox add <tool>`.

The daemon spawns this image with the hardened spec from `ContainerSpecBuilder`:

- `no-new-privileges` + the default-deny seccomp profile (the canonical moby default with
  `process_vm_readv` / `process_vm_writev` / `ptrace` removed from every allow rule and explicitly
  denied — G2 control 3) — never `seccomp=unconfined`
- `CapDrop: ALL`, no `CAP_SYS_PTRACE` (G2 control 4)
- userns remap, memory + pids limits, read-only rootfs (writable surfaces are tmpfs)
- the **only** bind mount is the ext4 agent worktree at `/workspace` (G-11)
- network egress only through `mainguard-egress-proxy` (default-deny)

Two distinct non-root users are baked in (G2 control 1): `agent` (uid 1000) runs the agent CLI;
`supervisor` (uid 1001) solely owns `/run/secrets/supervisor/oob.key` (mode 0400) so the agent uid can
never read the OOB session key `K`. Each uid gets its OWN `0700` tmpfs directory under a root-owned
`0711` `/run/secrets` (`/run/secrets/agent`, `/run/secrets/supervisor`), mounted by Docker already owned
by that uid — so each secret is created by its owner and never has to be `chown`ed to it. The jail's
`no-new-privileges` plus its non-root `User` leave even a uid-0 `docker exec` with no `CAP_CHOWN` on
Docker 20.10.24, so a directory the writer already owns is the only layout that works.

`seccomp.json` **is** the profile and the single source of truth: it is embedded into
`Mainguard.Agents` and returned verbatim by `SeccompProfile.Json`, which `ContainerSpecBuilder` passes to
`CreateContainerAsync` in `seccomp=<json>` at spawn. What the tests assert equals what the container
runs. It is the canonical moby default-deny profile (`defaultAction: SCMP_ACT_ERRNO`) with the three
memory-inspection syscalls removed from every allow rule and explicitly denied. A custom `seccomp=`
**replaces** Docker's default (it is not additive), which is why this file reproduces that default
rather than overlaying it.

> G2 control 2 (`kernel.yama.ptrace_scope=2`) is a non-namespaced VM-wide sysctl provisioned by the
> P2-05 bootstrapper — it is **not** set on the container create request.
