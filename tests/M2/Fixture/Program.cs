using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

if (args is ["--version"]) { Console.WriteLine("0.0.1"); return 0; }
if (args is ["--cli-protocol"]) { Console.WriteLine("1"); return 0; }
Console.OutputEncoding = new UTF8Encoding(false);
Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
Console.WriteLine(JsonSerializer.Serialize(args));
if (args is ["fail"]) return 23;
string Env(string name) => Environment.GetEnvironmentVariable(name)!;
using var pipe = new NamedPipeClientStream(".", Env("DOTHERD_CONTROL_PIPE"), PipeDirection.InOut);
pipe.Connect(5000);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
writer.WriteLine(JsonSerializer.Serialize(new { type = "hello", version = 1, instance = Env("DOTHERD_INSTANCE"), token = Env("DOTHERD_CONTROL_TOKEN") }));
writer.WriteLine("""{"type":"ready"}""");
while (reader.ReadLine() is { } line)
    if (JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() == "shutdown") break;
return 0;
