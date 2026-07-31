# PaperTodo 正文插件目录

每个插件使用一个与插件 ID 同名的自包含目录，不再区分 `web` 和 `native` 总目录：

```text
plugins\com.example.weather\
├─ plugin.json
├─ web\                      # Web 插件的静态根；原生插件不需要
│  ├─ index.html
│  └─ CSS、脚本与图片
├─ WeatherPlugin.dll / 依赖 DLL / 原生库   # 原生插件内容；Web 插件不需要
└─ .runtime\                 # PaperTodo 自动创建的该插件运行数据
```

`plugin.json` 必须包含 `kind`（`web` 或 `native`）、`id`、`entry`、`apiVersion` 和 `stateVersion`。目录名必须与 `id` 一致。

Web 示例：

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "description": "天气信息面板",
  "version": "1.0.0",
  "apiVersion": 1,
  "stateVersion": 1,
  "entry": "web/index.html",
  "capabilities": ["textZoom"]
}
```

Web 插件的 `entry` 所在目录会成为唯一静态根；建议固定使用 `web/`，使同一插件目录下的 `.runtime/` 不会被网页映射。Web 插件仅能在自己的 `https://<id>.papertodo.local/` 虚拟源中运行。顶层外链会交给默认浏览器，远程 iframe、弹窗、下载和权限请求会被拦截。可通过 `window.papertodo` 调用：

```js
papertodo.saveState({ city: "Shanghai" }); // 每次状态变化后立即调用
papertodo.registerStateProvider(() => currentState); // 关闭前的辅助快照，不能替代即时保存
papertodo.setTitle("上海天气");
papertodo.setCapsuleText("26°C 晴");
papertodo.markDirty();
papertodo.openExternal("https://example.com");
papertodo.onEvent(message => console.log(message));
```

宿主发送 `initialize`、`stateChanged`、`activated`、`deactivated`、`visibilityChanged`、`themeChanged`、`typographyChanged`、`dpiChanged`、`commitRequested` 和 `cancelInteractions`。`initialize` 同时提供 `stateVersion` 与 `targetStateVersion`，Web 插件可迁移旧状态后立即 `saveState`。

原生插件目录的 `entry` 指向实现 `PaperTodo.Plugin.IPaperBodyPlugin` 的入口 DLL，依赖、`.deps.json`、资源和本地库全部放在同一插件目录。PaperTodo 为每个纸片创建新的插件工厂对象；`IPaperBodyPlugin` 不应保存纸片实例状态，正文会话必须在 `Dispose` 中停止计时器、取消任务并解除事件。
