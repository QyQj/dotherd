using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NuGet.Packaging;
using NuGet.Versioning;

namespace DotHerd;

internal sealed record InstalledPackage(string Id, string Version, string Source, string Command, string Directory,
    string DisplayName, JsonObject? Tray, McpDeclaration? Mcp, bool? AsProcess = null);
internal sealed record McpDeclaration(string Transport, bool OnConnection, string[] Args, string? Url, string? Namespace,
    Dictionary<string,string> Environment, Dictionary<string,string> Headers, Dictionary<string,string> InputEnvironment);
internal sealed record ToolPlan(string? Command, string[] Commands, string[] Args, string Readiness, string DisplayName,
    JsonObject? Tray, string[] Actions, McpDeclaration? Mcp, bool Confirm, string? RidPackage);

internal static class ToolManifest
{
    internal static string? Text(this JsonObject obj, string key) => obj[key] == null ? null :
        obj[key] is JsonValue item && item.TryGetValue<string>(out var value) ? value : throw Error(key);
    internal static string Required(this JsonObject obj, string key) => obj.Text(key) is { Length: > 0 } value ? value : throw Error(key);
    internal static InvalidDataException Error(string field) => new($"Invalid or missing manifest field: {field}");
    internal static string[] Strings(this JsonObject obj, string key) => obj[key] == null ? [] : obj[key] is JsonArray array ?
        array.Select(v => v is JsonValue item && item.TryGetValue<string>(out var s) ? s : throw Error(key)).ToArray() : throw Error(key);

    public static ToolPlan Read(string archive, InstallRequest options, NuGetVersion version)
    {
        using var package = new PackageArchiveReader(archive);
        var types = package.GetPackageTypes().Select(t => t.Name).ToArray();
        if (types.Contains("DotnetToolRidPackage", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("RID implementation package: select the top-level .NET tool package.");
        if (!types.Contains("DotnetTool", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Package has no independently installable .NET tool entry. Use add for a local executable.");

        using var zip = ZipFile.OpenRead(archive);
        var settings = zip.Entries.Where(e => e.FullName.StartsWith("tools/", StringComparison.Ordinal) && e.Name == "DotnetToolSettings.xml")
            .Select(e => { using var xml = e.Open(); return XDocument.Load(xml); }).ToArray();
        var commands = settings.SelectMany(s => s.Descendants("Commands").Elements("Command")).Select(e => (string?)e.Attribute("Name"))
            .Select(name => SafeCommand(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (commands.Length == 0) throw Error("DotnetToolSettings.xml/Commands");
        var rids = settings.SelectMany(s => s.Descendants("RuntimeIdentifierPackage")).ToArray();
        string? rid = rids.Where(e => (string?)e.Attribute("RuntimeIdentifier") == "win-x64").Select(e => (string?)e.Attribute("Id")).Distinct().SingleOrDefault();
        if (rids.Length > 0 && rid == null) throw Error("RuntimeIdentifierPackages/win-x64");

        JsonObject? ReadJson(string path)
        {
            var entries = zip.Entries.Where(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length == 0) return null;
            if (entries.Length != 1 || entries[0].FullName != path) throw Error(path);
            try { using var input = entries[0].Open(); return JsonNode.Parse(input)?.AsObject() ?? throw Error(path); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new InvalidDataException($"{path}: {e.Message}", e); }
        }

        var app = ReadJson(".dotherd/app.json");
        var standard = ReadJson(".mcp/server.json");
        if (types.Contains("McpServer", StringComparer.OrdinalIgnoreCase) != (standard != null))
            throw Error("McpServer package type and .mcp/server.json must be present together");
        string? command = options.Command;
        string[] args = options.Args;
        string readiness = "process", display = options.Id;
        JsonObject? tray = null;
        string[] actions = [];
        McpDeclaration? mcp = null;
        if (app != null)
        {
            if (app["schemaVersion"] is not JsonValue schema || !schema.TryGetValue<int>(out int major) || major != 1)
                throw Error(".dotherd/app.json schemaVersion (supported major: 1)");
            command = app.Required("command");
            args = app.Strings("args");
            display = app.Text("displayName") ?? display;
            readiness = app.Text("readiness") ?? readiness;
            if (readiness is not ("notify" or "process")) throw Error("readiness");
            tray = app["tray"] == null ? null : app["tray"] as JsonObject ?? throw Error("tray");
            if (tray != null)
            {
                if (tray.Text("default") is not (null or "separate" or "manager")) throw Error("tray.default");
                actions = (tray["actions"]?.AsArray() ?? []).Select(item =>
                {
                    var action = item?.AsObject() ?? throw Error("tray.actions");
                    action.Required("label");
                    return action.Required("id");
                }).ToArray();
                if (actions.Distinct(StringComparer.Ordinal).Count() != actions.Length ||
                    tray.Text("onActivate") is { } activate && !actions.Contains(activate)) throw Error("tray.actions/onActivate");
                if (tray.Text("icon") is { } icon && (!SafeRelative(icon) || zip.GetEntry(icon) == null)) throw Error("tray.icon");
            }
            var kind = app.Text("kind") ?? "process";
            if (kind is not ("process" or "mcp") || (kind == "mcp") != (app["mcp"] is JsonObject) ||
                kind == "process" && app["mcp"] != null) throw Error("kind/mcp");
            if (app["mcp"] is JsonObject block)
            {
                string transport = block.Required("transport");
                string[] bridge = block.Strings("args");
                string? lifecycle = block.Text("lifecycle");
                if (lifecycle is not (null or "onConnection" or "resident") || lifecycle == "onConnection" && transport != "stdio") throw Error("mcp.lifecycle");
                if (lifecycle == "onConnection")
                {
                    if (block["args"] == null) bridge = args;
                    else if (app["args"] != null && !args.SequenceEqual(bridge)) throw Error("conflict: args/mcp.args for onConnection");
                    args = bridge;
                }
                mcp = McpManifest.Declare(transport, lifecycle == "onConnection", bridge, block.Text("url"), block.Text("namespace"));
            }
        }
        if (standard != null)
        {
            var declaration = McpManifest.Read(standard, options, version);
            if (app == null)
            {
                command = commands.Length == 1 ? commands[0] : throw Error("ambiguous MCP tool command");
                mcp = declaration;
                args = declaration.Args;
            }
            else
            {
                if (mcp == null) throw Error("conflict: kind and .mcp/server.json");
                if (mcp.Transport != declaration.Transport) throw Error("conflict: mcp.transport");
                if (mcp.Url != declaration.Url) throw Error("conflict: mcp.url");
                // A resident stdio bridge can require an existing core while the standalone command can start it.
                bool connectOnly = mcp is { Transport: "stdio", OnConnection: false } &&
                    mcp.Args is [.., "--connect-only"] && mcp.Args[..^1].SequenceEqual(declaration.Args);
                if (!connectOnly && !mcp.Args.SequenceEqual(declaration.Args)) throw Error("conflict: mcp.args/packageArguments");
                mcp = mcp with { Environment = declaration.Environment, Headers = declaration.Headers, InputEnvironment = declaration.InputEnvironment };
            }
        }
        if ((app != null || standard != null) && options.AsProcess) throw Error("--as-process applies to undeclared tools");
        if (standard == null && options.Inputs.Count > 0) throw Error("inputs require a standard MCP declaration");
        if (command != null && !commands.Contains(SafeCommand(command), StringComparer.OrdinalIgnoreCase)) throw Error($"command '{command}' does not belong to the package");
        return new(command, commands, args, readiness, display, tray, actions, mcp, app == null && standard != null, rid);
    }

    internal static bool SafeRelative(string value) => value.Length > 0 && !value.Contains('\\') && !value.Contains(':') &&
        value.IndexOfAny(Path.GetInvalidPathChars()) < 0 && value.Split('/').All(s => s.Length > 0 && s is not ("." or "..") &&
        s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !s.EndsWith('.') && !s.EndsWith(' '));

    private static string SafeCommand(string? name) => name != null && SafeRelative(name) && !name.Contains('/') ? name : throw Error("tool command name");
}
