# Phase 9：WatchTower 自选监控

工程 PulletCryptoRadar，日期 2026-10-29（上海时间）。入口 /watchtower。以唯一规格书的 ARIA 与 CostPrice=0.40 为初始示例，不将其描述为实际持仓。示例个人通知默认关闭。

## 完成范围

自选配置支持启用/暂停、成本、目标、关注价、手动支撑/压力、个人提醒及项目/团队/投资人 EVM 钱包地址标签。配置使用版本校验，冲突返回 409；不删除修改历史。地址只做链和格式校验，当前不采集链上行为。

每 15 秒生成 WatchTower-1.0 判断：TrendScore、DerivativeScore、OnChainScore、WalletRiskScore、CrowdingRisk、趋势、Stage、StrategyState、Risk、置信度、正面/风险/缺失证据与下一观察点。链上和钱包分保持 null，不因为没有数据就判断钱包稳定或整体风险低。

成本口径分开显示：相对成本涨跌=(当前价/成本-1)*100%；回到成本所需变化=(成本/当前价-1)*100%。例：现价 0.088、成本 0.4，分别是 -78% 与 +354.545…%。这是价格比较，不包含手续费、杠杆、仓位或可成交收益。

## 独立数据源

优先用新鲜 Binance 永续价格和其自身特征。Binance 价格不足时可以展示 Gate 候选；只有已确认 Gate 原生合约身份才计算成本距离与策略。Coinbase 现货不混入永续监控。源时间过期、未来、未知身份或暂停状态一律停止判断和价格提醒。

趋势只取本交易所 1h 收益，Gate 缺少此历史时为 Unknown，不能借用 Binance 趋势。OI 数量增加只表示持仓量增加，不能直接等同多头资金流入。

## 策略与解释

所有规则是第一版可审计的研究规则，不自动买卖：

- 严重下跌（15m ≤-15% 或 1h ≤-25%）同时 OI 15m ≤-8%；或独立下跌检测器 Crash 且至少两类确认；或新鲜系统性下跌且本标的 1h ≤-5%：CriticalRisk。
- Selloff 至少两类确认，或手动支撑跌破且 OI 15m ≤-3%：ReduceRisk。
- 价格跌破手动支撑（0.1% 缓冲）、上涨>5%但 OI 减少<-3%、下跌<-5%且 OI 增加>5%：RiskIncreasing。
- 8h 标准化资金费率绝对值 ≥0.003、多头占比 ≥85%、或 1h 上涨 ≥20%：AvoidChase。
- 1h >1%、OI 15m ≥3%、成交 RZ ≥2：PositiveObserve；有成本观察项且已知上涨趋势保持则 HoldObserve；异动确认不足为 WaitConfirmation，其余 Observe。

按上述顺序判断，风险覆盖普通上涨证据。OnChain 和 Wallet 尚无采集源，因此不虚构项目钱包、Holder 或交易所流入结论，也不把总体风险标为 Low。

TrendScore=clamp(50+1h收益百分数*2,0,100)；DerivativeScore=clamp(50+OI15m变化百分数*5,0,100)。CrowdingRisk 是已知标准化 Funding 与多头拥挤强度的最大值；这些是研究强度，非概率。价格/趋势/OI/成交/拥挤分别贡献最多 20/15/15/10/10 置信度，缺失链上/钱包使置信度最多 70。

下一观察点显示已设置的目标、关注价、支撑、压力及所需确认，而非只输出“继续持有”。

## 个人提醒

个人重大风险单独保留，不受全市场普通通知每日额度和系统性事件聚合抑制。普通自选观察使用新增 WatchListPersonal 类型，重大风险使用既有 WatchListCritical。两者沿用原有持久化通知队列和 Windows 提交状态，不更改 V2 分数或原 Episode。

目标价、关注价和压力价只在两次有效同源价格上穿时触发。首次建立基线不产生价格穿越；编辑配置、切换交易所、数据过期后恢复均重新建立价格基线。持续风险条件可在首次启用时记录提醒。

同一风险状态持续时不重复预留；每标的默认冷却 30 分钟，首次升级 Critical 可越过普通冷却。最后已通知条件单独持久化，短时恢复正常/数据未知后再进入 Critical 不会反复绕过冷却。未配置个人提醒或暂停监控不预留新通知；已关闭配置的 Pending 项在投递前改为 Disabled。

全局 ToastEnabled 仍决定是否向 Windows 提交。提交不代表用户一定看到；进程中断的 Dispatching 沿用 Indeterminate、不自动重发。实际消息 120 秒过期，不补发陈旧行情。

## 存储、历史与接口

数据库版本 8：WatchAsset、WatchAssetAudit、WatchCheckpoint、WatchHistory；既有 AlertEvent 作为个人提醒 outbox。所有写入仍经过单 PersistenceWorker，状态与提醒在业务库一个事务提交，配置版本不符整批回滚。

历史默认每分钟或状态/来源/配置变化保存一次，gzip 冻结当时配置、独立来源输入、市场状态、阈值及结果，保留 7 天。复算使用保存时间和参数，不读取当前行情，不发送通知，不代表收益回测。

市场库新增 WatchCandle1m：只保存已监控资产的已闭合 Binance 1m K 线，独立标明 Exchange=Binance、去重并保留 14 天。不把 Gate 报价塞进 Binance 历史。启用后逐步累积，不能宣称已有完整的过去 14 天；行情库与业务 outbox 不使用跨库物理外键，K 线先幂等落库，提醒与状态再原子提交。

- GET /api/watchlist：配置与匹配版本的最新判断。
- PUT /api/watchlist/{assetId}：expectedRevision、enabled、costPrice、targetPrice、attentionPrice、supportPrice、resistancePrice、enableAlerts、wallets。
- POST /api/watchlist/evaluate：基于缓存更新判断，不主动请求交易所。
- GET /api/watchlist/{id}/history?limit=30。
- GET /api/watchlist/{id}/audit?limit=30。

价格可留空；非空必须大于 0 且不超过 1 万亿美元，十进制字符串按服务端 decimal 读取。支撑必须低于压力。钱包每资产最多 20 个、不允许重复链地址，只接受 bsc/ethereum/base/arbitrum/polygon 与 project/team/investor 标签。

WatchTower 配置节含 IntervalSeconds、AlertCooldownMinutes、SevereDrop15m、SevereDrop1h、ExtremeFunding8h、CrowdedLongShare、SeedAriaExample。首个示例只在没有任何自选配置时创建；暂停现有项不会重新开启它。修改保存后转为用户配置标记。

## 验收记录

258 项 .NET Release 测试全部通过（Core 84、Scanner 89、Integration 85）；11 项前端测试、Vue 类型检查及生产构建全部通过。新增覆盖成本公式、未来/过期价格、未核对 Gate、风险优先、来源/配置切换时禁止虚假上穿、Critical 反复出现与未知间隔去重、重大风险升级、个人提醒关闭、配置 CAS、金额精度、outbox 原子提交、重启恢复、已闭合 1m 去重、历史冻结参数复算。

本机 HTTP 返回 Phase 0-9 / Ready。ARIA 示例已初始化为成本 0.40、个人提醒关闭、版本 1，保持规格书示例标记。实际行情驱动成本比较；未知趋势和衍生品不虚构填充。历史判断复核返回 Match；没有在生产环境开启个人提醒或修改真实成本配置。

前端输入负数成本被拦截，服务端使用十进制字符串的无效支撑/压力组合返回 400 且未保存。单写入器新增/暂停和恢复在隔离数据库验证。八页共享导航在 1280×720 下纵坐标保持 170/229/287/345/403/461/519/577，单个选中项，侧栏 scrollTop=0，无页面横向溢出。短窗口收起底部说明区，避免导航点击带动侧栏滚动。

证据：Phase09-LiveValidation.json、Phase09-NavigationValidation.json、Phase09-WatchTower.png。
