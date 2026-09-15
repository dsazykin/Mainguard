# T-14 — Multi-Host Auth UI + SSH Key Manager — Implementation Plan

**Task ID:** T-14 · **Milestone:** M4 (audit 2.8; fix 1.7 is the foundation) · **Priority:** P1
**Depends on:** 1.7 landed (no code deps). **Branch:** `plan/T-14-multihost-auth-ssh` → `feat/T-14-multihost-auth-ssh` off `main`.

> **Source of truth:** §T-14 of the Master Doc (contract summary), §TI-14 of the Test Strategy.
> **Security is the point of this task** — the invariant "no secret in argv/URL/logs" (G-4) is paramount.

---

## 0. Context

Fix 1.7 landed the multi-host **plumbing**: `GitHostDetector.Detect(url)` → `(Host, HostKind)`,
`UsernameForToken(kind)`, host-keyed keyring keys `token_<host>` (via `TokenKeyForHost`), and
`RunGitCheckedAuthenticated` (token in child-process env only). `GitHubAuthClient` does device flow;
`SecureKeyring` is DataProtection file-backed. This task adds the **provider abstraction, more hosts, an SSH
key manager, and the Accounts UI** on top of that foundation.

### What you can rely on

| Fact | Where |
|---|---|
| `GitHostDetector.Detect / UsernameForToken / TokenKeyForHost / ToHttpsUrl` | `Core/Security/GitHostDetector.cs` |
| `HostKind { GitHub, GitLab, Bitbucket, AzureDevOps, Unknown }` | `Core/Models/HostKind.cs` |
| `SecureKeyring` (DataProtection, file-backed) — round-trip save/retrieve/delete | `Core/Security/SecureKeyring.cs` |
| `GitHubAuthClient` device-flow OAuth | `Core/Sync/GitHubAuthClient.cs` |
| `RunGitCheckedAuthenticated` feeds the token via the git credential mechanism / child env — **never argv/URL** | `GitServices.cs:769` |
| `GetCredentialsProvider` returns `UsernamePasswordCredentials`; extend for SSH | `GitServices.cs:821,840` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Sync/IHostProvider.cs` + `GitHubProvider`, `GitLabProvider`, `BitbucketProvider`, `AzureDevOpsProvider`, `GenericHostProvider` |
| **Create** | `Mainguard.Agents/Sync/HostProviderRegistry.cs` (`Resolve(host, HostKind)`) |
| **Refactor** | `GitHubAuthClient` → `GitHubProvider` (device flow behind `IHostProvider`) |
| **Create** | `Mainguard.Agents/Security/SshKeyService.cs` |
| **Edit** | `GitServices.GetCredentialsProvider` — SSH-form remotes → `SshUserKeyCredentials` |
| **Edit** | `SecureKeyring` — add a **path override** constructor arg for testability (TI-14 #4) |
| **Create** | Preferences → "Accounts" page + "SSH keys" page (ViewModels + Views) |
| **Create** | `Mainguard.Tests/HostProviderRegistryTests.cs`, `SshKeyServiceTests.cs`; extend `SecureKeyringTests.cs` |

---

## 2. Contract

```csharp
// Mainguard.Agents/Sync/IHostProvider.cs
public interface IHostProvider
{
    string Host { get; }
    bool SupportsDeviceFlow { get; }
    string TokenUsername { get; }                            // == GitHostDetector.UsernameForToken(kind) — single source
    Task<string> AcquireTokenAsync(CancellationToken ct);    // device flow OR PAT-dialog result
}

// Mainguard.Agents/Sync/HostProviderRegistry.cs
public static class HostProviderRegistry
{
    public static IHostProvider Resolve(string host, HostKind kind);   // github.com->GitHub; gitlab->GitLab; unknown->Generic
}
```

`SshKeyService`: generate (`ssh-keygen -t ed25519 -f <path> -N <passphrase>` via **ArgumentList**, never a
shell string), list `~/.ssh` keys, copy the public key. Passphrase stored as `sshpass_<sanitized-keypath>`
in `SecureKeyring`. Token keys remain `token_<host>` (compat with the landed keyring).

---

## 3. Implementation notes

1. **Single source of truth for the username.** `IHostProvider.TokenUsername` and
   `RunGitCheckedAuthenticated` must both consult `GitHostDetector.UsernameForToken` (via the registry). If a
   duplicate host→username switch appears anywhere, delete it (TI-14 #2 references the same member the auth
   path uses — a duplicate fails the test's intent).
2. **Providers.** `GitHubProvider`/`GitLabProvider` do device flow (`SupportsDeviceFlow = true`);
   `BitbucketProvider`/`AzureDevOpsProvider`/`GenericHostProvider` are **PAT-dialog v1** (`AcquireTokenAsync`
   resolves from a modal the user pastes a token into). Refactor `GitHubAuthClient`'s existing device flow
   into `GitHubProvider` — don't rewrite it.
3. **SSH.** `SshKeyService.Generate(path, passphrase)` builds `ProcessStartInfo` with `ArgumentList`
   (`ssh-keygen`, `-t`, `ed25519`, `-f`, path, `-N`, passphrase) — the passphrase is an **argv element to
   ssh-keygen, which is a local key-gen tool, not a network op**; still prefer stdin where ssh-keygen
   supports it, and **never** log it. Store the private-key path; register the public key via each host's
   API (later); pass `SshUserKeyCredentials` (public path, private path, passphrase) to LibGit2Sharp for
   SSH-form remotes.
4. **Keyring testability.** Add a constructor path override to `SecureKeyring` so tests point it at a temp
   dir; `Retrieve` must return `null` (not throw) on a corrupt payload (TI-14 #4).
5. **Auth failure routing.** Unknown host with no stored token → `AuthenticationRequiredException(host)` →
   routed to the PAT dialog (extends the existing `HandleGitActionException` branch).

---

## 4. Invariants / Rejection triggers

**MUST (extends 1.7):** **no secret ever in argv/URL/logs/exception text** — including SSH passphrases
(prefer stdin; if a tool forces argv for a *local* op, document it and keep it off any network path); token
keys remain `token_<host>`; unknown-host-no-token → `AuthenticationRequiredException(host)` → PAT dialog.

**Rejection:** any token/passphrase appearing in a git URL or a network-command argv; a duplicate
host→username mapping diverging from `GitHostDetector`; `ssh-keygen` invoked via a shell string.

---

## 5. Test contract — TI-14

`HostProviderRegistryTests.cs`, `SshKeyServiceTests.cs`, extend `SecureKeyringTests.cs`:
1. `Resolve_ShouldPickProviderByHostAndKind` — github.com→GitHub; self-hosted GitLab (with hint)→GitLab; unknown→Generic.
2. `TokenUsername_ShouldMatchHostConvention` — single source; references the same member the auth path uses.
3. `SshKeygen_ArgConstruction_ShouldUseArgumentList_NeverShellString` — assert the built `ProcessStartInfo.ArgumentList`.
4. `SecureKeyring_SaveRetrieveDelete_RoundTrip` + `Retrieve_ShouldReturnNull_OnCorruptPayload` (write garbage to the `.keyring`; use the new path override).
5. **Manual matrix (documented in the PR, not automated):** device flow GitHub + GitLab; PAT Bitbucket/AzDO;
   SSH with passphrase; **process-listing check that no secret appears in argv** during each.

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~HostProvider|FullyQualifiedName~SshKey|FullyQualifiedName~SecureKeyring"
grep -rn "x-access-token:" Mainguard.Agents/                       # only inside GitHostDetector.UsernameForToken
grep -rn "ProcessStartInfo" Mainguard.Agents/Security/SshKeyService.cs  # uses ArgumentList, no shell string
```

- [ ] `IHostProvider` + five providers + `HostProviderRegistry`; `GitHubAuthClient` refactored into `GitHubProvider`.
- [ ] `SshKeyService` (ArgumentList keygen, keyring passphrase); `GetCredentialsProvider` SSH path.
- [ ] `SecureKeyring` path override + null-on-corrupt.
- [ ] Accounts + SSH keys preferences pages; unknown-host → PAT dialog.
- [ ] TI-14 automated green + manual matrix documented in the PR (incl. argv no-secret proof). One PR linking **T-14**.
```
