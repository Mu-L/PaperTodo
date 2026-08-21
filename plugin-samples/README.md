# PaperTodo 插件开发

本文是 **当前 PaperTodo 插件开发手册**。只描述现在可用的插件合同、运行边界、构建方式和示例，不记录协议演进历史。

新插件使用：

```json
"apiVersion": "2.0"
```

宿主继续兼容加载现有 `1.8` 插件，但 **Top Bar 扩展只属于 2.0**；1.8 插件调用对应接口会被拒绝。新开发和升级中的插件不要再以 1.8 作为目标版本。

插件公开类型以 [`../PaperTodo.Plugin.Abstractions/`](../PaperTodo.Plugin.Abstractions/) 为编译期合同；宿主实际校验和运行行为以当前代码为准。需要理解 PaperTodo 内部 ownership 时再看 [`../ARCHITECTURE.md`](../ARCHITECTURE.md)，插件作者不需要先阅读主程序架构才能开始开发。

> **信任边界：PaperTodo 不为插件提供安全沙箱。** Native 与 Web 插件都应视为可信代码，只安装可信来源的插件。

## 1. 快速开始

PaperTodo 支持两种插件：

| 类型 | 适合 | 入口 | 构建 |
| --- | --- | --- | --- |
| Web | HTML/CSS/JS、本地状态面板、轻量交互 | 本地 `entry` 页面 | 不需要编译 |
| Native | .NET/WPF、复杂本地 UI、原生依赖、自定义 WPF capsule/mini | 实现 `IPaperBodyPlugin` 的 DLL | `dotnet publish`，推荐使用仓库脚本 |

两种插件最终都安装到：

```text
plugins/<插件 ID>/
```

目录名必须与 `plugin.json` 的 `id` 一致。

### 1.1 最小 Web 插件

目录：

```text
plugins/com.example.hello/
├─ plugin.json
└─ web/
   └─ index.html
```

`plugin.json`：

```json
{
  "kind": "web",
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "web/index.html"
}
```

页面在 PaperTodo 的本地顶层 origin 中运行时会获得 `window.papertodo`：

```html
<!doctype html>
<meta charset="utf-8">
<button id="hello">Hello</button>
<script>
  const button = document.querySelector('#hello');

  papertodo.paper.setTitle('Hello');
  papertodo.paper.setHeaderText('Hello 插件');
  papertodo.paper.setCapsulePresentation({
    preferredWidth: 0,
    plainText: 'Hello',
    components: [{ kind: 'text', text: 'Hello', fill: true }]
  });

  button.addEventListener('click', () => {
    papertodo.saveState({ clickedAt: Date.now() });
  });
</script>
```

开发时把 `plugin.json` 和 `web/` 复制到对应 `plugins/<id>/` 即可。Web 插件文件变化可以通过插件重载重新扫描。

### 1.2 最小 Native 插件

Native 项目使用 .NET 10 + WPF，并引用：

```text
PaperTodo.Plugin.Abstractions/PaperTodo.Plugin.Abstractions.csproj
```

示例项目配置：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

入口程序集必须包含且只包含一个公开、非抽象、带 public 无参构造函数的 `IPaperBodyPlugin` 实现：

```csharp
using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

public sealed class HelloPlugin : IPaperBodyPlugin
{
    public string Id => "com.example.hello-native";
    public string DisplayName => "Hello Native";
    public Version Version => new(1, 0, 0);
    public string ApiVersion => "2.0";
    public int StateVersion => 1;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.None;
    public PaperBodyCapabilities Capabilities =>
        PaperBodyCapabilities.None;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new Session(context);

    private sealed class Session : IPaperBodySession
    {
        private readonly PaperBodyContext _context;

        public Session(PaperBodyContext context)
        {
            _context = context;
            View = new TextBlock
            {
                Text = "Hello PaperTodo",
                Margin = new Thickness(16)
            };
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "Hello",
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = "Hello",
                        Fill = true
                    }
                ]
            });
        }

        public FrameworkElement View { get; }
        public void Dispose() { }
    }
}
```

Native `plugin.json`：

```json
{
  "kind": "native",
  "id": "com.example.hello-native",
  "name": "Hello Native",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "HelloPlugin.dll"
}
```

`id`、`version`、`apiVersion`、`stateVersion` 和 `requires` 对应的 runtime requirements 必须与入口 DLL 实现一致，否则宿主拒绝激活。

### 1.3 构建并安装 Native 插件

仓库提供统一脚本：

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

脚本会：

- 执行 Release / `win-x64` / framework-dependent publish；
- 把同目录 `plugin.json` 放入最终包；
- 移除 PDB、XML、WebView2 loader 以及宿主已经提供的共享程序集；
- 保留目标插件现有 `.runtime/`；
- 安装到 `plugins/<插件 ID>/`。

替换 Native 插件前必须退出 PaperTodo。已经载入 CLR 的 Native 插件不会在当前进程中安全热替换，修改或删除后应重启 PaperTodo。

## 2. 目录与部署边界

仓库中的目录职责：

- `plugin-samples/`：插件源码、源码侧 `plugin.json`、示例和构建脚本；
- `plugins/`：已经构建、可由 PaperTodo 直接加载的最终插件；
- `plugins/data/`：PaperTodo 代管的插件 settings 与 per-paper state；
- `plugins/<id>/.runtime/`：插件自己管理的缓存或独立长期数据。

PaperTodo 的本地 publish 和 GitHub Release 都不捆绑插件，插件独立分发。

典型目录：

```text
plugins/
├─ data/
│  └─ com.example.weather.json
└─ com.example.weather/
   ├─ plugin.json
   ├─ web/
   │  ├─ index.html
   │  └─ mini.html
   ├─ WeatherPlugin.dll
   ├─ WeatherPlugin.deps.json
   ├─ 插件私有依赖 / 原生库
   └─ .runtime/
```

`data` 是宿主保留 ID。插件 ID 必须由 3～120 个 ASCII 字母、数字、`.`、`_`、`-` 组成。

Native 最终目录只保留运行所需内容。不要分发无必要的 PDB/XML，也不要重复携带宿主共享的 `PaperTodo.Plugin.Abstractions`、Windows SDK / WinRT 或 WebView2 共享程序集。

## 3. `plugin.json`

当前 manifest 支持：

| 字段 | 说明 |
| --- | --- |
| `kind` | `web` 或 `native` |
| `id` | 插件唯一 ID；目录名必须一致 |
| `name` | 显示名称；为空时回退到 ID |
| `description` | 插件说明 |
| `version` | 插件版本，必须能解析为 `Version` |
| `apiVersion` | 新插件使用字符串 `"2.0"`；宿主兼容加载既有 `"1.8"`，但 1.8 不提供 Top Bar 扩展 |
| `stateVersion` | per-paper state 版本，至少为 1 |
| `entry` | Web 主页面或 Native 入口 DLL，必须位于插件目录内 |
| `miniEntry` | 可选，仅 Web；专属 Edge Mini 页面 |
| `miniSize` | 可选，仅与 `miniEntry` 一起使用 |
| `capabilities` | 可选：`textZoom`、`noteLinks` |
| `requires` | 可选；当前支持 `backgroundUpdates` |
| `permissions` | 可选；Paper/Todo/Note workspace 权限 |
| `settings` | 可选；由宿主绘制和保存的全局设置 |
| `startupPaper` | 可选；按用户设置自动创建/恢复一张插件纸片 |

未知 `requires` 或 `permissions` 会拒绝加载。`capabilities` 只应填写宿主当前支持的能力名称。

### 3.1 Web `entry` / `miniEntry`

`entry` 和 `miniEntry` 都必须留在插件目录中；`miniEntry` 还必须位于 Web `entry` 所在静态目录内。

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 }
}
```

没有 `miniEntry` 时不能声明 `miniSize`。

### 3.2 `requires`

当前唯一 runtime requirement：

```json
"requires": ["backgroundUpdates"]
```

只有当插件在完整正文没有呈现时仍需要保持业务运行，才声明它。不要把它当成普通能力标记；长期计时、后台状态同步等场景才需要。

### 3.3 `startupPaper`

插件可以让一个 boolean setting 控制“启动后自动创建或恢复一张插件纸片”：

```json
{
  "startupPaper": {
    "enabledSetting": "autoStart",
    "instanceKey": "main",
    "presentation": "capsule",
    "title": "天气"
  },
  "settings": [
    {
      "id": "autoStart",
      "type": "boolean",
      "name": "启动后自动显示",
      "default": false
    }
  ]
}
```

约束：

- `enabledSetting` 必须引用同一 manifest 中的 boolean setting；
- `instanceKey` 为 1～80 个 ASCII 字母、数字、`.`、`_`、`-`；
- `presentation` 只能是 `capsule` 或 `expanded`；
- `title` 最长 120 个字符；
- 创建时机、去重和恢复由宿主管理；插件只声明意图；
- 如果用户已经把原自动创建纸片改造成其他 provider/type，宿主不会强行接管或偷偷再创建一个副本。

## 4. 插件运行模型

一个插件正文会话依附于一张 Note paper。PaperTodo 负责 paper/window/edge 外壳，插件负责正文内容与自己声明的能力。

Native 的 canonical context 分为三层：

### `PaperBodyContext.Paper`

属于承载插件的 paper：

- `PaperId`
- `SetTitle(...)`：正式纸片标题；
- `SetHeaderText(...)`：展开态运行时 header；
- `SetCapsulePresentation(...)`：折叠/贴边胶囊内容。

正式标题、展开态 header、胶囊 presentation 是三个独立概念，不要用其中一个隐式代替另外两个。

### `PaperBodyContext.Body`

属于完整正文 surface：

- `Controls`
- `Theme`
- `SetInputClaims(...)`
- `MarkDirty()`
- `OpenExternal(...)`
- `RequestReload()`

`SetInputClaims` 当前支持：

- `EscapeKey`
- `ContextMenu`

它是动态输入占用声明，不是权限。进入输入模式时声明，退出时及时释放。

### `PaperBodyContext.Workspace`

属于整个 PaperTodo workspace 的受控 Paper/Todo/Note API。必须先在 manifest 中声明对应 `permissions`。

`PaperBodyContext.Host` 仍是 `Workspace` 的便利别名，新代码优先使用 `Workspace`。

### 会话生命周期

Native `IPaperBodySession` 可以实现：

- `Commit()`
- `RefreshFromModel()`
- `CancelInteractions()`
- `OnActivated()` / `OnDeactivated()`
- `OnVisibilityChanged(bool)`
- `OnPresentationChanged(bool)`
- `OnThemeChanged(...)`
- `OnTypographyChanged(...)`
- `OnDpiChanged()`
- `OnSettingsChanged(...)`
- `Dispose()`

`OnVisibilityChanged` 表示这张 paper/plugin 是否仍作为运行对象存在；`OnPresentationChanged` 表示完整正文是否正在展示和交互。计时器、订阅、异步任务和外部资源必须在 `Dispose()` 中停止/释放。

`IPaperBodyPlugin` 应当是无 paper 实例状态的 factory。PaperTodo 为每个正文会话创建新的插件对象；未被任何纸片实际使用的 Native 插件启动时只扫描 manifest，不加载 DLL，也不执行构造函数。

## 5. 状态、设置与 `.runtime`

### 5.1 Per-paper state

每个插件的宿主管理状态位于：

```text
plugins/data/<插件 ID>.json
```

其中：

- `settings`：该插件所有纸片共享；
- `papers`：按 Paper ID 保存独立 state；
- 每张纸片 state 的保存上限是 **1 MiB UTF-8 JSON**。

Native 使用：

```csharp
context.StateJson
context.StateVersion
context.TargetStateVersion
context.SaveStateJson(json)
```

状态变化后应立即提交给宿主，不要只依赖 session `Commit()`。如果 Native 已保存版本低于当前 `StateVersion`，通过 `IPaperBodyPlugin.MigrateState(...)` 转换后再创建 session；保存数据比插件声明版本更新时，宿主不会猜测降级。

Web 使用：

```js
papertodo.saveState(nextState);
papertodo.registerStateProvider(() => currentState);
```

`initialize` 同时提供 `state`、`stateVersion`、`targetStateVersion`。Web 插件自己负责把旧 shape 归一化为当前 shape，并在真实迁移后保存；不要因为解析失败直接用空对象覆盖仍可能有价值的状态。

### 5.2 恢复行为

宿主读取正常数据文件失败时：

- 保留原文件；
- 当前进程从空插件状态继续；
- 后续写入稳定的 `<插件 ID>.json.recovered`；
- `.recovered` 存在时后续优先使用它。

插件数据故障不会让 PaperTodo 核心 `data.json` 失效。

### 5.3 全局 settings

宿主支持四种设置：

- `boolean`
- `string`
- `number`
- `select`

最多三个 `quick: true` 设置。可用约束包括：

- `default`
- `min` / `max` / `step`
- `maxLength`
- `suffix`
- `placeholder`
- `options`
- `description`

示例：

```json
{
  "settings": [
    {
      "id": "showForecast",
      "type": "boolean",
      "name": "显示预报",
      "default": true,
      "quick": true
    },
    {
      "id": "city",
      "type": "string",
      "name": "城市",
      "maxLength": 40
    },
    {
      "id": "refreshMinutes",
      "type": "number",
      "name": "刷新间隔",
      "default": 15,
      "min": 1,
      "max": 120,
      "step": 1,
      "suffix": "分钟"
    },
    {
      "id": "unit",
      "type": "select",
      "name": "温度单位",
      "default": "c",
      "options": [
        { "value": "c", "name": "摄氏度" },
        { "value": "f", "name": "华氏度" }
      ]
    }
  ]
}
```

Native 从 `SettingsJson` 读取初始设置，并通过 `OnSettingsChanged` 接收更新。Web 从 `initialize.settings` 读取，并接收 `settingsChanged`。

### 5.4 `.runtime/`

`.runtime/` 不属于宿主管理的 per-paper state 协议。它适合：

- WebView2 Profile；
- 可重建缓存；
- 大型本地索引；
- 必须独立于单张 paper 生命周期的插件私有数据。

插件自己负责 `.runtime/` 的格式版本、原子写入、损坏恢复和容量控制。普通单纸片 UI/业务状态不要同时写进 `.runtime/` 和 `plugins/data/`，否则会产生两份 authoritative state。

## 6. Workspace 权限与数据 API

manifest 可声明：

```text
papers.read
papers.observe
papers.create
papers.delete

todos.read
todos.observe
todos.append
todos.update
todos.delete

notes.read
notes.observe
notes.append
notes.replace
```

Native 使用 `PaperBodyContext.Workspace`；Web 使用：

```js
await papertodo.workspace.request(method, params);
```

Web method：

```text
papers.list
papers.get
papers.create
papers.delete

todos.list
todos.append
todos.update
todos.setReminder
todos.delete

notes.get
notes.write
```

几个容易遗漏的权限组合：

- 创建带正文的 Note：除了 `papers.create` 还需要 `notes.append`；
- 创建/追加带完成状态、提醒或 `linkedPaperId` 的 Todo：还需要 `todos.update`；
- `todos.setReminder` 使用 `todos.update`；
- `notes.write` 的 append/fill-blank 使用 `notes.append`，replace 使用 `notes.replace`；
- 插件不能删除当前承载自己 active session 的 paper。

Observe 权限独立于 Read 权限。没有对应 read 权限时，事件仍可按 observe 权限投递，但敏感字段会被宿主裁剪。

Native：

```csharp
using var subscription = context.Workspace.Subscribe(
    new PaperTodoEventFilter
    {
        Kinds = new HashSet<PaperTodoEventKind>
        {
            PaperTodoEventKind.TodoChanged
        },
        ExcludeOwnOperations = true
    },
    evt => { /* refresh model */ });
```

Web：

```js
const dispose = papertodo.onHostEvent(
  ['todo.changed'],
  event => console.log(event),
  { excludeOwnOperations: true }
);
```

可订阅事件：

```text
paper.created
paper.changed
paper.deleted
todo.created
todo.changed
todo.deleted
note.changed
```

会话失效或销毁后订阅自动失效；插件自己也应及时 dispose/unsubscribe 不再需要的监听。

### 6.1 正文读写边界

Top Bar 动作不提供另一套 `GetBodyText/SetBodyText`。需要读写目标纸片时继续使用 Workspace：

- Markdown Note：`notes.get` + `notes.write`，分别受 `notes.read` / `notes.append` / `notes.replace` 权限约束；
- Todo：使用结构化 `todos.*` API，不把 Todo 伪装成 Markdown 字符串；
- 自定义插件正文：正文数据仍由对应 provider 的 state/capability 拥有，宿主不会假装所有正文都是文本。

这与 MCP 共用同一个 `PaperCommandService` 业务边界，因此保存、失败回滚、UI reconcile 和事件顺序不因为入口不同而复制第二套实现。

## 6.5 Top Bar 扩展（2.0）

Top Bar 扩展只对 `apiVersion: "2.0"` 开放。**PaperTodo 始终拥有顶栏 WPF tree、按钮尺寸、位置、主题、Hover、DPI 和 responsive layout；插件只贡献动作描述。** 不接受插件直接塞 `FrameworkElement`、Button、WebView 或任意顶栏控件。

动作有两种 scope：

- **Paper**：只显示在承载当前 session 的这张插件纸片；最多 4 个；
- **Global**：显示在所有 PaperTodo 纸片；每个 provider 当前最多 2 个；只有至少一个真实活跃 session 注册并持有它时才存在。多个同 provider session 会按 provider 去重，由最近注册的活跃 session 持有当前全局动作集合。

Global 点击回调会给插件目标纸片：`TargetPaperId`、`TargetPaperType`、`TargetBodyProviderId`。插件可据此通过 Workspace 读取/修改当前 Markdown 或 Todo；Top Bar 本身不拥有业务数据 API。

### Native

先注册统一回调，再提交自己的纸片动作和/或全局动作：

```csharp
context.Workspace.SetTopBarActionHandler(invocation =>
{
    if (invocation.ActionId == "summarize" &&
        invocation.TargetPaperType == "note" &&
        invocation.TargetBodyProviderId == "builtin.markdown")
    {
        var note = context.Workspace.GetNote(invocation.TargetPaperId);
        // 根据 notes.read 权限读取；需要改写时继续调用 WriteNote(...)
    }
});

context.Workspace.SetPaperTopBarActions(
    [
        new PaperTopBarAction
        {
            Id = "refresh",
            Icon = PaperTopBarIcon.Character("↻"),
            ToolTip = "刷新插件"
        },
        new PaperTopBarAction
        {
            Id = "details",
            Icon = PaperTopBarIcon.SvgPath("M3,3 L13,3 13,13 3,13 Z"),
            ToolTip = "详情"
        }
    ],
    PaperHostTopBarActions.NewTodoPaper |
    PaperHostTopBarActions.NewNotePaper);

context.Workspace.SetGlobalTopBarActions(
    [
        new PaperTopBarAction
        {
            Id = "summarize",
            Icon = PaperTopBarIcon.Character("✦"),
            ToolTip = "处理当前纸片"
        }
    ]);
```

字符图标最多 8 个 UTF-16 字符。SVG 只接受 **Path Data**，不是完整 `<svg>` 文档；单个 path data 最长 4096 字符。图标最终缩放到宿主的小图标槽，颜色继承当前顶栏前景色。

自己的插件纸片可以请求隐藏的宿主动作目前只有：

```text
NewTodoPaper
NewNotePaper
```

关闭、置顶、标题拖动、窗口生命周期等宿主生命线不能被插件删除。

`SetPaperTopBarActions(...)` / `SetGlobalTopBarActions(...)` 都是 replace 语义；传空数组即可撤掉该 scope 的动作。`SetTopBarActionHandler(null)` 会撤掉这个 session 的全部 Top Bar contribution。即使插件忘记手工清理，Host API/session Dispose 也会自动回收。

### Web

Web 使用现有 `workspace.request`，不复制新的 transport：

```js
await papertodo.workspace.request('topbar.paper.set', {
  actions: [
    {
      id: 'refresh',
      icon: { kind: 'character', value: '↻' },
      toolTip: '刷新'
    }
  ],
  hiddenHostActions: ['newTodoPaper', 'newNotePaper']
});

await papertodo.workspace.request('topbar.global.set', {
  actions: [
    {
      id: 'summarize',
      icon: { kind: 'svgPath', value: 'M2,8 L8,2 14,8 8,14 Z' },
      toolTip: '处理当前纸片'
    }
  ]
});
```

点击后宿主发送普通 Web bridge 事件：

```js
papertodo.onEvent(message => {
  if (message.type !== 'topBarActionInvoked') return;
  const { actionId, scope, targetPaperId, targetPaperType, targetBodyProviderId } = message.action;
});
```

Web 与 Native 使用同一宿主注册表、同一 action ID/尺寸/数量限制和同一 session 生命周期。

## 7. 胶囊 presentation

### 7.1 宿主绘制的标准胶囊

插件可以提交 `PaperCapsulePresentation`。外壳、关闭区、Hover、拖动、贴边、跨屏、DPI 和输入始终由 PaperTodo 管理。

标准组件最多三个，按声明顺序排列：

- `text`
- `glyph`
- `statusDot`
- `progressRing`
- `progressBar`

组件支持 `fill`、固定 `width`、`tone` 和自定义 `color`。

宽度：

- Native：`PreferredWidth = PaperCapsulePresentation.AutomaticWidth`
- Web：`preferredWidth: 0`

表示让宿主按内容测量自然宽度。正数表示插件希望的完整内容段宽度（DIP），宿主仍会限制到合法范围。

动态文字和普通状态优先使用自动宽度；只有确实需要稳定槽位的仪表盘/画布再指定正数。

Web 示例：

```js
papertodo.paper.setCapsulePresentation({
  preferredWidth: 0,
  plainText: 'CPU 42% · 68℃',
  toolTip: 'CPU 42% / GPU 68℃',
  components: [
    { kind: 'progressRing', value: 0.42, tone: 'accent' },
    { kind: 'text', text: 'CPU', fill: true },
    { kind: 'text', text: '68℃', tone: 'warning' }
  ]
});
```

`plainText` 应始终提供一个有意义的纯文字表示，用于只接受文本的临时 surface 和安全回退。

### 7.2 Native 自定义 WPF 胶囊

Native session 可实现：

```csharp
IPaperCapsuleViewProvider
```

`CreateCapsuleView(PaperCapsuleViewContext)` 分别为 `Regular`、`Docked` 创建 WPF 内容 View。

规则：

- 两种 surface 必须返回不同的 WPF 对象；
- View 必须是 fresh、未挂载的 pure-WPF tree；
- 不接受 `Window`、`HwndHost`、`WindowsFormsHost`、WebView2 或已经有 parent 的控件；
- 自定义胶囊内容本身不拥有鼠标输入，按钮/输入框等交互不要放在这里；
- 宿主仍拥有外壳、关闭区、点击、右键、拖动、Hover、贴边和 DPI；
- 创建失败或返回 `null` 时使用标准胶囊；
- 自动宽度先由标准 presentation 解析，再把最终槽尺寸传给自定义 View；
- 同一 session/geometry 下宿主缓存 View，实时状态应原地刷新，不要靠不断重建 View 更新。

最小示例：

```csharp
private sealed class Session : IPaperBodySession, IPaperCapsuleViewProvider
{
    public FrameworkElement View { get; } = new Grid();

    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context) =>
        new CapsuleView(context);

    public void Dispose() { }
}
```

Web 插件不提供 WPF 自定义胶囊，只使用宿主绘制的标准 presentation。

## 8. Edge Mini

Edge Mini 是快速浏览 surface。**插件贡献内容，PaperTodo 始终拥有 Edge 窗口、队列 placement、卡片外框、尺寸归一化和输入路由。** 插件不要创建自己的 Edge HWND，也不要复制宿主的 queue/geometry 算法。

当前存在四类路径：

1. Native dedicated mini：`IPaperMiniViewProvider`；
2. Web dedicated mini：manifest `miniEntry`；
3. Native body migration：`IPaperBodyViewMigrationProvider`；
4. 没有 dedicated/migration 能力时，宿主根据自定义胶囊、标准胶囊或 `plainText` 构造只读 preview。

这些路径不是一条“逐层加载替换”的通用流水线。尤其是声明 `miniEntry` 的 Web 插件，其 Web mini 本身就是当前 preview 内容；准备期间不先画旧胶囊作为视觉替身。

### 8.1 Mini 尺寸

`PaperMiniViewSize` / `miniSize` 描述**包含宿主外框和关闭区的完整卡片尺寸**，单位 DIP。

协议不设置固定的 120×90 下限或 480×420 上限。插件声明的 `width` / `height` 必须是**正且有限的数值**；宿主只按当前显示器可用工作区对最终尺寸做约束。

默认：

```text
320 × 220 DIP
```

内置 Todo / Markdown 可以继续使用自己的 renderer envelope 和视觉默认尺寸；这些值不是插件协议限制。Native 插件若在一个已经可见的 bounded host generation 中临时请求更大的 Preferred Size，宿主可能先约束到当前安全 capacity，并把更大的请求留到下一次安全 host generation。插件应把 Preferred Size 当作稳定布局偏好，而不是持续动画参数。

### 8.2 Native dedicated mini

实现：

```csharp
IPaperMiniViewProvider
```

示例：

```csharp
private sealed class Session : IPaperBodySession, IPaperMiniViewProvider
{
    private readonly SharedState _state;

    public FrameworkElement View { get; }

    public PaperMiniViewSize PreferredMiniViewSize => new(300, 190);

    public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
    {
        return new ClockMiniView(_state, context.Theme);
    }

    public void OnMiniViewVisibilityChanged(bool visible)
    {
        if (!visible)
        {
            // 可以停止输入/动画刷新，但保留最后绘制的 WPF tree。
        }
    }

    public void Dispose() { }
}
```

规则：

- dedicated mini 与正文可以共享同一业务 state/model，但必须是不同的 WPF 控件实例；
- `CreateMiniView` 必须返回 fresh、unparented、pure-WPF tree；
- 不接受 `Window`、`HwndHost`、`WindowsFormsHost`、WebView2 或已挂载元素；
- 返回 `null` 或创建失败不会让正文 session 失败，宿主改用 capsule preview；
- `OnMiniViewVisibilityChanged(false)` 从收起开始时发送；可以暂停刷新和输入，但不要立即清空/Collapse 整棵树，因为宿主仍需要最后一帧完成离场动画；
- Edge host 不取得键盘焦点，mini 不应依赖文本输入。

标准 WPF `Button`、选择器、滚动条、`Thumb`、Hyperlink 等可以取得 pointer input。其他自定义元素可声明：

```csharp
PaperMiniViewInteraction.SetConsumesPointer(element, true);
```

未消费 pointer 的卡片区域仍由 PaperTodo 用于打开完整 paper、拖动等宿主交互。

### 8.3 Web dedicated mini

Web manifest：

```json
{
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 }
}
```

`miniEntry` 使用独立 WebView2。它应是本地、轻量的状态界面，不要再次加载一套完整远程应用。

当前 publication 流程：

1. Edge Mini 先建立透明内容占位，Edge 外框仍由宿主管理；
2. WebView2 的 cold initialization 在当前开启动画之后延后启动，避免把重初始化工作塞进同一个输入/动画窗口；
3. 当前 mini document 导航成功后收到 `initialize`；
4. 页面完成首轮真实布局后调用 `papertodo.mini.ready()`；
5. 宿主向**当前 document generation** 发起 ready challenge，避免旧 same-origin 文档的排队消息授权新页面；
6. challenge 成功后，再跨过真实 `CompositionTarget.Rendering` publication boundary；
7. 只有当前 preview 仍可见且 generation 仍匹配时，Web surface 才显示并接收输入。

因此不要假设 `mini.ready()` 一调用就立即可见，也不要依赖一个旧胶囊在加载期间替 Web 页面占位。初始化、导航、进程或 ready 校验失败时，Web surface 保持不发布。

迷你页：

```js
window.addEventListener('papertodo', event => {
  const message = event.detail;
  if (message?.type === 'initialize') {
    render(message.state, message.settings);
    requestAnimationFrame(() => papertodo.mini.ready());
  }
});
```

Web Mini 的 pointer 默认属于 PaperTodo。网页只有在某个局部区域确实需要自己处理点击、按下或拖动时，才在该元素上声明：

```html
<button type="button" data-papertodo-interactive>暂停</button>
```

宿主会把所有 `data-papertodo-interactive` 元素的当前 DOM 矩形镜像到 WPF 输入层，并随布局、属性、滚动和尺寸变化刷新；只有这些矩形内的 pointer 交给 Web surface。未标记区域继续用于打开完整 paper、拖动 Edge Mini 等宿主交互。不要把整个页面根节点无差别标记为 interactive。

正文与 mini 获得同一个宿主管理 state/settings。任一 surface `saveState` 后，宿主把新的 `stateChanged` 发给另一侧。接收方**不要在 `stateChanged` 中原样再调用 `saveState`**，否则两棵页面会形成回声；只有用户操作或真实业务状态变化才写回。

`miniVisibilityChanged` 用于暂停隐藏后的计时器、动画和输入。`visible: false` 从收起开始发送；和 Native mini 一样，应保留最后绘制内容完成宿主的离场动画。

Web mini 不取得键盘焦点，不要设计依赖键盘输入的表单。

### 8.4 Native body migration

纯 WPF 正文如果没有第二套 dedicated mini，可以选择实现：

```csharp
IPaperBodyViewMigrationProvider
```

这表示允许宿主在合适时机使用**唯一真实正文 View**作为 Edge Mini，并由宿主负责 reparent 与 snapshot handoff。

适用条件：

- 正文必须是 pure-WPF tree；
- WebView2、`HwndHost`、原生子窗口等 foreign/native surface 不可迁移；
- dedicated `IPaperMiniViewProvider` 优先级高于 migration；
- snapshot 只用于 handoff/后续快速预览，不建立持续截图循环，也不是第二份业务 UI。

如果插件正文包含 WebView2 等 native child，请像 `PaperTodo.Plugin.CloudGenshin` 一样把它留在完整正文，并为 Edge Mini 提供独立 pure-WPF 状态面板。

## 9. Web 插件

### 9.1 本地 origin 与 bridge

Web `entry` 所在目录是本地静态根，建议固定为 `web/`，避免把 `.runtime/` 暴露进页面资源映射。

插件自己的本地顶层页面运行在：

```text
https://<plugin-id>.papertodo.local/
```

只有该插件的本地 **top-level document** 获得 `window.papertodo`。远程页面、iframe 或其他 origin 不获得宿主 bridge。

PaperTodo 把 Web 插件视为可信内容；WebView2 保持正常导航、frame、popup 和 permission 行为。普通 HTTP/HTTPS 下载优先交给系统默认浏览器；`blob:`、`data:` 等 session-local download 保留 WebView2 默认行为。

### 9.2 Body bridge

正文页可用：

```js
papertodo.surface;                    // 'body'
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.setInputClaims(['escapeKey', 'contextMenu']);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.workspace.request(method, params);
papertodo.onHostEvent(types, listener, options);
papertodo.onEvent(listener);
```

2.0 的顶栏仍通过 `papertodo.workspace.request(...)` 进入同一 host request transport；详见 6.5。

宿主会发送：

```text
initialize
stateChanged
settingsChanged
activated
deactivated
visibilityChanged
presentationChanged
themeChanged
typographyChanged
dpiChanged
commitRequested
cancelInteractions
hostResponse
hostEvent
hostSubscriptionError
topBarActionInvoked
```

`initialize` 包含当前 `surface`、paper/provider ID、API/state 版本、state、settings、permissions、theme、runtime visibility 和 presentation visibility。

### 9.3 Mini bridge

`miniEntry` 页可用：

```js
papertodo.surface;                    // 'mini'
papertodo.mini.ready();
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.workspace.request(method, params);
papertodo.onEvent(listener);
```

Mini 没有正文的 `setInputClaims`；键盘焦点始终不属于 Edge Mini。Pointer 也默认归宿主，只有带 `data-papertodo-interactive` 的局部 DOM 区域会把 pointer 交给 Web 页面。

### 9.4 状态写入

每次真实状态 mutation 后尽快 `saveState`。`registerStateProvider` 只是让宿主在 `commitRequested`、页面隐藏/卸载等边界尽量 flush 当前状态，不应该被当作唯一 durability 机制。

## 10. Native 插件

Native 插件是 fully trusted / unsandboxed .NET/WPF 代码，与 PaperTodo 当前用户权限一致。

关键规则：

- `IPaperBodyPlugin` 作为 factory，不保存某一张 paper 的 session state；
- 每个 paper body session 使用新的 plugin object / `IPaperBodySession`；
- manifest-only discovery 不会在启动时加载所有 Native DLL；
- 首次实际选择对应 provider 时才加载入口 assembly、反射类型并创建对象；
- entry assembly 必须只有一个有效 `IPaperBodyPlugin` 实现；
- 已载入 DLL 的文件变化/删除需要重启才能稳定反映；
- 私有依赖和 native library 放在插件自包含目录；
- 不重复携带宿主共享程序集；
- 所有 timer、task、subscription、Top Bar contribution、外部资源都随 session 生命周期结束；插件自己创建的资源仍必须在 `Dispose()` 中清理。

需要宿主统一视觉的 select 可使用 `PaperBodyContext.Body.Controls`，不要复制 PaperTodo 内部 popup/theme/DPI 细节。

## 11. 示例项目怎么选

| 示例 | 重点 |
| --- | --- |
| `PaperTodo.Plugin.SampleClock` | Native 主示例：settings、background updates、标准 capsule、自定义 WPF capsule、dedicated WPF mini；当前仍作为 1.8 兼容样本 |
| `PaperTodo.Plugin.OfficialClockWeb` | Web 2.0 主示例：body/mini 双页面、`miniEntry`、state/settings 同步、startup paper、background updates |
| `PaperTodo.Plugin.FocusTimer` | Native 有状态交互：正文与 dedicated mini 共享同一计时模型，mini 内直接开始/暂停/继续 |
| `PaperTodo.Plugin.ReviewArchive` | Workspace 数据读取/observe、插件状态与长期数据的组合使用 |
| `PaperTodo.Plugin.CloudGenshin` | 正文含 WebView2/native child 时的边界：完整远程应用留在正文，Edge Mini 使用独立 pure-WPF 状态面板 |

开发新插件时优先从与目标最接近的示例复制最小结构，不要把五个示例的能力一次全部合并进去。需要 Top Bar 时以本手册 6.5 和 `PaperTodo.Plugin.Abstractions` 当前合同为准。

## 12. 常见错误

### Manifest

- 新插件仍以 `apiVersion: "1.8"` 为目标，导致无法使用 2.0 Top Bar；
- 插件目录名和 `id` 不一致；
- `id` 使用非法字符或保留 ID `data`；
- `miniSize` 没有对应 `miniEntry`；
- Web `miniEntry` 跑出 `entry` 的静态目录；
- Native manifest 与 DLL 的 id/version/API/state/runtime requirements 不一致；
- `quick: true` 超过三个；
- `startupPaper.enabledSetting` 没有指向 boolean setting；
- 声明未知 `requires` / `permissions`。

### Top Bar

- 给 PaperTodo 传 `FrameworkElement` / Button / 完整 SVG，而不是 action descriptor；
- action ID 重复、超过 64 字符或含非法字符；
- 一个插件贡献超过 4 个 Paper action 或 2 个 Global action；
- SVG 传完整 `<svg>` 文档而不是 Path Data；
- 想隐藏关闭/置顶/拖动等宿主生命线；
- 为 Top Bar 另写一套正文 mutation，而不是复用 Workspace；
- 把 Global contribution 当成 manifest 静态 UI，忽略它必须由活跃 session 持有的生命周期。

### WPF surface

- 把同一个 WPF 元素同时返回给正文、Regular capsule、Docked capsule 或 mini；
- 返回已经有 parent 的控件；
- 把 `Window`、`HwndHost`、WindowsFormsHost、WebView2 当成可迁移/custom mini tree；
- 在只读自定义 capsule 中放需要点击的按钮；
- 让 Edge Mini 依赖键盘焦点。

### Web Mini

- 认为 `miniSize` 仍有固定 120×90～480×420 协议范围；
- 需要网页自己处理点击的局部控件没有声明 `data-papertodo-interactive`；
- 为了接管所有输入把整个页面根节点无差别标记为 interactive；
- 假设 `mini.ready()` 调用后 Web surface 会同步立即显示。

### 状态

- 只在 `Commit()` 或页面卸载时保存，而不是每次 mutation 后提交；
- 收到 `stateChanged` 后原样 `saveState`，造成 body/mini 回声；
- 把普通 per-paper state 同时写进 `plugins/data` 和 `.runtime/`；
- state 迁移失败时直接写空对象覆盖旧数据；
- 单张 paper state 超过 1 MiB。

### Workspace / 生命周期

- 没有 permission 就调用 Workspace API；
- 用 observe 权限误当 read 权限；
- 尝试删除承载当前 active session 的 paper；
- 不需要后台运行却声明 `backgroundUpdates`；
- session Dispose 后仍让 timer/task/subscription 继续工作；
- 让插件自己接管 Edge HWND、queue placement、外框或 geometry。

## 13. 提交示例插件前

- 新插件 `plugin.json` 使用当前 `apiVersion: "2.0"`；确需兼容验证的旧样本才保留 1.8；
- Native manifest 与入口 DLL metadata/runtime requirements 一致；
- Native 使用统一 build/install 脚本跑通；
- 最终 `plugins/<id>/` 不包含 PDB/XML/重复 shared assemblies；
- `.runtime/` 不被构建脚本误删；
- Web body 与 mini 的 state/settings 同步没有回声；
- Web mini 只有真正需要网页处理 pointer 的局部元素声明 `data-papertodo-interactive`；
- Top Bar 只提交 host-rendered descriptor，Global contribution 随 session 自动撤销；
- capsule 提供合理 `plainText`；
- custom WPF surface 均为 fresh / unparented / pure-WPF；
- Edge Mini 不依赖键盘输入；
- 只声明实际需要的 permissions / `backgroundUpdates`；
- 切换 provider、重载、折叠/展开、关闭 paper 后没有遗留 timer、task、subscription、Top Bar contribution 或输入占用。
