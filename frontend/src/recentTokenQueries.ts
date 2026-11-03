export const recentTokenQueriesKey='pullet.token-check.recent-queries.v1'
export const recentTokenQueriesLimit=5

function validQuery(value:unknown):value is string {
  return typeof value==='string'&&value.trim().length>=2&&value.trim().length<=100&&!/[\u0000-\u001f\u007f]/.test(value)
}
function queryKey(query:string):string {
  // Solana Mints are case sensitive; names, symbols and EVM addresses are not.
  return /^[1-9A-HJ-NP-Za-km-z]{32,44}$/.test(query)?query:query.toLowerCase()
}
export function rememberTokenQuery(queries:readonly string[],query:string):string[] {
  if(!validQuery(query))return [...queries]
  const value=query.trim(),key=queryKey(value)
  return [value,...queries.filter(q=>validQuery(q)&&queryKey(q.trim())!==key)].slice(0,recentTokenQueriesLimit)
}
export function parseRecentTokenQueries(raw:string|null):string[]|null {
  if(raw===null||raw.length>4096)return null
  try {
    const value:unknown=JSON.parse(raw)
    if(!value||typeof value!=='object'||!('version' in value)||value.version!==1||!('queries' in value)||!Array.isArray(value.queries))return null
    const queries=value.queries.filter(validQuery).slice(0,100)
    return queries.reduceRight<string[]>((saved,q)=>rememberTokenQuery(saved,q),[])
  }catch{return null}
}
