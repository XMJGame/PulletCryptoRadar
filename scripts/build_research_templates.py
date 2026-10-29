"""Public, checksum-verified USD-M archives; descriptive templates, NOT a trading backtest.
Standard library only. Cache is isolated from the live application's databases/budgets.
Run: python scripts/build_research_templates.py --root <project> [--offline]
"""
from __future__ import annotations
import argparse, csv, hashlib, io, json, math, statistics, time, urllib.request, urllib.error, zipfile
from datetime import datetime, timezone, timedelta
from decimal import Decimal, InvalidOperation
from pathlib import Path

UTC = timezone.utc
BASE = 'https://data.binance.vision/data/futures/um/'
D = Decimal

def iso(stamp): return datetime.fromtimestamp(stamp, UTC).isoformat()
def pct(a, b): return round(float((a / b - 1) * 100), 4) if b > 0 else None
def months(start, end):
    y, m = map(int, start.split('-'))
    while f'{y:04}-{m:02}' <= end:
        yield f'{y:04}-{m:02}'
        y, m = (y+1, 1) if m == 12 else (y, m+1)

class Archive:
    def __init__(self, root, offline=False):
        self.root = root; self.offline = offline; self.entries = {}; self.stopped = False
        self.manifest_path = root / 'manifest.json'
        root.mkdir(parents=True, exist_ok=True)
        if self.manifest_path.exists(): self.entries = json.loads(self.manifest_path.read_text('utf-8'))
    def save(self): self.manifest_path.write_text(json.dumps(self.entries, ensure_ascii=False, indent=2), 'utf-8')
    def request(self, url):
        if self.stopped: raise RuntimeError('Archive host cooling down')
        for attempt in range(2):
            time.sleep(.12)
            try:
                with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent':'PulletCryptoRadar-Research/1.0'}), timeout=25) as response:
                    data = response.read(50_000_001)
                    if len(data) > 50_000_000: raise ValueError('Archive too large')
                    return data
            except urllib.error.HTTPError as e:
                if e.code in (418, 429): self.stopped = True; raise RuntimeError(f'Archive cooldown HTTP {e.code}') from None
                if e.code < 500 or attempt: raise
            except (TimeoutError, urllib.error.URLError):
                if attempt: raise
            time.sleep(1)
    def get(self, path):
        local = self.root / path
        entry = self.entries.get(path)
        if entry and entry.get('status') == 'verified' and local.exists():
            data = local.read_bytes()
            if hashlib.sha256(data).hexdigest() != entry['sha256']: raise ValueError('Cached checksum mismatch')
            return data
        if entry and entry.get('status') == 'missing': return None
        if self.offline: return None
        try:
            data = self.request(BASE + path)
            checksum = self.request(BASE + path + '.CHECKSUM').decode('ascii').split()[0].lower()
            if len(checksum) != 64 or hashlib.sha256(data).hexdigest() != checksum: raise ValueError('Source checksum mismatch')
            local.parent.mkdir(parents=True, exist_ok=True); local.write_bytes(data)
            self.entries[path] = {'status':'verified','sha256':checksum,'bytes':len(data),'url':BASE+path,'retrievedAt':datetime.now(UTC).isoformat()}
            return data
        except urllib.error.HTTPError as e:
            self.entries[path] = {'status':'missing' if e.code == 404 else 'unavailable','http':e.code,'url':BASE+path}
        except Exception as e:
            self.entries[path] = {'status':'invalid' if isinstance(e,ValueError) else 'unavailable','errorType':type(e).__name__,'url':BASE+path}
        finally: self.save()
        return None

def csv_rows(data):
    if data is None: return []
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = [n for n in z.namelist() if n.endswith('.csv')]
        if len(names) != 1 or z.getinfo(names[0]).file_size > 80_000_000: raise ValueError('Unexpected archive contents')
        return list(csv.reader(io.StringIO(z.read(names[0]).decode('utf-8-sig'))))

def parse_klines(data, asof):
    result = []
    for r in csv_rows(data):
        if not r or r[0] == 'open_time': continue
        stamp = int(r[0])//1000
        if stamp + 3600 > asof: continue
        o,h,l,c,v,q,tb = [D(r[i]) for i in (1,2,3,4,5,7,9)]
        if not all(x.is_finite() for x in (o,h,l,c,v,q,tb)) or min(o,h,l,c) <= 0 or min(v,q,tb) < 0 or tb > v or h < max(o,l,c) or l > min(o,h,c): raise ValueError('Invalid OHLCV')
        if int(r[6]) != int(r[0])+3_600_000-1 or stamp < 1483228800 or stamp % 3600: raise ValueError('Invalid hourly timestamp')
        result.append({'t':stamp,'o':o,'h':h,'l':l,'c':c,'v':v,'q':q,'tb':tb})
    return result

def combine(parts):
    rows = {}
    for part in parts:
        for row in part:
            if row['t'] in rows and rows[row['t']] != row: raise ValueError('Conflicting duplicate candle')
            rows[row['t']] = row
    return sorted(rows.values(), key=lambda r:r['t'])

def parse_metrics(data, symbol):
    rows = csv_rows(data)
    if not rows: return []
    names = rows[0]; result=[]
    for raw in rows[1:]:
        r=dict(zip(names,raw))
        if r['symbol'] != symbol: raise ValueError('Metric contract mismatch')
        t=int(datetime.strptime(r['create_time'],'%Y-%m-%d %H:%M:%S').replace(tzinfo=UTC).timestamp())
        qty=D(r['sum_open_interest']); value=D(r['sum_open_interest_value'])
        if not qty.is_finite() or not value.is_finite() or min(qty,value)<0: raise ValueError('Invalid OI')
        result.append({'t':t,'qty':qty,'usd':value})
    return result

def parse_funding(data):
    rows=csv_rows(data)
    if not rows: return []
    result=[]
    for raw in rows[1:]:
        r=dict(zip(rows[0],raw)); rate=D(r['last_funding_rate']); hours=int(r['funding_interval_hours'])
        if not rate.is_finite() or hours<1 or hours>24: raise ValueError('Invalid funding record')
        result.append({'t':int(r['calc_time'])//1000,'rate':rate,'hours':hours})
    return result

def triggers(rows):
    """Feature at bar close, labels from subsequent 24 closed hours. No same-bar fill."""
    output=[]; last=-9999
    for i in range(72,len(rows)-24):
        if i-last < 24 or rows[i+24]['t']-rows[i-72]['t'] != 96*3600: continue
        prior=rows[i-72:i]; high=max(r['h'] for r in prior); median=statistics.median(r['v'] for r in rows[i-24:i])
        if not(rows[i]['c'] > high*D('1.002') and median>0 and rows[i]['v']>=median*D('1.5')): continue
        last=i; future=rows[i+1:i+25]; entry=future[0]['o']
        mfe=pct(max(r['h'] for r in future),entry); mae=pct(min(r['l'] for r in future),entry)
        failed=mfe<5 and future[-1]['c']<high
        # All support candidates are confirmed before the signal closes (2 right-hand bars).
        pivots=[j for j in range(i-70,i-1) if rows[j]['l']<min(rows[j-2]['l'],rows[j-1]['l']) and rows[j]['l']<=min(rows[j+1]['l'],rows[j+2]['l'])]
        support=next((rows[j]['l'] for j in reversed(pivots) if rows[j]['l']<rows[i]['c']),None)
        output.append({'time':iso(rows[i]['t']+3600),'volumeRatio':round(float(rows[i]['v']/median),3),'prior72hHigh':str(high),'signalClose':str(rows[i]['c']),
            'nextHourOpenReference':str(entry),'confirmedSupportCandidate':str(support) if support else None,'supportDistancePercent':-pct(support,entry) if support else None,
            'forward24hMaxHighPercent':mfe,'forward24hMinLowPercent':mae,'forward24hClosePercent':pct(future[-1]['c'],entry),'failedBreakoutLabel':failed,
            'note':'Research trigger, not recommendation or filled trade; forward fields are outcome labels; one trigger per 24h per contract/window.'})
    return output

def choose_peak(config, rows):
    if config['role'] in ('crash-context','delisting-context'):
        crash=min(range(len(rows)),key=lambda i:rows[i]['c']/rows[i]['o'])
        return max((i for i in range(crash+1) if rows[i]['t']>=rows[crash]['t']-7*24*3600),key=lambda i:rows[i]['h'])
    return max(range(len(rows)),key=lambda i:rows[i]['h'])

def event_time(config, rows):
    return min(rows,key=lambda r:r['c']/r['o'])['t'] if config['role'] in ('crash-context','delisting-context') else None

def analyze_case(config, rows, funding, metrics, mark, status):
    if len(rows)<100: return {'symbol':config['symbol'],'key':config['symbol']+'-'+config['from'],'role':config['role'],'status':'insufficient','hours':len(rows),'files':status}
    peak_i=choose_peak(config,rows); peak=rows[peak_i]
    shock=event_time(config,rows); chart_start=min(peak['t'],shock or peak['t'])-72*3600; chart_end=max(peak['t'],shock or peak['t'])+73*3600
    start_i=min((i for i in range(peak_i+1) if rows[i]['t']>=peak['t']-14*24*3600),key=lambda i:rows[i]['l']); start=rows[start_i]
    after=[r for r in rows[peak_i+1:] if r['t']<=peak['t']+14*24*3600]; post_end=after[-1]['t']+3600 if after else peak['t']+3600
    high=peak['h']; low=min((r['l'] for r in after),default=None); burst=pct(high,start['c']); dd=pct(low,high) if low is not None else None
    # Within-bar extremes have unknown sequencing; do not claim a tradable high-to-low return.
    widest=max(rows,key=lambda r:float((r['h']-r['l'])/r['o']))
    funding_window=[r for r in funding if start['t']<=r['t']<=peak['t']+3600]
    oi_before=[r for r in metrics if r['t']<=peak['t']-24*3600]; oi_at=[r for r in metrics if r['t']<=peak['t']]
    oi_change=None; oi_span=None
    if oi_before and oi_at:
        a=max(oi_before,key=lambda r:r['t']); b=max(oi_at,key=lambda r:r['t'])
        if peak['t']-24*3600-a['t']<=900 and peak['t']-b['t']<=900 and a['qty']>0:
            oi_change=pct(b['qty'],a['qty']); oi_span=[iso(a['t']),iso(b['t'])]
    tags=[]
    if (widest['h']-widest['l'])/widest['o']>=D('.3'): tags.append('单小时大幅摆动 / 尖峰风险')
    if burst is not None and burst>=100: tags.append('阶段性大幅拉升')
    if dd is not None and dd<=-50: tags.append('高位后深度回撤')
    if oi_change is not None: tags.append('峰值前24h OI数量增加' if oi_change>0 else '峰值前24h OI数量减少')
    if funding_window and sum(r['rate'] for r in funding_window)<0: tags.append('选定上升区间累计资金费为负')
    if not tags: tags=['普通波动 / 对照']
    detected=triggers(rows); failed=[t for t in detected if t['failedBreakoutLabel']]
    focused=[r for r in rows if chart_start<=r['t']<chart_end]
    mark_focused=[r for r in mark if chart_start<=r['t']<chart_end]
    return {'key':config['symbol']+'-'+config['from'],'symbol':config['symbol'],'venue':'Binance USD-M perpetual','role':config['role'],'status':'observed',
        'period':[iso(rows[0]['t']),iso(rows[-1]['t']+3600)],'hours':len(rows),'gaps':sum(max(0,(b['t']-a['t'])//3600-1) for a,b in zip(rows,rows[1:])),
        'start':iso(start['t']),'peak':iso(peak['t']),'postEnd':iso(post_end),'startClose':str(start['c']),'peakHigh':str(high),'peakClose':str(peak['c']),
        'closeReturnToPeakHourPercent':pct(peak['c'],start['c']),'anchorMethod':'peak in 7 days before largest hourly close/open loss' if config['role'] in ('crash-context','delisting-context') else 'maximum high within predefined window',
        'eventTime':iso(shock) if shock is not None else None,'chartStart':iso(chart_start),'chartEnd':iso(chart_end),
        'riseToPeakHighPercent':burst,'postPeakMinLowPercent':dd,'postPeak14dAvailable':len(after)==336 and after[-1]['t']-peak['t']==336*3600,
        'unknownPeakBarSequence':True,'maxHourlyHighLowRangePercent':round(float((widest['h']-widest['l'])/widest['o']*100),4),'maxRangeHour':iso(widest['t']),
        'oiQuantityChange24hAtPeakPercent':oi_change,'oiComparisonTimes':oi_span,'oiRows':len(metrics),
        'actualFundingRows':len(funding),'fundingRiseRows':len(funding_window),'sumFundingRisePercent':round(float(sum(r['rate'] for r in funding_window)*100),5) if funding_window else None,
        'fundingIntervals':sorted(set(r['hours'] for r in funding_window)),'markHours':len(mark),'tags':tags,
        'breakoutTriggers':len(detected),'failedBreakoutLabels':len(failed),'failureExamples':failed[:3],
        'coverage':{'price':'observed','volume':'observed','takerVolume':'observed','oi':'event-window only' if metrics else 'missing','funding':'archived settlements' if funding else 'missing','mark':'focused months' if mark else 'missing','historicHolders':'missing','verifiedProjectWallets':'missing','newsCausation':'not verified'},
        'timeline':[{'time':iso(r['t']),'open':str(r['o']),'high':str(r['h']),'low':str(r['l']),'close':str(r['c']),'quoteVolume':str(r['q']),'takerBuyBaseShare':round(float(r['tb']/r['v']),5) if r['v']>0 else None} for r in focused],
        'markTimeline':[{'time':iso(r['t']),'low':str(r['l']),'high':str(r['h']),'close':str(r['c'])} for r in mark_focused],
        'oiTimeline':[{'time':iso(r['t']),'quantity':str(r['qty']),'notionalUsd':str(r['usd'])} for r in metrics],
        'fundingTimeline':[{'time':iso(r['t']),'rate':str(r['rate']),'intervalHours':r['hours']} for r in funding if chart_start<=r['t']<chart_end],
        'note':'Trough and peak selected retrospectively; descriptive pattern, not executable return or evidence of manipulation. No historical holder data fabricated.', 'files':status}

def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[1]); ap.add_argument('--offline',action='store_true'); args=ap.parse_args()
    root=args.root.resolve(); out=root/'docs/research/templates'; out.mkdir(parents=True,exist_ok=True)
    universe=json.loads((root/'docs/research/template-universe.json').read_text('utf-8-sig'))
    archive=Archive(root/'artifacts/research-archive',args.offline); asof=int(datetime.now(UTC).timestamp()); results=[]; market_rows={}
    for config in universe['cases']:
        symbol=config['symbol']; parts=[]; funding=[]; statuses=[]; mark=[]
        print('Collect',symbol,config['from'],config['to'],flush=True)
        try:
            for month in months(config['from'],config['to']):
                path=f'monthly/klines/{symbol}/1h/{symbol}-1h-{month}.zip'; data=archive.get(path); statuses.append({'path':path,'status':archive.entries.get(path,{}).get('status','offline-missing')}); parts.append(parse_klines(data,asof))
                fp=f'monthly/fundingRate/{symbol}/{symbol}-fundingRate-{month}.zip'; funding.extend(parse_funding(archive.get(fp)))
            rows=combine(parts)
            if config['role']=='market-control': market_rows[symbol]=combine([market_rows.get(symbol,[]),rows])
            if len(rows)<100: results.append(analyze_case(config,rows,funding,[],[],statuses));continue
            peak=rows[choose_peak(config,rows)]; date=datetime.fromtimestamp(peak['t'],UTC).replace(hour=0,minute=0,second=0,microsecond=0)
            metrics=[]
            dates={date}
            shock=event_time(config,rows)
            if shock is not None: dates.add(datetime.fromtimestamp(shock,UTC).replace(hour=0,minute=0,second=0,microsecond=0))
            metric_days=sorted({(d+timedelta(days=offset)).strftime('%Y-%m-%d') for d in dates for offset in range(-2,3)})
            for day in metric_days:
                path=f'daily/metrics/{symbol}/{symbol}-metrics-{day}.zip'
                metrics.extend(parse_metrics(archive.get(path),symbol))
            metrics=sorted(metrics,key=lambda r:r['t'])
            if config.get('mark'):
                target_months=sorted({(d+timedelta(hours=hours)).strftime('%Y-%m') for d in dates for hours in (-72,0,72)})
                for month in target_months:
                    path=f'monthly/markPriceKlines/{symbol}/1h/{symbol}-1h-{month}.zip'; mark.extend(parse_klines(archive.get(path),asof))
            result=analyze_case(config,rows,funding,metrics,combine([mark]),statuses);results.append(result)
            print('Done',symbol,'hours',len(rows),'OI',len(metrics),'tags',','.join(result.get('tags',[])),flush=True)
        except Exception as e:
            results.append({'key':symbol+'-'+config['from'],'symbol':symbol,'role':config['role'],'status':'invalid-or-unavailable','errorType':type(e).__name__,'note':str(e)[:100]})
            print('Unavailable',symbol,type(e).__name__,flush=True)
        (out/'TemplateLibrary.json').write_text(json.dumps({'version':universe['version'],'generatedAt':iso(asof),'selection':universe['selection'],'cases':results},ensure_ascii=False,indent=2), 'utf-8')
        if archive.stopped: break
    archive.save()
    (out/'ArchiveManifest.json').write_text(json.dumps(archive.entries,ensure_ascii=False,indent=2),'utf-8')
    for result in results:
        if result.get('status')!='observed': continue
        result['marketComparisons']=[]
        for symbol, series in market_rows.items():
            lookup={iso(r['t']):r for r in series}; a=lookup.get(result['start']); b=lookup.get(result['peak'])
            if a and b:
                move=pct(b['c'],a['c']); result['marketComparisons'].append({'symbol':symbol,'sameHours':True,'closeReturnPercent':move,'relativeCloseMovePp':round(result['closeReturnToPeakHourPercent']-move,4)})
    cases_dir=out/'cases'; cases_dir.mkdir(exist_ok=True)
    for result in results: (cases_dir/(result['key']+'.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2),'utf-8')
    summary={'version':universe['version'],'generatedAt':iso(asof),'selection':universe['selection'],'cases':results,'archive':{'verifiedFiles':sum(e['status']=='verified' for e in archive.entries.values()),'missingFiles':sum(e['status']=='missing' for e in archive.entries.values()),'unavailableFiles':sum(e['status'] not in ('verified','missing') for e in archive.entries.values()),'manifest':'artifacts/research-archive/manifest.json'},'automaticTradingEnabled':False}
    (out/'TemplateLibrary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),'utf-8')
    flat=[{k:v for k,v in c.items() if k not in ('timeline','markTimeline','oiTimeline','fundingTimeline','files')} for c in results]
    (out/'Summary.json').write_text(json.dumps({**summary,'cases':flat},ensure_ascii=False,indent=2),'utf-8')
    print('Saved',out/'TemplateLibrary.json',flush=True)

if __name__=='__main__': main()
