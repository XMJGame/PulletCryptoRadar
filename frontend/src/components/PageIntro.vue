<script setup lang="ts">defineProps<{ eyebrow: string; title: string; description: string }>()</script>
<template><div class="page-title"><div><div class="eyebrow">{{ eyebrow }}</div><h1>{{ title }}</h1><p>{{ description }}</p></div><slot /></div></template>
