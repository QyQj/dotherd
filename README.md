# DotHerd

DotHerd 在 Windows 后台托管当前用户的应用。可以在管理窗口或托盘里查看状态、打开应用和读日志，也可以让多个 agent 通过同一个 MCP 网关使用 Serena Pool 等本机服务。

## 安装并运行第一个应用

以下命令适用于 Windows x64、.NET SDK 11、.NET 10 和 ASP.NET Core 10 运行时。

### 从 NuGet.org 安装 DotHerd

```powershell
dotnet tool install -g DotHerd --version '[0.1.0-rc.1]' --source https://api.nuget.org/v3/index.json
dotherd
```

### 从本地源安装 Serena Pool 候选包

SerenaPool `0.3.1` 是独立仓库的本地候选。以下示例同时从本地源安装管理器和应用，将 DotHerd `0.1.0-rc.1` 顶层包、`DotHerd.win-x64` 实现包和 SerenaPool 包放在同一目录。已从 NuGet.org 安装 DotHerd 时，本地源只需包含 SerenaPool 候选包，设置 `$feed` 后直接执行 `dotherd install SerenaPool --source $feed`。

先用 `serena --version` 检查 worker 是否可用，也可通过宿主的 `Pool__Executable` 指定它的绝对路径。已有独立 Pool 使用默认端口时，先让对应客户端释放租约、断开连接，并等待独立 core 退出。

```powershell
# 从仓库根目录执行，也可将路径改为存放这三个 .nupkg 的目录
$feed = (Resolve-Path .\artifacts\windows-x64).Path
dotnet tool install -g DotHerd --version '[0.1.0-rc.1]' --source $feed
dotherd install SerenaPool --source $feed
dotherd
```

首次运行会启动宿主并登记当前用户的登录自启。输入 `dotherd` 打开管理窗口，关窗后应用继续运行。用 `dotherd status SerenaPool` 查看 core 的 PID 和就绪状态，用 `dotherd mcp list SerenaPool` 查看已发布的工具。在 **Applications** 选中 Serena Pool，点击 **Open dashboard** 可打开管理页。两个 agent 各自导入下文的网关配置后，可分别领取自己的工作目录。

也可以用 `dnx` 一次完成管理器安装和应用安装。第一个 `--source` 指定 DotHerd 的来源，第二个指定应用的来源。

```powershell
dnx DotHerd@0.1.0-rc.1 --source $feed --yes -- install SerenaPool --source $feed
```

### 从源码准备本地包源

在 DotHerd 仓库根目录打开 PowerShell，以下命令假定已将配套的 `serena-pool` 源码检出到同级目录。最后一行额外打包了支持 `ready`、`action` 和 `shutdown` 的作者示例，可以用 `dotherd install DotHerd.Sample.ControlApp --source $feed` 安装。

```powershell
$build = [IO.Directory]::CreateDirectory("$PWD/../temp/DotHerd-build").FullName
$feed = [IO.Directory]::CreateDirectory("$PWD/artifacts/windows-x64").FullName
dotnet pack .\src\DotHerd -c Release --artifacts-path "$build/manager" -o $feed -p:EnableRuntimePackDownload=false
dotnet pack ..\serena-pool\src\SerenaPool -c Release --artifacts-path "$build/pool" -o $feed
dotnet pack .\samples\ControlApp -c Release --artifacts-path "$build/sample" -o $feed
```

## 选择要托管的程序

- 安装**带 DotHerd 清单的 NuGet 工具**时，运行 `dotherd install 包ID` 会安装最高稳定版并启动应用。用 `包ID@版本` 固定版本；多个源提供同一候选版本时，用 `--source` 指定源名称、目录或地址。再次安装已登记的包会显示现状，手动停止的应用保持停止。
- 对于**普通 .NET Tool**，先用 `dotherd install 包ID` 列出包提供的命令。要把其中一个命令作为常驻进程启动，可使用 `dotherd install 包ID --as-process --command 命令名 -- 应用参数`。
- 安装**标准 MCP 工具**时，DotHerd 会预览本地传输、参数和输入，确认后登记。用 `--input-env API_KEY=MY_API_KEY` 将必填输入绑定到宿主可读取的环境变量，登记中只保存变量名。纯 stdio 服务通过发现后显示 `connectable`，没有常驻 PID。若发现失败，登记仍保留，可用 `dotherd mcp list` 查看协议诊断。

已有本地程序可以用 `add` 登记，再用 `start` 启动。以下示例假设应用位于 `C:\Apps\MyApi`，双横线后依次是可执行程序和传给它的参数。

```powershell
dotherd add local-api --cwd 'C:\Apps\MyApi' -- dotnet 'C:\Apps\MyApi\MyApi.dll'
dotherd start local-api
```

普通程序启动后显示 `running`。如果程序支持 DotHerd 就绪通知，可以在 `add` 的双横线前指定 `--readiness notify`。此时 `start` 最多等待 30 秒，收到通知后显示 `ready`。

## 日常管理

| 操作 | 命令 |
|---|---|
| 列出全部应用，或查看某个应用的状态和 PID | `dotherd status`、`dotherd status 名称` |
| 启动、停止或重启 | `dotherd start 名称`、`dotherd stop 名称`、`dotherd restart 名称` |
| 更新 NuGet 应用 | `dotherd update 包ID`、`dotherd update 包ID@版本` |
| 卸载 NuGet 应用或解除本地程序的托管 | `dotherd uninstall 名称` |
| 查看应用的标准输出、错误输出和退出原因 | `dotherd logs 名称` |
| 显示、隐藏独立托盘或恢复作者默认值 | `dotherd tray 名称 on`、`dotherd tray 名称 off`、`dotherd tray 名称 default` |
| 查看、关闭或开启当前用户的登录自启 | `dotherd startup status`、`dotherd startup off`、`dotherd startup on` |
| 查看受影响应用并确认结束本次守护 | `dotherd host exit` |

需要保持运行的应用自行退出时，DotHerd 会分别等待 1、2、4、8 秒重试。连续第五次失败后，状态变为 `failed`。用 `logs` 查看原因，修正后运行 `start`。手动 `stop` 会取消重试，下次打开宿主时仍保持停止。

默认数据目录为 `%LOCALAPPDATA%\DotHerd`。`packages\` 存放已安装的版本，`data\` 是包应用的工作目录，`logs\` 保存滚动日志。可将 `DOTHERD_HOME` 设为其他绝对路径来使用独立配置。

### 连接 MCP 网关

```powershell
dotherd mcp list
dotherd mcp config
dotherd mcp config --transport http --client my-agent
```

`mcp config` 导出可复制的客户端 JSON。stdio 客户端启动 `dotherd mcp connect`，每次连接获得独立的上游绑定。HTTP 客户端使用同一配置下固定的 `127.0.0.1` 地址和 `/mcp`，按凭据区分绑定。请给需要独立绑定的客户端分配不同名称。同一份 HTTP 凭据共享绑定，导出的授权头应作为秘密保存。

网关发布 `命名空间__工具名`，客户端重新列举即可看到路由变化。用 `dotherd mcp disable 包ID` 撤销路由，用 `dotherd mcp enable 包ID` 发布或重试发现。这个开关独立于常驻进程启停。主动停止应用也会撤销路由。启用的纯 stdio 服务按连接启动，stdio 客户端断开时回收；HTTP 绑定空闲约两分钟后回收。

例如，两个 agent 分别调用 `serena_pool__acquire` 并传入各自 worktree 的绝对路径，随后用返回的 `instance_id` 调用 `serena_pool__call`，完成后调用 `serena_pool__release`。重启 Pool 后需重新领取租约。遇到连接问题时，先查看 `dotherd mcp list SerenaPool` 的协议诊断，再用 `dotherd logs SerenaPool` 检查进程日志。

### 更新、恢复与卸载

`update` 默认从应用原有来源选择较新的稳定版。用 `@版本` 指定精确版本，用 `--source` 指定来源。显式登记的普通 .NET Tool 会沿用命令和参数，MCP 输入继续使用已登记的环境变量绑定。

DotHerd 先并排安装候选版本，再按应用的就绪方式判定是否切换。普通程序须能启动，`notify` 应用须报告就绪，启用的纯 stdio 服务须通过 MCP 工具发现。候选失败时，命令会报告失败原因和旧版恢复结果。已停止的应用更新后保持停止。纯 stdio 更新后，新绑定使用新版本，旧绑定继续使用原版本，最后一个旧绑定结束后清理旧包。回退覆盖宿主管理的二进制及活动版本，应用自行迁移的数据按应用作者的说明恢复。

`uninstall` 停止进程树、移除托管记录并清理 DotHerd 安装的包文件。用户数据、日志和本地程序原文件会保留。若宿主在切换或卸载期间结束，下次启动会按持久记录处理未完成的操作。如果文件占用等原因阻止清理，可在 `status` 查看 `recoveryError`，解除占用后重新执行对应命令。

`host exit` 显示受影响的应用，确认后结束本次守护。脚本可传 `--yes`。下次打开宿主或在启用登录自启时重新登录，会按原有运行意图恢复。注销和关机也保留该意图。

卸载 DotHerd 本身前，先解除当前配置的登录自启并停止宿主。

```powershell
dotherd disable
dotnet tool uninstall -g DotHerd
```

## 发布流程

推送与 [项目版本](src/DotHerd/DotHerd.csproj)一致的 `v<项目版本>` 标签，例如 `v0.1.0-rc.1`，会触发 GitHub Actions 的 [发布工作流](.github/workflows/publish.yml)。工作流将 `DotHerd*` 包发布到 NuGet.org 和 GitHub Packages，各平台实现包先于顶层工具包发布。NuGet.org 使用名为 `dotherd` 的 Trusted Publishing 策略，对应 GitHub 环境 `nuget`。
