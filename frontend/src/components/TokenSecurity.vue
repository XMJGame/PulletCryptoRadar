<script setup lang="ts">
import {computed} from 'vue'
import type {TokenSecurityEvidence} from '../onchain'
import {percentage} from '../onchain'
import {number,time} from '../format'
const props=withDefaults(defineProps<{evidence?:TokenSecurityEvidence|null;now:number;allowReview?:boolean}>(),{allowReview:true})
const emit=defineEmits<{review:[address:string]}>()
const evmFlags:Record<string,string>={is_open_source:'合约代码已公开',is_proxy:'代理合约',is_mintable:'可增发',is_honeypot:'蜜罐检测',cannot_sell_all:'存在全额卖出限制',transfer_pausable:'可暂停转账',owner_change_balance:'所有者可改余额',hidden_owner:'隐藏所有者',selfdestruct:'可自毁'}
const solanaFlags:Record<string,string>={mintable:'仍有增发权限',freezable:'仍有冻结权限',metadata_mutable:'元数据可修改',balance_mutable_authority:'余额修改权限',closable:'可关闭 Mint',default_account_state_upgradable:'可修改默认账户状态',transfer_fee_upgradable:'可修改转账费',transfer_hook_upgradable:'可修改转账钩子',non_transferable:'不可转让'}
const flags=computed(()=>props.evidence?.chain==='solana'?solanaFlags:evmFlags)
const claim=(v:boolean|null|undefined)=>v===true?'报告：是':v===false?'报告：否':'未提供'
const stale=computed(()=>{const t=Date.parse(props.evidence?.sourceTime??'');return !Number.isFinite(t)||t>props.now||props.now-t>45*60*1000})
</script>
<template>
<section class="detail">
<div class="section-heading"><h3>合约风险与地址核验线索</h3><a v-if="evidence" :href="evidence.evidenceUrl" target="_blank" rel="noopener noreferrer">{{evidence.provider}} 原始来源 ↗</a></div>
<p v-if="!evidence">等待下一轮定时采集补充 GoPlus 证据；已有主索引器快照继续保留。</p>
<template v-else>
<p>{{evidence.chain}} · {{evidence.contractAddress}}</p>
<p>源时间 {{time(evidence.sourceTime)}} · 接收 {{time(evidence.receivedTime)}} · {{stale?'过期或时间缺失，仅供复核':'观测时间在 45 分钟内'}}</p>
<p v-if="evidence.error" class="radar-notice">{{evidence.error}} · 最早重试 {{time(evidence.retryAt)}}</p>
<p>该来源报告持币地址 {{number(evidence.holderCount,0)}} 个、返回 {{evidence.holders.length}} 个大地址。与上方索引器分别保存，不拼接为 24h 增长。</p>
<p v-if="evidence.chain==='solana'" class="radar-notice">Solana Beta 数据同时保留 Owner 与 Token Account。人数按提供者口径，尚未证实是去重钱包数；返回条目不代表全链按 Owner 聚合的 Top10，集中度评分保持未知。元数据、名称及 trusted_token 标识不用于确认项目身份。</p>
<div class="detail-grid"><div v-for="(label,key) in flags" :key="key"><span>{{label}}</span><strong>{{claim(evidence.flags[key])}}</strong></div></div>
<details><summary>展开 {{evidence.holders.length}} 个待核验地址</summary><div class="table-scroll"><table><thead><tr><th>地址 / Owner</th><th>该来源供应量占比</th><th>合约 / 锁定</th><th>提供者标签</th><th>操作</th></tr></thead><tbody><tr v-for="h in evidence.holders" :key="h.tokenAccount??h.address"><td class="decision-reason">{{h.address}}<small v-if="evidence.chain==='solana'">Token Account · {{h.tokenAccount||'未提供'}}</small></td><td>{{percentage(h.percent)}}</td><td>{{claim(h.isContract)}} / {{claim(h.isLocked)}}</td><td>{{h.reportedTag||'未提供'}}</td><td><button v-if="allowReview" class="text-button" @click="emit('review',h.address)">核对归属</button><span v-else>仅线索，未确认归属</span></td></tr></tbody></table></div></details>
</template>
<p class="refresh-text">风险字段是提供者的检测结果，未检出不保证安全。地址是合约、带 LP 标签或未锁定，都不能证明实际控制人；这些线索不自动计入已核对分类或触发开仓。</p>
</section>
</template>
