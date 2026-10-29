import {describe,it,expect} from 'vitest'
import {tokenAmount,txLink,addressLink} from './flows'
describe('transfer evidence',()=>{
 it('keeps large uint256 values exact',()=>expect(tokenAmount('284166666200000000000000000',18)).toBe('284166666.2'))
 it('retains tiny fractions and raw values without known decimals',()=>{expect(tokenAmount('1',18)).toBe('0.000000000000000001');expect(tokenAmount('123',null)).toBe('123 最小单位');expect(tokenAmount('-1',18)).toBe('Unknown')})
 it('builds explorer links only for complete identifiers',()=>{expect(txLink('0x'+'a'.repeat(64))).toContain('bscscan.com/tx/');expect(txLink('javascript:alert(1)')).toBeNull();expect(addressLink('0x'+'b'.repeat(40))).toContain('bscscan.com/address/')})
})
