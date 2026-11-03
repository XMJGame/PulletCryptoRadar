# PulletCryptoRadar

Windows 本地加密行情研究工作台，当前版本 v1.0，已实施 **Phase 0～16**，包含 Vue 3 前端、行情研究、历史样板、结构策略离线诊断、省流控制和连续前瞻观察。已支持需主动启用的双向结构实验模拟（研究未验证），不发送真实委托。

当前交付目标已固定为**正式模拟盘**，稳定后再单独讨论真实资金接入。发布收尾及研究边界见[正式模拟盘发布清单](docs/正式模拟盘-发布清单-2026-11-01.md)。

11月1日交付独立维护工具：[在线备份、隔离恢复及24小时观察](docs/模拟盘-备份恢复与24小时验收.md)，并取得自然五笔实持截面。原定首轮观察为11月1日15:24:18至11月2日15:24:18；后续中断与恢复以11月3日复盘为准，已知原生编码异常根因仍未关闭。

11月3日复盘：原观察于11月1日21:02左右中断，未满24小时；查到系统0x101蓝屏事件及全零流量计数导致的恢复启动失败，已加固计数持久化/恢复并保留原账本。11月3日移除旧版界面后，固定网页版本的观察在本轮快查界面更新后改为11月3日13:22:06至11月4日13:22:06；服务未重启，策略未调整，前一窗口记录保留。详见[关机后复盘与恢复](docs/Run-Review-2026-11-03.md)。

日常界面已统一为四个主入口：[量化工作台](http://localhost:5218/)、[妖币埋伏](http://localhost:5218/opportunities)、[代币快查](http://localhost:5218/token-check)、[复盘与验证](http://localhost:5218/journal)。行情、链上、自选和运行管理进入[辅助工具](http://localhost:5218/research)。原版本账户、结算、早期前瞻及迁移演练已从网页移除，旧页面链接转到当前复盘，历史账本和存量仓位继续保留。详见[旧版界面移除记录](docs/UI-Legacy-Removal-2026-11-03.md)。

当前按[合约量化重构计划](docs/合约量化重构方案与推进计划-2026-10-31.md)推进。执行基础的真实数据采集、成本计算和核验已上线：[基础验收记录](docs/Execution-Foundation-2026-10-31.md)。首页“执行基础·真实成本对照”展示实际资金费、合约步长和盘口成本；新仓已使用新版执行，历史与原仓按各自版本保留。

新核心 `Quant-Shadow-1` 提供顺势回踩/放量突破的多空条件、BTC/ETH背景、OI、成本及组合风险方案，独立保存固定参数和前瞻输入，当前预览不计作交易。**2026-11-01 01:54:41已启用 `Quant-Paper-Experiment-1` 新仓执行**：旧仓旧规则、新仓新规则，共用原现金与五仓额度，实际资金费及迟到调整原子记账。策略研究仍未验证，详见[执行启用验收](docs/Quant-Paper-Experiment-1-验收记录-2026-11-01.md)、[冻结规则](docs/Quant-Shadow-1-规则与边界.md)。

第三阶段新增[固定规则回放与验证](http://localhost:5218/quant-validation)：60天按30/15/15天分段，比较30%/30%/40%尾仓与首目标全退、1/2/3倍假设成本，并核对已冻结的前瞻判断。原始输入、研究版本和逐笔退出独立留档，重启可续算。历史OI、盘口和档位缺覆盖，因此报告仅是价格条件与退出情景研究，不能当作1000U账户收益或完整策略盈利证明。详见[预登记](docs/Quant-Validation-1-预登记-2026-11-01.md)、[本轮验收](docs/Quant-Validation-1-验收记录-2026-11-01.md)。

回放页“亏损分解”可查看多空/环境/退出路径、同一路径成本桥接、同入场退出配对，以及币种和UTC日期贡献。它只读完整存档、不下载行情；这是看过结果后的探索性分析，不据此自动调参或挑币。首轮两个价格方案均未通过预设留出检验，详见[诊断结论与下一轮假设](docs/Quant-Diagnostics-1-验收与结论-2026-11-01.md)。

第4阶段A“真实成本与迁移演练”报告已归档，保留当时资金费与旧仓数量余数的核验；第4阶段B已接入原账户连续调度。历史报告所示状态属于冻结时点，当前执行状态以首页为准。详见[副本历史验收](docs/Quant-Migration-DryRun-1-验收记录-2026-11-01.md)。

第5阶段已上线[新模型每日复盘](http://localhost:5218/journal)：按上海日期核对覆盖、冻结判断、价格触发、计划、意向、成交和取消；新仓逐笔串联当时信号、后续盘口、结构目标、采样路径、浮盈亏与实际资金费。后台每5分钟保存本地日报，不增加外部请求。首笔EVAA于11月1日10:26:32自动止损退出，净额约-1.008334U，完整账目及已观察资金费范围已核对；盈利验证仍待前瞻样本。详见[日报验收](docs/Quant-Daily-1-验收记录-2026-11-01.md)、[首笔退出复盘](docs/First-Exit-复盘-2026-11-01.md)。

开仓执行新增 `Quant-Entry-Recheck-1`：后续盘口复核在原批准数量、杠杆、风险上限内计算，避免自由重算数量增加而直接取消合格意向。每日复盘的“逐次开仓复核”保存原批准、未约束重算、实际采用值及来源资料，与成交和现金原子提交。旧取消不追补原因；当前策略参数及原仓保留。详见[开仓复核验收](docs/Entry-Recheck-验收记录-2026-11-01.md)。

持仓路径新增 `Quant-Lifecycle-1` 完整交易核对：逐笔对照开仓/退出/剩余数量、保证金、分摊开仓费、退出费、实际资金费与迟到调整，以及触发后的盘口证据。只有已平仓、资料一致且资金费观察范围覆盖退出时刻，才展示最终净额；仍持仓和缺资料保持待确认。详见[完整交易核对验收](docs/Lifecycle-验收记录-2026-11-01.md)。

`Hourly-Warmup-2`修复小时缓存“有行但接收时间无效”不触发补数的问题：重新获取来源、检验后落库并复查，不改写冻结信号的原始输入。日报“未就绪明细”按币/小时/原因去重，将K线基线缺口与成交额门槛区分。详见[补数修复验收](docs/Hourly-Warmup-验收记录-2026-11-01.md)。

APR已于11点正常补数周期恢复。复盘页“当前补数进度与重试”可查看每币阶段、请求/返回/接受数量和重试时间，记录跨重启保留；失败币暂离轮换池仍重试。新退出增加本机判定及结算计算时间，旧记录缺失部分保持Unknown。1000SHIB于11:02:33已出现首笔逐次复核后的实际模拟成交。详见[数据与执行质量验收](docs/Execution-Quality-验收记录-2026-11-01.md)。

日报新增 `Quant-Review-1` 完整交易评审：日/周最终退出归属、实际净R、成本、亏损超计划及多空/策略/环境分组，未平仓和未核对完整的交易单独列出。退出质量汇总区分已记录、历史部分及未知时序。当前仅1笔完整前瞻交易，状态为样本积累，不自动调参或确认盈利；后台随日报保存，详见[评审验收与口径](docs/Quant-Review-验收记录-2026-11-01.md)。

工作台及日报增加 `Paper-Performance-1` 账户净值守恒、上海日初观测收益、近90日窗口已观测回撤；界面不再单列旧/当前规则贡献对照，底层归属仍保留。净值采样与账本原子保存（业务库Schema16）；旧滚动曲线不冒充可信历史，中途启动或缺有效零点采样时日收益未知，回撤标明实际采样起点。见[净值与收益验收](docs/Account-Performance-验收记录-2026-11-01.md)。

完整交易评审可展开 `Quant-Exit-Attribution-1` 退出风险差异：核对原含成本预算，按保存盘口及实际费用桥接超损/改善，分批、盈利和尾仓均保留有符号差额。缺旧判定时只展示能证明的合计价差，不归因为网络或程序延迟，不自动调参。见[退出归因验收](docs/Exit-Attribution-验收记录-2026-11-01.md)。

[复盘与验证 → 研究与验证](http://localhost:5218/journal?view=research)新增 `Quant-Portfolio-Validation-1`：显式生成保存准入的独立重算、冻结原账户的单轮内存副本及17项合成资金/五仓边界核验。当前7条准入一致、17项边界通过；原账户实持4笔，早期1笔入场缺留档，五笔实持及完整历史连续回放仍待补证。档案可独立重放，刷新只读取已保存结果。详见[组合核验验收](docs/Portfolio-Validation-验收记录-2026-11-01.md)。

入口：[当前信号](http://localhost:5218/signals) 展示当前策略判断与数据缺口；[通知记录](http://localhost:5218/alerts) 查看提醒、预算和拦截原因。

[衍生品数据](http://localhost:5218/derivatives) 展示全市场 OI、候选池持仓比、Taker 和本机强平观测。数据充分时异动检测升级到 V2；缺失时保留明确的 Unknown，不提高虚假置信度。

当前固定规则回放、成本压力与证据核对进入[复盘与验证](http://localhost:5218/journal?view=research)。早期事件回放页面已退场，原始记录继续留存在本地。

## 启动

首次从本仓库下载源码后，在工程根目录执行 `./scripts/Publish.ps1` 构建发布版。EXE、数据库、日志、依赖和原始历史档案缓存不随Git仓库上传。

仓库保留源码、测试、实施文档、界面截图及构建所需的 `docs/research` 研究输入。新产生的账户验收 JSON、系统诊断和本机备份仅在本地保存；文档中提到的这些原始证据需在运行电脑上查看。

已有发布版时双击根目录“启动服务器.cmd”，或运行 `./Start.ps1`，会显示独立观察窗口并打开 <http://localhost:5218>。关闭浏览器或观察窗口不会停止采集；双击“停止服务器.cmd”或在观察窗口按S才正常停止。后台是普通进程和按需守护，没有注册Windows服务或开机启动。详见 [日常启动与停止](docs/日常启动与停止.md)。

默认启用省流模式：关闭持续实时流，全市场报价约60秒、重点7标的约20秒补充；动态深度池默认60个标的，含提前观察、涨幅、走弱和轮换；原持仓及当小时冻结名单额外保留。按用户最新要求，每日900 MB、每月30 GB为本程序正文负载预算，不是VPN账单封顶。数据不足、断网或零 MAD 会显示 `Unknown` 或过期状态，页面不生成模拟行情。历史样板与离线诊断结果包含在文档目录；重新生成它们需要另行获取并校验原始档案。

## 开发与验证

需要 .NET 10 SDK（global.json 固定 10.0.403）和 Node.js 24 / npm。发布脚本自动构建 Vue 前端，再测试和发布后端。

```powershell
dotnet build PulletCryptoRadar.sln
dotnet test PulletCryptoRadar.sln
dotnet run --project src/PulletCryptoRadar.Host --no-launch-profile
./scripts/Publish.ps1
```

发布目标为 `win-x64`、SelfContained，运行发布版不需要单独安装 .NET。配置、Data、Logs、Backup 均以实际 EXE 所在目录为根，开发版与发布版各自维护数据。重新发布前先退出正在运行的发布版，升级时保留 Data、Backup 和自定义 appsettings.json。

## 分层

| 工程 | 职责 |
| --- | --- |
| Core | Asset/Instrument、Candle、标准行情事件、FeatureValue、Provider 接口 |
| Application | RollingWindow、FeatureEngine、RobustStatistics、MarketHistoryLoader、MarketService |
| Scanner | UpMoveDetector、DownMoveDetector、MarketRegimeDetector、EpisodeEngine |
| Scanner / Application | SignalGrader、AlertPolicyEngine、AlertService，信号与通知分离 |
| Providers.Binance | REST、WebSocket、DTO 解析、加权限流及 Retry-After |
| Infrastructure | RuntimeStore、Channel、SQLite/Dapper、Repository、单 PersistenceWorker、聚合/备份 |
| frontend | Vue 3 + TypeScript + Vite + Pinia + Vue Router，四项主导航及研究页面 |
| Host | 依赖注入、配置、后台调度、REST API、本地宿主 |
| Core.Tests / Scanner.Tests / IntegrationTests | 算法、检测器、Episode、持久化、解析、回填、重连测试 |

Providers.Market 已提供 CoinGecko 目录、市值和供应量采集。旧 Web/Blazor 源码保留供迁移参考，已退出解决方案和运行依赖。

## 数据通路

`Binance Provider → Channel<MarketEvent> → MarketEventWorker → RuntimeStore → FeatureWorker → MarketService → API / UI`

`SnapshotWorker → Channel<PersistenceCommand> → PersistenceWorker → SQLite`

实时 WebSocket 不接触 SQLite、UI 或 FeatureEngine。单个资产的状态在锁内更新并返回不可变快照；历史回填按时间合并，旧行情不能覆盖新行情。写库成功确认后才推进快照游标，失败下轮重试。

## 存储

- `Data/PulletCryptoRadar.db`：Asset、ExchangeInstrument、ProviderState、Settings、SchemaVersion。
- `Data/PulletCryptoRadar.Market.db`：MarketSnapshot5m（保留 90 天）、MarketSnapshot1h（永久）。
- Phase 5 新增 DerivativeSnapshot（90 天）与 LiquidationObservation（14 天），仍经单 PersistenceWorker 写入。数据库版本升级为 4。
- Phase 6 升级为版本 5：ReplayFrame 与雷达事件原子写入（gzip，默认 7 天），EvaluationEntry 冻结通知输入，SignalEvaluation 保留最近 168 份评估报告。回放不会写入真实 Episode 或发送通知。
- WAL、NORMAL、foreign_keys、busy_timeout=5000、temp_store=MEMORY。
- 金额保存十进制 TEXT，读取时使用 invariant culture 恢复 decimal；缺失金额为 SQL NULL。
- 每 30 秒检查新增的**已闭合** 5m K 线，并合成连续、完整的 12 根 5m 对应的 1h K 线。不会每秒写行情；尚未实现 WatchList，故没有 WatchList 1m 表。
- 每天上海时间 04:30 后的第一个调度周期执行清理及 SQLite Backup API 备份（当天启动晚于该时间也执行），每个数据库保留 14 份。
- 市场库以 AssetId 关联业务资产；跨库不存在物理外键。合约符号保存在 ExchangeInstrument，AssetId 在重新同步和下架后保持稳定。CoinGecko 身份通过 AssetMapping 单独管理；未核对身份保留 Unknown / SymbolMatched / Ambiguous。

## 指标约定

- 内部 UTC；币价、金额和百分比收益率为 decimal，统计量为 double。显示的收益率单位为百分数。
- 1m 环 2880 根、5m 环 2016 根、15m 环 2880 根；固定容量，拒绝无效 K 线，支持批量乱序回填。
- 1/3/5/15/30/60/240 分钟收益率用 1m 历史；24h 用 5m；3d 用 15m。当前价相对于**源时间所在周期边界减去周期长度**对应的闭合价计算，期间缺根则 Unknown。桶对齐意味着 24h/3d 不是逐秒滑动收益率。
- 价格 RZ 使用最近闭合的 5m/15m 收益率；成交量 RZ 使用最近闭合 5m 的 quoteVolume，绝不用 24h 累计量差值。
- `RobustZ = (X - Median) / (1.4826 × MAD)`，当前观测不进入历史基线。至少 30 个基线样本；MAD ≤ 1e-12 时 Unknown。
- `Percentile = 100 × (小于当前的数量 + 0.5 × 等于当前的数量) / 样本数`。普通币用 5m 历史（最多 7 天），上市不足 7 天的币用同一时刻其他合约的有效 5m 收益率横截面，明确显示 CrossSection。没有将其伪称为 30 天历史百分位。
- 突破距离为当前价相对之前完整窗口最高价的百分比；排除最新闭合 K 线。提供 1h、4h、24h、7d。
- Ticker 超过 30 秒为 Stale；5m/15m 统计量容许一个完整周期及接收延迟。字段同时保留 SourceTime、ReceivedTime；Provider Health 与行情新鲜度独立。
- UI 每 2 秒读取应用快照；FeatureWorker 每 5 秒计算。UI 的“已就绪”指 5m RZ 可计算，不表示全部历史已回填完成。

## API

`GET /health`、`GET /api/assets`、`GET /api/assets/{id}`、`GET /api/assets/{id}/history?interval=1|5|15`、`GET /api/providers/health`、`GET /api/settings`。

当前配置通过 EXE 旁的 appsettings.json 修改并重启生效。高级阶段的写入型 Settings、WatchList、Alert API 尚未开放。

## 设计依据与验收

- [唯一正式规格书](docs/Specification-v1.0.md)（原文归档，工程名称统一采用 PulletCryptoRadar）。
- [本轮实施与验收记录](docs/Phase02-Implementation.md)。
- [Phase 3 实施与验收记录](docs/Phase03-Implementation.md)，包含评分公式、阶段阈值、Episode 规则、数据库升级和测试结果。
- [Phase 4 实施与验收记录](docs/Phase04-Implementation.md)，包含通知等级、预算、Top-N、去重、持久化发送队列及 Windows Toast。
- [Phase 5 实施与验收记录](docs/Phase05-Implementation.md)，包含衍生品口径、V2 公式、冷却处理及真实采集状态。
- [Phase 6 实施与验收记录](docs/Phase06-Implementation.md)，包含时间因果约束、研究入口、指标公式、快照覆盖及验收结果。
- Binance [WebSocket 连接文档](https://developers.binance.com/en/docs/products/derivatives-trading-usds-futures/websocket-market-streams/Connect)、[Market 流定义](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market)。实现采用当前 `/market/stream`。

新增 API：`GET /api/volatility`、`GET /api/volatility/events?assetId=1&limit=50`、`GET /api/episodes?assetId=1&limit=50`。不传 assetId 时返回全市场记录，limit 限制在 1～200。

雷达参数位于 appsettings.json 的 Volatility 节，修改后重启生效。发布脚本保留已有配置，并仅补入缺失的顶层配置节。单 PersistenceWorker 同时写入新增的 RadarScan、MoveEpisode、RadarEvent；迁移保留旧行情和资产，不重建数据库。

新增 API：`GET /api/alerts?limit=50`（通知记录）和 `GET /api/alerts/policy`（最新等级/预算/策略结果）。均为只读接口。

Alert 参数在 appsettings.json 的 Alert 节。普通通知保留每日 5 条、滚动 30 分钟 2 个新标的的默认上限，默认先缓冲 60 秒参与竞争；通知记录和预算在重启后保留。`ToastEnabled=false` 关闭本应用的 Windows Toast，不修改系统通知设置。

Phase-3 基础模型置信度最高 60；V2 按实际可用证据计算完整度，有条件达到普通 A 通知最低 70。市值/链上风险仍未知，不生成 Risk.Low 的 S 级判断。全市场系统性事件依照独立市场广度门槛通知。Windows 提交通道使用当前用户开始菜单快捷方式与 `Pullet.PulletCryptoRadar` 身份，首次发送会注册本应用身份；需要 Windows 桌面会话和 Windows PowerShell 5.1。Submitted 只表示系统已接受 API 提交，专注模式/系统通知设置仍可能影响实际显示。

衍生品参数位于 Derivatives 配置节：全市场 OI 默认 300 秒一轮；候选池默认 80 个合约，按优先标的、异动统计量及成交量排序。统计接口另有每分钟 120 请求的节流，与全局权重及 Retry-After 冷却共同生效。实际周期可能因限流、故障和回填而延长，源时间决定数据有效性。

新增只读 API：`GET /api/assets/{id}/derivatives?limit=100`，每类最多 500 个原始样本；计算结果在 `/api/assets/{id}` 的 `features.derivatives` 下。强平连续覆盖 15 分钟前显示 Unknown；即使覆盖充分，也只是 Binance 抽样流的本机观测值。

评估参数位于 Evaluation 节：LookbackDays=3、PrecisionK=3、MaximumSignals=5000、ReplayRetentionDays=7。评估启动时及每小时执行；点击“更新评估”可提前重算。24h 窗口从信号后首个 5m 开盘开始，未模拟交易成交、手续费或滑点。原始 K 线缺口与观察未到期不能计为预测失败。

Phase 6 API：`GET /api/evaluation`、`POST /api/evaluation/refresh`、`GET /api/replay/availability`、`GET /api/replay/audit?limit=100`、`POST /api/replay`（JSON：from、to、symbol）。回放区间最多 6 小时 / 360 个快照；输入仅使用当时保存的生产特征及参数，不允许把当前缓存行情带回历史。连续快照从 Phase 6 起开始积累；早期只有事件输入时可做事件复算，不能伪称完整全市场分钟回放。

当前仅提供研究观察与风险预警。下一阶段为 Phase 7：CoinGecko + Asset Mapping。


## Vue 迁移与 Phase 7

前端已迁移 Vue 3，沿用 ASP.NET Core API 和 SQLite。入口新增[资产映射](http://localhost:5218/mappings)，支持目录搜索、候选核对、人工确认、链与地址校验及修改历史。CoinGecko 的市值、FDV、供应量、ATH 默认每 30 分钟采集；目录默认每天更新。身份未经确认时，仅展示候选市值，不计算可信比率。

Phase 7 不改变 V2 检测器及历史评分。后续 Phase 8 才接入新的评分逻辑。

开发时先启动后端，然后在另一个终端运行：

```powershell
cd frontend
npm ci
npm run dev
```

Vite 将 `/api` 和 `/health` 代理至 localhost:5218；生产构建输出到 Host/wwwroot，由同一宿主提供。源代码修改后使用 `./scripts/Publish.ps1` 发布，先关闭已运行的发布版，保留 Data 与配置。

- [Vue 迁移说明](docs/Vue-Migration.md)
- [Phase 7 实施与验收](docs/Phase07-Implementation.md)

## Phase 8：妖币雷达与备用来源

[妖币雷达](http://localhost:5218/speculative) 已提供独立 Speculative-1.0 异常分、风险分、固定权重覆盖、候选筛选与历史输入复算。与 V2 并行，不改变原通知逻辑。缺失身份、市值、OI 或成交额时，完整分数保持 Unknown。

Binance 永续数据不足时可查看 Gate 永续；Coinbase 作为 USD 现货参考。各交易所原生合约和时间分开保存，绝不把现货量当作永续 OI。Gate 与 CoinGecko 两层身份均需核对后才能计算可信市值评分。详情可直接跳到该资产的映射页。

Speculative 与 Alternatives 为新增配置节，发布脚本自动补入。完整公式、来源限制、接口、存储及验收见 [Phase 8 实施记录](docs/Phase08-Implementation.md)。

## Phase 9：WatchTower

[自选监控](http://localhost:5218/watchtower) 支持成本、目标、关注价、手动支撑/压力、个人提醒和钱包标签；输出状态、风险、可解释依据及下一观察点。ARIA / 成本 0.40 是规格书示例，个人提醒默认关闭，不代表实际持仓。

价格新鲜且身份可用时分别计算相对成本涨跌和回到成本所需变化；缺失链上、钱包证据保留 Unknown。历史判断可复算，个人提醒沿用既有 Windows outbox 的去重、状态和失效逻辑。详情见 [Phase 9 实施记录](docs/Phase09-Implementation.md)。

## Phase 10：链上分布

[链上分布](http://localhost:5218/onchain) 已加入 ARIA 的 BSC 精确合约参考，采集持币人数与 Top100，展示 Top10/25/50/100、调整后集中度、地址分类和历史。人数变化需累积可靠的同提供者 24h 基线；分类覆盖不足时集中度不用于可信评分。

BSC / Ethereum 使用 Binplorer / Ethplorer；其他支持链的 GoPlus Top10 只提供明确覆盖的字段。可在本机配置可选 PULLET_EXPLORER_API_KEY，默认公开开发接口受持久化预算约束。WatchTower 与妖币雷达升级为独立 1.1 版本，旧版本仍可复算。见 [Phase 10 实施与验收](docs/Phase10-Implementation.md)。


## Phase 11：地址核验与资金流

新增 /flows，BSC 自选资产按区块增量采集 Transfer 事件，原始数量保持精确字符串；保存断点、去重、代码核验证据及采集预算。WatchTower-1.2 接入经过归属与时间门槛的资金流风险，旧版本继续复算。当前 ARIA 大户归属尚缺充分证据，保持 Unknown，不自动认定项目方。详见 [Phase 11 实施与验收](docs/Phase11-Implementation.md)。


## Phase 12：工作台与两套策略研究

主导航收拢为今日工作台、观察列表、每日复盘、研究工具。新增1000 USDT虚拟账本和十份保证金规划，当前自动开仓关闭；研究提前埋伏与涨幅榜机会，未完成验证的固定止盈止损原型不会启用。导入LAB、MYX、BTW、APR的1396根闭合日线，分别标记OI与链上历史覆盖缺口。详见 [Phase 12 范围、研究设计与验收](docs/Phase12-StrategyResearch.md)。

历史样板扩展：新增 /template-library，17个原生期货合约、20个预选时段，按币安公开档案校验后展示价量、事件窗口OI、实际结算资金费和部分标记价。含TRB、YFI、GAS、UNFI、ACT、OM、旧LUNA以及BTC/ETH对照。自动开仓保持关闭。详见 [历史行为样板报告R1](docs/research/templates/历史行情行为样板报告-R1.md)。

Phase 13：新增 /strategy-replay 两套结构策略R1离线诊断（24组、17合约，未通过独立验证，不启用自动开仓）与 /network 省流/持久化用量。默认关闭实时流，全市场约60秒、重点7标的约20秒补充；每日正文负载150MB、每月5GB预算，非VPN账单封顶。详见 docs/Phase13-StructureReplay-Network.md，离线复现脚本 scripts/replay_strategy_research.py，不额外下载历史。

Phase 14：/forward 连续前瞻观察、/journal每日覆盖复盘，复用本地行情、不新增外部请求、不启用交易。默认双击“启动服务器.cmd”显示日志窗口并打开网页；“停止服务器.cmd”仅停止本项目进程。操作见 docs/日常启动与停止.md；规则与限制见 docs/Phase14-ForwardObservation.md。前瞻记录独立持久化，关闭服务器期间不会采集。

Phase 15：双向结构实验模拟，恢复按钮可启用。1000分10份、最多5笔、每份保证金最多100/10倍；量价与OI确认后才接收新意向，结构止损、30%/30%分批与40%尾仓。20%亏损仅复盘提醒，不停止或补本金。单笔风险按权益0.6%且最多6、组合3%且最多30。细节与假设见 docs/Phase15-ExperimentalLongShort.md；实际启用状态见工作台。

Phase 16：新增 [提前埋伏与涨幅机会](http://localhost:5218/opportunities)，全市场粗筛与动态60币深度池。身份未知不再阻断交易所量价研究，链上证据仍独立核验。观察每分钟更新、每5分钟留档90天；研究计划不能作为模拟成交输入。模拟候选池同步扩大，持仓、余额、结构止损与风险上限保留。详见 [Phase16-OpportunityPools.md](docs/Phase16-OpportunityPools.md)。

十倍妖币研究修订：提前组改为30日十倍目标、低流通市值身份核验与非托管筹码集中度时序，不再混入BTC/ETH或沿用72小时窄幅硬门槛；原有趋势模拟独立。基于已缓存17合约完成30日标签诊断，概率尚未校准。详见 [Tenfold-Research.md](docs/Tenfold-Research.md)。

独立手动辅助：[代币快查](http://localhost:5218/token-check)，输入名称、代码或完整合约/Mint，先分辨同名项目，再核对行情、估值、小时结构、OI与链上证据。当前参与判断与潜力依据分别解释，报告保留原时间和缺失；不修改妖币池、自选、映射或模拟账户。详见 [Token-QuickCheck.md](docs/Token-QuickCheck.md)。

服务器启停与存储加固：停止脚本改为正常退出并排空已接收写入，运行中的发布被拦截，同一数据目录限制单实例；读取压缩记录增加大小上限、完整性检查与错误指纹。历史底层退出的根因尚未确认。操作见 [日常启动与停止](docs/日常启动与停止.md)，验证与限制见 [稳定性记录](docs/Runtime-Stability-2026-10-31.md)。

代币快查第二阶段：前十大地址的人工归属、历史线索与未知项分开展示，复用本地转账分析直接往来/共同代币来源，提供Bubblemaps入口；AIO/ZBT原文条目和24h/72h/7天独立跟踪留档。完整解锁日历、最新新闻归因及多跳控制权仍缺覆盖，不能将大额地址认定为庄家。详见 [地址与项目研究](docs/Token-Research-2026-10-31.md)。

持续筹码与解锁观察：快查页可展开固定地址余额、AIO锁仓合约当前参数与过滤转账证据，按确认区块留档并恢复断点；ZBT等待完整身份门槛。7天普通释放上界采用当前合约参数，不当作确定解锁量或卖压，24h变化等待真实基线。详见 [持续监测说明](docs/Token-Monitoring-2026-10-31.md)。

连续运行与每日研究：默认启动由当前用户的按需计划任务托管独立守护，正常停止不拉起，异常按15/30/60秒有限恢复；没有开机/登录触发器。全局显示守护/断线及可能断档，`/journal`汇总筹码、释放参数和报告后续观察，缺失不作零。退出根因仍未确认，验证范围见 [连续运行与每日研究](docs/Reliability-DailyResearch-2026-10-31.md)。
