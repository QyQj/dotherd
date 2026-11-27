using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using DotHerd;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

Console.OutputEncoding = Wire.Utf8;
const string usage = """
    dotherd add <name> [--cwd <directory>] [--readiness process|notify] [--action <id>]... -- <executable> [args...]
    dotherd start|stop|restart|logs <name>
    dotherd status [name]
    dotherd action <name> <id>
    dotherd host exit
    """;
try
{
    if (args is [] or ["--help"] or ["-h"]) { Console.WriteLine(usage); return 0; }
    if (args is ["--version"]) { Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]); return 0; }
    var request = args switch
    {
        ["add", _, ..] => ParseAdd(args),
        ["start" or "stop" or "restart" or "logs" or "status", var name] => new Request(args[0], name),
        ["status"] => new Request("status"),
        ["action", var name, var id] => new Request("action", name, Action: id),
        ["host", "exit"] => new Request("host-exit"),
        _ => throw new ArgumentException(usage)
    };
    var profile = Profile.Open();
    using var pipe = new NamedPipeClientStream(".", profile.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try { await pipe.ConnectAsync(150); }
    catch (TimeoutException)
    {
        if (request.Command == "host-exit") { Console.WriteLine("Host is offline."); return 0; }
        using var candidate = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DotHerd.Host.exe")) { UseShellExecute = true });
        await pipe.ConnectAsync(10000);
    }
    using var writer = Wire.Writer(pipe);
    using var reader = Wire.Reader(pipe);
    await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json));
    var reply = JsonSerializer.Deserialize<Reply>(await reader.ReadLineAsync() ?? throw new IOException("Host disconnected."), Wire.Json)
        ?? throw new IOException("Missing host response.");
    (reply.Ok ? Console.Out : Console.Error).WriteLine(reply.Output);
    return reply.Ok ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

static Request ParseAdd(string[] args)
{
    string cwd = Environment.CurrentDirectory, readiness = "process";
    var actions = new List<string>();
    int i = 2;
    while (i < args.Length && args[i] != "--")
    {
        string flag = args[i++];
        if (i == args.Length) throw new ArgumentException($"Missing value for {flag}.");
        string value = args[i++];
        switch (flag)
        {
            case "--cwd": cwd = Path.GetFullPath(value); break;
            case "--readiness": readiness = value; break;
            case "--action": actions.Add(value); break;
            default: throw new ArgumentException($"Unknown add option: {flag}");
        }
    }
    if (++i >= args.Length) throw new ArgumentException("Expected -- <executable> [args...].");
    if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
    string command = args[i];
    ArgumentException.ThrowIfNullOrWhiteSpace(command);
    var dirs = new List<string> { cwd };
    if (Path.GetFileName(command) == command)
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"')).Where(Path.IsPathFullyQualified));
    foreach (string dir in dirs)
    {
        string path = Path.GetFullPath(command, dir);
        if (!File.Exists(path) && !Path.HasExtension(path)) path += ".exe";
        if (!File.Exists(path)) continue;
        var spec = new AppSpec(args[1], cwd, path, args[(i + 1)..], readiness, actions.ToArray());
        spec.Validate();
        return new Request("add", spec.Name, spec);
    }
    throw new FileNotFoundException($"Executable not found: {command}");
}
