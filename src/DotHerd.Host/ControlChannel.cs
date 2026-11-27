using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotHerd;

internal sealed class ControlChannel : IDisposable
{
    public string Instance { get; } = Guid.NewGuid().ToString("N");
    public string Pipe => "DotHerd-app-" + Instance;
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource closed = new();
    private readonly SemaphoreSlim writes = new(1);
    private StreamWriter? peer;
    public bool Connected => Volatile.Read(ref peer) is not null;

    public async Task Listen(Func<JsonElement, Task> receive, AppLog log)
    {
        while (!closed.IsCancellationRequested)
        {
            using var pipe = Wire.Server(Pipe);
            try
            {
                await pipe.WaitForConnectionAsync(closed.Token);
                using var reader = Wire.Reader(pipe);
                using var writer = Wire.Writer(pipe);
                using var hello = JsonDocument.Parse(await reader.ReadLineAsync(closed.Token) ?? throw new IOException("Missing hello."));
                var h = hello.RootElement;
                if (h.GetProperty("type").GetString() != "hello" || h.GetProperty("version").GetInt32() != 1 ||
                    h.GetProperty("instance").GetString() != Instance || h.GetProperty("token").GetString() != Token)
                    throw new InvalidDataException("Control authentication failed.");
                Volatile.Write(ref peer, writer);
                while (await reader.ReadLineAsync(closed.Token) is { } line)
                {
                    using var message = JsonDocument.Parse(line);
                    await receive(message.RootElement);
                }
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException)
            {
                if (!closed.IsCancellationRequested) log.Write("control", $"Rejected or disconnected peer ({e.GetType().Name}).");
            }
            finally { Volatile.Write(ref peer, null); }
        }
    }

    public async Task<bool> Send(object payload, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            var writer = Volatile.Read(ref peer);
            if (writer is null) return false;
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload, Wire.Json).AsMemory(), token);
            return true;
        }
        finally { writes.Release(); }
    }
    public void Dispose() => closed.Cancel();
}
