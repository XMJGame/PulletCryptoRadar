import type {Feature} from './types'
export type WatchWallet={chain:string;address:string;role:string}
export type WatchConfig={assetId:number;enabled:boolean;costPrice:number|null;targetPrice:number|null;attentionPrice:number|null;supportPrice:number|null;resistancePrice:number|null;enableAlerts:boolean;wallets:WatchWallet[];revision:number;createdAt:string;updatedAt:string;isSpecificationExample:boolean}
export type WatchResult={version:string;asOf:string;assetId:number;symbol:string;exchange:string;nativeSymbol:string;status:number;price:Feature;costReturnPercent:number|null;toCostPercent:number|null;trend:string;currentStage:string;state:number;risk:number;confidence:number;trendScore:number|null;derivativeScore:number|null;onChainScore:number|null;walletRiskScore:number|null;crowdingRisk:number|null;positiveReasons:string[];riskReasons:string[];missingEvidence:string[];nextTriggers:string[]}
export type WatchView={config:WatchConfig;result:WatchResult|null}
export type WatchPoint={input:{config:WatchConfig};result:WatchResult;activeCondition:string|null}
export const stateName=(state:number)=>['Observe','PositiveObserve','HoldObserve','WaitConfirmation','AvoidChase','RiskIncreasing','ReduceRisk','CriticalRisk'][state]??'Unknown'
export const riskName=(risk:number)=>['Unknown','Low','Medium','High','Critical'][risk]??'Unknown'
export function walletLines(text:string):WatchWallet[]{return text.split(/\r?\n/).filter(x=>x.trim()).map(line=>{const parts=line.split('|').map(x=>x.trim());if(parts.length!==3)throw new Error('钱包每行填写：链 | 地址 | project/team/investor');return {chain:parts[0]!,address:parts[1]!,role:parts[2]!}})}
export function priceInput(value:string):string|null{const text=value.trim();if(!text)return null;if(!/^(?:\d+(?:\.\d*)?|\.\d+)$/.test(text)||!Number.isFinite(Number(text))||Number(text)<=0)throw new Error('价格须为大于 0 的十进制数，未配置请留空');return text}
