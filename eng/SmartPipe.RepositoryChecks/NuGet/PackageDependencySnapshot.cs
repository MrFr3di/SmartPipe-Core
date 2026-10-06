using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.NuGet;

internal sealed record PackageDependencySnapshot
{
    [JsonPropertyOrder(0)]
    public required string PackageId { get; init; }

    [JsonPropertyOrder(1)]
    public required string Version { get; init; }

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<PackageDependencyGroupSnapshot> Groups { get; init; }
}

internal sealed record PackageDependencyGroupSnapshot
{
    [JsonPropertyOrder(0)]
    public required string TargetFramework { get; init; }

    [JsonPropertyOrder(1)]
    public required IReadOnlyList<PackageDependencyItemSnapshot> Dependencies { get; init; }
}

internal sealed record PackageDependencyItemSnapshot
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required string VersionRange { get; init; }
}
