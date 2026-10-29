# PulletCryptoRadar v1.0 · Phase 0～2 实施记录

验收日期：2026-10-29（Asia/Shanghai）。唯一需求依据为归档的《CryptoRadar 技术实施规格书 v1.0》，工程名称按用户指令统一为 PulletCryptoRadar。

## 交付范围

Phase 0：Solution、DI、配置校验、Microsoft Logging + Serilog 文件日志、SQLite + WAL、Repository、RuntimeCache、Channel、Blazor 页面、Provider Health、本地 API。

Phase 1：ExchangeInfo 过滤 USDT/PERPETUAL/TRADING、稳定本地 AssetId、每日同步与下架标记；全市场 Ticker、MarkPrice、Funding；1m/5m/15m 已闭合 K 线；分组 WebSocket 订阅、接收超时、23 小时主动换线、指数退避及 jitter、重连后历史补缺。公共接口不需要 API Key。

Phase 2：固定容量 RollingWindow、多个时间周期收益率、Median/MAD/RobustZ/Percentile、价格与成交量特征、前高距离、新上市币标记及横截面价格百分位。冷启动先基础预热，再补齐完整历史。

工程结构、核心模型、算法定义、SQLite 表结构、调度和 API 详见根目录 README。源码依赖从 Core 向外展开，Provider DTO 不进入特征层。未创建 Scanner、Wallet 等后续阶段实现。

## 自动化验证

最终测试为 **43 项通过，0 失败，0 跳过**：Core.Tests 30 项、IntegrationTests 13 项。Debug 与 Release 均验证通过；TRX 位于本地 TestResults（不提交运行产物）。

测试覆盖：

- 奇数/偶数中位数、极端值、MAD、公式、零 MAD 与样本不足、百分位同值中秩。
- 固定容量淘汰、批量乱序回填、重复时间戳、无效 K 线。
- 1m/3m/5m/15m/1h 时间对齐收益率、缺根拒绝、失效行情按其 SourceTime 锚定。
- 当前观测不进入异常基线、quoteVolume 口径。
- SQLite WAL、连接 PRAGMA、十进制精度与 NULL、重启读回、稳定资产 ID 和下架。
- 通过 SQLite Backup API 生成备份并执行 integrity_check。
- 单写入队列初始化与退出排空。
- REST DTO 合约筛选、完整/未闭合 K 线过滤、合并市场 COIN-M 事件过滤、事件时间和精度。
- HTTP 429 的 Retry-After 及永久性 HTTP 403 不继续重试。
- 本地真实 WebSocket 服务器主动关闭连接后，客户端重连并重新订阅，收到第二条事件。
- 模拟重启后“旧历史 + 新实时 K 线”形成内部缺口，回填恢复 100 根连续历史；新上市无历史时停止回填。

## 真实运行检查

在本机直接访问 Binance 正式公共接口，成功同步 **522 个活跃 USDT 永续合约**。实际读取到所有 522 个合约价格；全市场 Ticker、4 组 Kline WebSocket、REST、Feature Engine、History Backfill、SQLite 与 Symbol Sync 均曾报告 Healthy，错误计数为 0。

已通过浏览器验证中文行情页、真实价格、ARIA 搜索筛选、RZ 与百分位展示。首次检查发现 Host 静态资源加载问题，已修复：把根 App 组件归属 Host，加载静态资源清单。发布包中的 CSS 和 blazor.web.js 均返回 HTTP 200 且内容非空。

直接运行 `artifacts/win-x64/PulletCryptoRadar.exe` 验证通过：`/health` 返回 application=PulletCryptoRadar、status=Ready、phase=0-2；首页和 API 可访问。发布包为 Windows x64 自包含形式，Data、Logs、Backup 与 EXE 相邻。

## 实施约定与验收边界

- UI 的 24h 列是基于历史闭合价的同口径收益率，首次历史尚不足时为 Unknown；Binance 原始 24h 变化率单独保留在 API 的 exchangeReturn24h，避免混淆口径。
- Price RZ 与 Volume RZ 当前为闭合 5m 指标；价格 15m RZ 也在 API 中提供。历史百分位实际使用最多 7 天的 5m 历史，UI 没有冒称 30 天百分位。
- 5m 与 15m 直接订阅交易所对应周期的闭合 K 线，避免用进程启动时的残缺 1m 拼出假完整数据。
- 市场库尚不包含后续阶段表。WatchList 未实施，因此其专用 1m 持久化及配置留到 Phase 9。
- 无 API Key 管理需求；当前连接全部为匿名只读公共行情。没有交易和资金管理接口。
- 配置在 appsettings.json，重启生效；当前只提供只读 Settings API。
- 完整 48h/7d/30d 历史回填受公共 API 限流影响，不在启动瞬间承诺完成；页面显示每个资产的真实样本数。
- 自动化验证覆盖断开连接的恢复，但真实断网、睡眠唤醒、跨日连续运行与完整全市场窗口的长时间资源占用仍需持续运行验收。不能把本次短时检查等同于全天候稳定性证明。

按规格书要求，本轮结束后等待 Phase 0～2 验收，未进入 Phase 3。
