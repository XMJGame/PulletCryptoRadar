# PulletCryptoRadar

妖币早期发现与研究工具。以低流通市值、可核实产品和链上筹码变化为研究对象。运行模式为 `ResearchOnly`，现有数据库中的历史资料可继续追溯。

- **妖币雷达**是首页，展示候选、覆盖、持币变化和历史样本。
- **追涨跌分析**输入代币代码或 Binance USDT 永续交易对，选择看涨或看跌，按需核查小时线、成交额、未平仓量、资金费率与盘口。

双击 `启动服务器.cmd` 打开 http://localhost:5218，双击 `停止服务器.cmd` 正常关停。`检查研究状态.cmd` 读取本机 API 并保存研究状态摘要。发布用 `scripts/Publish.ps1`，保留 `artifacts/win-x64/Data`。采集的月度响应正文上限是 30 GB。

## 妖币研究目标

1. 优先核实低流通市值、有 Binance 永续且无 Binance 现货的代币。Alpha 是候选来源之一。逐币核对项目产品、供应与解锁、现货深度和交易状态。
2. 区分交易所、流动性池、锁仓、跨链托管和未知地址，跟踪同一批钱包的实际净买入、解锁和交易所进出。保留原始证据与时点。
3. 用首次放量建档，观察回踩及后续启动，结合现货成交、币数 OI、资金费。先验证提示能提前多久、误报多少、提示后先跌多少，再评价 30 天内触及十倍的情况。
4. 用 EVAA、LAB、RAVE、MYX、RIVER 和同期失败样本做对照，TRB 作为有现货的对照。当前雷达展示研究阶段与证据缺项，历史报告是量价探索基线，尚未验证上述组合。

历史研究命令是 `scripts/Research.ps1`。源码验证用 `dotnet test PulletCryptoRadar.sln -c Release`，前端验证用 `npm run build`。

API Key 从环境变量读取：`PULLET_COINGECKO_API_KEY`、`PULLET_CMC_API_KEY`、`PULLET_INSIGHTX_API_KEY`。Binance 公共行情无需交易 Key。
