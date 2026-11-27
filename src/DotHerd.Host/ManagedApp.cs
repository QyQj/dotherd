using System.Text.Json;

namespace DotHerd;

internal sealed class ManagedApp
{
    private sealed class Attempt
    {
        public readonly ControlChannel Control = new();
        public readonly TaskCompletionSource<Reply> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Ended = new();
        public CancellationToken Lifetime { get; }
        public Attempt() => Lifetime = Ended.Token;
        public ChildProcess? Child;
    }

    private readonly Store store;
    private Registration record;
    private readonly SemaphoreSlim gate = new(1);
    private readonly AppLog log;
    private Attempt? current;
    private CancellationTokenSource? retry;
    private string state;
    private string? reason, tooltip;
    private Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
    private bool suspended;
    private long generation;

    public ManagedApp(Store store, Registration record)
    {
        this.store = store;
        this.record = record;
        log = new(store.Logs, record.Id);
        state = record.Failures == 5 ? "failed" : "stopped";
        if (Package?.Mcp?.OnConnection == true && record.WantRunning) state = "registered";
        if (state == "failed") reason = record.LastFailure ?? "Five consecutive failures; use start to retry.";
    }

    private void Save(Registration next)
    {
        store.Save(next);
        record = next;
    }

    private object Snapshot() => new
    {
        record.App.Name, record.App.Cwd, record.App.Executable, record.App.Args, record.App.Readiness,
        record.WantRunning, state, instance = current?.Control.Instance, pid = current?.Child?.Process.Id,
        failures = record.Failures, reason, tooltip, package = Package,
        actions = record.App.Actions.ToDictionary(id => id, id => current?.Control.Connected == true &&
            state is "running" or "ready" && enabled.GetValueOrDefault(id, true))
    };

    public async Task<object> Status()
    {
        await gate.WaitAsync();
        try { return Snapshot(); } finally { gate.Release(); }
    }

    public string Logs() => log.Read();
    public InstalledPackage? Package => record.Package;

    public async Task<Reply> Run(string command, string? action = null)
    {
        Task<Reply>? wait = null;
        await gate.WaitAsync();
        try
        {
            if (suspended) throw new InvalidOperationException("Host is exiting.");
            if (Package?.Mcp?.OnConnection == true)
            {
                if (command == "action") throw new InvalidOperationException("No resident process for this on-connection service.");
                if (command is "start" or "restart" or "stop")
                {
                    bool enabled = command != "stop";
                    if (record.WantRunning != enabled) Save(record with { WantRunning = enabled });
                    state = enabled ? "registered" : "stopped";
                }
                return Reply.Data(Snapshot());
            }
            switch (command)
            {
                case "restore":
                    if (record.WantRunning && record.Failures < 5 && current is null && retry is null)
                        _ = Launch();
                    break;
                case "start":
                    if (current is not null)
                    {
                        if (state == "starting") wait = current.Started.Task;
                        break;
                    }
                    if (retry is not null) break;
                    Save(record with { WantRunning = true, Failures = 0, LastFailure = null });
                    wait = Launch();
                    break;
                case "restart":
                    Save(record with { WantRunning = true, Failures = 0, LastFailure = null });
                    generation++;
                    CancelRetry();
                    await StopAttempt("restart", graceful: true);
                    wait = Launch();
                    break;
                case "stop":
                    Save(record with { WantRunning = false, Failures = 0, LastFailure = null });
                    generation++;
                    CancelRetry();
                    await StopAttempt("stop", graceful: true);
                    state = "stopped";
                    reason = "stop";
                    break;
                case "action":
                    if (action is null || !record.App.Actions.Contains(action, StringComparer.Ordinal) ||
                        !enabled.GetValueOrDefault(action, true) || state is not ("ready" or "running") || current is null)
                        throw new InvalidOperationException("Action is undeclared, disabled, or offline.");
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        if (!await current.Control.Send(new { type = "action", id = action }, timeout.Token))
                            throw new InvalidOperationException("Application control channel is offline.");
                    break;
                default: throw new ArgumentException($"Unknown app command: {command}");
            }
            if (wait is null) return Reply.Data(Snapshot());
        }
        finally { gate.Release(); }
        return await wait;
    }

    private Task<Reply> Launch()
    {
        generation++;
        var run = new Attempt();
        current = run;
        state = "starting";
        reason = tooltip = null;
        enabled = new(StringComparer.Ordinal);
        try
        {
            log.Write("host", $"Starting instance {run.Control.Instance}.");
            _ = Observe(run.Control.Listen(message => Receive(run, message), log), run);
            run.Child = ChildProcess.Start(record.App, run.Control, log, Package?.Mcp);
            _ = Observe(Monitor(run), run);
            if (record.App.Readiness == "notify") _ = Observe(ReadinessTimeout(run), run);
            else Ready(run, "running");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            End(run);
            run.Started.TrySetResult(new(false, e.Message));
            Failure(e.Message);
        }
        return run.Started.Task;
    }

    private void Ready(Attempt run, string next)
    {
        state = next;
        log.Write("host", $"{next} pid={run.Child!.Process.Id} instance={run.Control.Instance}");
        run.Started.TrySetResult(Reply.Data(Snapshot()));
        if (record.Failures != 0) _ = Observe(After(TimeSpan.FromSeconds(60), run.Ended.Token, () =>
        {
            if (current == run && record.Failures != 0)
            {
                Save(record with { Failures = 0, LastFailure = null });
                log.Write("host", "Stable for 60 seconds; failure count reset.");
            }
        }), run);
    }

    private async Task Receive(Attempt run, JsonElement message)
    {
        try { await gate.WaitAsync(run.Lifetime); }
        catch (OperationCanceledException) when (run.Lifetime.IsCancellationRequested) { return; }
        try
        {
            if (current != run || state == "stopping") return;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    if (state == "starting") Ready(run, "ready");
                    break;
                case "tray.update":
                    var updates = new Dictionary<string, bool>(enabled, StringComparer.Ordinal);
                    if (message.TryGetProperty("enabled", out var items))
                        foreach (var item in items.EnumerateObject())
                        {
                            if (!record.App.Actions.Contains(item.Name, StringComparer.Ordinal)) throw new InvalidDataException("Unknown action ID.");
                            updates[item.Name] = item.Value.GetBoolean();
                        }
                    if (message.TryGetProperty("tooltip", out var text)) tooltip = text.GetString();
                    enabled = updates;
                    break;
                default: throw new InvalidDataException("Unknown control message.");
            }
        }
        finally { gate.Release(); }
    }

    private async Task Monitor(Attempt run)
    {
        var child = run.Child!;
        int code = await child.Exit;
        await gate.WaitAsync();
        try
        {
            if (current != run) return;
            try { await FinishTree(child); }
            finally { End(run); }
            string detail = $"Process exited with code {code}.";
            run.Started.TrySetResult(new(false, detail));
            Failure(detail);
        }
        finally { gate.Release(); }
    }

    private async Task ReadinessTimeout(Attempt run)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), run.Ended.Token); }
        catch (OperationCanceledException) when (run.Ended.IsCancellationRequested) { return; }
        await gate.WaitAsync();
        try
        {
            if (current != run || state != "starting") return;
            const string detail = "Readiness timed out after 30 seconds.";
            await StopAttempt(detail, graceful: false);
            Failure(detail);
        }
        finally { gate.Release(); }
    }

    private void Failure(string detail)
    {
        int n = record.Failures + 1;
        Save(record with { Failures = n, LastFailure = detail });
        reason = detail;
        state = n == 5 ? "failed" : "backoff";
        log.Write("host", $"{detail} Failure {n}/5.");
        if (n == 5) return;
        int seconds = 1 << (n - 1);
        log.Write("host", $"Retry in {seconds}s.");
        var ticket = retry = new();
        _ = Observe(After(TimeSpan.FromSeconds(seconds), ticket.Token, () =>
        {
            if (retry != ticket || suspended || !record.WantRunning) return;
            retry = null;
            ticket.Dispose();
            Launch();
        }), null);
    }

    private async Task After(TimeSpan delay, CancellationToken ct, Action action)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        await gate.WaitAsync();
        try { if (!ct.IsCancellationRequested) action(); } finally { gate.Release(); }
    }

    private void CancelRetry()
    {
        retry?.Cancel();
        retry?.Dispose();
        retry = null;
    }

    private async Task StopAttempt(string detail, bool graceful)
    {
        if (current is not { } run) return;
        state = "stopping";
        run.Ended.Cancel(); // Keep draining the old pipe while shutdown waits for the app to exit.
        run.Started.TrySetResult(new(false, detail));
        var child = run.Child!;
        try
        {
            if (graceful && !child.Exit.IsCompleted)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    try
                    {
                        if (!await run.Control.Send(new { type = "shutdown" }, grace.Token))
                            child.Process.CloseMainWindow();
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException)
                    { log.Write("host", $"Graceful stop unavailable: {e.Message}"); }
                    await child.Exit.WaitAsync(grace.Token);
                }
                catch (OperationCanceledException) when (grace.IsCancellationRequested)
                { log.Write("host", "10-second shutdown grace expired."); }
            }
            await FinishTree(child);
            log.Write("host", $"{detail}; exit code {await child.Exit}.");
            reason = detail;
        }
        catch (Exception e) { state = "failed"; reason = e.Message; throw; }
        finally { End(run); }
    }

    private static async Task FinishTree(ChildProcess child)
    {
        child.KillTree();
        await child.Exit;
        await child.Streams;
    }

    private void End(Attempt run)
    {
        current = null;
        run.Ended.Cancel();
        run.Control.Dispose();
        run.Child?.Dispose();
        run.Ended.Dispose();
    }

    public async Task Suspend()
    {
        await gate.WaitAsync();
        try
        {
            suspended = true;
            CancelRetry();
            try { await StopAttempt("host exit", graceful: true); }
            finally { log.Dispose(); }
        }
        finally { gate.Release(); }
    }

    private async Task Observe(Task task, Attempt? owner)
    {
        var observedGeneration = generation;
        try { await task; }
        catch (Exception e)
        {
            await gate.WaitAsync();
            try
            {
                string error = $"Supervision failed: {e.Message}";
                owner?.Started.TrySetResult(new(false, error));
                if (suspended || generation != observedGeneration) return;
                CancelRetry();
                if (current is { } run)
                {
                    run.Child?.KillTree();
                    run.Started.TrySetResult(new(false, error));
                    End(run);
                }
                reason = error;
                state = "failed";
            }
            finally { gate.Release(); }
        }
    }
}
