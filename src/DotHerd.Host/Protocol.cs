using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace DotHerd;

public sealed record AppSpec(string Name, string Cwd, string Executable, string[] Args, string Readiness, string[] Actions)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!Path.IsPathFullyQualified(Cwd) || !Path.IsPathFullyQualified(Executable))
            throw new InvalidDataException("Working directory and executable must be absolute.");
        if (Readiness is not ("process" or "notify") || Args is null || Actions is null ||
            Args.Any(a => a is null || a.Contains('\0')) ||
            Actions.Any(string.IsNullOrWhiteSpace) || Actions.Distinct(StringComparer.Ordinal).Count() != Actions.Length)
            throw new InvalidDataException("Invalid readiness, arguments, or action IDs.");
    }
}

public sealed record Request(string Command, string? Name = null, AppSpec? App = null, string? Action = null);
public sealed record Reply(bool Ok, string Output)
{
    public static Reply Data(object value) => new(true, JsonSerializer.Serialize(value, Wire.Json));
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static StreamReader Reader(Stream stream) => new(stream, Utf8, false, 4096, true);
    public static StreamWriter Writer(Stream stream) => new(stream, Utf8, 4096, true) { AutoFlush = true };
    public static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

public sealed record Profile(string Root, string Pipe)
{
    public static Profile Open()
    {
        string root = Environment.GetEnvironmentVariable("DOTHERD_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotHerd");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("DOTHERD_HOME must be absolute.");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "profile.id");
        if (!File.Exists(path))
        {
            string unique = Guid.NewGuid().ToString("N");
            string temp = Path.Combine(root, unique + ".tmp");
            try
            {
                File.WriteAllText(temp, unique);
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { /* A concurrent CLI initialized this profile. */ }
            }
            finally { File.Delete(temp); }
        }
        var id = Guid.ParseExact(File.ReadAllText(path), "N");
        using var identity = WindowsIdentity.GetCurrent();
        return new(root, $"DotHerd-{identity.User!.Value}-{id:N}");
    }
}
