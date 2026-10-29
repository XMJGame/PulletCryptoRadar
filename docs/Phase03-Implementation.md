# PulletCryptoRadar v1.0 · Phase 3 实施记录

日期：2026-10-29（Asia/Shanghai）。本轮对应《规格书 v1.0》Phase 3：Volatility Radar V1。沿用 Phase 0～2 数据链路，增加 Scanner 项目及 Scanner.Tests；没有实施通知预算、Toast、S/A/B/C、OI、主动买卖盘或链上功能。

## 已实现

- UpMoveDetector、DownMoveDetector：独立逻辑与版本号，分别输出研究分数、数据状态、阶段、候选标记、家族确认和缺失证据。
- MarketRegimeDetector：1h 广度、中位数、BTC/ETH、±5%/±10% 计数、样本覆盖率与 Normal/BroadRiskOn/BroadRiskOff/SystemicPump/SystemicCrash。
- EpisodeEngine：创建、更新、升级、过热、冷却、结束，阶段升级保留 ID；无变化不重复生成事件。缓存缺失暂停、反转关闭旧方向并创建新方向。
- VolatilityRadarService/Worker：默认每 60 秒扫描，读缓存，使用单写入队列保存成功后才发布快照；写入失败保留上次提交状态并在下轮重试。
- SQLite SchemaVersion 2：RadarScan、MoveEpisode、RadarEvent，事务落库、外键及幂等事件 ID；保留旧数据库内容。启动恢复最新 Episode，避免把同一行情拆成多个记录。
- `/volatility` 雷达页：市场状态、候选搜索/方向筛选/分页、评分说明、确认/缺失证据、活跃 Episode、已保存事件。行情页新增导航入口。

## Phase 3 评分约定

规格书的完整权重还包含后续阶段数据，因此本轮使用明确版本 `UpMove-Phase3-1.0` / `DownMove-Phase3-1.0`。本轮分数不是完整策略分数，不应用完整策略的 82/75 通知阈值。

本轮保留已有家族的相对权重：上涨 Price 15、Volume 20、Structure 10；下跌 Price 20、Volume 15、Structure 10。以固定分母 45 归一到 0～100。结构缺失不会提高剩余部分的权重；核心价格或成交量缺失时分数为 null，阶段 Unknown。数据新鲜度在检测时重新检查，不只信任 FeatureValue.Status。

定义 `clamp(x)=min(1,max(0,x))`：

- 上涨 Price 强度：`(clamp(max(Return15m/6,Return5m/3)) + clamp(PriceRZ/3))/2`。可用的 5m/15m RZ 取较大值。价格确认需 5m 或 15m ≥2% 且 RZ≥2。
- 上涨 Volume 强度：`clamp(VolumeRZ/3)`，确认需 RZ≥2。
- 下跌 Price 强度：`0.65*clamp(max(-R1m/1.5,-R5m/3,-R15m/6,-R1h/12,-R4h/25)) + 0.35*clamp(-PriceRZ5m/3)`。价格确认需 1m≤-1% 或 5m/15m≤-1.5%，同时 RZ≤-1.5 或出现大幅下跌（1m≤-3%、5m≤-5%、15m≤-8%）。RZ 缺失时，仅允许大幅价格冲击路径，说明中标记 RZ Unknown，不推断 OI/强平原因。
- 下跌 Volume 强度同上，但确认阈值为 RZ≥1.5，体现下跌侧不同敏感度。
- Structure 用前 1h/4h/24h 的高点或低点距离。突破/跌破缓冲默认 0.1%；确认后的强度分别为 0.55/0.8/1。多个周期取最长已确认周期。新增 DistanceToLow，且结构历史必须结束在当前源时间对应的周期边界，拒绝旧窗口造成的假突破。
- 缺失值不作为指标零值；评分中未提供证据的组件不贡献分数，missingFamilies 单独列出。默认候选需 Price 与 Volume 都确认，且基础分≥45。
- 置信度按本轮可用权重计算并限制最高 60；新上市币最高 45，明确缺少完整策略证据。这是证据完整程度约定，不是统计命中概率。

## 阶段约定

上涨：Price 与 Volume 确认后为 Preheat；额外确认 1h 结构为 Ignition，4h/24h 结构为 Breakout；1h≥20% 或 4h≥35% 为 Acceleration；15m≥25%、1h≥40% 或 4h≥70% 为 Overheated。缺少资金方向等数据，不输出 Distribution。

下跌：确认后为 Weakening；结构确认后为 Breakdown；5m≤-5%、15m≤-8% 或 1h≤-12% 为 Selloff；1m≤-5%、15m≤-15% 或 1h≤-25% 为 Crash。不输出 ShortBuild 或 LongLiquidation，避免用价格猜测持仓和强平。

## 市场状态约定

只统计源数据新鲜且有有效 1h 收益率的资产。至少 30 个样本且覆盖率≥70% 才分类，否则 Regime=null、Status=Missing；不把预热中的市场标成 Normal。

- ≥75% 同向且中位幅度≥5%：SystemicPump/SystemicCrash。
- ≥70% 同向且中位幅度≥1%：BroadRiskOn/BroadRiskOff。
- 其余充分样本市场：Normal。

这些阈值是可配置的第一版研究参数，尚未经历史校准。本阶段记录市场整体状态，通知去重在 Phase 4 实施。

## Episode 与持久化

每个资产同一时刻最多一个活跃方向。保存起始/当前价、运行中观察到的最高/最低价、起始/当前/最高分、最近观察及极值时间、方向、版本、阶段、状态和结束原因。最高/最低是每分钟检测快照中观察到的极值，不等于逐笔市场极值。

- 相同方向阶段变化仍属于同一 Episode；阶段变化或相对最近记录累计变分≥5时写事件。
- 非候选但分≥40进入 Cooling；分<40结束。
- 连续两小时没有新方向极值结束为 NoNewExtreme；保留冷却保护，直到方向新极值、反向候选或候选消退，避免每分钟重建相同事件。恢复时也保留此保护。
- 数据缺失不触发冷却或结束，也不更新极值；恢复后的观测间隔若≥两小时，关闭旧 Episode（DataGap）并允许新 Episode。
- 资产不再活跃时结束；空缓存冷启动不视为下架。
- 每次保存新事件时包含原始 MarketView/AssetFeatures、完整组件说明、版本、Episode 快照，所属 RadarScan 保存当时参数与市场状态，供之后实现 Replay 使用。当前只有审计输入保存及确定性重算测试，完整 Replay/Evaluation 留在 Phase 6。

所有 SQLite 写入仍经单 PersistenceWorker，不在检测器或 UI 直接写库。失败不推进应用已提交状态；重复提交同一个持久化批次不会插入重复事件。

## 验证

本轮最终自动化测试：**86 项通过，0 失败，0 跳过**（Core 31、Scanner 37、Integration 18）。

新增覆盖：单纯价格涨幅不进入候选、上涨家族确认与阶段升级、下跌独立敏感度与冲击路径、缺失/过期/未来源时间拒绝、新上市置信度、结构缺失不增权、市场样本覆盖、系统性行情、同 Episode 升级、事件重复抑制、冷却/反转/超时/缺数据/下架、旧数据库迁移、事务回滚、幂等写入、写入失败保留快照、重启 ID 恢复、原始输入持久化及确定性重新计算。

Windows x64 自包含程序发布到 `artifacts/win-x64`。真实行情验证已扫描 522 个合约，生成上下行候选、Episode 和持久化事件；连续扫描保留同一 Episode ID。浏览器已验证搜索、候选过滤、方向与依据详情；页面、静态资源和三个新增 API 均返回 HTTP 200。截图见 [雷达页验收截图](Phase03-Radar.jpg)。冷启动历史覆盖不足时 MarketRegime 保持 Unknown，与预期一致。长时稳定性及阈值命中率需要继续积累真实样本，未声称具备经过验证的预测能力。

最终发布后再次重启正式程序验证：重启前 8 条 Episode 记录全部保留原 ID，2 个活跃 Episode 成功恢复；`/health` 返回 Ready、Phase=0-3。恢复中的行情重新预热，不把未观测期间视为连续行情证据。
