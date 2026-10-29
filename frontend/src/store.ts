import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from './api'
import type { Health, MarketView } from './types'
export const useMarketStore = defineStore('market', () => {
  const assets = ref<MarketView[]>([]); const health = ref<Health[]>([]); const updatedAt = ref<string | null>(null)
  async function refresh(signal?: AbortSignal) {
    const [nextAssets, nextHealth] = await Promise.all([api<MarketView[]>('/api/assets', { signal }), api<Health[]>('/api/providers/health', { signal })])
    assets.value = nextAssets; health.value = nextHealth; updatedAt.value = new Date().toISOString()
  }
  return { assets, health, updatedAt, refresh }
})
