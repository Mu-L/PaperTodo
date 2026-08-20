# PaperTodo 架构

> 本文只描述 **PaperTodo 当前实际架构**。它不是历史记录，也不是未来设计稿。
>
> - 代码和可观察运行行为是最终事实；本文与代码不一致时，应先核对代码并在同一变更中修正文档。
> - 已采用、已否决以及曾经踩过的架构路线记录在 [`DECISIONS.md`](DECISIONS.md)。
> - Agent 必须遵守、但仅靠通读代码不容易发现的硬约束记录在 [`AGENTS.md`](AGENTS.md)。

## 1. 系统形态

PaperTodo 是一个 Windows 桌面“纸片”应用。当前主程序是单进程、单实例的 .NET 10 WPF 应用：

- 目标框架：`net10.0-windows10.0.17763.0`。
- UI：WPF；Windows Forms 只作为兼容依赖，不是主 UI 框架。
- 进程 DPI 策略：`PerMonitorV2,PerMonitor`。
- 每张纸片的主要 UI 对象是一个 `PaperWindow`。
- `AppController` 是应用级协调器，持有全局 `AppState`、纸片窗口集合、持久化、托盘、快捷键、显示器/全屏策略、提醒、插件运行时，以及边缘胶囊的跨窗口协调状态。
- `data.json` 保存结构化业务状态；图片二进制由 `NoteImageStore` 的 LMDB 存储独立管理。
- 主程序支持内置 Markdown body，以及本地 Native / Web paper-body 插件。
- 边缘胶囊不是 `PaperWindow` 的一个简单视觉样式，而是一套由单纸片状态机、队列级会话、bounded docked host、合成平移代理和 floating drag surface 共同组成的子系统。

高层关系：

```text
App
 └─ AppController
     ├─ AppState / StateStore
     ├─ NoteImageStore (LMDB)
     ├─ PaperBodyPluginRegistry
     ├─ PaperWindow[paperId]
     │   ├─ normal / expanded paper surface
     │   ├─ PaperBodyHost
     │   └─ EdgeCapsulePresenter + EdgeCapsuleHost
     ├─ MasterCapsuleWindow[queue]
     ├─ EdgeCapsuleDragWindow (process-global pooled host)
     ├─ tray / hotkeys / reminders / fullscreen / virtual desktop runtime
     └─ edge queue coordination / preview session / visual transactions /
        DirectComposition proxy lifecycle
```

## 2. 主要 ownership

| 领域 | 当前 authority | 负责 | 边界外 |
| --- | --- | --- | --- |
| 应用生命周期 | `App` | 启动模式、单实例入口、全局异常边界、创建 `AppController` | 纸片业务状态和窗口间业务协调 |
| 应用级业务协调 | `AppController` | `AppState`、窗口集合、保存节流、托盘、全局运行时、跨纸片协调 | 在各子系统之外再复制一套局部状态机 |
| 持久化协议 | `StateStore` | `data.json` / backup 的加载、恢复、序列化和版本化写入 | 在保存阶段重建业务不变量 |
| 单纸片 UI | `PaperWindow` | 纸片 WPF surface、纸片交互、provider 选择，以及和 controller / edge runtime 的适配 | 重新推导队列索引、复制 edge reducer 状态 |
| paper body session | `PaperBodyHost` | 一张纸当前 body session 的 attach / invoke / commit / dispose 边界 | WPF 放置和 provider 选择 |
| 插件发现 | `PaperBodyPluginRegistry` | builtin / native / web provider 发现、校验和激活 | 单纸片 session 生命周期 |
| edge 单纸片业务状态 | `EdgeCapsuleReducer` + `EdgeCapsuleModel` | typed intent 到 Slot / Visual / Gesture / Preview / Placement 的原子状态变化 | 队列级 owner 选择、WPF/HWND 副作用 |
| edge 队列级会话与事务 | `AppController` 的 edge partial modules | preview owner/corridor、跨纸片 arrange、visual transaction、proxy 路由与生命周期 | 维护第二份单纸片 desired model |
| edge 单纸片呈现 | `EdgeCapsulePresenter` | desired model、target plan、transition、applied frame、dirty/deferred work | 队列成员排序和 preview owner 选择 |
| edge 几何 | `EdgeCapsuleGeometry` | DIP / monitor / edge 到物理像素矩形的纯计算 | 从邻居窗口猜队列位置 |
| edge 队列位置 | `EdgeCapsuleQueueCoordinator` | index、master offset、slot count | HWND / WPF 呈现 |
| 队列主胶囊 | `MasterCapsuleWindow` + `AppController._masterCapsules` | 每队列 slot 0、数量/收起状态、纵向队列锚点交互 | 单纸片 presenter 和 preview 内容 |
| docked edge surface | `EdgeCapsuleHost` | 每纸片 docked HWND 和完整 WPF visual tree；唯一 `Apply(frame)` 副作用入口 | `FloatingFree` 外形 |
| 同队列平移动画 | `EdgeCapsuleQueueCompositionProxy` | 同尺寸 live HWND surface 的 X/Y DirectComposition translation 与视觉 authority handoff | resize、clip、scale、snapshot 或业务状态 |
| 跨边/跨队列拖拽 | `EdgeCapsuleDragWindow` | 独立完整 floating pill HWND；Windows native drag / docking cover | 复用 docked host 的单边外形 |
| 同 Dispatcher 动画节拍 | `EdgeCapsuleFrameScheduler` | 每帧统一时间/指针采样、按队列 native batch 推进、liveness rescue | 成为第二套 transition owner |

## 3. 启动与进程生命周期

### 3.1 启动入口

`App.OnStartup` 先区分 MCP bridge 模式和正常 GUI 模式。

正常 GUI 模式：

1. 在 WPF 主 UI 完整建立前应用已持久化的界面语言。
2. 注册 Dispatcher / AppDomain 全局异常处理。
3. 通过 `SingleInstanceHelper` 获取主实例；次实例只把启动参数发给主实例后退出。
4. 主实例尽早启动单实例命令监听；`AppController` 尚未完成启动时收到的命令先排队。
5. 创建 `AppController`，再进入 `StartAsync`。
6. 进程使用 `ShutdownMode.OnExplicitShutdown`，关闭普通纸片窗口不等于退出应用。

全局崩溃路径只写 crash log，不在异常边界强行把可能已经不一致的内存态重新序列化；正常 durability 由自动保存和 `data.backup.json` 承担。

### 3.2 `AppController` 启动

`AppController` 构造阶段首先加载 `AppState`，然后初始化主题、图片库、字体、插件 registry 和保存计时器。

`StartAsync` 负责建立托盘、全局快捷键、全屏规避、提醒、实验性窗口/虚拟桌面运行时，并恢复需要显示的纸片。

启动恢复有一个重要顺序：需要恢复为 edge capsule 的纸片先建立/预热其 edge host 并完成一次队列 arrange，再继续构造完整纸片 shell。这样 docked surface 可以先到 compositor，而不是被完整纸片 shell 的冷启动工作阻塞。

## 4. 状态与持久化

### 4.1 状态模型

`AppState` 是全局持久化根对象；`PaperData` 是单纸片持久化模型；Todo 行使用 `PaperItem`。

`PaperData` 的普通窗口几何：

- `X`
- `Y`
- `Width`
- `Height`

与 edge capsule 的队列/展开恢复信息是两套语义：

- `CapsuleSide`
- `CapsuleMonitorDeviceName`
- `DeepCapsuleExpandedX/Y/Width/Height`
- `DeepCapsuleExpandedSide`
- `DeepCapsuleExpandedMonitorDeviceName`

Deep capsule 使用独立 slot host 时，隐藏或 parked 的主 `PaperWindow` 不能把自己的临时位置写回普通纸片 `X/Y`。

### 4.2 `StateStore`

主文件是应用目录下的 `data.json`，备份是 `data.backup.json`。

加载策略是保守恢复：

1. 优先读取主文件。
2. 主文件不可用时尝试 backup。
3. 当主文件**存在但读取/解析失败**、随后从 backup 成功恢复时，下一次成功保存前先保留失败主文件和本次恢复所用 backup 的独立副本，避免正常保存覆盖唯一的故障证据和恢复源。
4. 未知旧字段允许被忽略，以兼容已经退休的实验字段。

保存策略：

- `AppController` 默认在最后一次变更约 1 秒后 idle save；持续编辑时另有约 10 秒 force cap。
- 保存前先把仍在编辑器/插件 session 中的待提交内容写回内存模型，再在 UI 线程同步序列化成一份 JSON 字符串快照。
- `StateStore` 使用单写锁和递增版本；旧版本写入不得覆盖新版本。
- 实际写盘先写 `.tmp`，正常情况下轮换 backup，再替换主文件。
- `PrepareForSave` 只修复会让序列化失败的值（null collection、非有限数字等），不在保存阶段重新解释链接、队列或其他业务语义。

### 4.3 图片资产

Markdown/Note 的图片二进制不嵌入 `data.json`，由 `NoteImageStore` / LMDB 管理。

图片回收是破坏性操作，因此其 reachability 判断采用 fail-closed：只有当前状态和需要保护的 recovery snapshots 都能可靠读取、并能完整收集图片引用时才允许 GC / id reuse；任一保护扫描不可信就禁用本轮回收，而不是猜测“未引用”。

## 5. Paper surface 与 paper-body 插件

### 5.1 `PaperWindow`

`PaperWindow` 是单纸片的 WPF UI owner。它仍承载普通 expanded/collapsed paper surface、Todo/Note 交互、标题/工具栏、纸片级窗口行为，以及和 edge subsystem 的适配入口。

边缘胶囊建立以后，`PaperWindow` 本身不是唯一可见 HWND：一张纸可能拥有普通 `PaperWindow`，也可能由单独的 `EdgeCapsuleHost` 提供 docked surface；跨队列拖拽时还可能临时租用进程级 `EdgeCapsuleDragWindow`。主胶囊则是每个队列独立的 `MasterCapsuleWindow`，不属于任何单张纸。

### 5.2 paper-body provider

当前 provider 类型：

- Built-in：内置 Markdown。
- Native plugin：完全信任、非沙箱化的本地 .NET/WPF 插件。
- Web plugin：本地 Web 内容，通过宿主 WebView2 能力运行。

`PaperBodyPluginRegistry` 扫描 `plugins/<plugin-id>/plugin.json`，当前支持/最低 API 版本均为 `1.8`。

Native plugin 已加载到 CLR 后不能安全地热替换，因此磁盘变化需要重启才能切到新版本；Web plugin 可以在进程内重新扫描/加载。

每张纸的实际 provider session 由 `PaperBodyHost` 隔离：它拥有当前 `IPaperBodySession` 的 attach、调用异常边界、commit/cancel/dispose；`PaperWindow` 仍拥有 provider 选择和 WPF 放置。

## 6. Edge Capsule V3 Lite

Edge Capsule 当前有两个不同层级的状态：

- **单纸片层**：Reducer / Model / Presenter 决定一张纸当前处于什么 slot、visual、gesture、preview 和 presentation。
- **队列层**：`AppController` 协调 preview owner、transfer corridor、队列 arrange、跨纸片 visual transaction、successor 和 proxy 生命周期。

队列层可以向多张纸 dispatch intent、捕获起终帧并组织一次事务，但不持有第二份单纸片 desired model。

### 6.1 单纸片状态与呈现主链路

```text
OS / WPF / controller event
        ↓
EdgeCapsuleIntent
        ↓
EdgeCapsuleReducer
        ↓
EdgeCapsuleModel
        ↓
EdgeCapsuleTargetPlanner + layout snapshot
        ↓
EdgeCapsulePresentationPlan
        ↓
EdgeCapsulePresenter transition / reconcile
        ↓
EdgeCapsulePresentationFrame
        ↓
EdgeCapsuleHost.Apply(frame)
```

`EdgeCapsuleModel` 把几个互相正交但有约束的维度放在一个不可变模型中：

- Slot：`CollapsedDocked`、`ExpandedReserved`、retracted/retracting 等。
- Visual：`Resting`、`Hovered`、`Active`。
- Gesture：点击、docked reorder、floating transfer/reorder、docking handoff/reveal 等。
- Preview：open/closed。
- Queue placement、drag session、context-menu、pointer-over 等补充事实。

会改变单纸片模型的产品事件通过 typed `EdgeCapsuleIntent` 进入 reducer；`EdgeCapsuleReducer` 原子地产生新模型，并在每次 accepted reduction 后检查结构不变量。`PaperWindow` 和 controller 不维护第二套单纸片 edge 状态机。

### 6.2 队列、placement 与 master

队列身份由显示器和边共同确定。`AppController` 决定哪些纸属于哪个队列；`EdgeCapsuleQueueCoordinator` 是以下信息的唯一计算 authority：

- per-queue index
- master visual offset
- slot count

`EdgeCapsulePlacement` 由 coordinator 传给 presenter；单个 presenter 不根据邻居窗口反推自己的 index。

开启 collapse-all master 时，每个有成员的队列拥有一个独立 `MasterCapsuleWindow`：

- master 固定占 slot 0，真实纸片从后续 slot 开始。
- master 只拥有自己的 pill、数量/active presentation 和纵向队列锚点手势。
- 真实纸片的 retract/release 仍由 controller dispatch 到各自 presenter。

队列始终按完整成员顺序连续排列，不按工作区高度分页、截断或隐藏 overflow。

### 6.3 物理几何

`EdgeCapsuleGeometry` 是 docked edge capsule 的唯一物理像素计算器。输入是 monitor geometry、edge、DIP 尺寸/offset，输出 wall-pinned `DeviceScreenRect` 和 `InteractiveBounds`。

重要区别：

- `Bounds`：当前用户真正看见的 capsule rectangle。
- `HostBounds`：该纸片 bounded docked HWND 的实际 native capacity。
- `InteractiveBounds`：当前真实输入区域，排除透明 chrome / capacity。

`HostBounds` 可以大于 `Bounds`，但两者固定在同一屏幕墙边；可见形状不会越出 host capacity，透明 capacity 不参与输入。

### 6.4 Bounded live host

每张 docked capsule 由独立 `EdgeCapsuleHost` 拥有真实 HWND 和完整 WPF visual tree。

V3 Lite 的 host 是 **bounded live host**：

- capacity 容纳这张纸在当前 monitor/DPI/edge 上的最大合法 preview，而不是工作区或整条队列。
- host generation 内 capacity 可以因真实 late-bound preview 需求增长，但正常交互中不随 Resting/Hover/Preview 来回缩放。
- Resting / Hovered / Active / Preview 的宽高、圆角、正文、关闭区和 opacity 都在这个稳定 host 内由 WPF visual 改变。
- `EdgeCapsuleHost.Apply(frame)` 是 per-paper docked surface 的唯一 presentation effect entry。

Native monitor/edge/DPI handoff 时，host 先把真实窗口设为不可见，移动 HWND 并验证目标 metrics，再应用目标 edge 的 visual layout，最后恢复可见性，避免目标 visual tree 在旧墙上短暂出现。

### 6.5 Presenter 与 frame scheduler

`EdgeCapsulePresenter` 是单纸片 presentation authority：

- desired `EdgeCapsuleModel`
- target plan
- active transition
- applied presentation
- dirty/deferred work
- transient native apply retry

标题测量、display metrics、pointer、presentation、frame 都回到同一个 reconcile 管线；同步 flush 也走同一条逻辑，而不是绕过 presenter 直接 planner/apply。

同一 UI Dispatcher 上的 presenter 共用一个 `EdgeCapsuleFrameScheduler`。正常 transition 使用 `CompositionTarget.Rendering`：

- 每一帧只采样一次物理指针和时间。
- presenter 仍各自拥有 transition。
- scheduler 按 native batch group / monitor-edge queue 推进和提交，某个坏 HWND 不阻断无关队列。

scheduler 有一个 demand-driven liveness watchdog：只有仍有 active transition 且正常 Rendering callback 没及时推进时才补一次 frame；它不是第二个长期 frame clock。

### 6.6 WPF 与 DirectComposition 的职责切分

这是 V3 Lite 最核心的边界：

**WPF / bounded host 负责 shape；DirectComposition 只负责 translation。**

WPF / Presenter 负责：

- Resting / Hover / Active / Preview 的宽高变化
- rounded geometry
- 内容布局和 opacity
- `InteractiveBounds` 等 presentation contract

DirectComposition queue proxy 只负责：

- 从真实 HWND 创建 live surface（`CreateSurfaceFromHwnd`）
- 对保持同一 surface identity / 尺寸的 visual 做 X/Y offset
- 在队列成员发生位置变化时提供 compositor translation
- 在真实 HWND 已被 cover 后，让真实 host 一次落到 endpoint，再完成 visual authority handoff

Production translation backend 当前不包含：

- clip animation
- scale animation
- bitmap snapshot / frozen frame
- effect-based resize
- Reveal/Conceal resize handoff
- deferred resize state machine

需要改变 live surface 物理宽高的 start/target 由 WPF bounded host 完成 shape change，或进入明确的 native fallback / snap 边界，不作为 DComp translation 处理。

当 proxy 拥有可见像素时，proxy output HWND 会根据同一份 sampled logical frame 和 `InteractiveBounds` 做命中与输入转发；它不从 output envelope 或透明 surface capacity 发明第二套交互几何。

### 6.7 队列 visual transaction 与 successor

跨纸片的同一轮队列变化由 `AppController.EdgeCapsuleVisualTransaction` 合并。

对于单一 queue：

1. controller 捕获参与成员的 current/target presentation。
2. 如果可以安全建立 queue proxy，就在既有 real HWND live surfaces 上构建 DComp root。
3. cover 发布并 cloak 真实源后，真实 HWND 在 cover 下提交 endpoint。
4. WPF morph 和 DComp translation 使用同一 QPC 起点/时长语义。
5. 动画结束后通过明确的 handoff boundary 解除 cloak、撤掉 proxy root，把视觉 authority 交还给真实 HWND。

如果 active proxy 期间又出现同队列 successor：

- successor 复用同一个 queue output HWND/target。
- predecessor 的当前可见 sample 是 successor 的起点，而不是从旧业务端点重新开始。
- predecessor 仍 cloak 的 live sources 显式转移/继承，stationary peer 不因 root replacement 消失。
- successor 只在现有 output envelope 已覆盖所需区域时接管，避免为了新 generation 移动 target HWND 而连带移动仍可见 predecessor。

跨 queue 的 visual ownership 不合并到一个 DComp target。已有 cover 会先安全结束，然后走既有的 batched native fallback / 对应队列事务。

### 6.8 Visual authority 与失败恢复

队列 proxy 的正确性不是“动画完成就 dispose”，而是 **任何时刻至少有一个可见 authority**：

- 真实 docked HWND，或
- 当前 queue compositor root / cover，或
- floating drag HWND（拖拽事务中）。

DComp root swap 与 DWM cloak/uncloak 通过显式 transaction boundary 协调。若 publish / endpoint / handoff 失败：

- 能 rollback 时恢复 predecessor root。
- cover 已丢失时立即尝试恢复真实 HWND 的 visible authority。
- 即时恢复本身失败时可以保留有界重试，但不能继续把已丢失的 cover 当作可见 authority。
- 不等待普通动画完成时钟来修复 all-hidden 状态。

成功 handoff 后再异步退休旧 COM visual resources；source authority 的转移和资源释放是两个阶段。

### 6.9 Pointer 与 preview

Hover/Preview 不直接相信 WPF `MouseEnter/MouseLeave`。这些事件主要负责唤醒采样；是否真正位于 capsule 上，以当前 **presented/applied physical `InteractiveBounds`** 为准。

Preview 浏览还维护 queue 内真实可交互项之间的 transfer corridor。空白 corridor 可以暂时维持浏览意图，但它不是 capsule hit area，`HostBounds` 或 proxy envelope 不参与交互区。

Preview transfer / leave 的细节由 controller 的 preview session / intent predictor 协调；物理 bounds 是最终事实，预测只帮助在合法 corridor 中判断移动意图，不延长已经物理离开整个区域的会话。

### 6.10 Floating drag / docking

跨队列或脱墙拖拽使用独立 `EdgeCapsuleDragWindow`，而不是把 docked host 变形成自由胶囊：

- 使用自己的对称 floating visual tree。
- 进程级维护一个可复用、长期存活的 pooled HWND/visual tree；当前 controller 序列化 capsule reorder，因此同一时刻只租用一个。
- docked host 在 `FloatingTransfer` / `FloatingReordering` / handoff 阶段进入 suppressed/不可输入状态。
- docking 使用显式 `DockingHandoff` / `DockingReveal` 事务，在真实 docked endpoint 已确认后才撤掉 floating cover。

因此 docked 单边列布局、wall-side close segment、圆角和宽度状态不会泄漏到 floating surface，反之亦然。

## 7. OS 与全局集成

`AppController` 还协调以下横切能力：

- Hardcodet tray icon / context menu。
- 全局快捷键。
- foreground fullscreen 检测与 topmost avoidance。
- display metrics / DPI 改变后的延迟刷新。
- Todo reminders。
- virtual desktop integration。
- 可选窗口 magnetism / tether 等实验运行时。
- MCP bridge / MCP runtime。

这些能力可以影响窗口 visibility、z-order、monitor placement；进入具体 surface 后仍遵循各自 ownership。例如 display metrics 改变通过 edge presenter 的 dirty/reconcile 与 queue transaction 进入 edge subsystem，而不是从全局 watcher 直接改 capsule visual 几何。

## 8. 仓库结构

当前根目录保留项目入口、仓库级说明、资源入口和跨项目文件；主 C# 源码位于 `src/`。

主要目录：

- `src/`：PaperTodo 主程序源码。
- `Resources/`：中文默认资源及英文、日文、韩文本地化 `.resx`。
- `PaperTodo.Plugin.Abstractions/`：paper-body 插件 ABI / 抽象。
- `plugins/`：可直接加载的最终插件产物；普通 publish / Release 不携带。
- `plugin-samples/`：插件源码/示例和构建说明。
- `native/`：PaperTodo 自有 native 组件，例如 LMDB bridge。
- `vendor/`：仓库内固定版本的 vendored dependency / submodule。
- `assets/`：应用图标和静态资源。
- `docs/`：GitHub Pages 站点、站点资源和发布网页文件；不是内部架构文档的默认堆放目录。
- `.github/workflows/`：CI / 发布工作流。

根目录的 `PaperTodo.csproj` 是主项目入口；`App.xaml` / `App.xaml.cs` 保持在根目录。仓库级 `README`、`CHANGELOG`、`AGENTS.md`、`ARCHITECTURE.md` 和 `DECISIONS.md` 也保留在根目录，作为直接入口。
