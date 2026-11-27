using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using DotHerd;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

Console.OutputEncoding = Wire.Utf8;
const string usage = """
    dotherd add <name> [--cwd <directory>] [--readiness process|notify] [--action <id>]... -- <executable> [args...]
    dotherd install <package>[@version] [--source <feed>] [--yes] [--input-env <input>=<variable>]
    dotherd install <package> --as-process --command <command> -- [args...]
    dotherd startup on|off|status
    dotherd start|stop|restart|logs <name>
    dotherd status [name]
    dotherd action <name> <id>
    dotherd host exit
    """;
try
{
    if (args is ["--help"] or ["-h"]) { Console.WriteLine(usage); return 0; }
    if (args is ["--version"]) { Console.WriteLine(Bootstrap.Version); return 0; }
    if (args is ["--cli-protocol"]) { Console.WriteLine(Bootstrap.Protocol); return 0; }
    bool yes = false;
    var request = args switch
    {
        [] => new Request("initialize"),
        ["install", _, ..] => ParseInstall(args, out yes),
        ["startup", "on" or "off" or "status"] => new Request("startup", Action: args[1]),
        ["add", _, ..] => ParseAdd(args),
        ["start" or "stop" or "restart" or "logs" or "status", var name] => new Request(args[0], name),
        ["status"] => new Request("status"),
        ["action", var name, var id] => new Request("action", name, Action: id),
        ["host", "exit"] => new Request("host-exit"),
        _ => throw new ArgumentException(usage)
    };
    if (await Bootstrap.Forward(args) is int forwarded) return forwarded;
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
    async Task<Reply> ReadReply() => JsonSerializer.Deserialize<Reply>(await reader.ReadLineAsync() ?? throw new IOException("Host disconnected."), Wire.Json)
        ?? throw new IOException("Missing host response.");
    var reply = await ReadReply();
    if (reply.Confirm)
    {
        Console.Error.WriteLine(reply.Output);
        Console.Error.WriteLine("Register this MCP service? [y/N] (or pass --yes)");
        var consent = yes || Console.ReadLine()?.Trim().ToLowerInvariant() is "yes" or "y";
        await writer.WriteLineAsync(consent ? "yes" : "no");
        reply = await ReadReply();
    }
    (reply.Ok ? Console.Out : Console.Error).WriteLine(reply.Output);
    return reply.Ok ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

static Request ParseInstall(string[] argv, out bool yes)
{
    string[] identity = argv[1].Split('@');
    if (identity.Length > 2 || identity.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Expected package ID or ID@exact-version.");
    string? source = null, command = null;
    bool process = false;
    yes = false;
    string[] extra = [];
    var inputs = new Dictionary<string,string>(StringComparer.Ordinal);
    for (int i = 2; i < argv.Length; i++)
    {
        string flag = argv[i];
        if (flag == "--") { extra = argv[(i + 1)..]; if (!process) throw new ArgumentException("Extra arguments require --as-process."); break; }
        string Value() => ++i < argv.Length ? argv[i] : throw new ArgumentException($"Missing value for {flag}.");
        switch (flag)
        {
            case "--source": source = Value(); break;
            case "--command": command = Value(); break;
            case "--as-process": process = true; break;
            case "--yes": yes = true; break;
            case "--input-env":
                var pair = Value().Split('=', 2);
                if (pair.Length != 2 || pair.Any(string.IsNullOrWhiteSpace) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(pair[1])))
                    throw new ArgumentException("--input-env expects INPUT=ENVIRONMENT_VARIABLE with a non-empty caller variable.");
                inputs.Add(pair[0], pair[1]); break;
            default: throw new ArgumentException($"Unknown install option: {flag}");
        }
    }
    if (process != (command != null)) throw new ArgumentException("--as-process and --command must be specified together.");
    return new("install", Install: new(identity[0], identity.ElementAtOrDefault(1), Environment.CurrentDirectory, source, process, command, extra, inputs));
}

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
