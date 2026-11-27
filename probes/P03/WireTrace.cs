using System.Threading.Channels;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

sealed class WireTrace(IClientTransport inner, string revision) : IClientTransport
{
    public string Name => inner.Name;
    public string? FirstMethod { get; private set; }
    public bool ProgressRequested { get; private set; }
    public int CancellationsSent;
    private bool initialized;
    private int modernRequests;
    public async Task<ITransport> ConnectAsync(CancellationToken ct = default)
        => new Connection(await inner.ConnectAsync(ct), this);

    private void Observe(JsonRpcMessage message)
    {
        if (message is JsonRpcNotification { Method: "notifications/initialized" }) initialized = true;
        if (message is JsonRpcNotification { Method: "notifications/cancelled" }) Interlocked.Increment(ref CancellationsSent);
        if (message is not JsonRpcRequest request) return;
        FirstMethod ??= request.Method;
        var meta = request.Params?["_meta"];
        if (meta?["progressToken"] is not null) ProgressRequested = true;
        if (revision == "2026-07-28")
        {
            var info = meta?["io.modelcontextprotocol/clientInfo"];
            if ((string?)meta?["io.modelcontextprotocol/protocolVersion"] != revision ||
                (string?)info?["name"] != "DotHerd.P03" || (string?)info?["version"] != "1.0.0" ||
                meta?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                throw new InvalidDataException($"Missing request metadata on {request.Method}.");
            Interlocked.Increment(ref modernRequests);
        }
    }

    public void CheckHandshake()
    {
        bool modern = revision == "2026-07-28";
        if (FirstMethod != (modern ? "server/discover" : "initialize") || initialized == modern || !ProgressRequested)
            throw new InvalidDataException("Unexpected handshake or missing progress token.");
        Console.WriteLine(modern ? $"validated request metadata on {modernRequests} requests" : "initialize + notifications/initialized observed");
    }

    private sealed class Connection(ITransport transport, WireTrace trace) : ITransport
    {
        public string? SessionId => transport.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => transport.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
        {
            trace.Observe(message);
            return transport.SendMessageAsync(message, ct);
        }
        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }
}
