import {describe,it,expect} from 'vitest'
import {poolFresh} from './opportunities'
import type {PoolSnapshot,PoolRow} from './opportunities'
describe('opportunity freshness',()=>{
  const now=Date.parse('2026-10-31T00:00:00Z')
  const snapshot={asOf:new Date(now).toISOString()} as PoolSnapshot
  const row={priceSourceTime:new Date(now-1000).toISOString(),priceReceivedTime:new Date(now).toISOString()} as PoolRow
  it('accepts a current ordered observation',()=>expect(poolFresh(snapshot,row,now)).toBe(true))
  it('does not treat a restored old snapshot as current',()=>expect(poolFresh({...snapshot,asOf:new Date(now-180000).toISOString()},row,now)).toBe(false))
  it('requires source time as well as a recent snapshot',()=>expect(poolFresh(snapshot,{...row,priceSourceTime:new Date(now-91000).toISOString()},now)).toBe(false))
  it('rejects future observations and missing inputs',()=>{
    expect(poolFresh(snapshot,{...row,priceReceivedTime:new Date(now+1000).toISOString()},now)).toBe(false)
    expect(poolFresh(snapshot,{...row,priceSourceTime:null},now)).toBe(false)
  })
})
