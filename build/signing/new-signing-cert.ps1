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

    [int] $ValidYears = 5,

    # Validate -OutputPath against the repo-containment rule and exit without creating anything.
    # Lets you check where a key would land — and lets the containment guard itself be tested.
    [switch] $CheckPathOnly
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core' -and $PSVersionTable.Platform -ne 'Win32NT') {
    throw "New-SelfSignedCertificate is a Windows facility. Run this on the release box."
}

# ===================================================================================================
# The key must never end up in the repo — an attacker who clones it can sign whatever they like and this
# build will accept it. Refuse rather than warn.
#
# This was a STRING PREFIX test (`$outFull.StartsWith($repoRoot)`), which is wrong in both directions:
#   * FALSE POSITIVE — a sibling whose name merely starts with the repo's name, e.g.
#     C:\code\Mainguard-keys\mainguard.pfx against a repo at C:\code\Mainguard, was refused. That is not
#     a nuisance: refusing the obvious place to put the key pushes people somewhere less considered.
#   * FALSE NEGATIVE — the real one. GetFullPath normalizes text; it does not resolve the filesystem.
#     A junction, a symlink or a `subst`'d drive pointing INTO the tree (S:\keys where S: is
#     C:\code\Mainguard\keys) produced a path sharing no prefix with the repo root and sailed through,
#     writing the release private key into the working tree — exactly what this guard exists to stop.
#
# So there are two checks. The lexical one is correct now (Path.GetRelativePath: a `..` first segment,
# or an absolute result, means "not under"). The authoritative one asks the FILESYSTEM: drop a
# uniquely-named marker in the repo root, then walk the destination's ancestors looking for it. If any
# ancestor can see the marker, the destination is inside the repository no matter how it was spelled —
# junction, symlink, subst, UNC alias or case.
# ===================================================================================================
$repoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$outFull = [System.IO.Path]::GetFullPath($OutputPath)
$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not $outDir) { throw "-OutputPath must include a directory and a file name: '$OutputPath'." }

$refusal = "Refusing to write the signing key inside the repository ($outFull). Put it in a credential " +
           "store or a directory outside the working tree."

# 1) Lexical. The trailing separator is the whole fix: comparing against 'C:\code\Mainguard' matched
#    'C:\code\Mainguard-keys\…'; comparing against 'C:\code\Mainguard\' does not.
#    (Path.GetRelativePath would be tidier but does not exist on .NET Framework, and this script must
#    keep running under Windows PowerShell 5.1 as its #Requires says.)
$rootNoSep = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$rootWithSep = $rootNoSep + [System.IO.Path]::DirectorySeparatorChar
if ($outFull.Equals($rootNoSep, [System.StringComparison]::OrdinalIgnoreCase) -or
    $outFull.StartsWith($rootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw $refusal
}

# 2) Filesystem identity — the one that sees through junctions / subst / symlinks.
#    Probing starts at the nearest EXISTING ancestor of the destination, so a refused path leaves
#    nothing behind: if that ancestor is inside the repository then so is everything under it.
$probe = $outDir
while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) {
    $parent = [System.IO.Path]::GetDirectoryName($probe)
    if (-not $parent -or $parent -eq $probe) { $probe = $null; break }
    $probe = $parent
}
if ($probe) {
    $markerName = ".mainguard-containment-probe-" + [guid]::NewGuid().ToString('N')
    $markerPath = Join-Path $repoRoot $markerName
    New-Item -ItemType File -Force -Path $markerPath | Out-Null
    try {
        while ($probe) {
            if (Test-Path -LiteralPath (Join-Path $probe $markerName)) {
                throw $refusal + " (resolved: '$probe' is the repository root reached through a link/alias.)"
            }
            $parent = [System.IO.Path]::GetDirectoryName($probe)
            if (-not $parent -or $parent -eq $probe) { break }
            $probe = $parent
        }
    } finally {
        Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
    }
}

if ($CheckPathOnly) {
    Write-Host "==> OK: '$outFull' is outside the repository working tree ('$repoRoot'). Nothing was created."
    return
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
