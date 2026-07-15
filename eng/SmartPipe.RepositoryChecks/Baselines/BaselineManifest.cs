using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Baselines;

internal sealed record BaselineManifest
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string BaselineName { get; init; }

    [JsonPropertyOrder(2)]
    public required string TargetRelease { get; init; }

    [JsonPropertyOrder(3)]
    public required RepositoryBaseline Repository { get; init; }

    [JsonPropertyOrder(4)]
    public required IReadOnlyList<PackageBaseline> Packages { get; init; }

    [JsonPropertyOrder(5)]
    public required SnapshotReference PublicApi { get; init; }

    [JsonPropertyOrder(6)]
    public required SnapshotReference PackageAssets { get; init; }

    [JsonPropertyOrder(7)]
    public required SnapshotReference PackageDependencies { get; init; }

    [JsonPropertyOrder(8)]
    public required SnapshotReference RepositoryDependencies { get; init; }
}

internal sealed record RepositoryBaseline
{
    [JsonPropertyOrder(0)]
    public required string FullName { get; init; }

    [JsonPropertyOrder(1)]
    public required string DefaultBranch { get; init; }

    [JsonPropertyOrder(2)]
    public required string CommitSha { get; init; }

    [JsonPropertyOrder(3)]
    public required string SdkVersion { get; init; }

    [JsonPropertyOrder(4)]
    public required string SolutionPath { get; init; }

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<WorkflowBaseline> RequiredWorkflows { get; init; }
}

internal sealed record WorkflowBaseline
{
    [JsonPropertyOrder(0)]
    public required string Name { get; init; }

    [JsonPropertyOrder(1)]
    public required long RunId { get; init; }

    [JsonPropertyOrder(2)]
    public required Uri Url { get; init; }

    [JsonPropertyOrder(3)]
    public required string Conclusion { get; init; }
}

internal sealed record PackageBaseline
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required string Version { get; init; }

    [JsonPropertyOrder(2)]
    public required Uri Source { get; init; }

    [JsonPropertyOrder(3)]
    public required string FileName { get; init; }

    [JsonPropertyOrder(4)]
    public required string Sha256 { get; init; }

    [JsonPropertyOrder(5)]
    public required bool RequireRepositorySignature { get; init; }
}

internal sealed record SnapshotReference
{
    [JsonPropertyOrder(0)]
    public required string Path { get; init; }

    [JsonPropertyOrder(1)]
    public required string Sha256 { get; init; }
}
