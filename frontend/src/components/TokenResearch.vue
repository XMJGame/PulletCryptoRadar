<script setup lang="ts">
import type {HolderAnalysis,TokenResearch,TokenFollowup} from '../tokenCheck'
import {number,percent,price,time} from '../format'
defineProps<{holders?:HolderAnalysis|null;research?:TokenResearch|null;followup?:TokenFollowup|null;followupError?:string}>()
</script>
<template>
<section class="detail">
<h2>前十大地址 · 归属与筹码</h2>
<p v-if="!holders">这份报告没有地址研究结果；旧报告保留原样，重新分析可采集新版证据。</p>
<template v-else>
<p>{{holders.chain}} · 原始时间 {{time(holders.sourceTime)}} · {{holders.fresh?'观测在45分钟内':'缺失/失鲜或身份未确认'}}</p>
<div class="detail-grid"><div><span>原始前十 / 供应量</span><strong>{{percent(holders.rawTop10Percent)}}</strong></div><div><span>已核验托管/基础设施部分</span><strong>{{percent(holders.knownCustodyOrInfrastructurePercent)}}</strong></div><div><span>剩余已观察部分（仍含未知）</span><strong>{{percent(holders.remainingObservedPercent)}}</strong></div><div><span>返回余额的人工分类覆盖</span><strong>{{percent(holders.classifiedBalancePercent)}}</strong></div></div>
<p>{{holders.distributionMeaning}}</p>
<p v-if="!holders.holders.length">尚未取得有效持币地址；Unknown 不代表分散持币或不存在大户。</p>
<div v-else class="table-scroll"><table><thead><tr><th>{{holders.orderedTop10?'名次 / 地址':'返回样本 / 地址'}}</th><th>供应占比 / 约24h变化</th><th>分类 / 证据等级</th><th>提供者线索 / 归属原文</th></tr></thead><tbody>
<tr v-for="(h,i) in holders.holders" :key="h.address"><td class="wallet">{{h.rank??`样本${i+1}`}} · <a :href="h.explorerUrl" target="_blank" rel="noopener noreferrer">{{h.address}} ↗</a><small v-if="h.tokenAccount">Token Account {{h.tokenAccount}}</small></td><td>{{percent(h.percent)}}<small>{{h.change24hPercentagePoints==null?'24h基线不足':`${number(h.change24hPercentagePoints,3)} 个百分点`}}</small></td><td>{{h.role}}<small>{{h.confidence}}</small></td><td>{{h.reportedTag||'无提供者标签'}}<small>合约 {{h.isContract==null?'未知':h.isContract?'报告是':'报告否'}} · 锁定 {{h.isLocked==null?'未知':h.isLocked?'报告是':'报告否'}}</small><a v-if="h.evidenceUrl" :href="h.evidenceUrl" target="_blank" rel="noopener noreferrer">归属证据 ↗ · {{time(h.labelCheckedAt)}}</a></td></tr>
</tbody></table></div>
<h3>地址关联 · 先看转账，再谈控制权</h3><p>{{holders.relationshipCoverage}}</p>
<p v-if="!holders.relationships.length">本地窗口未形成可展示的关联证据。尚未自动取得完整Gas资金来源、多跳路径或外部聚类数据，不能据此认定这些地址独立。</p>
<details v-for="(r,i) in holders.relationships" :key="i"><summary>{{r.kind}} · {{r.transferCount}} 条记录</summary><p class="wallet">{{r.addresses.join(' ↔ ')}}</p><p class="wallet">来源/对手方 {{r.counterparty}}</p><p>{{time(r.firstAt)}} → {{time(r.lastAt)}} · {{r.meaning}}</p><p v-for="url in r.transactionUrls" :key="url"><a :href="url" target="_blank" rel="noopener noreferrer">核对交易 {{url}} ↗</a></p></details>
<p v-for="tool in holders.tools" :key="tool.name"><a :href="tool.url" target="_blank" rel="noopener noreferrer">{{tool.name}} ↗</a></p><p class="refresh-text">外部工具仅在点击时打开，可能需要登录或付费，流量不计入本程序请求预算。尚未订阅外部聚类API，也不把外站标签自动作为已确认归属。</p>
<details><summary>分类与关联的口径</summary><p v-for="text in holders.caveats" :key="text">{{text}}</p></details>
</template></section>
<section class="detail"><h2>供应压力、项目与事件证据</h2>
<p v-if="!research">旧报告没有本阶段的项目证据，原结论与时间保持不变。</p>
<template v-else>
<p>流通/总供应 {{percent(research.supplyCirculatingPercent)}} · FDV/流通市值 {{number(research.fdvToMarketCap)}} 倍</p>
<p>已记录未来7天解锁量 {{number(research.documentedUnlockNext7Days,0)}} · 占当前流通 {{percent(research.unlockToCirculatingPercent)}}。{{research.unlockCalendarComplete?'已覆盖日历':'日历未完整覆盖，Unknown不等于零解锁。'}}</p>
<p>{{research.eventAttribution}}</p><p v-if="!research.items.length">当前项目还没有核对原文的研究条目。</p>
<article v-for="(item,i) in research.items" :key="i" class="claim"><h3>{{item.claim.title}} · {{item.status}}</h3><p>{{item.claim.summary}}</p><p class="refresh-text">{{item.meaning}}</p><p><a :href="item.claim.sourceUrl" target="_blank" rel="noopener noreferrer">原始来源 ↗</a> · 原文核对 {{time(item.claim.checkedAt)}} · 下次复核 {{time(item.claim.reviewAfter)}}</p><p v-if="item.claim.eventAt">事件发生时间 {{time(item.claim.eventAt)}}；与核对时间分开。</p></article>
<p v-for="m in research.missing" :key="m">{{m}}</p>
</template></section>
<section class="detail"><h2>报告之后 · 24h / 72h / 7天复盘</h2><p v-if="followupError" role="status">{{followupError}}</p><p v-if="!followup">等待本地复盘结果，历史判断保持原样。</p><template v-else><p>{{followup.benchmark}}</p><div class="table-scroll"><table><thead><tr><th>观察点 / 到期</th><th>状态 / 实际价格时点</th><th>参考价 / 变化</th><th>该收盘低于原失效位</th></tr></thead><tbody><tr v-for="p in followup.points" :key="p.hours"><td>{{p.hours===168?'7天':`${p.hours}小时`}}<small>{{time(p.dueAt)}}</small></td><td>{{p.status}}<small>{{time(p.priceAt)}}</small></td><td>{{price(p.price)}} / {{percent(p.returnPercent)}}</td><td>{{p.belowInvalidation==null?'Unknown':p.belowInvalidation?'是':'否'}}<small>{{p.note}}</small></td></tr></tbody></table></div><p class="refresh-text">评估 {{time(followup.evaluatedAt)}}。每15分钟复用本地小时线留档，不增加行情下载；打开报告可读取当前本地结果。样本未到期或缺数据继续等待，不填造收益。</p></template></section>
</template>
<style scoped>.wallet{overflow-wrap:anywhere;min-width:190px;max-width:400px}.claim{padding:12px 0;border-bottom:1px solid #273540}.detail small{display:block;color:#9aaebe;font-size:11px;margin-top:6px}details{margin:12px 0}a,p{overflow-wrap:anywhere}</style>
