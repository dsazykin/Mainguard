# Mainguard Architecture Decision Records (client / non-security)

**Status:** living document · **Scope:** the shipped single-user client and non-security phase-2 surfaces · **Subordinate to** `Mainguard_Master_Implementation_Document_v2.md` (where they disagree, the master doc wins). Daemon/sandbox/merge-queue/audit decisions are recorded elsewhere and are deliberately **not** in this file.

Format: one ADR per decision — Context → Decision → Consequences. An ADR is amended by a new ADR, never edited into silence. Entries below were recorded 2026-07-11 during the Lane H engineering-quality pass; each names the code it governs.

---

## ADR-001 — All libgit2 access goes through `IGitService.ExecuteWithRepo`, which owns the index.lock retry

**Context.** LibGit2Sharp wraps native libgit2 handles. Ad-hoc or long-lived `Repository` instances leak native memory and collide on `.git/index.lock` — the exact failure class Mainguard exists to prevent (Hotspot Register H7 is the daemon-side statement of the same rule). Historically two call sites inside `GitServices` itself still opened raw handles (`GetRemoteUrl`, the rebase-continue conflict check).

**Decision.** `GitServices.ExecuteWithRepo(...)` (both overloads; the `Action` overload delegates to the `Func` one) is the **single** place a `Repository` is constructed: it validates the path, opens the handle in a `using`, and owns the **bounded index.lock retry** — four attempts, 25/50/100 ms exponential backoff, on `LockedFileException` only. Retrying is safe because libgit2 raises `LockedFileException` when it fails to *acquire* the lockfile, i.e. before mutating anything; each retry re-opens the repository so no state leaks between attempts. Exhausted retries surface as a typed `GitOperationException` naming `index.lock` and the recovery path. The two stray raw-handle sites were routed through `ExecuteWithRepo`.

**Consequences.** Every git touch-point — including the T-19 journal's snapshot opens and the analytics walks — inherits the retry and the typed failure for free. A new raw `new Repository(...)` outside this method is a review-rejection trigger. The retry ceiling (~175 ms of waiting) is deliberately shorter than a human's perception of "hung"; a wedged lock fails fast with guidance instead of spinning. Pinned by `GitServiceIndexLockTests`.

---

## ADR-002 — Analytics history walk is cached per `(repoPath, HEAD SHA, cap)` in a bounded LRU

**Context.** Hotspot Register **H1**: `RepositoryAnalyzer.CollectCommitStatsAsync` walks up to 10k commits computing a tree-diff per commit — the most expensive single computation in the client. The analytics view constructs a fresh analyzer per open, so every tab visit re-paid the full walk.

**Decision.** A `CommitStatsCache` (bounded LRU, default 8 entries, `BlameCache`'s shape) shared statically across analyzer instances, keyed `(repoPath, headSha, maxCommits)`. Because the key pins the exact HEAD commit, cache entries are **immutable facts about history** — a new commit changes the key and misses naturally, so there is no invalidation protocol to get wrong; superseded entries age out of the LRU. The working-tree language walk is deliberately **not** cached: it is a function of the (dirty) worktree, not of HEAD, and a HEAD-keyed entry would serve stale sizes after any edit.

**Consequences.** Re-opening analytics on an unchanged HEAD is O(1) (measured on the test fixture: 380.9 ms cold → 9.2 ms cached; the gap widens with history size). Memory ceiling ≈ 8 × one capped walk (~1 MB each). "Never unbounded" (the T-11 rejection trigger) generalizes: any new cache must state its bound in its doc comment. Pinned by the cache tests in `RepositoryAnalyzerTests`.

---

## ADR-003 — Commit-graph router: O(1) SHA→lane mirror; left-most dominance *is* the crossing-minimization strategy

**Context.** Hotspot Register **H2**: `CommitGraphRouter.RouteCommits` scanned the active-lane list linearly (`IndexOf`/`Contains`/`FindIndex`) per commit — O(commits × L²) on a wide DAG, and an agent swarm's N branches are exactly the wide-DAG input (P2-13). 50k commits × 64 lanes measured ~1.9 s for the scans alone on the reference input.

**Decision.** Three moves, all output-identical:
1. A `Dictionary<string,int>` mirrors the lane list (SHA → index) and a `SortedSet<int>` holds free slots, kept in lock-step by `SetLane`/`ClearLane`/`TakeFreeLane` — every lookup O(1), left-most-free-slot O(log L).
2. `GraphLine` became a `readonly record struct`: a wide DAG emits ≈ lanes × commits line segments (3.2M on the bench input), and a reference type there is a heap allocation per segment. Per-node lists are pre-sized.
3. **No change to routing semantics.** The existing "left-most lane dominance" rule (converging lanes always collapse leftwards; new lines open in the left-most free slot) is retained *as* the crossing-minimization policy: it keeps the trunk straight, joins every branch leftwards exactly once, and never routes across an idle lane. A smarter global crossing minimizer (ordering-by-descendant heuristics) was considered and rejected: it would break the chunked-fringe contract (each 50-commit page must extend the previous one deterministically) and the visual stability users rely on while scrolling.

Equivalence is enforced, not assumed: `CommitGraphRouterWideDagTests` keeps a verbatim copy of the pre-optimization algorithm as a **semantic oracle** and asserts node-for-node identical output on seeded random DAGs, plus chunked-equals-whole for the fringe contract.

**Consequences.** The 50k × 64-lane route dropped from 1853 ms to 873 ms (same build/machine/input; ~3.5–7× vs the shipped code including the struct win) and the per-page incremental cost (~3.5 ms/200 rows) sits inside the H2 interactive budget. The enforcing micro-bench belongs in the future `Mainguard.Benchmarks` project ([PERF-2]); the xUnit tests print measurements but assert only structure — a timing assert inside the test suite remains a rejection trigger.

---

## ADR-004 — Diff viewer: syntax theme follows the app theme; intra-line emphasis has a noise-suppression policy

**Context.** Two quality gaps. (1) The TextMate installation was pinned to `DarkPlus` — Daylight Loom rendered dark-palette syntax colors on a light surface, violating the "never assume dark" rule; an unknown file extension also *kept* the previous file's grammar. (2) `IntraLineDiff.Compute` reports a whole-line changed span for a wholly-rewritten line pair; painting the entire line in the emphasis color on top of the add/remove tint re-states what the tint already says — and positional pairing (k-th delete ↔ k-th add) routinely pairs unrelated lines, so the noise was common.

**Decision.** (1) `DiffViewerView` resolves the TextMate theme from the actual Avalonia theme variant (`LightPlus`/`DarkPlus`), re-applies it on every `ThemeManager.ThemeChanged`, and clears the grammar when no language matches. (2) A new `IntraLineDiff.ComputeEmphasis` wraps `Compute` with the display policy: when the changed spans cover ≥ 95% of **both** sides, emphasis is suppressed (empty spans). The raw `Compute` keeps its pinned contract for callers that need geometry. The ViewModel's pairing uses `ComputeEmphasis`.

**Consequences.** Syntax highlighting reads correctly in all five themes and across live switches; wholly-replaced lines read as replaced (tint only), partially-edited lines keep word-level emphasis. The 95% threshold is a policy constant in one place (`EmphasisNoiseThreshold`) with the rationale attached. Pinned by `IntraLineDiffTests` (`ComputeEmphasis_*`).

---

## ADR-005 — Pure engines are property-tested with seeded `System.Random`, not an external property-testing framework

**Context.** The pure engines (`PatchParser`, `MergeDiffService`, `ChangelogGenerator`, `CommitGraphRouter`) promise *laws*, not just examples: byte-identical round-trips, conservation, nothing-ever-dropped. Example-based tests can't sweep the input space; FsCheck-style frameworks add a dependency, non-deterministic shrinking, and CI flake surface.

**Decision.** Property tests are plain xUnit theories over **seeded** `System.Random` generators (`PureEnginePropertyTests`, the random-DAG sweeps in `CommitGraphRouterWideDagTests`): a handful of fixed seeds × hundreds of cases each, fully deterministic on any machine. Each test states its law in prose; generator edge-cases that expose a *known model limit* are excluded **with a comment naming the pinned known-limit test** rather than silently narrowed (see ADR-006).

**Consequences.** ~1,000 generated cases per run at negligible cost, zero new dependencies, failures reproduce exactly (`seed`/`case` in the assertion message). The trade-off — no automatic shrinking — is accepted; failing cases are small by construction.

---

## ADR-006 — MergeChunk's joined-string slices: `""` on an Unchanged chunk is a blank line; on a resolved side it is a known, pinned limit

**Context.** `MergeChunk` stores each slice as one joined string, so a slice of exactly **one blank line** and an **empty** slice are both `""`. `AssembleMerged` skipped every empty `chosen` — which silently *ate the blank line* between two edited regions on every conflict resolve (found by the Part 4 property sweep; a direct "never lose work" violation).

**Decision.** For **Unchanged** chunks, `""` provably means one blank line (`FlushPending` never emits an empty pending run, and a hot region is non-empty on some side), so `AssembleMerged` now emits one empty line for it. For **changed/resolved sides**, `""` still means an empty slice; a side consisting of exactly one blank line remains unrepresentable and assembles as a deletion — a **KNOWN LIMIT**, pinned by `AssembleMerged_ResolvedSideOfExactlyOneBlankLine_KnownLimit_CollapsesToNothing` so any future fix is a deliberate model change (line counts on `MergeChunk`), not an accidental behavior shift.

**Consequences.** The common blank-line case is conserved (`AssembleMerged_BlankBaseLineBetweenTwoEditedRegions_IsPreserved`); the residual limit is documented at the decision site, in the test, and here. The property-test generator excludes empty-string lines and says why.

---

## ADR-007 — Performance numbers are *printed* by tests and *enforced* by a future benchmark project, never asserted in xUnit

**Context.** Hotspot Register OPEN DECISION [PERF-2]: timing asserts inside `dotnet test` flake on loaded runners and violate the "no timing-dependent replays" rule; but budgets that live only in prose regress silently.

**Decision.** Until `Mainguard.Benchmarks` (BenchmarkDotNet + checked-in baseline JSON, outside `Mainguard.slnx`) exists, perf-sensitive tests assert **structure** (counts, bounds, equivalence) and **print** measurements tagged `[H1]`/`[H2]` for the log. Where a before/after claim matters, the test carries the frozen old implementation and times both in the same run — same build, same machine, same input — so the comparison stays honest on every machine it runs on (`CommitGraphRouterWideDagTests.RouteCommits_PathologicalWideDag_*`).

**Consequences.** CI stays deterministic; regressions are visible in test logs immediately and become hard gates when [PERF-2] lands. The oracle-copy pattern doubles as the equivalence net (ADR-003).

---

## ADR-008 — The macos-host substrate: daemon native on the Mac, sandboxes through the machine's own Docker engine

**Context.** The ESC anticipated macOS as B4 "macos-vm" — mainguardd inside a Mainguard-owned Linux
VM, mirroring WSL2's topology — and deferred it. Porting phase2 to a real Apple Silicon machine
showed that topology buys nothing there: every macOS Docker engine (Docker Desktop, OrbStack,
Colima) already IS a managed Linux VM with host file sharing, and a second Mainguard-owned VM would
add lifecycle plumbing (import, keep-alive, upgrade) around a problem the engine already solves.
The owner's requirement was explicit: the substrate must work with whichever engine the machine
runs, not one vendor's.

**Decision.** `MacHostAgentEnvironment`, `SubstrateId = "macos-host"`: mainguardd runs natively on
the Mac (osx-arm64), daemon state lives under `~/mainguard`, and sandboxes run through the resolved
Docker endpoint (`DockerEndpointResolver`: `DOCKER_HOST` → the docker CLI's current context read
from disk → the well-known engine sockets → library default; on this dev machine the library
default was a DANGLING `/var/run/docker.sock` symlink while the live engine was OrbStack — the
resolver is not a nicety). The substrate-neutral collaborators are shared with WSL2 through
`AgentEnvironmentComposition`; `AgentEnvironmentFactory` makes the per-platform choice at the one
composition root. In-jail CLIs and toolchains install through `ContainerAdapterInstallHost` — a
disposable agent-base container with the daemon-owned roots mounted at their VM paths — never on
the macOS host. ESC-I1 is enforced STRUCTURALLY here: the substrate declares `AllowedMountRoots`
(`~/mainguard`, the data root) and `ContainerSpecBuilder` refuses any bind source outside them.

**Deviations from the ESC's B4 sketch, and their compensating controls.**
- *The daemon shares the host OS with the user.* The WSL2 design isolates mainguardd in the VM;
  here it is a user process. Compensated by the same pinned-mTLS/token control plane, and the jail
  boundary is unchanged — agents still run in hardened containers, not on the host.
- *`kernel.yama.ptrace_scope` is best-effort.* The engine's LinuxKit VM is not ours to provision;
  the boot sysctl is applied one-shot (privileged container) where the kernel carries Yama and
  tolerated absent, exactly as WSL2's FirstBootStep already tolerates it. Quartet members (1),(3),
  (4) remain fully structural per-container.
- *userns-remap is engine-dependent* (Docker Desktop lacks it). The spec keeps
  `UsernsMode = inherit` — identical posture to a WSL2 daemon with no remap configured — and
  virtiofs uid-squash means in-jail writes surface as the Mac user, never root.
- *The MG-4 gateway's `auto` bind picks a LAN-reachable address* so jails can reach it through the
  engine's NAT. On a laptop that is a broader listen surface than the WSL2 relay; the gateway's
  own per-agent token auth bounds it. Follow-up: bind the engine's bridge address specifically.
- *B4 "macos-vm" stays deferred*, unchanged: this ADR adds a sibling substrate, it does not
  redefine B4.

**Consequences.** The full agent pipeline runs on a Mac with any Docker engine and no elevation,
no VM import, no keep-alive; `wsl.exe`-shaped machinery stays Windows-only behind the existing
seams (`HostCommandRunner` interprets the in-distro command shapes on the host). The ESC §4 suite
subset (`SubstrateConformanceTests`) and the full RequiresDocker tier are green on-device; the §5
measurements live in `docs/phase-2/Mainguard_Substrate_MacHost.md`.
