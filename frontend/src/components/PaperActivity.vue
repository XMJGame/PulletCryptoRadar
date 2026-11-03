<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {time,number} from '../format'
import {paperDirection} from '../paper'
import type {ForwardDecision} from '../forward'
type Count={name:string;count:number}
type Hour={hour:string;symbols:number;observations:number;insufficient:number;noSignal:number;riskSkipped:number;candidates:number;oiEligible:number}
type Activity={asOf:string;from:string;accountUpdatedAt:string;status:string;summary:string;universeSymbols:number;lastAnalysis:string|null;lastSignalHour:string|null;
 analyses:number;uniqueSymbols:number;hours:number;insufficient:number;noSignal:number;riskSkipped:number;candidates:number;oiEligibleCandidates:number;
 intentCreated:number;openings:number;cancellations:number;auditTruncated:boolean;earliestAnalysis:string|null;reasons:Count[];failedConditions:Count[];hourly:Hour[];
 blockedPlans:ForwardDecision[];executionEvents:{time:string;symbol:string;action:string;reason:string}[];notices:string[]}
const data=ref<Activity|null>(null)
const {error}=usePolling(async signal=>{data.value=await api<Activity>('/api/paper/activity',{signal})},15000)
</script>
<template><section class="detail paper-activity" aria-label="为什么没有新持仓"><h2>为什么没有新持仓</h2>
<p v-if="error" class="notice-error">开仓诊断读取失败：{{error}}。下方若有数据，为上次成功读取的缓存。</p>
<template v-if="data"><p><strong>{{data.status}}</strong> · {{data.summary}}</p>
<p>账本更新 {{time(data.accountUpdatedAt)}}；最近分析尝试 {{time(data.lastAnalysis)}}；最近完成小时 {{time(data.lastSignalHour)}}。本小时冻结 {{data.universeSymbols}} 个合约。</p>
<p>滚动24小时审计：{{time(data.from)}} → {{time(data.asOf)}}；有记录 {{data.hours}} 个小时、{{data.uniqueSymbols}} 个不同合约。首次分析记录 {{time(data.earliestAnalysis)}}。</p>
<div v-if="data.auditTruncated" class="notice-error">审计批次超过读取上限，以下是不完整统计。</div>
<div class="activity-counts"><div v-for="item in [{name:'保存的分析',count:data.analyses},{name:'数据不足',count:data.insufficient},{name:'量价未触发',count:data.noSignal},{name:'结构风险跳过',count:data.riskSkipped},{name:'结构候选',count:data.candidates},{name:'候选中OI符合',count:data.oiEligibleCandidates},{name:'创建模拟意向',count:data.intentCreated},{name:'实际模拟开仓',count:data.openings}]" :key="item.name"><small>{{item.name}}</small><strong>{{item.count}}</strong></div></div>
<p class="muted">“OI符合”属于候选的子集，意向与开仓属于执行事件；不能把这些数字相加。A/B×多空每币每小时最多4条，非独立交易次数。24小时中缺失的小时不算已检查。</p>
<details><summary>查看逐小时统计、风险拦截与执行事件</summary>
<div class="table-scroll"><table><thead><tr><th>分析小时</th><th>合约</th><th>分析</th><th>数据不足</th><th>未触发</th><th>结构风险</th><th>候选</th><th>候选OI符合</th></tr></thead><tbody><tr v-for="h in data.hourly" :key="h.hour"><td>{{time(h.hour)}}</td><td>{{h.symbols}}</td><td>{{h.observations}}</td><td>{{h.insufficient}}</td><td>{{h.noSignal}}</td><td>{{h.riskSkipped}}</td><td>{{h.candidates}}</td><td>{{h.oiEligible}}</td></tr></tbody></table></div>
<h3>原始未操作原因</h3><p v-for="c in data.reasons" :key="c.name">{{c.count}} 条 · {{c.name}}</p>
<h3>新版逐项量价条件失败数（可重叠）</h3><p v-for="c in data.failedConditions" :key="c.name">{{c.count}} 条 · {{c.name}}</p><p v-if="!data.failedConditions.length">尚无新版逐项条件记录；等待下一次自然小时分析，不追补旧判断。</p>
<h3>最近结构候选 / 风险跳过</h3><div class="table-scroll"><table><thead><tr><th>小时 / 标的</th><th>方向 / 策略</th><th>原判断</th><th>OI</th><th>原因</th></tr></thead><tbody><tr v-for="d in data.blockedPlans" :key="d.id"><td>{{d.symbol}}<small>{{time(d.decisionAt)}}</small></td><td>{{paperDirection(d.direction)}} / {{d.strategy}}</td><td>{{d.status==='Candidate'?'结构候选':'结构风险跳过'}}</td><td>{{d.oi24hPercent==null?'Unknown':number(d.oi24hPercent,2)+'%'}}</td><td>{{d.reason}}</td></tr><tr v-if="!data.blockedPlans.length"><td colspan="5">窗口内没有结构候选或风险跳过记录。</td></tr></tbody></table></div>
<h3>最近执行事件</h3><p v-for="(e,i) in data.executionEvents" :key="i">{{time(e.time)}} · {{e.symbol}} · {{e.action}} · {{e.reason}}</p><p v-if="!data.executionEvents.length">窗口内没有执行事件。旧版同币/冷却等静默跳过不事后推断，新版起保留原因。</p>
<p v-for="n in data.notices" :key="n" class="muted">{{n}}</p>
</details></template><p v-else>正在读取本地审计，不请求外部行情。</p>
</section></template>
<style scoped>.activity-counts{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:12px;margin:18px 0}.activity-counts>div{border:1px solid var(--border,#26313a);padding:12px;border-radius:8px}.activity-counts strong{display:block;font-size:25px;margin-top:6px}summary{cursor:pointer;margin:16px 0}h3{margin-top:24px}td{vertical-align:top}td small{display:block}.muted{opacity:.72}</style>
