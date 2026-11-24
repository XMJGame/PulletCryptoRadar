export async function api<T=any>(path:string,options:RequestInit={}):Promise<T>{
  const response=await fetch(path,{...options,headers:{Accept:'application/json',...options.headers}})
  if(response.status===404&&path==='/api/market-context')return undefined as T
  if(!response.ok) throw new Error(`请求失败（${response.status}）：${path.split('?')[0]}`)
  return response.status===204?undefined as T:await response.json() as T
}
export function number(value:unknown,digits=2):string{
  return typeof value==='number'&&Number.isFinite(value)?value.toLocaleString('zh-CN',{minimumFractionDigits:digits,maximumFractionDigits:digits}):'—'
}
export function time(value:unknown):string{
  if(typeof value!=='string'||!Number.isFinite(Date.parse(value))) return '—'
  return new Date(value).toLocaleString('zh-CN',{hour12:false})
}
const labels:Record<string,string>={
  Long:'做多',Short:'做空',TrendPullback:'趋势回踩',BreakoutRetest:'突破回测',Blocked:'条件不足',NoSetup:'暂无形态',Waiting:'等待确认',
  Held:'正在持有',NotEvaluated:'尚未详细评估',ConditionsNotMet:'当前条件未满足',
  Background:'普通扫描/研究',Core:'核心采集',Critical:'执行/持仓',
  CooldownAfterTimeout:'请求超时后的冷却',CooldownAfterTransport:'网络失败后的冷却',CooldownAfterRateLimit:'供应商限流后的冷却',CooldownAfterForbidden:'供应商拒绝后的冷却',ProviderCooldown:'供应商冷却（旧记录未保存原因）',
  ProviderMinuteLimit:'分钟请求上限',ProviderDailyCalls:'供应商每日调用上限',ProviderMonthlyCalls:'供应商每月调用上限',ReservedCriticalMinuteCapacity:'请求额度已为更高优先级预留',
  DailyPayloadBudget:'每日正文预算不足',MonthlyPayloadBudget:'每月正文预算不足',ReservedCriticalDailyCapacity:'今日正文预算已为更高优先级预留',ReservedCriticalMonthlyCapacity:'本月正文预算已为更高优先级预留',
  LegacyQuotaUnclassified:'旧额度拒绝未记录细分原因',ProviderQuotaHeadersUncertainOrLow:'供应商额度头部缺失或余量过低',ProviderMinuteHeadroomLow:'供应商分钟余量过低',Quota:'本地请求未获准',Timeout:'请求超时',Transport:'网络/HTTP 失败',RateLimited:'供应商限流',Forbidden:'供应商拒绝',Invalid:'响应未通过校验',
  IdentityMissing:'身份未核实',NoHolderCoverage:'无有效持币覆盖',CollectorUnsupportedChain:'采集器尚不支持此链',NotCollectedOrMissing:'未采集或来源缺项',Usable:'通过核对',StaleOrInvalid:'失鲜或证据无效',
  PatternConfirmed:'形态已确认',Expired:'已过期',Invalidated:'已失效',Stale:'已失鲜',Observing:'观察中',InsufficientData:'证据不足',
  Participation:'参与度增强',Expansion:'量价持仓扩张',Overheated:'快速上涨 · 谨慎追涨',Excluded:'基准排除',
  Streaming:'实时推送正常',Idle:'等待观察目标',Connecting:'连接中',Disconnected:'连接已断开',WaitingOrStaleQuotes:'等待新鲜报价',
  Bull:'偏多',StrongBull:'强多',Bear:'偏空',StrongBear:'强空',Mixed:'分歧',Unknown:'资料不足',Pressure:'承压',Panic:'恐慌',
  SingleSource:'单一可信来源',Confirmed:'来源核对一致',Conflict:'来源冲突',Missing:'缺失',UnverifiedTime:'原始时间未核实',
  Healthy:'正常',Degraded:'最近请求失败',Cached:'已读取缓存',RefreshAttempted:'已完成刷新尝试',Partial:'部分资料缺失',
  RefreshBusyOrCoolingDown:'刷新中或额度冷却',RefreshFailed:'刷新失败',NotAssessed:'尚未形成买卖建议',
  ContractUnverifiedOrInactive:'合约未确认有效',Open:'开仓',Reduce:'减仓',Mark:'持仓观察',Funding:'资金费',ScaleOutSkipped:'减仓未满足最小数量',
  MarketContextUnavailableOrOpposed:'市场资料不足或方向相反',ParticipationInsufficient:'参与度尚未满足',TrendNotAligned:'小时与四小时趋势未一致',
  H1TrendNotAligned:'小时趋势方向不符',H4TrendNotAligned:'四小时趋势方向不符',VolumeRatioBelowThreshold:'量比低于当前阶段门槛',DirectionalTakerBelow52Percent:'同方向主动成交占比不足 52%',OiGrowthBelow1Percent:'一小时 OI 数量增长不足 1%',
  ArmingParticipationInsufficient:'启动形态时参与度不足',ConfirmationParticipationInsufficient:'等待确认阶段参与度不足',
  TerminalPatternRearmWindowReached:'已结束形态到达重新观察窗口',ConfirmationUsesArmedParticipation:'研究候选：沿用启动参与度并检查价格确认',
  SetupTimedOut:'形态等待超时',BreakoutArmedAwaitRetest:'突破已出现，等待回测',StructuralStopBreachedBeforeConfirmation:'确认前已触及结构止损',PullbackArmed:'回踩已出现，等待确认',ClosedBarPatternConfirmed:'后续闭合 K 线确认',StoredTerminalPatternNotNewAdmission:'已结束的形态，不重复准入',ConfirmationNotReached:'后续 K 线尚未确认',
  PatternNotPresentOrExtended:'未形成形态或价格已偏离',ExecutionAdmissionPending:'等待执行准入',UpcomingFundingScheduleUnverified:'结算前置检查待完成',
  PortfolioRiskNotConnected:'待执行阶段核对组合风险',FreshCausalExitBookRequired:'等待有效退出盘口',FundingReconciliationPending:'先核对持仓资金费',
  Opened:'已模拟开仓',PaperLiquidationBufferInsufficient:'止损与模拟清算线间距不足',CorrelationRiskExceeded:'相关组风险已达上限',
  DirectionalRiskExceeded:'同方向风险已达上限',TotalOpenRiskExceeded:'组合风险已达上限',MaximumPositionsReached:'已达持仓上限',
  NotFoundInVerifiedCatalog:'已核实目录中未找到',CatalogUnavailable:'合约目录尚未就绪',Ambiguous:'存在同名候选，请确认',
  SelectCandidate:'请选择具体代币',Exact:'找到精确候选',OpenedPaper:'模拟开仓',
}
export const label=(value:unknown)=>typeof value==='string'?(labels[value]??value):'—'
export const reason=(value:string)=>value.startsWith('MissingOrStale:')?`缺失或失鲜：${value.split(':')[1]}`:label(value)
