using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Baselines;

internal sealed record RepositoryDependencyBaseline(
    IReadOnlyList<ProjectDirectDependencySnapshot> Direct,
    IReadOnlyList<RestoredProjectSnapshot> Restored);

internal sealed record RepositoryBaselineSnapshots(
    byte[] PublicApi,
    byte[] RepositoryDependencies);

internal static class BaselineSnapshotJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static byte[] Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return CanonicalText.ToUtf8Bytes(json.TrimEnd('\r', '\n') + "\n");
    }
}

internal static class BaselineReport
{
    public static byte[] Create(BaselineManifest manifest)
    {
        var report = new StringBuilder()
            .AppendLine("# SmartPipe 2.1.2 baseline report")
            .AppendLine()
            .Append("Repository: `").Append(manifest.Repository.FullName).AppendLine("`")
            .Append("Capture commit: `").Append(manifest.Repository.CaptureCommitSha).AppendLine("`")
            .Append("SDK: `").Append(manifest.Repository.SdkVersion).AppendLine("`")
            .AppendLine()
            .AppendLine("## Packages")
            .AppendLine()
            .AppendLine("| Package | Version | SHA-256 |")
            .AppendLine("| --- | --- | --- |");
        foreach (var package in manifest.Packages.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            report.Append("| ").Append(package.Id).Append(" | ").Append(package.Version)
                .Append(" | `").Append(package.Sha256).AppendLine("` |");
        }

        report.AppendLine()
            .AppendLine("## Snapshots")
            .AppendLine()
            .AppendLine("| Path | SHA-256 |")
            .AppendLine("| --- | --- |");
        foreach (var snapshot in EnumerateSnapshots(manifest).OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            report.Append("| ").Append(snapshot.Path).Append(" | `").Append(snapshot.Sha256).AppendLine("` |");
        }

        report.AppendLine()
            .AppendLine("## Workflow evidence")
            .AppendLine()
            .AppendLine("| Workflow | Run ID | Head SHA | URL |")
            .AppendLine("| --- | ---: | --- | --- |");
        foreach (var workflow in manifest.Repository.RequiredWorkflows.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            report.Append("| ").Append(workflow.Name).Append(" | ").Append(workflow.RunId)
                .Append(" | `").Append(workflow.HeadSha).Append("` | ")
                .Append(workflow.Url).AppendLine(" |");
        }

        return CanonicalText.ToUtf8Bytes(report.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal));
    }

    private static IEnumerable<SnapshotReference> EnumerateSnapshots(BaselineManifest manifest)
    {
        yield return manifest.PublicApi;
        yield return manifest.PackageAssets;
        yield return manifest.PackageDependencies;
        yield return manifest.RepositoryDependencies;
    }
}

internal sealed class BaselineRepositorySnapshotReader
{
    private readonly RepositorySnapshotReader _repository;
    private readonly PublicApiSnapshotReader _publicApi = new();
    private readonly ProjectDependencySnapshotReader _dependencies;

    public BaselineRepositorySnapshotReader(IProcessRunner processRunner, string dotnetPath)
    {
        _repository = new RepositorySnapshotReader(processRunner, dotnetPath);
        _dependencies = new ProjectDependencySnapshotReader(processRunner, dotnetPath);
    }

    public async Task<RepositoryBaselineSnapshots> ReadAsync(
        string repositoryRoot,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var projects = await _repository.ReadPackableProjectsAsync(
            repositoryRoot, solutionPath, cancellationToken).ConfigureAwait(false);
        var publicApi = _publicApi.Read(repositoryRoot, projects);
        var direct = _dependencies.ReadDirect(repositoryRoot, projects);
        var restored = await _dependencies.ReadRestoredAsync(
            repositoryRoot, solutionPath, cancellationToken).ConfigureAwait(false);
        return new RepositoryBaselineSnapshots(
            BaselineSnapshotJson.Serialize(publicApi),
            BaselineSnapshotJson.Serialize(new RepositoryDependencyBaseline(direct, restored.Projects)));
    }
}
