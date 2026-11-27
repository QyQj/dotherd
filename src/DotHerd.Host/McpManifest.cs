using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using static DotHerd.ToolManifest;

namespace DotHerd;

internal static class McpManifest
{
    internal static McpDeclaration Declare(string transport, bool onConnection, string[] args, string? url, string? ns)
    {
        if (transport is not ("stdio" or "streamable-http")) throw Error("mcp.transport (stdio or streamable-http)");
        if (transport == "stdio" ? url != null : !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw Error("mcp.url (expected a local loopback HTTP endpoint)");
        return new(transport, onConnection, args, url, ns, [], [], []);
    }

    public static McpDeclaration Read(JsonObject manifest, InstallRequest request, NuGetVersion version)
    {
        var packages = (manifest["packages"]?.AsArray() ?? []).Select(n => n?.AsObject() ?? throw Error("packages"))
            .Where(p => p.Text("registryType") == "nuget" && string.Equals(p.Text("identifier"), request.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (packages.Length != 1) throw Error("packages (expected one local NuGet declaration for this ID; remote endpoints cannot be installed)");
        var package = packages[0];
        if (package.Text("version") is { } declared && (!NuGetVersion.TryParse(declared, out var exact) || exact != version)) throw Error("packages.version");
        if (package["runtimeArguments"]?.AsArray().Count > 0 || package.Text("runtimeHint") is not (null or "dnx"))
            throw Error("runtimeArguments/runtimeHint cannot be applied to the installed tool");
        var local = package["transport"]?.AsObject() ?? throw Error("packages.transport");
        var transport = local.Required("type");
        var args = new List<string>();
        var bindings = new Dictionary<string,string>(StringComparer.Ordinal);
        var environment = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string,string>(StringComparer.Ordinal);

        string? Value(JsonObject input, string? key)
        {
            string? literal = input.Text("value") ?? input.Text("default");
            if (key != null && request.Inputs.TryGetValue(key, out var env))
            {
                bindings[key] = env;
                return "{" + key + "}";
            }
            if (input["variables"] is JsonObject vars)
            {
                if (literal == null) throw Error("input template value");
                foreach (var variable in vars)
                {
                    var replacement = Value(variable.Value?.AsObject() ?? throw Error(variable.Key), variable.Key);
                    if (replacement == null || !literal.Contains("{" + variable.Key + "}")) throw Error("input variable " + variable.Key);
                    literal = literal.Replace("{" + variable.Key + "}", replacement, StringComparison.Ordinal);
                }
            }
            if (input["isSecret"]?.GetValue<bool>() == true && literal != null)
                throw Error($"secret input {key}: use --input-env {key}=ENV_NAME");
            if (literal == null && input["isRequired"]?.GetValue<bool>() == true)
                throw Error($"required input {key}: supply --input-env {key}=ENV_NAME");
            if (literal != null && (literal.Contains('\0') || Regex.Matches(literal, @"\{([^{}]+)\}").Any(m => !bindings.ContainsKey(m.Groups[1].Value))))
                throw Error("unresolved input in " + key);
            return literal;
        }

        foreach (var node in package["packageArguments"]?.AsArray() ?? [])
        {
            var arg = node?.AsObject() ?? throw Error("packageArguments");
            string type = arg.Required("type");
            string? name = type switch { "named" => arg.Required("name"), "positional" => arg.Text("valueHint"), _ => throw Error("packageArguments.type") };
            string? value = Value(arg, name);
            if (type == "named" && (value != null || arg["value"] == null && arg["default"] == null && arg["isRequired"] == null)) args.Add(name!);
            if (value != null) args.Add(value);
            if (value != null && name != null) values[name] = value;
        }
        foreach (var node in package["environmentVariables"]?.AsArray() ?? [])
        {
            var input = node?.AsObject() ?? throw Error("environmentVariables");
            string key = input.Required("name");
            if (key.IndexOfAny(['=', '\0']) >= 0) throw Error("environmentVariables.name");
            if (Value(input, key) is { } value) { environment.Add(key, value); values[key] = value; }
        }
        foreach (var node in local["headers"]?.AsArray() ?? [])
        {
            if (transport != "streamable-http") throw Error("transport.headers");
            var header = node?.AsObject() ?? throw Error("transport.headers");
            var key = header.Required("name");
            if (Value(header, key) is { } value) headers.Add(key, value);
        }
        if (request.Inputs.Keys.Except(bindings.Keys).FirstOrDefault() is { } unknown) throw Error($"undeclared input {unknown}");
        string? url = local.Text("url");
        if (url != null)
            url = Regex.Replace(url, @"\{([^{}]+)\}", m => values.TryGetValue(m.Groups[1].Value, out var value)
                ? value : throw Error("transport.url input " + m.Groups[1].Value));
        string? resolved = url == null ? null : Regex.Replace(url, @"\{([^{}]+)\}", m =>
            bindings.TryGetValue(m.Groups[1].Value, out var variable) && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
                ? value : throw Error("transport.url input " + m.Groups[1].Value));
        var spec = Declare(transport, transport == "stdio", args.ToArray(), resolved, null);
        return spec with { Url = url, Environment = environment, Headers = headers, InputEnvironment = bindings };
    }
}
