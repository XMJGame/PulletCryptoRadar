<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,time,price} from '../format'
import {dailyState,funnelConsistent,shanghaiDate,executionAction,inputCategory,type DailyReport} from '../quantDaily'
import QuantEntryReviews from './QuantEntryReviews.vue'
import QuantLifecycle from './QuantLifecycle.vue'
import QuantWarmup from './QuantWarmup.vue'
import QuantReview from './QuantReview.vue'
import AccountPerformance from './AccountPerformance.vue'
const warmupOpen=ref(false)
const selected=ref(shanghaiDate()),data=ref<DailyReport|null>(null),actionError=ref('');let generation=0
async function load(signal?:AbortSignal){const id=++generation;const result=await api<DailyReport|null>(`/api/quant/daily?date=${encodeURIComponent(selected.value)}`,{signal});if(id===generation)data.value=result}
const {error}=usePolling(load,60000)
async function change(){actionError.value='';data.value=null;try{await load()}catch(e){actionError.value=e instanceof Error?e.message:String(e)}}
</script>
<template><section class="detail quant-daily"><div class="daily-heading"><div><h2>当前策略日报</h2><p>按信号去重，先查覆盖，再查机会与执行。仅虚拟资金。</p></div><label>上海日期 <input v-model="selected" type="date" :max="shanghaiDate()" @change="change"/></label></div>
<p v-if="error||actionError" role="alert" class="radar-notice">{{actionError||error}}</p><p v-if="!data&&!error&&!actionError" class="empty">新执行尚未启用，或正在读取本地复盘。</p>
<template v-if="data"><p>{{data.control.version}} · {{data.dayEnded?'当日窗口已结束':'当日进行中'}} · 截至 {{time(data.through)}}。只使用启用后冻结的前瞻信号。</p>
<AccountPerformance v-if="data.accountPerformance" :value="data.accountPerformance"/>
<p v-if="data.accountStale||data.auditTruncated||data.pathsLimited||!funnelConsistent(data)" role="alert" class="radar-notice">{{data.accountStale?'账户调度超过30秒未更新；请先检查服务。 ':''}}{{data.auditTruncated?'审计窗口被截断，意向和取消数是可观测下界。 ':''}}{{data.pathsLimited?'持仓路径仅展示最近30笔。 ':''}}{{!funnelConsistent(data)?'漏斗分类不守恒，请核对报告。':''}}</p>
<div class="daily-funnel"><div><small>冻结判断</small><strong>{{data.funnel.observations}}</strong></div><div><small>价格触发</small><strong>{{data.funnel.priceCandidates}}</strong></div><div><small>曾通过计划</small><strong>{{data.funnel.everReady}}</strong></div><div><small>观察到的意向</small><strong>{{data.funnel.observedIntents}}</strong></div><div><small>实际模拟开仓</small><strong>{{data.funnel.openings}}</strong></div></div>
<p>价格触发的去向：曾通过 {{data.funnel.everReady}} · 仅待数据 {{data.funnel.awaitDataOnly}} · 仅拒绝 {{data.funnel.rejectedOnly}} · 缺少评估 {{data.funnel.unassessed}}。通过但未观察到意向 {{data.funnel.readyWithoutObservedIntent}}；当日取消事件 {{data.funnel.cancellations}}。</p>
<div class="daily-costs"><h3>当日结算净额 {{number(data.costs.realizedNet,4)}} USDT</h3><p>{{data.costs.settlementLegs}}条结算 / {{data.costs.finalExits}}次最终退出。毛盈亏 {{number(data.costs.grossPnl,4)}} − 已分摊手续费 {{number(data.costs.fees,4)}} − 已分摊资金费 {{number(data.costs.settledFunding,4)}} − 迟到资金费调整 {{number(data.costs.lateFundingAdjustment,4)}}。</p><p>当日入场手续费现金支出 {{number(data.costs.entryFeesPaid,4)}}；实际资金费入账 {{number(data.costs.fundingBooked,4)}}（正付负收）。两项不再重复扣入上面的结算净额。未平仓浮盈不计作已实现盈利。</p></div>
<QuantReview v-if="data.review" :value="data.review"/>
<h3>为什么没有开仓</h3><p v-if="!data.funnel.priceCandidates">当日冻结判断尚未出现价格触发。先看下面的覆盖与价格条件，不能据此判断策略过严。</p><p v-for="reason in data.reasons" :key="reason.name">{{reason.name}}：{{reason.count}}个信号</p>
<details><summary>价格条件未通过次数（同一判断可有多个原因）</summary><p v-for="reason in data.failedConditions" :key="reason.name">{{reason.name}}：{{reason.count}}</p><p v-if="!data.failedConditions.length">暂无已记录的失败条件。</p></details>
<details :open="data.candidates.length>0"><summary>价格候选与计划去向 · {{data.candidates.length}}</summary><div class="table-scroll"><table><thead><tr><th>信号</th><th>冻结时间</th><th>计划去向</th><th>依据</th></tr></thead><tbody><tr v-for="c in data.candidates" :key="c.id"><td>{{c.symbol}}<br/>{{c.strategy}} / {{c.direction==='Long'?'多':'空'}}</td><td>{{time(c.capturedAt)}}</td><td>{{dailyState(c.outcome)}}<br/>{{c.assessmentSamples}}次有效期内评估</td><td class="decision-reason">{{c.reason}}<br/><small v-if="c.firstReadyAt">首次通过 {{time(c.firstReadyAt)}}</small></td></tr></tbody></table></div></details>
<details :open="data.executionEvents.some(e=>e.action==='取消新意向'||e.action==='新仓准入拦截')"><summary>实际执行与取消原因 · 最近{{data.executionEvents.length}}条</summary><p v-for="(e,i) in data.executionEvents" :key="i">{{time(e.time)}} · {{e.symbol}} · {{executionAction(e.action)}}：{{e.reason}}</p><p v-if="!data.executionEvents.length">暂无已观察的执行事件。</p></details>
<QuantEntryReviews :window="data.entryReviews" :first-at="data.entryDiagnosticsStartedAt" :version="data.entryImplementationVersion"/>
<h3>持仓路径 · {{data.paths.length}}</h3><p v-if="!data.paths.length" class="empty">所选日暂无当前策略的持仓记录。</p><details v-for="p in data.paths" :key="p.positionId" class="daily-path"><summary>{{p.symbol}} · {{p.direction==='Long'?'做多':'做空'}} · {{p.leverage}}倍 · {{p.openNow?'仍持仓':'已平仓'}} · {{p.strategy}}</summary>
<p>入场 {{price(p.entry)}}，初始数量 {{number(p.initialQuantity,6)}}，剩余 {{number(p.remainingQuantity,6)}}；初始保证金 {{number(p.initialMargin,4)}} U，计划风险 {{number(p.plannedRisk,4)}} U。</p>
<p>信号冻结 {{time(p.signalAt)}} → 意向 {{time(p.intentAt)}} → 成交 {{time(p.openedAt)}}；盘口来源 {{time(p.bookAt)}} / 接收 {{time(p.bookReceivedAt)}}。{{p.subsequentBook?'已核对严格晚于意向的盘口':'后续盘口证据未通过核对'}}；原始输入{{p.inputArchived?'已存档':'缺失'}}。</p>
<p v-if="p.entryImplementationVersion">入场执行实现 {{p.entryImplementationVersion}} · <a v-if="p.entryReviewId" :href="`/api/quant/entry-review/${p.entryReviewId}`" target="_blank" rel="noopener">原始复核记录 →</a></p><p v-else>此仓入场早于逐次复核留档，未补写执行实现和前后值。</p>
<p>初始结构止损 {{price(p.initialStop)}}：{{p.stopBasis}}。目标1 {{price(p.target1)}}（{{p.target1Basis}}），目标2 {{price(p.target2)}}（{{p.target2Basis}}）。实际减仓仍取决于当时盘口、最小数量与最小名义额。</p>
<p>入场环境 {{p.environment}} · 量比 {{number(p.volumeRatio,2)}} · OI数量1h / 24h变化 {{number(p.oi1h,2)}}% / {{number(p.oi24h,2)}}%。这些是当时证据，不是未来胜率。</p>
<p>截至报告生成累计结算 {{p.settlementLegs}}条，已结算净额（含迟到调整）{{number(p.settledNet,4)}} U；资金费覆盖 {{p.fundingStatus}}。</p>
<p v-if="p.openNow">报告生成时未平仓毛浮盈亏 <strong>{{number(p.openGrossPnl,4)}} U</strong>；未分摊开仓费 {{number(p.remainingOpenFee,4)}} U，已分配给剩余仓位的资金费 {{number(p.allocatedOpenFunding,4)}} U。未含未来退出费用；Unknown表示报价失鲜或缺失。</p>
<p>{{p.freshSamples}}个新鲜采样点，顺持仓方向价格最好 {{number(p.sampledBestPricePercent,2)}}% / 最差 {{number(p.sampledWorstPricePercent,2)}}%；这是未乘杠杆的采样路径，{{p.samplesLimited?'仅最近1000条，较早路径可能遗漏。':'可能遗漏采样间的极值。'}}</p>
<QuantLifecycle v-if="p.lifecycle" :value="p.lifecycle"/><template v-else><p v-for="(exit,i) in p.exits" :key="i">{{exit}}</p></template><small>{{p.signalId}}</small></details>
<details><summary>整点覆盖与缺口 · {{data.hours.length}}个小时</summary><div class="table-scroll"><table><thead><tr><th>小时</th><th>冻结币 / 计划范围</th><th>启用后判断 / 价格触发 / 未就绪判断</th><th>覆盖</th></tr></thead><tbody><tr v-for="h in data.hours" :key="h.hour"><td>{{time(h.hour)}}</td><td>{{h.frozenSymbols}} / {{h.expectedSymbols||'未知'}}</td><td>{{h.eligibleObservations}} / {{h.candidates}} / {{h.insufficient}}</td><td>{{dailyState(h.status)}}</td></tr></tbody></table></div></details>
<details v-if="data.inputIssues?.length" class="input-issues"><summary>未就绪明细 · {{data.inputIssues.length}}个合约时段</summary><p>按合约、小时、原因去重。同一币的多空与两类策略最多对应4条判断。成交额/上市范围排除与采集缺口分开列出；这里记录冻结时的资料状态，后续补齐也不追改旧判断。</p><p v-if="data.inputIssues.length>100">显示最近100项；完整明细见本日报JSON。</p><div class="table-scroll"><table><thead><tr><th>小时 / 合约</th><th>分类</th><th>判断数</th><th>冻结时原因</th></tr></thead><tbody><tr v-for="issue in data.inputIssues.slice(0,100)" :key="`${issue.hour}:${issue.symbol}:${issue.reason}`"><td>{{time(issue.hour)}}<br/>{{issue.symbol}}</td><td>{{inputCategory(issue.category)}}</td><td>{{issue.judgments}}</td><td class="decision-reason">{{issue.reason}}</td></tr></tbody></table></div></details>
<details class="warmup-panel" @toggle="warmupOpen=($event.target as HTMLDetailsElement).open"><summary>当前补数进度与重试</summary><QuantWarmup v-if="warmupOpen"/></details>
<details><summary>统计口径与限制</summary><p v-for="n in data.notices" :key="n">{{n}}</p></details><p><a :href="`/api/quant/daily?date=${data.date}`" target="_blank" rel="noopener">查看本日报JSON →</a></p><small>{{data.version}} · 生成 {{time(data.generatedAt)}} · 账户版本 {{data.accountRevision}}。后台每5分钟保存本地日报，今天与昨日继续补证。</small></template></section></template>
<style scoped>.daily-heading{display:flex;justify-content:space-between;gap:20px;align-items:start;flex-wrap:wrap}.daily-heading input{display:block;background:#10171e;color:#d6e6ed;padding:9px;border:1px solid #30414b;border-radius:6px;margin-top:7px}.daily-funnel{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:10px;margin:20px 0}.daily-funnel>div{padding:14px;border:1px solid #263a40;border-radius:8px;background:#101a20}.daily-funnel strong{display:block;font-size:28px;color:#62dcb4;margin-top:8px}.daily-costs{border-left:2px solid #4bccaa;padding-left:16px;margin:22px 0}details{border-top:1px solid #26343c;padding:15px 0}summary{cursor:pointer}.daily-path small{overflow-wrap:anywhere}small{color:#8cabb8}@media(max-width:700px){.daily-funnel{grid-template-columns:repeat(2,minmax(0,1fr))}}</style>
