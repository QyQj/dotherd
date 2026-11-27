using System.IO.Pipes;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
string Env(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing {key}.");
using var pipe = new NamedPipeClientStream(".", Env("DOTHERD_CONTROL_PIPE"), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await pipe.ConnectAsync(10000);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
Task Send(object message) => writer.WriteLineAsync(JsonSerializer.Serialize(message));

await Send(new { type = "hello", version = 1, instance = Env("DOTHERD_INSTANCE"), token = Env("DOTHERD_CONTROL_TOKEN") });
Console.WriteLine($"args={JsonSerializer.Serialize(args)}");
Console.Error.WriteLine($"cwd={Environment.CurrentDirectory}");
await Send(new { type = "ready" });
await Send(new { type = "tray.update", tooltip = "Waiting for an action", enabled = new { open = true } });
while (await reader.ReadLineAsync() is { } line)
{
    using var message = JsonDocument.Parse(line);
    switch (message.RootElement.GetProperty("type").GetString())
    {
        case "action":
            Console.WriteLine($"action={message.RootElement.GetProperty("id").GetString()}");
            break;
        case "shutdown":
            Console.WriteLine("shutdown");
            return;
    }
}
