param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
try { $deploymentLease = New-Object IO.FileStream((Join-Path $OutputDirectory '.deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'Publication blocked: another startup or publication owns the deployment directory.' }
try {
$targetExecutable = Join-Path $OutputDirectory 'PulletCryptoRadar.exe'
$active = @(Get-CimInstance Win32_Process -Filter "Name = 'PulletCryptoRadar.exe'" | Where-Object {
    $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $targetExecutable, [StringComparison]::OrdinalIgnoreCase)
})
if ($active.Count -gt 0) { throw 'Publication blocked: the target server is running. Run Stop.ps1 and wait for graceful shutdown before publishing.' }
$supervisorLock = Join-Path $OutputDirectory 'Logs\supervisor-instance.lock'
if (Test-Path -LiteralPath $supervisorLock) {
    try { $testLease=New-Object IO.FileStream($supervisorLock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $testLease.Dispose() }
    catch { throw 'Publication blocked: supervisor is active. Run Stop.ps1 first.' }
}
Push-Location (Join-Path $projectRoot 'frontend')
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend typecheck/build failed.' }
} finally { Pop-Location }
dotnet test (Join-Path $projectRoot 'PulletCryptoRadar.sln') -c Release --logger 'trx;LogFilePrefix=Phase12' --results-directory (Join-Path $projectRoot 'TestResults')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$savedSettings = $null
$settingsPath = Join-Path $OutputDirectory 'appsettings.json'
if (Test-Path -LiteralPath $settingsPath) {
    $savedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
}
$active = @(Get-CimInstance Win32_Process -Filter "Name = 'PulletCryptoRadar.exe'" | Where-Object {
    $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $targetExecutable, [StringComparison]::OrdinalIgnoreCase)
})
if ($active.Count -gt 0) { throw 'Publication blocked: the target server was started during validation.' }
dotnet publish (Join-Path $projectRoot 'src\PulletCryptoRadar.Host\PulletCryptoRadar.Host.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$supervisorOutput = Join-Path $projectRoot 'artifacts\supervisor'
dotnet publish (Join-Path $projectRoot 'src\PulletCryptoRadar.Supervisor\PulletCryptoRadar.Supervisor.csproj') -c Release -r win-x64 --self-contained true -o $supervisorOutput
if ($LASTEXITCODE -ne 0) { throw 'Supervisor publish failed.' }
if ($null -ne $savedSettings) {
    $defaults = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in $defaults.Keys) { if (-not $savedSettings.ContainsKey($key)) { $savedSettings[$key] = $defaults[$key] } }
    $savedSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}
Write-Host "Published: $OutputDirectory\PulletCryptoRadar.exe"
} finally { $deploymentLease.Dispose() }
