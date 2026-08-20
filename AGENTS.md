# PaperTodo Agent 备忘

本文件只记录“不通读历史和全量代码很难知道”的项目约束。代码是真相；普通文件职责、字段含义、WPF/C# 常识不要写进来。

## 项目知识入口

开始涉及架构、ownership、跨子系统行为或历史方案的任务前，先按职责读取对应文档，不要只依赖当前对话、PR 描述或旧 Agent 记忆：

- [`ARCHITECTURE.md`](ARCHITECTURE.md) 记录**当前实际架构**、数据流和 ownership；改变架构事实时必须在同一变更中更新。
- [`DECISIONS.md`](DECISIONS.md) 记录**为什么选择当前路线、哪些路线已被否决以及关键踩坑**；产生新的明确取舍、推翻旧选择或准备恢复旧实现时必须同步核对和更新。
- `AGENTS.md` 只保留 Agent 必须遵守的隐藏硬约束和执行规则；可以为可执行性保留必要的不变量，但不要完整复述前两份文档成为第二套架构说明。
- 关键代码注释解释局部的 why、不变量和危险边界；改变对应实现时必须一起核对，不能让注释描述旧机制。

文档或注释与代码冲突时，不要默认相信任一方，也不要悄悄按旧文档改代码；先用当前代码、提交历史和可观察行为核对真实状态，再同步修正。

## 文档与代码同步流程

- 涉及架构、ownership、历史方案或文档整理时，先完整检查现有文档，再读相关代码和 git 提交记录；事实核对完成后再统一修订，不要边发现边写、让前面刚写的结论被后面的代码历史推翻。
- **每次代码变化都要显式检查四个知识落点：关键代码注释、`ARCHITECTURE.md`、`DECISIONS.md`、`AGENTS.md`。** 只更新真正受影响的部分，但不能因为改动“小”就跳过检查。
- 架构事实变化与 Architecture 同一变更更新；形成/推翻明确取舍与 Decisions 同一变更更新；Agent 执行约束变化与 Agents 同一变更更新；局部隐藏不变量变化与附近关键注释同一变更更新。不要计划“最后再补文档”。
- 不新增并行描述“当前完整架构”的专题文档；专题文档只能补充根文档没有承载的局部材料，并明确链接当前 Source of Truth。过期专题说明应删除或标记为历史，而不是继续和根文档竞争。
- 一次性验证结果、PR 过程和临时手工场景不升级成长期验收矩阵；能长期证明的正确性优先放在编译、测试、诊断日志和可执行检查中。

### `ARCHITECTURE.md` 写入规则

- `ARCHITECTURE.md` 是**当前状态文档**，不是历史日志。更新时优先修改或重写已有相关章节，使正文只描述变更后的最终架构；被替代的旧机制应从当前正文删除，不要用“以前 / 后来 / 这次改了”把演进过程累积进去。
- 以下变化通常必须更新：subsystem 的 authority / ownership、主要数据流、状态或持久化协议、paper/window/plugin 生命周期边界、关键运行时之间的职责切分、重要 OS/进程集成方式，以及仓库主结构。只改颜色、文案、普通常量或不改变职责边界的局部实现通常不写。
- 写入前以**当前代码**为准重新核对相关入口、owner 和调用链；不要从聊天记忆、PR 描述或旧文档直接推导“现在是什么”。无法从当前代码确认的猜测、计划和候选方案不写进 Architecture。
- Architecture 记录“选择后的结构结果”，不展开“为什么没选另一个方案”。需要解释取舍、历史坑或禁止回退路线时，在 `DECISIONS.md` 记录，并从 Architecture 保持简短链接或职责说明。
- 避免把易变实现参数写成架构合同，例如毫秒数、重试次数、诊断阈值；只有该数值本身构成稳定协议/兼容边界时才记录。
- 新增 subsystem 时先判断能否放入已有 ownership 表、数据流或章节；只有确实形成新的一级边界才新增章节，避免按类名机械扩张文档。
- 架构代码与 Architecture 应在**同一变更**中到达一致状态；如果本次只是纠正文档与既有代码的偏差，可以单独提交文档修正，但必须在提交说明中体现这是事实校准而不是架构变更。

### `DECISIONS.md` 写入规则

- `DECISIONS.md` 记录的是**以后仍需要知道的 why**。当本次工作形成明确技术/产品取舍、否决一条看似可行的路线、确认一个容易被重复踩到的结构性坑、改变兼容或 ownership 原则，或未来 Agent 很可能再次提出同一方案时，应在同一变更中更新。
- 普通 bugfix、参数微调、UI 调整、测试结果、临时诊断过程、PR 逐步试错不自动新增 decision。只有这些工作最终产生了可复用的选择或教训时，才提炼成决策条目。
- 写新条目前先搜索现有 D-xxx：如果只是同一决策的澄清、证据补充或边界收紧，直接更新原条目；不要为了每次提交新增重复 decision。只有出现新的独立选择，或旧选择被真正替代时才新增编号。
- 新条目使用下一个连续 `D-xxx`，至少包含 `Status`、`Decision`、`Why` 和 `Evidence`；确实存在危险旧路线时再写 `Rejected / Do not reintroduce`，有长期影响但不适合放正文时可加 `Consequences`。不要为了模板完整机械写空洞段落。
- `Decision` 写最终选择；`Why` 写核心约束和取舍；`Rejected / Do not reintroduce` 只记录已经有证据证明危险、复杂或不符合当前路线的方案，不把所有没选中的可能性列成禁令。
- `Evidence` 优先指向当前代码中的文件/类型/关键入口，并在历史因果确实重要时补关键 commit/PR；不要把聊天记录当长期证据，也不要堆一串与决策无关的提交号。
- 如果旧决策不再成立，不悄悄改写历史使其看起来从未存在：把旧条目标成 `Superseded`，说明被哪个新 D-xxx 替代；新的当前选择写在新条目中。若只是措辞或证据修正而没有推翻原选择，则直接维护原条目。
- Decisions 不是 changelog：不要按时间复述“先 A、再 B、又修 C”。把试错过程压缩成最终选择、关键失败原因和以后不能忘的边界；完整过程继续留在 git/PR。
- 如果代码变化没有产生新的取舍，也没有改变已有 decision 的适用范围，就**检查后不修改** `DECISIONS.md`，不要为了显示“同步过”制造无意义条目。

## 工作方式

不要用临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现来交付改动。除非改动巨大到需要重新定路线，必须先向用户确认，再按真实产品结构修改。

避免两种相反倾向：不要为了缺乏证据的少数极端场景把实现膨胀成过重框架，也不要用一次性补丁叠加并行状态。先修清 ownership、数据流和真实高风险边界，再决定是否需要新增机制。

需要提交时，如果未提交改动能按功能边界无损拆分，并且每个提交都保持可构建、可理解、可独立回滚，应拆成多个独立提交方便管理；不要把无关文档、备份文件或用户的其他改动混入功能提交。

## 产品边界

PaperTodo 当前的交互中心仍是“桌面上的几张纸”。在没有明确产品决策时，不要把局部需求自动扩张成中心式任务管理器、中心式知识库编辑器或主管理页，也不要自行补账号、云同步、分类、标签、搜索、归档等整套系统。

这是一条**默认防扩张规则**，不是永久否决清单。已经存在于代码中的能力，或用户通过新需求 / 新 decision 明确引入的产品方向，以当前代码和最新决策为准；发生产品边界变化时同步更新本节和 `DECISIONS.md`，不能让旧 Agent 文案反过来阻止已经明确的新路线。

Markdown 当前只做轻量显示和编辑辅助。可兼容少量单行内联 HTML 标签（`b/strong/i/em/s/del/u/code/a href`）；笔记图片只支持内部 `i:` 独占行图片块，不扩展网络图片、表格、附件、其他嵌入内容、块级 HTML 或完整块编辑器，除非后续有新的明确产品决策。

## 数据和保存

- `data.json` 是用户数据协议，不是内部缓存。新增字段要兼容旧数据；删除 / 改名字段要特别谨慎。
- 笔记图片保存在单个 `note-assets.lmdb` 中：原始字节与元数据分库、事务增量写入。为保持单文件，LMDB 使用 `MDB_NOSUBDIR | MDB_NOLOCK`，所有访问必须继续由进程内同一把锁串行化；不要绕过 `NoteImageStore` 直接开启事务。
- 启动失败时不能用空状态覆盖旧文件。严格解析失败的数据不要“修好后覆盖”，否则可能破坏可恢复数据。
- 保留 `_saveVersion`、`StateStore` 写锁和退出同步保存，避免旧异步保存覆盖新状态。
- 删除、隐藏、折叠是三种语义：删除才从 `Papers` 移除；隐藏仍保留纸片；折叠仍是可见纸片，只是胶囊形态。
- `paper.X/Y/Width/Height` 是普通纸片几何。胶囊尺寸和独立贴边 HWND 的坐标不能写回普通几何。
- 外部打开笔记的临时文件后缀只做文件名合法性校验；允许用户选择系统已关联的任意后缀。

## 单实例

只有主实例释放 Mutex。后续进程只转发启动参数并退出，不释放主实例锁。

`exit` / `quit` 在没有主实例时也应保存并退出；不要恢复窗口，也不要因为空数据目录创建默认待办纸。无参数的后续实例按 `show` 处理。

## 托盘

Hardcodet 托盘必须走 `TaskbarIcon.IconSource = LoadTrayIconSource()`。不要改回 `System.Drawing.Icon`；这个回归曾导致首次右键菜单位置错误、首次点击纸片被吞。

外部 `PaperTodo.ico` 是用户自定义入口，优先级高于内嵌图标。托盘菜单打开时重建，别用手动弹菜单、预热菜单、全局鼠标轮询等方式修首次菜单问题。

## 胶囊和贴边胶囊

这是最高风险区，问题通常来自“窗口几何、动画状态、隐藏状态、持久化状态”混在一起。先读 `ARCHITECTURE.md` 的 Edge Capsule 章节和 `DECISIONS.md` 的相关条目；下面只保留 Agent 不能误改的硬约束。

- 普通胶囊和贴边胶囊共用度量来源：`PaperLayoutDefaults` / `EdgeCapsuleLayout`。
- 应用清单固定为 `PerMonitorV2,PerMonitor`；贴边 HWND 的物理像素几何以目标显示器和已创建宿主的实际 DPI 为准，不得回退到主纸片窗口的 DPI。
- 贴边槽位不再由 `DeepCapsuleSlotWindow.cs` 或零散 `PaperWindow` 字段维护；`EdgeCapsuleHost` 独占 docked HWND 和视觉树，floating drag 继续使用独立 HWND。
- 所有会改变单纸片 Slot / Visual / Gesture / Preview / Placement 的输入，先变成带强类型参数的语义 `EdgeCapsuleIntent`，再经过 `EdgeCapsuleReducer`；不得重新引入 `SetSlot` / `SetVisual` / `SetPlacement` 这类字段 setter、通用参数袋或在 `PaperWindow` 另写布尔状态机。队列级 preview owner、corridor 和 visual transaction 仍由 controller 协调。
- 每张纸的 desired model、target presentation、业务 applied frame 和延迟工作只能由一个 `EdgeCapsulePresenter` 持有；`PaperWindow` 只提供环境快照和一个 `EdgeCapsuleHost.Apply(frame)` 效果入口，不得再增加业务状态机。队列级 Composition 代理只允许临时采样同一组起终帧来呈现过渡像素和命中几何，不能反写 reducer、持有第二份 desired state 或在交接后继续存在。
- `EdgeCapsuleTargetPlanner` 必须一次产出完整 shape plan；`Docked*` 和 `FloatingFree` 是互斥外形，悬浮拖拽窗口只能消费 planner 的 `FloatingFree`，不得由构造参数临时拼关闭区、圆角或宽度。
- 显示器、边、顶部、内容宽度和关闭宽度到 `DeviceScreenRect` 的转换只走纯 `EdgeCapsuleGeometry`；不得在窗口移动、动画或 measure 回调中复制物理像素公式。
- per-window 的显示器 settle、标题 measure、物理指针采样和 frame apply 共用一个 dirty/reconcile 调度入口；普通同步交接调用同一管线的 `Flush`，不得直接调用 planner/apply，也不得为新条件增加独立 pending/scheduled 布尔对。唯一例外是 controller-owned 队列代理：它可在统一 visual transaction 内捕获每项起终帧，并在所有真实源已被代理遮盖且 cloak 后直接提交、验证端点。跨胶囊 arrange 只由队列协调器单独合并。
- 同一 Dispatcher 上的 Presenter 必须共用一个调度器和每帧一次的物理指针采样；Resting/Hover/Preview 的宽高、圆角和内容变化只由 bounded host 内的 WPF Visual 完成，DComp queue proxy 只允许移动同尺寸 live surface。
- 指针是否位于胶囊上只根据当前 presented/applied frame 的物理 `InteractiveBounds` 判断；该矩形排除透明阴影边距，WPF enter/leave 只负责唤醒采样，不能直接写 Hover。proxy 拥有可见像素时也必须使用同一 logical frame 的 `InteractiveBounds` 路由输入。
- 边缘预览展开后，当前卡片与其他可浏览胶囊的 applied `InteractiveBounds` 是真实选择区；每段连续可交互队列项的外接矩形是临时空白转移区，但不是胶囊命中区，真实 `HostBounds` 和代理 envelope 都不得混入。不可交互或正在收回的旧卡必须切断前后矩形。指针在空白转移区内时，开启移动意图只在轨迹明确朝向某个可浏览胶囊时保活，否则按五档分别约 0.2 / 0.35 / 0.5 / 0.65 / 0.8 秒收起；关闭移动意图时固定等待 1 秒。越出该外接矩形在两种模式下都必须无条件立即收起，预测没有否决权；指针捕获期间不得触发。
- 每个队列的 index、master offset 和 slot count 只由 `EdgeCapsuleQueueCoordinator` 生成，`AppController` 和单个窗口不得各自重新推导。
- **贴边胶囊队列永远不分页。** 不得按工作区高度做安全容量、隐藏溢出胶囊、页头、页码、自动翻页或容量截断；队列始终按完整顺序连续向下排列，超过当前显示器工作区就允许直接出屏。后续不要以“防重叠”“小屏适配”或任何其他名义重新引入分页。
- `MasterCapsuleWindow` 只拥有每队列 slot 0、自己的 pill/手势和纵向队列锚点；真实纸片的 retract/release 由 controller 驱动，master 不得持有第二套纸片 presenter 状态。
- 每张纸的 docked HWND 使用 V3 Lite bounded host：容量只覆盖该纸在当前显示器上的最大合法 Preview，不得扩成工作区或整队列高度；`HostBounds` 是稳定容量，`Bounds` 是当前可见 WPF 形状。
- 贴边胶囊的关闭区位于屏幕墙边、悬停时从 0 宽度展开并把图标/标题推向屏幕内部；靠墙侧始终为直角，内容区拥有朝屏幕内部的圆角。
- 贴边胶囊水平伸缩只插值已经取整的可见物理宽度，并由 WPF Visual 在 bounded host 内完成；Composition 层不得改变 surface 尺寸、clip 尺寸或用 bitmap 缩放模拟 Preview。
- `EdgeCapsuleHost.Apply(frame)` 仍是每纸片真实 docked HWND 的唯一呈现契约；`HostBounds` 可大于 `Bounds`，但两者必须同墙、当前可见宽高不得超过容量，透明容量不得参与命中。
- Translation proxy 必须 `NOACTIVATE`、只包装同尺寸 live HWND surface，并在 cover 发布后把真实 HWND 一次落到 endpoint；禁止 snapshot、freeze、Reveal/Conceal resize handoff。取消、拖拽、DPI/显示器/z-order 变化必须立即恢复至少一个可见 authority。
- completion timer 只能发起完成尝试，不能证明 WPF terminal frame 已就绪；撤掉 cover 前必须重新完成 endpoint flush/apply、必要 render turn 和 bounds verify。cover 丢失时先立即恢复真实 HWND，只有即时恢复失败后才允许安排有界 retry。
- 跨队列拖拽使用独立的 floating drag HWND；贴边 slot host 永远只保留贴边布局，禁止把它改造成自由胶囊或在两种外形间复用列顺序、圆角和宽度状态。
- 拖动期间收到的全局 `ArrangeDeepCapsules` 请求必须合并并在拖动结束后刷新，不能静默丢弃；显示器指标刷新可用自己的延迟刷新吞并该请求。
- 标题测量刷新只改变 target 的真实内容宽度，不得重新推导 Hover / Active、关闭区或槽位语义；它不能覆盖已经排队的动画，动画中从当前 applied frame 平滑 retarget，拖动中则延迟到会话结束。
- 插件标准胶囊使用 `PaperCapsulePresentation.AutomaticWidth` 时，由宿主统一按标准组件、组件间距和模板内边距测量真实内容宽度；正数固定宽度继续原样支持。插件不得各自复制字符数估宽逻辑。
- 协议 1.8 边缘迷你内容固定按“专属迷你界面 → 明确允许的纯 WPF 正文迁移 → 1.7 自绘胶囊实时镜像 → 1.6 标准组件放大重绘 → 纯文字”降级；所有插件仍必须保留 1.6 结构化胶囊和 `plainText`。原生专属迷你界面、正文迁移和 1.7 自绘胶囊都拒绝 `Window`、`HwndHost`、WindowsFormsHost、WebView2 和已挂载控件。
- 1.8 迷你卡片尺寸包含宿主外框和关闭区，协议范围为 120×90～480×420 DIP；空待办和空笔记默认 130×120 DIP。一次浏览会话冻结尺寸，状态刷新不得改变整列布局。
- Web 插件的 `miniEntry` 必须位于正文 `entry` 的本地静态目录内；宿主先显示 1.6 放大回退，只有迷你页显式 `mini.ready()` 且再过一个渲染帧后才能替换，失败时不得清空回退。正文和迷你页共享宿主管理的状态、设置和主题，禁止各自维护会互相覆盖的权威副本。
- 纯 WPF 正文迁移只在插件显式实现能力时启用：首次未展示正文可暂时移动唯一真实 View；移回正文前必须先以内存截图接棒。之后浏览先显示旧截图、每次只刷新一次；截图任务必须防止旧结果覆盖新会话，禁止持续采样。
- 折叠胶囊、贴边胶囊、展开后的边缘激发态应复用同一套胶囊 UI。激发态只是持久外移、外描边和状态变化，不应再重绘一套 UI。
- `ShowDeepCapsuleWhileExpanded = true`：从贴边胶囊展开纸片后，边缘胶囊仍显示并占槽位。
- `UseCapsuleCollapseAll` 使用 slot 0 的主胶囊；真实纸片槽位从后面开始。`CapsuleCollapseAllActive` 为真时，真实胶囊收向主胶囊并隐藏可点击面。
- `HideLinkedPapersFromCapsules` 开启时，已被待办关联的纸片不应显示为胶囊。
- 隐藏全部、关闭胶囊模式、关闭贴边模式、从边缘展开后再隐藏，都要清理临时 slot / 激发态 / 动画状态，避免下次显示错位或残留占位。
- 边缘菜单的 Popup HWND 提升为 Topmost 后，关闭时可能在 UI 线程残留 PaperTodo 的 active / focus HWND；前台已经切到外部进程时，要等 WPF 退出菜单模式后再有条件清理，否则 Hardcodet 托盘菜单可能首次打开即关闭。不要改成无条件清焦点，也不要提前到菜单关闭过程内执行。

## 待办和笔记

- 多行粘贴待办只能形成一次撤销快照。
- `PaperItem.LinkedPaperId` 会影响删除纸片、关闭关联功能、显示关联纸片名称，以及“已关联纸片不显示为胶囊”。
- 笔记编辑态和浏览态共用同一个 `MarkdownTextBox`。不要拆成两套文本控件，否则滚动、换行、选区和测量容易漂。
- `MarkdownTextBox` 长度上限是 WPF 布局 / 渲染保护，不要直接删除。

## 主题、资源、提示

用户可见文本同步四个资源文件：中文、英文、日文、韩文。`ResourceTextVersion` 只是人工检查标记，不参与运行时逻辑。

主题变化要主动刷新动态生成控件、托盘菜单、AvalonEdit 背景 / 文本 / 光标 / 覆盖层；不要只依赖动态资源。

`EnableToolTips` 只控制普通操作提示，不应关闭设置页说明图标和扩展说明。

## 用户态更新日志

`CHANGELOG.md` 顶部固定说明：面向有一定计算机使用基础的用户，不记录内部实现细节，但用户可感知的功能、行为变化、改进和修复均应记录。未发布内容引起的 Bug 修复不需要写入；未发布内容的增强应合并写到内容本身的介绍中。

Git commit / PR 负责记录“开发过程怎么走到这里”：实现细节、试验方案、协议阶段、回归、开发期 Bug 与修复链都应留在提交或 PR 中；`CHANGELOG.md` 只记录“用户从上一个正式版本升级后最终拿到了什么”。

`CHANGELOG.md` 顶部按 `### 计划 / 待办`、`### 评估`、`### Unreleased` 组织。用户要求记录软件目标、修改计划或待办时写入计划；要求记录取舍、暂缓原因或实现评估时写入评估；二者都不等同于已完成改动。

改动完成后，只要影响用户可见行为，就必须更新 `### Unreleased`。纯内部整理、测试、文档、CI、构建流程、重构方式、文件名、状态机和仅开发者可见变化不写入用户更新日志。

判断是否写“修复”时，以最近一个正式发布版本为基准：如果问题在该正式版中已经存在，修复后应记录；如果问题只在 `Unreleased` 开发过程中由尚未发布的新功能、重构或中间方案引入，则不以独立 Bug 修复条目记录。

未发布功能后续增加能力、设置或体验增强时，直接合并进该功能原有介绍，使条目描述最终完整能力；不要按提交时间另起“新增”“优化”或“修复”条目。若开发期修复决定了最终产品行为，也只写最终行为，不写“先坏了什么、后来怎么修”。

同一未发布功能的阶段性协议、架构或产品形态必须折叠成最终状态；禁止在同一个 `Unreleased` 中保留 1.1 → 1.2 → 1.3 这类开发演进、已被替代的方案或彼此矛盾的旧描述。

`### Unreleased` 尽量按可直接挪到正式版本号下的发布格式维护：参考 v2.0 正式版，必要时用 `**新功能**`、`**胶囊相关改动**`、`**bug修复和边界修正**` 等粗体小分组组织条目；明显重磅的新功能单独成组，相关设置、增强和边界说明尽量收束在该组内。

发布前要从“上一个正式版本用户”的视角重新通读整个 `### Unreleased`：删除开发期回归、阶段性协议、已被替代的描述和已经被主功能吸收的增强，只保留最终用户可感知的版本差异。

发布版本小节按版本号从旧到新排列；从 `### Unreleased` 挪到具体版本号时，把新版本放到已有版本列表末尾的正确位置，不要插在 `Unreleased` 和旧版本之间。

更新日志条目里只有重点内容需要加粗；非重点条目不要为了统一格式而加粗。

## 构建和发布

版本号显式维护在 `PaperTodo.csproj`，不要恢复自动递增版本号。

`plugin-samples/` 只保存插件源码和构建说明，`plugins/` 只保存可直接加载的最终插件产物。普通开发构建可以复制 `plugins/` 方便调试，但本地 `dotnet publish` 和 GitHub Release 都不携带插件；插件单独构建和分发。最终插件目录不保留 PDB、XML 文档、重复原生库、宿主已提供的共享程序集或其他中间产物。

PR 分支按本次 push 的 HEAD 提交信息按需触发 Windows CI：`[debug]` 只运行 `PR Test Debug` 并生成 Debug 测试包，`[ci]` 只运行 `Pull request build`，`[debug-ci]` 两者都运行；没有这些标记时两个 Windows job 都必须直接跳过。Agent 需要用户真机验证时使用 `[debug]`，需要 Windows Release 编译验证时使用 `[ci]`，两者都需要时使用 `[debug-ci]`。标记必须放在本次 push 的最后一个 HEAD 提交；多提交一起 push 时不要指望更早提交里的标记生效，也不要为了触发单独制造空提交。两者保留 `workflow_dispatch` 手动兜底，但 GitHub 只允许 dispatch 已存在于默认分支的 workflow；`PR Test Debug` 尚未合入默认分支前以提交标记触发为准。Debug Artifact 只保留 1 天。

不要重新引入 `scripts/edge-refinement-tests/` 或依赖源码字符串、文件路径、方法排列的源码形状测试；边缘胶囊回归以真实编译、诊断日志和真机验证为准。

普通编译：

```powershell
dotnet build PaperTodo.csproj -c Release
```

`vendor/wpf-notifyicon` 使用父仓库记录的固定子模块提交。更新 fork 后，必须显式更新子模块 gitlink、完成构建与真实托盘手测，再将新的依赖提交一并提交到 PaperTodo。普通本地构建和云端 Release 不得在构建过程中自动拉取 fork 的最新分支。

云端 Release 发布两个 Windows x64 单文件：自包含 .NET Runtime 的 `…-self-contained.exe`，以及不带运行库的 `…-no-runtime.exe`。本地打包只生成 no-runtime 单文件。WPF 版本不要开启 `PublishTrimmed` 或 Native AOT。

仓库内 `native/lmdb/bin/win-x64/papertodo_lmdb.dll` 是本地没有 CMake / MSVC 环境时使用的默认原生库，普通 `dotnet build` / `dotnet publish` 必须复制或嵌入它，并在缺失时直接失败。GitHub Release 必须先调用 `native/lmdb/build.ps1 -ForceRebuild` 从仓库内 LMDB 源码重新生成 DLL，不能直接拿默认 DLL 冒充云端编译产物。

稳定正式版不要靠 tag push 自动发布；完成真实多屏 / 混合 DPI 等发布前手测后，用 GitHub Actions `workflow_dispatch` 并显式确认稳定版发布。`rc` / `alpha` / `beta` / `preview` 标签可以继续由 tag push 发布为预发布。

推送或移动稳定版 tag 只会把 tag/commit 送到 GitHub；Actions 是后置检查，失败不会撤回这次 push。不要把稳定版 tag push 当作发布步骤，也不要为了正式发布制造必然失败的稳定版 tag push run；正式版发布只认成功的 `workflow_dispatch` run。

## 更新本文

每次代码变更都必须先检查本文是否受影响；只有产品边界、持久化兼容、保存 / 单实例 / 托盘 / 胶囊 / 发布流程等隐藏硬约束或 Agent 执行规则发生变化时才实际修改本文。普通 UI 微调、文案、颜色、间距、动画参数如果没有改变这些约束，不需要为了制造 diff 而更新。
