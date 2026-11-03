param([Parameter(Mandatory=$true)][string]$ServerDirectory,[Parameter(Mandatory=$true)][string]$StopScript)
$ErrorActionPreference='Stop'
$ServerDirectory=[IO.Path]::GetFullPath($ServerDirectory)
$logs=Join-Path $ServerDirectory 'Logs'
[IO.Directory]::CreateDirectory($logs)|Out-Null
try{$viewLease=New-Object IO.FileStream((Join-Path $logs 'status-window.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch{exit 0}
function Read-SharedText([string]$path){
    $file=New-Object IO.FileStream($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try{$reader=New-Object IO.StreamReader($file,[Text.Encoding]::UTF8);try{return $reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$file.Dispose()}
}
try{
    $host.UI.RawUI.WindowTitle='PulletCryptoRadar 服务器观察窗口 - 关闭窗口后继续运行'
    Write-Host 'PulletCryptoRadar - http://localhost:5218' -ForegroundColor Cyan
    Write-Host '关闭本窗口 / Ctrl+C / Q：仅关闭观察窗口，服务器继续运行。' -ForegroundColor Yellow
    Write-Host 'S：正常停止服务器并排空写入；也可使用“停止服务器.cmd”。' -ForegroundColor Yellow
    Write-Host '本窗口读取本地状态和日志，不会因选中文本阻塞服务器。'
    $lastState='';$lastLog='';$hadServer=$false
    while($true){
        try{
            $path=Join-Path $logs 'supervisor-state.json'
            if(Test-Path -LiteralPath $path){
                $raw=Read-SharedText $path;$state=$raw|ConvertFrom-Json
                if($raw -ne $lastState){
                    $lastState=$raw;$at=[datetimeoffset]$state.At;$age=([datetimeoffset]::UtcNow-$at).TotalSeconds
                    $clock=$at.ToOffset([timespan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss')
                    $stale=if($age -gt 40){' [状态已过期，不保证进程仍运行]'}else{''}
                    Write-Host ("{0}  {1}  PID {2}{3}  {4}" -f $clock,$state.Phase,$state.ServerProcessId,$stale,$state.Message) -ForegroundColor Cyan
                    if($state.ServerProcessId){$hadServer=$true}
                    if($hadServer -and $state.Phase -eq 'Stopped'){Write-Host '服务器已停止，观察窗口关闭。' -ForegroundColor Yellow;break}
                    if($state.Phase -eq 'CircuitOpen'){Write-Host '连续退出保护已触发，请检查日志。Q仅关闭观察，S正常停止。' -ForegroundColor Yellow}
                }
            }
            if($state -and $state.Phase -eq 'StopRequested' -and $state.ServerProcessId){
                $server=Get-Process -Id $state.ServerProcessId -ErrorAction SilentlyContinue
                if(-not $server){Write-Host '服务器已按请求停止；关闭观察窗口。' -ForegroundColor Yellow;break}
            }
            $day=[datetimeoffset]::UtcNow.ToOffset([timespan]::FromHours(8)).ToString('yyyyMMdd')
            $logPath=Join-Path $logs ('PulletCryptoRadar-'+$day+'.log')
            if(Test-Path -LiteralPath $logPath){
                $tail=((Read-SharedText $logPath) -split "`r?`n"|Where-Object{$_}|Select-Object -Last 8) -join "`n"
                if($tail -ne $lastLog){$lastLog=$tail;Write-Host $tail}
            }
        }catch{Write-Host ('本地状态暂不可读：'+$_.Exception.GetType().Name) -ForegroundColor Yellow}
        for($i=0;$i -lt 20;$i++){
            if([Console]::KeyAvailable){
                $key=[Console]::ReadKey($true).Key
                if($key -eq 'Q'){return}
                if($key -eq 'S'){& $StopScript;Write-Host '已请求正常停止。';return}
            }
            Start-Sleep -Milliseconds 100
        }
    }
}finally{$viewLease.Dispose()}
