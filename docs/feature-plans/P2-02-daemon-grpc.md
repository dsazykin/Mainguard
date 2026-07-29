# P2-02 — `Mainguard.Server` Daemon + gRPC v1 Contract — Implementation Plan

**Task ID:** P2-02 · **Milestone:** M6 · **Priority:** P0
**Depends on:** nothing.
**Branch:** implement on `feature/P2-02-daemon-grpc` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — unit + in-proc daemon tests on both CI legs; no model, screenshot, or human step.
> Everything (auth, streaming, reconnect, log masking, loopback bind) is provable with the `DaemonFixture` in-proc tier and `WebApplicationFactory`; TI-P2-00's fixture acceptance tests are part of this PR's gate.
>
> **Source of truth:** §P2-02 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`; global
> invariants G-13 (secret transport), G-14 (transport-agnostic protos), G-18 (UI ↔ daemon only via
> gRPC). Contract, invariants, and edge-case matrix below are binding.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-02 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-02** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-02 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

There is no daemon. Every Phase-7 feature needs a process to live in: a headless daemon that owns
containers, PTYs, VM worktrees, the merge queue, the gateway, and (in the VM) its own SQLite. After
this task the UI becomes a gRPC client for agent features; existing local-repo Git features stay
in-process in `Mainguard.App.Shell`/`Mainguard.Agents` exactly as they are.

This task creates the skeleton: two new projects, the v1 proto surface (some RPCs as typed
`UNIMPLEMENTED` stubs bodied out by P2-06/P2-08), loopback-only auth, the logging mask, and the
client-side `DaemonClient` with reconnect.

### What you can rely on

| Fact | Where |
|---|---|
| Solution file is `Mainguard.slnx` (XML solution — add projects by editing it; `dotnet sln` supports `.slnx` on SDK 10) | repo root |
| SDK pinned `10.0.100`, `latestFeature` roll-forward | `global.json` |
| Typed exception hierarchy; no bare throws (G-1) | `Mainguard.Agents/Exceptions/` |
| No DI container in the App; the **daemon may use ASP.NET Core's builder DI internally** — that is host plumbing, not an App-side container | `Mainguard.App.Shell/App.axaml.cs` pattern note |
| Tests project references Core *and* App; headless Avalonia harness exists | `Mainguard.Tests/` |
| Docker wrappers for build/test reproduce the toolchain (`docker compose run --rm build|test`) | `docker-compose.yml` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Protos/Mainguard.Protos.csproj` (`Grpc.Tools` codegen, `<Protobuf>` items, netstandard-compatible target consumed by Server and App) |
| **Create** | `Mainguard.Protos/protos/mainguard/v1/agent.proto`, `terminal.proto`, `reposync.proto`, `gateway.proto`, `common.proto` |
| **Create** | `Mainguard.Server/Mainguard.Server.csproj` (ASP.NET Core gRPC host; `RuntimeIdentifiers` incl. `linux-x64`; runs on Windows for `--local-dev`) |
| **Create** | `Mainguard.Server/Program.cs` (Kestrel loopback bind, token bootstrap, `--local-dev` flag) |
| **Create** | `Mainguard.Server/Auth/BearerTokenInterceptor.cs`, `Mainguard.Server/Auth/SessionTokenFile.cs` |
| **Create** | `Mainguard.Server/Logging/SecretMaskingInterceptor.cs` + `SecretFieldMask.cs` (registry of `// SECRET` fields) |
| **Create** | `Mainguard.Server/Services/AgentGrpcService.cs`, `TerminalGrpcService.cs`, `RepoSyncGrpcService.cs`, `GatewayGrpcService.cs` (validation/dispatch only) |
| **Create** | `Mainguard.App.Shell/Services/DaemonClient.cs` |
| **Edit** | `Mainguard.slnx` — add `Mainguard.Server`, `Mainguard.Protos` |
| **Create** | **`Mainguard.Server.Tests` project (TI-P2-00 — lands with this task at the latest):** added to `Mainguard.slnx`, referencing `Microsoft.AspNetCore.Mvc.Testing` + `Grpc.Net.Client`; the daemon in-proc test tier lives here (pure daemon *logic* stays in `Mainguard.Agents`, tested from `Mainguard.Tests` — mirrors this task's no-logic-in-gRPC-classes rejection trigger) |
| **Create** | `Mainguard.Server.Tests/Fixtures/DaemonFixture.cs` (in-proc host + authenticated `GrpcChannel`; exposes the session token, a wrong-token channel factory, and a log-capture sink for G-13 field-mask assertions — **every** daemon in-proc test uses it; hand-rolled hosts are a bug) |
| **Create** | `Mainguard.Tests/TestTools/ScriptedAgent/` (**`ScriptedAgentHarness`** — tiny cross-platform console binary speaking the P2-09 control protocol: `[IPC_UPDATE_REQUESTED]` → `[IPC_UPDATE_READY]`, scripted timelines (write/commit/emit/exit/hang/crash), configurable to ignore yields; the single most important Phase-2 harness — P2-09/10/14/20/26/35/37 and Vibe tests all drive it) |
| **Create** | `Mainguard.Server.Tests/Fixtures/FakeModelEndpoint.cs` (local HTTP listener scripting model-API responses: 200 + rate-limit headers, 401, 429 + `Retry-After`, slow-stream — used by P2-01 fixtures and P2-08's no-raw-429 integration) |
| **Create** | `Mainguard.Server.Tests/Fixtures/DualRepoFixture.cs` (Windows-side `TempRepoFixture` + ext4-style bare mirror wired the P2-06 way; promotes v1 `CaptureRefState()` for byte-identical round-trip assertions) |
| **Create** | `Mainguard.Server.Tests/Fixtures/AuditProbe.cs` (wraps `IAuditLog`; `AssertSequence(...)`/`AssertExactlyOne(...)` — every G-17 touchpoint test asserts through it, never log-text grep). `SandboxFixture` follows in P2-07 when `SandboxEngine` exists |
| **Create** | `Mainguard.Tests/DaemonAuthTests.cs`, `DaemonStreamTests.cs`, `SecretMaskTests.cs`, `DaemonClientReconnectTests.cs` (thin client-side pieces; server-side twins live in `Mainguard.Server.Tests`) |
| **Edit** | CI workflow (`.github/workflows/ci.yml`) — daemon `--local-dev` smoke job on Windows runner |
| **Edit** | `docs/repo-map/` index (all new files + the two new projects) |

---

## 2. Contract (binding)

Proto package **`mainguard.v1`**. Services and methods (names exact):

- **`AgentService`**: `SpawnAgent`, `StopAgent`, `ListAgents`, `StreamAgentEvents` (server-stream).
- **`TerminalService`**: `Attach(agentId)` bidirectional stream; the output frame is
  `oneof { bytes raw; GridUpdate grid; }` **from day one** — P2-18's engine swap must not be a
  proto break.
- **`RepoSyncService`**: `ProvisionRepo`, `CreateWorktree`, `ListWorktrees`, `RemoveWorktree` —
  RPCs + typed `UNIMPLEMENTED` stubs land here; bodies land in P2-06.
- **`GatewayService`**: `GetBudgets`, `SetBudgets`, `StreamSpend` — bodies in P2-08.

Client side: `Mainguard.App.Shell/Services/DaemonClient.cs` — channel creation, token metadata,
reconnect-with-backoff, an `IObservable`-style connection state (`Connected/Degraded/Down`) the
Activity Bar consumes in P2-13.

### Proto sketch (shape, not verbatim-binding beyond names above)

```proto
syntax = "proto3";
package mainguard.v1;

message SpawnAgentRequest {
  string repo_handle = 1;       // opaque handle, never a daemon filesystem path (G-14)
  string task_prompt = 2;
  string agent_kind = 3;        // adapter id, e.g. "claude-code"
}
message AgentEvent {
  string agent_id = 1;
  oneof event { StateChange state = 2; LogLine log = 3; }
}
message TerminalOutput {
  oneof frame { bytes raw = 1; GridUpdate grid = 2; }   // grid unused until P2-18
}
```

Every request/response that could ever carry a credential marks the field `// SECRET` and registers
it in `SecretFieldMask` (G-13).

---

## 3. Implementation steps (ordered; build after each)

### 3.1 Projects + solution

1. `Mainguard.Protos`: `Grpc.Tools` + `Google.Protobuf` + `Grpc.Net.Client` references;
   `<Protobuf Include="protos/**/*.proto" GrpcServices="Both" />`. Consumed by both Server and App
   (App gets client stubs, Server gets service bases — `GrpcServices="Both"` on one shared project
   is acceptable; splitting client/server codegen is an acceptable variation).
2. `Mainguard.Server`: `Microsoft.AspNetCore.Grpc` host. Add both projects to `Mainguard.slnx`;
   **`dotnet build` green from the first commit** — stubs return `UNIMPLEMENTED`, nothing depends
   on runtime behavior yet.

### 3.2 Auth (step 2)

- On startup the daemon generates a random **256-bit** token
  (`RandomNumberGenerator.GetBytes(32)`, hex/base64url), writes it to a **user-only-readable file**
  (`SessionTokenFile`): Linux `~/.mainguard/daemon.token` mode `0600`; Windows (`--local-dev`)
  `%LocalAppData%\Mainguard\daemon.token` with an ACL restricted to the current user. Prints nothing.
- `BearerTokenInterceptor` (server interceptor): every call must carry
  `authorization: bearer <token>` metadata; constant-time comparison
  (`CryptographicOperations.FixedTimeEquals`); anything else → `PERMISSION_DENIED`. **No allowlist
  of "public" methods** (invariant 1).
- Kestrel binds `127.0.0.1` only (never `0.0.0.0`); port from config with a typed startup failure
  naming the port when already bound (edge row 3).

### 3.3 `--local-dev` flag (step 3)

Daemon runs directly on Windows/localhost (no WSL) for the dev loop and CI: same binary, same auth,
same loopback bind. Guard any Linux-only assumption (forkpty, cgroups) behind capability checks so
`--local-dev` starts clean — those subsystems arrive in later tasks anyway.

### 3.4 Logging interceptor + secret mask (step 4)

`SecretMaskingInterceptor` logs method, peer, status, duration — request/response bodies are logged
only through a formatter that consults `SecretFieldMask`: a static registry mapping
`(message type, field number)` → masked. Fields commented `// SECRET` in the proto **must** be
registered; the test in §7 enforces the mechanism. Never log a masked field's value, length, or
prefix.

### 3.5 `DaemonClient` (step 5)

- `GrpcChannel.ForAddress("http://127.0.0.1:<port>")` + a `CallCredentials`/interceptor attaching
  the bearer token read from the token file.
- Reconnect with exponential backoff + jitter (cap ~30 s); surfaced as a
  `Connected / Degraded / Down` enum on an observable property (plain `INotifyPropertyChanged` or
  an event — no Rx dependency; P2-13 binds to it).
- `StreamAgentEvents` consumer: on stream fault → mark `Degraded`, re-subscribe after backoff, and
  **resume** (server re-sends current agent list snapshot on new subscriptions so the client
  converges — design the RPC as snapshot-then-deltas to make resumption trivial).
- Every call site passes a deadline or cancellation token (rejection trigger otherwise).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| missing/wrong token | `PERMISSION_DENIED`; client does not degrade into a retry storm (backoff caps, auth failures are terminal until token re-read) |
| daemon restart mid-stream | client reconnects with backoff; `StreamAgentEvents` resumes (snapshot-then-deltas) |
| port already bound | typed startup failure naming the port |
| token file deleted while running | existing channels keep working; a newly launched client regenerates trust only on daemon restart |

---

## 5. Invariants (MUST)

1. Every RPC authenticated by the interceptor — no public-method allowlist.
2. Daemon binds loopback only — an integration test asserts the listening endpoint address.
3. Proto files carry no OS paths in client-facing messages except opaque handles (G-14).
4. The daemon builds and runs on both linux-x64 and Windows (`--local-dev`); CI exercises the latter.
5. UI never references `Docker.DotNet`/PTY libraries — daemon access only via gRPC (G-18; enforced
   from this task onward).

---

## 6. Test contract

In-proc daemon via `WebApplicationFactory<Program>` (Grpc.Net test pattern: channel over the test
server handler).

| # | Test | Assertion |
|---|---|---|
| 1 | `AuthedCall_Succeeds` | `ListAgents` with the correct bearer token → OK (empty list) |
| 2 | `WrongToken_PermissionDenied` | wrong/missing/malformed token → `StatusCode.PermissionDenied` for unary **and** streaming calls |
| 3 | `TerminalAttach_BidiEcho` | test service echoes input frames back as `raw` output frames byte-identically |
| 4 | `Reconnect_ResumesEventStream` | kill/restart the in-proc host → `DaemonClient` transitions `Connected→Down→Connected` and receives the post-restart snapshot |
| 5 | `SecretField_NeverLogged` | a request with a `// SECRET`-registered field captured through the logging interceptor → captured log output does not contain the value |
| 6 | `AuthCoverage_EveryMethodByReflection` (TI-P2-02.1) | `[Theory]` over **every** service/method pair reflected from the proto descriptor set → wrong token denied; new RPCs are covered automatically; an allowlist appearing later fails this test |
| 7 | **TI-P2-00 acceptance:** `DaemonFixture` smoke (authed echo OK, wrong token denied); `ScriptedAgentHarness` self-test (yield round-trip over a bare pipe); `FakeModelEndpoint` replay determinism | the shared-fixture contracts; suite headless + green on both CI legs |
| 6 | `LoopbackOnly_Bind` | server addresses all parse to loopback |
| 7 | `UnimplementedStubs_Typed` | `RepoSync.ProvisionRepo` → `UNIMPLEMENTED` with a stable message |
| 8 | `TokenFile_Permissions` | Linux: mode `0600` (skip-attribute on Windows); Windows: ACL current-user-only (skip on Linux) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** business logic inside gRPC service classes beyond validation/dispatch (logic goes in
`Mainguard.Agents`/daemon services so it is unit-testable); client code referencing server-only
assemblies; any RPC call site without a deadline/cancellation path; a non-loopback bind; logging a
`// SECRET` field.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Daemon|FullyQualifiedName~SecretMask"
grep -rn "0.0.0.0\|AnyIP" Mainguard.Server/            # 0 hits
grep -rn "Docker.DotNet\|Porta.Pty" Mainguard.App.Shell/     # 0 hits (G-18)
grep -rn "SECRET" Mainguard.Protos/protos/ | wc -l     # every hit registered in SecretFieldMask (spot-check)
dotnet run --project Mainguard.Server -- --local-dev & # starts, prints nothing, /health via grpc probe OK
```

---

## 8. Definition of done

- [ ] **TI-P2-00 shared infrastructure shipped**: `Mainguard.Server.Tests` in `Mainguard.slnx`, `DaemonFixture`/`ScriptedAgentHarness`/`FakeModelEndpoint`/`DualRepoFixture`/`AuditProbe` + their acceptance tests; the Linux CI PR leg builds `Mainguard.Server` + runs `Mainguard.Server.Tests` (A.5 topology).

- [ ] `Mainguard.Protos` + `Mainguard.Server` in `Mainguard.slnx`; build green from first commit.
- [ ] v1 proto surface exactly as §2 (incl. `oneof raw|grid` terminal frames); `// SECRET` convention + mask registry live.
- [ ] Token-file auth, loopback bind, `--local-dev`, typed port-bound failure.
- [ ] `DaemonClient` with backoff reconnect + connection-state enum.
- [ ] All edge-matrix rows tested; CI runs the Windows `--local-dev` smoke.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-02**, base `phase2`.
