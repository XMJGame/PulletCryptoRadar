import {describe,it,expect} from 'vitest'
import {liquidation,observedDrawdown} from './paper'
import type {PaperPosition,PaperRules} from './paper'
describe('paper account presentation',()=>{
 it('uses remaining isolated margin for estimated liquidation',()=>{const p={quantity:10,entryPrice:100,remainingMargin:100} as PaperPosition;const r={maintenanceRate:.005} as PaperRules;expect(liquidation(p,r)).toBeCloseTo(90.45226,4);expect(liquidation({...p,remainingMargin:90},r)).toBeGreaterThan(liquidation(p,r))})
 it('labels observed drawdown using complete observations only',()=>expect(observedDrawdown([{time:'1',equity:1000,complete:true},{time:'2',equity:100,complete:false},{time:'3',equity:900,complete:true}])).toBe(10))
})
