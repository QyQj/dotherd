using System.Text.Json;

namespace DotHerd;

internal sealed class ManagedApp
{
    private sealed class Attempt
    {
        public AppSpec App { get; }
        public bool Provisional { get; set; }
        public readonly ControlChannel Control = new();
        public readonly TaskCompletionSource<Reply> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Ended = new();
        public CancellationToken Lifetime { get; }
        public Attempt(AppSpec app, bool provisional)
        {
            App = app;
            Provisional = provisional;
            Lifetime = Ended.Token;
        }
        public ChildProcess? Child;
    }

    private readonly Store store;
    private readonly McpGateway gateway;
    private Registration record;
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim operations = new(1);
    private readonly AppLog log;
    private Attempt? current;
    private CancellationTokenSource? retry;
    private string state;
    private string? reason, tooltip, recoveryError;
    private Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
    private bool suspended;
    private long generation;

    public ManagedApp(Store store, Registration record, McpGateway gateway)
    {
        this.store = store;
        this.record = record;
        this.gateway = gateway;
        log = new(store.Logs, record.Id);
        state = record.Failures == 5 ? "failed" : "stopped";
        if (Package?.Mcp?.OnConnection == true && record.WantRunning) state = "registered";
        if (state == "failed") reason = record.LastFailure ?? "Five consecutive failures; use start to retry.";
        if (record.Uninstalling) state = "uninstalling";
    }

    private void Save(Registration next)
    {
        store.Save(next);
        record = next;
    }

    private object Snapshot() => new
    {
        record.Id, record.App.Name, record.App.Cwd, record.App.Executable, record.App.Args, record.App.Readiness,
        record.WantRunning, state = current?.Provisional == true ? "updating" :
            Package?.Mcp?.OnConnection == true && gateway.IsAvailable(record.Id) ? "connectable" : state,
        instance = current?.Control.Instance, pid = current?.Child?.Process.Id,
        failures = record.Failures, record.LastFailure, reason, tooltip, package = Package, record.UpdateFailure, recoveryError, mcp = McpStatusLocked(),
        separateTray = !record.Uninstalling && (record.SeparateTray ?? Package?.Tray?.Text("default") == "separate"),
        actions = record.App.Actions.ToDictionary(id => id, ActionAvailable)
    };

    private bool ActionAvailable(string id) => current is { Provisional: false, Control.Connected: true } &&
        state is "running" or "ready" && enabled.GetValueOrDefault(id, true);

    public async Task<object> Status()
    {
        await gate.WaitAsync();
        try { return Snapshot(); } finally { gate.Release(); }
    }

    public string Logs() => log.Read();
    public InstalledPackage? Package => record.Package;
    public Guid Id => record.Id;
    public bool Uninstalling => record.Uninstalling;
    public string? RecoveryError => recoveryError;

    private GatewayTarget Target(Registration registration)
    {
        var package = registration.Package!;
        string ns = record.McpNamespace ?? package.Mcp!.Namespace ?? McpGateway.DefaultNamespace(package.Id);
        return new(registration.Id, ns, registration.App, package, log);
    }

    private object? McpStatusLocked() => Package?.Mcp is null ? null : gateway.Status(Target(record), record.GatewayEnabled);

    public async Task<object?> McpStatus()
    {
        await gate.WaitAsync();
        try { return McpStatusLocked(); } finally { gate.Release(); }
    }

    private Task<string?> Publish(McpGateway.Catalog? catalog = null) =>
        Package?.Mcp is not null && record.WantRunning && record.GatewayEnabled
            ? gateway.Publish(Target(record), catalog) : Task.FromResult<string?>(null);

    public async Task<InstallRequest> UpdateRequest(InstallRequest request)
    {
        await gate.WaitAsync();
        try
        {
            if (record.Uninstalling) throw new InvalidOperationException($"Uninstall of {record.App.Name} is pending.");
            var package = Package ?? throw new InvalidOperationException($"{record.App.Name} is a local program without a NuGet version.");
            bool asProcess = PackageInstaller.WasProcessOptIn(package);
            return request with
            {
                Id = package.Id, Source = request.Source ?? package.Source,
                AsProcess = asProcess, Command = asProcess ? package.Command : null, Args = asProcess ? record.App.Args : [],
                SavedInputs = package.Mcp?.InputEnvironment
            };
        }
        finally { gate.Release(); }
    }

    public async Task PrepareUpdate(string version)
    {
        await gate.WaitAsync();
        try
        {
            if ((record.RetiredVersions ?? []).Contains(version, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Version {version} is still held by an MCP client. Close the binding before reinstalling that version.");
            Save(record with { DiscardVersion = version });
        }
        finally { gate.Release(); }
    }

    public async Task CleanUpdate(PackageInstaller installer, bool recovering = false)
    {
        await gate.WaitAsync();
        try
        {
            installer.RemoveStaging(record.Id);
            if (record.DiscardVersion is { } discard)
            {
                installer.RemovePackages(record.Id, discard);
                if (recovering) log.Write("host", $"Recovered interrupted update: keeping {Package!.Version}, discarding {discard}.");
                Save(record with { DiscardVersion = null });
            }
            CleanRetiredLocked(installer);
            recoveryError = null;
        }
        catch (Exception e) { recoveryError = e.Message; throw; }
        finally { gate.Release(); }
    }

    public async Task CleanRetired(PackageInstaller installer)
    {
        await gate.WaitAsync();
        try { if (!record.Uninstalling) CleanRetiredLocked(installer); }
        catch (Exception e) { recoveryError = e.Message; }
        finally { gate.Release(); }
    }

    private void CleanRetiredLocked(PackageInstaller installer)
    {
        foreach (string version in record.RetiredVersions ?? [])
        {
            if (gateway.InUse(record.Id, version)) continue;
            installer.RemovePackages(record.Id, version);
            Save(record with { RetiredVersions = record.RetiredVersions!.Where(v => v != version).ToArray() });
        }
    }

    public async Task<Reply> ApplyUpdate(Registration candidate)
    {
        await operations.WaitAsync();
        try
        {
            string oldVersion = Package!.Version;
            string newVersion = candidate.Package!.Version;
            bool oldOnConnection = Package.Mcp?.OnConnection == true;
            if (candidate.Package.Mcp?.OnConnection == true) return await ApplyOnConnectionUpdate(candidate, oldVersion);
            Task<Reply> ready;
            await gate.WaitAsync();
            try
            {
                if (!record.WantRunning)
                {
                    Activate(candidate, oldVersion);
                    return Reply.Data(Snapshot());
                }
                generation++;
                CancelRetry();
                await StopAttempt($"Update {oldVersion} to {newVersion}: stopping active version", graceful: true);
                log.Write("host", $"Starting candidate {newVersion}.");
                ready = Launch(candidate);
            }
            finally { gate.Release(); }

            var result = await ready;
            string failure;
            await gate.WaitAsync();
            try
            {
                if (result.Ok && current is { Provisional: true, Child: { } child } && !child.Exit.IsCompleted)
                {
                    try
                    {
                        Activate(candidate, oldVersion);
                        current.Provisional = false;
                        _ = Publish();
                        return Reply.Data(Snapshot());
                    }
                    catch (Exception e) when (record.Package!.Version == oldVersion)
                    {
                        result = new(false, $"Cannot commit the active version: {e.Message}");
                    }
                }
                string detail = result.Ok ? reason ?? "Process exited before the version could be committed." : result.Output;
                failure = $"Update to {newVersion} failed: {detail}";
                await StopAttempt(failure, graceful: false);
                log.Write("host", failure);
                Save(record with { Failures = 0, LastFailure = null, UpdateFailure = failure });
                ready = oldOnConnection ? Task.FromResult(new Reply(true, "The previous MCP registration is still active.")) : Launch();
            }
            finally { gate.Release(); }

            var rollback = await ready;
            string report = rollback.Ok ? $"{failure} Restored {oldVersion}." : $"{failure} Rollback to {oldVersion} failed: {rollback.Output}";
            await gate.WaitAsync();
            try
            {
                Save(record with { UpdateFailure = report });
                log.Write("host", report);
            }
            finally { gate.Release(); }
            return new(false, report);
        }
        finally { operations.Release(); }
    }

    private async Task<Reply> ApplyOnConnectionUpdate(Registration candidate, string oldVersion)
    {
        McpGateway.Catalog? catalog = null;
        var target = Target(candidate);
        if (record.WantRunning && record.GatewayEnabled)
        {
            try
            {
                if (gateway.NamespaceError(target) is { } error) throw new InvalidDataException(error);
                catalog = await McpGateway.Probe(target);
            }
            catch (Exception e)
            {
                string error = $"Update to {target.Package.Version} failed: {McpGateway.Describe(target, e)}. Active version: {oldVersion}.";
                await gate.WaitAsync();
                try { Save(record with { UpdateFailure = error }); }
                finally { gate.Release(); }
                return new(false, error);
            }
        }
        await gate.WaitAsync();
        try
        {
            generation++;
            CancelRetry();
            await StopAttempt($"Update {oldVersion} to {target.Package.Version}: stopping active version", graceful: true);
            void Commit()
            {
                Activate(candidate, oldVersion);
                state = record.WantRunning ? "registered" : "stopped";
            }
            if (catalog is null) Commit();
            else if (gateway.Switch(target, catalog, Commit) is { } error)
            {
                Save(record with { UpdateFailure = error });
                if (record.WantRunning && Package!.Mcp?.OnConnection != true) _ = Launch();
                return new(false, error);
            }
            return Reply.Data(Snapshot());
        }
        finally { gate.Release(); }
    }

    private void Activate(Registration candidate, string oldVersion)
    {
        bool retire = Package!.Mcp?.OnConnection == true;
        Save(record with
        {
            App = candidate.App, Package = candidate.Package, DiscardVersion = retire ? null : oldVersion,
            RetiredVersions = retire ? [.. record.RetiredVersions ?? [], oldVersion] : record.RetiredVersions,
            McpNamespace = record.McpNamespace ?? (candidate.Package?.Mcp is not null ? Target(candidate).Namespace : null),
            Failures = 0, LastFailure = null, UpdateFailure = null
        });
        if (!record.WantRunning) { state = "stopped"; reason = tooltip = null; }
    }

    public async Task Uninstall(PackageInstaller installer)
    {
        await operations.WaitAsync();
        await gate.WaitAsync();
        try
        {
            if (!record.Uninstalling)
                Save(record with { Uninstalling = true, WantRunning = false, Failures = 0, LastFailure = null });
            await gateway.Withdraw(record.Id);
            generation++;
            CancelRetry();
            await StopAttempt("uninstall", graceful: true);
            if (Package is not null)
            {
                installer.RemoveStaging(record.Id);
                installer.RemovePackages(record.Id);
            }
            log.Dispose();
            store.Remove(record.Id);
        }
        catch (Exception e) { recoveryError = e.Message; throw; }
        finally
        {
            if (record.Uninstalling) state = "uninstalling";
            gate.Release();
            operations.Release();
        }
    }

    public async Task<string?> ShutdownTarget()
    {
        await gate.WaitAsync();
        try { return current is not null || retry is not null || gateway.HasBindings(record.Id) ? record.App.Name : null; }
        finally { gate.Release(); }
    }

    public async Task<Reply> Run(string command, string? action = null, string? instance = null)
    {
        Task<Reply>? wait = null;
        await operations.WaitAsync();
        await gate.WaitAsync();
        try
        {
            if (suspended) throw new InvalidOperationException("Host is exiting.");
            if (record.Uninstalling) throw new InvalidOperationException($"Uninstall of {record.App.Name} is pending.");
            if (command == "tray")
            {
                bool? separate = action switch
                {
                    "on" => true,
                    "off" => false,
                    "default" => null,
                    _ => throw new ArgumentException("Expected tray on, off, or default.")
                };
                if (record.SeparateTray != separate) Save(record with { SeparateTray = separate });
                return Reply.Data(Snapshot());
            }
            if (Package?.Mcp is not null && record.McpNamespace is null) Save(record with { McpNamespace = Target(record).Namespace });
            if (command is "mcp-enable" or "mcp-disable")
            {
                if (Package?.Mcp is null) throw new InvalidOperationException($"{record.App.Name} has no MCP declaration.");
                Save(record with { GatewayEnabled = command == "mcp-enable" });
                if (!record.GatewayEnabled) await gateway.Withdraw(record.Id);
                else if (Package.Mcp.OnConnection || current is { Provisional: false } && state is "ready" or "running") _ = Publish();
                return Reply.Data(McpStatusLocked()!);
            }
            if (Package?.Mcp?.OnConnection == true)
            {
                if (command == "action") throw new InvalidOperationException("No resident process for this on-connection service.");
                if (command is "start" or "restart" or "stop")
                {
                    bool enabled = command != "stop";
                    if (record.WantRunning != enabled) Save(record with { WantRunning = enabled });
                    state = enabled ? "registered" : "stopped";
                    if (command is "restart" or "stop") await gateway.Withdraw(record.Id);
                }
                var discovery = Publish();
                if (command != "restore" && record.WantRunning && record.GatewayEnabled) wait = WaitForProbe(discovery);
            }
            else switch (command)
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
                        !ActionAvailable(action))
                        throw new InvalidOperationException("Action is undeclared, disabled, or offline.");
                    if (instance is not null && current!.Control.Instance != instance)
                        throw new InvalidOperationException("The application instance changed. Try the action again.");
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        if (!await current!.Control.Send(new { type = "action", id = action }, timeout.Token))
                            throw new InvalidOperationException("Application control channel is offline.");
                    break;
                default: throw new ArgumentException($"Unknown app command: {command}");
            }
            if (wait is null) return Reply.Data(Snapshot());
        }
        finally { gate.Release(); operations.Release(); }
        return await wait;
    }

    private async Task<Reply> WaitForProbe(Task<string?> discovery)
    {
        string? error = await discovery;
        return error is null ? Reply.Data(await Status()) : new(false, error);
    }

    private Task<Reply> Launch(Registration? candidate = null)
    {
        generation++;
        var launching = candidate ?? record;
        var run = new Attempt(launching.App, candidate is not null);
        current = run;
        state = "starting";
        reason = tooltip = null;
        enabled = new(StringComparer.Ordinal);
        try
        {
            log.Write("host", $"Starting instance {run.Control.Instance}.");
            _ = Observe(run.Control.Listen(message => Receive(run, message), log), run);
            run.Child = ChildProcess.Start(run.App, run.Control, log, launching.Package?.Mcp);
            _ = Observe(Monitor(run), run);
            if (run.App.Readiness == "notify") _ = Observe(ReadinessTimeout(run), run);
            else Ready(run, "running");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            End(run);
            run.Started.TrySetResult(new(false, e.Message));
            Failure(run, e.Message);
        }
        return run.Started.Task;
    }

    private void Ready(Attempt run, string next)
    {
        state = next;
        log.Write("host", $"{next} pid={run.Child!.Process.Id} instance={run.Control.Instance}");
        if (run.Provisional)
        {
            run.Started.TrySetResult(new(true, next));
            return;
        }
        _ = Publish();
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
                            if (!run.App.Actions.Contains(item.Name, StringComparer.Ordinal)) throw new InvalidDataException("Unknown action ID.");
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
            if (!run.Provisional) await gateway.Withdraw(record.Id);
            try { await FinishTree(child); }
            finally { End(run); }
            string detail = $"Process exited with code {code}.";
            run.Started.TrySetResult(new(false, detail));
            Failure(run, detail);
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
            Failure(run, detail);
        }
        finally { gate.Release(); }
    }

    private void Failure(Attempt run, string detail)
    {
        if (run.Provisional)
        {
            reason = detail;
            state = "failed";
            log.Write("host", detail);
            return;
        }
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
        if (!run.Provisional) await gateway.Withdraw(record.Id);
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
            await gateway.Withdraw(record.Id);
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
                    if (!run.Provisional) await gateway.Withdraw(record.Id);
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
