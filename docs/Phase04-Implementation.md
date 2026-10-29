# PulletCryptoRadar v1.0 · Phase 4 实施记录

日期：2026-10-29（Asia/Shanghai）。对应唯一正式规格书的 Phase 4：S/A/B/C、DailyBudget、TopN、EpisodeDedup、MarketWideDedup、Windows Toast。本轮没有实施 Phase 5 衍生品采集或改变 Phase 3 的评分权重与置信度。

## 实现与入口

- Scanner：SignalGrader、AlertOptions、AlertPolicyEngine。
- Application：AlertService；通过 IRadarSnapshotSource 读取已经提交的雷达快照。
- Infrastructure：AlertRepository、AlertPersistence、AlertWorker、WindowsToastSender，新增单写入队列命令。
- Host：DI、配置校验、只读 REST API，随发布包携带 Windows 通知 helper。
- Web：`/alerts` 通知中心、等级筛选、合约/拦截原因搜索、分页、预算及发送状态说明；行情/雷达页加入导航入口。
- API：`GET /api/alerts?limit=50`、`GET /api/alerts/policy`；没有暴露对外发送消息或写入合成行情的测试 API。

## 等级与数据门槛

- S：Score≥92、Confidence≥80、至少4个独立特征家族，且 Risk 明确为 Low。
- A：上涨 Score≥82；下跌风险 Score≥75（采用规格书下跌侧较敏感门槛）；Confidence≥70、至少3个独立家族。
- B：Score≥70但证据尚未达到 A/S，包括高分而置信度不足的信号。
- C：Score<70。数据无效、缺失或分数非法时为 Unknown。

特征家族去重，OI 与 OpenInterest 视为同一家族，任意字符串不被计作额外独立证据。普通通知还检查实时价格和快照新鲜度、候选标记及对应的活跃 Episode。

**Phase 3 基础模型仍然最高 Confidence=60。** 因此当前普通单币信号不会达到 A/S 通知门槛，继续显示在雷达和策略评估中。完整模型的深度数据来源在 Phase 5 以后接入，不能通过提高固定置信度或降低标准制造通知。

## Risk Gate 与阶段

上涨仅允许 Ignition/Breakout/Acceleration，High/Critical 风险否决；Preheat、Overheated、Distribution、Cooling 不作为新上涨机会通知。下跌仅允许已确认的 Breakdown/Selloff/Crash。系统性下跌期间合并全市场事件并抑制普通单币通知；系统性上涨期间合并同向上涨通知，下跌风险仍独立过滤。

WatchList Critical 预算豁免路径以 `IsWatchListCritical + Risk=Critical + Down` 为入口，仍需有效数据与 A/S 级证据，绕过普通每日预算、Top-N 和市场合并抑制。该规则已经用合成输入验证；当前没有 WatchList 数据来源，Phase 3 检测器不设置此标记，实际个人监控在 Phase 9 接入。

## 预算、排名与 Episode 去重

- 默认普通通知每天最多5条，按 Asia/Shanghai 自然日计算；不是按进程启动或 UTC 日期重置。
- 滚动30分钟最多2个新标的；已占用标的的升级仍消耗每日条数，但不增加窗口内独立标的数。
- 默认符合条件后等待60秒竞争缓冲。每次新扫描都重新验证当前证据，按等级、分数和稳定符号顺序排序；不使用已经失效的旧候选填满额度，也不承诺预测未来30分钟最高分。
- 同 Episode 最多2次通知。第二次要求相对首条分数提升至少12分，或 Ignition/Breakout→Acceleration，或下跌升级为 Crash。
- 提交失败、过期、系统禁用或无法确认结果时保留预留额度和 Episode 去重记录，避免反复尝试弹出。
- 系统性事件不占普通预算。持续同一市场事件使用同一个键；Unknown 不重置去重。同类型市场事件有默认120分钟去重冷却，短暂 Normal→Crash 摆动不会每分钟重复通知；恢复后超过冷却期可创建新事件。

参数均位于 appsettings.json 的 Alert 节，启动校验，重启生效。没有实现自动修改 Windows 专注模式或系统通知许可。

## 持久化与可靠性

业务库迁移到 SchemaVersion 3，保留已有资产、行情和 Episode：

- AlertCheckpoint：候选竞争缓冲、系统事件键、最近处理扫描，重启恢复。
- AlertRun：扫描 ID、评估时间、参数快照和审计结果。持久化高分/关联 Episode 的评估结果，保留7天；当前全部评估结果保存在应用快照供 UI 查询。
- AlertEvent：预留通知、唯一去重键、发送状态和详细内容。通知历史保留，用于跨日预算及长期 Episode 去重。

全部写入通过单 PersistenceWorker，策略运行与预留记录在同一 SQLite 事务提交，唯一键约束阻止重复预留。每次调度读取数据库权威状态，即使提交成功而回执丢失，也不会遗失待发送记录或重复扣额度。

发送流程：`Pending → 原子领取 Dispatching → Windows API → Submitted / Disabled / Failed / Indeterminate`。过期 Pending→Expired。不盲目重试已领取记录；重启发现 Dispatching 或提交结果保存失败时，转为 Indeterminate，避免把可能已经显示的消息再次弹出。Windows 和 SQLite 之间没有原子事务，无法承诺恰好一次展示；本实现选择保守去重并明确标注不确定结果。

## Windows Toast

通过独立隐藏 Windows PowerShell 5.1 进程调用 Windows.UI.Notifications；设置20秒超时，不启用 ExecutionPolicy Bypass，不更改系统执行策略。输入使用 Base64 JSON 环境变量，通知标题/正文写入 XML 文本节点，不将信号内容拼接成脚本。

首次发送创建当前用户的 `PulletCryptoRadar.lnk`，写入 PKEY_AppUserModel_ID，并注册本应用的当前用户 AppUserModelId 显示名称与图标。AUMID 为 `Pullet.PulletCryptoRadar`。不改系统通知开关；能获取到 Disabled 设置时记录 Disabled。部分未打包桌面身份的 Setting 查询会返回 ElementNotFound，此时允许尝试 Show，并只记录 API 接受提交的结果。

Toast 默认为静音，点击协议链接打开本地 `/alerts`；通知有效期默认120秒，Tag 基于通知 ID，避免相同通知在系统中心生成多份。Submitted 不证明用户已经看见提醒。

实现参考：[Microsoft 桌面 Toast 身份/快捷方式说明](https://learn.microsoft.com/en-us/windows/win32/shell/enable-desktop-toast-with-appusermodelid)、[PKEY_AppUserModel_ID 定义](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)。

## 验证结果

**122 项测试通过，0失败、0跳过**：Core 31、Scanner 64、Integration 27。覆盖等级与置信度门槛、独立家族去重、下跌门槛、Top-N 竞争、上海时间跨日、滚动窗口、Episode 两次上限/升级、过热/高风险过滤、系统事件预算豁免/状态抖动去重、WatchCritical 豁免以及持久化队列、事务回滚、原子领取、重启恢复、提交回执丢失、发送结果保存失败和过期不补发。

Windows helper 已以明确的“通知测试，不是行情信号”内容实际调用，返回 `Status=Submitted`。测试没有写入生产行情或制造真实行情警报；自动化单元/集成测试通过替身发送器运行，不弹出系统通知。真实系统性暴跌/暴涨事件尚未发生，不将合成策略测试等同于真实市场命中率验证。

发布版 `/health` 返回 Ready、Phase=0-4；真实扫描达到522个合约，通知策略评估1044个方向结果，普通通知额度为0/5，高分低置信度信号正确显示“数据置信度不足70，仅保留雷达观察”。Alert Policy、Volatility Radar 和 SQLite 健康检查正常。浏览器已验证等级筛选与搜索，截图见 [通知中心](Phase04-Alerts.jpg)。长期稳定性、真实系统性事件的连续运行表现仍需积累。

本轮到 Phase 4 为止。下一阶段为 Phase 5 Derivatives 深度数据，未提前实施。
