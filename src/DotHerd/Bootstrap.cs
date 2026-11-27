using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using DotHerd;

internal static class Bootstrap
{
    public const string Protocol = "5";
    public static string Version => typeof(Bootstrap).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static async Task<int?> Forward(string[] args)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ".nupkg.metadata"))) dir = dir.Parent;
        if (dir?.Parent?.Parent is not { } cache || dir.FullName.Split(Path.DirectorySeparatorChar).Contains(".store", StringComparer.OrdinalIgnoreCase))
            return null;

        var user = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cli = Path.Combine(user, ".dotnet", "tools", "dotherd.exe");
        using (var identity = WindowsIdentity.GetCurrent())
        using (var mutex = new Mutex(false, @"Local\DotHerd-global-install-" + identity.User!.Value))
        {
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { /* Recheck the installed shim after an interrupted bootstrap. */ }
            try
            {
                if (!File.Exists(cli))
                {
                    var install = ToolSdk.Command(ToolSdk.Dotnet, ["tool","install","--global","DotHerd","--version",$"[{Version}]","--source",cache.FullName]);
                    ToolSdk.Output(install).GetAwaiter().GetResult();
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        string installed = await ToolSdk.Output(ToolSdk.Command(cli, ["--version"]));
        string upgrade = $"dnx DotHerd {Version} cannot forward to global DotHerd {installed}. Run dotnet tool update --global DotHerd --version {Version}.";
        string protocol;
        try { protocol = await ToolSdk.Output(ToolSdk.Command(cli, ["--cli-protocol"])); }
        catch (InvalidOperationException e) { throw new InvalidOperationException(upgrade, e); }
        if (protocol != Protocol) throw new InvalidOperationException(upgrade);
        using var child = Process.Start(ToolSdk.Command(cli, args)) ?? throw new IOException($"Could not forward to {cli}.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
}
