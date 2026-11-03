"""Offline 30-day tenfold labels from checksum-verified archives, never live fills."""
import hashlib
import json
import statistics
from datetime import datetime, timezone
from pathlib import Path
from build_research_templates import Archive, months, parse_klines, combine, iso

ROOT = Path(__file__).resolve().parents[1]
HOUR = 3600


def observations(symbol, rows):
    result = []
    for i in range(336, len(rows), 24):
        past, future = rows[i-336:i+1], rows[i+1:i+721]
        if any(b['t']-a['t'] != HOUR for a,b in zip(past,past[1:])):
            continue
        complete = len(future) == 720 and future[-1]['t']-rows[i]['t'] == 720*HOUR
        complete = complete and all(b['t']-a['t'] == HOUR for a,b in zip(future,future[1:]))
        base = float(rows[i]['c'])
        known72 = past[-73:-1]
        median = statistics.median(float(r['v']) for r in past[-25:-1])
        # Future columns are outcome labels only; no learning/selection uses these values.
        high_multiple = max((float(r['h'])/base for r in future), default=None)
        close_multiple = max((float(r['c'])/base for r in future), default=None)
        result.append({'symbol':symbol,'decisionAt':iso(rows[i]['t']+HOUR),
            'range72hPercent':(float(max(r['h'] for r in known72))/float(min(r['l'] for r in known72))-1)*100,
            'volumeRatio':float(rows[i]['v'])/median if median else None,
            'return24hPercent':(base/float(rows[i-24]['c'])-1)*100,
            'return7dPercent':(base/float(rows[i-168]['c'])-1)*100,
            'complete30d':complete,'future30dHighMultiple':high_multiple if complete else None,
            'future30dCloseMaxMultiple':close_multiple if complete else None,
            'future30dTerminalMultiple':float(future[-1]['c'])/base if complete else None,
            'future30dWorstMultiple':min(float(r['l'])/base for r in future) if complete else None,
            'highTouch10x':high_multiple>=10 if complete else None,
            'closedHour10x':close_multiple>=10 if complete else None})
    return result


def main():
    universe=json.loads((ROOT/'docs/research/template-universe.json').read_text('utf-8-sig'))
    archive=Archive(ROOT/'artifacts/research-archive',offline=True)
    cutoff=int(datetime.now(timezone.utc).timestamp())
    series={}
    for case in universe['cases']:
        symbol=case['symbol'];parts=[]
        for month in months(case['from'],case['to']):
            path=f'monthly/klines/{symbol}/1h/{symbol}-1h-{month}.zip'
            data=archive.get(path)
            if data is None and archive.entries.get(path,{}).get('status')!='missing':
                raise ValueError('Required verified archive missing: '+path)
            parts.append(parse_klines(data,cutoff))
        series[symbol]=combine([series.get(symbol,[]),combine(parts)])
    records=[r for symbol,rows in series.items() for r in observations(symbol,rows)]
    complete=[r for r in records if r['complete30d']]
    positives=[r for r in complete if r['closedHour10x']]
    cohorts=[]
    for label,subset in [('10x close',positives),('below 10x',[r for r in complete if not r['closedHour10x']])]:
        cohorts.append({'label':label,'windows':len(subset),'symbols':len({r['symbol'] for r in subset}),
            'medianRange72hPercent':statistics.median(r['range72hPercent'] for r in subset) if subset else None,
            'medianVolumeRatio':statistics.median(r['volumeRatio'] for r in subset if r['volumeRatio'] is not None) if subset else None,
            'compressed12PercentFraction':sum(r['range72hPercent']<=12 for r in subset)/len(subset) if subset else None})
    report={'version':'Tenfold-30d-Diagnostic-1','generatedAt':datetime.now(timezone.utc).isoformat(),
        'archiveManifestHash':hashlib.sha256((ROOT/'docs/research/templates/ArchiveManifest.json').read_bytes()).hexdigest(),
        'symbols':len(series),'hours':sum(len(r) for r in series.values()),'windows':len(records),'completeWindows':len(complete),
        'highTouch10xWindows':sum(r['highTouch10x'] for r in complete),'closedHour10xWindows':len(positives),
        'positiveSymbols':sorted({r['symbol'] for r in positives}),'cohorts':cohorts,
        'limitations':['Purposefully selected 17 contracts; overlapping daily windows are not independent.',
            'These are labels and descriptive comparisons, not an out-of-sample success probability.',
            'Historical market caps, wallet identities, concentration and unlock calendars are not available for causal testing.',
            'High touch and closed-hour labels differ; neither establishes continuous daily gains or achievable fills.',
            'Incomplete/gapped 30-day windows remain Unknown. Current market caps are never backfilled.'],
        'positiveExamples':sorted(positives,key=lambda r:r['future30dCloseMaxMultiple'],reverse=True)[:20]}
    output=ROOT/'docs/research/tenfold';output.mkdir(parents=True,exist_ok=True)
    (output/'Summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    (output/'Windows.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ['positiveExamples','limitations']},ensure_ascii=False))


if __name__=='__main__':main()
