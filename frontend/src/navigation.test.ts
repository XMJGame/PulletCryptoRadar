import {describe,it,expect} from 'vitest'
import {primaryNavigation,auxiliaryNavigation,navigationSection} from './navigation'
describe('product navigation',()=>{
  it('has the four agreed daily entrances',()=>expect(primaryNavigation.map(n=>n.path)).toEqual(['/','/opportunities','/token-check','/journal']))
  it('keeps only current supporting tools in the auxiliary navigation',()=>expect(auxiliaryNavigation.map(n=>n.path)).toEqual(['/research']))
  it('keeps current signal, evidence and validation details with their daily entrance',()=>{expect(navigationSection('/signals')).toBe('/');expect(navigationSection('/execution')).toBe('/');expect(navigationSection('/quant-validation')).toBe('/journal');expect(navigationSection('/speculative')).toBe('/opportunities');expect(navigationSection('/watchtower')).toBe('/research')})
})
