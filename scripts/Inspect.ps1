param([switch]$AsJson)
$ErrorActionPreference='Stop'
function Read-Api([string]$path){
    Invoke-RestMethod ('http://localhost:5218'+$path) -TimeoutSec 5
}
try{
    $health=Read-Api '/health'
    if($health.application -ne 'PulletCryptoRadar' -or $health.mode -ne 'ResearchOnly'){
        throw '5218 响应不是当前妖币研究版。'
    }
    $observation=Read-Api '/api/observation'
    $radar=Read-Api '/api/radar/board'
    $budget=Read-Api '/api/collection/budget'
    $report=[ordered]@{
        CheckedAt=[DateTimeOffset]::UtcNow
        Mode=$health.mode
        HeartbeatFresh=$observation.heartbeatFresh
        ContinuousSeconds=$observation.continuousSeconds
        ProblemSamples24h=$observation.problemSamples24h
        Coverage=$radar.coverage
        ResearchCandidates=@($radar.research | Where-Object { $null -ne $_ }).Count
        DayResponseBytes=$budget.dayBytesIncludingReservations
        MonthResponseBytes=$budget.monthBytesIncludingReservations
    }
    $json=$report | ConvertTo-Json -Depth 8
    $directory=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64\Data\Reports'
    [IO.Directory]::CreateDirectory($directory)|Out-Null
    Set-Content -LiteralPath (Join-Path $directory 'research-status.json') -Value $json -Encoding UTF8
    if($AsJson){$json}else{$report | Format-List}
}catch{
    Write-Error ("研究检查失败：" + $_.Exception.Message)
    exit 1
}
