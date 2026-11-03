import type {MarketCoin} from './types'
import type {ChainSample} from './onchain'
export type TokenCandidate={coin:MarketCoin;instruments:{assetId:number;symbol:string;baseAsset:string}[];exactMatch:boolean}
export type TokenReport={id:string;version:string;asOf:string;expiresAt:string;coin:MarketCoin;platform:string;contractAddress:string;assetId:number|null;instrument:string|null;identityVerified:boolean;identityReason:string;verdict:string;summary:string;potential:string;evidenceCoverage:number;evidenceMeaning:string;
project:{sourceTime:string;receivedTime:string;priceUsd:number|null;return24h:number|null;marketCapUsd:number|null;fdvUsd:number|null;spotVolume24hUsd:number|null;circulatingSupply:number|null;totalSupply:number|null;websites:string[]}|null;
price:number|null;perpetualVolume24h:number|null;fundingRate:number|null;oi24hPercent:number|null;oiSourceTime:string|null;
technical:{hours:number;continuous:boolean;lastClosedPrice:number|null;return24h:number|null;ema20:number|null;ema60:number|null;atr14:number|null;support:number|null;supportTime:string|null;resistance:number|null;resistanceTime:string|null;invalidation:number|null;rewardRiskToResistance:number|null;trend:string;confirmation:string};chainSample:ChainSample|null;
holderAnalysis?:HolderAnalysis|null;research?:TokenResearch|null;
supporting:string[];risks:string[];missing:string[];nextChecks:string[];sources:{name:string;status:string;sourceTime:string|null;receivedTime:string|null;url:string;note:string}[];isolation:string}
export type TokenRecent=Pick<TokenReport,'id'|'version'|'asOf'|'expiresAt'|'coin'|'verdict'|'evidenceCoverage'|'platform'|'contractAddress'>
export const reportExpired=(expiresAt:string,now=Date.now())=>!Number.isFinite(Date.parse(expiresAt))||now>=Date.parse(expiresAt)
export const preferredPlatform=(platforms:Record<string,string>)=>['binance-smart-chain','ethereum','solana','base','arbitrum-one','polygon-pos'].find(p=>!!platforms[p])??Object.keys(platforms)[0]??''
export type HolderAnalysis={chain:string;contractAddress:string;sourceTime:string|null;fresh:boolean;orderedTop10:boolean;rawTop10Percent:number|null;knownCustodyOrInfrastructurePercent:number|null;remainingObservedPercent:number|null;classifiedBalancePercent:number|null;
holders:{rank:number|null;address:string;percent:number;role:string;confidence:string;reportedTag:string;isContract:boolean|null;isLocked:boolean|null;tokenAccount:string|null;explorerUrl:string;evidenceUrl:string|null;labelCheckedAt:string|null;change24hPercentagePoints:number|null}[];
relationships:{kind:string;addresses:string[];counterparty:string;transferCount:number;firstAt:string;lastAt:string;transactionUrls:string[];meaning:string}[];tools:{name:string;url:string}[];distributionMeaning:string;relationshipCoverage:string;caveats:string[]}
export type TokenResearch={items:{claim:{category:string;title:string;summary:string;sourceUrl:string;sourceKind:string;publishedAt:string|null;checkedAt:string;reviewAfter:string;eventAt:string|null;tokenAmount:number|null};status:string;meaning:string}[];
supplyCirculatingPercent:number|null;fdvToMarketCap:number|null;documentedUnlockNext7Days:number|null;unlockToCirculatingPercent:number|null;unlockCalendarComplete:boolean;eventAttribution:string;missing:string[]}
export type TokenFollowup={reportId:string;evaluatedAt:string;benchmark:string;points:{hours:number;dueAt:string;status:string;priceAt:string|null;price:number|null;returnPercent:number|null;belowInvalidation:boolean|null;note:string}[]}
export const currentEvidenceVersion=(version:string)=>version==='Token-QuickCheck-1.1'||version==='Token-QuickCheck-1.2'
