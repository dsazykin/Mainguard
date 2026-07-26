# Code signing & runtime pinning

Implements step 3 of [`docs/design/code-signing-plan.md`](../../docs/design/code-signing-plan.md).
**Mainguard ships unsigned by default** — every build in this repository has no pins and therefore ships
`UnsignedBuildSignatureVerifier`, which answers `NotAvailable` for everything and says so. This directory
is how you turn that off.

## The two halves

Signing and pinning are one mechanism split across build and runtime, and either half alone is worthless:

| Half | Where | What it establishes |
|---|---|---|
| **Sign** | `signtool` via `SignMainguardExecutables` (Mainguard.Pro.App.csproj) and `vpk --signParams` | these bytes were produced by the holder of our private key |
| **Pin** | `$(MainguardPinnedThumbprints)` → assembly metadata → `SigningPolicy` → `PinnedThumbprintSignatureVerifier` | the app refuses bytes that were **not** |

Signing without pinning ships a build that *looks* signed and verifies nothing. `pack.ps1` throws rather
than let that happen, and derives the pin from the same certificate it signs with so the two cannot drift.

## Doing it

```powershell
# once — creates the key, prints the thumbprint
pwsh build/signing/new-signing-cert.ps1 -OutputPath $env:USERPROFILE\.mainguard-signing\mainguard.pfx

# every release — signs AND pins, from one certificate
pwsh build/velopack/pack.ps1 -Channel pro -Version 0.2.5 `
    -SigningCertPath $env:USERPROFILE\.mainguard-signing\mainguard.pfx `
    -SigningCertPassword $env:MAINGUARD_CERT_PW
```

The `.pfx` never enters the repository. `new-signing-cert.ps1` refuses to write inside the working tree.

## What gets signed, and why that exact list

`SignMainguardExecutables` covers three files:

- `Mainguard.Pro.App.exe` — the resume target, which the OOBE registers as a Scheduled Task.
- `Mainguard.Installer.Elevated.exe` — the helper launched across the UAC boundary.
- `elevated-stage\Mainguard.Installer.Elevated.exe` — **the copy that gets promoted into
  `%ProgramFiles%\Mainguard\elevated`** (MG-15, step 1). Signing only the co-located copy would leave the
  relocated binary unsigned, and on a signed build the launcher would then refuse to run it.

## What the pin does *not* cover, on purpose

Authenticode signs Windows PE files. It cannot sign the MainguardOS daemon payload (Linux ELF) or the
agent-CLI tarballs (third-party npm packages we do not own). Those artifacts answer `NotAvailable` with a
reason naming what *does* cover them — GitHub artifact attestations and npm provenance, step 2 of the plan
— rather than silently reading as approval. `SigningPolicy.Covers` is the explicit list; a new
`SignedArtifactKind` is uncovered until someone decides what covers it.

## Rolling the certificate

Pin both thumbprints for one release, ship it, then drop the old one:

```powershell
pwsh build/velopack/pack.ps1 -Channel pro -Version 0.2.6 `
    -SigningCertPath ...\new.pfx -SigningCertPassword $env:MAINGUARD_CERT_PW `
    -PinnedThumbprints "OLDTHUMBPRINT;NEWTHUMBPRINT"
```

A single-pin cutover strands every install that has not updated yet: their app would refuse the new
helper, having never been told to trust it.

## Moving to a real certificate (step 4)

Buy Azure Trusted Signing, point `-SigningCertPath` (or the `signtool` invocation) at it, and pin its
thumbprint. **No application code changes** — that is the entire reason the paid certificate is ordered
last in the plan. What it buys is SmartScreen reputation, which is a distribution property; the tamper
detection above already works with the self-signed key.

## Verified where

- **Unit-tested (runs on Linux CI):** thumbprint parsing/normalisation, the pin match, coverage per
  artifact kind, and the full verdict table — including that an unsigned covered artifact on a
  signing-enabled build is `Rejected`, never `NotAvailable`. See `Mainguard.Tests/PinnedSignatureTests.cs`.
- **Manual Windows matrix only:** `WindowsAuthenticodeInspector`'s `WinVerifyTrust` P/Invoke, and the
  end-to-end case — sign a build, flip one byte of the signed exe, confirm the elevated launch is refused
  with a bad-digest message. Nothing below the `IAuthenticodeInspector` seam is unit-tested, because a
  mocked Win32 layer would only test the mock.
