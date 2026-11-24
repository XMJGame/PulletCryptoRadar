param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$database=Join-Path $project 'artifacts\win-x64\Data\PulletCryptoRadar.db'
$sqlite=(Get-Command sqlite3.exe -ErrorAction Stop).Source
$query=@'
SELECT json_extract(i.Payload,'$.BinanceSymbol') Symbol,e.At,e.Payload Evaluation,
 (SELECT f.Payload FROM V2Feature f WHERE f.AssetId=e.AssetId AND f.AsOf<=e.At ORDER BY f.AsOf DESC LIMIT 1) Feature,
 (SELECT json_object('Evidence',json_object('ReceivedTime',json_extract(r.Payload,'$.Evidence.ReceivedTime')),
    'Candles',json_array(json_extract(r.Payload,'$.Candles[#-1]')))
    FROM V2RawEvidence r WHERE r.Id=json_extract(e.Payload,'$.CandleEvidenceId')) Candles
FROM V2EntryEvaluation e JOIN V2Identity i USING(AssetId)
WHERE json_extract(e.Payload,'$.Setup.SetupId') IS NOT NULL ORDER BY e.At,e.AssetId;
'@
$raw=& $sqlite -readonly -json $database $query
if($LASTEXITCODE -ne 0){throw '只读回放查询失败'}
$records=($raw -join [Environment]::NewLine)|ConvertFrom-Json
$zone=[TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
$contracts=[IO.File]::ReadAllText((Join-Path $project 'src\PulletCryptoRadar.Core\Contracts.cs'))
$enumMatch=[regex]::Match($contracts,'public enum Metric\s*\{([^}]+)\}')
if(-not$enumMatch.Success){throw '找不到保存资料的指标定义，停止回放'}
$metricNames=@($enumMatch.Groups[1].Value.Split(',')|ForEach-Object {$_.Trim()})
function Local-Time($value){if($null-eq$value){return '未知'};[TimeZoneInfo]::ConvertTime([DateTimeOffset]::Parse([string]$value),$zone).ToString('yyyy-MM-dd HH:mm:ss')}
$rows=New-Object 'System.Collections.Generic.List[object]'
foreach($record in $records){
    $evaluation=$record.Evaluation|ConvertFrom-Json
    $feature=if($record.Feature){$record.Feature|ConvertFrom-Json}else{$null}
    $candles=if($record.Candles){$record.Candles|ConvertFrom-Json}else{$null}
    $last=if($candles){@($candles.Candles)[-1]}else{$null}
    $setup=$evaluation.Setup
    $inputValid=$null-ne$last -and $null-ne$feature -and $feature.AssetId-eq$evaluation.AssetId -and
        [DateTimeOffset]::Parse($feature.AsOf)-le[DateTimeOffset]::Parse($evaluation.AsOf) -and
        [DateTimeOffset]::Parse($candles.Evidence.ReceivedTime)-le[DateTimeOffset]::Parse($evaluation.AsOf)
    $freshBar=$inputValid -and [DateTimeOffset]::Parse($last.ClosedAt)-gt[DateTimeOffset]::Parse($setup.ArmedBarClosedAt)
    $sign=if($evaluation.Side-eq0){[decimal]1}else{[decimal]-1}
    $geometry=$false
    if($freshBar){
        $close=[decimal]$last.Close;$open=[decimal]$last.Open;$level=[decimal]$setup.Level;$atr=[decimal]$setup.AtrAtArm
        if($evaluation.Strategy-eq0){$geometry=$sign*($close-[decimal]$setup.Trigger)-gt0}
        else{
            $touch=if($evaluation.Side-eq0){[decimal]$last.Low-le$level+[decimal].25*$atr}else{[decimal]$last.High-ge$level-[decimal].25*$atr}
            $geometry=$touch -and $sign*($close-$level)-gt0 -and $sign*($close-$open)-gt0 -and $sign*($close-$level)-le$atr
        }
    }
    $values=@{}
    foreach($metric in @($feature.Metrics)){
        if($null-eq$metric){continue}
        $index=[int]$metric.Metric
        if($index-lt0-or$index-ge$metricNames.Count){throw '未知指标编号，停止回放'}
        $values[$metricNames[$index]]=$metric.Value
    }
    $volume=$values['VolumeRatioH1'];$buyShare=$values['TakerBuyShareH1'];$oi=$values['OiQuantityChange1h']
    $directional=if($null-ne$buyShare){if($evaluation.Side-eq0){[decimal]$buyShare}else{[decimal]1-[decimal]$buyShare}}else{$null}
    $participation=$null-ne$volume -and $null-ne$directional -and $null-ne$oi -and
        [decimal]$volume-ge[decimal]1.2 -and $directional-ge[decimal].52 -and [decimal]$oi-ge1
    $rows.Add([PSCustomObject]@{symbol=$record.Symbol;setupId=$setup.SetupId;at=$evaluation.AsOf;
        status=$evaluation.Status;reasons=@($evaluation.Reasons|Where-Object {$_-notin@('ExecutionAdmissionPending','UpcomingFundingScheduleUnverified','PortfolioRiskNotConnected')});
        closedBar=$last.ClosedAt;inputAvailable=$inputValid;newClosedBar=$freshBar;geometryConfirmed=$geometry;
        participationMet=$participation;volumeRatio=$volume;directionalTakerShare=$directional;oiQuantityChange1h=$oi;
        featureAsOf=$feature.AsOf;candleEvidenceId=$evaluation.CandleEvidenceId;expiresAt=$setup.ExpiresAt})
}
$out=Join-Path $project 'artifacts\win-x64\Data\Reports'
[IO.Directory]::CreateDirectory($out)|Out-Null
$encoding=New-Object Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $out 'signal-replay.json'),(ConvertTo-Json -InputObject $rows.ToArray() -Depth 10),$encoding)
$lines=New-Object 'System.Collections.Generic.List[string]'
$lines.Add('# 已保存信号回放');$lines.Add('')
$lines.Add('只读关机前保存资料，未重新下载行情，未启动服务器，也不产生模拟交易。仅核对几何确认与参与度条件，不替代完整策略、盘口和成本回测。每一行保留原评估原因；缺历史输入时不猜值。')
foreach($group in @($rows|Group-Object setupId)){
    $items=@($group.Group);$first=$items[0];$last=$items[-1]
    $lines.Add('');$lines.Add('## '+$first.symbol+' / '+$group.Name);$lines.Add('')
    $lines.Add('首次记录：'+(Local-Time $first.at)+'；最后记录：'+(Local-Time $last.at)+'；原形态到期：'+(Local-Time $last.expiresAt))
    $lines.Add('保存评估次数：'+$items.Count+'；新闭合 K 线评估中几何满足次数：'+@($items|Where-Object {$_.newClosedBar-and$_.geometryConfirmed}).Count+'；几何与参与度同时满足次数：'+@($items|Where-Object {$_.newClosedBar-and$_.geometryConfirmed-and$_.participationMet}).Count)
    $lines.Add('');$lines.Add('| 首次出现此闭合小时/原因 | 闭合 K 线 | 保存原因 | 几何确认 | 量比 | 方向主动成交占比 | 1h OI % |');$lines.Add('|---|---|---|---|---:|---:|---:|')
    $seen=@{}
    foreach($item in $items){
        $key=([string]$item.closedBar)+'/'+($item.reasons-join',')
        if($seen.ContainsKey($key)){continue};$seen[$key]=$true
        $geometry=if(-not$item.inputAvailable){'输入缺失'}elseif(-not$item.newClosedBar){'尚无下一根'}elseif($item.geometryConfirmed){'满足'}else{'未满足'}
        $lines.Add('| '+(Local-Time $item.at)+' | '+(Local-Time $item.closedBar)+' | '+($item.reasons-join', ')+' | '+$geometry+' | '+$item.volumeRatio+' | '+$item.directionalTakerShare+' | '+$item.oiQuantityChange1h+' |')
    }
}
$lines.Add('');$lines.Add('说明：这些计数包含同一闭合小时的重复评估，不能当独立信号数。参与度为现有回测确认门槛（量比≥1.2、方向主动成交占比≥0.52、OI 增量≥1%）；时间/趋势/失效等其他条件仍以原评估记录为准。关机后的价格与盈亏未知，本报告不推断。')
[IO.File]::WriteAllLines((Join-Path $out 'signal-replay.md'),$lines,$encoding)
Write-Output (Join-Path $out 'signal-replay.md')
foreach($group in @($rows|Group-Object symbol)){
    [PSCustomObject]@{symbol=$group.Name;rows=$group.Count;inputMissing=@($group.Group|Where-Object {-not$_.inputAvailable}).Count;
        geometryAndParticipation=@($group.Group|Where-Object {$_.newClosedBar-and$_.geometryConfirmed-and$_.participationMet}).Count}
}
