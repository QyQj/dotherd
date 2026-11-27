using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotHerd;

internal sealed partial class McpGateway
{
    internal sealed record Route(Tool Tool, Tool Original);
    internal sealed record Catalog(string Revision, Dictionary<string, Route> Routes);
    internal sealed class Service(GatewayTarget target)
    {
        public GatewayTarget Target { get; } = target;
        public CancellationTokenSource Lifetime { get; } = new();
        public Task<string?>? Discovery { get; set; }
        public Catalog? Catalog { get; set; }
        public string? Error { get; set; }
        public bool Reserved { get; set; }
    }

    internal sealed class Binding
    {
        public readonly Dictionary<Guid, Lease> Leases = [];
        public readonly Dictionary<RequestId, CancellationTokenSource> Requests = [];
        public bool Closed;
    }

    internal sealed class Lease
    {
        public Service Service { get; }
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationToken Token { get; }
        public Task<McpUpstream> Connection = null!;
        public Task? Closing;
        public int Calls;
        public long LastUsed = Environment.TickCount64;

        public Lease(Service service)
        {
            Service = service;
            Token = Lifetime.Token;
        }
    }

    private readonly object sync = new();
    private readonly Dictionary<Guid, Service> services = [];
    private readonly Dictionary<string, Binding> httpBindings = new(StringComparer.Ordinal);
    private readonly HashSet<Lease> leases = [];
    private readonly Dictionary<Service, Task> retiring = [];
    private string? stdioError;
    public event Action<Guid>? VersionReleased;
    public string? StdioError => Volatile.Read(ref stdioError);

    public void RecordStdioError(Exception error) =>
        Volatile.Write(ref stdioError, $"MCP stdio connection failed ({error.GetType().Name}).");

    public bool IsAvailable(Guid id)
    {
        lock (sync) return services.GetValueOrDefault(id)?.Catalog is not null;
    }

    public bool HasBindings(Guid id)
    {
        lock (sync) return leases.Any(lease => lease.Service.Target.Id == id);
    }

    public string? NamespaceError(GatewayTarget target)
    {
        lock (sync)
        {
            if (!NamespacePattern().IsMatch(target.Namespace) || target.Namespace.Contains("__", StringComparison.Ordinal))
                return $"Invalid MCP namespace '{target.Namespace}' for {target.App.Name}.";
            if (services.Values.FirstOrDefault(other => other.Reserved && other.Target.Id != target.Id && other.Target.Namespace == target.Namespace) is { } conflict)
                return $"MCP namespace '{target.Namespace}' for {target.App.Name} conflicts with {conflict.Target.App.Name}.";
            return null;
        }
    }

    public static string DefaultNamespace(string name)
    {
        string value = WordBoundary().Replace(name, "_");
        return NonWord().Replace(value, "_").Trim('_').ToLowerInvariant();
    }

    public object Status(GatewayTarget target, bool enabled)
    {
        lock (sync)
        {
            services.TryGetValue(target.Id, out var service);
            return new
            {
                target.App.Name, target.Package.Version, target.Namespace, enabled, transport = target.Declaration.Transport,
                onConnection = target.Declaration.OnConnection,
                state = service?.Catalog != null ? "available" : service?.Error != null ? "error" : service != null ? "discovering" : "offline",
                revision = service?.Catalog?.Revision, error = service?.Error,
                tools = service?.Catalog?.Routes.Keys.Order(StringComparer.Ordinal).ToArray() ?? []
            };
        }
    }

    public Task<string?> Publish(GatewayTarget target, Catalog? catalog = null)
    {
        lock (sync)
        {
            if (services.TryGetValue(target.Id, out var old))
            {
                if (old.Target == target && catalog is null && old.Error is null) return old.Discovery!;
                _ = Retire(old);
            }
            var service = new Service(target) { Catalog = catalog, Error = NamespaceError(target) };
            service.Reserved = service.Error is null;
            services[target.Id] = service;
            if (service.Error is not null) service.Catalog = null;
            return service.Discovery = service.Error is not null || catalog is not null
                ? Task.FromResult(service.Error)
                : Task.Run(() => Discover(service));
        }
    }

    public string? Switch(GatewayTarget target, Catalog catalog, Action commit)
    {
        lock (sync)
        {
            if (NamespaceError(target) is { } error) return error;
            commit();
            _ = Publish(target, catalog);
            return null;
        }
    }

    private async Task<string?> Discover(Service service)
    {
        try
        {
            var catalog = await Probe(service.Target, service.Lifetime.Token);
            lock (sync)
                if (services.GetValueOrDefault(service.Target.Id) == service) service.Catalog = catalog;
            return null;
        }
        catch (OperationCanceledException) when (service.Lifetime.IsCancellationRequested) { return "MCP discovery was cancelled."; }
        catch (Exception e)
        {
            string error = Describe(service.Target, e);
            lock (sync)
                if (services.GetValueOrDefault(service.Target.Id) == service) service.Error = error;
            return error;
        }
    }

    public static async Task<Catalog> Probe(GatewayTarget target, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var upstream = await McpUpstream.Connect(target, deadline.Token);
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
            do
            {
                var page = await upstream.Client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, deadline.Token);
                foreach (var tool in page.Tools)
                {
                    string name = target.Namespace + "__" + tool.Name;
                    if (!ToolPattern().IsMatch(tool.Name) || !ToolPattern().IsMatch(name))
                        throw new InvalidDataException($"Invalid MCP tool name '{tool.Name}' or public name '{name}'.");
                    var published = new Tool
                    {
                        Name = name, Title = tool.Title, Description = tool.Description, InputSchema = tool.InputSchema,
                        OutputSchema = tool.OutputSchema, Annotations = tool.Annotations, Icons = tool.Icons, Meta = tool.Meta
                    };
                    if (!routes.TryAdd(name, new(published, tool)))
                        throw new InvalidDataException($"Duplicate MCP tool name '{tool.Name}'.");
                }
                cursor = page.NextCursor;
                if (cursor is not null && !cursors.Add(cursor)) throw new InvalidDataException("MCP tools/list repeated a pagination cursor.");
            } while (cursor is not null);
            return new(upstream.Client.NegotiatedProtocolVersion!, routes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("MCP discovery did not complete in 30 seconds."); }
    }

    private Task Retire(Service service)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await service.Lifetime.CancelAsync();
                if (service.Discovery is not null) await service.Discovery;
            }
            finally
            {
                service.Lifetime.Dispose();
                lock (sync) retiring.Remove(service);
                VersionReleased?.Invoke(service.Target.Id);
            }
        });
        retiring.Add(service, task);
        return task;
    }

    public async Task Withdraw(Guid id)
    {
        Task[] closing;
        lock (sync)
        {
            var tasks = leases.Where(lease => lease.Service.Target.Id == id).Select(CloseLocked).ToList();
            tasks.AddRange(retiring.Where(entry => entry.Key.Target.Id == id).Select(entry => entry.Value));
            if (services.Remove(id, out var service)) tasks.Add(Retire(service));
            closing = tasks.ToArray();
        }
        await Task.WhenAll(closing);
    }

    public bool InUse(Guid id, string version)
    {
        lock (sync) return leases.Any(lease => lease.Service.Target.Id == id && lease.Service.Target.Package.Version == version) ||
            retiring.Keys.Any(service => service.Target.Id == id && service.Target.Package.Version == version);
    }

    public Binding HttpBinding(string client)
    {
        lock (sync)
        {
            if (!httpBindings.TryGetValue(client, out var binding)) httpBindings.Add(client, binding = new());
            return binding;
        }
    }

    public Task Close(Binding binding)
    {
        lock (sync)
        {
            binding.Closed = true;
            foreach (var request in binding.Requests.Values.ToArray()) request.Cancel();
            var closing = binding.Leases.Values.Select(CloseLocked).ToArray();
            binding.Leases.Clear();
            return Task.WhenAll(closing);
        }
    }

    public Task ReapIdleHttpBindings()
    {
        lock (sync)
        {
            long now = Environment.TickCount64;
            var closing = new List<Task>();
            foreach (var binding in httpBindings.Values)
                foreach (var (id, lease) in binding.Leases.ToArray())
                    if (lease.Calls == 0 && now - lease.LastUsed >= TimeSpan.FromMinutes(2).TotalMilliseconds)
                    {
                        binding.Leases.Remove(id);
                        closing.Add(CloseLocked(lease));
                    }
            return Task.WhenAll(closing);
        }
    }

    private Task CloseLocked(Lease lease) => lease.Closing ??= Task.Run(async () =>
    {
        var target = lease.Service.Target;
        try
        {
            await lease.Lifetime.CancelAsync();
            McpUpstream upstream;
            try { upstream = await lease.Connection; }
            catch (Exception) when (lease.Connection.IsFaulted || lease.Connection.IsCanceled) { return; }
            await upstream.DisposeAsync();
        }
        catch (Exception e)
        {
            lock (sync)
                if (services.GetValueOrDefault(target.Id) == lease.Service) lease.Service.Error = Describe(target, e);
        }
        finally
        {
            lock (sync) leases.Remove(lease);
            lease.Lifetime.Dispose();
            VersionReleased?.Invoke(target.Id);
        }
    });

    public ListToolsResult ListTools()
    {
        lock (sync) return new()
        {
            Tools = services.Values.Where(s => s.Catalog is not null).SelectMany(s => s.Catalog!.Routes.Values)
                .Select(r => r.Tool).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray()
        };
    }

    public async ValueTask<CallToolResult> Call(Binding binding, RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var request = context.Params ?? throw new McpProtocolException("Missing tool parameters.", McpErrorCode.InvalidParams);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new McpProtocolException("Missing tool name.", McpErrorCode.InvalidParams);
        var requestId = context.JsonRpcRequest.Id;
        using var cancellation = new CancellationTokenSource();
        Lease lease;
        Route route;
        lock (sync)
        {
            if (binding.Closed) throw new McpProtocolException("The MCP client binding is closed.");
            if (binding.Requests.ContainsKey(requestId))
                throw new McpProtocolException("The MCP client already has a call with this request ID.", McpErrorCode.InvalidRequest);
            var service = services.Values.FirstOrDefault(s => s.Catalog?.Routes.ContainsKey(request.Name) == true ||
                binding.Leases.TryGetValue(s.Target.Id, out var pinned) && pinned.Closing is null &&
                pinned.Service.Catalog?.Routes.ContainsKey(request.Name) == true);
            if (service is null) throw new McpProtocolException($"Unknown or offline MCP tool: {request.Name}", McpErrorCode.InvalidParams);
            var id = service.Target.Id;
            if (!binding.Leases.TryGetValue(id, out lease!) || lease.Closing is not null)
            {
                lease = new(service);
                var token = lease.Token;
                lease.Connection = Task.Run(async () =>
                {
                    var upstream = await McpUpstream.Connect(service.Target, token);
                    try
                    {
                        upstream.Client.AddKnownTools(service.Catalog!.Routes.Values.Select(entry => entry.Original));
                        return upstream;
                    }
                    catch { await upstream.DisposeAsync(); throw; }
                });
                binding.Leases[id] = lease;
                leases.Add(lease);
            }
            if (!lease.Service.Catalog!.Routes.TryGetValue(request.Name, out route!))
                throw new McpProtocolException($"{service.Target.App.Name}: this tool is unavailable on the bound version.", McpErrorCode.InvalidParams);
            lease.Calls++;
            binding.Requests.Add(requestId, cancellation);
        }

        var target = lease.Service.Target;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token, cancellation.Token);
        try
        {
            var upstream = await lease.Connection.WaitAsync(linked.Token);
            var originalProgress = request.ProgressToken;
            string? token = originalProgress is null ? null : Guid.NewGuid().ToString("N");
            var upstreamProgress = token is null ? (ProgressToken?)null : new ProgressToken(token);
            await using var notifications = upstreamProgress is null ? null :
                upstream.Client.RegisterNotificationHandler("notifications/progress", async (notification, _) =>
                {
                    if (linked.IsCancellationRequested) return;
                    var value = notification.Params?.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions);
                    if (value?.ProgressToken == upstreamProgress)
                        await context.Server.NotifyProgressAsync(originalProgress!.Value, value.Progress, cancellationToken: linked.Token);
                });

            var result = await upstream.Client.CallToolAsync(new CallToolRequestParams
            {
                Name = route.Original.Name, Arguments = request.Arguments,
                Meta = token is null ? null : new() { ["progressToken"] = token }
            }, linked.Token);
            lock (sync)
                if (services.GetValueOrDefault(target.Id) == lease.Service) lease.Service.Error = null;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw new McpProtocolException($"{target.App.Name}: request cancelled."); }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        { throw new McpProtocolException($"{target.App.Name}: the service binding was withdrawn."); }
        catch (McpProtocolException e) when (lease.Connection.IsCompletedSuccessfully)
        {
            string error = Describe(target, e);
            lock (sync)
                if (services.GetValueOrDefault(target.Id) == lease.Service) lease.Service.Error = error;
            throw new McpProtocolException(error, e.ErrorCode);
        }
        catch (Exception e)
        {
            string error = Describe(target, e);
            lock (sync)
            {
                if (services.GetValueOrDefault(target.Id) == lease.Service) lease.Service.Error = error;
                _ = CloseLocked(lease);
            }
            throw new McpProtocolException(error);
        }
        finally
        {
            lock (sync)
            {
                lease.Calls--;
                lease.LastUsed = Environment.TickCount64;
                binding.Requests.Remove(requestId);
            }
        }
    }

    public ValueTask Cancel(Binding binding, JsonRpcNotification notification)
    {
        var request = notification.Params?.Deserialize<CancelledNotificationParams>(McpJsonUtilities.DefaultOptions);
        if (request is not null)
            lock (sync)
                if (binding.Requests.TryGetValue(request.RequestId, out var cancellation)) cancellation.Cancel();
        return ValueTask.CompletedTask;
    }

    public async Task ServeStdio(Stream stream, CancellationToken ct)
    {
        var binding = new Binding();
        try
        {
            await using var server = McpServer.Create(new StreamServerTransport(stream, stream), new()
            {
                ServerInfo = new() { Name = "DotHerd", Version = "1.0" },
                Handlers = new()
                {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(ListTools()),
                    CallToolHandler = (context, cancellation) => Call(binding, context, cancellation)
                }
            });
            await server.RunAsync(ct);
        }
        finally { await Close(binding); }
    }

    public static string Describe(GatewayTarget target, Exception error) =>
        $"{target.App.Name}: {McpInputs.Redact(target.Declaration, error.Message)}";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_]*\z")]
    private static partial Regex NamespacePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,128}\z")]
    private static partial Regex ToolPattern();
    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundary();
    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonWord();
}
