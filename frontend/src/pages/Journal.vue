<script setup lang="ts">
import {ref} from 'vue'
import {api} from '../api'
import {usePolling} from '../usePolling'
import {number,time} from '../format'
import type {PaperOverview} from '../paper'
import PageIntro from '../components/PageIntro.vue'
import ForwardReview from '../components/ForwardReview.vue'
const data=ref<PaperOverview|null>(null);const {error}=usePolling(async signal=>{data.value=await api('/api/paper',{signal})},15000)
</script><template><PageIntro eyebrow="DAILY REVIEW · SHANGHAI TIME" title="每天复盘，按版本验证。" description="先看扣除成本后的结果，再看入场和退出哪里失效。小样本不急着调参。"/><p v-if="error" class="radar-notice">{{error}}</p><ForwardReview compact/><section class="detail"><h2>APRUSDT · 2026年10月29日案例</h2><p>你提供的记录：16:59 买入，价格 0.0566。系统保存的16:55:39上涨观察为 Breakout，参考价0.05654；17:03:39方向反转，参考价0.05279。按你的买价至这份后续观测，价格变化约 {{number((.05279/.0566-1)*100,2)}}%。这不是你的实际平仓结果，未假设你何时卖出。</p><p>它说明异动研究只描述已发生的价格和成交变化，不等同于入场计划。16:55的观察也不能证明16:59仍应买入。新模拟盘会独立检查入场门槛、等待下一报价，并记录止损及反向退出。</p><p>这次案例不会倒填进模拟账户，也不会被用来修改旧模型后声称“本来可以获利”。实际入场页面仍未确认；缺少成交数量、费用及退出记录，不计算你的实际盈亏。</p><RouterLink to="/volatility">查看原始异动研究 →</RouterLink></section>
<section class="detail"><h2>每日已平仓结果 · {{data?.state.rules.version}}</h2><p>账户开始 {{time(data?.state.startedAt)}}。按上海日期分组，未平仓盈亏在工作台另列；这里不是完整的每日净值收益。</p><div class="table-scroll"><table><thead><tr><th>日期</th><th>已平仓 / 获利笔数</th><th>净盈亏 USDT</th><th>手续费 / 资金费假设</th><th>复盘结论</th></tr></thead><tbody><tr v-for="d in data?.days" :key="d.date"><td>{{d.date}}</td><td>{{d.trades}} / {{d.wins}}</td><td>{{number(d.netPnl,3)}}</td><td>{{number(d.fees,3)}} / {{number(d.fundingCost,3)}}</td><td class="decision-reason">{{d.review}}</td></tr></tbody></table></div><h3>每天只回答三件事</h3><p>入场时依据是否齐全？退出是否按计划执行？扣除费用后，亏损来自信号失效、追高、行情间隔还是成本？</p><p>先积累至少30笔已平仓样本，继续查看回撤和数据完整度；这是最低复盘门槛，不代表策略已验证。任何参数调整另建版本，前瞻比较，不覆写历史。</p></section></template>
