import type {ForwardDecision} from './forward'
export type TenfoldProfile={status:string;verifiedSmallCap:boolean;coinGeckoId:string|null;marketCapUsd:number|null;referenceMarketCapUsd:number|null;marketCapSourceTime:string|null;adjustedTop10Percent:number|null;concentrationSourceTime:string|null;top10Change24hPoints:number|null;holderCount:number|null;holderChange24hPercent:number|null;past30DayPriceMultiple:number|null;targetPrice10x:number|null;impliedMarketCap10x:number|null;probability30d10x:number|null;prediction:string;supporting:string[];risks:string[];missing:string[]}
export type PoolRow={assetId:number;symbol:string;tracks:string[];price:number|null;return24h:number|null;volume24h:number|null;priceSourceTime:string|null;priceReceivedTime:string|null;hours:number;range72hPercent:number|null;hourVolumeRatio:number|null;oi24hPercent:number|null;oiSourceTime:string|null;oiReceivedTime:string|null;state:string;supporting:string[];risks:string[];missing:string[];plans:ForwardDecision[];tenfold?:TenfoldProfile|null}
export type UniverseRow={assetId:number;symbol:string;price:number|null;return24h:number|null;volume24h:number|null;priceSourceTime:string|null;priceReceivedTime:string|null;status:string}
export type PoolSnapshot={id:string;asOf:string;version:string;rulesHash:string;deepLimit:number;universe:UniverseRow[];rows:PoolRow[]}
export const tracks:Record<string,string>={Early:'30日十倍妖币研究',Momentum:'涨幅榜机会',Weakness:'走弱 / 空头观察',Explore:'轮换补齐'}
export function poolFresh(s:PoolSnapshot,r:PoolRow,now=Date.now()){
  const capture=Date.parse(s.asOf),source=Date.parse(r.priceSourceTime??''),received=Date.parse(r.priceReceivedTime??'')
  return [capture,source,received].every(Number.isFinite)&&capture<=now&&source<=capture&&received<=capture&&received>=source&&now-capture<=150000&&now-source<=90000&&now-received<=90000
}
export const poolOrder=(a:PoolRow,b:PoolRow)=>({'结构候选':0,'观察中':1,'待补数据':2}[a.state]??3)-({'结构候选':0,'观察中':1,'待补数据':2}[b.state]??3)||a.symbol.localeCompare(b.symbol)
