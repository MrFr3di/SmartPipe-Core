using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Agent;

internal sealed record AgentEvidenceCheck
{
    [JsonPropertyOrder(0)]
    public required string Check { get; init; }

    [JsonPropertyOrder(1)]
    public bool Success { get; init; }

    [JsonPropertyOrder(2)]
    public int ExitCode { get; init; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Counters { get; init; }
}

internal sealed record AgentEvidence
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyOrder(1)]
    public required string Epic { get; init; }

    [JsonPropertyOrder(2)]
    public required string Head { get; init; }

    [JsonPropertyOrder(3)]
    public required string Base { get; init; }

    [JsonPropertyOrder(4)]
    public required string Branch { get; init; }

    [JsonPropertyOrder(5)]
    public bool Clean { get; init; }

    [JsonPropertyOrder(6)]
    public required IReadOnlyList<string> ChangedPaths { get; init; }

    [JsonPropertyOrder(7)]
    public required string Fingerprint { get; init; }

    [JsonPropertyOrder(8)]
    public required string PlanSha { get; init; }

    [JsonPropertyOrder(9)]
    public required string Profile { get; init; }

    [JsonPropertyOrder(10)]
    public required string Status { get; init; }

    [JsonPropertyOrder(11)]
    public int ExitCode { get; init; }

    [JsonPropertyOrder(12)]
    public required IReadOnlyList<AgentEvidenceCheck> Checks { get; init; }
}
