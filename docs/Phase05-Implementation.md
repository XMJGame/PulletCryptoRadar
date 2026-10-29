# Phase 5 · Derivatives 深度数据

工程：PulletCryptoRadar v1.0。实施日期：2026-10-29。

## 本轮交付

- 当前 OI：全市场活跃 USDT 永续，默认每 300 秒一轮。
- DeepCandidate：默认 80 个合约；优先 ARIAUSDT/BTCUSDT/ETHUSDT，再按价格/量 RZ 和 24h 成交量排序。每轮采集 OI History、Global LongShort、Top Account、Top Position、Taker BuySell，默认各 100 个 5m 样本。
- `!forceOrder@arr` 强平流：共享 combined market 连接，记录源时间、成交方向、累计成交量和实际均价。
- DerivativeFeatureEngine、UpMove-2.0、DownMove-2.0、独立衍生品页面及只读 API。
- SQLite 版本 4 增量迁移，保留既有资产、行情、Episode、通知预算和记录。

未进入 Phase 6 的 ReplayEngine / SignalEvaluation；市值映射、OI/市值、链上及 WatchList 仍属于后续阶段。

## 数据含义与有效性

1. OI 变化采用 sumOpenInterest 数量，不能拿 sumOpenInterestValue 的价格上涨当成加仓。提供 5/15/60/240 分钟变化，要求窗口内每个 5m 桶均存在；不补历史空缺。
2. OI 美元值优先使用当前数量 × 30 秒内的 Mark Price；否则显示历史接口报告的 notional，并保留历史源时间。数量本身默认 11 分钟内有效。显示美元值不是 OI/市值。
3. Global、Top Account、Top Position 保留比例 0～1；页面乘 100 显示。Top Position 15m 差值以百分点 pp 表示，不能混作持仓增长率。
4. Taker 接口 timestamp 是周期开始，转换到开始 + 5m，只接受已结束周期；其他统计接口使用周期结束时间。主动买量占比为 buyVol / (buyVol + sellVol)；总量为 0 时 Unknown，sellVol 为 0 时买卖比 Unknown。
5. OI RobustZ 使用相邻 5m 数量变化，Taker RobustZ 使用买量占比；当前观测排除在基线外。至少 30 个历史样本，MAD 为 0 时 Unknown。初始 100 点不等于 30 天统计历史。
6. Price/OI 标注 PriceUpOiUp、ShortBuild、Deleveraging、ShortCovering。价格或 OI 数据不足时 Unknown。这是价格与仓位组合描述，不能推断账户动机。
7. 所有金额和数量为 decimal，统计量为 double；保留 UTC SourceTime、ReceivedTime、来源和 DataStatus。恢复历史不会刷新源时间。

## 强平观测的边界

Binance 每个标的每秒至多推送最新强平订单快照，不是完整逐笔流。使用 `z × ap`（累计成交数量 × 实际均价），不能使用委托数量 q 或委托价格 p。SELL 对应多头强平，BUY 对应空头强平。

按 symbol + transactionTime + side 做保守去重，重复快照保留较大的累计成交量。交易所没有提供可用于精确逐笔去重的订单 ID，因此同毫秒同方向的不同订单可能合并；始终标注“观测样本，非完整总额”。

15m 多/空强平观测额要求连接持续覆盖整个窗口且最近活动不超过 45 秒。已覆盖且无观测时可以为 0；预热、断线和数据不足为 Unknown。重连重新预热，不把 SQLite 中旧观测作为连续连接证明。内存最多保留每币 4096 条、1 天；淘汰数据时缩短覆盖范围，避免对缺失历史计算完整基线。Liquidation RZ 使用覆盖完整、互不重叠的历史 15m 窗口，需要至少 30 个样本，不能刚启动就宣称有效。

## V2 评分与门槛

| 家族 | Up 权重 | Down 权重 |
| --- | ---: | ---: |
| Price | 15 | 20 |
| Volume | 20 | 15 |
| Structure | 10 | 10 |
| OpenInterest | 20 | 20 |
| Flow | 15 | 15 |
| Trader | 10 | — |
| Funding | 5 | 5 |
| MarketCap | 5（缺失） | — |
| Liquidation | — | 15 |

分数为各权重 × 0～1 强度之和，固定 100 分母。缺失权重不重新分摊。OI15 与 Taker 任一缺失时 V2 分数为 Unknown；两者均缺失时保留原 Phase 3 基础模型及版本，置信度最高 60。

OI 确认：上涨需要 OI15 ≥ 3% 且 OI RZ ≥ 2；下跌需要 |OI15| ≥ 3% 且 |RZ| ≥ 2，或 OI15 ≤ -8%。Flow 确认采用买量占比 ≥ 60% / ≤ 40%。Trader 需要 Top Position 15m 增加 ≥ 1pp 且 Top Account 多头占比 ≥ 50%。Funding 不计为独立确认家族。下跌强平确认阈值默认本机观测多头强平 15m ≥ 100,000 USD，可通过 Volatility.ObservedLiquidationConfirmationUsd 调整；这不是跨币统一的市场完整强平规模估计。

数据置信度为 floor(90 × 有值家族权重 / 100)，表示数据可用程度，不是收益概率。新上市合约上限 60。基础价格/成交量无效时不能被衍生品“救活”。Funding ≥ 0.001 或 Global 多头占比 ≥ 80% 标为拥挤高风险，上涨 Overheated；高价、OI 减少且卖量占优可标 Distribution，取消上涨候选。下跌区分 Breakdown、Selloff、Crash；明显去杠杆并有强平观测支持时可进入 Crash。

Episode 保存具体 StrategyVersion；模型变更按既有 StrategyChanged 规则关闭旧 Episode，再生成新模型观测。普通通知门槛、每日 5 条、30 分钟 2 个新标的、竞争等待、持久化 outbox 与发送去重均保持。市值/链上风险未知，V2 不制造 Risk.Low，因此不能生成 S 级低风险结论。

## 调度、限流与恢复

当前 OI 和深度采集独立运行，并发默认各 3。只有通过标准 MarketEvent 才更新缓存；Provider 不写数据库或调用检测器。统计接口额外每分钟 120 请求，所有 REST 仍共用全局每分钟 600 权重。429/418 的 Retry-After 触发全 Provider 冷却；错误正文包含 `banned until` 的毫秒时间戳时同时遵守更长期限。无期限时 418 至少冷却 2 分钟。独立的 Binance Cooldown 健康项避免其他在途请求成功掩盖冷却，页面显示实际 UTC 截止时间。

重启时先用数据库中已知活跃合约建立 WebSocket，再刷新 ExchangeInfo，避免 REST 冷却阻塞所有实时流。全新数据库仍须先完成合约同步。外部 IP、共享出口或其他进程流量不在本进程限流器控制内；不能保证接口永不限流。

每 30 秒提交原始衍生品/强平批次，经单 PersistenceWorker 写入 market 数据库。主键幂等；源时间乱序时保留较新的接收修订。收到持久化成功确认后才清除 pending，期间同 key 的新修订不能被旧确认误删。OI/比率缓存每类最多 2016 点，候选合约按需恢复历史；连接连续性不从历史恢复。原始衍生品保留 90 天，强平观测 14 天。长期写库故障会保留 pending 重试，当前没有磁盘 spool 或无限故障的有界持久缓存保证。

## 页面与 API

- `/derivatives`：搜索、分页、深度可用筛选、源时间提示、合约详情、强平覆盖和冷却说明。页面每 5 秒读取应用缓存，不直接读 SQLite。
- `/api/assets/{id}` → features.derivatives：计算结果。
- `/api/assets/{id}/derivatives?limit=100`：原始时间序列及连接覆盖，每类最多 500 点。
- `/api/providers/health`：采集、持久化、流、冷却状态。
- `/health` → Phase `0-5`。

## 验证记录

Release 测试：**149 项全部通过**（Core 39、Scanner 69、Integration 41）。新增 27 项覆盖数量与美元值区分、周期端点、缺桶、样本数/MAD、零成交、Top Position 零基点、强平预热/断线/去重/淘汰、decimal 精度、幂等恢复、并发 pending 确认、V2 权重与缺失证据、新币及旧价格限制、双层请求节流、正文冷却期限与独立冷却健康状态。

已发布 self-contained win-x64 并在 localhost:5218 重启；原 Data 与自定义配置保留。浏览器验证搜索 ARIA、详情、Unknown 状态和连续流覆盖起点。真实 ARIA/BTC OI 已进入缓存与 SQLite，ARIA 15m OI 变化约 -0.02549%、基线 98 样本（当时观测，仅用于核对，不是固定测试值）。重启后 ARIA 实时价格与 Mark Price 更新正常，OI 美元值切换到“数量 × fresh mark price”。

**外部限制：验收期间 Binance 返回 HTTP 418，完整候选池及 Taker/持仓比整轮实时采集尚待冷却后完成。** 已有接口抽查和自动测试不能替代这项持续运行验收；页面不会把剩余 Unknown 伪装成数据齐备。强平 15m 完整窗口及较长 RZ 基线也须真实持续运行预热。此轮完成实现、发布和可执行验证，不宣称全市场所有新增字段已实时就绪。

## 官方接口依据

- [Binance USDⓈ-M REST Market Data](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data)：当前 OI、五类统计接口、时间戳和 IP 限流。
- [Binance USDⓈ-M Market Streams](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market)：combined market 流与强平抽样语义。

接口说明于 2026-10-29 核对。部分 Top Trader 文档标注 API Key，但本机先前匿名请求返回 200；本实现只使用公共数据，不申请账户/交易权限。若服务端后续要求认证，记录失败并保持 Unknown，不以替代值伪装成功。
