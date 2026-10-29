<script setup lang="ts">
import type { Feature } from '../types'
import { number, title, statusName } from '../format'
defineProps<{ feature?: Feature; multiplier?: number; suffix?: string }>()
</script>
<template><span :class="{muted: !feature || feature.status !== 0}" :title="title(feature)">{{ feature?.value == null ? 'Unknown' : number(feature.value * (multiplier ?? 1)) + (suffix ?? '') }}<small v-if="feature?.value != null && feature.status !== 0"> · {{ statusName(feature.status) }}</small></span></template>
