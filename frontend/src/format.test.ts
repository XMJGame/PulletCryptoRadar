import {describe,it,expect} from 'vitest'
import {compareNullable,flag,fresh,matchesVolume,money,percent,price} from './format'
describe('dashboard data semantics',()=>{
  it('keeps missing values distinct from zero',()=>{expect(price(null)).toBe('Unknown');expect(percent(null)).toBe('Unknown');expect(percent(0)).toBe('0.00%');expect(flag(null)).toBe('?');expect(flag(false)).toBe('否')})
  it('does not round low-priced coins to zero',()=>{expect(price(0.0000000012345)).toBe('0.0000000012345')})
  it('puts missing data last in both sort directions',()=>{expect(compareNullable(null,10,true)).toBe(1);expect(compareNullable(null,10,false)).toBe(1);expect(compareNullable(10,20,true)).toBeGreaterThan(0)})
  it('rejects future and expired source times',()=>{const now=Date.parse('2026-10-29T00:00:00Z');expect(fresh('2026-10-29T00:00:01Z',30,now)).toBe(false);expect(fresh('2026-10-28T23:59:29Z',30,now)).toBe(false);expect(fresh('2026-10-28T23:59:30Z',30,now)).toBe(true)})
  it('formats real zero and compact market values',()=>{expect(money(0)).toBe('$0');expect(money(1500000000)).toBe('$1.50B')})
  it('includes unknown volume in the unfiltered universe only',()=>{expect(matchesVolume(null,0)).toBe(true);expect(matchesVolume(null,5000000)).toBe(false);expect(matchesVolume(5000000,5000000)).toBe(true)})
})
