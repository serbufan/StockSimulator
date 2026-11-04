# 股票仿真 K 线生成器（Stock Simulator）

> **本软件为仿真行情生成工具，所有 K 线为人工模拟数据，非真实行情，仅用于策略回测学习，不构成投资建议。**

纯离线 WPF 桌面工具，不联网、不接券商、不爬行情。目标只有一个：
**产出标准 K 线 CSV，给网格回测（不对称网格）软件直接导入。**

- 技术栈：.NET 9 / WPF / **WPF UI 4.3（Fluent 风格）** / CommunityToolkit.Mvvm 8.4 / ScottPlot.WPF 5.1 / CsvHelper 33.1 / System.Text.Json
- 界面：`FluentWindow`（Mica 背景 + 圆角 + 自绘标题栏）+ 左侧导航 + 卡片式布局，全部颜色取自 Fluent 主题画刷
- 两大模式：**分段参数自动生成** + **ScottPlot 手绘编辑**
- 输出：标准 `Date,Open,High,Low,Close,Volume` + 可选扩展列（成交额、涨跌幅、事件标记、复权因子、滑点）

---

## 1. 快速运行

```powershell
# 运行程序
dotnet run --project src/StockSimulator/StockSimulator.csproj

# 运行单元测试（33 个用例，覆盖引擎/校验/CSV/工程/画布状态机）
dotnet test

# 界面冒烟测试（加载 Fluent 窗口 → 逐页截图 → 模拟手绘交互，输出到 bin/.../shots）
dotnet run --project tools/UiSmokeTest/UiSmokeTest.csproj
```

目录结构：

```
StockSimulator.sln
├─ src/StockSimulator/               应用本体
│  ├─ Core/                          ★ 核心引擎（无 UI 依赖，可单测）
│  │  ├─ KlineGenerator.cs           K线生成引擎（分段/跳空/停牌/除权/涨跌停/量能）
│  │  ├─ DeterministicRandom.cs      确定性随机（同种子=同结果）
│  │  ├─ TradingCalendar.cs          简化交易日历（周末+交易时段）
│  │  ├─ PatternLibrary.cs           形态模板：双顶/双底/头肩顶/头肩底/三角/一字横盘
│  │  ├─ TrendLineBuilder.cs         趋势线 → K线
│  │  ├─ NoiseSmoother.cs            噪声扰动 / 一键平滑
│  │  ├─ MultiPeriodConverter.cs     多周期：聚合 + 日线展开分钟线（布朗桥）
│  │  ├─ VariantGenerator.cs         批量变体生成
│  │  ├─ ConstraintValidator.cs      约束校验 + 一键修复
│  │  └─ KlineStatistics.cs          统计指标（涨跌幅/回撤/年化波动）
│  ├─ Models/                        Kline / SegmentConfig / MarketEvent / Annotation / 工程文档
│  ├─ Services/                      CSV 读写、工程文件、模板、内存联动、对话框
│  ├─ ViewModels/                    Main / Segment / ManualDraw / Batch / Export + Workspace
│  ├─ Controls/KlineCanvasControl    ScottPlot 封装（像素↔坐标、渲染、鼠标交互）
│  ├─ Converters/                    画刷/枚举/可见性转换器
│  └─ Views/                         4 个页面视图（XAML）
├─ tests/StockSimulator.Tests/       xUnit 单元测试
├─ tools/UiSmokeTest/                界面冒烟测试（加载窗口 + 截图 + 模拟手绘）
├─ tools/SampleGen/                  辅助工具：重新生成 samples/ 下的样例 CSV
└─ samples/
   ├─ sample_daily_ohlcv.csv         日线样例（含跳空/停牌/除权/滑点列）
   └─ sample_30min_ohlcv.csv         由日线展开的 30 分钟样例
```

---

## 2. 界面与功能对照

界面基于 **WPF UI 4.3** 的 Fluent 设计语言：

- `ui:FluentWindow` + `ui:TitleBar`：Mica 背景、圆角窗口、自绘标题栏
- 左侧 `ListBox` 导航（`SymbolIcon` 图标 + 标题/副标题），底部固定显示标的、K线数与周期
- 内容区四个视图常驻内存，按索引切换可见性（切页不丢失 ScottPlot 画布缩放与选中状态）
- 所有卡片为 Fluent 卡片（圆角 8、半透明填充、细描边），按钮用 `ui:Button` 的 `Appearance`
  （Primary / Danger）与 `SymbolIcon` 图标，颜色统一取 `{DynamicResource ...Brush}` 主题画刷
- 底部常驻免责声明条（Caution 配色）

四个页面：

### 页面1 · 分段参数（自动生成）

| 能力 | 说明 |
|---|---|
| 多阶段时间轴 | 每个阶段独立配置：K线数、**趋势漂移**、**波动率**、单根涨跌幅上限、**涨跌停**、**跳空概率/幅度**、**成交量系数**、**阳线偏好**、**均值回归**、放量/地量概率 |
| 时间轴可视化 | 按阶段K线数等比绘制色块，悬停显示该阶段全部参数与序号区间 |
| 突发事件 | 跳空利好/利空、停牌（可设根数）、除权除息（每股现金）、送股拆股（缩股比例） |
| 快速预设 | 上升/下跌/箱体/缩量横盘/恐慌急跌 一键套用 |
| 滑点分段 | 按K线序号区间设置滑点（bp + 跳数），随CSV导出 |
| A股规则 | 涨跌停开关与档位（±5%/±10%/±20%/自定义）、最小报价 0.01、价格取整 |
| 生成 & 校验 | 生成后立即跑约束校验，问题按「错误/警告/提示」列表展示，支持一键修复 |

### 页面2 · 手绘 K 线编辑（ScottPlot）

| 能力 | 说明 |
|---|---|
| 选择/拖拽 | 点选K线；拖拽实体改**收盘价**，拖拽上/下影线改**最高/最低价**（自动受涨跌停约束） |
| 单K绘制 | 按住拖拽确定实体，松开后覆盖当前槽位或**在末尾新增一根**（时间自动顺延） |
| 趋势线生成 | 拉一条直线，按对数路径 + 噪声自动生成一串 K 线 |
| 形态模板 | 双顶/双底/头肩顶/头肩底/三角整理/一字横盘，从选中位置插入 |
| 橡皮擦 | 点击删除单根K线，标注索引自动平移 |
| 成交量笔 | 在K线上上下拖拽直接画成交量柱（下部 20% 为量能区） |
| 撤销/重做 | 基于快照的撤销栈（60 步），任何会改数据的操作都可撤销 |
| 吸附 | 最小报价吸附（0.01）+ 自定义网格吸附 |
| OHLC 精细编辑 | 右侧面板可单独修改 开/高/低/收/量 |
| 导入真实行情 | 读入外部 CSV 后即可在真实走势上二次手绘 |
| 加工 | 一键平滑、叠加噪声、校验、一键修复、清空 |
| 标注 | 在指定K线上加文字标记（颜色可选，可定位/删除） |

### 页面3 · 批量生成 · 变体管理

- 基于「捕获基础行情」批量派生 N 份带随机扰动的K线（价格扰动σ、量能扰动σ、趋势偏移、随机注入跳空、种子步长）
- 每份变体独立种子 → 完全可复现
- 表格显示每份变体的涨跌幅/最大回撤/年化波动/均量，可「应用到工作区」继续手绘，或「导出全部到目录」（N 个 CSV + `index.csv` 统计汇总）

### 页面4 · 导出 · 设置 · 模板

- 标准 CSV 导出（可选列开关、价格小数位、日线只输出日期），导出前自动校验，有错误会二次确认
- 工程文件保存/打开（`.ksproj.json`：分段+事件+手绘K线+标注+滑点+快照全量还原）
- 行情模板管理（保存/套用/删除，存于 `%AppData%\StockSimulator\Templates`）
- 多周期转换：分钟→日/周/月（聚合）、日线→分钟（布朗桥展开）
- 快照：存快照、与当前行情对比（9 项指标差异表）、载入、导出
- **一键发送到网格回测**：内存传递，不写文件

---

## 3. 核心引擎算法（`Core/KlineGenerator.cs`）

逐根K线生成，全部使用确定性随机流（每个阶段独立种子，改前一段不影响后一段）：

```
1. 突发事件处理（停牌 / 除权除息 / 送股）
2. 涨跌停基准：limitUp = round(前收 × (1+幅度))，除权日以除权价为基准（符合A股规则）
3. 跳空：gap = volatility × gapScale × N(0,1)，事件跳空则强制为给定幅度
4. 开盘：open = 前收 × (1 + gap)，夹在 [跌停价, 涨停价]
5. 实体：ret = drift + volatility·z + bullBias·volatility·0.5 + 均值回归项
         close = open × exp(ret)，先按 maxChangePct 软约束，再按涨跌停硬约束
6. 影线：high/low 按 volatility × wickFactor 的正态绝对值扩展，同样受涨跌停约束
7. 取整：全部价格 round 到最小报价单位（0.01），并修正 OHLC 一致性
8. 成交量：基准量 × 阶段系数 × 波动放大 × 对数正态噪声 × (放量3~7倍 | 地量12%~37%) × 一字板缩量
9. 标记：U 涨停 / D 跌停 / G 跳空 / S 停牌 / X 除权，可组合（如 "XG"）
```

保证的不变量（单元测试覆盖）：

- 同一种子 + 同一参数 → **逐字段完全一致**的K线
- `Low ≤ min(Open,Close) ≤ max(Open,Close) ≤ High`、价格 > 0、成交量为 100 的整数倍
- 任何价格（含 high/low/close）不超过涨跌停价
- 停牌K线：`O=H=L=C=前收`，成交量为 0，标记 `S`

---

## 4. 导出 CSV 样例格式

文件：`samples/sample_daily_ohlcv.csv`（真实生成，70 根日线，含跳空/停牌/除权/滑点）

```csv
Date,Open,High,Low,Close,Volume,Amount,ChangePct,Flag,AdjFactor,SlippageBps,SlippageTicks
2024-01-02,12.8,12.81,12.66,12.77,3900700,49772932,-0.2344,,1,5,1
2024-01-03,12.77,12.94,12.74,12.85,4437600,56912220,0.6265,,1,5,1
2024-03-22,16.78,16.78,16.78,16.78,0,0,0,S,1,12,2
2024-03-25,16.78,16.78,16.78,16.78,0,0,0,S,1,12,2
2024-04-01,15.82,16.26,15.74,16.19,13368000,213921420,-0.2465,G,1,12,2
2024-04-02,15.99,16.62,15.7,16.62,19019500,308734033.75,2.656,X,0.987647,12,2
```

| 列 | 是否固定 | 说明 |
|---|---|---|
| `Date` | ✅ 必选 | 日线 `yyyy-MM-dd`，分钟线 `yyyy-MM-dd HH:mm`（该K线**开始时间**） |
| `Open/High/Low/Close` | ✅ 必选 | 已按最小报价单位取整 |
| `Volume` | ✅ 必选 | 成交量（股），100 的整数倍 |
| `Amount` | 可选 | 成交额（元） |
| `ChangePct` | 可选 | 相对**前收盘**的涨跌幅，单位 **%**（`2.656` = +2.656%） |
| `Flag` | 可选 | `S` 停牌 / `U` 涨停 / `D` 跌停 / `G` 跳空 / `X` 除权，可组合 |
| `AdjFactor` | 可选 | 复权因子，除权当日开始累乘（价格为**不复权**原始价） |
| `SlippageBps` | 可选 | 该根K线生效的滑点（基点，1bp=0.01%），来自「分段参数」页的滑点分段 |
| `SlippageTicks` | 可选 | 同一滑点的「最小报价单位跳数」表达，二选一使用 |

- 关闭插件列后即最朴素的 6 列 OHLCV，几乎所有回测软件都能读。
- 编码 UTF-8 with BOM（Excel 双击不乱码）；分隔符默认逗号。
- 导入支持中英文表头（`日期/开盘价/最高价/最低价/收盘价/成交量` 或 `Date/Open/High/Low/Close/Volume`）、
  逗号/分号/Tab 分隔、`yyyyMMdd`、`yyyy/MM/dd HH:mm`、Unix 时间戳等，自动排序去重。

`index.csv`（批量导出时生成）：`File,Name,Bars,ChangePct,MaxDrawdown,AnnualVolatility,AvgVolume,LastClose`

---

## 5. 工程文件（`.ksproj.json`）结构

```jsonc
{
  "FormatVersion": "1.0",
  "App": "StockSimulator",
  "Rules":      { "Symbol": "SIM001", "EnablePriceLimit": true, "LimitMode": "Percent10", "TickSize": 0.01 },
  "Settings":   { "StartPrice": 12.8, "StartDate": "2024-01-02T00:00:00", "Period": "Day", "BaseVolume": 3000000, "Seed": 20240102 },
  "Segments":   [ { "Name": "1-缩量筑底", "BarCount": 20, "Drift": 0.0005, "Volatility": 0.008,
                    "Events": [ { "Type": "GapUp", "AtBarIndex": 10, "Magnitude": 0.04 } ] } ],
  "Bars":       [ { "Time": "2024-01-02T00:00:00", "Open": 12.8, "High": 12.81, "Low": 12.66, "Close": 12.77,
                    "Volume": 3900700, "Amount": 49772932, "Flag": "", "AdjFactor": 1 } ],
  "Annotations":[ { "BarIndex": 12, "Text": "突破位", "ColorHex": "#FFB300" } ],
  "Slippages":  [ { "Name": "前段", "StartIndex": 0, "EndIndex": 49, "SlippageBps": 5, "SlippageTicks": 1 } ],
  "Snapshots":  [],
  "DataSource": "Manual"
}
```

---

## 6. 与网格回测程序联动（内存传递，无需文件）

本程序内置通道 `Services/KlineHandoff.cs`。网格回测端只需在启动时注册一个接收器：

```csharp
// 在网格回测程序初始化时
public sealed class MyGridBacktestReceiver : IGridBacktestReceiver
{
    public string Name => "不对称网格回测";

    public string Receive(KlineHandoffPayload payload)
    {
        // payload.Bars      : IReadOnlyList<Kline>（内存引用，未序列化）
        // payload.Rules     : A股规则（涨跌停/最小报价）
        // payload.Slippages : 滑点分段
        foreach (var bar in payload.Bars) { /* 逐根喂给网格引擎 */ }
        return $"已接收 {payload.Bars.Count} 根K线";
    }
}

KlineHandoff.Receiver = new MyGridBacktestReceiver();
// 也可以订阅事件做增量处理：
KlineHandoff.HandoffRequested += payload => { /* ... */ };
```

在「导出设置」页点「发送到网格回测」即触发；程序默认注册了一个
`DemoGridBacktestReceiver`（统计区间并推算网格线数量），用于验证通道是否打通。

---

## 7. 已知限制（有意为之的简化）

- 交易日历只跳过周末，**不含法定节假日**（可通过 `TradingCalendar.Holidays` 注入）。
- 日线在 X 轴上按真实日期排列，因此周末会在图上形成 3 天宽的空隙（与多数国内行情软件的连续K线不同）。
- 除权除息只做**不复权**价格缺口 + `AdjFactor` 记录，不做前复权/后复权序列计算。
- 手绘编辑作用于「已有时间轴」：覆盖当前槽位或追加到末尾，不会把后续K线整体后移。
- 单张画布超过 4000 根K线时不再绘制量柱以保证流畅。

---

## 8. 单元测试覆盖

`dotnet test` → 33 个用例全部通过：

| 测试类 | 覆盖内容 |
|---|---|
| `EngineTests` | 生成确定性、不同种子差异、涨跌停/tick/OHLC 不变量、成交量步长、停牌、除权除息、跳空、校验器、修复器、统计指标 |
| `ConversionTests` | 日线展开分钟线（区间/量能/时间递增）、分钟聚合回日线、批量变体、平滑降波动、噪声后仍合法、形态模板、趋势线 |
| `IoTests` | CSV 导出导入往返、中文表头导入、错误表头报错、工程文件往返、联动通道送达 |
| `CanvasEditingTests` | 选择拖拽改收盘（且**通知期间不出现非法中间态**）、撤销还原、单K末尾追加、覆盖已有K、趋势线生成、橡皮擦、成交量笔、形态插入、吸附、撤销重做 |
