param([switch]$NoBrowser,[switch]$Background)
$ErrorActionPreference='Stop'
$executable=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'))
$directory=[IO.Path]::GetDirectoryName($executable)
if(-not(Test-Path -LiteralPath $executable)){throw '请先运行 scripts\Publish.ps1 发布程序。'}
$lockPath=Join-Path $directory '.deployment.lock'
$lockWait=[Diagnostics.Stopwatch]::StartNew()
while($true){
    try{
        $lease=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None')
        break
    }catch{
        $cause=$_.Exception.GetBaseException()
        if($cause -isnot [IO.IOException] -or ($cause.HResult -band 0xffff) -notin 32,33){throw}
        if($lockWait.Elapsed.TotalSeconds -ge 45){throw '发布或启动仍在进行，请稍后重试。'}
        Start-Sleep -Milliseconds 250
    }
}
try{
    $health=$null
    try{$health=Invoke-RestMethod 'http://localhost:5218/health' -TimeoutSec 2}catch{}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$expected='PulletCryptoRadar-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($directory.TrimEnd('\').ToUpperInvariant())))).Replace('-','').Substring(0,20)}finally{$sha.Dispose()}
    if($health -and $health.instanceId -ne $expected){throw '5218 端口被其他程序或其他发布目录占用。'}
    $alreadyRunning=$null -ne $health
    if(-not $alreadyRunning){
        $style=if($Background){'Hidden'}else{'Normal'}
        Start-Process -FilePath $executable -WorkingDirectory $directory -WindowStyle $style
        for($i=0;$i -lt 40;$i++){
            try{$health=Invoke-RestMethod 'http://localhost:5218/health' -TimeoutSec 1;if($health.instanceId -eq $expected){break}}catch{}
            Start-Sleep -Milliseconds 500
        }
        if(-not $health -or $health.instanceId -ne $expected){throw '启动失败，请查看服务器窗口。'}
    }
    if($health.mode -ne 'ResearchOnly'){throw '当前程序不是妖币研究版，请停止服务器并重新发布。'}
}finally{$lease.Dispose()}
if(-not $NoBrowser){Start-Process 'http://localhost:5218'}
if($alreadyRunning){Write-Host 'PulletCryptoRadar 已在运行：http://localhost:5218' -ForegroundColor Cyan}
else{Write-Host 'PulletCryptoRadar 已启动：http://localhost:5218' -ForegroundColor Cyan}
Write-Host '妖币研究模式。关闭服务器窗口会停止运行，也可使用“停止服务器.cmd”正常停止。'
