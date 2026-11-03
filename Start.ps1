param([switch]$NoBrowser, [switch]$Background, [switch]$Diagnostics, [switch]$Direct)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'
$url = 'http://localhost:5218'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run scripts\Publish.ps1 first.' }
$deploymentPath = Join-Path (Split-Path -Parent $executable) '.deployment.lock'
try { $deploymentLease = New-Object IO.FileStream($deploymentPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'A publication or startup is already in progress for this server. Wait for it to finish.' }
try {
$existing = $null
try { $existing = Invoke-RestMethod "$url/health" -TimeoutSec 2 } catch { }
if ($existing -and $existing.Application -ne 'PulletCryptoRadar') { throw 'Port 5218 is occupied by another application.' }
if ($existing -and $existing.InstanceId) {
    $instanceDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $executable)).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $expectedInstance = 'PulletCryptoRadar-' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($instanceDirectory)))).Replace('-','').Substring(0,20) }
    finally { $sha.Dispose() }
    if ($existing.InstanceId -ne $expectedInstance) { throw 'Port 5218 is occupied by another PulletCryptoRadar deployment. This workspace server was not started.' }
}
if (-not $Direct) {
    $supervisor = Join-Path $PSScriptRoot 'artifacts\supervisor\PulletCryptoRadar.Supervisor.exe'
    if (-not (Test-Path -LiteralPath $supervisor)) { throw 'Publish the supervisor first with scripts\Publish.ps1. For a temporary direct launch use Start.ps1 -Direct.' }
    $instanceDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $executable)).ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($instanceDirectory)))).Replace('-','').Substring(0,20) }
    finally { $sha.Dispose() }
    $taskName = 'PulletCryptoRadar-' + $suffix
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $stopMarker = Join-Path (Split-Path -Parent $executable) 'Logs\supervisor-stop.request'
    if ($task -and $task.State -eq 'Running' -and (Test-Path -LiteralPath $stopMarker)) { throw 'The previous supervisor is stopping. Wait for Stop.ps1 to finish, then start again.' }
    if (-not $task -or $task.State -ne 'Running') {
        if (Test-Path -LiteralPath $stopMarker) { Remove-Item -LiteralPath $stopMarker }
        $arguments = '"' + (Split-Path -Parent $executable) + '" --background'
        $action = New-ScheduledTaskAction -Execute $supervisor -Argument $arguments -WorkingDirectory (Split-Path -Parent $supervisor)
        $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -DontStopOnIdleEnd
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Description 'On-demand PulletCryptoRadar supervisor. No boot/logon trigger. Current user only. Stop.ps1 stops recovery.' -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName
    }
    # Release before waiting: supervisor uses the same lock when launching the host.
    $deploymentLease.Dispose(); $deploymentLease = $null
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { $health = Invoke-RestMethod "$url/health" -TimeoutSec 1; if ($health.Application -eq 'PulletCryptoRadar') { $ready=$true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Supervised startup did not become ready. Check Logs\supervisor-state.json and server console. No force-stop was performed.' }
    Write-Host "On-demand supervisor task: $taskName (current user, no boot/logon trigger)."
} elseif (-not $existing) {
    $windowStyle = if ($Background) { 'Hidden' } else { 'Normal' }
    if ($Diagnostics) {
        $dumpDirectory = Join-Path (Split-Path -Parent $executable) 'Logs\Dumps'
        [IO.Directory]::CreateDirectory($dumpDirectory) | Out-Null
        $diagnosticValues = @{DOTNET_DbgEnableMiniDump='1';DOTNET_DbgMiniDumpType='1';DOTNET_DbgMiniDumpName=(Join-Path $dumpDirectory 'PulletCryptoRadar-%p-%t.dmp')}
        $previousValues = @{}
        foreach ($key in $diagnosticValues.Keys) { $previousValues[$key]=[Environment]::GetEnvironmentVariable($key,'Process'); [Environment]::SetEnvironmentVariable($key,$diagnosticValues[$key],'Process') }
        try { Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle $windowStyle }
        finally { foreach ($key in $previousValues.Keys) { [Environment]::SetEnvironmentVariable($key,$previousValues[$key],'Process') } }
    } else { Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle $windowStyle }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $health = Invoke-RestMethod "$url/health" -TimeoutSec 1; if ($health.Application -eq 'PulletCryptoRadar') { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Startup failed. Check artifacts\win-x64\Logs.' }
}
if (-not $NoBrowser) { Start-Process $url }
if (-not $Direct -and -not $Background) {
    $viewScript = Join-Path $PSScriptRoot 'scripts\Server-Window.ps1'
    if (-not (Test-Path -LiteralPath $viewScript)) { throw 'Server is running, but the status window script is missing.' }
    $viewLock = Join-Path (Split-Path -Parent $executable) 'Logs\status-window.lock'
    $viewOpen = $false
    try { $viewLease=New-Object IO.FileStream($viewLock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$viewLease.Dispose() }
    catch { $viewOpen=$true }
    if (-not $viewOpen) {
        $viewArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$viewScript+'"'),'-ServerDirectory',('"'+(Split-Path -Parent $executable)+'"'),'-StopScript',('"'+(Join-Path $PSScriptRoot 'Stop.ps1')+'"'))
        Start-Process -FilePath 'powershell.exe' -ArgumentList $viewArguments -WorkingDirectory $PSScriptRoot -WindowStyle Normal
    }
}
Write-Host "PulletCryptoRadar running at $url"
if (-not $Direct -and -not $Background) { Write-Host 'Status window opened. Closing it does NOT stop the server. Use Stop.ps1, the stop shortcut, or S in the status window.' }
elseif ($existing) { Write-Host 'The server was already running; no duplicate process was launched.' }
elseif ($Direct -and -not $Background) { Write-Host 'Direct server console is visible. Closing it stops the direct server.' }
} finally { if ($null -ne $deploymentLease) { $deploymentLease.Dispose() } }
