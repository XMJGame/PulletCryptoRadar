import {describe,it,expect} from 'vitest'
import {priceInput,walletLines} from './watch'
describe('watch input',()=>{
  it('preserves decimal strings and optional empty values',()=>{expect(priceInput('0.400000000000000001')).toBe('0.400000000000000001');expect(priceInput('')).toBeNull()})
  it('rejects zero, negatives and invalid price text',()=>{for(const value of ['0','-1','abc','Infinity'])expect(()=>priceInput(value)).toThrow()})
  it('parses labelled wallet lines without treating notes as addresses',()=>{expect(walletLines('bsc | 0x123 | project')[0]).toEqual({chain:'bsc',address:'0x123',role:'project'});expect(()=>walletLines('some note')).toThrow()})
})
