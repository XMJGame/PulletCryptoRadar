<script setup lang="ts">
import {onMounted,onUnmounted,ref} from 'vue'
import {api} from '../api'
import {number,percent,time} from '../format'
import type {TokenMonitorOverview} from '../tokenMonitor'
const data=ref<TokenMonitorOverview|null>(null);const error=ref('');let timer:ReturnType<typeof setInterval>|undefined;let request:AbortController|undefined
async function refresh(){request?.abort();request=new AbortController();const current=request;try{const value=await api<TokenMonitorOverview>('/api/token-monitoring',{signal:current.signal});if(!current.signal.aborted){data.value=value;error.value=''}}catch{if(!current.signal.aborted)error.value='持续研究状态暂不可读；先前快查报告仍可查看。'}}
onMounted(()=>{void refresh();timer=setInterval(()=>void refresh(),60000)});onUnmounted(()=>{request?.abort();clearInterval(timer)})
</script>
<template>
<section class="detail monitor"><h2>持续筹码与解锁观察 · 当前独立缓存</h2>
<p>这一栏是持续研究的当前记录，与下方历史快查报告的时间分开。固定地址余额不是全链前十排名，观察结果不作为模拟成交输入。</p>
<details><summary>展开 AIO / ZBT 的监测、锁仓参数与转账证据</summary>
<p v-if="error" role="status">{{error}}</p><p v-if="!data">正在读取本地监测状态…</p>
<template v-if="data"><p class="refresh-text">每{{data.pollMinutes}}分钟按需采集；本小时RPC预留 {{data.budget.reservedHour}} / {{data.budget.hourLimit}}，近24小时 {{data.budget.reservedDay}} / {{data.budget.dayLimit}}。仍受全程序30GB/月流量预算约束。</p>
<article v-for="m in data.targets" :key="m.target.id" class="monitor-target">
<h3>{{m.target.symbol}} · {{m.target.coinGeckoId}} · {{m.fresh?'有新鲜确认区块观测':'等待有效观测/旧记录'}}</h3>
<p class="wallet">{{m.target.platform}} · {{m.target.contractAddress}}</p><p>{{m.identityStatus}}</p>
<p v-if="m.identity">身份留证 {{time(m.identity.checkedAt)}} · 复核截止 {{time(m.identity.reviewAfter)}} · <a :href="m.identity.evidenceUrl" target="_blank" rel="noopener noreferrer">原始身份依据 ↗</a></p>
<p v-if="m.identityError||m.rpcError" class="radar-notice">{{m.identityError}} {{m.rpcError}}</p>
<p>确认区块 {{m.blockNumber??'Unknown'}} · 源时间 {{time(m.sourceTime)}} · 下次尝试 {{m.nextAttempt.startsWith('0001')?'等待调度':time(m.nextAttempt)}}</p>
<p>固定观察地址合计 / 总供应 {{percent(m.trackedSupplyPercent)}} · 24h基线 {{time(m.baselineTime)}}。缺少可比基线时不计算增减。</p>
<p v-if="m.finding">当时研究记录 · {{m.findingVersion}} · {{time(m.analyzedAt)}}：{{m.finding}}</p>
<details v-if="m.balances.length"><summary>展开 {{m.balances.length}} 个固定观察地址与余额变化</summary><div class="table-scroll"><table><thead><tr><th>地址 / 分类</th><th>当前代币数量 / 总供应占比</th><th>约24h数量 / 占比变化</th></tr></thead><tbody><tr v-for="b in m.balances" :key="b.address"><td class="wallet"><a :href="`https://bscscan.com/address/${b.address}`" target="_blank" rel="noopener noreferrer">{{b.address}} ↗</a><small>{{b.role}} · {{b.evidenceStatus}}</small></td><td>{{b.amount}}<small>{{percent(b.supplyPercent)}}</small></td><td>{{b.change24hAmount??'Unknown'}}<small>{{b.change24hPercentagePoints==null?'Unknown':`${number(b.change24hPercentagePoints,3)} 个百分点`}}</small></td></tr></tbody></table></div></details>
<template v-if="m.vesting"><h4>锁仓合约 · 已按区块比对运行代码</h4><p class="wallet"><a :href="m.vesting.sourceUrl" target="_blank" rel="noopener noreferrer">源码核对 ↗</a> · {{m.vesting.address}}</p><p class="wallet">管理员 {{m.vesting.admin}} · TGE状态 {{m.vesting.tgeReleased?'已释放':'未释放'}}。管理员实际身份/多签尚未确认。</p>
<p>{{m.vesting.permissionReview}}</p><p>从观测区块起7天，按管理员最早调用普通释放计算的额度上界：<strong>{{m.earliestPermitted7Days??'Unknown'}} {{m.target.symbol}}</strong> · 占总供应 {{percent(m.earliestPermitted7DaysSupplyPercent)}}。不是确定解锁量、实际流通增量或卖压；其他合约/余款路径仍须另查。</p>
<div class="table-scroll"><table><thead><tr><th>配置项 / 收款地址</th><th>上限 / 合约已记录释放</th><th>普通单次 / 间隔</th><th>当前下次可调用 / 状态</th><th>7天普通释放上界</th></tr></thead><tbody><tr v-for="r in m.releases" :key="r.name"><td>{{r.name}}<small class="wallet">{{r.recipient}}（控制人未知）</small></td><td>{{r.cap}} / {{r.released}}<small>剩余额度 {{r.remaining}}</small></td><td>{{r.perRelease}}<small>{{number(r.periodSeconds/86400,2)}} 天</small></td><td>{{time(r.nextRelease)}}<small>{{r.status}}</small></td><td>{{r.earliestPermitted7Days}}</td></tr></tbody></table></div></template>
<p v-else>尚无完成代码与代币身份核对的锁仓观测，不推算真实解锁日历。</p>
<h4>相关转账 · 已观测窗口</h4><p>{{m.transferCoverage}}</p><p v-if="m.sourceTime">起点 {{time(m.flowStartedAt)}} → 已处理 {{time(m.flowThroughTime)}}。近24h去重观察 {{m.observedTransfers??'Unknown'}} 条；转入已核验交易所 {{m.confirmedExchangeInflows??'Unknown'}} 条，转入历史标签候选交易所 {{m.candidateExchangeInflows??'Unknown'}} 条，后者不冒充已确认。</p><p v-else>尚未观测转账，不将缺失解释成零。</p>
<details v-if="m.transferEvidence.length"><summary>核对最近 {{m.transferEvidence.length}} 条转账证据</summary><p v-for="t in m.transferEvidence" :key="`${t.transactionHash}:${t.from}:${t.to}:${t.rawAmount}`" class="wallet"><a :href="`https://bscscan.com/tx/${t.transactionHash}`" target="_blank" rel="noopener noreferrer">{{time(t.blockTime)}} · 区块 {{t.blockNumber}} ↗</a><small>{{t.from}} → {{t.to}}</small><small>原始整数数量 {{t.rawAmount}}（非USD，不经过浮点转换）</small></p></details>
<details><summary>覆盖范围与判断限制</summary><p v-for="c in m.caveats" :key="c">{{c}}</p></details>
</article></template></details></section>
</template>
<style scoped>.monitor{border-color:#36574f}.monitor-target{border-top:1px solid #2b3944;margin-top:20px;padding-top:14px}.wallet,p{overflow-wrap:anywhere}.wallet{max-width:400px}small{display:block;color:#9aaebe;font-size:11px;margin-top:6px}details{margin:14px 0}h4{margin-bottom:10px}strong{color:#8ae0be}</style>
