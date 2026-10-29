import type { Feature, MarketView } from './types'
export const statusName = (status: number) => ['Valid', 'Stale', 'Missing', 'Invalid'][status] ?? 'Unknown'
export const number = (value: number | null | undefined, digits = 2) => value == null || !Number.isFinite(value) ? 'Unknown' : value.toLocaleString('en-US', { maximumFractionDigits: digits, minimumFractionDigits: digits })
export const price = (value: number | null | undefined) => value == null || !Number.isFinite(value) ? 'Unknown' : value.toLocaleString('en-US', { maximumSignificantDigits: 12, useGrouping: false })
export const percent = (value: number | null | undefined) => value == null ? 'Unknown' : `${number(value)}%`
export const money = (value: number | null | undefined) => value == null ? 'Unknown' : Math.abs(value) >= 1e9 ? `$${number(value / 1e9)}B` : Math.abs(value) >= 1e6 ? `$${number(value / 1e6)}M` : `$${number(value, 0)}`
export const time = (value: string | null | undefined, utc = false) => !value ? 'Unknown' : new Date(value).toLocaleString('zh-CN', { timeZone: utc ? 'UTC' : 'Asia/Shanghai', hour12: false }) + (utc ? ' UTC' : '')
export const direction = (value: number) => value === 0 ? '上涨' : '下跌'
export const grade = (value: number) => ['Unknown', 'C', 'B', 'A', 'S'][value] ?? 'Unknown'
export const fresh = (value: string | null, seconds = 30, now = Date.now()) => !!value && Date.parse(value) <= now && now - Date.parse(value) <= seconds * 1000
export const ready = (m: MarketView) => m.features.priceRobustZ5m.status === 0 && m.features.volumeRobustZ.status === 0
export const title = (f?: Feature) => !f ? 'Missing' : `${statusName(f.status)} · ${f.source} · ${time(f.sourceTime, true)}`
export function compareNullable(a: number | string | null | undefined, b: number | string | null | undefined, descending: boolean) {
  if (a == null) return b == null ? 0 : 1
  if (b == null) return -1
  const cmp = typeof a === 'string' && typeof b === 'string' ? a.localeCompare(b) : Number(a) - Number(b)
  return descending ? -cmp : cmp
}
export const flag = (b: boolean | null) => b == null ? '?' : b ? '是' : '否'
export const matchesVolume = (value:number|null, minimum:number) => minimum===0 || value!==null&&value>=minimum
