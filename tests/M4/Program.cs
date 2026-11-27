using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args is not [var cli]) return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = deadline.Token;
const string alpha = "m4_alpha__list", legacy = "m4_legacy__list", dual = "m2_dual__list";

async Task<string> Cli(string[] command, string? error = null)
{
    var info = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    foreach (string arg in command) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync(ct);
    var stderr = process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);
    if (error is not null)
    {
        Check(process.ExitCode != 0 && (await stderr).Contains(error, StringComparison.OrdinalIgnoreCase), $"Expected CLI error {error} from {command[0]}.");
        return "";
    }
    Check(process.ExitCode == 0, $"CLI {command[0]} failed: {await stderr}");
    return await stdout;
}

Task<McpClient> Stdio(string? revision = null) => McpClient.CreateAsync(new StdioClientTransport(new()
{
    Command = cli, Arguments = ["mcp", "connect"]
}), new() { ProtocolVersion = revision }, cancellationToken: ct);

async Task<(Uri Url, string Auth)> HttpConfig(string name)
{
    var config = JsonNode.Parse(await Cli(["mcp", "config", "--transport", "http", "--client", name]))!["mcpServers"]!["dotherd"]!;
    return (new((string)config["url"]!), (string)config["headers"]!["authorization"]!);
}

Task<McpClient> Http((Uri Url, string Auth) config, string? revision = null) =>
    McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = config.Url, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = config.Auth }
    }), new() { ProtocolVersion = revision }, cancellationToken: ct);

async Task<JsonNode> Status(string name) => JsonNode.Parse(await Cli(["status", name]))!;

async Task Until(Func<Task<bool>> check, string message)
{
    var timer = Stopwatch.StartNew();
    do
    {
        if (await check()) return;
        await Task.Delay(80, ct);
    } while (timer.Elapsed < TimeSpan.FromSeconds(15));
    throw new Exception(message);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonElement Value(CallToolResult result)
{
    Check(result.IsError != true && result.StructuredContent is not null, "Missing structured tool result.");
    return result.StructuredContent!.Value;
}

static string Label(CallToolResult result) => Value(result).GetProperty("label").GetString()!;
static int Pid(CallToolResult result) => Value(result).GetProperty("pid").GetInt32();

async Task<CallToolResult> Call(McpClient client, string tool, string? mode = null, string? marker = null,
    long? id = null, CancellationToken? cancellation = null)
{
    var arguments = new Dictionary<string, object?> { ["mode"] = mode, ["marker"] = marker };
    if (id is null) return await client.CallToolAsync(tool, arguments, cancellationToken: cancellation ?? ct);
    return await client.SendRequestAsync<CallToolRequestParams, CallToolResult>("tools/call", new()
    {
        Name = tool,
        Arguments = arguments.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)),
        Meta = new() { ["progressToken"] = "shared-progress" }
    }, McpJsonUtilities.DefaultOptions, new RequestId(id.Value), cancellation ?? ct);
}

async Task CheckIsolation(McpClient first, McpClient second, string tool, string cwd, string marker, bool sendNotification)
{
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var progressA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var progressB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var noticeA = first.RegisterNotificationHandler("notifications/progress", (notification, _) =>
    {
        if ((string?)notification.Params?["progressToken"] == "shared-progress") progressA.TrySetResult();
        return ValueTask.CompletedTask;
    });
    await using var noticeB = second.RegisterNotificationHandler("notifications/progress", (notification, _) =>
    {
        if ((string?)notification.Params?["progressToken"] == "shared-progress") progressB.TrySetResult();
        return ValueTask.CompletedTask;
    });
    string a = marker + "-a", b = marker + "-b";
    var firstCall = Call(first, tool, "wait", a, 42, cancellation.Token);
    var secondCall = Call(second, tool, "wait", b, 42);
    await Until(() => Task.FromResult(File.Exists(Path.Combine(cwd, "entered-" + a)) && File.Exists(Path.Combine(cwd, "entered-" + b))),
        "Both calls with ID 42 did not reach separate upstreams.");
    await Task.WhenAll(progressA.Task, progressB.Task).WaitAsync(TimeSpan.FromSeconds(10), ct);
    if (sendNotification)
        await first.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams
        { RequestId = new RequestId(42), Reason = "fixture cancellation" }, cancellationToken: ct);
    else cancellation.Cancel();
    await Until(() => Task.FromResult(File.Exists(Path.Combine(cwd, "canceled-" + a))), "First upstream did not observe cancellation.");
    Check(!File.Exists(Path.Combine(cwd, "canceled-" + b)) && !secondCall.IsCompleted, "Cancellation escaped the client binding.");
    File.WriteAllText(Path.Combine(cwd, "release-" + b), "");
    Check(Label(await secondCall) is "first" or "legacy", "Second client did not complete its original request.");
    try { await firstCall; throw new Exception("Cancelled request completed normally."); }
    catch (OperationCanceledException) { }
    catch (McpProtocolException e) when (e.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)) { }
}

var stdioConfig = JsonNode.Parse(await Cli(["mcp", "config"]))!["mcpServers"]!["dotherd"]!;
Check((string?)stdioConfig["command"] == "dotherd" && (string?)stdioConfig["args"]![1] == "connect", "Exported stdio config.");
var alphaStatus = await Status("M4.Alpha");
string alphaCwd = (string)alphaStatus["cwd"]!;
string legacyCwd = (string)(await Status("M4.Legacy"))["cwd"]!;
string oldDir = (string)alphaStatus["package"]!["directory"]!;

await using (var first = await Stdio())
await using (var second = await Stdio("2025-11-25"))
{
    Check(first.NegotiatedProtocolVersion == "2026-07-28" && second.NegotiatedProtocolVersion == "2025-11-25", "Frontend protocol revisions.");
    var tools = await first.ListToolsAsync(cancellationToken: ct);
    var info = tools.Single(t => t.Name == alpha).ProtocolTool;
    Check(tools.Any(t => t.Name == "m4_alpha__extra") && tools.Any(t => t.Name == "m4_legacy__extra") &&
        info.InputSchema.GetProperty("properties").TryGetProperty("mode", out _) &&
        info.OutputSchema?.GetProperty("properties").TryGetProperty("label", out _) == true, "Pagination or schema forwarding.");
    Check(Label(await Call(first, alpha)) == "first" && Label(await Call(first, legacy)) == "legacy" &&
        Value(await Call(first, legacy)).GetProperty("negotiated").GetString() == "2025-11-25", "Tool routing or legacy backend.");
    Check((await Call(first, alpha, "tool-error")).IsError == true, "Tool failure was rewritten.");
    Check(Pid(await Call(first, alpha)) != Pid(await Call(second, alpha)), "Stdio clients shared an upstream process.");
    await CheckIsolation(first, second, alpha, alphaCwd, "stdio", sendNotification: false);

    try { await Call(first, alpha, "disconnect", "once"); throw new Exception("Disconnected backend returned success."); }
    catch (McpProtocolException e) { Check(e.Message.Contains("M4.Alpha"), "Disconnect lacks service diagnostic."); }
    Check(File.ReadAllLines(Path.Combine(alphaCwd, "effect-once")).Length == 1 && Label(await Call(first, legacy)) == "legacy",
        "Disconnected call was replayed or took another service offline.");
}
Console.WriteLine("PASS M4 stdio real calls, pagination, schemas, both revisions, same-ID cancellation, progress and no replay");

var one = await HttpConfig("agent-a");
var two = await HttpConfig("agent-b");
Check(one.Url == two.Url && one.Auth != two.Auth, "HTTP credential isolation.");
var savedClients = File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("DOTHERD_HOME")!, "mcp.clients"));
Check(!System.Text.Encoding.UTF8.GetString(savedClients).Contains(one.Auth[7..], StringComparison.Ordinal), "HTTP credential was written in plaintext.");
using (var http = new HttpClient())
{
    using var noToken = await http.GetAsync(one.Url, ct);
    Check(noToken.StatusCode == HttpStatusCode.Unauthorized, "HTTP accepted missing credentials.");
    using var foreign = new HttpRequestMessage(HttpMethod.Get, one.Url);
    foreign.Headers.Add("Origin", "https://outside.example");
    foreign.Headers.Add("Authorization", one.Auth);
    using var rejected = await http.SendAsync(foreign, ct);
    Check(rejected.StatusCode == HttpStatusCode.Forbidden, "HTTP accepted a foreign Origin.");
    using var wrongHost = new HttpRequestMessage(HttpMethod.Get, one.Url);
    wrongHost.Headers.Host = "outside.example";
    using var rejectedHost = await http.SendAsync(wrongHost, ct);
    Check(rejectedHost.StatusCode == HttpStatusCode.Forbidden, "HTTP accepted a foreign Host.");
}

await using (var first = await Http(one))
await using (var second = await Http(two))
await using (var oldHttp = await Http(one, "2025-11-25"))
{
    Check(oldHttp.NegotiatedProtocolVersion == "2025-11-25" && Label(await Call(oldHttp, legacy)) == "legacy", "Legacy Streamable HTTP.");
    await CheckIsolation(first, second, legacy, legacyCwd, "http", sendNotification: true);
    Check(Pid(await Call(first, legacy)) == Pid(await Call(oldHttp, legacy)) &&
        Pid(await Call(first, legacy)) != Pid(await Call(second, legacy)), "HTTP bindings did not follow the credential.");
    string? upstreamError = null;
    try
    {
        await Until(async () =>
        {
            var state = JsonNode.Parse(await Cli(["mcp", "list", "M2.Http"]))!;
            if ((string?)state["state"] == "available") return true;
            upstreamError = (string?)state["error"];
            if ((string?)state["state"] == "error") await Cli(["mcp", "enable", "M2.Http"]);
            return false;
        }, "Loopback HTTP upstream did not publish.");
    }
    catch (Exception e) when (e is not OperationCanceledException)
    { throw new InvalidOperationException($"Loopback HTTP discovery failed: {upstreamError}", e); }
    Check(Label(await Call(first, "m2_http__list")) == "http", "Loopback HTTP upstream call.");
}
Console.WriteLine("PASS M4 HTTP authorization, Host/Origin, both revisions, token binding, same-ID cancellation and HTTP upstream");

await using (var original = await Stdio())
{
    var running = Call(original, alpha, "wait", "version");
    await Until(() => Task.FromResult(File.Exists(Path.Combine(alphaCwd, "entered-version"))), "Old-version call did not start.");
    await Cli(["update", "M4.Alpha@1.1.0", "--yes"]);
    Check(Directory.Exists(oldDir), "Old package was deleted while an MCP binding used it.");
    await using (var fresh = await Stdio())
        Check(Label(await Call(fresh, "m4_alpha__new-extra")) == "next", "A new binding did not use the committed version.");
    File.WriteAllText(Path.Combine(alphaCwd, "release-version"), "");
    Check(Label(await running) == "first" && Label(await Call(original, "m4_alpha__extra")) == "first", "Existing binding did not keep its old tools.");
}
await Until(() => Task.FromResult(!Directory.Exists(oldDir)), "Closing the last old binding did not clean the old package.");
await Cli(["update", "M4.Alpha@1.2.0", "--yes"], "Invalid MCP tool name");
Check((string?)(await Status("M4.Alpha"))["package"]!["version"] == "1.1.0", "Failed probe committed an incompatible candidate.");

await using (var frontend = await Stdio())
{
    var core = await Status("M2.Dual");
    var worker = await Call(frontend, dual);
    Check(Label(worker) == "bridge" && Pid(worker) != (int?)core["pid"], "Resident core bridge.");
    await Cli(["mcp", "disable", "M2.Dual"]);
    Check((int?)(await Status("M2.Dual"))["pid"] == (int?)core["pid"] &&
        !(await frontend.ListToolsAsync(cancellationToken: ct)).Any(t => t.Name == dual), "Disabling the route stopped the resident process.");
    await Cli(["stop", "M4.Alpha"]);
    Check(!(await frontend.ListToolsAsync(cancellationToken: ct)).Any(t => t.Name == alpha) &&
        (string?)(await Status("M4.Alpha"))["state"] == "stopped", "Stopped service was republished by list.");
    await Cli(["uninstall", "M4.Alpha"]);
}
await Cli(["host", "exit", "--yes"]);
var occupied = new TcpListener(IPAddress.Loopback, one.Url.Port);
occupied.Start();
try
{
    var conflict = JsonNode.Parse(await Cli(["mcp", "list"]))!["http"]!;
    Check((string?)conflict["url"] == one.Url.AbsoluteUri && conflict["error"] is not null, "Persisted port conflict was not diagnosed.");
    await Until(async () => (string?)JsonNode.Parse(await Cli(["mcp", "list", "M4.Legacy"]))?["state"] == "available",
        "Legacy service did not publish while the HTTP port was occupied.");
    await using (var surviving = await Stdio())
        Check(Label(await Call(surviving, legacy)) == "legacy", "Port conflict disabled stdio.");
    await Cli(["host", "exit", "--yes"]);
}
finally { occupied.Stop(); }
Check(await HttpConfig("agent-a") == one, "Credential or port changed after host restart.");
Console.WriteLine("PASS M4 resident bridge, route withdrawal, update pinning and cleanup, failed candidate, port conflict and persisted credentials");
return 0;
