using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args is ["start-mcp-server", ..]) return await WorkerTrace.Run(args);
if (args is not [var cli, var first, var second, var address]) return 2;

const string app = "SerenaPool", prefix = "serena_pool__";
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var ct = deadline.Token;
using var dashboard = new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(3) };

async Task<(int Exit, string Output, string Error)> Run(string command, params string[] arguments)
{
    var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    foreach (var arg in arguments) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync(ct);
    var stderr = process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);
    return (process.ExitCode, await stdout, await stderr);
}

async Task<string> Cli(params string[] arguments)
{
    var (exit, output, error) = await Run(cli, arguments);
    Check(exit == 0, $"CLI {string.Join(' ', arguments)} failed: {error}");
    return output;
}

async Task<JsonNode> Status() => JsonNode.Parse(await Cli("status", app))!;
async Task<bool> Available() => (string?)JsonNode.Parse(await Cli("mcp", "list", app))!["state"] == "available";
async Task<JsonArray> Workers() => JsonNode.Parse(await dashboard.GetStringAsync("api/workers", ct))!.AsArray();

async Task<McpClient> Connect(string revision = "2026-07-28") => await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Command = cli, Arguments = ["mcp", "connect"]
}), new() { ProtocolVersion = revision }, cancellationToken: ct);

async Task CheckCatalog(McpClient client, bool enabled)
{
    var names = (await client.ListToolsAsync(cancellationToken: ct)).Select(tool => tool.Name).Order();
    Check(names.SequenceEqual(enabled ? [prefix + "acquire", prefix + "call", prefix + "list", prefix + "release"] : []),
        $"Unexpected tool catalog: {string.Join(", ", names)}");
}

async Task<string> Acquire(McpClient client, string worktree)
{
    var result = await client.CallToolAsync(prefix + "acquire", new Dictionary<string, object?> { ["workdir"] = worktree }, cancellationToken: ct);
    var acquisition = JsonNode.Parse(Success(result))!;
    Check(string.Equals((string?)acquisition["workdir"], Path.GetFullPath(worktree), StringComparison.OrdinalIgnoreCase),
        "Acquire returned the wrong worktree.");
    string lease = (string)acquisition["instance_id"]!;
    Check(Guid.TryParseExact(lease, "N", out _), "Acquire returned an invalid lease.");
    return lease;
}

ValueTask<CallToolResult> Call(McpClient client, string lease) => client.CallToolAsync(prefix + "call", new Dictionary<string, object?>
{
    ["instance_id"] = lease, ["code"] = "from pathlib import Path\nPath('sample.py').read_text().strip()"
}, cancellationToken: ct);

async Task Release(McpClient client, string lease)
{
    var result = await client.CallToolAsync(prefix + "release", new Dictionary<string, object?> { ["instance_id"] = lease }, cancellationToken: ct);
    Check(bool.Parse(Success(result)), "Release failed.");
}

async Task Until(Func<Task<bool>> condition, string message)
{
    var timer = Stopwatch.StartNew();
    do
    {
        if (await condition()) return;
        await Task.Delay(200, ct);
    } while (timer.Elapsed < TimeSpan.FromSeconds(15));
    throw new InvalidOperationException(message);
}

var state = await Status();
var pool = (string)state["executable"]!;
var corePid = (int)state["pid"]!;
var coreInstance = (string)state["instance"]!;
var poolProcesses = () => InstalledProcesses(pool);
var logs = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "SerenaPool", "logs");
bool HasRole(int pid, string role) => Directory.EnumerateFiles(logs, $"{role}-*-{pid}.log").Any();
Check((string?)state["state"] == "ready" && (string?)state["package"]?["version"] == "0.3.1", "The installed core is not ready.");
Check(await dashboard.GetStringAsync("", ct) is { Length: > 0 }, "The dashboard is unavailable.");
await Until(async () => await Available() && poolProcesses().Length == 2,
    "The initial MCP catalog probe did not leave one core and one dashboard.");
int dashboardPid = poolProcesses().Single(pid => pid != corePid && HasRole(pid, "dashboard"));
bool OnlyCoreAndDashboard() => poolProcesses().Order().SequenceEqual(new[] { corePid, dashboardPid }.Order());

int[] workerPids;
await using (var a = await Connect())
await using (var b = await Connect("2025-11-25"))
{
    await CheckCatalog(a, enabled: true);
    await CheckCatalog(b, enabled: true);
    var timer = Stopwatch.StartNew();
    string leaseA = await Acquire(a, first);
    Check(timer.Elapsed >= TimeSpan.FromSeconds(7), "The first worker did not pass through the seven-second delay.");
    string leaseB = await Acquire(b, second);
    Check(leaseA != leaseB, "The clients received the same lease.");
    string resultA = Success(await Call(a, leaseA)), resultB = Success(await Call(b, leaseB));
    Check(resultA == "value = 42" && resultB == "value = 84", "The clients read the wrong worktrees.");
    var workers = await Workers();
    workerPids = workers.Select(row => (int)row!["pid"]!).ToArray();
    Check(workers.Count == 2 && workerPids.Distinct().Count() == 2 &&
        workers.All(row => row!["leases"]!.AsArray().Count == 1), "Dashboard did not see both workers and leases in the same core.");
    var shared = Success(await a.CallToolAsync(prefix + "list", cancellationToken: ct));
    Check(shared.Contains("worktree-a", StringComparison.Ordinal) && shared.Contains("worktree-b", StringComparison.Ordinal),
        "The first bridge did not see the pool shared with the second.");
    var processes = poolProcesses();
    var bridges = processes.Where(pid => HasRole(pid, "stdio")).ToArray();
    Check(processes.Length == 4 && processes.Contains(corePid) && processes.Contains(dashboardPid) && bridges.Length == 2,
        "Expected two bridges sharing one installed core and its dashboard.");
    await Release(a, leaseA);
    await Release(b, leaseB);
    Console.WriteLine($"PASS 7-second cold worker, MCP 2026-07-28 returned {resultA}, MCP 2025-11-25 returned {resultB}, both released");
    Console.WriteLine($"PASS core PID {corePid}, dashboard PID {dashboardPid}, bridge PIDs {string.Join(", ", bridges)}, worker PIDs {string.Join(", ", workerPids)}");
}

await Until(() => Task.FromResult(OnlyCoreAndDashboard()), "The detached clients left their bridges running.");
await Until(async () => (await Workers()).Count == 0 && workerPids.All(pid => !Running(pid)), "Idle workers were not recycled.");
await Task.Delay(TimeSpan.FromSeconds(16), ct);
state = await Status();
Check((int?)state["pid"] == corePid && (string?)state["instance"] == coreInstance && OnlyCoreAndDashboard(),
    "The managed core did not survive a full idle interval.");
Console.WriteLine("PASS idle workers recycled and original core survived 16 seconds with zero clients or bridges");

string stale;
await using (var client = await Connect())
{
    stale = await Acquire(client, first);
    await Cli("restart", app);
}
state = await Status();
int restartedPid = (int)state["pid"]!;
Check(restartedPid != corePid && (string?)state["instance"] != coreInstance && !Running(corePid), "Restart did not replace the core.");
await Until(Available, "The restarted core did not publish its MCP tools.");
await using (var client = await Connect())
{
    var expired = await Call(client, stale);
    Check(expired.IsError == true && Text(expired).Contains("lease has expired", StringComparison.OrdinalIgnoreCase), "The new core accepted an old lease.");
    string lease = await Acquire(client, second);
    Check(Success(await Call(client, lease)) == "value = 84", "Acquire and call after restart failed.");
    await Release(client, lease);
    await Cli("stop", app);
    await CheckCatalog(client, enabled: false);
}
await Cli("host", "exit", "--yes");
await using (var client = await Connect()) await CheckCatalog(client, enabled: false);
var (bridgeExit, bridgeStdout, bridgeStderr) = await Run(pool, "mcp", "--connect-only");
state = await Status();
Check(bridgeExit != 0 && bridgeStdout.Length == 0 && bridgeStderr.Contains("Start SerenaPool in DotHerd", StringComparison.Ordinal) &&
    (string?)state["state"] == "stopped" &&
    (bool?)state["wantRunning"] == false && poolProcesses().Length == 0,
    "A bridge or a reconnect after host restart violated the stopped state.");
Console.WriteLine($"PASS core PID {restartedPid} rejected the stale lease, new acquire succeeded and stop survived client and host restart");
Console.WriteLine($"PASS connect-only exit {bridgeExit} with zero stdout and a diagnostic on stderr");
return 0;

static string Success(CallToolResult result)
{
    var text = Text(result);
    Check(result.IsError != true, text);
    return text;
}

static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

static bool Running(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static int[] InstalledProcesses(string pool)
{
    var ids = new List<int>();
    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(pool)))
        using (process)
            try { if (string.Equals(process.MainModule?.FileName, pool, StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { /* The probe can exit during enumeration. */ }
    return ids.ToArray();
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
