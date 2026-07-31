# 原生 DLL 插件示例

```powershell
dotnet build .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj -c Release
```

在 `plugins` 中新建与插件 ID 同名的目录：

```text
plugins\sample.clock.native\
├─ plugin.json
├─ PaperTodo.Plugin.SampleClock.dll
├─ PaperTodo.Plugin.SampleClock.deps.json
└─ 该插件需要的其他 DLL / 资源 / 原生库
```

把本目录的 `plugin.json` 和 Release 输出文件复制进去，然后在「设置 → 插件」点击“重新扫描”。
新插件可立即识别；本次运行已经加载过的原生插件发生修改后，会明确提示重启 PaperTodo，不伪装成热更新。
`PaperTodo.Plugin.Abstractions.dll` 由主程序提供，目录中存在副本也会优先复用主程序版本。
