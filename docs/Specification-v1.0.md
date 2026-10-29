可以。你说得对，项目都还没开始，没必要叫 v3.0。下面我把前面三份内容**重新整理、去重、消除冲突，合并成唯一的《CryptoRadar 技术实施规格书 v1.0》**。

以后你只需要把这一份给桌面端，之前那三份都不用再给。

# CryptoRadar 技术实施规格书 v1.0

## 1. 项目定位

项目名称暂定：

**CryptoRadar**

CryptoRadar 是一个运行在 Windows 本机的：

**加密货币异常行情发现 + 指定币监控 + 妖币扫描 + 风险预警 + 策略辅助系统。**

系统第一阶段只做：

```text
研究
监控
扫描
评分
预警
历史验证
```

不做：

```text
自动交易
自动开仓
自动平仓
自动加仓
交易所资金管理
```

所有策略输出仅作为：

```text
研究信号
风险状态
观察建议
```

不直接执行交易。

---

# 2. 产品核心目标

CryptoRadar 第一版需要解决三个核心问题。

---

# 3. 功能一：WatchTower 指定币监控

针对用户已经关注或持有的代币。

例如：

```text
ARIA
```

用户可以配置：

```text
成本价
目标价
关注价格
关键支撑
关键压力
项目钱包
团队钱包
投资人钱包
```

系统持续回答：

```text
这个币目前强不强？

有没有资金进入？

OI 有没有异常？

Funding 是否过热？

大户是偏多还是偏空？

Holder 是否增加？

筹码集中度有没有变化？

项目方钱包有没有异动？

有没有向交易所转币？

当前属于启动、上涨、过热、派发还是下跌阶段？

距离我的成本还有多少？

风险是在增加还是降低？
```

最终输出：

```text
TrendScore

DerivativeScore

OnChainScore

WalletRiskScore

CrowdingRisk

CurrentStage

StrategyState
```

例如：

```text
ARIA

当前价格          $0.088

成本价            $0.400

距离成本          +354%

趋势              ↑

OI                ↑↑

Holder            ↑

项目钱包          →

交易所流入        低

当前阶段          Breakout

当前状态          PositiveObserve

风险等级          Medium
```

---

# 4. WatchTower 策略状态

定义：

```csharp
public enum StrategyState
{
    Observe,

    PositiveObserve,

    HoldObserve,

    WaitConfirmation,

    AvoidChase,

    RiskIncreasing,

    ReduceRisk,

    CriticalRisk
}
```

说明：

### Observe

普通观察。

没有明显方向。

### PositiveObserve

整体结构正在改善。

例如：

```text
Price ↑

OI ↑

Volume ↑

Holder ↑

项目钱包稳定
```

### HoldObserve

当前趋势尚未破坏。

适合继续观察已有持仓。

### WaitConfirmation

有异动，但证据不足。

### AvoidChase

行情较强，但：

```text
涨幅过大
Funding偏高
多头拥挤
```

不适合作为新追涨信号。

### RiskIncreasing

风险明显增加。

例如：

```text
项目钱包大额转出

OI结构恶化

Holder明显下降
```

### ReduceRisk

出现较明确高风险因素。

### CriticalRisk

出现：

```text
项目钱包大量进入交易所

严重崩盘

系统性风险
```

---

# 5. WatchTower 风险优先级

风险信号必须覆盖普通上涨信号。

优先级：

```text
Exchange Wallet Flow
        ↓
Project Wallet
        ↓
OnChain Risk
        ↓
Derivatives Risk
        ↓
Trend
        ↓
Price
```

例如：

```text
价格上涨30%
```

但：

```text
项目钱包 → Binance
2000万枚
```

系统不能仍然显示：

```text
Strong Bullish
```

必须至少：

```text
RiskIncreasing
```

甚至：

```text
ReduceRisk
```

---

# 6. 功能二：Speculative Radar 妖币雷达

目标：

扫描 Binance U 本位永续合约，寻找：

> 市值较低、OI 活跃、合约资金异常、成交量异常、筹码集中、可能出现极端行情的币。

内部名称：

```text
SpeculativeAnomalyDetector
```

UI 可以叫：

```text
妖币雷达
```

原因：

很多异常标的并不是真正 Meme Coin。

---

# 7. 妖币雷达不是买入排行榜

必须严格区分：

```text
异常程度
```

和：

```text
风险程度
```

例如：

```text
AAA

AnomalyScore = 91

RiskScore = 35

Stage = Ignition
```

可能比较值得继续观察。

但：

```text
BBB

AnomalyScore = 96

RiskScore = 94

Stage = Overheated
```

说明：

```text
非常妖
```

但并不代表：

```text
适合追
```

---

# 8. SpeculativeAnomalyScore

第一版初始权重：

```text
低市值特征            10%

Volume / MarketCap    15%

OI / MarketCap        20%

OI异常                15%

价格/成交异动         10%

Funding               5%

Top Trader            5%

筹码集中度            10%

Holder变化            5%

Wallet行为            5%
```

总计：

```text
100
```

这些权重只是第一版初始参数。

后续必须通过：

```text
Replay
历史统计
真实命中率
```

不断校准。

---

# 9. SpeculativeRiskScore

独立计算：

```text
SpeculativeRiskScore
```

主要考虑：

```text
严重过热

极端Funding

多头拥挤

OI / MC极端

流动性不足

Top Holder过于集中

Project Wallet流出

Exchange Inflow

FDV过高

Holder减少

短期涨幅过大
```

---

# 10. 功能三：Volatility Radar 异动雷达

目标：

实时扫描 Binance 全部 U 本位永续。

检测：

```text
异常上涨

异常下跌

突然放量

突然增仓

主动买盘增强

主动卖盘增强

突破

跌破

强平瀑布

OI崩塌

价格与OI背离
```

最重要目标不是：

```text
涨到50%以后告诉用户
```

而是：

```text
尽量在 +5% ~ +20%

阶段发现最终可能涨到 +50% 的币。
```

下跌同理。

---

# 11. Volatility Radar 时间周期

每个资产维护：

```text
1m
3m
5m
15m
30m
1h
4h
24h
3d
```

核心周期：

```text
5m
15m
1h
4h
24h
```

---

# 12. Price Features

维护：

```text
Return1m

Return3m

Return5m

Return15m

Return30m

Return1h

Return4h

Return24h

Return3d
```

以及：

```text
DistanceTo1hHigh

DistanceTo4hHigh

DistanceTo24hHigh

DistanceTo7dHigh
```

---

# 13. 为什么不能只用涨跌幅

例如两个币都上涨：

```text
+10%
```

A：

```text
Price +10%

Volume普通

OI +2%

Taker普通
```

B：

```text
Price +10%

Volume异常

OI +25%

Taker Buy明显增强

突破24H高点
```

B 才是真正需要进入 Radar 的标的。

因此：

```text
涨跌幅只是 Feature 之一
```

不是最终判断。

---

# 14. Feature Engine

所有 Collector 只负责：

```text
采集数据
```

禁止直接计算策略。

统一交给：

```text
FeatureEngine
```

输出：

```csharp
public sealed class AssetFeatures
{
    public PriceFeatures Price { get; set; }

    public VolumeFeatures Volume { get; set; }

    public OpenInterestFeatures OpenInterest { get; set; }

    public FlowFeatures Flow { get; set; }

    public FundingFeatures Funding { get; set; }

    public TraderFeatures Trader { get; set; }

    public MarketFeatures Market { get; set; }

    public OnChainFeatures OnChain { get; set; }
}
```

---

# 15. 异常检测不要主要依赖普通 ZScore

加密市场属于：

```text
长尾

极端值多

尖峰多
```

普通：

```text
Mean + StandardDeviation
```

容易被历史极端值污染。

第一版主要使用：

# Robust Z-Score

公式：

```text
RobustZ =

(X - Median)

/

(1.4826 × MAD)
```

其中：

```text
MAD =
Median(|Xi - Median|)
```

---

# 16. 需要计算的 RobustZ

例如：

```text
PriceRobustZ5m

PriceRobustZ15m

VolumeRobustZ

OIRobustZ

TakerRobustZ

LiquidationRobustZ
```

---

# 17. Percentile

同时计算历史百分位。

例如：

```text
当前 OI 1h 增幅
```

位于过去30天：

```text
99.2 Percentile
```

UI 显示：

```text
OI变化异常程度：

99.2%
```

内部同时保存：

```text
RobustZ

Percentile
```

---

# 18. Runtime 历史窗口

每个币内存保存：

```text
1m     48小时

5m     7天

15m    30天
```

长期：

```text
1h
```

存 SQLite。

---

# 19. UpMoveDetector

上涨检测器：

```text
UpMoveDetector
```

输出：

```text
UpSetupScore
```

范围：

```text
0 ~ 100
```

第一版权重：

```text
价格异常            15%

成交量异常          20%

OI异常              20%

Taker主动买盘       15%

突破结构            10%

大户行为            10%

Funding状态          5%

流动性/市值          5%
```

---

# 20. 为什么价格权重不能太高

如果价格占：

```text
40% ~ 50%
```

系统永远只能找到：

```text
已经涨很多的币
```

我们的目标是：

```text
价格还没完全启动
```

但：

```text
资金结构已经变化
```

所以：

```text
OI

Volume

Taker
```

占更高权重。

---

# 21. 上涨阶段

```csharp
public enum UpMoveStage
{
    Normal,

    Preheat,

    Ignition,

    Breakout,

    Acceleration,

    Overheated,

    Distribution,

    Cooling
}
```

---

# 22. Preheat

预热。

典型：

```text
Price15m

+2% ~ +6%

Volume RobustZ > 2

OI明显增加

Taker买盘增强
```

处理：

```text
进入 Radar

不通知
```

---

# 23. Ignition

点火。

要求：

```text
价格开始异常
```

并且：

```text
Volume

OI

Taker

Structure
```

至少两个进一步确认。

例如：

```text
Price15m        +7%

PriceRobustZ    3.1

VolumeRobustZ   4.2

OI15m           +12%

TakerRatio      1.7
```

进入：

```text
Strong Candidate
```

---

# 24. Breakout

要求：

```text
突破结构

+

资金确认
```

例如：

```text
突破24h High

Volume ↑

OI ↑
```

---

# 25. Acceleration

已经进入：

```text
快速上涨
```

例如：

```text
1h +20%

4h +35%
```

仍然记录。

但通知优先级开始降低。

---

# 26. Overheated

例如：

```text
短期涨幅巨大

Funding极高

Global Long严重偏多

OI/MC极高
```

此时：

```text
禁止继续作为“新机会”推送。
```

UI：

```text
🔥 Overheated
```

---

# 27. Distribution

可能出现：

```text
Price仍然高位

OI开始下降

Taker Sell增强

项目钱包流出

Whale减少
```

进入：

```text
Distribution Risk
```

---

# 28. 上涨信号触发

不能只：

```text
Score > 80
```

就通知。

第一版：

```text
UpSetupScore >= 82

AND

Stage >= Ignition

AND

DataConfidence >= 70

AND

至少3个Feature Family确认

AND

RiskGate未否决
```

Feature Family：

```text
Price

Volume

OI

Flow

Trader

Structure
```

---

# 29. DownMoveDetector

下跌单独设计。

不是：

```text
上涨公式 × -1
```

输出：

```text
DownRiskScore
```

第一版权重：

```text
价格异常             20%

成交量异常           15%

OI异常               20%

Taker卖盘             15%

Liquidation           15%

结构跌破              10%

Funding/Crowding       5%
```

---

# 30. 下跌需要区分两种情况

### Short Build

```text
Price ↓

OI ↑
```

表示：

```text
新空仓正在建立
```

---

### Long Liquidation / Long Squeeze Down

```text
Price ↓

OI ↓↓↓

Liquidation ↑↑
```

更像：

```text
多头强平

去杠杆
```

第二种风险等级通常更高。

---

# 31. 下跌通知阈值更敏感

普通上涨：

```text
Score >= 82
```

下跌风险：

```text
Score >= 75
```

即可进入 Critical Candidate。

特别是：

```text
WatchList
```

中的币。

---

# 32. Market Regime Detector

这是防骚扰的重要模块。

每分钟计算：

```text
上涨币数量

下跌币数量

> +5% 数量

< -5% 数量

> +10% 数量

< -10% 数量

MedianReturn

BTC Return

ETH Return
```

输出：

```csharp
public enum MarketRegime
{
    Normal,

    BroadRiskOn,

    BroadRiskOff,

    SystemicPump,

    SystemicCrash
}
```

---

# 33. 系统性行情处理

例如：

```text
75%的合约

1小时同时下跌
```

这时候：

```text
AAA -10%

BBB -12%

CCC -15%
```

不应该发送三条通知。

而应该认定：

```text
Market Wide Event
```

只推：

```text
🚨 Binance合约市场出现系统性下跌
```

WatchList Critical：

```text
仍然允许单独通知
```

---

# 34. Episode Engine

一次行情只创建一个：

```text
MoveEpisode
```

例如：

```text
10:01 Preheat

10:10 Ignition

10:25 Breakout

10:45 Acceleration

11:20 Overheated
```

全部属于：

```text
同一个Episode
```

不能产生五次独立提醒。

---

# 35. MoveEpisode 模型

```csharp
public sealed class MoveEpisode
{
    public long Id { get; set; }

    public int AssetId { get; set; }

    public MoveDirection Direction { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public decimal StartPrice { get; set; }

    public decimal CurrentPrice { get; set; }

    public decimal PeakPrice { get; set; }

    public decimal LowestPrice { get; set; }

    public double StartScore { get; set; }

    public double MaxScore { get; set; }

    public string Stage { get; set; }

    public string LastNotificationStage { get; set; }

    public DateTime? LastNotificationTime { get; set; }
}
```

---

# 36. Episode 生命周期

```text
Created

Active

Escalating

Overheated

Cooling

Closed
```

默认结束条件可考虑：

```text
持续2小时没有新高/新低

或者

Score < 40

或者

方向明确反转
```

后续通过真实数据调整。

---

# 37. 同一个 Episode 最多两次通知

第一次：

```text
Ignition / Breakout

Score >= 82
```

第二次必须明显升级：

例如：

```text
82 → 94
```

或者：

```text
Ignition
→
Acceleration
```

或者：

```text
出现Critical Risk
```

之后不继续通知。

---

# 38. Alert Policy Engine

必须明确：

```text
Signal != Notification
```

完整流程：

```text
Market Data
↓
Feature Engine
↓
Detector
↓
Signal
↓
Episode
↓
Risk Gate
↓
Rank
↓
Alert Policy
↓
Notification
```

---

# 39. 通知预算

普通市场：

```text
每天最多5条
```

实际期望：

```text
2 ~ 4条 / 天
```

WatchList Critical：

```text
不受普通额度限制
```

Systemic Event：

```text
不受普通额度限制
```

但必须去重。

---

# 40. Top-N竞争机制

过去：

```text
30分钟
```

例如：

```text
AAA 88

BBB 94

CCC 91

DDD 84
```

只通知：

```text
BBB

CCC
```

默认：

```text
30分钟最多2个新标的通知
```

其他：

```text
进入Dashboard
```

---

# 41. Signal 等级

UI 使用：

```text
S

A

B

C
```

---

## S级

```text
Score >= 92

Confidence >= 80

至少4个独立Feature确认

Risk较低
```

属于非常罕见信号。

---

## A级

```text
Score >= 82

Confidence >= 70

至少3个独立Feature确认
```

允许通知。

---

## B级

```text
70 ~ 81
```

进入 Radar。

默认不通知。

---

## C级

```text
< 70
```

只内部记录。

---

# 42. DataConfidence

必须独立存在。

例如：

```text
SignalScore = 91

DataConfidence = 42
```

表示：

```text
信号理论上很强

但数据不完整
```

不允许通知。

---

# 43. DataConfidence 来源

考虑：

```text
Binance Price

Binance OI

TopTrader

MarketCap

OnChain

Wallet Label
```

例如：

```text
Binance 100%

CoinGecko 100%

OnChain Missing

Wallet Unknown
```

最终 Confidence 下降。

---

# 44. 缺失数据不能当作 0

例如：

```text
Funding = Missing
```

不等于：

```text
Funding = 0
```

定义：

```csharp
public enum DataStatus
{
    Valid,

    Stale,

    Missing,

    Invalid
}
```

统一：

```csharp
public sealed class FeatureValue<T>
{
    public T? Value { get; init; }

    public DataStatus Status { get; init; }

    public DateTime SourceTime { get; init; }

    public string Source { get; init; }
}
```

---

# 45. 新上市币

上市：

```text
< 7天
```

没有足够30天历史。

标记：

```text
NewListing
```

使用：

```text
CrossSection Percentile
```

也就是：

```text
与当前Binance其他币比较
```

同时降低：

```text
DataConfidence
```

---

# 46. 整体技术架构

```text
                        Binance
                           │
             ┌─────────────┼──────────────┐
             │             │              │
           Ticker        Futures       Liquidation
             │             │              │
             └─────────────┼──────────────┘
                           ▼
                  Market Runtime Cache
                           │
                           ▼
                     Feature Engine
                           │
            ┌──────────────┼───────────────┐
            ▼              ▼               ▼
      Volatility      Speculative      WatchTower
        Radar            Radar            Engine
            │              │               │
            └──────────────┼───────────────┘
                           ▼
                      Signal Engine
                           │
                           ▼
                      Episode Engine
                           │
                           ▼
                     Risk Gate Engine
                           │
                           ▼
                       Rank Engine
                           │
                           ▼
                     Alert Policy
                     │           │
                     ▼           ▼
                 Dashboard    Notification
```

外围：

```text
CoinGecko
↓
MarketCap / FDV / Supply

OnChain Provider
↓
Holder / Concentration / Wallet
```

---

# 47. 技术栈

使用：

```text
C#

.NET 10
```

如果开发环境暂时主要是：

```text
.NET 8
```

也可以使用 .NET 8。

架构不得依赖特定版本。

---

后端：

```text
ASP.NET Core

BackgroundService

System.Threading.Channels

HttpClientFactory

Dapper

Microsoft.Data.Sqlite
```

---

UI：

```text
Blazor Web App

Interactive Server

ECharts
```

---

缓存：

```text
ConcurrentDictionary

CircularBuffer

IMemoryCache
```

---

不使用：

```text
Redis

PostgreSQL

Kafka

RabbitMQ

Docker

云服务器
```

---

# 48. 本地运行方式

最终：

```text
CryptoRadar.exe
```

运行后：

```text
http://localhost:5218
```

浏览器打开。

以后可增加：

```text
Windows Tray
```

负责：

```text
托盘

打开UI

开机启动

Windows通知
```

业务逻辑仍然在 Host。

---

# 49. Solution 结构

```text
CryptoRadar.sln

src/

├── CryptoRadar.Core

├── CryptoRadar.Application

├── CryptoRadar.Infrastructure

├── CryptoRadar.Providers.Binance

├── CryptoRadar.Providers.Market

├── CryptoRadar.Providers.OnChain

├── CryptoRadar.Scanner

├── CryptoRadar.Host

└── CryptoRadar.Web


tests/

├── CryptoRadar.Core.Tests

├── CryptoRadar.Scanner.Tests

└── CryptoRadar.IntegrationTests
```

---

# 50. Core

只放：

```text
Domain Model

Enum

ValueObject

Interface

Domain Event
```

禁止依赖：

```text
SQLite

Binance

CoinGecko

ASP.NET

UI
```

---

# 51. Application

放：

```text
WatchListService

AssetService

StrategyService

AlertService

WalletTrackingService

MarketAnalysisService
```

---

# 52. Infrastructure

放：

```text
SQLite

Repository

DatabaseMigration

Cache

Clock

Configuration

Encryption

Notification
```

---

# 53. Providers.Binance

```text
BinanceRestClient

BinanceWebSocketClient

BinanceSymbolProvider

BinanceOpenInterestProvider

BinancePositionProvider

BinanceFundingProvider

BinanceLiquidationProvider
```

---

# 54. Providers.Market

第一版：

```text
CoinGeckoProvider
```

负责：

```text
MarketCap

FDV

CirculatingSupply

TotalSupply

ATH
```

---

# 55. Providers.OnChain

统一接口。

第一版优先：

```text
BSC / EVM
```

可以实现：

```text
MoralisEvmProvider
```

以后可替换：

```text
Covalent

Alchemy

Birdeye

BscScan

其他Provider
```

业务层禁止直接依赖 Moralis DTO。

---

# 56. Asset 模型

不能直接使用：

```text
ARIAUSDT
```

作为资产唯一ID。

定义：

```csharp
public sealed class Asset
{
    public int Id { get; set; }

    public string Symbol { get; set; }

    public string Name { get; set; }

    public string? CoinGeckoId { get; set; }

    public MappingStatus MappingStatus { get; set; }
}
```

---

# 57. ChainToken

```csharp
public sealed class ChainToken
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public ChainType Chain { get; set; }

    public string ContractAddress { get; set; }

    public int Decimals { get; set; }

    public bool IsPrimary { get; set; }
}
```

原因：

```text
同名币

跨链币

1000PEPE

1000SHIB

Wrapped Token
```

都会造成映射问题。

---

# 58. MappingStatus

```csharp
public enum MappingStatus
{
    Unknown,

    SymbolMatched,

    Ambiguous,

    ContractVerified,

    ManualVerified
}
```

可信优先级：

```text
ManualVerified

>

ContractVerified

>

SymbolMatched

>

Ambiguous

>

Unknown
```

---

# 59. 映射不确定时

例如：

```text
MappingStatus = Ambiguous
```

则：

```text
MarketCap不能参与最终高置信度评分
```

UI：

```text
⚠ 资产映射待确认
```

允许人工选择：

```text
Binance Symbol

↓

CoinGecko ID

↓

Chain Contract
```

---

# 60. Binance Symbol 初始化

程序启动：

获取：

```text
USDT

PERPETUAL

TRADING
```

合约。

每天重新同步。

发现：

```text
新上币

下架

新增永续
```

---

# 61. Binance 数据分层

禁止：

```text
500个币
全部每分钟请求所有高级API
```

采用：

```text
Tier 0

Tier 1

Tier 2

Tier 3
```

---

# 62. Tier 0

全市场实时：

```text
Price

MarkPrice

Funding

24h Volume

24h PriceChange
```

主要来源：

```text
WebSocket
```

---

# 63. Tier 1

所有 USDT 永续：

每：

```text
5分钟
```

获取：

```text
Current OI
```

然后计算：

```text
OI / MC

Volume / MC

OI Change

Price Change
```

---

# 64. Tier 2

排名前：

```text
默认80
```

候选。

额外查询：

```text
Global LongShort

Top Trader Account Ratio

Top Trader Position Ratio

Taker BuySell

OI History
```

---

# 65. Tier 3

只对：

```text
WatchList

+

Scanner Top20~30
```

执行链上扫描。

例如：

```text
Holder

Top Holder

Wallet

Exchange Flow
```

---

# 66. RuntimeCache

接口：

```csharp
public interface IMarketRuntimeStore
{
    AssetRuntimeState Get(int assetId);

    IReadOnlyCollection<AssetRuntimeState> GetAll();

    void UpdatePrice(...);

    void UpdateMarket(...);

    void UpdateDerivative(...);

    void UpdateOnChain(...);
}
```

实现：

```text
ConcurrentDictionary<int, AssetRuntimeState>
```

---

# 67. AssetRuntimeState

```csharp
public sealed class AssetRuntimeState
{
    public int AssetId { get; init; }

    public MarketRuntime Market { get; init; }

    public DerivativeRuntime Derivative { get; init; }

    public OnChainRuntime OnChain { get; init; }

    public AssetFeatures Features { get; init; }

    public ScannerRuntime Scanner { get; init; }

    public DataFreshness Freshness { get; init; }

    public MoveEpisode? CurrentEpisode { get; set; }
}
```

---

# 68. CircularBuffer

每个币保留：

```text
1m

5m

15m
```

RollingWindow。

用于计算：

```text
Return

Median

MAD

Percentile

OIChange

VolumeChange

Breakout
```

避免频繁查询 SQLite。

---

# 69. SQLite 架构

本地使用：

```text
SQLite + Memory Cache
```

非常适合：

```text
单用户

单机

固定采集

读多写规律
```

---

# 70. 数据库拆分

```text
Data/

CryptoRadar.db

CryptoRadar.Market.db
```

---

# 71. CryptoRadar.db

业务数据：

```text
Asset

AssetChain

AssetMapping

WatchAsset

TrackedWallet

WalletTransfer

SignalEvent

AlertEvent

StrategyEvent

MoveEpisode

Settings

ProviderState
```

---

# 72. CryptoRadar.Market.db

时间序列：

```text
MarketSnapshot1m

MarketSnapshot5m

MarketSnapshot1h

DerivativeSnapshot

MarketCapSnapshot

OnChainSnapshot

ScannerSnapshot
```

---

# 73. SQLite 设置

启动执行：

```sql
PRAGMA journal_mode=WAL;

PRAGMA synchronous=NORMAL;

PRAGMA foreign_keys=ON;

PRAGMA busy_timeout=5000;

PRAGMA temp_store=MEMORY;
```

---

# 74. 单 PersistenceWorker

禁止多个 Worker 同时随意写 SQLite。

统一：

```text
Collector
↓
Channel<PersistenceCommand>
↓
PersistenceWorker
↓
SQLite
```

减少锁竞争。

---

# 75. 实时行情不能每秒写数据库

WebSocket：

```text
1秒级
```

只更新：

```text
RuntimeCache
```

然后聚合。

---

WatchList：

```text
1m
```

落库。

全市场：

```text
5m
```

落库。

---

# 76. 数据保存周期

默认：

```text
WatchList 1m

保留14天
```

全市场：

```text
5m

保留90天
```

聚合：

```text
1h
```

永久保留。

---

# 77. FuturesSnapshot5m

建议：

```sql
CREATE TABLE FuturesSnapshot5m
(
    AssetId INTEGER NOT NULL,

    Timestamp INTEGER NOT NULL,

    Price REAL NOT NULL,

    Volume24hUsd REAL,

    QuoteVolume5m REAL,

    OpenInterestUsd REAL,

    FundingRate REAL,

    GlobalLongShortRatio REAL,

    TopAccountLongShortRatio REAL,

    TopPositionLongShortRatio REAL,

    TakerBuySellRatio REAL,

    PRIMARY KEY (AssetId, Timestamp)
);
```

---

# 78. MarketCapSnapshot

```sql
CREATE TABLE MarketCapSnapshot
(
    AssetId INTEGER NOT NULL,

    Timestamp INTEGER NOT NULL,

    MarketCapUsd REAL,

    FdvUsd REAL,

    CirculatingSupply REAL,

    TotalSupply REAL,

    MaxSupply REAL,

    PRIMARY KEY (AssetId, Timestamp)
);
```

---

# 79. OnChainSnapshot

```sql
CREATE TABLE OnChainSnapshot
(
    AssetId INTEGER NOT NULL,

    Chain INTEGER NOT NULL,

    Timestamp INTEGER NOT NULL,

    HolderCount INTEGER,

    Top10Percent REAL,

    Top25Percent REAL,

    Top50Percent REAL,

    Top100Percent REAL,

    AdjustedTop10Percent REAL,

    ExchangeSupplyPercent REAL,

    ProjectControlledPercent REAL,

    PRIMARY KEY (AssetId, Chain, Timestamp)
);
```

---

# 80. WatchAsset

```sql
CREATE TABLE WatchAsset
(
    AssetId INTEGER PRIMARY KEY,

    Enabled INTEGER NOT NULL,

    CostPrice REAL,

    TargetPrice REAL,

    ManualSupportPrice REAL,

    ManualResistancePrice REAL,

    EnableWalletMonitor INTEGER NOT NULL,

    CreatedAt INTEGER NOT NULL
);
```

---

# 81. CoinGecko

采集：

```text
MarketCap

FDV

Volume

Supply

ATH
```

周期：

```text
30分钟
```

没必要一分钟查询一次。

---

# 82. OnChain Provider

统一：

```csharp
public interface IOnChainProvider
{
    Task<TokenHolderSummary?> GetHolderSummaryAsync(
        ChainToken token,
        CancellationToken ct);

    Task<IReadOnlyList<TokenHolder>> GetTopHoldersAsync(
        ChainToken token,
        int count,
        CancellationToken ct);

    Task<IReadOnlyList<TokenTransfer>> GetTransfersAsync(
        ChainToken token,
        string wallet,
        DateTime since,
        CancellationToken ct);

    Task<AddressLabel?> GetAddressLabelAsync(
        ChainType chain,
        string address,
        CancellationToken ct);
}
```

---

# 83. Holder 集中度必须分类

不能直接：

```text
Top10 = 80%
```

就认为：

```text
80%被项目方控制
```

因为可能包含：

```text
Burn

LP

Bridge

Exchange

Contract

Project

Team

Investor

Unknown Whale
```

---

# 84. WalletLabelType

```csharp
public enum WalletLabelType
{
    Unknown,

    Project,

    Team,

    Investor,

    Treasury,

    Exchange,

    LP,

    Bridge,

    Burn,

    Whale
}
```

---

# 85. Concentration 指标

同时计算：

```text
RawTop10Percent

AdjustedTop10Percent

ExchangeSupplyPercent

ProjectControlledPercent
```

AdjustedTop10：

默认排除：

```text
Burn

LP

Bridge
```

---

# 86. TrackedWallet

```csharp
public sealed class TrackedWallet
{
    public long Id { get; set; }

    public int AssetId { get; set; }

    public ChainType Chain { get; set; }

    public string Address { get; set; }

    public WalletLabelType LabelType { get; set; }

    public string Label { get; set; }

    public bool IsProjectRelated { get; set; }

    public bool Enabled { get; set; }
}
```

---

# 87. Wallet Transfer 分类

### Project → Project

```text
InternalTransfer
```

低风险。

---

### Project → Unknown

```text
ProjectOutflow
```

中风险。

---

### Project → Exchange

```text
ExchangeInflow
```

高风险。

---

### Project → Unknown → Exchange

金额、时间高度对应：

```text
SuspectedExchangeInflow
```

高风险。

UI 必须显示：

```text
疑似流向交易所
```

不能直接显示：

```text
项目方已卖出
```

链上转账不等于已出售。

---

# 88. Wallet Graph

第一版：

```text
最多追踪2 Hop
```

例如：

```text
Project Wallet
      │
      ▼
Transit Wallet
      │
      ▼
Binance
```

以后再考虑：

```text
3~4 Hop
```

---

# 89. Signal Engine

Signal 类型：

```text
OiSurge

VolumeSpike

FundingExtreme

LongCrowded

ShortCrowded

TopTraderLongIncrease

TopTraderRetailDivergence

HolderGrowth

HolderDecline

ConcentrationIncrease

ConcentrationDecrease

ProjectWalletOutflow

ExchangeInflow

Breakout

Breakdown

LiquidationSpike

SystemicCrash

SystemicPump
```

---

# 90. SignalEvent

```csharp
public sealed class SignalEvent
{
    public long Id { get; set; }

    public int AssetId { get; set; }

    public SignalType Type { get; set; }

    public SignalLevel Level { get; set; }

    public double Value { get; set; }

    public string Reason { get; set; }

    public string StrategyVersion { get; set; }

    public DateTime Time { get; set; }
}
```

---

# 91. SignalLevel

```text
Info

Notice

Warning

High

Critical
```

UI 可以同时映射：

```text
C

B

A

S
```

---

# 92. Strategy 必须可解释

禁止只输出：

```text
继续持有
```

必须：

```csharp
public sealed class StrategyResult
{
    public StrategyState State { get; set; }

    public int Confidence { get; set; }

    public List<StrategyReason> PositiveReasons { get; set; }

    public List<StrategyReason> RiskReasons { get; set; }

    public List<StrategyTrigger> NextTriggers { get; set; }
}
```

---

# 93. 示例

```text
ARIA

当前状态：

PositiveObserve

正面：

✓ OI持续增加

✓ Volume增加

✓ Holder增加

✓ 项目钱包暂未流入交易所


风险：

⚠ Funding开始偏高

⚠ Top10集中度较高


下一步关注：

突破 $0.10

突破 $0.12~0.15

Funding > 某历史高分位

项目钱包大额流出
```

---

# 94. Market Regime

首页必须显示：

```text
Market State
```

例如：

```text
Normal

Risk On

Risk Off

Systemic Pump

Systemic Crash
```

---

# 95. Dashboard

首页四个区域。

---

## Market State

例如：

```text
Binance Futures

487 Symbols

Market Regime:

Normal
```

---

## Volatility Radar

```text
AAA

UP

A

Ignition
```

```text
BBB

DOWN

S

Crash
```

```text
CCC

UP

B

Preheat
```

---

## Speculative Radar

显示：

```text
Symbol

MarketCap

OI/MC

Volume/MC

Anomaly

Risk

Stage
```

---

## WatchList

显示：

```text
ARIA

Price

Cost

Distance

Strategy

Risk

Wallet
```

---

# 96. Asset Detail

Tab：

```text
Overview

Price

Derivatives

OnChain

Wallets

Signals

Strategy

History
```

---

# 97. Derivatives 页面

主要展示：

```text
Price

OI

Volume

Funding

Global LongShort

Top Trader Account

Top Trader Position

Taker BuySell

Liquidation
```

重点：

```text
Price
VS
OI
```

同一时间轴。

---

# 98. OnChain 页面

显示：

```text
HolderCount

Top10

Top25

Top50

Top100

AdjustedTop10

ExchangeSupply

ProjectControlled
```

同时：

```text
24h

7d

30d
```

变化。

---

# 99. Wallet 页面

显示：

```text
Address

Label

Balance

Supply%

24h Change

7d Change

Last Activity
```

以及：

```text
Wallet Graph
```

---

# 100. Alert

第一版：

```text
Web UI

Windows Toast
```

后续：

```text
Telegram Bot
```

统一：

```csharp
public interface IAlertChannel
{
    Task SendAsync(AlertMessage message);
}
```

---

# 101. Alert 去重

例如：

```text
Funding过高
```

不能五分钟一次通知。

使用：

```text
AlertKey

AssetId

SignalType

EpisodeId
```

相同通知默认冷却：

```text
30分钟
```

风险升级：

```text
Warning
→
Critical
```

允许立即再次通知。

---

# 102. 通知示例

上涨：

```text
🚀 XYZUSDT 异常启动

阶段：Ignition

等级：A

15m        +8.4%

OI 15m     +19.7%

Volume     99.1分位

Taker      1.82


当前价格仍处于相对早期。

风险：

Funding暂未过热。
```

---

暴跌：

```text
🚨 XYZUSDT 多头踩踏

等级：S

5m         -7.8%

OI 5m      -18.4%

Liquidation 99.7分位

TakerSell明显占优


判断：

当前更接近多头强平/去杠杆，

而不是普通回调。
```

---

# 103. 日报

每天生成：

```text
CryptoRadar 日报
```

例如：

```text
今日异常上涨：

17


A/S级：

5


最终涨幅>20%：

3


异常下跌：

12
```

同时：

```text
AAA

首次发现：

+6.7%

今日最高：

+58%
```

---

# 104. ReplayEngine

必须第一版就规划。

生产系统和 Replay 必须使用同一套：

```text
FeatureEngine

Detector

SignalEngine

EpisodeEngine

AlertPolicy
```

不能另外写：

```text
BacktestStrategy
```

复制算法。

---

# 105. Replay

例如：

```text
2026-10-01 00:00

↓

00:01

↓

00:02
```

按真实历史顺序播放。

禁止使用：

```text
未来数据
```

参与当前判断。

---

# 106. 评价指标

不能只看：

```text
胜率
```

必须统计：

```text
MFE

MAE

Hit20

Hit50

Drop20

Drop30

CaptureLead

CrashLead

Precision@K

ExtremeMoveRecall
```

---

# 107. MFE

Maximum Favorable Excursion。

例如上涨信号后：

```text
24h最大涨幅
```

---

# 108. MAE

Maximum Adverse Excursion。

例如上涨信号后：

```text
24h最大回撤
```

---

# 109. Hit20

信号后24小时：

```text
最大上涨 >= 20%
```

---

# 110. Hit50

信号后24小时：

```text
最大上涨 >= 50%
```

---

# 111. CaptureLead

例如：

```text
系统第一次发现：

+8%

最终最高：

+62%
```

则：

```text
CaptureLead = 54个百分点
```

这是异常上涨检测最重要指标之一。

---

# 112. CrashLead

同理：

系统在：

```text
-8%
```

发现风险。

最终：

```text
-40%
```

则：

```text
CrashLead = 32个百分点
```

---

# 113. Precision@K

如果每天只通知：

```text
3个
```

上涨标的。

其中：

```text
2个最终Hit20
```

则：

```text
Precision@3 = 66.7%
```

这比整个市场：

```text
预测500个币
```

的准确率更有意义。

---

# 114. ExtremeMoveRecall

例如：

当天实际最终：

```text
+50%
```

以上标的：

```text
4个
```

系统提前捕捉：

```text
3个
```

则：

```text
ExtremeMoveRecall = 75%
```

---

# 115. 第一阶段优化目标

不是保证。

作为系统迭代目标：

```text
普通通知：

平均 <= 5条/天
```

尽量：

```text
2 ~ 4条/天
```

对于最终：

```text
+50%
```

的币：

尽量：

```text
+20%以前
```

进入 Radar。

暴跌：

尽量在：

```text
-15%以内
```

进入风险 Radar。

---

# 116. 策略不能凭感觉永远不变

所有策略必须：

```text
StrategyVersion
```

例如：

```text
UpMove-1.0

DownMove-1.0

Speculative-1.0
```

调整后：

```text
UpMove-1.1
```

历史数据保留原版本。

---

# 117. 参数调优

禁止：

```text
看昨天没抓到
今天立刻修改参数
```

应该：

```text
积累数据

↓

Replay

↓

Validation

↓

调整版本
```

---

# 118. 防止过拟合

未来参数验证：

```text
Train Window

Validation Window

Test Window
```

时间必须分开。

例如：

```text
1~2月

训练/调参


3月

Validation


4月

完全独立Test
```

---

# 119. 后续机器学习

至少积累：

```text
3~6个月
```

数据后再考虑。

候选：

```text
LightGBM

XGBoost

ML.NET
```

Features：

```text
PriceRobustZ

VolumeRobustZ

OIRobustZ

TakerImbalance

Funding

OI/MC

MarketCap

TopTrader

HolderGrowth

Top10

WalletFlow
```

Label：

```text
Hit20

Hit50

Drop20

Drop30
```

---

# 120. ML 不直接控制通知

ML 只输出：

```text
ModelScore
```

然后仍然经过：

```text
RiskGate

Rank

AlertPolicy
```

---

# 121. LLM 定位

以后可以加入大模型。

用于：

```text
自然语言解释
```

例如：

```text
XYZ 当前上涨仅9%，
但15分钟OI增长24%，
成交量处于过去30天99.4分位，
Taker买盘持续增强，
并突破24小时高点，
因此进入Ignition阶段。
```

但：

```text
LLM不直接决定买卖
```

---

# 122. Provider Rate Limit

统一：

```csharp
public interface IRequestLimiter
{
    Task WaitAsync(
        string provider,
        int weight,
        CancellationToken ct);
}
```

支持：

```text
Binance

CoinGecko

OnChain
```

---

# 123. 网络重试

例如：

```text
1s

2s

5s

10s
```

指数退避 + jitter。

如果：

```text
429
```

必须：

```text
读取Retry-After

降低请求

暂停Provider
```

不能无限重试。

---

# 124. Provider Health

首页显示：

```text
Binance WebSocket    Healthy

Binance REST         Healthy

CoinGecko            Healthy

OnChain              Delayed
```

保存：

```text
LastSuccess

LastFailure

Latency

ErrorCount
```

---

# 125. Freshness

每个数据保存：

```text
SourceTime

ReceivedTime
```

例如：

```text
Price       1秒

OI          3分钟

MarketCap   12分钟

OnChain     45分钟
```

过期：

```text
Stale
```

Scanner 不允许使用严重过期数据产生高置信度通知。

---

# 126. API Key

第一版只允许：

```text
读取市场数据需要的Key
```

禁止要求：

```text
Withdrawal权限

Trade权限

Futures Trade权限
```

敏感 Key：

禁止放：

```text
源码

普通appsettings.json
```

可使用：

```text
Windows DPAPI

或者

Windows Credential Manager
```

---

# 127. Settings

非敏感配置：

```json
{
  "Scanner": {
    "IntervalMinutes": 5,
    "DeepCandidateCount": 80,
    "OnChainCandidateCount": 25,
    "MinMarketCapUsd": 5000000,
    "MaxMarketCapUsd": 1000000000,
    "MinVolume24hUsd": 5000000,
    "MinOpenInterestUsd": 1000000
  },
  "Alert": {
    "DailyNormalLimit": 5,
    "WindowMinutes": 30,
    "MaxNewAssetsPerWindow": 2
  },
  "Storage": {
    "Snapshot5mRetentionDays": 90,
    "Watch1mRetentionDays": 14
  },
  "Wallet": {
    "MaxGraphDepth": 2,
    "MinLargeTransferUsd": 100000
  }
}
```

这些只是默认值。

全部需要可配置。

---

# 128. Worker

最终规划：

```text
SymbolSyncWorker

BinanceWebSocketWorker

TickerSnapshotWorker

OpenInterestWorker

DeepDerivativeWorker

MarketCapWorker

FeatureWorker

VolatilityScannerWorker

SpeculativeScannerWorker

MarketRegimeWorker

OnChainWorker

WalletTrackingWorker

SignalWorker

EpisodeWorker

StrategyWorker

PersistenceWorker

AlertWorker

AggregationWorker

EvaluationWorker
```

---

# 129. 初始调度

建议：

```text
Binance WebSocket     实时

Symbol Sync           每天

1m Runtime Snapshot   每分钟

OI                    5分钟

Deep Derivatives      5分钟

CoinGecko             30分钟

Volatility Scan       1分钟

Speculative Scan      5分钟

Watch Strategy        1分钟

Watch OnChain         30~60分钟

Candidate OnChain     6小时

Wallet                5~15分钟

Evaluation            每小时

Aggregation           每天
```

后续根据 API 配额调整。

---

# 130. Event Bus

内部：

```text
System.Threading.Channels
```

定义：

```text
MarketUpdatedEvent

DerivativeUpdatedEvent

OnChainUpdatedEvent

WalletTransferEvent

SignalCreatedEvent

EpisodeChangedEvent

StrategyChangedEvent
```

禁止：

```text
Collector
```

直接调用：

```text
UI

Scanner

Database
```

---

# 131. REST API

规划：

```text
GET /api/assets

GET /api/assets/{id}

GET /api/assets/{id}/history

GET /api/assets/{id}/derivatives

GET /api/assets/{id}/onchain

GET /api/assets/{id}/wallets

GET /api/assets/{id}/signals

GET /api/volatility

GET /api/speculative

GET /api/watchlist

POST /api/watchlist

DELETE /api/watchlist/{id}

GET /api/alerts

GET /api/providers/health

GET /api/settings

PUT /api/settings
```

UI 禁止直接访问 Repository。

---

# 132. 日志

使用：

```text
Microsoft.Extensions.Logging

+

Serilog
```

文件：

```text
Logs/

CryptoRadar-YYYY-MM-DD.log
```

保留：

```text
14~30天
```

禁止日志记录 API Secret。

---

# 133. Backup

每天：

```text
04:30
```

备份：

```text
Backup/

CryptoRadar-YYYYMMDD.db
```

保留：

```text
7~14份
```

必须使用正确的 SQLite Backup API。

不要简单复制正在运行的 WAL 数据库。

---

# 134. Windows 发布

发布：

```text
win-x64

SelfContained
```

目录：

```text
CryptoRadar/

CryptoRadar.exe

appsettings.json

Data/

Logs/

Backup/
```

---

# 135. 开机启动

后续：

```text
Windows Task Scheduler
```

提供设置：

```text
开机自动启动
```

无需安装 Windows Service。

---

# 136. UI风格

建议：

```text
Dark Dashboard
```

参考风格：

```text
TradingView

Coinglass

Arkham
```

但第一版不要做复杂视觉。

颜色只表达：

```text
Positive

Warning

High

Critical

Unknown
```

上涨颜色允许设置：

```text
中国习惯：

红涨绿跌

国际习惯：

绿涨红跌
```

---

# 137. 第一版明确不做

```text
自动交易

账户余额

下单

AI价格预测

高频订单簿

毫秒交易

全链节点

Ethereum Full Node

BSC Full Node

社交媒体情绪分析

新闻AI

几十条链

复杂机器学习

移动App
```

---

# 138. 开发阶段

项目必须严格分阶段。

不要一次完成所有模块。

---

# Phase 0：基础工程

目标：

```text
项目可以稳定启动
```

实现：

```text
Solution

DI

Configuration

Logging

SQLite

WAL

Repository

RuntimeCache

Blazor基础页面

Provider Health
```

验收：

```text
dotnet build

成功

dotnet test

成功

CryptoRadar.exe

成功启动

localhost正常访问
```

---

# Phase 1：Binance基础市场数据

实现：

```text
ExchangeInfo

USDT PERPETUAL Symbol

Ticker

MarkPrice

Funding

1m KLine

WebSocket reconnect
```

UI：

```text
Symbol

Price

1m

5m

15m

1h

24h

Volume
```

验收：

```text
Binance全部USDT永续正常显示

实时价格持续更新

断网后自动重连

WebSocket数据不每秒写SQLite
```

---

# Phase 2：Feature Engine

实现：

```text
RollingWindow

Return1m

Return5m

Return15m

Return1h

Return4h

Return24h

Median

MAD

RobustZ

Percentile

VolumeRobustZ

PriceRobustZ

BreakoutDistance
```

UI 增加：

```text
PriceRobustZ

VolumeRobustZ

Percentile
```

这是第一阶段最重要的地基。

---

# Phase 3：Volatility Radar V1

实现：

```text
UpMoveDetector

DownMoveDetector

MarketRegimeDetector

EpisodeEngine
```

使用：

```text
价格

Volume

技术结构
```

先形成第一版异常行情发现。

暂时不依赖 OI 深度数据。

---

# Phase 4：Alert Policy

实现：

```text
S/A/B/C

DailyBudget

TopN

EpisodeDedup

MarketWideDedup

Windows Toast
```

此时正式开始：

```text
少量通知
```

---

# Phase 5：Derivatives 深度数据

实现：

```text
OpenInterest

OI History

OI Change

Global LongShort

Top Account

Top Position

Taker BuySell

Liquidation
```

升级：

```text
UpMoveDetector V2

DownMoveDetector V2
```

加入：

```text
Price VS OI

OI RobustZ

Taker

Liquidation
```

---

# Phase 6：Replay + Evaluation

实现：

```text
ReplayEngine

SignalEvaluation

MFE

MAE

Hit20

Hit50

Drop20

Drop30

CaptureLead

CrashLead

Precision@K

ExtremeMoveRecall
```

开始真正评估：

```text
信号到底准不准
```

---

# Phase 7：CoinGecko + Asset Mapping

实现：

```text
CoinGecko

MarketCap

FDV

Supply

AssetMapping

Manual Mapping
```

完成：

```text
OI / MarketCap

Volume / MarketCap
```

---

# Phase 8：Speculative Radar V1

实现：

```text
SpeculativeAnomalyScore

SpeculativeRiskScore
```

开始真正寻找：

```text
低市值

高OI

高成交

异常资金
```

候选币。

---

# Phase 9：WatchTower

第一个完整测试币：

```text
ARIA
```

配置：

```text
CostPrice = 0.40
```

实现：

```text
WatchList

StrategyEngine

StrategyState

Risk

Personal Alert
```

能够回答：

```text
距离成本

趋势

当前Stage

当前风险

下一观察点
```

---

# Phase 10：BSC / EVM OnChain

以 ARIA 为第一测试币。

实现：

```text
HolderCount

HolderChange

Top10

Top25

Top50

Top100

Adjusted Concentration

Address Label
```

升级：

```text
Speculative Radar

WatchTower
```

---

# Phase 11：Wallet Engine

人工录入：

```text
Project

Team

Investor

Treasury

Transit
```

实现：

```text
Wallet Balance

Wallet Transfer

Exchange Flow

Project → Unknown

Project → Exchange

Project → Unknown → Exchange
```

---

# Phase 12：Wallet Graph

实现：

```text
2 Hop Graph
```

例如：

```text
Project

↓

Transit

↓

Exchange
```

完成 ARIA 全链路监控。

---

# 139. 第一轮开发范围

现在真正交给桌面端时：

**第一轮只允许做：**

```text
Phase 0

+

Phase 1

+

Phase 2
```

不要实现：

```text
OI策略

妖币评分

Wallet

链上

通知

机器学习

自动策略
```

---

# 140. 第一轮最终界面

只需要：

| Symbol | Price | 1m | 5m | 15m | 1h | 24h | Volume | Price RZ | Volume RZ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|

可以：

```text
排序

筛选

搜索
```

即可。

---

# 141. 第一轮验收重点

不是界面漂亮。

重点验证：

### Binance 数据是否可靠

### WebSocket 是否稳定

### 自动重连是否稳定

### RollingWindow 是否正确

### Return 是否正确

### Robust Z 是否正确

### Percentile 是否正确

### SQLite 是否正常

### Runtime Cache 是否与持久化解耦

### Provider Health 是否有效

---

# 142. 开发规范

必须遵守：

1. 所有时间内部统一 UTC。

2. 金额使用 decimal。

3. 缺失数据不能使用 0。

4. 所有 Provider 必须有接口层。

5. Scanner 不依赖 Binance DTO。

6. Strategy 不依赖具体 Provider。

7. UI 不直接访问 Repository。

8. WebSocket 不直接写 SQLite。

9. SQLite 使用单 PersistenceWorker。

10. SQLite 必须使用 WAL。

11. 数据必须保存 SourceTime。

12. 所有 Signal 必须保存 StrategyVersion。

13. 所有信号必须可以 Replay。

14. 所有策略必须可解释。

15. 风险信号优先于上涨信号。

16. 高分不等于买入。

17. Anomaly 和 Risk 必须分离。

18. Signal 和 Notification 必须分离。

19. 同一次行情必须使用 Episode 聚合。

20. 市场整体行情必须使用 MarketRegime 去重。

21. 第一阶段禁止自动交易。

22. 第一阶段禁止使用 LLM 决策。

23. 不确定的数据必须标 Unknown。

24. Asset Mapping 不明确时不得强行匹配。

25. 项目钱包转账不得直接等同于卖出。

---

# 143. 第一条完整验证链

以后开发到完整阶段时，必须首先跑通：

```text
Binance
↓
ARIA
↓
CoinGecko
↓
BSC
↓
Holder
↓
Project Wallet
↓
Exchange Flow
↓
Signal
↓
Strategy
↓
Alert
```

ARIA 是系统第一条完整测试资产。

原因：

ARIA 同时具备：

```text
Binance Futures

低市值

明显OI

高筹码集中度

项目钱包

中转钱包

历史交易所流入

个人成本价监控
```

很适合作为完整案例。

---

# 144. CryptoRadar 最终成功标准

不是：

```text
支持500个币

界面很好看

几十个指标
```

而是运行一段时间以后能够回答：

```text
过去30天：

系统产生多少A/S级上涨信号？

其中多少最终Hit20？

多少Hit50？

最终+50%的币有多少被提前发现？

平均发现时涨幅是多少？

平均CaptureLead是多少？

每日平均发多少条通知？

WatchList重大风险有没有漏掉？
```

例如未来可能看到：

```text
过去30天

A/S级上涨信号：

73


Hit20：

41


Precision：

56.2%


实际Hit50标的：

19


提前捕捉：

14


ExtremeMoveRecall：

73.7%


平均发现涨幅：

+11.4%


最终平均最高涨幅：

+43.8%


平均每日通知：

3.2条
```

到了这一步：

CryptoRadar 才真正具有价值。

---

# 145. 当前正式开发指令

本文件：

**《CryptoRadar 技术实施规格书 v1.0》**

作为当前唯一正式设计依据。

此前所有讨论只作为需求背景。

如果以前讨论与本文件发生冲突：

**以本 v1.0 为准。**

当前项目尚未开始开发。

因此：

```text
当前版本 = v1.0
```

第一轮只实施：

```text
Phase 0

Phase 1

Phase 2
```

完成后必须先汇报：

```text
Solution结构

核心Domain模型

Binance接入实现

SQLite结构

RuntimeCache

RollingWindow

FeatureEngine

Robust Z算法

Percentile算法

第一版UI

单元测试结果
```

验收通过后，再进入：

```text
Phase 3
```

不得提前实现后续阶段。

这份就是**正式 1.0 总方案**。你可以把这一整份直接给桌面端，并在最前面只补一句：

> **“这是项目唯一设计文档，请严格按照 v1.0 实施。现在只做 Phase 0～2，不要提前做后续功能。”**

后面桌面端把 Phase 0～2 做出来后，你把**它的实现结果、项目结构或者代码仓库**给我，我们再按这份 1.0 一阶段一阶段往下验收和调整，不再重新开一套设计。