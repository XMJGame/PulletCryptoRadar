# Phase 6 · Replay + Evaluation

工程：PulletCryptoRadar v1.0。实施日期：2026-10-29。依据唯一正式规格书第 104～114 节及 Phase 6。

## 交付范围

已实现 ReplayEngine、SignalEvaluation、MFE、MAE、Hit20、Hit50、Drop20、Drop30、CaptureLead、CrashLead、Precision@K、ExtremeMoveRecall；新增 `/evaluation` 页面、只读结果 API、手动运行 API、每小时 EvaluationWorker 和持久化结果。没有加入交易执行、自动调参、机器学习或 Phase 7 资产映射。

## 三类历史能力

| 能力 | 输入 | 算法和约束 |
| --- | --- | --- |
| 事件复核 | RadarEvent 中的 MarketView / Assessment + 对应 RadarScan 的历史参数 | 复用 Up/Down Detector，检查版本、评分、阶段、置信度、候选状态、风险、确认家族、组件。输出 Match / Mismatch / MissingInput / UnsupportedVersion / InvalidInput |
| 连续快照回放 | Phase 6 开始录制的完整市场 ReplayFrame | 生产 FeatureEngine 的历史输出 + 冻结 Volatility/Alert 参数；按严格时间顺序复用 Detector、MarketRegimeDetector、EpisodeEngine、SignalGrader、AlertPolicyEngine |
| 原始输入重算函数 | 明确提供的历史 MarketView、Candle、DerivativeRuntimeSnapshot | ReplayEngine.Recalculate 调用生产 FeatureEngine / DerivativeFeatureEngine；仅接受已经闭合且 SourceTime、ReceivedTime 都不晚于历史时刻的数据 |

没有另写一份 BacktestStrategy。页面主要使用连续特征快照模式，**不是 tick 级行情重建，也不能改变历史特征的计算参数进行重算**。原始输入函数不提供过去从未记录的 tick、资金费率或 1m 数据；单资产函数不重造当时的全市场新币横截面。历史事件复核只检查该事件的检测器结果，不代表复原整段市场或通知流程。

ReplayFrame 保存所有活跃合约当时的 MarketView、ScanId、AsOf、Volatility 参数、Alert 参数及 CaptureVersion。不得从当前 runtime 取价格补入过去，不得使用未来特征源时间。输入排序错误、重复时刻、未支持的引擎版本和未来时间会拒绝运行。快照源时间与价格接收时间须不晚于当时；原始重算还过滤延迟回填与未来修订。

回放默认空 Episode / 预算起点，按每次扫描推进。同一套通知策略会产生模拟 Pending 预留记录，但不调用 WindowsToastSender、不写生产 Episode、不使用真实通知预算。因为没有起点之前的完整政策状态，也没有真实 AlertWorker 调度延迟，**不宣称模拟通知逐条等同历史实发通知**。

每次最多 6 小时 / 360 个快照，全市场参与计算；页面选择合约仅控制轨迹显示。缺口超过相邻时刻配置扫描周期的两倍则单独计数。读取时逐帧解压，避免一次载入整个市场数小时的 JSON。SQLite 时间索引用毫秒，API 可用端点从 payload 恢复精确 ticks，避免闭区间漏掉最后一帧。返回并展示每帧合约数与新鲜价格数；早期预热或覆盖不足的历史帧原样保留，不伪造补齐。

## 信号样本与研究入口

- 雷达评估按 `Created` 事件，每个 Episode 只取首次信号；后续涨分与阶段事件不重复增加样本。
- 普通通知预留记录单独作为通知样本。EvaluationEntry 在通知持久化时冻结对应扫描的价格和参考价，与 AlertEvent 在同一事务提交，不受短期 ReplayFrame 清理影响。
- 旧通知缺少当时输入时仍保留样本，但价格标记不可用，不以现在的价格、附近事件或后来的开盘价假装恢复信号输入；其 Precision 维持 Unknown。
- 评价窗口入口是**信号后首个 5m 边界的开盘价**，延迟为 0～5 分钟；窗口结束为入口 + 24h。这是可复核的研究代理入口，不是实际可成交价格。
- 不使用横跨信号时刻的旧 K 线高低点，因为无法知道极值发生在信号前还是后。入口 K 线必须真实存在，不能跳到更晚的第一根可用 K 线。
- 仅使用同合约、有效 5m K 线，且 EndTime、SourceTime、ReceivedTime 不晚于评估截止时刻；排除窗口之外的极值。重复 OpenTime 选择已接收的最新修订。

完整结果要求恰好 288 根连续 K 线，首根从入口开始、末根在截止结束。状态：AwaitingEntry（入口缺失）、Pending（未到 24h）、Incomplete（已到期但有缺口）、Complete（完整）。前三种的 Hit/Drop 标签为 null，不计为 false。Pending/Incomplete 的 MFE/MAE 如可计算，仅表示已观测部分，并带 `*` 标记。

## 指标公式

设入口价格为 P，完整窗口最高为 H、最低为 L；有利和不利幅度最低为 0。

| 指标 | 定义 |
| --- | --- |
| Up MFE | max(0, H/P − 1) × 100 |
| Up MAE | max(0, 1 − L/P) × 100 |
| Down MFE | max(0, 1 − L/P) × 100 |
| Down MAE | max(0, H/P − 1) × 100 |
| Hit20 / Hit50 | 完整窗口上涨幅度 ≥ 20% / 50% |
| Drop20 / Drop30 | 完整窗口下跌幅度 ≥ 20% / 30% |
| CaptureLead | max(0, H − 信号时价格) / 冻结参考价 × 100，单位 pp |
| CrashLead | max(0, 信号时价格 − L) / 冻结参考价 × 100，单位 pp |

Lead 参考价优先从信号时有效的 24h Return 反推 `SignalPrice/(1+Return24h/100)`，否则从保存的 ExchangeReturn24h 反推；两者都不可用时 Lead 为 Unknown。该参考价在首次信号时冻结，不能用后来滚动变化的参考价。例：参考价 100，发现价 108，后续最高 162，CaptureLead=54pp；发现价 92、最低 60 时 CrashLead=32pp。Lead 与“入口之后百分比涨跌”不同，MAE 也不是从窗口最高点起算的峰谷回撤。

未模拟费用、资金费率支出、滑点、盘口、成交顺序、仓位或强平；指标不代表可执行交易收益。

## Precision@K

使用上海自然日，按上下行分别统计普通通知预留记录。一个标的当天同方向出现多次通知时取第一次；按该第一次的分数降序、符号稳定排序，选前 K 个。Up 成功标签为 Hit20，Down 为 Drop20。分母是实际选中的标的数，同时展示 K、Selected、Completed 和 Hits；不足 K 不虚增空名额。

只有整个统计日期落在查询范围内、当日结束、全部选中样本完整成熟、读取未被上限截断时才显示 Percent。不能排除尚未成熟的失败未知样本后，对剩余样本提前公布准确率。该指标衡量通知预留策略，不代表 Windows 实际显示或用户看到的通知。

## ExtremeMoveRecall

上海完整自然日，参考当天第一根 5m 的开盘价：上涨极端为最高 ≥ +50%，下跌极端为最低 ≤ −30%。当日合约集合采用本地已知、当天开始前已上市的合约（包含已失活历史合约），每币必须有完整 288 根价格 K 线才进入覆盖集合。

提前捕捉要求：同方向首次 Episode 信号出现在当天、严格早于首次达到极端阈值的 K 线**开始**，发现价尚未超过 +20% / −20% 的早期界限。不会猜测同一根 K 线内的信号和极值先后关系。

同时展示 ExpectedAssets、CoveredAssets、ExtremeAssets、CapturedAssets。扫描记录需覆盖当天，首尾距离边界不超过 2 分钟、相邻扫描间隔不超过 2 分钟、至少 720 个时间点。这里的连续覆盖指有扫描记录，并不证明每个输入字段始终有效。没有整日扫描、没有极端样本或记录被截断时 Percent 为 Unknown。价格不完整的标的不进入分母；结果只能解释为“覆盖集合内的召回”，不能冒充未经记录的完整历史市场召回。

启动合约注册与全市场快照读取共用注册锁，整个清单更新原子可见；扫描不能读取注册到一半的市场并误关其余资产的 Episode。并发测试在注册中途阻塞，验证读取只能等到完整清单发布。修复前录制的早期帧仍保留其原始覆盖，历史重放不能被当作每帧都完整的市场。

## 存储、调度和运行

业务库 SchemaVersion 升至 5，增量创建 ReplayFrame、EvaluationEntry、SignalEvaluation；保留原有行情、资产、Episode 和通知。ReplayFrame 与 RadarScan / 事件在同一事务中提交，重复 ScanId 幂等，失败整批回滚。

ReplayFrame 为 gzip blob，默认保留 7 天（可配置 2～30），写入新快照时清理过期帧。EvaluationEntry 为精确 decimal JSON，和原通知同寿命；SignalEvaluation 为压缩报告，仅保留最近 168 份，不保证手动多次刷新后仍覆盖完整 7 天。均通过单 PersistenceWorker 写入。回放读取不写生产状态。

EvaluationWorker 启动时执行一次，之后每小时执行。EvaluationService 的刷新与回放共用串行门，降低重复重负载。默认评估最近 3 天，最多读取 5000 个 Episode 和 5000 个普通通知；达到任一上限即标记 SignalsTruncated，并禁用 Precision/Recall 百分比。读取使用本地 5m 历史，不为评估额外轰击交易所 API。

配置：Evaluation.LookbackDays（1～7）、PrecisionK（1～20）、MaximumSignals（100～10000）、ReplayRetentionDays（2～30）。单次回放只保存于调用结果，不另写“正式回测成绩”；可以用 API 保存 JSON 供复核。没有实现参数调优，后续若调参必须按规格书拆分训练、验证、独立测试窗口。

## 页面与 API

- `/evaluation`：样本覆盖、状态、MFE/MAE、四类标签、Lead、详情、Precision/Recall、日期范围回放、历史事件复核。搜索、每页 20 条；轨迹最多展示最近 100 点。
- `GET /api/evaluation`：最新持久化报告。
- `POST /api/evaluation/refresh`：立即计算并保存报告。
- `GET /api/replay/availability`：精确可用范围、帧数、Created 事件输入数量。
- `GET /api/replay/audit?limit=100`：复核最近事件，limit 为 1～200。
- `POST /api/replay`：`{"from":"UTC or offset timestamp","to":"UTC or offset timestamp","symbol":"ARIAUSDT"}`。非法范围或不支持输入返回 400。
- `/health`：Phase `0-6`。

## 验证

Release：**185 项全部通过**（Core 65、Scanner 69、Integration 51），较 Phase 5 新增 36 项。涵盖：完整/未到期/缺口/入口缺失、方向性 MFE/MAE、Lead 的百分点口径、信号前极值隔离、未来接收修订、每日 Top-K 首通知去重、首日不完整、未成熟选中样本、整日 Recall 与保守越线时序、零样本 Unknown、生产评分与政策复用、历史参数、版本和时间顺序、未来特征、取消、原始回填因果约束、事务回滚、幂等和 decimal、迁移保留、帧清理、通知输入冻结、重启恢复、读取截断、端点小数秒精度。

真实数据初验：发布版健康入口已返回 Phase 0-6；原 124 条首次 Episode 信号被读取为评估样本，完整 24h 样本为 0，标签保持 Unknown。初次最近 100 条雷达事件中 86 条 Match，14 条 MissingInput（缺输入的事件不伪装为成功复算），0 条 Mismatch。连续快照从 2026-10-29 14:10:50（上海时间）开始积累，起始预热帧价格 Unknown，后续 ARIA 价格可用，未使用后来行情填补起始帧。

最终发布验收：185 项 Release 测试通过；已真实回放 16 个历史快照、7,993 个合约观测，时间缺口 0。旧帧合约范围 163～522，原子注册修复后的新帧为完整 522；结果明确展示该覆盖变化。当前评估已累计 125 条首次信号，完整 24h 样本仍为 0。最新 100 条事件为 83 条 Match、17 条 MissingInput、0 条 Mismatch。五个页面均 HTTP 200，发布进程保持后台运行。时间不同的初验与终验计数分别保留，不使用固定结果替代实际采集。

**限制：不能在上线当天得出真实 24h 准确率，也不能回造 Phase 6 上线前不存在的完整市场快照。** 旧事件仍可复核；完整评估与整日 Recall 需要持续采集并等待窗口成熟。Phase 5 的 Binance REST 冷却限制仍独立显示，实时流和 K 线连接继续运行并自动重连；缺失历史会直接体现在评估覆盖里。

验收截图与 API 记录见本目录 Phase06 文件。下一阶段：Phase 7 · CoinGecko + Asset Mapping。
