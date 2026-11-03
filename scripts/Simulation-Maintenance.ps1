param(
 [ValidateSet('Backup','VerifyBackup','RestoreTest','StartObservation','CheckObservation')][string]$Mode='CheckObservation',
 [string]$SourceDirectory='', [string]$Directory='', [string]$Destination=''
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $projectRoot 'artifacts\simulation-maintenance\PulletCryptoRadar.Maintenance.exe'
if(!(Test-Path -LiteralPath $tool)){throw 'Publish tools/PulletCryptoRadar.Maintenance to artifacts/simulation-maintenance first.'}
if(!$SourceDirectory){$SourceDirectory=Join-Path $projectRoot 'artifacts\win-x64'}
$pointer=Join-Path $projectRoot 'artifacts\simulation-observation-current.json'
switch($Mode){
 'Backup' {
  if(!$Destination){$Destination=Join-Path $projectRoot ('artifacts\simulation-backups\'+(Get-Date -Format yyyyMMdd-HHmmss))}
  & $tool backup $SourceDirectory $Destination
 }
 'VerifyBackup' {if(!$Directory){throw 'Specify -Directory with a backup directory.'};& $tool verify-backup $Directory}
 'RestoreTest' {
  if(!$Directory){throw 'Specify -Directory with a backup directory.'}
  if(!$Destination){$Destination=Join-Path $projectRoot ('artifacts\simulation-restore-tests\'+(Get-Date -Format yyyyMMdd-HHmmss))}
  & $tool restore-test $Directory $Destination
 }
 'StartObservation' {
  if(!$Destination){$Destination=Join-Path $projectRoot ('artifacts\simulation-observations\'+(Get-Date -Format yyyyMMdd-HHmmss))}
  & $tool soak-start $SourceDirectory $Destination
  if($LASTEXITCODE -eq 0){[ordered]@{directory=[IO.Path]::GetFullPath($Destination);baseline=(Join-Path ([IO.Path]::GetFullPath($Destination)) 'baseline.json')}|ConvertTo-Json|Set-Content -LiteralPath $pointer -Encoding utf8}
 }
 'CheckObservation' {
  if(!$Directory){if(!(Test-Path -LiteralPath $pointer)){throw 'No observation baseline. Run -Mode StartObservation first.'};$Directory=(Get-Content -LiteralPath $pointer -Raw|ConvertFrom-Json).directory}
  & $tool soak-check $Directory
 }
}
if($LASTEXITCODE -ne 0){throw "Maintenance exited with code $LASTEXITCODE. Keep diagnostic output; source account was not restored in place."}
