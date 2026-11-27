# DotHerd

DotHerd 的目标是让 Windows 用户从 NuGet 安装并托管常驻应用。M0 的工具包、Windows 宿主和 Serena Pool 直连验证均已完成。产品设计与后续任务见 [MVP 提案](DotHerd-MVP.md) 和 [项目规划](DotHerd-项目规划.md)。

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

## P02 Windows 宿主实测

2026-11-27 在 Windows x64、.NET SDK `11.0.100` 上，从本地 NuGet 源安装了 `DotHerd.P02@1.0.0`。同一个包内有控制台工具入口 `dotherd-p02.exe` 和桌面宿主 `P02Host.exe`（`WinExe`）。CLI 从自身程序集所在目录定位桌面宿主，用 Windows Shell 启动它，通过当前用户专用命名管道读取结果。宿主用命名互斥量选出唯一实例。探针用每轮独立的 GUID 隔离并发复验。

| 验收场景 | 一次运行的实际结果 |
|---|---|
| 两个首次命令同时到达 | CLI `7736`、`36352` 都确认宿主管道尚未建立，再同时放行。两者均返回宿主 PID `26988`，退出码均为 `0`。 |
| 关闭终端 | 两个 CLI 都已连接测试控制台。控制台进程 `22488` 退出后，只读 `status` 仍收到 `26988` 的响应，系统进程列表中有一个该轮宿主。 |
| 强制终止宿主 | 先观察到 `26988 → 46284 → 46328` 的宿主、应用、子进程关系，只终止 `26988` 后，另两个已持有句柄的进程均在 5 秒内退出。 |
| 直接启动登录入口 | 从含空格的持久安装路径执行带引号的 186 字符命令行，宿主 `32216` 在当前交互会话响应，`GetConsoleProcessList` 显示未连接控制台。按单活跃用户约定，本项覆盖直接启动；未修改 HKCU Run 或执行真实注销、重新登录。 |

M1 可以沿用同包双入口。CLI 显式设置 `UseShellExecute = true` 后，即使调用方重定向了输出，也能读到 EOF 并关闭终端。宿主为应用创建带 `KILL_ON_JOB_CLOSE` 的 Job Object，通过 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 在创建应用进程时就将其纳入 Job。之后应用自行创建的子进程也随之回收。[Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)

从仓库根目录运行 `pwsh -NoProfile -File .\probes\P02\verify.ps1` 可复验以上四项。脚本使用本地源、独立安装目录和无可见窗口的 Windows 控制台，完成后回收本轮进程和临时目录。[验收脚本](probes/P02/verify.ps1)

## P03 Serena Pool 直连

两种前端修订 `2026-07-28`、`2025-11-25` 都能直连 `serena-pool mcp`、发现四个工具、领取目录、从真实 REPL 得到 `42` 并释放租约。另已复现首次领取失败。worker 启动较慢时，core 会在同一连接上尝试旧握手，收到 `-32022`。该问题归入 M6。[版本、取消行为、三段链路与复验命令](probes/P03/README.md)
