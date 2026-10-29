export type Feature = { value: number | null; status: number; sourceTime: string | null; source: string }
export type CapitalFeatures = {coinGeckoId:string|null;mappingStatus:number;mappingRevision:number;trustedIdentity:boolean;mappingReason:string;
  marketCapUsd:Feature;fdvUsd:Feature;volume24hUsd:Feature;circulatingSupply:Feature;totalSupply:Feature;maxSupply:Feature;athUsd:Feature;
  oiMarketCapRatio:Feature;volumeMarketCapRatio:Feature}
export type AssetMapping = {assetId:number;coinGeckoId:string|null;status:number;candidateIds:string[];contractMultiplier:number;
  platform:string|null;contractAddress:string|null;revision:number;updatedAt:string;reason:string}
export type MappingView = {instrument:MarketView['instrument'];mapping:AssetMapping|null;capital:CapitalFeatures;catalogContainsId:boolean}
export type MarketCoin = {id:string;symbol:string;name:string;platforms:Record<string,string>}
export type DerivativeFeatures = {
  openInterestQuantity: Feature; openInterestUsd: Feature; oiChanges: Record<string, Feature>;
  oiRobustZ: Feature; oiPercentile: Feature; globalLongShare: Feature; topAccountLongShare: Feature;
  topPositionLongShare: Feature; topPositionChange15m: Feature; takerBuyShare: Feature; takerBuySellRatio: Feature;
  takerRobustZ: Feature; observedLongLiquidation15mUsd: Feature; observedShortLiquidation15mUsd: Feature;
  liquidationRobustZ: Feature; priceOiStructure: string; oiBaselineSamples: number;
  liquidationCoverageComplete: boolean; liquidationCaveat: string;
}
export type MarketView = {
  instrument: { assetId: number; symbol: string; baseAsset: string; onboardTime: string; active: boolean };
  price: number | null; volume24h: number | null; exchangeReturn24h: number | null;
  markPrice: number | null; fundingRate: number | null; priceSourceTime: string | null;
  priceReceivedTime: string | null; fundingSourceTime: string | null;
  minuteSamples: number; fiveMinuteSamples: number; fifteenMinuteSamples: number;
  features: { returns: Record<string, Feature>; priceRobustZ5m: Feature; priceRobustZ15m: Feature;
    volumeRobustZ: Feature; pricePercentile: Feature; volumePercentile: Feature;
    distanceToHigh: Record<string, Feature>; distanceToLow: Record<string, Feature>; baselineSamples: number;
    newListing: boolean; percentileBasis: string; derivatives: DerivativeFeatures; capital?:CapitalFeatures; onChain?:import("./onchain").ChainFeatures };
}
export type Health = { health: { name: string; lastSuccess: string | null; lastFailure: string | null; latencyMs: number | null;
  errorCount: number; error: string | null }; status: string }
export type Assessment = { direction: number; strategyVersion: string; score: number | null; dataConfidence: number;
  stage: string; status: number; isCandidate: boolean; risk: number; confirmedFamilies: string[]; missingFamilies: string[]; reasons: string[];
  components: { family: string; weight: number; strength: number | null; confirmed: boolean; description: string; sourceTime: string | null }[] }
export type Episode = { id: number; assetId: number; symbol: string; direction: number; strategyVersion: string;
  startTime: string; lastObservedTime: string; endTime: string | null; endReason: string | null; stage: string; state: number;
  startPrice: number; currentPrice: number; peakPrice: number; lowestPrice: number; currentScore: number; maxScore: number; dataStatus: number }
export type RadarEvent = { id: string; time: string; symbol: string; direction: number; kind: string; strategyVersion: string;
  episode: Episode; assessment: Assessment | null }
export type Radar = { scanId: string; asOf: string; observations: { input: MarketView; up: Assessment; down: Assessment; episodeId: number | null }[];
  activeEpisodes: Episode[]; market: { regime: number | null; status: number; totalAssets: number; validAssets: number; advancing: number;
    declining: number; above5: number; belowMinus5: number; above10: number; belowMinus10: number; medianReturn1h: number | null;
    btcReturn1h: number | null; ethReturn1h: number | null; coveragePercent: number; reason: string } }
export type Policy = { asOf: string; dailyUsed: number; dailyLimit: number; windowAssetsUsed: number; windowAssetLimit: number;
  budgetDate: string; toastEnabled: boolean; policyVersion: string; decisions: { key: string; symbol: string; direction: number;
    grade: number; score: number | null; confidence: number; stage: string; eligible: boolean; reason: string; episodeId: number | null }[] }
export type Notice = { id: string; createdAt: string; kind: number; title: string; status: number; error: string | null; body: string }
export type EvaluationSignal = { key: string; assetId: number; symbol: string; direction: number; time: string; price: number;
  score: number | null; strategyVersion: string; referencePrice: number | null; isNotification: boolean }
export type SignalEvaluation = { signal: EvaluationSignal; status: number; entryTime: string | null; entryPrice: number | null;
  windowEnd: string | null; observedBars: number; expectedBars: number; mfe: number | null; mae: number | null;
  hit20: boolean | null; hit50: boolean | null; drop20: boolean | null; drop30: boolean | null;
  captureLead: number | null; crashLead: number | null; reason: string }
export type Report = { id: string; asOf: string; from: string; version: string; signals: SignalEvaluation[]; signalsTruncated: boolean; methodology: string;
  precision: { date: string; direction: number; k: number; selected: number; completed: number; hits: number; percent: number | null }[];
  recall: { date: string; direction: number; expectedAssets: number; coveredAssets: number; extremeAssets: number;
    capturedAssets: number; scanCoverageComplete: boolean; percent: number | null }[] }
export type Availability = { first: string | null; last: string | null; frames: number; eventInputs: number }
export type Replay = { frames: number; observations: number; gapCount: number; note: string;
  signals: EvaluationSignal[]; simulatedNotifications: Notice[];
  trace: { time: string; assetId: number; symbol: string; price: number | null; up: Assessment; down: Assessment }[];
  coverage: { time: string; assets: number; freshPrices: number }[] }
export type Audit = { eventId: string; symbol: string; time: string; strategyVersion: string; status: string;
  savedScore: number | null; replayedScore: number | null; reason: string }
