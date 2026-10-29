# Phase 7：CoinGecko 与资产映射

工程：PulletCryptoRadar。验收日期：2026-10-29（上海时间）。本次任务按约定先迁移 Vue，再实施 Phase 7；已发布到 http://localhost:5218。

## 完成范围

新增 Providers.Market，采集 CoinGecko 资产目录、平台与合约地址、市值、FDV、24h 现货汇总成交额、流通/总/最大供应量以及 ATH。市值按唯一 CoinGecko ID 分批读取，默认 30 分钟采集、每批最多 100 个、每分钟最多 10 次，目录默认 24 小时更新。429 遵守 Retry-After 并至少冷却 60 秒；没有头时冷却 2 分钟。失败保留缓存，过期后显式标记 Stale，不用零填补缺失。

新增资产映射页面：交易所合约与稳定的本地 AssetId、CoinGecko ID、链平台、合约地址分开管理；支持目录搜索、候选列表、人工确认、清除映射及最近 50 条修改历史。清除后后台重新生成未确认候选，历史记录保留。

自动同名只产生 SymbolMatched；多候选为 Ambiguous，不按市值或排名猜测。1000 等前缀仅在无精确符号匹配时用于候选提示；金额已是 USD，绝不再按合约倍数乘一次。人工核对仍然必要，例如目录本身存在 1000BONK 与 BONK 等不同条目。

人工保存要求明确确认身份、目录中存在所选 ID。链与地址必须成对填写且匹配该 ID 的目录记录，EVM 地址忽略大小写，其他链保持大小写。人工映射不会被自动同步覆盖；目录删除所选 ID 时可信状态自动失效。编辑版本冻结并进行服务端乐观并发校验，旧版本返回 409，不覆盖新编辑。

## 数值与置信度

原始字段保留 CoinGecko last_updated 和接收时间。未来、负数和缺失字段不进入有效计算；源时间超过 60 分钟为 Stale。未确认的候选数值可以展示，身份仍提示“资产映射待确认”。

可信身份要求 ManualVerified 或 ContractVerified 且 ID 仍存在于目录。本轮不会自动制造 ContractVerified；人工核对保存为 ManualVerified。市值必须有效且大于零，分子也必须有效、新鲜：OI 最多 11 分钟，Binance ticker 成交额最多 30 秒。

- OI / MarketCap = Binance 美元 OI ÷ CoinGecko 美元市值。
- Volume / MarketCap = Binance 永续 24h 美元成交额 ÷ CoinGecko 美元市值。
- CoinGecko total_volume 单独展示为现货汇总成交额，不冒充永续成交额。
- 比率时间使用两端最旧源时间。溢出返回 Invalid，缺失或身份未确认保持 Unknown。

Phase 7 数据仅展示，V2 检测器和评分版本保持原有行为；新的投机热度评分留到 Phase 8，以免改变历史事件复算含义。

## 存储与恢复

数据库版本 6。业务库新增 CoinCatalog（gzip）、AssetMapping、AssetMappingAudit、ChainToken；映射和 canonical Asset 字段在一个事务中更新。整批版本冲突整体回滚。市场库新增按 CoinGeckoId + SourceTime 唯一的 MarketCapSnapshot，保留 90 天。换绑资产不会让旧 ID 的样本串到新身份。

所有写入经过原单 PersistenceWorker，写库确认后再发布内存状态；重启恢复目录、映射和最新市值缓存。decimal 通过 JSON 精确保留。现有资产、行情、通知和回放数据保留。

## API 与配置

- GET /api/mappings、/api/mappings/status。
- GET /api/coingecko/search?q=bitcoin（本地目录搜索，最多 100 条）。
- GET /api/mappings/{id}/candidates、/api/mappings/{id}/history。
- PUT /api/mappings/{id}，请求含 coinGeckoId、expectedRevision、confirmIdentity，可选 platform、contractAddress、reason；错误分别为 400/404/409。

appsettings.json 的 CoinGecko 节包含 Enabled、BaseUrl、PollMinutes、CatalogHours、RequestsPerMinute、BatchSize。BaseUrl 仅允许官方 HTTPS 主机；HTTP 客户端禁止自动重定向。无需在配置文件或浏览器保存 API Key。

可选服务端环境变量 PULLET_COINGECKO_API_KEY：Demo 使用 x-cg-demo-api-key，Pro 主机使用 x-cg-pro-api-key。只在本机环境中配置并重启程序，勿贴到聊天或写入仓库。状态接口只返回是否配置，不返回密钥。匿名接口受共享出口限额影响，持续限流时可以配置自己的服务端 Key。

## 验收

发布脚本完成 Vue 类型检查、生产构建、6 项前端测试及 221 项 .NET Release 测试（Core 84、Scanner 69、Integration 68），全部通过。新增覆盖重复符号、前缀候选、人工确认、链地址、版本冲突、事务回滚、重启恢复、decimal 精度、429、源时间、身份可信和比率边界。

2026-10-29 15:02 上海时间真实采集：22,565 个目录资产、522 个交易所合约。275 个 SymbolMatched、234 个 Ambiguous、13 个 Unknown；272 个有效候选市值，可信身份为 0。未擅自核对生产资产，全部可信比率保持 Unknown。BTC 有 13 个同名候选，也未自动选择 bitcoin。

六个 Vue 路由 HTTP 200、未知 API 返回 404；未经身份确认的映射 PUT 返回 400。六页选中状态和导航位置一致，无页面级横向溢出、无浏览器控制台 warning/error。真实回放读取 53 个快照、27,307 个合约观测，界面未报错；100 条历史事件复核 97 Match、3 MissingInput、0 Mismatch，未将缺失输入当成通过。

证据：Phase07-LiveValidation.json、Phase07-NavigationValidation.json、Phase07-Mappings.png。截图中的候选数值仍需人工确认身份，并不等于已经可信。

接口参考：[CoinGecko Coins List](https://docs.coingecko.com/demo/reference/coins-list)、[Coins Markets](https://docs.coingecko.com/demo/reference/coins-markets)。
