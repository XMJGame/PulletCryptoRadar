import {describe,it,expect} from 'vitest'
import {preferredPlatform,reportExpired} from './tokenCheck'
describe('quick check evidence time and chain choice',()=>{
  it('expires historical reports without refreshing their evidence time',()=>{const end='2026-10-31T01:32:00Z';expect(reportExpired(end,Date.parse(end))).toBe(true);expect(reportExpired(end,Date.parse(end)-1)).toBe(false);expect(reportExpired('invalid')).toBe(true)})
  it('keeps original Solana mint case when selecting a supported chain',()=>{const p={solana:'DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV'};expect(preferredPlatform(p)).toBe('solana');expect(p.solana.startsWith('Dvjb')).toBe(true)})
  it('handles a native coin without manufacturing an ERC20 contract',()=>{expect(preferredPlatform({})).toBe('')})
})
