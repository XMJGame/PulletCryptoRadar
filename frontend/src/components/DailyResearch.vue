<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,percent,time} from '../format'
import {shanghaiDate} from '../runtime'
type DailyReview={version:string;date:string;asOf:string;reports:number;monitoring:{targetId:string;symbol:string;contractAddress:string;snapshots:number;observedHalfHourSlots:number;completedDaySlots:number;firstSource:string|null;lastSource:string|null;hasComparable24hBaseline:boolean;finding:string;recordedReleaseDelta:string|null;releaseWindow:string|null;earliestOrdinaryRelease7Days:string|null;observedTransfers:number|null;confirmedExchangeInflows:number|null;candidateExchangeInflows:number|null;gaps:string[]}[];
outcomes:{verdict:string;hours:number;reports:number;observed:number;notDue:number;missing:number;noBenchmark:number;meanPriceChange:number|null;belowInvalidation:number}[];missing:string[];review:string[]}
const date=ref(shanghaiDate());const data=ref<DailyReview|null>(null);const localError=ref('')
async function load(signal?:AbortSignal){const selected=date.value;try{const result=await api<DailyReview>(`/api/research/daily?date=${encodeURIComponent(selected)}`,{signal});if(date.value===selected){data.value=result;localError.value=''}}catch(e){if(!signal?.aborted)localError.value=e instanceof Error?e.message:String(e)}}
usePolling(signal=>load(signal),60000)
</script>
<template><section class="detail"><h2>每日研究 · 筹码、释放与信号结果</h2><form class="toolbar" @submit.prevent="load()"><label>上海日期 <input v-model="date" type="date" :max="shanghaiDate()" aria-label="研究复盘日期"></label><button type="submit" class="replay-action">查看研究复盘</button></form>
<p v-if="localError" role="alert">{{localError}} · 已展示内容仍保留原评估时间。</p>
<template v-if="data"><p>{{data.date}}生成的快查报告 {{data.reports}} 份 · 评估 {{time(data.asOf)}} · {{data.version}}。每15分钟独立留档，读页面不改历史判断；本表按各观察实际可得数据计算。</p>
<article v-for="m in data.monitoring" :key="m.targetId" class="monitor-review"><h3>{{m.symbol}} · 固定观察集合</h3><p>{{m.snapshots}}份确认区块快照 · 已结束半小时槽有快照 {{m.observedHalfHourSlots}} / {{m.completedDaySlots}}（含当日未启动/停机等缺口，不是在线率）</p><p>当日源时间 {{time(m.firstSource)}} → {{time(m.lastSource)}} · 可比24h基线 {{m.hasComparable24hBaseline?'已有':'不足'}}</p><p>最近冻结记录：{{m.finding}}</p><p>可比观测窗口内合约已记录释放增量 {{m.recordedReleaseDelta??'Unknown'}} {{m.symbol}} · 当前7天普通可调用上界 {{m.earliestOrdinaryRelease7Days??'Unknown'}}（非实际解锁/卖压）</p><p v-if="m.releaseWindow">{{m.releaseWindow}}</p><p>当日已观察转账 {{m.observedTransfers??'Unknown'}} 条；最近24h转入已核验交易所 {{m.confirmedExchangeInflows??'Unknown'}} 条，候选历史标签 {{m.candidateExchangeInflows??'Unknown'}} 条。</p><details><summary>覆盖缺口与判断限制</summary><p v-for="g in m.gaps" :key="g">{{g}}</p></details></article>
<h3>当时判断 → 后续价格表现</h3><p>均值只计算已完成且基准可比的观察。相邻报告相关，不是独立交易；“暂不考虑买入”也保留后续涨跌，不能只挑成功案例。</p>
<p v-if="!data.outcomes.length">所选日还没有可对应的快查报告。</p><div v-else class="table-scroll"><table><thead><tr><th>原判断 / 观察点</th><th>报告 / 已观察</th><th>未到期 / 缺数据 / 无基准</th><th>已观察均值</th><th>该收盘低于失效位</th></tr></thead><tbody><tr v-for="g in data.outcomes" :key="`${g.verdict}:${g.hours}`"><td>{{g.verdict}}<small>{{g.hours===168?'7天':`${g.hours}h`}}</small></td><td>{{g.reports}} / {{g.observed}}</td><td>{{g.notDue}} / {{g.missing}} / {{g.noBenchmark}}</td><td>{{percent(g.meanPriceChange)}}</td><td>{{g.observed?number(g.belowInvalidation,0):'Unknown'}}</td></tr></tbody></table></div><p v-for="m in data.missing" :key="m">{{m}}</p><p v-for="r in data.review" :key="r">{{r}}</p></template></section></template>
<style scoped>input{background:#101820;color:inherit;border:1px solid #354550;border-radius:4px;padding:7px}.monitor-review{border-bottom:1px solid #2a3742;padding:10px 0}small{display:block;color:#9aaebe;margin-top:6px}p{overflow-wrap:anywhere}summary{cursor:pointer}</style>
