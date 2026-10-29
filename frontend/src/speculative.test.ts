import {describe,it,expect} from 'vitest'
import {rankSpec,specKey} from './speculative'
import type {SpecRow} from './speculative'
const row=(score:number|null,exchange='Binance')=>({assessment:{anomalyScore:score},input:{exchange,nativeSymbol:'ABCUSDT',market:{instrument:{assetId:1,symbol:'ABCUSDT'}}}} as SpecRow)
describe('speculative display',()=>{
  it('keeps unknown scores behind measured zero',()=>expect([row(null),row(0),row(50)].sort(rankSpec).map(r=>r.assessment.anomalyScore)).toEqual([50,0,null]))
  it('keeps the same asset at different venues as distinct rows',()=>expect(specKey(row(0,'Gate'))).not.toBe(specKey(row(0))))
})
