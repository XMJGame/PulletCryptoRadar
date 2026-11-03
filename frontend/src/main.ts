import {createApp} from 'vue'
import {createPinia} from 'pinia'
import {createRouter,createWebHistory} from 'vue-router'
import App from './App.vue'
import Home from './pages/Home.vue'
import {retiredPaths} from './navigation'
import './styles.css'
const router=createRouter({history:createWebHistory(),scrollBehavior(to,from,saved){return saved??(to.path===from.path?undefined:{top:0})},routes:[
 {path:'/',component:Home,meta:{title:'量化工作台'}},
 {path:'/opportunities',alias:'/speculative',component:()=>import('./pages/Opportunities.vue'),meta:{title:'妖币埋伏'}},
 {path:'/token-check',component:()=>import('./pages/TokenCheck.vue'),meta:{title:'代币快查'}},
 {path:'/journal',component:()=>import('./pages/Journal.vue'),meta:{title:'复盘与验证'}},
 {path:'/signals',component:()=>import('./pages/Signals.vue'),meta:{title:'当前信号'}},
 {path:'/execution',component:()=>import('./pages/Execution.vue'),meta:{title:'执行与费用'}},
 {path:'/research',component:()=>import('./pages/Research.vue'),meta:{title:'辅助工具'}},
 ...retiredPaths.map(path=>({path,redirect:{path:'/journal',query:{},hash:''}})),
 {path:'/quant-validation',component:()=>import('./pages/QuantValidation.vue'),meta:{title:'固定规则验证'}},
 {path:'/market',component:()=>import('./pages/Market.vue'),meta:{title:'全市场行情'}},
 {path:'/watchtower',component:()=>import('./pages/WatchTower.vue'),meta:{title:'自选观察'}},
 {path:'/network',component:()=>import('./pages/Network.vue'),meta:{title:'连接与流量'}},
 {path:'/template-library',component:()=>import('./pages/TemplateLibrary.vue'),meta:{title:'历史行为样板库'}},
 {path:'/derivatives',component:()=>import('./pages/Derivatives.vue'),meta:{title:'衍生品数据'}},
 {path:'/alerts',component:()=>import('./pages/Alerts.vue'),meta:{title:'通知记录'}},
 {path:'/mappings',component:()=>import('./pages/Mappings.vue'),meta:{title:'资产身份'}},
 {path:'/speculative-evidence',component:()=>import('./pages/Speculative.vue'),meta:{title:'市值与链上证据'}},
 {path:'/onchain',component:()=>import('./pages/OnChain.vue'),meta:{title:'持币地址与标签'}},
 {path:'/flows',component:()=>import('./pages/Flows.vue'),meta:{title:'链上资金流'}},
 {path:'/:pathMatch(.*)*',component:{template:'<section class="detail"><h1>页面不存在</h1><a href="/">返回工作台</a></section>'},meta:{title:'页面不存在'}},
]})
router.afterEach(to=>{document.title=`PulletCryptoRadar · ${String(to.meta.title??'工作台')}`})
createApp(App).use(createPinia()).use(router).mount('#app')
