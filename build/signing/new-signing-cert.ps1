#Requires -Version 5.1
<#
.SYNOPSIS
    Creates Mainguard's self-signed code-signing certificate and prints everything a signed release
    needs: the .pfx path, the password, and the thumbprint the runtime pins.

.DESCRIPTION
    Step 3 of docs/design/code-signing-plan.md. Mainguard has no certificate at all; this makes one.

    WHY A SELF-SIGNED CERTIFICATE IS ENOUGH FOR WHAT THIS DOES
    ---------------------------------------------------------
    The job is to detect a REPLACED binary — specifically, the elevated helper and the resume target,
    which is finding MG-15. An attacker who overwrites one of those files cannot produce a signature
    that matches our private key, so the check holds whether or not any public authority vouches for
    that key. Mainguard's verifier pins the certificate's THUMBPRINT and does not ask the machine's
    trust store for an opinion (PinnedThumbprintSignatureVerifier); an untrusted chain is the expected
    state, not a failure.

    WHAT IT DOES NOT BUY
    --------------------
    SmartScreen reputation. A self-signed certificate does nothing for "Windows protected your PC" —
    that is step 4 (Azure Trusted Signing, ~$120/yr, deliberately deferred). Because the pin arrives as
    a build property, moving to that certificate is a configuration change: sign with the new key, pin
    the new thumbprint, done. Nothing in the app changes.

    KEEPING THE KEY
    ---------------
    The .pfx is the release identity. Anyone holding it can produce binaries this build accepts, so it
    belongs in the release box's credential store (or a CI secret), NEVER in the repository. This script
    refuses to write into the repo working tree for that reason.

    ROLLING THE KEY
    ---------------
    Pin both thumbprints for one release (-PinnedThumbprints 'OLD;NEW' on pack.ps1), ship it, then drop
    the old one. A single-pin cutover strands anyone who has not updated yet.

.EXAMPLE
    pwsh build/signing/new-signing-cert.ps1 -OutputPath $env:USERPROFILE\.mainguard-signing\mainguard.pfx

.EXAMPLE
    # Then, at release time:
    pwsh build/velopack/pack.ps1 -Channel pro -Version 0.2.5 `
        -SigningCertPath $env:USERPROFILE\.mainguard-signing\mainguard.pfx `
        -SigningCertPassword $env:MAINGUARD_CERT_PW
    # pack.ps1 derives the runtime pin from that same certificate; there is nothing else to set.
#>
param(
    # Where to write the .pfx. Must be outside the repository working tree.
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    # The .pfx password. Generated if omitted, and printed once.
    [string] $Password = "",

    [string] $Subject = "CN=Mainguard, O=Mainguard",

    [int] $ValidYears = 5
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core' -and $PSVersionTable.Platform -ne 'Win32NT') {
    throw "New-SelfSignedCertificate is a Windows facility. Run this on the release box."
}

# The key must never end up in the repo — an attacker who clones it can sign whatever they like and this
# build will accept it. Refuse rather than warn.
$repoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$outFull = [System.IO.Path]::GetFullPath($OutputPath)
if ($outFull.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write the signing key inside the repository ($outFull). Put it in a credential " +
          "store or a directory outside the working tree."
}

if (-not $Password) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $Password = [Convert]::ToBase64String($bytes)
    Write-Host "==> Generated .pfx password (store it now; it is not written anywhere):"
    Write-Host "    $Password"
}

# -Type CodeSigningCert gives the certificate the Code Signing EKU (1.3.6.1.5.5.7.3.3), which signtool
# requires and WinVerifyTrust checks.
$cert = New-SelfSignedCertificate `
    -Subject $Subject `
    -Type CodeSigningCert `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation Cert:\CurrentUser\My

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFull) | Out-Null
$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $outFull -Password $secure | Out-Null

Write-Host ""
Write-Host "==> Certificate created."
Write-Host "    Subject     : $($cert.Subject)"
Write-Host "    Thumbprint  : $($cert.Thumbprint)"
Write-Host "    Not after   : $($cert.NotAfter)"
Write-Host "    PFX         : $outFull"
Write-Host ""
Write-Host "The thumbprint above is what the runtime pins. pack.ps1 derives it from the .pfx"
Write-Host "automatically, so a normal signed release needs only -SigningCertPath / -SigningCertPassword."
Write-Host "To pin by hand (e.g. a rollover):  /p:MainguardPinnedThumbprints=$($cert.Thumbprint)"
