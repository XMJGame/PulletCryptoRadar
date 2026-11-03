import {describe,it,expect} from 'vitest'
import {parseRecentTokenQueries,rememberTokenQuery} from './recentTokenQueries'

describe('recent token searches',()=>{
  it('keeps five unique searches with the latest first',()=>{
    let queries:string[]=[]
    for(const q of ['AIO','ZBT','MYX','TRB','LAB','BTW',' zbt '])queries=rememberTokenQuery(queries,q)
    expect(queries).toEqual(['zbt','BTW','LAB','TRB','MYX'])
  })
  it('retains the complete case sensitive Solana Mint',()=>{
    const mint='DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV',other='dvjb'+mint.slice(4)
    expect(rememberTokenQuery([mint],other)).toEqual([other,mint])
    expect(parseRecentTokenQueries(JSON.stringify({version:1,queries:[mint]}))).toEqual([mint])
  })
  it('restores ordering after persistence and distinguishes clearing from first use',()=>{
    const queries=['ZBT','OlaXBT']
    expect(parseRecentTokenQueries(JSON.stringify({version:1,queries}))).toEqual(queries)
    expect(parseRecentTokenQueries('{"version":1,"queries":[]}')).toEqual([])
    expect(parseRecentTokenQueries(null)).toBeNull()
  })
  it('rejects damaged or oversized storage and drops invalid entries',()=>{
    for(const raw of ['broken','null','[]','{"version":2,"queries":[]}', ' '.repeat(4097)])expect(parseRecentTokenQueries(raw)).toBeNull()
    expect(parseRecentTokenQueries(JSON.stringify({version:1,queries:[null,4,'A','B\nTC','AIO',{},' aio ','ZBT','x'.repeat(101)]}))).toEqual(['AIO','ZBT'])
  })
})
