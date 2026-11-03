<script setup lang="ts">
import {computed,ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,time} from '../format'
type Lane={key:string;name:string;stage:string;openPositions:number;closedPositions:number;settlementLegs:number;realizedNet:number;closedFees:number;closedFunding:number;openGrossPnl:number|null;averageClosedNet:number|null;closedWinRate:number|null}
type Review={asOf:string;lanes:Lane[];accounting:string}
const data=ref<Review|null>(null)
const {error}=usePolling(async signal=>{data.value=await api('/api/paper/strategies',{signal})},15000)
const lanes=computed(()=>data.value?.lanes.filter(l=>l.key==='Quant')??[])
</script>
<template><section class="detail"><div class="section-heading"><h2>当前策略累计表现</h2></div><p v-if="error" role="alert" class="radar-notice">{{error}}</p><template v-if="data"><section v-for="lane in lanes" :key="lane.key" class="performance-lane"><p>{{lane.name}} · {{lane.stage}}</p><div class="performance-grid"><div><span>已结算净额</span><strong>{{number(lane.realizedNet,4)}} U</strong></div><div><span>新鲜持仓毛浮盈亏</span><strong>{{number(lane.openGrossPnl,4)}} U</strong></div><div><span>持仓 / 完整平仓</span><strong>{{lane.openPositions}} / {{lane.closedPositions}}</strong></div><div><span>完整平仓胜率</span><strong>{{lane.closedWinRate==null?'暂无样本':number(lane.closedWinRate,1)+'%'}}</strong></div></div><p>结算{{lane.settlementLegs}}条 · 已分摊手续费{{number(lane.closedFees,4)}} U · 资金费及调整{{number(lane.closedFunding,4)}} U。小样本均值不代表未来收益。</p></section><p v-if="!lanes.length">当前策略暂无账户记录。</p><details><summary>统计口径</summary><p>{{data.accounting}}</p><p>开放仓毛浮盈未扣未来退出费用，不与已结算净额混为一项。妖币研究对象不计为已成交持仓或收益。</p><small>更新 {{time(data.asOf)}}</small></details></template></section></template>
<style scoped>.performance-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:18px 0}.performance-grid span{display:block;color:#92a3b2;font-size:11px}.performance-grid strong{display:block;font-size:19px;margin-top:10px;font-weight:500}.performance-lane+.performance-lane{border-top:1px solid #293a42;padding-top:15px}@media(max-width:750px){.performance-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}</style>
