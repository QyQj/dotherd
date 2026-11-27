using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace DotHerd;

internal sealed class McpHttpGateway(Profile profile, McpGateway gateway) : IAsyncDisposable
{
    private readonly string portFile = Path.Combine(profile.Root, "mcp.port");
    private readonly string clientsFile = Path.Combine(profile.Root, "mcp.clients");
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private Dictionary<string, string> clients = new(StringComparer.Ordinal);
    private WebApplication? app;
    private Task? reap;
    private int port;
    private string? error;
    private string Authority => $"127.0.0.1:{port}";
    private string Url => $"http://{Authority}/mcp";

    public object Status => new { url = port == 0 ? null : Url, error };

    public async Task Initialize()
    {
        try
        {
            if (File.Exists(portFile) && (!int.TryParse(File.ReadAllText(portFile), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535))
                throw new InvalidDataException("The persisted MCP port is invalid.");
            if (File.Exists(clientsFile))
                clients = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    ProtectedData.Unprotect(File.ReadAllBytes(clientsFile), null, DataProtectionScope.CurrentUser), Wire.Json)
                    ?? throw new InvalidDataException("The MCP client credential store is invalid.");

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = profile.Root });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Listen(IPAddress.Loopback, port);
            });
            builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "DotHerd", Version = "1.0" })
                .WithHttpTransport(options =>
                {
                    options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
                    options.ConfigureSessionOptions = (context, session, _) =>
                    {
                        var binding = gateway.HttpBinding(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                        session.Handlers = new()
                        {
                            ListToolsHandler = (_, _) => ValueTask.FromResult(gateway.ListTools()),
                            CallToolHandler = (request, ct) => gateway.Call(binding, request, ct),
                            NotificationHandlers = [new("notifications/cancelled", (notification, _) => gateway.Cancel(binding, notification))]
                        };
                        return Task.CompletedTask;
                    };
                });
            app = builder.Build();
            app.Use(async (context, next) =>
            {
                var request = context.Request;
                if (!string.Equals(request.Host.Value, Authority, StringComparison.OrdinalIgnoreCase) ||
                    request.Headers.Origin.Count > 0 && (request.Headers.Origin.Count != 1 ||
                        !string.Equals(request.Headers.Origin[0], $"http://{Authority}", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                string? client = Authenticate(request);
                if (client is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, client)], "Bearer"));
                await next(context);
            });
            app.MapMcp("/mcp");
            await app.StartAsync();
            if (port == 0)
            {
                port = new Uri(app.Urls.Single()).Port;
                SaveFile(portFile, Encoding.UTF8.GetBytes(port.ToString(CultureInfo.InvariantCulture)));
            }
            reap = Reap();
        }
        catch (Exception e)
        {
            error = $"MCP HTTP could not listen on loopback{(port == 0 ? "" : $" at {Authority}")}: {e.Message}";
            if (app is not null)
            {
                await app.DisposeAsync();
                app = null;
            }
        }
    }

    public Reply Config(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Use --client <name> with 1–64 ASCII letters, digits, hyphens or underscores.");
        if (error is not null) return new(false, error);
        lock (sync)
        {
            if (!clients.TryGetValue(name, out var token))
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var next = new Dictionary<string, string>(clients, StringComparer.Ordinal) { [name] = token };
                SaveFile(clientsFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(next, Wire.Json), null, DataProtectionScope.CurrentUser));
                clients = next;
            }
            return Reply.Data(new { mcpServers = new { dotherd = new { url = Url, headers = new { Authorization = $"Bearer {token}" } } } });
        }
    }

    private string? Authenticate(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1 || request.Headers.Authorization[0] is not { } header || header.Length != 71 ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var supplied = Encoding.UTF8.GetBytes(header[7..]);
        lock (sync)
        {
            string? owner = null;
            foreach (var (name, token) in clients)
                if (CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(token))) owner = name;
            return owner;
        }
    }

    private async Task Reap()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await gateway.ReapIdleHttpBindings();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static void SaveFile(string path, byte[] data)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(data);
            file.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (reap is not null) await reap;
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        lifetime.Dispose();
    }
}
