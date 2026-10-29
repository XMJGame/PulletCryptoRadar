"""Offline, deterministic long-only execution research. Never calls an exchange or live paper account.
Reads checksum-verified full-window archives, NOT the hindsight-focused chart timelines.
All samples were already inspected; chronological subsets are diagnostics, not untouched holdouts.
"""
from __future__ import annotations
import bisect, hashlib, json, statistics
from collections import Counter, defaultdict
from datetime import datetime, timezone, timedelta
from decimal import Decimal
from pathlib import Path
from build_research_templates import Archive, months, parse_klines, parse_funding, parse_metrics, combine, iso

ROOT=Path(__file__).resolve().parents[1]
H=3600
DAY_ZONE=timezone(timedelta(hours=8))
RULES={
 'version':'Structure-R1','initialCash':1000,'leverage':10,'marginCap':100,'maxPositions':2,
 'riskFraction':.01,'riskCap':10,'dailyLossCap':30,'drawdownEntryPause':.15,
 'feePerSide':.0005,'slippagePerSide':.0005,'maintenanceRate':.005,'fundingRiskBuffer':.001,
 'atrBars':14,'stopAtrBuffer':.5,'liquidationSafetyAtr':.5,'trailAtr':3,'maximumHours':168,
 'pivotRightBars':2,'compressionHours':72,'compressionRangeMax':.12,'positionInRangeMin':.7,
 'accumulationVolumeRatio':1.2,'breakoutBuffer':.002,'breakoutVolumeRatio':1.5,
 'gainer24hMin':.08,'gainer24hMax':.4,'oi24hMin':0,'oi24hMax':.30,
 'oiPublicationLagSeconds':300,'oiMaximumAgeSeconds':900,'cooldownHours':24,
 'firstFraction':.3,'secondFraction':.3,'targetRiskFallback':[2,4],
 'windows':[{'name':'早期诊断','start':'2022-01-01','end':'2024-07-01'},
            {'name':'中段诊断','start':'2024-07-01','end':'2026-01-01'},
            {'name':'后段诊断（已看过）','start':'2026-01-01','end':'2027-01-01'}],
 'limitations':['所有样本事先已看过，没有真正独立留出；按日期分段仅诊断。',
 '手选17合约，不含当时全市场可交易全集；B仅涨幅候选代理，不是涨幅榜回测。',
 '历史OI只在事后选定事件附近采集，覆盖偏差本身与结果有关；不能据此认定多维策略有效。',
 '尚无历史地址、已核对钱包、消息因果、精确合约步长、订单簿与真实分层维持保证金。',
 '小时内先后未知时，先估算强平，再止损，再分批止盈；保守压力情景不代表真实成交路径。',
 '有历史标记价时用标记价极值做清算压力检查；缺失时用成交价代理，单独计数。',
 '费用/滑点/维持保证金是假设；资金费采用实际结算率乘当小时标记开盘价或成交开盘代理。',
 '期末平仓与缺口后首个开盘强制退出单独标记；缺口内价格与强平路径未知。']}

def numeric(rows):
    return [{k:float(v) if isinstance(v,Decimal) else v for k,v in r.items()} for r in rows]

def pivots(rows,i,kind):
    # i is the last CLOSED bar; a pivot's two right-hand bars must already be closed.
    out=[]
    for j in range(max(2,i-335),i-1):
        v=rows[j][kind]; others=[rows[k][kind] for k in (j-2,j-1,j+1,j+2)]
        if (v<min(others) if kind=='l' else v>max(others)): out.append((j,v))
    return out

def features(rows,i):
    if i<336 or rows[i]['t']-rows[i-336]['t']!=336*H: return None
    r=rows[i]; prior=rows[i-72:i]; low=min(x['l'] for x in prior); high=max(x['h'] for x in prior)
    atr=statistics.mean(max(rows[j]['h']-rows[j]['l'],abs(rows[j]['h']-rows[j-1]['c']),abs(rows[j]['l']-rows[j-1]['c'])) for j in range(i-13,i+1))
    vol=statistics.median(x['v'] for x in rows[i-24:i]); ratio=r['v']/vol if vol>0 else 0
    ret=r['c']/rows[i-24]['c']-1
    supports=pivots(rows,i,'l'); support=next((v for _,v in reversed(supports) if v<r['c']),None)
    if atr<=0 or support is None: return None
    pressures=sorted({v for _,v in pivots(rows,i,'h') if v>r['c']})
    a=high/low-1<=RULES['compressionRangeMax'] and low+(high-low)*RULES['positionInRangeMin']<=r['c']<=high and 0<=ret<=.12 and ratio>=RULES['accumulationVolumeRatio']
    b=RULES['gainer24hMin']<=ret<=RULES['gainer24hMax'] and r['c']>high*(1+RULES['breakoutBuffer']) and ratio>=RULES['breakoutVolumeRatio'] and r['h']>r['l'] and (r['c']-r['l'])/(r['h']-r['l'])>=.7
    return {'decision':r['t']+H,'a':a,'b':b,'atr':atr,'stop':support-RULES['stopAtrBuffer']*atr,
            'support':support,'pressures':pressures,'volumeRatio':ratio,'return24h':ret,'close':r['c']}

def oi_feature(metrics, decision):
    ts=[r['t'] for r in metrics]; values=[]
    for target in (decision-RULES['oiPublicationLagSeconds'],decision-RULES['oiPublicationLagSeconds']-24*H):
        j=bisect.bisect_right(ts,target)-1
        if j<0 or target-ts[j]>RULES['oiMaximumAgeSeconds']: return None
        values.append(metrics[j])
    if values[1]['qty']<=0: return None
    return {'change':values[0]['qty']/values[1]['qty']-1,'observedAt':iso(values[0]['t']),
            'laggedAvailableAt':iso(values[0]['t']+RULES['oiPublicationLagSeconds'])}

def load_data():
    universe=json.loads((ROOT/'docs/research/template-universe.json').read_text('utf-8-sig'))
    archive=Archive(ROOT/'artifacts/research-archive',offline=True); series={}; evidence={}; meta={}; marks={}; funding={}; ends=defaultdict(list)
    for c in universe['cases']:
        symbol=c['symbol']; parts=[]; fs=[]
        y,m=map(int,c['to'].split('-'));y,m=(y+1,1) if m==12 else (y,m+1)
        ends[symbol].append(int(datetime(y,m,1,tzinfo=timezone.utc).timestamp()))
        for month in months(c['from'],c['to']):
            path=f'monthly/klines/{symbol}/1h/{symbol}-1h-{month}.zip'
            data=archive.get(path)
            if data is None and archive.entries.get(path,{}).get('status')!='missing': raise ValueError('Uncached required price archive: '+path)
            parts.append(parse_klines(data,2_000_000_000))
            fs.extend(parse_funding(archive.get(f'monthly/fundingRate/{symbol}/{symbol}-fundingRate-{month}.zip')))
        series[symbol]=combine([series.get(symbol,[]),combine(parts)])
        funding[symbol]=combine([funding.get(symbol,[]),fs]); meta[symbol]={'symbol':symbol,'role':c['role']}
    # Only use already verified event-window OI and mark archives; no downloads or fill-forward.
    for path,entry in archive.entries.items():
        if entry['status']!='verified': continue
        if path.startswith('daily/metrics/'):
            symbol=path.split('/')[2]; evidence.setdefault(symbol,[]).extend(parse_metrics(archive.get(path),symbol))
        elif path.startswith('monthly/markPriceKlines/'):
            symbol=path.split('/')[2]; marks.setdefault(symbol,[]).extend(parse_klines(archive.get(path),2_000_000_000))
    prepared={}
    for symbol,raw in series.items():
        rows=numeric(raw); metrics=numeric(combine([evidence.get(symbol,[])])); fs=numeric(funding[symbol]); fsmap={r['t']:r for r in fs}
        mm={r['t']:r for r in numeric(combine([marks.get(symbol,[])]))}; ff={}
        for i in range(336,len(rows)):
            f=features(rows,i)
            if f and (f['a'] or f['b']): f['oi']=oi_feature(metrics,f['decision']);ff[f['decision']]=f
        prepared[symbol]={'rows':rows,'lookup':{r['t']:r for r in rows},'features':ff,'funding':fsmap,
            'fundingTimes':sorted(fsmap),'marks':mm,'role':meta[symbol]['role'],'periodEnds':sorted(ends[symbol])}
    return prepared,hashlib.sha256((ROOT/'docs/research/templates/ArchiveManifest.json').read_bytes()).hexdigest()

def plan(f, entry_open, equity, cash, cost_multiplier=1):
    entry=entry_open*(1+RULES['slippagePerSide']*cost_multiplier); stop=f['stop']; atr=f['atr']
    liq=entry*(1-1/RULES['leverage'])/(1-RULES['maintenanceRate'])
    if not 0<stop<entry or stop<=liq+RULES['liquidationSafetyAtr']*atr: return None,'stop_liquidation_conflict'
    risk=min(RULES['riskCap'],equity*RULES['riskFraction']); distance=entry-stop
    fee=RULES['feePerSide']*cost_multiplier; slip=RULES['slippagePerSide']*cost_multiplier
    qty=min(RULES['marginCap']*RULES['leverage']/entry,risk/(distance+entry*(2*fee+slip+RULES['fundingRiskBuffer'])),cash/(entry/RULES['leverage']+entry*fee))
    if qty*entry<5: return None,'insufficient_cash_or_risk'
    pressure=[v for v in f['pressures'] if v>=entry+distance]
    t1=pressure[0] if pressure else entry+RULES['targetRiskFallback'][0]*distance
    t2=next((v for v in pressure if v>=t1+distance),entry+max(4*distance,t1-entry+distance))
    return {'entry':entry,'stop':stop,'qty':qty,'margin':qty*entry/RULES['leverage'],'fee':qty*entry*fee,
            'targets':[t1,t2],'targetBasis':['confirmed_pressure' if pressure else '2R_risk_release',
            'confirmed_pressure' if any(v>=t1+distance for v in pressure) else 'risk_projection'],
            'estimatedLossWithBuffer':qty*(distance+entry*(2*fee+slip+RULES['fundingRiskBuffer']))},None

def liquidation_price(p):
    return max(0,(p['qty']*p['entry']-p['margin'])/(p['qty']*(1-RULES['maintenanceRate'])))

def settle(p, qty, raw_price, reason, t, cost_multiplier=1, liquidated=False):
    qty=min(qty,p['qty']); part=qty/p['qty']; margin=p['margin']*part
    price=raw_price*(1-RULES['slippagePerSide']*cost_multiplier)
    gross=qty*(price-p['entry']); fee=qty*price*RULES['feePerSide']*cost_multiplier
    expected=margin+gross-fee; returned=0 if liquidated else max(0,expected)
    adjustment=returned-expected
    p['gross']+=gross;p['fees']+=fee;p['adjustment']+=adjustment;p['returns']+=returned
    p['margin']-=margin;p['qty']-=qty
    p['exits'].append({'time':iso(t),'reason':reason,'price':price,'quantity':qty,'returned':returned})
    return returned

def step(p,bar,mark,t,cost_multiplier=1):
    """Worst-path ordering, protect first; no stop raised using this bar before exits are tested."""
    low=mark['l'] if mark else bar['l']; liq=liquidation_price(p)
    if low<=liq:
        return settle(p,p['qty'],min(liq,bar['o']),'liquidation_stress',t,cost_multiplier,True)
    if bar['l']<=p['stop']:
        return settle(p,p['qty'],min(p['stop'],bar['o']),'structure_stop',t,cost_multiplier)
    returned=0
    for j,target in enumerate(p['targets']):
        if not p['hit'][j] and bar['h']>=target:
            p['hit'][j]=True
            returned+=settle(p,p['initialQty']*(RULES['firstFraction'] if j==0 else RULES['secondFraction']),target,f'partial_{j+1}',t,cost_multiplier)
    return returned

def replay(data,strategy,require_oi,start,end,cost_multiplier=1):
    # Each era/variant owns a separate virtual 1000-USDT portfolio. Never sum their balances.
    times=sorted({r['t'] for d in data.values() for r in d['rows'] if start<=r['t']<end}
        |{cut for d in data.values() for cut in d.get('periodEnds',[]) if start<=cut<end})
    cash=float(RULES['initialCash']); positions={}; trades=[]; skipped=Counter(); checks=Counter(); curve=[]; cooldown={}
    realized=defaultdict(float); peak=cash; maxdd=0; paused=False; maxmargin=0
    lastclose={}; fee=RULES['feePerSide']*cost_multiplier
    def finish(symbol,p,t):
        net=p['returns']-p['initialMargin']-p['entryFee']
        if abs(net-(p['gross']-p['fees']-p['funding']+p['adjustment']))>1e-6: raise AssertionError('Trade accounting mismatch')
        p['net']=net;p['closedAt']=iso(t);p['durationHours']=(t-p['entryTime'])/H
        trades.append({k:v for k,v in p.items() if k not in ('lastTime','lastFunding','qty','margin','peak','atr','hit')})
        realized[datetime.fromtimestamp(t,DAY_ZONE).strftime('%Y-%m-%d')]+=net
        cooldown[symbol]=t+RULES['cooldownHours']*H
        del positions[symbol]
    for t in times:
        bars={s:d['lookup'][t] for s,d in data.items() if t in d['lookup']}; day=datetime.fromtimestamp(t,DAY_ZONE).strftime('%Y-%m-%d')
        for symbol,p in list(positions.items()):
            d=data[symbol]; ft=d['fundingTimes']
            for stamp in ft[bisect.bisect_right(ft,p['lastFunding']):bisect.bisect_right(ft,t)]:
                if stamp<=p['entryTime']: continue
                proxy=d['lookup'].get(stamp)
                ref=d['marks'].get(stamp) or proxy
                rate=d['funding'][stamp]['rate']; price=ref['o'] if ref else lastclose.get(symbol,p['entry'])
                paid=p['qty']*price*rate;p['margin']-=paid;p['funding']+=paid
                if not d['marks'].get(stamp):p['fundingPriceProxyCount']+=1
            p['lastFunding']=t
            if any(p['entryTime']<cut<=t for cut in d.get('periodEnds',[])):
                p['incompletePath']=True;cash+=settle(p,p['qty'],p['entry'],'data_missing_collateral_stress',t,cost_multiplier,True)
                p['exits'][-1].update(price=None,fillAssumption='Unknown terminal path; remaining isolated collateral forfeited as stress, not an executed fill')
                finish(symbol,p,t);continue
            if symbol in bars and t-p['lastTime']>H:
                p['incompletePath']=True;cash+=settle(p,p['qty'],bars[symbol]['o'],'gap_resume_exit',t,cost_multiplier, bars[symbol]['o']<=liquidation_price(p));finish(symbol,p,t)
        equity=cash+sum(max(0,p['margin']+p['qty']*(lastclose.get(s,p['entry'])-p['entry'])) for s,p in positions.items())
        candidates=[]
        for s in bars:
            f=data[s]['features'].get(t)
            if f and f[strategy]:candidates.append((s,f))
        for s,f in sorted(candidates,key=lambda x:(-x[1]['volumeRatio'],x[0])):
            checks['rawCandidates']+=1
            if f['oi'] is None:checks['oiMissing']+=1
            if s in positions or t<cooldown.get(s,0): skipped['held_or_cooldown']+=1;continue
            if require_oi and (f['oi'] is None or not RULES['oi24hMin']<=f['oi']['change']<=RULES['oi24hMax']): skipped['oi_missing_or_outside_range']+=1;continue
            if paused or realized[day]<=-RULES['dailyLossCap']:skipped['account_risk_pause']+=1;continue
            if len(positions)>=RULES['maxPositions']:skipped['two_position_limit']+=1;continue
            d=data[s]; ft=d['fundingTimes']; known=bisect.bisect_left(ft,t)-1
            if known<0 or t-ft[known]>24*H:skipped['no_recent_funding_history']+=1;continue
            pp,reason=plan(f,bars[s]['o'],equity,cash,cost_multiplier)
            if pp is None:skipped[reason]+=1;continue
            cash-=pp['margin']+pp['fee']
            positions[s]={'symbol':s,'entryTime':t,'openedAt':iso(t),'decisionAt':iso(f['decision']),
              'entry':pp['entry'],'stop':pp['stop'],'initialStop':pp['stop'],'targets':pp['targets'],'targetBasis':pp['targetBasis'],
              'qty':pp['qty'],'initialQty':pp['qty'],'margin':pp['margin'],'initialMargin':pp['margin'],'entryFee':pp['fee'],
              'estimatedLossWithBuffer':pp['estimatedLossWithBuffer'],'fees':pp['fee'],'funding':0.,'gross':0.,'adjustment':0.,'returns':0.,
              'exits':[],'hit':[False,False],'lastTime':t-H,'lastFunding':t,'peak':pp['entry'],'atr':f['atr'],
              'incompletePath':False,'markProxyHours':0,'fundingPriceProxyCount':0,'oiAtDecision':f['oi']}
        maxmargin=max(maxmargin,sum(p['initialMargin']*(p['qty']/p['initialQty']) for p in positions.values()))
        for s,p in list(positions.items()):
            if s not in bars:continue
            b=bars[s];mark=data[s]['marks'].get(t)
            if mark is None:p['markProxyHours']+=1
            cash+=step(p,b,mark,t,cost_multiplier)
            if p['qty']>1e-10 and t+H-p['entryTime']>=RULES['maximumHours']*H:cash+=settle(p,p['qty'],b['c'],'time_exit',t+H,cost_multiplier)
            if p['qty']>1e-10 and t+H in data[s].get('periodEnds',[]):
                cash+=settle(p,p['qty'],b['c'],'window_end_exit',t+H,cost_multiplier)
            if p['qty']<=1e-10:finish(s,p,t+H);continue
            # Trail for NEXT hour only. 40% remains after both targets, with no upside cap.
            p['lastTime']=t;p['peak']=max(p['peak'],b['h'])
            if p['hit'][0]:p['stop']=max(p['stop'],p['peak']-RULES['trailAtr']*p['atr'])
        for s,b in bars.items():lastclose[s]=b['c']
        equity=cash+sum(max(0,p['margin']+p['qty']*(lastclose.get(s,p['entry'])-p['entry'])) for s,p in positions.items())
        peak=max(peak,equity);dd=1-equity/peak;maxdd=max(maxdd,dd)
        if dd>=RULES['drawdownEntryPause']:paused=True
        maxmargin=max(maxmargin,sum(p['initialMargin']*(p['qty']/p['initialQty']) for p in positions.values()))
        curve.append({'time':iso(t+H),'equity':round(equity,4)})
        if cash < -1e-7:raise AssertionError('Negative free cash')
    for s,p in list(positions.items()):
        p['incompletePath']|=p['lastTime']!=times[-1]
        if p['incompletePath']:
            cash+=settle(p,p['qty'],p['entry'],'data_missing_collateral_stress',times[-1]+H,cost_multiplier,True)
            p['exits'][-1].update(price=None,fillAssumption='Unknown terminal path; remaining collateral stress loss, no invented fill')
            finish(s,p,times[-1]+H)
        else:
            cash+=settle(p,p['qty'],lastclose[s],'window_end_exit',p['lastTime']+H,cost_multiplier);finish(s,p,p['lastTime']+H)
    if abs(cash-(RULES['initialCash']+sum(p['net'] for p in trades)))>1e-6:raise AssertionError('Account cash reconciliation failed')
    if times:curve.append({'time':iso(times[-1]+H),'equity':round(cash,4),'kind':'window_settlement'})
    peak=max(peak,cash);maxdd=max(maxdd,1-cash/peak)
    losses=-sum(min(0,p['net']) for p in trades);wins=sum(max(0,p['net']) for p in trades)
    return {'strategy':strategy,'oiRequired':require_oi,'costMultiplier':cost_multiplier,'initialCash':RULES['initialCash'],
      'finalCash':round(cash,4),'net':round(cash-RULES['initialCash'],4),'returnPercent':round((cash/RULES['initialCash']-1)*100,3),
      'closedHourMaxDrawdownPercent':round(maxdd*100,3),'trades':len(trades),'wins':sum(p['net']>0 for p in trades),
      'profitFactor':round(wins/losses,3) if losses else None,'fees':round(sum(p['fees'] for p in trades),4),
      'fundingPaidSigned':round(sum(p['funding'] for p in trades),4),'settlementAdjustment':round(sum(p['adjustment'] for p in trades),4),
      'incompletePathTrades':sum(p['incompletePath'] for p in trades),'markProxyTrades':sum(p['markProxyHours']>0 for p in trades),
      'windowEndTrades':sum(any(e['reason']=='window_end_exit' for e in p['exits']) for p in trades),
      'liquidationStressTrades':sum(any(e['reason']=='liquidation_stress' for e in p['exits']) for p in trades),
      'maximumMarginInUse':round(maxmargin,4),'entryCircuitPaused':paused,'checks':dict(checks),'skipped':dict(skipped),
      'bySymbol':[{'symbol':s,'trades':sum(p['symbol']==s for p in trades),'net':round(sum(p['net'] for p in trades if p['symbol']==s),4)} for s in sorted({p['symbol'] for p in trades})],
      'ledger':trades,'equity':curve[::max(1,len(curve)//500)]+curve[-1:]}

def main():
    out=ROOT/'docs/research/backtest';out.mkdir(parents=True,exist_ok=True)
    rules_path=out/'Rules-R1.json'
    if rules_path.exists() and json.loads(rules_path.read_text('utf-8'))!=RULES:raise ValueError('Frozen R1 changed: create a new version instead')
    rules_path.write_text(json.dumps(RULES,ensure_ascii=False,indent=2),'utf-8')
    data,manifest_hash=load_data();results=[]
    for w in RULES['windows']:
        start=int(datetime.fromisoformat(w['start']).replace(tzinfo=timezone.utc).timestamp());end=int(datetime.fromisoformat(w['end']).replace(tzinfo=timezone.utc).timestamp())
        for strategy in ('a','b'):
            for oi in (False,True):
                for cost in (1,2):
                    r=replay(data,strategy,oi,start,end,cost);r['window']=w['name'];r['period']=[w['start'],w['end']]
                    key=f"{w['start']}-{strategy}-{'oi' if oi else 'price'}-cost{cost}"
                    (out/(key+'.json')).write_text(json.dumps(r,ensure_ascii=False,indent=2),'utf-8')
                    results.append({k:v for k,v in r.items() if k not in ('ledger','equity')});print(key,r['trades'],r['net'],flush=True)
    summary={'version':RULES['version'],'generatedAt':iso(int(datetime.now(timezone.utc).timestamp())),'networkRequests':0,
      'rulesSha256':hashlib.sha256(rules_path.read_bytes()).hexdigest(),'archiveManifestSha256':manifest_hash,
      'engineSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'dailyRiskTimeZone':'Asia/Shanghai','windowBoundaryTimeZone':'UTC',
      'symbols':len(data),'hours':sum(len(d['rows']) for d in data.values()),'rules':RULES,'results':results,
      'validated':False,'paperEnabled':False,'verdict':'研究诊断完成，不具备开启自动模拟的验证资格；未使用真实留出与完整排行榜，OI覆盖存在事件选择偏差。'}
    (out/'Summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),'utf-8')
    lines=['# 两套结构策略 R1：离线回放诊断','',summary['verdict'],'','1000 USDT是每个时段、每个变体独立账户；余额不能相加。所有参数在首次回放前写入 Rules-R1.json，此轮不按结果调优。',
      '','A：72h收敛区上部的量能启动，属于提前观察的量价代理。B：24h涨8%～40%并突破72h高点的涨幅候选代理；并未还原全市场排行榜。',
      '','入场使用信号闭合后的下一小时开盘加滑点。支撑由右侧两根已闭合K线确认，止损加0.5ATR缓冲；每笔风险最多10 USDT且不超过权益1%，保证金最多100、10倍逐仓、最多两笔。',
      '','止盈优先使用当时已确认且至少1R远的历史压力；无合理压力时明确用2R/4R风险释放投影，绝不冒充历史压力。各减原始数量30%，剩余40%使用3ATR跟踪，跟踪保护从下一小时生效。',
      '','日内已实现损失按上海日界统计，达到30停止新开仓，时段内权益回撤达到15%停止新开仓。窗口起止日期为UTC。止损、资金费与清算估算仍可能让实际模型损失超出计划风险，不承诺不会强平。',
      '','这版组合允许17个入选合约（含BTC/ETH）参与，按当时信号量比排序，同量比按合约名稳定排序。它是选中样本的组合诊断，不是纯小币策略或全市场择币业绩；Summary及逐笔文件保留逐标的贡献，供后续单独比较。',
      '','|时段|策略|OI确认|成本倍率|交易|净额USDT|小时权益最大回撤|路径缺失|标记价代理交易|','|---|---|---|---:|---:|---:|---:|---:|---:|']
    for r in results:lines.append(f"|{r['window']}|{r['strategy'].upper()}|{'是' if r['oiRequired'] else '否'}|{r['costMultiplier']}|{r['trades']}|{r['net']:.2f}|{r['closedHourMaxDrawdownPercent']:.2f}%|{r['incompletePathTrades']}|{r['markProxyTrades']}|")
    lines+=['','## 数据与验证边界','']+['- '+x for x in RULES['limitations']]
    lines+=['','资金费按记录的实际结算时间与正负计入，不用事后累计费率当账户成本。缺少近期实际资金费记录则不开仓。持仓遇缺口保留至下一可见开盘压力退出并标记路径缺失；期间资金费结算价缺失时用最后可见收盘代理，计数留档。',
      '','各合约预先指定的月线窗口结束即模型平仓，不占用其他合约后续月份仓位；若数据提前中断且到窗口末仍未恢复，剩余逐仓保证金按全损压力结算，退出价格记Unknown，绝不回看补卖在旧收盘。OI可见时间额外延迟5分钟只是一项研究假设，没有历史真实到达时刻。',
      '','离线诊断使用双精度浮点计算并逐笔/组合核对到1e-6 USDT，不写入使用decimal的在线模拟账本；精确数量步长与报价单位仍缺。',
      '','回撤取小时收盘权益和期末结算，未覆盖逐笔盘中最大回撤，不能叫真实最大风险。无费用率、步长、盘口或真实分层维持保证金表的历史，所有成交均为假设。清算优先与资金费价格代理可能造成明显保守偏差。',
      '','本轮没有网络下载。原始档案逐一复核SHA256；回放完整预选月线，不使用事后峰值附近裁剪的展示时间线。结果、规则、各变体逐笔账本与权益曲线保存在同目录。',
      '','下一步应采集连续多维历史及当时全市场可交易全集，再登记真正未参与研究的前瞻时段；不能把这里的后段诊断包装为样本外通过。']
    (out/'策略回放诊断-R1.md').write_text('\n'.join(lines)+'\n','utf-8')

if __name__=='__main__':main()
