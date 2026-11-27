using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["fixture", var mode]) return await Fixture.Run(mode);
if (args is not [var cli, var sample, var root]) return 2;
var self = Environment.ProcessPath!;
var home = Path.Combine(root, "test profile");
Environment.SetEnvironmentVariable("DOTHERD_HOME", home);

async Task<(int Code, string Output)> Invoke(params string[] arguments)
{
    var info = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
    foreach (string arg in arguments) info.ArgumentList.Add(arg);
    using var proc = Process.Start(info)!;
    var stdout = proc.StandardOutput.ReadToEndAsync();
    var stderr = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
    return (proc.ExitCode, proc.ExitCode == 0 ? (await stdout).TrimEnd() : (await stderr).TrimEnd());
}

async Task<string> Ok(params string[] arguments)
{
    var result = await Invoke(arguments);
    Assert(result.Code == 0, $"{arguments[0]} failed: {result.Output}");
    return result.Output;
}

async Task<JsonNode> Status(string name) => JsonNode.Parse(await Ok("status", name))!;
async Task<JsonNode> Until(string name, Func<JsonNode, bool> match, int seconds = 15)
{
    var clock = Stopwatch.StartNew();
    JsonNode state;
    do
    {
        state = await Status(name);
        if (match(state)) return state;
        await Task.Delay(120);
    } while (clock.Elapsed < TimeSpan.FromSeconds(seconds));
    throw new Exception($"{name} timed out: {state}");
}

string Dir(string app) => Directory.CreateDirectory(Path.Combine(root, app + " 路径")).FullName;
async Task Register(string app, string readiness = "process") =>
    await Ok("add", app, "--cwd", Dir(app), "--readiness", readiness, "--", self, "fixture", app);

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

async Task ArgumentsAndControl()
{
    string cwd = Dir("sample");
    string[] values = ["two words", "", "a\"b", "trailing \\", "x\\\\\" y", "& %PATH% 的"];
    await Ok(["add", "sample", "--cwd", cwd, "--readiness", "notify", "--action", "open", "--", Path.GetRelativePath(cwd, sample), .. values]);
    var starts = await Task.WhenAll(Ok("start", "sample"), Ok("start", "sample"));
    var a = JsonNode.Parse(starts[0])!;
    var b = JsonNode.Parse(starts[1])!;
    Assert((string?)a["state"] == "ready" && (string?)a["instance"] == (string?)b["instance"] && (int?)a["pid"] == (int?)b["pid"], "concurrent starts diverged");
    await Until("sample", s => (string?)s["tooltip"] == "Waiting for an action");
    await Ok("action", "sample", "open");
    Assert((await Invoke("action", "sample", "unknown")).Code != 0, "undeclared action accepted");
    await Ok("stop", "sample");
    string logs = await Ok("logs", "sample");
    foreach (string line in new[] { "[stdout] args=" + JsonSerializer.Serialize(values), "[stderr] cwd=" + cwd, "[stdout] action=open", "[stdout] shutdown", "stop; exit code 0" })
        Assert(logs.Contains(line, StringComparison.Ordinal), "missing " + line);
    Assert((await Invoke("action", "sample", "open")).Code != 0, "offline action accepted");
    Console.WriteLine("PASS arguments, concurrent start, ready/tray/action/shutdown, stdout/stderr, graceful exit 0");

    var terminalStart = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "conhost.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--headless", cli, "start", "sample" }
    };
    using var terminal = Process.Start(terminalStart)!;
    await terminal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    var afterClose = await Status("sample");
    Assert(terminal.ExitCode == 0 && (string?)afterClose["state"] == "ready" &&
        (await Ok("logs", "sample")).Contains((string)afterClose["instance"]!), "application or logs lost after closing console");
    using var app = Process.GetProcessById((int)afterClose["pid"]!);
    app.Kill();
    await Until("sample", s => (string?)s["state"] == "ready" && (string?)s["instance"] != (string?)afterClose["instance"]);
    await Ok("stop", "sample");
    Console.WriteLine("PASS closing the console preserves the app and logs; killing the app triggers recovery");
}

async Task Failures()
{
    await Register("crash");
    await Ok("start", "crash");
    await Until("crash", s => (string?)s["state"] == "failed" && (int?)s["failures"] == 5, 35);
    string log = await Ok("logs", "crash");
    string[] events = log.Split('\n');
    foreach (int delay in new[] { 1, 2, 4, 8 })
    {
        int index = Array.FindIndex(events, line => line.Contains($"Retry in {delay}s."));
        Assert(index >= 0, "missing retry step");
        string next = events.Skip(index + 1).First(line => line.Contains("Starting instance"));
        var scheduled = DateTimeOffset.Parse(events[index].Split(' ')[0]);
        Assert((DateTimeOffset.Parse(next.Split(' ')[0]) - scheduled).TotalMilliseconds >= delay * 1000 - 50, "retry fired early");
    }
    Assert(log.Contains("code 0") && log.Contains("code 17"), "exit codes");
    Console.WriteLine("PASS exits 0/17, retries 1/2/4/8s and failure 5");
}

async Task StopDuringBackoff()
{
    await Register("backoff");
    await Ok("start", "backoff");
    await Until("backoff", s => (string?)s["state"] == "backoff" && (int?)s["failures"] >= 2);
    await Ok("stop", "backoff");
    string count = File.ReadAllText(Path.Combine(Dir("backoff"), "count"));
    await Task.Delay(2500);
    Assert(count == File.ReadAllText(Path.Combine(Dir("backoff"), "count")) && !(bool)(await Status("backoff"))["wantRunning"]!, "stop lost to retry");
    Console.WriteLine("PASS stop during backoff");
}

async Task ReadinessAndLateMessages()
{
    await Register("late", "notify");
    var first = Invoke("start", "late");
    var old = await Until("late", s => (string?)s["state"] == "starting");
    var clock = Stopwatch.StartNew();
    while (!(await Ok("logs", "late")).Contains("control-connected"))
    {
        Assert(clock.Elapsed.TotalSeconds < 10, "old instance did not connect");
        await Task.Delay(30);
    }
    var replacement = Ok("restart", "late");
    var next = await Until("late", s => (string?)s["state"] == "starting" && (string?)s["instance"] != (string?)old["instance"]);
    Assert((await first).Code != 0 && next["tooltip"] is null && !replacement.IsCompleted, "stale ready/update/exit affected replacement");
    Assert((await Ok("logs", "late")).Contains("sent-late-ready"), "old instance did not send stale event");
    File.WriteAllText(Path.Combine(Dir("late"), "allow-ready"), "");
    Assert((string?)JsonNode.Parse(await replacement)!["instance"] == (string?)next["instance"], "wrong ready acknowledgement");
    await Ok("stop", "late");
    Console.WriteLine("PASS restart before ready; late ready/update/exit ignored");
}

async Task NotifyTimeout()
{
    await Register("never", "notify");
    var clock = Stopwatch.StartNew();
    var reply = await Invoke("start", "never");
    Assert(reply.Code != 0 && reply.Output.Contains("30 seconds") && clock.Elapsed.TotalSeconds >= 30, "readiness deadline");
    await Ok("stop", "never");
    Assert((await Ok("logs", "never")).Contains("Retry in 1s."), "notify timeout did not enter retry policy");
    Console.WriteLine("PASS 30s readiness timeout and retry");
}

async Task Tree()
{
    await Register("stubborn", "notify");
    var live = JsonNode.Parse(await Ok("start", "stubborn"))!;
    using var app = Process.GetProcessById((int)live["pid"]!);
    using var worker = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(Dir("stubborn"), "worker"))));
    _ = app.Handle; _ = worker.Handle;
    var clock = Stopwatch.StartNew();
    await Ok("stop", "stubborn");
    Assert(clock.Elapsed.TotalSeconds >= 10 && app.WaitForExit(3000) && worker.WaitForExit(3000), "grace or process-tree cleanup");
    Assert((await Ok("logs", "stubborn")).Contains("shutdown-received"), "no graceful stop requested");
    Console.WriteLine("PASS 10s shutdown grace and child-tree cleanup");
}

async Task Stable()
{
    await Register("stable");
    await Ok("start", "stable");
    var retry = await Until("stable", s => (string?)s["state"] == "running" && (int?)s["failures"] == 1);
    await Task.Delay(TimeSpan.FromSeconds(60));
    var stable = await Until("stable", s => (int?)s["failures"] == 0);
    Assert((string?)stable["instance"] == (string?)retry["instance"], "stability observed across different instances");
    Console.WriteLine("PASS 60s stable run resets failures");
}

async Task Volume()
{
    await Register("volume", "notify");
    await Ok("start", "volume");
    await Ok("stop", "volume");
    var logs = await Ok("logs", "volume");
    Assert(logs.Contains("volume-end") && logs.Length < 2200000 && Directory.GetFiles(Path.Combine(home, "logs"), "*.log.1").Length > 0, "log rotation");
    Console.WriteLine("PASS bounded rolling stdout/stderr");
}

try
{
    await Ok("--help");
    Assert(!Directory.Exists(home), "help initialized profile");
    await Task.WhenAll(ArgumentsAndControl(), Failures(), StopDuringBackoff(), ReadinessAndLateMessages(), NotifyTimeout(), Tree(), Stable(), Volume());
    var before = await Status("stable");
    using var app = Process.GetProcessById((int)before["pid"]!);
    _ = app.Handle;
    var hosts = Process.GetProcessesByName("DotHerd.Host").Where(p => p.MainModule?.FileName?.StartsWith(
        Path.GetDirectoryName(cli)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true).ToArray();
    Assert(hosts.Length == 1, "host count");
    using var host = hosts.Single();
    host.Kill(); host.WaitForExit();
    Assert(app.WaitForExit(5000), "old tree survived host crash");
    var restored = await Until("stable", s => (string?)s["state"] == "running");
    Assert((string?)restored["instance"] != (string?)before["instance"] && (string?)(await Status("sample"))["state"] == "stopped", "restore desired states");
    var blocked = await Status("crash");
    Assert((string?)blocked["state"] == "failed" && (int?)blocked["failures"] == 5 && blocked["pid"] is null &&
        (string?)blocked["reason"] == "Process exited with code 0.", "fifth failure or its cause lost on reopen");
    await Ok("start", "crash");
    await Until("crash", s => (string?)s["state"] == "running" && (int?)s["failures"] == 0);
    await Ok("stop", "crash");
    Console.WriteLine("PASS fifth failure survives host restart and manual start recovers");
    await Ok("stop", "stable");
    await Ok("host", "exit", "--yes");
    Assert((string?)(await Status("stable"))["state"] == "stopped", "stopped state lost on reopen");
    Console.WriteLine("PASS host crash/reopen restores only desired applications");
    return 0;
}
finally { await Ok("host", "exit", "--yes"); }
