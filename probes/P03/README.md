# P03 · Serena Pool 直连

2026-11-27，Windows x64，.NET SDK `11.0.100`。客户端为本目录的 `DotHerd.P03/1.0.0`，客户端和 Pool 都使用 C# MCP SDK `2.4.0`。真实 REPL 返回了 worker 内的包版本 `serena-agent 2.1.0`、Python `mcp 2.2.0` 和计算结果 `6 × 7 = 42`。

| 客户端到 bridge 的修订 | 发现和能力 | 调用结果 |
|---|---|---|
| `2026-07-28` | 首包 `server/discover`，服务声明 `tools`。逐请求核对 `_meta` 中的协议修订、客户端信息和客户端能力。 | 发现 `acquire / call / list / release`；领取指定目录、执行 REPL、释放返回 `true`。 |
| `2025-11-25` | 首包 `initialize`，服务回报同一修订和 `tools` 能力。客户端发送 `notifications/initialized`。 | 同样发现四个工具，执行同一调用并释放。 |

两行均先直连本机正在使用的 `dotnet tool exec SerenaPool@0.2.0 -- mcp`，随后在隔离地址上用已安装的 `SerenaPool@0.3.0` 复验。`0.3.0` 的 worker stdio 记录显示，core 通过 `server/discover` 建立 `2026-07-28` 连接，再发 `tools/list` 和 `tools/call`。换成旧版前端后，core 继续用新版调用 worker。[新版协议](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning) · [旧版握手](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)

### 进度和取消

以下结果均在 `0.3.0` 上分别用两种前端修订实测。

- 客户端发送了 `progressToken`。普通调用和受控等待中的调用都收到 0 条进度通知。M4 可以转发实际收到的通知；这两个版本组合尚无服务端发出进度的正例。[进度规范](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/progress)
- 同一租约上，第一条 REPL 保持运行，第二条已进入队列。取消第二条后，客户端发出 `notifications/cancelled`，core 报告排队数从 1 降到 0；随后检查 REPL 状态，第二条代码从未执行。
- 在确认第一条已进入 worker 后取消它，客户端结束等待。放行 worker 后，新调用读到了第一条写入的完成标记。调用方取消等待后，已经开始的代码仍可能产生副作用。M4 应保留请求与绑定的对应关系，避免自动重放。

### 首次 acquire 的握手错误

本机 `0.2.0` 的历史 core 日志把错误定位在创建 worker 客户端的初始化阶段。在 `0.3.0` 上将测试 worker 的启动推迟 7 秒，可稳定制造相同的协议冲突。C# SDK 自动协商默认给 `server/discover` 5 秒，超时后尝试旧握手。[SDK 2.4.0 的超时定义](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.4.0/src/ModelContextProtocol.Core/Client/McpClientOptions.cs)

| 链路 | 慢启动时的证据 |
|---|---|
| 客户端 → stdio bridge | 前端完成新版发现，`tools/list` 返回全部四个工具。 |
| bridge → core 命名管道 | core 收到 `tools/call(acquire)` 并启动了本轮 worker。 |
| core → worker stdio | 同一连接先发送 `server/discover (2026-07-28)`，接着发送 `initialize (2025-11-25)`。worker 对发现返回成功，对握手返回 `-32022`。前端的 `acquire` 失败。 |

7 秒是本次复现条件；历史日志没有记录当时的 worker 启动耗时。M6 的作者侧待办是处理慢启动时的协商，并用相同条件验收首次领取。M1–M3 可继续实施。

### 复验

需要 Windows x64、PowerShell 7.4+、Git、.NET 10 运行环境、上述 SDK 和 PATH 中的 Serena 2.1.0。从仓库根目录运行：

```powershell
pwsh -NoProfile -File .\probes\P03\verify.ps1
pwsh -NoProfile -File .\probes\P03\verify.ps1 -SlowWorker
```

第一条预期退出码为 0。第二条在固定的 Pool 版本上预期以退出码 1 报告 `tools/call(acquire)` 和 `initialize error -32022`。脚本安装精确版本的 NuGet 工具，创建临时 Git 项目，使用独立的 core 和命名管道。worker 观察程序只记录协议方法、修订与握手结果。测试结束后回收本轮进程和临时目录。
