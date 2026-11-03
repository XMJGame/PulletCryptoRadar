# 多链实测、Solana 与同名币身份核验 · 2026-10-31

本轮新增九个已核验身份配置，连同 PARTI、MYX、ARIA 共十二个观察资产。采集成功仅表示数据入口可用，尚无足够分类证据生成提前埋伏买入判断。

## 正式采集结果

| 标的 | 观察链 | 本轮结果 |
| --- | --- | --- |
| ALPINE | BSC | Binplorer Top100 + 独立 GoPlus 补充证据；报告地址数 6,347 |
| BTW / Bitway | BSC | Binplorer Top100 + 独立 GoPlus 补充证据；报告地址数 43,707 |
| KAITO | Base | GoPlus Top10 与风险字段；报告地址数 343,913 |
| DOOD / Doodles | Solana | GoPlus Solana，09:10:34 源时间，报告地址数 109,820，返回十条 Owner / Token Account 记录 |
| PENGU / Pudgy Penguins | Solana | GoPlus Solana，09:10:33 源时间，报告地址数 562,718，返回十条记录 |
| PNUT / Peanut the Squirrel | Solana | 已核验 Mint；正式首采因一条缺少 Token Account 的记录被拒绝。可选字段解析已修复，保留原 09:40:04 冷却期限等待补采 |
| BLUR、TRB、LAB | Ethereum / Ethereum / BSC | 身份核验完成；本轮主索引器 HTTP Date 无效/过期，未伪装成实时数据；计划约 09:33 重试 |

PARTI 本轮取得带 GoPlus 补充的主快照；MYX 留存旧快照并标记失鲜；ARIA 后续定时采集成功。每项实际源/接收/下次尝试时间详见 `Solana-Chain-Validation.json`。

## Solana 数据语义

- 使用[官方 Solana Beta API](https://docs.gopluslabs.io/reference/solanatokensecurityusingget)，不把 Solana Mint 送到 EVM chain ID 接口。
- Base58 地址必须解码为 32 字节；完整 Mint 与 Owner 全程保留大小写，数据库、调度、身份比对、地址标签及前端键均使用链对应的比较规则。
- 保存增发、冻结、元数据修改、余额权限、关闭 Mint、转账费/钩子权限等三态字段；缺失保持 null。
- 分别保存 Owner 与 Token Account。允许提供者省略可选 Token Account，但不生成猜测地址；缺失账户记录可能与同 Owner 的已知账户重叠，合并时取可确认的较大下界，避免重复累加。
- 返回十条账户记录不证明全链按 Owner 聚合后的 Top10；因此 Solana 的 `OrderedTopHolders=false`，Top10 / 调整集中度保持未知。地址数量为提供者报告口径，未证明是去重钱包人数。
- `metadata.name/symbol`、Logo、可修改的元数据、`trusted_token` 标识均不确认资产身份。Solana 转账窗口/净流入暂未接入；已有资金流模块仍限 BSC。

## 同名币排除的实际证据

核验流程：交易所交易对/项目名称 → 原始公告或合同文档 → 指定链与完整合约 / Mint → Gate 当前链合约元数据 → 本地 CoinGecko 精确 ID / 链地址。多个同名候选保持 Ambiguous，直到完整地址证据一致；没有按市值、图标或价格最高者自动选择。

- PNUT 四个候选中，选择 `peanut-the-squirrel`，Mint `2qEHjDLDLbuBgRYvsxhc5D6uDWAivNFZGan56P1tpump`；[Binance 官方公告账号](https://www.binance.com/en/square/post/16083152529082)与 Gate SOL 地址逐字一致。其他候选没有获得该交易对的身份确认。
- DOOD 使用 `doodles` 的 Solana Mint `DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV`；[AscendEX 原始上币资料](https://ascendex.com/de/support/articles/130337-doodles-dood)、Gate SOL 地址与目录一致。
- PENGU 使用 `pudgy-penguins` 的 Solana Mint `2zMMhcVQEXDtdE6vsFS7S7D5oUodfJHE8vd1gnBouauv`；[Binance 原始公告](https://www.binance.com/en/support/announcement/detail/b60b629a7396476ba3c3391b016074e1)与 Gate 一致。
- LAB 从三个候选中确认 `lab`，BSC 地址与[Binance 合约公告](https://www.binance.com/en/support/announcement/detail/b7c479f8dfa64156a34e8bcefc241732)一致。
- BTW 从两个候选中确认当前交易对为 `bitway`，与[项目合同文档](https://docs.bitway.com/resources/btw-bitway-token)和 Gate BSC 地址一致；没有将 Banana Tape Wall 的 Solana 数据合入。
- APR 本轮仍未确认：Gate 返回不可用占位地址，目录名称亦需进一步核对，没有据此建立链上可信配置。

核验记录与映射历史保存在 `research/Chain-Identity-Expansion-2026-10-31.json`、`research/Solana-Identity-Verification-2026-10-31.json`、`research/Same-Name-Resolution-2026-10-31.json`。身份来源链接同时保存在链绑定中，修改启用状态不会清除同一身份的已存来源。

## 调度、验证和运行边界

- 修复固定只取前五个配置造成的饥饿：先筛已到期任务，再按最早到期、同到期自选优先、资产 ID 选每轮最多五个；下一轮继续轮换。
- 维持每轮三次预留、50 次/小时、200 次/日、1000 次/周及全应用 30GB/月预算。新增候选按六小时采样，自选仍按配置调度；总预算可能降低实际采样密度。不重置错误冷却。
- 测试覆盖：Solana 字节长度、大小写、相同名字错误 Mint、重复 Token Account、缺失可选账户、Owner 聚合、空/异常时间、未知权限、独立接口、SQLite 重启与来源保留、超过五个目标的轮换。
- 实际抽检发现 TUSDT 一根 Symbol=null 的缓存 K 线曾通过有效性判断，导致整批快照写入失败。已增加 Candle 身份必填、历史事件与内部 K 线符号一致的规则，拒绝缺失/串标数据，不按当前币名补造身份。
- 09:01:29 服务曾通过 .NET FailFast 退出，事件 1025 的末段为 JSON Deserialize / Decompress；根因未确认。09:02:50 恢复；两个 SQLite quick_check 均 ok，近期压缩 JSON 可解码。该退出与缺少 Symbol 的因果关系尚未证明，不能宣称退出问题已修复。
- 保留模拟账户现金、执行开始时间、持仓 ID 和已完成交易，没有改变资金、杠杆、交易策略或开真实委托。

验证汇总：后端 426 项、前端 30 项通过；Vue 类型检查与构建通过。界面截图 `Solana-OnChain.png` 展示正式采集的 DOOD 数据。
