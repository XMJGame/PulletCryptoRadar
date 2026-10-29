# Phase 10：BSC / EVM 链上分布

工程 PulletCryptoRadar，实施日期 2026-10-29（上海时间）。入口 /onchain。新增 Providers.OnChain、链上分布 Vue 页面、独立链上快照与地址分类，同时升级 Speculative-1.1 与 WatchTower-1.1；保留原模型以复核旧记录。

## ARIA 身份

本轮交叉核对的主链是 BSC，CoinGecko ID 为 aria-ai，完整合约为 0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238。CoinGecko 目录和币种页面、Binance Wallet 的精确合约链接一致；项目文档说明该资产属于 BNB Chain / BEP20。

参考：[CoinGecko ARIA](https://www.coingecko.com/en/coins/aria-ai)、[Binance Wallet 合约页](https://web3.binance.com/en/token/bsc/0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238)、[项目 Token Details](https://docs.playariagame.com/tokenomics/token-details)。

程序只在本地确有 ARIAUSDT，且当前 CoinGecko 目录的 aria-ai / binance-smart-chain 地址完全匹配时建立参考 ChainBinding。Verification 明示 Reference cross-check，保留来源链接。它不是链上区块级证明，也不冒充用户人工确认；不会修改既有市值映射、Gate 身份、自选成本或通知开关。其他同名 ARIA（包括 Solana 和其他网络）不会被合并。

## 数据源与时间

BSC 通过 Binplorer、Ethereum 通过 Ethplorer 的 getTokenInfo 与 getTopTokenHolders 读取人数、原始供应量及 Top100。其他支持的 EVM 链 base/arbitrum/polygon 使用 GoPlus token_security 的 Top10 数据。不会把 Top10 伪称为 Top100，也不把两个提供者的不同人数拼成增长曲线。

数据源依据：[Binplorer 官方 API](https://github.com/amilabs/Binplorer/wiki/Binplorer-API)、[GoPlus Token Security](https://docs.gopluslabs.io/reference/tokensecurityusingget_1)、[GoPlus 字段口径](https://docs.gopluslabs.io/reference/response-details)。页面保留数据源及 explorer 链接。

Binplorer freekey 是公开开发额度，不适合作为长期生产保障。服务端可选 PULLET_EXPLORER_API_KEY，浏览器只获知是否配置，密钥不写入配置文件或 UI。索引器要求 query API key，故该客户端禁用 HTTP 日志，错误只返回经过控制的说明，不记录 URL 或响应中的错误详情。

SourceTime 使用索引器 lastUpdated 与 HTTP Date 中较早者；没有 lastUpdated 则明示 HTTP 观测时间。不是经过验证的区块高度或逐块快照。超过 45 分钟为 Stale，未来时间为 Invalid；过期字段保留其值并标记状态，不参与新鲜评分。供应量与排名接口非同一区块的原子读取，不能宣称逐块一致。

当前公开开发策略为每次请求至少 1.1 秒，小时/日/周预算默认 50/200/1000 个请求槽。每次采集先为最多 3 个请求预留预算（常规 Binplorer 实际使用 2 个），预算与 NextAttempt 经单写入器持久化，重启不归零。自选默认 30 分钟，其他已配置资产 6 小时，单轮最多 5 个。403/429 等失败保留重试截止时间，不反复立即请求；页面“读取缓存”不发起外部请求。

## 数值与覆盖

原始余额、总供应量按 uint256 字符串处理，BigInteger 做整数运算，比例在有界精度后转换为 decimal；不经过 double 或浏览器浮点换算。每个地址只出现一次、余额须按降序、返回余额总和不得超过供应量、响应合约必须匹配。无效列表拒绝写入。

RawTop10/25/50/100 的分母都是同一索引器的原始总供应量，不是 CoinGecko 流通量。必须返回足够排名，或明确覆盖全部持有人，才计算相应 TopN。缺少样本保持 null；空列表不冒充 0% 集中度。

AdjustedTop10 从返回的排名中排除已核对 Burn、LP、Bridge，再重新选取前十，分母保持原总供应量。若排除后不足 10 个且尾部未知，则保持 Unknown，而不是把剩余 9 个相加当作完整前十。零地址与常规 dead 地址按系统 Burn 口径处理。

ExchangeSupply / ProjectControlled 在本轮只输出“已观测、已标注地址余额占比下界”，不会把 Top100 的已观测余额当成全链统计。Project/Team/Investor/Treasury 按已核对标签归类；不把 Creator、普通 Contract 或 Whale 自动当作项目方。

标签类型包含 Unknown、Project、Team、Investor、Treasury、Exchange、LP、Bridge、Burn、Whale、Contract。非 Unknown 须提供 HTTPS 证据并明确核对，使用修订号防止旧编辑覆盖；Unknown 可撤销已核对分类并保留审计。提供者 tag 只作为线索，不自动赋予已核对身份。

分类覆盖=已核对明确类别的返回余额/全部返回余额。普通 Contract 与 Unknown 均不计入分类覆盖，Contract 也不排除。覆盖不足 50% 时仍展示原始/调整值，但集中度不进入可信评分。这避免把大量未分类合约直接理解为项目方控制。

## HolderChange

24h 变化只比较同链、同合约、同提供者、约 23～25.5 小时间隔的可靠人数快照。绝对变化=current-prior；比例变化=变化/prior*100，prior 必须大于 0。首次启动、提供者切换、缺失或过期时保持 Unknown，不用现在查询到的数值编造过去人数。人数是地址数，不等于独立用户数。

## 模型接入与历史

AssetFeatures 增加 OnChainFeatures；FeatureWorker 每 5 秒从内存中的已落库链上快照计算，不在行情热路径查询数据库或外部 API。

Speculative-1.1 在原固定 100 权重中填充质量通过的 Concentration 10、HolderChange 5、HolderConcentration 风险 10、HolderDecline 风险 5。集中度强度=clamp(AdjustedTop10/80)；人数变化强度=clamp(abs(24h变动百分数)/5)；减少风险取负变动。未经核对的市值或备用交易所身份仍不能绕过原完整评分门槛，钱包行为保持未知。

WatchTower-1.1 增加已观测 OnChainScore：分布部分最多 60*(1-AdjustedTop10/100)，Holder 部分最多 40*clamp((24h变动百分数+5)/10)。缺失项不放大；两项都缺失时为 null。质量通过的分布和人数变化分别增加最多 10/5 置信度，钱包风险仍未知。

调整后 Top10 ≥80% 或人数减少至少 5% 时覆盖普通正面判断为 RiskIncreasing，两者共同出现为 ReduceRisk；不会覆盖既有 CriticalRisk 或伪造钱包流出证据。链上指标仍为研究代理，并非买卖建议或概率。

旧 Speculative-1.0、WatchTower-1.0 逻辑和历史记录保留。复算按保存版本分派引擎，使用冻结输入、标签口径、源时间及参数，不用当前标签改写旧判断。原 V2 异动模型保持不变。

## 存储与接口

数据库版本 9：业务库新增 ChainBinding/ChainBindingAudit、AddressLabel/AddressLabelAudit、OnChainAttempt、OnChainBudget；市场库新增 gzip OnChainSnapshot，按链/合约/提供者/源时间分开，保留 90 天。原始大整数余额随快照保存。写库成功确认后再更新内存；失败不发布伪成功状态。

- GET /api/onchain：参考身份、指标、样本及采集尝试。
- GET /api/onchain/status：配置和 Key 是否配置。
- GET /api/onchain/{assetId}/history?limit=60：当前主链合约的历史。
- PUT /api/onchain/{assetId}：chain、contractAddress、coinGeckoId、enabled、expectedRevision、confirmIdentity。
- PUT /api/onchain/labels：chain、address、type、evidenceUrl、expectedRevision、confirmEvidence。

每资产配置一个主链；手工链上配置必须匹配当前 CoinGecko 目录。更换主链或地址不会把旧合约快照串入新身份。链上配置不代替市值或备用交易所配置。转账、项目钱包流出、交易所流入及多跳图谱属于后续阶段，本轮不假装已经实现。

## 实际验收（2026-10-29 上海时间）

Release 测试：Core 93、Scanner 94、Integration 96，共 283 项通过；前端 13 项通过，类型检查与生产构建通过。测试日志见 TestResults/Phase10_*.trx。git diff --check 通过。

本机 /onchain 已启动。首次落库 Binplorer 源时间 16:42:47、接收时间 17:14:45，ARIA 人数 84,143，返回 100 个地址。原始 Top10/25/50/100 分别约 85.7644% / 96.2519% / 97.6894% / 98.4253%。这是该索引器在该源时间的观测值，不作为持续不变的当前数值。重启后身份、快照与下次采集时间均恢复，页面读取缓存不触发外部采集。

当次没有人工核对标签，分类覆盖为 0%，ConcentrationUsable=false；调整后数值虽可显示，不进入可信集中度评分。24h 人数变化没有合格历史基线，保持 Unknown。不能从单份样本推断人数增长，也不能把原始 Top10 直接视为项目方持仓。

本机接口实测：身份未确认 400、标签证据未确认 400、旧身份修订号 409，实际身份版本仍为 1、标签仍为空。历史复算抽查 Speculative 最近 3 份、WatchTower 最近 30 份、原异动模型最近 100 份，全部 Match。完整结果见 Phase10-LiveValidation.json。

浏览器验证了配置确认门槛、原始地址展开、标签编辑确认门槛、取消编辑、历史读取；未保存任何测试性真实地址分类。页面无浏览器 error 日志。桌面 1280×720 无整页横向溢出，当前导航只有一项激活；九项导航位置检查见 Phase10-NavigationValidation.json。默认窄窗表格可横向滚动，长证据 URL 仍可能轻微超出视口（约 3px），后续响应式整理可统一处理。

截图：Phase10-OnChain-Desktop.png、Phase10-OnChain.png。后续钱包流出、交易所流入与多跳资金路径仍未纳入本阶段。
