using System.Text.Json;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Ownership;

internal sealed record TypeOwnershipSnapshot(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Implementations,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Forwarders);

internal sealed class TypeForwarderReader
{
    public async Task<TypeOwnershipSnapshot> ReadPackagesAsync(string directory, IEnumerable<string> packageIds, string version, CancellationToken ct)
    {
        var packages = new List<(string Id, IReadOnlyList<PackageAssemblySnapshot> Assemblies)>();
        foreach (var id in packageIds)
        {
            var path = Path.Combine(directory, $"{id}.{version}.nupkg");
            if (!File.Exists(path)) continue;
            var package = await new NuGetPackageReader().ReadAsync(path, id, version, ct).ConfigureAwait(false);
            packages.Add((id, package.Assets.Assemblies));
        }
        return Create(packages);
    }

    public async Task<TypeOwnershipSnapshot> ReadBaselineAsync(string packageAssetsPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(packageAssetsPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var packages = new List<(string, IReadOnlyList<PackageAssemblySnapshot>)>();
        foreach (var package in document.RootElement.EnumerateArray())
        {
            var assemblies = new List<PackageAssemblySnapshot>();
            foreach (var assembly in package.GetProperty("assemblies").EnumerateArray())
                assemblies.Add(new PackageAssemblySnapshot
                {
                    Name = assembly.GetProperty("name").GetString()!,
                    Version = assembly.GetProperty("version").GetString()!,
                    Culture = "",
                    PublicKeyToken = "",
                    AssetFamily = "lib",
                    AssetPath = assembly.GetProperty("assetPath").GetString()!,
                    TargetFramework = assembly.GetProperty("targetFramework").GetString()!,
                    ExportedTypes = assembly.GetProperty("exportedTypes").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    TypeForwarders = assembly.GetProperty("typeForwarders").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                });
            packages.Add((package.GetProperty("packageId").GetString()!, assemblies));
        }
        return Create(packages);
    }

    private static TypeOwnershipSnapshot Create(IEnumerable<(string Id, IReadOnlyList<PackageAssemblySnapshot> Assemblies)> packages)
    {
        var implementations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var forwarders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var package in packages)
            foreach (var assembly in package.Assemblies)
            {
                foreach (var type in assembly.ExportedTypes) Add(implementations, type, package.Id);
                foreach (var type in assembly.TypeForwarders) Add(forwarders, type, package.Id);
            }
        return new(
            implementations.ToDictionary(x => x.Key, x => (IReadOnlySet<string>)x.Value, StringComparer.Ordinal),
            forwarders.ToDictionary(x => x.Key, x => (IReadOnlySet<string>)x.Value, StringComparer.Ordinal));
        static void Add(Dictionary<string, HashSet<string>> map, string type, string package) { if (!map.TryGetValue(type, out var set)) map[type] = set = new(StringComparer.OrdinalIgnoreCase); set.Add(package); }
    }
}
