using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed record PackedDependencyGroup(string TargetFramework, IReadOnlyList<PackageDependencyItemSnapshot> Dependencies);
internal sealed record PackedPackageModel(string Id, string Version, IReadOnlyList<PackedDependencyGroup> Groups, IReadOnlyList<PackageFileSnapshot> Files, IReadOnlyList<PackageAssemblySnapshot> Assemblies);
internal interface IPackedNuspecReader { Task<PackedPackageModel> ReadAsync(string nupkgPath, CancellationToken ct); }

internal sealed class PackedNuspecReader(NuGetPackageReader? reader = null) : IPackedNuspecReader
{
    private readonly NuGetPackageReader _reader = reader ?? new NuGetPackageReader();
    public async Task<PackedPackageModel> ReadAsync(string nupkgPath, CancellationToken ct)
    {
        var snapshot = await _reader.ReadAsync(nupkgPath, ct).ConfigureAwait(false);
        foreach (var dependency in snapshot.Dependencies.Groups.SelectMany(x => x.Dependencies))
        {
            if (dependency.Id.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase) && HasExactOrUpperBound(dependency.VersionRange))
                throw new PackageGraphException("SPGRAPH060", $"Internal dependency {dependency.Id} has exact or upper-bounded range {dependency.VersionRange}.");
        }
        return new(snapshot.Id, snapshot.Version,
            snapshot.Dependencies.Groups.Select(x => new PackedDependencyGroup(x.TargetFramework, x.Dependencies)).ToArray(),
            snapshot.Assets.Files, snapshot.Assets.Assemblies);
    }

    private static bool HasExactOrUpperBound(string range)
    {
        var comma = range.IndexOf(',');
        if (comma < 0) return range.Length >= 2 && range[0] == '[' && range[^1] == ']';
        var upper = range[(comma + 1)..].Trim().TrimEnd(')', ']');
        return upper.Length > 0;
    }
}
