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
var login = new LoginStartup(profile);
var installGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
using var closing = new CancellationTokenSource();
var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int installations = 0;
var apps = new Dictionary<string, ManagedApp>(StringComparer.OrdinalIgnoreCase);
object registry = new();
using var stop = new CancellationTokenSource();
string? loadError = null;
bool exiting = false;
try
{
    foreach (var record in store.Load()) apps.Add(record.App.Name, new(store, record));
    await Task.WhenAll(apps.Values.Select(app => app.Run("restore")));
}
catch (Exception e) { loadError = $"Cannot restore profile {profile.Root}: {e.Message}"; }

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
        try
        {
            using var reader = Wire.Reader(pipe);
            using var writer = Wire.Writer(pipe);
            Reply reply;
            try
            {
                var request = JsonSerializer.Deserialize<Request>(await reader.ReadLineAsync() ?? throw new IOException("Empty request."), Wire.Json)
                    ?? throw new InvalidDataException("Missing request.");
                if (request.Command == "host-exit")
                {
                    ManagedApp[] all;
                    lock (registry)
                    {
                        if (exiting) throw new InvalidOperationException("Host is already exiting.");
                        exiting = true;
                        if (installations == 0) drained.TrySetResult();
                        all = apps.Values.ToArray();
                    }
                    quit = true;
                    closing.Cancel();
                    await drained.Task;
                    await Task.WhenAll(all.Select(a => a.Suspend()));
                    reply = new(true, "Host stopped.");
                }
                else if (loadError is not null) reply = new(false, loadError);
                else if (request.Command == "install" && request.Install is { } install)
                {
                    lock (registry)
                    {
                        if (exiting) throw new InvalidOperationException("Host is exiting.");
                        installations++;
                    }
                    try { reply = await Install(install, async preview =>
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new Reply(true, preview, Confirm: true), Wire.Json));
                        return await reader.ReadLineAsync(closing.Token) == "yes";
                    }); }
                    finally { lock (registry) { if (--installations == 0 && exiting) drained.TrySetResult(); } }
                }
                else reply = await Dispatch(request);
            }
            catch (Exception e) { reply = new(false, e.Message); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Wire.Json));
        }
        catch (IOException) { /* A CLI can disconnect after submitting its request. */ }
        finally { if (quit) stop.Cancel(); }
    }
}

async Task<Reply> Dispatch(Request request)
{
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
            app = new(store, record);
            store.Save(record);
            apps.Add(spec.Name, app);
        }
        else if (request is { Command: "status", Name: null }) all = apps.Values.ToArray();
        else if (request.Name is null || !apps.TryGetValue(request.Name, out app))
            throw new KeyNotFoundException($"Unknown application: {request.Name}");
    }
    if (all is not null) return Reply.Data(await Task.WhenAll(all.Select(a => a.Status())));
    return request.Command switch
    {
        "add" or "status" => Reply.Data(await app!.Status()),
        "logs" => new(true, app!.Logs()),
        "start" or "stop" or "restart" or "action" => await app!.Run(request.Command, request.Action),
        _ => throw new ArgumentException($"Unknown command: {request.Command}")
    };
}

async Task<Reply> Install(InstallRequest install, Func<string,Task<bool>> confirm)
{
    var exact = PackageFeed.ValidateIdentity(install.Id, install.Version);
    login.Configure("initialize");
    var gate = installGates.GetOrAdd(install.Id, _ => new(1));
    await gate.WaitAsync(closing.Token);
    try
    {
        ManagedApp? existing;
        lock (registry) apps.TryGetValue(install.Id, out existing);
        if (existing != null)
        {
            var package = existing.Package ?? throw new InvalidOperationException($"A local application already uses the name {install.Id}.");
            if (exact != null && exact != NuGet.Versioning.NuGetVersion.Parse(package.Version))
                throw new InvalidOperationException($"Installed {package.Id}@{package.Version}. Use dotherd update {install.Id}@{exact} to change versions.");
            return Reply.Data(await existing.Status());
        }
        return await installer.Install(install, confirm, record =>
        {
            lock (registry)
            {
                if (exiting || apps.ContainsKey(install.Id)) throw new InvalidOperationException($"Cannot register {install.Id}: host exiting or name in use.");
                var app = new ManagedApp(store, record);
                store.Save(record);
                apps.Add(install.Id, app);
                return app.Run("start");
            }
        });
    }
    finally { gate.Release(); }
}
