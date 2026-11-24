<script setup lang="ts">
import {ref} from 'vue'
import {api, number, time} from './api'

type Direction = 'Long' | 'Short'
type RecentQuery = {symbol:string; direction:Direction}
type Candidate = {symbol:string; name:string; binanceSymbol:string; exactMatch:boolean}
type Report = {
  symbol:string; direction:Direction; verdict:string; checkedAt:string; priceAt:string|null; lastClosedHour:string|null
  price:number|null; return24h:number|null; volumeRatio:number|null; openInterestChange1h:number|null
  fundingPercent:number|null; spreadPercent:number|null; support:number|null; resistance:number|null
  invalidation:number|null; reasons:string[]; retryAt:string|null
  history:{closedHours:number; from:string|null; through:string|null; low7d:number|null; high7d:number|null;
    return7d:number|null; trend4h:string|null; lastClosed4h:string|null}
}

const query = ref('')
const direction = ref<Direction>('Long')
const candidates = ref<Candidate[]>([])
const report = ref<Report>()
const busy = ref(false)
const error = ref('')
const pair = /^[\p{L}\p{N}]{1,40}USDT$/u
const recentKey = 'pullet.direction-recent.v1'
const recentLimit = 8
const recent = ref<RecentQuery[]>(readRecent())
const directionName = (value:Direction) => value === 'Long' ? '看涨' : '看跌'
const formatted = (value:number|null, digits:number, unit='') => value === null ? '—' : `${number(value,digits)}${unit}`
const priceText = (value:number|null) => value === null ? '—' : value.toLocaleString('zh-CN',{maximumFractionDigits:10})

function readRecent():RecentQuery[] {
  try {
    const saved:unknown = JSON.parse(localStorage.getItem(recentKey) ?? '[]')
    return Array.isArray(saved) ? saved.filter((item):item is RecentQuery =>
      item && typeof item.symbol === 'string' && pair.test(item.symbol) &&
      (item.direction === 'Long' || item.direction === 'Short')).slice(0,recentLimit) : []
  } catch {
    return []
  }
}

function sameQuery(a:RecentQuery,b:RecentQuery) {
  return a.symbol === b.symbol && a.direction === b.direction
}

function saveRecent(items:RecentQuery[]) {
  recent.value = items.slice(0,recentLimit)
  try { localStorage.setItem(recentKey,JSON.stringify(recent.value)) } catch { /* Keep the in-page list available. */ }
}

async function requestReport(symbol:string) {
  const path = `/api/direction/${encodeURIComponent(symbol)}?direction=${direction.value}`
  candidates.value = []
  const result = await api<Report>(path,{method:'POST'})
  report.value = result
  if (result.price !== null || result.history.closedHours > 0) {
    const entry = {symbol:result.symbol,direction:result.direction}
    saveRecent([entry,...recent.value.filter(item=>!sameQuery(item,entry))])
  }
}

function openRecent(item:RecentQuery) {
  if (busy.value) return
  query.value = item.symbol
  direction.value = item.direction
  void submit(item.symbol)
}

function removeRecent(item:RecentQuery) {
  saveRecent(recent.value.filter(entry=>!sameQuery(entry,item)))
}

async function submit(symbol?:string) {
  if (busy.value || !query.value.trim()) return
  busy.value = true
  error.value = ''
  report.value = undefined
  try {
    const input = query.value.trim()
    const pairInput = input.toUpperCase().replace(/[-/_ ]USDT$/u,'USDT')
    if (symbol || pair.test(pairInput)) {
      await requestReport(symbol ?? pairInput)
      return
    }
    const result = await api<{candidates:Candidate[]}>(`/api/direction/search?query=${encodeURIComponent(input)}`)
    candidates.value = result.candidates
    const [first] = candidates.value
    if (candidates.value.length === 1 && first?.exactMatch) await requestReport(first.binanceSymbol)
    else if (!first) error.value = '目录里没找到这个代币。请从榜单复制完整的 Binance USDT 永续交易对，例如 BTCUSDT。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '分析失败，请稍后重试。'
  } finally {
    busy.value = false
  }
}

function clearResult() {
  candidates.value = []
  report.value = undefined
  error.value = ''
}
</script>

<template>
  <section class="panel search-panel direction-form">
    <form @submit.prevent="submit()">
      <input v-model="query" :disabled="busy" aria-label="代币名称或交易对" placeholder="代币名称、代码或交易对，例如 BTCUSDT" maxlength="128" @input="clearResult">
      <select v-model="direction" :disabled="busy" aria-label="分析方向" @change="clearResult">
        <option value="Long">看涨</option>
        <option value="Short">看跌</option>
      </select>
      <button class="primary" :disabled="busy || !query.trim()">{{ busy ? '正在分析…' : '分析' }}</button>
    </form>
    <p v-if="busy" class="direction-loading" role="status">正在拉取行情。短时请求配额满时，会等空位后自动补拉。</p>
    <div v-if="recent.length" class="recent direction-recent" aria-label="最近查询">
      <span>最近查询</span>
      <div v-for="item in recent" :key="`${item.symbol}-${item.direction}`" class="recent-chip">
        <button type="button" :disabled="busy" @click="openRecent(item)">{{ item.symbol }} · {{ directionName(item.direction) }}</button>
        <button type="button" :disabled="busy" :aria-label="`删除 ${item.symbol} ${directionName(item.direction)} 的查询记录`" @click="removeRecent(item)">×</button>
      </div>
    </div>
  </section>

  <div v-if="error" class="notice danger" role="alert">{{ error }}</div>
  <section v-if="candidates.length" class="panel">
    <div class="panel-heading"><h2>确认交易对</h2></div>
    <article v-for="candidate in candidates" :key="candidate.binanceSymbol" class="list-row">
      <div><strong>{{ candidate.binanceSymbol }}</strong><small>{{ candidate.name }}</small></div>
      <button class="primary" :disabled="busy" @click="submit(candidate.binanceSymbol)">分析</button>
    </article>
  </section>

  <section v-if="report" class="panel" aria-live="polite">
    <div class="panel-heading">
      <h2>{{ report.symbol }} · {{ directionName(report.direction) }}</h2>
      <span class="muted">Binance USDT 永续 · {{ time(report.checkedAt) }}</span>
    </div>
    <div class="report-summary" :class="{'report-incomplete':report.verdict === '数据不足'}">
      <h3>{{ report.verdict }}</h3>
      <p v-for="reason in report.reasons" :key="reason">{{ reason }}</p>
      <p v-if="report.invalidation !== null && report.verdict === '数据不足'"><strong>已观察到的突破位</strong> · {{ priceText(report.invalidation) }}</p>
      <p v-else-if="report.invalidation !== null"><strong>失效条件</strong> · 小时线{{ report.direction === 'Long' ? '跌回' : '收复' }} {{ priceText(report.invalidation) }}</p>
      <p v-if="report.retryAt">建议在 {{ time(report.retryAt) }} 后重新分析。<button type="button" class="icon-button" @click="submit(report.symbol)">重新分析</button></p>
    </div>
    <div class="rules direction-evidence">
      <p><span>最新成交价</span><strong>{{ priceText(report.price) }}</strong></p>
      <p><span>近 24 小时收盘涨跌</span><strong>{{ formatted(report.return24h,2,'%') }}</strong></p>
      <p><span>突破小时成交额倍数</span><strong>{{ formatted(report.volumeRatio,2,' 倍') }}</strong></p>
      <p><span>近 1 小时未平仓量变化</span><strong>{{ formatted(report.openInterestChange1h,2,'%') }}</strong></p>
      <p><span>单期资金费率</span><strong>{{ formatted(report.fundingPercent,4,'%') }}</strong></p>
      <p><span>买卖价差</span><strong>{{ formatted(report.spreadPercent,4,'%') }}</strong></p>
      <p><span>前 12 小时支撑 / 压力</span><strong>{{ priceText(report.support) }} / {{ priceText(report.resistance) }}</strong></p>
    </div>
    <p class="panel-note">成交价 {{ time(report.priceAt) }} · 最近收盘小时线 {{ time(report.lastClosedHour) }}</p>
    <div class="panel-heading">
      <h2>历史 K 线</h2>
      <span class="muted">{{ report.history.closedHours }} 根连续已收盘小时线</span>
    </div>
    <div v-if="report.history.closedHours" class="rules direction-evidence">
      <p><span>历史覆盖</span><strong>{{ time(report.history.from) }} – {{ time(report.history.through) }}</strong></p>
      <p><span>此前 7 天低点 / 高点</span><strong>{{ priceText(report.history.low7d) }} / {{ priceText(report.history.high7d) }}</strong></p>
      <p><span>近 7 天收盘涨跌</span><strong>{{ formatted(report.history.return7d,2,'%') }}</strong></p>
      <p><span>4 小时趋势</span><strong>{{ report.history.trend4h ?? '历史不足' }}</strong></p>
      <p v-if="report.history.lastClosed4h"><span>最近收盘 4 小时线</span><strong>{{ time(report.history.lastClosed4h) }}</strong></p>
    </div>
    <p v-else class="panel-note">本次没有可用的已收盘 K 线。</p>
  </section>
</template>

<style scoped>
.direction-form { padding-bottom: 20px; }
.direction-form input { min-width: 0; }
.direction-loading { color: var(--muted); margin: 12px 0 0; font-size: 11px; }
.direction-recent { margin-bottom: -20px; }
.direction-evidence { padding-top: 12px; }
.report-incomplete { background: #292219; }
.report-incomplete h3 { color: #ebc596; }
.report-incomplete p { color: #bcb0a0; }
</style>
