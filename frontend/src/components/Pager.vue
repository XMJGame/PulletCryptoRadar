<script setup lang="ts">
import { computed, watch } from 'vue'
const props = defineProps<{ total: number; size?: number }>()
const page = defineModel<number>({ required: true })
const pages = computed(() => Math.max(1, Math.ceil(props.total / (props.size ?? 30))))
watch(pages, n => { if (page.value >= n) page.value = n - 1 })
</script>
<template><div class="table-footer"><span><slot /></span><div><button aria-label="上一页" :disabled="page === 0" @click="page--">←</button><span>{{ page + 1 }} / {{ pages }}</span><button aria-label="下一页" :disabled="page + 1 >= pages" @click="page++">→</button></div></div></template>
