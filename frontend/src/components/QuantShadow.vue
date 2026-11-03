<script setup lang="ts">
import {computed,ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,price,time} from '../format'
import {quantLabel} from '../quant'
import type {QuantStatus} from '../quant'
defineProps<{executionVersion?:string}>()
const data=ref<QuantStatus|null>(null);const filter=ref('focus');const search=ref('')
const {error}=usePolling(async signal=>{data.value=await api<QuantStatus>('/api/quant/status',{signal})},15000)
const latest=computed(()=>data.value?.latest)
const rows=computed(()=>[...(latest.value?.decisions??[])].sort((a,b)=>Number(b.status==='PriceCandidate')-Number(a.status==='PriceCandidate')||a.symbol.localeCompare(b.symbol)))
const visible=computed(()=>rows.value.filter(d=>(filter.value==='all'||filter.value==='focus'&&d.status!=='NoSignal'||d.strategy===filter.value)&&d.symbol.includes(search.value.trim().toUpperCase())).slice(0,80))
const count=(status:string)=>rows.value.filter(d=>d.status===status).length
const ready=computed(()=>latest.value?.assessments.filter(a=>a.status==='ShadowReady').length??0)
const archived=computed(()=>data.value?.forwardHours.reduce((n,h)=>n+h.observations,0)??0)
const assessment=(id:string)=>latest.value?.assessments.find(a=>a.decisionId===id)
</script>
<template><section class="detail quant-shadow" aria-label="当前合约信号观察">
<div class="quant-heading"><div><small>QUANT CORE · CONDITIONS AND RISK</small><h2>合约信号与风险检查</h2></div><span class="quant-badge">{{executionVersion?'已连接当前模拟策略':'独立影子计划'}}</span></div>
<p>顺势回踩、放量突破分别判断多空。先核对价格结构，再检查OI、市场环境、成本与组合风险。</p>
<p><RouterLink to="/quant-validation">查看固定规则回放与前瞻核对 →</RouterLink></p>
<p v-if="error||data?.error" class="radar-notice" role="alert">{{data?.error||error}}</p>
<p v-if="!data?.cohort">后台准备新的观察批次，暂未形成可评估结果。</p>
<template v-if="data?.cohort"><p class="muted">{{data.cohort.version}} · 前瞻起点 {{time(data.cohort.startedAt)}} · 已存 {{archived}} 条前瞻判断（最近48轮小时记录）。参数与原模拟盘独立。</p>
<p v-if="data.stale" class="radar-notice">影子快照尚未就绪或已超过90秒，不采用旧结果。</p>
<template v-if="latest"><div class="quant-stats"><div><small>当前观察</small><strong>{{quantLabel(latest.mode)}}</strong><span>{{time(latest.hour)}} 闭合小时</span></div><div><small>价格触发 / 全部判断</small><strong>{{count('PriceCandidate')}} / {{rows.length}}</strong><span>{{latest.universe.length}} 个合约 × 两类策略 × 多空</span></div><div><small>计划风险通过</small><strong>{{ready}}</strong><span>{{executionVersion?'方案数，实际成交另查账户':'方案数，未创建任何仓位'}}</span></div><div><small>环境 / 新鲜广度</small><strong>{{quantLabel(latest.context.state)}}</strong><span>{{latest.context.freshSymbols}} 个 · 上涨 {{number(latest.context.positiveBreadthPercent,1)}}%</span></div></div>
<p class="muted">BTC 4h：{{quantLabel(latest.context.btcTrend)}}；ETH 4h：{{quantLabel(latest.context.ethTrend)}}。参考权益 {{number(latest.portfolio.equity)}} U，原盘占用 {{latest.portfolio.reservations.length}} 笔。资料不足 {{count('Insufficient')}} 条，未触发 {{count('NoSignal')}} 条。更新 {{time(latest.asOf)}}。</p>
<p v-if="latest.mode==='CurrentPreview'" class="radar-notice">这是当前结构预览，不计入前瞻信号或成交。新批次只在之后整点的前10分钟冻结判断。</p>
<details><summary>查看条件、风险拒绝原因与影子计划</summary>
<div class="quant-filters"><label>查看<select v-model="filter" aria-label="量化策略筛选"><option value="focus">触发与资料不足</option><option value="TrendPullback">顺势回踩 · 全部</option><option value="Breakout20">放量突破 · 全部</option><option value="all">全部判断</option></select></label><label>合约<input v-model="search" placeholder="如 BTCUSDT" aria-label="查找量化合约"/></label><span>最多显示80条；完整前瞻输入保存在本地档案。</span></div>
<p v-if="visible.length===0">当前筛选没有记录。未触发的判断可在“全部判断”查看。</p>
<details v-for="d in visible" :key="d.id" class="quant-decision"><summary><strong>{{d.symbol}}</strong> · {{quantLabel(d.strategy)}} · {{quantLabel(d.direction)}} <span>{{quantLabel(assessment(d.id)?.status??d.status)}}</span></summary>
<p>{{assessment(d.id)?.reason??d.reason}}</p><p class="muted">{{quantLabel(d.mode)}} · 判断时间 {{time(d.capturedAt)}} · 有效至 {{time(d.expiresAt)}}。OI 1h {{number(d.oi.change1hPercent)}}% / 24h {{number(d.oi.change24hPercent)}}%；{{quantLabel(d.oi.status)}}。{{d.oi.meaning}}</p>
<div class="table-wrap"><table><thead><tr><th>价格条件</th><th>实际</th><th>要求</th><th>结果</th></tr></thead><tbody><tr v-for="c in d.conditions" :key="c.key"><td>{{c.name}}</td><td>{{c.actual==null?'见规则':number(c.actual,4)}}</td><td>{{c.requirement}}</td><td>{{c.passed?'通过':'未齐'}}</td></tr></tbody></table></div>
<p v-if="d.stop!=null">结构失效价 {{price(d.stop)}}；{{d.stopBasis}}。</p>
<div v-if="assessment(d.id)"><ul><li v-for="c in assessment(d.id)!.checks" :key="c.key">{{c.name}}：{{c.actual==null?'Unknown':number(c.actual,4)}}；{{c.requirement}} · {{c.passed?'通过':'未通过'}}</li></ul>
<template v-if="assessment(d.id)?.plan"><p><strong>影子方案：</strong>{{assessment(d.id)!.plan!.leverage}}倍 · 数量 {{price(assessment(d.id)!.plan!.quantity)}} · 保证金 {{number(assessment(d.id)!.plan!.margin)}} U · 含预留计划风险 {{number(assessment(d.id)!.plan!.estimatedRisk,4)}} U。</p><p>估算入场 {{price(assessment(d.id)!.plan!.entry)}}；止损 {{price(assessment(d.id)!.plan!.stop)}}；首目标 {{price(assessment(d.id)!.plan!.target1)}}（{{assessment(d.id)!.plan!.target1Basis}}），次目标 {{price(assessment(d.id)!.plan!.target2)}}（{{assessment(d.id)!.plan!.target2Basis}}）。</p><p class="muted">退出计划：30% / 30% / 40%尾仓，最长{{assessment(d.id)!.plan!.maximumHoldHours}}小时。强平估值 {{price(assessment(d.id)!.plan!.liquidationEstimate)}} 含假设，不能保证不穿仓。</p></template></div>
<small class="muted">参数 {{d.inputHash?latest.rulesHash.slice(0,12):'Unknown'}} · 输入 {{d.inputHash.slice(0,12)}}（{{d.inputArchived?'原始输入已存档':'当前计算摘要，未作为前瞻样本存档'}}）</small>
</details>
</details>
<details><summary>观察边界与最近前瞻记录</summary><p v-for="n in data.notices.filter(n=>!executionVersion||!n.includes('隔离，不创建'))" :key="n" class="muted">{{n}}</p><p class="muted">热点成本补采：{{data.costTargets.join('、')||'暂无'}}（最多3个）。每笔基础风险0.4%且最多4 U，先以半额校准；总计划风险最多2%且最多20 U，同时包含原盘风险。</p><div v-if="data.forwardHours.length" class="table-wrap"><table><thead><tr><th>闭合小时</th><th>合约 / 判断</th><th>价格触发</th><th>资料不足</th></tr></thead><tbody><tr v-for="h in data.forwardHours" :key="h.hour"><td>{{time(h.hour)}}</td><td>{{h.symbols}} / {{h.observations}}</td><td>{{h.priceCandidates}}</td><td>{{h.insufficient}}</td></tr></tbody></table></div><p v-else>尚无符合批次时间边界的前瞻冻结记录。</p></details>
</template></template></section></template>
<style scoped>
.quant-shadow{border-color:#28695c}.quant-heading{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}.quant-heading h2{margin-top:6px}.quant-heading small{letter-spacing:.12em;color:#79b6aa}.quant-badge{padding:7px 12px;border:1px solid #3c8273;border-radius:20px;color:#9ce2cf;font-size:12px}.quant-stats{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:20px 0}.quant-stats>div{padding:16px;background:#101b22;border:1px solid #293b46;border-radius:8px}.quant-stats small,.quant-stats span{display:block;color:#8da7bb;font-size:12px}.quant-stats strong{display:block;font-size:21px;margin:9px 0;color:#d7efec}.quant-filters{display:flex;gap:16px;align-items:end;flex-wrap:wrap;margin:16px 0}.quant-filters label{display:grid;gap:6px}.quant-filters input,.quant-filters select{padding:9px;background:#101820;border:1px solid #3b4c59;color:inherit;border-radius:4px}.quant-filters span{font-size:12px;color:#8da7bb}.quant-decision{padding:14px 0;border-bottom:1px solid #293b46}.quant-decision summary{line-height:1.8}.quant-decision summary span{color:#8ccfbc;margin-left:14px}.quant-shadow>details{margin-top:18px}.quant-shadow summary{cursor:pointer}.quant-shadow li{margin:7px 0}.quant-shadow .muted{overflow-wrap:anywhere}@media(max-width:1000px){.quant-stats{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:600px){.quant-stats{grid-template-columns:1fr}.quant-stats strong{font-size:18px}}
</style>
