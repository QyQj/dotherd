using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

if (args is ["--version"]) { Console.WriteLine("0.0.1"); return 0; }
if (args is ["--cli-protocol"]) { Console.WriteLine("1"); return 0; }
if (args is ["mcp" or "bridge", ..]) return await FixtureMcp.Stdio(args);
if (args is [var listen] && int.TryParse(listen, out int port)) return await FixtureMcp.Http(port);
Console.OutputEncoding = new UTF8Encoding(false);
Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
Console.WriteLine(JsonSerializer.Serialize(args));
if (args is ["fail"]) return 23;
if (args is ["next"] && File.Exists("reject-old")) return 24;
string Env(string name) => Environment.GetEnvironmentVariable(name)!;
using var pipe = new NamedPipeClientStream(".", Env("DOTHERD_CONTROL_PIPE"), PipeDirection.InOut);
pipe.Connect(5000);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
writer.WriteLine(JsonSerializer.Serialize(new { type = "hello", version = 1, instance = Env("DOTHERD_INSTANCE"), token = Env("DOTHERD_CONTROL_TOKEN") }));
if (args is ["paused-ready"])
{
    File.WriteAllText("candidate-started", Environment.ProcessId.ToString());
    while (!File.Exists("allow-ready")) Thread.Sleep(50);
}
writer.WriteLine("""{"type":"ready"}""");
while (reader.ReadLine() is { } line)
    if (JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() == "shutdown")
    {
        if (File.Exists("hold-shutdown"))
        {
            File.WriteAllText("shutdown-received", "");
            while (File.Exists("hold-shutdown")) Thread.Sleep(50);
        }
        break;
    }
return 0;
