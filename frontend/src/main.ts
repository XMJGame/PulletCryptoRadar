import {createApp} from 'vue'
import {createRouter,createWebHistory} from 'vue-router'
import App from './App.vue'
import './styles.css'

const router=createRouter({history:createWebHistory(),scrollBehavior:()=>({top:0}),routes:[
  {path:'/',redirect:'/radar'},
  ...['/radar','/direction'].map(path=>({path,component:{render:()=>null}})),
  {path:'/quick-check',redirect:'/direction'},
  {path:'/:pathMatch(.*)*',redirect:'/radar'},
]})
createApp(App).use(router).mount('#app')
