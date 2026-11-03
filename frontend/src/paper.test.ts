import {describe,it,expect} from 'vitest'
import {liquidation,observedDrawdown,positionGross} from './paper'
import type {PaperPosition,PaperRules} from './paper'
describe('paper account presentation',()=>{
 it('uses remaining isolated margin for estimated liquidation',()=>{const p={quantity:10,entryPrice:100,remainingMargin:100} as PaperPosition;const r={maintenanceRate:.005} as PaperRules;expect(liquidation(p,r)).toBeCloseTo(90.45226,4);expect(liquidation({...p,remainingMargin:90},r)).toBeGreaterThan(liquidation(p,r))})
 it('labels observed drawdown using complete observations only',()=>expect(observedDrawdown([{time:'1',equity:1000,complete:true},{time:'2',equity:100,complete:false},{time:'3',equity:900,complete:true}])).toBe(10))
 it('uses the position version maintenance rate for both directions',()=>{const p={quantity:10,entryPrice:100,remainingMargin:100,quant:{parameters:{assumedMaintenanceFraction:.01}}} as PaperPosition;const r={maintenanceRate:.005} as PaperRules;expect(liquidation(p,r)).toBeCloseTo(90/.99);expect(liquidation({...p,direction:'Short'},r)).toBeCloseTo(110/1.01)})
 it('shows fresh directional gross pnl but leaves stale and future prices unknown',()=>{const at='2026-11-01T01:00:00Z';const p={quantity:2,entryPrice:100,lastMark:90,lastMarkSourceTime:at,lastQuoteTime:at,direction:'Long'} as PaperPosition;expect(positionGross(p,at)).toBe(-20);expect(positionGross({...p,direction:'Short'},at)).toBe(20);expect(positionGross(p,'2026-11-01T01:01:00Z')).toBe(null);expect(positionGross(p,'2026-11-01T00:59:59Z')).toBe(null)})
})

import {paperDirection,canOperate} from './paper'
describe('experimental direction and permission',()=>{
  it('keeps executable experimental mode distinct from research validation',()=>{expect(canOperate({experimentalSimulation:true,researchValidated:false})).toBe(true);expect(canOperate({experimentalSimulation:false,researchValidated:false})).toBe(false)})
  it('computes short liquidation above entry',()=>{const p={direction:'Short',quantity:10,entryPrice:100,remainingMargin:100} as PaperPosition;expect(liquidation(p,{maintenanceRate:.005} as PaperRules)).toBeGreaterThan(100);expect(paperDirection('Short')).toBe('做空')})
})
