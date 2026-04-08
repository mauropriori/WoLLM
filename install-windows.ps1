#Requires -Version 5.1
<#
.SYNOPSIS
    Registers WoLLM to start at user login via Windows Task Scheduler.

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

if (-not (Test-Path $WollmExe)) {
    Write-Error "wollm.exe not found at: $WollmExe"
    exit 1
}

$action = New-ScheduledTaskAction `
    -Execute $WollmExe `
    -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable

Register-ScheduledTask `
    -TaskName  $TaskName `
    -Action    $action `
    -Trigger   $trigger `
    -Settings  $settings `
    -RunLevel  Limited `
    -Force | Out-Null

Write-Host "Task '$TaskName' registered. WoLLM will start at next login."
Write-Host "To start now: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "To remove:    Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
