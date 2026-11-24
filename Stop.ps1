param([ValidateRange(5,120)][int]$TimeoutSeconds=60)
$ErrorActionPreference='Stop'
$executable=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'))
$running=@(Get-Process -Name PulletCryptoRadar -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path,$executable,[StringComparison]::OrdinalIgnoreCase)
})
if($running.Count -eq 0){Write-Host '本项目服务器未运行。';exit 0}
$directory=[IO.Path]::GetDirectoryName($executable).TrimEnd('\').ToUpperInvariant()
$sha=[Security.Cryptography.SHA256]::Create()
try{$instance='PulletCryptoRadar-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($directory)))).Replace('-','').Substring(0,20)}finally{$sha.Dispose()}
$pipe=New-Object IO.Pipes.NamedPipeClientStream('.', $instance,[IO.Pipes.PipeDirection]::InOut)
try{
    $pipe.Connect(3000)
    $bytes=[Text.Encoding]::UTF8.GetBytes("stop`n");$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush()
    $reader=New-Object IO.StreamReader($pipe)
    $reply=$reader.ReadLineAsync()
    if(-not $reply.Wait(3000) -or $reply.Result -ne 'ok'){throw '服务器未确认停止请求。'}
}finally{$pipe.Dispose()}
foreach($server in $running){if(-not $server.WaitForExit($TimeoutSeconds*1000)){throw '服务器仍在排空写入，暂未强制结束。'}}
Write-Host '服务器已正常停止，本地数据保留。' -ForegroundColor Cyan
