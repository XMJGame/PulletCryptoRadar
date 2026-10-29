export async function api<T>(url: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(url, { ...options, headers: { Accept: 'application/json', ...options.headers } })
  if (!response.ok) {
    const text = await response.text()
    let detail = `请求失败（HTTP ${response.status}）`
    try { const error = JSON.parse(text); detail = error.error ?? error.detail ?? detail } catch { /* HTML failures are never rendered as trusted markup. */ }
    if (response.status === 503) detail = '后台正在初始化，请稍后重试。'
    throw new Error(detail)
  }
  return response.json() as Promise<T>
}
export const post = <T>(url: string, body?: unknown) => api<T>(url, {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}),
})
