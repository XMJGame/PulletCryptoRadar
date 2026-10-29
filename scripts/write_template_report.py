"""Render the descriptive report from the frozen case inventory; no new network requests."""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'docs/research/templates'
def local(s):return datetime.fromisoformat(s).astimezone(timezone(timedelta(hours=8))).strftime('%Y-%m-%d %H:%M') if s else 'Unknown'
def percent(v):return 'Unknown' if v is None else f'{v:+.2f}%'

def main():
    r=json.loads((OUT/'Summary.json').read_text('utf-8'));cases=r['cases'];valid=[c for c in cases if c['status']=='observed']
    roles={'seed':'种子','seed-contrast':'种子/风险对照','historical':'历史形态','crash-context':'急跌背景','delisting-context':'退市背景','market-control':'市场对照'}
    lines=['# 历史行情行为样板报告 R1','',f"生成：{local(r['generatedAt'])}（上海时间）；版本 {r['version']}。",'',
        '## 本轮实际取得了什么','',
        f"首批有意挑选 {len(set(c['symbol'] for c in cases))} 个合约、{len(cases)} 个时段，{len(valid)} 个时段通过数据解析。不是全市场统计，也不是已经验证的策略。",
        f"共 {sum(c.get('hours',0) for c in valid):,} 根小时K线、{sum(c.get('oiRows',0) for c in valid):,} 条事件窗口OI记录、{sum(c.get('actualFundingRows',0) for c in valid):,} 条实际结算资金费记录。资金费记录跨不同预选月份，不能把样本条数视为独立交易次数。",
        f"{r['archive']['verifiedFiles']} 个公开档案SHA256校验通过，{r['archive']['missingFiles']} 个档案缺失。原始ZIP缓存于 artifacts/research-archive；URL、抓取时间和哈希冻结在 ArchiveManifest.json。",
        '历史持币人数、已核对项目钱包归属及其转账、消息催化未补齐。小时K线不能确定同小时内高低先后，不能证明止损或强平未发生。',
        '', '## 如何理解“操盘手法”', '',
        '本报告只建立可观察行为样板，不认定主体或违法操纵。同一资产不同阶段可能属于多种形态；不把一个币永久贴成某种庄家类型。',
        '', '| 样板方向 | 已有可观察证据 | 仍不能推出什么 | 下一项核验 |','|---|---|---|---|',
        '| 价格推进、OI数量回落 | TRB、GAS、MYX、LAB等选段可比较价格与实际OI | 不能单凭OI下降断定空头平仓、逼空或控盘 | 分阶段主动买卖、现货跟随、完整强平与OI变动先后 |',
        '| 上涨同时OI扩张 | YFI、AUCTION等峰值小时前OI增加 | 多空双方同时形成新仓，不等于项目方或大户做多 | 追踪资金费与主动成交，检查后续延续/反转 |',
        '| 尖峰与高位急跌 | 小时高低范围、峰值后后续小时低点、标记价 | 不能把上影线认定为出货，不能假设最高价能卖出 | 补分钟路径和盘口，比较成交价与标记价 |',
        '| 突破后回到原区间 | 触发时已有区间/量能特征，之后固定24h结果标签 | 失败标签不等于完整交易亏损，其他结果也不等于盈利 | 按支撑失效、费用、强平、分批退出做执行回测 |',
        '| 急跌/退市背景 | ACT、OM、旧LUNA及UNFI退市时段 | 不应把结构性失败简单当作普通洗盘，也不据此推断内幕 | 事件日历、合约结算及流动性消失 |',
        '| 市场整体影响 | 相同小时的BTC/ETH收盘变化 | 超额涨幅不证明独立资金或操纵 | 同期对照、匹配币龄/流动性/波动率 |',
        '', '## 样板索引（全部为事后描述）', '',
        '起点为峰值前14日内事后低点所在小时的收盘；这不是当时能知道的买点。涨幅使用该收盘至峰值最高价。回撤严格使用峰值小时之后的小时低点，避免将同小时内可能先发生的低点冒充后续回撤。',
        '急跌/退市案例以预选范围内最大单小时收盘/开盘跌幅之前7日高点锚定，避免只看与危机无关的早期高点。其余案例取预选范围最高价。', '',
        '| 原生合约 / 时段 | 背景 | 峰值小时（上海） | 起点收盘→最高价标签 | 随后低点相对峰值 | 峰值小时起点前24h OI数量 |',
        '|---|---|---|---:|---:|---:|']
    for c in cases:
        if c['status']!='observed':lines.append(f"|{c['key']}|{roles.get(c['role'],c['role'])}|数据不足|Unknown|Unknown|Unknown|");continue
        cut='' if c['postPeak14dAvailable'] else '（截断）'
        lines.append(f"|{c['key']}|{roles.get(c['role'],c['role'])}|{local(c['peak'])}|{percent(c['riseToPeakHighPercent'])}|{percent(c['postPeakMinLowPercent'])}{cut}|{percent(c['oiQuantityChange24hAtPeakPercent'])}|")
    lines += ['', '## 第一批值得保留的差异', '',
        '1. 大涨不是同一资金结构。TRB峰值小时起点前24h OI数量约-25.66%，YFI约+18.78%；单一“价涨OI涨”的规则会漏掉前者，也不能自动判断后者更安全。',
        '2. 资金费必须用实际结算和真实周期，不能一律假定8小时。TRB选段包含4小时结算；负费率只是持仓/套保结构线索，不单独证明挤压，更不是无风险做多收益。',
        '3. 上涨潜力和持有风险必须一起看。同一选段可以出现很大的最高价涨幅，也发生深度回撤。不能把最高价标签当作10倍仓位可以完整拿到的利润。',
        '4. 旧LUNA与UNFI的退市背景必须保留，不能只留下还在交易、曾经大涨的资产。这里仍是有意挑选的样板；真实泛化验证还需要当时完整可交易宇宙和匹配失败对照。',
        '', '## 机械突破对照：不是胜率', '',
        f"在预选小时序列中，按固定定义发现 {sum(c.get('breakoutTriggers',0) for c in valid)} 个研究触发，其中 {sum(c.get('failedBreakoutLabels',0) for c in valid)} 个符合‘突破失败’结果标签。不能将其余触发当成盈利，也不能把两者之比当作策略胜率。",
        '触发：当前小时收盘超过此前72根高点0.2%，且当前小时量至少为此前24小时量中位数1.5倍。每个合约/预选时段24h最多取一次。只计算前后窗口完整连续的数据。',
        '结果标签：随后24h最高价相对次小时开盘参考涨幅不足5%，且24h最后收盘低于原72h高点。特征在当前小时闭合时可见；未来字段仅作结果标签。阈值为研究定义，尚未调优或验证成入场规则。',
        '支撑候选仅取触发时已用右侧两根闭合K线确认的摆动低点，未加波动缓冲、费用及强平空间，不能直接当止损。并未模拟任何真实委托或交易收益。',
        '', '## 对两套策略的用途', '',
        '- 提前埋伏：先检验价格收敛、成交变化、OI和实际资金费的可见先后，再补供给/钱包。峰值前后五天OI不能替代整个建仓期历史；起点前没取得的数据继续Unknown。',
        '- 涨幅榜：在当时榜单宇宙和可交易性范围内检验首次启动、回踩和二次延续，剔除过热/流动性不足/结构止损与10倍杠杆冲突的情形。当前榜单宇宙重建尚未完成。',
        '- 分批止盈：下一阶段用分钟级路径比较压力位减仓与尾仓跟踪；先固定方案与样本切分，再检验，不能用本报告事后峰值指导卖点。',
        '', '## 来源与可复现', '',
        '- [Binance公开数据说明](https://github.com/binance/binance-public-data)：USD-M K线、交易量及校验格式；档案可能被官方更正，故保留本次哈希。',
        '- [TRB 2023-12 小时线ZIP](https://data.binance.vision/data/futures/um/monthly/klines/TRBUSDT/1h/TRBUSDT-1h-2023-12.zip)、[对应校验](https://data.binance.vision/data/futures/um/monthly/klines/TRBUSDT/1h/TRBUSDT-1h-2023-12.zip.CHECKSUM)。',
        '- [TRB 2023-12-31 OI/比率档案](https://data.binance.vision/data/futures/um/daily/metrics/TRBUSDT/TRBUSDT-metrics-2023-12-31.zip)、[2023-12实际资金费](https://data.binance.vision/data/futures/um/monthly/fundingRate/TRBUSDT/TRBUSDT-fundingRate-2023-12.zip)。',
        '- [Binance旧LUNA期货下架公告](https://www.binance.com/en/support/announcement/detail/ef3ce76d5c2d45ee9b6dc3f281cde744)、[UNFI下架背景公告](https://www.binance.com/en/square/post/15245171029058)。用于事件背景，不证明操纵归因。',
        '', '运行 scripts/build_research_templates.py 可补采与重建；--offline 使用已校验缓存。scripts/test_research_templates.py 验证时间、数量口径、未来标签隔离、同小时先后、缺口与哈希。配置在 docs/research/template-universe.json；完整输入清单在 ArchiveManifest.json；每个样板独立JSON供页面读取。',
        '', '自动模拟开仓仍关闭。首版样板不是稳定信号、盈利承诺或保证不穿仓的执行系统。']
    (OUT/'历史行情行为样板报告-R1.md').write_text('\n'.join(lines)+'\n','utf-8')
    print('Report saved; cases:',len(cases))

if __name__=='__main__':main()
