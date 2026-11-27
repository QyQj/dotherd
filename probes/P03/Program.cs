using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args is ["start-mcp-server", ..]) return await WorkerTrace.Run(args);
bool checkQueue = args is ["--queue", ..];
if (checkQueue) args = args[1..];
if (args is not [var revision, var directory, var command, ..] || args.Length < 4)
{
    Console.Error.WriteLine("Usage: Probe [--queue] <revision> <project> <executable> <stdio arguments...>");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = deadline.Token;
var trace = new WireTrace(new StdioClientTransport(new()
{
    Command = command, Arguments = args[3..], WorkingDirectory = directory
}), revision);
var options = new McpClientOptions
{
    ClientInfo = new() { Name = "DotHerd.P03", Version = "1.0.0" }, ProtocolVersion = revision
};
string step = "connect";
try
{
    await using var client = await McpClient.CreateAsync(trace, options, cancellationToken: ct);
    Check(client.NegotiatedProtocolVersion == revision && client.ServerCapabilities.Tools is not null, "protocol or tools capability");
    step = "tools/list";
    var tools = await client.ListToolsAsync(cancellationToken: ct);
    Check(tools.Select(t => t.Name).Order().SequenceEqual(["acquire", "call", "list", "release"]), "tool catalog");
    Console.WriteLine($"{revision} | {trace.FirstMethod} | tools: {string.Join(", ", tools.Select(t => t.Name).Order())}");
    step = "tools/call(acquire)";
    var acquisition = JsonNode.Parse(Text(await client.CallToolAsync("acquire",
        new Dictionary<string, object?> { ["workdir"] = directory }, cancellationToken: ct)))!;
    string lease = (string)acquisition["instance_id"]!;
    Check(Guid.TryParseExact(lease, "N", out _), "invalid lease in acquire response");
    try
    {
        Check(string.Equals((string?)acquisition["workdir"], directory, StringComparison.OrdinalIgnoreCase), "acquired project");
        step = "tools/call(call)";
        var progress = new ProgressCount();
        var result = await client.CallToolAsync("call", new Dictionary<string, object?>
        {
            ["instance_id"] = lease,
            ["code"] = "from importlib.metadata import version\n'/'.join([version('serena-agent'), version('mcp'), str(6 * 7)])"
        }, progress, cancellationToken: ct);
        var value = Text(result);
        Check(value == "2.1.0/2.2.0/42", "worker versions or REPL output differ from 2.1.0/2.2.0/42");
        Console.WriteLine($"call = {value} | progress token = {trace.ProgressRequested}, notifications = {progress.Count}");
        if (checkQueue)
        {
            step = "tools/call(call), queued cancellation";
            await CheckCancellation(client, trace, lease, directory, ct);
        }
    }
    finally
    {
        string operation = step;
        step = "tools/call(release)";
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Check(bool.Parse(Text(await client.CallToolAsync("release", new Dictionary<string, object?> { ["instance_id"] = lease },
            cancellationToken: cleanup.Token))), "release");
        Console.WriteLine("release = true");
        step = operation;
    }
    step = "handshake evidence";
    trace.CheckHandshake();
    return 0;
}
catch (Exception e)
{
    // Worker errors can include a local path, stderr tail, or lease. Publish only the diagnostic category.
    string reason = e.ToString().Contains("initialize handshake is not accepted", StringComparison.OrdinalIgnoreCase)
        ? "initialize handshake rejected on a 2026-07-28 connection" : e is ProbeFailure ? e.Message : e.GetType().Name;
    Console.Error.WriteLine($"{step} failed: {reason}");
    return 1;
}

static string Text(CallToolResult result)
{
    string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    if (result.IsError == true) throw new McpException(text);
    return text;
}

static void Check(bool condition, string label)
{
    if (!condition) throw new ProbeFailure(label);
}

static async Task CheckCancellation(McpClient client, WireTrace trace, string lease, string directory, CancellationToken ct)
{
    Task<CallToolResult> Call(string code, CancellationToken token, ProgressCount? progress = null) => client.CallToolAsync("call",
        new Dictionary<string, object?> { ["instance_id"] = lease, ["code"] = code }, progress, cancellationToken: token).AsTask();
    async Task Cancel(CancellationTokenSource stop, Task<CallToolResult> request)
    {
        stop.Cancel();
        try { await request; throw new ProbeFailure("call completed before cancellation"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested) { }
    }
    async Task WaitForQueue(int count)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var list = await client.CallToolAsync("list", cancellationToken: wait.Token);
            if (list.IsError == true) throw new ProbeFailure("pool list returned a tool error");
            foreach (var block in list.Content.OfType<TextContentBlock>())
            {
                var value = JsonNode.Parse(block.Text);
                IEnumerable<JsonNode?> rows = value is JsonArray array ? array : [value];
                if (rows.Any(row => (string?)row?["workdir"] == directory && (bool?)row?["running"] == true &&
                    (int?)row?["queued"] == count)) return;
            }
            await Task.Delay(50, wait.Token);
        }
    }
    // Use a file in the disposable project to keep the first call running until cancellation is observable.
    string gate = Path.Combine(directory, Guid.NewGuid().ToString("N"));
    string literal = System.Text.Json.JsonSerializer.Serialize(gate);
    using var active = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var progress = new ProgressCount();
    var running = Call($"import time, pathlib\np03_gate = pathlib.Path({literal})\np03_gate.with_suffix('.entered').touch()\nfor _ in range(600):\n if p03_gate.exists(): break\n time.sleep(0.05)\np03_finished = True", active.Token, progress);
    try
    {
        await WaitForQueue(0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queued = Call("p03_cancelled_call_ran = True", stop.Token);
        await WaitForQueue(1);
        await Cancel(stop, queued);
        await WaitForQueue(0);
        while (!File.Exists(Path.ChangeExtension(gate, ".entered"))) await Task.Delay(50, ct);
        await Cancel(active, running);
    }
    finally
    {
        await File.WriteAllTextAsync(gate, "");
    }
    Check(Text(await Call("'ok' if globals().get('p03_finished') and 'p03_cancelled_call_ran' not in globals() else 'unexpected'", ct)) == "ok"
        && trace.CancellationsSent == 2, "unexpected cancellation effect on the worker");
    Console.WriteLine($"queued cancel: 1 -> 0, code did not execute; active cancel: client stopped waiting, REPL finished; progress notifications = {progress.Count}");
}

sealed class ProbeFailure(string reason) : Exception(reason);
sealed class ProgressCount : IProgress<ProgressNotificationValue>
{
    public int Count;
    public void Report(ProgressNotificationValue value) => Interlocked.Increment(ref Count);
}
