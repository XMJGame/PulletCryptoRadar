param([string]$Destination='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$Destination){$Destination=Join-Path $projectRoot ('artifacts\simulation-packages\Simulation-RC-'+(Get-Date -Format yyyyMMdd-HHmmss))}
$Destination=[IO.Path]::GetFullPath($Destination)
$server=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\win-x64'))
if((Test-Path -LiteralPath $Destination) -or $Destination.StartsWith($server+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $Destination -eq $server){throw 'Package destination must be a new directory outside the server.'}
$health=Invoke-RestMethod http://localhost:5218/health -TimeoutSec 10
if($health.application -ne 'PulletCryptoRadar' -or $health.status -ne 'Ready'){throw 'The source deployment is not ready.'}
foreach($path in @('artifacts\simulation-maintenance\PulletCryptoRadar.Maintenance.exe','artifacts\supervisor\PulletCryptoRadar.Supervisor.exe','docs\模拟盘-备份恢复与24小时验收.md')){if(!(Test-Path -LiteralPath (Join-Path $projectRoot $path))){throw "Missing package input: $path"}}
[IO.Directory]::CreateDirectory($Destination)|Out-Null
$target=Join-Path $Destination 'artifacts\win-x64';[IO.Directory]::CreateDirectory($target)|Out-Null
foreach($item in Get-ChildItem -LiteralPath $server){if($item.Name -in @('Data','Logs','.deployment.lock','Backup')){continue};Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse}
foreach($name in @('simulation-maintenance','supervisor')){
 $helper=Join-Path $projectRoot ('artifacts\'+$name)
 if(@(Get-ChildItem -LiteralPath $helper -Directory|Where-Object Name -in @('Data','Logs','Backup')).Count -gt 0){throw 'Unexpected state directory in packaged helper.'}
 Copy-Item -LiteralPath $helper -Destination (Join-Path $Destination 'artifacts') -Recurse
}
[IO.Directory]::CreateDirectory((Join-Path $Destination 'scripts'))|Out-Null
foreach($name in @('Server-Window.ps1','Simulation-Maintenance.ps1','New-SimulationPackage.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $Destination 'scripts')}
foreach($name in @('Start.ps1','Stop.ps1','启动服务器.cmd','停止服务器.cmd')){Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $Destination}
[IO.Directory]::CreateDirectory((Join-Path $Destination 'docs'))|Out-Null
foreach($name in @('模拟盘-备份恢复与24小时验收.md','正式模拟盘-发布清单-2026-11-01.md','日常启动与停止.md','Simulation-Release-验收记录-2026-11-01.md')){Copy-Item -LiteralPath (Join-Path $projectRoot ('docs\'+$name)) -Destination (Join-Path $Destination 'docs')}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\模拟盘-备份恢复与24小时验收.md') -Destination (Join-Path $Destination 'README.md')
$files=@(Get-ChildItem -LiteralPath $Destination -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($Destination,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$manifest=[ordered]@{version='Simulation-RC-2026-11-01';createdAt=[DateTimeOffset]::Now.ToString('o');state='CandidateAwait24HourObservation';knownIssues=@('Native JSON encoder faults in test/maintenance processes remain unresolved','24-hour observation and complete strategy evidence pending');scope='Virtual funds only; no real-order integration';sourcePid=$health.processId;accountIncluded=$false;files=$files}
$manifest|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Destination 'release.json') -Encoding utf8
Compress-Archive -LiteralPath $Destination -DestinationPath ($Destination+'.zip') -CompressionLevel Optimal
[pscustomobject]@{directory=$Destination;zip=($Destination+'.zip');files=$files.Count;bytes=(Get-Item -LiteralPath ($Destination+'.zip')).Length;sha256=(Get-FileHash -LiteralPath ($Destination+'.zip') -Algorithm SHA256).Hash}|ConvertTo-Json
