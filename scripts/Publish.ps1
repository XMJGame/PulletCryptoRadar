param([switch]$SkipTests,[switch]$NoRestore)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$output=Join-Path $project 'artifacts\win-x64'
$executable=Join-Path $output 'PulletCryptoRadar.exe'
if(@(Get-Process -Name PulletCryptoRadar -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path,$executable,[StringComparison]::OrdinalIgnoreCase)
}).Count){throw '请先运行 Stop.ps1，再发布；不会覆盖正在运行的程序。'}
[IO.Directory]::CreateDirectory($output)|Out-Null
try{$lease=[IO.File]::Open((Join-Path $output '.deployment.lock'),'OpenOrCreate','ReadWrite','None')}catch{throw '其他发布或启动正在进行。'}
try{
    Push-Location (Join-Path $project 'frontend')
    try{
        if(-not(Test-Path -LiteralPath 'node_modules')){npm ci;if($LASTEXITCODE -ne 0){throw '前端依赖还原失败'}}
        npm run build
        if($LASTEXITCODE -ne 0){throw '前端构建失败'}
    }finally{Pop-Location}
    if(-not $NoRestore){
        dotnet restore (Join-Path $project 'PulletCryptoRadar.sln') --locked-mode --verbosity quiet
        if($LASTEXITCODE -ne 0){throw '锁定依赖还原失败'}
    }
    if(-not $SkipTests){dotnet test (Join-Path $project 'PulletCryptoRadar.sln') -c Release --no-restore --verbosity quiet;if($LASTEXITCODE -ne 0){throw '后端测试失败'}}
    dotnet publish (Join-Path $project 'src\PulletCryptoRadar.Host') -c Release -r win-x64 --self-contained true -o $output --no-restore --verbosity quiet
    if($LASTEXITCODE -ne 0){throw '发布失败'}
    Write-Host "已发布：$executable" -ForegroundColor Cyan
    Write-Host 'Data 目录保留；不生成旧版本备份。运行 Start.ps1 启动。'
}finally{$lease.Dispose()}
