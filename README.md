# DotHerd

DotHerd 的目标是让 Windows 用户从 NuGet 安装并托管常驻应用。当前完成了 M0 的 P01 工具包验证。产品设计与后续任务见 [MVP 提案](DotHerd-MVP.md) 和 [项目规划](DotHerd-项目规划.md)。

## P01 本地 NuGet 源实测

2026-11-27 在 Windows x64 上使用 .NET SDK `11.0.100`，工具目标框架为 `net10.0`。三个本地源均提供 `DotHerd.P01.Probe@1.0.0`。各包在构建时写入不同的来源标记，运行程序时只传清单里的两个参数 `--probe` 和 `two words`。

| 选定源 | 顶层包中的 SDK 工具设置 | 安装目录中的命令 | 实现入口 | 标准输出 | 退出码 |
|---|---|---|---|---|---:|
| A | `tools/net10.0/any/DotnetToolSettings.xml` | `dotherd-p01.exe` | `Probe.dll` | `source-A` | 0 |
| B | `tools/net10.0/any/DotnetToolSettings.xml` | `dotherd-p01.exe` | `Probe.dll` | `source-B` | 0 |
| RID | `tools/any/any/DotnetToolSettings.xml` | `dotherd-p01.cmd` | `Probe.exe` | `source-RID` | 0 |

三个顶层包的 `.nuspec` 均只声明 `DotnetTool`，归档根目录均有大小写完全匹配的 `.dotherd/app.json`。其中的 `command` 与 SDK 设置中的命令名均为 `dotherd-p01`。

RID 顶层包通过 `RuntimeIdentifierPackages` 把 `win-x64` 映射到 `DotHerd.P01.Probe.win-x64`。子包类型为 `DotnetToolRidPackage`，入口位于 `tools/net10.0/win-x64/Probe.exe`。样例只在顶层包打入 DotHerd 清单。M2 可据此按包类型识别顶层包，并按安装实际生成的文件解析命令。固定拼接 `.exe` 会漏掉本次 RID 安装的入口。

单独指定 RID 子包 ID 时，SDK 11.0.100 也成功安装了 `dotherd-p01.cmd`。用相同参数运行，得到 `source-RID` 和退出码 0。M2 要在安装前检查包类型，遇到 `DotnetToolRidPackage` 时提示选择顶层包。

### 复验

在 Windows x64 上安装 PowerShell 7.4+、上述 SDK 和 .NET 10 开发环境后，从仓库根目录运行：

```powershell
pwsh -NoProfile -File .\probes\P01\verify.ps1
```

[验收脚本](probes/P01/verify.ps1) 先打出全部包，再依次从 A、B、RID 读取顶层清单，最后额外按子包 ID 安装一次。每次安装都使用新目录，共用本轮独立的 NuGet 缓存。脚本用 `--source` 指定唯一来源，用 `--version '[1.0.0]'` 固定版本，用 `--tool-path` 指定目录。它以该目录内的绝对路径启动命令，逐一核对参数、来源输出和退出码。构建、包源、缓存和安装目录均在仓库同级的 `temp` 下，本轮结束后自动清理。[dotnet tool install 文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install)
