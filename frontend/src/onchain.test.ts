import {describe,it,expect} from 'vitest'
import {addressKey,percentage} from './onchain'
describe('on-chain display',()=>{
  it('keeps chain identity while normalizing EVM address case',()=>{expect(addressKey('bsc','0xABC')).toBe(addressKey('bsc','0xabc'));expect(addressKey('ethereum','0xabc')).not.toBe(addressKey('bsc','0xabc'))})
  it('does not display a positive tiny holder share as zero',()=>{expect(percentage(.00001)).toBe('<0.01%');expect(percentage(0)).toBe('0.00%')})
})
