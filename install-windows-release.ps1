#Requires -Version 5.1
<#
.SYNOPSIS
    Installs WoLLM into a stable folder and registers it to start at system boot.

.DESCRIPTION
    Copies the extracted GitHub Release contents into a stable installation
    directory, attempts to remove the Internet download mark from the files,
    and then registers WoLLM in Task Scheduler.

.PARAMETER SourceDir
    Folder containing the extracted GitHub Release files. Defaults to the script directory.

.PARAMETER InstallDir
    Target folder for the WoLLM installation. Defaults to Program Files\WoLLM.

.PARAMETER TaskName
    Task Scheduler task name. Defaults to "WoLLM_Autostart".

.EXAMPLE
    .\install-windows-release.ps1
    .\install-windows-release.ps1 -InstallDir "D:\Apps\WoLLM" -TaskName "MyWoLLM"
#>
param(
    [string]$SourceDir = $PSScriptRoot,
    [string]$InstallDir = (Join-Path ${env:ProgramFiles} "WoLLM"),
    [string]$TaskName = "WoLLM_Autostart"
)

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Administrator privileges are required. Re-run PowerShell as Administrator, then run .\\install-windows-release.ps1 again."
    exit 1
}

if (-not (Test-Path -LiteralPath $SourceDir)) {
    Write-Error "Source directory not found at: $SourceDir"
    exit 1
}

$resolvedSourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
$sourceExe = Join-Path $resolvedSourceDir "wollm.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    Write-Error "wollm.exe not found in source directory: $resolvedSourceDir"
    exit 1
}

$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Path $resolvedInstallDir -Force | Out-Null

Get-ChildItem -LiteralPath $resolvedSourceDir -Recurse -File -Force | ForEach-Object {
    Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
}

Get-ChildItem -LiteralPath $resolvedSourceDir -Force | ForEach-Object {
    $destination = Join-Path $resolvedInstallDir $_.Name
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
}

Get-ChildItem -LiteralPath $resolvedInstallDir -Recurse -File -Force | ForEach-Object {
    Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
}

$installedExe = Join-Path $resolvedInstallDir "wollm.exe"
if (-not (Test-Path -LiteralPath $installedExe)) {
    Write-Error "Installed wollm.exe not found at: $installedExe"
    exit 1
}

$action = New-ScheduledTaskAction `
    -Execute $installedExe `
    -WorkingDirectory $resolvedInstallDir

$trigger = New-ScheduledTaskTrigger -AtStartup

$taskPrincipal = New-ScheduledTaskPrincipal `
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
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $taskPrincipal `
    -Description "Starts WoLLM automatically at system boot, without requiring user logon." `
    -Force | Out-Null

Write-Host "WoLLM installed to: $resolvedInstallDir"
Write-Host "Task '$TaskName' registered. WoLLM will start at next boot without requiring user logon."
Write-Host "To start now: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "To remove:    Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
