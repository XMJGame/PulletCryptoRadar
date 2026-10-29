import { onMounted, onUnmounted, ref } from 'vue'
export function usePolling(work: (signal: AbortSignal) => Promise<void>, milliseconds: number) {
  const error = ref(''); const updatedAt = ref<string | null>(null); const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined; let running = false; let stopped = false
  async function refresh() {
    if (running || stopped) return
    running = true
    try { await work(controller.signal); updatedAt.value = new Date().toISOString(); error.value = '' }
    catch (e) { if (!controller.signal.aborted) error.value = e instanceof Error ? e.message : '数据暂时不可用' }
    finally { running = false }
  }
  async function loop() { await refresh(); if (!stopped) timer = setTimeout(loop, milliseconds) }
  onMounted(loop)
  onUnmounted(() => { stopped = true; controller.abort(); clearTimeout(timer) })
  return { error, updatedAt, refresh }
}
