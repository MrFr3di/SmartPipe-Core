using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Release;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class VerifyReleaseVersionCommand
{
    public async Task<ReleaseVersionResult> ExecuteAsync(VerifyReleaseVersionOptions options, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(options.RepositoryRoot, "eng/package-graph.json", ct).ConfigureAwait(false);
        return await new ReleaseVersionValidator().ValidateAsync(graph, options.Tag, options.Mode, options.RepositoryRoot, options.PackageDirectory, ct).ConfigureAwait(false);
    }
}
