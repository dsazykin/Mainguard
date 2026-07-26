<#
.SYNOPSIS
  Registers the P2-21 reboot-resume Scheduled Task (an ONLOGON task — never a one-shot registry
  autostart).

.DESCRIPTION
  After the OOBE enables the two Windows optional features (which require a reboot to finish), setup
  must continue automatically after the restart. We use an ONLOGON Scheduled Task rather than a one-shot
  registry autostart because a Scheduled Task survives the reboot cleanly and self-deletes once setup
  reaches Done.

  RUN LEVEL (audit MG-15) — this is the important part, and it is derived, never chosen:

    * '-RunLevel Highest' makes Windows launch the target ELEVATED at every logon WITH NO UAC PROMPT.
      Pointing that at an executable inside a directory the logged-on user can write (which is what
      %LocalAppData% — Mainguard's per-user Velopack install — is) hands any process running as that
      user a one-write path to administrator rights. A path check cannot see it: the path is still
      correct, only the bytes changed.
    * So Highest is used ONLY when the target lives under an administrator-owned root (Program Files,
      the Windows directory). Everything else gets '-RunLevel Limited'.
    * Limited is sufficient for what the resume actually does: the optional features were enabled
      before the reboot and DISM completes them during it, and the remaining work (`wsl --import`)
      needs no administrator rights. If a resumed run ever needs elevation again it takes the same
      single UAC prompt the first pass took.

  This script is the canonical registration the elevated helper performs
  (Mainguard.Agents InstallerCommands.RegisterResumeTask + ResumeTaskPolicy — that pair is the tested
  implementation; this file mirrors it for the human matrix). It must be run elevated.

  G-12: this script never issues the VM-wide WSL shutdown verb; it only registers/unregisters our task.

.PARAMETER ResumeExePath
  Full path to the exe relaunched with --resume by the task.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $ResumeExePath
)

$ErrorActionPreference = 'Stop'
$TaskName = 'Mainguard-OOBE-Resume'

# --- MG-15: derive the run level from where the target lives (see .DESCRIPTION). ---
$targetDir = [System.IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $ResumeExePath).Path)
$protectedRoots = @(
    $env:ProgramW6432,
    ${env:ProgramFiles},
    ${env:ProgramFiles(x86)},
    $env:SystemRoot
) | Where-Object { $_ }
$userRoots = @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP) | Where-Object { $_ }

function Test-Within([string] $Candidate, [string] $Root) {
    $c = $Candidate.TrimEnd('\') + '\'
    $r = $Root.TrimEnd('\') + '\'
    return $c.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

$isUserWritable = @($userRoots | Where-Object { Test-Within $targetDir $_ }).Count -gt 0
$isProtected = (-not $isUserWritable) -and (@($protectedRoots | Where-Object { Test-Within $targetDir $_ }).Count -gt 0)
$runLevel = if ($isProtected) { 'Highest' } else { 'Limited' }

# ONLOGON trigger so it fires after the reboot's next logon.
$action    = New-ScheduledTaskAction -Execute $ResumeExePath -Argument '--resume'
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId (whoami) -LogonType Interactive -RunLevel $runLevel
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Registered resume task '$TaskName' (RunLevel $runLevel) → $ResumeExePath --resume"
if (-not $isProtected) {
    Write-Host "  RunLevel is Limited because '$targetDir' is not an administrator-owned directory —" `
        "a no-prompt elevated task must never point at a binary this user can replace (MG-15)."
}
