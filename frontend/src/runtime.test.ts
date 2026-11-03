import {describe,it,expect} from 'vitest'
import {shanghaiDate,supervisorLabel} from './runtime'
import type {RuntimeReliability} from './runtime'
describe('runtime status and research day',()=>{
  it('never labels a cached connected supervisor as live during HTTP failure',()=>{
    expect(supervisorLabel({supervisorFresh:true} as RuntimeReliability,true)).toContain('连接中断')
    expect(supervisorLabel({supervisorFresh:false} as RuntimeReliability,false)).toContain('未确认')
    expect(supervisorLabel(null,false)).toContain('读取')
  })
  it('uses Shanghai calendar day across UTC midnight boundaries',()=>{
    expect(shanghaiDate(new Date('2026-10-30T16:01:00Z'))).toBe('2026-10-31')
    expect(shanghaiDate(new Date('2026-10-30T15:59:00Z'))).toBe('2026-10-30')
  })
})
