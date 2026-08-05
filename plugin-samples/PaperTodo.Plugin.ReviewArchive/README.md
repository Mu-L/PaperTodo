# 待办复盘记录池 1.1

这是 PaperTodo 协议 1.3 待办事件监听与只读数据能力的完整原生插件。1.1 版在原有生命周期记录上增加提醒变化和更有用的复盘指标。

## 记录内容

- 新建待办的创建时刻；
- 待办完成、取消完成、删除和恢复的时刻；
- 正文与所属纸片标题变化；
- 提醒设置、取消和调整事件，以及当前提醒时间；
- 来源是否已经删除；
- 用户、MCP、插件等事件来源。

## 新版展示

- 今日完成、近 7 天完成、连续完成日和进行中数量；
- “重新打开”和“有提醒”独立筛选；
- 未来 24 小时提醒与已到期提醒高亮；
- 胶囊可显示累计完成、今日完成、连续完成日或进行中数量；
- CSV 补齐完成次数、最后重新打开、提醒时间和提醒变更次数，并修复旧版表头与数据列错位。

记录池保存在插件目录的 `.runtime/review-archive.json`，不属于任何一张纸片的 `StateJson`。升级时会将存储版本 1/2 自动迁移到版本 3，原有记录与事件不会丢失。

插件只在至少有一张使用它的笔记仍可见时监听变化。折叠成胶囊仍会继续记录；彻底隐藏、删除最后一张插件纸片或退出 PaperTodo 后，期间发生的变化无法被纯后台补记。再次打开后可用“导入当前”补录现状，但补录时间会标记为“首次观察值”。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.ReviewArchive\PaperTodo.Plugin.ReviewArchive.csproj
```

安装目录：

```text
plugins\sample.review-archive.native\
```
