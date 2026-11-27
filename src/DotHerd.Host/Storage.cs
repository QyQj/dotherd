using System.Text;
using System.Text.Json;

namespace DotHerd;

internal sealed record Registration(Guid Id, AppSpec App, bool WantRunning, int Failures, string? LastFailure = null, InstalledPackage? Package = null);

internal sealed class Store(string root)
{
    private readonly string apps = Directory.CreateDirectory(Path.Combine(root, "apps")).FullName;
    public string Logs { get; } = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;

    public IEnumerable<Registration> Load()
    {
        foreach (var path in Directory.EnumerateFiles(apps, "*.json"))
        {
            var record = JsonSerializer.Deserialize<Registration>(File.ReadAllText(path), Wire.Json);
            if (record is null || record.App is null || record.Id == Guid.Empty ||
                Path.GetFileNameWithoutExtension(path) != record.Id.ToString("N") || record.Failures is < 0 or > 5)
                throw new InvalidDataException($"Invalid registration: {path}");
            record.App.Validate();
            yield return record;
        }
    }

    public void Save(Registration record)
    {
        string path = Path.Combine(apps, $"{record.Id:N}.json");
        string temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, record, Wire.Json);
            file.Flush(true);
        }
        File.Move(temp, path, true);
    }
}

internal sealed class AppLog(string directory, Guid id) : IDisposable
{
    private readonly string path = Path.Combine(directory, $"{id:N}.log");
    private readonly object sync = new();
    private FileStream? file;

    public void Write(string source, string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} [{source}] {message}{Environment.NewLine}");
        lock (sync)
        {
            file ??= Open();
            if (file.Position > 0 && file.Position + bytes.Length > 1024 * 1024)
            {
                file.Dispose();
                file = null;
                File.Move(path, path + ".1", true);
                file = Open();
            }
            file.Write(bytes);
            if (source == "host") file.Flush();
        }
    }

    private FileStream Open() => new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    public string Read()
    {
        lock (sync)
        {
            file?.Flush();
            return string.Concat(new[] { path + ".1", path }.Where(File.Exists).Select(p =>
            {
                using var reader = new StreamReader(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                return reader.ReadToEnd();
            }));
        }
    }

    public async Task Capture(Stream stream, string source)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
        var line = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { Write(source, line.ToString().TrimEnd('\r')); line.Clear(); }
                else
                {
                    line.Append(buffer[i]);
                    if (line.Length == 4096) { Write(source, line.ToString()); line.Clear(); }
                }
            }
        }
        if (line.Length != 0) Write(source, line.ToString());
    }

    public void Dispose() { lock (sync) { file?.Dispose(); file = null; } }
}
