# DotHerd

DotHerd 是面向 Windows 的常驻应用管理器。M1 已支持从 CLI 登记本地程序、启动和停止、查看日志，并在程序意外退出后自动重启。托管 NuGet 应用从 M2 开始。[项目规划](DotHerd-项目规划.md) · [MVP 提案](DotHerd-MVP.md)

## M1 本地程序守护

在 Windows x64、PowerShell 7.4+、.NET SDK 11.0.100 和 .NET 10 开发环境中，从仓库根目录运行下面的命令即可复验。脚本从源码打包，把 `DotHerd@0.1.0-preview.1` 安装到独立目录，再通过安装出的 `dotherd.exe` 操作真实进程。包源、缓存、测试配置和程序均位于同级 `temp` 的本轮子目录，结束后自动清理。[端到端验收脚本](tests/M1/verify.ps1)

```powershell
pwsh -NoProfile -File .\tests\M1\verify.ps1
```

### 手动运行作者示例

以下命令使用单独的演示配置。重新打开终端时，把 `DOTHERD_HOME` 和 `$cli` 设回相同路径，就可以继续控制原实例。

```powershell
$demo = [IO.Directory]::CreateDirectory("$PWD/../temp/DotHerd-M1-demo").FullName
$feed = [IO.Directory]::CreateDirectory("$demo/feed").FullName
$env:DOTHERD_HOME = "$demo/profile"
dotnet pack .\src\DotHerd -c Release --artifacts-path "$demo/build" --source $feed -o $feed -p:EnableRuntimePackDownload=false
dotnet tool install DotHerd --version '[0.1.0-preview.1]' --tool-path "$demo/tools" --source $feed
dotnet build .\samples\ControlApp -c Release --artifacts-path "$demo/build" --source $feed
$cli = "$demo/tools/dotherd.exe"
& $cli add demo --cwd $demo --readiness notify --action open -- "$demo/build/bin/ControlApp/release/ControlApp.exe" 'two words'
& $cli start demo
& $cli action demo open
& $cli logs demo
& $cli stop demo
& $cli host exit
```

`add` 登记程序后保持停止。普通前台程序使用默认的 `--readiness process`，启动后显示 `running`。选择 `notify` 时，`start` 等待应用发送 `ready`，超过 30 秒会报告失败并进入重试。用 `dotherd status [名称]` 查看状态、PID、启动实例及失败次数，`dotherd restart <名称>` 有序停旧启新。

| 场景 | 宿主行为 |
|---|---|
| 应用自行退出 | 记录退出码，含退出码 0。连续失败按 1、2、4、8 秒重试，第五次停在 `failed` 等待手动 `start`。连续运行或就绪 60 秒后清零。 |
| 用户停止 | 先保存停止意图并取消退避，再请求退出。10 秒宽限到期后回收该应用的整棵进程树。 |
| 宿主退出并重新打开 | 每个应用使用独立的 Windows Job Object。重新打开宿主时恢复应当运行的应用，手动停止和连续五次失败的应用保持原状态。 |
| 查看日志 | `logs` 按时间戳标记 stdout、stderr 和宿主事件。应用文本输出建议用 UTF-8。每个应用保留当前日志和上一份，每份最多约 1 MiB。 |

默认配置位于 `%LOCALAPPDATA%\DotHerd`，`apps\` 保存登记和运行意图，`logs\` 保存日志。控制协议 v1 使用每次启动独立的命名管道，每行一条 UTF-8 JSON。应用从环境读取 `DOTHERD_CONTROL_PIPE`、`DOTHERD_INSTANCE` 和 `DOTHERD_CONTROL_TOKEN`，先发送包含版本、实例和令牌的 `hello`。随后可上报 `ready`、`tray.update`，并接收 `action` 和 `shutdown`。[完整 .NET 作者示例](samples/ControlApp/Program.cs) · [消息格式](DotHerd-MVP.md#5-管理界面与应用托盘接口)

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
