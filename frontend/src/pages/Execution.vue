<script setup lang="ts">
import PageIntro from '../components/PageIntro.vue'
import QuantExecution from '../components/QuantExecution.vue'
import ExecutionFoundation from '../components/ExecutionFoundation.vue'
</script>
<template><PageIntro eyebrow="CURRENT STRATEGY · EXECUTION" title="执行规则与费用核对" description="当前策略的风险额度、资金费覆盖和盘口证据；持仓按各自入场规则管理。"><RouterLink to="/">返回工作台 →</RouterLink></PageIntro><QuantExecution/><ExecutionFoundation/></template>
