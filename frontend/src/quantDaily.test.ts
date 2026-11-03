import {describe,it,expect} from 'vitest'
import {dailyState,shanghaiDate,funnelConsistent,type DailyReport} from './quantDaily'
describe('new execution daily review',()=>{
  it('uses Shanghai dates across the UTC boundary',()=>{expect(shanghaiDate(new Date('2026-10-31T16:01:00Z'))).toBe('2026-11-01')})
  it('distinguishes missing coverage from a completed zero',()=>{expect(dailyState('MissingUniverse')).toBe('缺少冻结范围');expect(dailyState('Frozen')).toBe('冻结完成')})
  it('keeps missing assessments in the candidate conservation check',()=>{const r={funnel:{priceCandidates:4,everReady:1,awaitDataOnly:1,rejectedOnly:1,unassessed:1}} as DailyReport;expect(funnelConsistent(r)).toBe(true);r.funnel.unassessed=0;expect(funnelConsistent(r)).toBe(false)})
})
