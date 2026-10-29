param([switch]$NoBrowser, [switch]$Background)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'
$url = 'http://localhost:5218'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run scripts\Publish.ps1 first.' }
$existing = $null
try { $existing = Invoke-RestMethod "$url/health" -TimeoutSec 2 } catch { }
if ($existing -and $existing.Application -ne 'PulletCryptoRadar') { throw 'Port 5218 is occupied by another application.' }
if (-not $existing) {
    $windowStyle = if ($Background) { 'Hidden' } else { 'Normal' }
    Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle $windowStyle
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $health = Invoke-RestMethod "$url/health" -TimeoutSec 1; if ($health.Application -eq 'PulletCryptoRadar') { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Startup failed. Check artifacts\win-x64\Logs.' }
}
if (-not $NoBrowser) { Start-Process $url }
Write-Host "PulletCryptoRadar running at $url"
if ($existing) { Write-Host 'The server was already running; its window visibility was not changed. Run Stop.ps1 then Start.ps1 to change the launch mode.' }
elseif (-not $Background) { Write-Host 'Server console is visible. Keep it open while collecting; close it or run Stop.ps1 to stop.' }
