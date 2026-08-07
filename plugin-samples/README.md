# PaperTodo 插件源码

仓库中的插件目录有明确边界：

- `plugin-samples/` 保存插件源码、清单源文件和构建说明；
- `plugins/` 只保存已经构建、可由 PaperTodo 直接加载的最终产物；
- `plugins/data/` 保存 PaperTodo 代管的插件全局设置和纸片状态；
- 插件自己的 `.runtime/` 可保存运行缓存或独立于纸片的长期数据，构建安装脚本会保留它；
- PaperTodo 的本地发布和 GitHub Release 都不携带插件，插件需要单独分发。

最终原生插件目录只保留 `plugin.json`、入口 DLL、必要的 `.deps.json`、插件私有依赖和原生库。不要放入 PDB、XML 文档、重复 DLL 或 PaperTodo 宿主已经提供的共享程序集。

## 示例定位

- `PaperTodo.Plugin.SampleClock`：完整 WPF 时钟，演示多种宿主设置、主题、缩放、后台更新和运行时标题；
- `PaperTodo.Plugin.OfficialClockWeb`：与原生时钟功能接近的 Web 对照实现；
- `PaperTodo.Plugin.FocusTimer`：完整 WPF 番茄钟，演示状态恢复、自动轮转、声音和每日统计；
- `PaperTodo.Plugin.ReviewArchive`：Issue #37 的实现，演示协议 1.3 数据读取、事件监听、插件私有长期存储和 CSV 导出；
- `PaperTodo.Plugin.CloudGenshin`：WebView2 远程应用嵌入、导航、输入占用和进程恢复示例。

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
   │  └─ CSS、脚本与图片
   ├─ WeatherPlugin.dll / 依赖 DLL / 原生库
   └─ .runtime\                     # 插件私有缓存或长期数据
```

当前 PaperTodo 插件协议为 **1.7**，宿主只加载 **1.7** 插件。1.5 负责整理插件实例的能力范围；1.6 提供宿主绘制的胶囊模板；1.7 允许原生插件在同一固定高度内容区内自由绘制。目录名必须与 `id` 一致，`data` 是宿主保留 ID。

原生插件使用 `PaperBodyContext.Paper`、`PaperBodyContext.Body` 与 `PaperBodyContext.Workspace`。正式标题、展开态运行时标题和折叠态胶囊文字互相独立；Web 插件对应使用 `papertodo.paper.*`、`papertodo.body.*` 与 `papertodo.workspace.request()`。

## 协议 1.6 胶囊模板

插件可以为自己的纸片提交一个固定高度的胶囊模板，由 PaperTodo 负责真正绘制外壳、关闭区、Hover、拖动、贴边和 DPI。模板最多包含三个任意顺序的组件，组件可选 `text`、`glyph`、`statusDot`、`progressRing`、`progressBar`；`fill` 可占用剩余横向空间，`width` 可指定固定段宽。插件只请求内容区宽度，宿主把它限制在合理范围内，胶囊高度仍保持 PaperTodo 默认值。

原生插件使用 `context.Paper.SetCapsulePresentation(...)`；Web 插件使用 `papertodo.paper.setCapsulePresentation(...)`。`plainText` 是启动、拖动过渡和其他纯文字环境的回退文本；自定义模板停止后恢复普通胶囊。

```js
papertodo.paper.setCapsulePresentation({
  preferredWidth: 150,
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

原生插件会话可额外实现 `IPaperCapsuleViewProvider`，在协议 1.6 模板之外为普通胶囊和贴边胶囊分别创建一个自由 WPF 内容视图。宿主仍固定胶囊高度，并继续拥有外壳、关闭区、点击、右键、拖动、Hover、贴边、跨屏与 DPI；自定义视图统一禁用命中测试，第一版只负责显示，不拥有按钮、输入框或独立点击区域。

自由视图创建失败时只回退到 1.6 模板，不会让正文插件失败。插件仍应提供 `PaperCapsulePresentation` 与 `plainText` 作为回退；跨队列拖动的临时浮动胶囊继续使用纯文字回退，避免把自由视图接入高风险的拖拽交接链。Web 插件继续使用 1.6 模板，不在胶囊内创建额外 WebView2。

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
  "apiVersion": "1.7",
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

原生插件使用 `PaperBodyContext.Host`；Web 插件使用 `papertodo.request()` 与 `papertodo.onHostEvent()`。

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
papertodo.setInputClaims([]);
papertodo.markDirty();
papertodo.openExternal("https://example.com");
papertodo.onEvent(message => console.log(message));
```

协议 1.5 起，正式标题、展开态运行时标题与折叠态胶囊表现是三个独立概念；协议 1.6 使用 `paper.setHeaderText` 与 `paper.setCapsulePresentation` 分别更新后两者。

宿主发送 `initialize`、`stateChanged`、`settingsChanged`、`activated`、`deactivated`、`visibilityChanged`、`presentationChanged`、`themeChanged`、`typographyChanged`、`dpiChanged`、`commitRequested` 和 `cancelInteractions`。`initialize` 提供 `apiVersion`、`stateVersion`、`targetStateVersion`、`settings`、`visible` 和 `presentationVisible`。

`setInputClaims` 是动态输入占用声明，不是权限。声明 `escapeKey` 时，PaperTodo 不再用 Esc 折叠纸片；声明 `contextMenu` 时，只阻止插件正文区域继承的 PaperTodo 右键菜单。插件应在进入输入模式前声明并在退出时释放；切换插件、重载、失败或销毁会话时，宿主会自动清空声明。

## 原生插件

原生插件目录的 `entry` 指向实现 `PaperTodo.Plugin.IPaperBodyPlugin` 的入口 DLL，依赖、`.deps.json`、资源和本地库全部放在同一插件目录。自协议 1.2 起，DLL 必须显式实现 `ApiVersion` 和 `RuntimeRequirements`，并与 `plugin.json` 完全一致；不一致时拒绝加载。PaperTodo 为每个纸片创建新的插件工厂对象，`IPaperBodyPlugin` 不应保存纸片实例状态。

原生会话可通过 `OnPresentationChanged` 判断完整正文是否显示，通过 `OnVisibilityChanged` 判断运行时是否应保持活动，通过 `OnSettingsChanged` 接收全局设置变化，并通过 `PaperBodyContext.SetInputClaims` 动态占用 Esc 或正文右键菜单；正文会话必须在 `Dispose` 中停止计时器、取消任务并解除事件。

未被任何纸片使用的原生插件在启动时只读取 `plugin.json`，不会加载 DLL 或调用构造函数；入口程序集会在首次创建对应正文时加载并校验。
