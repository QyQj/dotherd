#nullable enable
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class FixtureMcp
{
    public static async Task<int> Stdio(string[] args)
    {
        string label = args.ElementAtOrDefault(1) ?? args[0];
        await using var server = McpServer.Create(new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()), Options(label));
        await server.RunAsync();
        return 0;
    }

    public static async Task<int> Http(int port)
    {
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var fixture = Options("http");
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = fixture.ServerInfo;
            options.Handlers = fixture.Handlers;
        }).WithHttpTransport();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var expected = Environment.GetEnvironmentVariable("M2_FIXTURE_VALUE");
            if (expected is not null && context.Request.Headers.Authorization != expected)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.RunAsync();
        return 0;
    }

    private static McpServerOptions Options(string label)
    {
        return new()
        {
            ServerInfo = new() { Name = "Fixture", Version = "1" },
            ProtocolVersion = label == "legacy" ? "2025-11-25" : null,
            Handlers = new()
            {
                ListToolsHandler = (context, _) =>
                {
                    bool second = context.Params?.Cursor == "second";
                    return ValueTask.FromResult(new ListToolsResult
                    {
                        NextCursor = second ? null : "second",
                        Tools = [new Tool
                        {
                            Name = second ? (label == "next" ? "new-extra" : "extra") : label == "invalid" ? "invalid tool" : "list",
                            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"mode":{"type":"string"},"marker":{"type":"string"}}}"""),
                            OutputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"label":{"type":"string"}}}""")
                        }]
                    });
                },
                CallToolHandler = async (context, ct) =>
                {
                    var request = context.Params!;
                    string? Arg(string name) => request.Arguments?.TryGetValue(name, out var value) == true ? value.GetString() : null;
                    string? mode = Arg("mode");
                    string? marker = Arg("marker");
                    if (request.ProgressToken is { } progress)
                        await context.Server.NotifyProgressAsync(progress, new() { Progress = 1, Total = 2, Message = label }, cancellationToken: ct);
                    if (mode == "wait")
                    {
                        File.WriteAllText($"entered-{marker}", "");
                        try
                        {
                            while (!File.Exists($"release-{marker}")) await Task.Delay(40, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            File.WriteAllText($"canceled-{marker}", "");
                            throw;
                        }
                    }
                    if (mode == "disconnect")
                    {
                        File.AppendAllText($"effect-{marker}", "one\n");
                        Environment.Exit(31);
                    }
                    var result = new { label, pid = Environment.ProcessId, name = request.Name,
                        negotiated = context.Server.NegotiatedProtocolVersion };
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
                        StructuredContent = JsonSerializer.SerializeToElement(result),
                        IsError = mode == "tool-error"
                    };
                }
            }
        };
    }
}
