# WPF 原生专注计时器 1.3

这是一个不依赖 WebView2、完全由 WPF 控件构成的番茄钟插件。1.3 版适配 PaperTodo 插件协议 1.3，并把计时器与真实待办连接起来。

## 新增能力

- 在计时器中选择任意待办纸上的未完成项目；
- 待办正文、所属纸片、完成和删除状态会通过 `todos.observe` / `papers.observe` 实时同步；
- 可选择在完整完成一轮专注时，通过 `todos.update` 自动完成关联待办；
- 可在完成后自动选择下一条未完成待办；
- 胶囊标题可显示当前待办和倒计时；
- 旧版计时进度、累计轮数和每日统计会从状态版本 1/2 无损迁移到版本 3。

跳过当前阶段不会完成关联待办。关闭“专注结束后完成待办”时，关联只作为上下文显示，不会修改待办数据。

## 原有能力

- 专注 / 休息阶段切换、暂停、继续、跳过和重置；
- 默认时长、加减步长、每日目标、自动轮转和结束提示音；
- 今日与累计完成轮数；
- UTC 截止时间恢复；
- 折叠成胶囊后继续计时，隐藏后暂停运行时刷新；
- 主题、字体、正文缩放和完整会话生命周期。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.FocusTimer\PaperTodo.Plugin.FocusTimer.csproj
```

安装目录：

```text
plugins\sample.focus-timer.native\
```

原生 DLL 已在当前进程加载后不能热替换；重新构建后需要重启 PaperTodo。
