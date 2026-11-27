using Microsoft.Win32;

namespace DotHerd;

internal sealed class LoginStartup(Profile profile)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly object sync = new();

    public Reply Configure(string mode)
    {
        lock (sync)
        {
            bool custom = !Path.GetFullPath(profile.Root).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotHerd"), StringComparison.OrdinalIgnoreCase);
            var name = custom ? "DotHerd-" + profile.Pipe.Split('-').Last() : "DotHerd";
            string host = Environment.ProcessPath!;
            string command = "\"" + host + "\"" + (custom ? " --home \"" + Path.TrimEndingDirectorySeparator(profile.Root) + "\"" : "");
            var marker = Path.Combine(profile.Root, "startup.setting");
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            string? existing = run.GetValue(name) as string;
            if (mode == "initialize" && File.Exists(marker)) mode = "status";
            switch (mode)
            {
                case "initialize":
                case "on":
                    if (command.Length > 260) throw new IOException("The persistent host command exceeds the Windows Run limit (260 characters). Install in a shorter path.");
                    if (existing != null && !existing.Equals(command, StringComparison.OrdinalIgnoreCase) && mode == "initialize")
                        throw new IOException($"A different login entry already owns {name}.");
                    run.SetValue(name, command, RegistryValueKind.String);
                    File.WriteAllText(marker, "on");
                    break;
                case "off":
                    run.DeleteValue(name, false);
                    File.WriteAllText(marker, "off");
                    break;
                case "status": break;
                default: throw new ArgumentException("Expected startup on, off, or status.");
            }
            return Reply.Data(new { enabled = run.GetValue(name) is string, valueName = name, command = run.GetValue(name) });
        }
    }
}
