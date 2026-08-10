# PaperTodo 插件源码

仓库中的插件目录有明确边界：

- `plugin-samples/` 保存插件源码、清单源文件和构建说明；
- `plugins/` 只保存已经构建、可由 PaperTodo 直接加载的最终产物；
- `plugins/data/` 保存 PaperTodo 代管的插件全局设置和纸片状态；
- 插件自己的 `.runtime/` 可保存运行缓存或独立于纸片的长期数据，构建安装脚本会保留它；
- PaperTodo 的本地发布和 GitHub Release 都不携带插件，插件需要单独分发。

最终原生插件目录只保留 `plugin.json`、入口 DLL、必要的 `.deps.json`、插件私有依赖和原生库。不要放入 PDB、XML 文档、重复 DLL 或 PaperTodo 宿主已经提供的共享程序集。

## 示例定位

- `PaperTodo.Plugin.SampleClock`：原生主示例；保留 1.6 模板、实现 1.7 自绘胶囊，并通过 1.8 `IPaperMiniViewProvider` 提供专属 WPF 迷你界面；
- `PaperTodo.Plugin.OfficialClockWeb`：Web 对照实现；正文使用 `entry`，1.8 专属迷你网页使用 `miniEntry`，加载前由放大的 1.6 胶囊无空帧接棒；
- `PaperTodo.Plugin.FocusTimer`：完整 WPF 番茄钟；1.8 专属迷你界面共享同一计时状态，并允许直接开始、继续或暂停；
- `PaperTodo.Plugin.ReviewArchive`：Issue #37 的实现；1.6 胶囊同步用户选择的复盘指标，并按“显示复盘指标”设置附带进行中数量；同时演示数据监听与长期存储；
- `PaperTodo.Plugin.CloudGenshin`：WebView2 远程应用嵌入；完整网页只留在正文，1.8 迷你界面使用独立的纯 WPF 状态面板，不创建第二个 WebView2。

所有示例都继续提供协议 1.6 的 `SetCapsulePresentation` 和 `plainText`。1.8 是额外能力而不是替代回退：专属迷你界面失败时，宿主仍能逐级退回自绘胶囊、结构化胶囊和纯文字。胶囊的点击、右键、拖动、Hover、关闭和贴边交互始终由 PaperTodo 宿主管理。

## 原生插件构建与安装

所有原生 DLL 插件共用 `plugin-samples/Build-And-Install-NativePlugin.ps1`。脚本从项目同目录读取 `plugin.json`，执行 Release 发布，清理宿主共享程序集，并保留目标插件原有的 `.runtime` 数据。

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj

.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.FocusTimer\PaperTodo.Plugin.FocusTimer.csproj

.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.ReviewArchive\PaperTodo.Plugin.ReviewArchive.csproj

.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.CloudGenshin\PaperTodo.Plugin.CloudGenshin.csproj
```

纯 Web 插件不需要编译，直接将清单和 `web/` 静态文件复制到对应的 `plugins/<插件 ID>/` 目录。

## 部署目录

> **信任边界：PaperTodo 不为插件提供沙箱。** 插件拥有与当前用户相同的权限，请只安装可信来源的插件。

每个插件使用一个与插件 ID 同名的自包含目录，不再区分 `web` 和 `native` 总目录：

```text
plugins\
├─ data\
│  └─ com.example.weather.json      # PaperTodo 代管的设置与纸片状态
└─ com.example.weather\
   ├─ plugin.json
   ├─ web\                          # Web 插件的静态根；原生插件不需要
   │  ├─ index.html
   │  ├─ mini.html                  # 可选的 1.8 Web 迷你入口
   │  └─ CSS、脚本与图片
   ├─ WeatherPlugin.dll / 依赖 DLL / 原生库
   └─ .runtime\                     # 插件私有缓存或长期数据
```

当前 PaperTodo 插件协议为 **1.8**，宿主只加载 **1.8** 插件。1.5 负责整理插件实例的能力范围；1.6 提供宿主绘制的胶囊模板；1.7 允许原生插件在固定高度内容区内自由绘制；1.8 为边缘快速浏览增加专属迷你界面、原生纯 WPF 正文迁移和统一的放大回退。目录名必须与 `id` 一致，`data` 是宿主保留 ID。

原生插件使用 `PaperBodyContext.Paper`、`PaperBodyContext.Body` 与 `PaperBodyContext.Workspace`。正式标题、展开态运行时标题和折叠态胶囊文字互相独立；Web 插件对应使用 `papertodo.paper.*`、`papertodo.body.*` 与 `papertodo.workspace.request()`。

## 协议 1.6 胶囊模板

插件可以为自己的纸片提交一个固定高度的胶囊模板，由 PaperTodo 负责真正绘制外壳、关闭区、Hover、拖动、贴边和 DPI。模板最多包含三个任意顺序的组件，组件可选 `text`、`glyph`、`statusDot`、`progressRing`、`progressBar`；`fill` 可占用剩余横向空间，`width` 可指定固定段宽。

宽度有两种模式：原生插件使用 `PreferredWidth = PaperCapsulePresentation.AutomaticWidth`、Web 插件使用 `preferredWidth: 0` 时，宿主会按当前文字、图标、状态点、进度组件、组件间距和模板内边距测量自然宽度；正数则表示插件明确要求的完整内容段宽度（DIP）。两种模式都会限制在宿主允许的范围内。动态状态一般应优先使用自动宽度，只有仪表盘、固定画布等确实需要稳定槽位时才指定正数。

原生插件使用 `context.Paper.SetCapsulePresentation(...)`；Web 插件使用 `papertodo.paper.setCapsulePresentation(...)`。`plainText` 是启动、拖动过渡和其他纯文字环境的回退文本；自定义模板停止后恢复普通胶囊。

```js
papertodo.paper.setCapsulePresentation({
  preferredWidth: 0,
  plainText: "CPU 42% · 68℃",
  toolTip: "CPU 42% / GPU 68℃",
  components: [
    { kind: "progressRing", value: 0.42, tone: "accent" },
    { kind: "text", text: "CPU", fill: true },
    { kind: "text", text: "68℃", tone: "warning" }
  ]
});
```

## 协议 1.7 原生胶囊自由绘制

原生插件会话可额外实现 `IPaperCapsuleViewProvider`，在协议 1.6 模板之外为普通胶囊和贴边胶囊分别创建一个自由 WPF 内容视图。`PaperCapsuleViewContext.Width/Height` 就是该视图最终获得的完整布局槽尺寸（DIP），宿主不会再扣除 1.6 模板的内部留白；插件若需要内边距，应在自己的 View 内实现。宿主仍拥有外壳、关闭区、点击、右键、拖动、Hover、贴边、跨屏与 DPI，自定义视图统一禁用命中测试，不应放置需要独立点击的按钮或输入框。

宿主在一个正文会话、同一内容槽几何下，对 `Regular`、`Docked` 各最多尝试创建一次，并缓存“成功 View”或 `null` 回退。普通胶囊与贴边胶囊必须返回两个不同的 WPF 对象，不能把同一个已挂载元素重复返回。使用自动宽度时，宿主先测量对应 1.6 标准组件，再把结果作为自由视图的 `Width`；只要最终解析出的宽度改变，宿主就会丢弃旧缓存并按新尺寸重建两种 View。普通状态刷新、主题切换或 DPI 变化不会无条件要求重建。需要实时显示的插件应保留当前 View 引用，在自己的状态更新、`OnThemeChanged` / `OnTypographyChanged` 中原地刷新；只有自定义绘制确实依赖 DPI 细节时才需要额外处理 `OnDpiChanged`。

最小实现可以参考 `PaperTodo.Plugin.SampleClock`：

```csharp
private sealed class Session : IPaperBodySession, IPaperCapsuleViewProvider
{
    private CapsuleView? _regular;
    private CapsuleView? _docked;

    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        var view = new CapsuleView(context);
        if (context.Surface == PaperCapsuleSurfaceKind.Docked)
            _docked = view;
        else
            _regular = view;
        return view;
    }
}
```

自由视图创建失败或主动返回 `null` 时只回退到 1.6 模板，不会让正文插件失败。因此插件仍应持续提供 `PaperCapsulePresentation` 与 `plainText`；跨队列拖动的临时浮动胶囊也继续使用纯文字回退。1.7 自绘胶囊只接受纯 WPF 视觉树，`Window`、`HwndHost`、`WindowsFormsHost`、WebView2 和已经挂载的控件会被拒绝并回退到 1.6。纯 Web 插件继续使用 1.6 胶囊模板。

## 协议 1.8 边缘快速浏览

宿主按固定优先级选择边缘迷你内容：

1. 插件提供 1.8 专属迷你界面时使用专属界面；
2. 原生纯 WPF 插件明确实现正文迁移能力时，首次可迁移唯一真实正文视图；
3. 存在 1.7 纯 WPF 自绘胶囊时，显示只读的实时放大镜像；
4. 存在 1.6 结构化胶囊时，由宿主用更大的字号、图标、状态点和进度组件重新绘制；
5. 前面全部不可用时，回退到 `plainText` 或纸片标题。

前两项是插件可选能力，后三项由宿主自动完成。插件不能依赖专属迷你界面始终成功，必须继续提交 1.6 结构化胶囊和 `plainText`。放大的 1.6/1.7 回退只读，点击卡片背景打开完整纸片；专属迷你界面可包含按钮、复选框、滚动、选择器和链接等轻量交互，但不应复制完整编辑器。

插件声明的是包含外框和关闭区在内的完整卡片尺寸，单位为 DIP。协议绝对范围是 `120 × 90` 至 `480 × 420`；未声明的专属迷你界面默认 `320 × 220`。宿主会结合当前显示器工作区再次限制尺寸，并在一次浏览会话中冻结大小，内容更新不会让整列胶囊反复跳动。当前空待办和空笔记默认都是 `130 × 120`。

### 原生专属迷你界面

原生会话实现 `IPaperMiniViewProvider`，同一个会话可以让正文与迷你界面共享业务状态，但必须创建两个不同的 WPF 控件实例。WPF 控件对象只能有一个父级，不能直接复制或同时挂到两处。宿主缓存一个成功的迷你 View；创建失败或返回 `null` 时自动回退，不会终止正文会话。

```csharp
private sealed class Session : IPaperBodySession, IPaperMiniViewProvider
{
    public PaperMiniViewSize PreferredMiniViewSize => new(300, 190);

    public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
    {
        // context.Width / Height 是插件真正获得的内部内容槽。
        return new ClockMiniView(sharedState, context.Theme);
    }

    public void OnMiniViewVisibilityChanged(bool visible)
    {
        // false 从收起动画开始时发送：可暂停计时器和输入，但不要清空已绘制的树。
        // 业务状态仍遵循正文会话的可见性规则。
    }
}
```

专属原生迷你界面只接受纯 WPF，不接受 `Window`、`HwndHost`、`WindowsFormsHost`、WebView2 或已挂载控件。`OnMiniViewVisibilityChanged(false)` 会在收起开始时停止交互，但宿主仍要用最后一帧完成动画，因此插件只能暂停刷新，不能立即清空或折叠整棵迷你树。标准 WPF 按钮、输入框、选择器、滚动条、拖块和链接会自动取得输入；其他自定义命中元素可调用 `PaperMiniViewInteraction.SetConsumesPointer(element, true)`。

### 原生正文迁移

没有第二套实时界面、但正文完全由纯 WPF 构成的插件，可以显式实现 `IPaperBodyViewMigrationProvider`。专属迷你界面的优先级更高；WebView2、原生子窗口或其他外部合成表面不能迁移。

首次浏览且正文从未展示时，宿主可以把唯一真实 View 暂时放到迷你卡片。点击打开时，宿主先截图替换迷你内容，再把真实 View 原子移回主纸片；完整纸片鼠标移出时再保存一张截图。以后浏览会先立即显示旧截图，再异步截取一次新图替换。截图只保存在内存中，不持续采样；失败时保留旧图，永远不以空白层接棒。

```csharp
private sealed class Session : IPaperBodySession, IPaperBodyViewMigrationProvider
{
    public PaperMiniViewSize PreferredMigratedMiniViewSize => new(360, 260);
}
```

### Web 专属迷你界面

Web 插件可在清单中声明同一 `entry` 静态目录下的 `miniEntry`。迷你网页拥有独立 WebView2，但应是本地、轻量的状态界面，不应再次加载完整远程应用：

```json
{
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 }
}
```

宿主先同步显示放大的 1.6 胶囊，迷你 WebView2 初始化期间不会出现空卡。迷你页收到 `initialize` 后完成首轮布局，再显式调用 `papertodo.mini.ready()`；宿主会等到下一渲染帧才替换回退层。初始化、导航、脚本失败或页面始终未声明就绪时都继续保留放大胶囊；若就绪消息恰好落在收起期间，宿主只记录结果，下一次移入后才切换界面。

迷你页与正文获得同一份 `state`、`settings`、主题和权限；`window.papertodo.surface` 与 `initialize.surface` 分别为 `mini` 或 `body`，共用脚本可以据此选择布局。任何一侧 `saveState` 后都会通知另一侧 `stateChanged`。接收方应只把 `stateChanged` 应用到自己的控件，不要在事件处理器里原样回写 `saveState`；只有用户操作或真实业务变化才写回，否则两棵页面会形成回声。迷你页还可使用 `paper`、`body.openExternal` 与 `workspace.request()`；`miniVisibilityChanged` 用于暂停隐藏后的计时器或动画。`visible: false` 从收起动画开始时发送，页面应停止输入和刷新，但要保留最后一次 DOM 绘制，不能立即清空根节点。宿主状态始终是真相来源，不应让正文页和迷你页各自保存互相覆盖的私有副本。

需要在纸片折叠为可见胶囊后继续运行的插件，必须声明 `"requires": ["backgroundUpdates"]`。未声明时，宿主会在完整正文不显示时通知插件暂停运行；未知的必需能力会拒绝加载。

## 宿主绘制的插件设置

协议 1.2 起支持 `boolean`、`string`、`number` 和 `select` 四种全局设置。插件只声明结构，PaperTodo 负责绘制和保存。约束字段均可省略；`quick: true` 的设置最多三个，会直接显示在插件卡片右侧，其余设置放在“更多设置”中。

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "description": "天气信息面板",
  "version": "1.0.0",
  "apiVersion": "1.8",
  "stateVersion": 1,
  "entry": "web/index.html",
  "capabilities": ["textZoom"],
  "requires": ["backgroundUpdates"],
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
      "name": "城市"
    },
    {
      "id": "refreshMinutes",
      "type": "number",
      "name": "刷新间隔",
      "suffix": "分钟"
    },
    {
      "id": "unit",
      "type": "select",
      "name": "温度单位",
      "options": [
        { "value": "c", "name": "摄氏度" },
        { "value": "f", "name": "华氏度" }
      ]
    }
  ]
}
```

每个插件的宿主管理数据保存在 `plugins/data/<插件 ID>.json`：`settings` 是插件所有纸片共享的设置，`papers` 以纸片 ID 保存独立状态。单张纸片状态上限为 1 MiB（只在保存时按 UTF-8 JSON 字节数检查）。删除纸片时会同步删除各插件中的对应状态。正常数据文件无法读取时，原文件保持不变，插件从空状态运行，之后只写入唯一的 `<插件 ID>.json.recovered`，该文件存在时会优先使用。

`.runtime/` 不受宿主状态协议管理，适合 WebView2 Profile、可重建缓存或必须独立于纸片生命周期的插件私有数据。原生插件应自行负责格式版本、原子写入、损坏恢复和容量控制，不应把普通单纸片界面状态重复放入 `.runtime`。

原生插件通过 `PaperBodyContext.SettingsJson` 获取初始设置，并通过 `IPaperBodySession.OnSettingsChanged` 接收更新。

## 协议 1.3 数据能力

插件通过 `permissions` 声明 Paper、Todo 与 Note 的读取、动态监听和受控写入。监听只在纸片插件会话存活时注册；未使用插件时不启动事件扫描，隐藏时暂停事件投递，切换正文或销毁会话后订阅自动释放。折叠胶囊是否继续接收仍由 `backgroundUpdates` 决定。

支持：`papers.read/observe/create/delete`、`todos.read/observe/append/update/delete`、`notes.read/observe/append/replace`。写入结果只返回 ID 或内容长度，不会绕过独立的读取权限。

原生插件使用 canonical `PaperBodyContext.Workspace`；Web 插件使用 `papertodo.workspace.request()` 与 `papertodo.onHostEvent()`。`Host` / 顶层 `request()` 仍是便利别名，但示例代码统一使用 canonical scope，避免把纸片、正文和工作区能力混在一起。

## Web 插件

Web 插件的 `entry` 所在目录会成为本地静态根；建议固定使用 `web/`，使同一插件目录下的 `.runtime/` 不会被网页映射。插件自己的本地顶层页面运行在 `https://<id>.papertodo.local/`，只有该本地顶层页面会获得 `window.papertodo` 桥接。外部顶层导航、远程 iframe、弹窗和浏览器权限请求使用 WebView2 默认行为。

常规 HTTP/HTTPS 下载会交给系统默认浏览器；`blob:`、`data:` 或会话内生成的下载保留 WebView2 默认下载行为。

可通过 `window.papertodo` 调用：

```js
papertodo.saveState({ city: "Shanghai" });
papertodo.registerStateProvider(() => currentState);
papertodo.paper.setTitle("上海天气");
papertodo.paper.setHeaderText("上海天气 · 已更新");
papertodo.paper.setCapsulePresentation({
  plainText: "26°C 晴",
  components: [{ kind: "text", text: "26°C 晴", fill: true }]
});
papertodo.body.setInputClaims(["escapeKey", "contextMenu"]);
papertodo.body.setInputClaims([]);
papertodo.body.markDirty();
papertodo.body.openExternal("https://example.com");
papertodo.onEvent(message => console.log(message));
```

协议 1.5 起，正式标题、展开态运行时标题与折叠态胶囊表现是三个独立概念；协议 1.6 使用 `paper.setHeaderText` 与 `paper.setCapsulePresentation` 分别更新后两者。

宿主发送 `initialize`、`stateChanged`、`settingsChanged`、`activated`、`deactivated`、`visibilityChanged`、`presentationChanged`、`themeChanged`、`typographyChanged`、`dpiChanged`、`commitRequested` 和 `cancelInteractions`。`initialize` 提供 `surface`、`apiVersion`、`stateVersion`、`targetStateVersion`、`settings`、`visible` 和 `presentationVisible`。

`setInputClaims` 是动态输入占用声明，不是权限。声明 `escapeKey` 时，PaperTodo 不再用 Esc 折叠纸片；声明 `contextMenu` 时，只阻止插件正文区域继承的 PaperTodo 右键菜单。插件应在进入输入模式前声明并在退出时释放；切换插件、重载、失败或销毁会话时，宿主会自动清空声明。

## 原生插件

原生插件目录的 `entry` 指向实现 `PaperTodo.Plugin.IPaperBodyPlugin` 的入口 DLL，依赖、`.deps.json`、资源和本地库全部放在同一插件目录。自协议 1.2 起，DLL 必须显式实现 `ApiVersion` 和 `RuntimeRequirements`，并与 `plugin.json` 完全一致；不一致时拒绝加载。PaperTodo 为每个纸片创建新的插件工厂对象，`IPaperBodyPlugin` 不应保存纸片实例状态。

原生会话可通过 `OnPresentationChanged` 判断完整正文是否显示，通过 `OnVisibilityChanged` 判断运行时是否应保持活动，通过 `OnSettingsChanged` 接收全局设置变化，并通过 `PaperBodyContext.Body.SetInputClaims` 动态占用 Esc 或正文右键菜单；正文会话必须在 `Dispose` 中停止计时器、取消任务并解除事件。

未被任何纸片使用的原生插件在启动时只读取 `plugin.json`，不会加载 DLL 或调用构造函数；入口程序集会在首次创建对应正文时加载并校验。
