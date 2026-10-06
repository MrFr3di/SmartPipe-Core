using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.NuGet;

internal sealed record NuGetPackageSnapshot
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required string Version { get; init; }

    [JsonPropertyOrder(2)]
    public required PackageAssetSnapshot Assets { get; init; }

    [JsonPropertyOrder(3)]
    public required PackageDependencySnapshot Dependencies { get; init; }
}

internal sealed record PackageAssetSnapshot
{
    [JsonPropertyOrder(0)]
    public required string PackageId { get; init; }

    [JsonPropertyOrder(1)]
    public required string Version { get; init; }

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<PackageFileSnapshot> Files { get; init; }

    [JsonPropertyOrder(3)]
    public required IReadOnlyList<PackageAssemblySnapshot> Assemblies { get; init; }
}

internal sealed record PackageFileSnapshot
{
    [JsonPropertyOrder(0)]
    public required string Path { get; init; }

    [JsonPropertyOrder(1)]
    public required long UncompressedLength { get; init; }

    [JsonPropertyOrder(2)]
    public required string Sha256 { get; init; }

    [JsonPropertyOrder(3)]
    public required string Category { get; init; }
}

internal sealed record PackageAssemblySnapshot
{
    [JsonPropertyOrder(0)]
    public required string Name { get; init; }

    [JsonPropertyOrder(1)]
    public required string Version { get; init; }

    [JsonPropertyOrder(2)]
    public required string Culture { get; init; }

    [JsonPropertyOrder(3)]
    public required string PublicKeyToken { get; init; }

    [JsonPropertyOrder(4)]
    public required string AssetFamily { get; init; }

    [JsonPropertyOrder(5)]
    public required string AssetPath { get; init; }

    [JsonPropertyOrder(6)]
    public required string TargetFramework { get; init; }

    [JsonPropertyOrder(7)]
    public required IReadOnlyList<string> ExportedTypes { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<string> TypeForwarders { get; init; }

    // Runtime inspection evidence; immutable baseline JSON retains its original shape.
    [JsonIgnore]
    public IReadOnlyDictionary<string, AssemblyReferenceIdentity> ForwarderDestinations { get; init; } = new Dictionary<string, AssemblyReferenceIdentity>();
}

internal sealed record AssemblyReferenceIdentity(string Name, Version Version, string Culture, string PublicKeyToken)
{
    public bool CanBindTo(PackageAssemblySnapshot implementation) =>
        string.Equals(Name, implementation.Name, StringComparison.OrdinalIgnoreCase)
        && Version <= System.Version.Parse(implementation.Version)
        && string.Equals(Culture, implementation.Culture, StringComparison.OrdinalIgnoreCase)
        && string.Equals(PublicKeyToken, implementation.PublicKeyToken, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name}, Version={Version}, Culture={Culture}, PublicKeyToken={PublicKeyToken}";
}
