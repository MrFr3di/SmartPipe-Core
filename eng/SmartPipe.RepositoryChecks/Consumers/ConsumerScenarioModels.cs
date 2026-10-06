using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Consumers;

[JsonConverter(typeof(JsonStringEnumConverter<ConsumerMode>))]
internal enum ConsumerMode
{
    [JsonStringEnumMemberName("build-and-run")] BuildAndRun,
    [JsonStringEnumMemberName("publish-trimmed")] PublishTrimmed,
    [JsonStringEnumMemberName("publish-native-aot")] PublishNativeAot,
    [JsonStringEnumMemberName("binary-compatibility")] BinaryCompatibility,
}

internal sealed record ConsumerScenario
{
    public required string Id { get; init; }
    public required string Set { get; init; }
    public string? Category { get; init; }
    public required ConsumerMode Mode { get; init; }
    public required string TemplatePath { get; init; }
    public required IReadOnlyList<string> PackageIds { get; init; }
    public required IReadOnlyList<string> ExpectedSmartPipeDependencies { get; init; }
    public required IReadOnlyList<string> ForbiddenDependencies { get; init; }
    public string? BaselineVersion { get; init; }
    public ExpectedPublishDiagnostic? ExpectedPublishDiagnostic { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required bool RunSecondLockedRestore { get; init; }
}

internal sealed record ExpectedPublishDiagnostic
{
    public required string Code { get; init; }
    public required string SourcePath { get; init; }
    public required int Line { get; init; }
    public required IReadOnlyList<string> MsBuildProperties { get; init; }
}

internal sealed record ConsumerScenarioDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<ConsumerScenario> Scenarios { get; init; }
    public required IReadOnlyList<string> RequiredAtRelease { get; init; }
}

internal sealed record ConsumerScenarioResult(
    int SchemaVersion,
    string Scenario,
    string Status,
    string PackageVersion,
    bool RestoreLocked,
    long DurationMs,
    IReadOnlyList<string> ObservedSmartPipeDependencies,
    IReadOnlyList<ConsumerCommandEvent> Commands);

internal sealed record ConsumerCommandEvent(string Phase, string Command, int ExitCode, DateTimeOffset StartedUtc, long DurationMs, string StandardOutputLog, string StandardErrorLog);

internal sealed class ConsumerScenarioException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
