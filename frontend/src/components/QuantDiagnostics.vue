<script setup lang="ts">
import {computed,ref,watch} from 'vue'
import {api} from '../api'
import {number} from '../format'
import {quantLabel} from '../quant'
import {studyLabel} from '../quantStudy'
import {diagnosticLabel} from '../quantDiagnostics'
import type {DiagnosticReport} from '../quantDiagnostics'
const props=defineProps<{id:string;window:string;strategy:string;policy:string;cost:number}>()
const data=ref<DiagnosticReport|null>(null);const loading=ref(false);const error=ref('')
watch(()=>[props.id,props.window,props.strategy,props.policy,props.cost] as const,async([id,window,strategy,policy,cost],_,cleanup)=>{
  const controller=new AbortController();cleanup(()=>controller.abort());data.value=null;error.value='';loading.value=true
  try{const q=new URLSearchParams({id,window,strategy,policy,cost:String(cost)});const value=await api<DiagnosticReport>(`/api/quant/validation/diagnostics?${q}`,{signal:controller.signal});if(controller.signal.aborted)return
    if(value.studyId!==id||value.window!==window||value.strategy!==strategy||value.policy!==policy||value.costMultiplier!==cost)throw new Error('诊断与当前筛选不一致')
    data.value=value
  }catch(e){if(!controller.signal.aborted)error.value=e instanceof Error?e.message:String(e)}finally{if(!controller.signal.aborted)loading.value=false}
},{immediate:true})
const sections=computed(()=>['Direction','Environment','ExitPath'].map(d=>({dimension:d,rows:data.value?.groups.filter(g=>g.dimension===d)??[]})))
const contributions=computed(()=>['Symbol','UtcDate'].map(d=>({dimension:d,rows:(data.value?.groups.filter(g=>g.dimension===d)??[]).slice().sort((a,b)=>d==='Symbol'?b.metrics.sumNetR-a.metrics.sumNetR:a.key.localeCompare(b.key))})))
</script>
<template><section class="detail diagnostic" aria-label="回放亏损与成本分解"><h2>亏损与成本分解</h2><p>{{studyLabel(window)}} · {{quantLabel(strategy)}} · {{studyLabel(policy)}} · {{cost}}倍成本</p><p class="radar-notice">看过结果后的探索性诊断。某个方向、币种或环境表现较好，不能直接改成交易准入条件；本次留出已经被查看。</p><p v-if="loading" role="status">读取完整本地研究存档并核对成本。</p><p v-if="error" role="alert">{{error}}</p>
<template v-if="data"><p>完整且记账一致 {{data.metrics.completed}} / {{data.metrics.samples}}；未完成 {{data.metrics.unfinished}}，记账异常 {{data.metrics.invalidAccounting}}。平均持有 {{number(data.metrics.meanHoldingHours,1)}} 小时。</p>
<h3>同一路径的平均成本桥接（R）</h3><div class="bridge"><span>还原滑点前毛R<strong>{{number(data.metrics.meanRawGrossR,3)}}</strong></span><span>减：滑点R<strong>{{number(data.metrics.meanSlippageR,3)}}</strong></span><span>减：手续费R<strong>{{number(data.metrics.meanFeeR,3)}}</strong></span><span>减：资金费压力R<strong>{{number(data.metrics.meanFundingR,3)}}</strong></span><span>模型净R<strong>{{number(data.metrics.meanNetR,3)}}</strong></span></div><p class="muted">{{data.metrics.costFlipped}} / {{data.metrics.completed}}条完整样本在还原滑点后的毛额为正，扣除模型成本后不为正。桥接保持原数量及退出路径，不是零成本策略回测。</p>
<template v-for="section in sections" :key="section.dimension"><h3>{{diagnosticLabel(section.dimension)}}</h3><div class="table-wrap"><table><thead><tr><th>分组</th><th>完整 / 样本</th><th>未完成 / 异常</th><th>平均净R</th><th>平均毛R</th><th>平均持有小时</th></tr></thead><tbody><tr v-for="g in section.rows" :key="g.key"><td>{{diagnosticLabel(g.key)}}</td><td>{{g.metrics.completed}} / {{g.metrics.samples}}</td><td>{{g.metrics.unfinished}} / {{g.metrics.invalidAccounting}}</td><td>{{number(g.metrics.meanNetR,3)}}</td><td>{{number(g.metrics.meanRawGrossR,3)}}</td><td>{{number(g.metrics.meanHoldingHours,1)}}</td></tr></tbody></table></div></template>
<h3>退出方式：同一入场配对</h3><p>种子一致且完整配对 {{data.pairing.matched}} 组；只在尾仓方案出现 {{data.pairing.tailOnly}}，只在首目标全退出现 {{data.pairing.fullOnly}}；路径未完整 {{data.pairing.unfinishedPairs}}，配对异常 {{data.pairing.invalidPairs}}。</p><p>尾仓减首目标全退的平均净R差：{{number(data.pairing.meanTailMinusFullR,3)}}。尾仓较好 {{data.pairing.tailBetter}}、全退较好 {{data.pairing.fullBetter}}、相同 {{data.pairing.equal}}。仅比较可配对机会，不能推断之后哪种退出更优。</p>
<h3>集中度敏感性检查</h3><p v-for="c in data.concentration" :key="c.dimension">{{diagnosticLabel(c.dimension)}}：剔除最多三个正贡献组 {{c.removedPositiveGroups.join('、')||'无'}} 后，剩余 {{c.remainingCompleted}} 条完整样本，平均净R {{number(c.meanWithoutTopThree,3)}}。所剔除组占正贡献分组合计 {{number(c.topThreeShareOfPositiveGroupNet,1)}}%。</p>
<details v-for="section in contributions" :key="section.dimension"><summary>{{diagnosticLabel(section.dimension)}}明细</summary><p class="muted">按UTC信号日期分组；币种按样本净R之和排序。净R之和是机会贡献，不能相加成账户收益。</p><div class="table-wrap"><table><thead><tr><th>分组</th><th>完整 / 样本</th><th>未完成 / 异常</th><th>平均净R</th><th>样本净R之和</th></tr></thead><tbody><tr v-for="g in section.rows" :key="g.key"><td>{{g.key}}</td><td>{{g.metrics.completed}} / {{g.metrics.samples}}</td><td>{{g.metrics.unfinished}} / {{g.metrics.invalidAccounting}}</td><td>{{number(g.metrics.meanNetR,3)}}</td><td>{{number(g.metrics.sumNetR,3)}}</td></tr></tbody></table></div></details>
<details><summary>诊断方法与存档摘要</summary><p v-for="n in data.notes" :key="n" class="muted">{{n}}</p><p class="muted">{{data.version}} · 研究 {{data.studyId}}；研究摘要 {{data.studyHash}}；完整逐笔摘要 {{data.tradeHash}}。</p></details></template></section></template>
<style scoped>
.table-wrap{max-width:100%;overflow-x:auto}
.bridge{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:12px;margin:16px 0}.bridge span{padding:12px;background:#101b22;border:1px solid #30444e;border-radius:7px;color:#a7bbc6;font-size:13px}.bridge strong{display:block;font-size:21px;color:#dce9ee;margin-top:8px}.diagnostic h3{margin-top:24px}.diagnostic details{padding:15px 0}.diagnostic summary{cursor:pointer}.muted{overflow-wrap:anywhere}@media(max-width:900px){.bridge{grid-template-columns:repeat(2,minmax(0,1fr))}}</style>
