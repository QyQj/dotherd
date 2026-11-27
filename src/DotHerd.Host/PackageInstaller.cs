using System.Text.Json;
using System.Xml.Linq;
using NuGet.Protocol.Core.Types;

namespace DotHerd;

internal sealed class PackageInstaller(string root)
{
    public async Task<Reply> Install(InstallRequest request, Func<string,Task<bool>> confirm, Func<Registration,Task<Reply>> register)
    {
        var id = Guid.NewGuid();
        string staging = Path.Combine(root, "staging", id.ToString("N"));
        string binaries = Path.Combine(root, "packages", id.ToString("N"));
        string data = Path.Combine(root, "data", id.ToString("N"));
        Directory.CreateDirectory(staging);
        bool registered = false;
        try
        {
            using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
            var feed = await PackageFeed.Resolve(request, cache);
            string version = feed.Version.ToNormalizedString();
            string snapshot = Directory.CreateDirectory(Path.Combine(staging, "source")).FullName;
            string archive = await feed.Download(request.Id, snapshot, cache);
            var plan = ToolManifest.Read(archive, request, feed.Version);
            if (plan.Command == null)
                return new(true, $"{request.Id}@{version}: {string.Join(", ", plan.Commands)}. No resident entry declared. To start one, use --as-process --command <name> -- [arguments].");
            if (plan.Confirm)
            {
                var preview = JsonSerializer.Serialize(new { package = request.Id, version, source = feed.Origin, plan.Command, plan.Mcp }, Wire.Json);
                if (!await confirm(preview)) return new(false, "MCP registration was not confirmed.");
            }
            if (plan.RidPackage != null) await feed.Download(plan.RidPackage, snapshot, cache);
            string install = Path.Combine(binaries, version);
            string config = Path.Combine(staging, "NuGet.Config");
            new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "selected"), new XAttribute("value", snapshot))))).Save(config);
            var sdk = ToolSdk.Command(ToolSdk.Dotnet, ["tool", "install", request.Id, "--version", $"[{version}]",
                "--tool-path", install, "--configfile", config]);
            sdk.WorkingDirectory = staging;
            sdk.Environment["NUGET_PACKAGES"] = Path.Combine(staging, "cache");
            await ToolSdk.Output(sdk);
            string executable = ResolveEntry(install, plan.Command);
            Directory.CreateDirectory(data);
            var spec = new AppSpec(request.Id, data, executable, plan.Args, plan.Readiness, plan.Actions);
            spec.Validate();
            var package = new InstalledPackage(request.Id, version, feed.Origin, plan.Command, install, plan.DisplayName, plan.Tray, plan.Mcp);
            var startup = register(new(id, spec, true, 0, Package: package));
            registered = true;
            return await startup;
        }
        finally
        {
            Directory.Delete(staging, true);
            if (!registered)
            {
                if (Directory.Exists(binaries)) Directory.Delete(binaries, true);
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
        }
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
