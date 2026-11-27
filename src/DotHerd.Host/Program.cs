using System.IO.Pipes;
using System.Text.Json;
using DotHerd;
using System.Collections.Concurrent;

if (args is ["--home", var root]) Environment.SetEnvironmentVariable("DOTHERD_HOME", root);
else if (args.Length != 0) throw new ArgumentException("Expected --home <profile>.");
var profile = Profile.Open();
using var mutex = new Mutex(false, @"Local\" + profile.Pipe, out bool first);
if (!first) return;
var store = new Store(profile.Root);
var installer = new PackageInstaller(profile.Root);
var gateway = new McpGateway();
await using var httpGateway = new McpHttpGateway(profile, gateway);
var login = new LoginStartup(profile);
var packageGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
using var closing = new CancellationTokenSource();
var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int packageOperations = 0;
var apps = new Dictionary<string, ManagedApp>(StringComparer.OrdinalIgnoreCase);
object registry = new();
using var stop = new CancellationTokenSource();
string? loadError = null;
bool exiting = false;
gateway.VersionReleased += id =>
{
    ManagedApp? owner;
    lock (registry) owner = apps.Values.FirstOrDefault(app => app.Id == id);
    if (owner is not null) _ = owner.CleanRetired(installer);
};
try
{
    foreach (var record in store.Load()) apps.Add(record.App.Name, new(store, record, gateway));
    foreach (var (name, app) in apps.ToArray())
    {
        try
        {
            if (app.Uninstalling)
            {
                await app.Uninstall(installer);
                apps.Remove(name);
            }
            else if (app.Package is not null) await app.CleanUpdate(installer, recovering: true);
        }
        catch (Exception) when (app.RecoveryError is not null) { /* Keep the failed operation visible and retryable. */ }
    }
    await Task.WhenAll(apps.Values.Where(app => !app.Uninstalling).Select(app => app.Run("restore")));
}
catch (Exception e) { loadError = $"Cannot restore profile {profile.Root}: {e.Message}"; }
await httpGateway.Initialize();
using var desktop = Environment.UserInteractive ? new DesktopSession(profile) : null;

while (!stop.IsCancellationRequested)
{
    var pipe = Wire.Server(profile.Pipe);
    try { await pipe.WaitForConnectionAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { pipe.Dispose(); break; }
    _ = Serve(pipe);
}

async Task Serve(NamedPipeServerStream pipe)
{
    using (pipe)
    {
        bool quit = false;
        bool mcpStream = false;
        try
        {
            using var reader = Wire.Reader(pipe);
            using var writer = Wire.Writer(pipe);
            Reply reply;
            try
            {
                var request = JsonSerializer.Deserialize<Request>(await reader.ReadLineAsync() ?? throw new IOException("Empty request."), Wire.Json)
                    ?? throw new InvalidDataException("Missing request.");
                async Task<bool> Confirm(string preview)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new Reply(true, preview, Confirm: true), Wire.Json));
                    return await reader.ReadLineAsync(closing.Token) == "yes";
                }
                if (request.Command == "mcp-connect" && loadError is null && !closing.IsCancellationRequested)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new Reply(true, ""), Wire.Json));
                    mcpStream = true;
                    await gateway.ServeStdio(pipe, closing.Token);
                    return;
                }
                if (request.Command is "host-exit-preview" or "disable")
                {
                    ManagedApp[] all;
                    lock (registry) all = apps.Values.ToArray();
                    string?[] confirmed = [];
                    if (request.Command == "host-exit-preview")
                    {
                        confirmed = await Task.WhenAll(all.Select(a => a.ShutdownTarget()));
                        var names = string.Join(", ", confirmed.OfType<string>().Order());
                        if (!await Confirm($"Exit DotHerd and stop this session's applications? Affected applications: {(names.Length == 0 ? "(none)" : names)}."))
                            throw new InvalidOperationException("Host exit was not confirmed.");
                    }
                    lock (registry)
                    {
                        if (exiting) throw new InvalidOperationException("Host is already exiting.");
                        exiting = true;
                        all = apps.Values.ToArray();
                    }
                    try
                    {
                        if (request.Command == "disable") login.Configure("off");
                        else
                        {
                            var affected = await Task.WhenAll(all.Select(a => a.ShutdownTarget()));
                            if (affected.OfType<string>().Except(confirmed.OfType<string>(), StringComparer.OrdinalIgnoreCase).Any())
                                throw new InvalidOperationException("The affected applications changed. Run host exit again to review them.");
                        }
                    }
                    catch { lock (registry) exiting = false; throw; }
                    quit = true;
                    closing.Cancel();
                    lock (registry) if (packageOperations == 0) drained.TrySetResult();
                    await drained.Task;
                    await Task.WhenAll(all.Select(a => a.Suspend()));
                    reply = new(true, request.Command == "disable" ? "Login startup disabled. Host stopped." : "Host stopped.",
                        ExitingPid: Environment.ProcessId);
                }
                else if (loadError is not null) reply = new(false, loadError);
                else if (request.Command is "install" or "update" or "uninstall")
                {
                    lock (registry)
                    {
                        if (exiting) throw new InvalidOperationException("Host is exiting.");
                        packageOperations++;
                    }
                    try { reply = await ChangePackage(request, Confirm); }
                    finally { lock (registry) { if (--packageOperations == 0 && closing.IsCancellationRequested) drained.TrySetResult(); } }
                }
                else reply = await Dispatch(request);
            }
            catch (IOException) when (mcpStream) { return; }
            catch (OperationCanceledException) when (mcpStream && closing.IsCancellationRequested) { return; }
            catch (Exception e) when (mcpStream) { gateway.RecordStdioError(e); return; }
            catch (Exception e) { reply = new(false, e.Message); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Wire.Json));
        }
        catch (IOException) { /* A CLI can disconnect after submitting its request. */ }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        finally { if (quit) stop.Cancel(); }
    }
}

async Task<Reply> Dispatch(Request request)
{
    if (request.Command == "show")
    {
        Reply initialized;
        lock (registry)
        {
            if (exiting) throw new InvalidOperationException("Host is exiting.");
            initialized = login.Configure("initialize");
        }
        if (desktop is null) throw new InvalidOperationException("The host was started outside an interactive Windows session.");
        await desktop.Show();
        return initialized;
    }
    ManagedApp? app = null;
    ManagedApp[]? all = null;
    lock (registry)
    {
        if (exiting) throw new InvalidOperationException("Host is exiting.");
        if (request.Command is "initialize" or "startup") return login.Configure(request.Action ?? "initialize");
        if (request.Command == "add")
        {
            var spec = request.App ?? throw new ArgumentException("Missing local program.");
            spec.Validate();
            if (!Directory.Exists(spec.Cwd) || !File.Exists(spec.Executable)) throw new FileNotFoundException("Working directory or executable no longer exists.");
            if (apps.ContainsKey(spec.Name)) throw new InvalidOperationException($"Already registered: {spec.Name}");
            var record = new Registration(Guid.NewGuid(), spec, false, 0);
            app = new(store, record, gateway);
            store.Save(record);
            apps.Add(spec.Name, app);
        }
        else if (request is { Command: "status" or "mcp-list", Name: null }) all = apps.Values.ToArray();
        else if (request.Command == "mcp-config-http") return httpGateway.Config(request.Name);
        else if (request.Name is null || !apps.TryGetValue(request.Name, out app))
            throw new KeyNotFoundException($"Unknown application: {request.Name}");
    }
    if (all is not null)
    {
        if (request.Command == "mcp-list")
            return Reply.Data(new { http = httpGateway.Status, gateway.StdioError, services = (await Task.WhenAll(all.Select(a => a.McpStatus()))).OfType<object>() });
        return Reply.Data(await Task.WhenAll(all.Select(a => a.Status())));
    }
    return request.Command switch
    {
        "add" or "status" => Reply.Data(await app!.Status()),
        "logs" => new(true, app!.Logs()),
        "start" or "stop" or "restart" or "action" or "tray" or "mcp-enable" or "mcp-disable" =>
            await app!.Run(request.Command, request.Action, request.Instance),
        "mcp-list" => Reply.Data(await app!.McpStatus() ?? throw new InvalidOperationException($"{request.Name} has no MCP declaration.")),
        _ => throw new ArgumentException($"Unknown command: {request.Command}")
    };
}

async Task<Reply> ChangePackage(Request request, Func<string,Task<bool>> confirm)
{
    var install = request.Install;
    string name = install?.Id ?? request.Name ?? throw new ArgumentException("Application name is required.");
    var exact = install is null ? null : PackageFeed.ValidateIdentity(name, install.Version);
    var gate = packageGates.GetOrAdd(name, _ => new(1));
    await gate.WaitAsync(closing.Token);
    try
    {
        ManagedApp? existing;
        lock (registry)
        {
            if (exiting) throw new InvalidOperationException("Host is exiting.");
            apps.TryGetValue(name, out existing);
        }
        if (request.Command == "uninstall")
        {
            if (existing is null) throw new KeyNotFoundException($"Unknown application: {name}");
            await existing.Uninstall(installer);
            lock (registry) apps.Remove(name);
            return new(true, $"Uninstalled {name}. Data and logs retained.");
        }
        if (install is null) throw new ArgumentException("Missing package request.");
        if (request.Command == "update")
        {
            if (existing is null) throw new KeyNotFoundException($"Unknown application: {name}");
            var options = await existing.UpdateRequest(install);
            await existing.CleanUpdate(installer);
            Reply result;
            try
            {
                result = await installer.Install(options, existing.Id, existing.Package, confirm, existing.ApplyUpdate, existing.PrepareUpdate);
            }
            catch (Exception e) { result = new(false, e.Message); }
            try { await existing.CleanUpdate(installer); }
            catch (Exception e)
            {
                string outcome = result.Ok ? $"Active version: {name}@{existing.Package!.Version}." : result.Output;
                return new(false, $"{outcome}{Environment.NewLine}Package cleanup pending: {e.Message}");
            }
            return result;
        }
        login.Configure("initialize");
        if (existing != null)
        {
            if (existing.Uninstalling) throw new InvalidOperationException($"Uninstall of {name} is pending.");
            var package = existing.Package ?? throw new InvalidOperationException($"A local application already uses the name {name}.");
            if (exact != null && exact != NuGet.Versioning.NuGetVersion.Parse(package.Version))
                throw new InvalidOperationException($"Installed {package.Id}@{package.Version}. Use dotherd update {name}@{exact} to change versions.");
            return Reply.Data(await existing.Status());
        }
        return await installer.Install(install, Guid.NewGuid(), null, confirm, record =>
        {
            lock (registry)
            {
                if (exiting || apps.ContainsKey(name)) throw new InvalidOperationException($"Cannot register {name}: host exiting or name in use.");
                var app = new ManagedApp(store, record, gateway);
                store.Save(record);
                apps.Add(name, app);
                return app.Run("start");
            }
        });
    }
    finally { gate.Release(); }
}
