param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$now = [DateTimeOffset]::UtcNow
$datasets = @(
    @{ Symbol='LAB_USDT'; Venue='Gate perpetual'; File='LAB_USDT-1d-raw.json'; Format='Gate' },
    @{ Symbol='MYXUSDT'; Venue='Binance perpetual'; File='MYXUSDT-Binance-1d-raw.json'; Format='Binance' },
    @{ Symbol='BTW_USDT'; Venue='Gate perpetual'; File='BTW_USDT-1d-raw.json'; Format='Gate' },
    @{ Symbol='APR_USDT'; Venue='Gate perpetual'; File='APR_USDT-1d-raw.json'; Format='Gate' }
)
$report = foreach ($dataset in $datasets) {
    $raw = Get-Content -LiteralPath (Join-Path $Root ('docs/research/'+$dataset.File)) -Raw | ConvertFrom-Json
    $rows = @(foreach ($row in $raw) {
        if ($dataset.Format -eq 'Gate') { $stamp=[long]$row.t; $open=[decimal]$row.o; $high=[decimal]$row.h; $low=[decimal]$row.l; $close=[decimal]$row.c; $volume=[decimal]$row.v }
        else { $stamp=[long]($row[0]/1000); $open=[decimal]$row[1]; $high=[decimal]$row[2]; $low=[decimal]$row[3]; $close=[decimal]$row[4]; $volume=[decimal]$row[5] }
        if ([DateTimeOffset]::FromUnixTimeSeconds($stamp+86400) -gt $now) { continue }
        if ($low -le 0 -or $open -le 0 -or $close -le 0 -or $high -lt [Math]::Max($open,$close) -or $low -gt [Math]::Min($open,$close) -or $volume -lt 0) { throw "Invalid closed OHLCV in $($dataset.Symbol)" }
        [pscustomobject]@{T=$stamp;O=$open;H=$high;L=$low;C=$close;V=$volume}
    }) | Sort-Object T
    if (@($rows.T | Select-Object -Unique).Count -ne $rows.Count) { throw 'Duplicate daily candles' }
    $cases = @(for ($i=30; $i -lt $rows.Count-7; $i++) {
        $before = @($rows[($i-20)..($i-1)])
        $sortedVolume = @($before.V | Sort-Object)
        $median = ($sortedVolume[9]+$sortedVolume[10])/2
        $window = @($rows[($i+1)..($i+7)])
        if ($rows[$i+7].T-$rows[$i].T -ne 7*86400 -or $rows[$i].T-$rows[$i-20].T -ne 20*86400) { continue }
        $maxHigh = ($window.H | Measure-Object -Maximum).Maximum
        $minLow = ($window.L | Measure-Object -Minimum).Minimum
        [pscustomobject]@{
            AtClose=[DateTimeOffset]::FromUnixTimeSeconds($rows[$i].T+86400).ToString('o')
            Close=$rows[$i].C
            Past20DayRangePercent=[Math]::Round((($before.H|Measure-Object -Maximum).Maximum/($before.L|Measure-Object -Minimum).Minimum-1)*100,3)
            KnownVolumeRatio=if($median -gt 0){[Math]::Round($rows[$i].V/$median,3)}else{$null}
            Forward7DayMaxHighPercent=[Math]::Round(($maxHigh/$rows[$i].C-1)*100,3)
            Forward7DayMinLowPercent=[Math]::Round(($minLow/$rows[$i].C-1)*100,3)
            LabelOnly='Forward fields are future labels, not executable returns or entry features'
        }
    })
    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($case in @($cases | Sort-Object Forward7DayMaxHighPercent -Descending)) {
        if ($selected.Count -ge 3) { break }
        $date=[DateTimeOffset]::Parse($case.AtClose)
        if (@($selected | Where-Object {[Math]::Abs(($date-[DateTimeOffset]::Parse($_.AtClose)).TotalDays) -lt 14}).Count -eq 0) { $selected.Add($case) }
    }
    [pscustomobject]@{Symbol=$dataset.Symbol;Venue=$dataset.Venue;ClosedDailyRows=$rows.Count;First=[DateTimeOffset]::FromUnixTimeSeconds($rows[0].T).ToString('o');LastClose=[DateTimeOffset]::FromUnixTimeSeconds($rows[-1].T+86400).ToString('o');RetrospectiveCases=$selected.ToArray();Status='Descriptive case selection only; no strategy validated'}
}
$result=[ordered]@{GeneratedAt=$now.ToString('o');Warning='Winners selected retrospectively; separate venues; daily bars cannot establish intraday fills, historic OI or wallet causation. Failed control cohort and walk-forward tests remain required.';Datasets=@($report)}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Root 'docs/research/CaseInventory.json') -Encoding utf8
$report | Select-Object Symbol,Venue,ClosedDailyRows,First,LastClose | ConvertTo-Json
