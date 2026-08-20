# PaperTodo 架构

> 本文只描述 **PaperTodo 当前实际架构**。它不是历史记录，也不是未来设计稿。
>
> - 当前代码和可观察运行行为描述实现事实；若与本文或 [`DECISIONS.md`](DECISIONS.md) 冲突，应结合代码、提交历史和行为重新核对，不默认任一方天然正确。
> - 已采用、已否决以及曾经踩过的架构路线记录在 [`DECISIONS.md`](DECISIONS.md)。
> - Agent 执行时必须遵守的硬约束和文档维护规则记录在 [`AGENTS.md`](AGENTS.md)。

## 1. 系统形态

PaperTodo 是一个 Windows 桌面“纸片”应用。当前主程序是单进程、单实例的 .NET 10 WPF 应用：

- 目标框架：`net10.0-windows10.0.17763.0`。
- UI：WPF；Windows Forms 只作为兼容依赖，不是主 UI 框架。
- 进程 DPI 策略：`PerMonitorV2,PerMonitor`。
- 每张纸片的主要 UI 对象是一个 `PaperWindow`。
- `AppController` 是应用级协调器，持有全局 `AppState`、纸片窗口集合、持久化、托盘、快捷键、显示器/全屏策略、提醒、插件运行时，以及边缘胶囊的跨窗口协调状态。
- `data.json` 保存结构化业务状态；图片二进制由 `NoteImageStore` 的 LMDB 存储独立管理。
- 主程序支持内置 Markdown body，以及本地 Native / Web paper-body 插件。
- 边缘胶囊是一套由单纸片状态机、队列级会话、bounded docked host、合成平移代理和 floating drag surface 共同组成的子系统。

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
| 应用级业务协调 | `AppController` | `AppState`、窗口集合、保存节流、托盘、全局运行时、跨纸片协调 | 在各子系统之外复制第二套局部状态机 |
| 持久化协议 | `StateStore` | `data.json` / backup 的加载、恢复、序列化和版本化写入 | 在保存阶段重建业务不变量 |
| 图片资产 | `NoteImageStore` | LMDB 生命周期、事务串行化、图片编号和缓存/回收 | 外部代码直接开启 LMDB 事务 |
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

需要恢复为 edge capsule 的纸片先建立/预热其 edge host 并完成一次队列 arrange，再继续构造完整纸片 shell，使 docked surface 先进入 compositor，避免被完整纸片 shell 的冷启动工作阻塞。

## 4. 状态、可见性与持久化

### 4.1 状态模型与纸片语义

`AppState` 是全局持久化根对象；`PaperData` 是单纸片持久化模型；Todo 行使用 `PaperItem`。

删除、隐藏、折叠是不同语义：

- 删除：从 `State.Papers` 移除纸片。
- 隐藏：纸片仍保留，只是 `IsVisible = false`，后续可由托盘/命令恢复。
- 折叠：纸片仍是可见纸片，只改变为 capsule presentation。

`PaperItem.LinkedPaperId` 是跨纸片关系的一部分，会影响关联纸片标题刷新、删除/解除关联和“已关联纸片是否进入 capsule 队列”等行为。

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

Deep capsule 使用独立 slot host 时，隐藏或 parked 的主 `PaperWindow` 不把自己的临时位置写回普通纸片 `X/Y`。

### 4.2 `StateStore`

主文件是应用目录下的 `data.json`，备份是 `data.backup.json`。

加载策略是保守恢复：

1. 优先读取主文件。
2. 主文件不可用时尝试 backup。
3. 当主文件存在但读取/解析失败、随后从 backup 成功恢复时，下一次成功保存前先保留失败主文件和本次恢复所用 backup 的独立副本，避免正常保存覆盖唯一的故障证据和恢复源。
4. 未知旧字段允许被忽略，以兼容已经退休的实验字段。

保存策略：

- `AppController` 在变更后做 idle debounce，并对持续编辑设置 force-save 上限。
- 保存前先把仍在编辑器/插件 session 中的待提交内容写回内存模型，再在 UI 线程同步序列化成一份 JSON 字符串快照。
- `StateStore` 使用单写锁和递增版本；旧版本写入不得覆盖新版本。
- 实际写盘先写 `.tmp`，正常情况下轮换 backup，再替换主文件。
- `PrepareForSave` 只修复会让序列化失败的值，不在保存阶段重新解释链接、队列或其他业务语义。
- 正常退出执行同步保存；全局 crash handler 不使用普通保存流程覆盖当前数据。

### 4.3 图片资产与 LMDB

Markdown/Note 的图片二进制不嵌入 `data.json`，由 `NoteImageStore` / LMDB 管理。

当前 LMDB 使用单文件模式，并由 `NoteImageStore` 在进程内统一串行化访问；外部业务代码不拥有独立 LMDB transaction authority。图片回收是破坏性操作，因此 reachability 判断采用 fail-closed：只有当前状态和需要保护的 recovery snapshots 都能可靠读取、并能完整收集图片引用时才允许 GC / id reuse；任一保护扫描不可信就禁用本轮回收。

## 5. Paper surface 与 paper-body 插件

### 5.1 `PaperWindow` 与 Note surface

`PaperWindow` 是单纸片的 WPF UI owner。它承载普通 expanded/collapsed paper surface、Todo/Note 交互、标题/工具栏、纸片级窗口行为，以及和 edge subsystem 的适配入口。

边缘胶囊建立以后，`PaperWindow` 本身不是唯一可见 HWND：一张纸可能拥有普通 `PaperWindow`，也可能由单独的 `EdgeCapsuleHost` 提供 docked surface；跨队列拖拽时还可能临时租用进程级 `EdgeCapsuleDragWindow`。主胶囊则是每个队列独立的 `MasterCapsuleWindow`，不属于任何单张纸。

内置 Note 的编辑态和浏览态共用同一个 `MarkdownTextBox` 与同一份布局/滚动/选区状态，而不是维护两套文本控件。

### 5.2 paper-body provider

当前 provider 类型：

- Built-in：内置 Markdown。
- Native plugin：完全信任、非沙箱化的本地 .NET/WPF 插件。
- Web plugin：本地 Web 内容，通过宿主 WebView2 能力运行。

`PaperBodyPluginRegistry` 扫描 `plugins/<plugin-id>/plugin.json`，当前支持/最低 API 版本由 plugin contract 定义。

Native plugin 已加载到 CLR 后不能安全地热替换，因此磁盘变化需要重启才能切到新版本；Web plugin 可以在进程内重新扫描/加载。

每张纸的实际 provider session 由 `PaperBodyHost` 隔离：它拥有当前 `IPaperBodySession` 的 attach、调用异常边界、commit/cancel/dispose；`PaperWindow` 仍拥有 provider 选择和 WPF 放置。

### 5.3 Edge mini presentation

插件进入 edge preview 时仍由宿主控制窗口、队列和交互 authority，插件只提供可被宿主消费的 capsule/mini presentation 能力。

当前降级链按“能力最强且安全的宿主呈现 → 结构化 capsule → plain text”收敛：

1. 插件显式提供的专属 mini presentation。
2. 明确允许迁移的纯 WPF 正文 View。
3. 插件自定义 capsule presentation 的宿主实时镜像。
4. 标准结构化 capsule presentation。
5. `plainText` 最终回退。

具体 API 版本、尺寸上下限和默认尺寸属于 plugin contract / 当前代码事实，不在架构文档复制。

关键边界：

- 标准 capsule 的自动宽度由宿主按标准组件、间距和模板内边距测量；插件不各自维护字符数估宽算法。
- 可迁移的 Native mini/WPF View 必须是宿主可安全接管的纯 WPF 内容；`Window`、`HwndHost`、WindowsFormsHost、WebView2 或已挂载控件不作为可迁移子树。
- Web `miniEntry` 位于正文 entry 的本地静态内容边界内。宿主先保留结构化 fallback，mini 显式 ready 并经过渲染边界后才替换；失败不会清空 fallback。
- 正文 View 迁移只在 provider 显式声明能力时发生。真实 View 从 mini 归还正文前由宿主使用快照接棒；之后的 preview 复用受控快照刷新，而不是持续采样/复制第二份业务状态。
- 一次 preview 会话冻结外层 mini 尺寸，状态刷新不重新定义整个队列 placement。

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

`EdgeCapsuleModel` 把 Slot、Visual、Gesture、Preview、Placement、drag session、context-menu 和 pointer-over 等互相关联的事实放在一个不可变模型中。会改变单纸片模型的产品事件通过 typed `EdgeCapsuleIntent` 进入 reducer；controller 和 `PaperWindow` 不维护第二套单纸片 edge 状态机。

### 6.2 队列、placement 与 master

队列身份由显示器和边共同确定。`AppController` 决定哪些纸属于哪个队列；`EdgeCapsuleQueueCoordinator` 是 per-queue index、master visual offset 和 slot count 的唯一计算 authority。

开启 collapse-all master 时，每个有成员的队列拥有一个独立 `MasterCapsuleWindow`：master 固定占 slot 0，只拥有自己的 pill、数量/active presentation 和纵向队列锚点手势；真实纸片的 retract/release 仍由 controller dispatch 到各自 presenter。

队列始终按完整成员顺序连续排列，不按工作区高度分页、截断或隐藏 overflow。

### 6.3 物理几何

`EdgeCapsuleGeometry` 是 docked edge capsule 的唯一物理像素计算器。输入是 monitor geometry、edge、DIP 尺寸/offset，输出 wall-pinned `DeviceScreenRect` 和 `InteractiveBounds`。

- `Bounds`：当前用户真正看见的 capsule rectangle。
- `HostBounds`：该纸片 bounded docked HWND 的实际 native capacity。
- `InteractiveBounds`：当前真实输入区域，排除透明 chrome / capacity。

`HostBounds` 可以大于 `Bounds`，但两者固定在同一屏幕墙边；透明 capacity 不参与输入。

### 6.4 Bounded live host

每张 docked capsule 由独立 `EdgeCapsuleHost` 拥有真实 HWND 和完整 WPF visual tree。

V3 Lite 的 host 是 bounded live host：capacity 只覆盖该纸在当前 monitor/DPI/edge 上的最大合法 preview，不扩成工作区或整条队列；host generation 内 capacity 可以因真实 late-bound preview 需求增长，但正常交互中不随 Resting/Hover/Preview 来回缩放。

Resting / Hovered / Active / Preview 的宽高、圆角、正文、关闭区和 opacity 都在稳定 host 内由 WPF visual 改变。`EdgeCapsuleHost.Apply(frame)` 是 per-paper docked surface 的唯一 presentation effect entry。

Native monitor/edge/DPI handoff 时，host 先把真实窗口设为不可见，移动 HWND 并验证目标 metrics，再应用目标 edge visual layout，最后恢复可见性。

### 6.5 Presenter 与 frame scheduler

`EdgeCapsulePresenter` 是单纸片 presentation authority，持有 desired model、target plan、active transition、applied presentation、dirty/deferred work 和 transient native apply retry。

标题测量、display metrics、pointer、presentation、frame 都回到同一个 reconcile 管线；同步 flush 也走同一条逻辑。

同一 UI Dispatcher 上的 presenter 共用一个 `EdgeCapsuleFrameScheduler`。正常 transition 使用 `CompositionTarget.Rendering`；scheduler 每个真实 frame 只采样一次 pointer/time，并按 native batch group / monitor-edge queue 推进。liveness watchdog 只在 Rendering 未及时推进 active transition 时补一次 frame，不成为第二套长期 frame clock。

### 6.6 WPF 与 DirectComposition 的职责切分

V3 Lite 的核心边界是：**WPF / bounded host 负责 shape；DirectComposition 只负责 translation。**

WPF / Presenter 负责可见宽高、rounded geometry、内容布局/opacity 和 `InteractiveBounds` 等 presentation contract。

DirectComposition queue proxy 只负责：

- 从真实 HWND 创建 live surface。
- 对保持同一 surface identity / 尺寸的 visual 做 X/Y offset。
- 在队列成员发生位置变化时提供 compositor translation。
- 在真实 HWND 已被 cover 后，让真实 host 一次 settle 到 endpoint，再完成 visual authority handoff。
- proxy 期间使用同一 sampled logical frame 的 `InteractiveBounds` 做输入命中和转发。

Production translation backend 不拥有 clip/scale/effect/snapshot 或 Reveal/Conceal/deferred-resize 能力。需要改变 live surface 物理宽高的变化由 WPF bounded host 完成，或进入明确的 native fallback / snap 边界。

### 6.7 队列 visual transaction 与 successor

跨纸片的同一轮队列变化由 `AppController.EdgeCapsuleVisualTransaction` 合并。

对于单一 queue：controller 捕获成员 current/target presentation；满足条件时建立 queue proxy；cover 发布并 cloak 真实源后，真实 HWND 在 cover 下提交 endpoint；WPF morph 和 DComp translation 使用同一 QPC 起点/时长语义；动画结束后通过显式 handoff boundary 把视觉 authority 交还真实 HWND。

active proxy 期间的新同队列事务作为 successor generation：复用同一个 queue output HWND/target，从 predecessor 当前呈现 sample 重新基线化，并显式 carry forward 仍被 predecessor 持有的成员和 live source。只有现有 output envelope 已覆盖 successor 所需区域时才直接接管。

跨 queue visual ownership 不合并到一个 DComp target。

### 6.8 Visual authority 与失败恢复

真实 docked HWND、queue compositor root/cover、floating drag HWND 是显式 visual authority。publication、successor、handoff、rollback 任一边界都必须保证至少一个可见 authority 存在。

DComp root swap 与 DWM cloak/uncloak 通过显式 transaction boundary 协调。cover 丢失时立即尝试恢复真实 HWND；即时恢复失败时可以保留有界重试，但不能继续把已丢失的 cover 当作可见 authority。成功 handoff 后再退休旧 COM visual resources，source authority 转移和资源释放是两个阶段。

### 6.9 Pointer 与 preview

Hover/Preview 不直接以 WPF `MouseEnter/MouseLeave` 作为 truth；这些事件主要唤醒采样。是否真正位于 capsule 上，以当前 presented/applied physical `InteractiveBounds` 为准。

Preview 浏览维护 queue 内真实可交互项之间的 transfer corridor。空白 corridor 可以暂时维持浏览意图，但不是 capsule hit area；`HostBounds` 或 proxy envelope 不参与交互区。预测只帮助合法 corridor 中的移动意图判断，不延长已经物理离开整个区域的会话。

### 6.10 Floating drag / docking

跨队列或脱墙拖拽使用独立 `EdgeCapsuleDragWindow`，而不是把 docked host 变形成自由胶囊。进程级维护一个可复用、长期存活的 pooled HWND/visual tree；docked host 在 floating transfer/reorder/handoff 阶段进入 suppressed/不可输入状态。

docking 使用显式 handoff/reveal 事务，在真实 docked endpoint 已确认后才撤掉 floating cover，因此 docked 单边布局和 floating 对称外形不会互相泄漏状态。

## 7. OS 与全局集成

`AppController` 还协调：

- Hardcodet tray icon / context menu。
- 全局快捷键。
- foreground fullscreen 检测与 topmost avoidance。
- display metrics / DPI 改变后的延迟刷新。
- Todo reminders。
- virtual desktop integration。
- 可选窗口 magnetism / tether 等实验运行时。
- MCP bridge / MCP runtime。

### 7.1 托盘

当前托盘使用 Hardcodet `TaskbarIcon`，图标走 WPF `IconSource`；外部 `PaperTodo.ico` 是用户自定义覆盖入口。托盘菜单在打开时按当前状态重建，不维护第二套长期菜单状态。

这些全局能力可以影响 window visibility、z-order、monitor placement；进入具体 surface 后仍遵循各自 ownership。例如 display metrics 改变通过 edge presenter 的 dirty/reconcile 与 queue transaction 进入 edge subsystem，而不是从全局 watcher 直接改 capsule visual 几何。

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
