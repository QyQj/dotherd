using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

if (args.Length is < 2 or > 3 || args[0] is not ("start" or "status") ||
    !Guid.TryParseExact(args[1], "N", out var id) ||
    (args.Length == 3 && (args[0] != "start" || args[2] != "--sync-cold")))
{
    Console.Error.WriteLine("Usage: dotherd-p02 start|status <probe-guid> [--sync-cold]");
    return 2;
}

string pipeName = Probe.Pipe(id);
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.CurrentUserOnly);
try
{
    try
    {
        pipe.Connect(args[0] == "start" ? 150 : 10000);
    }
    catch (TimeoutException) when (args[0] == "start")
    {
        // The verifier releases both first calls only after each has observed the empty pipe.
        if (args.Length == 3)
        {
            using var ready = Semaphore.OpenExisting(pipeName + ".ready");
            using var gate = EventWaitHandle.OpenExisting(pipeName + ".go");
            ready.Release();
            if (!gate.WaitOne(10000)) throw new TimeoutException("Both cold calls did not reach the barrier.");
        }

        string host = Path.Combine(AppContext.BaseDirectory, "P02Host.exe");
        using var candidate = Process.Start(new ProcessStartInfo(host) { UseShellExecute = true, ArgumentList = { "host", args[1] } });
        pipe.Connect(10000);
    }

    using var reader = new StreamReader(pipe);
    var reply = JsonNode.Parse(reader.ReadLine() ?? throw new EndOfStreamException("Host closed the pipe."))!.AsObject();
    reply["cliPid"] = Environment.ProcessId;
    reply["cliConsole"] = Probe.HasConsole;
    Console.WriteLine(reply.ToJsonString());
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
