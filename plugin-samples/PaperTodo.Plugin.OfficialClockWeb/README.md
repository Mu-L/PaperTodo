# 官方 Web 时钟插件源码

这个目录保存 `official.clock.web` 的源码。它与原生时钟提供接近的功能，用于对照同一产品如何分别使用 Web 与 WPF 实现：

- 12 / 24 小时制、秒数、日期、星期、周数和日进度；
- 本地、UTC 和多个常用城市时区；
- 多种日期格式、标题模式和显示缩放；
- `initialize`、`settingsChanged`、`themeChanged`、`visibilityChanged` 生命周期；
- 1.8 `miniEntry` 提供独立轻量时钟，收到初始化后完成首帧再调用 `papertodo.mini.ready()`；
- 迷你网页就绪前由放大的 1.6 胶囊立即显示，不出现空白卡片；
- `paper.setHeaderText` 与 `paper.setCapsulePresentation` 分别同步纸片顶栏和 1.6 胶囊模板，胶囊按当前标题和日进度组件自动适配宽度；
- 正文可用较高频率对齐秒边界，但对宿主胶囊写入做去重，避免无意义地重复重建同一模板。

Web 插件不需要编译，部署产物是 `plugin.json` 和 `web/` 的原样副本。1.7 自绘胶囊仍只属于原生插件；本示例的紧凑胶囊使用 1.6 宿主模板，边缘快速浏览使用 1.8 Web 迷你入口。仓库中的可加载副本位于：

```text
plugins\official.clock.web\
```

修改源码后，将本目录的 `plugin.json` 和 `web/` 同步到上述目录即可重载。PaperTodo 的本地发布和 GitHub Release 不携带该插件。
