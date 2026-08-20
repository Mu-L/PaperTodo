# PaperTodo Agent 备忘

本文件只记录“不通读历史和全量代码很难知道”的项目约束。代码是真相；普通文件职责、字段含义、WPF/C# 常识不要写进来。

## 工作方式

不要用临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现来交付改动。除非改动巨大到需要重新定路线，必须先向用户确认，再按真实产品结构修改。

尽量不引入过重实现，打补丁叠屎山代码，不要过于考虑边界场景和少数极端情况，

需要提交时，如果未提交改动能按功能边界无损拆分，并且每个提交都保持可构建、可理解、可独立回滚，应拆成多个独立提交方便管理；不要把无关文档、备份文件或用户的其他改动混入功能提交。

## 产品边界

PaperTodo 是“桌面上的几张纸”，不是任务管理器、知识库或文档编辑器。默认不做账号、同步、分类、标签、搜索、归档、统计、提醒、日历、主管理页和集中列表页。

Markdown 只做轻量显示和编辑辅助。可兼容少量单行内联 HTML 标签（`b/strong/i/em/s/del/u/code/a href`）；笔记图片只支持内部 `i:` 独占行图片块，不扩展网络图片、表格、附件、其他嵌入内容、块级 HTML 或完整块编辑器。

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

这是最高风险区，问题通常来自“窗口几何、动画状态、隐藏状态、持久化状态”混在一起。

- 普通胶囊和贴边胶囊共用度量来源：`PaperLayoutDefaults` / `EdgeCapsuleLayout`。
- 应用清单固定为 `PerMonitorV2,PerMonitor`；贴边 HWND 的物理像素几何以目标显示器和已创建宿主的实际 DPI 为准，不得回退到主纸片窗口的 DPI。
- 贴边槽位不再由 `DeepCapsuleSlotWindow.cs` 或零散 `PaperWindow` 字段维护；`EdgeCapsuleHost` 独占 docked HWND 和视觉树，floating drag 继续使用独立 HWND。
- 所有贴边输入先变成带强类型参数的语义 `EdgeCapsuleIntent`，再经过 `EdgeCapsuleReducer`；不得重新引入 `SetSlot` / `SetVisual` / `SetPlacement` 这类字段 setter、通用参数袋或在 `PaperWindow` 另写布尔状态机。
- 每张纸的 desired model、target presentation、业务 applied frame 和延迟工作只能由一个 `EdgeCapsulePresenter` 持有；`PaperWindow` 只提供环境快照和一个 `EdgeCapsuleHost.Apply(frame)` 效果入口，不得再增加业务状态机。队列级 Composition 代理只允许临时采样同一组起终帧来呈现过渡像素和命中几何，不能反写 reducer、持有第二份 desired state 或在交接后继续存在。
- `EdgeCapsuleTargetPlanner` 必须一次产出完整 shape plan；`Docked*` 和 `FloatingFree` 是互斥外形，悬浮拖拽窗口只能消费 planner 的 `FloatingFree`，不得由构造参数临时拼关闭区、圆角或宽度。
- 显示器、边、顶部、内容宽度和关闭宽度到 `DeviceScreenRect` 的转换只走纯 `EdgeCapsuleGeometry`；不得在窗口移动、动画或 measure 回调中复制物理像素公式。
- per-window 的显示器 settle、标题 measure、物理指针采样和 frame apply 共用一个 dirty/reconcile 调度入口；普通同步交接调用同一管线的 `Flush`，不得直接调用 planner/apply，也不得为新条件增加独立 pending/scheduled 布尔对。唯一例外是 controller-owned 队列代理：它可在统一 visual transaction 内捕获每项起终帧，并在所有真实源已被代理遮盖且 cloak 后直接提交、验证端点。跨胶囊 arrange 只由队列协调器单独合并。
- 同一 Dispatcher 上的 Presenter 必须共用一个调度器和每帧一次的物理指针采样；Resting/Hover/Preview 的宽高、圆角和内容变化只由 bounded host 内的 WPF Visual 完成，DComp queue proxy 只允许移动同尺寸 live surface。
- 指针是否位于胶囊上只根据 applied frame 的物理 `InteractiveBounds` 判断；该矩形排除透明阴影边距，WPF enter/leave 只负责唤醒采样，不能直接写 Hover。
- 边缘预览展开后，当前卡片与其他可浏览胶囊的 applied `InteractiveBounds` 是真实选择区；每段连续可交互队列项的外接矩形是临时空白转移区，但不是胶囊命中区，真实 `HostBounds` 和代理 envelope 都不得混入。不可交互或正在收回的旧卡必须切断前后矩形。指针在空白转移区内时，开启移动意图只在轨迹明确朝向某个可浏览胶囊时保活，否则按五档分别约 0.2 / 0.35 / 0.5 / 0.65 / 0.8 秒收起；关闭移动意图时固定等待 1 秒。越出该外接矩形在两种模式下都必须无条件立即收起，预测没有否决权；指针捕获期间不得触发。
- 每个队列的 index、master offset 和 slot count 只由 `EdgeCapsuleQueueCoordinator` 生成，`AppController` 和单个窗口不得各自重新推导。
- **贴边胶囊队列永远不分页。** 不得按工作区高度做安全容量、隐藏溢出胶囊、页头、页码、自动翻页或容量截断；队列始终按完整顺序连续向下排列，超过当前显示器工作区就允许直接出屏。后续不要以“防重叠”“小屏适配”或任何其他名义重新引入分页。
- 每张纸的 docked HWND 使用 V3 Lite bounded host：容量只覆盖该纸在当前显示器上的最大合法 Preview，不得扩成工作区或整队列高度；`HostBounds` 是稳定容量，`Bounds` 是当前可见 WPF 形状。
- 贴边胶囊的关闭区位于屏幕墙边、悬停时从 0 宽度展开并把图标/标题推向屏幕内部；靠墙侧始终为直角，内容区拥有朝屏幕内部的圆角。
- 贴边胶囊水平伸缩只插值已经取整的可见物理宽度，并由 WPF Visual 在 bounded host 内完成；Composition 层不得改变 surface 尺寸、clip 尺寸或用 bitmap 缩放模拟 Preview。
- `EdgeCapsuleHost.Apply(frame)` 仍是每纸片真实 docked HWND 的唯一呈现契约；`HostBounds` 可大于 `Bounds`，但两者必须同墙、当前可见宽高不得超过容量，透明容量不得参与命中。
- Translation proxy 必须 `NOACTIVATE`、只包装同尺寸 live HWND surface，并在 cover 发布后把真实 HWND 一次落到 endpoint；禁止 snapshot、freeze、Reveal/Conceal resize handoff。取消、拖拽、DPI/显示器/z-order 变化必须立即恢复至少一个可见 authority。
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
- `PaperItem.LinkedPaperId` 会影响删除纸片、关闭关联功能、显示关联纸片名称、以及“已关联纸片不显示为胶囊”。
- 笔记编辑态和浏览态共用同一个 `MarkdownTextBox`。不要拆成两套文本控件，否则滚动、换行、选区和测量容易漂。
- `MarkdownTextBox` 长度上限是 WPF布局 / 渲染保护，不要直接删除。

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

PR 分支按本次 push 的 HEAD 提交信息按需触发 Windows CI：`[debug]` 只运行 `PR Test Debug` 并生成 Debug 测试包，`[ci]` 只运行 `Pull request build`，`[debug-ci]` 两者都运行；没有这些标记时两个 Windows job 都必须直接跳过。Agent 需要用户真机验证时使用 `[debug]`，需要完整编译与 edge refinement checks 时使用 `[ci]`，两者都需要时使用 `[debug-ci]`。标记必须放在本次 push 的最后一个 HEAD 提交；多提交一起 push 时不要指望更早提交里的标记生效，也不要为了触发单独制造空提交。两者保留 `workflow_dispatch` 手动兜底，但 GitHub 只允许 dispatch 已存在于默认分支的 workflow；`PR Test Debug` 尚未合入默认分支前以提交标记触发为准。Debug Artifact 只保留 1 天。

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

只有产品边界、持久化兼容、保存 / 单实例 / 托盘 / 胶囊 / 发布流程发生变化时才更新本文。普通 UI 微调、文案、颜色、间距、动画参数不需要同步。

- DComp translation backend 的类型层不得暴露 clip、scale、effect、snapshot、Reveal/Conceal 或 deferred resize；这些能力不是“暂时不用”，而是 V3 Lite 中禁止存在。
