# PaperTodo 架构

> 本文记录 **PaperTodo 当前有效的技术选型、架构结构和已经确立的技术方向**。
>
> - 它回答“系统现在按什么原则组织、各层由谁负责、后续实现应沿什么边界继续”。
> - 它不是代码目录、历史日志、PR 复盘或未来路线草案；任务入口与阅读顺序见 [`AGENTS.md`](AGENTS.md)，历史取舍和踩坑见 [`DECISIONS.md`](DECISIONS.md)。
> - 具体执行细节仍以当前代码为准。若本文、代码或 Decisions 冲突，先核对当前实现、提交历史和可观察行为，再统一修正。

## 1. 架构目标与当前方向

PaperTodo 是 Windows 桌面“纸片”应用。当前技术路线围绕几个长期方向组织：

- **paper 是主要对象和交互边界。** Todo、Markdown/Note、插件正文和 Edge Capsule 都围绕 `PaperData` / `PaperWindow` 组合；应用级能力由 `AppController` 协调，而不是默认把所有行为收束成一个中心主界面。
- **每个职责尽量只有一个 authority。** 状态、几何、队列 placement、presentation、持久化和外部 mutation 不各自复制第二套“近似真相”。
- **复杂 UI 状态优先走显式状态与单通道 reconcile。** Edge Capsule 使用 Intent → Reducer → Presenter；窗口和 controller 不通过并行 bool/临时 setter 绕过它。
- **WPF 是主 UI / shape owner，native/DirectComposition 只承担确有必要的 Windows 边界能力。** 不把 compositor 扩成第二套 UI renderer。
- **插件贡献 intent / content，宿主持有产品 chrome 与关键生命周期 authority。** capsule、Edge Mini、Top Bar 都遵守这一方向；插件不能因为获得扩展点就接管 PaperWindow、Edge HWND 或顶栏 WPF tree。
- **持久化按数据生命周期和失败语义分域。** 核心状态、图片资产、插件状态分别由各自 store 管理；破坏性恢复/回收采用保守策略。
- **当前 Architecture 只记录已经确立的方向。** 未确认的未来方案、实验候选和一次性 workaround 不写成当前架构。

技术基础：

- .NET 10，目标 `net10.0-windows10.0.17763.0`。
- WPF 是主 UI；Windows Forms 只作为兼容依赖。
- 进程 DPI 策略：`PerMonitorV2,PerMonitor`。
- 主项目入口为根目录 `PaperTodo.csproj`。

## 2. 系统形态与 ownership

正常 GUI 模式由 `App` 建立一个单实例 WPF 主宿主；`AppController` 是应用级协调器。相同的 `PaperTodo.exe` 还支持独立 `--mcp` bridge 模式，该模式在 GUI 单实例协议之前分流，不拥有第二份 `AppState`。

高层关系：

```text
PaperTodo.exe
├─ --mcp
│   └─ McpBridge
│       └─ stdio MCP ↔ GUI-side MCP runtime
└─ GUI App
    └─ AppController
        ├─ AppState / StateStore
        ├─ NoteImageStore (LMDB)
        ├─ PaperBodyPluginRegistry / PaperBodyPluginDataStore
        ├─ PaperCommandService
        ├─ plugin Top Bar session registry
        ├─ PaperWindow[paperId]
        │   ├─ paper shell / Todo / built-in Note
        │   ├─ PaperBodyHost
        │   ├─ host-owned Top Bar renderer
        │   └─ EdgeCapsulePresenter + EdgeCapsuleHost
        ├─ MasterCapsuleWindow[queue]
        ├─ EdgeCapsuleDragWindow (process-global pooled host)
        ├─ tray / hotkeys / reminders / fullscreen / virtual desktop runtime
        └─ edge queue coordination / preview session / visual transaction /
           DirectComposition proxy lifecycle
```

主要 authority：

| 领域 | 当前 authority | 结构性职责 |
| --- | --- | --- |
| GUI 启动与进程生命周期 | `App` + `SingleInstanceHelper` | GUI 单实例、启动命令转发、全局异常边界、创建 `AppController` |
| 应用级业务协调 | `AppController` | `AppState`、窗口集合、保存调度、托盘、全局 runtime、跨纸片协调 |
| 核心持久化 | `StateStore` | `data.json` / backup 的加载、恢复和版本化写入 |
| 图片资产 | `NoteImageStore` | LMDB 生命周期、串行访问、图片编号、缓存和回收 |
| 插件状态 | `PaperBodyPluginDataStore` | provider settings 与 per-paper plugin state 的独立保存/恢复 |
| 外部 Paper/Todo/Note 命令 | `PaperCommandService` | 插件/MCP 共用的验证、mutation、同步提交/回滚和事件发布 |
| 单纸片 UI | `PaperWindow` | paper WPF shell、普通交互、provider 选择、子系统适配 |
| paper-body session | `PaperBodyHost` | 当前 `IPaperBodySession` 的 attach / invoke / commit / dispose |
| 插件发现与合同 | `PaperBodyPluginRegistry` | builtin / Native / Web provider 发现、校验、激活 |
| 插件 Top Bar 注册 | `AppController.PluginTopBar` | session lease、Paper/Global action 集合、Global provider 去重、输入校验 |
| 插件 Top Bar 绘制 | `PaperWindow.PluginTopBar` | 标准按钮、字符/SVG 图标、主题/DPI/响应式布局、宿主按钮 suppression reconcile |
| Edge 单纸片业务状态 | `EdgeCapsuleReducer` + `EdgeCapsuleModel` | 单纸片 typed intent 到完整 model 的原子变化 |
| Edge 单纸片呈现 | `EdgeCapsulePresenter` | desired model、target plan、transition、applied frame、reconcile |
| Edge 队列级协调 | `AppController` edge partials | preview owner/corridor、arrange、visual transaction、proxy lifecycle |
| Edge 队列 placement | `EdgeCapsuleQueueCoordinator` | queue index、master offset、slot count |
| Edge 物理几何 | `EdgeCapsuleGeometry` | monitor/edge/DIP 到 wall-pinned physical rectangles |
| docked Edge surface | `EdgeCapsuleHost` | 每纸片 bounded HWND 和完整 WPF visual tree |
| 同队列 compositor translation | `EdgeCapsuleQueueCompositionProxy` | live HWND surface 的 X/Y translation 与 visual-authority handoff |
| floating drag | `EdgeCapsuleDragWindow` | 独立 floating pill HWND |
| 同 Dispatcher 动画节拍 | `EdgeCapsuleFrameScheduler` | Rendering cadence、统一 pointer/time sample、liveness rescue |

## 3. 进程与运行时边界

### 3.1 GUI 单实例

正常 GUI 启动使用 `SingleInstanceHelper` 的 Mutex + named pipe。只有主 GUI 实例建立 `AppController`；后续 GUI 启动只把参数转发给主实例后退出。

`AppController` 尚未完成启动时收到的单实例命令先排队，待 controller 可用后再执行。普通纸片窗口全部关闭不等于退出应用，进程使用显式 shutdown 生命周期。

### 3.2 MCP

`--mcp` 是同一可执行文件的独立 bridge 模式。它在 GUI Mutex 之前分流，通过 stdio 暴露 MCP server；GUI 主宿主内部的 MCP runtime 由 `AppController` 管理。

MCP 的 transport、权限策略和 bridge 生命周期不拥有 Paper/Todo/Note 的第二套业务写入逻辑；真正的业务 mutation 仍回到 GUI 主宿主和共享命令边界。

### 3.3 辅助进程与插件 runtime

Web 插件使用 WebView2 runtime；脚本胶囊可以启动 PowerShell 子进程。这些进程/运行时只提供对应能力，不成为核心 `AppState` authority。

插件协议当前以 **2.0** 为新开发目标，同时兼容加载既有 **1.8** 插件。2.0 的 Top Bar 是新增的 session/presentation capability；兼容加载 1.8 不意味着把 2.0 presentation API 反向开放给旧协议。

## 4. 状态与持久化架构

### 4.1 三个数据域

当前长期数据按语义拆成三个主要域：

| 数据域 | 当前存储 | authority | 方向 |
| --- | --- | --- | --- |
| 核心应用与纸片状态 | `data.json` + `data.backup.json` | `StateStore` | 保持可迁移、可恢复的结构化业务状态 |
| Note 图片二进制 | `note-assets.lmdb` | `NoteImageStore` / `LmdbImageDatabase` | 大体积二进制与 JSON 分离，独立做引用/容量管理 |
| 插件 settings / per-paper state | `plugins/data/*.json` | `PaperBodyPluginDataStore` | 插件生命周期与核心状态解耦，独立迁移和恢复 |

这三类数据不能因为“都属于一张纸”就合并成一个写入协议。核心状态保存、图片回收和插件状态恢复具有不同失败语义，因此保持各自 authority。

### 4.2 核心状态

`AppState` 是核心持久化根；`PaperData` 是单纸片模型；Todo 行使用 `PaperItem`。

删除、隐藏、折叠是不同语义：

- 删除从 `State.Papers` 移除对象。
- 隐藏保留对象，仅改变可见性。
- 折叠仍是可见纸片，只切换到 capsule presentation。

普通窗口 `X/Y/Width/Height` 与 Edge Capsule 的 queue / expanded recovery geometry 不是同一套状态，不能由 parked/hidden shell 相互覆盖。

`StateStore` 的方向是保守恢复与版本化写入：主文件失败后可从 backup 恢复；需要保护失败源时先保留证据再允许正常保存覆盖。保存阶段只修复序列化无效值，不重新解释业务不变量。

全局 crash boundary 不执行普通“最后强行保存”。正常 durability 由常规保存、同步退出保存和 backup 提供。

### 4.3 图片资产

图片二进制不进入 `data.json`。`NoteImageStore` 统一串行化 LMDB 访问，外部业务代码不直接拥有 LMDB transaction authority。

Markdown 中的 Note 图片只通过 PaperTodo 内部 `i:` asset URI 引用宿主管理的图片；网络图片或任意外部图片不是当前 Note 图片资产协议的一部分。

图片 GC / id reuse 是破坏性操作，因此 reachability 采用 fail-closed：无法可靠证明当前状态和需要保护的 recovery snapshot 都可扫描时，本轮不回收。

### 4.4 插件状态

插件 settings 与 per-paper state 由 `PaperBodyPluginDataStore` 独立保存，不塞回 `data.json`。插件数据读失败时保留原始问题源，并通过受控 recovery 路径继续；插件数据故障不应把核心 Paper 数据变成不可加载。

删除 paper 后，插件 per-paper state 属于附属数据：核心 paper 删除先成为 authority，插件 state 清理可在后续成功保存边界继续重试，不反向阻塞核心删除。

## 5. Paper 与 paper-body 插件

### 5.1 Paper shell

`PaperWindow` 是单纸片 UI owner，负责普通 paper shell、Todo/Note 交互、标题/工具栏、窗口行为和各子系统适配。

Edge Capsule 启用后，一张纸的可见 surface 不再等价于一个 `PaperWindow` HWND：docked capsule 由 `EdgeCapsuleHost` 提供，跨队列/脱墙拖拽可以临时使用 `EdgeCapsuleDragWindow`；这些 surface 仍引用同一 `PaperData`，不复制业务对象。

内置 Markdown Note 的编辑态和浏览态复用同一个 `MarkdownTextBox`，通过 interaction/presentation 状态切换，而不是维护两套正文 surface。

### 5.2 Provider / session 分层

Provider 当前分三类：

- Built-in Markdown。
- fully trusted / unsandboxed Native .NET/WPF plugin。
- 本地 Web plugin，通过宿主 WebView2 运行。

`PaperBodyPluginRegistry` 负责 provider 发现和合同校验；`PaperBodyHost` 负责一张纸当前 session 的 attach / invoke / commit / dispose；`PaperWindow` 仍拥有窗口 placement、paper chrome 和 provider 选择。

Native assembly 一旦载入 CLR，不按 Web provider 的方式做进程内热替换；需要重启才能稳定切换已加载版本。

### 5.3 外部读写

插件 `Workspace` 与 GUI 侧 MCP 对 Paper/Todo/Note 的共享业务 mutation 统一进入 `PaperCommandService`。该边界负责：

- 参数和类型约束；
- mutation 前提交仍停留在 UI/provider session 的待提交内容；
- 保存成功才完成外部 mutation；
- 保存失败回滚内存状态；
- 提交后刷新必要 UI 并发布外部变更事件。

transport 权限、Web/Native surface 生命周期、Top Bar presentation 和 MCP protocol 不下沉到 `PaperCommandService`；反过来，transport / presentation 层也不建立另一套核心 mutation 实现。

### 5.4 Protocol 2.0 Top Bar

Top Bar 是 **session-scoped presentation capability**，不是 Workspace 数据 API。Native 通过 `PaperBodyContext.TopBar` / `IPaperTopBarApi` 使用；Web body 通过 root bridge transport 提交 `topbar.paper.set` / `topbar.global.set`，但真正的数据读写仍回到 Workspace。

当前 ownership：

- `PaperWindow` 始终拥有顶栏 WPF tree、按钮大小、位置、Hover、主题、DPI、字体/缩放和 responsive layout。
- 插件只提交 action descriptor：ID、字符或受限 SVG Path 图标、Tooltip、Enabled/Visible 与点击处理。
- 不接受插件直接传 Button、`FrameworkElement`、WebView 或完整 SVG document。
- SVG Path 支持宿主前景色 `Fill` 或 `Stroke`；Stroke width 受协议范围约束。
- Paper scope 只作用于承载当前 session 的纸片；Global scope 由活跃 session lease 持有并显示在全部纸片。
- 同 provider 多 session 的 Global owner 只由**最后一次 Global 注册顺序**决定；Paper scope 更新不改变 Global ownership。
- Global action 的点击上下文包含目标 `PaperId` / `Type` / `BodyProviderId`；插件需要读写目标正文时继续调用 Workspace。
- 插件自己的纸片只能 suppression 宿主明确白名单中的 `NewTodoPaper` / `NewNotePaper`；关闭、置顶、标题拖动和窗口生命周期不属于插件可删减区域。
- 用户设置决定宿主按钮的 base visibility，插件 suppression 是最终 paper-local reconcile 层；两条路径不互相覆盖。
- session Dispose 会回收全部 contribution。Web 进一步把 contribution 绑定当前 **body document generation**：导航、renderer failure 或 body WebView replacement 会立即撤销旧 document 的 contribution；Web Mini 不注册 Top Bar。

Global Top Bar 不是 manifest 静态 UI。仅安装插件、仅存在 descriptor、或不存在有效 runtime session 时，不应留下全局按钮。

### 5.5 Edge mini

插件可以提供专属 mini、允许迁移的纯 WPF 正文 View、custom/standard capsule presentation 或 plain-text fallback，但 **Edge 的窗口、queue placement、外层尺寸会话和输入 authority 始终属于宿主**。

当前技术方向是“插件贡献内容能力，宿主决定如何安全呈现”：

- Native mini 只接纳 fresh / unparented / pure-WPF tree。
- Web `miniEntry` 使用独立 Web mini surface；它自己的 ready/publication 时序属于 Web session 实现，不把 WebView2 当作可迁移 WPF child。
- Web Mini pointer 默认归宿主；只有 `data-papertodo-interactive` 声明的局部矩形交给 Web surface。
- 正文 View migration 只对 provider 明确声明且宿主可以安全接管的纯 WPF View 启用。
- 没有专属能力时由宿主降级到 capsule/plain text。

具体 fallback 次序、尺寸和 ready 时序属于当前 contract/代码实现；为什么形成这些边界见 D-018。

## 6. Edge Capsule V3 Lite

V3 Lite 的当前方向不是“再叠一个更聪明的代理”，而是保持 **单一 per-paper presentation authority + 极薄 native/compositor 边界**。

### 6.1 单纸片状态与呈现

主链：

```text
OS / WPF / controller event
        ↓
EdgeCapsuleIntent
        ↓
EdgeCapsuleReducer
        ↓
EdgeCapsuleModel
        ↓
EdgeCapsuleTargetPlanner
        ↓
EdgeCapsulePresentationPlan
        ↓
EdgeCapsulePresenter reconcile / transition
        ↓
EdgeCapsulePresentationFrame
        ↓
EdgeCapsuleHost / floating drag host
```

`EdgeCapsulePresenter` 是一张纸 desired model、target plan、transition、applied frame 和 deferred work 的唯一 presentation authority。`PaperWindow` 和 controller 可以转发/协调，但不保存第二份近似状态。

`EdgeCapsuleTargetPlanner` 是 desired model → 完整 plan 的纯规划层。Docked shape 与 `FloatingFree` shape 互斥；不能在各个调用点临时拼 shape。

measure、display refresh、DPI refresh 是同一个 reconcile 的新输入，不建立平行状态入口。手势期间不能安全 apply 的刷新进入 presenter deferred work，手势结束后统一重算。

### 6.2 monitor / DPI / geometry

目标 monitor/DPI 来自 `EdgeCapsuleLayoutSnapshot` 和当前 target environment，不拿主 `PaperWindow` 的 DPI 猜 docked host 的目标 DPI。

`ScreenGeometry` 的 typed wrapper 用来阻止 device pixel / global DIP / local DIP 混算。Queue placement 由 `EdgeCapsuleQueueCoordinator` 负责，物理 docked geometry 由 `EdgeCapsuleGeometry` 负责。

### 6.3 bounded live host

每张 docked capsule 的真实 HWND 由 `EdgeCapsuleHost` 长期拥有。`HostBounds` 是当前 host generation 的稳定 bounded capacity；`Bounds` 是当前可见 WPF shape；`InteractiveBounds` 是真实可交互物理范围。

容量只覆盖该纸在当前 monitor/DPI/edge 上真实可能需要的 Preview，不扩成整个工作区或整条队列。Late-bound plugin preview 可以让后续安全 generation 增长，但普通热帧不为协议理论最大值预留巨型透明 surface。

### 6.4 WPF shape / DComp translation

WPF 始终拥有可见 shape、圆角、内容、clip 语义。正常同队列动画不每帧提交 native HWND geometry；`EdgeCapsuleQueueCompositionProxy` 只代理 live HWND surface 的 X/Y translation 和必要 visual-authority handoff。

DComp 不拥有第二份 snapshot renderer、shape morph、scale/effect、Reveal/Conceal state machine 或延后 resize 模型。出现需要持续增加 shadow ownership / deferred recovery state 的情况，应先重新检查 authority 边界，而不是继续堆 proxy 状态。

### 6.5 visual authority transaction

视觉事务的原子单位是一次**用户可见 authority swap**，不是单个 HWND API 调用。

同一 queue 中共享 endpoint settle / reveal / cloak / root detach 的成员应：

1. 先准备全部成员；
2. 跨过同一个 render/composition publication boundary；
3. 统一验证并完成 handoff。

切换期间不能出现所有真实 surface 同时不可见的空档。固定毫秒 delay 不是“视觉已经发布”的证明。

### 6.6 Preview owner / corridor / pointer truth

Preview 的物理命中真相来自当前 applied `InteractiveBounds`：

- WPF/native enter/leave 只负责唤醒 sampling，不直接建立第二套命中状态机。
- `HostBounds`、proxy envelope、透明容量不能扩张 hit area。
- 没有 session 时，verified physical hit 可以立即建立第一个 owner。
- session 已建立后，当前 owner 是 queue-wide pointer arbiter，owner / candidate / corridor / outside 走一个 controller 路径。
- A→B 的已有 session 切换可以使用 residence / stability / predictor policy；具体时间参数留在代码，不进 Architecture。
- corridor 只维护从当前 owner 到候选的连续性，不是新的可交互区域。
- 真实物理 exit 是 hard boundary；predictor 不能覆盖真实 outside。
- pointer capture 期间暂停 leave 决策，保持当前交互 owner。

### 6.7 cadence 与边界清理

正常动画节拍使用共享 `CompositionTarget.Rendering`；watchdog 只负责 liveness rescue，不建立第二个主动画循环。

Display/DPI/z-order/drag-end/hide/disable 等 lifecycle boundary 先安全结束当前 visual authority，再清理 transient state。不能先丢 proxy/owner/host 状态，再依赖后续偶然事件恢复。

## 7. OS 与全局集成

Windows 边界能力统一由宿主封装，不让普通业务代码到处直接 P/Invoke：

- taskbar / window-switcher owner；
- topmost / fullscreen avoidance；
- virtual desktop；
- global hotkey；
- tray；
- native geometry / DComp；
- power/display/runtime notification。

这些能力仍以 Paper / AppController 的业务 ownership 为入口；OS handle 不是新的业务对象 authority。

## 8. 仓库结构

根 `PaperTodo.csproj` 是主应用项目；主要实现位于 `src/`。`PaperTodo.Plugin.Abstractions/` 是公开插件编译期合同；`plugin-samples/` 是当前插件开发手册、源码示例和构建脚本；`plugins/` 保存可直接加载的最终插件产物。

文档职责：

- `AGENTS.md`：Agent 入口、任务路由、必须直接执行的规则/禁区；
- `ARCHITECTURE.md`：当前有效架构、ownership、技术方向和稳定 invariant；
- `DECISIONS.md`：历史取舍、失败路线、trade-off 与 why；
- `plugin-samples/README.md`：当前插件 API/开发流程；
- 代码与关键注释：局部实现事实、具体 invariant 和不易从类型/调用关系直接看出的 why。

不要为同一事实再维护一份独立手工验收矩阵；稳定语义优先落到可执行检查、诊断或当前代码合同中。
