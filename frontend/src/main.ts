import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import Market from './pages/Market.vue'
import Home from './pages/Home.vue'
import Journal from './pages/Journal.vue'
import Research from './pages/Research.vue'
import StrategyResearch from './pages/StrategyResearch.vue'
import TemplateLibrary from './pages/TemplateLibrary.vue'
import StrategyReplay from './pages/StrategyReplay.vue'
import Network from './pages/Network.vue'
import Forward from './pages/Forward.vue'
import Volatility from './pages/Volatility.vue'
import Derivatives from './pages/Derivatives.vue'
import Alerts from './pages/Alerts.vue'
import Evaluation from './pages/Evaluation.vue'
import Mappings from './pages/Mappings.vue'
import Speculative from './pages/Speculative.vue'
import WatchTower from './pages/WatchTower.vue'
import OnChain from './pages/OnChain.vue'
import Flows from './pages/Flows.vue'
import './styles.css'

const router = createRouter({ history: createWebHistory(), routes: [
  { path: '/', component: Home, meta: { title: '今日工作台' } },
  { path: '/market', component: Market, meta: { title: '全市场行情' } },
  { path: '/journal', component: Journal, meta: { title: '每日复盘' } },
  { path: '/research', component: Research, meta: { title: '研究工具' } },
  { path: '/strategy-replay', component: StrategyReplay, meta: { title: '结构策略回放' } },
  { path: '/forward', component: Forward, meta: { title: '连续前瞻观察' } },
  { path: '/network', component: Network, meta: { title: '流量与连接' } },
  { path: '/template-library', component: TemplateLibrary, meta: { title: '历史行为样板库' } },
  { path: '/strategy-research', component: StrategyResearch, meta: { title: '两套策略研究' } },
  { path: '/volatility', component: Volatility, meta: { title: '异动雷达' } },
  { path: '/alerts', component: Alerts, meta: { title: '通知中心' } },
  { path: '/derivatives', component: Derivatives, meta: { title: '衍生品数据' } },
  { path: '/evaluation', component: Evaluation, meta: { title: '回放与评估' } },
  { path: '/mappings', component: Mappings, meta: { title: '资产映射' } },
  { path: '/speculative', component: Speculative, meta: { title: '妖币雷达' } },
  { path: '/watchtower', component: WatchTower, meta: { title: '自选监控' } },
  { path: '/flows', component: Flows, meta: { title: '链上资金流' } },
  { path: '/onchain', component: OnChain, meta: { title: '链上分布' } },
  { path: '/:pathMatch(.*)*', component: { template: '<section class="detail"><h1>页面不存在</h1><a href="/">返回行情台</a></section>' }, meta: { title: '页面不存在' } },
] })
router.afterEach(to => { document.title = `PulletCryptoRadar · ${String(to.meta.title ?? '工作台')}` })
createApp(App).use(createPinia()).use(router).mount('#app')
