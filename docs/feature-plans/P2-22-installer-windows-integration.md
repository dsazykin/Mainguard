# P2-22 — Installer Part 2: Windows Integration, Loopback OAuth, Adapter Channel, Teardown — Implementation Plan

**Task ID:** P2-22 · **Milestone:** M7 · **Priority:** P0
**Depends on:** P2-21.
**Branch:** implement on `feature/P2-22-installer-windows-integration` off `phase2`; PR targets
`phase2`.

> **Verification profile:** Automated PKCE/manifest/pin units + **required manual matrix: live OAuth flows + uninstall with a personal distro — human approval required**.
> RFC 7636 vectors, state rejection, manifest schema, and pin-survival simulation are CI. Live provider OAuth (real browser round-trip) and the uninstall-leaves-personal-distros-untouched check are human-executed and evidenced in the PR (G-12).
>
> **Source of truth:** §P2-22 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §§J-4–J-6). Security-relevant PR (global rule 4): OAuth + deep-link checks evidenced.
> The **pinned adapter channel** is a survival requirement (agent-agnostic, market v2 §5.4).

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-22 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-22** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-22 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-21 gets the VM installed. This task finishes distribution: Explorer integration, the one
OAuth listener every token flow must use, the `mainguard://` protocol handler, the adapter channel
that installs agent CLIs at pinned versions inside the VM, and a clean uninstall that leaves the
user's machine exactly as found (minus Mainguard).

### What you can rely on

| Fact | Where |
|---|---|
| Installer skeleton + elevated helper + OOBE state | P2-21 |
| `WslRunner` + distro lifecycle verbs (G-12) | P2-05 |
| Existing token storage (`token_<host>` in `SecureKeyring`) + `CredentialResolver` | `Mainguard.Agents/Security/` |
| Existing host OAuth/browser launch path (`SafeWebUrl`, `BrowserLauncher` — scheme-validated single launcher) | `Mainguard.Agents/Security/SafeWebUrl.cs`, `Mainguard.App.Shell/Services/BrowserLauncher.cs` |
| Sandbox exec for in-VM installs | P2-07 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Security/LoopbackOAuthListener.cs` (shared: ephemeral port, PKCE, `state`, single-use, 5-min timeout) |
| **Create** | `Mainguard.Agents/Security/Pkce.cs` (pure: verifier/challenge generation S256) |
| **Create** | `Mainguard.App.Shell/Services/DeepLinkHandler.cs` (`mainguard://` — non-secret deep links only) + protocol registration in installer |
| **Create** | `Mainguard.Agents/Agents/Adapters/AdapterManifest.cs` (+ JSON schema), `AdapterChannel.cs` (fetch/verify/install/health-probe at pinned versions) |
| **Create** | `installer/` additions: Explorer context-menu registration ("Open in Mainguard"), uninstaller (`Mainguard.Uninstall`) |
| **Edit** | existing host-auth flows → route token acquisition through `LoopbackOAuthListener` (one listener implementation, everywhere) |
| **Create** | `Mainguard.Tests/PkceTests.cs`, `LoopbackOAuthListenerTests.cs`, `DeepLinkHandlerTests.cs`, `AdapterManifestTests.cs`, `AdapterPinSimulationTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (binding)

- **Explorer context menus** written by install, removed by uninstall (per-user registry keys —
  no HKLM unless the install is machine-wide; default per-user).
- **RFC 8252 loopback + PKCE for every token flow** — shared `LoopbackOAuthListener`: ephemeral
  port on `127.0.0.1`, `state` validation, **single-use**, 5-minute timeout.
- **`mainguard://` handler for non-secret deep links only** (open repo/PR/agent views). **No token
  ever in a `mainguard://` URL** (grep + code-path test).
- **Pinned adapter channel:** `adapters.json` manifest (cli → version, install cmd, config shims,
  health probe) fetched from a Mainguard-owned channel, installed **inside the VM** at pinned
  versions, never `@latest`, updated independently of app releases (keeps perpetual-fallback
  licenses functional).
- **Clean uninstall:** `wsl --terminate MainguardEnv` → poll → `--unregister MainguardEnv`; registry/
  Scheduled-Task/appdata removal; user repos untouched; optional sync-remote removal (the SC-2-resolved `SyncRemote.Name` — `mainguard-vm` on WSL2; never assume the literal, P2-06)
  (asks); personal distros untouched (G-12).

---

## 3. Implementation steps

1. **`Pkce` (pure):** verifier = 32 random bytes base64url; challenge = S256(verifier). RFC 7636
   test vectors.
2. **`LoopbackOAuthListener`:** `HttpListener` (or Kestrel-min) on `http://127.0.0.1:{ephemeral}/callback`;
   flow: generate PKCE + `state` → build the authorize URL (through the existing scheme-validated
   launcher) → await exactly **one** callback ≤ 5 min → validate `state` (constant-time) →
   exchange code + verifier for the token (host-specific exchange delegated to the caller) →
   listener disposed (single-use — a second hit gets 410). Errors typed (timeout, state mismatch,
   user denial). Token lands in `SecureKeyring` via the existing path — never logged, never in a
   URL we construct beyond the provider's own redirect.
3. **Migrate existing flows:** every current token acquisition (GitHub OAuth/device flows in
   Accounts) routes through this listener where the host supports loopback redirect; a second
   listener implementation anywhere is a rejection trigger.
4. **`mainguard://`:** installer registers the protocol (per-user `HKCU\Software\Classes`).
   `DeepLinkHandler.Parse(uri)` → typed commands (`OpenRepo(path-id)`, `OpenPr(host,repo,n)`,
   `OpenAgent(id)`); **reject** any URI containing query/fragment keys matching secret patterns
   (`token`, `code`, `secret`, `key`) — parse test enforces; unknown verbs ignored gracefully.
   Single-instance forwarding: second app launch with a URI forwards to the running instance
   (named pipe) — matches how the OAuth listener avoids the protocol handler entirely.
5. **Adapter channel:** manifest schema
   `{ adapters: [{ id, displayName, version, sha256, installCmd[], configShims[], healthProbe }] }`,
   signature/hash verified after fetch (HTTPS + content hash pin); `AdapterChannel.EnsureAsync(id)`
   runs the install **inside the VM** (P2-07 exec, base image devbox), verifies the health probe
   (`claude --version` etc. matching the pinned version), applies config shims (e.g. non-
   interactive flags). Channel URL + cache under appdata; refresh independent of app updates.
   **Pin simulation test:** manifest pins vX while "upstream" (fixture registry) publishes a
   breaking vY → installed adapter remains vX and healthy.
6. **Uninstaller:** ordered, each step tolerant: stop daemon → `--terminate MainguardEnv` → poll
   state → `--unregister MainguardEnv` → remove protocol/context-menu registry keys → remove
   Scheduled Tasks → remove appdata (offer keep-settings) → optionally remove the SC-2-resolved sync
   remote from known repos (explicit checkbox; default off) → **never touches** user repos or
   other distros. Evidence matrix run on a machine with a personal distro.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| OAuth callback with wrong `state` | rejected, typed error, listener still single-use (flow aborted) |
| no callback within 5 min | typed timeout; port released |
| second callback hit | 410; first result unaffected |
| `mainguard://…?token=x` | rejected by the secret-pattern guard; nothing launched |
| adapter upstream ships breaking `@latest` | pinned version unaffected (simulation) |
| uninstall with personal distros present | only `MainguardEnv` unregistered; `wsl --list` diff proves it |

---

## 5. Invariants (MUST)

1. No token ever in a `mainguard://` URL (grep + code-path test).
2. Personal distros untouched by uninstall (G-12).
3. Pinned adapter unaffected by a breaking upstream release (simulated test).
4. One `LoopbackOAuthListener` implementation for every token flow.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Pkce_Rfc7636Vectors` | verifier/challenge match spec vectors; base64url no padding |
| 2 | `Listener_StateMismatchRejected` | forged callback → typed error |
| 3 | `Listener_TimeoutAndSingleUse` | 5-min timeout typed; second hit → 410 |
| 4 | `DeepLink_ParseMatrix` | valid verbs parse; secret-pattern URIs rejected; unknown verbs ignored |
| 5 | `Manifest_SchemaAndHash` | valid/invalid manifests; hash mismatch → typed refusal |
| 6 | `AdapterPin_Simulation` | fixture registry breaking release → pinned install intact + probe green |
| 7 | Uninstall matrix (manual, evidenced) | registry/tasks/appdata gone; repos + personal distros intact; screenshots + `wsl --list` before/after in PR |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second OAuth listener; tokens in URLs/deep links; `@latest` installs;
uninstall touching `/mnt/c` repos or other distros; HKLM writes in a per-user install.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Pkce|FullyQualifiedName~LoopbackOAuth|FullyQualifiedName~DeepLink|FullyQualifiedName~Adapter"
grep -rn "mainguard://" Mainguard.Agents/ Mainguard.App.Shell/ | grep -i "token\|secret\|code="   # 0 hits
grep -rn "@latest" Mainguard.Agents/Agents/Adapters/ installer/                          # 0 hits
grep -rn -- "--shutdown" installer/                                                  # 0 hits
```

---

## 8. Definition of done

- [ ] Shared PKCE loopback listener; existing flows migrated; single-use/timeout/state tests green.
- [ ] `mainguard://` non-secret handler + registration; secret-pattern guard.
- [ ] Adapter channel: schema, hash verification, in-VM pinned installs, config shims, health probes, pin simulation.
- [ ] Explorer menus + clean uninstall (evidence matrix with a personal distro).
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-22**, base `phase2`.
