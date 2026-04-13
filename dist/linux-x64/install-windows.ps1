#Requires -Version 5.1
<#
.SYNOPSIS
    Registers WoLLM to start at system boot via Windows Task Scheduler.

.DESCRIPTION
    Does NOT install as a Windows Service — Task Scheduler with RunLevel Limited
    keeps WoLLM in the user session, which is required for GPU driver access,
    CUDA contexts, and conda/virtualenv environments.

.PARAMETER WollmExe
    Path to wollm.exe. Defaults to wollm.exe in the same directory as this script.

.PARAMETER TaskName
    Task Scheduler task name. Defaults to "WoLLM_Autostart".

.EXAMPLE
    .\install-windows.ps1
    .\install-windows.ps1 -WollmExe "D:\AI\wollm.exe" -TaskName "MyWoLLM"
#>
param(
    [string]$WollmExe  = (Join-Path $PSScriptRoot "wollm.exe"),
    [string]$TaskName  = "WoLLM_Autostart"
)

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Administrator privileges are required. Re-run PowerShell as Administrator, then run .\\install-windows.ps1 again."
    exit 1
}

if (-not (Test-Path $WollmExe)) {
    Write-Error "wollm.exe not found at: $WollmExe"
    exit 1
}

$resolvedExe = (Resolve-Path $WollmExe).Path
$exeDir = Split-Path -Parent $resolvedExe

$action = New-ScheduledTaskAction `
    -Execute $resolvedExe `
    -WorkingDirectory $exeDir

$trigger = New-ScheduledTaskTrigger -AtStartup

$principal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask `
    -TaskName  $TaskName `
    -Action    $action `
    -Trigger   $trigger `
    -Settings  $settings `
    -Principal $principal `
    -Description "Starts WoLLM automatically at system boot, without requiring user logon." `
    -Force | Out-Null

Write-Host "Task '$TaskName' registered. WoLLM will start at next boot without requiring user logon."
Write-Host "To start now: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "To remove:    Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
