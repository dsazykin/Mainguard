# P2-01 — BYOK Key Store + Key Health Check — Implementation Plan

**Task ID:** P2-01 · **Milestone:** M6 · **Priority:** P0 (agents need keys before anything else works)
**Depends on:** nothing.
**Branch:** implement on `feature/P2-01-byok-key-store` off `phase2`; PR targets `phase2` (`gh pr create --base phase2`).

> **Verification profile:** Automated (unit + ViewModel fixtures), plus a themed screenshot pass on the settings page.
> The health check, injector, and keystore are 100% offline-testable through the `HttpMessageHandler` seam and recorded fixtures — no live key needed in CI. A one-off live-provider smoke (`RequiresNetwork`, real Anthropic/OpenAI key) is recommended before ship to catch header drift. The settings page needs render-harness PNGs in all five themes + a quick human visual pass; no other human step.
>
> **Source of truth:** §P2-01 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`. The Contract,
> Invariants, and Edge-case matrix below are binding; deviations from the Implementation steps must
> still satisfy every invariant.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-01 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-01** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-01 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

Phase-7 agents consume LLM API keys. Keys must live in the OS keyring (never plaintext config), be
validated at entry so the user learns their realistic concurrency ceiling *before* the first 429,
and be injectable into sandboxes via tmpfs only (the injection consumer lands in P2-07 — this task
ships the content builder). The Anthropic subscription-OAuth ban (enforced 2026-04-04) makes the
API-key path the primary documented one, with a recorded ToS acknowledgment for CLI-OAuth users.

### What you can rely on

| Fact | Where |
|---|---|
| `SecureKeyring` — DataProtection-based store, DPAPI-wrapped key ring on Windows; methods `Save` / `Retrieve` / `DeleteSecret`; key naming `token_<host>` / `sshpass_<keypath>` | `Mainguard.Agents/Security/SecureKeyring.cs` |
| Token-scrub helper `Redact` (single sanctioned copy of secret-scrub logic) | `Mainguard.Agents/Hosting/GitHubApiClient.cs` |
| `AppDbContext` + EF migrations; DB migrates on app startup | `Mainguard.Agents/AppDbContext.cs`, `Mainguard.Agents/Migrations/`, `Mainguard.App.Shell/App.axaml.cs` |
| Settings-page ViewModel patterns (masked secrets, async save, `IsBusy`) | `Mainguard.App.Shell/ViewModels/AccountsViewModel.cs`, `SshKeysViewModel.cs` |
| No DI container: services constructed directly; `App` exposes a static `Settings` | `Mainguard.App.Shell/App.axaml.cs` |
| Typed exception hierarchy rooted at `MainguardException` | `Mainguard.Agents/Exceptions/` |
| xUnit harness incl. headless Avalonia (TI-00) for ViewModel tests | `Mainguard.Tests/` |

**Key names for LLM keys:** `llm_anthropic`, `llm_openai`, `llm_<provider>` — filesystem-safe,
mirrors the existing `token_<host>` convention.

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Security/ISecureKeyStore.cs` |
| **Edit** | `Mainguard.Agents/Security/SecureKeyring.cs` (implement `ISecureKeyStore`) |
| **Create** | `Mainguard.Agents/Security/ApiKeyHealthService.cs` (`KeyHealth` + service) |
| **Create** | `Mainguard.Agents/Security/CredentialInjector.cs` |
| **Create** | `Mainguard.Agents/Http/RedactionExtensions.cs` (move `Redact` out of `GitHubApiClient` — shared internal helper; `GitHubApiClient` delegates) |
| **Create** | `Mainguard.Agents/Models/TosAcknowledgment.cs` (entity) + `AppDbContext` DbSet + migration `AddTosAcknowledgment` |
| **Create** | `Mainguard.App.Shell/ViewModels/ApiKeySettingsViewModel.cs` |
| **Create** | `Mainguard.App.Shell/Views/ApiKeySettingsView.axaml(.cs)` (paired via `ViewLocator`) |
| **Create** | `Mainguard.App.Shell/Views/CliOAuthTosDialog.axaml(.cs)` + `Mainguard.App.Shell/ViewModels/CliOAuthTosDialogViewModel.cs` |
| **Edit** | settings navigation (wherever `AccountsViewModel`/`SshKeysViewModel` are registered) to add the "AI Providers" page |
| **Create** | `Mainguard.Tests/ApiKeyHealthServiceTests.cs`, `Mainguard.Tests/CredentialInjectorTests.cs`, `Mainguard.Tests/SecureKeyStoreTests.cs`, `Mainguard.Tests/ApiKeySettingsViewModelTests.cs` |
| **Edit** | `docs/repo-map/` index — every file above indexed in the same PR |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Security/ISecureKeyStore.cs
namespace Mainguard.Agents.Security;

public interface ISecureKeyStore
{
    void Set(string key, string secret);
    string? Get(string key);
    void Delete(string key);
}
// SecureKeyring implements ISecureKeyStore (Set/Get/Delete delegate to Save/Retrieve/DeleteSecret).
```

```csharp
// Mainguard.Agents/Security/ApiKeyHealthService.cs
public sealed class KeyHealth
{
    public bool IsValid { get; init; }
    public string? FailureReason { get; init; }          // token-scrubbed
    public int? RequestsPerMinute { get; init; }         // from provider rate-limit headers
    public int? TokensPerMinute { get; init; }
    public int EstimatedConcurrentAgents { get; init; }  // conservative mapping table in code
}
public sealed class ApiKeyHealthService
{
    public ApiKeyHealthService(HttpMessageHandler? handler = null);   // seam for offline tests
    public Task<KeyHealth> CheckAsync(string provider, string apiKey, CancellationToken ct);
}
```

```csharp
// Mainguard.Agents/Security/CredentialInjector.cs  (contract now; daemon side consumes it in P2-07)
public static class CredentialInjector
{
    /// <summary>Env-file content for an agent (KEY=value lines), built in memory only.</summary>
    public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> secrets);
}
```

Signatures, namespaces, and parameter names/order are binding.

---

## 3. Core implementation

### 3.1 `ISecureKeyStore` extraction (step 1 — no behavior change)

`SecureKeyring : ISecureKeyring, ISecureKeyStore`. The new members are thin delegates:

```csharp
void ISecureKeyStore.Set(string key, string secret) => Save(key, secret);
string? ISecureKeyStore.Get(string key) => Retrieve(key);
void ISecureKeyStore.Delete(string key) => DeleteSecret(key);
```

The interface is what the daemon (P2-02+) and the P2-24 compliance backends implement later — keep
it in `Mainguard.Agents.Security`, no UI or EF types in the signature. Build after this step; the whole
suite must stay green (pure refactor).

### 3.2 `RedactionExtensions` move (step 1b)

Move the existing `Redact` implementation from `GitHubApiClient` to
`Mainguard.Agents/Http/RedactionExtensions.cs` (`internal static class`), leaving a delegating call in
`GitHubApiClient` so its call sites don't churn. **Do not write a second scrubber** — a second copy
of token-scrub logic is a rejection trigger. Extend it only if it cannot already scrub an arbitrary
provided secret from a string (`Redact(string text, string secret)` overload).

### 3.3 `ApiKeyHealthService.CheckAsync` (step 2)

- Construct one `HttpClient` over the injected `HttpMessageHandler` (default `SocketsHttpHandler`);
  the seam makes every test offline (recorded fixtures, never live calls).
- **Anthropic** (`provider == "anthropic"`): `POST https://api.anthropic.com/v1/messages` with
  `max_tokens: 1`, a minimal one-token user message, key in the **`x-api-key` header**, plus the
  required `anthropic-version` header. Parse `anthropic-ratelimit-requests-limit` and
  `anthropic-ratelimit-tokens-limit` response headers.
- **OpenAI** (`provider == "openai"`): `GET https://api.openai.com/v1/models`,
  `Authorization: Bearer <key>`. Parse `x-ratelimit-limit-requests` / `x-ratelimit-limit-tokens`.
- Unknown provider → typed `MainguardException` ("unknown LLM provider '<x>'") — not a silent
  `IsValid=false`.
- **401/403** → `IsValid = false`, `FailureReason` = the provider's error message passed through
  `RedactionExtensions` with the key as the scrub target. The key must be unrecoverable from the
  result object.
- **Ceiling table:** a `static readonly` table in code mapping requests/min → estimated concurrent
  agents. Be conservative and document each row with a comment, e.g.:

```csharp
// requests/min ceiling -> conservative concurrent-agent estimate.
// An interactive CLI agent averages ~10 req/min sustained (bursts higher); halve for safety.
private static readonly (int MinRpm, int Agents)[] CeilingTable =
{
    (0, 1),      // headers missing or tiny tier: conservative floor
    (50, 2),
    (100, 4),
    (400, 8),
    (1000, 12),
};
```

- Missing headers on a 2xx → `IsValid=true`, `RequestsPerMinute`/`TokensPerMinute` null,
  `EstimatedConcurrentAgents = 1` (the floor row).
- Network failure / timeout → **throw** a typed exception (the caller renders a retry affordance);
  do not return a `KeyHealth` for transport failures — "unreachable" is not "invalid".
- Honor the `CancellationToken` on the HTTP call (rejection trigger: no cancellation path).

### 3.4 `CredentialInjector.BuildEnvFileContent` (step 5)

Pure string building, in memory only — no file I/O in this class (the daemon writes it to tmpfs in
P2-07):

```csharp
public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> secrets)
{
    var sb = new StringBuilder();
    foreach (var (name, value) in secrets)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException($"Secret '{name}' contains a newline; env-file integrity forbids it.");
        sb.Append(name).Append('=').Append(value).Append('\n');
    }
    return sb.ToString();
}
```

No quoting games — values are opaque tokens; the newline rejection is the only validation.
Newline-terminated output (every line, including the last).

### 3.5 ToS acknowledgment persistence (step 4)

New entity + table:

```csharp
public sealed class TosAcknowledgment
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public DateTimeOffset AcknowledgedAt { get; set; }
}
```

`dotnet tool restore && dotnet ef migrations add AddTosAcknowledgment --project Mainguard.Agents`;
commit migration + snapshot together. Expose a query helper (`HasAcknowledgment(provider)`) so
P2-15 can chain it later. The acknowledgment must survive restarts (invariant 4).

---

## 4. UI

### 4.1 `ApiKeySettingsViewModel` + settings page

Follow the `AccountsViewModel` pattern (CommunityToolkit `[ObservableProperty]` /
`[RelayCommand]`, constructed directly — no DI):

- Provider dropdown (`anthropic`, `openai` to start; the list is a static array so `llm_<provider>`
  extends without UI rework).
- Masked entry: `TextBox` with `PasswordChar="●"`. The VM holds the candidate key in a plain
  `string` property only while the page is open; **null it out after a successful save** and after
  the health check completes.
- **Save = validate-then-store:** `SaveCommand` runs `CheckAsync` off the UI thread
  (`Task.Run` + `IsBusy`, cancellation on page close). `IsValid=false` → inline error from
  `FailureReason`, **nothing stored**. Valid → `ISecureKeyStore.Set($"llm_{provider}", key)`, then
  render "Key valid — supports ~N concurrent agents".
- Per-provider Delete → `ISecureKeyStore.Delete`, row state resets to "no key stored".
- Health result line re-renders on re-save (re-check is mandatory on overwrite — edge row 5).

### 4.2 CLI-OAuth ToS notice dialog

Shown when the user selects "use my Claude subscription (CLI OAuth)" as the auth path. Modal dialog
(`CliOAuthTosDialog`) whose text states the April-2026 Anthropic restriction and that API-key is
the supported path. The "I understand" action writes a `TosAcknowledgment(provider,'now')` row via
`AppDbContext` **before** the option activates; cancel leaves the option off. If an acknowledgment
already exists, the dialog is skipped.

Design system: component classes + `{DynamicResource}` tokens only; no raw colors; test against a
light theme (Daylight Loom) — never assume dark.

---

## 5. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| invalid key | inline error, key absent from keyring and from any log/exception |
| valid key, headers missing | `IsValid=true`, ceilings null, agents estimate = 1 (conservative floor) |
| provider unreachable | typed failure, retry affordance, nothing stored |
| key value containing newline | typed `ArgumentException` from the injector (env-file integrity) |
| re-save over an existing key | old value overwritten atomically, health re-checked |
| delete | keyring entry gone (verify backing file removed) |

---

## 6. Invariants (MUST)

1. The key appears only in: the keyring, the in-memory HTTP header of the health check, and (later)
   tmpfs env content. Never argv, settings JSON, logs, or exception text.
2. Health check is fully offline-testable through the `HttpMessageHandler` seam (recorded fixtures).
3. An invalid key is never persisted.
4. ToS acknowledgment persists across restarts and is queryable (P2-15 will chain it).

---

## 7. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `CheckAsync_Anthropic_ValidKey_ParsesRateLimitHeaders` | fixture 200 + headers → `IsValid`, RPM/TPM populated, agents per table |
| 2 | `CheckAsync_OpenAi_ValidKey_ParsesRateLimitHeaders` | same for OpenAI header names |
| 3 | `CheckAsync_401_IsInvalid_AndScrubbed` | per provider: `IsValid=false`, `FailureReason` non-null and does **not** contain the key |
| 4 | `CheckAsync_MissingHeaders_ConservativeFloor` | 200 w/o headers → valid, nulls, `EstimatedConcurrentAgents == 1` |
| 5 | `CheckAsync_Unreachable_ThrowsTyped` | handler throws `HttpRequestException` → typed Mainguard exception, not a result |
| 6 | `CeilingTable_IsMonotonic` | table rows ascend in both columns (guards future edits) |
| 7 | `BuildEnvFileContent_Purity_And_Format` | `KEY=value\n` lines, newline-terminated, dictionary order preserved |
| 8 | `BuildEnvFileContent_NewlineValue_Throws` | `\n` and `\r` values → `ArgumentException` |
| 9 | `SecureKeyStore_RoundTrip_ThroughInterface` | `Set`/`Get`/`Delete` via `ISecureKeyStore` round-trips and removes the backing file |
| 10 | `ApiKeySettingsViewModel_InvalidKey_NotStored` | VM save with a 401 fixture → keyring dir empty, inline error set |
| 11 | `TosAcknowledgment_PersistsAcrossContexts` | write in one `AppDbContext`, read in a fresh one |

Health-check fixtures are recorded HTTP responses checked in under `Mainguard.Tests/Fixtures/`
(valid + 401 + missing-headers, per provider).

---

## 8. Rejection triggers / Reviewer script

**Rejection:** a second copy of token-scrub logic (reuse/move the existing one); health check
called on the UI thread or without cancellation; any `llm_*` value readable from
`UserPreferences`/`config.json`; storing before validating.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~ApiKeyHealth|FullyQualifiedName~CredentialInjector|FullyQualifiedName~SecureKeyStore"
grep -rn "llm_" Mainguard.App.Shell/ | grep -i "preferences\|settings.json"   # 0 hits
grep -rn "Redact" Mainguard.Agents/ | grep -v "RedactionExtensions\|GitHubApiClient"  # no third copy
```

---

## 9. Definition of done

- [ ] `ISecureKeyStore` extracted; `SecureKeyring` implements it; suite green after the pure refactor step.
- [ ] `ApiKeyHealthService` with offline fixtures, ceiling table, scrubbed failures, cancellation.
- [ ] `CredentialInjector` pure + newline rejection.
- [ ] Settings page (masked entry, validate-on-save, per-provider delete, health line) + ToS dialog + migration.
- [ ] All edge-matrix rows and invariants tested; reviewer script clean.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-01**, base `phase2`.
