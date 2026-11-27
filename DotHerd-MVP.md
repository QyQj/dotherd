# DotHerd · MVP 设计提案

**产品名 DotHerd，NuGet 包名 `DotHerd`，命令行短名 `dotherd`。** DotHerd 管理当前用户安装的常驻应用。作者将应用发布为 NuGet 上的 .NET Tool，用户用一条命令完成下载安装、首次启动和守护。支持手动登记普通工具和本地可执行程序。管理界面单列 MCP 服务；多个 agent 通过 DotHerd 的同一个本机网关发现和调用已启用服务的工具。首版交付 Windows；包约定、命令、管理界面和应用通信协议按跨平台设计。

## 1. 用户体验和默认行为

```shell
dotnet tool install -g DotHerd
dotherd install SerenaPool
dotherd
```

第二行首次执行时完成管理器初始化、注册当前用户登录自启、启动常驻管理器，再安装和启动应用。第三行打开管理窗口。若全局安装后先执行 `dotherd`，也会初始化并打开窗口。标准 `dotnet tool install -g` 只完成工具安装；产品文档应把第一次执行写清楚。管理器按用户安装，无需管理员权限。

一次性入口为 `dnx DotHerd -- install SerenaPool`。这里“一次性”指调用 CLI 的方式。首次调用需要持久守护时，由引导入口安装同版本的全局 DotHerd，再把原命令转交给它。如果已全局安装，复用现有版本；CLI 协议不兼容时报告版本及升级命令。注册登录项时使用持久安装的入口，避免指向 dnx 缓存。只看帮助或版本不触发安装。两种入口共用一份应用列表。

默认一个用户一个管理器、一个管理器托盘。关窗口后继续托管。Windows MVP 的“开机启动”定义为**用户交互登录后启动**，用户可在设置中关闭。持久记录首次初始化和用户选择，日常 CLI 操作不会重新启用已关闭的登录项。注销或关机保留应用期望状态，下次登录恢复。管理器托盘的“退出并停止应用”结束本次会话的守护，执行前显示受影响的应用；单个应用的“停止”则持久生效。

## 2. NuGet 包规范 v1

作者按标准 .NET Tool 打包，并在顶层 `.nupkg` 的根目录放入 `.dotherd/app.json`，以声明该版本的 DotHerd 托管能力。普通工具沿用 `DotnetTool`，标准 NuGet MCP 工具沿用 `DotnetTool;McpServer`。包 ID、版本、运行时和工具命令沿用 NuGet / .NET SDK 的元数据。

已有 .NET Tool 的打包改动只需包含清单。下例适用于尚未包含该文件的项目。若文件已是项目项，在对应项上使用 `Update` 设置打包属性。打包后检查顶层包内的清单路径，并验证清单声明的命令能从安装目录启动。

```xml
<ItemGroup>
  <None Include=".dotherd/app.json" Pack="true" PackagePath=".dotherd/" />
</ItemGroup>
```

以下是 **Serena Pool 接入后的拟议清单**。当前发布包尚未实现示例中的托管参数和控制协议。

```json
{
  "schemaVersion": 1,
  "displayName": "Serena Pool",
  "command": "serena-pool",
  "args": ["--supervised"],
  "readiness": "notify",
  "tray": {
    "default": "separate",
    "actions": [{ "id": "open", "label": "打开控制台" }],
    "onActivate": "open"
  },
  "kind": "mcp",
  "mcp": {
    "namespace": "serena_pool",
    "transport": "stdio",
    "args": ["mcp", "--connect-only"]
  }
}
```

- 必填 `schemaVersion`、`command`。`command` 必须对应当前工具安装产生的命令，以持久目录内的绝对路径执行。参数用字符串数组原样传递。宿主以普通用户权限运行。清单描述启动行为，不提供发布者身份证明。应用必须保持前台进程存活，交出可监督的生命周期。
- 可选 `displayName` 默认用包 ID，`args` 默认空数组。`readiness` 默认 `process`，存活状态显示“运行中”；选择 `notify` 表示应用接入控制协议，在完成自身初始化后发出就绪消息，才显示“已就绪”。
- 可选 `tray` 默认只在管理器中列出该应用。`separate` 是作者建议的独立托盘，用户设置可覆盖。动作 ID 在本应用内唯一，`onActivate` 引用其中一个动作。`kind` 缺省为 `process`。`kind: "mcp"` 要求提供 `mcp` 声明并在 MCP 服务页登记；类型与独立托盘可以同时启用。
- 按来源、包 ID 和精确版本读取顶层工具包。RID 专用发布也将清单放在顶层工具包，由 SDK 选择平台实现。出现来源歧义时要求用户选择，安装使用已选来源和版本，并保留来源信息。

| 输入 | `install` / `add` 的处理 |
|---|---|
| 顶层 .NET Tool 含 `.dotherd/app.json` | `dotherd install <包ID>[@版本]` 优先校验该清单。若同时提供标准 MCP 声明，发现冲突时指出字段并停止安装。清单损坏、主版本不受支持或入口非法时终止安装。诊断指明出错位置或字段，版本错误时列出支持的主版本。校验通过后按清单托管，常驻应用等待启动或就绪，纯 stdio 服务按连接启动。 |
| 无 DotHerd 清单，带 `McpServer` 和 `.mcp/server.json` 的标准 MCP 工具 | 安装器读取本地传输和必填输入，预览并经用户确认后登记到 MCP 页。纯 stdio 工具由网关按连接启动。只声明远程端点或声明无法解析时显示原因。 |
| DotHerd 清单与标准 MCP 声明均缺省的普通 .NET Tool | 默认只列出它提供的命令并提示“未声明常驻入口，本次未启动”。用户可显式执行 `dotherd install Some.Tool --as-process --command some-tool -- serve`。入口必须属于该包，最终的 `serve` 是传给应用的参数。 |
| 普通 NuGet 类库 | 返回“该包没有可独立安装的 .NET Tool 入口”；若已有本地可执行文件，可用 `add`。 |
| RID 实现子包 | 按 `DotnetToolRidPackage` 识别并提示“请选择工具顶层包”。SDK 可直接安装某些实现子包，DotHerd 的托管清单位于顶层包。[P01 实测](README.md#p01-本地-nuget-源实测) |
| 本机已有可执行程序 | `dotherd add local-api --cwd "C:/Apps/MyApi" -- dotnet "C:/Apps/MyApi/MyApi.dll"`。显式登记名称、命令和参数。记录解析出的程序路径，不经 shell 拼接。移除时仅解除托管，保留用户原文件。 |

## 3. 安装、更新和卸载

1. **解析**。CLI 把请求交给管理器。取符合约束的版本，按第 2 节分类。首次省略版本时选稳定版并保存精确版本；再次 `install` 已登记的同包只报告现状。用户显式指定另一版本时提示使用 `update`。这些操作不改写已手动停止的状态。
2. **安装**。同一应用的操作串行。带托管清单的工具、经确认的标准 MCP 工具和显式接受的普通工具用 `dotnet tool install <ID> --version <精确版本> --tool-path <应用版本目录>` 安装。目录为当前用户专有，分应用、分版本。先完成 SDK 安装和入口校验，再登记候选版本。日志、配置和用户数据独立于版本目录。宿主捕获常驻程序的 stdout、stderr 和退出原因，按应用滚动写日志；CLI 可用 `dotherd logs <应用>` 查看。
3. **首次启动**。登记期望状态为“运行”，管理器启动程序并返回真实结果。安装成功但启动失败时命令返回非零，应用保留为“已安装／启动失败”，可查看原因并重试。对 `notify` 应用，默认等待 30 秒，超时会停止本次尝试、记录失败并进入守护策略；对普通程序只能确认进程已启动。对纯 stdio MCP，安装成功表示登记完成并通过工具发现，界面显示“可连接”；空闲期间没有常驻进程属于预期。更新时先探测候选版本，已有连接随原版本结束，再回收旧文件。
4. **更新**。显式执行 `dotherd update <应用>[@版本]`。按第 2 节校验所选新版本，并排安装成功后再停旧进程，沿用参数、配置和期望状态。运行中的应用通过新版本启动判定后提交切换，失败则尝试恢复旧版本并报告两次结果。已停止的应用只切换安装版本，继续保持停止。应用自行迁移的数据不在二进制回退保证内。
5. **卸载**。`dotherd uninstall <应用>` 先持久取消恢复和重试，再停止进程树、移除应用托盘和托管记录，最后清理宿主管理的包目录。默认保留用户数据。卸载管理器前由 `dotherd disable` 解除登录项并停止宿主，再执行 `dotnet tool uninstall -g DotHerd`。普通用户级工具卸载只管理工具本身，不能充当桌面集成的卸载钩子。

管理器保存精确版本、来源、入口、期望状态和正在进行的操作。应用操作来自 CLI、托盘或管理界面时走同一写入路径。版本切换和卸载中断后，下次启动先对照持久记录与目录恢复未完成操作，再恢复进程。应用状态不能仅靠上次记录的 PID 判断。

## 4. 进程守护和 Windows 宿主

| 事件 | 行为 |
|---|---|
| 安装成功、显式 start 或登录恢复 | 期望为“运行”才启动。同一应用只允许一个受管主进程，重复 start 返回当前状态。 |
| 就绪、退出、崩溃 | 分开展示“启动中／运行中／已就绪／等待重试／失败”和退出原因。按连接创建的 MCP 子进程随绑定正常结束时直接释放。常驻应用在期望运行时自行退出，无论退出码是否为 0，均按重启策略处理。 |
| 连续启动失败 | MVP 建议退避 1、2、4、8 秒，连续第 5 次失败后转“失败”，保留错误和日志，等待手动重试。连续稳定运行 60 秒后清零。该策略防止配置错误形成重启风暴。 |
| 主动 stop / restart | stop 先写“已停止”并取消待执行重试，再请求优雅退出。接入控制协议的应用收到 shutdown；普通程序按平台能力请求退出。建议默认宽限 10 秒，超时后收回受管进程树并记录原因。restart 是一次有序停启。 |
| 宿主关闭、异常退出 | 正常关闭先停受管进程。Windows 使用每个应用独立的 Job Object 约束进程树，防止 core 或子 worker 遗留。宿主下次运行只恢复期望为“运行”的应用。首版由登录自启、重新打开管理器或执行 start 恢复宿主。 |

CLI 和桌面宿主是两个进程角色；短命令通过本地 IPC 调用唯一宿主。Windows 用当前用户专用命名管道、单实例锁和 HKCU Run 登录项；登录启动无控制台窗口。管理界面采用 Avalonia，包识别、状态机、CLI 和托盘动作模型共用。登录启动登记、进程树控制和 IPC 接入点由平台实现。Linux/macOS 后续沿用应用包，无需作者为了 Windows 注册表或托盘 API 修改应用逻辑。

## 5. 管理界面与应用托盘接口

管理窗口提供已安装应用、版本、状态、最近失败原因和日志入口，支持安装、启停、重启、更新、卸载、打开作者界面，以及切换独立托盘。关闭窗口后保留管理器托盘。主托盘可打开管理窗口、快速查看失败应用和退出。

独立托盘由 DotHerd 绘制。可选用清单内的 `tray.icon` 指定包内相对图标路径。宿主应拒绝越出包目录的路径。应用不必引入 Avalonia 或引用宿主程序集。宿主通过子进程环境传入该次启动专用的本地控制通道和凭据；协议 v1 使用一行一条 UTF-8 JSON 消息，以启动实例区分新旧连接。应用作者可用一个薄 .NET SDK 订阅事件，也可以直接实现协议。通道与应用的标准输入输出分离，日志和 MCP 数据各走原有通道。

| 方向 | v1 消息 | 行为 |
|---|---|---|
| 应用 → 宿主 | `{"type":"ready"}` | 通知应用入口已可服务。 |
| 应用 → 宿主 | `{"type":"tray.update","tooltip":"2 个活跃会话","enabled":{"open":true}}` | 更新本应用图标提示和已声明菜单项的可用状态。宿主校验动作 ID；用户关闭独立托盘后仍保留最新状态。 |
| 宿主 → 应用 | `{"type":"action","id":"open"}` | 点击独立托盘、菜单或管理器里的“打开”使用同一动作。作者激活已有原生窗口，或打开自己的 WebView / 本机网页。 |
| 宿主 → 应用 | `{"type":"shutdown"}` | 应用停止接受新工作、清理资源并退出。 |

作者可在 `tray.icon` 给出包内相对路径；宿主只加载当前包内的图片，缺省用宿主的应用图标。应用离线时禁用其动态动作，仍允许用户从管理器查看日志和重启。同一应用已有自己的托盘时，可以保留原图标并关闭 DotHerd 的独立图标；也可像下面的 Serena Pool 一样，将图标交给 DotHerd。

## 6. MCP 类型与统一本机网关

每个客户端只配置一次 DotHerd。网关作为 MCP 服务端对接 agent，作为 MCP 客户端对接已登记的服务。Moor 的单入口模式是交互参考。DotHerd 自行管理 NuGet 安装、进程和连接；同一应用只有一个生命周期所有者。

**管理界面与命令。** “应用”显示已安装程序；“MCP 服务”是同一批记录的专属视图，展示进程状态、网关启用状态、传输、工具列表和最近的协议错误。安装符合规范的 MCP 包后自动登记并尝试发布；安装成功、进程存活、工具可调用分别显示。关闭某服务的网关开关会撤销其路由，保留常驻进程和原有托盘。主动停止应用也会撤销路由；列举工具或客户端重连不得把它重新拉起。可用 `dotherd mcp list` 查看状态，`dotherd mcp config` 导出统一客户端配置。

| 边界 | MVP 约定 |
|---|---|
| agent → 网关 | 每用户一个固定的回环地址和 `/mcp` 路径，提供 Streamable HTTP。安装时确定并持久保存端口，后续占用时报错。HTTP 使用当前用户管理的客户端凭据，校验 Host、Origin；凭据不写日志。仅支持 stdio 的客户端用 `dotherd mcp connect` 接入同一网关，经用户专用 IPC 建立独立连接。两种入口看到相同的工具命名规则。 |
| 网关 → 应用 | 本地 stdio 或回环 Streamable HTTP。前台 core 与协议入口分开时，管理器守护 core，网关由活动安装版本创建 bridge；纯 stdio 包的主命令本身就是协议入口，连接结束即回收该子进程。清单为前者声明主进程及 `mcp.args`，纯 stdio 模式显式设置 `mcp.lifecycle: "onConnection"`，仅按客户端绑定启动协议进程；HTTP 服务在 `mcp.url` 中声明本机 MCP 地址，与管理页面地址分别校验。 |
| 工具发现与调用 | 对每个启用的服务按其协议版本发现工具，读取完整分页并保留输入／输出 schema。以持久命名空间加双下划线发布，如 `serena_pool__acquire`。保存公开名到原始服务、工具名的映射；冲突或超出客户端名称约束的条目给出明确诊断。调用按映射路由，保留工具返回和错误。安装、停用、更新时刷新目录，按连接支持的协议通知或允许重新列举。 |
| 会话与故障 | 每个 stdio 前端连接有独立绑定；HTTP 用独立凭据区分客户端配置。对于有状态的 stdio 后端，每个绑定独享上游连接。多个 agent 共用同一份 HTTP 凭据时，网关无法自动区分它们；需要严格隔离时使用不同凭据或各自启动 stdio 前端。网关作用域内关联请求、进度及取消。后端退出仅影响发给它的调用，明确返回服务名和失败原因；禁止自动重放可能已执行的调用。 |
| 协议能力 | 首版交付 tools 发现与调用。按两侧实际支持的 MCP 修订分别处理请求；仅声明已实现能力。2026-07-28 已取消旧式 initialize 握手和协议会话，绑定身份由 DotHerd 管理。旧版客户端／后端进入对应握手流程；遇到不支持的修订明确标记不兼容。资源、提示词和需要反向请求的高级能力列为后续扩展。 |

**托管状态与协议状态独立。** 进程就绪后，网关再做协议探测与工具发现。若握手失败，保留可诊断的常驻进程和日志，界面显示“进程已运行／MCP 不兼容”；此时不发布该服务工具，也不因协议错误反复重启健康进程。对于纯 stdio 包可以创建短时发现连接，发现完成即释放，真实调用使用对应客户端绑定的连接。

## 7. Serena Pool 接入示例

**本机仓库的现状**：包 ID 是 `SerenaPool`，工具入口是 `serena-pool`。每个 MCP 客户端启动一个 `serena-pool mcp` stdio bridge，多个 bridge 通过当前用户的命名管道连接共享 core。默认 `http://127.0.0.1:32980/` 是 Web 管理面板。这个地址不能作为 agent 的 MCP 地址。core 已有前台入口 `--core`，但默认 bridge 在连接失败时会自行拉起 core。core 在无客户端且池空闲时会在 15 秒的检查中退出。Windows 托盘由 dashboard 进程创建。

**作者改动建议**：

1. 沿用 `DotnetTool;McpServer` 和现有 `.mcp/server.json`，按第 2 节将 `.dotherd/app.json` 打入顶层包。新增公开的前台参数 `--supervised`。复用现有 core 路径和按用户、端口的单实例保护。此模式取消 **core 自身的空闲退出**；worker 空闲回收、租约过期仍由 Serena Pool 负责。如发现同端口已有独立 core，报告冲突并给出迁移提示，避免强杀已有会话。
2. 受管 core 就绪时发送 `ready`，管理命名管道和 MCP 管道开放后才报告可用。若启用 dashboard，由 core 启动沿用现有 dashboard 的子进程，单独抑制它自建的 Windows 托盘。现有 `Pool__WebDashboard=false` 会连管理页面一起关闭，因此要新增只抑制旧托盘的行为。`open` 动作打开当前配置的 dashboard；dashboard 未启用时禁用该动作。
3. 给 `mcp` 增加 `--connect-only`。bridge 只连接受管 core，失败时向 stderr 给出可操作的原因和非零退出码。由 DotHerd 网关根据 `mcp.args` 启动活动版本的 bridge，每个客户端绑定独立连接，共享同一个受管 core。bridge 的 stdout 专供 MCP，stderr 供诊断。Pool 已停止时网关撤销它的工具。
4. 安装前核对 Serena Pool 既有运行依赖及其可发现性，包括 Serena worker 所需的 `serena` 命令。缺依赖时明确报告。DotHerd 管理应用包和常驻进程；所托管的程序继续负责说明其外部依赖。

接入后用户运行 `dotherd install SerenaPool`。agent 只登记 DotHerd，后续增删 MCP 服务沿用这份配置。以下是常见 stdio 客户端格式；HTTP 客户端可通过 `dotherd mcp config --transport http --client <名称>` 获取同一网关 URL 及对应凭据的配置说明。

```json
{
  "mcpServers": {
    "dotherd": { "command": "dotherd", "args": ["mcp", "connect"] }
  }
}
```

例如，agent A 调用 `serena_pool__acquire` 时，网关在 A 的 bridge 上调用原始 `acquire`，将返回的租约原样交给 A；B 可通过另一绑定在自己的 worktree 上执行同样流程。core、worker、worktree 选择和租约释放仍由 Serena Pool 管理。core 重启会使旧租约失效，调用方需重新 acquire。协议可用性必须通过实际工具调用验收；进程或管理网页在线均不足以判定成功。

## 8. 第一版验收

| 验收 | 通过条件 |
|---|---|
| 首装和重登 | 用全局入口及干净环境下的 dnx 入口分别安装；关闭终端不影响进程。重新登录只出现一个宿主、一个 core，用户关闭登录自启后不会恢复。 |
| 识别边界 | 仅有 `DotnetTool` 类型且托管清单有效的包可自动接入。无托管清单时，标准 MCP 工具经确认登记，普通 Tool 默认列出命令，显式入口可守护。托管清单损坏时即使有标准 MCP 声明也终止安装。普通类库不执行，本地程序卸载保留原文件。 |
| 真正守护 | 模拟 core 崩溃可恢复；故意配置错误能从管理界面看出原因；手动 stop 后连续连接或下次登录都不会把它重新拉起。 |
| 托盘与管理 | 主窗口关闭后仍可通过主托盘恢复；独立托盘的开关、单击和菜单均有效；Serena Pool 的 dashboard 正常且每个应用只有预期数量的图标。 |
| 版本切换 | 更新成功后 agent 保持相同配置；新版本无法就绪时显示失败并尝试恢复旧版本；更新或卸载中途退出后状态可恢复。 |
| MCP 网关 | 同一份网关配置可发现两个都有 `list` 的服务，命名空间与调用目标正确。新增或停用一个服务无需编辑 agent 配置。一个后端协议不兼容或退出时另一个仍能调用；进行中的请求不会被重复执行。 |
| HTTP 与 stdio 隔离 | 从相同 URL 使用不同凭据，或使用两条独立 stdio 前端连接，验证上游绑定、并发请求 ID 和取消消息分别归属正确；管理端、令牌和应用日志不进入 agent 工具目录。 |
| Serena 端到端 | 两个 agent 连接同一 core，分别 acquire 不同 worktree 并实际调用；释放后无客户端超过 15 秒 core 仍在，worker 按自己的策略回收。 |

## 参考依据

- 标准工具安装、NuGet 包类型、平台专用包和 dnx 入口：[dotnet tool install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install) · [dotnet tool exec](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec) · [NuGet 包类型](https://learn.microsoft.com/en-us/nuget/create-packages/set-package-type) · [SDK 工具打包逻辑](https://github.com/dotnet/sdk/blob/v10.0.100/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.PackTool.targets) · [RID 专用工具](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools)
- PM2 进程模型、Windows 进程树、登录启动和桌面托盘：[PM2](https://pm2.io/docs/runtime/guide/process-management/) · [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) · [登录启动](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys) · [Avalonia TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon/)
- MCP 单入口和协议：[Moor 网关](https://github.com/varandrew/moor#api) · [MCP 2026-07-28 修订](https://blog.modelcontextprotocol.io/posts/2026-07-28/) · [MCP 工具规范](https://modelcontextprotocol.io/specification/2026-07-28/server/tools) · [NuGet MCP 包规范](https://learn.microsoft.com/nuget/concepts/nuget-mcp)。
- Serena Pool 现状来自本机仓库的 README、SerenaPool.csproj、Program.cs、StdioBridge.cs、LocalPipeServer.cs 和 WebDashboard.cs。[Serena Pool 仓库](https://github.com/QyQj/serena-pool)

本稿是设计提案。产品名已定为 DotHerd，命令为 dotherd，NuGet 包 ID 尚未预留。M0 已验证工具包、同包桌面宿主和 Serena Pool 的实际 REPL 调用，产品级 Windows 登录与故障恢复待实现。首次 acquire 的握手错误已定位到 core 与慢启动 worker 的协议协商，归入 M6 作者侧联调。[M0 验证记录](README.md) · [握手错误复现](probes/P03/README.md)
