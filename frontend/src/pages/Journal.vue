<script setup lang="ts">
import {computed,ref,watch} from 'vue'
import {useRoute,useRouter} from 'vue-router'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,time} from '../format'
import type {PaperOverview} from '../paper'
import PageIntro from '../components/PageIntro.vue'
import HoldingHistory from '../components/HoldingHistory.vue'
import StrategyReview from '../components/StrategyReview.vue'
import DailyResearch from '../components/DailyResearch.vue'
import QuantDaily from '../components/QuantDaily.vue'
import QuantPortfolioValidation from '../components/QuantPortfolioValidation.vue'
const route=useRoute(),router=useRouter(),data=ref<PaperOverview|null>(null),researchOpen=ref(false)
const view=computed(()=>['holdings','research'].includes(String(route.query.view))?String(route.query.view):'daily')
const tabs=[{key:'daily',label:'交易日报'},{key:'holdings',label:'持仓记录'},{key:'research',label:'研究与验证'}]
function select(key:string){router.replace({path:'/journal',query:key==='daily'?{}:{view:key}})}
const {error,refresh}=usePolling(async signal=>{if(view.value==='holdings')data.value=await api('/api/paper',{signal})},10000)
watch(view,()=>{void refresh()})
</script>
<template><PageIntro eyebrow="REVIEW · FORWARD EVIDENCE" title="复盘与验证" description="当前交易、持仓过程、研究验证分开查看。先核对证据与成本，再决定是否调整策略。"/><nav class="section-tabs" aria-label="复盘内容"><button v-for="tab in tabs" :key="tab.key" :aria-pressed="view===tab.key" :class="{active:view===tab.key}" @click="select(tab.key)">{{tab.label}}</button></nav>
<template v-if="view==='daily'"><QuantDaily/><StrategyReview/></template>
<template v-else-if="view==='holdings'"><p v-if="error" role="alert" class="radar-notice">{{error}}</p><details class="detail"><summary>查看全账户按日结算（含沿用仓位）</summary><p>共享账户自{{time(data?.state.startedAt)}}起的最近30个结算日，合并各执行版本。分批结算不是完整交易胜率；迟到资金费按入账日调整。</p><div class="table-scroll"><table><thead><tr><th>上海日期</th><th>结算条数 / 正净额条数</th><th>净额 U</th><th>手续费 / 资金费及调整</th></tr></thead><tbody><tr v-for="day in data?.days" :key="day.date"><td>{{day.date}}</td><td>{{day.trades}} / {{day.wins}}</td><td>{{number(day.netPnl,4)}}</td><td>{{number(day.fees,4)}} / {{number(day.fundingCost,4)}}</td></tr></tbody></table></div></details><HoldingHistory :positions="data?.state.positions" :trades="data?.state.recentTrades"/></template>
<template v-else><QuantPortfolioValidation/><div class="tool-grid"><RouterLink to="/quant-validation" class="tool-card"><span class="eyebrow">当前规则研究</span><h2>固定规则回放与验证</h2><p>查看留出检验、成本压力与前瞻输入核对。历史价格研究尚未通过盈利检验。</p><span>查看研究 →</span></RouterLink></div><details class="detail" @toggle="researchOpen=($event.target as HTMLDetailsElement).open"><summary>筹码与快查的研究跟踪</summary><DailyResearch v-if="researchOpen"/></details></template></template>
