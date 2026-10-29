$ErrorActionPreference = 'Stop'
$executable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'))
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'PulletCryptoRadar.exe'" | Where-Object {
    $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $executable, [StringComparison]::OrdinalIgnoreCase)
})
foreach ($server in $running) { Stop-Process -Id $server.ProcessId -ErrorAction Stop }
if ($running.Count -eq 0) { Write-Host 'This workspace server is not running.' }
else { Write-Host 'PulletCryptoRadar stopped. Data files are retained. Collection stops until the next startup.' }
