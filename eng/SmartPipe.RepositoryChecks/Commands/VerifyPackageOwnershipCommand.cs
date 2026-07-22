using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class VerifyPackageOwnershipCommand
{
    public async Task<OwnershipResult> ExecuteAsync(VerifyPackageOwnershipOptions options, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(options.RepositoryRoot, "eng/package-graph.json", ct).ConfigureAwait(false);
        var ownership = await new OwnershipLoader().LoadAsync(options.RepositoryRoot, "eng/package-ownership.json", graph, ct).ConfigureAwait(false);
        var reader = new TypeForwarderReader();
        var baseline = await reader.ReadBaselineAsync(Path.Combine(options.BaselineDirectory, "package-assets.json"), ct).ConfigureAwait(false);
        var ids = graph.Packages.Where(x => options.Mode == PackageGraphMode.Release || x.Lifecycle != PackageLifecycle.Planned).Select(x => x.Id);
        var current = await reader.ReadPackagesAsync(options.PackageDirectory, ids, graph.ReleaseVersion, ct).ConfigureAwait(false);
        return new OwnershipValidator().Validate(ownership, graph, baseline, current, options.Mode);
    }
}
