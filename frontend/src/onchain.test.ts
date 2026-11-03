import {describe,it,expect} from 'vitest'
import {addressKey,percentage} from './onchain'
describe('on-chain display',()=>{
  it('keeps chain identity while normalizing EVM address case',()=>{expect(addressKey('bsc','0xABC')).toBe(addressKey('bsc','0xabc'));expect(addressKey('ethereum','0xabc')).not.toBe(addressKey('bsc','0xabc'))})
  it('does not display a positive tiny holder share as zero',()=>{expect(percentage(.00001)).toBe('<0.01%');expect(percentage(0)).toBe('0.00%')})
  it('keeps Solana mint and wallet addresses case sensitive',()=>{expect(addressKey('solana','DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV')).not.toBe(addressKey('solana','dvjbesdca43oqcw2h3hw1ct7n3x5vrcr3qrvtuhnxvgv'))})
})
