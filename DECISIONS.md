# PaperTodo 架构决策

> 本文记录 **已经做出的技术选择、明确取舍、被淘汰的路线，以及值得防止重复踩坑的结论**。
>
> 它不是 changelog，也不是验收清单。只要一次变更形成了会影响后续实现判断的明确选择、取舍或可复用踩坑结论，就应在同一变更中更新这里；没有新取舍的机械 bugfix、文案或参数调整不需要为了“留痕”新增条目。
>
> - 当前系统“现在是什么”见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。
> - Agent 工作时必须遵守的硬约束见 [`AGENTS.md`](AGENTS.md)。
> - 当前代码和可观察行为描述实现事实；提交号是决策证据，不是替代代码阅读的规范文本。
> - 本文首次建立时以 `main@626fe60d` 为代码基线，并独立回读 PR #94 / V3 Lite 及其后续收敛提交。

## 维护规则

每条记录尽量回答四个问题：

1. **选择了什么。**
2. **为什么这样选、当时取舍了什么。**
3. **哪些路线已经被证明不应该重新引入。**
4. **从哪里可以看到这个选择真正进入了代码。**

决定是否记录的标准不是“改动大不大”，而是“这次改动是否产生了以后仍需要知道的 why”。如果后来证明某条 decision 已经不成立，应把状态改为 `Superseded` 并指向新条目，不让旧结论继续伪装成当前规则。

---

## D-001 — 保持“桌面纸片”作为主要交互和对象边界

**Status:** Accepted

### Decision

PaperTodo 当前的主要交互单元仍是一张可独立存在、独立显示和独立交互的桌面纸片。Todo、Markdown/Note、插件 body、胶囊等能力以 paper 为自然组合边界；应用级能力由 controller 协调，但不会在没有新产品决策的情况下自动把所有 paper 行为收束成一个中心主界面。

这条选择约束的是对象与交互边界，不是永久禁止新的后端、索引、提醒、外部数据源或知识管理能力。后续如果产品方向明确扩展，应通过新的 decision 更新边界，而不是让旧结论反过来否决已经明确的新路线。

### Why

大量窗口生命周期、持久化几何、跨纸片链接、托盘恢复、边缘胶囊和插件 session 都以 `PaperData` / `PaperWindow` 为自然边界。保持这个边界能让新增能力组合到纸片上，而不是默认把所有功能提升到全局 controller。

### Consequences

- `AppController` 可以全局协调，但不吸走所有单纸片业务行为。
- `PaperWindow`、`PaperBodyHost`、edge per-paper presenter/host 保留清晰的 paper ownership。
- 新功能先判断它属于 paper、paper body 还是应用级 runtime；若要改变这个分层，记录新的明确决策。

### Evidence

- 当前 `AppState -> PaperData[] -> PaperWindow` 主结构。
- 当前 Architecture 的 ownership 表。

---

## D-002 — 业务状态使用 `data.json`，图片资产独立进入 LMDB

**Status:** Accepted

### Decision

`data.json` 是 PaperTodo 的主业务状态协议；图片二进制不进入 JSON，而由 `NoteImageStore` / LMDB 独立保存。

恢复和图片 GC 均采用保守策略：无法证明恢复源或图片引用扫描可信时，宁可保留旧数据/禁用 GC，也不猜测删除。

### Why

JSON 适合可迁移、可恢复的结构化 paper 状态；图片二进制会放大写入、备份和恢复成本。将两者分开后，可以对 JSON 做版本化 snapshot 保存，对图片做引用 reachability 和独立容量管理。

### Rejected / Do not reintroduce

- 不把图片 blob/base64 重新塞回 `data.json`。
- 不在失败启动后用默认空状态覆盖无法解析的主文件。
- 不在保护 snapshot 无法可靠扫描时继续做破坏性 image GC/id reuse。
- 不绕过 `NoteImageStore` 建立另一套 LMDB transaction authority。

### Evidence

- `StateStore.cs` 的 primary/backup/recovery-preservation、版本化写入和 `PrepareForSave`。
- `NoteImageStore.cs` 与 `AppController` 的图片库初始化、保护引用扫描和回收流程。

---

## D-003 — Crash boundary 不做“最后一次强行保存”

**Status:** Accepted

### Decision

未处理异常的全局边界负责记录 crash log 和结束当前错误路径，不尝试把此刻内存中的整个 `AppState` 强制持久化。

### Why

抛出未处理异常时，内存对象可能已经处于只完成一半的业务事务中。此时“尽量保存”可能比丢失最后几秒编辑更危险，因为它可能覆盖此前健康的 `data.json` / backup。

正常 durability 由自动保存、force-save 上限、同步退出保存和 `data.backup.json` 提供。

### Rejected / Do not reintroduce

不要在 Dispatcher/AppDomain crash handler 中直接调用普通保存流程，除非未来先建立可证明一致的 crash-safe snapshot 机制。

### Evidence

- `App.xaml.cs` 当前异常处理路径。
- `StateStore` 的正常恢复/backup 机制。

---

## D-004 — Paper body 插件通过 session 边界接入，而不是直接接管 `PaperWindow`

**Status:** Accepted

### Decision

Provider 发现与单纸片 provider session 分开：

- `PaperBodyPluginRegistry` 发现/校验 builtin、Native、Web provider。
- `PaperBodyHost` 管理一张纸当前 `IPaperBodySession` 的 attach、invoke、commit/cancel/dispose。
- `PaperWindow` 仍拥有 WPF placement、paper chrome 和 provider 选择。

Native plugin 是 fully trusted / unsandboxed，并且已载入版本不能在同一进程中安全热替换；Web provider 可以重新扫描/加载。

### Why

插件只替换“纸片 body”，不会把窗口 placement、保存、edge capsule、主题和宿主生命周期一起变成插件 ABI。

### Rejected / Do not reintroduce

- 不让插件直接成为 `PaperWindow` 生命周期 authority。
- 不假设 Native WPF assembly 可以像 Web 内容一样无代价热替换。

### Evidence

- `PaperBodyHost.cs`。
- `PaperBodyPluginRegistry.cs`。

---

## D-005 — 单纸片 Edge 状态必须走 typed Intent → Reducer → Presenter

**Status:** Accepted

### Decision

会改变一张纸 Slot / Visual / Gesture / Preview / Placement 的产品级输入使用 `EdgeCapsuleIntent`；`EdgeCapsuleReducer` 原子地产生完整 `EdgeCapsuleModel`；`EdgeCapsulePresenter` 是该纸 desired model、target plan、transition、applied frame 和 deferred work 的 authority。

队列级 preview owner、transfer corridor、arrange、visual transaction 和 proxy 生命周期由 `AppController` 协调。它可以向多张纸 dispatch intent、捕获起终帧和组织事务，但不持有第二份单纸片 desired model。

### Why

Edge capsule 同时存在单纸片状态与跨纸片会话。若 `PaperWindow`、controller、host 各自直接修改一组 bool/enum，局部修复很容易制造无法枚举的非法组合；反过来，如果把整个队列会话强塞进每张纸 reducer，又会复制 owner/corridor/transaction 状态。

### Rejected / Do not reintroduce

- 不增加通用 `SetEdgeState(...)` / 一组公开 field setter。
- 不在 `PaperWindow` 再维护第二套单纸片 edge FSM。
- 不让 controller 的 preview/transaction 状态反写成另一份 per-paper model。
- 不为每个新 race 单独增加一对 `pendingX/scheduledX` 绕开现有 reconcile。

### Evidence

- `EdgeCapsuleModel.cs`。
- `EdgeCapsuleReducer.cs`。
- `EdgeCapsulePresenter.cs`。
- `AppController.EdgeCapsulePreview*.cs` 与 `AppController.EdgeCapsuleVisualTransaction.cs`。

---

## D-006 — Queue placement 与物理几何各只有一个 authority

**Status:** Accepted

### Decision

- 队列 index / master offset / slot count 只由 `EdgeCapsuleQueueCoordinator` 计算。
- monitor/edge/DIP 到物理 `DeviceScreenRect` 的 docked geometry 只由 `EdgeCapsuleGeometry` 计算。
- 队列不分页；超出工作区的成员仍保持完整顺序，可以直接延伸出屏幕。

### Why

最危险的一类 edge bug 来自“每个窗口都能从邻居/当前 HWND 猜一次队列位置”和“多个路径复制像素取整公式”。PerMonitorV2、多 DPI、左右墙和跨屏环境会把这类复制放大成 1px/一帧分歧。

分页还会把纯 placement 升级成可变 visibility/state ownership，为 reorder、preview corridor、drag 和 master offset 增加另一套隐藏状态。

### Rejected / Do not reintroduce

- 不按工作区高度推导“安全容量”。
- 不加入 overflow page/header/page number/自动翻页。
- 不在动画、measure、host apply 或 controller 中复制 docked physical-pixel 公式。

### Evidence

- `EdgeCapsuleQueueCoordinator.cs`。
- `EdgeCapsuleGeometry.cs`。

---

## D-007 — V3 Lite 采用 per-paper bounded live host

**Status:** Accepted

### Decision

每张 docked capsule 的真实 HWND 由 `EdgeCapsuleHost` 长期拥有。`HostBounds` 是当前 host generation 的稳定 bounded capacity；`Bounds` 是当前可见 WPF shape。

容量只覆盖该纸在当前 monitor/DPI/edge 上真实可能需要的最大 Preview，不扩成整个工作区或整条队列。Late-bound plugin preview 可以让 capacity 增长，但正常 Resting/Hover/Preview 不靠反复 resize native host 做形变。

### Why

endpoint-sized HWND 会让 Hover/Preview 每轮改变 native surface identity，使 compositor translation 和 resize ownership 缠在一起；过大的长期透明 host 又扩大 hit-test、z-order、资源和透明区域 ownership 问题。bounded live host 把 native capacity 与可见 shape 分开。

### Rejected / Do not reintroduce

- 不恢复逐帧 resize 真实 HWND 的 endpoint-sized 架构。
- 不把每张纸扩成 work-area-sized / queue-sized 透明合成面。
- 透明 capacity 不能被当成交互区域。

### Evidence

- `32866e9085c2002c3411d4a2c93a96903fe6c9ee` — `refactor(edge): establish V3 Lite bounded live hosts`。
- `ca70631d2c3b77a883a5c78f5a912cfe2ccc9294` — late-bound plugin preview capacity。
- 当前 `EdgeCapsuleTargetPlanner.cs` / `EdgeCapsuleHost.cs`。

---

## D-008 — WPF 拥有 shape；DirectComposition 只允许 live-surface translation

**Status:** Accepted

### Decision

V3 Lite 的最终职责切分：

**WPF / bounded host**：width/height morph、Resting/Hover/Active/Preview shape、rounded geometry、content/opacity、presentation/interactive bounds。

**DirectComposition queue proxy**：获取真实 live HWND surface、保持 surface identity/尺寸不变、只改变 X/Y offset、在 cover 下让 real HWND 一次 settle 到 endpoint，并用同一 logical frame 路由 proxy 输入。

### Why

如果 compositor 同时拥有 translation、clip、scale、resize、snapshot，而 WPF 也在改变真实 HWND/visual size，就会出现两套 presentation model。successor、pointer hit test、DPI handoff 和 rollback 都必须额外判断“此刻谁才是真的”。translation-only 把 compositor 限制成位置加速层，而不是第二套 UI renderer。

### Rejected / Do not reintroduce

V3 Lite production translation backend 明确不包含：

- bitmap snapshot / frozen frame
- clip resize
- scale resize
- effect-based resize
- Reveal / Conceal resize handoff
- deferred resize state machine
- 用 compositor opacity/bitmap trick 模拟 WPF Preview shape

### Evidence

- `32866e9085c2002c3411d4a2c93a96903fe6c9ee`。
- `d4af6affc0d5b704e20e020ae9e9621170613c8c` — 删除 snapshot/pointer-proxy 路径并收紧 backend 能力。
- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb` — closeout 验证 live-surface bridge 且拒绝 clip/scale/effect/snapshot。
- 当前 `EdgeCapsuleQueueCompositionProxy.Visuals.cs` / `Routing.cs`。

---

## D-009 — Visual authority 必须显式交接；失败路径不能出现 all-hidden gap

**Status:** Accepted

### Decision

Queue compositor、真实 docked HWND 和 floating drag HWND 是显式 visual authority。publication / successor / handoff / rollback 任一边界都必须保证至少一个可见 authority 存在。

DComp root replacement 与 DWM cloak/uncloak 通过可验证 transaction boundary 协调。cover 丢失时先立即尝试恢复真实 HWND；只有即时恢复本身失败时才进入有界 completion retry。

### Why

真实 HWND 已经到 endpoint 并不代表用户一定能看到它。如果 source 仍 cloaked 而 proxy root 已撤，用户会看到空白；如果 proxy 和 real source 无约束同时可见，又会出现 duplicate/flash。真正需要原子化的是谁拥有可见像素。

### Rejected / Do not reintroduce

- 不允许“先全部 cloak，稍后再发布 cover”。
- 不允许 cover 丢失后什么都不做、先空等 timer 才首次恢复 real source。
- 不把资源 Dispose 当作 authority transfer。

### Evidence

- `f444f2897d1a741d2478a5d9af15744ed6a99716`。
- `bb45739d49b16b4e609333476888f65f402fb17b`。
- 当前 `EdgeCapsuleQueueCompositionProxy.Handoff.cs`。
- 当前 `FinishEdgeCapsuleQueueCompositionProxy` 的 cover-loss 恢复顺序。

---

## D-010 — Successor 继承 predecessor 的 live authority

**Status:** Accepted

### Decision

同一 monitor/edge queue 上已有 active proxy 时，新事务作为 successor generation：复用同一 output HWND / DComp target，从 predecessor 当前呈现 sample 重新基线化，carry forward predecessor 仍拥有的成员和 cloaked real source；引入新 source 时可以用 predecessor live surfaces + 新 live sources 组成短暂 admission cover。只有现有 output envelope 已覆盖 successor 需要区域时才允许直接 admission。

### Why

把 successor 当成一次新的冷 proxy，会产生两套 cloak/source 集合和两个 output HWND 的 z-order，并可能让 predecessor 的 stationary peer 在 root replacement 时消失。

### Rejected / Do not reintroduce

- 不先 dispose predecessor 再冷启动 successor。
- root replacement 不只带本次 changed member 而遗漏 predecessor stationary peers。
- 不为扩大 successor envelope 移动仍承载 predecessor root 的 output HWND。

### Evidence

- `be94659d555b79759853fb392b1af5a4577d19fa`。
- `bb45739d49b16b4e609333476888f65f402fb17b`。
- 当前 successor admission / carry-forward 代码。

---

## D-011 — Floating drag 是独立且持久复用的真实 HWND

**Status:** Accepted

### Decision

脱离队列/跨边拖拽使用 `EdgeCapsuleDragWindow`，不复用 docked 单边 host。controller 序列化 capsule reorder，因此进程级只维护一个 pooled drag HWND；其 HWND 和 WPF tree 长期存在，lease 时只重新绑定 paper-specific presentation。

### Why

Docked capsule 有 wall-side straight edge、close segment、bounded capacity 和 queue placement；FloatingFree 是对称自由胶囊。复用同一 host/visual tree 会让 edge column、corner、DPI 和 width 状态相互污染；每次重新 Create/Show/Close 又会把 WPF Window 冷启动放回输入热路径。

### Rejected / Do not reintroduce

- 不让 `EdgeCapsuleHost` 临时变成 floating pill。
- 不为每次拖拽重建 WPF visual tree / HWND。
- 在 controller 仍保证单 drag session 时，不建立多个备用 drag host 池。

### Evidence

- `cc9906ab940bc0e11905401fb079fdedc1f05427` — `fix(edge): keep one persistent drag host`。
- 当前 `EdgeCapsuleDragWindow.cs`。

---

## D-012 — Presenter transition 使用 Rendering cadence；watchdog 只救活

**Status:** Accepted

### Decision

正常 edge transition 由 presenter 持有，并由同 Dispatcher 的 shared `EdgeCapsuleFrameScheduler` 在 `CompositionTarget.Rendering` 上推进。liveness watchdog 只在 active transition 未及时得到 Rendering 推进时补一次 frame；具体阈值属于实现参数，不是架构决策。

### Why

长期 `DispatcherTimer`/高频 timer 会与 WPF compositor cadence 漂移；纯 Rendering 又可能在某些调度边界失去活性。最终选择是一套正常 frame clock + 一个按需 rescue，而不是两套持续竞速的 frame producer。

### Rejected / Do not reintroduce

- 不恢复长期固定间隔 `DispatcherTimer` 作为第二动画引擎。
- watchdog 不在无 active transition 时持续运行。
- pending reconcile / external native batch 未释放时，watchdog 不穿透 ownership 强行推进。

### Evidence

- `303c9ebd22fa69d75a32bb7cb923c42cfb512fb5`。
- `708dcd267827cee9f9174d9e9c49303ae3b760e8`。
- `e5e07526da0d9b6178975e5c7e90debf4d4a6241`。
- `ce406c10507418c67b32bd17b9c7b99819201145`。
- `a3c8b62962178ca5d6a63f5c555c7c0a847eee56`。
- 当前 `EdgeCapsuleFrameScheduler.cs`。

---

## D-013 — Proxy handoff 等待真实 WPF terminal presentation，而不是靠额外 delay

**Status:** Accepted

### Decision

proxy animation 到逻辑终点后，最终 real/WPF presentation 必须先完成 endpoint flush/apply、必要的 WPF render turn、真实 bounds verify 和 authority swap 条件，再允许撤 compositor cover。

completion timer 只能发起完成尝试，本身不是 WPF terminal frame 已就绪的证明；尝试失败时 cover 继续持有 authority，并在后续重试中重新走 endpoint 准备与验证。

### Why

DComp 动画结束和 WPF 最后一帧真正进入 DWM 并非天然同一个调用点。固定 completion guard 只能降低 race 概率，不能构成 correctness proof。

### Rejected / Do not reintroduce

- 不使用“再延迟几毫秒”作为 terminal-frame 正确性的证明。
- 不把 completion timer 到期等同于 WPF 已完成。
- endpoint apply/layout/verify 失败时不先撤 cover。

### Evidence

- `c9aa1910d6533e95947567e4b057e87b0e93f7ae`。
- `bcc6740e992af048cc28f8b810168301434f9555`。
- `4200162d363dc4f22bacc198e599ba917da3f36f`。
- `9f7a04ba4c1d01103fb53679f3b939b9e16083d0`。
- 当前 `FinishEdgeCapsuleQueueCompositionProxy`。

---

## D-014 — Pointer truth 来自 presented `InteractiveBounds`

**Status:** Accepted

### Decision

Hover、Preview 和 preview corridor 的物理命中，以当前用户实际看见/已应用 presentation 的 `InteractiveBounds` 为准。WPF/native enter/leave 只是触发重新采样的 signal。proxy 拥有可见像素时，也消费同一 sampled logical frame 的 `InteractiveBounds` 做输入路由。

### Why

透明 chrome、bounded host capacity、DComp translation 和 WPF transition 会让 HWND rectangle 与真正可交互 capsule rectangle 不一致。native leave 不是最终 truth，整个 host/proxy envelope 也不能变成 hit area。

### Rejected / Do not reintroduce

- 不直接在 `MouseEnter/MouseLeave` handler 写 Hover business state。
- 不把透明 host capacity 或 compositor envelope 当 capsule/corridor bounds。
- 预测算法不能否决“已经物理离开整个合法区域”的事实。

### Evidence

- 当前 `EdgeCapsulePresenter.Reconcile`。
- 当前 `EdgeCapsuleQueueCompositionProxy.Routing.cs`。
- `dcf2033d41b3b52c3036eb6a3d4204b2b3441cd9` / `e15796d57f6126e242445c54a8813fd022c35978`。

---

## D-015 — 四类知识分工；不维护第二套完整架构或长期验收矩阵

**Status:** Accepted

### Decision

PaperTodo 长期维护四类互补知识：

- `ARCHITECTURE.md`：当前架构事实、数据流和 ownership。
- `DECISIONS.md`：明确取舍、失败路线和可复用踩坑结论。
- `AGENTS.md`：Agent 执行规则、真正的硬约束、提交/发布/维护规则。
- 关键代码注释：局部 why、不变量和危险边界。

`AGENTS.md` 不再重复完整 Edge 实现链、插件 mini fallback、持久化结构等当前架构事实；需要执行层禁令时只保留最短规则并指向 Architecture / D-xxx。易变数值和普通实现参数只留代码。

不建立需要人工长期同步的场景矩阵/验收清单。可执行正确性优先进入编译、测试、probe、诊断日志或任务当次验证记录。

### Why

同一架构事实复制到多份说明会让文档之间出现漂移；验收矩阵又会重复产品状态、代码路径和测试意图。知识按“现在是什么 / 为什么 / Agent 怎么做 / 局部 why”分层后，每种信息只有一个自然 owner。

### Consequences

- 架构事实变化同步 Architecture。
- 产生或推翻取舍同步 Decisions。
- Agent 执行约束变化同步 Agents。
- 局部隐藏不变量变化同步附近关键注释。
- AGENTS 中发现成段的“当前实现说明”时，优先迁移到 Architecture/Decisions，而不是继续扩写。
- 一次性验收过程不独立沉淀为长期手工矩阵。
- `docs/edge-presentation-v3-lite.md` 不再作为并行当前架构文档保留；历史演进由 git/PR 保存。

---

## D-016 — V3 Lite 完成后删除一次性验证脚手架

**Status:** Accepted

### Decision

PR #94 为完成 V3 Lite 曾引入 source export、finalizer、clean-state verifier 等一次性 workflow/script。最终实现验证完成后，这些迁移脚手架被删除，主线只保留生产代码、通用 CI 和仍有长期价值的 diagnostics。

### Why

一次性迁移脚本适合受控大重构，但完成后继续留在仓库会制造“它是不是仍是生产流程的一部分”的歧义，并增加根目录/Actions 噪音。通用 diagnostics 与一次性 orchestrator 是不同类别。

### Evidence

- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb`。
- `899f3cd284eaa19b45cc8ae6a953f5500ca2a57b` — PR #94 merge。

---

## D-017 — Hardcodet 托盘继续使用 WPF `IconSource`，不回退到 `System.Drawing.Icon`/手动 popup 修补

**Status:** Accepted

### Decision

Hardcodet 托盘图标使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`；外部 `PaperTodo.ico` 保持用户覆盖入口。托盘菜单按当前状态在打开时重建，不用手动弹菜单、预热菜单或全局鼠标轮询绕过控件本身生命周期。

### Why

曾经把托盘恢复成 `System.Drawing.Icon` / 非标准 popup 修补时，会重新引入首次右键菜单定位异常、首次点击纸片被吞以及 focus/menu-mode 时序问题。问题本质不是“菜单没预热”，而是宿主控件的 icon/menu/focus 生命周期被绕开。

### Rejected / Do not reintroduce

- 不把 Hardcodet `IconSource` 换回 `System.Drawing.Icon` 作为默认路径。
- 不用手动 popup、预热菜单或全局鼠标轮询修首次菜单问题。
- edge context-menu 的 focus 清理不能无条件前移到 WPF menu mode 尚未退出的时点。

### Evidence

- 当前 `AppController` 托盘创建/重建路径。
- `DeepCapsuleContextMenuSession` 及 edge menu focus cleanup。
- 相关托盘回归修复提交历史。

---

## D-018 — Edge plugin mini 由宿主持有 presentation/queue authority，并按能力安全降级

**Status:** Accepted

### Decision

插件可以提供专属 mini、可迁移纯 WPF View、自定义 capsule、标准结构化 capsule 和 plain-text 等不同能力，但 edge preview 的窗口、队列、尺寸会话和输入 authority 始终属于宿主。

宿主从能力最强且安全的 presentation 向结构化/plain-text fallback 降级；Web mini 在显式 ready 前保留宿主 fallback；真实 WPF View 迁移只对 provider 明确声明且宿主可安全接管的纯 WPF 内容启用。

### Why

如果插件自己拥有 preview HWND、队列 placement 或另一份 authoritative state，Edge 会重新出现多套 geometry/visibility/input ownership。直接搬运 `HwndHost`、WebView2、已挂载控件等 foreign/attached tree 也会把独立 native 生命周期带入 bounded host，破坏 V3 Lite 的 surface 边界。

对真实 WPF View 使用受控迁移 + snapshot handoff，可以避免维护第二份业务 UI，同时不在每一帧持续截图。

### Rejected / Do not reintroduce

- 不让插件创建/拥有 edge queue HWND 或 placement authority。
- 不把 `Window`、`HwndHost`、WindowsFormsHost、WebView2 或已挂载控件当作可直接迁移的 Native mini tree。
- 不在 Web mini 未 ready 时清空结构化 fallback。
- 不为 mini preview 维护持续截图循环或第二份 authoritative plugin state。
- 不让插件各自复制宿主的自动宽度/队列测量算法。

### Evidence

- 当前 plugin contract / `PaperBodyPluginRegistry`。
- 当前 edge mini presentation / WPF migration / Web mini host 代码。
- Architecture 5.3。

---

## D-019 — 内置 Note 编辑与浏览共享同一个 `MarkdownTextBox`

**Status:** Accepted

### Decision

内置 Note 的编辑态和浏览态复用同一个 `MarkdownTextBox`，通过 presentation/interaction 状态切换，而不是建立两套文本控件并同步内容、滚动和选区。

### Why

两套文本 surface 会产生滚动位置、换行测量、selection、caret、图片布局和编辑提交时序的双向同步问题。对桌面纸片这种持续存在的轻量编辑 surface，单控件切状态的 ownership 更简单，也能避免“浏览显示”和“真实编辑内容”短暂不一致。

### Rejected / Do not reintroduce

- 不为了浏览态视觉方便复制第二个 Markdown 编辑/显示控件并做双向同步。
- 不无依据删除 `MarkdownTextBox` 的长度/布局保护；若保护策略需要改变，应以真实 WPF 性能证据重新决策。

### Evidence

- 当前 `PaperWindow` / Markdown note UI 实现。
- `MarkdownTextBox` 的浏览/编辑状态与布局保护代码。
