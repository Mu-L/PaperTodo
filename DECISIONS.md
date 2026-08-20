# PaperTodo 架构决策

> 本文记录 **已经做出的技术选择、明确取舍、被淘汰的路线，以及值得防止重复踩坑的结论**。
>
> 它不是 changelog，也不是验收清单。只要一次变更形成了会影响后续实现判断的明确选择、取舍或可复用踩坑结论，就应在同一变更中更新这里；没有新取舍的机械 bugfix、文案或参数调整不需要为了“留痕”新增条目。
>
> - 当前系统“现在是什么”见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。
> - Agent 工作时必须遵守的硬约束见 [`AGENTS.md`](AGENTS.md)。
> - 代码和可观察运行行为仍是最终事实；提交号是决策证据，不是替代代码阅读的规范文本。
> - 本文首次建立时以 `main@626fe60d` 为代码基线，并独立回读 PR #94 / V3 Lite 及其后续收敛提交。

## 维护规则

每条记录尽量回答四个问题：

1. **选择了什么。**
2. **为什么这样选、当时取舍了什么。**
3. **哪些路线已经被证明不应该重新引入。**
4. **从哪里可以看到这个选择真正进入了代码。**

决定是否记录的标准不是“改动大不大”，而是“这次改动是否产生了以后仍需要知道的 why”。如果后来证明某条 decision 已经不成立，应把状态改为 Superseded 并指向新条目，不让旧结论继续伪装成当前规则。

---

## D-001 — 保持“桌面纸片”作为主要交互和对象边界

**Status:** Accepted

### Decision

PaperTodo 当前的主要交互单元仍是一张可独立存在、独立显示和独立交互的桌面纸片。Todo、Markdown/Note、插件 body、胶囊等能力以 paper 为自然组合边界；应用级能力由 controller 协调，但不会在没有新产品决策的情况下自动把所有 paper 行为收束成一个中心主界面。

这条选择约束的是**对象与交互边界**，不是永久禁止新的后端、索引、提醒、外部数据源或知识管理能力。后续如果产品方向明确扩展，应通过新的 decision 更新边界，而不是让这条旧结论反过来否决已经明确的新路线。

### Why

大量现有窗口生命周期、持久化几何、跨纸片链接、托盘恢复、边缘胶囊以及插件 session 都以 `PaperData` / `PaperWindow` 为自然边界。保持这个边界能让新增能力组合到纸片上，而不是默认把所有功能提升到全局 controller。

### Consequences

- `AppController` 可以全局协调，但不吸走所有单纸片业务行为。
- `PaperWindow`、`PaperBodyHost`、edge per-paper presenter/host 保留清晰的 paper ownership。
- 新功能先判断它属于 paper、paper body 还是应用级 runtime；若要改变这个分层，记录新的明确决策。

### Evidence

- 当前 `AppState -> PaperData[] -> PaperWindow` 主结构。
- `AGENTS.md` 的产品边界约束。

---

## D-002 — 业务状态使用 `data.json`，大体积图片资产独立进入 LMDB

**Status:** Accepted

### Decision

`data.json` 是 PaperTodo 的主业务状态协议；图片二进制不进入 JSON，而由 `NoteImageStore` / LMDB 独立保存。

恢复和图片 GC 均采用保守策略：无法证明恢复源或图片引用扫描可信时，宁可保留旧数据/禁用 GC，也不猜测删除。

### Why

JSON 适合可迁移、可恢复的结构化 paper 状态；图片二进制会放大写入、备份和恢复成本。将二者分开后，可以对 JSON 做版本化 snapshot 保存，对图片做引用 reachability 和独立容量管理。

### Rejected / Do not reintroduce

- 不把图片 blob/base64 重新塞回 `data.json`。
- 不在一次失败启动后用“当前默认态”覆盖无法解析的主文件。
- 不在保护 snapshot 无法可靠扫描时继续做破坏性 image GC/id reuse。

### Evidence

- `StateStore.cs` 的 primary/backup/recovery-preservation、版本化写入和 `PrepareForSave`。
- `AppController` 对 `NoteImageStore` 和 protected image IDs 的独立初始化。

---

## D-003 — Crash boundary 不做“最后一次强行保存”

**Status:** Accepted

### Decision

未处理异常的全局边界负责记录 crash log 和结束当前错误路径，不尝试把此刻内存中的整个 `AppState` 强制持久化。

### Why

抛出未处理异常时，内存对象可能已经处于只完成了一半的业务事务中。此时“尽量保存”可能比丢失最后几秒编辑更危险：它可能覆盖此前健康的 `data.json` / backup。

正常 durability 由 idle auto-save、force-save cap 和 `data.backup.json` 提供。

### Rejected / Do not reintroduce

不要在 Dispatcher/AppDomain crash handler 中直接调用一次普通保存流程，除非未来先建立了可证明一致的 crash-safe snapshot 机制。

### Evidence

- `App.xaml.cs` 当前异常处理路径。
- `StateStore` 的正常恢复/backup 机制。

---

## D-004 — Paper body 插件通过 session 边界接入，而不是直接接管 `PaperWindow`

**Status:** Accepted

### Decision

Provider 发现与单纸片 provider session 分开：

- `PaperBodyPluginRegistry` 发现/校验 builtin、Native、Web provider。
- `PaperBodyHost` 只管理一张纸当前 `IPaperBodySession` 的 attach、invoke、commit/cancel/dispose。
- `PaperWindow` 仍然拥有 WPF placement、paper chrome 和 provider 选择。

Native plugin 是 fully trusted / unsandboxed，并且已载入版本不能在同一进程中安全热替换；Web provider 可以重新扫描/加载。

### Why

这样插件只替换“纸片 body”，不会把窗口 placement、保存、edge capsule、主题和宿主生命周期一起变成插件 ABI。

### Rejected / Do not reintroduce

- 不让插件直接成为 `PaperWindow` 生命周期 authority。
- 不假设 Native WPF assembly 可以像 Web 内容一样无代价热替换。

### Evidence

- `PaperBodyHost.cs`。
- `PaperBodyPluginRegistry.cs`，当前 API version `1.8`。

---

## D-005 — 单纸片 Edge 状态必须走 typed Intent → Reducer → Presenter

**Status:** Accepted

### Decision

会改变一张纸 Slot / Visual / Gesture / Preview / Placement 的产品级输入使用 `EdgeCapsuleIntent`；`EdgeCapsuleReducer` 原子地产生完整 `EdgeCapsuleModel`；`EdgeCapsulePresenter` 是该纸 desired model、target plan、transition、applied frame 和 deferred work 的 authority。

队列级 preview owner、transfer corridor、arrange、visual transaction 和 proxy 生命周期由 `AppController` 协调。它可以向多张纸 dispatch intent、捕获起终帧和组织事务，但不持有第二份单纸片 desired model。

### Why

Edge capsule 同时存在单纸片状态与跨纸片会话。若 `PaperWindow`、controller、host 各自直接修改一组 bool/enum，局部修复很容易制造合法性无法枚举的组合；反过来，如果把整个队列会话强塞进每张纸的 reducer，又会复制 owner/corridor/transaction 状态。

Reducer 让单纸片输入表达“发生了什么”，而不是“请把三个内部字段改成什么”；Presenter 让单纸片呈现失效回到同一个 reconcile 管线；controller 只保留天然属于队列层的协调状态。

### Rejected / Do not reintroduce

- 不增加通用 `SetEdgeState(...)` / 一组公开 field setter。
- 不在 `PaperWindow` 再维护第二套单纸片 edge FSM。
- 不让 controller 的 preview/transaction 状态反写成另一份 per-paper model。
- 不为每个新 race 单独增加一对 `pendingX/scheduledX`，绕开已有 dirty/reconcile。

### Evidence

- `EdgeCapsuleModel.cs`。
- `EdgeCapsuleReducer.cs`。
- `EdgeCapsulePresenter.cs`。
- `AppController.EdgeCapsulePreview*.cs` 与 `AppController.EdgeCapsuleVisualTransaction.cs` 的队列级协调。

---

## D-006 — Queue placement 与物理几何各只有一个 authority

**Status:** Accepted

### Decision

- 队列的 index / master offset / slot count 只由 `EdgeCapsuleQueueCoordinator` 计算。
- monitor/edge/DIP 到物理 `DeviceScreenRect` 的 docked geometry 只由 `EdgeCapsuleGeometry` 计算。
- 队列不分页；超出工作区的成员仍保持完整顺序，可以直接延伸出屏幕。

### Why

过去最危险的一类 edge bug 来自“每个窗口都能从邻居/当前 HWND 猜一次队列位置”和“多个路径复制像素取整公式”。在 PerMonitorV2、多 DPI、左右墙、预览宽度和跨屏情况下，这些复制几乎必然产生 1px 或一帧分歧。

分页则会把纯 placement 再升级为可变 visibility/state ownership，给 reorder、preview corridor、drag 和 master offset 增加一套隐藏状态。

### Rejected / Do not reintroduce

- 不按当前工作区高度推导“安全容量”。
- 不加入 overflow page/header/page number/自动翻页。
- 不在动画、measure、host apply 或 controller 里复制 docked physical-pixel 公式。

### Evidence

- `EdgeCapsuleQueueCoordinator.cs`。
- `EdgeCapsuleGeometry.cs`。
- `AGENTS.md` 的 no-paging 约束。

---

## D-007 — V3 Lite 采用 per-paper bounded live host

**Status:** Accepted

### Decision

每张 docked capsule 的真实 HWND 由 `EdgeCapsuleHost` 长期拥有。`HostBounds` 是当前 host generation 的稳定 bounded capacity；`Bounds` 是当前可见 WPF shape。

容量只覆盖该纸在当前 monitor/DPI/edge 上真实可能需要的最大 Preview，不扩成整个工作区或整条队列。Late-bound plugin preview 可以让 capacity 增长，但正常 Resting/Hover/Preview 不靠反复 resize native host 来做形变。

### Why

早期/中间方案在两个方向上都付出了额外复杂度：

1. endpoint-sized HWND 需要 Hover/Preview 每轮改变 native surface identity，导致 compositor translation 和 resize ownership 相互缠绕；
2. 过大的长期透明 host 又会扩大 hit-test、z-order、资源和“透明区域到底属于谁”的问题。

bounded live host 把两者分开：native capacity 稳定且有限，真正的 shape 在 WPF visual 内变化。

### Rejected / Do not reintroduce

- 不恢复“当前端点多宽，真实 HWND 就逐帧多宽”的 resize 架构。
- 不为省 handoff 把每张纸扩成 work-area-sized / queue-sized 透明合成面。
- `HostBounds` 大于 `Bounds` 的透明 capacity 不能被当成交互区域。

### Evidence

- `32866e9085c2002c3411d4a2c93a96903fe6c9ee` — `refactor(edge): establish V3 Lite bounded live hosts`。
- `ca70631d2c3b77a883a5c78f5a912cfe2ccc9294` — late-bound plugin preview capacity。
- 当前 `EdgeCapsuleTargetPlanner.cs` / `EdgeCapsuleHost.cs`。

---

## D-008 — WPF 拥有 shape；DirectComposition 只允许 live-surface translation

**Status:** Accepted

### Decision

V3 Lite 的最终职责切分：

**WPF / bounded host：**

- width / height morph
- Resting / Hover / Active / Preview shape
- rounded geometry
- content / opacity
- presentation / interactive bounds

**DirectComposition queue proxy：**

- `CreateSurfaceFromHwnd` 获取真实 live HWND surface
- 保持 surface identity/尺寸不变
- 只改变 X/Y offset
- 在 cover 下让 real HWND 一次 settle 到 endpoint
- 使用同一 logical frame 路由 proxy 期间的输入，不发明第二套 hit geometry

### Why

当 compositor 同时拥有 translation、clip、scale、resize、snapshot，而 WPF 也在改变真实 HWND/visual size 时，就会出现两个 presentation model：一个描述业务 endpoint，另一个描述用户实际看到的中间帧。successor、pointer hit test、DPI handoff 和失败 rollback 都必须额外回答“此刻谁才是真的”。

translation-only 把 compositor 限制成“位置加速层”，而不是第二套 UI renderer。

### Rejected / Do not reintroduce

以下能力不是“目前没用”，而是在 V3 Lite production translation backend 中被明确禁止：

- bitmap snapshot / frozen frame
- clip resize
- scale resize
- effect-based resize
- Reveal / Conceal resize handoff
- deferred resize state machine
- 用 compositor opacity/bitmap trick 模拟 WPF Preview shape

PR #94 中间阶段确实存在 snapshot preparation、pointer composition proxy 等实现；最终收敛主动删除了这些路径，因此不能把它们当作“现成旧代码，遇到 bug 可以再开回来”。

### Evidence

- `32866e9085c2002c3411d4a2c93a96903fe6c9ee` — bounded host 职责切分写入代码/Agent 约束。
- `d4af6affc0d5b704e20e020ae9e9621170613c8c` — `refactor(edge): finish V3 Lite translation architecture`，删除 snapshot/pointer-proxy 路径并把 forbidden capabilities 固化到类型边界。
- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb` — closeout 验证明确检查 live-surface bridge 且拒绝 clip/scale/effect/snapshot。
- 当前 `EdgeCapsuleQueueCompositionProxy.Visuals.cs` 只对 visual 设置 X/Y offset。

---

## D-009 — Visual authority 必须显式交接；失败路径不能出现 all-hidden gap

**Status:** Accepted

### Decision

Queue compositor、真实 docked HWND 和 floating drag HWND 不是“谁最后写 wins”，而是显式的 visual authority。

任意 publication / successor / handoff / rollback 边界都必须保证至少一个可见 authority 存在。DComp root replacement 与 DWM cloak/uncloak 通过可验证的 transaction boundary 协调；失败时优先恢复真实可见 source 或 predecessor root，而不是等待普通动画时钟猜测恢复。

cover 丢失时必须立即尝试恢复真实 HWND。只有即时恢复本身失败时才进入有界 completion retry；重试不是把已丢失的 cover 继续当作视觉 authority。

### Why

只把“真实 HWND 已经到 endpoint”当作成功条件是不够的。如果真实 HWND 仍 cloaked，而 proxy root 已经撤掉，用户会看到确定性的空白帧；相反，如果 proxy 和 real source 同时无约束可见，又会出现 duplicate/flash。

真正需要原子化的是 **谁拥有可见像素**，不是某一个字段值。

### Rejected / Do not reintroduce

- 不允许“先全部 cloak，稍后再发布 cover”。
- 不允许 cover 丢失后什么都不做、先空等一次 50ms timer 才首次恢复 real source。
- 不把资源 Dispose 当作 authority transfer；先完成 source ownership，再退休 COM resources。

### Evidence

- `f444f2897d1a741d2478a5d9af15744ed6a99716` — `seal preview authority boundaries`。
- `bb45739d49b16b4e609333476888f65f402fb17b` — `harden successor authority boundaries`，successor union live cover + 统一 post-endpoint QPC。
- 当前 `EdgeCapsuleQueueCompositionProxy.Handoff.cs` 的 rollback / uncloak / root swap。
- 当前 `FinishEdgeCapsuleQueueCompositionProxy` 先走 `ReleaseAfterCoverLoss()`，即时恢复失败后才安排 completion retry。

---

## D-010 — Successor 继承 predecessor 的 live authority，而不是重新冷启动一套代理

**Status:** Accepted

### Decision

同一 monitor/edge queue 上已有 active proxy 时，新事务作为 successor generation：

- 复用同一 output HWND / DComp target。
- 从 predecessor 当前呈现 sample 重新基线化，而不是回到旧逻辑 start。
- carry forward predecessor 仍拥有的成员和 cloaked real source。
- 有新 real source 时，可用 predecessor live surfaces + 新 live sources 组成短暂 admission cover。
- 只有现有 output envelope 已覆盖 successor 需要的物理区域时才允许直接 successor admission。

### Why

重新创建一个互不相关的临时 proxy，会迫使 controller 同时维护两个 cloak/source 集合和两个 output HWND 的 z-order；更严重的是 predecessor 中静止成员的真实 HWND 仍被 cloak，而 successor 如果没有 carry forward 它们，它们会直接消失。

### Rejected / Do not reintroduce

- 不把 successor 当成“先 dispose predecessor，再重新启动一次冷代理”。
- 不在 root replacement 时只带本次 changed member，遗漏 predecessor stationary peers。
- 不为了 successor envelope 变大直接移动仍承载 predecessor root 的 output HWND。

### Evidence

- `be94659d555b79759853fb392b1af5a4577d19fa` — `preserve live translation successors`。
- `bb45739d49b16b4e609333476888f65f402fb17b` — successor authority hardening。
- 当前 `CarryForwardEdgeCapsuleQueueProxyMembers`、`TryCreate` successor admission 和 `SnapshotStaticCoverSources`。

---

## D-011 — Floating drag 是独立且持久复用的真实 HWND

**Status:** Accepted

### Decision

脱离队列/跨边拖拽使用 `EdgeCapsuleDragWindow`，它是完整的 floating pill，不复用 docked 单边 host。

因为 controller 会序列化 capsule reorder，进程级只维护一个 pooled drag HWND。该 HWND 和 WPF tree 在应用生命周期内持续存在，lease 时只重新绑定 paper-specific 文本、brush 和 geometry；仅在 host 不可用或 dispatcher shutdown 时销毁。

### Why

Docked capsule 有 wall-side straight edge、close segment、bounded capacity 和 queue placement；FloatingFree 是对称自由胶囊。把同一个 visual tree/host 在两者之间变形，会把 edge column、corner、DPI 和 width 状态带入拖拽事务。

反复为每次 drag Create/Show/Close WPF Window 又会把 HWND/visual tree 冷启动放回输入热路径。

### Rejected / Do not reintroduce

- 不让 `EdgeCapsuleHost` 临时变成 floating pill。
- 不为每次拖拽重建 WPF visual tree/DropShadowEffect/新 HWND。
- 在 controller 仍保证单 drag session 的前提下，不建立多个“备用 drag window 池”。

### Evidence

- `cc9906ab940bc0e11905401fb079fdedc1f05427` — `fix(edge): keep one persistent drag host`。
- 当前 `PaperWindow.cs` 对 docked/floating owner 的字段级分离。
- 当前 `EdgeCapsuleDragWindow.cs` 的 process-global pooled host。

---

## D-012 — Presenter transition 使用 Rendering cadence；watchdog 只救活，不成为第二时钟

**Status:** Accepted

### Decision

正常 edge transition 由 presenter 持有，并由同 Dispatcher 的 shared `EdgeCapsuleFrameScheduler` 在 `CompositionTarget.Rendering` 上推进。

Scheduler 每个真实 frame 只采样一次 pointer/time，并按 native batch group 提交。若 active transition 因 Rendering callback 没有及时推进，liveness watchdog 可以 demand-driven 地补一次 `AdvanceSharedFrame`，随后继续等待正常 Rendering；具体阈值属于实现参数，不是架构决策本身。

### Why

独立 `DispatcherTimer`/高频 timer 作为长期动画时钟会与 WPF compositor cadence 漂移；但纯 Rendering 在某些 pending reconcile、重复 callback、量化 no-op 或 UI 调度边界上又可能出现 transition 活性问题。

最终选择是“一套正常 frame clock + 一个只在超期时救活的 watchdog”，而不是两套持续竞速的 frame producer。

### Rejected / Do not reintroduce

- 不恢复长期固定间隔 `DispatcherTimer` 作为第二动画引擎。
- watchdog 不应在没有 active transition 时持续运行。
- pending reconcile / external native batch 未释放时，watchdog 不能穿透 ownership 强行推进。

### Evidence

- `303c9ebd22fa69d75a32bb7cb923c42cfb512fb5` — unblock render-priority scheduler cadence。
- `708dcd267827cee9f9174d9e9c49303ae3b760e8` — wake quantized no-op transitions。
- `e5e07526da0d9b6178975e5c7e90debf4d4a6241` — keep liveness watchdog armed。
- `ce406c10507418c67b32bd17b9c7b99819201145` — enforce watchdog deadlines。
- `a3c8b62962178ca5d6a63f5c555c7c0a847eee56` — wake transitions off dispatcher timer。
- 当前 `EdgeCapsuleFrameScheduler.cs`。

---

## D-013 — Proxy handoff 等待真实 WPF terminal presentation，而不是靠额外时间延迟掩盖

**Status:** Accepted

### Decision

当 proxy animation 到达逻辑终点时，最终 real/WPF presentation 必须先真实提交到 terminal geometry，再允许撤掉 compositor cover。handoff 的正确条件是 endpoint 已 flush/apply、必要布局已进入 WPF render turn、真实 bounds 已验证并能完成 authority swap，而不是“动画持续时间到了 + 再多等 N ms”。

completion timer 可以负责**发起一次完成尝试**，但它本身不是 WPF terminal frame 已经就绪的证明；尝试失败时 cover 继续持有 authority，并在后续重试中重新走 endpoint 准备与验证。

### Why

DComp 动画结束和 WPF 最后一帧真正进入 DWM 并非天然同一个调用点。如果先撤 cover，再等 WPF terminal frame，可能出现 1px/一帧回弹、compact title 位置变化或短暂旧 geometry。

追加固定 completion guard 只能把 race 改成概率更低的 race，并会随刷新率/机器负载变化。

### Rejected / Do not reintroduce

- 不使用“再延迟几毫秒”作为 terminal-frame correctness 的证明。
- 不把 proxy completion timer 到期等同于 WPF 已完成。
- 不在 endpoint apply/layout/verify 失败时先撤 cover。

### Evidence

- `c9aa1910d6533e95947567e4b057e87b0e93f7ae` — settle proxy endpoint before handoff。
- `bcc6740e992af048cc28f8b810168301434f9555` — let WPF final frame precede proxy handoff。
- `4200162d363dc4f22bacc198e599ba917da3f36f` — release preview surface at compact geometry。
- `9f7a04ba4c1d01103fb53679f3b939b9e16083d0` — 明确 revert “仅靠额外 completion guard” 的 workaround。
- 当前 `FinishEdgeCapsuleQueueCompositionProxy` 的 flush / apply / render / verify / release 顺序。

---

## D-014 — Pointer truth 来自实际 presented `InteractiveBounds`，不是 WPF enter/leave 本身

**Status:** Accepted

### Decision

Hover、Preview 和 preview corridor 的物理命中，以当前用户实际看见/已应用 presentation 的 `InteractiveBounds` 为准。WPF/native enter/leave 只是触发重新采样的 signal，不能直接把 Visual 状态写成 Hover/Resting。

空白 transfer corridor 可以在合法队列区域内维持短暂浏览意图，但不是 capsule hit area；`HostBounds`、透明 bounded capacity 和 proxy output envelope 都不能扩大成输入区。proxy 拥有可见像素时，也必须消费同一 sampled logical frame 的 `InteractiveBounds` 来做输入路由。

### Why

透明 chrome、bounded host capacity、DComp translation 和 WPF transition 会让 HWND rectangle 与真正可交互 capsule rectangle 不相同。把 native leave 当最终 truth 会在视觉仍位于指针下时提前关闭；把整个 host/proxy envelope 当 hit area 又会产生“透明区域吸住鼠标”。

### Rejected / Do not reintroduce

- 不直接在 `MouseEnter/MouseLeave` handler 修改 Hover business state。
- 不把透明 host capacity 或 compositor envelope 当 preview corridor / capsule bounds。
- 预测算法不能否决“已经物理离开整个合法区域”的事实。

### Evidence

- 当前 `EdgeCapsulePresenter.Reconcile` 每 frame 基于 presented/applied frame 重采样 pointer。
- 当前 `EdgeCapsuleQueueCompositionProxy.Routing.cs` 根据 sampled `InteractiveBounds` 判断和转发 proxy 输入。
- `dcf2033d41b3b52c3036eb6a3d4204b2b3441cd9` / `e15796d57f6126e242445c54a8813fd022c35978` 的 preview leave 修复。

---

## D-015 — 四类知识实时同步；不长期维护第二套验收矩阵

**Status:** Accepted

### Decision

PaperTodo 长期维护四类互补知识：

- `ARCHITECTURE.md`：当前架构事实、数据流和 ownership。
- `DECISIONS.md`：每次形成明确取舍时记录 why、被否决路线和可复用踩坑结论。
- `AGENTS.md`：Agent 执行时必须遵守的核心硬约束和工作规则。
- 关键代码注释：只解释离开代码现场就容易误改的局部原因、不变量和危险边界。

代码发生变化时，必须判断这四类知识中哪些被影响，并在同一变更中实时同步；“不需要更新”应来自确实没有相关事实/取舍变化，而不是任务结束时忘了检查。

涉及架构、ownership、历史方案或文档整理的任务，先完整读现有文档、相关代码和提交记录，确认事实后再统一修订文档；不要依赖当前聊天上下文边发现边写出一套可能随后被推翻的说明。

不为了覆盖当前行为再建立一份需要人工长期同步的场景矩阵/验收清单。可执行的正确性优先进入测试、probe、日志断言或任务当次验证记录；临时手工验收可以留在 PR/issue/task 中，不自动升级成第五套长期事实源。

### Why

文档漂移通常来自两类重复：一类是同一个架构事实被复制进多份说明，另一类是验收矩阵把产品状态、代码路径和测试意图又抄了一遍。先完成事实核对，再按四类职责同步，可以让 Architecture 回答“现在是什么”、Decisions 回答“为什么”、Agents 回答“执行时不能违反什么”、代码注释解释“这一小段为什么必须这样”。

### Consequences

- 改变架构事实时，同步 Architecture。
- 产生或推翻明确取舍时，同步 Decisions。
- 改变 Agent 执行约束时，同步 Agents。
- 改变依赖局部隐藏不变量的代码时，同步附近关键注释。
- 一次性验收过程不独立沉淀为长期手工矩阵。
- 不再保留并行描述“当前 V3 Lite 架构 + verification gates”的 `docs/edge-presentation-v3-lite.md`；历史演进由 git/PR 保存，当前事实回到根文档。

---

## D-016 — V3 Lite 完成后删除一次性验证脚手架，而不是永久保留临时治理层

**Status:** Accepted

### Decision

PR #94 为完成 V3 Lite 曾经引入 source export、finalizer、clean-state verifier 等一次性 workflow/script。最终实现经过 Debug/Release/edge checks 和 architecture-shape 检查后，这些临时脚手架被删除，主线只保留生产代码、通用 CI 和必要 diagnostics。

### Why

一次性迁移脚本和专项验证 workflow 很适合跨大量文件的受控重构，但完成后继续留在仓库会制造“它是不是仍然是生产流程的一部分”的歧义，也增加根目录/Actions 噪音。

这条原则与“诊断代码永远删除”不同：通用、仍能证明当前运行机制的 diagnostics 可以长期保留；只服务一次迁移的 orchestrator 应在任务完成后清走。

### Evidence

- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb` — `refactor(edge): complete V3 Lite closeout` 删除 PR94 一次性 finalizer/source-export infrastructure。
- `899f3cd284eaa19b45cc8ae6a953f5500ca2a57b` — PR #94 最终 merge。
