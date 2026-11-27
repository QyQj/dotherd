using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out var id))
    return 2;

string exe = Environment.ProcessPath!;
switch (args[0])
{
    case "host":
        using (var mutex = new Mutex(true, @"Local\" + Probe.Pipe(id), out bool first))
        {
            if (!first) return 0;
            using var job = WindowsJob.Create();
            int appPid = WindowsJob.StartApp(job, exe, id);
            var state = new { hostPid = Environment.ProcessId, appPid, hostConsole = Probe.HasConsole, hostPath = exe };
            string response = JsonSerializer.Serialize(state);
            while (true)
            {
                using var pipe = new NamedPipeServerStream(Probe.Pipe(id), PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(response);
            }
        }
    case "app":
        using (var worker = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "worker", args[1] } })
            ?? throw new InvalidOperationException("Worker did not start."))
        {
            worker.WaitForExit();
            return worker.ExitCode;
        }
    case "worker":
        Thread.Sleep(Timeout.Infinite);
        return 0;
    default:
        return 2;
}

public static partial class Probe
{
    public static string Pipe(Guid id) => $"DotHerd.P02.{id:N}";
    public static bool HasConsole
    {
        get
        {
            if (GetConsoleProcessList(out _, 1) != 0) return true;
            int error = Marshal.GetLastPInvokeError();
            if (error == 6) return false; // ERROR_INVALID_HANDLE when detached.
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(out uint firstProcessId, uint capacity);
}
