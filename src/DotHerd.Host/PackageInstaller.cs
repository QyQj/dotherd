using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotHerd;

internal sealed class PackageInstaller(string root)
{
    public async Task<Reply> Install(InstallRequest request, Guid id, InstalledPackage? active, Func<string,Task<bool>> confirm,
        Func<Registration,Task<Reply>> register, Func<string,Task>? prepare = null)
    {
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        var feed = await PackageFeed.Resolve(request, cache);
        string version = feed.Version.ToNormalizedString();
        if (active is not null && (feed.Version == NuGetVersion.Parse(active.Version) ||
            request.Version is null && feed.Version < NuGetVersion.Parse(active.Version)))
            return new(true, $"{active.Id}@{active.Version} is already installed.");

        string staging = OwnedPath("staging", id);
        string install = OwnedPath("packages", id, version);
        string data = Path.Combine(root, "data", id.ToString("N"));
        Directory.CreateDirectory(staging);
        bool registered = false;
        try
        {
            string snapshot = Directory.CreateDirectory(Path.Combine(staging, "source")).FullName;
            string archive = await feed.Download(request.Id, snapshot, cache);
            var plan = ToolManifest.Read(archive, request, feed.Version);
            if (plan.Command == null)
            {
                if (active is not null) throw new InvalidDataException($"{request.Id}@{version} no longer declares a managed entry.");
                return new(true, $"{request.Id}@{version}: {string.Join(", ", plan.Commands)}. No resident entry declared. To start one, use --as-process --command <name> -- [arguments].");
            }
            if (plan.Confirm)
            {
                var preview = JsonSerializer.Serialize(new { package = request.Id, version, source = feed.Origin, plan.Command, plan.Mcp }, Wire.Json);
                if (!await confirm(preview)) return new(false, "MCP registration was not confirmed.");
            }
            if (plan.RidPackage != null) await feed.Download(plan.RidPackage, snapshot, cache);
            if (prepare is not null) await prepare(version);
            string config = Path.Combine(staging, "NuGet.Config");
            new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "selected"), new XAttribute("value", snapshot))))).Save(config);
            var sdk = ToolSdk.Command(ToolSdk.Dotnet, ["tool", "install", request.Id, "--version", $"[{version}]",
                "--tool-path", install, "--configfile", config]);
            sdk.WorkingDirectory = staging;
            sdk.Environment["NUGET_PACKAGES"] = Path.Combine(staging, "cache");
            await ToolSdk.HostedOutput(sdk);
            string executable = ResolveEntry(install, plan.Command);
            Directory.CreateDirectory(data);
            var spec = new AppSpec(request.Id, data, executable, plan.Args, plan.Readiness, plan.Actions);
            spec.Validate();
            var package = new InstalledPackage(request.Id, version, feed.Origin, plan.Command, install, plan.DisplayName, plan.Tray,
                plan.Mcp, request.AsProcess);
            var startup = register(new(id, spec, true, 0, Package: package));
            registered = true;
            return await startup;
        }
        finally
        {
            if (active is null)
            {
                DeleteOwned("staging", id);
                if (!registered)
                {
                    DeleteOwned("packages", id);
                    if (Directory.Exists(data)) Directory.Delete(data);
                }
            }
        }
    }

    public void RemovePackages(Guid id, string? version = null) => DeleteOwned("packages", id, version);

    public void RemoveStaging(Guid id) => DeleteOwned("staging", id);

    public static bool WasProcessOptIn(InstalledPackage package)
    {
        if (package.AsProcess is bool saved) return saved;
        if (package.Mcp is not null) return false;
        // M2 installations predate the opt-in flag. The installed top-level archive preserves the declaration.
        var archive = Directory.EnumerateFiles(Path.Combine(package.Directory, ".store"), "*.nupkg", SearchOption.AllDirectories)
            .SingleOrDefault(path => Path.GetFileName(path).Equals($"{package.Id}.{package.Version}.nupkg", StringComparison.OrdinalIgnoreCase));
        if (archive is null) throw new InvalidDataException($"Cannot verify the original installation mode of {package.Id}@{package.Version}.");
        using var zip = ZipFile.OpenRead(archive);
        return zip.GetEntry(".dotherd/app.json") is null;
    }

    private string OwnedPath(string area, Guid id, string? version = null)
    {
        string parent = Path.Combine(root, area);
        string app = Path.Combine(parent, id.ToString("N"));
        if (version is not null && (!NuGetVersion.TryParse(version, out var parsed) || parsed.ToNormalizedString() != version))
            throw new InvalidDataException($"Invalid managed version directory: {version}");
        string path = version is null ? app : Path.Combine(app, version);
        foreach (string directory in new[] { parent, app, path }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException($"Expected a regular managed directory: {directory}");
        }
        return path;
    }

    private void DeleteOwned(string area, Guid id, string? version = null)
    {
        string path = OwnedPath(area, id, version);
        try { Directory.Delete(path, true); }
        catch (DirectoryNotFoundException) { }
    }

    // Native RID tools can have a .cmd shim. Launch its SDK-declared binary directly to preserve argv.
    private static string ResolveEntry(string install, string command)
    {
        string shim = Path.Combine(install, command);
        if (File.Exists(shim + ".exe")) return shim + ".exe";
        if (!File.Exists(shim + ".cmd")) throw new InvalidDataException($"SDK did not install command '{command}'.");
        var executables = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(install, ".store"), "DotnetToolSettings.xml", SearchOption.AllDirectories))
        {
            foreach (var entry in XDocument.Load(path).Descendants("Command").Where(e =>
                string.Equals((string?)e.Attribute("Name"), command, StringComparison.OrdinalIgnoreCase) && (string?)e.Attribute("Runner") == "executable"))
            {
                string? relative = (string?)entry.Attribute("EntryPoint");
                if (relative == null || !ToolManifest.SafeRelative(relative)) throw ToolManifest.Error("SDK EntryPoint");
                string exe = Path.Combine(Path.GetDirectoryName(path)!, relative);
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe)) executables.Add(exe);
            }
        }
        return executables.Count == 1 ? executables[0] : throw new InvalidDataException($"Expected one installed native entry for '{command}', found {executables.Count}.");
    }
}
