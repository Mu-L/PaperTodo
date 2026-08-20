# PaperTodo 架构地图

> 本文是 **当前项目地图**：回答“这块是谁负责、改这里先去看哪些文件”。它不是完整设计说明、历史记录或未来方案。
>
> - 具体实现以当前代码为准；发现本文、代码或 [`DECISIONS.md`](DECISIONS.md) 冲突时，结合提交历史和可观察行为重新核对。
> - “为什么这样选、哪些旧路线不要恢复”见 [`DECISIONS.md`](DECISIONS.md)。
> - Agent 执行规则、硬禁令和文档维护方式见 [`AGENTS.md`](AGENTS.md)。

## 1. 快速入口

| 想改/想查 | 先看 | 主要 authority |
| --- | --- | --- |
| 启动、GUI 单实例、全局异常 | `App.xaml.cs`、`SingleInstanceHelper.cs` | `App` / `SingleInstanceHelper` |
| `--mcp` 独立 bridge | `McpBridge.cs`、`McpPipeClient.cs` | `McpBridge` |
| GUI 侧 MCP runtime | `AppController.Mcp.cs`、`McpApiHost.cs`、`McpCommandService.cs` | `AppController` |
| 全局状态、纸片集合、跨纸片协调 | `AppController.cs` 及各 partial | `AppController` |
| `data.json` 保存/恢复 | `StateStore.cs`、`Models.cs` | `StateStore` / `AppState` |
| 笔记图片 | `NoteImageStore.cs`、`LmdbImageDatabase.cs` | `NoteImageStore` |
| 插件设置与 per-paper 状态 | `PaperBodyPluginDataStore.cs`、`PaperBodyPluginRegistry.Settings.cs` | `PaperBodyPluginDataStore` |
| 插件/MCP 对 Paper/Todo/Note 的统一读写 | `PaperCommandService.cs`、`AppController.PluginApi.cs` | `PaperCommandService` |
| 普通纸片 UI / 生命周期 | `PaperWindow.cs`、`PaperWindow.Lifecycle.cs` | `PaperWindow` |
| Todo | `PaperWindow.Todo.cs`、`TodoRules.cs` | `PaperWindow` / `PaperData.Items` |
| Markdown Note | `PaperWindow.Note.cs`、`MarkdownTextBox.cs` | `PaperWindow` / `MarkdownTextBox` |
| paper-body provider/session | `PaperBodyPluginRegistry.cs`、`PaperBodyHost.cs` | Registry / `PaperBodyHost` |
| 插件 Host API | `PaperBodyPluginHostApi.cs`、`AppController.PluginApi.cs` | Host API + `PaperCommandService` |
| Web body / Web mini | `WebPaperBodySession*.cs` | `WebPaperBodySession` |
| Native mini / 正文迁移 | `PaperWindow.PluginMiniView.cs`、`PaperWindow.PluginBodyMigration.cs` | `PaperWindow` 的插件呈现适配层 |
| Edge 单纸片状态 | `EdgeCapsuleModel.cs`、`EdgeCapsuleReducer.cs` | Reducer / Model |
| Edge 单纸片呈现 | `EdgeCapsulePresenter.cs`、`EdgeCapsuleTargetPlanner.cs` | `EdgeCapsulePresenter` |
| Edge 物理几何 | `EdgeCapsuleGeometry.cs` | `EdgeCapsuleGeometry` |
| Edge 队列位置 | `EdgeCapsuleQueueCoordinator.cs` | `EdgeCapsuleQueueCoordinator` |
| Edge 队列 preview / transaction | `AppController.EdgeCapsule*.cs` | `AppController` edge partials |
| Edge docked HWND | `EdgeCapsuleHost.cs` | `EdgeCapsuleHost` |
| Edge DComp 平移 | `EdgeCapsuleQueueCompositionProxy*.cs` | queue composition proxy |
| Edge floating drag | `EdgeCapsuleDragWindow.cs` | `EdgeCapsuleDragWindow` |
| Edge 动画节拍 | `EdgeCapsuleFrameScheduler.cs` | shared frame scheduler |
| 主胶囊 | `MasterCapsuleWindow.cs`、相关 `AppController` partial | `MasterCapsuleWindow` + controller |
| 托盘 | `AppController.Tray.cs` | `AppController` / Hardcodet `TaskbarIcon` |
| 全屏、显示器、虚拟桌面 | `AppController.Fullscreen*.cs`、`AppController.VirtualDesktops.cs` 等 | `AppController` 对应 runtime |
| 脚本胶囊 / PowerShell | `PaperWindow.Note.cs` 中 script runtime | `PaperWindow` 的共享 script-process runtime |

## 2. 进程与启动边界

PaperTodo 的**正常 GUI 宿主是单实例 WPF 进程**。GUI 启动通过 `SingleInstanceHelper` 的 Mutex + named pipe 转发后续启动命令；主实例创建 `AppController` 并恢复应用运行时。

同一个 `PaperTodo.exe` 还支持独立的 `--mcp` bridge 模式。`App.OnStartup` 会在进入 GUI 单实例协议之前把该模式交给 `McpBridge`；bridge 使用 stdio MCP transport，再通过 GUI 侧 MCP 接口访问主宿主。

脚本胶囊可启动 PowerShell 子进程，Web 插件使用 WebView2 runtime。这些辅助进程不拥有第二份 `AppState` authority。

## 3. 状态与持久化地图

当前有三个主要持久化域：

| 数据 | 位置 | authority |
| --- | --- | --- |
| 核心应用/纸片状态 | `data.json` + `data.backup.json` | `StateStore` |
| Note 图片二进制 | `note-assets.lmdb` | `NoteImageStore` / `LmdbImageDatabase` |
| 插件 settings + per-paper state | `plugins/data/*.json` | `PaperBodyPluginDataStore` |

`AppState` 是核心持久化根；`PaperData` 是单纸片模型；Todo 行是 `PaperItem`。删除、隐藏、折叠是不同语义，普通纸片几何与 Edge 的 slot/expanded 恢复几何也是不同语义。

保存/恢复、备份保护和图片 GC 的具体安全取舍见 D-002、D-003。插件状态独立于 `data.json`，不要从核心保存流程推导它的恢复/写入行为。

## 4. Paper、插件与外部命令

`PaperWindow` 拥有单纸片 WPF shell、普通交互和 provider 选择；`PaperBodyHost` 拥有一张纸当前 `IPaperBodySession` 的 attach / invoke / commit / dispose 边界；`PaperBodyPluginRegistry` 负责 builtin / Native / Web provider 的发现和校验。

Native plugin 是 fully trusted / unsandboxed；已经载入 CLR 的 Native provider 不能按 Web provider 的方式安全热替换。当前插件协议、尺寸和权限等具体合同直接看 `PaperTodo.Plugin.Abstractions/` 与 `PaperBodyPluginRegistry*.cs`。

插件和 MCP 需要读写 Paper/Todo/Note 时，共享 `PaperCommandService`。它统一处理验证、保存/失败回滚、外部变更发布和必要的 UI 刷新；transport 权限和 surface 生命周期留在各自上层。

插件 Edge mini 的当前实现入口：

- Native 专属 mini：`PaperWindow.PluginMiniView.cs`。
- Web `miniEntry`：`WebPaperBodySession.Mini.cs`。
- 可迁移 Native 正文 View：`PaperWindow.PluginBodyMigration.cs`。
- 没有专属能力时的 capsule/plain-text fallback：`PaperWindow.PluginMiniView.cs`。

具体 fallback 时序和实现参数不在本文复制；相关长期 ownership 取舍见 D-018。

## 5. Edge Capsule 地图

Edge Capsule 分成两个层级：

- **单纸片**：`EdgeCapsuleIntent` → `EdgeCapsuleReducer` / `EdgeCapsuleModel` → `EdgeCapsulePresenter` → `EdgeCapsuleHost.Apply(frame)`。
- **队列级**：`AppController.EdgeCapsule*.cs` 协调 preview owner/corridor、arrange、visual transaction、proxy 生命周期与跨纸片事务。

找问题时按职责进入：

- 状态合法性：Reducer / Model。
- target 和动画 reconcile：Presenter / TargetPlanner。
- monitor/edge/DIP → physical rect：Geometry。
- index/master offset/slot count：QueueCoordinator。
- docked HWND/WPF visual：Host。
- 同队列 compositor translation / handoff：QueueCompositionProxy。
- floating drag：DragWindow。
- shared Rendering cadence：FrameScheduler。
- master slot 0：MasterCapsuleWindow。

长期不可回退的 V3 Lite 取舍集中在 D-005～D-014：单一 per-paper authority、bounded live host、WPF shape / DComp translation-only、显式 visual authority、successor 继承、terminal-frame handoff、真实 `InteractiveBounds` 命中、独立 floating host、队列不分页等。需要理解原因时读 Decisions，而不是在本文找完整实现说明。

## 6. OS 与全局运行时

`AppController` 负责托盘、全局快捷键、foreground/fullscreen 与 topmost 策略、display/DPI 延迟刷新、Todo reminders、virtual desktop、实验性窗口能力以及 GUI 侧 MCP runtime。

托盘当前入口是 `AppController.Tray.cs`，底层使用仓库固定的 `vendor/wpf-notifyicon`；历史原因见 D-017。

这些全局 runtime 可以触发 visibility、z-order、monitor placement 等变化，但进入具体 Paper/Edge surface 后仍应走对应 subsystem 的 authority，而不是在 watcher 中复制几何或状态机。

## 7. 仓库结构

- `src/`：主程序 C# 源码。
- `Resources/`：中文默认资源及 en/ja/ko 本地化 `.resx`。
- `PaperTodo.Plugin.Abstractions/`：插件 ABI / host contract。
- `plugins/`：可直接加载的插件产物与 `plugins/data/` 运行状态。
- `plugin-samples/`：插件源码、示例和构建说明。
- `native/`：PaperTodo 自有 native 组件，例如 LMDB bridge。
- `vendor/`：固定版本 vendored dependency / submodule。
- `assets/`：图标和静态资源。
- `docs/`：GitHub Pages 站点资源，不作为内部架构文档默认目录。
- `.github/workflows/`：CI / Release。

根目录保留 `PaperTodo.csproj`、`App.xaml*`、README/CHANGELOG，以及 `AGENTS.md`、`ARCHITECTURE.md`、`DECISIONS.md` 三个仓库级知识入口。
