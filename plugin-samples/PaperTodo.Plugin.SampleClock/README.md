# 原生 DLL 插件示例

所有原生 DLL 示例共用仓库根目录下的构建安装脚本。先完全退出 PaperTodo，然后运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

脚本会读取本目录的 `plugin.json`，构建 Release DLL，清理宿主已提供的共享程序集，并安装到：

```text
plugins\sample.clock.native\
```

新插件可立即识别；本次运行已经加载过的原生插件发生修改后，会明确提示重启 PaperTodo，不伪装成热更新。
`PaperTodo.Plugin.Abstractions.dll` 由主程序提供，不会被复制进最终插件目录。
