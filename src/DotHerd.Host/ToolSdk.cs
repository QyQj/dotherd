using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace DotHerd;

public static class ToolSdk
{
    public static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    public static ProcessStartInfo Command(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<string> Output(ProcessStartInfo info)
    {
        info.RedirectStandardError = info.RedirectStandardOutput = true;
        info.CreateNoWindow = true;
        using var process = Process.Start(info) ?? throw new IOException($"Could not launch {info.FileName}.");
        return await Output(info.FileName, process, process.StandardOutput, process.StandardError);
    }

    internal static async Task<string> HostedOutput(ProcessStartInfo info)
    {
        using var job = WindowsJob.Create();
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        string command = string.Join(' ', info.ArgumentList.Prepend(info.FileName).Select(ChildProcess.Quote));
        string environment = string.Join('\0', info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value)) + "\0\0";
        using var process = WindowsJob.Start(job, info.FileName, command, info.WorkingDirectory, environment,
            [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()]);
        stdin.Dispose();
        stdout.DisposeLocalCopyOfClientHandle();
        stderr.DisposeLocalCopyOfClientHandle();
        using var output = new StreamReader(stdout, Encoding.UTF8);
        using var error = new StreamReader(stderr, Encoding.UTF8);
        return await Output(info.FileName, process, output, error);
    }

    private static async Task<string> Output(string fileName, Process process, StreamReader outputReader, StreamReader errorReader)
    {
        var stdout = outputReader.ReadToEndAsync();
        var stderr = errorReader.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {process.ExitCode}: {output} {error}");
        return output;
    }
}
