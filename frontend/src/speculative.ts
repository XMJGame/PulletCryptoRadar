import type {Assessment,Feature,MarketView} from './types'
export type SpecInput={market:MarketView;exchange:string;nativeSymbol:string;venueIdentityVerified:boolean;price:Feature;volume24hUsd:Feature;
  return1h:Feature;return24h:Feature;fundingRate8h:Feature;openInterestUsd:Feature;oiChange15m:Feature;oiRobustZ:Feature;
  longShare:Feature;topPositionChange15m:Feature;priceRobustZ:Feature;volumeRobustZ:Feature;sourceNote:string}
export type SpecAssessment={strategyVersion:string;anomalyScore:number|null;riskScore:number|null;anomalyCoverage:number;riskCoverage:number;
  stage:string;isCandidate:boolean;status:number;anomalyComponents:Assessment['components'];riskComponents:Assessment['components'];missingFamilies:string[];reason:string}
export type SpecRow={input:SpecInput;assessment:SpecAssessment;isSelectedSource:boolean;binanceUp:Assessment|null;binanceDown:Assessment|null}
export type SpecSnapshot={id:string;asOf:string;strategyVersion:string;parametersJson:string;rows:SpecRow[]}
export type Binding={assetId:number;exchange:string;symbol:string|null;revision:number;updatedAt:string}
export type SpecAudit={id:string;asOf:string;status:string;rows:number;mismatches:number;reason:string}
export const specKey=(r:SpecRow)=>`${r.input.market.instrument.assetId}:${r.input.exchange}:${r.input.nativeSymbol}`
export function rankSpec(a:SpecRow,b:SpecRow){return (b.assessment.anomalyScore??-1)-(a.assessment.anomalyScore??-1)||a.input.market.instrument.symbol.localeCompare(b.input.market.instrument.symbol)||a.input.exchange.localeCompare(b.input.exchange)}
