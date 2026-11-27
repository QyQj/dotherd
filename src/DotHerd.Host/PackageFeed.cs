using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotHerd;

internal sealed record PackageFeed(PackageSource Source, FindPackageByIdResource Resource, NuGetVersion Version)
{
    public string Origin
    {
        get
        {
            if (!Uri.TryCreate(Source.Source, UriKind.Absolute, out var uri) || uri.IsFile ||
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0) return Source.Source;
            if (Source.Name != Source.Source) return Source.Name;
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        }
    }

    internal static NuGetVersion? ValidateIdentity(string id, string? version)
    {
        if (!PackageIdValidator.IsValidPackageId(id)) throw new ArgumentException($"Invalid package ID: {id}");
        if (version == null) return null;
        return NuGetVersion.TryParse(version, out var exact) ? exact : throw new ArgumentException($"Expected an exact NuGet version: {version}");
    }

    public static async Task<PackageFeed> Resolve(InstallRequest request, SourceCacheContext cache)
    {
        var exact = ValidateIdentity(request.Id, request.Version);
        var settings = Settings.LoadDefaultSettings(request.Cwd);
        var sources = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToArray();
        if (request.Source is string selection)
        {
            var named = sources.FirstOrDefault(s => s.Name.Equals(selection, StringComparison.OrdinalIgnoreCase) ||
                s.Source.Equals(selection, StringComparison.OrdinalIgnoreCase));
            var location = Uri.TryCreate(selection, UriKind.Absolute, out var uri) ? (uri.IsFile ? uri.LocalPath : selection)
                : Path.GetFullPath(selection, request.Cwd);
            sources = [named ?? new PackageSource(location)];
        }
        else
        {
            var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
            if (mapping.IsEnabled)
            {
                var allowed = mapping.GetConfiguredPackageSources(request.Id);
                sources = sources.Where(s => allowed.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
            }
        }
        var matches = await Task.WhenAll(sources.DistinctBy(s => s.Source).Select(async source =>
        {
            var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>() ?? throw new InvalidDataException($"Unsupported NuGet source: {source.Source}");
            var versions = await resource.GetAllVersionsAsync(request.Id, cache, NullLogger.Instance, default);
            var version = versions.Where(v => exact == null ? !v.IsPrerelease : v == exact).Max();
            return version == null ? null : new PackageFeed(source, resource, version);
        }));
        var found = matches.OfType<PackageFeed>().OrderByDescending(f => f.Version).ToArray();
        if (found.Length == 0) throw new InvalidDataException($"No {(exact == null ? "stable version" : request.Version)} of {request.Id} in the selected sources.");
        var highest = found.Where(f => f.Version == found[0].Version).ToArray();
        if (highest.Length > 1)
            throw new InvalidDataException($"{request.Id}@{highest[0].Version} exists in multiple sources. Select --source: {string.Join(", ", highest.Select(f => f.Origin))}");
        return highest[0];
    }

    public async Task<string> Download(string id, string directory, SourceCacheContext cache)
    {
        ValidateIdentity(id, null);
        string path = Path.Combine(directory, $"{id}.{Version.ToNormalizedString()}.nupkg");
        using (var output = File.Open(path, FileMode.CreateNew))
            if (!await Resource.CopyNupkgToStreamAsync(id, Version, output, cache, NullLogger.Instance, default))
                throw new FileNotFoundException($"Missing {id}@{Version} at selected source.");
        using var archive = new PackageArchiveReader(path);
        var identity = archive.GetIdentity();
        if (!identity.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || identity.Version != Version)
            throw new InvalidDataException($"Source returned {identity} for {id}@{Version}.");
        return path;
    }
}
