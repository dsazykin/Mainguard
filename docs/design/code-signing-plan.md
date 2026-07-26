# Code signing & artifact trust — plan of record

**Status:** decided 2026-07-26. Steps 1–3 are free and are being implemented now; **step 4 is a deliberate deferral with a cost attached — revisit before public launch.**

---

## Where we actually are

Mainguard ships **entirely unsigned**. What exists is two dormant opt-in hooks:

- `Mainguard.Pro.App/Mainguard.Pro.App.csproj` — the `SignMainguardExecutables` target, gated on `$(MainguardSigningCertPath)`; a no-op when unset. Covers only `Mainguard.Pro.App.exe` and `Mainguard.Installer.Elevated.exe`.
- `build/velopack/pack.ps1` — `$SigningCertPath` / `$SigningCertPassword` params forwarded to `vpk --signParams`.

**Neither is wired into any CI job, no cert secret is referenced anywhere in `.github/workflows/`, and no certificate appears to exist.** Nothing this repo produces is signed — not the client head, not the daemon payload, not `MainguardOS.tar.gz`, not the adapter tarballs. `build/velopack/README.md` lists signed packaging as an unverified "owner Windows matrix" item.

## What signing would and would not buy

A certificate's headline value is **SmartScreen reputation** — users not seeing *"Windows protected your PC."* That is a **distribution/UX** property.

It is *not* what closes the security findings. Those are:

| Finding | Real problem | Closed by |
|---|---|---|
| **MG-15** | elevated binaries launch from a **user-writable** dir; ONLOGON `/RL HIGHEST` fires with **no UAC prompt** | removing the write primitive (step 1) |
| **MG-14** | daemon payload promoted to root with **no integrity check** | pinned-signature verification (step 3) |
| **MG-9** | update hash is computed from **the same bytes just fetched** (TOFU) — verifies nothing | build provenance (step 2) |

A cert helps with MG-14/MG-9 but is **not required**, and it does nothing for MG-15 that step 1 doesn't do better.

## The plan

### 1. Relocate elevated binaries out of `%LocalAppData%` — free
Per-user Velopack install means `AppContext.BaseDirectory` — which resolves both the elevated helper and the daemon payload — is writable by the same unprivileged user the elevated task runs as. Moving the elevated pieces to a protected location **removes the primitive entirely**; there is nothing to detect because the tampering cannot happen.

This also gives the step-3 verifier a home the attacker cannot rewrite, which is what makes verification meaningful. Signing alone has a bootstrapping hole: a verifier living in the writable dir can simply be replaced too.

> Scope decision: relocating **only the elevated pieces** closes the escalation while leaving per-user self-update UX intact. Full Program Files migration is defence-in-depth with a real UX cost (every update needs elevation or a privileged updater service).

### 2. Build provenance — free
**GitHub Artifact Attestations** for our own artifacts (daemon payload, `MainguardOS.tar.gz`), verified with `gh attestation verify`. This is what actually fixes MG-9: the attestation originates from the build, not from the same fetch as the artifact. **npm provenance attestations** for the third-party agent CLIs, replacing the self-derived hash.

### 3. Self-signed certificate + **pinned thumbprint** verification — free
A public trust anchor is not needed to detect a *replaced binary*. Sign at build with our own key; the verifier checks against a pinned thumbprint. An attacker who overwrites the binary cannot produce a matching signature without the private key.

Gives nothing for SmartScreen — that is step 4's job. **Build it so swapping in a real cert is configuration, not a rewrite.**

### 4. ⏳ Azure Trusted Signing — **deferred, ~$10/month**
When approaching public distribution, buy **Azure Trusted Signing** (~$120/yr): Microsoft manages the certificate and HSM, and it carries SmartScreen reputation. Roughly 5× cheaper than an EV certificate (~$400–700/yr), whose cost is largely the mandatory hardware key storage.

- Requires a validated legal entity; individual-developer support also exists.
- If Mainguard is open source, check **SignPath Foundation** — free signing for OSS projects.
- EV remains the alternative if immediate reputation matters more than cost.

**By the time this is purchased the verification plumbing already exists**, so it becomes "configure a cert," not "build a feature." That is the entire point of ordering it last.

---

## Residual limits — do not overstate the result

- **Install-dir ACLs do not stop same-user malware**, which is the actual threat model here. Only relocation to a protected path does.
- **Path validation stops an arbitrary exe, not a replaced one** at the legitimate path. Signature pinning covers that case; path checks do not.
- **Signing does not stop a downgrade** to an older, still-validly-signed build — the monotonic version guards are required under every scheme.
