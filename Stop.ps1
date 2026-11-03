param([switch]$Force, [ValidateRange(5,120)][int]$TimeoutSeconds=60)
$ErrorActionPreference = 'Stop'
$executable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\win-x64\PulletCryptoRadar.exe'))
$root = [IO.Path]::GetDirectoryName($executable)
$stopLog = Join-Path $root 'Logs'
[IO.Directory]::CreateDirectory($stopLog) | Out-Null
# Coordinate with the supervisor before enumerating processes. Once this marker exists,
# even an unexpected exit or an in-progress backoff must not restart the host.
$deploymentLease = $null
for ($attempt=0; $attempt -lt 100 -and $null -eq $deploymentLease; $attempt++) {
    try { $deploymentLease = New-Object IO.FileStream((Join-Path $root '.deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch { Start-Sleep -Milliseconds 100 }
}
if ($null -eq $deploymentLease) { throw 'Startup/publication is still active; stop request was not sent. Wait for it to finish.' }
try { [IO.File]::WriteAllText((Join-Path $stopLog 'supervisor-stop.request'),[DateTimeOffset]::UtcNow.ToString('O')) }
finally { $deploymentLease.Dispose() }
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'PulletCryptoRadar.exe'" | Where-Object {
    $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $executable, [StringComparison]::OrdinalIgnoreCase)
})
foreach ($server in $running) {
    if ($Force) { Stop-Process -Id $server.ProcessId -ErrorAction Stop; continue }
    $process = Get-Process -Id $server.ProcessId -ErrorAction Stop
    $directory = [IO.Path]::GetDirectoryName($executable).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($directory)))).Replace('-', '').Substring(0,20) }
    finally { $sha.Dispose() }
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', ('PulletCryptoRadar-' + $suffix), [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(2000)
        $command = [Text.Encoding]::UTF8.GetBytes("stop`n")
        $pipe.Write($command,0,$command.Length); $pipe.Flush()
        $reader = New-Object IO.StreamReader($pipe)
        $reply = $reader.ReadLineAsync()
        if (-not $reply.Wait(2000) -or $reply.Result -ne 'ok') { throw 'The server did not acknowledge shutdown.' }
    } catch { throw ('Graceful shutdown failed; server was not killed. Check the console. Old builds without local control can be stopped explicitly with Stop.ps1 -Force. ' + $_.Exception.Message) }
    finally { $pipe.Dispose() }
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Shutdown is still draining writes; publication is blocked. Check the server console.' }
}
if ($running.Count -eq 0) { Write-Host 'This workspace server is not running.' }
elseif ($Force) { Write-Host 'PulletCryptoRadar force-stopped. Data files are retained; in-flight work may be incomplete.' }
else { Write-Host 'PulletCryptoRadar stopped gracefully after draining accepted writes. Data files are retained.' }
$supervisorLock = Join-Path $stopLog 'supervisor-instance.lock'
$supervisorStopped=$false
for ($attempt=0; $attempt -lt 150; $attempt++) {
    try { $lock=New-Object IO.FileStream($supervisorLock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $lock.Dispose(); $supervisorStopped=$true; break }
    catch { Start-Sleep -Milliseconds 100 }
}
if (-not $supervisorStopped) { throw 'Server stop completed but supervisor has not released its lease; publication remains blocked. Check supervisor-state.json.' }
