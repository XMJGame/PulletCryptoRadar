import {describe,it,expect} from 'vitest'
import {decisionLabel,crossingLabel} from './forward'
describe('forward observation labels',()=>{
  it('keeps unknown separate from negative observations',()=>{expect(crossingLabel(null)).toBe('Unknown');expect(crossingLabel(false)).toContain('不排除盘中');expect(crossingLabel(true)).toBe('采样曾越线')})
  it('does not call a candidate a filled trade',()=>{expect(decisionLabel('Candidate')).toBe('观察计划');expect(decisionLabel('Insufficient')).toBe('数据不足')})
})
