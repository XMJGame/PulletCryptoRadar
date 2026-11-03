# 数据源调研与补充核验接入 · 2026-10-31

目标：提前埋伏研究需要区分交易所托管、项目分配/释放、跨链和个人大户，不能把 Top10 原始余额当作庄家筹码。当前仍没有足够证据给出月内十倍的可靠概率。

## 对照项目与采用方式

| 项目 / 原始来源 | 实际获取方式与边界 | 本工程采用 |
| --- | --- | --- |
| [Rotki](https://github.com/rotki/rotki/blob/4e58e80b0dfe1946320886d84664794c20b7e2e6/rotkehlchen/db/constants.py)、[外部服务说明](https://docs.rotki.com/usage-guides/integrations/external-services) | 按来源保存 Etherscan、Blockscout、Routescan 等索引器记录；索引器覆盖与服务凭证仍有约束 | 保留提供者与原始观测时间；跨提供者人数不拼接。只借鉴设计，未复制 AGPL 实现 |
| [Dune Spellbook BNB CEX 模型](https://github.com/duneanalytics/spellbook/blob/f707157be9bb0701b6aeb27c992eb344c13a10f1/dbt_subprojects/hourly_spellbook/models/_sector/cex/addresses/chains/bnb/cex_bnb_addresses.sql) | 静态归属表结合链上 transfers / creation traces；模型依赖 Dune 已索引的数据表，仓库不等于完整免费数据 API | 按链、完整地址、证据链接核对。没有导入未核验标签；[许可证](https://github.com/duneanalytics/spellbook/blob/f707157be9bb0701b6aeb27c992eb344c13a10f1/LICENSE)为 BSL 1.1，未复制其 SQL |
| [GoPlus API 文档](https://github.com/GoPlusLabs/OpenAPI/blob/main/SecurityAPI.md)、[官方 API 概览](https://docs.gopluslabs.io/reference/api-overview) | 按 chain ID + contract 返回持币 Top10 与安全检测字段；标签和检测结果均为提供者声明 | BSC / Ethereum Top100 主快照外附独立 GoPlus 记录；其他支持链沿用 GoPlus，附相同核验字段 |

DefiLlama 的指定 CEX / emissions 目录请求返回 404，本轮没有把第三方 fork 或猜测的 URL 当作正式解锁源。自建全节点/全链索引会增加部署、存储和同步成本，本轮不引入。

## 已实现

- `OnChainSample.Security` 保存独立提供者、链/合约、原始来源链接、HTTP Date 观测时间、接收时间、持币人数、Top10 标签/合约/锁定线索及九项三态风险字段。
- 风险字段只接受 0/1；缺失、空值或其他值保持 null。没有以 HTTP Date 冒充已确认区块时间。
- 大地址线索不生成 `AddressLabel.Verified`，不改变分类覆盖、不触发开仓、不进入现有评分。合同身份、代理合约与实际控制人不是同一概念。
- 现有每轮预留 3 次请求，BSC / Ethereum 从 2 次主索引器请求加至 1 次补充请求；自选 30 分钟、其他 6 小时，仍受每小时/日/周预算和全应用 30GB 月预算限制。
- 主数据成功、补充数据失败：保留主快照，单独记录失败与退避，下一轮不早于 Retry-After；不会为补充接口立即重试。
- `/api/onchain` 及历史记录携带证据；SQLite JSON 兼容旧快照，无须重置账户或链上历史。
- 页面「链上分布 → PARTI / MYX → 合约风险与地址核验线索」可查看并进入已有标签核验表单。

## 本轮真实探测发现（研究资料，不回填历史基线）

本轮 GoPlus 请求对 PARTI 与 MYX 的 BSC 合约均返回 code=1，各返回 10 个大地址。PARTI 第一地址 `0xf977814e90da44bfa03b6295a0616a897441acec` 的该来源供应量占比约 51.59%；MYX 前几名报告为合约。它们不能据此标成庄家、团队或交易所。

[Binance 历史钱包披露](https://www.binance.com/en/square/post/97671)将相同地址列在 ETH 链；这仍不能直接确认该地址在 BSC 的当前归属，未据此套用到 BSC。MYX 第二大地址的 [BscScan 代码页面](https://bscscan.com/address/0x3a8bdFF831369cdd7DD4eBEAc2AaD999297b4637#code)显示 GradualRelease 合约，是后续释放计划核验入口；页面缓存不是当前可领取额度，不能直接填成实时解锁量。

GoPlus 与 Binplorer 报告的人数不同；探测时点也不同。差异分别保留，不能当成短时新增持有人，更不能用它补足 24h 变化。

## 尚缺与下一优先级

1. 验证 PARTI 大地址在 BSC 的当前交易所托管证据；核对 MYX 释放合约的代币、owner/gov、释放参数与实际调用记录，再分类。
2. 连续保存同提供者约 24h / 7d 的筹码变化，并核对覆盖缺口。实际日/周请求预算可能降低采样密度。
3. 解锁、退市公告的结构化来源与生效时间尚未完整接入；不能将“没有记录”视为“没有风险”。
4. 转账窗口、区块连续性与地址归属仍需补全；未分类流向不能称为交易所净流入。
5. 用可靠证据扩展低市值候选的身份覆盖，再检验历史样板及独立样本。当前二十个提前埋伏候选不等于二十个可执行推荐。

本轮没有更改模拟交易策略、资金分配、杠杆、风控或真实下单能力。
