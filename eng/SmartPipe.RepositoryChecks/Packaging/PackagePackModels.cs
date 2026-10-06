using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Packaging;

internal sealed record PackagePackManifest
{
    public required int SchemaVersion { get; init; }
    public required string Mode { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<PackagePackArtifact> Packages { get; init; }
}

internal sealed record PackagePackArtifact
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string NupkgPath { get; init; }
    public required string NupkgSha256 { get; init; }
    public required string SnupkgPath { get; init; }
    public required string SnupkgSha256 { get; init; }
    public required int PublishOrder { get; init; }
}

internal sealed class PackagePackException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
