<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {time} from '../format'
type Row={symbol:string;hour:string;startedAt:string;updatedAt:string;nextAttemptAt:string;status:string;phase:string;missingHours:number;rejectedRows:number;fetchLimit:number;returnedRows:number;acceptedRows:number;reason:string}
type Status={asOf:string;hour:string;records:Row[];meaning:string}
const data=ref<Status|null>(null)
const {error}=usePolling(async signal=>{data.value=await api('/api/quant/warmup',{signal})},30000)
const status=(value:string)=>({Ready:'当次缓存通过',Missing:'仍缺资料',Failed:'阶段失败',Running:'采集中'}[value]??value)
const phase=(value:string)=>({ReadCache:'读缓存',History:'请求历史',PersistHistory:'保存历史',OpenInterest:'持仓量',Quotes:'报价',Recheck:'落库复查',Complete:'完成'}[value]??value)
</script>
<template><section class="warmup"><p v-if="error" role="alert" class="radar-notice">{{error}}</p><template v-if="data"><p>{{data.meaning}}</p><p v-if="!data.records.length">尚无本版本补数尝试记录，等待后台调度。</p><div v-else class="table-scroll"><table><thead><tr><th>合约 / 检查小时</th><th>状态 / 阶段</th><th>缺口 / 不合格行</th><th>请求上限 / 返回 / 接受</th><th>最近记录 / 下次检查</th><th>原因</th></tr></thead><tbody><tr v-for="r in data.records" :key="r.symbol"><td>{{r.symbol}}<small>{{time(r.hour)}}</small></td><td>{{status(r.status)}}<small>{{phase(r.phase)}}{{r.hour!==data.hour?' · 待本小时复核':''}}</small></td><td>{{r.missingHours}} / {{r.rejectedRows}}</td><td>{{r.fetchLimit}} / {{r.returnedRows}} / {{r.acceptedRows}}</td><td>{{time(r.updatedAt)}}<small>{{time(r.nextAttemptAt)}}</small></td><td>{{r.reason}}</td></tr></tbody></table></div><small>只读本地记录；面板打开时每30秒刷新，查看不会发起外部补数。更新时间 {{time(data.asOf)}}。</small></template></section></template>
<style scoped>.warmup td{white-space:normal;min-width:100px}.warmup small{display:block;color:#8da5af;overflow-wrap:anywhere}.warmup p{line-height:1.8}</style>
