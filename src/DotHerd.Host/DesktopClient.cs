using System.IO.Pipes;
using System.Text.Json;

namespace DotHerd;

internal sealed record DesktopStatus(Guid Id, string Name, string State, string? Instance, int? Pid, bool WantRunning, int Failures,
    string? Reason, string? LastFailure, string? UpdateFailure, string? RecoveryError, string? Tooltip,
    InstalledPackage? Package, bool SeparateTray, Dictionary<string, bool> Actions, DesktopMcpStatus? Mcp)
{
    public string DisplayName => Package?.DisplayName ?? Name;
    public bool Failed => Failures > 0 || State == "failed" || UpdateFailure is not null || RecoveryError is not null || Mcp?.Error is not null;
    public string? Activate => Package?.Tray?.Text("onActivate") ?? (Package is null && Actions.ContainsKey("open") ? "open" : null);

    public string Label(string id) =>
        Package?.Tray?["actions"]?.AsArray().FirstOrDefault(item => item?.AsObject().Text("id") == id)?.AsObject().Text("label") ?? id;

    public bool CanInvoke(string id) => Instance is not null && Actions.GetValueOrDefault(id);
}

internal sealed record DesktopMcpStatus(string Transport, bool OnConnection, string State, bool Enabled,
    string? Revision, string? Error, string[] Tools);

internal sealed class DesktopClient(Profile profile)
{
    public async Task<Reply> Send(Request request, Func<string, Task<bool>>? confirm = null, CancellationToken cancellation = default)
    {
        using var pipe = new NamedPipeClientStream(".", profile.Pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000, cancellation);
        using var writer = Wire.Writer(pipe);
        using var reader = Wire.Reader(pipe);

        async Task<Reply> Read() => JsonSerializer.Deserialize<Reply>(
            await reader.ReadLineAsync(cancellation) ?? throw new IOException("The host disconnected."), Wire.Json)
            ?? throw new InvalidDataException("Missing host response.");

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), cancellation);
        var reply = await Read();
        if (reply.Confirm)
        {
            bool accepted = confirm is not null && await confirm(reply.Output);
            await writer.WriteLineAsync((accepted ? "yes" : "no").AsMemory(), cancellation);
            reply = await Read();
        }
        if (!reply.Ok) throw new InvalidOperationException(reply.Output);
        return reply;
    }

    public async Task<T> Read<T>(Request request, CancellationToken cancellation = default) =>
        JsonSerializer.Deserialize<T>((await Send(request, cancellation: cancellation)).Output, Wire.Json)
        ?? throw new InvalidDataException("Missing host data.");
}
