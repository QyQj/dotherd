# DotHerd

DotHerd 在 Windows 后台运行和管理常驻应用。你可以从 NuGet 安装应用，也可以登记已有的本地程序。关闭终端后，应用继续运行；重新打开 PowerShell 即可查看状态和日志。

## 安装并运行第一个应用

以下示例适用于 Windows x64、.NET SDK 11 和 .NET 10 运行时。将 `$feed` 设为存放 DotHerd 和示例应用的 NuGet 包目录。如果手头只有仓库源码，可以先[准备本地包源](#从源码准备本地包源)。

```powershell
$feed = 'C:\Packages\DotHerd'
dotnet tool install -g DotHerd --version '[0.1.0-preview.2]' --source $feed
dotherd install DotHerd.Sample.ControlApp --source $feed
dotherd status DotHerd.Sample.ControlApp
dotherd action DotHerd.Sample.ControlApp open
dotherd logs DotHerd.Sample.ControlApp
dotherd stop DotHerd.Sample.ControlApp
```

示例应用就绪后，`status` 的 `state` 为 `ready`。执行 `open` 后，日志里会出现 `action=open`。首次执行 `dotherd` 或 `dotherd install` 会启动后台宿主，并为当前用户启用登录自启。

也可以用 `dnx` 一次完成管理器安装和应用安装。第一个 `--source` 指定 DotHerd 的来源，第二个指定应用的来源。

```powershell
dnx DotHerd@0.1.0-preview.2 --source $feed --yes -- install DotHerd.Sample.ControlApp --source $feed
```

### 从源码准备本地包源

在仓库根目录打开 PowerShell，运行以下命令生成两个工具包，然后在同一终端使用上面的安装命令。`$feed` 已设为生成的包目录，可跳过示例中的赋值行。

```powershell
$build = [IO.Directory]::CreateDirectory("$PWD/../temp/DotHerd-build").FullName
$feed = [IO.Directory]::CreateDirectory("$build/feed").FullName
dotnet pack .\src\DotHerd -c Release --artifacts-path "$build/artifacts" -o $feed -p:EnableRuntimePackDownload=false
dotnet pack .\samples\ControlApp -c Release --artifacts-path "$build/artifacts" --source $feed -o $feed
```

## 选择要托管的程序

- 安装**带 DotHerd 清单的 NuGet 工具**时，运行 `dotherd install 包ID` 会安装最高稳定版并启动应用。用 `包ID@版本` 固定版本；多个源提供同一候选版本时，用 `--source` 指定源名称、目录或地址。再次安装已登记的包会显示现状，手动停止的应用保持停止。
- 对于**普通 .NET Tool**，先用 `dotherd install 包ID` 列出包提供的命令。要把其中一个命令作为常驻进程启动，可使用 `dotherd install 包ID --as-process --command 命令名 -- 应用参数`。
- 安装**标准 MCP 工具**时，DotHerd 会预览本地传输、参数和输入，确认后登记。用 `--input-env API_KEY=MY_API_KEY` 将必填输入绑定到宿主可读取的环境变量，登记中只保存变量名。纯 stdio 服务显示 `registered`，没有常驻 PID。

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
| 查看应用的标准输出、错误输出和退出原因 | `dotherd logs 名称` |
| 查看、关闭或开启当前用户的登录自启 | `dotherd startup status`、`dotherd startup off`、`dotherd startup on` |
| 结束本次后台宿主及受管进程 | `dotherd host exit` |

需要保持运行的应用自行退出时，DotHerd 会分别等待 1、2、4、8 秒重试。连续第五次失败后，状态变为 `failed`。用 `logs` 查看原因，修正后运行 `start`。手动 `stop` 会取消重试，下次打开宿主时仍保持停止。

默认数据目录为 `%LOCALAPPDATA%\DotHerd`。`packages\` 存放已安装的版本，`data\` 是包应用的工作目录，`logs\` 保存滚动日志。可将 `DOTHERD_HOME` 设为其他绝对路径来使用独立配置。
