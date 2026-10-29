param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\win-x64' }
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
dotnet publish (Join-Path $projectRoot 'src\PulletCryptoRadar.Host\PulletCryptoRadar.Host.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if ($null -ne $savedSettings) {
    $defaults = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in $defaults.Keys) { if (-not $savedSettings.ContainsKey($key)) { $savedSettings[$key] = $defaults[$key] } }
    $savedSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}
Write-Host "Published: $OutputDirectory\PulletCryptoRadar.exe"
