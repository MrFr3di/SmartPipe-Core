using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.PackageGraph;

[JsonConverter(typeof(JsonStringEnumConverter<PackageLifecycle>))]
internal enum PackageLifecycle
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("planned")] Planned,
    [JsonStringEnumMemberName("compatibility-facade")] CompatibilityFacade,
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageAotContract>))]
internal enum PackageAotContract
{
    [JsonStringEnumMemberName("full")] Full,
    [JsonStringEnumMemberName("full-json-type-info")] FullJsonTypeInfo,
    [JsonStringEnumMemberName("transport-full")] TransportFull,
    [JsonStringEnumMemberName("explicit-sql")] ExplicitSql,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("verified-no-blanket")] VerifiedNoBlanket,
    [JsonStringEnumMemberName("annotated-reflection")] AnnotatedReflection,
    [JsonStringEnumMemberName("unsupported-blanket")] UnsupportedBlanket,
    [JsonStringEnumMemberName("not-runtime")] NotRuntime,
    [JsonStringEnumMemberName("no-blanket")] NoBlanket,
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageScaffoldKind>))]
internal enum PackageScaffoldKind
{
    [JsonStringEnumMemberName("core-leaf")] CoreLeaf,
    [JsonStringEnumMemberName("framework-integration")] FrameworkIntegration,
    [JsonStringEnumMemberName("composed-integration")] ComposedIntegration,
    [JsonStringEnumMemberName("host-integration")] HostIntegration,
    [JsonStringEnumMemberName("testing")] Testing,
}

internal sealed record PackageGraphDocument
{
    [JsonPropertyOrder(0)] public required int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string ReleaseVersion { get; init; }
    [JsonPropertyOrder(2)] public required IReadOnlyList<PackageNode> Packages { get; init; }
}

internal sealed record PackageNode
{
    [JsonPropertyOrder(0)] public required string Id { get; init; }
    [JsonPropertyOrder(1)] public required string ProjectPath { get; init; }
    [JsonPropertyOrder(2)] public required PackageLifecycle Lifecycle { get; init; }
    [JsonPropertyOrder(3)] public string? ActivationEpic { get; init; }
    [JsonPropertyOrder(4)] public required PackageScaffoldKind? ScaffoldKind { get; init; }
    [JsonPropertyOrder(5)] public required int PublishOrder { get; init; }
    [JsonPropertyOrder(6)] public string? BaselineVersion { get; init; }
    [JsonPropertyOrder(7)] public required PackageAotContract AotContract { get; init; }
    [JsonPropertyOrder(8)] public required DependencyPolicy CurrentDependencies { get; init; }
    [JsonPropertyOrder(9)] public required DependencyPolicy ReleaseDependencies { get; init; }
    [JsonPropertyOrder(10)] public required IReadOnlyList<TemporaryDependencyAllowance> TemporaryAllowances { get; init; }
    [JsonPropertyOrder(11)] public required IReadOnlyList<string> ConsumerScenarios { get; init; }
}

internal sealed record DependencyPolicy
{
    [JsonPropertyOrder(0)] public required IReadOnlyList<string> RequiredSmartPipePackages { get; init; }
    [JsonPropertyOrder(1)] public required IReadOnlyList<string> AllowedSmartPipePackages { get; init; }
    [JsonPropertyOrder(2)] public required IReadOnlyList<string> AllowedExternalPackages { get; init; }
    [JsonPropertyOrder(3)] public required IReadOnlyList<string> ForbiddenPackagePatterns { get; init; }
}

internal sealed record TemporaryDependencyAllowance
{
    [JsonPropertyOrder(0)] public required string Dependency { get; init; }
    [JsonPropertyOrder(1)] public required string Reason { get; init; }
    [JsonPropertyOrder(2)] public required string OwnerEpic { get; init; }
    [JsonPropertyOrder(3)] public required bool ExpiresBeforeRelease { get; init; }
    [JsonPropertyOrder(4)] public required string Evidence { get; init; }
}

internal enum PackageGraphMode { Current, Release }

internal sealed class PackageGraphException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal sealed record PackageGraphViolation(string Code, string PackageId, string Representation, string? Dependency, string Rule);
