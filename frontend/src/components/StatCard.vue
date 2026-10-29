<script setup lang="ts">defineProps<{ label: string; value: string | number; foot: string }>()</script>
<template><div class="stat"><span>{{ label }}</span><strong>{{ value }}</strong><div class="stat-foot">{{ foot }}</div></div></template>
