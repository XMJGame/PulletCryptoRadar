<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {time} from '../format'
import {supervisorLabel} from '../runtime'
import type {RuntimeReliability} from '../runtime'
const status=ref<RuntimeReliability|null>(null)
const network=ref<{paused:boolean}|null>(null)
const {error}=usePolling(async signal=>{status.value=await api('/api/runtime/reliability',{signal:AbortSignal.any([signal,AbortSignal.timeout(5000)])})},10000)
const {error:networkError}=usePolling(async signal=>{network.value=await api('/api/network',{signal:AbortSignal.any([signal,AbortSignal.timeout(5000)])})},30000)
</script>
<template><section class="runtime-panel" :class="{warning:error||status&&!status.supervisorFresh}" role="status"><div class="runtime-line"><strong>{{supervisorLabel(status,!!error)}}</strong><span v-if="!error&&status">{{status.lastHeartbeatAt?'最近心跳 '+time(status.lastHeartbeatAt):'本次启动 '+time(status.startedAt)}}</span><RouterLink to="/network">连接与流量 →</RouterLink><details v-if="status"><summary>运行详情</summary><div class="runtime-details"><p>{{status.supervisor?.message}}</p><p>本次启动 {{time(status.startedAt)}}。进程健康不代表每一项行情、链上资料均新鲜。</p><p v-for="(gap,i) in status.gaps.filter(g=>!g.cleanExit).slice(0,3)" :key="i">历史未完整退出：{{time(gap.lastEvidenceAt)}} → {{time(gap.resumedAt)}}；{{gap.meaning}}</p></div></details></div><p v-if="error">服务器暂不可访问；已有数据保留原时间。检查服务器窗口，缓存不代表正在采集。</p><p v-else-if="status&&!status.supervisorFresh">{{status.supervisor?.message}}；请检查服务器窗口。</p><p v-if="network?.paused" role="alert">外部采集已暂停：请查看连接与流量。报价可能失鲜，持仓无法保证及时退出。</p><p v-else-if="networkError" role="alert">联网预算状态暂不可读取；请查看连接与流量。</p></section></template>
<style scoped>.runtime-panel{margin:0 0 24px;padding:11px 15px;border:1px solid #29443b;border-radius:7px;color:#8fa99f;font-size:11px;line-height:1.7}.runtime-line{display:flex;align-items:baseline;gap:14px;flex-wrap:wrap}.runtime-line strong{color:#92d7bc}.runtime-line>a{margin-left:auto}.runtime-panel.warning{border-color:#846843;color:#d0b284}.runtime-panel.warning strong{color:#e4bd85}.runtime-panel p{margin:8px 0 0}.runtime-panel summary{cursor:pointer}.runtime-details{padding:8px 0;max-width:850px}.runtime-line details[open]{flex-basis:100%}@media(max-width:650px){.runtime-line>span{display:none}}</style>
