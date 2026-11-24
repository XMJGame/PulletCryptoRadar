$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$executable=Join-Path $project 'artifacts\win-x64\PulletCryptoRadar.exe'
$data=Join-Path $project 'artifacts\win-x64\Data'
if(-not(Test-Path -LiteralPath $executable)){throw '请先发布程序。'}
& $executable --research-history $data
$result=$LASTEXITCODE
if($result -eq 2){Write-Warning '报告已生成，部分历史区间缺失。';exit 2}
if($result -ne 0){throw "历史研究失败，退出码 $result"}
