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
