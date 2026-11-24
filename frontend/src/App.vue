<script setup lang="ts">
import {computed,onMounted,onUnmounted,ref,watch} from 'vue'
import {useRoute} from 'vue-router'
import {api,number,time,label} from './api'
import DirectionCheck from './DirectionCheck.vue'
const route=useRoute()
const navigation=[{path:'/radar',title:'妖币雷达',icon:'◎',sub:'候选与筹码观察'},
  {path:'/direction',title:'追涨跌分析',icon:'↕',sub:'指定代币和方向'}]
const active=computed(()=>navigation.find(item=>item.path===route.path)??navigation[0]!)
const data=ref<Record<string,any>>({}),errors=ref<string[]>([]),updated=ref<string>(),busy=ref(false)
let timer:ReturnType<typeof setTimeout>|undefined,disposed=false,pendingRefresh=false
async function refresh(){
  if(document.hidden||disposed||route.path!=='/radar')return
  if(busy.value){pendingRefresh=true;return}
  busy.value=true
  const endpoints:Record<string,string>={}
  if(route.path==='/radar'){endpoints.radar='/api/radar/board';endpoints.identities='/api/research/identities';endpoints.holders='/api/radar/holders';endpoints.history='/api/research/history'}
  const results=await Promise.allSettled(Object.entries(endpoints).map(async([key,url])=>{data.value[key]=await api(url)}))
  if(disposed)return
  errors.value=results.flatMap(result=>result.status==='rejected'?[String(result.reason.message??result.reason)]:[])
  if(results.some(result=>result.status==='fulfilled'))updated.value=new Date().toISOString()
  busy.value=false
  if(pendingRefresh){pendingRefresh=false;void refresh()}
}
async function poll(){await refresh();if(!disposed)timer=setTimeout(poll,10000)}
watch(()=>route.path,()=>{document.title=`PulletCryptoRadar · ${active.value.title}`;void refresh()})
onMounted(()=>{void poll()})
onUnmounted(()=>{disposed=true;clearTimeout(timer)})
const holderChange=(item:any,address:string)=>item.comparison?.addresses?.find((row:any)=>item.snapshot.chain==='Solana'?row.address===address:row.address.toLowerCase()===address.toLowerCase())?.balanceChange
const holderRole=(item:any,address:string)=>item.roles?.addresses?.find((row:any)=>item.snapshot.chain==='Solana'?row.holder.address===address:row.holder.address.toLowerCase()===address.toLowerCase())
const roleNames:Record<string,string>={Unknown:'用途未知',Exchange:'交易所托管',DexAuthority:'DEX 权限地址',LiquidityPool:'流动性池',TeamTreasury:'团队/金库',Vesting:'归属/锁仓合约',Burn:'销毁地址'}
const roleStatusNames:Record<string,string>={SourceAttributed:'公开来源标注',Unknown:'未核实',ReviewExpired:'复核已过期',HistoricalOnly:'仅有历史证据',ConflictingSources:'来源冲突'}
const metricNames:Record<string,string>={Price:'最新成交价',MarkPrice:'标记价',MarketCap:'流通市值',Fdv:'完全稀释估值',CirculatingSupply:'流通量',Return24hClosed:'闭合 24h 涨跌幅 %',
  DexLiquidity:'DEX 流动性',Top10:'前十地址占比',TotalClusterPercent:'关联地址集群占比',Volume24h:'24h 合约成交额',OpenInterestUsd:'OI 名义价值',
  FundingRaw:'原结算期资金费率',Funding8h:'8h 研究折算',FundingIntervalHours:'结算周期（小时）',OiQuantityChange1h:'1h OI 数量变化 %',
  OiQuantityChange4h:'4h OI 数量变化 %',OiQuantityChange24h:'24h OI 数量变化 %',TakerBuyShareH1:'主动买入占比（0～1）',VolumeRatioH1:'小时成交量比'}
const radarRows=computed<any[]>(()=>data.value.radar?.research??[])
const radarActivity=computed<any[]>(()=>data.value.radar?.activity??[])
const radarChecks=computed<any[]>(()=>data.value.radar?.dataChecks??[])
const radarCoverage=computed(()=>data.value.radar?.coverage)
const holdersByAsset=computed(()=>new Map<string,any>(
  (data.value.holders??[]).map((item:any)=>[item.assetId,item])))
const watchMetric=(row:any,name:string)=>row.metrics.find((metric:any)=>metric.metric===name)
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <RouterLink to="/" class="brand"><span class="brand-mark">◉</span><span>PULLET<small>CRYPTO RADAR</small></span></RouterLink>
      <div class="sidebar-caption">妖币研究工作区</div>
      <nav><RouterLink v-for="item in navigation" :key="item.path" :to="item.path" :class="{selected:route.path===item.path}"><span class="nav-icon">{{item.icon}}</span><span>{{item.title}}<small>{{item.sub}}</small></span><i v-if="route.path===item.path"></i></RouterLink></nav>
      <div class="sidebar-foot"><span class="live-dot"></span>研究模式<p>行情 · 筹码 · 历史样本</p></div>
    </aside>
    <main>
      <header class="topbar"><span>工作区 <b>/</b> {{active.title}}</span><div><span class="badge">研究模式</span><button v-if="route.path==='/radar'" class="icon-button" :disabled="busy" @click="refresh">{{busy?'更新中…':'↻ 刷新'}}</button></div></header>
      <div class="content">
        <div v-if="route.path==='/radar' && errors.length" class="notice danger">部分数据暂不可用，已保留的数值可能过期。<details><summary>查看原因</summary><p v-for="error in errors" :key="error">{{error}}</p></details></div>
        <section class="page-heading">
          <div>
            <h1>{{active.title}}<span class="heading-dot">.</span></h1>
            <p>{{route.path==='/radar'?'观察低市值候选和长期筹码变化。':'选择方向，核查 Binance USDT 永续的短线延续迹象。'}}</p>
          </div>
          <div v-if="route.path==='/radar'" class="updated"><span :class="['live-dot',{off:errors.length}]"></span>{{updated?'最近同步 '+new Date(updated).toLocaleTimeString('zh-CN',{hour12:false}):'正在连接本地服务器…'}}</div>
        </section>

        <template v-if="route.path==='/radar'">
          <section class="panel">
            <div class="panel-heading"><div><h2>起涨前观察</h2><span class="count">{{data.radar?.earlyWatchCount??'—'}}</span></div><span class="muted">按流通市值排序</span></div>
            <p class="panel-note">观察期按数月考虑。低流通市值和已核实链上身份提供入选理由。产品、代币用途、筹码和退出条件各有独立证据。</p>
            <div v-if="!data.radar?.earlyWatch?.length" class="compact-empty">当前没有身份与市值均通过核对的候选。</div>
            <div v-else class="radar-grid">
              <article v-for="row in data.radar.earlyWatch" :key="row.assetId" class="radar-card">
                <div class="card-title"><h3>{{row.symbol}}</h3><span class="badge warning">{{row.longHoldReview?.verdict??'项目待审'}}</span></div>
                <p><strong>入选理由</strong> · 流通市值 {{number(watchMetric(row,'MarketCap')?.value/1000000,2)}} 百万美元，链上身份已核对，见于 Binance 永续目录。</p>
                <p><strong>项目与代币</strong> · {{row.longHoldReview?.product??'产品、团队及持续运营待查。'}} {{row.longHoldReview?.tokenUtility??'实际代币用途待查。'}}</p>
                <p><strong>筹码与风险</strong> · {{row.longHoldReview?.risk??'控制主体、近期开锁及安全记录待核实。'}}</p>
                <p v-if="holdersByAsset.get(row.assetId)?.fresh && holdersByAsset.get(row.assetId)?.roles">
                  已采样地址合计 {{number(holdersByAsset.get(row.assetId).roles.listedSupplyPercent,2)}}%，其中用途未知占总供应
                  {{number(holdersByAsset.get(row.assetId).roles.unknownListedPercent,2)}}%。详见持币地址持续观察。
                </p>
                <p v-else>尚缺新鲜且已拆解的持币地址快照。</p>
                <p><strong>继续观察的条件</strong> · {{row.longHoldReview?.recheck??'核对产品是否实际运行、未来 90 天解锁、关联钱包变动及现货退出深度。'}}</p>
                <p>Binance 现货资格与可退出深度待查。</p>
                <div v-if="row.longHoldReview">
                  <small>项目审阅 {{row.longHoldReview.reviewedOn}}</small>
                  <p v-for="source in row.longHoldReview.sources" :key="source.url"><a :href="source.url" target="_blank" rel="noopener noreferrer">{{source.title}} ↗</a></p>
                </div>
                <details><summary>查看市值、量价和筹码资料</summary><p v-for="metric in row.metrics" :key="metric.metric">{{metricNames[metric.metric]??metric.metric}} · {{number(metric.value,4)}} · {{label(metric.status)}}</p></details>
                <small>最近采集 {{time(row.lastCollectedAt)}}</small>
              </article>
            </div>
          </section>
          <section v-if="data.history?.report" class="panel"><div class="panel-heading"><h2>历史样本检验</h2><span class="muted">价格/成交量探索基线 · {{time(data.history.report.generatedAt)}}</span></div>
            <p class="panel-note">固定条件：突破过去 20 小时最高价、量比≥1.5、24h 涨幅 0～15%、主动买入占比≥0.52。只看当时已闭合 K 线；同币触发至少间隔 7 天。参考价不是成交价，下面不是含成本收益或预测概率。</p>
            <div class="table-wrap"><table><thead><tr><th>样本 / 作用</th><th>历史覆盖</th><th>触发数</th><th>结果明细</th></tr></thead><tbody><tr v-for="sample in data.history.report.samples" :key="sample.sample.symbol"><td>{{sample.sample.symbol}}<small>{{sample.sample.role==='UserCaseDiscovery'?'用户案例（探索）':sample.sample.role==='Comparison'?'同期对照（未按失败结果挑选）':'身份与覆盖待核实'}}</small></td><td>{{sample.status==='PriceHistoryReady'?'价格归档通过':'覆盖不完整'}}<small>{{sample.bars}} 小时 · 缺口 {{sample.missingHourlyIntervals}} 小时</small></td><td>{{sample.triggers.length}}</td><td><details><summary>查看 7 / 30 天结果</summary><p>{{sample.sample.selectionReason}}</p><p v-for="missing in sample.missingArchives" :key="missing">{{missing}}</p><div v-for="trigger in sample.triggers" :key="trigger.at"><strong>{{time(trigger.at)}}</strong><p v-for="outcome in trigger.outcomes" :key="outcome.days">{{outcome.days}} 天：{{outcome.complete?`闭合价变化 ${number(outcome.closeReturnPercent)}%；区间最高 ${number(outcome.maximumRisePercent)}%；最低 ${number(outcome.maximumFallPercent)}%；收盘路径最大回撤 ${number(outcome.maximumDrawdownPercent)}%`:'未来窗口不足，结果缺失'}}</p></div><p v-if="!sample.triggers.length">此区间无符合该固定条件的触发。</p></details></td></tr></tbody></table></div>
            <details><summary>检验限制与缺项</summary><p v-for="limitation in data.history.report.limitations" :key="limitation" class="panel-note">{{limitation}}</p><p class="panel-note">尚无独立留出检验；重叠窗口不是独立交易样本。持币集中度和低市值条件仍需历史验证。</p></details>
          </section>
          <section class="panel"><div class="panel-heading"><h2>持币地址持续观察</h2><span class="muted">每 6 小时 · GoPlus · 研究资料</span></div>
            <p class="panel-note">这里保存实际地址与余额。至少两次相隔 5 小时的快照后显示变化；时间按快照接收时刻记录。</p>
            <div v-if="!data.holders?.length" class="compact-empty">等待已核实链上身份的持币资料。</div>
            <article v-for="item in data.holders??[]" :key="item.assetId" class="radar-card">
              <div class="card-title"><h3>{{item.symbol}}</h3><span class="badge">{{!item.snapshot?'尚无有效快照':!item.fresh?'快照已失鲜':item.source?.failure&&item.source.failure!=='None'?'最近更新失败，保留上次快照':'已有地址快照'}}</span></div>
              <p v-if="item.source?.failure&&item.source.failure!=='None'" class="muted">最近采集：{{label(item.source.failure)}} · 重试时间 {{time(item.source.retryAt)}}</p>
              <template v-if="item.snapshot">
                <p>供应商持有人数 {{number(item.snapshot.holderCount,0)}} · 所列 {{item.snapshot.addresses.length}} 个条目占总供应 {{number(item.snapshot.listedSupplyPercent)}}% · 已保存 {{item.sampleCount}}{{item.sampleCount===32?'（最近窗口）':''}} 次</p>
                <small>链 {{item.snapshot.chain}} · 接收 {{time(item.snapshot.receivedAt)}} · 证据 {{item.snapshot.evidenceId}}</small>
                <div v-if="item.roles" class="holder-role-summary">
                  <strong>持仓用途拆解 · 占总供应</strong>
                  <p><span v-for="category in item.roles.categories.filter((c:any)=>c.role!=='Unknown'&&c.addresses>0)" :key="category.role">{{roleNames[category.role]}} {{number(category.supplyPercent,4)}}%（{{category.addresses}} 条） · </span><span v-if="item.roles.sourceAttributedPercent===0">尚无已核实用途 · </span>前十用途未知 {{number(item.roles.unknownListedPercent,4)}}% · 榜外未分析 {{number(item.roles.outsideListPercent,4)}}%</p>
                  <small>来源标注覆盖 {{number(item.roles.sourceAttributedPercent,4)}}%；未知中合约占 {{number(item.roles.unknownContractPercent,4)}}%（子集，不重复相加）。未列出的分类表示尚未识别，不代表不存在。未计算去除后的全网前十或庄家占比。</small>
                </div>
                <p v-if="item.comparison?.status==='ReceiptSnapshotsOnly'">相对 {{time(item.comparison.baselineReceivedAt)}}：所列地址占比变化 {{number(item.comparison.listedShareChangePoints,4)}} 个百分点，持有人数变化 {{number(item.comparison.holderCountChange,0)}}{{item.comparison.supplyChanged?'；总供应也有变化':''}}。仅比较供应商快照，不代表吸筹。</p>
                <p v-else class="muted">等待至少相隔 5 小时、口径一致的第二次快照。</p>
                <details><summary>查看持币地址、用途来源与变化</summary><div class="table-wrap"><table><thead><tr><th>地址</th><th>余额 / 总供应占比</th><th>用途及证据</th><th>供应商标签 / 类型</th><th>与基线余额变化</th></tr></thead><tbody>
                  <tr v-for="holder in item.snapshot.addresses" :key="holder.address"><td class="endpoint">{{holder.address}}<details v-if="holder.tokenAccounts.length"><summary>Token accounts（{{holder.tokenAccounts.length}}）</summary><p v-for="account in holder.tokenAccounts" :key="account">{{account}}</p></details></td><td>{{number(holder.balance,4)}}<small>{{number(holder.supplyPercent,4)}}%</small></td><td class="endpoint"><strong>{{roleNames[holderRole(item,holder.address)?.role??'Unknown']}}</strong><small>{{holderRole(item,holder.address)?.entity??'主体未知'}} · {{roleStatusNames[holderRole(item,holder.address)?.status??'Unknown']}}</small><details v-if="holderRole(item,holder.address)?.evidence?.length"><summary>核对来源（{{holderRole(item,holder.address).evidence.length}}）</summary><p v-for="proof in holderRole(item,holder.address).evidence" :key="proof.id"><a :href="proof.sourceUrl" target="_blank" rel="noopener noreferrer">{{proof.sourceKind==='HistoricalIssuerDisclosure'?'历史披露（辅助）':proof.sourceKind==='ProtocolReference'?'协议源码/文档':'浏览器标签'}} ↗</a><br>{{proof.statement}}<br>复核 {{time(proof.reviewedAt)}}<br>有效至 {{time(proof.expiresAt)}}</p></details></td><td>{{holder.providerTag||'无标签'}}<small>{{holder.isContract===true?'合约':holder.isContract===false?'普通地址（用途未知）':'类型未知'}} · {{holder.isLocked===true?'供应商标记锁定':holder.isLocked===false?'供应商未标记锁定':'锁定未知'}}</small></td><td>{{number(holderChange(item,holder.address),4)}}<small>新进名单不当作从零买入</small></td></tr>
                </tbody></table></div><p v-for="change in item.comparison?.addresses?.filter((a:any)=>a.status==='LeftList')??[]" :key="change.address" class="muted">{{change.address}}：移出前十名单，当前余额未知。</p></details>
                <p class="footnote">{{item.limitation}}</p>
                <details><summary>按用途比较变化及市场参考</summary>
                  <p v-if="item.marketSchedule" class="muted">研究样本定期采集：OI 最长间隔 5 分钟，原始时间接近失鲜时提前重试；K 线每小时整点后更新，均受后台额度约束。下次 OI {{time(item.marketSchedule.nextOiAt)}} · 下次 K 线 {{time(item.marketSchedule.nextCandlesAt)}}。</p>
                  <p v-else class="muted">市场资料由核心或持仓观察任务维护；独立研究采集仅覆盖最多 4 个已核实、仍在交易的样本。</p>
                  <p v-if="!item.roleChanges?.length" class="muted">尚无可比较的第二轮快照；不推断买入、卖出或资金流向。</p>
                  <p v-for="change in item.roleChanges?.filter((r:any)=>r.comparableAddresses>0||r.enteredList>0||r.leftList>0)??[]" :key="change.role">{{roleNames[change.role]}}：同地址可比 {{change.comparableAddresses}} 条，余额变化 {{number(change.balanceChange,4)}}，总供应占比变化 {{number(change.supplyPercentChangePoints,4)}} 个百分点；新进 {{change.enteredList}} 条、移出 {{change.leftList}} 条。</p>
                  <small>两轮使用当前同一套用途证据，避免标签更新造成虚假的持仓变化；此处不是历史标签回放。交易所余额变动不代表交易所买卖，DEX 权限地址不是独立投资者。</small>
                  <p v-for="metric in item.marketEvidence??[]" :key="metric.metric">{{metricNames[metric.metric]??metric.metric}}：{{metric.fresh?number(metric.value,4):'资料缺失或失鲜'}} · {{time(metric.sourceTime)}}</p>
                  <small>市场参考与持币快照的时间不同，不把并列现象当作因果关系或自动买入建议。</small>
                </details>
              </template>
            </article>
          </section>
          <section v-if="data.identities?.length" class="panel"><div class="panel-heading"><h2>交叉核对的身份样本</h2><span class="muted">验证数据链路，不是推荐名单</span></div><article v-for="proof in data.identities" :key="proof.evidenceId" class="list-row"><div><strong>{{proof.identity.binanceSymbol}} · {{proof.identity.name}}</strong><small>市值平台 ID：{{proof.identity.coinGeckoId}} · 核对时间 {{time(proof.verifiedAt)}}</small><small v-for="token in proof.identity.chains" :key="token.chain">{{token.chain}} · {{token.address}} · 精度 {{token.decimals}}</small><a :href="proof.referenceUrl" target="_blank" rel="noopener noreferrer">查看独立参考来源 ↗</a></div></article></section>
          <div class="notice">本功能尚未完成妖币埋伏分析。全市场合约只是扫描范围；只有低市值、链上身份、所有权、流动性和当前活动证据都通过检查，才进入下方资料初筛池。资料齐备也不代表会涨十倍。</div>
          <div class="stats-grid"><article class="stat"><span>合约扫描范围</span><strong>{{radarCoverage?.scannedContracts??'—'}}</strong><p>排除 BTC / ETH；不是妖币数量</p></article><article class="stat"><span>已核实链上身份</span><strong>{{radarCoverage?.verifiedIdentities??'—'}}</strong><p>未映射 {{radarCoverage?.unmappedIdentities??'—'}} 个</p></article><article class="stat"><span>可信且新鲜的市值</span><strong>{{radarCoverage?.marketCapReady??'—'}}</strong><p>冲突值、未核实时间不计入</p></article><article class="stat"><span>研究资料初筛通过</span><strong>{{radarCoverage?.dataQualified??'—'}}</strong><p>仅资料资格，无收益概率或买入指令</p></article></div>
          <section class="panel"><div class="panel-heading"><div><h2>低市值研究资料初筛池</h2><span class="count">{{radarCoverage?.dataQualified??'—'}}</span></div><span class="muted">最多展示 24 个</span></div><div v-if="!radarRows.length" class="empty"><span>◎</span><h3>目前没有证据齐备的研究对象</h3><p>身份映射和链上覆盖尚未完成，等待普通合约轮换不会自动补齐这些资料。<br>下面公开已有资料的问题和实际能力边界。</p></div><div v-else class="radar-grid"><article v-for="row in radarRows" :key="row.assetId" class="radar-card"><div class="card-title"><h3>{{row.symbol}}</h3><span class="badge">{{label(row.stage)}}</span></div><p class="positive">当前低市值研究资料通过初筛，尚无埋伏结论。</p><details><summary>查看指标</summary><p v-for="metric in row.metrics" :key="metric.metric">{{metricNames[metric.metric]??metric.metric}}：{{number(metric.value,4)}} · {{label(metric.status)}}</p></details><small>最近采集 {{time(row.lastCollectedAt)}}</small></article></div></section>
          <section class="panel"><div class="panel-heading"><h2>已有研究资料核对</h2><span class="muted">仅展示已有关联资料的币，最多 12 个</span></div><div v-if="!radarChecks.length" class="compact-empty">尚未建立可核对的研究资料身份。</div><article v-for="row in radarChecks" :key="row.assetId" class="radar-card"><div class="card-title"><h3>{{row.symbol}}</h3><span class="badge">{{row.dataQualified?'资料初筛通过':'资料仍未齐备'}}</span></div><p v-for="chainToken in row.chains" :key="chainToken.chain" class="muted">{{chainToken.chain}} · {{chainToken.address}}</p><div class="table-wrap"><table><thead><tr><th>资料</th><th>已保存值</th><th>核对结果</th><th>原因及来源</th></tr></thead><tbody><tr v-for="metric in row.metrics.filter((m:any)=>['MarketCap','DexLiquidity','Top10','TotalClusterPercent'].includes(m.metric))" :key="metric.metric"><td>{{metricNames[metric.metric]}}</td><td>{{number(metric.value,4)}}</td><td>{{label(metric.status)}}</td><td class="endpoint">{{metric.reason}}<details v-if="metric.sources.length"><summary>来源数值及时间</summary><p v-for="(source,i) in metric.sources" :key="i">{{source.provider}}：{{number(source.value,4)}}<br>数据时间 {{time(source.sourceTime)}}<br>接收时间 {{time(source.receivedTime)}} · {{source.timeQuality}}</p></details></td></tr></tbody></table></div></article></section>
          <section class="panel"><div class="panel-heading"><h2>合约活跃参考</h2><span class="muted">最多 12 个 · 不属于低市值埋伏名单</span></div><p class="panel-note">当前活动资料新鲜 {{radarCoverage?.activityFresh??'—'}} 个，缺失或失鲜 {{radarCoverage?.activityStale??'—'}} 个；仅显示出现参与度、扩张或过热组合的合约。</p><div v-if="!radarActivity.length" class="compact-empty">当前没有符合活动组合的参考对象。</div><article v-for="row in radarActivity" :key="row.assetId" class="list-row"><div><strong>{{row.symbol}} · {{label(row.stage)}}</strong><small>低市值与链上证据未通过；活动不能证明庄家吸筹。</small><details><summary>查看活动指标</summary><p v-for="metric in row.metrics.filter((m:any)=>!['MarketCap','DexLiquidity','Top10','TotalClusterPercent'].includes(m.metric))" :key="metric.metric">{{metricNames[metric.metric]}}：{{number(metric.value,4)}} · {{label(metric.status)}}</p></details></div></article></section>
          <section class="panel"><div class="panel-heading"><h2>尚未实现的能力</h2></div><div class="compact-empty"><p v-for="item in data.radar?.limitations??[]" :key="item">{{item}}</p></div></section>
        </template>

        <DirectionCheck v-else />
        <footer>PULLET CRYPTO RADAR <span>发现 · 跟踪 · 验证</span></footer>
      </div>
    </main>
  </div>
</template>
