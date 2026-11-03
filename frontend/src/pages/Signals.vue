<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import PageIntro from '../components/PageIntro.vue'
import QuantShadow from '../components/QuantShadow.vue'
const control=ref<{version:string}|null>(null)
const {error}=usePolling(async signal=>{control.value=(await api<{control:{version:string}|null}>('/api/quant/execution',{signal})).control},15000)
</script>
<template><PageIntro eyebrow="CURRENT STRATEGY · SIGNALS" title="当前信号与开仓条件" description="观察当前价格结构、市场环境和风险检查；实际成交以账户账本为准。"><RouterLink to="/">返回工作台 →</RouterLink></PageIntro><p v-if="error" role="alert" class="radar-notice">{{error}}</p><QuantShadow :execution-version="control?.version"/></template>
