<script setup lang="ts">
import type {MarketView} from '../types'
import FeatureValue from './FeatureValue.vue'
defineProps<{market:MarketView}>()
</script>
<template><section v-if="market.features.capital" class="detail"><div class="section-heading"><h2>{{market.instrument.symbol}} · 资产规模</h2><RouterLink to="/mappings" class="text-button">核对资产映射 →</RouterLink></div><p>CoinGecko ID：{{market.features.capital.coinGeckoId??'Unknown'}} · {{market.features.capital.trustedIdentity?'身份已确认':'资产映射待确认'}}。{{market.features.capital.mappingReason}}</p><div class="detail-grid"><div><span>市值 USD</span><strong><FeatureValue :feature="market.features.capital.marketCapUsd"/></strong></div><div><span>FDV USD</span><strong><FeatureValue :feature="market.features.capital.fdvUsd"/></strong></div><div><span>OI / 市值</span><strong><FeatureValue :feature="market.features.capital.oiMarketCapRatio" :multiplier="100" suffix="%"/></strong></div><div><span>永续成交额 / 市值</span><strong><FeatureValue :feature="market.features.capital.volumeMarketCapRatio" :multiplier="100" suffix="%"/></strong></div></div><p>市值数据当前仅展示，不用于当前 V2 的评分或低风险判断。</p></section></template>
