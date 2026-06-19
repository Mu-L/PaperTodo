# PaperTodo 全量审核进度

目标：把当前版本审核到“能解释任何行为、定位任何状态来源、判断任何改动代价”的程度，并尽最大工程可能降低 bug、结构债、性能浪费、交互断点和动画缺口。

本文件是执行清单，不是总结稿。每完成一个阶段，就在这里打勾，并补充证据、结论和遗留风险。没有证据的项目不能打勾。

## 基线

- 审核起点日期：2026-06-19
- 分支：`feature/multi-master-capsule`
- 起点提交：`8e4fab8`
- 当前变更范围相对 `main...HEAD`：17 个文件，1965 insertions / 665 deletions
- 纳入范围：全部 `.cs`、`.xaml`、`.resx`、`.csproj`、`.md`，以及发布相关目录和配置
- 排除范围：`输出/`、`obj/`、缓存、截图、历史临时文件；除非它们影响发布结果

## 打勾规则

- `[ ]` 未开始或证据不足
- `[-]` 正在进行
- `[x]` 已完成，且有当前状态证据
- `[!]` 发现问题，需要修复或明确接受风险

任何 “完成” 都必须包含至少一种证据：文件行号、命令输出、测试结果、手测路径、差异核对、资源核对、构建结果或明确的代码推演。

## 总进度

- [x] 创建全量审核进度文档
  - 证据：本文件已加入仓库根目录
- [x] 阶段 0：冻结基线与审核边界
- [x] 阶段 1：建立系统地图
- [ ] 阶段 2：逐文件深读
- [ ] 阶段 3：跨模块不变量审查
- [ ] 阶段 4：高风险专项攻击
- [ ] 阶段 5：性能审查
- [ ] 阶段 6：交互、视觉、动画审查
- [ ] 阶段 7：修复循环
- [ ] 阶段 8：回归矩阵
- [ ] 阶段 9：加载用户蒸馏层做最终产品复核
- [ ] 阶段 10：发布判断和最终报告

## 阶段 0：冻结基线与审核边界

- [x] 记录当前分支和起点提交
  - 证据：`git rev-parse --abbrev-ref HEAD` -> `feature/multi-master-capsule`；`git rev-parse --short HEAD` -> `8e4fab8`
- [x] 记录相对 main 的变更规模
  - 证据：`git diff --stat main...HEAD` -> 17 files changed, 1965 insertions(+), 665 deletions(-)
- [x] 记录当前审核文件集合规模
  - 证据：`rg --files -g "*.cs" -g "*.xaml" -g "*.resx" -g "*.csproj" -g "*.md"` -> 40 个文件，其中 `.cs` 29 个
- [x] 保存完整审核文件清单
  - 证据：见下方“审核文件清单”
- [x] 完成职责草图
  - 证据：见“阶段 1：系统地图”的第一版职责图；后续逐文件深读会修正它
- [x] 运行并记录基线构建
  - 证据：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error，输出 `输出\PaperTodo-v2.0\PaperTodo.dll`
- [x] 运行并记录空白 / 格式检查
  - 证据：`git diff --check` -> 无输出
- [x] 运行并记录资源 key parity
  - 证据：`ko missing=0 extra=0`，`en missing=0 extra=0`，`ja missing=0 extra=0`
- [x] 明确发布产物与版本号状态
  - 证据：`PaperTodo.csproj` -> `TargetFramework=net10.0-windows`，`Version=2.0`，`AssemblyVersion=2.0.0.0`，`FileVersion=2.0.0.0`，`InformationalVersion=2.0`，`OutputPath=输出\PaperTodo-v$(Version)\`

### 审核文件清单

- `AGENTS.md`
- `AnimationHelper.cs`
- `App.xaml`
- `App.xaml.cs`
- `AppController.cs`
- `AppController.Settings.cs`
- `AppController.Tray.cs`
- `CHANGELOG.md`
- `ClipboardHelper.cs`
- `DeepCapsuleLayout.cs`
- `FullscreenForegroundWindowDetector.cs`
- `MarkdownTextBox.cs`
- `MasterCapsuleWindow.cs`
- `Models.cs`
- `NoteTypography.cs`
- `PaperTitles.cs`
- `PaperTodo.csproj`
- `PaperWindow.Capsule.cs`
- `PaperWindow.cs`
- `PaperWindow.DeepCapsule.cs`
- `PaperWindow.Native.cs`
- `PaperWindow.Note.cs`
- `PaperWindow.Todo.cs`
- `README.en.md`
- `README.md`
- `Resources/Strings.en.resx`
- `Resources/Strings.ja.resx`
- `Resources/Strings.ko.resx`
- `Resources/Strings.resx`
- `SingleInstanceHelper.cs`
- `StartupCommand.cs`
- `StateStore.cs`
- `Strings.cs`
- `SystemSettingsHelper.cs`
- `Theme.cs`
- `TodoTextBox.cs`
- `ToolTipPreferences.cs`
- `WindowNative.cs`
- `WindowWorkAreaHelper.cs`
- `md-sample.md`

## 阶段 1：系统地图

目标：先理解系统，不急着修 bug。每个模块要能回答“谁拥有状态、谁能改变它、什么时候落盘、谁会恢复它”。

- [x] 启动、单实例、启动命令
  - 文件：`App.xaml.cs`、`SingleInstanceHelper.cs`、`StartupCommand.cs`
- [x] 数据模型、保存、加载、崩溃恢复
  - 文件：`Models.cs`、`StateStore.cs`、`App.xaml.cs`
- [x] AppController 总调度
  - 文件：`AppController.cs`
- [x] 设置、主题、资源刷新
  - 文件：`AppController.Settings.cs`、`Theme.cs`、`ToolTipPreferences.cs`、`Strings.cs`
- [x] 托盘和菜单
  - 文件：`AppController.Tray.cs`
- [x] 普通纸片窗口生命周期
  - 文件：`PaperWindow.cs`
- [x] 普通胶囊
  - 文件：`PaperWindow.Capsule.cs`
- [x] 贴边胶囊、多队列、多屏、DPI
  - 文件：`PaperWindow.DeepCapsule.cs`、`DeepCapsuleLayout.cs`、`MasterCapsuleWindow.cs`、`WindowWorkAreaHelper.cs`、`WindowNative.cs`、`PaperWindow.Native.cs`
- [x] 待办编辑、拖拽、撤销、关联笔记
  - 文件：`PaperWindow.Todo.cs`、`TodoTextBox.cs`
- [x] 笔记、Markdown、外部打开
  - 文件：`PaperWindow.Note.cs`、`MarkdownTextBox.cs`、`NoteTypography.cs`
- [x] 全屏避让和 topmost
  - 文件：`FullscreenForegroundWindowDetector.cs`、`WindowNative.cs`
- [x] 发布、更新日志、项目配置
  - 文件：`PaperTodo.csproj`、`CHANGELOG.md`、`README.md`、`README.en.md`、`AGENTS.md`

### 系统地图第一版

这张图是第一版职责草图，目标是建立“状态归属和调用方向”。它不是逐文件深读结论，不能替代阶段 2。

| 模块 | 主要文件 | 当前理解 | 状态归属 / 写入点 | 审核重点 |
| --- | --- | --- | --- | --- |
| 启动与单实例 | `App.xaml.cs`、`StartupCommand.cs`、`SingleInstanceHelper.cs` | 解析启动参数；主实例持有 Mutex；后续实例通过 named pipe 转发参数后退出。 | `_singleInstance` 属于 `App`；命令最终进入 `AppController.ExecuteStartupCommand()`。 | `exit/quit`、无参数二次启动、Mutex 释放、异常启动不覆盖数据。 |
| 数据协议与保存 | `Models.cs`、`StateStore.cs` | `AppState` / `PaperData` / `PaperItem` 是 `data.json` 协议；`StateStore` 负责加载、规范化、同步/异步写入、backup。 | `StateStore.Normalize()` 会修正 live state；`AppController.SaveNow()` 生成版本化 JSON。 | 坏数据不覆盖、未知字段兼容、旧异步保存不能覆盖新状态、字段迁移不能破坏旧数据。 |
| 控制器总调度 | `AppController.cs` | 应用状态、所有纸片窗口、托盘、保存 timer、topmost timer、多主胶囊窗口都由控制器协调。 | `State`、`_windows`、`_masterCapsules`、`_saveTimer`、`_visibilityAnimationVersions`。 | 状态变更后是否刷新 UI / 保存 / 重排；隐藏、折叠、删除语义是否清楚。 |
| 设置与主题 | `AppController.Settings.cs`、`Theme.cs`、`ToolTipPreferences.cs` | 设置页直接修改 `State`，再通知窗口、托盘、主题资源或胶囊重排。 | `State.Theme`、`ColorScheme`、功能开关、胶囊模式开关。 | 关闭模式清理状态、资源动态刷新、说明 tooltip 不被普通 tooltip 开关误关。 |
| 托盘 | `AppController.Tray.cs` | Hardcodet `TaskbarIcon` + 自绘 WPF `ContextMenu`；菜单打开时重建；纸片行支持显隐和删除确认。 | `_trayIcon`、`_trayMenu`、行内局部 confirm/suppress 状态。 | 首次点击、菜单焦点、行内按钮事件顺序、菜单重建时状态丢失。 |
| 纸片主窗口 | `PaperWindow.cs` | 单个纸片的 WPF Window，承载标题栏、主体、胶囊 shell、拖拽层、topmost、主题和几何保存。 | `_paper` 是持久数据；大量 `_deepCapsule*` / `_todo*` 是窗口瞬态状态。 | `SuppressGeometrySave`、关闭即隐藏、动画完成回调、窗口激活/topmost。 |
| 普通胶囊 | `PaperWindow.Capsule.cs` | 普通折叠胶囊 UI 和折叠/展开动画；不一定贴边。 | `_paper.IsCollapsed`、窗口 `Width/Height`、transition progress。 | 折叠时保存几何、展开恢复尺寸、不可胶囊时自动展开。 |
| 贴边胶囊与多队列 | `PaperWindow.DeepCapsule.cs`、`DeepCapsuleLayout.cs`、`MasterCapsuleWindow.cs`、`WindowWorkAreaHelper.cs`、`WindowNative.cs` | 贴边胶囊使用独立 slot-host window；队列按 `(monitor, edge)` 分组；每队列一个 master；拖单个胶囊跨边/跨屏。 | `PaperData.CapsuleSide`、`CapsuleMonitorDeviceName`、`State.CapsuleCollapseAllActiveQueues`、`State.DeepCapsuleQueueStartTopMargins`；窗口瞬态 `SlotState/VisualState/GestureState/OpenOrigin`。 | 最高风险：几何、动画、隐藏状态、持久化状态混用；多屏 DPI；slot 清理；collapse-all per-queue。 |
| 待办 | `PaperWindow.Todo.cs`、`TodoTextBox.cs` | 待办行 UI、输入、粘贴、拖拽排序、撤销/重做、关联笔记入口。 | `_paper.Items` 持久；`_undoStack` / `_redoStack` / `_todoDrag` 瞬态。 | 多行粘贴单次撤销、拖拽结束清理、关联笔记影响胶囊资格。 |
| 笔记与 Markdown | `PaperWindow.Note.cs`、`MarkdownTextBox.cs`、`NoteTypography.cs` | 笔记共用一个 `MarkdownTextBox`，在编辑/预览间切换；支持轻量 Markdown 和部分 inline HTML；外部打开写临时文件。 | `_paper.Content`、`_paper.TextZoom` 持久；`_noteBox`、`_showNotePreview` 瞬态。 | 大文本保护、滚动/选区保持、外部后缀合法性、预览点击链接。 |
| 全屏避让 / topmost | `FullscreenForegroundWindowDetector.cs`、`WindowNative.cs`、`AppController.cs` | 定时检查外部全屏窗口，必要时让纸片和胶囊退出 topmost 或插到避让窗口后。 | `_suppressTopmostForFullscreenForeground`、`_fullscreenAvoidanceWindow`。 | 200ms timer 成本、全屏误判、恢复 topmost、slot-host 和 master 一致刷新。 |
| 资源 / 发布 | `Resources/*.resx`、`PaperTodo.csproj`、`CHANGELOG.md`、`README*.md`、`AGENTS.md` | 资源四语言同步；版本号显式维护；changelog 只写用户可见行为。 | `.resx` keys、项目版本、发布输出目录。 | key parity、版本和 changelog 是否一致、发布形态是否符合 no-runtime 单文件要求。 |

### 状态所有权第一版

- `AppState`：持久化应用协议，唯一长期来源是 `data.json`；由 `AppController.State` 持有。
- `PaperData`：单纸片持久状态，包括普通窗口几何、可见性、折叠、文本、待办、胶囊队列归属。
- `PaperItem`：待办项协议，包括文本、完成状态、顺序、关联笔记 id。
- `PaperWindow` 瞬态状态：动画、slot host、拖拽、标题编辑、撤销栈、note preview，原则上不能直接成为 `data.json` 协议。
- `MasterCapsuleWindow` 瞬态状态：每队列主胶囊 UI、hover、拖动中状态；持久结果只应通过 `State.DeepCapsuleQueueStartTopMargins` 和 `State.CapsuleCollapseAllActiveQueues` 表达。
- `WindowWorkAreaHelper` / `DeepCapsuleLayout`：几何计算工具；不能拥有业务状态，除兼容旧静态 anchor 外应尽量用显式 `(monitor, edge)` 输入。

## 阶段 2：逐文件深读

每个文件记录：职责、写入状态、读取状态、外部依赖、不变量、异常路径、性能热点、动画/视觉责任、发现问题、排除问题。

- [x] `Models.cs`
- [x] `StateStore.cs`
- [x] `App.xaml.cs`
- [x] `SingleInstanceHelper.cs`
- [x] `StartupCommand.cs`
- [-] `AppController.cs`
- [x] `AppController.Settings.cs`
- [x] `AppController.Tray.cs`
- [x] `PaperWindow.cs`
- [x] `PaperWindow.Capsule.cs`
- [x] `PaperWindow.DeepCapsule.cs`
- [x] `PaperWindow.Native.cs`
- [x] `MasterCapsuleWindow.cs`
- [ ] `DeepCapsuleLayout.cs`
- [x] `WindowWorkAreaHelper.cs`
- [x] `WindowNative.cs`
- [ ] `PaperWindow.Todo.cs`
- [ ] `TodoTextBox.cs`
- [ ] `PaperWindow.Note.cs`
- [ ] `MarkdownTextBox.cs`
- [ ] `NoteTypography.cs`
- [ ] `PaperTitles.cs`
- [ ] `FullscreenForegroundWindowDetector.cs`
- [ ] `Theme.cs`
- [ ] `ToolTipPreferences.cs`
- [ ] `SystemSettingsHelper.cs`
- [ ] `Strings.cs`
- [ ] `ClipboardHelper.cs`
- [ ] `AnimationHelper.cs`
- [x] `App.xaml`
- [ ] `Resources/*.resx`
- [ ] `PaperTodo.csproj`
- [ ] `CHANGELOG.md`
- [ ] `README*.md`
- [ ] `AGENTS.md`

### 逐文件深读记录

#### `Models.cs`

- 职责：定义 `data.json` 协议层状态、纸片、待办项，以及模式 / 尺寸 / 后缀等轻量枚举规范化。
- 持久状态证据：`AppState` 在 `Models.cs:130` 开始；多队列收起状态 `CapsuleCollapseAllActiveQueues` 在 `Models.cs:150`；per-queue 起始高度 `DeepCapsuleQueueStartTopMargins` 在 `Models.cs:157`；单纸片队列归属在 `PaperData.CapsuleSide` / `CapsuleMonitorDeviceName`，见 `Models.cs:193`。
- 协议兼容证据：旧的 `ShowTopBarNewPaperButtons` 通过 nullable + `JsonIgnore` 保留迁移入口，见 `Models.cs:170`；外部 Markdown 后缀只做文件名合法性校验，不限制业务含义，见 `Models.cs:40`。
- 结论：本轮未发现模型层字段默认值和 AGENTS 约束直接冲突；后续跨模块要继续验证普通几何与胶囊几何是否混写。

#### `StartupCommand.cs`

- 职责：把启动参数规整为 `show/hide/toggle/new-todo/new-note/exit` 等命令。
- 证据：空参数默认值由调用方传入，见 `StartupCommand.cs:25`；二次实例可把空参数解释为 `Show`，对应 `App.xaml.cs:69`；`CreatesPaper` 只覆盖 `NewTodo/NewNote`，见 `StartupCommand.cs:23`。
- 结论：命令解析本身边界清楚；`exit/quit` 是否不创建默认纸片由 `App.xaml.cs` 和 `AppController.Exit()` 继续承担。

#### `SingleInstanceHelper.cs`

- 职责：主实例 Mutex、后续实例 named pipe 转发、主实例监听。
- Mutex 证据：`TryAcquire()` 只在 `createdNew` 时设置 `_ownsMutex`，见 `SingleInstanceHelper.cs:36`；`Dispose()` 只在 `_ownsMutex` 为真时 `ReleaseMutex()`，见 `SingleInstanceHelper.cs:176`。
- 转发证据：后续实例用 Base64 JSON 写 pipe，见 `SingleInstanceHelper.cs:53`、`SingleInstanceHelper.cs:135`；主实例监听 pipe 后回调命令，见 `SingleInstanceHelper.cs:90`。
- 结论：符合“只有主实例释放 Mutex；后续实例转发后退出”的项目约束。未发现二次实例释放主锁的路径。

#### `App.xaml.cs`

- 职责：WPF 启动入口、单实例分流、启动失败处理、全局异常恢复。
- 单实例证据：拿不到 Mutex 时只 `SignalPrimaryInstance(e.Args)` 后退出，见 `App.xaml.cs:22`；主实例 listener 对空参数使用 `StartupCommandKind.Show`，见 `App.xaml.cs:65`。
- 启动命令证据：首实例 `exit` 在 `Start()` 之前执行，见 `App.xaml.cs:55`；普通启动只有非建纸命令才 `createDefaultPaper=true`，见 `App.xaml.cs:62`。
- 崩溃恢复证据：全局异常写 `PaperTodo.crash.log` 并尝试保存 `data.crash_recovery.json`，见 `App.xaml.cs:89`。
- 结论：启动入口符合单实例和 `exit` 不创建默认纸片的方向；数据恢复文件是否会被后续保存破坏，见 A001。

#### `StateStore.cs`

- 职责：加载、规范化、序列化、同步 / 异步写入、backup。
- 加载证据：主文件存在时先读 `data.json`，失败后尝试 `data.backup.json`，见 `StateStore.cs:25`；主 / 备都失败才抛出，见 `StateStore.cs:73`。
- 保存证据：`_writeLock` + `_latestWrittenVersion` 防止旧异步保存覆盖新保存，见 `StateStore.cs:79`、`StateStore.cs:88`、`StateStore.cs:106`；写入前会把当前 `data.json` 复制到 `data.backup.json`，见 `StateStore.cs:137`。
- 规范化证据：关闭胶囊 / 贴边 / 收起全部时清理 per-queue 起始高度，见 `StateStore.cs:225`；关联笔记失效时清空 `LinkedNoteId`，见 `StateStore.cs:321`；隐藏已关联笔记时强制取消其折叠，见 `StateStore.cs:330`。
- 发现并修复：A001。若主 `data.json` 解析失败但 backup 能加载，旧逻辑后续第一次保存会先把解析失败的主文件复制覆盖 `data.backup.json`，再用从 backup 规范化后的状态覆盖 `data.json`。已改为记录 backup 恢复态，首次保存前复制保留失败主文件和本次使用的 backup，并跳过这一次 backup 轮换；证据见 `StateStore.cs:24`、`StateStore.cs:72`、`StateStore.cs:146`、`StateStore.cs:170`。

#### `App.xaml`

- 职责：应用级滚动条 / ScrollViewer 样式。
- 证据：只定义资源和控件模板，不持有业务状态；`PaperScrollThumbStyle` 在 `App.xaml:10`，全局 `ScrollBar` 样式在 `App.xaml:34`，全局 `ScrollViewer` 模板在 `App.xaml:84`。
- 结论：本轮未发现它参与数据、胶囊、托盘或启动状态；视觉细节留到阶段 6。

#### `AppController.cs`（进行中）

- 已读范围：启动命令分发、纸片创建、显示 / 隐藏 / 删除、关联笔记资格刷新、普通几何保存、贴边队列重排、收起全部每队列状态、保存入口。
- 启动命令证据：`ExecuteStartupCommand()` 在 `AppController.cs:596`，`NewTodo/NewNote` 只创建纸片，`Exit` 直接走 `Exit()`；普通启动默认纸片创建在 `AppController.cs:93`。
- 纸片创建证据：`CreatePaper()` 限制最多 100 张，初始化标题、几何、可见性和队列归属，见 `AppController.cs:132`；新纸片会继承来源纸片队列或全局队列，见 `AppController.cs:208`；显示前会避开贴边胶囊栏，见 `AppController.cs:218`。
- 显示 / 隐藏证据：`ShowPaper()` 会在不可胶囊时取消折叠、设置 `IsVisible=true`、取消旧动画、按是否贴边折叠选择主窗口或 slot，见 `AppController.cs:641`；`HidePaper()` 会先 `IsVisible=false`，从贴边栈分离，隐藏后把折叠态清掉，见 `AppController.cs:882`。
- 隐藏全部证据：`HideAllPapers()` 先清所有 `IsVisible`，逐窗口 `DetachFromDeepCapsuleStack()` 并 `SetCollapsedState(false)`，最后清所有 `IsCollapsed`，见 `AppController.cs:961`。这符合“隐藏、折叠语义分开；隐藏全部要清理 slot”的约束。
- 删除证据：`DeletePaper()` 关闭窗口、从 `State.Papers` 移除，删除笔记时清理待办链接，最后重排胶囊并保存，见 `AppController.cs:988`；`ClearTodoLinksToNote()` 后会刷新待办行和胶囊资格，见 `AppController.cs:1023`。
- 普通几何证据：`UpdateGeometry()` 遇到 `PaperWindow.SuppressGeometrySave` 直接返回，折叠时不写 `Width/Height`，见 `AppController.cs:1270`。此片段未发现把贴边半隐藏尺寸写回普通几何的直接路径，仍需继续交叉审 `PaperWindow.*`。
- 收起全部证据：队列 key 由 `(monitorDeviceName, side)` 组成，见 `AppController.cs:1295`；`ToggleCapsuleCollapseAllActive()` 只切换当前 queue key 的 `CapsuleCollapseAllActiveQueues`，见 `AppController.cs:1500`；`MigrateLegacyCollapseAllActiveQueues()` 只在旧全局 active 且没有任一 live queue entry 时迁移，见 `AppController.cs:1305`。本片段未发现“点击一个主胶囊收起所有队列”的当前直接路径。
- 关联笔记资格证据：`RefreshCapsuleEligibilityForLinkedNotes()` 自己只刷新 UI / 布局，不保存；设置调用方随后 `SaveNow()`，待办链接 / 解绑调用方先 `MarkDirty()`，见 `AppController.Settings.cs:1118`、`PaperWindow.Todo.cs:1017`。
- 仍未完成：topmost / 全屏避让、master 胶囊窗口同步细节、`ArrangeDeepCapsules()` 内 slot 视觉状态、保存失败 UI、offscreen rescue、退出清理、与 `PaperWindow.DeepCapsule.cs` 的拖拽交叉路径。

#### `PaperWindow.DeepCapsule.cs`

- 已读范围：slot host 创建、拖拽入口、跨队列拖拽视觉、混合 DPI 坐标、滑出 / 滑回横向动画、drop 结束路径。
- slot host 证据：贴边胶囊不再由单独 `DeepCapsuleSlotWindow.cs` 维护，而是在 `PaperWindow` 内部 `EnsureDeepCapsuleSlotHost()` 创建透明 host，见 `PaperWindow.DeepCapsule.cs:38`；拖拽缩放层 `_deepCapsuleSlotDragScale` 在 host root 上，见 `PaperWindow.DeepCapsule.cs:45`。
- 拖拽入口证据：左侧可点击区域按下后进入 `PendingClick` 并捕获鼠标，见 `PaperWindow.DeepCapsule.cs:168`；移动时若可排序，会先进入 reorder drag，见 `PaperWindow.DeepCapsule.cs:175`；开始拖动阈值使用系统最小拖动距离 + 额外 4 DIP，常量见 `PaperWindow.cs:191`。
- 磁吸 / 跨队列证据：`StartDeepCapsuleReorderDrag()` 会先把 host 锁回当前边缘 `_deepCapsuleDragLeft`，见 `PaperWindow.DeepCapsule.cs:1799`；`UpdateDeepCapsuleReorderDrag()` 在未解锁前只改 Top，不改 Left，见 `PaperWindow.DeepCapsule.cs:1831`；`ShouldUnlockDeepCapsuleCrossQueueDrag()` 只有向外拖超过 `DeepCapsuleCrossQueueDragUnlockDistance=56` 才解锁跨队列，见 `PaperWindow.DeepCapsule.cs:1953`、`PaperWindow.cs:192`。
- 拖拽视觉证据：跨队列拖拽宽度由“左 padding + 图标 + 标题 + 右 padding + chrome”计算，并取不小于普通胶囊宽度，见 `PaperWindow.DeepCapsule.cs:1013`；进入跨队列视觉时关闭区透明且不可命中，见 `PaperWindow.DeepCapsule.cs:1039`、`PaperWindow.DeepCapsule.cs:1870`。这符合“拖拽态是去掉关闭区的正常胶囊，不是纯球”的目标。
- 混合 DPI 证据：拖拽中的起点、当前位置和 drop 点通过 `WindowWorkAreaHelper.DeviceScreenPointToDip()` 从设备像素转 DIP，见 `PaperWindow.DeepCapsule.cs:1794`；drop 监视器用 `MonitorAtDeviceScreenPoint()`，见 `PaperWindow.DeepCapsule.cs:1990`。这与多屏混合 DPI 修复方向一致。
- drop 结束证据：未解锁跨队列时只调用 `ReorderDeepCapsule()`，解锁后根据 drop 点所在监视器和左右半区决定目标队列，见 `PaperWindow.DeepCapsule.cs:1961`；若目标队列未变则仍回退为队内排序。
- 滑出 / 滑回证据：`MoveExpandedDeepCapsuleSlotHost()` 不再独立动画 `Left + Width`，而是每帧固定墙边，按 rounded left/right 差值推导宽度，见 `PaperWindow.DeepCapsule.cs:499`；`ApplyDeepCapsuleSlotHorizontalProgress()` 同样按左右边界舍入后设置 host bounds，见 `PaperWindow.DeepCapsule.cs:1234`。这针对右侧贴边漏白和滑回先变窄问题。
- slot 释放证据：`ClearDeepCapsulePlacement()` 会先关闭跨队列拖拽视觉，再按是否需要动画走 `RetractAndHideDeepCapsuleSlotHost()` 或立即清状态，见 `PaperWindow.DeepCapsule.cs:1683`；释放动画用 `_deepCapsuleSlotMoveGeneration` 拦截过期 Completed 回调，见 `PaperWindow.DeepCapsule.cs:1568`、`PaperWindow.DeepCapsule.cs:1617`；隐藏完成会恢复 host root opacity / hit test，避免残留不可点状态，见 `PaperWindow.DeepCapsule.cs:1641`。
- 右键菜单 guard 证据：贴边 slot 菜单打开时注册 foreground 和低级鼠标 hook，见 `PaperWindow.DeepCapsule.cs:772`；菜单关闭、slot host 真关闭时都会停止 guard，见 `PaperWindow.DeepCapsule.cs:760`、`PaperWindow.DeepCapsule.cs:703`。本片段未发现 hook 永久残留路径。
- expanded reservation 证据：`UpdateDeepCapsuleExpandedSlotMode()` 在关闭贴边 / 隐藏 / 关闭保留槽位时会把 `ExpandedReserved` 拉回 `None` 或触发 `ClearDeepCapsulePlacement()`，见 `PaperWindow.DeepCapsule.cs:1759`；设置调用方随后 `ArrangeDeepCapsules()` 和 `SaveNow()`，见 `AppController.Settings.cs:1185`。
- 左 / 右镜像证据：slot host 位置按纸片自己的 `CapsuleSide` 解析，不依赖全局 anchor，见 `PaperWindow.DeepCapsule.cs:916`；固定布局会按边缘镜像 chrome / shell / outline 的 margin 和 alignment，见 `PaperWindow.DeepCapsule.cs:1100`；内部 close 区和标题区左右交换，见 `PaperWindow.DeepCapsule.cs:1153`。
- 右侧缩回保护证据：`SetDeepCapsuleSlotHostHorizontalBounds()` 对右侧队列按“缩小时先移动再改宽、外扩时先改宽再移动”的顺序操作顶层窗口，降低 Win32 窗口 Left / Width 分离更新导致的漏边风险，见 `PaperWindow.DeepCapsule.cs:952`。
- 关闭区证据：slot close area 点击只调用 `HidePaper(_paper)`，语义是隐藏不是删除，见 `PaperWindow.DeepCapsule.cs:248`；`CloseForReal()` 会先 `CloseExpandedDeepCapsuleSlotHostForReal()`，见 `PaperWindow.cs:608`。
- 结论：本文件逐段深读完成。未在本文件内发现新的持久化状态混写、hook 残留、过期动画回调误清新状态或跨队列拖拽绕过磁吸阈值的问题。仍需在跨模块阶段继续验证它和 `PaperWindow.Capsule.cs` 的普通折叠 / 展开状态机边界。

#### `PaperWindow.Capsule.cs`

- 职责：普通胶囊 UI、折叠 / 展开动画、普通胶囊关闭区、胶囊资格变化后的自动展开。
- 胶囊资格证据：`UpdateCapsuleMode()` 和 `RefreshCapsuleEligibility()` 都会在当前纸片已经折叠但不能再显示胶囊时调用恢复逻辑，见 `PaperWindow.Capsule.cs:64`、`PaperWindow.Capsule.cs:80`。
- 发现并修复：A003。旧逻辑在贴边折叠纸片失去胶囊资格时先 `ClearDeepCapsulePlacement()`，再 `SetCollapsedState(false)`；由于贴边休眠时主窗口通常是隐藏的，可能留下 `paper.IsVisible=true` 但无可见主窗口 / slot 的状态。已改为 `RestoreFromCapsuleAfterEligibilityLoss()`：先从贴边 slot 恢复主窗口，再展开，并按贴边边缘对齐；证据见 `PaperWindow.Capsule.cs:96`。
- expanded slot 资格证据：`keepDeepCapsuleSlotReservation` 已加入 `CanDisplayAsCapsule()`，所以失去资格的纸片展开时不会继续保留贴边 expanded slot，见 `PaperWindow.Capsule.cs:469`。
- 普通关闭区证据：普通胶囊关闭区点击调用 `_controller.HidePaper(_paper)`，语义为隐藏而非删除，见 `PaperWindow.Capsule.cs:399`。
- 折叠动画证据：`SetCollapsedState()` 使用 `_collapseTransitionGeneration` 防止旧动画 Completed 覆盖新状态，见 `PaperWindow.Capsule.cs:456`、`PaperWindow.Capsule.cs:633`；折叠完成后如果是贴边模式，会 `ArrangeDeepCapsules()`、重置 `OpenOrigin` 并隐藏主窗口，见 `PaperWindow.Capsule.cs:690`。
- 几何证据：折叠 / 展开期间 `_isApplyingCollapsedState=true`，`SaveGeometryIfAllowed()` 会跳过保存；动画完成后 `UpdateGeometry()` 只在非折叠状态保存宽高，跨文件证据见 `PaperWindow.cs:2129`、`AppController.cs:1270`。
- 验证：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

#### `PaperWindow.cs`

- 职责：单纸片主窗口、共享视觉资源、主窗口关闭语义、标题栏、上下文菜单、标题编辑、删除确认、笔记拖到待办入口、几何保存、topmost 刷新、折叠动画依赖属性。
- 状态边界证据：窗口持有 `_paper` 和 `_controller`，同时集中声明瞬态 UI / 动画 / 拖拽状态，见 `PaperWindow.cs:49`；深胶囊状态机文档化在 `PaperWindow.cs:228`，说明 `SlotState / VisualState / GestureState / OpenOrigin` 是窗口瞬态轴。
- 生命周期证据：构造函数注册 `Loaded / LocationChanged / SizeChanged` 到 `SaveGeometryIfAllowed()`，并注册关闭、拖拽取消、激活刷新等事件，见 `PaperWindow.cs:559`；`CloseForReal()` 先关闭贴边 slot host，再设置 `_closeForReal` 真关闭，见 `PaperWindow.cs:608`。
- 关闭语义证据：`OnClosing()` 默认 `e.Cancel=true` 并调用 `_controller.HidePaper(_paper)`，只有 `_closeForReal` 才真实关闭窗口，见 `PaperWindow.cs:1797`。这符合“关闭是隐藏，不是删除”的约束。
- 几何保存证据：`SaveGeometryIfAllowed()` 在 `_isApplyingCollapsedState` 或 `SuppressGeometrySave` 时跳过，见 `PaperWindow.cs:2129`；`MoveWindowWithoutGeometrySave()` 是窗口内部摆放 / 对齐的统一闸，见 `PaperWindow.cs:2139`。
- topmost 证据：普通窗口 topmost 由 `AlwaysOnTop` 或“胶囊折叠”决定，再叠加全屏避让状态，见 `PaperWindow.cs:1434`；贴边 slot host topmost 走同一套 `WindowNative.ApplyTopmostZOrder()`，见 `PaperWindow.cs:1447`。
- 标题 / 菜单证据：标题编辑只通过 `_controller.UpdatePaperTitle()` 提交，见 `PaperWindow.cs:1536`；菜单里的隐藏 / 删除分别调用 `_controller.HidePaper()` 和 `_controller.DeletePaper()`，见 `PaperWindow.cs:1387`、`PaperWindow.cs:1625`。
- 关联笔记拖拽证据：拖动开始 / 更新 / 结束均委托给 `AppController.BeginNoteLinkDrag()` / `UpdateNoteLinkDrag()` / `EndNoteLinkDrag()`，本文件只负责视觉 ghost 和鼠标捕获，见 `PaperWindow.cs:1119`。
- 主题证据：`UpdateTheme()` 刷新窗口动态资源、主壳画刷动画、标题 / 图标 / note box / todo rows、贴边 slot host theme，见 `PaperWindow.cs:712`。这覆盖 AGENTS 中“主题变化要刷新动态生成控件和 AvalonEdit”的窗口侧要求，托盘 / 设置侧仍在对应文件审。
- 结论：本文件逐段深读完成。未发现关闭 / 删除语义混淆、窗口内部摆放直接写坏普通几何、或绕过控制器修改持久纸片状态的新问题。

#### `PaperWindow.Native.cs`

- 职责：`PaperWindow` 私有的 Win32 hook / foreground / low-level mouse P/Invoke 声明，主要服务贴边 slot 右键菜单 guard。
- 证据：包含 `SetWinEventHook`、`UnhookWinEvent`、`SetWindowsHookEx`、`UnhookWindowsHookEx`、`CallNextHookEx` 和 `GetWindowThreadProcessId` 声明，见 `PaperWindow.Native.cs:35`；没有业务状态写入，实际生命周期在 `PaperWindow.DeepCapsule.cs` 中开启 / 停止。
- 结论：本文件为声明层；未发现主实例、保存、胶囊状态或用户数据协议风险。

#### `WindowNative.cs`

- 职责：共享 Win32 window style / z-order helper，供纸片窗口、贴边 slot host、主胶囊使用。
- no-activate 证据：`ApplyNoActivateStyle()` 给窗口加 `WS_EX_NOACTIVATE`，避免贴边 slot / 主胶囊点击抢焦点，见 `WindowNative.cs:23`。
- topmost 证据：`ApplyTopmostZOrder()` 使用 `SetWindowPos` 切换 topmost / no-topmost，带 `SWP_NOACTIVATE`，并在退出 topmost 时可插到全屏避让窗口后，见 `WindowNative.cs:37`。
- 结论：本文件不拥有业务状态；当前调用方均先检查窗口 handle / 可见性。未发现会主动激活窗口或绕过全屏避让的路径。

#### `WindowWorkAreaHelper.cs`

- 职责：把窗口 / DIP 矩形 / 设备点解析到显示器工作区，提供多屏和混合 DPI 坐标转换。
- 工作区证据：`WorkAreaFor(Rect)` 把 DIP rect 转设备 rect 后用 `MonitorFromRect()` 找最近显示器，失败回退 `SystemParameters.WorkArea`，见 `WindowWorkAreaHelper.cs:12`；`WorkAreaFor(Window)` 用窗口 handle 找最近显示器，见 `WindowWorkAreaHelper.cs:49`。
- 持久 monitor 证据：`WorkAreaForDevice()` 枚举 monitor device name，找不到时返回 null，让上层回退主屏，见 `WindowWorkAreaHelper.cs:89`。这符合拔掉显示器后 graceful fallback 的目标。
- 混合 DPI 证据：`MonitorAtDeviceScreenPoint()` 接收 `PointToScreen` 的设备像素点直接调用 `MonitorFromPoint()`，再把工作区转系统 DIP，见 `WindowWorkAreaHelper.cs:127`；贴边胶囊跨屏 drop 使用这条路径，见 `PaperWindow.DeepCapsule.cs:1990`。
- 坐标转换证据：`DeviceScreenPointToDip()` 和无 reference 的 `DeviceRectToDip()` 都用 `SystemDpiScale()`，见 `WindowWorkAreaHelper.cs:111`、`WindowWorkAreaHelper.cs:251`；`WorkAreaFor(Window)` 则使用窗口 visual 的 `TransformFromDevice`，见 `WindowWorkAreaHelper.cs:218`。
- 结论：本文件不写业务状态；主要风险是 Windows / WPF DPI 坐标系复杂，后续阶段 4 仍需要混合 DPI 手测验证。代码层未发现对 monitor device name、空显示器或失败路径的未处理异常。

#### `AppController.Settings.cs`

- 职责：设置窗口构建、设置项状态写入、主题 / 资源刷新、胶囊相关模式开关、启动项切换、tooltip 和系统主题变化响应。
- 主题证据：`SetTheme()` / `SetColorScheme()` 修改状态后 `Theme.Invalidate()`、保存、逐窗口 `UpdateTheme()`、逐 master `UpdateTheme()`、重建托盘菜单，见 `AppController.Settings.cs:19`、`AppController.Settings.cs:47`；系统主题变化且当前为 system 时也走同一刷新链，见 `AppController.Settings.cs:989`。
- 渲染 / 外部打开证据：Markdown 渲染模式写状态后刷新每个窗口的 Markdown 显示并重建托盘，见 `AppController.Settings.cs:81`；外部 Markdown 后缀通过 `ExternalMarkdownFileExtensions.Normalize()` 规范化后保存并通知窗口，见 `AppController.Settings.cs:218`。
- tooltip 证据：普通 tooltip 开关写 `State.EnableToolTips` 后刷新所有纸片、master 和设置窗口，见 `AppController.Settings.cs:1031`；设置说明图标用 `ToolTipPreferences.SetAlwaysEnabled(hint, true)`，不会被普通 tooltip 开关关闭，见 `AppController.Settings.cs:714`。
- 胶囊模式证据：关闭普通胶囊模式时同时关闭贴边模式、收起全部、per-queue active，并调用 `ResetDeepCapsuleStartTopMargins()`，再取消所有纸片折叠，见 `AppController.Settings.cs:1059`；关闭贴边模式时同样清收起全部和 per-queue 起始高度，见 `AppController.Settings.cs:1153`。
- 关联笔记证据：`ToggleHideLinkedNotesFromCapsules()` / `ToggleTodoNoteLinks()` 都调用 `RefreshCapsuleEligibilityForLinkedNotes()` 后保存，见 `AppController.Settings.cs:1119`、`AppController.Settings.cs:1127`。这覆盖 A003 的触发入口。
- expanded slot 证据：`ToggleDeepCapsuleExpandedSlot()` 更新每个窗口的 expanded slot 模式，然后 `ArrangeDeepCapsules()` 并保存，见 `AppController.Settings.cs:1185`。
- 可见面恢复证据：关闭胶囊 / 贴边后会调用 `RestoreMissingVisiblePaperSurfaces()`，把 `paper.IsVisible=true` 但无窗口表面的纸片恢复出来，见 `AppController.Settings.cs:1199`。
- 结论：本文件逐段深读完成。未发现新增的模式关闭漏清理、tooltip 说明误关、主题刷新缺口或设置后不保存的问题。

#### `AppController.Tray.cs`

- 职责：Hardcodet 托盘图标、托盘菜单模板 / 样式、菜单重建、纸片列表显隐、行内删除确认、自定义图标加载。
- 托盘图标证据：`CreateTrayIcon()` 使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`，见 `AppController.Tray.cs:21`；`LoadTrayIconSource()` 优先加载程序目录 `PaperTodo.ico`，再找内嵌 `.PaperTodo.ico`，最后生成 fallback bitmap，见 `AppController.Tray.cs:43`。
- 菜单重建证据：`PreviewTrayContextMenuOpen` 每次打开前 `RebuildTrayMenu()`，见 `AppController.Tray.cs:30`；`RefreshTrayMenu()` 只有菜单正打开且没有 suppression 时重建，见 `AppController.Tray.cs:443`。
- 首次菜单焦点证据：`CreateTrayMenu()` 的 `Opened` 事件调用 `ActivateTrayContextMenu()`，后者同步和 Dispatcher Input 阶段各尝试 focus，并调用 `WindowNative.TrySetForegroundWindow()`，见 `AppController.Tray.cs:345`、`AppController.Tray.cs:364`。
- 行内按钮抑制证据：`TrayPaperItem()` 内部用 `suppressRowClickToken` 和 `InputManager.Current.PostProcessInput` 在删除 / 确认按钮手势期间抑制行点击，见 `AppController.Tray.cs:543`；行点击处理在 `suppressRowClick || confirmMode` 时直接吞掉，见 `AppController.Tray.cs:721`。
- 删除确认证据：首次点删除进入 confirm mode，确认按钮才调用 `DeletePaper(paper)`，见 `AppController.Tray.cs:680`、`AppController.Tray.cs:701`。删除语义仍由 `AppController.DeletePaper()` 统一处理。
- 结论：本文件逐段深读完成。未发现托盘改回 `System.Drawing.Icon`、手动弹菜单、全局轮询修首次菜单或行内按钮明显误触发行点击的新路径。

#### `MasterCapsuleWindow.cs`

- 职责：每个贴边队列的 slot 0 主胶囊；显示收起全部入口、切换当前队列收起状态、拖动当前队列起始高度。
- 队列归属证据：窗口持有 `_queueEdge` 和 `_queueMonitorDeviceName`，见 `MasterCapsuleWindow.cs:42`；控制器为每个 queue key 创建 / 更新一个 master，见 `AppController.cs:1425`。
- 点击 / 拖动证据：按下时记录当前队列的起始高度，见 `MasterCapsuleWindow.cs:203`；拖动中只调用 `SetDeepCapsuleStartTopMargin(_queueMonitorDeviceName, _queueEdge, ...)`，见 `MasterCapsuleWindow.cs:243`；松手未拖动时只切换当前队列收起状态，见 `MasterCapsuleWindow.cs:261`。
- 拖动保存证据：拖动中不 `commit`，只实时重排；松手后 `commit: true` 保存，见 `MasterCapsuleWindow.cs:254` 和 `AppController.cs:2007`。这避免拖动中持续落盘。
- 左 / 右镜像证据：`ApplyMasterEdgeLayout()` 按 `_queueEdge` 镜像 margin、内容方向和 chevron 顺序，见 `MasterCapsuleWindow.cs:397`；`MoveToTarget()` 用 `DeepCapsuleLayout.DockedLeft(area, visibleWidth, _queueEdge)` 解析所属队列边缘，见 `MasterCapsuleWindow.cs:472`。
- 发现并修复：A002。右侧主胶囊在文字宽度变化时旧逻辑会先更新 `Width`，再动画 `Left`，宽度变小时会短暂离开屏幕边缘。已改为检测 `widthChanged`，宽度变化时同步把 `Left` 设到目标贴边位置，仅保留纵向动画；证据见 `MasterCapsuleWindow.cs:480`、`MasterCapsuleWindow.cs:486`、`MasterCapsuleWindow.cs:503`。
- 验证：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

## 阶段 3：跨模块不变量审查

- [ ] `data.json` 损坏时不能被空状态覆盖
- [ ] 未知字段兼容和字段迁移不能破坏旧数据
- [ ] `_saveVersion`、写锁、退出同步保存必须防止旧保存覆盖新状态
- [ ] 普通纸片 `X/Y/Width/Height` 不能保存胶囊半隐藏坐标
- [ ] 删除、隐藏、折叠三种语义不能混
- [ ] 关闭胶囊 / 贴边 / 收起全部必须清理临时 slot、激发态、动画态
- [ ] `ShowDeepCapsuleWhileExpanded` 为真时展开纸片仍保留边缘胶囊槽位
- [ ] `UseCapsuleCollapseAll` 使用 slot 0 主胶囊，真实纸片从后面开始
- [ ] 每个 `(monitor, edge)` 队列独立排序、起始高度、收起状态
- [ ] `HideLinkedNotesFromCapsules` 和待办关联状态变化后胶囊资格一致
- [ ] 单实例主进程 Mutex 释放规则正确
- [ ] `exit` / `quit` 在无主实例时保存并退出，不创建默认纸片
- [ ] 托盘必须使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`
- [ ] 主题变化必须刷新动态控件、托盘菜单、AvalonEdit
- [ ] 四语言资源 key 必须一致

## 阶段 4：高风险专项攻击

- [ ] 单屏 100% DPI 贴边胶囊
- [ ] 单屏 125% / 150% DPI 贴边胶囊
- [ ] 双屏同 DPI 左右侧队列
- [ ] 双屏混合 DPI 跨屏拖拽
- [ ] 左侧与右侧 hover 滑出 / 滑回视觉一致性
- [ ] 收起全部每队列独立收起 / 展开
- [ ] 拖单个胶囊上下排序和跨边磁吸阈值
- [ ] 拖拽中丢失捕获、Alt-Tab、释放到菜单外
- [ ] 隐藏全部 / 显示全部 / 关闭模式 / 重启恢复
- [ ] 托盘菜单首次点击、行点击、删除确认、菜单重建
- [ ] 新建纸片位置和来源队列继承
- [ ] 待办多行粘贴、拖拽排序、撤销重做
- [ ] 笔记 Markdown 大文本、链接、外部打开
- [ ] 全屏避让和 topmost 层级

## 阶段 5：性能审查

- [ ] 200ms topmost timer 是否做重活
- [ ] 拖拽过程中是否触发保存、全量重建或昂贵测量
- [ ] 胶囊重排复杂度是否可接受
- [ ] Markdown 渲染和大文本保护是否仍有效
- [ ] 托盘菜单重建是否只在必要时发生
- [ ] 主题切换是否重复 rebuild 过多
- [ ] 透明窗口移动 / 动画是否造成可感知压力

## 阶段 6：交互、视觉、动画审查

补动画原则：状态去向不清楚、跳变让用户误解、左右侧不一致时补；如果动画拖慢操作、制造错觉或增加风险，就不补。

- [ ] 普通纸片显示 / 隐藏
- [ ] 普通胶囊折叠 / 展开
- [ ] 贴边胶囊 hover 滑出 / 滑回
- [ ] 展开后边缘胶囊激发态
- [ ] 收起全部 retract / release
- [ ] 单胶囊跨队列拖出、松手归位
- [ ] 待办新增 / 删除 / 排序
- [ ] 关联笔记入口变化
- [ ] 托盘操作反馈
- [ ] 设置切换后的即时反馈
- [ ] 主题切换过渡
- [ ] 关闭动画开关后所有动画立即完成

## 阶段 7：修复循环

每个问题必须记录：

- 问题描述
- 影响范围
- 触发路径
- 修复方案
- 代价和风险
- 是否更新 `CHANGELOG.md`
- 验证路径

问题列表：

- [x] A001：backup 恢复后首次保存可能覆盖解析失败的主数据文件
  - 问题描述：`StateStore.Load()` 在 `data.json` 解析失败但 `data.backup.json` 可加载时返回 backup 状态；随后 `WriteJsonInternal()` 会先把当前 `data.json` 复制到 `data.backup.json`，再覆盖 `data.json`。这会丢失 backup 原件，并用旧 backup 生成的新主文件覆盖失败主文件。
  - 影响范围：数据恢复、启动失败保护、`exit` 启动命令、正常启动后的首次自动保存。
  - 触发路径：`StateStore.cs:25` -> `StateStore.cs:55` -> `AppController.cs:68` -> `AppController.cs:93` / `AppController.cs:2046` -> `StateStore.cs:124`。
  - 修复方案：`StateStore` 记录本次是否从 backup 恢复且主文件解析失败；首次保存前保留失败主文件和可用 backup 的原始副本，并跳过这次把坏主文件轮换进 backup 的操作。
  - 代价和风险：需要小幅扩展保存层状态；要避免影响正常保存、异步版本锁和 backup 轮换。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为数据恢复保护修复。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示；隔离复制发布目录后构造损坏 `data.json` + 可用 `data.backup.json`，执行 `PaperTodo.exe exit` -> `ExitCode=0`、`FailedCopies=1`、`RecoveryBackupCopies=1`、`BackupStillOriginal=True`、`MainRecovered=True`。

- [x] A002：右侧贴边主胶囊宽度变化时可能短暂离开屏幕边缘
  - 问题描述：`MasterCapsuleWindow.MoveToTarget()` 旧逻辑先调用 `ApplyDockedWidth()` 改 `Width`，再动画 `Left`。右侧队列里如果主胶囊文案宽度变小，窗口右边缘在动画期间会短暂从屏幕边缘向内缩，形成和普通贴边胶囊旧问题同类的漏白 / 离边。
  - 影响范围：收起全部主胶囊，尤其是右侧队列、收起 / 展开切换、计数文本宽度变化、语言文本长度变化。
  - 触发路径：`AppController.SyncMasterCapsules()` -> `MasterCapsuleWindow.UpdateState()` -> `MasterCapsuleWindow.MoveToTarget()`。
  - 修复方案：在 `MoveToTarget()` 中检测可见宽度变化；宽度变化时同步把 `Left` 放到新的贴边目标，不再让宽度变化和水平移动分两阶段呈现；纯位置变化仍保留水平动画。
  - 代价和风险：右侧主胶囊文案宽度变化时少一个短距离水平补间，但换来边缘始终贴合；纵向移动动画保留。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为贴边主胶囊动画修正。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A003：贴边胶囊失去胶囊资格时可能恢复成不可见纸片
  - 问题描述：已折叠且停靠在贴边 slot 的纸片，如果因为关联笔记隐藏、关联功能切换等原因不能再显示为胶囊，旧逻辑会先清 slot，再展开普通窗口；但主窗口在贴边休眠状态下通常是隐藏的，展开逻辑不会自动 `Show()`，可能造成纸片仍 `IsVisible=true` 但没有可见表面。
  - 影响范围：隐藏已关联笔记、关闭 / 打开待办关联功能、其他导致 `CanPaperDisplayAsCapsule()` 从 true 变 false 的路径。
  - 触发路径：`AppController.RefreshCapsuleEligibilityForLinkedNotes()` -> `PaperWindow.RefreshCapsuleEligibility()` -> `ClearDeepCapsulePlacement()` -> `SetCollapsedState(false)`。
  - 修复方案：新增 `RestoreFromCapsuleAfterEligibilityLoss()`，在贴边 slot 仍存在时先 `ShowMainWindowForDeepCapsuleActivation()` 恢复主窗口，再调用 `SetCollapsedState(false, alignExpandedToDockedEdge: true)`；同时 expanded slot 保留条件加入 `CanDisplayAsCapsule()`。
  - 代价和风险：资格丢失时会主动把纸片展开为普通窗口，这是“不能再显示为胶囊”的正确退路；不新增持久化字段。
  - 是否更新 `CHANGELOG.md`：已合并进 `### Unreleased` 的待办关联设置修复描述。
  - 验证结果：代码路径核对 + `dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

## 阶段 8：回归矩阵

- [ ] `dotnet build PaperTodo.csproj -c Release`
- [ ] `git diff --check`
- [ ] 资源 key parity
- [ ] 单屏基础手测
- [ ] 多屏同 DPI 手测
- [ ] 多屏混合 DPI 手测
- [ ] 托盘菜单手测
- [ ] 待办 / 笔记核心路径手测
- [ ] 退出保存 / 重启恢复手测
- [ ] 启动命令手测：`show` / `hide` / `toggle` / `new-todo` / `new-note` / `exit`

## 阶段 9：用户蒸馏层最终复核

在代码事实审计完成后，再读取用户蒸馏文件，用它审查产品判断：

- [ ] 读取蒸馏文件
- [ ] 是否仍符合“桌面上的几张纸”
- [ ] 是否有功能膨胀
- [ ] 是否把实现复杂度转嫁给用户
- [ ] 哪些应该砍、留、延后

## 阶段 10：最终报告

- [ ] 高 / 中 / 低风险问题清单
- [ ] 已修复问题清单
- [ ] 已排除风险清单
- [ ] 结构债清单
- [ ] 性能判断
- [ ] 视觉和动画判断
- [ ] 发布前必修清单
- [ ] 发布后可缓修清单
- [ ] 最终建议：能否发 rc，能否发正式版，剩余风险是什么

## 审核日志

### 2026-06-19

- 创建本文件，建立全量审核执行框架。
- 已记录起点分支、提交、变更规模和文件集合规模。
- 完成基线构建、空白检查、资源 key parity 和版本号状态记录。
- 完成系统地图第一版，记录模块职责和状态所有权；后续逐文件深读会校正这张图。
- 完成 `Models.cs`、`StartupCommand.cs`、`SingleInstanceHelper.cs`、`App.xaml.cs`、`App.xaml` 的逐文件深读记录。
- 深读 `StateStore.cs` 时发现 A001：backup 恢复后的首次保存可能破坏失败主文件和 backup 原件，已加入修复循环。
- 修复 A001：backup 恢复后的首次保存会先保留失败主文件和恢复用 backup，并跳过一次 backup 轮换；`CHANGELOG.md` 已记录用户可感知的数据恢复保护改进。
- 验证 A001：Release 构建通过；隔离运行 `PaperTodo.exe exit` 覆盖损坏主文件 + 可用 backup 场景，确认失败主文件副本、恢复用 backup 副本、原 backup 和恢复后的主文件均符合预期。
- 开始 `AppController.cs` 分段深读：已覆盖启动命令、纸片创建、显隐、删除、关联笔记资格、普通几何、队列重排和每队列收起状态；文件仍标记为进行中。
- 开始 `PaperWindow.DeepCapsule.cs` 分段深读：已覆盖拖拽磁吸、跨队列解锁、拖拽视觉宽度、混合 DPI drop、贴边滑出 / 滑回横向动画；文件仍标记为进行中。
- 补读 `PaperWindow.DeepCapsule.cs` 的 slot 隐藏 / 释放、context menu guard、expanded reservation 清理路径，未发现 hook 永久残留或过期动画回调误清新状态的直接路径。
- 完成 `MasterCapsuleWindow.cs` 深读，发现并修复 A002：右侧主胶囊文字宽度变化时保持贴边；Release 构建和空白检查通过。
- 完成 `PaperWindow.DeepCapsule.cs` 逐文件深读：补齐左 / 右镜像、右侧缩回顺序保护、关闭区语义、CloseForReal 清理路径；文件标记为已完成。
- 完成 `PaperWindow.Capsule.cs` 深读，发现并修复 A003：贴边胶囊失去胶囊资格时先恢复主窗口再展开，避免纸片可见状态与实际表面不一致；Release 构建和空白检查通过。
- 完成 `PaperWindow.cs`、`PaperWindow.Native.cs`、`WindowNative.cs`、`WindowWorkAreaHelper.cs` 深读记录；覆盖关闭语义、几何保存、topmost/no-activate、多屏工作区和混合 DPI 坐标转换。
- 本轮复验：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。
- 完成 `AppController.Settings.cs` 深读记录；覆盖设置窗口、主题刷新、tooltip 说明、胶囊模式关闭清理、关联笔记资格刷新和可见面恢复。
- 完成 `AppController.Tray.cs` 深读记录；覆盖 Hardcodet `IconSource`、外部图标优先、菜单打开重建、首次菜单焦点、纸片行内删除确认和行点击抑制。
