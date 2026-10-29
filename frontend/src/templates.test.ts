import {describe,it,expect} from 'vitest'
import {splitPaths,roleName} from './templates'
describe('research evidence charts',()=>{
 it('splits missing time windows instead of inventing an OI path',()=>{expect(splitPaths([{t:0,v:1},{t:300000,v:2},{t:86400000,v:3}],900000).map(p=>p.length)).toEqual([2,1])})
 it('sorts observations and excludes invalid values',()=>{expect(splitPaths([{t:2,v:2},{t:0,v:1},{t:3,v:NaN}],10)[0]?.map(p=>p.t)).toEqual([0,2])})
 it('keeps delisting and market controls explicitly labelled',()=>{expect(roleName('delisting-context')).toBe('退市背景');expect(roleName('market-control')).toBe('市场对照')})
})
