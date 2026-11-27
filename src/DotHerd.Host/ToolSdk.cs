using System.Diagnostics;
using System.Runtime.InteropServices;

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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(info.FileName)} exited {process.ExitCode}: {output} {error}");
        return output;
    }
}
