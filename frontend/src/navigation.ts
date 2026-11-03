export const primaryNavigation=[
  {path:'/',icon:'◉',label:'量化工作台'},
  {path:'/opportunities',icon:'⌖',label:'妖币埋伏'},
  {path:'/token-check',icon:'⌕',label:'代币快查'},
  {path:'/journal',icon:'◷',label:'复盘与验证'},
] as const
export const auxiliaryNavigation=[{path:'/research',icon:'▦',label:'辅助工具'}] as const
export const retiredPaths=['/archive','/forward','/strategy-replay','/strategy-research','/volatility','/evaluation'] as const
export function navigationSection(path:string):string {
  if(['/','/signals','/execution'].includes(path))return '/'
  if(['/opportunities','/speculative','/speculative-evidence','/template-library'].includes(path))return '/opportunities'
  if(path==='/token-check')return '/token-check'
  if(['/journal','/quant-validation'].includes(path))return '/journal'
  return '/research'
}
