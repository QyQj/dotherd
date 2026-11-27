using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotHerd;

internal sealed record GatewayTarget(Guid Id, string Namespace, AppSpec App, InstalledPackage Package, AppLog Log)
{
    public McpDeclaration Declaration => Package.Mcp!;
}

internal static partial class McpInputs
{
    private static string? Read(string name) => Environment.GetEnvironmentVariable(name) ??
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public static string Expand(McpDeclaration? declaration, string value) =>
        declaration is null || declaration.InputEnvironment.Count == 0 ? value : Placeholder().Replace(value, match =>
        {
            if (!declaration.InputEnvironment.TryGetValue(match.Groups[1].Value, out var variable)) return match.Value;
            return Read(variable) is { Length: > 0 } input ? input : throw new ArgumentException($"Missing environment input: {variable}");
        });

    public static string Redact(McpDeclaration declaration, string message)
    {
        foreach (var secret in declaration.InputEnvironment.Values.Select(Read)
            .OfType<string>().Where(value => value.Length != 0).Distinct().OrderByDescending(value => value.Length))
            message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();
}

internal sealed class McpUpstream : IAsyncDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false,
        ConnectCallback = async (connection, ct) =>
        {
            var endpoint = connection.DnsEndPoint;
            var addresses = (await Dns.GetHostAddressesAsync(endpoint.Host, ct)).Where(IPAddress.IsLoopback).ToArray();
            if (addresses.Length == 0) throw new IOException("The MCP endpoint does not resolve to loopback.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(addresses, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly GatewayTarget target;
    private readonly SafeFileHandle? job;
    private readonly Process? process;
    private readonly AnonymousPipeServerStream? stdin, stdout, stderr;
    private readonly Task stderrPump = Task.CompletedTask;
    private McpClient? client;
    public McpClient Client => client!;

    private McpUpstream(GatewayTarget target) => this.target = target;

    private McpUpstream(GatewayTarget target, SafeFileHandle job, Process process, AnonymousPipeServerStream stdin,
        AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr) : this(target)
    {
        this.job = job;
        this.process = process;
        this.stdin = stdin;
        this.stdout = stdout;
        this.stderr = stderr;
        stderrPump = CaptureErrors();
    }

    public static async Task<McpUpstream> Connect(GatewayTarget target, CancellationToken ct)
    {
        var declaration = target.Declaration;
        McpUpstream upstream;
        IClientTransport transport;
        if (declaration.Transport == "stdio")
        {
            upstream = StartStdio(target);
            try { transport = new StreamClientTransport(serverInput: upstream.stdin!, serverOutput: upstream.stdout!); }
            catch { await upstream.DisposeAsync(); throw; }
        }
        else
        {
            string url = McpInputs.Expand(declaration, declaration.Url!);
            var endpoint = new Uri(url);
            if (!endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                throw new InvalidDataException("The expanded MCP endpoint must be a loopback HTTP URL.");
            var headers = declaration.Headers.ToDictionary(p => p.Key, p => McpInputs.Expand(declaration, p.Value),
                StringComparer.OrdinalIgnoreCase);
            if (headers.ContainsKey("Host") || headers.ContainsKey("Mcp-Session-Id"))
                throw new InvalidDataException("The MCP transport owns the Host and Mcp-Session-Id headers.");
            if (headers.Values.Any(value => value.IndexOfAny(['\r', '\n', '\0']) >= 0))
                throw new InvalidDataException("An MCP HTTP header contains a control character.");
            upstream = new(target);
            transport = new HttpClientTransport(new()
            {
                Endpoint = endpoint, Name = target.App.Name, TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers, EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0
            }, Http, ownsHttpClient: false);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                upstream.client = await McpClient.CreateAsync(transport, new() { ClientInfo = new() { Name = "DotHerd", Version = "1.0" } },
                    cancellationToken: timeout.Token);
                if (upstream.Client.ServerCapabilities.Tools is null) throw new InvalidDataException("The server did not declare the MCP tools capability.");
                return upstream;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("MCP negotiation did not complete in 30 seconds.");
            }
        }
        catch { await upstream.DisposeAsync(); throw; }
    }

    private static McpUpstream StartStdio(GatewayTarget target)
    {
        var declaration = target.Declaration;
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (!((string)variable.Key).StartsWith("DOTHERD_", StringComparison.OrdinalIgnoreCase))
                variables[(string)variable.Key] = (string)variable.Value!;
        foreach (var (key, value) in declaration.Environment) variables[key] = McpInputs.Expand(declaration, value);
        var args = declaration.Args.Select(arg => McpInputs.Expand(declaration, arg));
        string command = string.Join(' ', args.Prepend(target.App.Executable).Select(ChildProcess.Quote));
        string environment = string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";

        SafeFileHandle? job = null;
        Process? process = null;
        AnonymousPipeServerStream? stdin = null, stdout = null, stderr = null;
        try
        {
            job = WindowsJob.Create();
            stdin = new(PipeDirection.Out, HandleInheritability.Inheritable);
            stdout = new(PipeDirection.In, HandleInheritability.Inheritable);
            stderr = new(PipeDirection.In, HandleInheritability.Inheritable);
            process = WindowsJob.Start(job, target.App.Executable, command, target.App.Cwd, environment,
                [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()]);
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new(target, job, process, stdin, stdout, stderr);
        }
        catch { job?.Dispose(); process?.Dispose(); stdin?.Dispose(); stdout?.Dispose(); stderr?.Dispose(); throw; }
    }

    private async Task CaptureErrors()
    {
        using var reader = new StreamReader(stderr!);
        while (await reader.ReadLineAsync() is { } line)
            target.Log.Write("mcp stderr", McpInputs.Redact(target.Declaration, line));
    }

    public async ValueTask DisposeAsync()
    {
        job?.Dispose();
        try
        {
            if (client is not null) await client.DisposeAsync();
            if (process is not null) await process.WaitForExitAsync();
            await stderrPump;
        }
        finally { stdin?.Dispose(); stdout?.Dispose(); stderr?.Dispose(); process?.Dispose(); }
    }
}
