param(
    [string]$ServerDirectory = (Join-Path $PSScriptRoot '../artifacts/win-x64'),
    [string]$CoinId = 'aria-ai',
    [string]$Chain = 'binance-smart-chain',
    [DateTimeOffset]$Since = [DateTimeOffset]::MinValue
)

$ErrorActionPreference = 'Stop'
$data = Join-Path $ServerDirectory 'Data'
$observations = Join-Path $data 'market-foundation-shadow.jsonl'
$rows = [Collections.Generic.List[object]]::new()
$invalid = 0
if (Test-Path -LiteralPath $observations) {
    foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $observations).Path)) {
        try {
            $row = $line | ConvertFrom-Json -AsHashtable -DateKind String
            $at = [DateTimeOffset]::Parse($row.observedAt)
            if ($row.shadowOnly -eq $true -and $row.coinId -ceq $CoinId -and
                $row.chain -ceq $Chain -and $at -ge $Since) {
                $rows.Add($row)
            }
        } catch { $invalid++ }
    }
}
$ordered = @($rows | Sort-Object { [DateTimeOffset]::Parse($_.observedAt) })
$routes = [ordered]@{
    CoinGecko = { $_.gecko }
    CoinMarketCap = { $_.cmc }
    DEXScreener = { $_.dex }
    InsightXDistribution = { $_.ownership.distribution }
    InsightXClusters = { $_.ownership.clusters }
}
$sources = foreach ($entry in $routes.GetEnumerator()) {
    $samples = @($ordered | ForEach-Object $entry.Value)
    $success = @($samples | Where-Object { $_.Error -eq 0 -and $null -ne $_.Value })
    $latency = @($samples | Where-Object { $null -ne $_.ElapsedMilliseconds } |
        ForEach-Object { [double]$_.ElapsedMilliseconds } | Sort-Object)
    [ordered]@{
        source = $entry.Key
        observed = @($samples | Where-Object { $_.Error -ne 11 }).Count
        notRequested = @($samples | Where-Object { $_.Error -eq 11 }).Count
        successful = $success.Count
        coveragePercent = if ($ordered.Count) { [Math]::Round(100 * $success.Count / $ordered.Count, 2) }
        errors = @($samples | Where-Object { $_.Error -notin 0, 11 } | Group-Object Error |
            Select-Object Name, Count)
        latencySamples = $latency.Count
        medianLatencyMs = if ($latency.Count) { ($latency[[int][Math]::Floor(($latency.Count - 1) / 2)] + $latency[[int][Math]::Floor($latency.Count / 2)]) / 2 }
    }
}
$comparable = @($ordered | Where-Object { $_.marketCapital.status -in 'Confirmed', 'Conflict' })
$conflicts = @($comparable | Where-Object { $_.marketCapital.status -eq 'Conflict' })
$latest = $ordered | Select-Object -Last 1
$quota = $null
$quotaState = 'Missing'
$ledger = Join-Path $data 'foundation-quota.json'
if (Test-Path -LiteralPath $ledger) {
    try {
        $quota = Get-Content -LiteralPath $ledger -Raw | ConvertFrom-Json -AsHashtable
        $quotaState = 'Available'
    } catch { $quotaState = 'Unreadable' }
}
$continuity = $null
$settings = Join-Path $ServerDirectory 'appsettings.json'
if ($CoinId -ceq 'aria-ai' -and $Chain -ceq 'binance-smart-chain' -and
    (Test-Path -LiteralPath $settings)) {
    $configuration = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $shadow = $configuration.DataFoundation
    if ($shadow.AriaShadowEnabled -eq $true -and $shadow.StartedAt) {
        $start = [DateTimeOffset]::Parse($shadow.StartedAt)
        $now = [DateTimeOffset]::UtcNow
        $end = $start.AddDays(7)
        $times = @($ordered | ForEach-Object { [DateTimeOffset]::Parse($_.observedAt) })
        $closed = 0
        $missed = @()
        for ($slot = $start; $slot -lt $end; $slot = $slot.AddHours(6)) {
            $boundary = $slot.AddHours(6)
            if ($slot -lt $Since -or $now -lt $boundary.AddMinutes(15)) { continue }
            $closed++
            if (-not ($times | Where-Object { $_ -ge $slot -and $_ -lt $boundary } |
                Select-Object -First 1)) { $missed += $slot }
        }
        $continuity = [ordered]@{
            startedAt = $start
            dueAt = $end
            state = if ($now -lt $end.AddMinutes(15)) { 'Collecting' } else { 'WindowElapsed' }
            completedSlots = $closed
            missedSlots = $missed
            scheduledSamples = @($ordered | Where-Object { $_.origin -eq "Scheduled" }).Count
            manualSamples = @($ordered | Where-Object { $_.origin -eq "Manual" }).Count
            unknownOriginSamples = @($ordered | Where-Object { $_.origin -notin "Scheduled", "Manual" }).Count
        }
    }
}
[ordered]@{
    coinId = $CoinId
    chain = $Chain
    observations = $ordered.Count
    continuity = $continuity
    invalidLines = $invalid
    firstObservedAt = ($ordered | Select-Object -First 1).observedAt
    lastObservedAt = $latest.observedAt
    comparableMarketCapital = $comparable.Count
    conflicts = $conflicts.Count
    conflictPercent = if ($comparable.Count) { [Math]::Round(100 * $conflicts.Count / $comparable.Count, 2) }
    latestMarketCapital = if ($latest) {
        @{ status = $latest.marketCapital.status; geckoUsd = $latest.gecko.Value.MarketCap
            cmcUsd = $latest.cmc.Value.MarketCap; differenceRatio = $latest.marketCapital.difference
            geckoCirculatingSupply = $latest.gecko.Value.CirculatingSupply; cmcCirculatingSupply = $latest.cmc.Value.CirculatingSupply
            geckoSourceTime = $latest.gecko.SourceTime; cmcSourceTime = $latest.cmc.SourceTime }
    }
    latestPrimaryLiquidityUsd = $latest.primaryLiquidityUsd
    sources = @($sources)
    quotaLedgerState = $quotaState
    localDailyReservations = $quota
} | ConvertTo-Json -Depth 8
