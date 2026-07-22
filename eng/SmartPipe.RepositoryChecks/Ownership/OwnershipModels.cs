using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Ownership;

[JsonConverter(typeof(JsonStringEnumConverter<OwnershipStrategy>))]
internal enum OwnershipStrategy
{
    [JsonStringEnumMemberName("stay")] Stay,
    [JsonStringEnumMemberName("type-forward")] TypeForward,
    [JsonStringEnumMemberName("obsolete-wrapper")] ObsoleteWrapper,
}

internal sealed record OwnershipDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<OwnershipAssignment> Assignments { get; init; }
}
internal sealed record OwnershipAssignment
{
    public required string TypePattern { get; init; }
    public required string BaselineAssembly { get; init; }
    public required string CurrentImplementationAssembly { get; init; }
    public required string TargetImplementationAssembly { get; init; }
    public string? CompatibilityAssembly { get; init; }
    public required OwnershipStrategy Strategy { get; init; }
    public required string MigrationEpic { get; init; }
    public required bool NamespacePreserved { get; init; }
    public required string Evidence { get; init; }
}
internal sealed class OwnershipException(string code, string message, Exception? inner = null) : Exception(message, inner) { public string Code { get; } = code; }
internal sealed record OwnershipViolation(string Code, string Type, string Rule);
internal sealed record OwnershipResult(int BaselineTypes, IReadOnlyList<OwnershipViolation> Violations) { public bool Success => Violations.Count == 0; }
