# PaperTodo Agent 备忘

本文件只记录 **Agent 执行规则、隐藏硬约束和容易误改的禁区**。它不是 PaperTodo 的第三份架构说明。

当前代码描述“现在实际怎么跑”，但不天然代表正确设计；代码、文档、决策或注释冲突时，先结合当前实现、提交历史和可观察行为核对，再统一修正。

## 项目知识入口

涉及架构、ownership、跨子系统行为或历史方案时，按职责读取：

- [`ARCHITECTURE.md`](ARCHITECTURE.md)：**当前实际架构、数据流、ownership**。
- [`DECISIONS.md`](DECISIONS.md)：**为什么这样选、哪些路线已被否决、关键踩坑**。
- `AGENTS.md`：Agent 的工作方法、硬禁令、提交/发布/文档维护规则。
- 关键代码注释：局部 why、不变量和危险边界。

不要只依赖当前对话、PR 描述或旧 Agent 记忆。需要理解“系统现在是什么”时优先读 Architecture；需要判断“某条旧方案能不能重新做”时优先查 Decisions；不要把两者的正文复制回本文件。

## 文档与代码同步

每次代码变更在提交前做一次**知识影响判断**：是否改变了架构事实、已有技术取舍、Agent 硬约束或局部隐藏不变量。只有受影响的知识源需要修改，但一旦受影响，必须和代码在同一变更中同步。

涉及架构、ownership、历史方案或文档整理时，先完整检查现有文档，再读相关代码和 git 提交记录；事实核对完成后再统一修订，不要边发现边写出一套随后又被推翻的说明。

不要新增并行描述“当前完整架构”的专题文档。专题文档只能补充根文档没有承载的局部材料，并明确链接当前 Source of Truth。一次性验证、PR 过程和临时手工场景不升级成长期验收矩阵；长期可证明的正确性优先进入编译、测试、诊断日志和可执行检查。

### `ARCHITECTURE.md` 写入规则

- 它是**当前状态文档**，不是历史日志。优先修改/重写已有章节，使正文只保留变更后的最终架构；被替代机制从当前正文删除。
- ownership、主要数据流、状态/持久化协议、paper/window/plugin 生命周期边界、关键 runtime 职责、重要 OS/进程集成和仓库主结构变化通常需要更新；颜色、文案、普通常量和不改变职责边界的局部实现通常不写。
- 写入前重新核对当前代码入口、owner 和调用链。无法从当前代码确认的计划、猜测和候选方案不写进 Architecture。
- Architecture 写“选择后的结构结果”；取舍原因、失败路线和禁止回退的历史放到 Decisions。
- 不复制易变实现参数，例如普通毫秒数、重试次数、诊断阈值；只有数值本身构成稳定协议/兼容边界时才记录。
- 新增 subsystem 时先尝试纳入现有 ownership 表、数据流和章节；只有形成新的一级边界才扩章节。
- 若只是纠正文档与既有代码的偏差，可单独做事实校准提交；不要把文档校准伪装成架构变更。

### `DECISIONS.md` 写入规则

- Decisions 记录**以后仍需要知道的 why**。形成明确技术/产品取舍、否决一条看似可行的路线、确认可复用的结构性踩坑、改变兼容/ownership 原则，或未来 Agent 很可能再次提出同一方案时更新。
- 普通 bugfix、参数微调、UI 调整、测试结果、临时诊断和 PR 逐步试错不自动新增 decision；只有最终产生可复用选择或教训时才提炼。
- 写新条目前先搜索现有 D-xxx。同一决策的澄清、证据补充或边界收紧直接更新原条目；只有独立新选择或真正替代旧选择时新增编号。
- 新条目使用下一个连续 `D-xxx`，至少包含 `Status`、`Decision`、`Why`、`Evidence`；确有危险旧路线时再写 `Rejected / Do not reintroduce`，需要时加 `Consequences`。
- `Rejected` 只记录已有证据证明危险、复杂或不符合当前路线的方案，不把“没选中”自动升级成永久禁令。
- `Evidence` 优先指向当前代码中的文件/类型/关键入口；历史因果确实重要时再补关键 commit/PR。聊天记录不作为长期证据。
- 旧决策真正失效时标记 `Superseded` 并指向新 D-xxx；只是措辞/证据修正则维护原条目。
- Decisions 不是 changelog。把试错压缩成最终选择、关键失败原因和以后不能忘的边界；完整过程留在 git/PR。
- 检查后确认没有新取舍、也没有改变已有 decision 适用范围时，不修改 Decisions。

## 工作方式

不要用临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现交付改动。除非改动巨大到需要重新定路线，应按真实产品结构解决。

避免两个极端：不要为缺乏证据的少数极端场景把系统膨胀成过重框架，也不要用一次性补丁不断叠加并行状态。优先修清 ownership、数据流和真实高风险边界。

需要提交时，如果改动能按功能边界无损拆分，并且每个提交都保持可理解、可独立回滚，应拆成独立提交；不要混入无关文档、备份文件或用户的其他改动。

## 产品边界

PaperTodo 当前交互中心仍是“桌面上的几张纸”。没有明确产品决策时，不要把局部需求自行扩张成中心式任务管理器、中心式知识库编辑器、主管理页或整套账号/云同步/分类/标签/搜索/归档系统。

这只是默认防扩张规则，不是永久否决清单。已经存在的能力或后续明确的新方向以当前代码和最新 decision 为准；产品边界发生变化时更新 D-001 和本节。

Markdown 当前保持轻量。若要扩展到网络图片、表格、附件、块级 HTML 或完整块编辑器，先按产品/架构变更处理，不要在局部渲染代码里偷偷扩协议。

## 数据与持久化硬约束

当前数据结构、保存流程和图片资产 ownership 见 Architecture 第 4 节；数据安全取舍见 D-002、D-003。

- `data.json` 是用户数据协议，不是缓存。字段删除/改名必须考虑旧数据兼容。
- 不绕过 `StateStore` 建立第二套主状态写入；保留版本化写入和退出同步保存语义。
- 不绕过 `NoteImageStore` 直接开启 LMDB transaction；图片 GC / id reuse 不能在保护引用扫描不可信时继续执行。
- 启动解析失败时不能用默认空状态覆盖旧数据；crash handler 不走普通“最后强存一次”流程。
- 普通纸片几何与 edge slot/expanded 恢复几何不能互相覆盖。
- 外部打开笔记的临时文件后缀只做文件名合法性校验；不要擅自收窄成固定白名单。

## 单实例与托盘

- 只有主实例释放 single-instance Mutex；后续进程只转发命令并退出。
- `exit` / `quit` 在没有现成主实例时也不能为了执行命令恢复窗口或创建默认纸片。
- 托盘当前实现见 Architecture 7.1，历史原因见 D-017。不要把 Hardcodet `TaskbarIcon.IconSource` 改回 `System.Drawing.Icon`，也不要用手动 popup、预热菜单或全局鼠标轮询重新修同一首次菜单问题。

## Edge Capsule 硬约束

先读 Architecture 第 6 节以及 D-005～D-014、D-018。这里仅保留不能靠“实现方便”突破的边界：

- 单纸片 desired model / target / transition / applied frame 只有一个 `EdgeCapsulePresenter` authority；队列级 preview/transaction 由 controller 协调，但不能形成第二份 per-paper model。
- 队列 index/master offset/slot count 只由 `EdgeCapsuleQueueCoordinator` 计算；docked 物理像素几何只由 `EdgeCapsuleGeometry` 计算。**队列不分页。**
- `EdgeCapsuleHost` 只拥有 docked bounded host；`EdgeCapsuleDragWindow` 只拥有 floating surface。不要把同一 HWND/visual tree 在两种外形之间复用。
- WPF/bounded host 拥有 shape；DComp queue proxy 只做同尺寸 live-surface translation。不要重新引入 snapshot、clip/scale/effect resize、Reveal/Conceal 或 deferred-resize backend。
- proxy、real HWND、floating cover 的 visual authority 必须显式交接；任何失败路径不能出现 all-hidden gap，也不能用固定 delay 当作 terminal-frame 正确性的证明。
- pointer/preview 命中以当前 presented/applied `InteractiveBounds` 为 truth；透明 `HostBounds`、proxy envelope 和 WPF enter/leave 本身不能扩大或替代真实 hit geometry。
- `MasterCapsuleWindow` 只拥有 slot 0、自身 pill/手势和队列纵向锚点，不持有真实纸片的第二套 presenter 状态。
- 拖拽期间收到的全局 arrange 不能静默丢弃；display/DPI/z-order/drag 等环境边界必须先安全结束或恢复当前 visual authority，再进入下一状态。
- 插件 edge mini 由宿主拥有窗口/队列/输入 authority；能力链和 View 迁移边界见 Architecture 5.3 与 D-018。不要把任意 child HWND/WebView2/已挂载控件直接塞进可迁移 WPF mini，也不要在插件侧复制宿主的队列/尺寸 authority。

## 待办、笔记、主题与资源

- 多行粘贴待办形成一次用户操作时，只形成一次撤销快照。
- `PaperItem.LinkedPaperId` 是跨纸片关系，不要只在单个 UI 路径里清理；其当前影响范围见 Architecture 4.1。
- 内置 Note 编辑/浏览共享一个 `MarkdownTextBox`；不要拆成两套独立文本 surface（见 D-019）。`MarkdownTextBox` 长度上限属于 WPF 布局/渲染保护，不要无依据删除。
- 用户可见文本同步中文、英文、日文、韩文资源；`ResourceTextVersion` 只是人工检查标记，不参与运行时逻辑。
- 主题变化要主动刷新动态生成控件、托盘菜单、AvalonEdit 背景/文本/光标/覆盖层；不要假设所有动态 UI 都会自动响应资源变化。
- `EnableToolTips` 只控制普通操作提示，不关闭设置页说明图标和扩展说明。

## 用户态更新日志

`CHANGELOG.md` 面向用户，只记录从上一个正式版到当前最终状态的**用户可感知差异**；实现过程、开发期回归、协议阶段和内部重构留在 git/PR。

- `### 计划 / 待办` 写尚未完成的产品计划；`### 评估` 写取舍/暂缓原因；`### Unreleased` 只写已经完成、最终会进入下一版本的用户变化。
- 正式版已存在的问题被修复时写入；只在尚未发布开发过程中引入又修掉的回归，不单独作为用户 Bug 条目。
- 同一未发布功能后续增强直接合并进原条目，描述最终能力；不要留下 1.1 → 1.2 → 1.3 式开发演进。
- 纯内部文档、测试、CI、文件整理和无用户行为变化的重构不写 Unreleased。
- 发布前从“上一个正式版用户”的视角重读整个 Unreleased，删除阶段性和被替代描述。
- 版本小节保持既有顺序；只给真正重点内容加粗，不为格式统一滥用粗体。

## 构建与发布

- 版本号显式维护在 `PaperTodo.csproj`；不要恢复自动递增。
- `plugin-samples/` 保存插件源码/说明，`plugins/` 保存可直接加载的最终产物；主程序 publish/Release 不捆绑插件。最终插件目录不保留无必要的 PDB/XML/重复 native/shared assemblies。
- PR 分支 Windows CI 由 HEAD commit marker 控制：`[debug]` → Debug 测试包，`[ci]` → Release build，`[debug-ci]` → 两者。标记必须在本次 push 的最后一个 HEAD；不要为了触发制造空提交。
- 不重新引入已删除的 `scripts/edge-refinement-tests/` 或依赖源码字符串/文件路径/方法排列的 source-shape test；Edge 回归依赖真实编译、诊断日志和真机验证。
- 普通编译：`dotnet build PaperTodo.csproj -c Release`。
- `vendor/wpf-notifyicon` 使用父仓库记录的固定 submodule commit；更新 fork 时显式更新 gitlink，并完成构建和真实托盘手测。构建过程不自动拉取最新分支。
- 云端 Release 发布 Windows x64 self-contained 与 no-runtime 两个单文件；本地打包只生成 no-runtime。WPF 版本不启用 `PublishTrimmed` 或 Native AOT。
- 普通 build/publish 使用仓库内默认 `papertodo_lmdb.dll`；GitHub Release 必须先从仓库内 LMDB 源码 `-ForceRebuild`，不能把默认 DLL 冒充云端编译产物。
- 稳定正式版只通过完成真实多屏/混合 DPI 等发布前手测后的 `workflow_dispatch` 发布；稳定 tag push 不是发布步骤。`rc` / `alpha` / `beta` / `preview` tag 可以发布预发行版。

## 更新本文

只有 Agent 执行方式、产品默认边界、数据安全禁令、关键不可破坏 invariant、CHANGELOG/CI/发布规则等发生变化时才修改 `AGENTS.md`。系统“现在怎么实现”的细节应更新 Architecture；“为什么这样选”的内容应更新 Decisions；普通 UI/参数变化不为了制造同步痕迹修改本文件。
