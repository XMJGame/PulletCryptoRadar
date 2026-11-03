# 服务器稳定性加固记录（2026-10-31）

本轮完成安全启停、发布保护、压缩记录读取保护和排查证据留存。没有确认原始底层崩溃根因，也没有通过长时间运行证明异常不再出现。模拟策略、资金和原持仓未因本轮维护重置。

## 故障证据与判断边界

Windows应用程序事件在北京时间08:23:19记录coreclr的0xc0000005，相关.NET事件栈经过sqlite3_prepare_v2、Dapper和PersistenceWorker；09:01:29记录System.Private.CoreLib的0x80131623，FailFast栈经过流式JSON反序列化与SqliteDatabase.Decompress。现有WER归档只有Report.wer，没有可用于定位的转储。栈经过某个函数不足以证明该函数或某条记录就是原因。

对3个数据库以只读方式执行SQLite quick_check，并逐条检查445,297条Payload记录的JSON；压缩记录还经过解压大小限制、gzip校验。3库检查均为ok，未发现坏记录。该结果属于检查时的快照，不能排除瞬时并发、运行时或后续故障；不包含没有Payload列的全部行情行逐字段检查。详情见 [Storage-Integrity-2026-10-31.json](Storage-Integrity-2026-10-31.json)，复查脚本为 `scripts/audit_runtime_storage.py`。

## 已实施

- `Stop.ps1`通过仅限当前用户的本地管道请求Host正常退出。Host先取消生产者，最后排空写入队列；默认等候60秒，失败不自动强杀，`-Force`是显式备用选项。
- `Start.ps1`和发布脚本共享部署目录锁，发布前检查目标exe是否运行，运行时拒绝覆盖。数据目录的独占文件句柄阻止两个服务器共同管理账本；`/health`返回部署实例标识和PID，避免将另一个目录的服务器误认成本项目。
- 压缩记录先独立解压，再以UTF-8缓冲区反序列化，单条展开上限64MiB。对坏头、截断、大小超限、无效JSON明确报错并附类型、压缩长度和SHA256，不静默跳过账户或创建新账户。它绕开观察到的流式JSON重抛路径，不构成底层异常已经修复的证据。
- `runtime-sessions.jsonl`记录Starting、Started、Stopping、Stopped；托管未处理异常可记录类型和截断堆栈。原生崩溃、FailFast或断电不保证回调执行。
- `Start.ps1 -Diagnostics`仅为新启动的进程设置崩溃转储参数，当前普通多文件发布请求Mini转储，输出在Logs/Dumps。本轮没有主动制造崩溃验证转储生成，也未设置全局环境或注册表。参数依据 [.NET官方转储说明](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/collect-dumps-crash)；未来若改成单文件或NativeAOT发布，官方要求Full转储。转储保留本地，不上传仓库。

## 验证

后端全量448项通过：Core 93、Scanner 100、Integration 255。其中7项稳定性测试覆盖坏JSON、截断gzip与展开上限、500次并行独立解码、独占锁重开、管道拒绝非法指令后正常停止、150条已接收写入排空，以及损坏PaperAccount在重复恢复失败后原版本和字节不变。测试结果在本地TestResults/Stability-2026-10-31目录。

已用Windows PowerShell实际完成两轮正常停止与重启，生命周期日志均含Stopping/Stopped；重复Start保持同一PID。目标正在运行时发布脚本拒绝执行，exe散列保持一致。独占锁的重复实例行为通过自动测试；未再启动第二个真实服务器进程作冲突实验。

160次本地HTTP读取、并发6，失败0，耗时0.406秒；最长单请求0.078秒。覆盖健康、持仓、持仓历史、链上及映射等读取，不新建快查报告、交易或外部请求。该测试是短时读取验证，不能替代持续采集和长时间稳定性观察。详情见 [Runtime-Read-Stress-2026-10-31.json](Runtime-Read-Stress-2026-10-31.json)。

最新实例于北京时间09:41:59启动。核对时模拟启用状态、执行起点、现金970.451475046751 USDT、1笔已关闭交易和原BTCUSDT空仓ID保持一致，持仓观察持续更新；现金不是含浮盈亏的总权益。采集仍受各数据源时效校验和限流影响，Gate/Coinbase存在响应时间无效告警，不将这些缺口冒充有效行情。最终时点与核对结果见 [Runtime-Stability-Validation.json](Runtime-Stability-Validation.json)。

## 后续

保留本轮会话日志并继续观察实际采集。若同类底层退出再发生，结合转储、Windows事件、会话记录定位，不通过删除数据库或重置模拟账户规避。代币快查后续尚缺解锁、项目基本面及新闻归因的可核验证据；这些缺口目前仍明确展示，不折算成收益概率。
