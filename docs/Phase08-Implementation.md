# Phase 8：Speculative Radar V1 与备用行情

工程：PulletCryptoRadar。实施日期：2026-10-29（上海时间）。本轮范围为规格书 Phase 8，及用户要求的 Binance 不可用时查看 Gate / Coinbase。

## 架构与边界

新增独立 Speculative-1.0 评分器、5 分钟扫描、妖币雷达 Vue 页面及历史输入复算。与既有 UpMove/DownMove V2 并行，不替换原分数、Episode、通知策略，也不从此页发送实际通知。每次扫描保存同期 Binance V2 上下行结果作为参考；不同模型目标不同，不直接比较分数大小。

用户可以从评分详情直接跳转到已筛选该合约的资产映射页。每个外部交易所保留原生符号和来源时间。同名跨交易所只产生候选，必须在详情中核对 Gate 原生合约身份，并在资产映射页核对 CoinGecko 身份，才能使用可信市值评分。Gate 身份确认可撤销，历史记录保留。编辑冻结版本并进行 CAS 校验，拒绝旧版本及重复绑定原生合约。

## 两种独立评分

异常权重严格采用正式规格书：低市值 10、成交额/市值 15、OI/市值 20、OI 异常 15、价格/成交异常 10、Funding 5、Top Trader 5、筹码集中 10、Holder 变化 5、Wallet 行为 5。总计 100。

每个已知强度在 0～1 之间，按固定权重求和；未采集项保留 null，并显示可用权重覆盖，不按已有项重新归一化。本轮未接链上集中度、Holder、钱包数据，异常可用权重理论上最多 80；Gate 缺少独立价格/成交 RZ，理论上最多 70。所有必要输入还必须满足身份和新鲜度要求。

初始强度公式为明确的研究假设，尚未通过真实命中率校准：

- 低市值：clamp(1 - log(MC/MinMC)/log(MaxMC/MinMC))。
- 成交额/市值：clamp(VolumeMC/1)；OI/市值：clamp(OIMC/0.2)。分子来自同一永续交易所。
- OI 异常：clamp(OI 数量变化 RZ/3)。Gate 用独立 5m 数量变化和至少 30 个历史变化计算 MAD，当前观测不进入基线。
- 价格/成交：同交易所 abs(价格 RZ)/3 与 成交 RZ/3 的 clamp 均值。
- Funding：abs(8h 标准化费率)/0.003；Top Trader：abs(Top Position 15m 变化百分点)/3，均 clamp。

风险权重独立定义：短期涨跌过热 15、极端 Funding 10、多头拥挤 10、极端 OI/MC 15、成交额流动性代理 10、FDV/MC 10、Top Holder 10、项目钱包流出 10、交易所流入 5、Holder 减少 5。总计 100，可用权重理论上最多 70。

风险强度：已知 1h/24h 绝对收益除以 20/50 取最大；abs(Funding8h)/0.003；(LongShare-0.5)/0.35；OIMC/0.5；1-clamp(Volume24h/(MinVolume*2))；(FDV/MC-1)/(10-1)。分别 clamp，缺失周期不补值。成交额只是流动性代理，不表示盘口深度或可成交能力。

任何一套分数都要求：可信市值>0、同交易所有效价格、OI、成交额，以及该套可用权重至少 50。缺失、未来或过期输入阻止完整分数，不以零替代。风险分低且覆盖不足不能解释为安全。

候选初始条件：MC 在 500 万～10 亿 USD，永续 24h 成交额至少 500 万、OI 至少 100 万、异常分至少 45，成交额/MC、OI/MC、OI 异常、价格/成交四项中至少两项确认。极端 Funding、OI/MC 或短期涨跌强度达到 1 时标记 Overheated，并从观察候选中排除。其余为 Ignition / Observing；不满足必要输入为 Unknown。

上述阈值均在 Speculative 配置节中明确保存。调整公式应提升策略版本；阈值调整随每轮参数冻结，历史复算使用历史参数。

## Gate 与 Coinbase

Gate 公共永续 API 已实现 contracts、tickers、contract_stats。仅使用正常交易、未下架的 USDT direct 合约，按完整基础符号精确匹配本地候选；不猜测别名、迁移币或 1000 倍合约映射。原生 Gate 记录按交易所与符号分开缓存，旧源时间不能覆盖新样本。

Gate OI 直接使用带时间戳的 contract_stats.open_interest_usd；15m OI 变化与 OI RZ 使用同交易所 open_interest 合约数量。没有从 total_size 推导单边 OI，因为该字段与 position_size 的计数口径不同。成交额使用 volume_24h_quote，不使用合约张数或已弃用字段。Funding 根据该合约实际 funding_interval 秒数线性标准化至 8h。

Gate ticker 没有逐字段事件时间，明确采用 HTTP Date 作为观测响应时间，绝不冒充交易事件时间；拒绝缺失、未来或超过 3 分钟的响应时间。OI 源时间仍使用 stats.time。USDT 报价金额明示为 USD 代理，不代表测量了 USDT/USD 汇率。

默认每 120 秒完成一次采集后的等待，逐请求至少 1 秒；批量行情覆盖全市场，深度统计优先 ARIA/BTC/ETH 和异动资产，默认最多 25 个。403/429 遵守 Retry-After，至少冷却 2 分钟，默认 5 分钟；其他网络错误交由下一轮重试，失败保留明确过期的缓存。

Coinbase 公共 products 与 ticker 作为独立 USD 现货参考，默认最多 5 个存在且在线的候选交易对。Ticker 的 volume 是基币量，不能冒充美元成交额；不使用 price*baseVolume 伪造真实美元成交额，更不伪造永续 OI。现货不参与永续评分，也不覆盖 Binance 的 K 线或历史基线。

当 Binance 必要永续输入可用时优先使用 Binance；不足时有新鲜 Gate 价格则选择 Gate 参考。只有完成身份确认且本交易所输入充分，Gate 才有完整分数。Coinbase 只在“全部来源对照”或来源筛选中显示为现货参考。

Binance Funding 的合约结算周期可能不同。本轮没有可靠的全合约周期元数据，故不假定所有 Binance 合约都是 8h，新增评分里的 Funding 暂为 Unknown；原 V2 保持其原历史语义。

启动时会在发布就绪信号之前恢复已保存的 Binance 冷却截止时间。快照工作器每 30 秒保存健康状态；若冷却后立即强制终止进程，最近一次状态仍可能未落盘。恢复逻辑避免常规更新重启立即再次碰触仍被限制的接口，独立 WebSocket 和备用交易所不受此等待影响。

## 存储、API 与回放

数据库版本 7。业务库新增 VenueBinding / VenueBindingAudit；市场库新增 gzip SpeculativeScan，保留 7 天。写入统一经过单 PersistenceWorker，确认写库后才发布新评分快照。每轮保存当时的 MarketView、Capital 身份与修订、独立交易所输入、参数及完整贡献，decimal 在持久化中保留精度。

- GET /api/speculative：最新完整扫描快照。
- POST /api/speculative/refresh：重算当前缓存并保存，不主动请求外部 API。
- GET /api/speculative/audit?limit=3：最近保存扫描复算，最多 12 轮；不读取当前行情。
- GET /api/venues：独立外部交易所报价。
- GET /api/venues/bindings：身份确认状态。
- PUT /api/venues/bindings/{assetId}：exchange、symbol、expectedRevision、confirmIdentity；撤销传 revoke=true，修改记录保留。

原 Phase 6 连续行情回放与事件复核保持原有版本。Phase 8 独立快照复算只检验输入和结果的可复现性，尚不能宣称真实 Precision/Recall 或可成交收益；需要后续成熟样本研究。

官方口径参考：[Gate 永续 API](https://www.gate.com/docs/developers/apiv4/en/futures/)、[Coinbase ticker](https://docs.cdp.coinbase.com/api-reference/exchange-api/rest-api/products/get-product-ticker)。

## 本轮验收结果

Release 发布流程通过：242 项后端测试（Core 84 / Scanner 78 / Integration 80）、8 项前端测试、Vue 类型检查及生产构建。新增测试覆盖固定权重、缺失证据、身份双重门槛、过热排除、未来/过期 OI、零市值、跨交易所基线隔离、OI 精度与源时间、Gate 429 冷却、Coinbase 现货量口径、绑定 CAS/撤销/审计、单写入器保存及冻结参数复算。

15:25 上海时间运行证据：Gate 1,065 条永续报价、候选中 12 条有效 OI，Coinbase 5 条现货参考；522 个本地标的共 1,015 条独立来源观察，选择 Gate 488 个、Binance 34 个。该统计是当时的参考来源选择，不表示所有 Gate 标的都具备深度 OI 或已确认身份。ARIA Gate 报价、成交额及 OI 均已实际取得。

生产跨交易所身份确认 0 条，CoinGecko 人工确认仍为 0，因此完整异常分 0 条；这是必要的身份门槛，不生成伪造榜单。未经确认的绑定 PUT 实测返回 400，未修改真实资产。人工确认、撤销后禁用评分的闭环在隔离数据库中通过单 PersistenceWorker 测试。

最近 3 轮新评分输入复算均 Match（1,015 / 522 / 522 条，差异 0）；原历史事件 100 条复算均 Match。七个 Vue 页面导航位置固定、各一个选中标记、页面无横向溢出；从 ARIA 评分详情跳转映射页后搜索自动填入 ARIAUSDT。

HTTP 首次验收发现服务方法名 BindAsync 与 ASP.NET Minimal API 绑定约定冲突，已改名 SetVenueBindingAsync 并重新发布。最终 /health 返回 Ready / Phase 0-8；保留此记录说明为什么编译、单元测试之后还需要真实 HTTP 验收。

运行证据见 Phase08-LiveValidation.json；导航证据见 Phase08-NavigationValidation.json；截图见 Phase08-Speculative.png。持续采集后的样本和健康状态可能变化，应核对页面扫描时间、字段源时间与覆盖，不把旧快照当作当前行情。
