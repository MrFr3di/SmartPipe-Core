using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class VerifyPackageMetadataCommand
{
    private readonly PackageGraphLoader _graphLoader;
    internal VerifyPackageMetadataCommand(PackageGraphLoader? graphLoader = null) => _graphLoader = graphLoader ?? new PackageGraphLoader();
    public async Task<PackageMetadataReport> ExecuteAsync(VerifyPackageMetadataOptions options, CancellationToken ct)
    {
        var graph = await _graphLoader.LoadAsync(options.RepositoryRoot, options.GraphPath, ct).ConfigureAwait(false);
        var errors = new List<PackageMetadataViolation>();
        var reader = new PackageMetadataReader();
        var validator = new PackageContentValidator();
        var count = 0;
        foreach (var node in graph.Packages.Where(x => x.Lifecycle != PackageLifecycle.Planned))
        {
            var nupkg = Path.Combine(options.PackageDirectory, $"{node.Id}.{graph.ReleaseVersion}.nupkg");
            var snupkg = Path.Combine(options.PackageDirectory, $"{node.Id}.{graph.ReleaseVersion}.snupkg");
            if (!File.Exists(nupkg)) { errors.Add(new("SPMETA001", node.Id, "expected nupkg is missing", nupkg)); continue; }
            count++;
            try
            {
                var metadata = await reader.ReadAsync(nupkg, ct).ConfigureAwait(false);
                errors.AddRange(await validator.ValidateAsync(node, graph.ReleaseVersion, metadata, snupkg, options.Mode, ct).ConfigureAwait(false));
            }
            catch (RepositoryCheckException exception) { errors.Add(new("SPMETA016", node.Id, exception.Message, nupkg)); }
        }
        var report = new PackageMetadataReport { Mode = options.Mode.ToString().ToLowerInvariant(), Packages = count, Violations = errors.OrderBy(x => x.PackageId, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray() };
        if (options.ReportPath is not null)
        {
            var json = CanonicalJson.Serialize(report, RepositoryChecksJsonContext.Default.PackageMetadataReport);
            Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
            var temp = options.ReportPath + ".tmp";
            await File.WriteAllTextAsync(temp, json, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temp, options.ReportPath, overwrite: true);
        }
        return report;
    }
}
