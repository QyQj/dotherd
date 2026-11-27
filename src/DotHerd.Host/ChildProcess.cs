using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotHerd;

internal sealed class ChildProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly AnonymousPipeServerStream stdout, stderr;
    public Process Process { get; }
    public Task<int> Exit { get; }
    public Task Streams { get; }

    private ChildProcess(SafeFileHandle job, Process process, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr, AppLog log)
    {
        this.job = job;
        this.stdout = stdout;
        this.stderr = stderr;
        Process = process;
        Exit = ReadExit();
        Streams = Task.WhenAll(Capture(stdout, "stdout"), Capture(stderr, "stderr"));
        async Task Capture(Stream stream, string name)
        {
            try { await log.Capture(stream, name); }
            catch { KillTree(); throw; }
        }
    }

    private async Task<int> ReadExit() { await Process.WaitForExitAsync(); return Process.ExitCode; }
    public void KillTree() => job.Dispose();

    public static ChildProcess Start(AppSpec app, ControlChannel channel, AppLog log)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
            variables[(string)pair.Key] = (string)pair.Value!;
        variables["DOTHERD_CONTROL_PIPE"] = channel.Pipe;
        variables["DOTHERD_CONTROL_TOKEN"] = channel.Token;
        variables["DOTHERD_INSTANCE"] = channel.Instance;
        string environment = string.Join('\0', variables.Select(p => p.Key + "=" + p.Value)) + "\0\0";
        string command = string.Join(' ', app.Args.Prepend(app.Executable).Select(Quote));

        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        AnonymousPipeServerStream? stderr = null;
        SafeFileHandle? job = null;
        Process? process = null;
        try
        {
            stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            job = WindowsJob.Create();
            process = WindowsJob.Start(job, app.Executable, command, app.Cwd, environment,
                [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()]);
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new ChildProcess(job, process, stdout, stderr, log);
        }
        catch { job?.Dispose(); process?.Dispose(); stdout.Dispose(); stderr?.Dispose(); throw; }
    }

    // Encode one argv element for the Windows C runtime, including empty strings and trailing backslashes.
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose() { job.Dispose(); stdout.Dispose(); stderr.Dispose(); Process.Dispose(); }
}
