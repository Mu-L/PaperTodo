# PaperTodo 架构决策

> 本文记录 **已经做出的技术选择、明确取舍、被淘汰的路线，以及值得防止重复踩坑的结论**。
>
> 它不是 changelog，也不是验收清单。当前项目地图见 [`ARCHITECTURE.md`](ARCHITECTURE.md)，Agent 执行规则见 [`AGENTS.md`](AGENTS.md)。

## 维护规则

- 只记录以后仍需要知道的 why；普通 bugfix、参数微调、UI 调整和一次性验证不为了“留痕”新增 decision。
- 同一决策的澄清、证据补充或边界收紧直接更新原条目；真正被新选择替代时把旧条目标为 `Superseded` 并指向新 D-xxx。
- Evidence 优先指向当前代码入口；历史因果重要时再补关键 commit/PR。

---

## D-001 — 保持“桌面纸片”作为主要交互和对象边界

**Status:** Accepted

### Decision

PaperTodo 当前的主要交互单元仍是一张独立桌面纸片。Todo、Note、插件 body、胶囊等能力以 paper 为自然组合边界；应用级能力可以全局协调，但不会在没有新产品决策时自动收束成中心主界面。

这不是永久禁止新的后端、索引、提醒、外部数据源或知识管理能力；产品方向明确变化时应更新本决策。

### Why

窗口生命周期、持久化几何、跨纸片链接、托盘恢复、Edge 和插件 session 都以 `PaperData` / `PaperWindow` 为自然边界。

### Evidence

- `src/Models.cs`
- `src/PaperWindow.cs`
- `src/AppController.cs`

---

## D-002 — 核心状态、图片资产和插件状态按 ownership 分域持久化

**Status:** Accepted

### Decision

PaperTodo 不把所有持久化数据塞进一个文件：

- `data.json` / backup：核心 `AppState` / `PaperData`。
- `note-assets.lmdb`：Note 图片二进制与图片索引。
- `plugins/data/*.json`：宿主管理的 provider settings 与 per-paper plugin state。

各域使用自己的恢复/保存语义，不互相冒充 authority。破坏性图片回收采用 fail-closed：无法证明引用扫描可信时宁可不删。

### Why

核心状态需要可恢复的结构化 snapshot；大体积图片需要独立资产存储；插件状态又有独立版本、恢复和生命周期。混在同一协议会放大保存、兼容和故障恢复耦合。

### Rejected / Do not reintroduce

- 不把图片 blob/base64 塞回 `data.json`。
- 不把 provider settings/per-paper state 塞回核心 `AppState` 作为默认路径。
- 不让插件自行建立另一套宿主不管理的 authoritative state 文件。
- 不绕过 `NoteImageStore` 建立另一套 LMDB transaction authority。
- 不在保护 snapshot 无法可靠扫描时继续做破坏性 image GC/id reuse。

### Evidence

- `src/StateStore.cs`
- `src/NoteImageStore.cs`
- `src/LmdbImageDatabase.cs`
- `src/PaperBodyPluginDataStore.cs`

---

## D-003 — Crash boundary 不做“最后一次强行保存”

**Status:** Accepted

### Decision

未处理异常的全局边界记录 crash log 并结束错误路径，不把此刻内存中的整个 `AppState` 再普通保存一次。

### Why

未处理异常发生时，内存对象可能只完成了部分业务事务；强行保存可能覆盖此前健康的主文件/backup。正常 durability 由自动保存、同步退出保存和 backup 提供。

### Rejected / Do not reintroduce

除非未来建立可证明一致的 crash-safe snapshot，不在 Dispatcher/AppDomain crash handler 中直接调用普通保存流程。

### Evidence

- `App.xaml.cs`
- `src/StateStore.cs`

---

## D-004 — Paper body 插件通过 session 边界接入，而不是接管 `PaperWindow`

**Status:** Accepted

### Decision

Provider 发现与单纸片 provider session 分开：`PaperBodyPluginRegistry` 负责发现/校验，`PaperBodyHost` 管理一张纸当前 `IPaperBodySession`，`PaperWindow` 继续拥有 WPF placement、paper chrome 和 provider 选择。

Native plugin 是 fully trusted / unsandboxed；已载入 CLR 的 Native provider 不能按 Web provider 的方式安全热替换。

### Why

插件只替换 paper body，避免把窗口 placement、保存、Edge、主题和宿主生命周期整体变成插件 ABI。

### Rejected / Do not reintroduce

- 不让插件直接成为 `PaperWindow` 生命周期 authority。
- 不假设 Native WPF assembly 可以像 Web 内容一样无代价热替换。

### Evidence

- `src/PaperBodyHost.cs`
- `src/PaperBodyPluginRegistry.cs`

---

## D-005 — 单纸片 Edge 状态必须走 typed Intent → Reducer → Presenter

**Status:** Accepted

### Decision

会改变一张纸 Slot / Visual / Gesture / Preview / Placement 的产品级输入进入 `EdgeCapsuleIntent`；`EdgeCapsuleReducer` 原子产生 `EdgeCapsuleModel`；`EdgeCapsulePresenter` 是该纸 desired model、target、transition、applied frame 和 deferred work 的 authority。

队列级 preview owner、corridor、arrange、visual transaction 和 proxy 生命周期由 `AppController` 协调，但不持有第二份 per-paper desired model。

### Why

把状态散在 `PaperWindow`、controller 和 host 会制造无法枚举的组合；把天然属于队列的会话状态强塞进每张纸 reducer 又会复制 owner/transaction 状态。

### Rejected / Do not reintroduce

- 不增加通用 `SetEdgeState(...)` / field setter 状态机。
- 不在 `PaperWindow` 建第二套 per-paper FSM。
- 不为每个新 race 增加绕开 reconcile 的 `pendingX/scheduledX`。

### Evidence

- `src/EdgeCapsuleModel.cs`
- `src/EdgeCapsuleReducer.cs`
- `src/EdgeCapsulePresenter.cs`
- `src/AppController.EdgeCapsulePreview*.cs`

---

## D-006 — Queue placement 与 docked 物理几何各只有一个 authority；队列不分页

**Status:** Accepted

### Decision

- index / master offset / slot count 只由 `EdgeCapsuleQueueCoordinator` 计算。
- monitor/edge/DIP 到物理矩形只由 `EdgeCapsuleGeometry` 计算。
- 队列不按工作区高度分页、截断或隐藏 overflow。

### Why

多处推导队列位置或像素公式会在 PerMonitorV2、多 DPI、左右墙和跨屏环境产生分歧；分页会把纯 placement 升级为另一套 visibility/state ownership。

### Rejected / Do not reintroduce

- 不从邻居 HWND 反推 index。
- 不复制 docked physical-pixel 公式。
- 不加入 overflow page/header/page number/自动翻页。

### Evidence

- `src/EdgeCapsuleQueueCoordinator.cs`
- `src/EdgeCapsuleGeometry.cs`

---

## D-007 — V3 Lite 采用 per-paper bounded live host

**Status:** Accepted

### Decision

每张 docked capsule 的真实 HWND 由 `EdgeCapsuleHost` 长期拥有；`HostBounds` 是稳定 bounded capacity，`Bounds` 是当前可见 WPF shape。正常 Resting/Hover/Preview 不靠反复 resize native host 做形变。

### Why

endpoint-sized HWND 会让每轮 Hover/Preview 改变 native surface identity；过大的长期透明 host 又扩大 hit-test、z-order 和透明区域 ownership。bounded host 把 native capacity 与可见 shape 分开。

### Rejected / Do not reintroduce

- 不恢复逐帧 resize 真实 HWND 的 endpoint-sized 架构。
- 不把每张纸扩成 work-area-sized / queue-sized 透明合成面。
- 透明 capacity 不变成交互区。

### Evidence

- `src/EdgeCapsuleHost.cs`
- `src/EdgeCapsuleTargetPlanner.cs`
- `32866e9085c2002c3411d4a2c93a96903fe6c9ee` — bounded live hosts
- `ca70631d2c3b77a883a5c78f5a912cfe2ccc9294` — late-bound preview capacity

---

## D-008 — WPF 拥有 shape；DirectComposition 只允许 live-surface translation

**Status:** Accepted

### Decision

WPF/bounded host 负责宽高、圆角、内容、opacity 和 `InteractiveBounds`；DComp queue proxy 获取真实 live HWND surface 并只改变 X/Y offset，在 cover 下让 real HWND 一次 settle 到 endpoint。

### Why

如果 compositor 同时拥有 translation、clip、scale、resize、snapshot，而 WPF 也改变真实 visual size，就会出现两套 presentation model，successor、pointer、DPI 和 rollback 都必须额外判断“谁是真的”。

### Rejected / Do not reintroduce

Production translation backend 不包含 bitmap snapshot/frozen frame、clip/scale/effect resize、Reveal/Conceal resize handoff、deferred resize state machine 或用 compositor trick 模拟 WPF Preview shape。

### Evidence

- `src/EdgeCapsuleQueueCompositionProxy.Visuals.cs`
- `src/EdgeCapsuleQueueCompositionProxy.Routing.cs`
- `d4af6affc0d5b704e20e020ae9e9621170613c8c`
- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb`

---

## D-009 — Visual authority 必须显式交接；失败路径不能出现 all-hidden gap

**Status:** Accepted

### Decision

Queue compositor、真实 docked HWND 和 floating drag HWND 是显式 visual authority。publication / successor / handoff / rollback 任一边界都必须保证至少一个可见 authority 存在；cover 丢失时先恢复真实 HWND，再考虑有界 retry。

### Why

真实 HWND 到 endpoint 不代表用户一定可见；source 仍 cloaked 而 proxy root 已撤会产生空白，反过来无约束同时可见会 duplicate/flash。

### Rejected / Do not reintroduce

- 不先全部 cloak 再稍后发布 cover。
- 不在 cover 丢失后先空等 timer 才首次恢复 real source。
- 不把资源 Dispose 当 authority transfer。

### Evidence

- `src/EdgeCapsuleQueueCompositionProxy.Handoff.cs`
- `src/AppController.EdgeCapsuleQueueProxy.cs`
- `f444f2897d1a741d2478a5d9af15744ed6a99716`
- `bb45739d49b16b4e609333476888f65f402fb17b`

---

## D-010 — Successor 继承 predecessor 的 live authority

**Status:** Accepted

### Decision

同队列已有 active proxy 时，新事务作为 successor generation：复用同一 output HWND/target，从 predecessor 当前呈现重新基线化，并 carry forward predecessor 仍持有的成员与 cloaked live source。只有现有 output envelope 覆盖所需区域时才直接 admission。

### Why

把 successor 当新冷 proxy 会产生两套 cloak/source ownership，并可能让 predecessor 的 stationary peer 在 root replacement 时消失。

### Rejected / Do not reintroduce

- 不先 dispose predecessor 再冷启动 successor。
- root replacement 不遗漏 stationary peers。
- 不为扩大 successor envelope 移动仍承载 predecessor root 的 output HWND。

### Evidence

- `src/EdgeCapsuleQueueCompositionProxy.Core.cs`
- `src/AppController.EdgeCapsuleQueueProxy.cs`
- `be94659d555b79759853fb392b1af5a4577d19fa`
- `bb45739d49b16b4e609333476888f65f402fb17b`

---

## D-011 — Floating drag 是独立且持久复用的真实 HWND

**Status:** Accepted

### Decision

脱离队列/跨边拖拽使用 `EdgeCapsuleDragWindow`，不复用 docked 单边 host；当前 controller 序列化 capsule reorder，因此进程级只维护一个 pooled drag HWND/visual tree。

### Why

Docked 与 FloatingFree 的外形、placement 和生命周期不同；复用同一 host 会污染 edge/corner/DPI/width 状态，每次重建 WPF Window 又把冷启动放回输入热路径。

### Rejected / Do not reintroduce

- 不让 `EdgeCapsuleHost` 临时变成 floating pill。
- 不为每次拖拽重建 HWND/visual tree。
- 在单 drag session 前提下不建立多备用 host 池。

### Evidence

- `src/EdgeCapsuleDragWindow.cs`
- `cc9906ab940bc0e11905401fb079fdedc1f05427` — persistent drag host

---

## D-012 — Presenter transition 使用 Rendering cadence；watchdog 只救活

**Status:** Accepted

### Decision

正常 Edge transition 由 presenter 持有，并由同 Dispatcher 的 shared `EdgeCapsuleFrameScheduler` 在 `CompositionTarget.Rendering` 上推进。watchdog 只在 active transition 未及时得到 Rendering 推进时做 demand-driven rescue；阈值是实现参数。

### Why

长期高频 timer 会与 WPF compositor cadence 漂移；纯 Rendering 又可能在某些调度边界失去活性。最终是一套正常 frame clock + 一个按需 rescue，而不是两套持续竞速 producer。

### Rejected / Do not reintroduce

- 不恢复长期固定间隔 timer 作为第二动画引擎。
- watchdog 不在无 active transition 时持续运行。
- 不穿透 pending reconcile / external native batch ownership 强推 frame。

### Evidence

- `src/EdgeCapsuleFrameScheduler.cs`
- `303c9ebd22fa69d75a32bb7cb923c42cfb512fb5`
- `708dcd267827cee9f9174d9e9c49303ae3b760e8`
- `e5e07526da0d9b6178975e5c7e90debf4d4a6241`
- `ce406c10507418c67b32bd17b9c7b99819201145`
- `a3c8b62962178ca5d6a63f5c555c7c0a847eee56`

---

## D-013 — Proxy handoff 等待真实 WPF terminal presentation，而不是靠额外 delay

**Status:** Accepted

### Decision

proxy 到逻辑终点后，real/WPF presentation 必须完成 endpoint flush/apply、必要 render turn、真实 bounds verify 和 authority swap 条件，再撤 compositor cover。completion timer 只能发起一次完成尝试。

### Why

DComp 动画结束和 WPF 最后一帧进入 DWM 不是天然同一个调用点；固定 completion guard 只能降低 race 概率，不能成为 correctness proof。

### Rejected / Do not reintroduce

- 不以“再延迟几毫秒”证明 terminal frame 正确。
- 不把 timer 到期等同于 WPF 已完成。
- endpoint apply/layout/verify 失败时不先撤 cover。

### Evidence

- `src/AppController.EdgeCapsuleQueueProxy.cs`
- `c9aa1910d6533e95947567e4b057e87b0e93f7ae`
- `bcc6740e992af048cc28f8b810168301434f9555`
- `4200162d363dc4f22bacc198e599ba917da3f36f`
- `9f7a04ba4c1d01103fb53679f3b939b9e16083d0`

---

## D-014 — Pointer truth 来自 presented `InteractiveBounds`

**Status:** Accepted

### Decision

Hover、Preview 和 corridor 的物理命中以当前 presented/applied `InteractiveBounds` 为准；WPF/native enter/leave 只是重新采样 signal。proxy 也消费同一 logical frame 的 `InteractiveBounds` 做输入路由。

### Why

透明 chrome、bounded host capacity 和 DComp translation 让 HWND rectangle 与真实交互区域不一致。

### Rejected / Do not reintroduce

- 不直接在 `MouseEnter/MouseLeave` 写 Hover business state。
- 不把透明 host capacity / compositor envelope 当 capsule/corridor bounds。
- 预测不能否决“已物理离开整个合法区域”。

### Evidence

- `src/EdgeCapsulePresenter.cs`
- `src/EdgeCapsuleQueueCompositionProxy.Routing.cs`
- `dcf2033d41b3b52c3036eb6a3d4204b2b3441cd9`
- `e15796d57f6126e242445c54a8813fd022c35978`

---

## D-015 — 四类知识分工；Architecture 保持项目地图而非完整实现说明

**Status:** Accepted

### Decision

PaperTodo 长期维护四类互补知识：

- `ARCHITECTURE.md`：项目地图——主要入口、ownership、数据域以及“去哪查什么”。
- `DECISIONS.md`：明确取舍、失败路线和可复用踩坑结论。
- `AGENTS.md`：Agent 执行规则、硬约束、提交/发布/维护规则。
- 关键代码注释：局部 why、不变量和危险边界。

易变参数、完整调用时序和普通实现细节只留代码；不建立需要人工长期同步的第二套完整架构或场景验收矩阵。

### Why

同一事实复制到多份说明会漂移。Architecture 只做导航后，可以保持足够稳定；需要理解具体机制时直接进入代码，需要理解历史取舍时进入 Decisions。

### Consequences

- ownership/入口变化同步 Architecture。
- 产生或推翻 durable why 同步 Decisions。
- Agent 执行规则变化同步 Agents。
- 局部隐藏不变量同步代码注释。
- `docs/edge-presentation-v3-lite.md` 不再作为并行当前架构文档保留；历史演进留 git/PR。

---

## D-016 — V3 Lite 完成后删除一次性验证脚手架

**Status:** Accepted

### Decision

PR #94 为完成 V3 Lite 引入的一次性 source export、finalizer、clean-state verifier 等迁移脚手架在收敛后删除；主线只保留生产代码、通用 CI 和仍有长期价值的 diagnostics。

### Why

一次性迁移 orchestrator 适合受控大重构，但长期保留会制造“它是不是生产流程”的歧义，并增加仓库/Actions 噪音。

### Evidence

- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb`
- `899f3cd284eaa19b45cc8ae6a953f5500ca2a57b` — PR #94 merge

---

## D-017 — 托盘继续使用当前 Hardcodet WPF `IconSource` / popup lifecycle

**Status:** Accepted

### Decision

托盘图标使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`，外部 `PaperTodo.ico` 保持用户覆盖入口；菜单在打开时按当前状态重建。PaperTodo 不回退到旧 `System.Drawing.Icon` / 手动 popup / 预热菜单 / 全局鼠标轮询修补路线。

### Why

当前 `IconSource + 本地 wpf-notifyicon fork + 真实 Popup HWND` 是已经解决首次菜单、跨 DPI 定位和 focus 时序问题的一整套路径。绕开这套控件生命周期会重新暴露既有回归；这里禁止的是回退到已失败的旧整体路线，而不是声称单个 API 独自造成所有问题。

### Rejected / Do not reintroduce

- 不把默认托盘路径换回 `System.Drawing.Icon`。
- 不用手动 popup、菜单预热或全局鼠标轮询修首次菜单问题。
- edge context-menu focus 清理不无条件提前到 WPF menu mode 尚未退出时。

### Evidence

- `src/AppController.Tray.cs`
- `vendor/wpf-notifyicon`
- `200b23e0826632dae630bc565b41328421381b63` — 接入本地 wpf-notifyicon fork 处理 DPI/focus
- `5da90e5428e8f68a29b777227454556f862b8e5c` — 托盘打开前清理遗留激活状态

---

## D-018 — Edge plugin mini 由宿主持有 window/queue/input authority，能力路径各自安全收敛

**Status:** Accepted

### Decision

插件可以提供 Native 专属 mini、Web `miniEntry`、可迁移纯 WPF 正文 View、自定义 capsule、标准 capsule 或 plain text，但 edge preview 的窗口、队列、尺寸会话和输入 authority 始终属于宿主。

当前路径不是一条统一“先结构化 fallback 再替换”的流水线：

- Native 专属 mini 创建失败时可降级到 capsule fallback。
- 声明 `miniEntry` 的 Web provider 直接使用专属 mini host；当前准备期间使用透明占位，只有当前文档完成、`mini.ready()` challenge 验证通过并跨过真实 Rendering 边界后才显示 Web surface。
- Native 正文迁移依据预热/快照/真实 View 状态安全进入 preview；不维护第二份业务 UI。
- 没有专属 1.8 preview 能力时，才使用自绘/标准 capsule/plain-text fallback。

### Why

插件如果自己拥有 preview HWND、queue placement 或第二份 authoritative state，Edge 会重新出现多套 geometry/visibility/input ownership。foreign HWND/WebView2/已挂载 tree 也不能被当作可迁移纯 WPF 内容直接搬进 bounded host。

### Rejected / Do not reintroduce

- 不让插件拥有 edge queue HWND 或 placement authority。
- 不把 `Window`、`HwndHost`、WindowsFormsHost、WebView2 或已挂载控件当作可迁移 Native mini tree。
- 不让旧 same-origin Web 文档仅凭 queued `miniReady` 获得新 generation 的 publication authority。
- 不为正文迁移维护持续截图循环或第二份 authoritative plugin state。
- 不让插件各自复制宿主的自动宽度/队列测量算法。

### Evidence

- `src/PaperWindow.PluginMiniView.cs`
- `src/WebPaperBodySession.Mini.cs`
- `src/PaperWindow.PluginBodyMigration.cs`
- `src/PaperBodyPluginRegistry.cs`

---

## D-019 — 内置 Note 编辑与浏览共享同一个 `MarkdownTextBox`

**Status:** Accepted

### Decision

内置 Note 的编辑态和浏览态复用同一个 `MarkdownTextBox`，通过 presentation/interaction 状态切换，而不是建立两套文本控件并同步内容、滚动和选区。

### Why

两套文本 surface 会引入滚动位置、换行测量、selection、caret、图片布局和编辑提交时序的双向同步问题。单控件切状态保持一份真实文本 surface。

### Rejected / Do not reintroduce

不为了浏览态视觉方便复制第二个 Markdown 编辑/显示控件并做双向同步。

### Evidence

- `src/PaperWindow.Note.cs`
- `src/MarkdownTextBox.cs`
